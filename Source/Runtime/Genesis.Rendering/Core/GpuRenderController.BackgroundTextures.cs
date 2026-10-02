using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StbImageSharp;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Textures;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;

namespace Genesis.Rendering.Core
{
    // Textures read and decoded away from the game's thread. Reading a texture is its file, its
    // decode and, for a lit surface, its chain of smaller copies: tens of milliseconds for a large
    // one, and a room that shows thirty new ones used to spend seconds on them in its first frame.
    // Here a worker does all of that, and the game's thread only hands the finished bytes to the
    // graphics card, a few milliseconds' worth in each frame.
    public sealed partial class GpuRenderController
    {
        /// <summary>A texture that has been read and decoded and is ready for the graphics card.</summary>
        private sealed class PreparedTexture
        {
            public GpuTextureDesc Desc;
            public byte[] Payload;
        }

        private sealed class TextureBeingRead
        {
            public Task<PreparedTexture> Reading;
            public long LastAskedMilliseconds;
            /// <summary>When the file turned out not to be readable as a texture; 0 otherwise.</summary>
            public long FailedMilliseconds;
        }

        private readonly Dictionary<(string Path, TextureColorSpace ColorSpace), TextureBeingRead> _texturesBeingRead = new();
        // A few at a time: each large texture holds tens of megabytes while it is decoded.
        private static readonly SemaphoreSlim TextureReaders = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        private long _textureUploadTicksThisFrame;
        private long _texturesBeingReadSweptMilliseconds;

        /// <summary>
        /// Time one frame may spend sending textures read in the background to the graphics card,
        /// in milliseconds. One texture is always sent, however large.
        /// </summary>
        public double BackgroundTextureUploadMilliseconds { get; set; } = 4;

        /// <summary>
        /// Textures asked for with <see cref="LoadTextureInBackground"/> within the last half
        /// second that are not on the graphics card yet. One that nothing asks for any more is
        /// not counted, so a loading screen does not wait for it.
        /// </summary>
        public int BackgroundTexturesPending
        {
            get
            {
                if (_texturesBeingRead.Count == 0) return 0;
                long now = Environment.TickCount64;
                int pending = 0;
                foreach (TextureBeingRead entry in _texturesBeingRead.Values)
                    if (entry.FailedMilliseconds == 0 && now - entry.LastAskedMilliseconds < 500) pending++;
                return pending;
            }
        }

        public TextureHandle LoadTextureInBackground(string path, TextureColorSpace colorSpace, out bool pending)
        {
            pending = false;
            if (!_initialized) return TextureHandle.Invalid;
            bool waiting = false;
            // The cache decides, as for any texture, whether the file needs loading at all. Its
            // loader here answers "not yet" until a worker has the bytes ready.
            TextureHandle handle = _textureCache.Load(path, colorSpace, (fullPath, space) =>
            {
                var key = (fullPath, space);
                long now = Environment.TickCount64;
                if (!_texturesBeingRead.TryGetValue(key, out TextureBeingRead entry))
                {
                    bool software = string.Equals(_gpu.BackendName, "Software", StringComparison.Ordinal);
                    _texturesBeingRead[key] = new TextureBeingRead
                    {
                        Reading = ReadTextureWhenFreeAsync(fullPath, space, software),
                        LastAskedMilliseconds = now,
                    };
                    waiting = true;
                    return TextureHandle.Invalid;
                }

                entry.LastAskedMilliseconds = now;
                if (entry.FailedMilliseconds != 0)
                {
                    // It could not be read a moment ago. Do not hand it to a worker again on every
                    // frame; try afresh after a few seconds, in case the file has been repaired.
                    if (now - entry.FailedMilliseconds < 5000) return TextureHandle.Invalid;
                    _texturesBeingRead.Remove(key);
                    waiting = true;
                    return TextureHandle.Invalid;
                }

                if (!entry.Reading.IsCompleted || !TextureUploadAllowed())
                {
                    waiting = true;
                    return TextureHandle.Invalid;
                }

                PreparedTexture prepared = entry.Reading.IsCompletedSuccessfully ? entry.Reading.Result : null;
                if (prepared == null)
                {
                    // A file the worker could not make sense of is given to the ordinary loader
                    // once, which knows the fallbacks and reports what it finds.
                    TextureHandle fallback = LoadTextureUncached(fullPath, space);
                    if (fallback.IsValid) _texturesBeingRead.Remove(key);
                    else entry.FailedMilliseconds = now;
                    return fallback;
                }

                _texturesBeingRead.Remove(key);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    using var timed = LoadClock.Measure(LoadWork.Texture);
                    GpuTextureHandle texture = _gpu.CreateTexture(prepared.Desc, prepared.Payload);
                    _texGpu.Add(texture);
                    return new TextureHandle(_texGpu.Count);
                }
                catch (Exception exception)
                {
                    // An older or unsupported cook must not make the editable image unavailable.
                    Debug.WriteLine($"[Renderer] Texture read in the background was rejected, loading it again: {fullPath} — {exception.Message}");
                    return LoadTextureUncached(fullPath, space);
                }
                finally
                {
                    _textureUploadTicksThisFrame += Stopwatch.GetTimestamp() - started;
                }
            }, ReleaseTextureUncached);
            pending = waiting;
            return handle;
        }

        private bool TextureUploadAllowed() =>
            _textureUploadTicksThisFrame == 0
            || Stopwatch.GetElapsedTime(0, _textureUploadTicksThisFrame).TotalMilliseconds < BackgroundTextureUploadMilliseconds;

        /// <summary>Starts a frame's allowance, and lets go of textures that were read for something no longer drawn.</summary>
        private void BeginBackgroundTextureFrame()
        {
            _textureUploadTicksThisFrame = 0;
            if (_texturesBeingRead.Count == 0) return;
            long now = Environment.TickCount64;
            if (now - _texturesBeingReadSweptMilliseconds < 2000) return;
            _texturesBeingReadSweptMilliseconds = now;
            List<(string, TextureColorSpace)> stale = null;
            foreach (KeyValuePair<(string Path, TextureColorSpace ColorSpace), TextureBeingRead> pair in _texturesBeingRead)
                if (pair.Value.Reading.IsCompleted && now - pair.Value.LastAskedMilliseconds > 10_000)
                    (stale ??= new List<(string, TextureColorSpace)>()).Add(pair.Key);
            if (stale == null) return;
            foreach ((string, TextureColorSpace) key in stale) _texturesBeingRead.Remove(key);
        }

        private static async Task<PreparedTexture> ReadTextureWhenFreeAsync(string fullPath, TextureColorSpace colorSpace, bool softwareRasterizer)
        {
            await TextureReaders.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => PrepareTexture(fullPath, colorSpace, softwareRasterizer)).ConfigureAwait(false);
            }
            finally
            {
                TextureReaders.Release();
            }
        }

        /// <summary>
        /// Everything about loading a texture that needs no graphics card: the same choices, in
        /// the same order, as <see cref="LoadTextureUncached"/>. Safe on any thread. Null when
        /// the file cannot be read as a texture.
        /// </summary>
        private static PreparedTexture PrepareTexture(string fullPath, TextureColorSpace colorSpace, bool softwareRasterizer)
        {
            try
            {
                if (!softwareRasterizer
                    && CookedTextureManifestStore.TryResolve(fullPath, out string cookedPath, out CookedTextureManifest _)
                    && DdsTextureData.TryLoad(cookedPath, colorSpace, out DdsTextureData cooked))
                {
                    return new PreparedTexture
                    {
                        Desc = new GpuTextureDesc
                        {
                            Width = cooked.Width,
                            Height = cooked.Height,
                            MipLevels = cooked.MipLevels,
                            ArrayLayers = 1,
                            Format = cooked.Format,
                            Usage = GpuBufferUsage.Immutable,
                            BindFlags = GpuBindFlags.ShaderResource,
                            DebugName = "Renderer.CookedTexture",
                        },
                        Payload = cooked.Payload,
                    };
                }

                using FileStream stream = File.OpenRead(fullPath);
                ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                if (image == null || image.Width <= 0 || image.Height <= 0) return null;
                if (colorSpace == TextureColorSpace.Display)
                {
                    return new PreparedTexture
                    {
                        Desc = new GpuTextureDesc
                        {
                            Width = image.Width,
                            Height = image.Height,
                            MipLevels = 1,
                            ArrayLayers = 1,
                            Format = GpuFormat.R8G8B8A8UNorm,
                            Usage = GpuBufferUsage.Immutable,
                            BindFlags = GpuBindFlags.ShaderResource,
                            DebugName = "Renderer.Texture",
                        },
                        Payload = image.Data,
                    };
                }

                bool srgb = colorSpace == TextureColorSpace.Srgb;
                byte[] chain = TextureMipBuilder.BuildChain(image.Data, image.Width, image.Height, srgb, out int mipLevels);
                return new PreparedTexture
                {
                    Desc = new GpuTextureDesc
                    {
                        Width = image.Width,
                        Height = image.Height,
                        MipLevels = mipLevels,
                        ArrayLayers = 1,
                        Format = GpuFormat.R8G8B8A8UNorm,
                        Usage = GpuBufferUsage.Immutable,
                        BindFlags = GpuBindFlags.ShaderResource,
                        DebugName = srgb ? "Renderer.ColorTextureMips" : "Renderer.DataTextureMips",
                    },
                    Payload = chain,
                };
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Renderer] Reading a texture in the background failed: {fullPath} — {exception.Message}");
                return null;
            }
        }
    }
}

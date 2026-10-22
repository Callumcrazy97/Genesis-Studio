using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;

namespace Genesis.Rendering.Core
{
    // Texture streaming by distance. A texture with mips larger than TextureStreamingStartSize is
    // first sent to the graphics card from the level that size fits (a 4096 texture from its 512
    // level: a sixty-fourth of the memory), so a world full of big textures loads quickly. Each
    // frame the meshes drawn with it say how many texels they need: as many as the pixels they
    // cover on screen. A texture asked for more detail is read again on a worker, from the level
    // wanted down, and swapped in behind its handle (the old one is released at the next frame).
    // While the textures on the card take more than TextureStreamingBudgetBytes, those not asked
    // for their detail for TextureStreamingDropSeconds go back to the level they are wanted at.
    public sealed partial class GpuRenderController
    {
        private const int NotStreamed = -2;

        private sealed class StreamedTexture
        {
            public string Path;
            public TextureColorSpace ColorSpace;
            public GpuFormat Format;
            public int FullWidth, FullHeight, FullMips;
            public int ResidentTop;
            // The level it started at: what it goes back to when nothing asks for more.
            public int StartTop;
            public long ResidentBytes;
            // The most detail this frame's draws asked for (a level; FullMips when nothing asked).
            public int WantedThisFrame;
            public int Wanted;
            public long WantedSince;
            public Task<PreparedTexture> Loading;
        }

        private readonly Dictionary<int, StreamedTexture> _streamed = new();
        private long _streamedBytes;
        private Vector3 _streamCamera;
        private float _streamProjectionScale = 1f;

        /// <summary>Whether big textures stream their detail by distance (on unless GENESIS_TEXTURE_STREAMING is 0).</summary>
        public static bool TextureStreamingEnabled { get; set; } = Environment.GetEnvironmentVariable("GENESIS_TEXTURE_STREAMING") != "0";

        /// <summary>The largest level a streamed texture starts at, in texels a side.</summary>
        public static int TextureStreamingStartSize { get; set; } = 512;

        /// <summary>Texture memory above which detail not asked for is let go (GENESIS_TEXTURE_BUDGET_MB, 1 024 MB unless set).</summary>
        public long TextureStreamingBudgetBytes { get; set; } =
            (int.TryParse(Environment.GetEnvironmentVariable("GENESIS_TEXTURE_BUDGET_MB"), out int megabytes) && megabytes > 0 ? megabytes : 1024) * 1024L * 1024L;

        /// <summary>How long detail must go unasked for before it may be let go.</summary>
        public double TextureStreamingDropSeconds { get; set; } = 5;

        /// <summary>Streamed textures on the graphics card, and the bytes they take.</summary>
        public int StreamedTextureCount => _streamed.Count;
        public long StreamedTextureBytes => _streamedBytes;

        /// <summary>The level a streamed texture's largest resident image is (0 = full size); -1 when it is not streamed.</summary>
        public int StreamedTextureLevel(TextureHandle texture) =>
            _streamed.TryGetValue(texture.Id, out StreamedTexture entry) ? entry.ResidentTop : -1;

        /// <summary>
        /// Starts a texture with mips at the level <see cref="TextureStreamingStartSize"/> fits, or at
        /// <paramref name="top"/> when one is asked for; <see cref="NotStreamed"/> leaves it whole.
        /// </summary>
        private static PreparedTexture TrimForStreaming(PreparedTexture prepared, int top)
        {
            GpuTextureDesc desc = prepared.Desc;
            if (top == NotStreamed || !TextureStreamingEnabled || desc.MipLevels <= 1) return prepared;
            prepared.Streamable = true;
            prepared.FullWidth = desc.Width;
            prepared.FullHeight = desc.Height;
            prepared.FullMips = desc.MipLevels;
            if (top < 0)
            {
                top = 0;
                while (top < desc.MipLevels - 1 && Math.Max(desc.Width >> top, desc.Height >> top) > TextureStreamingStartSize) top++;
            }
            top = Math.Clamp(top, 0, desc.MipLevels - 1);
            prepared.Top = top;
            if (top == 0) return prepared;
            int skipped = GpuTextureLayout.GetMipChainSize(desc.Format, desc.Width, desc.Height, top);
            prepared.Payload = prepared.Payload.AsSpan(skipped).ToArray();
            desc.Width = Math.Max(1, desc.Width >> top);
            desc.Height = Math.Max(1, desc.Height >> top);
            desc.MipLevels -= top;
            prepared.Desc = desc;
            return prepared;
        }

        private void RegisterStreamedTexture(int id, PreparedTexture prepared, string path, TextureColorSpace colorSpace)
        {
            if (!prepared.Streamable) return;
            var entry = new StreamedTexture
            {
                Path = path, ColorSpace = colorSpace, Format = prepared.Desc.Format,
                FullWidth = prepared.FullWidth, FullHeight = prepared.FullHeight, FullMips = prepared.FullMips,
                ResidentTop = prepared.Top, StartTop = prepared.Top, ResidentBytes = prepared.Payload.LongLength,
                WantedThisFrame = prepared.FullMips, Wanted = prepared.Top, WantedSince = Environment.TickCount64,
            };
            _streamed[id] = entry;
            _streamedBytes += entry.ResidentBytes;
        }

        private void ForgetStreamedTexture(int id)
        {
            if (!_streamed.Remove(id, out StreamedTexture entry)) return;
            _streamedBytes -= entry.ResidentBytes;
        }

        /// <summary>Where the camera is and how large one unit at one unit away is on screen, for the levels draws ask for.</summary>
        private void NoteStreamingCamera(in Matrix4x4 view, in Matrix4x4 projection)
        {
            if (Matrix4x4.Invert(view, out Matrix4x4 world)) _streamCamera = world.Translation;
            // Half the screen's height in pixels times the projection's vertical scale.
            _streamProjectionScale = MathF.Abs(projection.M22) * MathF.Max(1, PixelHeight) * 0.5f;
        }

        /// <summary>A mesh drawn with a streamed texture asks for the level its size on screen needs.</summary>
        private void NoteStreamedTextureUse(TextureHandle texture, MeshHandle mesh, in Matrix4x4 world)
        {
            if (!_streamed.TryGetValue(texture.Id, out StreamedTexture entry)) return;
            float scale = MathF.Max(new Vector3(world.M11, world.M12, world.M13).Length(),
                MathF.Max(new Vector3(world.M21, world.M22, world.M23).Length(), new Vector3(world.M31, world.M32, world.M33).Length()));
            float radius = MathF.Max(0.01f, (_fwd?.MeshBoundsRadius(mesh.Id) ?? 1f) * scale);
            float distance = Vector3.Distance(world.Translation, _streamCamera) - radius;
            // Texels the mesh needs across: the pixels it covers (all of them when the camera is at it).
            float texels = distance <= 0.01f ? float.MaxValue : 2f * radius * _streamProjectionScale / distance;
            int level = 0;
            int size = Math.Max(entry.FullWidth, entry.FullHeight);
            while (level < entry.FullMips - 1 && (size >> (level + 1)) >= texels) level++;
            if (level < entry.WantedThisFrame) entry.WantedThisFrame = level;
        }

        /// <summary>Once a frame: reads more detail where it is asked for, lets it go over the budget, and swaps in what was read.</summary>
        private void UpdateTextureStreaming()
        {
            if (_streamed.Count == 0) return;
            long now = Environment.TickCount64;
            bool overBudget = _streamedBytes > TextureStreamingBudgetBytes;
            foreach (KeyValuePair<int, StreamedTexture> pair in _streamed)
            {
                StreamedTexture entry = pair.Value;
                // What the last frame's draws asked for; detail asked for recently is kept.
                if (entry.WantedThisFrame <= entry.Wanted) { entry.Wanted = entry.WantedThisFrame; entry.WantedSince = now; }
                else if (now - entry.WantedSince > TextureStreamingDropSeconds * 1000) { entry.Wanted = Math.Min(entry.WantedThisFrame, entry.StartTop); entry.WantedSince = now; }
                entry.WantedThisFrame = entry.FullMips;

                if (entry.Loading != null)
                {
                    if (!entry.Loading.IsCompleted || !TextureUploadAllowed()) continue;
                    PreparedTexture read = entry.Loading.IsCompletedSuccessfully ? entry.Loading.Result : null;
                    entry.Loading = null;
                    if (read == null || read.Top == entry.ResidentTop || pair.Key - 1 >= _texGpu.Count || !_texGpu[pair.Key - 1].IsValid) continue;
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        GpuTextureHandle texture = _gpu.CreateTexture(read.Desc, read.Payload);
                        // Batches already submitted this frame hold the old one: it goes at the next.
                        _deferredTextureReleases.Add(_texGpu[pair.Key - 1]);
                        _texGpu[pair.Key - 1] = texture;
                        _streamedBytes += read.Payload.LongLength - entry.ResidentBytes;
                        entry.ResidentBytes = read.Payload.LongLength;
                        entry.ResidentTop = read.Top;
                    }
                    catch (Exception error)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Renderer] A streamed texture level was not made: {entry.Path} — {error.Message}");
                    }
                    finally
                    {
                        _textureUploadTicksThisFrame += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                    }
                    continue;
                }

                // More detail when it is asked for; less only over the budget, once unasked for a while.
                int target = entry.Wanted < entry.ResidentTop ? entry.Wanted
                    : overBudget && entry.Wanted > entry.ResidentTop ? entry.Wanted
                    : entry.ResidentTop;
                if (target == entry.ResidentTop) continue;
                entry.Loading = ReadTextureWhenFreeAsync(entry.Path, entry.ColorSpace, softwareRasterizer: false, target);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using StbImageSharp;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Overlay;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.Diagnostics;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Textures;

namespace Genesis.Rendering.Core
{
    public sealed unsafe partial class GpuRenderController : IRenderController, Genesis.Rendering.Particles.IGpuParticleRenderer
    {
        // ── Surface ──────────────────────────────────────────────────────────────

        private IGpuSwapChain _gpuSwapChain;
        private bool   _initialized;
        private bool   _vsync;

        // ── Pipeline ─────────────────────────────────────────────────────────────

        private ForwardRenderer _fwd;
        private SpriteRenderer  _spr;
        private readonly IGpuDevice _gpu;
        private RoomFogState  _spriteFog = RoomFogState.Disabled;

        // Cached so a per-frame pass costs no allocation. GpuRenderPassDesc.ColorActions is an
        // array, and these two shapes cover every pass the controller opens.
        private static readonly GpuAttachmentAction[] KeepColor = { GpuAttachmentAction.Keep() };
        private static readonly GpuAttachmentAction[] ClearColor = { GpuAttachmentAction.Clear(0f, 0f, 0f, 1f) };

        // ── Texture registry ─────────────────────────────────────────────────────

        private readonly System.Collections.Generic.List<GpuTextureHandle> _texGpu = new();
        private readonly TextureAssetCache _textureCache = new();
        private GpuTextureHandle _whiteTexture;

        // ── Overlay ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Above ordinary sprite primitives (which default to -10000) but inside the depth range,
        /// so HUD icons submitted afterwards can still be placed on top of it.
        /// </summary>
        private const int OverlayDepth = -10200;

        private readonly OverlayCommandList _overlayCommands = new();
        private readonly List<GlyphQuad> _glyphQuads = new();
        private GlyphAtlas _glyphAtlas;
        private GpuTextureHandle _glyphAtlasGpuTexture = GpuTextureHandle.Invalid;
        private TextureHandle _glyphAtlasTexture = TextureHandle.Invalid;

        // ── Public props ─────────────────────────────────────────────────────────

        public bool   IsInitialized => _initialized;
        public bool   IsFramebufferReady => _initialized && _gpuSwapChain != null && _gpuSwapChain.IsReady;
        public string BackendName   => _gpu.BackendName;
        public string AdapterName   => _gpu?.AdapterName ?? string.Empty;
        public int    PixelWidth    => _gpuSwapChain?.Width  ?? 0;
        public int    PixelHeight   => _gpuSwapChain?.Height ?? 0;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        /// <summary>Drives a frame over any backend that implements <see cref="IGpuDevice"/>.</summary>
        /// <remarks>
        /// The device arrives from outside rather than being constructed here, which is the whole
        /// point of the class: it used to create a D3D11 device itself and reach past the
        /// abstraction for native views, so a second backend would have meant a second copy of
        /// 1100 lines of renderer orchestration.
        /// </remarks>
        public GpuRenderController(IGpuDevice device) =>
            _gpu = device ?? throw new ArgumentNullException(nameof(device));

        public void Initialize(IntPtr windowHandle, int width, int height)
        {
            if (_initialized) return;
            _gpuSwapChain = _gpu.CreateSwapChain(windowHandle, width, height);
            _fwd = new ForwardRenderer(_gpu);
            _fwd.ExternalParticles = DrawSubmittedParticles3D;
            byte[] spriteVs = ShaderCompiler.CompileForBackend(
                SpriteShaders.Source, "VS", GpuShaderStage.Vertex, _gpu.ShaderBinaryFormat).Blob;
            byte[] spritePs = ShaderCompiler.CompileForBackend(
                SpriteShaders.Source, "PS", GpuShaderStage.Pixel, _gpu.ShaderBinaryFormat).Blob;
            _spr = new SpriteRenderer(_gpu, spriteVs, spritePs);
            _shaderPreview = new ShaderPreviewPass(_gpu);
            CreateWhiteTexture();

            // Explicit backends may have opened a command list while staging built-in textures.
            // Submit that initialization batch now so the first gameplay frame starts with a fresh
            // upload-ring slot instead of inheriting tens of MiB of one-time resource uploads.
            _gpu.EndFrame();
            _gpu.WaitIdle();

            _initialized = true;
        }

        private void CreateWhiteTexture()
        {
            byte[] px = { 255, 255, 255, 255 };
            _whiteTexture = _gpu.CreateTexture(new GpuTextureDesc
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = GpuFormat.R8G8B8A8UNorm,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Renderer.WhiteTexture",
            }, px);
        }

        public void Resize(int width, int height) => TryResize(width, height);

        public bool TryResize(int width, int height)
        {
            if (!_initialized || _gpuSwapChain == null) return false;

            // Readback deliberately opens a fresh frame so a caller can keep drawing. A resize may
            // be the next operation, however, and SDL/D3D12 cannot resize a window while that frame
            // owns an acquired swap-chain image. Finish it, release all target bindings, and wait
            // until no in-flight command can still reference the old colour/depth resources.
            _gpu.EndFrame();
            _gpu.UnbindRenderTargets();
            _gpu.WaitIdle();
            return _gpuSwapChain.Resize(width, height);
        }

        /// <summary>
        /// Opens a pass on a target and sizes the viewport to it.
        /// </summary>
        /// <remarks>
        /// The controller used to fetch native render-target views from the device and call
        /// <c>OMSetRenderTargets</c>/<c>RSSetViewports</c> itself, in six places. That worked while
        /// D3D11 was the only backend and is unimplementable on any other — an explicit API needs
        /// the pass declared up front to know its resource states and pipeline signature. Going
        /// through <see cref="IGpuDevice.BeginRenderPass"/> is what lets one controller drive every
        /// backend; the D3D11 device does exactly what this code used to do inline.
        ///
        /// Depth is cleared on request rather than by policy: UI sprites use an orthographic depth
        /// range incompatible with the 3D perspective buffer, so HUD and menus need a clear to sit
        /// on top of the world.
        /// </remarks>
        private void BeginPass(
            GpuRenderTargetHandle target,
            GpuTextureHandle depthTexture,
            int width,
            int height,
            bool clearDepth,
            string debugName)
        {
            GpuRenderPassDesc desc = new()
            {
                Target = target,
                DepthTexture = depthTexture,
                ColorActions = KeepColor,
                DepthAction = clearDepth
                    ? GpuAttachmentAction.Clear(1f, 0f, 0f, 0f)
                    : GpuAttachmentAction.Keep(),
                HasDepth = true,
                DebugName = debugName,
            };

            _gpu.BeginRenderPass(desc);
            _gpu.SetViewport(0f, 0f, width, height);
        }

        public void Shutdown()
        {
            if (!_initialized) return;
            // Be tolerant of shutdown during a caller-owned frame. SDL GPU forbids cancelling a
            // command buffer after it has acquired a swap-chain texture, so submit it before
            // releasing render resources or the window.
            _gpu.EndFrame();
            ClearPreviewShaderOverride();
            if (_previewOverrideCb.IsValid)
            {
                _gpu.ReleaseBuffer(_previewOverrideCb);
                _previewOverrideCb = GpuBufferHandle.Invalid;
            }
            if (_previewParametersCb.IsValid)
            {
                _gpu.ReleaseBuffer(_previewParametersCb);
                _previewParametersCb = GpuBufferHandle.Invalid;
            }
            DisposeParticles();
            _fwd?.Dispose(); _fwd = null;
            _spr?.Dispose(); _spr = null;
            _shaderPreview?.Dispose(); _shaderPreview = null;
            ReleaseOverlayTexture();
            _glyphAtlas?.Dispose(); _glyphAtlas = null;

            for (int i = 0; i < _renderTargets.Count; i++)
            {
                RenderTargetEntry entry = _renderTargets[i];
                if (entry.IsReleased) continue;
                InvalidateTextureRegistry(entry.ColorTexture, releaseGpu: false);
                _gpu.ReleaseRenderTarget(entry.Target);
            }
            _renderTargets.Clear();
            _activeRenderTarget = 0;

            for (int i = 0; i < _texGpu.Count; i++)
            {
                if (_texGpu[i].IsValid) _gpu.ReleaseTexture(_texGpu[i]);
            }
            _texGpu.Clear();
            _textureCache.Clear();
            if (_whiteTexture.IsValid) _gpu.ReleaseTexture(_whiteTexture);
            _whiteTexture = GpuTextureHandle.Invalid;
            _gpuSwapChain?.Dispose(); _gpuSwapChain = null;
            _gpu?.Dispose();
            _initialized = false;
        }

        public void Dispose() => Shutdown();

        // ── Frame ────────────────────────────────────────────────────────────────

        public void BeginFrame()
        {
            if (!_initialized) return;

            _gpu.BeginFrame();

            _cameraPostProcessing = false;
            _fwd?.BeginSubmitFrame();
            _spr?.BeginFrame();

            if (_gpuSwapChain == null || !_gpuSwapChain.IsReady) return;

            // A frame always starts on the back buffer; an offscreen target is something a pass
            // opts into part-way through and is expected to leave behind.
            SetRenderTargetDefault();
        }

        private bool _cameraPostProcessing;
        public void BeginCameraPass(bool postProcessing)
        {
            _cameraPostProcessing = postProcessing;
            _fwd?.BeginSubmitFrame();
            _spr?.BeginFrame();
        }

        public void Clear(float r, float g, float b, float a = 1f)
        {
            if (!_initialized) return;

            // Clears whatever is bound, so a pass rendering into an offscreen target clears that
            // target rather than silently wiping the back buffer behind it.
            bool offscreen = TryGetRenderTarget(
                new RenderTargetHandle(_activeRenderTarget), out RenderTargetEntry entry);
            if (!offscreen && _gpuSwapChain == null) return;

            GpuRenderPassDesc desc = new()
            {
                Target = offscreen ? entry.Target : GpuRenderTargetHandle.Invalid,
                DepthTexture = GpuTextureHandle.Invalid,
                ColorActions = new[] { GpuAttachmentAction.Clear(r, g, b, a) },
                DepthAction = GpuAttachmentAction.Clear(1f, 0f, 0f, 0f),
                HasDepth = true,
                DebugName = "Controller.Clear",
            };

            _gpu.BeginRenderPass(desc);
            _gpu.SetViewport(
                0f, 0f,
                offscreen ? entry.Width : _gpuSwapChain.Width,
                offscreen ? entry.Height : _gpuSwapChain.Height);
            // SDL GPU cannot record a copy pass while a render pass is open. Leaving Clear
            // bound until the first 3D pass forced instance uploads onto a dedicated command
            // buffer that CPU-waited — a full GPU drain every frame (~15 FPS). The 3D main
            // pass clears colour itself, so this pass only needs to land the clear.
            _gpu.EndRenderPass();
        }

        public void EndFrame()
        {
            if (!_initialized) return;
            BindPreviewOverrideCb();

            // Flush into whatever is bound. This used to hard-code the swap chain, which meant
            // SetRenderTarget bound a target the forward renderer then ignored — the binding was
            // real but nothing ever rendered into it.
            bool offscreen = TryGetRenderTarget(
                new RenderTargetHandle(_activeRenderTarget), out RenderTargetEntry target);

            if (!offscreen && (_gpuSwapChain == null || !_gpuSwapChain.IsReady))
            {
                _fwd?.BeginSubmitFrame();
                return;
            }

            GpuRenderTargetHandle gpuTarget = offscreen ? target.Target : GpuRenderTargetHandle.Invalid;
            GpuTextureHandle depthTexture = offscreen ? target.DepthTexture : _gpuSwapChain.DepthTexture;
            int width  = offscreen ? target.Width  : _gpuSwapChain.Width;
            int height = offscreen ? target.Height : _gpuSwapChain.Height;

            PrepareSubmittedParticles();

            if (_frame3DActive)
                _fwd?.Flush(gpuTarget, _whiteTexture, width, height, depthTexture,
                    allowPostProcess: !offscreen || _cameraPostProcessing);
            else
                _fwd?.BeginSubmitFrame();

            // Reopen the target: the forward renderer's own passes (shadow map, post-process)
            // rebound it. Depth is cleared because UI sprites use an orthographic range
            // incompatible with the 3D perspective buffer, so HUD and menus draw over the world.
            // Upload sprite instances before opening the pass so SDL can copy on the frame
            // command buffer instead of CPU-waiting a dedicated copy CB.
            _spr?.UploadPendingInstances();
            BeginPass(gpuTarget, GpuTextureHandle.Invalid, width, height,
                clearDepth: true, "Controller.Sprites2D");

            _spr?.SetFog(_spriteFog);
            _spr?.Flush(width, height, ResolveGpuTexture, _whiteTexture, _cam2DX, _cam2DY, _cam2DZoom);
            // Close the 2D pass before post-EndFrame overlay composition. SDL cannot record a
            // copy while a pass is open; leaving this bound forced HUD instance uploads onto a
            // dedicated command buffer that waited for the previous 3D frame (~15 FPS).
            _gpu.EndRenderPass();
            _particleDraws.Clear();
        }

        public void FlushOverlaySprites()
        {
            if (!_initialized || _gpuSwapChain == null || !_gpuSwapChain.IsReady) return;
            _spr?.UploadPendingInstances();
            BeginPass(GpuRenderTargetHandle.Invalid, GpuTextureHandle.Invalid,
                _gpuSwapChain.Width, _gpuSwapChain.Height, clearDepth: true, "Controller.OverlaySprites");
            // GUI/HUD is screen-space and must remain legible regardless of room fog.
            _spr?.SetFog(RoomFogState.Disabled);
            _spr?.Flush(_gpuSwapChain.Width, _gpuSwapChain.Height, ResolveGpuTexture, _whiteTexture, _gpuSwapChain.Width / 2f, _gpuSwapChain.Height / 2f, 1f);
            _spr?.SetFog(_spriteFog);
            _gpu.EndRenderPass();
        }

        private GpuTextureHandle ResolveGpuTexture(int id)
        {
            int index = id - 1;
            return index >= 0 && index < _texGpu.Count
                ? _texGpu[index]
                : GpuTextureHandle.Invalid;
        }

        public void SetVSync(bool vsync) => _vsync = vsync;

        // ── Overlay (R4: backend-neutral) ────────────────────────────────────────

        /// <summary>
        /// Records HUD draws through the shared canvas and submits them as ordinary GPU primitives:
        /// shapes become sprite rects and lines, text becomes one textured quad per glyph out of the
        /// shared atlas. Nothing here is Direct3D-specific except the surface it lands on, so Vulkan
        /// and OpenGL reuse the identical text and layout.
        /// </summary>
        public bool ComposeOverlay(Action<IOverlayCanvas> draw)
        {
            if (!_initialized || draw == null) return false;
            if (!EnsureOverlayFrame()) return false;

            draw(_overlayCommands);
            return FlushOverlay();
        }

        /// <summary>
        /// Prepares the overlay command list for this frame. Repeated calls within one frame keep
        /// accumulating, so queued <see cref="DrawText"/> calls survive until the overlay composes.
        /// </summary>
        private bool EnsureOverlayFrame()
        {
            if (_gpuSwapChain == null || !_gpuSwapChain.IsReady || _gpuSwapChain.Width <= 0 || _gpuSwapChain.Height <= 0) return false;

            _glyphAtlas ??= new GlyphAtlas();
            if (_overlayCommands.Width != _gpuSwapChain.Width || _overlayCommands.Height != _gpuSwapChain.Height)
            {
                if (_overlayCommands.Count > 0)
                {
                    // The surface changed mid-frame; queued commands were laid out for the old size
                    // and re-anchoring them is guesswork, so drop them rather than draw them wrong.
                    RenderLog.Line(
                        $"Overlay surface changed to {_gpuSwapChain.Width}x{_gpuSwapChain.Height} with "
                        + $"{_overlayCommands.Count} commands queued; the queue was discarded.");
                }

                _overlayCommands.Reset(_gpuSwapChain.Width, _gpuSwapChain.Height);
            }

            return true;
        }

        /// <summary>
        /// Submits the overlay as GPU primitives and flushes them onto the back buffer.
        /// </summary>
        /// <remarks>
        /// Every command shares one depth, so the sprite renderer's stable sort preserves submission
        /// order and the overlay paints exactly as it was authored. Text and shapes end up in the
        /// same batch, and after a glyph's first appearance the whole path is GPU-only.
        /// </remarks>
        private bool FlushOverlay()
        {
            if (_overlayCommands.Count == 0)
            {
                _overlayCommands.Reset(_gpuSwapChain.Width, _gpuSwapChain.Height);
                return false;
            }

            bool submitted = false;
            _glyphQuads.Clear();

            foreach (OverlayCommand command in _overlayCommands.Commands)
            {
                switch (command.Kind)
                {
                    case OverlayCommandKind.Rect:
                        SubmitOverlayRect(command);
                        submitted = true;
                        break;

                    case OverlayCommandKind.Line:
                        _spr?.SubmitLine(command.A, command.B, command.C, command.D,
                            ToRenderColor(command.Color), command.Stroke, OverlayDepth);
                        submitted = true;
                        break;

                    case OverlayCommandKind.Text:
                        submitted |= SubmitOverlayText(
                            command, command.A, command.B, command.Color);
                        break;

                    case OverlayCommandKind.TextCentered:
                    {
                        float width = _glyphAtlas.MeasureRun(
                            command.Text, command.FontFamily, command.Size, command.Bold);
                        submitted |= SubmitOverlayText(
                            command, command.A - (width * 0.5f), command.B, command.Color);
                        break;
                    }
                }
            }

            _overlayCommands.Reset(_gpuSwapChain.Width, _gpuSwapChain.Height);

            // Newly rasterised glyphs are the only CPU→GPU traffic the overlay ever generates, and
            // only on a glyph's first appearance. A steady HUD drains nothing here.
            UploadNewGlyphs();

            if (!submitted) return false;

            _spr?.UploadPendingInstances();
            BeginPass(GpuRenderTargetHandle.Invalid, GpuTextureHandle.Invalid,
                _gpuSwapChain.Width, _gpuSwapChain.Height, clearDepth: false, "Controller.OverlayCompose");

            _spr?.Flush(_gpuSwapChain.Width, _gpuSwapChain.Height, ResolveGpuTexture, _whiteTexture,
                _gpuSwapChain.Width / 2f, _gpuSwapChain.Height / 2f, 1f);
            _gpu.EndRenderPass();
            return true;
        }

        private void SubmitOverlayRect(in OverlayCommand command)
        {
            RenderColor color = ToRenderColor(command.Color);
            if (command.Filled)
            {
                _spr?.SubmitRect(command.A, command.B, command.C, command.D, color,
                    filled: true, depth: OverlayDepth);
                return;
            }

            // Four explicit lines rather than SubmitRect's outline, which hard-codes 1px and would
            // silently ignore the stroke width the canvas contract accepts.
            float x = command.A, y = command.B, w = command.C, h = command.D, s = command.Stroke;
            _spr?.SubmitLine(x, y, x + w, y, color, s, OverlayDepth);
            _spr?.SubmitLine(x + w, y, x + w, y + h, color, s, OverlayDepth);
            _spr?.SubmitLine(x + w, y + h, x, y + h, color, s, OverlayDepth);
            _spr?.SubmitLine(x, y + h, x, y, color, s, OverlayDepth);
        }

        private bool SubmitOverlayText(in OverlayCommand command, float x, float topY, Vector4 color)
        {
            _glyphQuads.Clear();
            _glyphAtlas.LayoutRun(
                command.Text, command.FontFamily, command.Size, command.Bold, x, topY, _glyphQuads);
            if (_glyphQuads.Count == 0) return false;

            EnsureGlyphAtlasTexture();
            RenderColor tint = ToRenderColor(color);

            for (int i = 0; i < _glyphQuads.Count; i++)
            {
                GlyphQuad quad = _glyphQuads[i];
                _spr?.Submit(new SpriteDrawCall
                {
                    Texture = _glyphAtlasTexture,
                    X = quad.X,
                    Y = quad.Y,
                    Width = quad.Width,
                    Height = quad.Height,
                    ScaleX = 1,
                    ScaleY = 1,
                    Alpha = 1f,
                    // Atlas texels are white with coverage in alpha, so the tint IS the text colour.
                    Tint = tint,
                    Depth = OverlayDepth,
                    UvRect = new Vector4(quad.U0, quad.V0, quad.U1, quad.V1),
                });
            }

            return true;
        }

        private void EnsureGlyphAtlasTexture()
        {
            if (_glyphAtlasGpuTexture.IsValid) return;

            _glyphAtlasGpuTexture = _gpu.CreateTexture(new GpuTextureDesc
            {
                Width = GlyphAtlas.AtlasSize,
                Height = GlyphAtlas.AtlasSize,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = GpuFormat.B8G8R8A8UNorm,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Renderer.GlyphAtlas",
            }, ReadOnlySpan<byte>.Empty);
            _texGpu.Add(_glyphAtlasGpuTexture);
            _glyphAtlasTexture = new TextureHandle(_texGpu.Count);
        }

        private void UploadNewGlyphs()
        {
            IReadOnlyList<GlyphUpload> uploads = _glyphAtlas.DrainUploads();
            if (uploads.Count == 0) return;

            EnsureGlyphAtlasTexture();
            for (int i = 0; i < uploads.Count; i++)
            {
                GlyphUpload upload = uploads[i];
                _gpu.UpdateTexture(
                    _glyphAtlasGpuTexture, upload.X, upload.Y, upload.Width, upload.Height,
                    upload.Pixels);
            }
        }

        private static RenderColor ToRenderColor(Vector4 color) =>
            new(color.X, color.Y, color.Z, color.W);

        private void ReleaseOverlayTexture()
        {
            if (!_glyphAtlasGpuTexture.IsValid) return;
            InvalidateTextureRegistry(_glyphAtlasTexture, releaseGpu: true);
            _glyphAtlasGpuTexture = GpuTextureHandle.Invalid;
            _glyphAtlasTexture = TextureHandle.Invalid;
        }

        public void Present()
        {
            if (!_initialized || _gpuSwapChain == null || !_gpuSwapChain.IsReady) return;

            // DrawText queues onto the overlay rather than drawing immediately, so anything still
            // pending is composited here — a caller that only ever calls DrawText never has to know
            // the overlay exists.
            if (_overlayCommands.Count > 0 && EnsureOverlayFrame()) FlushOverlay();

            _gpu.EndFrame();
            if (_gpuSwapChain != null && _gpuSwapChain.IsReady)
                _gpuSwapChain.Present(_vsync);
        }

        /// <summary>
        /// Reads the current back buffer as tightly-packed BGRA through the shared device contract.
        /// </summary>
        /// <remarks>
        /// This used to be a hand-written DX11 staging copy on this controller, which every caller
        /// had to downcast to reach. It now goes through <see cref="IGpuSwapChain.AcquireBackBuffer"/>
        /// and <see cref="IGpuDevice.TryReadTexture"/>, both of which every backend must implement,
        /// and which already guarantee top-down BGRA regardless of the backend's framebuffer origin.
        /// Call after <see cref="EndFrame"/> and BEFORE <see cref="Present"/> — flip-model
        /// presentation recycles the back buffer.
        /// </remarks>
        public bool TryReadFramePixels(out int width, out int height, out byte[] bgra)
            => TryReadFramePixelsInternal(submitFrame: true, out width, out height, out bgra);

        /// <summary>
        /// Reads the current back buffer after the host has already called <see cref="EndFrame"/>.
        /// Used by the autoshot hook, which runs between EndFrame and Present.
        /// </summary>
        public bool TryReadSubmittedFramePixels(out int width, out int height, out byte[] bgra)
            => TryReadFramePixelsInternal(submitFrame: false, out width, out height, out bgra);

        private bool TryReadFramePixelsInternal(bool submitFrame, out int width, out int height, out byte[] bgra)
        {
            width = 0; height = 0; bgra = null;
            if (!_initialized || _gpuSwapChain == null || !_gpuSwapChain.IsReady) return false;

            if (submitFrame)
            {
                // Submit the frame so far so the GPU actually draws it before we read back.
                _gpu.EndFrame();
            }

            _gpu.WaitIdle();

            GpuTextureHandle backBuffer = _gpuSwapChain.AcquireBackBuffer();
            if (!backBuffer.IsValid) return false;

            if (!_gpu.TryReadTexture(backBuffer, out width, out height, out bgra)) return false;

            MakeFrameOpaque(bgra);
            return true;
        }

        /// <summary>
        /// Forces the readback's alpha channel opaque (NEXT-091).
        /// </summary>
        /// <remarks>
        /// The swap chain is created with <c>AlphaMode.Unspecified</c>, so presentation ignores the
        /// back buffer's alpha entirely — but the sprite blend writes it (<c>SrcAlpha=One</c>,
        /// <c>DstAlpha=Zero</c>), so a translucent HUD panel leaves that translucency behind in the
        /// alpha channel. Preserved into a 32-bit-ARGB PNG, the capture then renders against the
        /// viewer's background rather than the game's, and looks nothing like the presented frame.
        /// Carrying alpha through would make every capture of a translucent HUD a lie.
        /// </remarks>
        private static void MakeFrameOpaque(byte[] bgra)
        {
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        }

        // ── Viewport / camera ────────────────────────────────────────────────────

        public void SetViewport(int x, int y, int width, int height)
        {
            if (!_initialized) return;
            _gpu.SetViewport(x, y, width, height);
        }

        public void SetCamera2D(float x, float y, float zoom, float rotation)
        {
            // Camera stored; applied by SpriteRenderer.Flush via the orthographic CB upload.
            // SpriteRenderer uses pixel-space internally — this method maps to a 2D pan/zoom offset
            // if callers override the transform. For the current demos the renderer uses full-pixel
            // ortho, so we store the offset/zoom for the window to use when building sprite positions.
            _cam2DX = x; _cam2DY = y; _cam2DZoom = Math.Max(zoom, 0.001f);
        }

        private float _cam2DX, _cam2DY, _cam2DZoom = 1f;
        private bool _frame3DActive = true;

        public void Set3DFrameActive(bool active) => _frame3DActive = active;

        public void SetCamera3D(Matrix4x4 view, Matrix4x4 projection)
        {
            _fwd?.SetCamera(view, projection);
        }

        public void SetBlendMode(BlendMode mode)    { }
        public void SetSamplerState(SamplerFilter f) => _spr?.SetSamplerFilter(f);
        public void SetRoomFog(RoomFogState state)
        {
            _spriteFog = state;
            _spr?.SetFog(state);
        }

        // ── 2D ───────────────────────────────────────────────────────────────────

        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls)
        {
            if (_spr == null) return;
            for (int i = 0; i < calls.Length; i++)
                _spr.Submit(calls[i]);
        }

        public void DrawSprite(in SpriteDrawCall call)
        {
            if (_spr == null) return;
            _spr.Submit(call);
        }

        public void DrawLine(float x1, float y1, float x2, float y2, RenderColor color, float thickness = 1f, int depth = -10000)
            => _spr?.SubmitLine(x1, y1, x2, y2, color, thickness, depth);

        public void DrawRect(float x, float y, float w, float h, RenderColor color, bool filled = true, int depth = -10000)
            => _spr?.SubmitRect(x, y, w, h, color, filled, depth);

        /// <summary>
        /// Queues screen-space text onto the shared overlay. It is rasterised and composited when
        /// the overlay is composed, or at <see cref="Present"/> if the caller never composes one.
        /// </summary>
        public void DrawText(string text, float x, float y, float size, RenderColor color)
        {
            if (string.IsNullOrEmpty(text) || !EnsureOverlayFrame()) return;
            _overlayCommands.DrawText(
                text,
                new Vector2(x, y),
                size > 0.1f ? size : 12f,
                new Vector4(color.R, color.G, color.B, color.A));
        }

        // ── 3D ───────────────────────────────────────────────────────────────────

        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls)
        {
            if (_fwd == null) return;
            for (int i = 0; i < calls.Length; i++)
            {
                ref readonly var c = ref calls[i];
                if (!c.Mesh.IsValid) continue;

                float alpha    = c.Alpha > 0f ? c.Alpha : 1f;
                float emissive = c.Emissive;
                if ((c.Flags & MeshDrawFlags.Emissive) != 0 && emissive <= 0f) emissive = 0.5f;

                var color  = new Vector4(c.Tint.R, c.Tint.G, c.Tint.B, alpha);
                GpuTextureHandle texture = GetGpuTexture(c.Texture);
                GpuTextureHandle normal = ResolveGpuTexture(c.NormalMap.Id);
                GpuTextureHandle orm = ResolveGpuTexture(c.OrmMap.Id);
                GpuTextureHandle height = ResolveGpuTexture(c.HeightMap.Id);
                GpuTextureHandle emission = ResolveGpuTexture(c.EmissionMap.Id);
                GpuTextureHandle extras = ResolveGpuTexture(c.ExtrasMap.Id);
                GpuTextureHandle flow = ResolveGpuTexture(c.FlowMap.Id);
                int  texId     = c.Texture.IsValid ? c.Texture.Id : 0;

                Vector4 waterDeep = default, waterParams = default, skyHorizon = default, skyZenith = default;
                if ((c.Flags & MeshDrawFlags.Water) != 0)
                {
                    waterDeep = new Vector4(c.DeepTint.R, c.DeepTint.G, c.DeepTint.B, c.DeepTint.A);
                    waterParams = c.WaterParams;
                    skyHorizon = c.SkyHorizon;
                    skyZenith = c.SkyZenith;
                }

                _fwd.Submit(c.Mesh, c.World, color, texId, texture, c.Flags, emissive, c.ShadowMesh,
                    normal: normal, chunkId: c.ChunkId, atlasLayer: c.AtlasLayer,
                    waterDeep: waterDeep, waterParams: waterParams, skyHorizon: skyHorizon, skyZenith: skyZenith,
                    normalId: c.NormalMap.Id, ormId: c.OrmMap.Id, heightId: c.HeightMap.Id,
                    emissionId: c.EmissionMap.Id, extrasId: c.ExtrasMap.Id, flowId: c.FlowMap.Id,
                    orm: orm, height: height, emission: emission,
                    extras: extras, flow: flow, heightMode: c.HeightMode,
                    surfaceParams: c.SurfaceParams, detailParams: c.DetailParams,
                    subsurfaceColorSteps: c.SubsurfaceColorSteps, skinPalette: c.SkinPalette,
                    shader: c.Shader, shaderParams0: c.ShaderParams0, shaderParams1: c.ShaderParams1,
                    shaderParams2: c.ShaderParams2, shaderParams3: c.ShaderParams3,
                    authoredTextures: ResolveAuthoredTextures(c.AuthoredTextures));
            }
        }

        public void DrawMesh(in MeshDrawCall call)
        {
            MeshDrawCall single = call;
            DrawMeshBatch(MemoryMarshal.CreateReadOnlySpan(ref single, 1));
        }

        public void DrawMeshInstances(in MeshDrawCall template, ReadOnlySpan<MeshInstanceData> instances)
        {
            if (_fwd == null || !template.Mesh.IsValid || instances.IsEmpty) return;

            float emissive = template.Emissive;
            if ((template.Flags & MeshDrawFlags.Emissive) != 0 && emissive <= 0f) emissive = 0.5f;
            _fwd.SubmitInstances(
                template.Mesh,
                instances,
                template.Texture.IsValid ? template.Texture.Id : 0,
                GetGpuTexture(template.Texture),
                template.Flags,
                emissive,
                normal: ResolveGpuTexture(template.NormalMap.Id),
                shader: template.Shader,
                shaderParams0: template.ShaderParams0,
                shaderParams1: template.ShaderParams1,
                shaderParams2: template.ShaderParams2,
                shaderParams3: template.ShaderParams3,
                authoredTextures: ResolveAuthoredTextures(template.AuthoredTextures));
        }

        private GpuTextureHandle GetGpuTexture(TextureHandle handle)
        {
            GpuTextureHandle texture = ResolveGpuTexture(handle.Id);
            return texture.IsValid ? texture : _whiteTexture;
        }

        private AuthoredGpuTextures ResolveAuthoredTextures(in AuthoredShaderTextures source)
        {
            var resolved = new AuthoredGpuTextures { Count = source.Count };
            if (source.Count > 0)
            {
                resolved.Slot0 = source.Slot0;
                resolved.Tex0 = ResolveGpuTexture(source.Handle0.Id);
                resolved.TexId0 = source.Handle0.Id;
            }
            if (source.Count > 1)
            {
                resolved.Slot1 = source.Slot1;
                resolved.Tex1 = ResolveGpuTexture(source.Handle1.Id);
                resolved.TexId1 = source.Handle1.Id;
            }
            if (source.Count > 2)
            {
                resolved.Slot2 = source.Slot2;
                resolved.Tex2 = ResolveGpuTexture(source.Handle2.Id);
                resolved.TexId2 = source.Handle2.Id;
            }
            if (source.Count > 3)
            {
                resolved.Slot3 = source.Slot3;
                resolved.Tex3 = ResolveGpuTexture(source.Handle3.Id);
                resolved.TexId3 = source.Handle3.Id;
            }

            return resolved;
        }

        public MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => _fwd?.RegisterMesh(vertices, indices) ?? MeshHandle.Invalid;

        public MeshHandle RegisterSkinnedMesh(ReadOnlySpan<SkinnedMeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => _fwd?.RegisterSkinnedMesh(vertices, indices) ?? MeshHandle.Invalid;

        public SkinPaletteHandle CreateSkinPalette(int matrixCount)
            => _fwd?.CreateSkinPalette(matrixCount) ?? SkinPaletteHandle.Invalid;

        public void UpdateSkinPalette(SkinPaletteHandle handle, ReadOnlySpan<Matrix4x4> matrices)
            => _fwd?.UpdateSkinPalette(handle, matrices);

        public void ReleaseSkinPalette(SkinPaletteHandle handle)
            => _fwd?.ReleaseSkinPalette(handle);

        public MeshHandle RegisterCombinedMesh(ReadOnlySpan<MeshCombinePart> parts)
        {
            if (parts.Length == 0) return MeshHandle.Invalid;
            var (verts, indices) = Meshes.MeshCombiner.Combine(parts);
            if (verts.Length == 0) return MeshHandle.Invalid;
            return RegisterMesh(verts, indices);
        }

        public void UpdateMesh(MeshHandle handle, ReadOnlySpan<MeshVertex> vertices)
            => _fwd?.UpdateMesh(handle, vertices);

        public void ReleaseMesh(MeshHandle handle) => _fwd?.ReleaseMesh(handle);

        public void SetMesh3DState(Mesh3DState state)
            => _fwd?.SetState(state);

        public MeshHandle GetBuiltinMesh(BuiltinMeshKind kind) => kind switch
        {
            BuiltinMeshKind.Floor => _fwd?.FloorMesh ?? MeshHandle.Invalid,
            BuiltinMeshKind.Sun   => _fwd?.SunMesh   ?? MeshHandle.Invalid,
            BuiltinMeshKind.Cube  => _fwd?.CubeMesh  ?? MeshHandle.Invalid,
            BuiltinMeshKind.Sphere => _fwd?.SphereMesh ?? MeshHandle.Invalid,
            _                     => MeshHandle.Invalid,
        };

        public void Advance3DTime(float dt)
        {
            _fwd?.AddDeltaTime(dt);
            AdvanceShaderTime(dt);
        }

        public void AdvanceShaderPreviewTime(float dt) => AdvanceShaderTime(dt);

        // ── Lighting ─────────────────────────────────────────────────────────────

        public void AddPointLight(Vector3 position, Vector3 color, float radius, float intensity = 1f, float falloff = 2f)
            => _fwd?.AddPointLight(position, color, radius, intensity, falloff);

        public void ClearPointLights()
            => _fwd?.ClearPointLights();

        public void AddFogVolume(FogVolume volume)
            => _fwd?.AddFogVolume(volume);

        public void ClearFogVolumes()
            => _fwd?.ClearFogVolumes();

        public void AddSmokeVolume(Vector3 center, float radius, float density)
            => _fwd?.AddSmokeVolume(center, radius, density);

        public void ClearSmokeVolumes()
            => _fwd?.ClearSmokeVolumes();

        public void SetWeatherMapRgba8(ReadOnlySpan<byte> rgba, int width, int height, float worldHalfExtent, Vector2 worldCenter = default)
            => _fwd?.SetWeatherMapRgba8(rgba, width, height, worldHalfExtent, worldCenter);

        // ── Chunk culling ─────────────────────────────────────────────────────────

        public void SetChunkBounds(int chunkId, Vector3 boundsMin, Vector3 boundsMax)
            => _fwd?.SetChunkBounds(chunkId, boundsMin, boundsMax);

        public void ClearChunkBounds()
            => _fwd?.ClearChunkBounds();

        // ── Texture management ────────────────────────────────────────────────────

        public TextureHandle LoadTexture(string path)
            => LoadTexture(path, Genesis.Shared.Materials.TextureColorSpace.Srgb);

        public TextureHandle LoadTexture(string path, Genesis.Shared.Materials.TextureColorSpace colorSpace)
        {
            if (!_initialized) return TextureHandle.Invalid;
            return _textureCache.Load(path, colorSpace, LoadTextureUncached, ReleaseTextureUncached);
        }

        private TextureHandle LoadTextureUncached(
            string fullPath,
            Genesis.Shared.Materials.TextureColorSpace colorSpace)
        {
            try
            {
                // Prefer a fresh cooked DDS. This bypasses image decode and uploads the complete
                // pre-generated mip chain directly in BC5/BC7 form. If the manifest is absent,
                // malformed or stale, the editable source remains the authoritative fallback.
                // The CPU rasterizer cannot sample BCn payloads, so Software always decodes the
                // editable RGBA source instead of uploading compressed blocks as if they were pixels.
                bool softwareRasterizer = string.Equals(
                    _gpu.BackendName, "Software", StringComparison.Ordinal);
                if (!softwareRasterizer &&
                    Genesis.Shared.Assets.CookedTextureManifestStore.TryResolve(
                        fullPath, out string cookedPath, out Genesis.Shared.Assets.CookedTextureManifest _) &&
                    DdsTextureData.TryLoad(cookedPath, colorSpace, out DdsTextureData cooked))
                {
                    GpuTextureHandle gpuTexture = _gpu.CreateTexture(new GpuTextureDesc
                    {
                        Width = cooked.Width,
                        Height = cooked.Height,
                        MipLevels = cooked.MipLevels,
                        ArrayLayers = 1,
                        Format = cooked.Format,
                        Usage = GpuBufferUsage.Immutable,
                        BindFlags = GpuBindFlags.ShaderResource,
                        DebugName = "Renderer.CookedTexture",
                    }, cooked.Payload);
                    _texGpu.Add(gpuTexture);
                    return new TextureHandle(_texGpu.Count);
                }

                using var stream = File.OpenRead(fullPath);
                ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                if (image == null || image.Width <= 0 || image.Height <= 0)
                    return TextureHandle.Invalid;
                return CreateTexture(image.Width, image.Height, image.Data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Renderer] LoadTexture failed: {fullPath} — {ex.Message}");
                return TextureHandle.Invalid;
            }
        }

        private static bool TextureMipsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable("GENESIS_TEXTURE_MIPS"), "1", StringComparison.Ordinal);

        private static uint CalcMipLevels(int w, int h)
        {
            if (!TextureMipsEnabled)
                return 1;
            int maxDim = Math.Max(w, h);
            if (maxDim < 128)
                return 1;
            uint levels = 1;
            while (maxDim > 1)
            {
                levels++;
                maxDim >>= 1;
            }
            return levels;
        }

        public TextureHandle CreateTexture(int w, int h, ReadOnlySpan<byte> rgba)
        {
            if (!_initialized || w <= 0 || h <= 0 || rgba.Length < checked(w * h * 4))
                return TextureHandle.Invalid;
            try
            {
                GpuTextureHandle texture = _gpu.CreateTexture(new GpuTextureDesc
                {
                    Width = w,
                    Height = h,
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Format = GpuFormat.R8G8B8A8UNorm,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.ShaderResource,
                    DebugName = "Renderer.Texture",
                }, rgba);
                _texGpu.Add(texture);
                return new TextureHandle(_texGpu.Count);  // public registry remains 1-based
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Renderer] CreateTexture failed: {ex.Message}");
                return TextureHandle.Invalid;
            }
        }

        public void UpdateTexture(TextureHandle handle, int w, int h, ReadOnlySpan<byte> rgba)
        {
            if (!_initialized || w <= 0 || h <= 0 || !handle.IsValid ||
                rgba.Length < checked(w * h * 4)) return;
            GpuTextureHandle texture = ResolveGpuTexture(handle.Id);
            if (!texture.IsValid) return;
            _gpu.UpdateTexture(texture, 0, 0, w, h, rgba);
        }

        public void ReleaseTexture(TextureHandle handle)
        {
            InvalidateTextureRegistry(handle, releaseGpu: true);
        }

        private void InvalidateTextureRegistry(TextureHandle handle, bool releaseGpu)
        {
            _textureCache.Remove(handle);
            ReleaseTextureRegistryEntry(handle, releaseGpu);
        }

        private void ReleaseTextureUncached(TextureHandle handle)
            => ReleaseTextureRegistryEntry(handle, releaseGpu: true);

        private void ReleaseTextureRegistryEntry(TextureHandle handle, bool releaseGpu)
        {
            int idx = handle.Id - 1;
            if (idx < 0 || idx >= _texGpu.Count) return;
            if (releaseGpu && _texGpu[idx].IsValid) _gpu.ReleaseTexture(_texGpu[idx]);
            _texGpu[idx] = GpuTextureHandle.Invalid;
        }

        // ── Render targets ────────────────────────────────────────────────────────

        /// <summary>
        /// One offscreen target: colour texture + RTV, its own depth buffer, and an SRV registered
        /// in the ordinary texture registry so the result can be sampled by a later pass.
        /// </summary>
        private struct RenderTargetEntry
        {
            // The render target owns its colour/depth textures. ColorTexture is only the public
            // registry alias used to submit the colour attachment for sampling; it is invalidated
            // without releasing the target-owned texture separately.
            public GpuRenderTargetHandle Target;
            public GpuTextureHandle DepthTexture;
            public int           Width;
            public int           Height;
            public TextureHandle ColorTexture;
            public bool          IsReleased;
        }

        private readonly System.Collections.Generic.List<RenderTargetEntry> _renderTargets = new();

        /// <summary>Id of the bound target; 0 means the swap-chain back buffer.</summary>
        private int _activeRenderTarget;

        public RenderTargetHandle CreateRenderTarget(int w, int h)
        {
            if (!_initialized || w <= 0 || h <= 0) return RenderTargetHandle.Invalid;

            GpuRenderTargetHandle target = GpuRenderTargetHandle.Invalid;
            int colorRegistryIndex = -1;

            try
            {
                // B8G8R8A8_UNORM matches the swap chain, so a target can be composited or read back
                // through exactly the same paths as the back buffer with no format conversion.
                target = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = w,
                    Height = h,
                    ColorFormats = new[] { GpuFormat.B8G8R8A8UNorm },
                    DepthFormat = GpuFormat.D32Float,
                    DepthSampleable = true,
                    DebugName = "Renderer.OffscreenColor",
                });
                GpuTextureHandle colorGpu = _gpu.GetRenderTargetTexture(target);
                GpuTextureHandle depthGpu = _gpu.GetRenderTargetDepthTexture(target);

                // Share the texture registry so the colour attachment is an ordinary TextureHandle
                // and every existing sampling path works on it unchanged.
                _texGpu.Add(colorGpu);
                colorRegistryIndex = _texGpu.Count - 1;
                var colorHandle = new TextureHandle(_texGpu.Count);

                _renderTargets.Add(new RenderTargetEntry
                {
                    Target       = target,
                    DepthTexture = depthGpu,
                    Width        = w,
                    Height       = h,
                    ColorTexture = colorHandle,
                });

                return new RenderTargetHandle(_renderTargets.Count);   // 1-based
            }
            catch (Exception ex)
            {
                RenderLog.Line($"CreateRenderTarget({w}x{h}) failed: {ex.Message}");
                if (colorRegistryIndex >= 0)
                    _texGpu[colorRegistryIndex] = GpuTextureHandle.Invalid;
                if (target.IsValid) _gpu.ReleaseRenderTarget(target);
                return RenderTargetHandle.Invalid;
            }
        }

        /// <summary>The colour attachment, bindable as an ordinary texture for a later pass.</summary>
        public TextureHandle GetRenderTargetTexture(RenderTargetHandle handle) =>
            TryGetRenderTarget(handle, out RenderTargetEntry entry)
                ? entry.ColorTexture
                : TextureHandle.Invalid;

        public void SetRenderTarget(RenderTargetHandle handle)
        {
            if (!TryGetRenderTarget(handle, out RenderTargetEntry entry)) return;

            BeginPass(entry.Target, GpuTextureHandle.Invalid, entry.Width, entry.Height,
                clearDepth: false, "Controller.SetRenderTarget");
            _activeRenderTarget = handle.Id;
        }

        public void SetRenderTargetDefault()
        {
            _activeRenderTarget = 0;
            if (!_initialized || _gpuSwapChain == null || !_gpuSwapChain.IsReady) return;

            BeginPass(GpuRenderTargetHandle.Invalid, GpuTextureHandle.Invalid,
                _gpuSwapChain.Width, _gpuSwapChain.Height, clearDepth: false, "Controller.BackBuffer");
        }

        public void ReleaseRenderTarget(RenderTargetHandle handle)
        {
            if (!TryGetRenderTarget(handle, out RenderTargetEntry entry)) return;

            if (_activeRenderTarget == handle.Id)
                SetRenderTargetDefault();

            _gpu.ReleaseRenderTarget(entry.Target);
            entry.IsReleased = true;
            _renderTargets[handle.Id - 1] = entry;

            // The colour attachment's registry slot goes with it — anything still holding that
            // TextureHandle would otherwise sample a destroyed resource.
            InvalidateTextureRegistry(entry.ColorTexture, releaseGpu: false);
        }

        private bool TryGetRenderTarget(RenderTargetHandle handle, out RenderTargetEntry entry)
        {
            entry = default;
            if (!_initialized || !handle.IsValid) return false;

            int index = handle.Id - 1;
            if (index < 0 || index >= _renderTargets.Count) return false;

            entry = _renderTargets[index];
            return !entry.IsReleased && entry.Target.IsValid;
        }

        // ── Stats ─────────────────────────────────────────────────────────────────

        public RenderStats GetStats()
        {
            int spriteDraws = _spr != null ? Math.Max(_spr.FrameDrawCalls, _spr.LastDrawCalls) : 0;
            int spriteInstances = _spr != null
                ? Math.Max(_spr.FrameInstancesDrawn, _spr.LastInstancesDrawn)
                : 0;
            int meshInstances = _fwd?.LastFrameInstancesDrawn ?? 0;
            return new RenderStats
            {
                DrawCalls          = (_fwd?.LastDrawCalls ?? 0) + spriteDraws,
                Triangles          = (_fwd?.LastTriangles ?? 0) + (_spr != null ? Math.Max(_spr.FrameTriangles, _spr.LastTriangles) : 0),
                TextureSwitches    = _spr != null ? Math.Max(_spr.FrameTextureSwitches, _spr.LastTextureSwitches) : 0,
                DrawCalls2D        = spriteDraws,
                DrawCalls3D        = _fwd?.LastDrawCalls ?? 0,
                Triangles3D        = _fwd?.LastTriangles ?? 0,
                ItemsSubmitted     = _fwd?.LastItemsSubmitted ?? 0,
                InstancesDrawn     = meshInstances + spriteInstances,
                InstancesCulled    = _fwd?.LastFrameInstancesCulled ?? 0,
                SpriteInstances    = spriteInstances,
                MeshInstances      = meshInstances,
                SpriteInstanceCap  = RenderCapacityDefaults.SpriteInstanceCap,
                MeshInstanceCap    = RenderCapacityDefaults.MeshInstanceCap,
                LightsUsed         = _fwd?.LastFramePointLights ?? 0,
                LightsCap          = RenderCapacityDefaults.EffectiveShadedLightCap,
                Batches            = (_fwd?.LastFrameBatchCount ?? 0) + spriteDraws,
                FoliageInstances   = _fwd?.LastFrameFoliageInstances ?? 0,
                FoliageBatches     = _fwd?.LastFrameFoliageBatches ?? 0,
                FoliageUploadBytes = _fwd?.LastFrameFoliageUploadBytes ?? 0,
                WorldMeshes        = _fwd?.LastFrameWorldMeshes ?? 0,
                GpuMs              = _fwd?.LastGpuMs ?? 0.0,
                AoMs               = _fwd?.LastAoMs ?? 0.0,
                ContactShadowMs    = _fwd?.LastContactShadowMs ?? 0.0,
                LocalVolumetricMs  = _fwd?.LastLocalVolumetricMs ?? 0.0,
                SmokeExtinctionMs  = _fwd?.LastSmokeExtinctionMs ?? 0.0,
                BloomMs            = _fwd?.LastBloomMs ?? 0.0,
                AtmosphereLutMs    = _fwd?.LastAtmosphereLutMs ?? 0.0,
                RaymarchedCloudsMs = _fwd?.LastRaymarchedCloudsMs ?? 0.0,
                CelestialExtrasMs  = _fwd?.LastCelestialExtrasMs ?? 0.0,
            };
        }

        // ── Editor Shader Overrides ─────────────────────────────────────────────

        private GpuBufferHandle           _previewOverrideCb = GpuBufferHandle.Invalid;
        private GpuBufferHandle           _previewParametersCb = GpuBufferHandle.Invalid;
        private Vector4                   _previewParameters0, _previewParameters1, _previewParameters2, _previewParameters3;
        private float                     _previewOverrideTime;
        private float                     _previewOverrideFrame;
        private ShaderPreviewProfile      _previewProfile = ShaderPreviewProfile.SpritePipeline;
        private ShaderPreviewPass         _shaderPreview;
        private AuthoredGpuTextures       _previewAuthoredTextures;

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderEditorConstants
        {
            public float Time;
            public float Frame;
            public float ResX;
            public float ResY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderEditorParameters
        {
            public Vector4 Row0, Row1, Row2, Row3;
        }

        public void SetPreviewShaderOverride(
            string hlslSourceCode,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null,
            string entryPoint = null,
            string compilerProfile = null)
        {
            if (!_initialized) return;

            var includeRoots = ShaderCompiler.BuildDefaultIncludeSearchPaths(sourcePath, projectPath);
            byte[] psBlob = ShaderCompiler.CompileForBackend(
                hlslSourceCode,
                string.IsNullOrWhiteSpace(entryPoint) ? "MainPS" : entryPoint,
                GpuShaderStage.Pixel,
                _gpu.ShaderBinaryFormat,
                sourcePath,
                includeRoots).Blob;
            ApplyPreviewShaderBytecode(psBlob, profile);
        }

        public void SetPreviewShaderProgramOverride(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            string sourcePath = null,
            ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline,
            string projectPath = null)
        {
            if (!_initialized) return;
            if (string.IsNullOrWhiteSpace(vertexEntryPoint))
            {
                SetPreviewShaderOverride(
                    hlslSourceCode,
                    sourcePath,
                    profile,
                    projectPath,
                    pixelEntryPoint);
                return;
            }

            var includeRoots = ShaderCompiler.BuildDefaultIncludeSearchPaths(sourcePath, projectPath);
            byte[] vertex = ShaderCompiler.CompileForBackend(
                hlslSourceCode,
                vertexEntryPoint.Trim(),
                GpuShaderStage.Vertex,
                _gpu.ShaderBinaryFormat,
                sourcePath,
                includeRoots).Blob;
            byte[] pixel = ShaderCompiler.CompileForBackend(
                hlslSourceCode,
                string.IsNullOrWhiteSpace(pixelEntryPoint) ? "MainPS" : pixelEntryPoint.Trim(),
                GpuShaderStage.Pixel,
                _gpu.ShaderBinaryFormat,
                sourcePath,
                includeRoots).Blob;

            if (profile == ShaderPreviewProfile.SpritePipeline)
            {
                _spr?.SetShaderProgramOverride(vertex, pixel);
                _fwd?.SetPixelShaderOverride(null);
                _shaderPreview?.SetPixelShader(null);
            }
            else if (profile == ShaderPreviewProfile.MeshPipeline)
            {
                _fwd?.SetShaderProgramOverride(vertex, pixel);
                _spr?.ClearPixelShaderOverride();
                _shaderPreview?.SetPixelShader(null);
            }
            else
            {
                _shaderPreview?.SetShaderProgram(vertex, pixel);
                _spr?.ClearPixelShaderOverride();
                _fwd?.SetPixelShaderOverride(null);
            }

            _previewProfile = profile;
            EnsureShaderFrameBuffer();
        }

        public void ApplyPreviewShaderBytecode(byte[] pixelShaderBytecode, ShaderPreviewProfile profile = ShaderPreviewProfile.SpritePipeline)
        {
            if (!_initialized || pixelShaderBytecode == null || pixelShaderBytecode.Length == 0) return;

            // Install first. A failed compile/link must leave the last working preview intact.
            if (profile == ShaderPreviewProfile.SpritePipeline)
            {
                _spr?.SetPixelShaderOverride(pixelShaderBytecode);
                _fwd?.SetPixelShaderOverride(null);
                _shaderPreview?.SetPixelShader(null);
            }
            else if (profile == ShaderPreviewProfile.MeshPipeline)
            {
                _fwd?.SetPixelShaderOverride(pixelShaderBytecode);
                _spr?.ClearPixelShaderOverride();
                _shaderPreview?.SetPixelShader(null);
            }
            else
            {
                _shaderPreview?.SetPixelShader(pixelShaderBytecode);
                _spr?.ClearPixelShaderOverride();
                _fwd?.SetPixelShaderOverride(null);
            }

            _previewProfile = profile;
            EnsureShaderFrameBuffer();
        }

        public void ClearPreviewShaderOverride()
        {
            _spr?.ClearPixelShaderOverride();
            _fwd?.SetPixelShaderOverride(null);
            _shaderPreview?.SetPixelShader(null);
        }

        public RuntimeShaderHandle RegisterRuntimeShader(
            string hlslSourceCode,
            string entryPoint,
            ShaderPreviewProfile profile,
            string sourcePath = null,
            string projectPath = null)
        {
            if (!_initialized || string.IsNullOrWhiteSpace(hlslSourceCode)) return RuntimeShaderHandle.Invalid;
            if (profile == ShaderPreviewProfile.FullscreenEffect)
                throw new NotSupportedException("Fullscreen shaders are preview effects and cannot be bound to an object draw.");
            string resolvedEntryPoint = string.IsNullOrWhiteSpace(entryPoint) ? "MainPS" : entryPoint;
            string compileSource = hlslSourceCode;
            var includeRoots = ShaderCompiler.BuildDefaultIncludeSearchPaths(sourcePath, projectPath);
            byte[] blob = ShaderCompiler.CompileForBackend(
                compileSource,
                resolvedEntryPoint,
                GpuShaderStage.Pixel,
                _gpu.ShaderBinaryFormat,
                sourcePath,
                includeRoots).Blob;
            EnsureShaderFrameBuffer();
            return profile == ShaderPreviewProfile.MeshPipeline
                ? _fwd.RegisterRuntimeShader(blob)
                : _spr.RegisterRuntimeShader(blob);
        }

        public RuntimeShaderHandle RegisterRuntimeShaderProgram(
            string hlslSourceCode,
            string vertexEntryPoint,
            string pixelEntryPoint,
            ShaderPreviewProfile profile,
            string sourcePath = null,
            string projectPath = null)
        {
            if (!_initialized || string.IsNullOrWhiteSpace(hlslSourceCode)) return RuntimeShaderHandle.Invalid;
            if (profile == ShaderPreviewProfile.FullscreenEffect)
                throw new NotSupportedException("Fullscreen shaders are preview effects and cannot be bound to an object draw.");
            if (string.IsNullOrWhiteSpace(vertexEntryPoint))
                return RegisterRuntimeShader(hlslSourceCode, pixelEntryPoint, profile, sourcePath, projectPath);

            var includeRoots = ShaderCompiler.BuildDefaultIncludeSearchPaths(sourcePath, projectPath);
            byte[] vertex = ShaderCompiler.CompileForBackend(
                hlslSourceCode,
                vertexEntryPoint.Trim(),
                GpuShaderStage.Vertex,
                _gpu.ShaderBinaryFormat,
                sourcePath,
                includeRoots).Blob;
            byte[] pixel = ShaderCompiler.CompileForBackend(
                hlslSourceCode,
                string.IsNullOrWhiteSpace(pixelEntryPoint) ? "MainPS" : pixelEntryPoint.Trim(),
                GpuShaderStage.Pixel,
                _gpu.ShaderBinaryFormat,
                sourcePath,
                includeRoots).Blob;
            EnsureShaderFrameBuffer();
            return profile == ShaderPreviewProfile.MeshPipeline
                ? _fwd.RegisterRuntimeShaderProgram(vertex, pixel)
                : _spr.RegisterRuntimeShaderProgram(vertex, pixel);
        }

        public void ReleaseRuntimeShader(RuntimeShaderHandle handle, ShaderPreviewProfile profile)
        {
            if (profile == ShaderPreviewProfile.MeshPipeline) _fwd?.ReleaseRuntimeShader(handle);
            else if (profile == ShaderPreviewProfile.SpritePipeline) _spr?.ReleaseRuntimeShader(handle);
        }

        private void EnsureShaderFrameBuffer()
        {
            if (_previewOverrideCb.IsValid) return;
            _previewOverrideCb = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = sizeof(ShaderEditorConstants),
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer,
                DebugName = "GenesisShader.FrameConstants",
            }, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>Set extra Texture2D resources reflected from the authored preview shader.</summary>
        public void SetShaderPreviewTextures(in AuthoredShaderTextures textures) =>
            _previewAuthoredTextures = ResolveAuthoredTextures(textures);

        public void DrawShaderPreviewFullscreen(int x = 0, int y = 0, int width = 0, int height = 0)
        {
            if (!_initialized || _previewProfile != ShaderPreviewProfile.FullscreenEffect || _shaderPreview == null)
                return;

            if (width <= 0) width = PixelWidth;
            if (height <= 0) height = PixelHeight;

            BindPreviewOverrideCb();
            _previewAuthoredTextures.Bind(_gpu);
            _shaderPreview.Draw(x, y, width, height, _previewOverrideCb);
        }

        public void ResetShaderPreviewTime()
        {
            _previewOverrideTime = 0f;
            _previewOverrideFrame = 0f;
        }

        public void SetShaderPreviewParameters(Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3)
        {
            _previewParameters0 = row0; _previewParameters1 = row1;
            _previewParameters2 = row2; _previewParameters3 = row3;
            if (!_previewParametersCb.IsValid)
            {
                _previewParametersCb = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = sizeof(Vector4) * 4,
                    Usage = GpuBufferUsage.Dynamic,
                    BindFlags = GpuBindFlags.ConstantBuffer,
                    DebugName = "ShaderPreview.Parameters",
                }, ReadOnlySpan<byte>.Empty);
            }
        }

        public void AdvanceShaderTime(float dt)
        {
            _previewOverrideTime += dt;
            _previewOverrideFrame += dt > 0f ? 1f : dt < 0f ? -1f : 0f;
        }

        internal void BindPreviewOverrideCb()
        {
            bool spriteOverride = _previewProfile == ShaderPreviewProfile.SpritePipeline
                && (_spr?.HasPixelShaderOverride ?? false);
            bool meshOverride = _previewProfile == ShaderPreviewProfile.MeshPipeline
                && (_fwd?.HasPixelShaderOverride ?? false);
            bool fullscreenOverride = _shaderPreview?.HasOverride ?? false;
            bool runtimeShader = (_spr?.HasRuntimeShaders ?? false) || (_fwd?.HasRuntimeShaders ?? false);
            if ((!spriteOverride && !meshOverride && !fullscreenOverride && !runtimeShader) ||
                !_previewOverrideCb.IsValid) return;

            _gpu.UpdateConstantBuffer(_previewOverrideCb, new ShaderEditorConstants
            {
                Time  = _previewOverrideTime,
                Frame = _previewOverrideFrame,
                ResX  = PixelWidth,
                ResY  = PixelHeight,
            });
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 4, _previewOverrideCb);
            if (_previewParametersCb.IsValid)
            {
                ShaderEditorParameters parameters = new()
                {
                    Row0 = _previewParameters0, Row1 = _previewParameters1,
                    Row2 = _previewParameters2, Row3 = _previewParameters3,
                };
                _gpu.UpdateConstantBuffer(_previewParametersCb, parameters);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 5, _previewParametersCb);
            }
        }

        /// <summary>GPU milliseconds for the last resolved frame.</summary>
        public double LastGpuMilliseconds => _fwd?.LastGpuMs ?? 0.0;
    }
}

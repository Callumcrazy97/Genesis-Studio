using System;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Genesis.Rendering.Diagnostics;

namespace Genesis.Rendering.SilkNet.DX11
{
    /// <summary>
    /// Clean-slate flip-model swap chain bound to an HWND, with a colour view and a depth view.
    /// Deliberately minimal and modelled on EngineTest's working SwapChainTarget:
    ///   • FLIP_DISCARD, 2 buffers, B8G8R8A8_UNORM, no flags (no ALLOW_TEARING)
    ///   • MakeWindowAssociation(NO_WINDOW_CHANGES | NO_ALT_ENTER) — DXGI must not auto-resize on WM_SIZE
    ///   • Resize = unbind → dispose views → Present → ResizeBuffers → recreate views (never recreate swap chain on failure)
    /// Nothing else touches the swap chain, so there are never stray back-buffer references.
    /// </summary>
    internal sealed unsafe class Dx11SwapChain : IDisposable
    {
        private const uint BufferCount = 2;
        private const Format ColorFormat = Format.FormatB8G8R8A8Unorm;
        private const uint MwaNoWindowChanges = 1;
        private const uint MwaNoAltEnter      = 2;

        // Tearing (uncapped/benchmark present). Flag must be set at creation AND on every
        // ResizeBuffers; the present flag is only valid with sync interval 0.
        private const uint DxgiSwapChainFlagAllowTearing = 2048;     // DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING (1<<11)
        private const uint DxgiPresentAllowTearing       = 0x00000200; // DXGI_PRESENT_ALLOW_TEARING

        private readonly SilkNetDx11Runtime _rt;
        private readonly IntPtr _hwnd;
        private bool _allowTearing;
        private uint _swapChainFlags;   // flags used at creation; ResizeBuffers must reuse them

        private ComPtr<IDXGISwapChain1>        _swapChain;
        private ComPtr<ID3D11RenderTargetView> _rtv;
        private ComPtr<ID3D11Texture2D>        _depthTex;
        private ComPtr<ID3D11DepthStencilView> _dsv;
        private ComPtr<ID3D11ShaderResourceView> _depthSrv;

        public int Width  { get; private set; }
        public int Height { get; private set; }

        public ID3D11RenderTargetView* Rtv => _rtv.Handle;
        public ID3D11DepthStencilView* Dsv => _dsv.Handle;
        public nint DepthSrv => (nint)_depthSrv.Handle;
        internal ID3D11Texture2D* DepthTextureHandle => _depthTex.Handle;
        public IDXGISwapChain1*        Handle => _swapChain.Handle;
        public bool IsReady => _swapChain.Handle != null && _rtv.Handle != null && _dsv.Handle != null;

        public Dx11SwapChain(SilkNetDx11Runtime rt, IntPtr hwnd, int width, int height)
        {
            _rt    = rt;
            _hwnd  = hwnd;
            Width  = Math.Max(1, width);
            Height = Math.Max(1, height);
            CreateSwapChain();
            CreateViews();
            RenderLog.Line($"Dx11SwapChain created {Width}x{Height}");
        }

        // ── Resize ────────────────────────────────────────────────────────────────

        public bool Resize(int width, int height)
        {
            // Minimized / hidden — skip until a real client size arrives.
            if (width <= 0 || height <= 0)
                return IsReady;

            int w = Math.Max(1, width);
            int h = Math.Max(1, height);
            if (w == Width && h == Height && IsReady)
                return true;

            if (_swapChain.Handle == null)
                return false;

            int oldW = Width;
            int oldH = Height;

            PrepareForResize();

            int hr = TryResizeBuffers(w, h);
            if (hr >= 0)
            {
                SyncDimensionsFromSwapChain();
                try
                {
                    CreateViews();
                    RenderLog.Line($"Resize OK via ResizeBuffers {Width}x{Height} ready={IsReady}");
                    return IsReady;
                }
                catch (Exception ex)
                {
                    RenderLog.Line($"CreateViews after resize threw: {ex.Message}");
                }
            }
            else
            {
                RenderLog.Line($"ResizeBuffers FAILED 0x{hr:X8} — restoring views, will retry (no swap-chain recreate)");
            }

            // INVALID_CALL means outstanding buffer refs or bad timing — never recreate here
            // (recreate on the same HWND while the old chain is alive → E_ACCESSDENIED).
            Width  = oldW;
            Height = oldH;
            if (TryRestoreViews())
            {
                RenderLog.Line($"Resize deferred — framebuffers restored at {Width}x{Height}");
                return false;
            }

            RenderLog.Line("Resize FAILED — could not restore framebuffers; install Graphics Tools for DXGI debug output");
            return false;
        }

        // DXGI requires: unbind pipeline → dispose all back-buffer views → Present → ResizeBuffers.
        private void PrepareForResize()
        {
            ID3D11DeviceContext* ctx = _rt.ImmediateContext.Handle;
            if (ctx != null)
            {
                ctx->OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
                ctx->ClearState();
                ctx->Flush();
            }

            ReleaseViews();

            if (ctx != null)
                ctx->Flush();

            // NOTE (NEXT-068): this used to Present here, to "complete any in-flight flip so buffer
            // refcounts drop to zero". Measurement says the refcount is already 0 by this point,
            // and on a flip-model chain a Present *queues* a frame — which is itself a reason
            // ResizeBuffers can return DXGI_ERROR_INVALID_CALL. Flushing is enough to retire
            // outstanding GPU work; presenting is not part of the required protocol.
            if (ctx != null)
                ctx->Flush();
        }

        /// <summary>
        /// Outstanding references to back buffer 0, or -1 if it could not be queried.
        /// </summary>
        /// <remarks>
        /// ResizeBuffers returns DXGI_ERROR_INVALID_CALL when anything still holds a back buffer,
        /// and says nothing about what. GetBuffer adds a reference and Release returns the count
        /// after dropping it, so the number this reports is what remains *besides* this probe: 0
        /// means the buffers are free and the failure is elsewhere (flags, timing), anything higher
        /// names the problem. See Issues.md NEXT-068.
        /// </remarks>
        private int BackBufferReferenceCount()
        {
            if (_swapChain.Handle == null) return -1;

            ID3D11Texture2D* backBuffer = null;
            Guid tex2dGuid = ID3D11Texture2D.Guid;
            if (_swapChain.Handle->GetBuffer(0, &tex2dGuid, (void**)&backBuffer) < 0 || backBuffer == null)
                return -1;

            return (int)((IUnknown*)backBuffer)->Release();
        }

        private int TryResizeBuffers(int w, int h)
        {
            RenderLog.Line(
                $"ResizeBuffers → {w}x{h}, flags=0x{_swapChainFlags:X}, " +
                $"outstanding back-buffer refs={BackBufferReferenceCount()}");

            // Flags MUST match the creation flags (tearing) or ResizeBuffers returns INVALID_CALL.
            int hr = _swapChain.Handle->ResizeBuffers(BufferCount, (uint)w, (uint)h, ColorFormat, _swapChainFlags);
            if (hr >= 0) return hr;

            // One retry after a flush: D3D11 destroys released device children lazily, so a view
            // freed microseconds ago can still be holding a back buffer on this attempt.
            Thread.Sleep(1);
            _rt.ImmediateContext.Handle->Flush();
            return _swapChain.Handle->ResizeBuffers(BufferCount, (uint)w, (uint)h, ColorFormat, _swapChainFlags);
        }

        private bool TryRestoreViews()
        {
            if (_swapChain.Handle == null) return false;
            if (IsReady) return true;
            try
            {
                CreateViews();
                return IsReady;
            }
            catch (Exception ex)
            {
                RenderLog.Line($"TryRestoreViews threw: {ex.Message}");
                return false;
            }
        }

        private void DestroySwapChain()
        {
            ClearWindowAssociation();
            _swapChain.Dispose();
            _swapChain = default;
            _rt.ImmediateContext.Handle->ClearState();
            _rt.ImmediateContext.Handle->Flush();
        }

        private void ClearWindowAssociation()
        {
            if (_hwnd == IntPtr.Zero) return;

            IDXGIDevice*   dxgiDev = null;
            IDXGIAdapter*  adapter = null;
            IDXGIFactory2* factory = null;

            Guid dxgiDevGuid  = IDXGIDevice.Guid;
            Guid factory2Guid = IDXGIFactory2.Guid;

            if (_rt.Device.Handle->QueryInterface(&dxgiDevGuid, (void**)&dxgiDev) < 0) return;
            try
            {
                if (dxgiDev->GetAdapter(&adapter) < 0) return;
                try
                {
                    if (adapter->GetParent(&factory2Guid, (void**)&factory) < 0) return;
                    factory->MakeWindowAssociation(_hwnd, 0u);
                }
                finally
                {
                    if (factory != null) factory->Release();
                    if (adapter != null) adapter->Release();
                }
            }
            finally
            {
                if (dxgiDev != null) dxgiDev->Release();
            }
        }

        private void SyncDimensionsFromSwapChain()
        {
            if (_swapChain.Handle == null) return;
            var desc = new SwapChainDesc1();
            if (_swapChain.Handle->GetDesc1(&desc) < 0) return;
            Width  = Math.Max(1, (int)desc.Width);
            Height = Math.Max(1, (int)desc.Height);
        }

        // ── Present ───────────────────────────────────────────────────────────────

        public void Present(bool vsync)
        {
            if (_swapChain.Handle == null) return;
            // VSync on → sync interval 1 (refresh-locked, smooth editing).
            // VSync off → sync interval 0; add the tearing flag when supported so the loop's
            // TargetFps (incl. 0 = uncapped) is the real cap rather than DWM composition.
            uint sync  = vsync ? 1u : 0u;
            uint flags = (!vsync && _allowTearing) ? DxgiPresentAllowTearing : 0u;
            int hr = _swapChain.Handle->Present(sync, flags);
            if (hr < 0)
                RenderLog.Line($"Present FAILED 0x{hr:X8}");
        }

        /// <summary>
        /// Acquires a caller-owned reference to the current flip-model back buffer. The caller
        /// must release it before ResizeBuffers; <see cref="IGpuSwapChain.AcquireBackBuffer"/>
        /// makes that lifetime one frame in the abstraction adapter.
        /// </summary>
        internal ID3D11Texture2D* AcquireBackBufferTexture()
        {
            if (_swapChain.Handle == null) return null;
            ID3D11Texture2D* texture = null;
            Guid iid = ID3D11Texture2D.Guid;
            return _swapChain.Handle->GetBuffer(0, &iid, (void**)&texture) >= 0 ? texture : null;
        }

        // ── Creation ──────────────────────────────────────────────────────────────

        private void CreateSwapChain()
        {
            IDXGIDevice*   dxgiDev = null;
            IDXGIAdapter*  adapter = null;
            IDXGIFactory2* factory = null;

            Guid dxgiDevGuid  = IDXGIDevice.Guid;
            Guid factory2Guid = IDXGIFactory2.Guid;

            SilkMarshal.ThrowHResult(_rt.Device.Handle->QueryInterface(&dxgiDevGuid, (void**)&dxgiDev));
            try
            {
                SilkMarshal.ThrowHResult(dxgiDev->GetAdapter(&adapter));
                SilkMarshal.ThrowHResult(adapter->GetParent(&factory2Guid, (void**)&factory));

                // Detect tearing support (IDXGIFactory5). If present, create with the tearing
                // flag so VSync-off presents can pass DXGI_PRESENT_ALLOW_TEARING.
                // NOTE: if your Silk.NET version names the enum member differently, adjust
                // "Feature.PresentAllowTearing".
                _allowTearing = false;
                IDXGIFactory5* factory5 = null;
                Guid factory5Guid = IDXGIFactory5.Guid;
                if (factory->QueryInterface(&factory5Guid, (void**)&factory5) >= 0 && factory5 != null)
                {
                    int allow = 0;
                    // Fully qualified: 'Feature' exists in both Silk.NET.DXGI and Silk.NET.Direct3D11.
                    if (factory5->CheckFeatureSupport(Silk.NET.DXGI.Feature.PresentAllowTearing, &allow, (uint)sizeof(int)) >= 0)
                        _allowTearing = allow != 0;
                    factory5->Release();
                }
                _swapChainFlags = _allowTearing ? DxgiSwapChainFlagAllowTearing : 0u;

                var desc = new SwapChainDesc1
                {
                    Width       = (uint)Width,
                    Height      = (uint)Height,
                    Format      = ColorFormat,
                    Stereo      = false,
                    SampleDesc  = new SampleDesc { Count = 1, Quality = 0 },
                    BufferUsage = DXGI.UsageRenderTargetOutput,
                    BufferCount = BufferCount,
                    Scaling     = Scaling.Stretch,
                    SwapEffect  = SwapEffect.FlipDiscard,
                    AlphaMode   = AlphaMode.Unspecified,
                    Flags       = _swapChainFlags,
                };

                var fsDesc = new SwapChainFullscreenDesc { Windowed = true };

                IDXGISwapChain1* sc = null;
                SilkMarshal.ThrowHResult(factory->CreateSwapChainForHwnd(
                    (IUnknown*)_rt.Device.Handle, _hwnd, &desc,
                    &fsDesc, (IDXGIOutput*)null, &sc));
                _swapChain = new ComPtr<IDXGISwapChain1>(sc);
                DropCreationReference(sc);

                // DXGI must not auto-resize on WM_SIZE — we own ResizeBuffers on the render tick.
                // Without this, GLFW resize + manual ResizeBuffers race → DXGI_ERROR_INVALID_CALL.
                factory->MakeWindowAssociation(_hwnd, MwaNoWindowChanges | MwaNoAltEnter);
            }
            finally
            {
                if (factory != null) factory->Release();
                if (adapter != null) adapter->Release();
                if (dxgiDev != null) dxgiDev->Release();
            }
        }

        private void CreateViews()
        {
            ID3D11Device* dev = _rt.Device.Handle;

            // Colour view from back buffer 0.
            ID3D11Texture2D* bb = null;
            Guid tex2dGuid = ID3D11Texture2D.Guid;
            SilkMarshal.ThrowHResult(_swapChain.Handle->GetBuffer(0, &tex2dGuid, (void**)&bb));
            ID3D11RenderTargetView* rtv = null;
            SilkMarshal.ThrowHResult(dev->CreateRenderTargetView((ID3D11Resource*)bb, null, &rtv));
            _rtv = new ComPtr<ID3D11RenderTargetView>(rtv);
            DropCreationReference(rtv);
            bb->Release();

            // Depth/stencil (D24S8, matches EngineTest).
            var depthDesc = new Texture2DDesc
            {
                Width      = (uint)Width,
                Height     = (uint)Height,
                MipLevels  = 1,
                ArraySize  = 1,
                Format     = Format.FormatR24G8Typeless,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage      = Usage.Default,
                BindFlags  = (uint)(BindFlag.DepthStencil | BindFlag.ShaderResource),
            };
            ID3D11Texture2D* depth = null;
            SilkMarshal.ThrowHResult(dev->CreateTexture2D(&depthDesc, null, &depth));
            _depthTex = new ComPtr<ID3D11Texture2D>(depth);
            DropCreationReference(depth);

            var dsvDesc = new DepthStencilViewDesc
            {
                Format        = Format.FormatD24UnormS8Uint,
                ViewDimension = DsvDimension.Texture2D,
            };
            dsvDesc.Anonymous.Texture2D.MipSlice = 0;
            ID3D11DepthStencilView* dsv = null;
            SilkMarshal.ThrowHResult(dev->CreateDepthStencilView((ID3D11Resource*)depth, &dsvDesc, &dsv));
            _dsv = new ComPtr<ID3D11DepthStencilView>(dsv);
            DropCreationReference(dsv);

            var srvDesc = new ShaderResourceViewDesc
            {
                Format        = Format.FormatR24UnormX8Typeless,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2D,
            };
            srvDesc.Anonymous.Texture2D.MipLevels = 1;
            srvDesc.Anonymous.Texture2D.MostDetailedMip = 0;
            ID3D11ShaderResourceView* depthSrv = null;
            SilkMarshal.ThrowHResult(dev->CreateShaderResourceView((ID3D11Resource*)depth, &srvDesc, &depthSrv));
            _depthSrv = new ComPtr<ID3D11ShaderResourceView>(depthSrv);
            DropCreationReference(depthSrv);
        }

        /// <summary>
        /// Drops the creation reference on an object that has just been handed to a
        /// <see cref="ComPtr{T}"/>, leaving the ComPtr as its sole owner.
        /// </summary>
        /// <remarks>
        /// Silk.NET's <c>ComPtr&lt;T&gt;</c> constructor <b>AddRefs</b> what it wraps, so wrapping a
        /// freshly created object left TWO references: the creation one and the ComPtr's. Disposing
        /// the ComPtr then dropped only one, so the object survived — and this class creates exactly
        /// the objects for which surviving is fatal:
        /// <list type="bullet">
        /// <item>a leaked render-target view holds an indirect reference on back buffer 0, and
        /// <c>ResizeBuffers</c> fails with <c>DXGI_ERROR_INVALID_CALL</c> while <i>any</i> reference
        /// remains — so every viewport resize failed, permanently, and DXGI's Scaling.Stretch
        /// silently covered it by stretching a stale back buffer over the new client area;</item>
        /// <item>a leaked swap chain keeps its HWND association, so recreating one on the same
        /// window fails with <c>E_ACCESSDENIED</c> — which is why "recreate on failure" never
        /// worked either and looked like a DXGI restriction.</item>
        /// </list>
        /// Measured, not assumed: every object here reported a reference count of 2 immediately
        /// after construction. See Issues.md NEXT-101.
        /// </remarks>
        private static void DropCreationReference(void* created)
        {
            if (created != null) ((IUnknown*)created)->Release();
        }

        private void ReleaseViews()
        {
            ID3D11DeviceContext* ctx = _rt.ImmediateContext.Handle;
            if (ctx != null)
                ctx->OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);

            _rtv.Dispose();      _rtv      = default;
            _dsv.Dispose();      _dsv      = default;
            _depthSrv.Dispose(); _depthSrv = default;
            _depthTex.Dispose(); _depthTex = default;
        }

        public void Dispose()
        {
            ReleaseViews();
            DestroySwapChain();
        }
    }
}

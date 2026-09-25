using System;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Genesis.Rendering.Diagnostics;

namespace Genesis.Rendering.SilkNet.DX11
{
    // Singleton that owns the shared ID3D11Device + ID3D11DeviceContext.
    // ALL D3DViewportControl instances share this one device to avoid VRAM duplication.
    internal sealed unsafe class SilkNetDx11Runtime : IDisposable
    {
        private static SilkNetDx11Runtime _instance;

        private D3D11 _d3d11Api;

        internal ComPtr<ID3D11Device>        Device;
        internal ComPtr<ID3D11DeviceContext> ImmediateContext;

        internal static SilkNetDx11Runtime EnsureDevice()
        {
            _instance ??= new SilkNetDx11Runtime();
            return _instance;
        }

        // Explicit reference adapter for native conformance tests only. Production always requests hardware.
        internal static SilkNetDx11Runtime CreateValidationWarp() => new(D3DDriverType.Warp);

        private SilkNetDx11Runtime(D3DDriverType driver = D3DDriverType.Hardware)
        {
            _d3d11Api = D3D11.GetApi(null, false);
            CreateDevice(driver);
        }

        private void CreateDevice(D3DDriverType requestedDriver)
        {
            ID3D11Device*        dev = null;
            ID3D11DeviceContext* ctx = null;
            D3DFeatureLevel      featureLevel = default;

            // Never silently change the user's hardware selection into WARP.
            D3DDriverType[] drivers = { requestedDriver };

            // Try with the debug layer first (it prints the precise reason for failures like
            // ResizeBuffers INVALID_CALL to the debugger / DebugView). Fall back to no debug if
            // the Windows "Graphics Tools" feature isn't installed.
            uint[] flagSets =
            {
                (uint)(CreateDeviceFlag.BgraSupport | CreateDeviceFlag.Debug),
                (uint)CreateDeviceFlag.BgraSupport,
            };

            int  hr    = unchecked((int)0x80004005); // E_FAIL
            bool debug = false;

            foreach (uint flags in flagSets)
            {
                foreach (var driver in drivers)
                {
                    hr = _d3d11Api.CreateDevice(
                        (IDXGIAdapter*)null, driver, 0, flags, null, 0,
                        D3D11.SdkVersion, &dev, &featureLevel, &ctx);
                    if (hr >= 0) break;
                }
                if (hr >= 0) { debug = (flags & (uint)CreateDeviceFlag.Debug) != 0; break; }
            }

            SilkMarshal.ThrowHResult(hr);
            Device           = new ComPtr<ID3D11Device>(dev);
            ImmediateContext = new ComPtr<ID3D11DeviceContext>(ctx);
            RenderLog.Line($"Device created (debugLayer={debug}, featureLevel={featureLevel})");
            if (!debug)
                RenderLog.Line("Tip: install Windows 'Graphics Tools' optional feature for DXGI debug-layer resize diagnostics");
        }

        public void Dispose()
        {
            ImmediateContext.Dispose();
            Device.Dispose();
            _d3d11Api.Dispose();
            if (ReferenceEquals(_instance, this)) _instance = null;
        }
    }
}

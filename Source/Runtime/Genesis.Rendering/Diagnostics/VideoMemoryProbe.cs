using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;

namespace Genesis.Rendering.Diagnostics;

/// <summary>
/// The video memory this process is using, as Windows' display driver model counts it, read
/// through DXGI (<c>IDXGIAdapter3.QueryVideoMemoryInfo</c>). The figure is per process and does
/// not depend on which backend draws (Direct3D, Vulkan and OpenGL allocations are all counted),
/// so the debug screen can show it whichever renderer is running. Adapters are opened once; each
/// query is one driver call per hardware adapter.
/// </summary>
public static unsafe class VideoMemoryProbe
{
    private static readonly object Gate = new();
    private static ComPtr<IDXGIAdapter3>[] _adapters;
    private static bool _unavailable;

    /// <summary>
    /// The local (dedicated) video memory this process uses, summed over hardware adapters, and the
    /// largest budget the driver offers it. False when DXGI 1.4 is not available.
    /// </summary>
    public static bool TryQuery(out long usageBytes, out long budgetBytes)
    {
        usageBytes = 0;
        budgetBytes = 0;
        if (!OperatingSystem.IsWindows()) return false;
        lock (Gate)
        {
            if (_unavailable) return false;
            if (_adapters == null && !Open())
            {
                _unavailable = true;
                return false;
            }

            bool any = false;
            foreach (ComPtr<IDXGIAdapter3> adapter in _adapters)
            {
                QueryVideoMemoryInfo info = default;
                if (adapter.Handle->QueryVideoMemoryInfo(0u, MemorySegmentGroup.Local, &info) < 0) continue;
                any = true;
                usageBytes += (long)info.CurrentUsage;
                budgetBytes = Math.Max(budgetBytes, (long)info.Budget);
            }

            return any;
        }
    }

    private static bool Open()
    {
        try
        {
            DXGI dxgi = DXGI.GetApi(null);
            ComPtr<IDXGIFactory1> factory = default;
            if (dxgi.CreateDXGIFactory1(SilkMarshal.GuidPtrOf<IDXGIFactory1>(), (void**)factory.GetAddressOf()) < 0)
                return false;

            var adapters = new List<ComPtr<IDXGIAdapter3>>();
            for (uint index = 0; index < 16; index++)
            {
                ComPtr<IDXGIAdapter1> adapter = default;
                if (factory.Handle->EnumAdapters1(index, adapter.GetAddressOf()) != 0) break;
                AdapterDesc1 desc;
                adapter.Handle->GetDesc1(&desc);
                bool software = ((AdapterFlag)desc.Flags & AdapterFlag.Software) != 0;
                ComPtr<IDXGIAdapter3> adapter3 = default;
                if (!software && adapter.Handle->QueryInterface(SilkMarshal.GuidPtrOf<IDXGIAdapter3>(), (void**)adapter3.GetAddressOf()) >= 0)
                    adapters.Add(adapter3);
                adapter.Dispose();
            }

            factory.Dispose();
            _adapters = adapters.ToArray();
            return _adapters.Length > 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

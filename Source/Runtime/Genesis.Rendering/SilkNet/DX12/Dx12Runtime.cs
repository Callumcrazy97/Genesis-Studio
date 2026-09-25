using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// Owns the D3D12 device, its direct queue, and the DXGI factory they were made from.
    /// </summary>
    /// <remarks>
    /// Process-wide and shared, matching <c>SilkNetDx11Runtime</c>: Studio opens several viewports
    /// and every one of them must render through the same device, or textures created for one
    /// cannot be sampled by another.
    /// </remarks>
    internal sealed unsafe class Dx12Runtime : IDisposable
    {
        private static Dx12Runtime _shared;

        private Dx12Runtime(
            D3D12 api,
            DXGI dxgi,
            ComPtr<IDXGIFactory4> factory,
            ComPtr<ID3D12Device> device,
            ComPtr<ID3D12CommandQueue> queue,
            string adapterName)
        {
            Api = api;
            Dxgi = dxgi;
            Factory = factory;
            Device = device;
            Queue = queue;
            AdapterName = adapterName;
        }

        public D3D12 Api { get; }
        public DXGI Dxgi { get; }
        public ComPtr<IDXGIFactory4> Factory { get; private set; }
        public ComPtr<ID3D12Device> Device { get; private set; }
        public ComPtr<ID3D12CommandQueue> Queue { get; private set; }
        public string AdapterName { get; }

        /// <summary>True when GENESIS_D3D12_DEBUG asked for the validation layer.</summary>
        public bool DebugEnabled { get; private set; }

        private ComPtr<ID3D12InfoQueue> _infoQueue;

        /// <summary>
        /// Prints and clears anything the validation layer has queued.
        /// </summary>
        /// <remarks>
        /// Without this the debug layer writes to the native debugger's output window, which a
        /// console test run never sees — so an invalid call that removes the device produces a
        /// <c>DEVICE_REMOVED</c> from some unrelated later call and no explanation at all.
        /// </remarks>
        public void DrainDiagnostics()
        {
            if (_infoQueue.Handle == null)
            {
                return;
            }

            ulong count = _infoQueue.Handle->GetNumStoredMessages();
            for (ulong i = 0; i < count; i++)
            {
                nuint length = 0;
                _infoQueue.Handle->GetMessageA(i, null, &length);
                if (length == 0) continue;

                void* buffer = NativeMemory.Alloc(length);
                try
                {
                    var message = (Message*)buffer;
                    if (_infoQueue.Handle->GetMessageA(i, message, &length) < 0) continue;
                    string text = SilkMarshal.PtrToString((nint)message->PDescription);
                    Genesis.Rendering.Diagnostics.RenderLog.Line($"[D3D12 {message->Severity}] {text}");
                    Console.Error.WriteLine($"[D3D12 {message->Severity}] {text}");
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }

            _infoQueue.Handle->ClearStoredMessages();
        }

        /// <summary>Creates the shared runtime on first use, then returns it.</summary>
        public static Dx12Runtime EnsureDevice() => _shared ??= Create();

        /// <summary>True when this machine can create a D3D12 device at all.</summary>
        /// <remarks>
        /// Asked before the backend is selected so an unsupported adapter falls back to D3D11 with
        /// a logged reason rather than failing to launch. Deliberately creates and throws away a
        /// device: adapter feature flags are not a reliable predictor of whether creation succeeds.
        /// </remarks>
        public static bool IsSupported()
        {
            try
            {
                // Probes by creating the shared runtime, not a throwaway one. Standing up a whole
                // second device and immediately destroying it perturbed the driver badly enough
                // that the *shared* device came back already removed — its first
                // CreateCommandAllocator failed with DEVICE_REMOVED. It also cost a full device
                // creation for a question the shared device answers by existing.
                _ = EnsureDevice();
                return true;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException
                    or InvalidOperationException or NotSupportedException)
            {
                // Any of these mean "no usable D3D12 here", which is a fallback, not a failure.
                return false;
            }
        }

        private static Dx12Runtime Create()
        {
            D3D12 api = D3D12.GetApi();
            DXGI dxgi = DXGI.GetApi(null);

            // Device Removed Extended Data, enabled before the device exists because that is the
            // only point it can be. Unlike the validation layer this needs no optional Windows
            // feature, and it is the tool aimed squarely at this failure: after a removal it
            // reports the last command-list operations the GPU actually completed.
            EnableDeviceRemovedExtendedData(api);

            // The debug layer costs real frame time and this runs inside the build's backend smoke,
            // so it is opt-in rather than on for debug builds.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GENESIS_D3D12_DEBUG")))
            {
                EnableDebugLayer(api);
            }

            ComPtr<IDXGIFactory4> factory = default;
            SilkMarshal.ThrowHResult(dxgi.CreateDXGIFactory2(
                0u, SilkMarshal.GuidPtrOf<IDXGIFactory4>(), (void**)factory.GetAddressOf()));

            ComPtr<ID3D12Device> device = default;
            string adapterName = SelectAdapter(api, factory, ref device);

            var queueDesc = new CommandQueueDesc
            {
                Type = CommandListType.Direct,
                Priority = (int)CommandQueuePriority.Normal,
                Flags = CommandQueueFlags.None,
                NodeMask = 0,
            };
            ComPtr<ID3D12CommandQueue> queue = default;
            SilkMarshal.ThrowHResult(device.Handle->CreateCommandQueue(
                &queueDesc, SilkMarshal.GuidPtrOf<ID3D12CommandQueue>(), (void**)queue.GetAddressOf()));

            var runtime = new Dx12Runtime(api, dxgi, factory, device, queue, adapterName);

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GENESIS_D3D12_DEBUG")))
            {
                runtime.DebugEnabled = true;
                ComPtr<ID3D12InfoQueue> queueInfo = default;
                if (device.Handle->QueryInterface(
                        SilkMarshal.GuidPtrOf<ID3D12InfoQueue>(), (void**)queueInfo.GetAddressOf()) >= 0)
                {
                    runtime._infoQueue = queueInfo;
                }
            }

            return runtime;
        }

        /// <summary>
        /// Picks the best adapter that can actually create a device, preferring discrete hardware.
        /// </summary>
        /// <remarks>
        /// WARP is accepted only when nothing else works. It renders correctly and slowly, which is
        /// the right outcome for a machine with no usable GPU and the wrong one for a machine whose
        /// first enumerated adapter merely happened to be software.
        /// </remarks>
        private static string SelectAdapter(D3D12 api, ComPtr<IDXGIFactory4> factory, ref ComPtr<ID3D12Device> device)
        {
            for (uint index = 0; ; index++)
            {
                ComPtr<IDXGIAdapter1> adapter = default;
                if (factory.Handle->EnumAdapters1(index, adapter.GetAddressOf()) != 0)
                {
                    break;
                }

                AdapterDesc1 desc;
                adapter.Handle->GetDesc1(&desc);
                bool software = ((AdapterFlag)desc.Flags & AdapterFlag.Software) != 0;

                if (!software && TryCreateDevice(api, adapter, ref device))
                {
                    string name = new string((char*)desc.Description);
                    adapter.Dispose();
                    return string.IsNullOrWhiteSpace(name) ? "Direct3D 12 adapter" : name;
                }

                adapter.Dispose();
            }

            // Nothing hardware-capable answered; fall back to whatever the default adapter is,
            // including WARP.
            ComPtr<IDXGIAdapter1> fallback = default;
            if (factory.Handle->EnumAdapters1(0u, fallback.GetAddressOf()) == 0)
            {
                bool created = TryCreateDevice(api, fallback, ref device);
                fallback.Dispose();
                if (created)
                {
                    return "Direct3D 12 software adapter";
                }
            }

            throw new NotSupportedException(
                "No adapter on this machine could create a Direct3D 12 device at feature level 11_0.");
        }

        private static bool TryCreateDevice(D3D12 api, ComPtr<IDXGIAdapter1> adapter, ref ComPtr<ID3D12Device> device)
        {
            ComPtr<ID3D12Device> created = default;
            int hr = api.CreateDevice(
                (IUnknown*)adapter.Handle,
                D3DFeatureLevel.Level110,
                SilkMarshal.GuidPtrOf<ID3D12Device>(),
                (void**)created.GetAddressOf());

            if (hr < 0)
            {
                return false;
            }

            device = created;
            return true;
        }

        /// <summary>
        /// Why the device was removed, as a readable string, or null if it is still alive.
        /// </summary>
        /// <remarks>
        /// A removed device reports <c>DXGI_ERROR_DEVICE_REMOVED</c> from whatever call happens to
        /// come next, which is almost never the call that caused it. The reason code is the only
        /// thing that points at the actual fault.
        /// </remarks>
        public string DescribeRemoval()
        {
            int reason = Device.Handle->GetDeviceRemovedReason();
            return reason switch
            {
                0 => null,
                unchecked((int)0x887A0006) => "DXGI_ERROR_DEVICE_HUNG — the GPU stopped responding to "
                    + "submitted work, usually an infinite loop in a shader or a malformed draw.",
                unchecked((int)0x887A0005) => "DXGI_ERROR_DEVICE_REMOVED — the device was lost.",
                unchecked((int)0x887A0007) => "DXGI_ERROR_DEVICE_RESET — the device was reset by the driver.",
                unchecked((int)0x887A0020) => "DXGI_ERROR_DRIVER_INTERNAL_ERROR — the driver failed internally, "
                    + "typically after invalid API use such as a descriptor whose type does not match "
                    + "the root-signature range it was written into.",
                unchecked((int)0x887A0001) => "DXGI_ERROR_INVALID_CALL — an API call was invalid.",
                _ => $"unrecognised removal reason 0x{reason:X8}",
            };
        }

        /// <summary>Turns on auto-breadcrumbs and page-fault reporting for device removals.</summary>
        private static void EnableDeviceRemovedExtendedData(D3D12 api)
        {
            ComPtr<ID3D12DeviceRemovedExtendedDataSettings> dred = default;
            if (api.GetDebugInterface(
                    SilkMarshal.GuidPtrOf<ID3D12DeviceRemovedExtendedDataSettings>(),
                    (void**)dred.GetAddressOf()) < 0)
            {
                // Older Windows without DRED still runs; it just cannot explain a removal.
                return;
            }

            dred.Handle->SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
            dred.Handle->SetPageFaultEnablement(DredEnablement.ForcedOn);
            dred.Dispose();
        }

        private static void EnableDebugLayer(D3D12 api)
        {
            ComPtr<ID3D12Debug> debug = default;
            if (api.GetDebugInterface(SilkMarshal.GuidPtrOf<ID3D12Debug>(), (void**)debug.GetAddressOf()) >= 0)
            {
                debug.Handle->EnableDebugLayer();
                debug.Dispose();
            }
        }

        public void Dispose()
        {
            Queue.Dispose();
            Queue = default;
            Device.Dispose();
            Device = default;
            Factory.Dispose();
            Factory = default;
        }
    }
}

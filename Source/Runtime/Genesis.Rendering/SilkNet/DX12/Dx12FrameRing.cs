using System;
using System.Runtime.InteropServices;
using Genesis.Rendering.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// The command allocators, list and fence that carry one frame, cycled over several frames.
    /// </summary>
    /// <remarks>
    /// D3D12 gives back none of the synchronisation D3D11 did. A command allocator's memory is
    /// still being read by the GPU after the CPU has moved on, so reusing one before that frame's
    /// fence has been signalled corrupts the commands currently executing. Cycling
    /// <see cref="Dx12BackendReadiness.FramesInFlight"/> allocators and waiting on the matching
    /// fence value is what makes reuse safe while still letting the CPU run ahead.
    ///
    /// One command list is enough. Every viewport in Studio already serialises onto the UI thread
    /// under a render lock, so there is no parallel recording to support — the same reasoning that
    /// let <see cref="Abstractions.IGpuDevice"/> stay immediate-mode rather than record-then-submit.
    /// </remarks>
    internal sealed unsafe class Dx12FrameRing : IDisposable
    {
        public const int FramesInFlight = Dx12BackendReadiness.FramesInFlight;

        private readonly Dx12Runtime _runtime;
        private readonly ComPtr<ID3D12CommandAllocator>[] _allocators = new ComPtr<ID3D12CommandAllocator>[FramesInFlight];
        private readonly ulong[] _frameFenceValues = new ulong[FramesInFlight];

        private ComPtr<ID3D12GraphicsCommandList> _list;
        private ComPtr<ID3D12Fence> _fence;
        private IntPtr _fenceEvent;
        private ulong _nextFenceValue = 1;
        private int _frameIndex;
        private bool _recording;
        private bool _disposed;

        public Dx12FrameRing(Dx12Runtime runtime)
        {
            _runtime = runtime;

            for (int i = 0; i < FramesInFlight; i++)
            {
                ComPtr<ID3D12CommandAllocator> allocator = default;
                SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateCommandAllocator(
                    CommandListType.Direct,
                    SilkMarshal.GuidPtrOf<ID3D12CommandAllocator>(),
                    (void**)allocator.GetAddressOf()));
                _allocators[i] = allocator;
            }

            ComPtr<ID3D12GraphicsCommandList> list = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateCommandList(
                0u,
                CommandListType.Direct,
                _allocators[0],
                (ID3D12PipelineState*)null,
                SilkMarshal.GuidPtrOf<ID3D12GraphicsCommandList>(),
                (void**)list.GetAddressOf()));
            _list = list;

            // Created open; close it so the first BeginFrame can reset it like any other frame.
            SilkMarshal.ThrowHResult(_list.Handle->Close());

            ComPtr<ID3D12Fence> fence = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateFence(
                0ul, FenceFlags.None, SilkMarshal.GuidPtrOf<ID3D12Fence>(), (void**)fence.GetAddressOf()));
            _fence = fence;

            _fenceEvent = CreateEventW(IntPtr.Zero, false, false, null);
            if (_fenceEvent == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create the Direct3D 12 frame fence event.");
            }
        }

        /// <summary>The list currently open for recording, or null between frames.</summary>
        public ID3D12GraphicsCommandList* List => _recording ? _list.Handle : null;

        /// <summary>Which slot of the ring this frame is using.</summary>
        public int FrameIndex => _frameIndex;

        public bool IsRecording => _recording;

        /// <summary>
        /// The fence value that will be signalled for work recorded right now.
        /// </summary>
        /// <remarks>
        /// A resource touched by the command list currently open is not safe to destroy until the
        /// GPU has passed this value. Deferred releases are stamped with it.
        /// </remarks>
        public ulong PendingFenceValue => _nextFenceValue;

        /// <summary>The fence value the GPU has actually reached.</summary>
        public ulong CompletedFenceValue => _fence.Handle->GetCompletedValue();

        /// <summary>Waits for this ring slot to be free, then opens it for recording.</summary>
        public void BeginFrame()
        {
            if (_recording)
            {
                return;
            }

            WaitForFenceValue(_frameFenceValues[_frameIndex]);

            SilkMarshal.ThrowHResult(_allocators[_frameIndex].Handle->Reset());
            SilkMarshal.ThrowHResult(_list.Handle->Reset(_allocators[_frameIndex], (ID3D12PipelineState*)null));
            _recording = true;
        }

        /// <summary>Closes and submits the frame, then advances the ring.</summary>
        public void EndFrame()
        {
            if (!_recording)
            {
                return;
            }

            SilkMarshal.ThrowHResult(_list.Handle->Close());
            _recording = false;

            ID3D12CommandList* raw = (ID3D12CommandList*)_list.Handle;
            _runtime.Queue.Handle->ExecuteCommandLists(1u, &raw);

            ulong signalled = _nextFenceValue++;
            SilkMarshal.ThrowHResult(_runtime.Queue.Handle->Signal(_fence, signalled));
            _frameFenceValues[_frameIndex] = signalled;
            _frameIndex = (_frameIndex + 1) % FramesInFlight;
        }

        /// <summary>
        /// Submits anything in flight and blocks until the GPU has finished every frame.
        /// </summary>
        /// <remarks>
        /// Required before a resize or a teardown: the swap chain's buffers cannot be released
        /// while a queued command list still references them.
        /// </remarks>
        public void WaitIdle()
        {
            if (_recording)
            {
                EndFrame();
            }

            ulong signalled = _nextFenceValue++;
            SilkMarshal.ThrowHResult(_runtime.Queue.Handle->Signal(_fence, signalled));
            WaitForFenceValue(signalled);

            for (int i = 0; i < FramesInFlight; i++)
            {
                _frameFenceValues[i] = 0ul;
            }
        }

        /// <summary>Submits what has been recorded so far and immediately reopens the list.</summary>
        /// <remarks>
        /// The abstraction lets a caller read a texture back mid-frame, which needs the copy to have
        /// actually executed. Flushing keeps that legal without making every frame synchronous.
        /// </remarks>
        public void FlushAndReopen()
        {
            bool wasRecording = _recording;
            WaitIdle();
            if (wasRecording)
            {
                BeginFrame();
            }
        }

        private void WaitForFenceValue(ulong value)
        {
            if (value == 0ul || _fence.Handle->GetCompletedValue() >= value)
            {
                return;
            }

            SilkMarshal.ThrowHResult(_fence.Handle->SetEventOnCompletion(value, (void*)_fenceEvent));
            WaitForSingleObject(_fenceEvent, 0xFFFFFFFFu);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WaitIdle();

            _list.Dispose();
            _list = default;
            for (int i = 0; i < FramesInFlight; i++)
            {
                _allocators[i].Dispose();
                _allocators[i] = default;
            }

            _fence.Dispose();
            _fence = default;

            if (_fenceEvent != IntPtr.Zero)
            {
                CloseHandle(_fenceEvent);
                _fenceEvent = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

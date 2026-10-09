using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Genesis.Rendering.Core;
using Genesis.Rendering.Diagnostics;
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

            WaitForFenceValue(_frameFenceValues[_frameIndex], "the frame that last used this command allocator");

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
            WaitForFenceValue(signalled, "every submitted frame (waiting for the GPU to go idle)");

            for (int i = 0; i < FramesInFlight; i++)
            {
                _frameFenceValues[i] = 0ul;
            }
        }

        /// <summary>
        /// <see cref="WaitIdle"/> that gives up after <paramref name="timeout"/>, for work that can
        /// be skipped (a screenshot) rather than worth freezing the game for. False when the GPU
        /// had not finished; the ring stays consistent either way, because every slot keeps the
        /// fence value its allocator must wait for.
        /// </summary>
        /// <remarks>The caller ends the frame first (the device has to put the back buffer in its
        /// present state before the list is closed).</remarks>
        public bool TryWaitIdle(TimeSpan timeout, string purpose)
        {
            if (_recording)
            {
                EndFrame();
            }

            ulong signalled = _nextFenceValue++;
            SilkMarshal.ThrowHResult(_runtime.Queue.Handle->Signal(_fence, signalled));
            if (!WaitForFenceValue(signalled, purpose, timeout))
            {
                return false;
            }

            for (int i = 0; i < FramesInFlight; i++)
            {
                _frameFenceValues[i] = 0ul;
            }

            return true;
        }

        /// <summary>Submits what has been recorded so far and immediately reopens the list.</summary>
        /// <remarks>
        /// <para>The abstraction lets a caller read a texture back mid-frame, which needs the copy to
        /// have actually executed. Flushing keeps that legal without making every frame synchronous.</para>
        ///
        /// <para>The list reopens in the <b>same</b> ring slot. Ending the frame here used to advance
        /// the ring, but the device's descriptor ring and upload pages stay on the slot the frame
        /// began in; the rest of the frame then wrote that slot's descriptors while being fenced as
        /// the next slot, so the slot could be recycled while the GPU still read them — a
        /// timing-dependent corruption of whatever the frame drew after a readback.</para>
        /// </remarks>
        public void FlushAndReopen() => TryFlushAndReopen(Timeout.InfiniteTimeSpan, "a mid-frame read-back");

        /// <summary>
        /// <see cref="FlushAndReopen"/> that gives up waiting after <paramref name="timeout"/>.
        /// </summary>
        /// <returns>
        /// True with the list reopened in the same slot. False when the GPU had not finished: the
        /// submission then stands as the end of this frame — the slot keeps its fence value and the
        /// ring moves on, exactly as <see cref="EndFrame"/> leaves it — because resetting an
        /// allocator the GPU may still be reading is never safe. Nothing is recording afterwards.
        /// </returns>
        public bool TryFlushAndReopen(TimeSpan timeout, string purpose)
        {
            if (!_recording)
            {
                if (timeout == Timeout.InfiniteTimeSpan)
                {
                    WaitIdle();
                    return true;
                }

                return TryWaitIdle(timeout, purpose);
            }

            SilkMarshal.ThrowHResult(_list.Handle->Close());
            ID3D12CommandList* raw = (ID3D12CommandList*)_list.Handle;
            _runtime.Queue.Handle->ExecuteCommandLists(1u, &raw);
            ulong signalled = _nextFenceValue++;
            SilkMarshal.ThrowHResult(_runtime.Queue.Handle->Signal(_fence, signalled));
            if (!WaitForFenceValue(signalled, purpose, timeout))
            {
                _recording = false;
                _frameFenceValues[_frameIndex] = signalled;
                _frameIndex = (_frameIndex + 1) % FramesInFlight;
                return false;
            }

            // Every submission has now completed, so this slot's allocator can be reset in place.
            SilkMarshal.ThrowHResult(_allocators[_frameIndex].Handle->Reset());
            SilkMarshal.ThrowHResult(_list.Handle->Reset(_allocators[_frameIndex], (ID3D12PipelineState*)null));
            return true;
        }

        /// <summary>A wait longer than this is written to the render log, with what it waits for.</summary>
        private static readonly TimeSpan SlowWait = TimeSpan.FromSeconds(2);

        private void WaitForFenceValue(ulong value, string purpose) =>
            WaitForFenceValue(value, purpose, Timeout.InfiniteTimeSpan);

        /// <summary>
        /// Waits for the GPU to pass <paramref name="value"/>, in slices: between them it checks
        /// whether the device has been removed (a hung GPU the driver reset, for instance) and
        /// throws with the reason rather than waiting for ever, and a wait that has gone on for
        /// seconds is logged with what it is waiting for. Infinite timeouts still wait as long as
        /// the GPU takes: what follows them reuses memory the GPU may be reading.
        /// </summary>
        /// <returns>False when <paramref name="timeout"/> passed first.</returns>
        private bool WaitForFenceValue(ulong value, string purpose, TimeSpan timeout)
        {
            if (value == 0ul || _fence.Handle->GetCompletedValue() >= value)
            {
                return true;
            }

            SilkMarshal.ThrowHResult(_fence.Handle->SetEventOnCompletion(value, (void*)_fenceEvent));
            long started = Stopwatch.GetTimestamp();
            TimeSpan nextReport = SlowWait;
            while (true)
            {
                // The event is shared by every wait, so it can also be set by an earlier one that
                // gave up: the fence itself is the answer.
                WaitForSingleObject(_fenceEvent, 250u);
                if (_fence.Handle->GetCompletedValue() >= value)
                {
                    break;
                }

                TimeSpan waited = Stopwatch.GetElapsedTime(started);
                string removal = _runtime.DescribeRemoval();
                if (removal is not null)
                {
                    RenderLog.Line($"[D3D12] device removed while waiting {waited.TotalSeconds:F1} s for {purpose}: {removal}");
                    throw new InvalidOperationException($"Direct3D 12 device removed while waiting for {purpose}: {removal}");
                }

                if (timeout != Timeout.InfiniteTimeSpan && waited >= timeout)
                {
                    RenderLog.Line($"[D3D12] gave up after {waited.TotalSeconds:F1} s waiting for {purpose} "
                        + $"(fence {value}, GPU at {_fence.Handle->GetCompletedValue()})");
                    return false;
                }

                if (waited >= nextReport)
                {
                    RenderLog.Line($"[D3D12] still waiting after {waited.TotalSeconds:F1} s for {purpose} "
                        + $"(fence {value}, GPU at {_fence.Handle->GetCompletedValue()})");
                    nextReport += TimeSpan.FromSeconds(10);
                }
            }

            TimeSpan total = Stopwatch.GetElapsedTime(started);
            if (total >= SlowWait)
            {
                RenderLog.Line($"[D3D12] waited {total.TotalSeconds:F1} s for {purpose}");
            }

            return true;
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

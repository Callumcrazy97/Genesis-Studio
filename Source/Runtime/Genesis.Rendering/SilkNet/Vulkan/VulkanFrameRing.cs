using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>
    /// The command buffers, fences and semaphores that carry one frame, cycled over several frames.
    /// </summary>
    /// <remarks>
    /// <para>Vulkan hands back none of the synchronisation D3D11 provided. A command buffer is still
    /// being read by the GPU after the CPU has moved on, so resetting one before its fence has
    /// signalled corrupts work in flight. Cycling a small number of frames and waiting on the
    /// matching fence makes reuse safe while letting the CPU run ahead.</para>
    ///
    /// <para>Presentation needs two semaphores per frame: one the acquire signals and the submission
    /// waits on, and one the submission signals and the present waits on. Fences synchronise CPU
    /// with GPU; semaphores synchronise GPU work with GPU work, and using a fence where a semaphore
    /// belongs stalls the pipeline without fixing the hazard.</para>
    /// </remarks>
    internal sealed unsafe class VulkanFrameRing : IDisposable
    {
        public const int FramesInFlight = 3;

        private readonly VulkanRuntime _runtime;
        private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[FramesInFlight];
        private readonly Fence[] _fences = new Fence[FramesInFlight];
        private readonly VkSemaphore[] _acquired = new VkSemaphore[FramesInFlight];
        private readonly VkSemaphore[] _rendered = new VkSemaphore[FramesInFlight];

        /// <summary>Destructors waiting for the frame index that makes them safe to run.</summary>
        private readonly List<(Action Destroy, ulong Frame)> _deferred = new();

        private CommandPool _pool;
        private CommandBuffer _immediateBuffer;
        private Fence _immediateFence;
        private int _frameIndex;
        private ulong _submitted;
        private ulong _completedSubmission;
        private readonly ulong[] _submissionForSlot = new ulong[FramesInFlight];
        private bool _recording;
        private bool _disposed;

        public VulkanFrameRing(VulkanRuntime runtime)
        {
            _runtime = runtime;

            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = runtime.GraphicsQueueFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };

            CommandPool pool;
            VulkanRuntime.Check(
                runtime.Api.CreateCommandPool(runtime.Device, &poolInfo, null, &pool),
                "creating the command pool");
            _pool = pool;

            var allocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = FramesInFlight,
            };

            fixed (CommandBuffer* buffers = _commandBuffers)
            {
                VulkanRuntime.Check(
                    runtime.Api.AllocateCommandBuffers(runtime.Device, &allocate, buffers),
                    "allocating command buffers");
            }

            // One more, outside the ring, for uploads and readback.
            allocate.CommandBufferCount = 1;
            CommandBuffer immediate;
            VulkanRuntime.Check(
                runtime.Api.AllocateCommandBuffers(runtime.Device, &allocate, &immediate),
                "allocating the upload command buffer");
            _immediateBuffer = immediate;

            var immediateFence = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Fence uploadFence;
            VulkanRuntime.Check(
                runtime.Api.CreateFence(runtime.Device, &immediateFence, null, &uploadFence),
                "creating the upload fence");
            _immediateFence = uploadFence;

            for (int i = 0; i < FramesInFlight; i++)
            {
                // Created signalled so the very first BeginFrame does not wait on work that was
                // never submitted.
                var fenceInfo = new FenceCreateInfo
                {
                    SType = StructureType.FenceCreateInfo,
                    Flags = FenceCreateFlags.SignaledBit,
                };

                Fence fence;
                VulkanRuntime.Check(
                    runtime.Api.CreateFence(runtime.Device, &fenceInfo, null, &fence), "creating a frame fence");
                _fences[i] = fence;

                var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

                VkSemaphore acquired;
                VulkanRuntime.Check(
                    runtime.Api.CreateSemaphore(runtime.Device, &semaphoreInfo, null, &acquired),
                    "creating the image-available semaphore");
                _acquired[i] = acquired;

                VkSemaphore rendered;
                VulkanRuntime.Check(
                    runtime.Api.CreateSemaphore(runtime.Device, &semaphoreInfo, null, &rendered),
                    "creating the render-finished semaphore");
                _rendered[i] = rendered;
            }
        }

        /// <summary>
        /// Opens the dedicated upload command buffer, which is separate from the frame's.
        /// </summary>
        /// <remarks>
        /// <para>Uploads and readbacks used to share the frame's command buffer and flush it to make
        /// the copy execute. That submitted the frame early and ended its render pass, so every draw
        /// recorded afterwards was silently dropped — the world vanished and the picture flickered
        /// depending on when streaming happened to upload a texture.</para>
        ///
        /// <para>A separate buffer has no render pass of its own, so a copy recorded here is legal
        /// while the frame is mid-pass, and submitting it leaves the frame untouched.</para>
        /// </remarks>
        public CommandBuffer BeginImmediateCommands()
        {
            VulkanRuntime.Check(
                _runtime.Api.ResetCommandBuffer(_immediateBuffer, CommandBufferResetFlags.None),
                "resetting the upload command buffer");

            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            VulkanRuntime.Check(
                _runtime.Api.BeginCommandBuffer(_immediateBuffer, &begin), "beginning the upload command buffer");
            return _immediateBuffer;
        }

        /// <summary>Submits the upload buffer and waits, so the caller may free or read what it wrote.</summary>
        public void SubmitImmediateCommands()
        {
            VulkanRuntime.Check(
                _runtime.Api.EndCommandBuffer(_immediateBuffer), "ending the upload command buffer");

            CommandBuffer command = _immediateBuffer;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &command,
            };

            Fence fence = _immediateFence;
            VulkanRuntime.Check(
                _runtime.Api.ResetFences(_runtime.Device, 1, &fence), "resetting the upload fence");
            VulkanRuntime.Check(
                _runtime.Api.QueueSubmit(_runtime.GraphicsQueue, 1, &submit, fence), "submitting an upload");
            VulkanRuntime.Check(
                _runtime.Api.WaitForFences(_runtime.Device, 1, &fence, true, ulong.MaxValue),
                "waiting for an upload");
        }

        public int FrameIndex => _frameIndex;

        public bool IsRecording => _recording;

        public CommandBuffer CurrentCommandBuffer => _commandBuffers[_frameIndex];

        public VkSemaphore AcquiredSemaphore => _acquired[_frameIndex];

        public VkSemaphore RenderedSemaphore => _rendered[_frameIndex];

        public ulong SubmittedFrames => _submitted;

        /// <summary>Non-blocking completion for asynchronous diagnostics copies.</summary>
        public ulong CompletedSubmission
        {
            get
            {
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (_submissionForSlot[i] > _completedSubmission
                        && _runtime.Api.GetFenceStatus(_runtime.Device, _fences[i]) == Result.Success)
                        _completedSubmission = _submissionForSlot[i];
                }
                return _completedSubmission;
            }
        }

        /// <summary>Waits for this slot to be free, then opens its command buffer.</summary>
        public void BeginFrame()
        {
            if (_recording)
            {
                return;
            }

            Fence fence = _fences[_frameIndex];
            VulkanRuntime.Check(
                _runtime.Api.WaitForFences(_runtime.Device, 1, &fence, true, ulong.MaxValue),
                "waiting for the frame fence");
            _completedSubmission = Math.Max(_completedSubmission, _submissionForSlot[_frameIndex]);
            _submissionForSlot[_frameIndex] = 0;
            VulkanRuntime.Check(
                _runtime.Api.ResetFences(_runtime.Device, 1, &fence), "resetting the frame fence");

            DrainDeferred(force: false);

            VulkanRuntime.Check(
                _runtime.Api.ResetCommandBuffer(CurrentCommandBuffer, CommandBufferResetFlags.None),
                "resetting the command buffer");

            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            VulkanRuntime.Check(
                _runtime.Api.BeginCommandBuffer(CurrentCommandBuffer, &begin), "beginning the command buffer");
            _recording = true;
        }

        /// <summary>Closes and submits the frame, optionally synchronising with presentation.</summary>
        /// <param name="waitForAcquire">
        /// True when this frame draws into a swap chain image, so the submission waits on the
        /// acquire semaphore and signals the render-finished one.
        /// </param>
        public void EndFrameAndSubmit(
            bool waitForAcquire = false,
            VkSemaphore waitSemaphore = default,
            VkSemaphore signalSemaphore = default)
        {
            if (!_recording)
            {
                return;
            }

            VulkanRuntime.Check(
                _runtime.Api.EndCommandBuffer(CurrentCommandBuffer), "ending the command buffer");
            _recording = false;

            CommandBuffer command = CurrentCommandBuffer;
            VkSemaphore wait = waitSemaphore.Handle != 0 ? waitSemaphore : _acquired[_frameIndex];
            VkSemaphore signal = signalSemaphore.Handle != 0 ? signalSemaphore : _rendered[_frameIndex];
            PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit;

            bool hasWait = waitForAcquire && wait.Handle != 0;
            bool hasSignal = waitForAcquire && signal.Handle != 0;

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &command,
                WaitSemaphoreCount = hasWait ? 1u : 0u,
                PWaitSemaphores = hasWait ? &wait : null,
                PWaitDstStageMask = hasWait ? &waitStage : null,
                SignalSemaphoreCount = hasSignal ? 1u : 0u,
                PSignalSemaphores = hasSignal ? &signal : null,
            };

            VulkanRuntime.Check(
                _runtime.Api.QueueSubmit(_runtime.GraphicsQueue, 1, &submit, _fences[_frameIndex]),
                "submitting the frame");

            _submitted++;
            _submissionForSlot[_frameIndex] = _submitted;
            _frameIndex = (_frameIndex + 1) % FramesInFlight;
        }

        /// <summary>Submits anything open and blocks until the GPU has finished everything.</summary>
        public void WaitIdle()
        {
            if (_recording)
            {
                EndFrameAndSubmit(waitForAcquire: false);
            }

            VulkanRuntime.Check(
                _runtime.Api.DeviceWaitIdle(_runtime.Device), "waiting for the device to idle");
            _completedSubmission = _submitted;
            DrainDeferred(force: true);
        }

        /// <summary>Submits what has been recorded and reopens the buffer.</summary>
        /// <remarks>
        /// The abstraction lets a caller read a texture back mid-frame, which needs the copy to have
        /// actually executed.
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

        /// <summary>Queues a destructor to run once the GPU can no longer be using the object.</summary>
        /// <remarks>
        /// The rule that took the D3D12 backend down twice: destroying a resource that a recorded or
        /// in-flight command buffer still references is undefined behaviour, and in Vulkan it is a
        /// validation error followed by a device loss rather than anything self-evident.
        /// </remarks>
        public void Defer(Action destroy)
        {
            if (destroy is null)
            {
                return;
            }

            _deferred.Add((destroy, _submitted + FramesInFlight));
        }

        private void DrainDeferred(bool force)
        {
            for (int i = _deferred.Count - 1; i >= 0; i--)
            {
                if (!force && _deferred[i].Frame > _submitted)
                {
                    continue;
                }

                _deferred[i].Destroy();
                _deferred.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            WaitIdle();

            Vk api = _runtime.Api;
            Device device = _runtime.Device;

            for (int i = 0; i < FramesInFlight; i++)
            {
                if (_fences[i].Handle != 0) api.DestroyFence(device, _fences[i], null);
                if (_acquired[i].Handle != 0) api.DestroySemaphore(device, _acquired[i], null);
                if (_rendered[i].Handle != 0) api.DestroySemaphore(device, _rendered[i], null);
            }

            if (_immediateFence.Handle != 0)
            {
                api.DestroyFence(device, _immediateFence, null);
                _immediateFence = default;
            }

            if (_pool.Handle != 0)
            {
                api.DestroyCommandPool(device, _pool, null);
                _pool = default;
            }
        }
    }
}

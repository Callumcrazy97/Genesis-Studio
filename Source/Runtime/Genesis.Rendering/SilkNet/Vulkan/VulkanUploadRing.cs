using System;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>
    /// Per-frame host-visible memory that every dynamic buffer write is sub-allocated from.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> <see cref="Abstractions.IGpuDevice.UpdateConstantBuffer{T}"/>
    /// promises that "the written data is valid for every draw issued between this call and the next
    /// update of the same buffer", and states outright that backends may sub-allocate to honour it.
    /// D3D11 gets that from <c>Map(WriteDiscard)</c> renaming and D3D12 from its own upload ring.
    /// Vulkan records draws now and executes them at submit, so writing in place into one persistently
    /// mapped allocation gives every draw in the frame whatever the CPU wrote <i>last</i> — the
    /// renderer rewrites per-draw constants dozens of times a frame, so all but the final draw read
    /// the wrong data. Handing each write its own slice restores the contract.</para>
    ///
    /// <para><b>Why it is per frame.</b> A slice must stay untouched until the GPU has finished
    /// reading it. Each frame slot owns its own region and is only reused once
    /// <see cref="VulkanFrameRing"/> has waited on that slot's fence, so the CPU running
    /// <see cref="VulkanFrameRing.FramesInFlight"/> frames ahead can never overwrite memory a
    /// submitted command buffer is still reading.</para>
    /// </remarks>
    internal sealed unsafe class VulkanUploadRing : IDisposable
    {
        /// <summary>
        /// Per-frame capacity. The sprite renderer alone can stage 32768 × 80-byte instances (2.5 MiB)
        /// in a frame, and the forward renderer adds a constant-buffer slice per draw.
        /// </summary>
        private const int BytesPerFrame = 64 * 1024 * 1024;

        private readonly VulkanRuntime _runtime;
        private readonly VkBuffer[] _buffers = new VkBuffer[VulkanFrameRing.FramesInFlight];
        private readonly DeviceMemory[] _memory = new DeviceMemory[VulkanFrameRing.FramesInFlight];
        private readonly byte*[] _mapped = new byte*[VulkanFrameRing.FramesInFlight];
        private readonly int[] _used = new int[VulkanFrameRing.FramesInFlight];

        private int _frameSlot;
        private bool _disposed;

        public VulkanUploadRing(VulkanRuntime runtime)
        {
            _runtime = runtime;
            for (int i = 0; i < VulkanFrameRing.FramesInFlight; i++)
            {
                Create(i);
            }
        }

        /// <summary>Alignment that satisfies every way a dynamic slice may later be bound.</summary>
        /// <remarks>
        /// A buffer's bind flags are a set, not a choice — the sprite instance buffer is a structured
        /// buffer, others are constant buffers — so the slice takes the strictest applicable
        /// alignment. A structured buffer additionally starts on a multiple of its stride, because a
        /// shader addresses it in elements from the descriptor's offset.
        /// </remarks>
        public int AlignmentFor(Abstractions.GpuBindFlags bindFlags, int structureStride)
        {
            int alignment = 16;

            if ((bindFlags & Abstractions.GpuBindFlags.ConstantBuffer) != 0)
            {
                alignment = Lcm(alignment, (int)_runtime.MinUniformBufferOffsetAlignment);
            }

            if ((bindFlags & (Abstractions.GpuBindFlags.StructuredBuffer
                | Abstractions.GpuBindFlags.ShaderResource)) != 0)
            {
                alignment = Lcm(alignment, (int)_runtime.MinStorageBufferOffsetAlignment);
            }

            if ((bindFlags & Abstractions.GpuBindFlags.StructuredBuffer) != 0 && structureStride > 0)
            {
                alignment = Lcm(alignment, structureStride);
            }

            return alignment;
        }

        /// <summary>Releases this slot's region for reuse. The fence for it has already been waited on.</summary>
        public void BeginFrame(int frameSlot)
        {
            _frameSlot = frameSlot;
            _used[frameSlot] = 0;
        }

        /// <summary>Carves an aligned slice out of this frame's region and returns where to write it.</summary>
        public void* Allocate(int size, int alignment, out VkBuffer buffer, out ulong offset)
        {
            int slot = _frameSlot;
            int start = Align(_used[slot], Math.Max(1, alignment));

            if (start + size > BytesPerFrame)
            {
                throw new InvalidOperationException(
                    $"Vulkan dynamic upload ring exhausted: {BytesPerFrame / (1024 * 1024)} MiB per frame "
                    + $"and this frame asked for {start + size} bytes. Raise BytesPerFrame.");
            }

            _used[slot] = start + size;
            buffer = _buffers[slot];
            offset = (ulong)start;
            return _mapped[slot] + start;
        }

        private void Create(int slot)
        {
            var info = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = BytesPerFrame,

                // One region backs every dynamic bind point, so it declares all of them.
                Usage = BufferUsageFlags.UniformBufferBit
                    | BufferUsageFlags.StorageBufferBit
                    | BufferUsageFlags.VertexBufferBit
                    | BufferUsageFlags.IndexBufferBit
                    | BufferUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
            };

            VkBuffer buffer;
            VulkanRuntime.Check(
                _runtime.Api.CreateBuffer(_runtime.Device, &info, null, &buffer),
                "creating the dynamic upload ring");
            _buffers[slot] = buffer;

            _runtime.Api.GetBufferMemoryRequirements(_runtime.Device, buffer, out MemoryRequirements requirements);

            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,

                // Coherent so a write is visible to the GPU without an explicit flush; the ring is
                // written every frame and manual range flushing would cost more than it saves.
                MemoryTypeIndex = _runtime.FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
            };

            DeviceMemory memory;
            VulkanRuntime.Check(
                _runtime.Api.AllocateMemory(_runtime.Device, &allocate, null, &memory),
                "allocating the dynamic upload ring");
            _memory[slot] = memory;

            VulkanRuntime.Check(
                _runtime.Api.BindBufferMemory(_runtime.Device, buffer, memory, 0),
                "binding the dynamic upload ring");

            void* mapped = null;
            VulkanRuntime.Check(
                _runtime.Api.MapMemory(_runtime.Device, memory, 0, BytesPerFrame, 0, &mapped),
                "mapping the dynamic upload ring");
            _mapped[slot] = (byte*)mapped;
        }

        private static int Align(int value, int alignment) =>
            (value + alignment - 1) / alignment * alignment;

        private static int Lcm(int a, int b)
        {
            if (a <= 0 || b <= 0) return Math.Max(1, Math.Max(a, b));
            return a / Gcd(a, b) * b;
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return a;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < VulkanFrameRing.FramesInFlight; i++)
            {
                if (_memory[i].Handle != 0)
                {
                    _runtime.Api.UnmapMemory(_runtime.Device, _memory[i]);
                }

                if (_buffers[i].Handle != 0)
                {
                    _runtime.Api.DestroyBuffer(_runtime.Device, _buffers[i], null);
                }

                if (_memory[i].Handle != 0)
                {
                    _runtime.Api.FreeMemory(_runtime.Device, _memory[i], null);
                }
            }
        }
    }
}

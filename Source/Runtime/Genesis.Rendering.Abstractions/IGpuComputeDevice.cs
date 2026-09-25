using System;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// GPU-resident buffer compute and indirect submission. This is deliberately a capability
    /// contract, not a CPU emulation: a hardware backend either implements it or rejects the effect.
    /// All operations use the device's current command stream and the render thread.
    /// </summary>
    public interface IGpuComputeDevice : IGpuDevice
    {
        /// <summary>Bind a structured read/write buffer to u0..u3; invalid clears the binding.</summary>
        void SetUnorderedAccessBuffer(int uRegister, GpuBufferHandle buffer);
        void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1);
        /// <summary>Read three uint group counts from a GPU buffer, without CPU readback.</summary>
        void DispatchIndirect(GpuBufferHandle arguments, int byteOffset = 0);
        /// <summary>Make preceding UAV writes visible to later compute, graphics and indirect reads.</summary>
        void ComputeMemoryBarrier();
        /// <summary>Read the five DrawIndexedInstanced arguments directly from GPU memory.</summary>
        void DrawIndexedIndirect(GpuBufferHandle arguments, int byteOffset = 0);
        /// <summary>
        /// Enqueue a bounded staging copy. TryComplete never waits for the GPU; Release is safe
        /// while the copy is still in flight. This is for diagnostics, not simulation/draw counts.
        /// </summary>
        GpuBufferReadbackHandle BeginBufferReadback(GpuBufferHandle source, int byteOffset, int byteCount);
        bool TryCompleteBufferReadback(GpuBufferReadbackHandle request, Span<byte> destination);
        void ReleaseBufferReadback(GpuBufferReadbackHandle request);
    }

    public readonly struct GpuBufferReadbackHandle
    {
        public readonly int Id;
        public GpuBufferReadbackHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
    }

    /// <summary>The portable buffer-only compute binding layout; OpenGL requires eight SSBO slots.</summary>
    public static class GpuComputeLimits
    {
        public const int ConstantBuffers = 4;
        public const int ReadOnlyBuffers = 4;
        public const int WritableBuffers = 4;
        public const int OpenGlWritableBase = 4;
        public const int MaxDispatchGroups = 65535;
        public const int MaxReadbackBytes = 1024 * 1024;

        public static void ValidateGroups(int x, int y, int z)
        {
            if ((uint)x > MaxDispatchGroups || (uint)y > MaxDispatchGroups || (uint)z > MaxDispatchGroups)
                throw new ArgumentOutOfRangeException(nameof(x), "Compute group counts must be between 0 and 65535.");
        }

        public static void ValidateIndirect(int sizeBytes, int offset, int argumentBytes)
        {
            if (offset < 0 || (offset & 3) != 0 || argumentBytes > sizeBytes - offset)
                throw new ArgumentOutOfRangeException(nameof(offset), "Indirect arguments must be aligned and wholly inside the buffer.");
        }

        public static void ValidateReadback(int sizeBytes, int offset, int count)
        {
            if (offset < 0 || count <= 0 || count > MaxReadbackBytes || count > sizeBytes - offset)
                throw new ArgumentOutOfRangeException(nameof(count), "Readback must be a bounded, nonempty range inside the buffer.");
        }
    }
}

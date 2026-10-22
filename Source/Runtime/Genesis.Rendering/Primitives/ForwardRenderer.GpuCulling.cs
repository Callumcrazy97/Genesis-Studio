using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // GPU culling of the opaque instanced batches (GpuCullingShaders): before the main pass, every
    // batch of at least GpuCullMinimumInstances instances has its instances tested against the
    // camera on the GPU, the visible ones written into _cullVisible at the batch's own offset, and
    // its draw arguments counted there; the main pass then draws it with DrawIndexedIndirect.
    // Shadows, reflections and smaller batches draw every instance as before.
    internal sealed partial class ForwardRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CullCB
        {
            public Vector4 Left, Right, Bottom, Top;
            public Vector4 Sphere;
            public uint First, Count, IndexCount, ArgumentOffset;
        }

        // Fewer instances than this are drawn whole: a dispatch costs more than it saves.
        private const int GpuCullMinimumInstances = 64;
        private const int DrawArgumentBytes = 20;

        private bool _gpuCullTried;
        private IGpuComputeDevice _cullDevice;
        private GpuShaderProgramHandle _cullProgram;
        private GpuShaderProgramHandle _cullResetProgram;
        private GpuBufferHandle _cbCull;
        private GpuBufferHandle _cullVisible;
        private GpuBufferHandle _cullArguments;
        private int _cullCapacity;
        private int _cullArgumentSlots;
        // For each batch of the frame's opaque list: its argument slot, or -1 when it is drawn whole.
        private readonly List<int> _cullSlots = new();

        /// <summary>Batches culled on the GPU in the last frame.</summary>
        public int LastGpuCulledBatches { get; private set; }

        // The draw arguments read back now and then (a diagnostic copy, never waited for): how
        // many of the culled batches' instances were in view, a few frames ago.
        private GpuBufferReadbackHandle _cullReadback;
        private int _cullReadbackSlots;
        private int _cullReadbackCandidates;
        private byte[] _cullReadbackBytes = [];

        /// <summary>Instances of GPU-culled batches that were in view, as last read back (-1 before any).</summary>
        public int LastGpuCullVisible { get; private set; } = -1;

        /// <summary>Instances the GPU tested in the frame <see cref="LastGpuCullVisible"/> was read from.</summary>
        public int LastGpuCullCandidates { get; private set; }

        private void PollCullReadback()
        {
            if (!_cullReadback.IsValid || _cullDevice == null) return;
            int bytes = _cullReadbackSlots * DrawArgumentBytes;
            if (_cullReadbackBytes.Length < bytes) _cullReadbackBytes = new byte[bytes];
            if (!_cullDevice.TryCompleteBufferReadback(_cullReadback, _cullReadbackBytes.AsSpan(0, bytes))) return;
            _cullDevice.ReleaseBufferReadback(_cullReadback);
            _cullReadback = default;
            int visible = 0;
            for (int slot = 0; slot < _cullReadbackSlots; slot++)
                visible += BitConverter.ToInt32(_cullReadbackBytes, slot * DrawArgumentBytes + 4);
            LastGpuCullVisible = visible;
            LastGpuCullCandidates = _cullReadbackCandidates;
        }

        private bool EnsureGpuCulling()
        {
            if (_gpuCullTried) return _cullProgram.IsValid && _cullResetProgram.IsValid;
            _gpuCullTried = true;
            if (_gpu is not IGpuComputeDevice compute || !_gpu.Capabilities.SupportsComputeShaders
                || !_gpu.Capabilities.SupportsIndirectDraw || !_gpu.Capabilities.SupportsStructuredBuffers)
                return false;
            try
            {
                _cullProgram = MakeCullKernel("Cull");
                _cullResetProgram = MakeCullKernel("ResetArguments");
                _cbCull = MakeCB<CullCB>("GPU culling constants");
                _cullDevice = compute;
                RenderLog.Line("GPU culling: on for batches of " + GpuCullMinimumInstances + " instances or more");
            }
            catch (Exception error)
            {
                // Every instance is drawn instead, as it was.
                RenderLog.Line($"GPU culling could not be made on {_gpu.BackendName}: {error.GetBaseException().Message}");
                if (_cullProgram.IsValid) _gpu.ReleaseShaderProgram(_cullProgram);
                _cullProgram = _cullResetProgram = GpuShaderProgramHandle.Invalid;
            }
            return _cullProgram.IsValid && _cullResetProgram.IsValid;
        }

        private GpuShaderProgramHandle MakeCullKernel(string entry) => _gpu.CreateShaderProgram(new GpuShaderProgramDesc
        {
            BinaryFormat = _gpu.ShaderBinaryFormat,
            ComputeShader = CompileShader(GpuCullingShaders.Source, entry, GpuShaderStage.Compute),
            DebugName = "GPU culling " + entry,
        });

        private void EnsureCullBuffers(int instances, int slots)
        {
            int stride = Marshal.SizeOf<InstanceGpu>();
            if (instances > _cullCapacity)
            {
                if (_cullVisible.IsValid) _gpu.ReleaseBuffer(_cullVisible);
                _cullCapacity = Math.Max(instances, Math.Max(1024, _cullCapacity * 2));
                _cullVisible = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = _cullCapacity * stride,
                    StructureStride = stride,
                    Usage = GpuBufferUsage.Gpu,
                    BindFlags = GpuBindFlags.StructuredBuffer | GpuBindFlags.ShaderResource | GpuBindFlags.UnorderedAccess,
                    DebugName = "GPU culling visible instances",
                }, ReadOnlySpan<byte>.Empty);
            }
            if (slots > _cullArgumentSlots)
            {
                if (_cullArguments.IsValid) _gpu.ReleaseBuffer(_cullArguments);
                _cullArgumentSlots = Math.Max(slots, Math.Max(64, _cullArgumentSlots * 2));
                _cullArguments = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = _cullArgumentSlots * DrawArgumentBytes,
                    StructureStride = 0,
                    Usage = GpuBufferUsage.Gpu,
                    BindFlags = GpuBindFlags.UnorderedAccess | GpuBindFlags.IndirectArguments,
                    DebugName = "GPU culling draw arguments",
                }, ReadOnlySpan<byte>.Empty);
            }
        }

        private void ReleaseGpuCulling()
        {
            if (_cullReadback.IsValid && _cullDevice != null) _cullDevice.ReleaseBufferReadback(_cullReadback);
            _cullReadback = default;
            if (_cullVisible.IsValid) _gpu.ReleaseBuffer(_cullVisible);
            if (_cullArguments.IsValid) _gpu.ReleaseBuffer(_cullArguments);
            if (_cbCull.IsValid) _gpu.ReleaseBuffer(_cbCull);
            foreach (GpuShaderProgramHandle program in new[] { _cullProgram, _cullResetProgram })
                if (program.IsValid) _gpu.ReleaseShaderProgram(program);
            _cullVisible = _cullArguments = _cbCull = GpuBufferHandle.Invalid;
            _cullProgram = _cullResetProgram = GpuShaderProgramHandle.Invalid;
            _cullCapacity = _cullArgumentSlots = 0;
            _cullDevice = null;
            _gpuCullTried = false;
        }

        /// <summary>
        /// Culls the frame's big opaque batches against the camera on the GPU, after the instances
        /// are uploaded and before any pass draws them (a dispatch may not run inside a render pass).
        /// </summary>
        private void CullBatchesOnGpu()
        {
            _cullSlots.Clear();
            LastGpuCulledBatches = 0;
            PollCullReadback();
            if (!RenderCapacityDefaults.GpuCulling || IsSoftwareBackend) return;

            // The batches worth it, at the offsets the main pass draws them from.
            int offset = 0, slots = 0;
            foreach (Batch b in _batchList)
            {
                int count = b.Instances.Count;
                int drawn = Math.Min(count, Math.Max(0, SoftMeshInstanceCap - offset));
                bool cull = drawn >= GpuCullMinimumInstances && !b.TerrainGround
                    && (b.RasterOverride & MeshDrawFlags.EditorReference) == 0
                    && TryGetMesh(b.MeshId, out MeshEntry mesh) && mesh.BoundsRadius > 0f;
                _cullSlots.Add(cull ? slots++ : -1);
                offset += count;
            }
            if (slots == 0 || !EnsureGpuCulling())
            {
                _cullSlots.Clear();
                return;
            }
            EnsureCullBuffers(Math.Min(offset, SoftMeshInstanceCap), slots);

            Matrix4x4 m = UnjitteredSceneViewProj;
            var constants = new CullCB
            {
                Left = Plane(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
                Right = Plane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
                Bottom = Plane(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
                Top = Plane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
            };

            // The visible buffer was the vertex shader's last frame: let go of it there before
            // writing it (D3D11 will not bind one buffer both ways).
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, GpuBufferHandle.Invalid);
            _cullDevice.SetUnorderedAccessBuffer(0, _cullVisible);
            _cullDevice.SetUnorderedAccessBuffer(1, _cullArguments);
            _gpu.SetStructuredBuffer(GpuShaderStage.Compute, 0, _instanceBuf);
            offset = 0;
            int candidates = 0;
            for (int index = 0; index < _batchList.Count; index++)
            {
                Batch b = _batchList[index];
                int slot = _cullSlots[index];
                if (slot >= 0 && TryGetMesh(b.MeshId, out MeshEntry mesh))
                {
                    int drawn = Math.Min(b.Instances.Count, Math.Max(0, SoftMeshInstanceCap - offset));
                    // Padded: wind and the shaders' own movement may take a vertex a little outside.
                    constants.Sphere = new Vector4(mesh.BoundsCenter, mesh.BoundsRadius * 1.1f + 0.25f);
                    constants.First = (uint)offset;
                    constants.Count = (uint)drawn;
                    constants.IndexCount = (uint)mesh.IndexCount;
                    constants.ArgumentOffset = (uint)(slot * DrawArgumentBytes);
                    _gpu.UpdateConstantBuffer(_cbCull, constants);
                    _gpu.SetConstantBuffer(GpuShaderStage.Compute, 0, _cbCull);
                    _gpu.SetShaderProgram(_cullResetProgram);
                    _cullDevice.Dispatch(1);
                    _cullDevice.ComputeMemoryBarrier();
                    _gpu.SetShaderProgram(_cullProgram);
                    _cullDevice.Dispatch((drawn + 63) / 64);
                    LastGpuCulledBatches++;
                    candidates += drawn;
                }
                offset += b.Instances.Count;
            }
            _cullDevice.ComputeMemoryBarrier();
            _cullDevice.SetUnorderedAccessBuffer(0, GpuBufferHandle.Invalid);
            _cullDevice.SetUnorderedAccessBuffer(1, GpuBufferHandle.Invalid);
            _gpu.SetStructuredBuffer(GpuShaderStage.Compute, 0, GpuBufferHandle.Invalid);
            if (!_cullReadback.IsValid)
            {
                _cullReadback = _cullDevice.BeginBufferReadback(_cullArguments, 0, slots * DrawArgumentBytes);
                _cullReadbackSlots = slots;
                _cullReadbackCandidates = candidates;
            }
        }

        private static Vector4 Plane(float x, float y, float z, float w)
        {
            float length = MathF.Sqrt(x * x + y * y + z * z);
            return length > 1e-12f ? new Vector4(x, y, z, w) / length : new Vector4(0f, 0f, 0f, 1f);
        }

        /// <summary>
        /// Draws batch <paramref name="index"/> of the opaque list from its GPU-culled instances; false
        /// when it was not culled (or this pass is not the camera's), and it is drawn whole as before.
        /// </summary>
        private bool TryDrawCulledBatch(int index)
        {
            if (_reflectionPassActive || _cullDevice == null || index >= _cullSlots.Count) return false;
            int slot = _cullSlots[index];
            if (slot < 0) return false;
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, _cullVisible);
            _cullDevice.DrawIndexedIndirect(_cullArguments, slot * DrawArgumentBytes);
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, _instanceBuf);
            return true;
        }
    }
}

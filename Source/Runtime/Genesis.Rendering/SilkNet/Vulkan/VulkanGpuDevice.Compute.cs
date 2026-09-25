using System;
using System.Collections.Generic;
using System.Text;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Silk.NET.Vulkan;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    internal sealed unsafe partial class VulkanGpuDevice
    {
        private readonly GpuBufferHandle[] _boundUavs = new GpuBufferHandle[GpuComputeLimits.WritableBuffers];
        private sealed class BufferReadback
        {
            public BufferResource Buffer;
            public ulong Submission;
            public int Count;
        }
        private readonly Dictionary<int, BufferReadback> _bufferReadbacks = new();

        private GpuShaderProgramHandle CreateComputeProgram(in GpuShaderProgramDesc desc)
        {
            if (desc.VertexShader?.Length > 0 || desc.PixelShader?.Length > 0)
                throw new ArgumentException("A compute program cannot contain graphics stages.", nameof(desc));
            VulkanDescriptors.ProgramBindings bindings = _descriptors.CreateLayout(SpirVReflection.ReadBindings(desc.ComputeShader));
            ShaderModule shader = default;
            PipelineLayout pipelineLayout = default;
            Pipeline pipeline = default;
            try
            {
                shader = CreateShaderModule(desc.ComputeShader);
                DescriptorSetLayout layout = bindings.Layout;
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &layout,
                };
                VulkanRuntime.Check(_runtime.Api.CreatePipelineLayout(_runtime.Device, &layoutInfo, null, &pipelineLayout), "creating compute pipeline layout");
                byte[] entry = Encoding.UTF8.GetBytes(SpirVReflection.ReadEntryPoint(desc.ComputeShader, SpirVReflection.ExecutionModelCompute) + "\0");
                fixed (byte* name = entry)
                {
                    var info = new ComputePipelineCreateInfo
                    {
                        SType = StructureType.ComputePipelineCreateInfo, Layout = pipelineLayout,
                        Stage = new PipelineShaderStageCreateInfo
                        {
                            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                            Module = shader, PName = name,
                        },
                    };
                    VulkanRuntime.Check(_runtime.Api.CreateComputePipelines(_runtime.Device, default, 1, &info, null, &pipeline), "creating compute pipeline");
                }
                int id = _nextId++;
                _programs.Add(id, new ProgramResource
                {
                    ComputeShader = shader, ComputePipeline = pipeline, Bindings = bindings, PipelineLayout = pipelineLayout,
                });
                return new GpuShaderProgramHandle(id);
            }
            catch
            {
                if (pipeline.Handle != 0) _runtime.Api.DestroyPipeline(_runtime.Device, pipeline, null);
                if (pipelineLayout.Handle != 0) _runtime.Api.DestroyPipelineLayout(_runtime.Device, pipelineLayout, null);
                if (shader.Handle != 0) _runtime.Api.DestroyShaderModule(_runtime.Device, shader, null);
                _descriptors.DestroyLayout(bindings.Layout);
                throw;
            }
        }

        public void SetUnorderedAccessBuffer(int uRegister, GpuBufferHandle buffer)
        {
            if ((uint)uRegister >= _boundUavs.Length) throw new ArgumentOutOfRangeException(nameof(uRegister));
            if (buffer.IsValid && (!_buffers.TryGetValue(buffer.Id, out BufferResource resource)
                || (resource.BindFlags & GpuBindFlags.UnorderedAccess) == 0 || resource.Usage != GpuBufferUsage.Gpu))
                throw new ArgumentException("Unordered access requires device-local writable storage.", nameof(buffer));
            _boundUavs[uRegister] = buffer;
        }

        private CommandBuffer BindForCompute()
        {
            if (!_inFrame) BeginFrame();
            EndRenderPass();
            if (!_programs.TryGetValue(_program.Id, out ProgramResource program) || program.ComputePipeline.Handle == 0)
                throw new InvalidOperationException("A compute shader must be selected before dispatch.");
            CommandBuffer command = _frameRing.CurrentCommandBuffer;
            _runtime.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, program.ComputePipeline);
            WriteDescriptors(command, program, PipelineBindPoint.Compute);
            return command;
        }

        public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
        {
            GpuComputeLimits.ValidateGroups(groupsX, groupsY, groupsZ);
            if (groupsX == 0 || groupsY == 0 || groupsZ == 0) return;
            CommandBuffer command = BindForCompute();
            _runtime.Api.CmdDispatch(command, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
        }

        public void DispatchIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 12);
            CommandBuffer command = BindForCompute();
            _runtime.Api.CmdDispatchIndirect(command, buffer.Buffer, (ulong)byteOffset);
        }

        public void ComputeMemoryBarrier()
        {
            if (!_inFrame) BeginFrame();
            EndRenderPass();
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.IndirectCommandReadBit
                    | AccessFlags.VertexAttributeReadBit | AccessFlags.TransferReadBit,
            };
            _runtime.Api.CmdPipelineBarrier(_frameRing.CurrentCommandBuffer,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0,
                1, &barrier, 0, null, 0, null);
        }

        public void DrawIndexedIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 20);
            if (!BindForDraw(out CommandBuffer command) || !BindIndexBuffer(command))
                throw new InvalidOperationException("Indirect drawing requires a graphics pass, program and index buffer.");
            _runtime.Api.CmdDrawIndexedIndirect(command, buffer.Buffer, (ulong)byteOffset, 1, 20);
        }

        private BufferResource RequireIndirectBuffer(GpuBufferHandle handle, int offset, int size)
        {
            if (!_buffers.TryGetValue(handle.Id, out BufferResource buffer)
                || (buffer.BindFlags & GpuBindFlags.IndirectArguments) == 0 || buffer.Usage == GpuBufferUsage.Dynamic)
                throw new ArgumentException("Indirect arguments require a device-local argument buffer.", nameof(handle));
            GpuComputeLimits.ValidateIndirect(buffer.SizeBytes, offset, size);
            foreach (GpuBufferHandle writable in _boundUavs)
                if (writable.Id == handle.Id) throw new InvalidOperationException("Unbind argument storage from UAV slots before reading it indirectly.");
            return buffer;
        }

        public GpuBufferReadbackHandle BeginBufferReadback(GpuBufferHandle source, int byteOffset, int byteCount)
        {
            if (!_buffers.TryGetValue(source.Id, out BufferResource resource)) throw new ArgumentException("Unknown buffer.", nameof(source));
            GpuComputeLimits.ValidateReadback(resource.SizeBytes, byteOffset, byteCount);
            if (!_inFrame) BeginFrame();
            EndRenderPass();
            ComputeMemoryBarrier();
            BufferResource staging = AllocateBuffer(byteCount, BufferUsageFlags.TransferDstBit, hostVisible: true);
            var copy = new BufferCopy { SrcOffset = (ulong)byteOffset, DstOffset = 0, Size = (ulong)byteCount };
            _runtime.Api.CmdCopyBuffer(_frameRing.CurrentCommandBuffer, resource.Buffer, staging.Buffer, 1, &copy);
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit,
            };
            _runtime.Api.CmdPipelineBarrier(_frameRing.CurrentCommandBuffer, PipelineStageFlags.TransferBit,
                PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
            int id = _nextId++;
            _bufferReadbacks.Add(id, new BufferReadback { Buffer = staging, Count = byteCount, Submission = _frameRing.SubmittedFrames + 1 });
            return new GpuBufferReadbackHandle(id);
        }

        public bool TryCompleteBufferReadback(GpuBufferReadbackHandle request, Span<byte> destination)
        {
            if (!_bufferReadbacks.TryGetValue(request.Id, out BufferReadback readback)) return false;
            if (destination.Length < readback.Count) throw new ArgumentException("Readback destination is too small.", nameof(destination));
            if (_frameRing.CompletedSubmission < readback.Submission) return false;
            new ReadOnlySpan<byte>(readback.Buffer.Mapped, readback.Count).CopyTo(destination);
            return true;
        }

        public void ReleaseBufferReadback(GpuBufferReadbackHandle request)
        {
            if (!_bufferReadbacks.Remove(request.Id, out BufferReadback readback)) return;
            _frameRing.Defer(() => DestroyReadback(readback));
        }

        private void DestroyReadback(BufferReadback readback)
        {
            _runtime.Api.UnmapMemory(_runtime.Device, readback.Buffer.Memory);
            _runtime.Api.DestroyBuffer(_runtime.Device, readback.Buffer.Buffer, null);
            _runtime.Api.FreeMemory(_runtime.Device, readback.Buffer.Memory, null);
        }

        private void DisposeComputeResources()
        {
            foreach (BufferReadback readback in _bufferReadbacks.Values) DestroyReadback(readback);
            _bufferReadbacks.Clear();
        }
    }
}

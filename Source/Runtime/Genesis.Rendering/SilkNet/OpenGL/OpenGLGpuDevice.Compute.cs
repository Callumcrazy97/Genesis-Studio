using System;
using System.Collections.Generic;
using System.Text;
using Genesis.Rendering.Abstractions;
using Silk.NET.OpenGL;

namespace Genesis.Rendering.SilkNet.OpenGL
{
    internal sealed unsafe partial class OpenGLGpuDevice
    {
        private sealed class BufferReadback
        {
            public uint Buffer;
            public nint Fence;
            public int Count;
        }
        private readonly Dictionary<int, BufferReadback> _bufferReadbacks = new();

        private GpuShaderProgramHandle CreateComputeProgram(in GpuShaderProgramDesc desc)
        {
            if (desc.VertexShader?.Length > 0 || desc.PixelShader?.Length > 0)
                throw new ArgumentException("A compute program cannot contain graphics stages.", nameof(desc));
            uint shader = 0, program = 0;
            try
            {
                shader = CompileStage(ShaderType.ComputeShader, Encoding.UTF8.GetString(desc.ComputeShader), desc.DebugName);
                program = _gl.CreateProgram();
                _gl.AttachShader(program, shader);
                _gl.LinkProgram(program);
                _gl.GetProgram(program, GLEnum.LinkStatus, out int linked);
                if (linked == 0)
                    throw new InvalidOperationException($"OpenGL compute '{desc.DebugName}' failed to link: {_gl.GetProgramInfoLog(program)}");
                _gl.DetachShader(program, shader);
                int id = _nextId++;
                _programs.Add(id, new ProgramResource { Name = program, IsCompute = true, SamplerRegisterForUnit = Array.Empty<int>() });
                program = 0;
                return new GpuShaderProgramHandle(id);
            }
            finally
            {
                if (program != 0) _gl.DeleteProgram(program);
                if (shader != 0) _gl.DeleteShader(shader);
            }
        }

        public void SetUnorderedAccessBuffer(int uRegister, GpuBufferHandle buffer)
        {
            if ((uint)uRegister >= GpuComputeLimits.WritableBuffers) throw new ArgumentOutOfRangeException(nameof(uRegister));
            uint name = 0;
            if (buffer.IsValid)
            {
                if (!_buffers.TryGetValue(buffer.Id, out BufferResource resource)
                    || (resource.BindFlags & GpuBindFlags.UnorderedAccess) == 0)
                    throw new ArgumentException("Buffer was not created for unordered access.", nameof(buffer));
                name = resource.Name;
            }
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, (uint)(GpuComputeLimits.OpenGlWritableBase + uRegister), name);
        }

        private void BindForCompute()
        {
            if (!_programs.TryGetValue(_program.Id, out ProgramResource program) || !program.IsCompute)
                throw new InvalidOperationException("A compute shader must be selected before dispatch.");
            _gl.UseProgram(program.Name);
        }

        public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
        {
            GpuComputeLimits.ValidateGroups(groupsX, groupsY, groupsZ);
            if (groupsX == 0 || groupsY == 0 || groupsZ == 0) return;
            BindForCompute();
            _gl.DispatchCompute((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        }

        public void DispatchIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 12);
            BindForCompute();
            _gl.BindBuffer(BufferTargetARB.DispatchIndirectBuffer, buffer.Name);
            _gl.DispatchComputeIndirect(byteOffset);
        }

        public void ComputeMemoryBarrier() => _gl.MemoryBarrier(
            MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.CommandBarrierBit
            | MemoryBarrierMask.VertexAttribArrayBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);

        public void DrawIndexedIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 20);
            if (_indexOffset != 0) throw new NotSupportedException("Indirect GL draws require a zero index-buffer byte offset.");
            if (!_buffers.TryGetValue(_indexBuffer.Id, out BufferResource indices))
                throw new InvalidOperationException("An index buffer is required for an indirect indexed draw.");
            if (!BindForDraw()) throw new InvalidOperationException("A graphics program is required for an indirect draw.");
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, indices.Name);
            _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, buffer.Name);
            _gl.DrawElementsIndirect(OpenGLGpuFormats.ToTopology(_topology), IndexType(), (void*)(nint)byteOffset);
        }

        private BufferResource RequireIndirectBuffer(GpuBufferHandle handle, int offset, int size)
        {
            if (!_buffers.TryGetValue(handle.Id, out BufferResource buffer)
                || (buffer.BindFlags & GpuBindFlags.IndirectArguments) == 0)
                throw new ArgumentException("Buffer was not created for indirect arguments.", nameof(handle));
            GpuComputeLimits.ValidateIndirect(buffer.SizeBytes, offset, size);
            return buffer;
        }

        public GpuBufferReadbackHandle BeginBufferReadback(GpuBufferHandle source, int byteOffset, int byteCount)
        {
            if (!_buffers.TryGetValue(source.Id, out BufferResource buffer)) throw new ArgumentException("Unknown buffer.", nameof(source));
            GpuComputeLimits.ValidateReadback(buffer.SizeBytes, byteOffset, byteCount);
            var request = new BufferReadback { Count = byteCount };
            try
            {
                request.Buffer = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, request.Buffer);
                _gl.BufferData(BufferTargetARB.CopyWriteBuffer, (nuint)byteCount, null, BufferUsageARB.StreamRead);
                _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, buffer.Name);
                _gl.CopyBufferSubData(GLEnum.CopyReadBuffer, GLEnum.CopyWriteBuffer, byteOffset, 0, (nuint)byteCount);
                request.Fence = _gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, 0u);
                int id = _nextId++;
                _bufferReadbacks.Add(id, request);
                return new GpuBufferReadbackHandle(id);
            }
            catch
            {
                if (request.Fence != 0) _gl.DeleteSync(request.Fence);
                if (request.Buffer != 0) _gl.DeleteBuffer(request.Buffer);
                throw;
            }
        }

        public bool TryCompleteBufferReadback(GpuBufferReadbackHandle request, Span<byte> destination)
        {
            if (!_bufferReadbacks.TryGetValue(request.Id, out BufferReadback readback)) return false;
            if (destination.Length < readback.Count) throw new ArgumentException("Readback destination is too small.", nameof(destination));
            // No SYNC_FLUSH_COMMANDS_BIT and a zero timeout: diagnostics never stall the frame.
            GLEnum status = _gl.ClientWaitSync(readback.Fence, 0u, 0ul);
            if (status == GLEnum.TimeoutExpired) return false;
            if (status == GLEnum.WaitFailed) throw new InvalidOperationException("OpenGL readback fence failed.");
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, readback.Buffer);
            fixed (byte* output = destination)
                _gl.GetBufferSubData(BufferTargetARB.CopyReadBuffer, 0, (nuint)readback.Count, output);
            return true;
        }

        public void ReleaseBufferReadback(GpuBufferReadbackHandle request)
        {
            if (!_bufferReadbacks.Remove(request.Id, out BufferReadback readback)) return;
            _gl.DeleteSync(readback.Fence);
            _gl.DeleteBuffer(readback.Buffer);
        }

        private void DisposeComputeResources()
        {
            foreach (BufferReadback readback in _bufferReadbacks.Values)
            {
                _gl.DeleteSync(readback.Fence);
                _gl.DeleteBuffer(readback.Buffer);
            }
            _bufferReadbacks.Clear();
        }
    }
}

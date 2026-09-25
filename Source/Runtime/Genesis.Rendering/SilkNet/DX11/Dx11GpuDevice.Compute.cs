using System;
using System.Collections.Generic;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Genesis.Rendering.SilkNet.DX11
{
    public sealed unsafe partial class Dx11GpuDevice
    {
        private sealed class BufferReadback
        {
            public ID3D11Buffer* Buffer;
            public ID3D11Query* Fence;
            public int Count;
        }

        private readonly Dictionary<int, BufferReadback> _bufferReadbacks = new();
        private int _nextBufferReadback = 1;

        public void SetUnorderedAccessBuffer(int uRegister, GpuBufferHandle buffer)
        {
            if ((uint)uRegister >= GpuComputeLimits.WritableBuffers)
                throw new ArgumentOutOfRangeException(nameof(uRegister));
            ID3D11UnorderedAccessView* view = null;
            if (buffer.IsValid)
            {
                BufferResource resource = Require(_buffers, buffer.Id, nameof(buffer));
                view = resource.Uav;
                if (view == null) throw new ArgumentException("Buffer was not created for unordered access.", nameof(buffer));
            }
            // UINT(-1) preserves a view's counter; these are ordinary structured views, not append views.
            uint count = uint.MaxValue;
            Context->CSSetUnorderedAccessViews((uint)uRegister, 1, &view, &count);
        }

        public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
        {
            GpuComputeLimits.ValidateGroups(groupsX, groupsY, groupsZ);
            if (groupsX == 0 || groupsY == 0 || groupsZ == 0) return;
            Context->Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        }

        public void DispatchIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 12);
            Context->DispatchIndirect(buffer.Buffer, (uint)byteOffset);
        }

        public void ComputeMemoryBarrier()
        {
            // D3D11 orders UAV accesses between dispatches and draw/copy commands on the immediate
            // context. The caller unbinds views before changing a buffer from writable to readable.
            // No Flush/Map/CPU wait is needed (or permitted) on this hot path.
        }

        public void DrawIndexedIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 20);
            Context->DrawIndexedInstancedIndirect(buffer.Buffer, (uint)byteOffset);
        }

        private BufferResource RequireIndirectBuffer(GpuBufferHandle handle, int offset, int size)
        {
            BufferResource buffer = Require(_buffers, handle.Id, nameof(handle));
            if ((buffer.BindFlags & GpuBindFlags.IndirectArguments) == 0)
                throw new ArgumentException("Buffer was not created for indirect arguments.", nameof(handle));
            GpuComputeLimits.ValidateIndirect(buffer.SizeBytes, offset, size);
            return buffer;
        }

        public GpuBufferReadbackHandle BeginBufferReadback(GpuBufferHandle source, int byteOffset, int byteCount)
        {
            BufferResource resource = Require(_buffers, source.Id, nameof(source));
            GpuComputeLimits.ValidateReadback(resource.SizeBytes, byteOffset, byteCount);
            var request = new BufferReadback { Count = byteCount };
            try
            {
                var desc = new BufferDesc
                {
                    ByteWidth = (uint)byteCount, Usage = Usage.Staging, CPUAccessFlags = (uint)CpuAccessFlag.Read,
                };
                ID3D11Buffer* staging = null;
                SilkMarshal.ThrowHResult(Device->CreateBuffer(&desc, null, &staging));
                request.Buffer = staging;
                var queryDesc = new QueryDesc { Query = Query.Event };
                ID3D11Query* fence = null;
                SilkMarshal.ThrowHResult(Device->CreateQuery(&queryDesc, &fence));
                request.Fence = fence;
                var box = new Box { Left = (uint)byteOffset, Right = (uint)(byteOffset + byteCount), Bottom = 1, Back = 1 };
                Context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0,
                    (ID3D11Resource*)resource.Buffer, 0, &box);
                Context->End((ID3D11Asynchronous*)fence);
                int id = _nextBufferReadback++;
                _bufferReadbacks.Add(id, request);
                return new GpuBufferReadbackHandle(id);
            }
            catch
            {
                Release(request.Fence); Release(request.Buffer);
                throw;
            }
        }

        public bool TryCompleteBufferReadback(GpuBufferReadbackHandle request, Span<byte> destination)
        {
            if (!_bufferReadbacks.TryGetValue(request.Id, out BufferReadback readback)) return false;
            if (destination.Length < readback.Count) throw new ArgumentException("Readback destination is too small.", nameof(destination));
            int ready = 0;
            // D3D11_ASYNC_GETDATA_DONOTFLUSH: never submit work or wait just to update diagnostics.
            int hr = Context->GetData((ID3D11Asynchronous*)readback.Fence, &ready, sizeof(int), 1);
            if (hr < 0) SilkMarshal.ThrowHResult(hr);
            if (hr != 0 || ready == 0) return false;
            MappedSubresource mapped;
            hr = Context->Map((ID3D11Resource*)readback.Buffer, 0, Map.Read, 0x100000u /* D3D11_MAP_FLAG_DO_NOT_WAIT */, &mapped);
            if (hr == unchecked((int)0x887A000A)) return false; // DXGI_ERROR_WAS_STILL_DRAWING
            SilkMarshal.ThrowHResult(hr);
            try { new ReadOnlySpan<byte>(mapped.PData, readback.Count).CopyTo(destination); }
            finally { Context->Unmap((ID3D11Resource*)readback.Buffer, 0); }
            return true;
        }

        public void ReleaseBufferReadback(GpuBufferReadbackHandle request)
        {
            if (!_bufferReadbacks.Remove(request.Id, out BufferReadback readback)) return;
            // D3D11 command submission retains native resource references until use is complete.
            Release(readback.Fence); Release(readback.Buffer);
        }

        private void DisposeComputeResources()
        {
            foreach (BufferReadback readback in _bufferReadbacks.Values)
            {
                Release(readback.Fence); Release(readback.Buffer);
            }
            _bufferReadbacks.Clear();
        }
    }
}

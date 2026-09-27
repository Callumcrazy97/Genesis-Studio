using System;
using System.Collections.Generic;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Genesis.Rendering.SilkNet.DX12
{
    internal sealed unsafe partial class Dx12GpuDevice
    {
        private readonly GpuBufferHandle[] _csCbv = new GpuBufferHandle[GpuComputeLimits.ConstantBuffers];
        private readonly GpuBufferHandle[] _csSrv = new GpuBufferHandle[GpuComputeLimits.ReadOnlyBuffers];
        private readonly GpuBufferHandle[] _csUav = new GpuBufferHandle[GpuComputeLimits.WritableBuffers];
        private ComPtr<ID3D12RootSignature> _computeRootSignature;
        private ComPtr<ID3D12CommandSignature> _dispatchSignature;
        private ComPtr<ID3D12CommandSignature> _drawIndexedSignature;

        private sealed class BufferReadback
        {
            public ComPtr<ID3D12Resource> Resource;
            public ulong Fence;
            public int Count;
        }
        private readonly Dictionary<int, BufferReadback> _bufferReadbacks = new();
        private int _nextBufferReadback = 1;

        public void SetUnorderedAccessBuffer(int uRegister, GpuBufferHandle buffer)
        {
            if ((uint)uRegister >= _csUav.Length) throw new ArgumentOutOfRangeException(nameof(uRegister));
            if (buffer.IsValid)
            {
                BufferResource resource = Require(_buffers, buffer.Id, nameof(buffer));
                if (resource.Usage != GpuBufferUsage.Gpu || (resource.BindFlags & GpuBindFlags.UnorderedAccess) == 0)
                    throw new ArgumentException("Unordered access requires a device-local writable buffer.", nameof(buffer));
            }
            _csUav[uRegister] = buffer;
        }

        private void EnsureComputeRootSignature()
        {
            if (_computeRootSignature.Handle != null) return;
            const int count = GpuComputeLimits.ConstantBuffers + GpuComputeLimits.ReadOnlyBuffers + GpuComputeLimits.WritableBuffers;
            Span<RootParameter> storage = stackalloc RootParameter[count];
            for (int i = 0; i < count; i++)
            {
                storage[i] = new RootParameter { ShaderVisibility = ShaderVisibility.All };
                storage[i].ParameterType = i < 4 ? RootParameterType.TypeCbv : i < 8 ? RootParameterType.TypeSrv : RootParameterType.TypeUav;
                storage[i].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = (uint)(i % 4), RegisterSpace = 0 };
            }
            ID3D10Blob* serialized = null;
            ID3D10Blob* error = null;
            try
            {
                fixed (RootParameter* parameters = storage)
                {
                    var desc = new RootSignatureDesc { NumParameters = count, PParameters = parameters };
                    int hr = _runtime.Api.SerializeRootSignature(&desc, D3DRootSignatureVersion.Version10, &serialized, &error);
                    if (hr < 0)
                        throw new InvalidOperationException("Compute root signature: " + (error == null ? hr.ToString("X8") : SilkMarshal.PtrToString((nint)error->GetBufferPointer())));
                }
                ComPtr<ID3D12RootSignature> signature = default;
                SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateRootSignature(0,
                    serialized->GetBufferPointer(), serialized->GetBufferSize(), SilkMarshal.GuidPtrOf<ID3D12RootSignature>(),
                    (void**)signature.GetAddressOf()));
                _computeRootSignature = signature;
            }
            finally
            {
                if (serialized != null) serialized->Release();
                if (error != null) error->Release();
            }
        }

        private void BindForCompute()
        {
            EnsureRecording();
            ShaderProgramResource program = Require(_shaderPrograms, _pipeline.Program.Id, "compute program");
            if (program.ComputeShader is not { Length: > 0 })
                throw new InvalidOperationException("A compute shader must be selected before dispatch.");
            EnsureComputeRootSignature();
            if (program.ComputePipeline.Handle == null)
            {
                fixed (byte* code = program.ComputeShader)
                {
                    var desc = new ComputePipelineStateDesc
                    {
                        PRootSignature = _computeRootSignature.Handle,
                        CS = new ShaderBytecode { PShaderBytecode = code, BytecodeLength = (nuint)program.ComputeShader.Length },
                    };
                    ComPtr<ID3D12PipelineState> pipeline = default;
                    SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateComputePipelineState(&desc,
                        SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pipeline.GetAddressOf()));
                    program.ComputePipeline = pipeline;
                }
            }
            _frames.List->SetPipelineState(program.ComputePipeline.Handle);
            _frames.List->SetComputeRootSignature(_computeRootSignature.Handle);
            _boundPipeline = 0; // The next graphics draw must restore its PSO.
            for (int i = 0; i < 4; i++)
            {
                _frames.List->SetComputeRootConstantBufferView((uint)i, ComputeBufferAddress(_csCbv[i], ResourceStates.GenericRead));
                _frames.List->SetComputeRootShaderResourceView((uint)(4 + i), ComputeBufferAddress(_csSrv[i], ResourceStates.NonPixelShaderResource));
                _frames.List->SetComputeRootUnorderedAccessView((uint)(8 + i), ComputeBufferAddress(_csUav[i], ResourceStates.UnorderedAccess));
            }
        }

        private ulong ComputeBufferAddress(GpuBufferHandle handle, ResourceStates state)
        {
            if (!handle.IsValid) return 0;
            BufferResource buffer = Require(_buffers, handle.Id, nameof(handle));
            if (buffer.Usage == GpuBufferUsage.Dynamic) return buffer.DynamicAddress;
            TransitionResource(buffer.Resource, ref buffer.State, state);
            return buffer.Resource.Handle->GetGPUVirtualAddress();
        }

        public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
        {
            GpuComputeLimits.ValidateGroups(groupsX, groupsY, groupsZ);
            if (groupsX == 0 || groupsY == 0 || groupsZ == 0) return;
            BindForCompute();
            _frames.List->Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        }

        public void ComputeMemoryBarrier()
        {
            EnsureRecording();
            var barrier = new ResourceBarrier { Type = ResourceBarrierType.Uav };
            barrier.Anonymous.UAV = new ResourceUavBarrier { PResource = null };
            _frames.List->ResourceBarrier(1, &barrier);
        }

        public void DispatchIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 12);
            EnsureCommandSignature(ref _dispatchSignature, IndirectArgumentType.Dispatch, 12);
            BindForCompute();
            TransitionResource(buffer.Resource, ref buffer.State, ResourceStates.IndirectArgument);
            _frames.List->ExecuteIndirect(_dispatchSignature.Handle, 1, buffer.Resource.Handle,
                (ulong)byteOffset, null, 0);
        }

        public void DrawIndexedIndirect(GpuBufferHandle arguments, int byteOffset = 0)
        {
            BufferResource buffer = RequireIndirectBuffer(arguments, byteOffset, 20);
            EnsureRecording();
            EnsureCommandSignature(ref _drawIndexedSignature, IndirectArgumentType.DrawIndexed, 20);
            if (!PrepareDraw()) throw new InvalidOperationException("A graphics program is required for indirect drawing.");
            TransitionResource(buffer.Resource, ref buffer.State, ResourceStates.IndirectArgument);
            _frames.List->ExecuteIndirect(_drawIndexedSignature.Handle, 1, buffer.Resource.Handle,
                (ulong)byteOffset, null, 0);
        }

        private BufferResource RequireIndirectBuffer(GpuBufferHandle handle, int offset, int size)
        {
            BufferResource buffer = Require(_buffers, handle.Id, nameof(handle));
            if ((buffer.BindFlags & GpuBindFlags.IndirectArguments) == 0 || buffer.Usage == GpuBufferUsage.Dynamic)
                throw new ArgumentException("Indirect arguments require a device-local argument buffer.", nameof(handle));
            GpuComputeLimits.ValidateIndirect(buffer.SizeBytes, offset, size);
            foreach (GpuBufferHandle writable in _csUav)
                if (writable.Id == handle.Id) throw new InvalidOperationException("Unbind argument storage from UAV slots before reading it indirectly.");
            return buffer;
        }

        private void EnsureCommandSignature(ref ComPtr<ID3D12CommandSignature> signature, IndirectArgumentType type, uint stride)
        {
            if (signature.Handle != null) return;
            var argument = new IndirectArgumentDesc { Type = type };
            var desc = new CommandSignatureDesc { ByteStride = stride, NumArgumentDescs = 1, PArgumentDescs = &argument };
            ComPtr<ID3D12CommandSignature> created = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateCommandSignature(&desc, null,
                SilkMarshal.GuidPtrOf<ID3D12CommandSignature>(), (void**)created.GetAddressOf()));
            signature = created;
        }

        public GpuBufferReadbackHandle BeginBufferReadback(GpuBufferHandle source, int byteOffset, int byteCount)
        {
            BufferResource buffer = Require(_buffers, source.Id, nameof(source));
            GpuComputeLimits.ValidateReadback(buffer.SizeBytes, byteOffset, byteCount);
            if (buffer.Usage == GpuBufferUsage.Dynamic) throw new ArgumentException("Readback expects device-local storage.", nameof(source));
            EnsureRecording();
            var request = new BufferReadback
            {
                Resource = CreateCommittedBuffer(byteCount, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None),
                Count = byteCount, Fence = _frames.PendingFenceValue,
            };
            ResourceStates previous = buffer.State;
            TransitionResource(buffer.Resource, ref buffer.State, ResourceStates.CopySource);
            _frames.List->CopyBufferRegion(request.Resource.Handle, 0, buffer.Resource.Handle, (ulong)byteOffset, (ulong)byteCount);
            TransitionResource(buffer.Resource, ref buffer.State, previous);
            int id = _nextBufferReadback++;
            _bufferReadbacks.Add(id, request);
            return new GpuBufferReadbackHandle(id);
        }

        public bool TryCompleteBufferReadback(GpuBufferReadbackHandle request, Span<byte> destination)
        {
            if (!_bufferReadbacks.TryGetValue(request.Id, out BufferReadback readback)) return false;
            if (destination.Length < readback.Count) throw new ArgumentException("Readback destination is too small.", nameof(destination));
            if (_frames.CompletedFenceValue < readback.Fence) return false;
            var range = new Silk.NET.Direct3D12.Range { Begin = 0, End = (nuint)readback.Count };
            void* data = null;
            SilkMarshal.ThrowHResult(readback.Resource.Handle->Map(0, &range, &data));
            try { new ReadOnlySpan<byte>(data, readback.Count).CopyTo(destination); }
            finally
            {
                var empty = new Silk.NET.Direct3D12.Range();
                readback.Resource.Handle->Unmap(0, &empty);
            }
            return true;
        }

        public void ReleaseBufferReadback(GpuBufferReadbackHandle request)
        {
            if (!_bufferReadbacks.Remove(request.Id, out BufferReadback readback)) return;
            DeferResource(ref readback.Resource);
        }

        private void DisposeComputeResources()
        {
            foreach (BufferReadback request in _bufferReadbacks.Values) request.Resource.Dispose();
            _bufferReadbacks.Clear();
            foreach (ShaderProgramResource program in _shaderPrograms.Values) program.ComputePipeline.Dispose();
            _dispatchSignature.Dispose();
            _drawIndexedSignature.Dispose();
            _computeRootSignature.Dispose();
        }
    }
}

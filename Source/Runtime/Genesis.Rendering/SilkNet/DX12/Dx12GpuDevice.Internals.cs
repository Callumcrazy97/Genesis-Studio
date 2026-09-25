using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// The machinery behind the <see cref="IGpuDevice"/> surface: pipeline objects, uploads,
    /// resource states and the per-draw descriptor assembly.
    /// </summary>
    internal sealed unsafe partial class Dx12GpuDevice
    {
        // ── Deferred release ────────────────────────────────────────────────────

        /// <summary>
        /// Hands a COM object to the deletion queue instead of destroying it now.
        /// </summary>
        /// <remarks>
        /// <para>The single most important difference from D3D11. There, the runtime tracked which
        /// resources a submitted command referenced and kept them alive; here the application owns
        /// lifetime completely, and destroying a resource a recorded-or-in-flight command list still
        /// references is undefined behaviour.</para>
        ///
        /// <para>It presents as a device <i>hang</i>, several unrelated calls later, with nothing to
        /// connect it to the release that caused it. The validation layer says it plainly —
        /// <c>"An ID3D12Resource object … was deleted prior to closing the command list"</c> —
        /// followed by <c>DXGI_ERROR_DEVICE_HUNG</c>. Releasing a texture immediately after
        /// recording its upload was enough to take the device out, which is why every release goes
        /// through here.</para>
        ///
        /// <para>Ownership transfers: the caller's <c>ComPtr</c> must be cleared without disposing,
        /// because the single reference it held now belongs to this queue.</para>
        /// </remarks>
        private void Defer(nint comObject)
        {
            if (comObject == 0)
            {
                return;
            }

            _deferred.Add((comObject, _frames.PendingFenceValue));
        }

        /// <summary>Destroys everything the GPU has finished with.</summary>
        /// <param name="force">
        /// True only during teardown, after <c>WaitIdle</c>, when nothing can still be in flight.
        /// </param>
        private void DrainDeferred(bool force)
        {
            ulong completed = force ? ulong.MaxValue : _frames.CompletedFenceValue;

            for (int i = _deferred.Count - 1; i >= 0; i--)
            {
                if (_deferred[i].Fence > completed)
                {
                    continue;
                }

                ((IUnknown*)_deferred[i].Object)->Release();
                _deferred.RemoveAt(i);
            }
        }

        /// <summary>Moves a resource's reference into the deletion queue and clears the holder.</summary>
        private void DeferResource(ref ComPtr<ID3D12Resource> resource)
        {
            Defer((nint)resource.Handle);
            resource = default;
        }

        private void DeferHeap(ref ComPtr<ID3D12DescriptorHeap> heap)
        {
            Defer((nint)heap.Handle);
            heap = default;
        }

        // ── Upload ring ─────────────────────────────────────────────────────────

        private void CreateUploadRing()
        {
            _uploadRing = new ComPtr<ID3D12Resource>[Dx12FrameRing.FramesInFlight];
            _uploadCursorBase = new byte*[Dx12FrameRing.FramesInFlight];
            _uploadUsed = new int[Dx12FrameRing.FramesInFlight];

            for (int i = 0; i < Dx12FrameRing.FramesInFlight; i++)
            {
                _uploadRing[i] = CreateCommittedBuffer(
                    UploadRingBytes, HeapType.Upload, ResourceStates.GenericRead, ResourceFlags.None);

                // Mapped once for the life of the device. An upload heap is CPU-visible and
                // persistent mapping is the documented pattern; unmapping per write would cost more
                // than the writes.
                void* mapped = null;
                var empty = new Silk.NET.Direct3D12.Range();
                SilkMarshal.ThrowHResult(_uploadRing[i].Handle->Map(0u, &empty, &mapped));
                _uploadCursorBase[i] = (byte*)mapped;
            }
        }

        /// <summary>
        /// Carves an aligned block out of this frame's upload ring and returns where to write it.
        /// </summary>
        private void* AllocateUpload(int size, int alignment, out ulong gpuAddress) =>
            AllocateUpload(size, alignment, out gpuAddress, out _, out _);

        private void* AllocateUpload(
            int size, int alignment, out ulong gpuAddress, out int ringSlot, out ulong ringOffset)
        {
            int slot = _frames.FrameIndex;
            int offset = Align(_uploadUsed[slot], alignment);

            if (offset + size > UploadRingBytes)
            {
                throw new InvalidOperationException(
                    $"Direct3D 12 upload ring exhausted: {UploadRingBytes / (1024 * 1024)} MiB per frame "
                    + $"and this frame asked for {offset + size} bytes. Raise UploadRingBytes.");
            }

            _uploadUsed[slot] = offset + size;
            gpuAddress = _uploadRing[slot].Handle->GetGPUVirtualAddress() + (ulong)offset;
            ringSlot = slot;
            ringOffset = (ulong)offset;
            return _uploadCursorBase[slot] + offset;
        }

        /// <summary>
        /// How a dynamic buffer's ring slice must be aligned, given what it will be bound as.
        /// </summary>
        /// <remarks>
        /// A constant buffer view requires 256-byte placement. A shader resource view over a
        /// structured buffer instead addresses its data in <i>elements</i>, so the slice has to start
        /// on a multiple of the structure stride or <c>FirstElement</c> cannot name it exactly —
        /// 256 is not a multiple of every stride.
        /// </remarks>
        private static int DynamicAlignment(BufferResource buffer)
        {
            if ((buffer.BindFlags & GpuBindFlags.StructuredBuffer) != 0 && buffer.StructureStride > 0)
            {
                return buffer.StructureStride;
            }

            return (buffer.BindFlags & GpuBindFlags.ConstantBuffer) != 0 ? ConstantAlignment : 16;
        }

        /// <summary>Writes a dynamic buffer's contents into this frame's ring.</summary>
        private void WriteDynamic(BufferResource buffer, ReadOnlySpan<byte> data)
        {
            EnsureRecording();
            int written = Math.Max(data.Length, 1);
            int viewSize = ConstantViewSize(buffer, written);
            void* destination = AllocateUpload(
                viewSize, DynamicAlignment(buffer), out ulong address, out int slot, out ulong offset);
            Span<byte> dest = new(destination, viewSize);
            if (viewSize > written)
            {
                dest.Clear();
            }

            data.CopyTo(dest[..written]);
            buffer.DynamicAddress = address;
            buffer.DynamicSize = viewSize;
            buffer.DynamicRingSlot = slot;
            buffer.DynamicRingOffset = offset;
        }

        /// <summary>Stages data through the upload ring into a default-heap buffer.</summary>
        private void UploadIntoBuffer(BufferResource buffer, ReadOnlySpan<byte> data, int byteOffset)
        {
            if (data.IsEmpty) return;

            EnsureRecording();
            void* staging = AllocateUpload(data.Length, 4, out _);
            data.CopyTo(new Span<byte>(staging, data.Length));

            int slot = _frames.FrameIndex;
            ulong sourceOffset = (ulong)((byte*)staging - _uploadCursorBase[slot]);

            TransitionResource(buffer.Resource, ref buffer.State, ResourceStates.CopyDest);
            _frames.List->CopyBufferRegion(
                buffer.Resource, (ulong)byteOffset,
                _uploadRing[slot], sourceOffset, (ulong)data.Length);
            TransitionResource(buffer.Resource, ref buffer.State, ResourceStates.GenericRead);
        }

        /// <summary>
        /// Stages an image region into a texture, honouring D3D12's row-pitch alignment.
        /// </summary>
        /// <remarks>
        /// A copy footprint's row pitch must be a multiple of 256 bytes, which is almost never the
        /// natural width of the data. Copying row by row into the padded layout is what stops the
        /// classic diagonal shear — and for a block-compressed format a "row" is a row of 4x4
        /// blocks covering four pixel rows, not one.
        /// </remarks>
        private void UploadTexture(
            TextureResource texture, int x, int y, int width, int height,
            ReadOnlySpan<byte> data, int arraySlice)
        {
            if (data.IsEmpty || width <= 0 || height <= 0) return;

            EnsureRecording();
            TransitionResource(texture.Resource, ref texture.State, ResourceStates.CopyDest);
            CopyMip(texture, Subresource(texture, arraySlice, 0), x, y, width, height, data);
            TransitionResource(texture.Resource, ref texture.State, ShaderResourceState);
        }

        /// <summary>
        /// Uploads a whole tightly-packed mip chain, one copy per subresource.
        /// </summary>
        /// <remarks>
        /// Genesis's cooked-texture payload is every mip of every array slice packed end to end, and
        /// a single copy cannot express that: D3D12 addresses each mip as its own subresource with
        /// its own footprint. Describing subresource 0 at the full texture size while handing it the
        /// entire chain is an invalid copy — the GPU faults on it and the device is removed, which
        /// then surfaces as <c>DEVICE_REMOVED</c> from whatever unrelated call comes next. BC5 and
        /// BC7 make it certain, because every cooked texture carries mips.
        /// </remarks>
        private void UploadMipChain(TextureResource texture, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;

            EnsureRecording();
            TransitionResource(texture.Resource, ref texture.State, ResourceStates.CopyDest);

            int offset = 0;
            for (int layer = 0; layer < texture.ArrayLayers; layer++)
            {
                for (int mip = 0; mip < texture.MipLevels; mip++)
                {
                    int mipWidth = Math.Max(1, texture.Width >> mip);
                    int mipHeight = Math.Max(1, texture.Height >> mip);
                    int mipBytes = Dx12GpuFormats.RowBytes(texture.Format, mipWidth)
                        * Dx12GpuFormats.RowCount(texture.Format, mipHeight);

                    // A caller may legitimately supply fewer mips than the resource declares; stop
                    // rather than reading past the payload.
                    if (offset + mipBytes > data.Length) goto done;

                    CopyMip(
                        texture, Subresource(texture, layer, mip),
                        0, 0, mipWidth, mipHeight, data.Slice(offset, mipBytes));
                    offset += mipBytes;
                }
            }

        done:
            TransitionResource(texture.Resource, ref texture.State, ShaderResourceState);
        }

        /// <summary>D3D12 subresource index: mip slice first, then array slice.</summary>
        private static uint Subresource(TextureResource texture, int arraySlice, int mip) =>
            (uint)((arraySlice * texture.MipLevels) + mip);

        /// <summary>
        /// Stages one subresource through the upload ring, honouring D3D12 copy-footprint rules.
        /// </summary>
        /// <remarks>
        /// Row pitch is a multiple of 256 (<c>D3D12_TEXTURE_DATA_PITCH_ALIGNMENT</c>). The
        /// <i>offset</i> of that footprint inside the upload buffer is a multiple of 512
        /// (<c>D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT</c>) — aligning only the pitch left half of
        /// all copies on a 256-not-512 offset, which is an invalid copy and lands garbage in the
        /// default-heap texture. Floor tiles then fail <c>clip(tex.a - 0.35)</c> and world albedos
        /// band.
        /// </remarks>
        private void CopyMip(
            TextureResource texture, uint subresource,
            int x, int y, int width, int height, ReadOnlySpan<byte> data)
        {
            // Ask the device for the legal footprint of a width×height texture of this format.
            // Hand-rolling BC 1×1/2×2 as 4×4 (the previous code) is an invalid copy into a smaller
            // mip and is why cooked world albedos came out as banding / exploded displacement.
            var sliceDesc = new ResourceDesc
            {
                Dimension = ResourceDimension.Texture2D,
                Width = (ulong)Math.Max(1, width),
                Height = (uint)Math.Max(1, height),
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Dx12GpuFormats.ToDxgi(texture.Format),
                SampleDesc = new SampleDesc(1u, 0u),
                Layout = TextureLayout.LayoutUnknown,
            };

            PlacedSubresourceFootprint layout = default;
            uint rows = 0;
            ulong rowSize = 0;
            ulong totalBytes = 0;
            _runtime.Device.Handle->GetCopyableFootprints(
                &sliceDesc, 0u, 1u, 0ul, &layout, &rows, &rowSize, &totalBytes);

            int paddedRow = (int)layout.Footprint.RowPitch;
            int packedRow = (int)Math.Min(rowSize, (ulong)int.MaxValue);
            int rowCount = (int)rows;
            int uploadBytes = (int)Math.Max(totalBytes, (ulong)(paddedRow * Math.Max(1, rowCount)));

            void* staging = AllocateUpload(uploadBytes, TexturePlacementAlignment, out _);
            int slot = _frames.FrameIndex;
            ulong sourceOffset = (ulong)((byte*)staging - _uploadCursorBase[slot]);
            new Span<byte>(staging, uploadBytes).Clear();

            fixed (byte* source = data)
            {
                for (int row = 0; row < rowCount; row++)
                {
                    long available = data.Length - ((long)row * packedRow);
                    if (available <= 0) break;
                    int copy = (int)Math.Min(packedRow, available);
                    Buffer.MemoryCopy(
                        source + ((long)row * packedRow),
                        (byte*)staging + ((long)row * paddedRow),
                        copy, copy);
                }
            }

            var destination = new TextureCopyLocation
            {
                PResource = texture.Resource,
                Type = TextureCopyType.SubresourceIndex,
            };
            destination.Anonymous.SubresourceIndex = subresource;

            layout.Offset = sourceOffset;
            var source2 = new TextureCopyLocation
            {
                PResource = _uploadRing[slot],
                Type = TextureCopyType.PlacedFootprint,
            };
            source2.Anonymous.PlacedFootprint = layout;

            _frames.List->CopyTextureRegion(&destination, (uint)x, (uint)y, 0u, &source2, (Box*)null);
        }

        private ComPtr<ID3D12Resource> CreateCommittedBuffer(
            int sizeBytes, HeapType heapType, ResourceStates state, ResourceFlags flags)
        {
            var heap = new HeapProperties
            {
                Type = heapType,
                CreationNodeMask = 1u,
                VisibleNodeMask = 1u,
            };

            var desc = new ResourceDesc
            {
                Dimension = ResourceDimension.Buffer,
                Width = (ulong)Math.Max(1, sizeBytes),
                Height = 1u,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.FormatUnknown,
                SampleDesc = new SampleDesc(1u, 0u),
                Layout = TextureLayout.LayoutRowMajor,
                Flags = flags,
            };

            ComPtr<ID3D12Resource> resource = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateCommittedResource(
                &heap, HeapFlags.None, &desc, state, (ClearValue*)null,
                SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)resource.GetAddressOf()));
            return resource;
        }

        // ── Resource states ─────────────────────────────────────────────────────

        private void TransitionResource(
            ComPtr<ID3D12Resource> resource, ref ResourceStates current, ResourceStates target)
        {
            if (current == target) return;

            var barrier = new ResourceBarrier
            {
                Type = ResourceBarrierType.Transition,
                Flags = ResourceBarrierFlags.None,
            };
            barrier.Anonymous.Transition = new ResourceTransitionBarrier
            {
                PResource = resource,
                Subresource = 0xFFFFFFFF, // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
                StateBefore = current,
                StateAfter = target,
            };

            _frames.List->ResourceBarrier(1u, &barrier);
            current = target;
        }

        private void TransitionTexture(GpuTextureHandle handle, ResourceStates target)
        {
            if (!handle.IsValid || !_textures.TryGetValue(handle.Id, out TextureResource texture)) return;
            TransitionResource(texture.Resource, ref texture.State, target);
        }

        private void TransitionBackBuffer(ResourceStates target)
        {
            if (_activeSwapChain is not { IsReady: true } || !_backBufferTexture.IsValid) return;
            if (!_textures.TryGetValue(_backBufferTexture.Id, out TextureResource texture)) return;
            TransitionResource(texture.Resource, ref texture.State, target);
        }

        /// <summary>
        /// Publishes the swap chain's depth buffer as a sampleable texture handle.
        /// </summary>
        /// <remarks>
        /// Sampleable, not merely valid. Two different consumers need this handle for two different
        /// reasons: <c>ForwardRenderer.Flush</c> only checks that it *exists* before it will draw
        /// anything at all, but the post-process composite then *samples* it for screen-space fog. A
        /// handle without an SRV would satisfy the first and silently feed zeroes to the second.
        ///
        /// The resource is wrapped without a net reference change and marked
        /// <see cref="TextureResource.External"/>, exactly as the back buffer is: the swap chain owns
        /// these and releases them on resize, so taking ownership here would double-release. That
        /// ownership confusion is the same family as NEXT-101, and it is worth being explicit about.
        /// </remarks>
        private void EnsureSwapChainDepthTexture(Dx12SwapChain chain)
        {
            if (chain is not { IsReady: true }) return;

            ID3D12Resource* current = chain.DepthBuffer;
            if (current == null) return;

            if (!_depthTexture.IsValid)
            {
                int id = _nextTexture++;
                _textures[id] = new TextureResource
                {
                    External = true,
                    IsDepth = true,
                    Format = GpuFormat.D32Float,
                    State = ResourceStates.DepthWrite,
                    MipLevels = 1,
                    ArrayLayers = 1,
                };
                _depthTexture = new GpuTextureHandle(id);
            }

            TextureResource texture = _textures[_depthTexture.Id];
            if (texture.Resource.Handle != current)
            {
                // A resize rebuilds the depth buffer, so the handle must be re-pointed and its old
                // SRV retired — a stale descriptor would sample a destroyed resource.
                texture.Resource = new ComPtr<ID3D12Resource>(current);
                current->Release();
                texture.State = ResourceStates.DepthWrite;
                if (texture.SrvSlot >= 0)
                {
                    _freeSrvSlots.Push(texture.SrvSlot);
                    texture.SrvSlot = -1;
                }
            }

            texture.Width = chain.Width;
            texture.Height = chain.Height;
            texture.MipLevels = 1;
            texture.ArrayLayers = 1;

            if (texture.SrvSlot < 0)
            {
                texture.SrvSlot = AllocateSrvSlot();
                CreateSrv(texture, texture.SrvSlot);
            }
        }

        /// <summary>Moves the published swap-chain depth into the state a caller needs.</summary>
        /// <remarks>
        /// Necessary because the composite pass samples this resource, which transitions it to
        /// <see cref="ResourceStates.PixelShaderResource"/>. Without transitioning it back before the
        /// next pass binds its DSV, D3D12 would be asked to depth-write a resource it believes is a
        /// shader resource — a validation error, and on some drivers a device removal.
        /// </remarks>
        private void TransitionSwapChainDepth(ResourceStates target)
        {
            if (!_depthTexture.IsValid) return;
            if (!_textures.TryGetValue(_depthTexture.Id, out TextureResource texture)) return;
            TransitionResource(texture.Resource, ref texture.State, target);
        }

        /// <summary>Drops the published depth handle ahead of a resize.</summary>
        /// <remarks>
        /// The resource itself is the swap chain's to destroy; this only retires the handle and its
        /// descriptor slot so the next <see cref="EnsureSwapChainDepthTexture"/> rebuilds both
        /// against the new buffer.
        /// </remarks>
        private void ReleaseSwapChainDepthTexture()
        {
            if (!_depthTexture.IsValid) return;

            if (_textures.TryGetValue(_depthTexture.Id, out TextureResource texture))
            {
                if (texture.SrvSlot >= 0)
                {
                    _freeSrvSlots.Push(texture.SrvSlot);
                    texture.SrvSlot = -1;
                }

                // Not disposed: External means the swap chain owns it.
                texture.Resource = default;
                _textures.Remove(_depthTexture.Id);
            }

            _depthTexture = GpuTextureHandle.Invalid;
        }

        /// <summary>
        /// Publishes the current back buffer as an ordinary texture handle.
        /// </summary>
        /// <remarks>
        /// Re-pointed every frame because flip-model presentation cycles buffers — the contract on
        /// <see cref="IGpuSwapChain.AcquireBackBuffer"/> says the handle is valid only for the frame
        /// it was acquired in, and this is why.
        /// </remarks>
        /// <param name="preferPresented">
        /// True for readback, which wants the buffer that was last shown rather than the one the
        /// next frame will draw into. False while rendering, which wants the current one.
        /// </param>
        private void EnsureBackBufferTexture(bool preferPresented = false)
        {
            if (_activeSwapChain is not { IsReady: true }) return;

            if (!_backBufferTexture.IsValid)
            {
                int id = _nextTexture++;
                _textures[id] = new TextureResource { External = true, State = ResourceStates.Present };
                _backBufferTexture = new GpuTextureHandle(id);
            }

            TextureResource texture = _textures[_backBufferTexture.Id];
            int presented = _activeSwapChain.PresentedBackBufferIndex;
            ID3D12Resource* current = preferPresented && presented >= 0
                ? _activeSwapChain.BackBufferAt((uint)presented)
                : _activeSwapChain.CurrentBackBuffer;
            if (texture.Resource.Handle != current)
            {
                // Not owned: the swap chain releases these. Wrapping without an AddRef here is
                // deliberate, and External stops Dispose from over-releasing.
                texture.Resource = new ComPtr<ID3D12Resource>(current);
                current->Release();
                texture.State = ResourceStates.Present;
            }

            texture.Width = _activeSwapChain.Width;
            texture.Height = _activeSwapChain.Height;
            texture.Format = GpuFormat.R8G8B8A8UNorm;
        }

        private void CreateSrv(TextureResource texture, int slot)
        {
            var desc = new ShaderResourceViewDesc
            {
                Format = texture.IsDepth
                    ? Dx12GpuFormats.ToDepthShaderView(texture.Format)
                    : Dx12GpuFormats.ToDxgi(texture.Format),
                Shader4ComponentMapping = 0x1688, // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
            };

            if (texture.ArrayLayers > 1)
            {
                desc.ViewDimension = SrvDimension.Texture2Darray;
                desc.Anonymous.Texture2DArray = new Tex2DArraySrv
                {
                    MipLevels = (uint)texture.MipLevels,
                    ArraySize = (uint)texture.ArrayLayers,
                };
            }
            else
            {
                desc.ViewDimension = SrvDimension.Texture2D;
                desc.Anonymous.Texture2D = new Tex2DSrv { MipLevels = (uint)texture.MipLevels };
            }

            _runtime.Device.Handle->CreateShaderResourceView(
                texture.Resource, &desc, _bindings.StagingCbvSrvUav(slot));
        }

        private void CreateTimestampResources()
        {
            var heapDesc = new QueryHeapDesc
            {
                Type = QueryHeapType.Timestamp,
                Count = MaxTimestampScopes * 2,
            };
            ComPtr<ID3D12QueryHeap> heap = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateQueryHeap(
                &heapDesc, SilkMarshal.GuidPtrOf<ID3D12QueryHeap>(), (void**)heap.GetAddressOf()));
            _timestampHeap = heap;

            _timestampReadback = CreateCommittedBuffer(
                MaxTimestampScopes * 2 * sizeof(ulong), HeapType.Readback,
                ResourceStates.CopyDest, ResourceFlags.None);

            ulong frequency;
            if (_runtime.Queue.Handle->GetTimestampFrequency(&frequency) >= 0)
            {
                _timestampFrequency = frequency;
            }
        }

        private void CreateDummyTexture()
        {
            byte[] white = { 255, 255, 255, 255 };
            _dummyTexture = CreateTexture(new GpuTextureDesc
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = GpuFormat.R8G8B8A8UNorm,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Dx12.FallbackTexture",
            }, white);
        }

        // ── Draw preparation ────────────────────────────────────────────────────

        /// <summary>
        /// Resolves pending state to a PSO, assembles this draw's descriptor tables, and binds the
        /// input assembler. Returns false when the draw cannot proceed.
        /// </summary>
        private bool PrepareDraw()
        {
            if (!_frames.IsRecording || !_pipeline.Program.IsValid)
            {
                return false;
            }

            nint pso = ResolvePipeline();
            if (pso == 0)
            {
                return false;
            }

            if (pso != _boundPipeline)
            {
                _frames.List->SetPipelineState((ID3D12PipelineState*)pso);
                _boundPipeline = pso;
            }

            _frames.List->IASetPrimitiveTopology(Dx12GpuFormats.ToTopology(_pipeline.Topology));
            BindDescriptorTables();
            BindInputAssembler();
            return true;
        }

        private void BindInputAssembler()
        {
            VertexBufferView* views = stackalloc VertexBufferView[4];
            uint count = 0;

            for (int slot = 0; slot < _vertexBuffers.Length; slot++)
            {
                if (!_vertexBuffers[slot].IsValid ||
                    !_buffers.TryGetValue(_vertexBuffers[slot].Id, out BufferResource buffer))
                {
                    break;
                }

                ulong address = buffer.Usage == GpuBufferUsage.Dynamic
                    ? buffer.DynamicAddress
                    : buffer.Resource.Handle->GetGPUVirtualAddress();
                if (address == 0) break;

                views[count++] = new VertexBufferView
                {
                    BufferLocation = address + (ulong)_vertexOffsets[slot],
                    SizeInBytes = (uint)(buffer.Usage == GpuBufferUsage.Dynamic
                        ? buffer.DynamicSize
                        : buffer.SizeBytes),
                    StrideInBytes = (uint)_vertexStrides[slot],
                };
            }

            if (count > 0)
            {
                _frames.List->IASetVertexBuffers(0u, count, views);
            }

            if (_indexBuffer.IsValid && _buffers.TryGetValue(_indexBuffer.Id, out BufferResource index))
            {
                ulong address = index.Usage == GpuBufferUsage.Dynamic
                    ? index.DynamicAddress
                    : index.Resource.Handle->GetGPUVirtualAddress();
                if (address != 0)
                {
                    var view = new IndexBufferView
                    {
                        BufferLocation = address + (ulong)_indexOffset,
                        SizeInBytes = (uint)(index.Usage == GpuBufferUsage.Dynamic
                            ? index.DynamicSize
                            : index.SizeBytes),
                        Format = _indexFormat == GpuIndexFormat.UInt32
                            ? Format.FormatR32Uint
                            : Format.FormatR16Uint,
                    };
                    _frames.List->IASetIndexBuffer(&view);
                }
            }
        }

        /// <summary>
        /// Copies this draw's views into the frame's descriptor ring and points the root tables at them.
        /// </summary>
        private void BindDescriptorTables()
        {
            _bindings.Reserve(Dx12Bindings.DescriptorsPerDraw, out CpuDescriptorHandle cpu, out GpuDescriptorHandle gpu);
            uint stride = _bindings.CbvSrvUavStride;

            GpuDescriptorHandle vsCbvTable = gpu;
            WriteCbvRange(_vsCbv, ref cpu, stride);

            GpuDescriptorHandle vsSrvTable = Advance(gpu, stride, Dx12Bindings.VertexCbvCount);
            WriteSrvRange(_vsSrv, _vsStructured, ref cpu, stride);

            GpuDescriptorHandle psCbvTable = Advance(
                gpu, stride, Dx12Bindings.VertexCbvCount + Dx12Bindings.VertexSrvCount);
            WriteCbvRange(_psCbv, ref cpu, stride);

            GpuDescriptorHandle psSrvTable = Advance(
                gpu, stride,
                Dx12Bindings.VertexCbvCount + Dx12Bindings.VertexSrvCount + Dx12Bindings.PixelCbvCount);
            WriteSrvRange(_psSrv, _psStructured, ref cpu, stride);

            _frames.List->SetGraphicsRootDescriptorTable(Dx12Bindings.RootVertexCbv, vsCbvTable);
            _frames.List->SetGraphicsRootDescriptorTable(Dx12Bindings.RootVertexSrv, vsSrvTable);
            _frames.List->SetGraphicsRootDescriptorTable(Dx12Bindings.RootPixelCbv, psCbvTable);
            _frames.List->SetGraphicsRootDescriptorTable(Dx12Bindings.RootPixelSrv, psSrvTable);

            _frames.List->SetGraphicsRootDescriptorTable(
                Dx12Bindings.RootVertexSampler, SamplerTableFor(_vsSampler));
            _frames.List->SetGraphicsRootDescriptorTable(
                Dx12Bindings.RootPixelSampler, SamplerTableFor(_psSampler));
        }

        private static GpuDescriptorHandle Advance(GpuDescriptorHandle start, uint stride, int count)
        {
            start.Ptr += (ulong)count * stride;
            return start;
        }

        private void WriteCbvRange(GpuBufferHandle[] slots, ref CpuDescriptorHandle cursor, uint stride)
        {
            foreach (GpuBufferHandle handle in slots)
            {
                var desc = new ConstantBufferViewDesc();
                if (handle.IsValid && _buffers.TryGetValue(handle.Id, out BufferResource buffer))
                {
                    desc.BufferLocation = buffer.Usage == GpuBufferUsage.Dynamic
                        ? buffer.DynamicAddress
                        : buffer.Resource.Handle->GetGPUVirtualAddress();
                    desc.SizeInBytes = (uint)Align(
                        buffer.Usage == GpuBufferUsage.Dynamic ? buffer.DynamicSize : buffer.SizeBytes,
                        ConstantAlignment);
                }

                // A null CBV is legal and reads as zeroes, which is the right answer for a slot the
                // shader declares but this draw does not use.
                _runtime.Device.Handle->CreateConstantBufferView(
                    desc.BufferLocation == 0 ? null : &desc, cursor);
                cursor.Ptr += (nuint)stride;
            }
        }

        private void WriteSrvRange(
            GpuTextureHandle[] textures, GpuBufferHandle[] structured,
            ref CpuDescriptorHandle cursor, uint stride)
        {
            for (int i = 0; i < textures.Length; i++)
            {
                // SrvSlot is -1 for a texture created without ShaderResource — a depth attachment
                // that was never asked to be sampleable, for instance. Copying from a negative
                // offset reads outside the staging heap entirely.
                if (textures[i].IsValid
                    && _textures.TryGetValue(textures[i].Id, out TextureResource texture)
                    && texture.SrvSlot >= 0)
                {
                    TransitionResource(texture.Resource, ref texture.State, ShaderResourceState);
                    _bindings.CopyDescriptor(cursor, _bindings.StagingCbvSrvUav(texture.SrvSlot));
                    cursor.Ptr += (nuint)stride;
                    continue;
                }

                if (structured[i].IsValid && _buffers.TryGetValue(structured[i].Id, out BufferResource buffer))
                {
                    int elementStride = Math.Max(1, buffer.StructureStride);
                    bool dynamic = buffer.Usage == GpuBufferUsage.Dynamic;

                    // A dynamic buffer owns no committed resource — its contents live in this
                    // frame's upload ring — so the view must name the ring and start at the element
                    // its slice begins on. Pointing a structured-buffer SRV at a null resource is
                    // legal and silently reads zeroes, which is how the sprite renderer's instance
                    // data vanished: three instances drawn, every one of them at the origin with
                    // zero size, and a blank frame that reported successful draw calls.
                    ID3D12Resource* resource = dynamic
                        ? (buffer.DynamicRingSlot >= 0 ? _uploadRing[buffer.DynamicRingSlot].Handle : null)
                        : buffer.Resource.Handle;

                    if (resource == null)
                    {
                        WriteNullSrv(ref cursor, stride);
                        continue;
                    }

                    int visibleBytes = dynamic ? buffer.DynamicSize : buffer.SizeBytes;
                    var desc = new ShaderResourceViewDesc
                    {
                        Format = Format.FormatUnknown,
                        ViewDimension = SrvDimension.Buffer,
                        Shader4ComponentMapping = 0x1688,
                    };
                    desc.Anonymous.Buffer = new BufferSrv
                    {
                        FirstElement = dynamic ? buffer.DynamicRingOffset / (ulong)elementStride : 0ul,
                        NumElements = (uint)Math.Max(1, visibleBytes / elementStride),
                        StructureByteStride = (uint)elementStride,
                        Flags = BufferSrvFlags.None,
                    };
                    _runtime.Device.Handle->CreateShaderResourceView(resource, &desc, cursor);
                    cursor.Ptr += (nuint)stride;
                    continue;
                }

                WriteNullSrv(ref cursor, stride);
            }
        }

        /// <summary>
        /// Writes a null SRV, which samples as zero rather than faulting.
        /// </summary>
        /// <remarks>
        /// Every descriptor a table declares must be initialised even when the shader never reads
        /// it; an uninitialised one is undefined behaviour and removes the device on some drivers.
        /// </remarks>
        private void WriteNullSrv(ref CpuDescriptorHandle cursor, uint stride)
        {
            var desc = new ShaderResourceViewDesc
            {
                Format = Format.FormatR8G8B8A8Unorm,
                ViewDimension = SrvDimension.Texture2D,
                Shader4ComponentMapping = 0x1688,
            };
            desc.Anonymous.Texture2D = new Tex2DSrv { MipLevels = 1u };
            _runtime.Device.Handle->CreateShaderResourceView((ID3D12Resource*)null, &desc, cursor);
            cursor.Ptr += (nuint)stride;
        }

        private GpuDescriptorHandle SamplerTableFor(GpuSamplerHandle[] slots)
        {
            var key = new Dx12Bindings.SamplerBlockKey(
                slots.Length > 0 ? slots[0].Id : 0,
                slots.Length > 1 ? slots[1].Id : 0,
                slots.Length > 2 ? slots[2].Id : 0,
                slots.Length > 3 ? slots[3].Id : 0);

            Span<SamplerDesc> descs = stackalloc SamplerDesc[Dx12Bindings.SamplerCount];
            for (int i = 0; i < Dx12Bindings.SamplerCount; i++)
            {
                descs[i] = i < slots.Length && _samplers.TryGetValue(slots[i].Id, out SamplerDesc desc)
                    ? desc
                    : Dx12Bindings.DefaultSampler();
            }

            return _bindings.SamplerTable(key, descs);
        }

        // ── Pipeline state objects ──────────────────────────────────────────────

        /// <summary>
        /// Returns the PSO for the pending state, building it the first time that state is seen.
        /// </summary>
        /// <remarks>
        /// This is what makes an immediate-mode interface implementable on D3D12. The setters record
        /// a key; only a draw needs a real pipeline, and identical keys share one object. Building a
        /// PSO per draw would be correct and unusably slow, which is why the gate counts creations
        /// rather than just checking the picture.
        /// </remarks>
        private nint ResolvePipeline()
        {
            GpuPipelineKey key = _pipeline;
            key.PassSignature = PassSignature();

            if (_pipelines.TryGetValue(key, out nint cached))
            {
                return cached;
            }

            if (!_shaderPrograms.TryGetValue(key.Program.Id, out ShaderProgramResource program))
            {
                return 0;
            }

            nint created = BuildPipeline(key, program);
            _pipelines[key] = created;
            return created;
        }

        /// <summary>Identifies the attachment formats a PSO must be compiled against.</summary>
        private int PassSignature()
        {
            if (!_activeTarget.IsValid)
            {
                return -1;
            }

            RenderTargetResource target = _renderTargets[_activeTarget.Id];
            int signature = 17;
            foreach (GpuFormat format in target.ColorFormats)
            {
                signature = (signature * 31) + (int)format;
            }

            return (signature * 31) + (int)target.DepthFormat;
        }

        private nint BuildPipeline(in GpuPipelineKey key, ShaderProgramResource program)
        {
            var desc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _bindings.RootSignature,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = Dx12GpuFormats.ToTopologyType(key.Topology),
                SampleDesc = new SampleDesc(1u, 0u),
            };

            // Attachment formats come from whichever target is bound, because a PSO is compiled
            // against a specific output signature and mismatching it is a device removal.
            if (_activeTarget.IsValid && _renderTargets.TryGetValue(_activeTarget.Id, out RenderTargetResource target))
            {
                desc.NumRenderTargets = (uint)target.ColorFormats.Length;
                for (int i = 0; i < target.ColorFormats.Length && i < 8; i++)
                {
                    desc.RTVFormats[i] = Dx12GpuFormats.ToDxgi(target.ColorFormats[i]);
                }

                desc.DSVFormat = target.DepthFormat == GpuFormat.Unknown
                    ? Format.FormatUnknown
                    : Dx12GpuFormats.ToDxgi(target.DepthFormat);
            }
            else
            {
                desc.NumRenderTargets = 1u;
                desc.RTVFormats[0] = Dx12SwapChain.ColorFormat;
                desc.DSVFormat = Dx12SwapChain.DepthStencilFormat;
            }

            desc.BlendState = BuildBlend(key.Blend, (int)desc.NumRenderTargets);
            desc.DepthStencilState = BuildDepth(key.Depth, desc.DSVFormat != Format.FormatUnknown);
            desc.RasterizerState = BuildRaster(key.Raster);

            var pinned = new List<GCHandle>();
            try
            {
                fixed (byte* vs = program.VertexShader)
                fixed (byte* ps = program.PixelShader)
                {
                    desc.VS = new ShaderBytecode
                    {
                        PShaderBytecode = vs,
                        BytecodeLength = (nuint)(program.VertexShader?.Length ?? 0),
                    };
                    desc.PS = new ShaderBytecode
                    {
                        PShaderBytecode = ps,
                        BytecodeLength = (nuint)(program.PixelShader?.Length ?? 0),
                    };

                    InputElementDesc* elements = null;
                    uint elementCount = 0;
                    if (key.VertexLayout.IsValid && _vertexLayouts.TryGetValue(key.VertexLayout.Id, out GpuVertexLayoutDesc layout)
                        && layout.Elements is { Length: > 0 })
                    {
                        elementCount = (uint)layout.Elements.Length;
                        elements = (InputElementDesc*)NativeMemory.Alloc(
                            (nuint)(sizeof(InputElementDesc) * elementCount));

                        for (int i = 0; i < layout.Elements.Length; i++)
                        {
                            GpuVertexElement element = layout.Elements[i];
                            GCHandle name = GCHandle.Alloc(
                                System.Text.Encoding.ASCII.GetBytes(element.Semantic + "\0"),
                                GCHandleType.Pinned);
                            pinned.Add(name);

                            elements[i] = new InputElementDesc
                            {
                                SemanticName = (byte*)name.AddrOfPinnedObject(),
                                SemanticIndex = (uint)element.SemanticIndex,
                                Format = Dx12GpuFormats.ToDxgi(element.Format),
                                InputSlot = (uint)element.Slot,
                                AlignedByteOffset = (uint)element.OffsetBytes,
                                InputSlotClass = element.InstanceStepRate > 0
                                    ? InputClassification.PerInstanceData
                                    : InputClassification.PerVertexData,
                                InstanceDataStepRate = (uint)element.InstanceStepRate,
                            };
                        }
                    }

                    desc.InputLayout = new InputLayoutDesc
                    {
                        PInputElementDescs = elements,
                        NumElements = elementCount,
                    };

                    ComPtr<ID3D12PipelineState> pso = default;
                    int hr = _runtime.Device.Handle->CreateGraphicsPipelineState(
                        &desc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pso.GetAddressOf());

                    if (elements != null)
                    {
                        NativeMemory.Free(elements);
                    }

                    if (hr < 0)
                    {
                        Diagnostics.RenderLog.Line(
                            $"[DX12] Pipeline state creation failed (0x{hr:X8}); the draw is skipped.");
                        return 0;
                    }

                    return (nint)pso.Handle;
                }
            }
            finally
            {
                foreach (GCHandle handle in pinned)
                {
                    handle.Free();
                }
            }
        }

        private static BlendDesc BuildBlend(in GpuBlendState state, int targetCount)
        {
            var blend = new BlendDesc
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = state.IndependentBlend,
            };

            // Match the D3D11 device: RT0 is always the primary formula. When IndependentBlend is
            // set, RT1 uses the secondary fields even if SecondaryEnabled is false — that false is
            // how the fog-skip MRT gets a clean overwrite instead of inheriting alpha blending.
            blend.RenderTarget[0] = RtBlend(
                state.Enabled,
                state.SrcColor, state.DstColor, state.ColorOp,
                state.SrcAlpha, state.DstAlpha, state.AlphaOp,
                WriteMask(state, secondary: false));

            if (state.IndependentBlend)
            {
                blend.RenderTarget[1] = RtBlend(
                    state.SecondaryEnabled,
                    state.SecondarySrcColor, state.SecondaryDstColor, state.SecondaryColorOp,
                    state.SecondarySrcAlpha, state.SecondaryDstAlpha, state.SecondaryAlphaOp,
                    WriteMask(state, secondary: true));
            }

            return blend;
        }

        private static RenderTargetBlendDesc RtBlend(
            bool enabled,
            GpuBlendFactor srcColor, GpuBlendFactor dstColor, GpuBlendOp colorOp,
            GpuBlendFactor srcAlpha, GpuBlendFactor dstAlpha, GpuBlendOp alphaOp,
            int writeMask) => new()
        {
            BlendEnable = enabled,
            LogicOpEnable = false,
            SrcBlend = Dx12GpuFormats.ToBlend(srcColor),
            DestBlend = Dx12GpuFormats.ToBlend(dstColor),
            BlendOp = Dx12GpuFormats.ToBlendOp(colorOp),
            SrcBlendAlpha = Dx12GpuFormats.ToBlend(srcAlpha),
            DestBlendAlpha = Dx12GpuFormats.ToBlend(dstAlpha),
            BlendOpAlpha = Dx12GpuFormats.ToBlendOp(alphaOp),
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)writeMask,
        };

        private static int WriteMask(in GpuBlendState state, bool secondary)
        {
            int mask = 0;
            if (secondary ? state.SecondaryWriteR : state.WriteR) mask |= 1;
            if (secondary ? state.SecondaryWriteG : state.WriteG) mask |= 2;
            if (secondary ? state.SecondaryWriteB : state.WriteB) mask |= 4;
            if (secondary ? state.SecondaryWriteA : state.WriteA) mask |= 8;
            return mask;
        }

        private static DepthStencilDesc BuildDepth(in GpuDepthState state, bool hasDepthAttachment) => new()
        {
            DepthEnable = state.TestEnabled && hasDepthAttachment,
            DepthWriteMask = state.WriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthFunc = Dx12GpuFormats.ToComparison(state.Compare),
            StencilEnable = false,
        };

        private static RasterizerDesc BuildRaster(in GpuRasterState state) => new()
        {
            FillMode = Dx12GpuFormats.ToFill(state.FillMode),
            CullMode = Dx12GpuFormats.ToCull(state.CullMode),
            FrontCounterClockwise = state.FrontCounterClockwise,
            DepthBias = (int)state.DepthBias,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = state.SlopeScaledDepthBias,
            DepthClipEnable = state.DepthClipEnabled,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
            ForcedSampleCount = 0u,
            ConservativeRaster = ConservativeRasterizationMode.Off,
        };
    }
}

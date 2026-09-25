using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;
using VkImage = Silk.NET.Vulkan.Image;
using VkSampler = Silk.NET.Vulkan.Sampler;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>The Vulkan implementation of <see cref="IGpuDevice"/>.</summary>
    /// <remarks>
    /// <para><b>Immediate-mode on a record-then-submit API.</b> The interface sets state one call at
    /// a time and resolves it at draw; this device owns the frame's command buffer and records as
    /// calls arrive, resolving pipeline and descriptor state in <see cref="BindForDraw"/>.</para>
    ///
    /// <para><b>Direct3D coordinate conventions are preserved</b> with a negative-height viewport
    /// (core since Vulkan 1.1), so <c>ClipSpaceZeroToOne</c> and <c>FramebufferOriginTopLeft</c> both
    /// hold and no matrix or shader differs from the other backends. The cost is a reversed winding,
    /// which the pipeline cache negates.</para>
    /// </remarks>
    internal sealed unsafe partial class VulkanGpuDevice : IGpuDevice
    {
        private const int MaxVertexSlots = 4;

        private readonly VulkanRuntime _runtime;
        private readonly VulkanFrameRing _frameRing;
        private readonly VulkanDescriptors _descriptors;
        private readonly VulkanPipelineCache _pipelines;
        private readonly VulkanUploadRing _uploadRing;

        private int _nextId = 1;
        private bool _disposed;
        private bool _inFrame;

        // ── Tracked state, resolved at draw ─────────────────────────────────────
        private GpuPrimitiveTopology _topology = GpuPrimitiveTopology.TriangleList;
        private GpuBlendState _blend = GpuBlendState.Opaque;
        private GpuDepthState _depth = GpuDepthState.Default;
        private GpuRasterState _raster = GpuRasterState.Default;
        private GpuShaderProgramHandle _program;
        private GpuVertexLayoutHandle _vertexLayout;

        private readonly GpuBufferHandle[] _vertexBuffers = new GpuBufferHandle[MaxVertexSlots];
        private readonly int[] _vertexOffsets = new int[MaxVertexSlots];
        private GpuBufferHandle _indexBuffer;
        private GpuIndexFormat _indexFormat = GpuIndexFormat.UInt16;
        private int _indexOffset;

        private readonly GpuBufferHandle[] _boundCbvs = new GpuBufferHandle[VulkanDescriptors.CbvCount];
        private readonly GpuTextureHandle[] _boundSrvs = new GpuTextureHandle[VulkanDescriptors.SrvCount];
        private readonly GpuBufferHandle[] _boundStructured = new GpuBufferHandle[VulkanDescriptors.SrvCount];
        private readonly GpuSamplerHandle[] _boundSamplers = new GpuSamplerHandle[VulkanDescriptors.SamplerCount];

        private VulkanSwapChainAdapter _activeSwapChain;
        private RenderPass _activeRenderPass;
        private Framebuffer _activeFramebuffer;
        private int _passWidth, _passHeight, _passColorCount;
        private bool _passHasDepth;
        private bool _inRenderPass;

        private GpuTextureHandle _fallbackTexture = GpuTextureHandle.Invalid;
        private GpuTextureHandle _fallbackTextureArray = GpuTextureHandle.Invalid;
        private GpuTextureHandle _fallbackDepthTexture = GpuTextureHandle.Invalid;

        /// <summary>
        /// The offscreen target the current pass is drawing into, or null for the swap chain.
        /// </summary>
        /// <remarks>
        /// Tracked purely so <see cref="EndRenderPass"/> can return its attachments to a sampleable
        /// layout once the pass has closed. Colour-only passes (composite, water) leave depth in
        /// the layout the previous pass finished in so it can be sampled without being re-attached
        /// (NEXT-138).
        /// </remarks>
        private RenderTargetResource _activeTargetResource;
        private bool _activePassIncludesDepth;
        private GpuSamplerHandle _fallbackSampler = GpuSamplerHandle.Invalid;
        private GpuBufferHandle _fallbackBuffer = GpuBufferHandle.Invalid;

        public VulkanGpuDevice()
        {
            _runtime = VulkanRuntime.Acquire();
            _frameRing = new VulkanFrameRing(_runtime);
            _descriptors = new VulkanDescriptors(_runtime);
            _pipelines = new VulkanPipelineCache(_runtime);
            _uploadRing = new VulkanUploadRing(_runtime);

            Capabilities = new GpuCapabilities
            {
                ClipSpaceZeroToOne = true,
                FramebufferOriginTopLeft = true,
                SupportsStructuredBuffers = true,
                SupportsUInt32Indices = true,
                SupportsTimestampQueries = false,
                SupportsComputeShaders = true,
                SupportsIndirectDraw = true,
                MaxTextureArrayLayers = 2048,
                MaxAnisotropy = 16,
                MaxColorAttachments = 8,
                DepthBiasScale = 1f,
            };

            CreateFallbacks();
        }

        public string BackendName => "Vulkan";

        public string AdapterName => _runtime.AdapterName;

        public GpuShaderBinaryFormat ShaderBinaryFormat => GpuShaderBinaryFormat.SpirV;

        public GpuCapabilities Capabilities { get; }

        // ── Frame envelope ──────────────────────────────────────────────────────

        public IGpuSwapChain CreateSwapChain(IntPtr windowHandle, int width, int height)
        {
            var swapChain = new VulkanSwapChain(_runtime, windowHandle, width, height);
            var adapter = new VulkanSwapChainAdapter(this, swapChain);
            _activeSwapChain = adapter;
            return adapter;
        }

        public void BeginFrame()
        {
            if (_inFrame) return;

            // OUT_OF_DATE/SUBOPTIMAL is reported by Present, so the rebuild must happen before a
            // new frame slot and command buffer are opened. Rebuilding from AcquireForFrame used
            // to call WaitIdle after _inFrame was set; WaitIdle quite correctly ended that frame,
            // but BeginFrame then carried on recording into the command buffer it had just closed.
            // The eventual present received a stale/default semaphore and could access-violate in
            // the Vulkan loader instead of producing a managed error.
            _activeSwapChain?.EnsureReadyForFrame();
            _inFrame = true;

            // A new command buffer cannot be inside anything the previous frame left open.
            _inRenderPass = false;
            _activeRenderPass = default;

            _frameRing.BeginFrame();
            _descriptors.BeginFrame(_frameRing.FrameIndex);
            _uploadRing.BeginFrame(_frameRing.FrameIndex);
            foreach (BufferResource buffer in _buffers.Values)
            {
                if (buffer.Usage == GpuBufferUsage.Dynamic)
                {
                    buffer.DynamicBuffer = default;
                    buffer.DynamicOffset = 0;
                }
            }
            _activeSwapChain?.AcquireForFrame(_frameRing.AcquiredSemaphore);
        }

        public void EndFrame()
        {
            if (!_inFrame) return;
            _inFrame = false;

            EndRenderPass();
            _activeSwapChain?.PrepareForPresent(_frameRing.CurrentCommandBuffer);

            // Wait/signal only on the submission that actually acquired the image. A readback frame
            // re-enters BeginFrame on an image it already holds; waiting again on an acquire
            // semaphore nothing re-signalled would deadlock the queue, and signalling
            // render-finished twice without an intervening wait is a validation error.
            bool acquiredNow = _activeSwapChain is { AcquiredThisFrame: true };
            VkSemaphore wait = acquiredNow ? _activeSwapChain.AcquiredSemaphore : default;
            VkSemaphore signal = acquiredNow ? _activeSwapChain.RenderFinishedSemaphore : default;
            if (acquiredNow)
            {
                _pendingPresentSemaphore = signal;
            }

            _frameRing.EndFrameAndSubmit(waitForAcquire: acquiredNow, wait, signal);
        }

        private Silk.NET.Vulkan.Semaphore _pendingPresentSemaphore;

        /// <summary>The semaphore the frame's submission signalled, captured before the slot advanced.</summary>
        internal Silk.NET.Vulkan.Semaphore PendingPresentSemaphore => _pendingPresentSemaphore;

        internal void ClearPendingPresentSemaphore() => _pendingPresentSemaphore = default;

        internal Silk.NET.Vulkan.Semaphore CurrentAcquiredSemaphore => _frameRing.AcquiredSemaphore;

        public void WaitIdle()
        {
            if (_inFrame)
            {
                EndFrame();
            }
            else
            {
                EndRenderPass();
            }
            _frameRing.WaitIdle();
        }

        /// <summary>Drops pipelines built against a render pass a resize has replaced.</summary>
        internal void OnSwapChainRecreated(RenderPass previous) => _pipelines.InvalidateForRenderPass(previous);

        internal void ForgetSwapChain(VulkanSwapChainAdapter adapter)
        {
            if (ReferenceEquals(_activeSwapChain, adapter)) _activeSwapChain = null;
        }

        /// <summary>Removes a non-owning image wrapper before its swap chain destroys the image.</summary>
        internal void ReleaseSwapChainTextureWrapper(GpuTextureHandle handle)
        {
            if (!handle.IsValid || !_textures.Remove(handle.Id, out TextureResource texture)) return;

            // The image belongs to the swap chain, but this additional view belongs to the wrapper.
            // Vulkan requires every view to be gone before the underlying swap-chain image is.
            if (texture.View.Handle != 0)
            {
                _runtime.Api.DestroyImageView(_runtime.Device, texture.View, null);
            }
        }

        internal void TransitionSwapChainImage(
            CommandBuffer cmd, VkImage image, ImageLayout from, ImageLayout to, ImageAspectFlags aspect)
        {
            if (from == to) return;

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
                SrcAccessMask = AccessFlags.None,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
            };

            _runtime.Api.CmdPipelineBarrier(
                cmd,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                DependencyFlags.None,
                0, null, 0, null, 1, &barrier);
        }

        /// <summary>
        /// Presents a swap chain image as a texture handle, reusing the same handle each frame.
        /// </summary>
        /// <remarks>
        /// The image belongs to the swap chain, so the wrapper is marked as not owning it — freeing
        /// it would destroy a resource the presentation engine still holds.
        /// </remarks>
        internal GpuTextureHandle WrapSwapChainImage(
            GpuTextureHandle existing, VkImage image, int width, int height)
        {
            ImageLayout currentLayout = _activeSwapChain != null && _activeSwapChain.HasAcquiredImage
                ? _activeSwapChain.Inner.LayoutAt(_activeSwapChain.Inner.AcquiredIndex)
                : ImageLayout.ColorAttachmentOptimal;

            if (existing.IsValid && _textures.TryGetValue(existing.Id, out TextureResource previous))
            {
                if (previous.Image.Handle == image.Handle)
                {
                    previous.Layout = currentLayout;
                    return existing;
                }
                if (previous.View.Handle != 0) _runtime.Api.DestroyImageView(_runtime.Device, previous.View, null);
                _textures.Remove(existing.Id);
            }

            var resource = new TextureResource
            {
                Image = image,
                Width = width,
                Height = height,
                MipLevels = 1,
                Format = GpuFormat.B8G8R8A8UNorm,
                OwnsImage = false,
                Layout = currentLayout,
            };

            resource.View = CreateView(
                image, VulkanGpuFormats.ToVulkan(GpuFormat.B8G8R8A8UNorm), ImageAspectFlags.ColorBit, 1);

            int id = _nextId++;
            _textures[id] = resource;
            return new GpuTextureHandle(id);
        }

        /// <summary>
        /// Publishes the swap chain's depth image as a sampleable texture handle.
        /// </summary>
        /// <remarks>
        /// The image already existed and was already transitioned for rendering — only the *handle*
        /// was missing, and `ForwardRenderer.Flush` returns before drawing anything when the swap
        /// chain reports no depth texture. That single unpublished handle is why Vulkan rendered no
        /// 3D at all while 2D worked perfectly (the sprite pass runs with depth explicitly invalid
        /// and never needed one).
        ///
        /// The view is created over the depth aspect so the post-process composite can sample scene
        /// depth for screen-space fog; a handle without a sampleable view would satisfy the gate and
        /// then quietly feed the fog zeroes. Ownership stays with the swap chain.
        /// </remarks>
        internal GpuTextureHandle WrapSwapChainDepthImage(
            GpuTextureHandle existing, VkImage image, int width, int height, GpuFormat format)
        {
            if (image.Handle == 0) return GpuTextureHandle.Invalid;

            if (existing.IsValid && _textures.TryGetValue(existing.Id, out TextureResource previous))
            {
                if (previous.Image.Handle == image.Handle)
                {
                    return existing;
                }

                // A resize rebuilds the depth image; the old view would sample freed memory.
                if (previous.View.Handle != 0) _runtime.Api.DestroyImageView(_runtime.Device, previous.View, null);
                _textures.Remove(existing.Id);
            }

            var resource = new TextureResource
            {
                Image = image,
                Width = width,
                Height = height,
                MipLevels = 1,
                Format = format,
                IsDepth = true,
                OwnsImage = false,
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };

            resource.View = CreateView(
                image, VulkanGpuFormats.ToVulkan(format), ImageAspectFlags.DepthBit, 1);

            int id = _nextId++;
            _textures[id] = resource;
            return new GpuTextureHandle(id);
        }

        // ── Passes ──────────────────────────────────────────────────────────────

        public void BeginRenderPass(in GpuRenderPassDesc desc)
        {
            // Readback and surface recreation can finish the previous frame. A new pass needs
            // a recording command buffer before any image-layout barriers are emitted.
            if (!_inFrame) BeginFrame();
            EndRenderPass();

            if (desc.Target.IsValid && _renderTargets.TryGetValue(desc.Target.Id, out RenderTargetResource target))
            {
                bool useDepth = desc.HasDepth && target.Depth.IsValid;
                if (!useDepth && target.ColorOnlyRenderPass.Handle != 0)
                {
                    _activeRenderPass = target.ColorOnlyRenderPass;
                    _activeFramebuffer = target.ColorOnlyFramebuffer;
                    _passHasDepth = false;
                    TransitionRenderTarget(target, toAttachment: true, includeDepth: false);
                }
                else
                {
                    _activeRenderPass = target.RenderPass;
                    _activeFramebuffer = target.Framebuffer;
                    _passHasDepth = target.Depth.IsValid;
                    TransitionRenderTarget(target, toAttachment: true, includeDepth: _passHasDepth);
                }

                _passWidth = target.Width;
                _passHeight = target.Height;
                _passColorCount = Math.Max(1, target.Colors.Length);
                _activePassIncludesDepth = _passHasDepth;
                _activeTargetResource = target;
            }
            else if (_activeSwapChain is { HasAcquiredImage: true } swapChain)
            {
                bool useDepth = desc.HasDepth;
                _activeRenderPass = useDepth ? swapChain.Inner.RenderPass : swapChain.Inner.ColorOnlyRenderPass;
                _activeFramebuffer = useDepth
                    ? swapChain.Inner.AcquiredFramebuffer
                    : swapChain.Inner.AcquiredColorOnlyFramebuffer;
                _passWidth = swapChain.Inner.Width;
                _passHeight = swapChain.Inner.Height;
                _passColorCount = 1;
                _passHasDepth = useDepth;
                _activePassIncludesDepth = useDepth;
                _activeTargetResource = null;
                swapChain.PrepareForRendering(_frameRing.CurrentCommandBuffer, includeDepth: useDepth);
            }
            else
            {
                return;
            }

            if (_activeRenderPass.Handle == 0 || _activeFramebuffer.Handle == 0 || _passWidth <= 0 || _passHeight <= 0)
            {
                return;
            }

            CommandBuffer cmd = _frameRing.CurrentCommandBuffer;
            var area = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_passWidth, (uint)_passHeight));

            var begin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _activeRenderPass,
                Framebuffer = _activeFramebuffer,
                RenderArea = area,
                ClearValueCount = 0,
            };

            _runtime.Api.CmdBeginRenderPass(cmd, &begin, SubpassContents.Inline);
            _inRenderPass = true;

            ApplyClears(cmd, desc, area);
            SetViewport(0, 0, _passWidth, _passHeight);
            SetScissor(0, 0, _passWidth, _passHeight);
        }

        private void ApplyClears(CommandBuffer cmd, in GpuRenderPassDesc desc, Rect2D area)
        {
            GpuAttachmentAction[] colors = desc.ColorActions ?? Array.Empty<GpuAttachmentAction>();
            var attachments = stackalloc ClearAttachment[9];
            int count = 0;

            for (int i = 0; i < colors.Length && i < 8; i++)
            {
                if (colors[i].Load != GpuLoadAction.Clear) continue;
                var ccv = new ClearColorValue(colors[i].ClearR, colors[i].ClearG, colors[i].ClearB, colors[i].ClearA);
                attachments[count++] = new ClearAttachment
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    ColorAttachment = (uint)i,
                    ClearValue = new ClearValue(ccv),
                };
            }

            if (_passHasDepth && desc.HasDepth && desc.DepthAction.Load == GpuLoadAction.Clear)
            {
                attachments[count++] = new ClearAttachment
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(desc.DepthAction.ClearR, 0)),
                };
            }

            if (count == 0) return;

            // Cleared inside the pass because the attachments load rather than clear: one render
            // pass then serves both cleared and preserved passes, and therefore one set of pipelines.
            var rect = new ClearRect { Rect = area, BaseArrayLayer = 0, LayerCount = 1 };
            _runtime.Api.CmdClearAttachments(cmd, (uint)count, attachments, 1, &rect);
        }

        public void EndRenderPass()
        {
            // Guarded on the ring still recording, not just on the flag. A mid-frame flush (texture
            // upload, readback) submits the command buffer and advances the slot, which ends the
            // pass implicitly — issuing vkCmdEndRenderPass afterwards targets a buffer that is not
            // recording and takes the process out with an access violation.
            if (!_inRenderPass || !_frameRing.IsRecording)
            {
                _inRenderPass = false;
                _activeRenderPass = default;
                _activeTargetResource = null;
                _activePassIncludesDepth = false;
                return;
            }

            _runtime.Api.CmdEndRenderPass(_frameRing.CurrentCommandBuffer);
            _inRenderPass = false;
            _activeRenderPass = default;

            // Attachments become sampleable here, after the pass has ended — a layout barrier is
            // illegal inside a render pass, which is why this cannot live where the descriptor is
            // written. The descriptor has always declared ShaderReadOnlyOptimal; nothing ever moved
            // the image into it, so every sample of a render target read an image in the wrong
            // layout. Colour-only passes must not transition depth: water/composite sample it.
            if (_activeTargetResource is not null)
            {
                TransitionRenderTarget(
                    _activeTargetResource, toAttachment: false, includeDepth: _activePassIncludesDepth);
                _activeTargetResource = null;
            }

            _activePassIncludesDepth = false;
        }

        public void UnbindRenderTargets() => EndRenderPass();

        public void SetViewport(float x, float y, float width, float height, float minZ = 0f, float maxZ = 1f)
        {
            if (!_inRenderPass) return;

            // Negative height with the origin moved to the bottom: this is what makes Vulkan agree
            // with Direct3D's top-left framebuffer origin, so no projection matrix or shader has to
            // differ per backend. Core since 1.1 (VK_KHR_maintenance1).
            var viewport = new Viewport
            {
                X = x,
                Y = y + height,
                Width = width,
                Height = -height,
                MinDepth = minZ,
                MaxDepth = maxZ,
            };

            _runtime.Api.CmdSetViewport(_frameRing.CurrentCommandBuffer, 0, 1, &viewport);
        }

        public void SetScissor(int x, int y, int width, int height)
        {
            if (!_inRenderPass) return;

            var scissor = new Rect2D(
                new Offset2D(Math.Max(0, x), Math.Max(0, y)),
                new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
            _runtime.Api.CmdSetScissor(_frameRing.CurrentCommandBuffer, 0, 1, &scissor);
        }

        // ── Pipeline state ──────────────────────────────────────────────────────

        public void SetShaderProgram(GpuShaderProgramHandle handle) => _program = handle;
        public void SetVertexLayout(GpuVertexLayoutHandle handle) => _vertexLayout = handle;
        public void SetPrimitiveTopology(GpuPrimitiveTopology topology) => _topology = topology;
        public void SetBlendState(in GpuBlendState state) => _blend = state;
        public void SetDepthState(in GpuDepthState state) => _depth = state;
        public void SetRasterState(in GpuRasterState state) => _raster = state;

        // ── Bindings ────────────────────────────────────────────────────────────

        public void SetConstantBuffer(GpuShaderStage stage, int bRegister, GpuBufferHandle handle)
        {
            if ((uint)bRegister < _boundCbvs.Length) _boundCbvs[bRegister] = handle;
        }

        public void SetTexture(GpuShaderStage stage, int tRegister, GpuTextureHandle handle)
        {
            if ((uint)tRegister < _boundSrvs.Length)
            {
                // Leave Invalid as Invalid. WriteDescriptors picks a colour, array, or depth
                // fallback from the SPIR-V image type so SampleCmp never sees R8G8B8A8 (NEXT-139).
                _boundSrvs[tRegister] = handle;
            }
        }

        public void ClearTexture(GpuShaderStage stage, int tRegister)
        {
            if ((uint)tRegister < _boundSrvs.Length) _boundSrvs[tRegister] = GpuTextureHandle.Invalid;
        }

        /// <summary>
        /// Binds a structured buffer, which shares the <c>t</c> register space with textures exactly
        /// as it does in HLSL and is written as a storage-buffer descriptor at the same binding.
        /// </summary>
        public void SetStructuredBuffer(GpuShaderStage stage, int tRegister, GpuBufferHandle handle)
        {
            if ((uint)tRegister < _boundStructured.Length) _boundStructured[tRegister] = handle;
        }

        public void SetSampler(GpuShaderStage stage, int sRegister, GpuSamplerHandle handle)
        {
            if ((uint)sRegister < _boundSamplers.Length) _boundSamplers[sRegister] = handle;
        }

        public void SetVertexBuffer(int slot, GpuBufferHandle handle, int stride, int offset = 0)
        {
            if ((uint)slot >= MaxVertexSlots) return;
            _vertexBuffers[slot] = handle;
            _vertexOffsets[slot] = offset;
        }

        public void SetIndexBuffer(GpuBufferHandle handle, GpuIndexFormat format, int offset = 0)
        {
            _indexBuffer = handle;
            _indexFormat = format;
            _indexOffset = offset;
        }

        // ── Draws ───────────────────────────────────────────────────────────────

        public void Draw(int vertexCount, int startVertex = 0)
        {
            if (!BindForDraw(out CommandBuffer cmd)) return;
            _runtime.Api.CmdDraw(cmd, (uint)vertexCount, 1, (uint)startVertex, 0);
        }

        public void DrawIndexed(int indexCount, int startIndex = 0, int baseVertex = 0)
        {
            if (!BindForDraw(out CommandBuffer cmd) || !BindIndexBuffer(cmd)) return;
            _runtime.Api.CmdDrawIndexed(cmd, (uint)indexCount, 1, (uint)startIndex, baseVertex, 0);
        }

        public void DrawIndexedInstanced(
            int indexCountPerInstance, int instanceCount,
            int startIndex = 0, int baseVertex = 0, int startInstance = 0)
        {
            if (!BindForDraw(out CommandBuffer cmd) || !BindIndexBuffer(cmd)) return;
            _runtime.Api.CmdDrawIndexed(
                cmd, (uint)indexCountPerInstance, (uint)instanceCount,
                (uint)startIndex, baseVertex, (uint)startInstance);
        }

        private bool BindIndexBuffer(CommandBuffer cmd)
        {
            if (!_buffers.TryGetValue(_indexBuffer.Id, out BufferResource index)) return false;

            VkBuffer indexBuffer = BufferFor(index, out ulong indexBase);
            ulong offset = indexBase + (ulong)_indexOffset;
            _runtime.Api.CmdBindIndexBuffer(
                cmd, indexBuffer, offset,
                _indexFormat == GpuIndexFormat.UInt16 ? IndexType.Uint16 : IndexType.Uint32);
            return true;
        }

        /// <summary>Resolves every deferred setter into a bound pipeline and descriptor set.</summary>
        private bool BindForDraw(out CommandBuffer cmd)
        {
            cmd = _frameRing.CurrentCommandBuffer;

            if (!_inRenderPass || !_programs.TryGetValue(_program.Id, out ProgramResource program))
            {
                return false;
            }

            // A missing vertex layout is legal, not a failure. The post-process composite draws a
            // fullscreen triangle generated entirely from SV_VertexID with no vertex buffer bound,
            // so it deliberately sets an invalid layout handle. Rejecting the draw here is what
            // left the finished 3D scene sitting in the offscreen HDR target, never blitted to the
            // back buffer — the frame rendered correctly and then nothing put it on screen.
            if (!_vertexLayouts.TryGetValue(_vertexLayout.Id, out GpuVertexLayoutDesc layout))
            {
                layout = default;
            }

            var key = new VulkanPipelineKey(
                _program.Id, _vertexLayout.Id, _topology, _blend, _depth, _raster,
                _activeRenderPass.Handle, _passColorCount, _passHasDepth);

            Pipeline pipeline = _pipelines.GetOrCreate(
                key, program.VertexShader, program.VertexEntry, program.PixelShader, program.PixelEntry,
                program.PipelineLayout, layout, _activeRenderPass);

            _runtime.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            BindVertexBuffers(cmd, layout);
            WriteDescriptors(cmd, program);
            return true;
        }

        private void BindVertexBuffers(CommandBuffer cmd, in GpuVertexLayoutDesc layout)
        {
            int[] strides = layout.SlotStrides ?? Array.Empty<int>();
            for (int slot = 0; slot < strides.Length && slot < MaxVertexSlots; slot++)
            {
                if (strides[slot] <= 0
                    || !_buffers.TryGetValue(_vertexBuffers[slot].Id, out BufferResource vertex))
                {
                    continue;
                }

                VkBuffer buffer = BufferFor(vertex, out ulong baseOffset);
                ulong offset = baseOffset + (ulong)_vertexOffsets[slot];
                _runtime.Api.CmdBindVertexBuffers(cmd, (uint)slot, 1, &buffer, &offset);
            }
        }

        /// <summary>
        /// Writes one descriptor per binding the program declared, using the type it declared.
        /// </summary>
        /// <remarks>
        /// Every declared binding is written, falling back to a white texture, a default sampler or
        /// an empty buffer when the renderer bound nothing. That keeps the set complete without
        /// depending on <c>descriptorBindingPartiallyBound</c> — and writing the wrong descriptor
        /// type is invalid but silent, which is what made the previous backend render blank.
        /// </remarks>
        private void WriteDescriptors(CommandBuffer cmd, ProgramResource program)
        {
            DescriptorSet set = _descriptors.Allocate(program.Bindings.Layout);

            int count = program.Bindings.Types.Count;
            if (count == 0)
            {
                _runtime.Api.CmdBindDescriptorSets(
                    cmd, PipelineBindPoint.Graphics, program.PipelineLayout, 0, 0, null, 0, null);
                return;
            }

            var writes = stackalloc WriteDescriptorSet[count];
            var buffers = stackalloc DescriptorBufferInfo[count];
            var images = stackalloc DescriptorImageInfo[count];
            int writeCount = 0, bufferCount = 0, imageCount = 0;

            foreach (KeyValuePair<uint, DescriptorType> entry in program.Bindings.Types)
            {
                uint binding = entry.Key;
                DescriptorType type = entry.Value;
                int register = RegisterFor(binding, type);

                switch (type)
                {
                    case DescriptorType.UniformBuffer:
                    {
                        BufferResource resource = ResolveBuffer(
                            register >= 0 && register < _boundCbvs.Length ? _boundCbvs[register] : default);
                        buffers[bufferCount] = DescribeBuffer(resource);
                        writes[writeCount++] = Write(set, binding, type, buffer: &buffers[bufferCount++]);
                        break;
                    }

                    case DescriptorType.StorageBuffer:
                    {
                        BufferResource resource = ResolveBuffer(
                            register >= 0 && register < _boundStructured.Length ? _boundStructured[register] : default);
                        buffers[bufferCount] = DescribeBuffer(resource);
                        writes[writeCount++] = Write(set, binding, type, buffer: &buffers[bufferCount++]);
                        break;
                    }

                    case DescriptorType.SampledImage:
                    {
                        TextureResource texture = ResolveTexture(
                            register >= 0 && register < _boundSrvs.Length ? _boundSrvs[register] : default,
                            program.Bindings.TextureArrayBindings.Contains(binding),
                            program.Bindings.DepthImageBindings.Contains(binding));
                        images[imageCount] = new DescriptorImageInfo
                        {
                            ImageView = texture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                        };
                        writes[writeCount++] = Write(set, binding, type, image: &images[imageCount++]);
                        break;
                    }

                    case DescriptorType.Sampler:
                    {
                        VkSampler sampler = ResolveSampler(
                            register >= 0 && register < _boundSamplers.Length ? _boundSamplers[register] : default);
                        images[imageCount] = new DescriptorImageInfo { Sampler = sampler };
                        writes[writeCount++] = Write(set, binding, type, image: &images[imageCount++]);
                        break;
                    }
                }
            }

            if (writeCount > 0)
            {
                _runtime.Api.UpdateDescriptorSets(_runtime.Device, (uint)writeCount, writes, 0, null);
            }

            DescriptorSet local = set;
            _runtime.Api.CmdBindDescriptorSets(
                cmd, PipelineBindPoint.Graphics, program.PipelineLayout, 0, 1, &local, 0, null);
        }

        /// <summary>
        /// Names the memory a buffer is currently backed by: its own allocation, or — for a dynamic
        /// buffer — the ring slice its most recent write took.
        /// </summary>
        private DescriptorBufferInfo DescribeBuffer(BufferResource resource)
        {
            if (resource.Usage == GpuBufferUsage.Dynamic && resource.DynamicBuffer.Handle != 0)
            {
                return new DescriptorBufferInfo
                {
                    Buffer = resource.DynamicBuffer,
                    Offset = resource.DynamicOffset,
                    Range = (ulong)resource.DynamicSize,
                };
            }

            VkBuffer buffer = resource.Buffer.Handle != 0 ? resource.Buffer : _buffers[_fallbackBuffer.Id].Buffer;
            return new DescriptorBufferInfo
            {
                Buffer = buffer,
                Offset = 0,
                Range = Vk.WholeSize,
            };
        }

        /// <summary>The buffer and starting offset a bind should use for this resource.</summary>
        private VkBuffer BufferFor(BufferResource resource, out ulong baseOffset)
        {
            if (resource.Usage == GpuBufferUsage.Dynamic && resource.DynamicBuffer.Handle != 0)
            {
                baseOffset = resource.DynamicOffset;
                return resource.DynamicBuffer;
            }

            baseOffset = 0;
            return resource.Buffer.Handle != 0 ? resource.Buffer : _buffers[_fallbackBuffer.Id].Buffer;
        }

        private static WriteDescriptorSet Write(
            DescriptorSet set, uint binding, DescriptorType type,
            DescriptorBufferInfo* buffer = null, DescriptorImageInfo* image = null) => new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = type,
            PBufferInfo = buffer,
            PImageInfo = image,
        };

        /// <summary>Maps a Vulkan binding back to the HLSL register it came from.</summary>
        private static int RegisterFor(uint binding, DescriptorType type) => type switch
        {
            DescriptorType.UniformBuffer => (int)binding - VulkanShaderBindingPolicy.CbvBaseBinding,
            DescriptorType.StorageBuffer or DescriptorType.SampledImage =>
                (int)binding - VulkanShaderBindingPolicy.SrvBaseBinding,
            DescriptorType.Sampler => (int)binding - VulkanShaderBindingPolicy.SamplerBaseBinding,
            _ => -1,
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_inFrame)
            {
                EndFrame();
            }
            else
            {
                EndRenderPass();
            }
            _frameRing.WaitIdle();
            DisposeResources();

            _pipelines.Dispose();
            _descriptors.Dispose();
            _uploadRing.Dispose();
            _frameRing.Dispose();
            VulkanRuntime.Release();
        }
    }
}

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

namespace Genesis.Rendering.SilkNet.Vulkan
{
    internal sealed unsafe partial class VulkanGpuDevice
    {
        internal sealed class BufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public int SizeBytes;
            public bool HostVisible;
            public void* Mapped;

            public GpuBufferUsage Usage;
            public GpuBindFlags BindFlags;
            public int StructureStride;
            public string? DebugName;

            /// <summary>
            /// Where this dynamic buffer's most recent write landed in the frame's upload ring.
            /// </summary>
            /// <remarks>
            /// A dynamic buffer has no storage of its own: every write takes a fresh slice, and a
            /// draw binds whichever slice was current when it was recorded. That is what makes the
            /// per-draw contract on <c>UpdateConstantBuffer</c> hold on a deferred API.
            /// </remarks>
            public VkBuffer DynamicBuffer;
            public ulong DynamicOffset;
            public int DynamicSize;
            public int DynamicAlignment = 16;
        }

        internal sealed class TextureResource
        {
            public VkImage Image;
            public DeviceMemory Memory;
            public ImageView View;
            public int Width, Height, MipLevels, ArrayLayers = 1;
            public GpuFormat Format;
            public bool IsDepth;
            public bool OwnsImage = true;
            public ImageLayout Layout = ImageLayout.Undefined;
        }

        internal sealed class RenderTargetResource
        {
            public GpuTextureHandle[] Colors = Array.Empty<GpuTextureHandle>();
            public GpuTextureHandle Depth = GpuTextureHandle.Invalid;
            public RenderPass RenderPass;
            public Framebuffer Framebuffer;
            /// <summary>
            /// Colour-only signature used when a pass sets <c>HasDepth = false</c> so it can
            /// sample this target's depth without attaching it (NEXT-138).
            /// </summary>
            public RenderPass ColorOnlyRenderPass;
            public Framebuffer ColorOnlyFramebuffer;
            public int Width, Height;
        }

        internal sealed class ProgramResource
        {
            public ShaderModule VertexShader;
            public ShaderModule PixelShader;
            public string VertexEntry;
            public string PixelEntry;
            public VulkanDescriptors.ProgramBindings Bindings;
            public PipelineLayout PipelineLayout;
        }

        private readonly Dictionary<int, BufferResource> _buffers = new();
        private readonly Dictionary<int, TextureResource> _textures = new();
        private readonly Dictionary<int, VkSampler> _samplers = new();
        private readonly Dictionary<int, RenderTargetResource> _renderTargets = new();
        private readonly Dictionary<int, ProgramResource> _programs = new();
        private readonly Dictionary<int, GpuVertexLayoutDesc> _vertexLayouts = new();

        // ── Fallbacks ───────────────────────────────────────────────────────────

        /// <summary>
        /// Resources bound when the renderer left a declared slot empty.
        /// </summary>
        /// <remarks>
        /// Every binding a shader declares must be written before a draw. Rather than depend on the
        /// optional <c>descriptorBindingPartiallyBound</c> feature — whose absence is what made the
        /// previous backend fail — an unbound slot gets a harmless stand-in: opaque white multiplies
        /// through rather than blackening a result.
        /// </remarks>
        private void CreateFallbacks()
        {
            Span<byte> white = stackalloc byte[4] { 255, 255, 255, 255 };
            _fallbackTexture = CreateTexture(new GpuTextureDesc
            {
                Width = 1, Height = 1, MipLevels = 1, ArrayLayers = 1,
                Format = GpuFormat.R8G8B8A8UNorm,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Vulkan.FallbackTexture",
            }, white);

            Span<byte> whiteArray = stackalloc byte[8] { 255, 255, 255, 255, 255, 255, 255, 255 };
            _fallbackTextureArray = CreateTexture(new GpuTextureDesc
            {
                Width = 1, Height = 1, MipLevels = 1, ArrayLayers = 2,
                Format = GpuFormat.R8G8B8A8UNorm,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Vulkan.FallbackTextureArray",
            }, whiteArray);

            _fallbackDepthTexture = CreateTexture(new GpuTextureDesc
            {
                Width = 1, Height = 1, MipLevels = 1, ArrayLayers = 1,
                Format = GpuFormat.D32Float,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Vulkan.FallbackDepthTexture",
            }, ReadOnlySpan<byte>.Empty);
            if (_textures.TryGetValue(_fallbackDepthTexture.Id, out TextureResource depthFallback))
            {
                TransitionImmediate(depthFallback, ImageLayout.ShaderReadOnlyOptimal);
            }

            _fallbackSampler = CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Linear,
                AddressU = GpuAddressMode.Clamp,
                AddressV = GpuAddressMode.Clamp,
                AddressW = GpuAddressMode.Clamp,
                MaxAnisotropy = 1,
                CompareOp = GpuCompare.Never,
                DebugName = "Vulkan.FallbackSampler",
            });

            _fallbackBuffer = CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = 256,
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer | GpuBindFlags.ShaderResource,
                DebugName = "Vulkan.FallbackBuffer",
            }, ReadOnlySpan<byte>.Empty);
        }

        private BufferResource ResolveBuffer(GpuBufferHandle handle) =>
            _buffers.TryGetValue(handle.Id, out BufferResource resource)
                ? resource
                : _buffers[_fallbackBuffer.Id];

        private TextureResource ResolveTexture(
            GpuTextureHandle handle, bool textureArray = false, bool depthImage = false)
        {
            if (_textures.TryGetValue(handle.Id, out TextureResource resource) && resource.View.Handle != 0)
            {
                if (textureArray && resource.ArrayLayers <= 1)
                    return FallbackArrayOrColor();
                if (depthImage && !resource.IsDepth)
                    return FallbackDepthOrColor();
                return resource;
            }

            if (textureArray)
                return FallbackArrayOrColor();
            if (depthImage)
                return FallbackDepthOrColor();
            return _textures[_fallbackTexture.Id];
        }

        private TextureResource FallbackArrayOrColor() =>
            _fallbackTextureArray.IsValid && _textures.TryGetValue(_fallbackTextureArray.Id, out TextureResource arrayRes)
                ? arrayRes
                : _textures[_fallbackTexture.Id];

        private TextureResource FallbackDepthOrColor() =>
            _fallbackDepthTexture.IsValid && _textures.TryGetValue(_fallbackDepthTexture.Id, out TextureResource depthRes)
                ? depthRes
                : _textures[_fallbackTexture.Id];

        private VkSampler ResolveSampler(GpuSamplerHandle handle) =>
            _samplers.TryGetValue(handle.Id, out VkSampler sampler)
                ? sampler
                : _samplers[_fallbackSampler.Id];

        // ── Buffers ─────────────────────────────────────────────────────────────

        public GpuBufferHandle CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
        {
            bool dynamic = desc.Usage == GpuBufferUsage.Dynamic;
            var resource = new BufferResource
            {
                SizeBytes = desc.SizeBytes,
                Usage = desc.Usage,
                BindFlags = desc.BindFlags,
                StructureStride = desc.StructureStride,
                DynamicAlignment = _uploadRing.AlignmentFor(desc.BindFlags, desc.StructureStride),
                DebugName = desc.DebugName,
            };

            // Every buffer gets a dedicated backing allocation so its initial contents (and any
            // contents when not dynamically written in a given frame) are preserved permanently.
            BufferResource allocated = AllocateBuffer(
                desc.SizeBytes,
                VulkanGpuFormats.ToVulkanUsage(desc.Usage, desc.BindFlags),
                hostVisible: !initialData.IsEmpty || desc.Usage == GpuBufferUsage.Staging || dynamic);
            resource.Buffer = allocated.Buffer;
            resource.Memory = allocated.Memory;
            resource.Mapped = allocated.Mapped;
            resource.HostVisible = allocated.HostVisible;

            if (!initialData.IsEmpty)
            {
                Write(resource, initialData, 0);
            }

            int id = _nextId++;
            _buffers[id] = resource;
            return new GpuBufferHandle(id);
        }

        private BufferResource AllocateBuffer(int size, BufferUsageFlags usage, bool hostVisible)
        {
            var info = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            VkBuffer buffer;
            VulkanRuntime.Check(
                _runtime.Api.CreateBuffer(_runtime.Device, &info, null, &buffer), "creating a buffer");

            _runtime.Api.GetBufferMemoryRequirements(_runtime.Device, buffer, out MemoryRequirements requirements);

            MemoryPropertyFlags properties = hostVisible
                ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
                : MemoryPropertyFlags.DeviceLocalBit;

            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = _runtime.FindMemoryType(requirements.MemoryTypeBits, properties),
            };

            DeviceMemory memory;
            VulkanRuntime.Check(
                _runtime.Api.AllocateMemory(_runtime.Device, &allocate, null, &memory), "allocating buffer memory");
            VulkanRuntime.Check(
                _runtime.Api.BindBufferMemory(_runtime.Device, buffer, memory, 0), "binding buffer memory");

            var resource = new BufferResource
            {
                Buffer = buffer, Memory = memory, SizeBytes = size, HostVisible = hostVisible,
            };

            if (hostVisible)
            {
                void* mapped = null;
                VulkanRuntime.Check(
                    // Readback invalidates the whole mapping. Include allocation padding so its
                    // end satisfies nonCoherentAtomSize even for an odd-sized viewport.
                    _runtime.Api.MapMemory(_runtime.Device, memory, 0, Vk.WholeSize, 0, &mapped), "mapping a buffer");
                resource.Mapped = mapped;
            }

            return resource;
        }

        private void Write(BufferResource resource, ReadOnlySpan<byte> data, int offset)
        {
            if (resource.Mapped == null || data.IsEmpty) return;

            int length = Math.Min(data.Length, resource.SizeBytes - offset);
            if (length <= 0) return;

            fixed (byte* source = data)
            {
                System.Buffer.MemoryCopy(source, (byte*)resource.Mapped + offset, length, length);
            }
        }

        /// <summary>Takes a fresh ring slice for a dynamic buffer, so earlier draws keep their data.</summary>
        private byte* AllocateDynamicSlice(BufferResource resource, bool clear)
        {
            void* destination = _uploadRing.Allocate(
                resource.SizeBytes, resource.DynamicAlignment,
                out VkBuffer buffer, out ulong offset);

            resource.DynamicBuffer = buffer;
            resource.DynamicOffset = offset;
            resource.DynamicSize = resource.SizeBytes;

            if (clear) new Span<byte>(destination, resource.SizeBytes).Clear();
            return (byte*)destination;
        }

        public void UpdateBuffer(GpuBufferHandle handle, ReadOnlySpan<byte> data, int byteOffset = 0)
        {
            if (!_buffers.TryGetValue(handle.Id, out BufferResource resource)) return;

            if (resource.Usage != GpuBufferUsage.Dynamic)
            {
                Write(resource, data, byteOffset);
                return;
            }

            // A dynamic write starts a new slice. Anything outside the written range would otherwise
            // be whatever a previous frame left in that ring memory, so it is zeroed.
            byte* destination = AllocateDynamicSlice(resource, clear: byteOffset > 0 || data.Length < resource.SizeBytes);
            int length = Math.Min(data.Length, resource.SizeBytes - byteOffset);
            if (length > 0) data[..length].CopyTo(new Span<byte>(destination + byteOffset, length));
        }

        public void UpdateConstantBuffer<T>(GpuBufferHandle handle, in T data) where T : unmanaged
        {
            fixed (T* value = &data)
            {
                UpdateBuffer(handle, new ReadOnlySpan<byte>(value, Unsafe.SizeOf<T>()));
            }
        }

        public bool TryMapDiscard(GpuBufferHandle handle, out Span<byte> span, int byteCount = 0)
        {
            span = default;
            if (!_buffers.TryGetValue(handle.Id, out BufferResource resource)) return false;

            if (resource.Usage != GpuBufferUsage.Dynamic)
            {
                if (resource.Mapped == null) return false;
                span = new Span<byte>(resource.Mapped, resource.SizeBytes);
                if (byteCount > 0 && byteCount < span.Length)
                    span = span.Slice(0, byteCount);
                return true;
            }

            // Discard semantics: a fresh slice, zeroed so a partially filled span leaves no remnant
            // of an earlier frame. Draws already recorded keep pointing at the slice they were given.
            span = new Span<byte>(AllocateDynamicSlice(resource, clear: true), resource.SizeBytes);
            if (byteCount > 0 && byteCount < span.Length)
                span = span.Slice(0, byteCount);
            return true;
        }

        public void Unmap(GpuBufferHandle handle)
        {
            // Ring memory is coherent and stays mapped for the life of the device; the caller wrote
            // straight into the slice, so there is nothing to copy or undo.
            _ = handle;
        }

        public void ReleaseBuffer(GpuBufferHandle handle)
        {
            if (!_buffers.Remove(handle.Id, out BufferResource resource)) return;

            // A dynamic buffer owns no allocation — its storage is ring memory the device outlives.
            if (resource.Buffer.Handle == 0) return;

            VkBuffer buffer = resource.Buffer;
            DeviceMemory memory = resource.Memory;
            bool mapped = resource.Mapped != null;

            _frameRing.Defer(() =>
            {
                if (mapped) _runtime.Api.UnmapMemory(_runtime.Device, memory);
                _runtime.Api.DestroyBuffer(_runtime.Device, buffer, null);
                _runtime.Api.FreeMemory(_runtime.Device, memory, null);
            });
        }

        // ── Textures ────────────────────────────────────────────────────────────

        public GpuTextureHandle CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData)
        {
            VkFormat format = VulkanGpuFormats.ToVulkan(desc.Format);
            bool isDepth = VulkanGpuFormats.IsDepth(desc.Format);
            int mips = Math.Max(1, desc.MipLevels);

            ImageUsageFlags usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit;
            usage |= isDepth ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.SampledBit;
            if ((desc.BindFlags & GpuBindFlags.RenderTarget) != 0) usage |= ImageUsageFlags.ColorAttachmentBit;
            if (!isDepth && (desc.BindFlags & GpuBindFlags.ShaderResource) != 0) usage |= ImageUsageFlags.SampledBit;
            if (isDepth && (desc.BindFlags & GpuBindFlags.ShaderResource) != 0) usage |= ImageUsageFlags.SampledBit;

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D((uint)Math.Max(1, desc.Width), (uint)Math.Max(1, desc.Height), 1),
                MipLevels = (uint)mips,
                ArrayLayers = (uint)Math.Max(1, desc.ArrayLayers),
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };

            VkImage image;
            VulkanRuntime.Check(_runtime.Api.CreateImage(_runtime.Device, &imageInfo, null, &image), "creating an image");

            _runtime.Api.GetImageMemoryRequirements(_runtime.Device, image, out MemoryRequirements requirements);
            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = _runtime.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };

            DeviceMemory memory;
            VulkanRuntime.Check(
                _runtime.Api.AllocateMemory(_runtime.Device, &allocate, null, &memory), "allocating image memory");
            VulkanRuntime.Check(
                _runtime.Api.BindImageMemory(_runtime.Device, image, memory, 0), "binding image memory");

            var resource = new TextureResource
            {
                Image = image, Memory = memory,
                Width = desc.Width, Height = desc.Height, MipLevels = mips,
                ArrayLayers = Math.Max(1, desc.ArrayLayers),
                Format = desc.Format, IsDepth = isDepth,
            };

            resource.View = CreateView(image, format, isDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, mips, Math.Max(1, desc.ArrayLayers));

            int id = _nextId++;
            _textures[id] = resource;

            if (!initialData.IsEmpty)
            {
                UploadMipChain(resource, initialData);
            }
            else if (!isDepth)
            {
                // Left in a readable layout so an unwritten texture bound to a shader is legal.
                TransitionImmediate(resource, ImageLayout.ShaderReadOnlyOptimal);
            }

            return new GpuTextureHandle(id);
        }

        private ImageView CreateView(VkImage image, VkFormat format, ImageAspectFlags aspect, int mips, int arrayLayers = 1)
        {
            var info = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = arrayLayers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, (uint)mips, 0, (uint)Math.Max(1, arrayLayers)),
            };

            ImageView view;
            VulkanRuntime.Check(
                _runtime.Api.CreateImageView(_runtime.Device, &info, null, &view), "creating an image view");
            return view;
        }

        /// <summary>Uploads Genesis's tightly packed mip payload through a staging buffer.</summary>
        private void UploadMipChain(TextureResource texture, ReadOnlySpan<byte> data)
        {
            BufferResource staging = AllocateBuffer(data.Length, BufferUsageFlags.TransferSrcBit, hostVisible: true);
            Write(staging, data, 0);

            CommandBuffer cmd = BeginImmediate();
            TransitionInCommand(cmd, texture, ImageLayout.TransferDstOptimal);

            var regions = new List<BufferImageCopy>(texture.MipLevels);
            int offset = 0;
            for (int level = 0; level < texture.MipLevels; level++)
            {
                int width = Math.Max(1, texture.Width >> level);
                int height = Math.Max(1, texture.Height >> level);
                int bytes = VulkanGpuFormats.RowBytes(texture.Format, width)
                    * VulkanGpuFormats.RowCount(texture.Format, height);
                if (offset + bytes > data.Length) break;

                regions.Add(new BufferImageCopy
                {
                    BufferOffset = (ulong)offset,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D((uint)width, (uint)height, 1),
                });

                offset += bytes;
            }

            if (regions.Count > 0)
            {
                BufferImageCopy[] array = regions.ToArray();
                fixed (BufferImageCopy* pointer = array)
                {
                    _runtime.Api.CmdCopyBufferToImage(
                        cmd, staging.Buffer, texture.Image, ImageLayout.TransferDstOptimal,
                        (uint)array.Length, pointer);
                }
            }

            TransitionInCommand(cmd, texture, ImageLayout.ShaderReadOnlyOptimal);

            // Flushed for the same reason: the staging buffer is freed on the next line.
            EndImmediate(cmd, flush: true);

            _runtime.Api.UnmapMemory(_runtime.Device, staging.Memory);
            _runtime.Api.DestroyBuffer(_runtime.Device, staging.Buffer, null);
            _runtime.Api.FreeMemory(_runtime.Device, staging.Memory, null);
        }

        public void UpdateTexture(
            GpuTextureHandle handle, int x, int y, int width, int height,
            ReadOnlySpan<byte> data, int arraySlice = 0)
        {
            if (!_textures.TryGetValue(handle.Id, out TextureResource texture) || data.IsEmpty) return;

            BufferResource staging = AllocateBuffer(data.Length, BufferUsageFlags.TransferSrcBit, hostVisible: true);
            Write(staging, data, 0);

            CommandBuffer cmd = BeginImmediate();
            TransitionInCommand(cmd, texture, ImageLayout.TransferDstOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)arraySlice, 1),
                ImageOffset = new Offset3D(x, y, 0),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };

            _runtime.Api.CmdCopyBufferToImage(
                cmd, staging.Buffer, texture.Image, ImageLayout.TransferDstOptimal, 1, &region);
            TransitionInCommand(cmd, texture, ImageLayout.ShaderReadOnlyOptimal);

            // Flushed: the staging buffer is destroyed immediately below, and destroying a resource
            // a queued command still reads is a device loss.
            EndImmediate(cmd, flush: true);

            _runtime.Api.UnmapMemory(_runtime.Device, staging.Memory);
            _runtime.Api.DestroyBuffer(_runtime.Device, staging.Buffer, null);
            _runtime.Api.FreeMemory(_runtime.Device, staging.Memory, null);
        }

        public void ReleaseTexture(GpuTextureHandle handle)
        {
            if (!_textures.Remove(handle.Id, out TextureResource texture)) return;

            ImageView view = texture.View;
            VkImage image = texture.Image;
            DeviceMemory memory = texture.Memory;
            bool owns = texture.OwnsImage;

            _frameRing.Defer(() =>
            {
                if (view.Handle != 0) _runtime.Api.DestroyImageView(_runtime.Device, view, null);
                if (owns && image.Handle != 0) _runtime.Api.DestroyImage(_runtime.Device, image, null);
                if (owns && memory.Handle != 0) _runtime.Api.FreeMemory(_runtime.Device, memory, null);
            });
        }

        // ── Samplers ────────────────────────────────────────────────────────────

        public GpuSamplerHandle CreateSampler(in GpuSamplerDesc desc)
        {
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = VulkanGpuFormats.ToFilter(desc.Filter),
                MinFilter = VulkanGpuFormats.ToFilter(desc.Filter),
                MipmapMode = VulkanGpuFormats.ToMipmapMode(desc.Filter),
                AddressModeU = VulkanGpuFormats.ToAddress(desc.AddressU),
                AddressModeV = VulkanGpuFormats.ToAddress(desc.AddressV),
                AddressModeW = VulkanGpuFormats.ToAddress(desc.AddressW),
                AnisotropyEnable = desc.Filter == GpuFilter.Anisotropic && desc.MaxAnisotropy > 1,
                MaxAnisotropy = Math.Max(1, desc.MaxAnisotropy),

                // Anything but Never makes this HLSL's SamplerComparisonState — the shadow cascades.
                CompareEnable = desc.CompareOp != GpuCompare.Never,
                CompareOp = VulkanGpuFormats.ToCompare(desc.CompareOp),
                MinLod = 0,
                MaxLod = Vk.LodClampNone,
                BorderColor = BorderColor.FloatOpaqueBlack,
                UnnormalizedCoordinates = false,
            };

            VkSampler sampler;
            VulkanRuntime.Check(
                _runtime.Api.CreateSampler(_runtime.Device, &info, null, &sampler), "creating a sampler");

            int id = _nextId++;
            _samplers[id] = sampler;
            return new GpuSamplerHandle(id);
        }

        public void ReleaseSampler(GpuSamplerHandle handle)
        {
            if (!_samplers.Remove(handle.Id, out VkSampler sampler)) return;
            _frameRing.Defer(() => _runtime.Api.DestroySampler(_runtime.Device, sampler, null));
        }

        // ── Render targets ──────────────────────────────────────────────────────

        public GpuRenderTargetHandle CreateRenderTarget(in GpuRenderTargetDesc desc)
        {
            GpuFormat[] colorFormats = desc.ColorFormats ?? Array.Empty<GpuFormat>();
            var target = new RenderTargetResource { Width = desc.Width, Height = desc.Height };
            var colors = new GpuTextureHandle[colorFormats.Length];

            for (int i = 0; i < colorFormats.Length; i++)
            {
                colors[i] = CreateTexture(new GpuTextureDesc
                {
                    Width = desc.Width, Height = desc.Height, MipLevels = 1, ArrayLayers = 1,
                    Format = colorFormats[i],
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.RenderTarget | GpuBindFlags.ShaderResource,
                    DebugName = desc.DebugName,
                }, ReadOnlySpan<byte>.Empty);
            }

            target.Colors = colors;

            if (desc.DepthFormat != GpuFormat.Unknown)
            {
                target.Depth = CreateTexture(new GpuTextureDesc
                {
                    Width = desc.Width, Height = desc.Height, MipLevels = 1, ArrayLayers = 1,
                    Format = desc.DepthFormat,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = desc.DepthSampleable ? GpuBindFlags.ShaderResource : GpuBindFlags.None,
                    DebugName = desc.DebugName,
                }, ReadOnlySpan<byte>.Empty);
            }

            target.RenderPass = CreateOffscreenRenderPass(colorFormats, desc.DepthFormat);
            target.Framebuffer = CreateOffscreenFramebuffer(
                target, target.RenderPass, includeDepth: desc.DepthFormat != GpuFormat.Unknown);

            if (colorFormats.Length > 0)
            {
                target.ColorOnlyRenderPass = CreateOffscreenRenderPass(colorFormats, GpuFormat.Unknown);
                target.ColorOnlyFramebuffer = CreateOffscreenFramebuffer(
                    target, target.ColorOnlyRenderPass, includeDepth: false);
            }

            int id = _nextId++;
            _renderTargets[id] = target;
            return new GpuRenderTargetHandle(id);
        }

        private RenderPass CreateOffscreenRenderPass(GpuFormat[] colorFormats, GpuFormat depthFormat)
        {
            bool hasDepth = depthFormat != GpuFormat.Unknown;
            int total = colorFormats.Length + (hasDepth ? 1 : 0);

            var attachments = stackalloc AttachmentDescription[Math.Max(1, total)];
            var references = stackalloc AttachmentReference[Math.Max(1, colorFormats.Length)];

            for (int i = 0; i < colorFormats.Length; i++)
            {
                attachments[i] = new AttachmentDescription
                {
                    Format = VulkanGpuFormats.ToVulkan(colorFormats[i]),
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.ColorAttachmentOptimal,
                    FinalLayout = ImageLayout.ColorAttachmentOptimal,
                };
                references[i] = new AttachmentReference((uint)i, ImageLayout.ColorAttachmentOptimal);
            }

            AttachmentReference depthReference = default;
            if (hasDepth)
            {
                attachments[colorFormats.Length] = new AttachmentDescription
                {
                    Format = VulkanGpuFormats.ToVulkan(depthFormat),
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                depthReference = new AttachmentReference(
                    (uint)colorFormats.Length, ImageLayout.DepthStencilAttachmentOptimal);
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)colorFormats.Length,
                PColorAttachments = colorFormats.Length > 0 ? references : null,
                PDepthStencilAttachment = hasDepth ? &depthReference : null,
            };

            var dependencies = stackalloc SubpassDependency[2];
            dependencies[0] = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            };
            dependencies[1] = new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };

            var info = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)total,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 2,
                PDependencies = dependencies,
            };

            RenderPass renderPass;
            VulkanRuntime.Check(
                _runtime.Api.CreateRenderPass(_runtime.Device, &info, null, &renderPass),
                "creating an offscreen render pass");
            return renderPass;
        }

        private Framebuffer CreateOffscreenFramebuffer(
            RenderTargetResource target, RenderPass renderPass, bool includeDepth)
        {
            int total = target.Colors.Length + (includeDepth && target.Depth.IsValid ? 1 : 0);
            var views = stackalloc ImageView[Math.Max(1, total)];

            for (int i = 0; i < target.Colors.Length; i++)
            {
                views[i] = _textures[target.Colors[i].Id].View;
            }

            if (includeDepth && target.Depth.IsValid)
            {
                views[target.Colors.Length] = _textures[target.Depth.Id].View;
            }

            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)total,
                PAttachments = views,
                Width = (uint)target.Width,
                Height = (uint)target.Height,
                Layers = 1,
            };

            Framebuffer framebuffer;
            VulkanRuntime.Check(
                _runtime.Api.CreateFramebuffer(_runtime.Device, &info, null, &framebuffer),
                "creating an offscreen framebuffer");
            return framebuffer;
        }

        /// <summary>Moves a target's attachments into or out of the layouts its render pass declares.</summary>
        private void TransitionRenderTarget(RenderTargetResource target, bool toAttachment, bool includeDepth)
        {
            CommandBuffer cmd = _frameRing.CurrentCommandBuffer;
            foreach (GpuTextureHandle handle in target.Colors)
            {
                if (_textures.TryGetValue(handle.Id, out TextureResource colour))
                {
                    TransitionInCommand(
                        cmd, colour,
                        toAttachment ? ImageLayout.ColorAttachmentOptimal : ImageLayout.ShaderReadOnlyOptimal);
                }
            }

            if (includeDepth && target.Depth.IsValid && _textures.TryGetValue(target.Depth.Id, out TextureResource depth))
            {
                TransitionInCommand(
                    cmd, depth,
                    toAttachment ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.ShaderReadOnlyOptimal);
            }
        }

        public GpuTextureHandle GetRenderTargetTexture(GpuRenderTargetHandle handle, int attachment = 0) =>
            _renderTargets.TryGetValue(handle.Id, out RenderTargetResource target)
            && (uint)attachment < target.Colors.Length
                ? target.Colors[attachment]
                : GpuTextureHandle.Invalid;

        public GpuTextureHandle GetRenderTargetDepthTexture(GpuRenderTargetHandle handle) =>
            _renderTargets.TryGetValue(handle.Id, out RenderTargetResource target)
                ? target.Depth
                : GpuTextureHandle.Invalid;

        public void ReleaseRenderTarget(GpuRenderTargetHandle handle)
        {
            if (!_renderTargets.Remove(handle.Id, out RenderTargetResource target)) return;

            _pipelines.InvalidateForRenderPass(target.RenderPass);
            if (target.ColorOnlyRenderPass.Handle != 0)
                _pipelines.InvalidateForRenderPass(target.ColorOnlyRenderPass);

            Framebuffer framebuffer = target.Framebuffer;
            RenderPass renderPass = target.RenderPass;
            Framebuffer colorOnlyFramebuffer = target.ColorOnlyFramebuffer;
            RenderPass colorOnlyRenderPass = target.ColorOnlyRenderPass;
            _frameRing.Defer(() =>
            {
                _runtime.Api.DestroyFramebuffer(_runtime.Device, framebuffer, null);
                _runtime.Api.DestroyRenderPass(_runtime.Device, renderPass, null);
                if (colorOnlyFramebuffer.Handle != 0)
                    _runtime.Api.DestroyFramebuffer(_runtime.Device, colorOnlyFramebuffer, null);
                if (colorOnlyRenderPass.Handle != 0)
                    _runtime.Api.DestroyRenderPass(_runtime.Device, colorOnlyRenderPass, null);
            });

            foreach (GpuTextureHandle colour in target.Colors) ReleaseTexture(colour);
            if (target.Depth.IsValid) ReleaseTexture(target.Depth);
        }

        // ── Shader programs and vertex layouts ──────────────────────────────────

        public GpuShaderProgramHandle CreateShaderProgram(in GpuShaderProgramDesc desc)
        {
            if (desc.BinaryFormat != GpuShaderBinaryFormat.SpirV)
            {
                throw new InvalidOperationException(
                    $"The Vulkan backend requires SPIR-V shaders; got {desc.BinaryFormat}.");
            }

            var declared = new List<SpirVBinding>();
            if (desc.VertexShader is { Length: > 0 }) declared.AddRange(SpirVReflection.ReadBindings(desc.VertexShader));
            if (desc.PixelShader is { Length: > 0 }) declared.AddRange(SpirVReflection.ReadBindings(desc.PixelShader));

            VulkanDescriptors.ProgramBindings bindings = _descriptors.CreateLayout(declared);

            DescriptorSetLayout layout = bindings.Layout;
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &layout,
                PushConstantRangeCount = 0,
            };

            PipelineLayout pipelineLayout;
            VulkanRuntime.Check(
                _runtime.Api.CreatePipelineLayout(_runtime.Device, &layoutInfo, null, &pipelineLayout),
                "creating a pipeline layout");

            int id = _nextId++;
            _programs[id] = new ProgramResource
            {
                VertexShader = CreateShaderModule(desc.VertexShader),
                PixelShader = CreateShaderModule(desc.PixelShader),

                // DXC keeps the HLSL entry name rather than renaming to "main", so it is read back
                // from the module instead of assumed.
                VertexEntry = desc.VertexShader is { Length: > 0 }
                    ? SpirVReflection.ReadEntryPoint(desc.VertexShader, SpirVReflection.ExecutionModelVertex)
                    : null,
                PixelEntry = desc.PixelShader is { Length: > 0 }
                    ? SpirVReflection.ReadEntryPoint(desc.PixelShader, SpirVReflection.ExecutionModelFragment)
                    : null,
                Bindings = bindings,
                PipelineLayout = pipelineLayout,
            };

            return new GpuShaderProgramHandle(id);
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            if (code == null || code.Length == 0) return default;

            fixed (byte* pointer = code)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)pointer,
                };

                ShaderModule module;
                VulkanRuntime.Check(
                    _runtime.Api.CreateShaderModule(_runtime.Device, &info, null, &module),
                    "creating a shader module");
                return module;
            }
        }

        public void ReleaseShaderProgram(GpuShaderProgramHandle handle)
        {
            if (!_programs.Remove(handle.Id, out ProgramResource program)) return;

            // Live shader edits retire programs while earlier frames can still reference their
            // layouts. Use the same fence-delayed lifetime as uploaded mesh buffers.
            _frameRing.Defer(() =>
            {
                _runtime.Api.DestroyPipelineLayout(_runtime.Device, program.PipelineLayout, null);
                _descriptors.DestroyLayout(program.Bindings.Layout);
                if (program.PixelShader.Handle != 0) _runtime.Api.DestroyShaderModule(_runtime.Device, program.PixelShader, null);
                if (program.VertexShader.Handle != 0) _runtime.Api.DestroyShaderModule(_runtime.Device, program.VertexShader, null);
            });
        }

        public GpuVertexLayoutHandle CreateVertexLayout(in GpuVertexLayoutDesc desc, GpuShaderProgramHandle program)
        {
            int id = _nextId++;
            _vertexLayouts[id] = desc;
            return new GpuVertexLayoutHandle(id);
        }

        public void ReleaseVertexLayout(GpuVertexLayoutHandle handle) => _vertexLayouts.Remove(handle.Id);

        // ── Timestamps ──────────────────────────────────────────────────────────

        public GpuQueryHandle BeginTimestampScope() => GpuQueryHandle.Invalid;

        public void EndTimestampScope(GpuQueryHandle handle)
        {
        }

        public bool TryResolveTimestamp(GpuQueryHandle handle, out double milliseconds)
        {
            // Reported unsupported through Capabilities rather than returning an invented number,
            // so the profiler shows nothing instead of showing something wrong.
            milliseconds = 0;
            return false;
        }

        // ── Readback ────────────────────────────────────────────────────────────

        public bool TryReadTexture(GpuTextureHandle handle, out int width, out int height, out byte[] bgra)
        {
            width = 0;
            height = 0;
            bgra = null;

            if (!_textures.TryGetValue(handle.Id, out TextureResource texture) || texture.IsDepth) return false;

            _runtime.Api.QueueWaitIdle(_runtime.GraphicsQueue);

            width = texture.Width;
            height = texture.Height;
            int bytes = width * height * 4;
            bgra = new byte[bytes];

            BufferResource staging = AllocateBuffer(bytes, BufferUsageFlags.TransferDstBit, hostVisible: true);

            ImageLayout original = texture.Layout;
            CommandBuffer cmd = BeginImmediate();
            TransitionInCommand(cmd, texture, ImageLayout.TransferSrcOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };

            _runtime.Api.CmdCopyImageToBuffer(
                cmd, texture.Image, ImageLayout.TransferSrcOptimal, staging.Buffer, 1, &region);
            ImageLayout restoreLayout = original == ImageLayout.Undefined ? ImageLayout.ShaderReadOnlyOptimal : original;
            TransitionInCommand(cmd, texture, restoreLayout);

            // Flushed: the bytes are read immediately below.
            EndImmediate(cmd, flush: true);

            var memRange = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = staging.Memory,
                Offset = 0,
                Size = Vk.WholeSize,
            };
            _runtime.Api.InvalidateMappedMemoryRanges(_runtime.Device, 1, &memRange);

            if (_activeSwapChain != null && _activeSwapChain.HasAcquiredImage
                && _activeSwapChain.Inner.AcquiredImage.Handle == texture.Image.Handle)
            {
                _activeSwapChain.Inner.SetLayout(_activeSwapChain.Inner.AcquiredIndex, texture.Layout);
            }

            new ReadOnlySpan<byte>(staging.Mapped, bytes).CopyTo(bgra);

            // Genesis's contract is BGRA; an RGBA surface needs the channels swapped on the way out.
            if (texture.Format is GpuFormat.R8G8B8A8UNorm or GpuFormat.R8G8B8A8UNormSrgb)
            {
                for (int i = 0; i < bytes; i += 4)
                {
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                }
            }

            _runtime.Api.UnmapMemory(_runtime.Device, staging.Memory);
            _runtime.Api.DestroyBuffer(_runtime.Device, staging.Buffer, null);
            _runtime.Api.FreeMemory(_runtime.Device, staging.Memory, null);
            return true;
        }

        // ── Immediate submission and layout transitions ─────────────────────────

        /// <summary>
        /// Opens a one-shot command buffer for uploads and readback.
        /// </summary>
        /// <remarks>
        /// Uses the frame ring's buffer when a frame is open — reusing it keeps ordering with the
        /// draws around it — and otherwise submits standalone.
        /// </remarks>
        /// <summary>
        /// Opens the dedicated upload command buffer, leaving the frame's completely alone.
        /// </summary>
        /// <remarks>
        /// This deliberately does not touch the frame or its render pass. Sharing the frame's
        /// command buffer meant an upload had to flush it, which ended the pass mid-frame and
        /// dropped every draw recorded afterwards — the world disappeared and the image flickered
        /// with whatever streaming happened to do that frame.
        /// </remarks>
        private CommandBuffer BeginImmediate() => _frameRing.BeginImmediateCommands();

        /// <summary>
        /// Submits the upload and waits, so the caller may free its staging buffer or read the bytes.
        /// </summary>
        /// <param name="flush">
        /// Retained for call-site clarity about why a wait is needed; the dedicated buffer always
        /// submits, because nothing else will carry its work.
        /// </param>
        private void EndImmediate(CommandBuffer cmd, bool flush) => _frameRing.SubmitImmediateCommands();

        private void TransitionImmediate(TextureResource texture, ImageLayout layout)
        {
            // A barrier alone has nothing the CPU waits for, so it rides along with the frame.
            CommandBuffer cmd = BeginImmediate();
            TransitionInCommand(cmd, texture, layout);
            EndImmediate(cmd, flush: false);
        }

        private void TransitionInCommand(CommandBuffer cmd, TextureResource texture, ImageLayout layout)
        {
            if (texture.Layout == layout) return;

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = texture.Layout,
                NewLayout = layout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = texture.Image,
                SubresourceRange = new ImageSubresourceRange(
                    texture.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit,
                    0, (uint)texture.MipLevels, 0, (uint)Math.Max(1, texture.ArrayLayers)),
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            _runtime.Api.CmdPipelineBarrier(
                cmd,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                DependencyFlags.None,
                0, null, 0, null, 1, &barrier);

            texture.Layout = layout;
        }

        private void DisposeResources()
        {
            foreach (RenderTargetResource target in _renderTargets.Values)
            {
                _runtime.Api.DestroyFramebuffer(_runtime.Device, target.Framebuffer, null);
                _runtime.Api.DestroyRenderPass(_runtime.Device, target.RenderPass, null);
                if (target.ColorOnlyFramebuffer.Handle != 0)
                    _runtime.Api.DestroyFramebuffer(_runtime.Device, target.ColorOnlyFramebuffer, null);
                if (target.ColorOnlyRenderPass.Handle != 0)
                    _runtime.Api.DestroyRenderPass(_runtime.Device, target.ColorOnlyRenderPass, null);
            }

            foreach (ProgramResource program in _programs.Values)
            {
                _runtime.Api.DestroyPipelineLayout(_runtime.Device, program.PipelineLayout, null);
                if (program.PixelShader.Handle != 0) _runtime.Api.DestroyShaderModule(_runtime.Device, program.PixelShader, null);
                if (program.VertexShader.Handle != 0) _runtime.Api.DestroyShaderModule(_runtime.Device, program.VertexShader, null);
            }

            foreach (TextureResource texture in _textures.Values)
            {
                if (texture.View.Handle != 0) _runtime.Api.DestroyImageView(_runtime.Device, texture.View, null);
                if (texture.OwnsImage && texture.Image.Handle != 0) _runtime.Api.DestroyImage(_runtime.Device, texture.Image, null);
                if (texture.OwnsImage && texture.Memory.Handle != 0) _runtime.Api.FreeMemory(_runtime.Device, texture.Memory, null);
            }

            foreach (VkSampler sampler in _samplers.Values)
            {
                _runtime.Api.DestroySampler(_runtime.Device, sampler, null);
            }

            foreach (BufferResource buffer in _buffers.Values)
            {
                if (buffer.Mapped != null) _runtime.Api.UnmapMemory(_runtime.Device, buffer.Memory);
                _runtime.Api.DestroyBuffer(_runtime.Device, buffer.Buffer, null);
                _runtime.Api.FreeMemory(_runtime.Device, buffer.Memory, null);
            }

            _renderTargets.Clear();
            _programs.Clear();
            _textures.Clear();
            _samplers.Clear();
            _buffers.Clear();
            _vertexLayouts.Clear();
        }
    }
}

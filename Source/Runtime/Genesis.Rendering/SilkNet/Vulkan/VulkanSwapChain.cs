using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkFormat = Silk.NET.Vulkan.Format;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>A viewport's presentable Vulkan surface: swap chain, depth, render pass, framebuffers.</summary>
    /// <remarks>
    /// <para>The colour format is chosen as BGRA where the surface allows it, because Genesis's
    /// readback contract is tightly packed BGRA — matching here means a capture is a straight copy
    /// rather than a per-pixel swizzle.</para>
    ///
    /// <para>Colour attachments load with <see cref="AttachmentLoadOp.Load"/> and clears are issued
    /// inside the pass with <c>vkCmdClearAttachments</c>. A second, colour-only render pass exists
    /// so a draw that samples depth (composite, water) is not also attaching that same image
    /// (NEXT-138). Pipelines are cached per pass, so the two signatures do not share PSO objects.</para>
    /// </remarks>
    internal sealed unsafe class VulkanSwapChain : IDisposable
    {
        private const GpuFormat DepthGpuFormat = GpuFormat.D32Float;

        private readonly VulkanRuntime _runtime;
        private readonly KhrSurface _surfaceApi;
        private readonly KhrSwapchain _swapchainApi;
        private readonly IntPtr _windowHandle;

        private SurfaceKHR _surface;
        private SwapchainKHR _swapchain;
        private VkImage[] _images = Array.Empty<VkImage>();
        private ImageView[] _imageViews = Array.Empty<ImageView>();
        private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();
        private Framebuffer[] _colorOnlyFramebuffers = Array.Empty<Framebuffer>();
        private ImageLayout[] _imageLayouts = Array.Empty<ImageLayout>();

        private VkImage _depthImage;
        private DeviceMemory _depthMemory;
        private ImageView _depthView;

        private RenderPass _renderPass;
        private RenderPass _colorOnlyRenderPass;
        private VkFormat _colorFormat = VkFormat.B8G8R8A8Unorm;
        private VkSemaphore[] _renderFinishedSemaphores = Array.Empty<VkSemaphore>();
        private bool _disposed;

        public VulkanSwapChain(VulkanRuntime runtime, IntPtr windowHandle, int width, int height)
        {
            _runtime = runtime;
            _windowHandle = windowHandle;

            if (!runtime.Api.TryGetInstanceExtension(runtime.Instance, out _surfaceApi))
            {
                throw new InvalidOperationException("VK_KHR_surface is unavailable on this instance.");
            }

            if (!runtime.Api.TryGetDeviceExtension(runtime.Instance, runtime.Device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable on this device.");
            }

            CreateSurface();
            Recreate(width, height);
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool IsReady => !_disposed && _swapchain.Handle != 0 && Width > 0 && Height > 0;

        public RenderPass RenderPass => _renderPass;

        public RenderPass ColorOnlyRenderPass => _colorOnlyRenderPass;

        public VkFormat ColorFormat => _colorFormat;

        public static GpuFormat DepthFormat => DepthGpuFormat;

        public int ImageCount => _images.Length;

        /// <summary>Index acquired this frame, or -1 when nothing is acquired.</summary>
        public int AcquiredIndex { get; private set; } = -1;

        public VkImage AcquiredImage =>
            AcquiredIndex >= 0 && AcquiredIndex < _images.Length ? _images[AcquiredIndex] : default;

        /// <summary>
        /// The depth attachment, which callers must transition into the layout the render pass
        /// declares before the first pass that uses it.
        /// </summary>
        public VkImage DepthImage => _depthImage;

        public Framebuffer AcquiredFramebuffer =>
            AcquiredIndex >= 0 && AcquiredIndex < _framebuffers.Length ? _framebuffers[AcquiredIndex] : default;

        public Framebuffer AcquiredColorOnlyFramebuffer =>
            AcquiredIndex >= 0 && AcquiredIndex < _colorOnlyFramebuffers.Length
                ? _colorOnlyFramebuffers[AcquiredIndex]
                : default;

        public ImageLayout LayoutAt(int index) =>
            index >= 0 && index < _imageLayouts.Length ? _imageLayouts[index] : ImageLayout.Undefined;

        public void SetLayout(int index, ImageLayout layout)
        {
            if (index >= 0 && index < _imageLayouts.Length)
                _imageLayouts[index] = layout;
        }

        public VkSemaphore RenderFinishedSemaphore =>
            AcquiredIndex >= 0 && AcquiredIndex < _renderFinishedSemaphores.Length
                ? _renderFinishedSemaphores[AcquiredIndex]
                : default;

        private void CreateSurface()
        {
            if (!_runtime.Api.TryGetInstanceExtension(_runtime.Instance, out KhrWin32Surface win32))
            {
                throw new InvalidOperationException("VK_KHR_win32_surface is unavailable on this instance.");
            }

            var info = new Win32SurfaceCreateInfoKHR
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                Hinstance = System.Runtime.InteropServices.Marshal.GetHINSTANCE(typeof(VulkanSwapChain).Module),
                Hwnd = _windowHandle,
            };

            SurfaceKHR surface;
            VulkanRuntime.Check(
                win32.CreateWin32Surface(_runtime.Instance, &info, null, &surface), "creating the Win32 surface");
            _surface = surface;

            // A surface the chosen queue family cannot present to would fail later, inside the first
            // present, with nothing pointing back at the cause.
            Bool32 supported = false;
            VulkanRuntime.Check(
                _surfaceApi.GetPhysicalDeviceSurfaceSupport(
                    _runtime.PhysicalDevice, _runtime.GraphicsQueueFamily, _surface, &supported),
                "querying surface presentation support");

            if (!supported)
            {
                throw new InvalidOperationException(
                    "The selected Vulkan queue family cannot present to this window's surface.");
            }
        }

        /// <summary>Builds or rebuilds the swap chain and everything sized with it.</summary>
        public bool Recreate(int width, int height)
        {
            if (_disposed || width <= 0 || height <= 0)
            {
                return false;
            }

            _runtime.WaitIdle();
            DestroySizedResources();

            SurfaceCapabilitiesKHR capabilities;
            VulkanRuntime.Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_runtime.PhysicalDevice, _surface, &capabilities),
                "querying surface capabilities");

            // A minimised window reports a zero extent; there is nothing to build until it returns.
            if (capabilities.CurrentExtent.Width == 0 || capabilities.CurrentExtent.Height == 0)
            {
                Width = 0;
                Height = 0;
                return false;
            }

            Extent2D extent = capabilities.CurrentExtent.Width != uint.MaxValue
                ? capabilities.CurrentExtent
                : new Extent2D
                {
                    Width = Math.Clamp((uint)width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                    Height = Math.Clamp((uint)height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
                };

            Width = (int)extent.Width;
            Height = (int)extent.Height;

            SurfaceFormatKHR format = ChooseFormat();
            _colorFormat = format.Format;

            uint imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            {
                imageCount = capabilities.MaxImageCount;
            }

            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = format.Format,
                ImageColorSpace = format.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,

                // TransferSrc so a frame can be read back for the headless captures.
                ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = ChoosePresentMode(),
                Clipped = true,
                OldSwapchain = default,
            };

            SwapchainKHR swapchain;
            VulkanRuntime.Check(
                _swapchainApi.CreateSwapchain(_runtime.Device, &createInfo, null, &swapchain),
                "creating the swap chain");
            _swapchain = swapchain;

            CreateImageViews();
            CreateDepthResources();
            _renderPass = CreateRenderPass(includeDepth: true);
            _colorOnlyRenderPass = CreateRenderPass(includeDepth: false);
            _framebuffers = CreateFramebuffers(_renderPass, includeDepth: true);
            _colorOnlyFramebuffers = CreateFramebuffers(_colorOnlyRenderPass, includeDepth: false);
            return true;
        }

        private SurfaceFormatKHR ChooseFormat()
        {
            uint count = 0;
            VulkanRuntime.Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_runtime.PhysicalDevice, _surface, &count, null),
                "querying surface formats");

            var formats = new SurfaceFormatKHR[Math.Max(1u, count)];
            fixed (SurfaceFormatKHR* pointer = formats)
            {
                VulkanRuntime.Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_runtime.PhysicalDevice, _surface, &count, pointer),
                    "querying surface formats");
            }

            // BGRA preferred: it matches Genesis's readback contract, so a capture needs no swizzle.
            foreach (SurfaceFormatKHR candidate in formats)
            {
                if (candidate.Format == VkFormat.B8G8R8A8Unorm
                    && candidate.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return candidate;
                }
            }

            return formats[0];
        }

        private PresentModeKHR ChoosePresentMode()
        {
            uint count = 0;
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(_runtime.PhysicalDevice, _surface, &count, null)
                != Result.Success || count == 0)
            {
                return PresentModeKHR.FifoKhr;
            }

            var modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* pointer = modes)
            {
                _surfaceApi.GetPhysicalDeviceSurfacePresentModes(_runtime.PhysicalDevice, _surface, &count, pointer);
            }

            foreach (PresentModeKHR mode in modes)
            {
                if (mode == PresentModeKHR.MailboxKhr)
                {
                    return mode;
                }
            }

            // FIFO is the only mode guaranteed to exist, and is a correct vsync.
            return PresentModeKHR.FifoKhr;
        }

        private void CreateImageViews()
        {
            uint count = 0;
            VulkanRuntime.Check(
                _swapchainApi.GetSwapchainImages(_runtime.Device, _swapchain, &count, null),
                "querying swap chain images");

            _images = new VkImage[count];
            fixed (VkImage* pointer = _images)
            {
                VulkanRuntime.Check(
                    _swapchainApi.GetSwapchainImages(_runtime.Device, _swapchain, &count, pointer),
                    "querying swap chain images");
            }

            _imageViews = new ImageView[count];
            _imageLayouts = new ImageLayout[count];
            _renderFinishedSemaphores = new VkSemaphore[count];

            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            for (int i = 0; i < count; i++)
            {
                _imageLayouts[i] = ImageLayout.Undefined;
                _imageViews[i] = CreateView(_images[i], _colorFormat, ImageAspectFlags.ColorBit);

                VkSemaphore sem;
                VulkanRuntime.Check(
                    _runtime.Api.CreateSemaphore(_runtime.Device, &semaphoreInfo, null, &sem),
                    "creating render finished semaphore");
                _renderFinishedSemaphores[i] = sem;
            }
        }

        private ImageView CreateView(VkImage image, VkFormat format, ImageAspectFlags aspect)
        {
            var info = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            ImageView view;
            VulkanRuntime.Check(
                _runtime.Api.CreateImageView(_runtime.Device, &info, null, &view), "creating an image view");
            return view;
        }

        private void CreateDepthResources()
        {
            VkFormat depthFormat = VulkanGpuFormats.ToVulkan(DepthGpuFormat);

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = depthFormat,
                Extent = new Extent3D((uint)Width, (uint)Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };

            VkImage image;
            VulkanRuntime.Check(
                _runtime.Api.CreateImage(_runtime.Device, &imageInfo, null, &image), "creating the depth image");
            _depthImage = image;

            _runtime.Api.GetImageMemoryRequirements(_runtime.Device, _depthImage, out MemoryRequirements requirements);

            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = _runtime.FindMemoryType(
                    requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };

            DeviceMemory memory;
            VulkanRuntime.Check(
                _runtime.Api.AllocateMemory(_runtime.Device, &allocate, null, &memory), "allocating depth memory");
            _depthMemory = memory;

            VulkanRuntime.Check(
                _runtime.Api.BindImageMemory(_runtime.Device, _depthImage, _depthMemory, 0), "binding depth memory");

            _depthView = CreateView(_depthImage, depthFormat, ImageAspectFlags.DepthBit);
        }

        private RenderPass CreateRenderPass(bool includeDepth)
        {
            int attachmentCount = includeDepth ? 2 : 1;
            var attachments = stackalloc AttachmentDescription[attachmentCount];

            // Load rather than Clear: whether a pass clears is Genesis's per-pass decision, made
            // inside the pass with vkCmdClearAttachments. Baking it in here would need two render
            // passes per depth mode — we already keep a colour-only signature (NEXT-138) so a
            // pass that samples depth cannot also attach it.
            attachments[0] = new AttachmentDescription
            {
                Format = _colorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };

            AttachmentReference colorReference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
            AttachmentReference depthReference = default;
            if (includeDepth)
            {
                attachments[1] = new AttachmentDescription
                {
                    Format = VulkanGpuFormats.ToVulkan(DepthGpuFormat),
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                depthReference = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
                PDepthStencilAttachment = includeDepth ? &depthReference : null,
            };

            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit
                    | PipelineStageFlags.EarlyFragmentTestsBit,
                SrcAccessMask = 0,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit
                    | PipelineStageFlags.EarlyFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit
                    | AccessFlags.DepthStencilAttachmentWriteBit,
            };

            var info = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };

            RenderPass renderPass;
            VulkanRuntime.Check(
                _runtime.Api.CreateRenderPass(_runtime.Device, &info, null, &renderPass),
                includeDepth ? "creating the render pass" : "creating the colour-only render pass");
            return renderPass;
        }

        private Framebuffer[] CreateFramebuffers(RenderPass renderPass, bool includeDepth)
        {
            var framebuffers = new Framebuffer[_imageViews.Length];
            int viewCount = includeDepth ? 2 : 1;
            var views = stackalloc ImageView[viewCount];
            for (int i = 0; i < _imageViews.Length; i++)
            {
                views[0] = _imageViews[i];
                if (includeDepth)
                    views[1] = _depthView;

                var info = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = (uint)viewCount,
                    PAttachments = views,
                    Width = (uint)Width,
                    Height = (uint)Height,
                    Layers = 1,
                };

                Framebuffer framebuffer;
                VulkanRuntime.Check(
                    _runtime.Api.CreateFramebuffer(_runtime.Device, &info, null, &framebuffer),
                    includeDepth ? "creating a framebuffer" : "creating a colour-only framebuffer");
                framebuffers[i] = framebuffer;
            }

            return framebuffers;
        }

        /// <summary>Acquires the next image. False when the swap chain needs rebuilding.</summary>
        public bool TryAcquire(VkSemaphore signal, out bool needsRecreate)
        {
            needsRecreate = false;
            AcquiredIndex = -1;

            if (!IsReady)
            {
                needsRecreate = true;
                return false;
            }

            uint index = 0;
            Result result = _swapchainApi.AcquireNextImage(
                _runtime.Device, _swapchain, ulong.MaxValue, signal, default, &index);

            if (result is Result.ErrorOutOfDateKhr)
            {
                needsRecreate = true;
                return false;
            }

            if (result is not (Result.Success or Result.SuboptimalKhr))
            {
                throw new InvalidOperationException($"vkAcquireNextImageKHR failed: {result}.");
            }

            AcquiredIndex = (int)index;
            return true;
        }

        /// <summary>Presents the acquired image. False when the swap chain needs rebuilding.</summary>
        public bool Present(VkSemaphore wait, out bool needsRecreate)
        {
            needsRecreate = false;
            if (AcquiredIndex < 0)
            {
                return false;
            }

            if (wait.Handle == 0)
            {
                throw new InvalidOperationException(
                    "vkQueuePresentKHR requires the semaphore signalled by this frame's submission.");
            }

            uint index = (uint)AcquiredIndex;
            SwapchainKHR swapchain = _swapchain;
            VkSemaphore waitSemaphore = wait;

            var info = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &waitSemaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &index,
            };

            Result result = _swapchainApi.QueuePresent(_runtime.GraphicsQueue, &info);
            AcquiredIndex = -1;

            if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
            {
                needsRecreate = true;
                return false;
            }

            if (result != Result.Success)
            {
                throw new InvalidOperationException($"vkQueuePresentKHR failed: {result}.");
            }

            return true;
        }

        private void DestroySizedResources()
        {
            Vk api = _runtime.Api;
            Device device = _runtime.Device;
            AcquiredIndex = -1;

            foreach (Framebuffer framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0) api.DestroyFramebuffer(device, framebuffer, null);
            }

            _framebuffers = Array.Empty<Framebuffer>();

            foreach (Framebuffer framebuffer in _colorOnlyFramebuffers)
            {
                if (framebuffer.Handle != 0) api.DestroyFramebuffer(device, framebuffer, null);
            }

            _colorOnlyFramebuffers = Array.Empty<Framebuffer>();

            if (_renderPass.Handle != 0)
            {
                api.DestroyRenderPass(device, _renderPass, null);
                _renderPass = default;
            }

            if (_colorOnlyRenderPass.Handle != 0)
            {
                api.DestroyRenderPass(device, _colorOnlyRenderPass, null);
                _colorOnlyRenderPass = default;
            }

            if (_depthView.Handle != 0)
            {
                api.DestroyImageView(device, _depthView, null);
                _depthView = default;
            }

            if (_depthImage.Handle != 0)
            {
                api.DestroyImage(device, _depthImage, null);
                _depthImage = default;
            }

            if (_depthMemory.Handle != 0)
            {
                api.FreeMemory(device, _depthMemory, null);
                _depthMemory = default;
            }

            foreach (ImageView view in _imageViews)
            {
                if (view.Handle != 0) api.DestroyImageView(device, view, null);
            }

            _imageViews = Array.Empty<ImageView>();

            // The images themselves are owned by the swap chain and must not be destroyed here.
            _images = Array.Empty<VkImage>();

            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(device, _swapchain, null);
                _swapchain = default;
            }

            foreach (VkSemaphore sem in _renderFinishedSemaphores)
            {
                if (sem.Handle != 0) api.DestroySemaphore(device, sem, null);
            }
            _renderFinishedSemaphores = Array.Empty<VkSemaphore>();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _runtime.WaitIdle();
            DestroySizedResources();

            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_runtime.Instance, _surface, null);
                _surface = default;
            }

            _swapchainApi?.Dispose();
            _surfaceApi?.Dispose();
        }
    }
}

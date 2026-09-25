using System;
using Genesis.Rendering.SilkNet.OpenGL;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>
    /// Proves the Vulkan foundation end to end: acquire, render pass, clear, present, read back.
    /// </summary>
    /// <remarks>
    /// This is the B1 gate from Phase6_2_Vulkan_Rebuild.md, kept in the assembly because everything
    /// it drives is internal. It exercises exactly the path the device will use — swap chain
    /// acquire, layout transitions, render pass, <c>vkCmdClearAttachments</c>, copy-to-buffer and
    /// present — so a green result means those pieces genuinely work rather than merely compile.
    /// </remarks>
    internal static unsafe class VulkanFoundationProbe
    {
        public readonly struct ProbeResult
        {
            public ProbeResult(bool succeeded, byte r, byte g, byte b, byte a, string message)
            {
                Succeeded = succeeded;
                R = r;
                G = g;
                B = b;
                A = a;
                Message = message;
            }

            public bool Succeeded { get; }
            public byte R { get; }
            public byte G { get; }
            public byte B { get; }
            public byte A { get; }
            public string Message { get; }

            public override string ToString() =>
                $"{(Succeeded ? "OK" : "FAILED")} rgba=({R},{G},{B},{A}) {Message}";
        }

        /// <summary>Clears a real swap chain image to a known colour and reads the result back.</summary>
        public static ProbeResult Run(int width = 64, int height = 64)
        {
            IntPtr window = Wgl.CreateWindowExA(
                0, "STATIC", "GenesisVulkanProbe", 0, 0, 0, width, height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                return new ProbeResult(false, 0, 0, 0, 0, "could not create the probe window");
            }

            VulkanRuntime runtime = VulkanRuntime.Acquire();
            VulkanSwapChain swapChain = null;
            VulkanFrameRing frameRing = null;

            try
            {
                swapChain = new VulkanSwapChain(runtime, window, width, height);
                if (!swapChain.IsReady)
                {
                    return new ProbeResult(false, 0, 0, 0, 0, "the swap chain reported it was not ready");
                }

                frameRing = new VulkanFrameRing(runtime);

                // A deliberately asymmetric colour: a channel-order mistake shows up immediately
                // rather than surviving as a plausible grey.
                const float clearR = 0.25f, clearG = 0.50f, clearB = 0.75f;

                frameRing.BeginFrame();
                if (!swapChain.TryAcquire(frameRing.AcquiredSemaphore, out _))
                {
                    return new ProbeResult(false, 0, 0, 0, 0, "the swap chain could not acquire an image");
                }

                CommandBuffer cmd = frameRing.CurrentCommandBuffer;
                int index = swapChain.AcquiredIndex;
                VkImage image = swapChain.AcquiredImage;

                // The surface decides the extent, not the requested size — a window's client area is
                // whatever the shell gave it, and copying the requested size overruns the image.
                int surfaceWidth = swapChain.Width;
                int surfaceHeight = swapChain.Height;

                Transition(runtime, cmd, image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

                // The render pass declares the depth attachment already in its optimal layout, so
                // the image has to be moved there before the first pass that reads the declaration.
                Transition(
                    runtime, cmd, swapChain.DepthImage,
                    ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal,
                    ImageAspectFlags.DepthBit);

                ClearInsideRenderPass(runtime, cmd, swapChain, clearR, clearG, clearB);
                Transition(runtime, cmd, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal);

                (VkBuffer staging, DeviceMemory memory) =
                    CreateStagingBuffer(runtime, surfaceWidth * surfaceHeight * 4);
                try
                {
                    CopyToBuffer(runtime, cmd, image, staging, surfaceWidth, surfaceHeight);
                    Transition(runtime, cmd, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr);
                    swapChain.SetLayout(index, ImageLayout.PresentSrcKhr);

                    // Captured before submitting: EndFrameAndSubmit advances to the next frame slot,
                    // so reading it afterwards yields a semaphore nothing has signalled.
                    var rendered = frameRing.RenderedSemaphore;

                    frameRing.EndFrameAndSubmit(waitForAcquire: true);
                    swapChain.Present(rendered, out _);
                    runtime.WaitIdle();

                    (byte b, byte g, byte r, byte a) = ReadFirstPixel(runtime, memory);

                    bool matches = Near(r, clearR) && Near(g, clearG) && Near(b, clearB);
                    return new ProbeResult(
                        matches, r, g, b, a,
                        matches
                            ? $"cleared, presented and read back the expected colour at {surfaceWidth}x{surfaceHeight}"
                            : $"expected rgb≈({Byte(clearR)},{Byte(clearG)},{Byte(clearB)})");
                }
                finally
                {
                    runtime.Api.DestroyBuffer(runtime.Device, staging, null);
                    runtime.Api.FreeMemory(runtime.Device, memory, null);
                }
            }
            catch (Exception ex)
            {
                return new ProbeResult(false, 0, 0, 0, 0, ex.Message);
            }
            finally
            {
                frameRing?.Dispose();
                swapChain?.Dispose();
                VulkanRuntime.Release();
                Wgl.DestroyWindow(window);
            }
        }

        private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

        // sRGB-vs-UNORM and rounding both move the value a little; the point is the channel, not the
        // exact byte.
        private static bool Near(byte actual, float expected) => Math.Abs(actual - Byte(expected)) <= 8;

        private static void ClearInsideRenderPass(
            VulkanRuntime runtime, CommandBuffer cmd, VulkanSwapChain swapChain, float r, float g, float b)
        {
            var area = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)swapChain.Width, (uint)swapChain.Height));

            var begin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = swapChain.RenderPass,
                Framebuffer = swapChain.AcquiredFramebuffer,
                RenderArea = area,
                ClearValueCount = 0,
            };

            runtime.Api.CmdBeginRenderPass(cmd, &begin, SubpassContents.Inline);

            // Cleared inside the pass because the attachments load rather than clear — which is what
            // lets one render pass serve both cleared and preserved passes.
            var attachments = stackalloc ClearAttachment[2];
            attachments[0] = new ClearAttachment
            {
                AspectMask = ImageAspectFlags.ColorBit,
                ColorAttachment = 0,
                ClearValue = new ClearValue(new ClearColorValue(r, g, b, 1f)),
            };
            attachments[1] = new ClearAttachment
            {
                AspectMask = ImageAspectFlags.DepthBit,
                ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)),
            };

            var rect = new ClearRect { Rect = area, BaseArrayLayer = 0, LayerCount = 1 };
            runtime.Api.CmdClearAttachments(cmd, 2, attachments, 1, &rect);
            runtime.Api.CmdEndRenderPass(cmd);
        }

        private static void Transition(
            VulkanRuntime runtime,
            CommandBuffer cmd,
            VkImage image,
            ImageLayout from,
            ImageLayout to,
            ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            runtime.Api.CmdPipelineBarrier(
                cmd,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                DependencyFlags.None,
                0, null, 0, null, 1, &barrier);
        }

        private static (VkBuffer Buffer, DeviceMemory Memory) CreateStagingBuffer(VulkanRuntime runtime, int bytes)
        {
            var info = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)bytes,
                Usage = BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };

            VkBuffer buffer;
            VulkanRuntime.Check(
                runtime.Api.CreateBuffer(runtime.Device, &info, null, &buffer), "creating the readback buffer");

            runtime.Api.GetBufferMemoryRequirements(runtime.Device, buffer, out MemoryRequirements requirements);

            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = runtime.FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
            };

            DeviceMemory memory;
            VulkanRuntime.Check(
                runtime.Api.AllocateMemory(runtime.Device, &allocate, null, &memory), "allocating readback memory");
            VulkanRuntime.Check(
                runtime.Api.BindBufferMemory(runtime.Device, buffer, memory, 0), "binding readback memory");

            return (buffer, memory);
        }

        private static void CopyToBuffer(
            VulkanRuntime runtime, CommandBuffer cmd, VkImage image, VkBuffer destination, int width, int height)
        {
            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };

            runtime.Api.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, destination, 1, &region);
        }

        /// <summary>Reads the first texel. The surface is BGRA, so that is the order returned.</summary>
        private static (byte B, byte G, byte R, byte A) ReadFirstPixel(VulkanRuntime runtime, DeviceMemory memory)
        {
            void* mapped = null;
            VulkanRuntime.Check(
                runtime.Api.MapMemory(runtime.Device, memory, 0, 4, 0, &mapped), "mapping the readback buffer");

            byte* pixel = (byte*)mapped;
            (byte, byte, byte, byte) value = (pixel[0], pixel[1], pixel[2], pixel[3]);
            runtime.Api.UnmapMemory(runtime.Device, memory);
            return value;
        }
    }
}

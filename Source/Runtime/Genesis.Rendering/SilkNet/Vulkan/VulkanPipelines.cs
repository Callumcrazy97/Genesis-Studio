using System;
using System.Collections.Generic;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>
    /// Everything Genesis sets one call at a time, collapsed into the immutable object Vulkan wants.
    /// </summary>
    /// <remarks>
    /// The <see cref="Abstractions.IGpuDevice"/> contract is immediate-mode: blend, depth, raster,
    /// topology, program and vertex layout arrive as independent setters. Vulkan bakes all of them
    /// into a <c>VkPipeline</c> that must exist before a draw is recorded, so they are resolved at
    /// draw time against a cache keyed on the whole state vector. The render pass is part of the key
    /// because a pipeline is only compatible with the pass it was built against.
    /// </remarks>
    internal readonly struct VulkanPipelineKey : IEquatable<VulkanPipelineKey>
    {
        public readonly int ProgramId;
        public readonly int VertexLayoutId;
        public readonly GpuPrimitiveTopology Topology;
        public readonly GpuBlendState Blend;
        public readonly GpuDepthState Depth;
        public readonly GpuRasterState Raster;
        public readonly ulong RenderPass;
        public readonly int ColorAttachmentCount;
        public readonly bool HasDepth;

        public VulkanPipelineKey(
            int programId, int vertexLayoutId, GpuPrimitiveTopology topology,
            in GpuBlendState blend, in GpuDepthState depth, in GpuRasterState raster,
            ulong renderPass, int colorAttachmentCount, bool hasDepth)
        {
            ProgramId = programId;
            VertexLayoutId = vertexLayoutId;
            Topology = topology;
            Blend = blend;
            Depth = depth;
            Raster = raster;
            RenderPass = renderPass;
            ColorAttachmentCount = colorAttachmentCount;
            HasDepth = hasDepth;
        }

        public bool Equals(VulkanPipelineKey other) =>
            ProgramId == other.ProgramId
            && VertexLayoutId == other.VertexLayoutId
            && Topology == other.Topology
            && RenderPass == other.RenderPass
            && ColorAttachmentCount == other.ColorAttachmentCount
            && HasDepth == other.HasDepth
            && Blend.Equals(other.Blend)
            && Depth.Equals(other.Depth)
            && Raster.Equals(other.Raster);

        public override bool Equals(object obj) => obj is VulkanPipelineKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(ProgramId);
            hash.Add(VertexLayoutId);
            hash.Add(Topology);
            hash.Add(RenderPass);
            hash.Add(ColorAttachmentCount);
            hash.Add(HasDepth);
            hash.Add(Blend);
            hash.Add(Depth);
            hash.Add(Raster);
            return hash.ToHashCode();
        }
    }

    /// <summary>Builds and reuses graphics pipelines for the state combinations actually drawn.</summary>
    internal sealed unsafe class VulkanPipelineCache : IDisposable
    {
        private readonly VulkanRuntime _runtime;
        private readonly Dictionary<VulkanPipelineKey, Pipeline> _pipelines = new();
        private bool _disposed;

        public VulkanPipelineCache(VulkanRuntime runtime) => _runtime = runtime;

        public int Count => _pipelines.Count;

        public Pipeline GetOrCreate(
            in VulkanPipelineKey key,
            ShaderModule vertexShader, string vertexEntry,
            ShaderModule fragmentShader, string fragmentEntry,
            PipelineLayout layout,
            in GpuVertexLayoutDesc vertexLayout,
            RenderPass renderPass)
        {
            if (_pipelines.TryGetValue(key, out Pipeline existing)) return existing;

            Pipeline created = Create(
                key, vertexShader, vertexEntry, fragmentShader, fragmentEntry, layout, vertexLayout, renderPass);
            _pipelines[key] = created;
            return created;
        }

        private Pipeline Create(
            in VulkanPipelineKey key,
            ShaderModule vertexShader, string vertexEntry,
            ShaderModule fragmentShader, string fragmentEntry,
            PipelineLayout layout,
            in GpuVertexLayoutDesc vertexLayout,
            RenderPass renderPass)
        {
            // DXC keeps the HLSL entry name in the SPIR-V rather than renaming it to "main", so the
            // stage must name whatever the module actually declares or creation fails.
            IntPtr vertexName = SilkMarshal.StringToPtr(string.IsNullOrEmpty(vertexEntry) ? "main" : vertexEntry);
            IntPtr fragmentName = SilkMarshal.StringToPtr(string.IsNullOrEmpty(fragmentEntry) ? "main" : fragmentEntry);

            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                uint stageCount = 0;

                if (vertexShader.Handle != 0)
                {
                    stages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.VertexBit,
                        Module = vertexShader,
                        PName = (byte*)vertexName,
                    };
                }

                if (fragmentShader.Handle != 0)
                {
                    stages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.FragmentBit,
                        Module = fragmentShader,
                        PName = (byte*)fragmentName,
                    };
                }

                GpuVertexElement[] elements = vertexLayout.Elements ?? Array.Empty<GpuVertexElement>();
                int[] strides = vertexLayout.SlotStrides ?? Array.Empty<int>();

                var bindings = stackalloc VertexInputBindingDescription[8];
                uint bindingCount = 0;
                for (int slot = 0; slot < strides.Length && slot < 8; slot++)
                {
                    if (strides[slot] <= 0) continue;
                    bindings[bindingCount++] = new VertexInputBindingDescription
                    {
                        Binding = (uint)slot,
                        Stride = (uint)strides[slot],
                        InputRate = InstanceRateForSlot(elements, slot)
                            ? VertexInputRate.Instance
                            : VertexInputRate.Vertex,
                    };
                }

                var attributes = stackalloc VertexInputAttributeDescription[32];
                uint attributeCount = 0;
                for (int i = 0; i < elements.Length && attributeCount < 32; i++)
                {
                    // Location is the declaration index: DXC was invoked with
                    // -fvk-stage-io-order=decl, so it numbered inputs in HLSL declaration order and
                    // GpuVertexLayoutDesc lists them the same way.
                    attributes[attributeCount++] = new VertexInputAttributeDescription
                    {
                        Location = (uint)i,
                        Binding = (uint)elements[i].Slot,
                        Format = VulkanGpuFormats.ToVulkan(elements[i].Format),
                        Offset = (uint)elements[i].OffsetBytes,
                    };
                }

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = bindingCount,
                    PVertexBindingDescriptions = bindings,
                    VertexAttributeDescriptionCount = attributeCount,
                    PVertexAttributeDescriptions = attributes,
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = VulkanGpuFormats.ToTopology(key.Topology),
                    PrimitiveRestartEnable = false,
                };

                // Viewport and scissor are dynamic; baking them in would rebuild every pipeline on
                // a window resize.
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = VulkanGpuFormats.ToFill(key.Raster.FillMode),
                    CullMode = VulkanGpuFormats.ToCull(key.Raster.CullMode),

                    // With a negative viewport height (Y-flip to match D3D NDC), clockwise triangles in
                    // D3D NDC retain clockwise orientation in framebuffer space.
                    FrontFace = key.Raster.FrontCounterClockwise
                        ? FrontFace.CounterClockwise
                        : FrontFace.Clockwise,
                    DepthBiasEnable = key.Raster.DepthBias != 0 || key.Raster.SlopeScaledDepthBias != 0f,
                    DepthBiasConstantFactor = key.Raster.DepthBias,
                    DepthBiasSlopeFactor = key.Raster.SlopeScaledDepthBias,
                    DepthBiasClamp = 0f,
                    LineWidth = 1f,
                };

                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                    MinSampleShading = 1f,
                };

                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = key.HasDepth && key.Depth.TestEnabled,
                    DepthWriteEnable = key.HasDepth && key.Depth.WriteEnabled,
                    DepthCompareOp = VulkanGpuFormats.ToCompare(key.Depth.Compare),
                    DepthBoundsTestEnable = false,
                    StencilTestEnable = false,
                };

                int attachmentCount = Math.Max(1, key.ColorAttachmentCount);
                var blendAttachments = stackalloc PipelineColorBlendAttachmentState[8];
                for (int i = 0; i < attachmentCount && i < 8; i++)
                {
                    // When independent blending is unavailable Vulkan requires every attachment
                    // state to be byte-for-byte identical. The primary state is the portable
                    // fallback; devices advertising the feature retain Genesis's MRT state.
                    bool secondary = i == 1
                        && key.Blend.IndependentBlend
                        && _runtime.IndependentBlendEnabled;
                    blendAttachments[i] = secondary
                        ? BlendAttachment(
                            key.Blend.SecondaryEnabled,
                            key.Blend.SecondarySrcColor, key.Blend.SecondaryDstColor, key.Blend.SecondaryColorOp,
                            key.Blend.SecondarySrcAlpha, key.Blend.SecondaryDstAlpha, key.Blend.SecondaryAlphaOp,
                            key.Blend.SecondaryWriteR, key.Blend.SecondaryWriteG,
                            key.Blend.SecondaryWriteB, key.Blend.SecondaryWriteA)
                        : BlendAttachment(
                            key.Blend.Enabled,
                            key.Blend.SrcColor, key.Blend.DstColor, key.Blend.ColorOp,
                            key.Blend.SrcAlpha, key.Blend.DstAlpha, key.Blend.AlphaOp,
                            key.Blend.WriteR, key.Blend.WriteG, key.Blend.WriteB, key.Blend.WriteA);
                }

                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = (uint)Math.Min(attachmentCount, 8),
                    PAttachments = blendAttachments,
                };

                var dynamicStates = stackalloc DynamicState[2];
                dynamicStates[0] = DynamicState.Viewport;
                dynamicStates[1] = DynamicState.Scissor;

                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates,
                };

                var createInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = stageCount,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = layout,
                    RenderPass = renderPass,
                    Subpass = 0,
                };

                Pipeline pipeline;
                VulkanRuntime.Check(
                    _runtime.Api.CreateGraphicsPipelines(_runtime.Device, default, 1, &createInfo, null, &pipeline),
                    "creating a graphics pipeline");
                return pipeline;
            }
            finally
            {
                SilkMarshal.Free(vertexName);
                SilkMarshal.Free(fragmentName);
            }
        }

        private static bool InstanceRateForSlot(GpuVertexElement[] elements, int slot)
        {
            foreach (GpuVertexElement element in elements)
            {
                if (element.Slot == slot && element.InstanceStepRate > 0) return true;
            }

            return false;
        }

        private static PipelineColorBlendAttachmentState BlendAttachment(
            bool enabled,
            GpuBlendFactor srcColor, GpuBlendFactor dstColor, GpuBlendOp colorOp,
            GpuBlendFactor srcAlpha, GpuBlendFactor dstAlpha, GpuBlendOp alphaOp,
            bool writeR, bool writeG, bool writeB, bool writeA) => new()
        {
            BlendEnable = enabled,
            SrcColorBlendFactor = VulkanGpuFormats.ToBlend(srcColor),
            DstColorBlendFactor = VulkanGpuFormats.ToBlend(dstColor),
            ColorBlendOp = VulkanGpuFormats.ToBlendOp(colorOp),
            SrcAlphaBlendFactor = VulkanGpuFormats.ToBlend(srcAlpha),
            DstAlphaBlendFactor = VulkanGpuFormats.ToBlend(dstAlpha),
            AlphaBlendOp = VulkanGpuFormats.ToBlendOp(alphaOp),
            ColorWriteMask = VulkanGpuFormats.ToWriteMask(writeR, writeG, writeB, writeA),
        };

        /// <summary>Drops every pipeline built against a render pass that is going away.</summary>
        /// <remarks>
        /// Destroying a render pass leaves its pipelines dangling, and the next draw through one is
        /// undefined behaviour rather than an error. A swap chain resize recreates its pass.
        /// </remarks>
        public void InvalidateForRenderPass(RenderPass renderPass)
        {
            var doomed = new List<VulkanPipelineKey>();
            foreach (KeyValuePair<VulkanPipelineKey, Pipeline> entry in _pipelines)
            {
                if (entry.Key.RenderPass == renderPass.Handle) doomed.Add(entry.Key);
            }

            foreach (VulkanPipelineKey key in doomed)
            {
                _runtime.Api.DestroyPipeline(_runtime.Device, _pipelines[key], null);
                _pipelines.Remove(key);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (Pipeline pipeline in _pipelines.Values)
            {
                _runtime.Api.DestroyPipeline(_runtime.Device, pipeline, null);
            }

            _pipelines.Clear();
        }
    }
}

using System;

namespace Genesis.Rendering.Abstractions
{
    public struct GpuBlendState : IEquatable<GpuBlendState>
    {
        public bool           Enabled;
        public GpuBlendFactor SrcColor;
        public GpuBlendFactor DstColor;
        public GpuBlendOp     ColorOp;
        public GpuBlendFactor SrcAlpha;
        public GpuBlendFactor DstAlpha;
        public GpuBlendOp     AlphaOp;
        public bool           WriteR, WriteG, WriteB, WriteA;

        /// <summary>
        /// Enables a distinct state for colour attachment 1. The forward pass uses this for its
        /// fog-skip mask: scene colour blends in attachment 0 while the binary mask overwrites in
        /// attachment 1. Backends may ignore the secondary fields when this is false.
        /// </summary>
        public bool           IndependentBlend;
        public bool           SecondaryEnabled;
        public GpuBlendFactor SecondarySrcColor;
        public GpuBlendFactor SecondaryDstColor;
        public GpuBlendOp     SecondaryColorOp;
        public GpuBlendFactor SecondarySrcAlpha;
        public GpuBlendFactor SecondaryDstAlpha;
        public GpuBlendOp     SecondaryAlphaOp;
        public bool           SecondaryWriteR, SecondaryWriteG, SecondaryWriteB, SecondaryWriteA;

        public static GpuBlendState Opaque => new GpuBlendState
        {
            Enabled = false,
            WriteR = true, WriteG = true, WriteB = true, WriteA = true,
        };

        public static GpuBlendState AlphaBlend => new GpuBlendState
        {
            Enabled = true,
            SrcColor = GpuBlendFactor.SrcAlpha, DstColor = GpuBlendFactor.InvSrcAlpha, ColorOp = GpuBlendOp.Add,
            SrcAlpha = GpuBlendFactor.One,      DstAlpha = GpuBlendFactor.InvSrcAlpha, AlphaOp = GpuBlendOp.Add,
            WriteR = true, WriteG = true, WriteB = true, WriteA = true,
        };

        public static GpuBlendState Additive => new GpuBlendState
        {
            Enabled = true,
            SrcColor = GpuBlendFactor.SrcAlpha, DstColor = GpuBlendFactor.One, ColorOp = GpuBlendOp.Add,
            SrcAlpha = GpuBlendFactor.Zero,     DstAlpha = GpuBlendFactor.One, AlphaOp = GpuBlendOp.Add,
            WriteR = true, WriteG = true, WriteB = true, WriteA = true,
        };

        public static GpuBlendState Multiply => new GpuBlendState
        {
            Enabled = true,
            SrcColor = GpuBlendFactor.DestColor, DstColor = GpuBlendFactor.InvSrcAlpha, ColorOp = GpuBlendOp.Add,
            SrcAlpha = GpuBlendFactor.Zero,      DstAlpha = GpuBlendFactor.One, AlphaOp = GpuBlendOp.Add,
            WriteR = true, WriteG = true, WriteB = true, WriteA = true,
        };

        public bool Equals(GpuBlendState o) =>
            Enabled == o.Enabled && SrcColor == o.SrcColor && DstColor == o.DstColor && ColorOp == o.ColorOp
            && SrcAlpha == o.SrcAlpha && DstAlpha == o.DstAlpha && AlphaOp == o.AlphaOp
            && WriteR == o.WriteR && WriteG == o.WriteG && WriteB == o.WriteB && WriteA == o.WriteA
            && IndependentBlend == o.IndependentBlend && SecondaryEnabled == o.SecondaryEnabled
            && SecondarySrcColor == o.SecondarySrcColor && SecondaryDstColor == o.SecondaryDstColor
            && SecondaryColorOp == o.SecondaryColorOp
            && SecondarySrcAlpha == o.SecondarySrcAlpha && SecondaryDstAlpha == o.SecondaryDstAlpha
            && SecondaryAlphaOp == o.SecondaryAlphaOp
            && SecondaryWriteR == o.SecondaryWriteR && SecondaryWriteG == o.SecondaryWriteG
            && SecondaryWriteB == o.SecondaryWriteB && SecondaryWriteA == o.SecondaryWriteA;

        public override bool Equals(object obj) => obj is GpuBlendState o && Equals(o);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Enabled); h.Add(SrcColor); h.Add(DstColor); h.Add(ColorOp);
            h.Add(SrcAlpha); h.Add(DstAlpha); h.Add(AlphaOp);
            h.Add(WriteR); h.Add(WriteG); h.Add(WriteB); h.Add(WriteA);
            h.Add(IndependentBlend); h.Add(SecondaryEnabled);
            h.Add(SecondarySrcColor); h.Add(SecondaryDstColor); h.Add(SecondaryColorOp);
            h.Add(SecondarySrcAlpha); h.Add(SecondaryDstAlpha); h.Add(SecondaryAlphaOp);
            h.Add(SecondaryWriteR); h.Add(SecondaryWriteG); h.Add(SecondaryWriteB); h.Add(SecondaryWriteA);
            return h.ToHashCode();
        }
    }

    public struct GpuDepthState : IEquatable<GpuDepthState>
    {
        public bool       TestEnabled;
        public bool       WriteEnabled;
        public GpuCompare Compare;

        public static GpuDepthState Default => new GpuDepthState
        {
            TestEnabled = true, WriteEnabled = true, Compare = GpuCompare.LessEqual,
        };

        public static GpuDepthState ReadOnly => new GpuDepthState
        {
            TestEnabled = true, WriteEnabled = false, Compare = GpuCompare.LessEqual,
        };

        public static GpuDepthState Disabled => new GpuDepthState
        {
            TestEnabled = false, WriteEnabled = false, Compare = GpuCompare.Always,
        };

        public bool Equals(GpuDepthState o) =>
            TestEnabled == o.TestEnabled && WriteEnabled == o.WriteEnabled && Compare == o.Compare;

        public override bool Equals(object obj) => obj is GpuDepthState o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(TestEnabled, WriteEnabled, Compare);
    }

    public struct GpuRasterState : IEquatable<GpuRasterState>
    {
        public GpuCullMode CullMode;
        public GpuFillMode FillMode;
        /// <summary>
        /// Which winding counts as front-facing. OpenGL flips this internally: rendering with
        /// GL_UPPER_LEFT clip control to match D3D's framebuffer origin inverts the effective
        /// winding, so the GL backend negates this rather than the contract changing.
        /// </summary>
        public bool        FrontCounterClockwise;
        public bool        ScissorEnabled;
        /// <summary>Clips primitives outside the depth range. Sprites deliberately disable this.</summary>
        public bool        DepthClipEnabled;
        public float       DepthBias;
        public float       SlopeScaledDepthBias;

        public static GpuRasterState Default => new GpuRasterState
        {
            CullMode = GpuCullMode.Back,
            FillMode = GpuFillMode.Solid,
            FrontCounterClockwise = false,
            DepthClipEnabled = true,
        };

        public static GpuRasterState NoCull => new GpuRasterState
        {
            CullMode = GpuCullMode.None,
            FillMode = GpuFillMode.Solid,
            FrontCounterClockwise = false,
            DepthClipEnabled = true,
        };

        public bool Equals(GpuRasterState o) =>
            CullMode == o.CullMode && FillMode == o.FillMode
            && FrontCounterClockwise == o.FrontCounterClockwise && ScissorEnabled == o.ScissorEnabled
            && DepthClipEnabled == o.DepthClipEnabled
            && DepthBias.Equals(o.DepthBias) && SlopeScaledDepthBias.Equals(o.SlopeScaledDepthBias);

        public override bool Equals(object obj) => obj is GpuRasterState o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(CullMode, FillMode, FrontCounterClockwise, ScissorEnabled,
                DepthClipEnabled, DepthBias, SlopeScaledDepthBias);
    }

    /// <summary>
    /// The full pipeline signature a draw resolves to.
    /// </summary>
    /// <remarks>
    /// This type is why an immediate-mode interface can be implemented on Vulkan at all. The state
    /// setters on <see cref="IGpuDevice"/> are immediate and simply write into the device's current
    /// key; the *draw* resolves it. D3D11 and OpenGL apply the delta directly, while Vulkan hashes
    /// the key and looks up (or builds) the matching VkPipeline. Without that, porting a
    /// multi-thousand-line immediate-mode renderer would mean restructuring its control flow rather than
    /// mechanically swapping one call for another — and a big-bang rewrite of the renderer is
    /// exactly the risk this whole design is arranged to avoid.
    /// </remarks>
    public struct GpuPipelineKey : IEquatable<GpuPipelineKey>
    {
        public GpuShaderProgramHandle Program;
        public GpuVertexLayoutHandle  VertexLayout;
        public GpuPrimitiveTopology   Topology;
        public GpuBlendState          Blend;
        public GpuDepthState          Depth;
        public GpuRasterState         Raster;
        /// <summary>
        /// Identifies the render-pass signature (attachment formats and count). Vulkan pipelines are
        /// bound to one, so two otherwise identical states used in different passes are different
        /// pipelines.
        /// </summary>
        public int                    PassSignature;

        public bool Equals(GpuPipelineKey o) =>
            Program.Id == o.Program.Id && VertexLayout.Id == o.VertexLayout.Id
            && Topology == o.Topology && PassSignature == o.PassSignature
            && Blend.Equals(o.Blend) && Depth.Equals(o.Depth) && Raster.Equals(o.Raster);

        public override bool Equals(object obj) => obj is GpuPipelineKey o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(Program.Id, VertexLayout.Id, Topology, PassSignature, Blend, Depth, Raster);
    }
}

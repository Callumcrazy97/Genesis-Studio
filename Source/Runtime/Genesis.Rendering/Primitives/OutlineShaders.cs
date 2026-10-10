namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Outlines and the object images (ForwardRenderer.Outlines): marked draws are drawn again into
    /// two images, ObjectMarks (rgb the outline colour, a its width) and ObjectIds (the draw's id),
    /// each pixel keeping the nearest mark; a full-screen pass then draws each mark's outline over
    /// the finished frame. Project post effects read both images.
    /// </summary>
    public static class OutlineShaders
    {
        /// <summary>Marks are written as <c>(width + 1) / MarkScale</c> in alpha, so 0 means unmarked.</summary>
        public const float MarkScale = 63f;

        /// <summary>The widest outline, in pixels.</summary>
        public const int MaxWidth = 16;

        /// <summary>
        /// The marking pixel shader, after the forward shader's declarations so its vertex output and
        /// constants are the forward vertex shader's. MaterialColor.rgb is the outline colour,
        /// MaterialParams x the id, y 1 to show through what is in front, z the width in pixels.
        /// </summary>
        public static readonly string MarkSource = MarkPrelude() + @"
Texture2D<float> OutlineSceneDepth : register(t17);

#define GENESIS_DEPTH_REVERSED (StylizedParams2.z > 0.5)
" + SceneDepthHlsl.Helpers + @"

struct OutlineMarkOut
{
    float4 Mark : SV_Target0;
    float  Id   : SV_Target1;
};

OutlineMarkOut PS_OutlineMark(VSOut IN)
{
    // Hidden: something the scene shows lies in front of this surface (the scene holds this
    // surface's own depth where it is what the scene shows).
    float scene = OutlineSceneDepth.Load(int3(int2(IN.SvPos.xy), 0));
    float slack = GENESIS_DEPTH_REVERSED ? max(scene * 1e-4, 1e-9) : 2e-6;
    bool hidden = SceneDepthBehind(IN.SvPos.z, scene) > slack;
    if (hidden && MaterialParams.y < 0.5)
        discard;
    OutlineMarkOut o;
    o.Mark = float4(saturate(MaterialColor.rgb), (clamp(MaterialParams.z, 0.0, 62.0) + 1.0) / 63.0);
    // A hidden mark's id is negative, so a post effect can treat what is behind walls differently.
    o.Id = hidden ? -MaterialParams.x : MaterialParams.x;
    return o;
}
";

        /// <summary>The forward shader up to its pixel shader: constants, resources, VSOut, helpers.</summary>
        private static string MarkPrelude()
        {
            string forward = ForwardShaders.Source;
            int split = forward.IndexOf(MeshSurfaceShaders.PixelSectionMarker, StringComparison.Ordinal);
            if (split < 0) throw new InvalidOperationException("The forward shader no longer has the section the outline marks follow.");
            return forward.Substring(0, split);
        }

        /// <summary>
        /// Draws each mark's outline around it: a pixel takes the colour of the nearest mark (of
        /// another id than its own) whose width reaches it, blended by how far inside that width it is.
        /// </summary>
        public const string CompositeSource = @"
Texture2D<float4> ObjectMarks : register(t0);
Texture2D<float>  ObjectIds   : register(t1);

cbuffer OutlineConstants : register(b0)
{
    float4 OutlineParams; // x = widest outline this frame (pixels), yz = image size
};

struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };

static const float2 OutlineDirections[12] =
{
    float2( 1.0,  0.0), float2( 0.866,  0.5), float2( 0.5,  0.866),
    float2( 0.0,  1.0), float2(-0.5,  0.866), float2(-0.866,  0.5),
    float2(-1.0,  0.0), float2(-0.866, -0.5), float2(-0.5, -0.866),
    float2( 0.0, -1.0), float2( 0.5, -0.866), float2( 0.866, -0.5),
};

float MarkWidth(float a) { return round(a * 63.0) - 1.0; }

float4 PS_OutlineComposite(PreviewVSOut IN) : SV_Target
{
    int2 size = int2(OutlineParams.yz);
    int2 p = int2(IN.SvPos.xy);
    float4 here = ObjectMarks.Load(int3(p, 0));
    float hereId = here.a > 0.0 ? abs(ObjectIds.Load(int3(p, 0))) : 0.0;
    int rings = (int)OutlineParams.x;
    float bestDistance = 1e9;
    float bestWidth = 0.0;
    float3 bestColour = float3(0.0, 0.0, 0.0);
    [loop]
    for (int r = 1; r <= rings; r++)
    {
        [unroll]
        for (int k = 0; k < 12; k++)
        {
            int2 offset = int2(round(OutlineDirections[k] * r));
            int2 q = clamp(p + offset, int2(0, 0), size - 1);
            float4 m = ObjectMarks.Load(int3(q, 0));
            if (m.a <= 0.0) continue;
            float width = MarkWidth(m.a);
            float distance = length(float2(offset));
            if (width + 0.5 < distance || distance >= bestDistance) continue;
            if (here.a > 0.0 && abs(ObjectIds.Load(int3(q, 0))) == hereId) continue;
            bestDistance = distance;
            bestWidth = width;
            bestColour = m.rgb;
        }
        if (bestDistance < 1e8) break;
    }
    if (bestDistance >= 1e8) discard;
    return float4(bestColour, saturate(bestWidth + 0.5 - bestDistance + 0.5));
}
";
    }
}

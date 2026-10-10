namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Projected decals (IRenderController.DrawDecals). Each decal is a box around a point on a
    /// surface; after the opaque world is drawn and lit, a screen rectangle covering the box reads the
    /// scene depth, rebuilds the world position of each pixel and paints the picture on whatever lies
    /// inside the box: walls, models, terrain. Surfaces turned away from the decal take none of it.
    /// </summary>
    /// <remarks>
    /// The pass draws into the scene's HDR colour and its fog/ambient attachment with no depth
    /// attachment (the depth is read as a texture). Multiply decals scale the colour and the
    /// stored ambient term alike, so the composite's ambient occlusion still removes exactly the
    /// indirect light that is left. The Software renderer draws the same pass on the CPU
    /// (SoftwareDecals) with the same constants and instance layout.
    /// </remarks>
    public static class DecalShaders
    {
        public const string Source = @"
cbuffer DecalConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 InvViewProjection;
    float4 CameraPos;     // xyz = eye, w = scene depth is reversed
    float4 Viewport;      // width, height, 1 / width, 1 / height
    float4 DecalParams;   // x = first decal of this draw, y = blend (0 multiply, 1 alpha, 2 add), z = linear colour, w = decals in this draw
};

// One decal: the box is Center + AxisX * x + AxisY * y + AxisZ * z for x, y, z in [-0.5, 0.5].
// AxisX runs across the picture (left to right), AxisY up it, AxisZ out of the surface.
struct DecalGpu
{
    float4 Center;
    float4 AxisX;
    float4 AxisY;
    float4 AxisZ;
    float4 Color;    // linear tint rgb, a = opacity
    float4 Params;   // x = cosine of the angle with full strength, y = cosine where it is gone
};

StructuredBuffer<DecalGpu> Decals    : register(t0);
Texture2D<float>           SceneDepth : register(t1);
Texture2D                  DecalTex   : register(t2);
SamplerState               DecalSamp  : register(s0);

#define GENESIS_DEPTH_REVERSED (CameraPos.w > 0.5)
" + SceneDepthHlsl.Helpers + @"

struct VSIn
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float4 Color    : COLOR;
    float2 UV       : TEXCOORD0;
};

struct VSOut
{
    float4 SvPos : SV_Position;
    nointerpolation float4 Center : TEXCOORD0;
    nointerpolation float4 InvX   : TEXCOORD1;
    nointerpolation float4 InvY   : TEXCOORD2;
    nointerpolation float4 InvZ   : TEXCOORD3;
    nointerpolation float4 Tint   : TEXCOORD4;
    nointerpolation float4 Fade   : TEXCOORD5;
};

// The decal quad's corners are (0,0), (0,1), (1,1), (1,0) in Position.xy: the quad becomes the
// screen rectangle round the projected box (the whole screen when the box reaches behind the eye),
// so every covered pixel is shaded exactly once whichever way the camera winds its triangles.
VSOut VS_Decal(VSIn IN, uint instanceId : SV_InstanceID)
{
    DecalGpu d = Decals[instanceId + (uint)(DecalParams.x + 0.5)];
    float2 lo = float2(1.0, 1.0);
    float2 hi = float2(-1.0, -1.0);
    bool behind = false;
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float sx = (i & 1) != 0 ? 0.5 : -0.5;
        float sy = (i & 2) != 0 ? 0.5 : -0.5;
        float sz = (i & 4) != 0 ? 0.5 : -0.5;
        float3 corner = d.Center.xyz + d.AxisX.xyz * sx + d.AxisY.xyz * sy + d.AxisZ.xyz * sz;
        float4 clip = mul(float4(corner, 1.0), ViewProjection);
        if (clip.w <= 1e-4)
        {
            behind = true;
        }
        else
        {
            float2 ndc = clip.xy / clip.w;
            lo = min(lo, ndc);
            hi = max(hi, ndc);
        }
    }
    if (behind)
    {
        lo = float2(-1.0, -1.0);
        hi = float2(1.0, 1.0);
    }
    lo = clamp(lo, -1.0, 1.0);
    hi = clamp(hi, lo, 1.0);

    VSOut OUT;
    OUT.SvPos = float4(lerp(lo, hi, saturate(IN.Position.xy)), 0.5, 1.0);
    OUT.Center = d.Center;
    OUT.InvX = float4(d.AxisX.xyz / max(dot(d.AxisX.xyz, d.AxisX.xyz), 1e-8), 0.0);
    OUT.InvY = float4(d.AxisY.xyz / max(dot(d.AxisY.xyz, d.AxisY.xyz), 1e-8), 0.0);
    OUT.InvZ = float4(d.AxisZ.xyz / max(dot(d.AxisZ.xyz, d.AxisZ.xyz), 1e-8), 0.0);
    OUT.Tint = d.Color;
    OUT.Fade = d.Params;
    return OUT;
}

float3 DecalWorldAt(int2 pixel, float depth)
{
    float2 uv = (float2(pixel) + 0.5) * Viewport.zw;
    float4 h = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0), InvViewProjection);
    return h.xyz / h.w;
}

// The surface's normal from the depths beside this pixel: on each axis the neighbour nearer in
// depth, so the edge of a wall in front of a far floor takes the wall's normal, not the gap's.
float3 DecalSurfaceNormal(int2 pixel, float3 p, float depth)
{
    int2 last = max(int2(Viewport.xy) - 1, int2(0, 0));
    int2 l = clamp(pixel - int2(1, 0), int2(0, 0), last);
    int2 r = clamp(pixel + int2(1, 0), int2(0, 0), last);
    int2 u = clamp(pixel - int2(0, 1), int2(0, 0), last);
    int2 b = clamp(pixel + int2(0, 1), int2(0, 0), last);
    float dl = SceneDepth.Load(int3(l, 0));
    float dr = SceneDepth.Load(int3(r, 0));
    float du = SceneDepth.Load(int3(u, 0));
    float db = SceneDepth.Load(int3(b, 0));
    float3 across;
    if (abs(dr - depth) < abs(dl - depth))
        across = DecalWorldAt(r, dr) - p;
    else
        across = p - DecalWorldAt(l, dl);
    float3 down;
    if (abs(db - depth) < abs(du - depth))
        down = DecalWorldAt(b, db) - p;
    else
        down = p - DecalWorldAt(u, du);
    float3 n = cross(down, across);
    float len = length(n);
    if (len < 1e-12)
        return normalize(CameraPos.xyz - p);
    n /= len;
    // Whatever is seen faces the eye.
    if (dot(n, CameraPos.xyz - p) < 0.0)
        n = -n;
    return n;
}

float3 DecalSrgbToLinear(float3 c)
{
    c = max(c, 0.0);
    return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045));
}

struct PSOut
{
    float4 Color : SV_Target0;
    // Multiply: gba scale the stored ambient term with the colour; r, the fog flag, is not written.
    float4 Aux   : SV_Target1;
};

PSOut PS_Decal(VSOut IN)
{
    int2 pixel = int2(IN.SvPos.xy);
    float depth = SceneDepth.Load(int3(pixel, 0));
    if (SceneDepthIsSky(depth))
        discard;
    float3 p = DecalWorldAt(pixel, depth);
    float3 rel = p - IN.Center.xyz;
    float3 local = float3(dot(rel, IN.InvX.xyz), dot(rel, IN.InvY.xyz), dot(rel, IN.InvZ.xyz));
    if (abs(local.x) > 0.5 || abs(local.y) > 0.5 || abs(local.z) > 0.5)
        discard;

    // Full strength on surfaces facing the decal, nothing on those turned past the fade angle,
    // and nothing at all on a surface facing away (the back of a wall).
    float3 n = DecalSurfaceNormal(pixel, p, depth);
    float facing = dot(n, normalize(IN.InvZ.xyz));
    float angleFade = saturate((facing - IN.Fade.y) / max(IN.Fade.x - IN.Fade.y, 1e-3));
    // Softly out towards the ends of the box, so bumpy ground is not cut by a hard edge.
    float depthFade = saturate((0.5 - abs(local.z)) * 6.0);

    float4 tex = DecalTex.SampleLevel(DecalSamp, float2(local.x + 0.5, 0.5 - local.y), 0.0);
    if (DecalParams.z > 0.5)
        tex.rgb = DecalSrgbToLinear(tex.rgb);
    float alpha = saturate(tex.a * IN.Tint.a * angleFade * depthFade);
    if (alpha <= 0.002)
        discard;
    float3 rgb = tex.rgb * IN.Tint.rgb;

    PSOut OUT;
    if (DecalParams.y < 0.5)
    {
        float3 f = lerp(float3(1.0, 1.0, 1.0), rgb, alpha);
        OUT.Color = float4(f, 1.0);
        OUT.Aux = float4(1.0, f);
    }
    else if (DecalParams.y < 1.5)
    {
        OUT.Color = float4(rgb, alpha);
        // Not written (its blend is off); zero also leaves it as it was under this formula.
        OUT.Aux = float4(0.0, 0.0, 0.0, 0.0);
    }
    else
    {
        OUT.Color = float4(rgb * alpha, alpha);
        OUT.Aux = float4(0.0, 0.0, 0.0, 0.0);
    }
    return OUT;
}
";
    }
}

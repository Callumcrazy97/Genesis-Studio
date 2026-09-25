namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.4 half-resolution screen-space contact shadows (ForestLight <c>screenContactShadow</c>).
/// Writes an occlusion mask — 0 = the ray reached the light, higher = more contact shadow — which
/// FogPostShaders.cs applies as a sun/CSM-gated multiply. Depth-derived normals, no G-buffer.
/// </summary>
internal static class ContactShadowShaders
{
    public const string Source = @"
cbuffer ContactShadowConstants : register(b0)
{
    float4 ClipPlanes;                      // x=near, y=far, z=fullWidth, w=fullHeight
    row_major float4x4 InvViewProjection;   // pixel -> world
    row_major float4x4 ViewProjection;      // world -> clip, to re-project each march sample
    // xyz = unit direction *toward* the light. The engine stores the direction light travels
    // (EngineCB LightDirEnabled.xyz, Mesh3DState.LightDirection), so the caller negates it —
    // the march must follow ForestLight's directionalL, not the travel direction.
    float4 LightDirPad;
    float4 Params;                          // x=maxDistance, y=strength (applied at composite), z=time
};

Texture2D<float> SceneDepth  : register(t0);
SamplerState     LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = float2(p.x, 1.0 - p.y);
    return o;
}

float3 ReconstructWorldPos(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 world = mul(float4(ndc, depth, 1.0), InvViewProjection);
    return world.xyz / max(world.w, 1e-5);
}

// Interleaved Gradient Noise from real pixel coordinates only — no time term. A time-seeded
// jitter here makes contact shadows crawl and fizz frame to frame (the ForestLight bug this
// pass exists to avoid); a per-pixel dither breaks the step banding while staying stable under
// sub-pixel camera motion.
float HashIGN(float2 pixel)
{
    pixel = floor(pixel);
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// Nearest of the tap and its four cross neighbours: a single half-res tap misses thin occluders
// that fall between pixels, and biasing toward ""something is in front here"" is the stable
// choice for a march this short.
float NearestSceneDepth(float2 uv, float2 texel)
{
    float d = SceneDepth.SampleLevel(LinearClamp, uv, 0).r;
    d = min(d, SceneDepth.SampleLevel(LinearClamp, uv + float2(-texel.x, 0), 0).r);
    d = min(d, SceneDepth.SampleLevel(LinearClamp, uv + float2( texel.x, 0), 0).r);
    d = min(d, SceneDepth.SampleLevel(LinearClamp, uv + float2(0, -texel.y), 0).r);
    d = min(d, SceneDepth.SampleLevel(LinearClamp, uv + float2(0,  texel.y), 0).r);
    return d;
}

float4 PS_Contact(VSOut IN) : SV_Target
{
    float2 fullRes = max(ClipPlanes.zw, float2(2, 2));
    float2 texel = 1.0 / fullRes;
    float centerDepth = SceneDepth.SampleLevel(LinearClamp, IN.uv, 0).r;
    if (centerDepth >= 0.99999)
        return float4(0, 0, 0, 1);

    float3 worldPos = ReconstructWorldPos(IN.uv, centerDepth);

    // The ray from the camera through this pixel, derived from two depths on the same UV so no
    // camera position needs to ride in this cbuffer.
    float3 viewRay = normalize(worldPos - ReconstructWorldPos(IN.uv, 0.0));

    float3 dx = ReconstructWorldPos(
        IN.uv + float2(texel.x, 0),
        SceneDepth.SampleLevel(LinearClamp, IN.uv + float2(texel.x, 0), 0).r) - worldPos;
    float3 dy = ReconstructWorldPos(
        IN.uv + float2(0, texel.y),
        SceneDepth.SampleLevel(LinearClamp, IN.uv + float2(0, texel.y), 0).r) - worldPos;
    float3 normal = normalize(cross(dx, dy));
    // Depth-derived normals flip sign across silhouettes. Force the camera-facing sign so the
    // bias below never pushes the ray origin into the surface and self-occlude every pixel.
    if (dot(normal, viewRay) > 0.0)
        normal = -normal;

    float3 origin = worldPos + normal * 0.05;
    float3 toLight = normalize(LightDirPad.xyz);
    float maxDistance = max(Params.x, 0.0);
    float jitter = HashIGN(IN.pos.xy) - 0.5;
    float occlusion = 0.0;

    const int stepCount = 12;

    [loop]
    for (int i = 0; i < stepCount; i++)
    {
        // Micro-jitter the first step only: it is the step that lands on the pixel itself, so
        // dithering it removes the hard inner edge without smearing the shadow's reach.
        float t = (float(i) + 0.65 + (i == 0 ? jitter * 0.5 : 0.0)) / float(stepCount);
        float3 samplePos = origin + toLight * (maxDistance * t * t);

        float4 clip = mul(float4(samplePos, 1.0), ViewProjection);
        if (clip.w <= 1e-5)
            continue;
        float3 ndc = clip.xyz / clip.w;
        float2 sampleUv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (sampleUv.x < 0.0 || sampleUv.x > 1.0 || sampleUv.y < 0.0 || sampleUv.y > 1.0)
            continue;
        if (ndc.z <= 0.0 || ndc.z >= 1.0)
            continue;

        float sceneDepth = NearestSceneDepth(sampleUv, texel);
        if (sceneDepth >= 0.99999)
            continue;

        float separation = ndc.z - sceneDepth;
        float thickness = 0.00045 + t * 0.00145;
        float hit = smoothstep(thickness, thickness * 3.2, separation);
        occlusion = max(occlusion, hit * (1.0 - t * 0.58));
    }

    occlusion = clamp(occlusion, 0.0, 0.92);
    return float4(occlusion, occlusion, occlusion, 1);
}
";
}

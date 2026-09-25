namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF2.4 temporal resolve (+ optional bilateral upsample) for raymarched clouds.
/// </summary>
/// <remarks>
/// Temporal resolve runs at the quality-ladder internal resolution after the every-frame
/// raymarch. History is neighbourhood-clamped and rejected on bad UV / motion / depth /
/// silhouette. Cinematic may run <c>PS_BilateralUpsample</c> to full-res before FogPost;
/// other tiers rely on FogPost's existing LinearClamp sample of the internal CloudMap.
/// </remarks>
internal static class CloudTemporalShaders
{
    public const string Source = @"
cbuffer CloudTemporalConstants : register(b0)
{
    float4 ClipPlanes;                      // x=near, y=far, z=internalW, w=internalH
    row_major float4x4 InvViewProjection;   // current pixel -> world
    row_major float4x4 PrevViewProjection;  // world -> previous clip
    float4 CameraPosPad;                    // xyz = camera world position
    float4 TemporalParams;                  // x=baseBlend, y=resetHistory, z=motionThresh, w=depthThresh
    float4 UpsampleParams;                  // xy=texel (1/srcW,1/srcH), z=enabled, w unused
};

Texture2D CurrentCloud : register(t0);
Texture2D HistoryCloud : register(t1);
Texture2D<float> SceneDepth : register(t2);
SamplerState LinearClamp : register(s0);
SamplerState PointClamp : register(s1);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = p;
    return o;
}

float3 ReconstructWorldPos(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 world = mul(float4(ndc, depth, 1.0), InvViewProjection);
    return world.xyz / max(world.w, 1e-5);
}

float2 ReprojectUv(float3 worldPos)
{
    float4 clip = mul(float4(worldPos, 1.0), PrevViewProjection);
    float invW = rcp(max(abs(clip.w), 1e-5));
    float2 ndc = clip.xy * invW;
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
}

bool UvInBounds(float2 uv)
{
    return uv.x >= 0.002 && uv.x <= 0.998 && uv.y >= 0.002 && uv.y <= 0.998;
}

float4 NeighbourhoodMinMax(float2 uv, float2 texel, out float4 nMax)
{
    float4 nMin = float4(1e9, 1e9, 1e9, 1e9);
    nMax = float4(-1e9, -1e9, -1e9, -1e9);
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float4 s = CurrentCloud.SampleLevel(PointClamp, uv + float2(x, y) * texel, 0);
            nMin = min(nMin, s);
            nMax = max(nMax, s);
        }
    }
    return nMin;
}

float4 PS_TemporalResolve(VSOut IN) : SV_Target
{
    float2 texel = float2(rcp(max(ClipPlanes.z, 1.0)), rcp(max(ClipPlanes.w, 1.0)));
    float4 current = CurrentCloud.SampleLevel(PointClamp, IN.uv, 0);

    if (TemporalParams.y > 0.5)
        return current;

    float depth = SceneDepth.SampleLevel(PointClamp, IN.uv, 0).r;
    float3 worldPos = ReconstructWorldPos(IN.uv, depth);
    // Far/sky pixels: anchor at a mid-slab distance along the view ray so clouds still reproject.
    if (depth >= 0.99999)
    {
        float3 worldFar = ReconstructWorldPos(IN.uv, 1.0);
        float3 ray = normalize(worldFar - CameraPosPad.xyz);
        worldPos = CameraPosPad.xyz + ray * 220.0;
    }

    float2 historyUv = ReprojectUv(worldPos);
    float motionLen = length(historyUv - IN.uv);

    float4 history = float4(0, 0, 0, 0);
    float alphaRange = 1.0;
    float depthDelta = 1.0;
    bool valid = UvInBounds(historyUv);

    if (valid)
    {
        history = HistoryCloud.SampleLevel(LinearClamp, historyUv, 0);
        float4 nMax;
        float4 nMin = NeighbourhoodMinMax(IN.uv, texel, nMax);
        history = float4(
            clamp(history.rgb, nMin.rgb, nMax.rgb),
            clamp(history.a, nMin.a, nMax.a));
        alphaRange = nMax.a - nMin.a;

        float histDepth = SceneDepth.SampleLevel(PointClamp, historyUv, 0).r;
        depthDelta = abs(histDepth - depth);
    }

    float baseBlend = TemporalParams.x;
    float motionThresh = TemporalParams.z;
    float depthThresh = TemporalParams.w;
    float weight = 0.0;
    if (valid && motionLen <= motionThresh && depthDelta <= depthThresh)
    {
        weight = saturate(baseBlend);
        if (alphaRange > 0.05)
        {
            float t = saturate((alphaRange - 0.05) / 0.10);
            weight *= 1.0 - 0.75 * t;
        }
    }

    return lerp(current, history, weight);
}

// 3×3 alpha-aware bilateral upsample from internal cloud RT toward the destination UV grid.
// Used when CloudQuality is Cinematic; other tiers leave FogPost's bilinear sample alone.
float4 PS_BilateralUpsample(VSOut IN) : SV_Target
{
    float2 texel = UpsampleParams.xy;
    float4 center = CurrentCloud.SampleLevel(LinearClamp, IN.uv, 0);
    float centerA = center.a;
    float3 accumRgb = 0;
    float accumA = 0;
    float wSum = 0;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 uv = IN.uv + float2(x, y) * texel;
            float4 s = CurrentCloud.SampleLevel(LinearClamp, uv, 0);
            float wa = exp(-abs(s.a - centerA) * 12.0);
            float ws = (x == 0 && y == 0) ? 1.0 : 0.55;
            float w = wa * ws;
            accumRgb += s.rgb * w;
            accumA += s.a * w;
            wSum += w;
        }
    }

    float inv = rcp(max(wSum, 1e-5));
    return float4(accumRgb * inv, saturate(accumA * inv));
}
";
}

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.2 half-resolution GTAO + depth-aware bilateral blur (forward-compatible screen pass).
/// Depth-derived normals — no deferred G-buffer.
/// </summary>
internal static class GtaoShaders
{
    public const string Source = @"
cbuffer GtaoConstants : register(b0)
{
    float4 ClipPlanes;                 // x=near, y=far, z=fullWidth, w=fullHeight
    row_major float4x4 InvProjection;  // camera projection inverse
    float4 Params;                     // x=time, y=horizontal blur flag, zw unused
};

Texture2D<float> SceneDepth : register(t0);
Texture2D        AoMap      : register(t1);
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

float LinearizeDepth(float depth)
{
    float z = depth * 2.0 - 1.0;
    float n = max(ClipPlanes.x, 0.01);
    float f = max(n + 1.0, ClipPlanes.y);
    return (2.0 * n * f) / max(f + n - z * (f - n), 1e-5);
}

float3 ReconstructViewPos(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 clip = float4(ndc, depth, 1.0);
    float4 view = mul(clip, InvProjection);
    return view.xyz / max(view.w, 1e-5);
}

float3 ReconstructNormal(float2 uv, float2 texel)
{
    float3 c = ReconstructViewPos(uv, SceneDepth.SampleLevel(LinearClamp, uv, 0).r);
    float3 px = ReconstructViewPos(uv + float2(texel.x, 0),
        SceneDepth.SampleLevel(LinearClamp, uv + float2(texel.x, 0), 0).r) - c;
    float3 py = ReconstructViewPos(uv + float2(0, texel.y),
        SceneDepth.SampleLevel(LinearClamp, uv + float2(0, texel.y), 0).r) - c;
    return normalize(cross(py, px));
}

float HashIGN(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float4 PS_Gtao(VSOut IN) : SV_Target
{
    float2 fullRes = max(ClipPlanes.zw, float2(2, 2));
    float2 texel = 1.0 / fullRes;
    float centerDepth = SceneDepth.SampleLevel(LinearClamp, IN.uv, 0).r;
    if (centerDepth >= 0.99999)
        return float4(1, 1, 1, 1);

    float3 center = ReconstructViewPos(IN.uv, centerDepth);
    float3 normal = ReconstructNormal(IN.uv, texel);
    float viewDepth = max(0.1, abs(center.z));
    float projectedRadius = clamp(28.0 / sqrt(viewDepth + 0.35), 3.0, 52.0);
    float rotation = HashIGN(floor(IN.pos.xy)) * 6.2831853;

    float horizonSum = 0.0;
    float contactSum = 0.0;
    const int directionCount = 8;
    const int stepCount = 4;

    [loop]
    for (int directionIndex = 0; directionIndex < directionCount; directionIndex++)
    {
        float angle = rotation + (float(directionIndex) + 0.5) * 6.2831853 / float(directionCount);
        float2 direction = float2(cos(angle), sin(angle));
        float horizon = 0.0;
        float directionalContact = 0.0;

        [loop]
        for (int stepIndex = 1; stepIndex <= stepCount; stepIndex++)
        {
            float step01 = float(stepIndex) / float(stepCount);
            float pixelRadius = projectedRadius * (0.20 + 0.80 * step01 * step01);
            float2 sampleUv = IN.uv + direction * pixelRadius / fullRes;
            if (sampleUv.x <= 0.001 || sampleUv.x >= 0.999 || sampleUv.y <= 0.001 || sampleUv.y >= 0.999)
                continue;

            float sampleDepth = SceneDepth.SampleLevel(LinearClamp, sampleUv, 0).r;
            if (sampleDepth >= 0.99999)
                continue;

            float3 samplePosition = ReconstructViewPos(sampleUv, sampleDepth);
            float3 delta = samplePosition - center;
            float distanceSquared = dot(delta, delta);
            if (distanceSquared < 0.000004 || distanceSquared > 36.0)
                continue;

            float inverseDistance = rsqrt(distanceSquared);
            float distanceToSample = distanceSquared * inverseDistance;
            float3 sampleDirection = delta * inverseDistance;
            float normalHorizon = max(dot(normal, sampleDirection) - 0.045, 0.0);
            float falloff = 1.0 - smoothstep(0.30, 6.0, distanceToSample);
            horizon = max(horizon, normalHorizon * falloff);

            if (stepIndex <= 2)
            {
                float contactFalloff = 1.0 - smoothstep(0.035, 1.10, distanceToSample);
                directionalContact = max(directionalContact, normalHorizon * contactFalloff);
            }
        }

        horizonSum += horizon;
        contactSum += directionalContact;
    }

    float horizonOcclusion = horizonSum / float(directionCount);
    float contactOcclusion = contactSum / float(directionCount);
    float distanceFade = 1.0 - smoothstep(70.0, 150.0, viewDepth);
    float ao = 1.0 - (horizonOcclusion * 1.55 + contactOcclusion * 0.95) * distanceFade;
    ao = clamp(ao, 0.055, 1.0);
    ao = pow(ao, 1.18);
    return float4(ao, ao, ao, 1);
}

float4 PS_Blur(VSOut IN) : SV_Target
{
    float2 aoSize = max(ClipPlanes.zw * 0.5, float2(2, 2));
    float2 aoTexel = 1.0 / aoSize;
    float2 axis = Params.y > 0.5 ? float2(aoTexel.x, 0) : float2(0, aoTexel.y);
    float centerDepth = LinearizeDepth(SceneDepth.SampleLevel(LinearClamp, IN.uv, 0).r);
    float weights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };
    float sum = AoMap.SampleLevel(LinearClamp, IN.uv, 0).r * weights[0];
    float total = weights[0];

    [unroll]
    for (int i = 1; i < 5; i++)
    {
        float2 offset = axis * float(i);
        float2 uvA = IN.uv + offset;
        float2 uvB = IN.uv - offset;
        float depthA = LinearizeDepth(SceneDepth.SampleLevel(LinearClamp, uvA, 0).r);
        float depthB = LinearizeDepth(SceneDepth.SampleLevel(LinearClamp, uvB, 0).r);
        float weightA = weights[i] * exp(-abs(depthA - centerDepth) * 2.8);
        float weightB = weights[i] * exp(-abs(depthB - centerDepth) * 2.8);
        sum += AoMap.SampleLevel(LinearClamp, uvA, 0).r * weightA;
        sum += AoMap.SampleLevel(LinearClamp, uvB, 0).r * weightB;
        total += weightA + weightB;
    }

    float ao = sum / max(total, 0.0001);
    return float4(ao, ao, ao, 1);
}
";
}

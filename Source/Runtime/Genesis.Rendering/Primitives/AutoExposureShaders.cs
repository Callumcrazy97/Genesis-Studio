namespace Genesis.Rendering.Primitives;

/// <summary>
/// Request 64b: eye adaptation. Three tiny pixel-shader passes, so every backend runs them alike:
/// <c>PS_Meter</c> takes the log of the HDR scene's luminance into a 64×32 grid (sixteen taps a
/// cell, the picture's centre weighted, engine-sky pixels replaced by the sky's own radiance since
/// the composite draws the sky later), <c>PS_Reduce</c> averages that to 8×4, and <c>PS_Adapt</c>
/// averages the rest, limits it, and moves last frame's adapted value towards it (an R32F texel,
/// ping-ponged). The composite (<see cref="FogPostShaders"/>) divides the key by it. The GUI and
/// held view models are drawn after the composite, so they are never metered.
/// </summary>
internal static class AutoExposureShaders
{
    public const string Source = @"
cbuffer AutoExposureConstants : register(b0)
{
    // x = 1 / width, y = 1 / height of the target's source in uv cells (meter) or texels (reduce),
    // z = centre weight (0 even, 1 centre), w = 1 when engine-sky pixels use the sky radiance below.
    float4 MeterParams;
    // x = seconds since the last adaptation, y = darken seconds, z = brighten seconds, w = 1 to jump.
    float4 AdaptParams;
    // x = log2 key, y = min EV, z = max EV, w = scene depth is reversed.
    float4 TargetParams;
    // xy = full-resolution scene size in pixels.
    float4 SourceSize;
    row_major float4x4 InvViewProjection;
    float4 CameraPosition;
    float4 SkyZenith;
    float4 SkyHorizon;
};

Texture2D SourceMap : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D<float> PreviousState : register(t2);
SamplerState LinearClamp : register(s0);

#define GENESIS_DEPTH_REVERSED (TargetParams.w > 0.5)
" + SceneDepthHlsl.Helpers + @"

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = p;
    return o;
}

// The drawn sky's radiance along the view ray through uv (the composite's horizon-to-zenith curve).
float3 SkyRadianceAt(float2 uv)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 world = mul(float4(ndc, SceneDepthFar(), 1.0), InvViewProjection);
    float3 ray = normalize(world.xyz / max(world.w, 1e-4) - CameraPosition.xyz);
    return lerp(SkyHorizon.rgb, SkyZenith.rgb, pow(max(ray.y, 1e-4), 0.45));
}

float4 PS_Meter(VSOut IN) : SV_Target
{
    float2 cell = MeterParams.xy;
    float2 origin = IN.uv - cell * 0.5;
    float sumLog = 0.0;
    float sumWeight = 0.0;
    [unroll] for (int j = 0; j < 4; j++)
    {
        [unroll] for (int i = 0; i < 4; i++)
        {
            float2 uv = origin + cell * ((float2(i, j) + 0.5) * 0.25);
            float3 c = SourceMap.SampleLevel(LinearClamp, uv, 0).rgb;
            if (MeterParams.w > 0.5)
            {
                int2 pixel = int2(min(uv * SourceSize.xy, SourceSize.xy - 1.0));
                if (SceneDepthIsSky(SceneDepth.Load(int3(pixel, 0))))
                    c = SkyRadianceAt(uv);
            }
            float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
            lum = lum > 1e-5 ? min(lum, 65000.0) : 1e-5;    // also turns NaN into black
            float2 d = uv - 0.5;
            float weight = lerp(1.0, saturate(1.0 - dot(d, d) * 3.2), MeterParams.z) + 0.02;
            sumLog += log2(lum) * weight;
            sumWeight += weight;
        }
    }
    return float4(sumLog * 0.0625, sumWeight * 0.0625, 0.0, 1.0);
}

// 8x8 source texels to one: sixteen bilinear taps, each the mean of a 2x2 quad.
float4 PS_Reduce(VSOut IN) : SV_Target
{
    float2 texel = MeterParams.xy;
    float2 origin = IN.uv - texel * 4.0;
    float2 sum = float2(0.0, 0.0);
    [unroll] for (int j = 0; j < 4; j++)
    {
        [unroll] for (int i = 0; i < 4; i++)
        {
            sum += SourceMap.SampleLevel(LinearClamp, origin + texel * (float2(i, j) * 2.0 + 1.0), 0).rg;
        }
    }
    return float4(sum * 0.0625, 0.0, 1.0);
}

// The 8x4 grid to the scene's weighted log-average luminance, then last frame's adapted value
// moved towards it. Exposure = key / exp2(adapted), so limiting the target keeps the exposure
// between 2^minEv and 2^maxEv without the adapted value running away past the limit.
float4 PS_Adapt(VSOut IN) : SV_Target
{
    float2 sum = float2(0.0, 0.0);
    [unroll] for (int y = 0; y < 4; y++)
    {
        [unroll] for (int x = 0; x < 8; x++)
        {
            sum += SourceMap.Load(int3(x, y, 0)).rg;
        }
    }
    float sceneLog = sum.x / max(sum.y, 1e-6);
    float target = clamp(sceneLog, TargetParams.x - TargetParams.z, TargetParams.x - TargetParams.y);
    float previous = PreviousState.Load(int3(0, 0, 0));
    if (AdaptParams.w > 0.5 || !(abs(previous) < 64.0))
        return float4(target, 0.0, 0.0, 1.0);
    float seconds = target > previous ? AdaptParams.y : AdaptParams.z;
    // About 95% of the way in the given seconds (three time constants).
    float blend = seconds > 1e-4 ? 1.0 - exp(-3.0 * AdaptParams.x / seconds) : 1.0;
    return float4(previous + (target - previous) * blend, 0.0, 0.0, 1.0);
}
";
}

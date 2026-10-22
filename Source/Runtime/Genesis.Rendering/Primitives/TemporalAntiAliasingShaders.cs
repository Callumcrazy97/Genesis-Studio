namespace Genesis.Rendering.Primitives;

/// <summary>
/// Temporal anti-aliasing: the frame, drawn from a camera nudged by under a pixel, is blended into
/// the image built up from the frames before it. Each pixel takes the history from where its point
/// was last frame (worked out from the depth buffer and last frame's camera), kept within the
/// range of colours around the pixel now, so a moving thing or a newly uncovered one does not
/// leave a trail; off the last frame's edges, and on the first frame, the pixel is the new frame's.
/// </summary>
/// <remarks>
/// Runs where FXAA and SMAA do: over the tonemapped, display-encoded image, before the held items
/// (drawn afterwards, so they stay sharp), a project's post effects and the GUI. Points are moved
/// by the camera's motion only; something that moves by itself is held to its neighbourhood's
/// colours instead. Pixels are read by integer position, as the other passes read them.
/// </remarks>
internal static class TemporalAntiAliasingShaders
{
    public const string Source = @"
cbuffer TemporalConstants : register(b0)
{
    // This frame's camera without the nudge, inverted, and last frame's (both with the depth convention the scene uses).
    row_major float4x4 InvViewProjection;
    row_major float4x4 PreviousViewProjection;
    // x = 1/width, y = 1/height, z = width, w = height.
    float4 Metrics;
    // x = the new frame's share of the blend, y = 1 when there is a history to use.
    float4 Params;
};

Texture2D ColorTex : register(t0);
Texture2D HistoryTex : register(t1);
Texture2D DepthTex : register(t2);
SamplerState LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = p;
    return o;
}

int2 PixelOf(float2 uv) { return clamp(int2(floor(uv * Metrics.zw)), int2(0, 0), int2(Metrics.zw) - int2(1, 1)); }

float3 ToYCoCg(float3 c) { return float3(dot(c, float3(0.25, 0.5, 0.25)), dot(c, float3(0.5, 0.0, -0.5)), dot(c, float3(-0.25, 0.5, -0.25))); }
float3 FromYCoCg(float3 c) { return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

float4 PS_Taa(VSOut IN) : SV_Target
{
    int2 p = PixelOf(IN.uv);
    int2 last = int2(Metrics.zw) - int2(1, 1);
    float3 current = ColorTex.Load(int3(p, 0)).rgb;

    // The spread of colours around the pixel: the history is kept within it.
    float3 sum = 0.0, squares = 0.0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float3 c = ToYCoCg(ColorTex.Load(int3(clamp(p + int2(x, y), int2(0, 0), last), 0)).rgb);
            sum += c;
            squares += c * c;
        }
    }
    float3 mean = sum / 9.0;
    float3 spread = sqrt(max(squares / 9.0 - mean * mean, 0.0)) * 1.25;

    // Where this pixel's point was on last frame's image. Kept homogeneous: a point of the sky is
    // at infinity, and only the final divide may meet it.
    float depth = DepthTex.Load(int3(p, 0)).r;
    float2 uv = (float2(p) + 0.5) * Metrics.xy;
    float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0), InvViewProjection);
    float4 before = mul(world, PreviousViewProjection) * (world.w < 0.0 ? -1.0 : 1.0);
    if (Params.y < 0.5 || before.w <= 1e-6) return float4(current, 1.0);
    float2 previous = float2(before.x / before.w * 0.5 + 0.5, 0.5 - before.y / before.w * 0.5);
    if (any(previous < 0.0) || any(previous > 1.0)) return float4(current, 1.0);

    float3 history = ToYCoCg(HistoryTex.SampleLevel(LinearClamp, previous, 0).rgb);
    history = clamp(history, mean - spread, mean + spread);
    float3 blended = lerp(history, ToYCoCg(current), Params.x);
    return float4(saturate(FromYCoCg(blended)), 1.0);
}

// The built-up image, as it is, to the next stage.
float4 PS_TaaCopy(VSOut IN) : SV_Target
{
    return ColorTex.Load(int3(PixelOf(IN.uv), 0));
}
";
}

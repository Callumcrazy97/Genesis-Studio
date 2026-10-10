namespace Genesis.Rendering.Primitives;

/// <summary>
/// Camera motion blur: each pixel is smeared along the path its point in the world took across
/// the screen since the last frame, as the camera moved and turned. Runs on the tonemapped image
/// after the composite and before the held items, anti-aliasing, post effects and the GUI.
/// </summary>
/// <remarks>
/// Only the camera's own motion is blurred: the renderer keeps no velocity for moving objects, so
/// a running character is as sharp as the ground beside it would be. The sky is treated as far
/// away (it blurs as the camera turns, not as it walks). Mixing is in linear light.
/// </remarks>
internal static class MotionBlurShaders
{
    public const string Source = @"
cbuffer MotionBlurConstants : register(b0)
{
    row_major float4x4 InvViewProjection;   // this frame: pixel -> world
    row_major float4x4 PrevViewProjection;  // world -> last frame's clip space
    float4 Metrics;                         // 1/width, 1/height, width, height
    float4 Params;                          // x = shutter (0..1), y = longest blur in pixels, z = 1 when depth is reversed, w = samples
    float4 CameraPosition;                  // xyz = this frame's camera
};

Texture2D ColorTex : register(t0);
Texture2D<float> SceneDepth : register(t1);
SamplerState LinearClamp : register(s0);
#define GENESIS_DEPTH_REVERSED (Params.z > 0.5)
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

float3 ToLinear(float3 c)
{
    c = saturate(c);
    return lerp(pow(max((c + 0.055) / 1.055, 1e-7), 2.4), c / 12.92, step(c, 0.04045));
}

float3 ToDisplay(float3 c)
{
    c = saturate(c);
    return lerp(1.055 * pow(max(c, 1e-7), 1.0 / 2.4) - 0.055, c * 12.92, step(c, 0.0031308));
}

float4 PS_MotionBlur(VSOut IN) : SV_Target
{
    int2 p = clamp(int2(floor(IN.uv * Metrics.zw)), int2(0, 0), int2(Metrics.zw) - int2(1, 1));
    float4 centre = ColorTex.Load(int3(p, 0));
    float depth = SceneDepth.Load(int3(p, 0));
    float2 ndc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
    float3 world;
    if (SceneDepthIsSky(depth))
    {
        // The sky: far away along this pixel's ray, so walking does not blur it but turning does.
        float4 nearPoint = mul(float4(ndc, SceneDepthNear(), 1.0), InvViewProjection);
        float3 ray = normalize(nearPoint.xyz / nearPoint.w - CameraPosition.xyz);
        world = CameraPosition.xyz + ray * 10000.0;
    }
    else
    {
        float4 h = mul(float4(ndc, depth, 1.0), InvViewProjection);
        world = h.xyz / h.w;
    }

    float4 previous = mul(float4(world, 1.0), PrevViewProjection);
    if (previous.w <= 1e-4)
        return centre;
    float2 previousUv = float2(previous.x / previous.w * 0.5 + 0.5, 0.5 - previous.y / previous.w * 0.5);
    float2 motion = (IN.uv - previousUv) * Params.x;
    float blurPixels = length(motion * Metrics.zw);
    if (blurPixels < 0.5)
        return centre;
    if (blurPixels > Params.y)
        motion *= Params.y / blurPixels;

    // Samples spread over the shutter, centred on the pixel, offset a little per pixel so a long
    // blur shows grain rather than copies.
    int samples = (int)Params.w;
    float offset = frac(52.9829189 * frac(dot(float2(p), float2(0.06711056, 0.00583715))));
    float3 sum = ToLinear(centre.rgb);
    [loop]
    for (int i = 0; i < samples; i++)
    {
        float t = (i + offset) / samples - 0.5;
        sum += ToLinear(ColorTex.SampleLevel(LinearClamp, IN.uv + motion * t, 0).rgb);
    }
    return float4(ToDisplay(sum / (samples + 1)), centre.a);
}
";
}

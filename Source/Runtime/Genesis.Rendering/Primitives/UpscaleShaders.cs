namespace Genesis.Rendering.Primitives;

/// <summary>
/// Render scale (ForwardRenderer.RenderScale): the frame drawn at a share of the view's size is
/// stretched onto it, then sharpened a little where the picture is flat enough to take it (a
/// contrast-adaptive sharpen over the four nearest pixels), so the edges softened by the stretch
/// read crisp again without ringing.
/// </summary>
internal static class UpscaleShaders
{
    public static string Source => @"
cbuffer UpscaleConstants : register(b0)
{
    float4 Source;   // x = 1 / width, y = 1 / height of the drawn frame, z = sharpening 0..1
};

Texture2D    Frame       : register(t0);
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

float4 PS_Upscale(VSOut IN) : SV_Target
{
    float4 centre = Frame.SampleLevel(LinearClamp, IN.uv, 0);
    if (Source.z <= 0.0) return centre;
    float3 up    = Frame.SampleLevel(LinearClamp, IN.uv + float2(0.0, -Source.y), 0).rgb;
    float3 down  = Frame.SampleLevel(LinearClamp, IN.uv + float2(0.0,  Source.y), 0).rgb;
    float3 left  = Frame.SampleLevel(LinearClamp, IN.uv + float2(-Source.x, 0.0), 0).rgb;
    float3 right = Frame.SampleLevel(LinearClamp, IN.uv + float2( Source.x, 0.0), 0).rgb;
    float3 lowest  = min(centre.rgb, min(min(up, down), min(left, right)));
    float3 highest = max(centre.rgb, max(max(up, down), max(left, right)));
    // Less where the neighbourhood already has contrast: no halos round hard edges.
    float3 room = saturate(min(lowest, 1.0 - highest) / max(highest, 1e-4));
    float3 lobe = -sqrt(room) * lerp(0.08, 0.2, Source.z);
    float3 sharpened = (centre.rgb + (up + down + left + right) * lobe) / (1.0 + 4.0 * lobe);
    return float4(max(sharpened, 0.0), centre.a);
}
";
}

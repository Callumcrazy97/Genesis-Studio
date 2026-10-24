namespace Genesis.Rendering.Primitives;

/// <summary>
/// Depth of field (ForwardRenderer.DepthOfField): each pixel of the HDR scene is blurred by its
/// circle of confusion, how far its depth is outside the sharp range round the focus distance.
/// A gather over a golden-angle disc where a neighbour counts only when its own blur reaches this
/// pixel, so sharp things in front do not smear over a blurred background and the reverse.
/// </summary>
internal static class DepthOfFieldShaders
{
    public static string Source => @"
cbuffer DepthOfFieldConstants : register(b0)
{
    float4 Focus;   // x = focus distance, y = sharp range either side, z = largest blur in pixels, w = 1 for auto focus
    float4 Planes;  // x = near, y = far, z = 1 / width, w = 1 / height
    float4 Flags;   // x = scene depth is reversed
};

Texture2D        SceneColor : register(t0);
Texture2D<float> SceneDepth : register(t1);
SamplerState     LinearClamp : register(s0);

#define GENESIS_DEPTH_REVERSED (Flags.x > 0.5)
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

float ViewDepthAt(float2 uv)
{
    int2 size = int2(round(1.0 / Planes.zw));
    int2 pixel = clamp(int2(uv * size), int2(0, 0), size - 1);
    float d = SceneDepth.Load(int3(pixel, 0));
    return SceneDepthIsSky(d) ? Planes.y : SceneDepthToView(d, Planes.x, Planes.y);
}

// 0 inside the sharp range, rising to 1 one range further out (or at once for no range).
float BlurAt(float2 uv, float focus)
{
    float outside = abs(ViewDepthAt(uv) - focus) - Focus.y;
    return saturate(outside / max(Focus.y, 0.25 + focus * 0.1));
}

float4 PS_DepthOfField(VSOut IN) : SV_Target
{
    float4 centre = SceneColor.SampleLevel(LinearClamp, IN.uv, 0);
    float focus = Focus.w > 0.5 ? ViewDepthAt(float2(0.5, 0.5)) : Focus.x;
    float radius = BlurAt(IN.uv, focus) * Focus.z;
    if (radius < 0.5) return centre;
    float3 sum = centre.rgb;
    float weight = 1.0;
    const int Taps = 24;
    [loop] for (int i = 0; i < Taps; i++)
    {
        float r = sqrt((i + 0.5) / Taps) * radius;
        float a = i * 2.39996323;
        float2 uv = IN.uv + float2(cos(a), sin(a)) * r * Planes.zw;
        // A neighbour counts only when its own blur reaches this far: sharp edges stay sharp.
        float reach = BlurAt(uv, focus) * Focus.z;
        float w = saturate(reach - r + 1.0);
        sum += SceneColor.SampleLevel(LinearClamp, uv, 0).rgb * w;
        weight += w;
    }
    return float4(sum / weight, centre.a);
}
";
}

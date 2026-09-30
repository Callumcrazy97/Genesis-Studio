namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.7 half-resolution HDR bloom pyramid (threshold extract + downsample + upsample).
/// Writes an HDR bloom buffer that FogPostShaders.cs adds before exposure/grading/ACES.
/// </summary>
/// <remarks>
/// v1 samples the main-pass scene colour (HDR pre-fog). Intermediate HDR after fog is heavier on
/// DX11 and is deferred; document that choice in the AF1.7 README entry. Four mips starting at
/// half-res; skipped entirely when bloom is off (and always on the Software backend).
/// </remarks>
internal static class BloomShaders
{
    public const string Source = @"
cbuffer BloomConstants : register(b0)
{
    // xy = source texel size (1/width, 1/height), z = luma threshold (extract pass),
    // w = 1 when FineMap should be added during upsample, 2 when FineMap is the GPU particle
    // layer to composite over the scene before extracting.
    float4 Params;
};

Texture2D SourceMap : register(t0);
Texture2D FineMap   : register(t1);
SamplerState LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    // Every pyramid level keeps the source orientation; the clip transform
    // above already handles Y, as it does in the final post composite.
    o.uv = p;
    return o;
}

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// One source tap; in the extract pass with the particle layer, the layer goes over the scene.
float3 SourceTap(float2 uv)
{
    float3 color = SourceMap.SampleLevel(LinearClamp, uv, 0).rgb;
    if (Params.w > 1.5)
    {
        float4 particles = FineMap.SampleLevel(LinearClamp, uv, 0);
        color = color * (1.0 - saturate(particles.a)) + particles.rgb;
    }
    return color;
}

// Dual-filter style 13-tap downsample (Karis / Call of Duty).
float3 Downsample13(float2 uv, float2 texel)
{
    float3 a = SourceTap(uv + texel * float2(-1.0, -1.0));
    float3 b = SourceTap(uv + texel * float2( 0.0, -1.0));
    float3 c = SourceTap(uv + texel * float2( 1.0, -1.0));
    float3 d = SourceTap(uv + texel * float2(-1.0,  0.0));
    float3 e = SourceTap(uv);
    float3 f = SourceTap(uv + texel * float2( 1.0,  0.0));
    float3 g = SourceTap(uv + texel * float2(-1.0,  1.0));
    float3 h = SourceTap(uv + texel * float2( 0.0,  1.0));
    float3 i = SourceTap(uv + texel * float2( 1.0,  1.0));
    float3 j = SourceTap(uv + texel * float2(-2.0,  0.0));
    float3 k = SourceTap(uv + texel * float2( 2.0,  0.0));
    float3 l = SourceTap(uv + texel * float2( 0.0, -2.0));
    float3 m = SourceTap(uv + texel * float2( 0.0,  2.0));
    return e * 0.125
         + (a + c + g + i) * 0.03125
         + (b + d + f + h) * 0.0625
         + (j + k + l + m) * 0.125 * 0.5;
}

// 9-tap tent upsample.
float3 Upsample9(float2 uv, float2 texel)
{
    float3 color = SourceMap.SampleLevel(LinearClamp, uv, 0).rgb * 4.0;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2( texel.x, 0.0), 0).rgb * 2.0;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2(-texel.x, 0.0), 0).rgb * 2.0;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2(0.0,  texel.y), 0).rgb * 2.0;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2(0.0, -texel.y), 0).rgb * 2.0;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2( texel.x,  texel.y), 0).rgb;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2(-texel.x,  texel.y), 0).rgb;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2( texel.x, -texel.y), 0).rgb;
    color += SourceMap.SampleLevel(LinearClamp, uv + float2(-texel.x, -texel.y), 0).rgb;
    return color * (1.0 / 16.0);
}

// First half-res pass: downsample the HDR scene and keep only bright energy.
float4 PS_ThresholdExtract(VSOut IN) : SV_Target
{
    float2 texel = Params.xy;
    float3 color = Downsample13(IN.uv, texel);
    float threshold = Params.z;
    float luma = Luma(color);
    if (luma <= threshold)
        return float4(0, 0, 0, 1);
    float contribution = (luma - threshold) / max(luma, 1e-4);
    return float4(color * contribution, 1);
}

float4 PS_Downsample(VSOut IN) : SV_Target
{
    return float4(Downsample13(IN.uv, Params.xy), 1);
}

float4 PS_Upsample(VSOut IN) : SV_Target
{
    float3 color = Upsample9(IN.uv, Params.xy);
    if (Params.w > 0.5)
        color += FineMap.SampleLevel(LinearClamp, IN.uv, 0).rgb;
    return float4(color, 1);
}
";
}

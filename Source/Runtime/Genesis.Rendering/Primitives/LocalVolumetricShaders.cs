namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.5 half-resolution local-light volumetric scatter. Writes HDR
/// inscatter that FogPostShaders.cs *adds* to the composited scene — never a multiply-darken, so
/// the existing height fog, distance haze and CSM sun shafts are untouched by it.
/// </summary>
/// <remarks>
/// Bounded on purpose: at most <c>Params.z</c> (≤4) lights, at most 12 steps, over a short segment
/// of the view ray. This is not ForestLight's 24-step atmosphere and must never be run on a
/// 1-in-4 frame schedule — see LocalVolumetricMath's remarks.
/// </remarks>
internal static class LocalVolumetricShaders
{
    public const string Source = @"
cbuffer LocalVolumetricConstants : register(b0)
{
    float4 ClipPlanes;                      // x=near, y=far, z=fullWidth, w=fullHeight
    row_major float4x4 InvViewProjection;   // pixel -> world
    float4 CameraPosPad;                    // xyz = camera world position
    // x=step count, y=strength, z=selected light count, w=max ray distance (world units)
    float4 Params;
    // Strongest-N clustered lights, chosen on the CPU by OmniShadowMath.SelectSlots so the beam
    // and the AF1.3 cubemap always land on the same torch. Unused slots are left at zero radius.
    float4 LightPosRadius[4];
    float4 LightColorIntensity[4];
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

// Interleaved Gradient Noise from pixel coordinates only — deliberately no time term. A
// time-seeded jitter makes a beam this bright fizz and crawl frame to frame; a static per-pixel
// dither breaks the step banding while staying stable under sub-pixel camera motion.
float HashIGN(float2 pixel)
{
    pixel = floor(pixel);
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float4 PS_LocalVol(VSOut IN) : SV_Target
{
    float depth = SceneDepth.SampleLevel(LinearClamp, IN.uv, 0).r;
    if (depth >= 0.99999)
        return float4(0, 0, 0, 1);

    int lightCount = clamp(int(Params.z), 0, 4);
    if (lightCount <= 0)
        return float4(0, 0, 0, 1);

    float3 cameraPos = CameraPosPad.xyz;
    float3 worldPos = ReconstructWorldPos(IN.uv, depth);
    float3 toSurface = worldPos - cameraPos;
    float surfaceDistance = length(toSurface);
    if (surfaceDistance < 1e-4)
        return float4(0, 0, 0, 1);

    float3 viewRay = toSurface / surfaceDistance;
    // Depth-bounded: never march past the shaded surface, and never past the short local segment.
    float rayLength = min(surfaceDistance, max(Params.w, 0.0));
    if (rayLength <= 1e-4)
        return float4(0, 0, 0, 1);

    int stepCount = clamp(int(Params.x), 1, 12);
    float segment = rayLength / float(stepCount);
    float jitter = HashIGN(IN.pos.xy) - 0.5;
    float3 scatter = float3(0, 0, 0);

    [loop]
    for (int i = 0; i < stepCount; i++)
    {
        // Jitter the first step only. It is the sample nearest the camera, where the step spacing
        // is most visible as a ring; dithering it dissolves that without smearing the beam.
        float t = (float(i) + 0.5 + (i == 0 ? jitter * 0.5 : 0.0)) / float(stepCount);
        float3 samplePos = cameraPos + viewRay * (rayLength * t);

        [loop]
        for (int l = 0; l < lightCount; l++)
        {
            float3 lightPos = LightPosRadius[l].xyz;
            float radius = max(LightPosRadius[l].w, 1e-3);
            float intensity = max(LightColorIntensity[l].w, 0.0);
            float falloff = saturate(1.0 - distance(samplePos, lightPos) / radius);
            // Matches LocalVolumetricMath: falloff^2 * DensityScale, integrated over the segment.
            scatter += LightColorIntensity[l].rgb * (intensity * falloff * falloff * 0.25 * segment);
        }
    }

    scatter = min(scatter * max(Params.y, 0.0), float3(4.0, 4.0, 4.0));
    return float4(scatter, 1);
}
";
}

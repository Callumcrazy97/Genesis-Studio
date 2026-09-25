namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF2.3 half-resolution raymarched cloud pass. Writes premultiplied HDR inscatter + opacity
/// that FogPost composites after LocalVol and before bloom. Density uses the WeatherMap RGBA8 upload.
/// </summary>
/// <remarks>
/// Runs every frame when enabled — never a 1-in-4 schedule. Software skips this program entirely
/// and keeps FogVolumes. Step budget is ≤48; empty coverage takes larger steps.
/// </remarks>
internal static class RaymarchedCloudsShaders
{
    public const string Source = @"
cbuffer RaymarchedCloudsConstants : register(b0)
{
    float4 ClipPlanes;                      // x=near, y=far, z=fullWidth, w=fullHeight
    row_major float4x4 InvViewProjection;   // pixel -> world
    float4 CameraPosPad;                    // xyz = camera world position
    float4 LightDirPad;                     // xyz = unit direction toward sun, w = illumination
    // x=cloudBase, y=thickness, z=step count, w=intensity
    float4 CloudParams;
    // x=worldHalfExtent, y=enabled, z=coverage scale, w=density
    float4 WeatherParams;
    float4 WeatherOrigin;                  // xy = grid world XZ centre, zw = texture size
    float4 CloudSunColor;                  // rgb=solar colour, w=authored lighting enabled
    float4 CloudAmbientColor;
};

Texture2D WeatherMap : register(t0);
Texture2D<float> SceneDepth : register(t1);
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

float3 ReconstructWorldPos(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 world = mul(float4(ndc, depth, 1.0), InvViewProjection);
    return world.xyz / max(world.w, 1e-5);
}

float HashIGN(float2 pixel)
{
    pixel = floor(pixel);
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float Hash3(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float ValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i + float3(0, 0, 0));
    float n100 = Hash3(i + float3(1, 0, 0));
    float n010 = Hash3(i + float3(0, 1, 0));
    float n110 = Hash3(i + float3(1, 1, 0));
    float n001 = Hash3(i + float3(0, 0, 1));
    float n101 = Hash3(i + float3(1, 0, 1));
    float n011 = Hash3(i + float3(0, 1, 1));
    float n111 = Hash3(i + float3(1, 1, 1));
    float nx00 = lerp(n000, n100, f.x);
    float nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x);
    float nx11 = lerp(n011, n111, f.x);
    float nxy0 = lerp(nx00, nx10, f.y);
    float nxy1 = lerp(nx01, nx11, f.y);
    return lerp(nxy0, nxy1, f.z);
}

float FbmDensity(float3 p, float footprint)
{
    float sum = 0.0;
    float amp = 0.5;
    float freq = 1.0;
    float norm = 0.0;
    [unroll]
    for (int o = 0; o < 3; o++)
    {
        float detail = 1.0 - smoothstep(0.5, 1.5, footprint * freq);
        float noise = detail > 0.0 ? ValueNoise(p * freq) : 0.5;
        sum += lerp(0.5, noise, detail) * amp;
        norm += amp;
        amp *= 0.5;
        freq *= 2.03;
    }
    return norm > 1e-5 ? sum / norm : 0.0;
}

float4 SampleWeather(float3 worldPos)
{
    float halfExtent = max(WeatherParams.x, 1.0);
    float2 uv = float2(
        (worldPos.x - WeatherOrigin.x + halfExtent) / (halfExtent * 2.0),
        (worldPos.z - WeatherOrigin.y + halfExtent) / (halfExtent * 2.0));
    // Outside the map → thin coverage so empty-space skip dominates.
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return float4(0.05, 0.3, 0.4, 0.2);
    float boundaryFade = 1.0 - smoothstep(0.85, 1.0, max(abs(uv.x * 2.0 - 1.0), abs(uv.y * 2.0 - 1.0)));
    float2 size = max(WeatherOrigin.zw, float2(1.0, 1.0));
    uv = (saturate(uv) * (size - 1.0) + 0.5) / size;
    float4 weather = WeatherMap.SampleLevel(LinearClamp, uv, 0);
    // CPU coverage is authored cover multiplied by a mean-0.5 spatial noise field.
    // Treat that field as modulation around the authored coverage, rather than halving
    // it again before thresholding the 3D cloud shape (which erased partly cloudy skies).
    float coverageNormalization = CloudSunColor.w > 0.5 ? 2.0 : 1.0;
    weather.r = saturate(weather.r * WeatherParams.z * coverageNormalization) * boundaryFade;
    return weather;
}

bool IntersectSlab(float3 origin, float3 dir, float y0, float y1, out float tEnter, out float tExit)
{
    tEnter = 0.0;
    tExit = 0.0;
    if (abs(dir.y) < 1e-5)
    {
        if (origin.y < y0 || origin.y > y1)
            return false;
        tEnter = 0.0;
        tExit = 1e6;
        return true;
    }
    float t0 = (y0 - origin.y) / dir.y;
    float t1 = (y1 - origin.y) / dir.y;
    tEnter = min(t0, t1);
    tExit = max(t0, t1);
    return tExit > max(tEnter, 0.0);
}

float SampleCloudDensity(float3 worldPos, float4 weather, float sampleSpacing)
{
    float coverage = saturate(weather.r);
    if (coverage < 0.16)
        return 0.0;

    float cloudBase = CloudParams.x;
    float thickness = max(CloudParams.y, 1e-3);
    float vertical = saturate(weather.a);
    float topY = cloudBase + thickness * (0.55 + 0.45 * vertical);
    if (worldPos.y < cloudBase || worldPos.y > topY)
        return 0.0;

    float h = (worldPos.y - cloudBase) / max(topY - cloudBase, 1e-3);
    float slab = pow(max(0.0, 1.0 - abs(h * 2.0 - 1.0)), 1.35);
    if (slab <= 1e-5)
        return 0.0;

    float erosion = saturate(weather.b);
    float cloudType = saturate(weather.g);
    float3 noisePos = worldPos * 0.018 + float3(cloudType * 3.1, 0.0, cloudType * 1.7);
    // Filter detail smaller than a march step. Near-horizontal rays otherwise turn
    // a bounded 24-step march into undersampled black speckles at the horizon.
    float noise = FbmDensity(noisePos, CloudSunColor.w > 0.5 ? sampleSpacing * 0.018 : 0.0);
    float threshold = CloudSunColor.w > 0.5 ? 0.62 - coverage * 0.4 : 1.0 - coverage * 0.92;
    float density = saturate((noise - threshold) / max(1.0 - threshold, 1e-3));
    density *= slab * coverage;
    float carve = saturate(1.0 - erosion * (0.35 + 0.55 * (1.0 - density)));
    return saturate(density * carve);
}

float4 PS_Clouds(VSOut IN) : SV_Target
{
    if (WeatherParams.y < 0.5)
        return float4(0, 0, 0, 0);

    int2 pixel = min(int2(IN.uv * ClipPlanes.zw), int2(ClipPlanes.zw) - 1);
    float depth = SceneDepth.Load(int3(pixel, 0)).r;
    float3 cameraPos = CameraPosPad.xyz;
    float3 worldFar = ReconstructWorldPos(IN.uv, 1.0);
    float3 viewRay = normalize(worldFar - cameraPos);

    float surfaceDist = 1e6;
    if (depth < 0.99999)
    {
        float3 worldPos = ReconstructWorldPos(IN.uv, depth);
        surfaceDist = length(worldPos - cameraPos);
    }

    float cloudBase = CloudParams.x;
    float thickness = max(CloudParams.y, 1e-3);
    float slabTop = cloudBase + thickness;
    float tEnter, tExit;
    if (!IntersectSlab(cameraPos, viewRay, cloudBase, slabTop, tEnter, tExit))
        return float4(0, 0, 0, 0);

    tEnter = max(tEnter, 0.0);
    tExit = min(tExit, surfaceDist);
    if (tExit <= tEnter + 1e-3)
        return float4(0, 0, 0, 0);

    int maxSteps = clamp(int(CloudParams.z), 1, 48);
    float densityScale = max(WeatherParams.w, 0.0);
    float intensity = densityScale > 0.0 ? max(CloudParams.w, 0.0) / densityScale : 0.0;
    float3 lightDir = normalize(LightDirPad.xyz);
    float jitter = HashIGN(IN.pos.xy) - 0.5;

    float3 scatter = float3(0, 0, 0);
    float opacity = 0.0;
    float t = tEnter;
    float rayLen = tExit - tEnter;
    float baseStep = rayLen / float(maxSteps);

    [loop]
    for (int i = 0; i < maxSteps; i++)
    {
        if (t >= tExit || opacity > 0.97)
            break;

        float3 samplePos = cameraPos + viewRay * (t + baseStep * (0.5 + jitter * 0.35));
        float4 weather = SampleWeather(samplePos);
        float coverage = weather.r;

        // Empty-space: larger steps when coverage is low.
        float stepLen = coverage < 0.16 ? baseStep * 2.5 : baseStep;
        float density = SampleCloudDensity(samplePos, weather, stepLen);
        if (density > 1e-4)
        {
            float optical = density * densityScale * stepLen * 0.085;
            float absorb = 1.0 - exp(-optical);
            float powder = 1.0 - exp(-density * 2.0);
            float phase = 0.55 + 0.45 * saturate(dot(viewRay, lightDir));
            float3 lightColor = float3(0.92, 0.95, 1.0) * phase;
            float3 ambient = float3(0.55, 0.62, 0.78) * 0.35;
            float3 lighting = (lightColor * powder + ambient) * LightDirPad.w;
            if (CloudSunColor.w > 0.5)
            {
                float height = saturate((samplePos.y - cloudBase) / thickness);
                float selfShadow = exp(-density * thickness * (1.0 - height) * 0.025);
                float3 skyAmbient = max(CloudAmbientColor.rgb * 0.3, float3(0.012, 0.016, 0.028));
                skyAmbient += float3(0.3, 0.32, 0.35) * LightDirPad.w;
                lighting = skyAmbient + CloudSunColor.rgb * LightDirPad.w * phase * selfShadow;
            }
            float3 inscatter = lighting * absorb * (1.0 - opacity);
            scatter += inscatter;
            opacity += absorb * (1.0 - opacity);
        }

        t += stepLen;
    }

    scatter = min(scatter * intensity, float3(4.0, 4.0, 4.0));
    return float4(scatter, saturate(opacity));
}
";
}

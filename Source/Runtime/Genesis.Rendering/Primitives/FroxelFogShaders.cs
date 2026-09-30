namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Froxel volumetric fog. The volume is a W × (H·D) RGBA16F atlas: slice k of a camera-aligned
    /// grid occupies rows [k·H, (k+1)·H). Three fullscreen passes build it each frame:
    /// <list type="bullet">
    /// <item><c>PS_FroxelInject</c>: density (height fog, noise, placed fog volumes) and in-scattered
    /// light (sun through the cascaded shadow maps, clustered point/spot lights through the local
    /// shadow atlas) per froxel — rgb = σs·L, a = σt.</item>
    /// <item><c>PS_FroxelTemporal</c>: blends with last frame's reprojected volume.</item>
    /// <item><c>PS_FroxelIntegrate</c>: front-to-back accumulation — rgb = in-scatter,
    /// a = transmittance from the camera to each slice's far edge.</item>
    /// </list>
    /// <see cref="ApplySource"/> is appended to every shader that fogs a surface (forward, water, post
    /// composite): two bilinear taps between neighbouring slices, plus a closed-form height-fog far
    /// field beyond the volume. <see cref="FroxelFogMath"/> is the CPU reference.
    /// </summary>
    internal static class FroxelFogShaders
    {
        public const string Source = @"
cbuffer PerFrameConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 LightViewProjection;
    row_major float4x4 LightViewProjectionNear;
    float4             CameraPosTime;
    row_major float4x4 LightViewProjectionMid;
};

struct PointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    float Falloff; float _pad0; float _pad1; float _pad2;
};

// Same layout as ForwardShaders' ClusterPointLight (and the C# ClusterPointLightGpu).
struct ClusterPointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    float Falloff; float _pad0; float _pad1; float _pad2;
    float3 SpotDir; float SpotCosOuter;
};

struct FogVolumeGpu { float4 CenterDensity; float4 ExtentsFalloff; float4 ColorShape; float4 KindDirection; };

// A prefix of the C# EngineCB, exactly as ForwardShaders declares it.
cbuffer EngineConstants : register(b1)
{
    float4     LightDirEnabled;
    float4     FogParams;
    float4     FogColor;
    float4     AmbientColor;
    float4     AmbientGroundColor;
    float4     SunColorIntensity;
    float4     FogParams2;
    float4     ShadowParams;
    float4     EffectParams;
    float4     VolumetricParams;
    float4     ViewportParams;
    float4     ShadowCascadeParams;
    float4     PointLightCounts;
    PointLight PointLights[8];
};

cbuffer FroxelConstants : register(b2)
{
    row_major float4x4 FroxelInvViewProjection;
    row_major float4x4 FroxelPrevViewProjection;
    float4             FroxelGrid;     // x = width, y = height, z = slices, w = far depth
    float4             FroxelCamera;   // xyz = camera position, w = near depth
    float4             FroxelForward;  // xyz = camera forward, w = fog volume count
    float4             FroxelJitter;   // xyz = cell jitter, w = history weight (0 = no history)
    float4             FroxelLighting; // x = local light scatter, y = shadowed floor, z = horizon boost
};

cbuffer LocalShadowConstants : register(b4)
{
    row_major float4x4 LocalShadowFaceVP[24];
    float4             LocalShadowSlots[4];
    float4             LocalShadowKinds;
    float4             LocalShadowParams;
};

Texture2D                           FroxelSource     : register(t0);
Texture2D                           FroxelHistory    : register(t1);
Texture2D<float>                    ShadowMapFar     : register(t2);
Texture2D<float>                    ShadowMapNear    : register(t5);
StructuredBuffer<ClusterPointLight> ClusterLights    : register(t6);
StructuredBuffer<FogVolumeGpu>      FroxelFogVolumes : register(t7);
StructuredBuffer<uint>              TileLightIndices : register(t13);
Texture2D<float>                    ShadowMapMid     : register(t14);
Texture2D<float>                    LocalShadowAtlas : register(t15);
SamplerState                        LinearClamp      : register(s0);
SamplerComparisonState              ShadowSamp       : register(s1);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = p;
    return o;
}

// ── Grid ─────────────────────────────────────────────────────────────────────

float FroxelSliceDepth(float slice)
{
    return FroxelCamera.w * pow(FroxelGrid.w / FroxelCamera.w, slice / FroxelGrid.z);
}

float FroxelSliceCoordinate(float depth)
{
    return clamp(log2(max(depth, 1e-4) / FroxelCamera.w) / log2(FroxelGrid.w / FroxelCamera.w) * FroxelGrid.z,
        0.0, FroxelGrid.z);
}

// Unit view ray through a screen uv.
float3 FroxelRay(float2 screenUv)
{
    float2 ndc = float2(screenUv.x * 2.0 - 1.0, 1.0 - screenUv.y * 2.0);
    float4 farH = mul(float4(ndc, 1.0, 1.0), FroxelInvViewProjection);
    return normalize(farH.xyz / farH.w - FroxelCamera.xyz);
}

// Atlas pixel → (column, row, slice).
int3 FroxelCell(float2 pixel)
{
    int height = (int)FroxelGrid.y;
    int y = (int)pixel.y;
    int slice = y / height;
    return int3((int)pixel.x, y - slice * height, slice);
}

float3 FroxelWorldPosition(float3 ray, float depth)
{
    return FroxelCamera.xyz + ray * (depth / max(dot(ray, FroxelForward.xyz), 0.05));
}

// ── Density (same model the post composite marched before froxels) ───────────

float Hash21(float2 p)
{
    uint n = asuint(floor(p.x)) * 1597334677u ^ asuint(floor(p.y)) * 3812015801u;
    n ^= n >> 16u;
    n *= 0x7feb352du;
    n ^= n >> 15u;
    n *= 0x846ca68bu;
    n ^= n >> 16u;
    return n * 2.3283064365386963e-10;
}

float NoiseXZ(float2 xz)
{
    float2 uv = xz * 0.035;
    float2 i = floor(uv);
    float2 f = frac(uv);
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + float2(1.0, 0.0));
    float c = Hash21(i + float2(0.0, 1.0));
    float d = Hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float FogDensityAt(float3 worldPos)
{
    float fogDensity = FogParams.w;
    float noiseAmp   = saturate(EffectParams.w) * fogDensity;
    float density    = fogDensity + (NoiseXZ(worldPos.xz) - 0.5) * noiseAmp;
    // Exponential height fog: densest at and below the base height, thinning with altitude.
    float heightMul = exp(-max(FogParams2.y, 0.0001) * max(worldPos.y - FogParams2.x, 0.0));
    density *= lerp(1.0, heightMul, saturate(FogParams2.z));
    return max(density, 0.0);
}

// Placed fog volume shapes: 0=Box, 1=Sphere, 2=Ellipsoid, 3=HeightSlab, 4=Cone.
float FogVolumeInsideFactor(float3 localPos, float3 extents, int shape, float3 direction)
{
    float3 e = max(extents, 0.0001);
    if (shape == 1)
        return saturate(1.0 - length(localPos) / e.x);
    if (shape == 3)
        return saturate(1.0 - abs(localPos.y) / e.y);
    if (shape == 4)
    {
        // Cones carry parameters in Extents: x = length, y = source radius, z = end radius.
        float3 axisDirection = dot(direction, direction) > 0.000001 ? normalize(direction) : float3(1.0, 0.0, 0.0);
        float axial = dot(localPos, axisDirection);
        float shaftLength = max(e.x, 0.0001);
        if (axial <= 0.0 || axial >= shaftLength)
            return 0.0;
        float along = saturate(axial / shaftLength);
        float radius = max(lerp(e.y, e.z, along), 0.0001);
        float sideFade = saturate(1.0 - length(localPos - axisDirection * axial) / radius);
        float sourceFade = saturate(axial / max(shaftLength * 0.08, 0.02));
        float endFade = saturate((shaftLength - axial) / max(shaftLength * 0.18, 0.02));
        return sideFade * min(sourceFade, endFade);
    }
    float3 n = localPos / e;
    if (shape == 2)
        return saturate(1.0 - length(n));
    return saturate(1.0 - max(max(abs(n.x), abs(n.y)), abs(n.z)));
}

float ComputeFogVolumes(float3 worldPos, out float3 outColor)
{
    float totalWeight = 0.0;
    float3 colorAccum = float3(0.0, 0.0, 0.0);
    int count = (int)FroxelForward.w;
    [loop]
    for (int i = 0; i < 64; i++)
    {
        if (i >= count) break;
        FogVolumeGpu v = FroxelFogVolumes[i];
        float inside = FogVolumeInsideFactor(worldPos - v.CenterDensity.xyz, v.ExtentsFalloff.xyz,
            (int)v.ColorShape.w, v.KindDirection.yzw);
        if (inside <= 0.0)
            continue;
        float weight = pow(inside, max(v.ExtentsFalloff.w, 0.0001)) * saturate(v.CenterDensity.w);
        colorAccum += v.ColorShape.xyz * weight;
        totalWeight += weight;
    }
    outColor = totalWeight > 0.0001 ? colorAccum / totalWeight : float3(0.0, 0.0, 0.0);
    return totalWeight;
}

// ── Lighting ─────────────────────────────────────────────────────────────────

float FogPhase(float cosTheta)
{
    const float g = 0.25;
    float denominator = max(1.0 + g * g - 2.0 * g * cosTheta, 1e-4);
    return (1.0 - g * g) / (denominator * sqrt(denominator));
}

// Scatter relative to side-on viewing: FogColor is what an author sees away from the light;
// toward it the Henyey-Greenstein lobe brightens the haze.
float FogSunScatter(float cosTheta)
{
    return max(FogPhase(cosTheta) / FogPhase(0.0), 1.0);
}

// The sun's forward-scatter glow fades with the lighting switch and the sun's own intensity: an
// unlit scene or a night sky keeps the authored fog colour instead of glowing toward a dark sun.
float SunGlow(float cosTheta)
{
    float sunWeight = saturate(LightDirEnabled.w) * saturate(SunColorIntensity.w);
    return lerp(1.0, FogSunScatter(cosTheta), sunWeight);
}

// Sun visibility through the cascades, nearest first. Casters above a cascade's near plane are
// pancaked onto it, so tall occluders still carve shafts.
float SunVisibility(float3 worldPos)
{
    if (ShadowParams.x < 0.5)
        return 1.0;
    float strength = saturate(ShadowCascadeParams.w) * saturate(LightDirEnabled.w);
    if (ShadowCascadeParams.z > 0.5)
    {
        float4 p = mul(float4(worldPos, 1.0), LightViewProjectionNear);
        float3 ndc = p.xyz / max(p.w, 1e-4);
        float2 uv = float2(ndc.x * 0.5 + 0.5, -ndc.y * 0.5 + 0.5);
        if (ndc.z > 0.0 && ndc.z < 1.0 && uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0)
            return lerp(1.0, ShadowMapNear.SampleCmpLevelZero(ShadowSamp, uv, ndc.z - ShadowParams.y * 0.5), strength);
    }
    if (ShadowCascadeParams.z > 1.5)
    {
        float4 p = mul(float4(worldPos, 1.0), LightViewProjectionMid);
        float3 ndc = p.xyz / max(p.w, 1e-4);
        float2 uv = float2(ndc.x * 0.5 + 0.5, -ndc.y * 0.5 + 0.5);
        if (ndc.z > 0.0 && ndc.z < 1.0 && uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0)
            return lerp(1.0, ShadowMapMid.SampleCmpLevelZero(ShadowSamp, uv, ndc.z - ShadowParams.y * 0.75), strength);
    }
    float4 pf = mul(float4(worldPos, 1.0), LightViewProjection);
    float3 ndcFar = pf.xyz / max(pf.w, 1e-4);
    float2 uvFar = float2(ndcFar.x * 0.5 + 0.5, -ndcFar.y * 0.5 + 0.5);
    if (ndcFar.z <= 0.0 || ndcFar.z >= 1.0 || uvFar.x < 0.0 || uvFar.x > 1.0 || uvFar.y < 0.0 || uvFar.y > 1.0)
        return 1.0;
    return lerp(1.0, ShadowMapFar.SampleCmpLevelZero(ShadowSamp, uvFar, ndcFar.z - ShadowParams.y), strength);
}

int OmniCubeFaceIndex(float3 dir)
{
    float3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z)
        return dir.x >= 0.0 ? 0 : 1;
    if (a.y >= a.z)
        return dir.y >= 0.0 ? 2 : 3;
    return dir.z >= 0.0 ? 4 : 5;
}

// Same lookup as ForwardShaders' SampleLocalShadow, single tap (fog is soft already).
float SampleLocalShadow(int slot, float3 worldPos, float3 lightPos)
{
    float3 toPoint = worldPos - lightPos;
    float len = length(toPoint);
    if (len < 1e-5)
        return 1.0;
    float kind = dot(LocalShadowKinds, float4(slot == 0, slot == 1, slot == 2, slot == 3));
    int face = kind > 0.5 ? 0 : OmniCubeFaceIndex(toPoint / len);
    float4 clip = mul(float4(worldPos, 1.0), LocalShadowFaceVP[slot * 6 + face]);
    if (clip.w <= 1e-5)
        return 1.0;
    float3 proj = clip.xyz / clip.w;
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;
    float2 tileUv = float2(proj.x * 0.5 + 0.5, -proj.y * 0.5 + 0.5);
    if (tileUv.x < 0.0 || tileUv.x > 1.0 || tileUv.y < 0.0 || tileUv.y > 1.0)
        return 1.0;
    float texel = LocalShadowParams.z;
    float2 uv = clamp(tileUv, texel, 1.0 - texel);
    float2 atlasScale = float2(1.0 / 6.0, 1.0 / max(LocalShadowParams.y, 1.0));
    float bias = max(0.0015, 0.02 * (1.0 - saturate(len / max(LocalShadowSlots[slot].w, 0.001))));
    return LocalShadowAtlas.SampleCmpLevelZero(ShadowSamp, (float2(face, slot) + uv) * atlasScale, proj.z - bias);
}

// Point and spot lights scattering at a froxel, from the same clustered lists forward shading uses.
float3 LocalLightScatter(float3 worldPos, float3 viewDir, float2 screenUv, float depth)
{
    int nLights = (int)PointLightCounts.x;
    int grid = (int)PointLightCounts.y;
    float3 result = float3(0.0, 0.0, 0.0);
    if (nLights <= 0 || grid < 2)
        return result;
    int tx = clamp((int)floor(screenUv.x * grid), 0, grid - 1);
    int ty = clamp((int)floor(screenUv.y * grid), 0, grid - 1);
    const float sliceScale = 24.0 / log2(1000.0 / 0.25);
    int clusterSlice = clamp((int)floor(log2(max(depth, 1e-4) / 0.25) * sliceScale), 0, 23);
    uint cluster = (uint)((clusterSlice * grid + ty) * grid + tx);
    uint offset = TileLightIndices[cluster * 2];
    uint count = min(TileLightIndices[cluster * 2 + 1], (uint)PointLightCounts.z);
    [loop]
    for (uint i = 0; i < count; i++)
    {
        uint li = TileLightIndices[offset + i];
        if (li >= (uint)nLights) continue;
        ClusterPointLight L = ClusterLights[li];
        float3 toLight = L.Pos - worldPos;
        float dist = length(toLight);
        float radius = max(L.Radius, 0.001);
        if (dist >= radius) continue;
        float attenuation = pow(1.0 - saturate(dist / radius), max(L.Falloff, 0.05));
        float3 l = toLight / max(dist, 1e-4);
        if (dot(L.SpotDir, L.SpotDir) > 0.25)
            attenuation *= smoothstep(L.SpotCosOuter, max(L._pad2, L.SpotCosOuter + 1e-4), dot(-l, L.SpotDir));
        if (attenuation <= 0.0) continue;
        if (L._pad0 > 0.5 && LocalShadowParams.x > 0.5)
            attenuation *= SampleLocalShadow((int)(L._pad0 - 0.5), worldPos, L.Pos);
        result += L.Color * L.Intensity * attenuation * FogSunScatter(dot(viewDir, l));
    }
    return result;
}

// ── Passes ───────────────────────────────────────────────────────────────────

float4 PS_FroxelInject(VSOut IN) : SV_Target
{
    int3 cell = FroxelCell(IN.pos.xy);
    float2 screenUv = (float2(cell.xy) + 0.5 + FroxelJitter.xy) / FroxelGrid.xy;
    float depth = FroxelSliceDepth(cell.z + 0.5 + FroxelJitter.z);
    float3 ray = FroxelRay(screenUv);
    float3 worldPos = FroxelWorldPosition(ray, depth);

    float horizon = 1.0 - saturate(abs(ray.y));
    float3 volumeColor;
    float volumeDensity = ComputeFogVolumes(worldPos, volumeColor);
    float density = FogDensityAt(worldPos) + volumeDensity + horizon * horizon * FroxelLighting.z;
    if (density <= 1e-7)
        return float4(0.0, 0.0, 0.0, 0.0);

    // Shadowed froxels keep an ambient floor so fog never goes pitch black; lit ones scatter the sun.
    float lightAmount = lerp(FroxelLighting.y, SunGlow(dot(ray, normalize(-LightDirEnabled.xyz))),
        SunVisibility(worldPos));
    float3 albedo = volumeDensity > 1e-4
        ? lerp(FogColor.rgb, volumeColor, saturate(volumeDensity / density))
        : FogColor.rgb;
    float3 radiance = albedo * lightAmount;
    if (FroxelLighting.x > 0.0)
        radiance += LocalLightScatter(worldPos, ray, screenUv, depth) * FroxelLighting.x;
    precise float4 injected = float4(radiance * density, density);
    return injected;
}

// History tap confined to one slice's rows so filtering never bleeds into the neighbouring slice.
float4 HistorySlice(float2 uv, float slice)
{
    float height = FroxelGrid.y;
    float u = clamp(uv.x * FroxelGrid.x, 0.5, FroxelGrid.x - 0.5) / FroxelGrid.x;
    float v = (clamp(uv.y * height, 0.5, height - 0.5) + slice * height) / (height * FroxelGrid.z);
    return FroxelHistory.SampleLevel(LinearClamp, float2(u, v), 0);
}

float4 PS_FroxelTemporal(VSOut IN) : SV_Target
{
    float4 current = FroxelSource.Load(int3(int2(IN.pos.xy), 0));
    if (FroxelJitter.w <= 0.0)
        return current;

    int3 cell = FroxelCell(IN.pos.xy);
    float2 screenUv = (float2(cell.xy) + 0.5) / FroxelGrid.xy;
    float3 worldPos = FroxelWorldPosition(FroxelRay(screenUv), FroxelSliceDepth(cell.z + 0.5));
    float4 previousClip = mul(float4(worldPos, 1.0), FroxelPrevViewProjection);
    if (previousClip.w <= 1e-4)
        return current;
    float2 previousUv = float2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
    if (previousUv.x < 0.0 || previousUv.x > 1.0 || previousUv.y < 0.0 || previousUv.y > 1.0)
        return current;
    float previousSlice = FroxelSliceCoordinate(previousClip.w) - 0.5;
    if (previousSlice < -0.5 || previousSlice > FroxelGrid.z - 0.5)
        return current;
    float s0 = clamp(floor(previousSlice), 0.0, FroxelGrid.z - 1.0);
    float s1 = min(s0 + 1.0, FroxelGrid.z - 1.0);
    float4 history = lerp(HistorySlice(previousUv, s0), HistorySlice(previousUv, s1), saturate(previousSlice - s0));
    return lerp(current, history, FroxelJitter.w);
}

float4 PS_FroxelIntegrate(VSOut IN) : SV_Target
{
    int3 cell = FroxelCell(IN.pos.xy);
    int height = (int)FroxelGrid.y;
    float3 ray = FroxelRay((float2(cell.xy) + 0.5) / FroxelGrid.xy);
    // World distance per unit of view depth along this column's ray.
    float rayScale = 1.0 / max(dot(ray, FroxelForward.xyz), 0.05);
    precise float3 inscatter = float3(0.0, 0.0, 0.0);
    precise float transmittance = 1.0;
    float previousDepth = 0.0;
    [loop]
    for (int s = 0; s < 128; s++)
    {
        if (s > cell.z) break;
        float4 froxel = FroxelSource.Load(int3(cell.x, s * height + cell.y, 0));
        float sliceEnd = FroxelSliceDepth(s + 1.0);
        float len = (sliceEnd - previousDepth) * rayScale;
        previousDepth = sliceEnd;
        // Energy-conserving slice integration (Hillaire 2015); equals the old per-step march when σs = σt.
        float extinction = max(froxel.a, 1e-6);
        float sliceTransmittance = exp(-extinction * len);
        inscatter += transmittance * (froxel.rgb - froxel.rgb * sliceTransmittance) / extinction;
        transmittance *= sliceTransmittance;
    }
    return float4(inscatter, transmittance);
}
";

        /// <summary>
        /// Appended to every shader that fogs a surface. Requires the including shader to declare
        /// EngineConstants (FogParams, FogParams2, FogColor, LightDirEnabled). Binds t16, s2 and b6
        /// (b5 belongs to terrain layers and authored shader parameters).
        /// </summary>
        public const string ApplySource = @"
// ── Froxel fog apply (shared: forward, water and post composite) ─────────────
Texture2D    FroxelVolume  : register(t16);
SamplerState FroxelSampler : register(s2);

cbuffer FroxelApplyConstants : register(b6)
{
    float4 FroxelApplyGrid;   // x = width, y = height, z = slices, w = far depth
    float4 FroxelApplyParams; // x = active, y = near depth, z = horizon boost, w = unused
};

float4 FroxelVolumeSlice(float2 uv, float slice)
{
    float height = FroxelApplyGrid.y;
    float u = clamp(uv.x * FroxelApplyGrid.x, 0.5, FroxelApplyGrid.x - 0.5) / FroxelApplyGrid.x;
    float v = (clamp(uv.y * height, 0.5, height - 0.5) + slice * height) / (height * FroxelApplyGrid.z);
    return FroxelVolume.SampleLevel(FroxelSampler, float2(u, v), 0);
}

// Closed-form optical depth of the height fog along a segment (FroxelFogMath.HeightFogOpticalDepth).
float FroxelHeightAntiderivative(float y, float heightBase, float k)
{
    return y <= heightBase ? y - heightBase : (1.0 - exp(-k * (y - heightBase))) / k;
}

float FroxelHeightOpticalDepth(float3 fromPos, float3 toPos)
{
    float len = length(toPos - fromPos);
    float density = max(FogParams.w, 0.0);
    if (len <= 0.0 || density <= 0.0)
        return 0.0;
    float k = max(FogParams2.y, 1e-4);
    float a = saturate(FogParams2.z);
    float dy = toPos.y - fromPos.y;
    float meanHeight = abs(dy) < 1e-4
        ? exp(-k * max(0.5 * (fromPos.y + toPos.y) - FogParams2.x, 0.0))
        : (FroxelHeightAntiderivative(toPos.y, FogParams2.x, k) - FroxelHeightAntiderivative(fromPos.y, FogParams2.x, k)) / dy;
    return len * density * ((1.0 - a) + a * meanHeight);
}

float FroxelScatterPhase(float cosTheta)
{
    const float g = 0.25;
    float denominator = max(1.0 + g * g - 2.0 * g * cosTheta, 1e-4);
    return (1.0 - g * g) / (denominator * sqrt(denominator));
}

// Volumetric fog between the camera and worldPos, seen through screenUv at view depth depthW
// (clip w). rgb = in-scattered light, a = transmittance: apply as color * a + rgb.
float4 FroxelFog(float2 screenUv, float3 cameraPos, float3 worldPos, float depthW)
{
    if (FroxelApplyParams.x < 0.5)
        return float4(0.0, 0.0, 0.0, 1.0);
    float farDepth = FroxelApplyGrid.w;
    float nearDepth = FroxelApplyParams.y;
    float coordinate = clamp(log2(max(min(depthW, farDepth), 1e-4) / nearDepth) / log2(farDepth / nearDepth) * FroxelApplyGrid.z,
        0.0, FroxelApplyGrid.z);
    // Slice i stores the integral to its far edge, which sits at coordinate i + 1.
    float index = coordinate - 1.0;
    float4 fog;
    if (index <= 0.0)
    {
        fog = lerp(float4(0.0, 0.0, 0.0, 1.0), FroxelVolumeSlice(screenUv, 0.0), saturate(coordinate));
    }
    else
    {
        float i0 = floor(index);
        float i1 = min(i0 + 1.0, FroxelApplyGrid.z - 1.0);
        fog = lerp(FroxelVolumeSlice(screenUv, i0), FroxelVolumeSlice(screenUv, i1), index - i0);
    }

    if (depthW > farDepth)
    {
        // Far field: analytic height fog lit by the sun (beyond the cascades, as the old march was).
        float3 farStart = cameraPos + (worldPos - cameraPos) * (farDepth / depthW);
        float3 viewDir = normalize(worldPos - cameraPos);
        float horizon = 1.0 - saturate(abs(viewDir.y));
        float opticalDepth = FroxelHeightOpticalDepth(farStart, worldPos)
            + horizon * horizon * FroxelApplyParams.z * length(worldPos - farStart);
        float farTransmittance = exp(-opticalDepth);
        float sunScatter = lerp(1.0,
            max(FroxelScatterPhase(dot(viewDir, normalize(-LightDirEnabled.xyz))) / FroxelScatterPhase(0.0), 1.0),
            saturate(LightDirEnabled.w) * saturate(SunColorIntensity.w));
        fog.rgb += fog.a * FogColor.rgb * sunScatter * (1.0 - farTransmittance);
        fog.a *= farTransmittance;
    }
    return fog;
}
";
    }
}

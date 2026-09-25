namespace Genesis.Rendering.Primitives

{

    internal static class FogPostShaders

    {

        public const string Source = @"

struct PostPointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    // Three scalars rather than float3: std140 aligns a 3-component vector to 16, which would
    // place the pad at offset 48 instead of 36 and make EngineConstants inexpressible in GLSL.
    float Falloff; float _pad0; float _pad1; float _pad2;
};

// Issue 6 Stage 1: analytic placeable fog volume. Must match Genesis.Rendering.Primitives
// .ForwardRenderer's FogVolumeData (CenterDensity/ExtentsFalloff/ColorShape/KindPad) field
// for field — see ComputeFogVolumes() below for the shape enum (0=Box,1=Sphere,2=Ellipsoid,
// 3=HeightSlab) and kind enum (0=GroundMist,1=Cloud,2=Haze) this is unpacked against.
struct FogVolumeGpu { float4 CenterDensity; float4 ExtentsFalloff; float4 ColorShape; float4 KindPad; };

cbuffer EngineConstants : register(b0)

{

    float4 LightDirEnabled;

    float4 FogParams;

    float4 FogColor;

    float4 AmbientColor;

    float4 AmbientGroundColor;

    float4 SunColorIntensity;

    float4 FogParams2;

    float4 ShadowParams;

    float4 EffectParams;

    float4 VolumetricParams;

    float4 ViewportParams;

    // Trailing fields below are unused by this shader's own logic but must be declared so this
    // cbuffer remains a valid *prefix* of the C# EngineCB struct in exact field order — every
    // shader sharing _cbEngine has to agree on layout up to the point each one stops reading
    // (see the cbuffer-layout bug this exact mismatch caused, fixed earlier on AmbientGroundColor).
    float4 ShadowCascadeParams;

    float4 PointLightCounts;

    PostPointLight PointLights[8];

    float4 FogVolumeCounts;

    FogVolumeGpu FogVolumes[8];

};



cbuffer FogPostConstants : register(b1)

{

    float4 ClipPlanes;

    float4 CameraPosPad;

    row_major float4x4 InvViewProjection;

    row_major float4x4 LightViewProjection;

    row_major float4x4 LightViewProjectionNear;

    row_major float4x4 LightViewProjectionMid;

    float4 AoParams;

    // AF1.4: append-only — x=contact shadows enabled, y=strength, zw unused. Strength already
    // accounts for GTAO being on (ContactShadowMath.EffectiveStrength), because the two darken
    // the same creases and stacking them at full force reads as a smear rather than contact.
    float4 ContactParams;

    // AF1.5: append-only — x=local volumetric scatter enabled, y=strength, zw unused. Additive
    // inscatter, so unlike ContactParams this never darkens: with no local lights in range the
    // map is zero and the composite is unchanged.
    float4 LocalVolParams;

    // AF1.6: append-only — x=smoke extinction enabled, y=extinction coefficient, z=volume count,
    // w unused. The volumes themselves ride here rather than in EngineConstants on purpose: the
    // forward path never reads them, so its cbuffer prefix (and the three shaders that duplicate
    // it) stay exactly as they were when this feature is off.
    float4 SmokeParams;

    // xyz = sphere centre, w = radius.
    float4 SmokePosRadius[8];

    // x = density (per world unit); yzw unused. Two arrays rather than one packed float4 because
    // position+radius already fills a full row and splitting keeps both aligned without padding
    // maths in the managed mirror.
    float4 SmokeDensity[8];

    // AF1.7: append-only — x=bloom enabled, y=intensity, zw unused. BloomMap is the half-res
    // pyramid result; when x is 0 the add is skipped and the texture may be unbound.
    float4 BloomParams;

    // AF1.7: x=exposure, y=contrast, z=saturation, w unused. Defaults 1/1/1 are identity.
    float4 ExposureParams;

    // AF1.7: x=vignette strength (0 = no-op), yzw unused.
    float4 VignetteParams;

    // AF2.1: append-only — x=atmosphere LUT enabled, y=sunHeight (−LightDirection.Y),
    // z=daylight (smoothstep of sunHeight), w unused. AtmosphereLut is the CPU-baked atlas;
    // when x is 0 the sample path is skipped and the texture may be unbound.
    float4 AtmosphereLutParams;

    // AF2.3: append-only — x=raymarched clouds enabled, y=intensity, zw unused. CloudMap is the
    // half-res march result; additive after LocalVol / before bloom. Software never binds it.
    float4 CloudCompositeParams;

    // AF2.5: append-only — x=celestial extras enabled, y=nightFactor, z=siderealAngle,
    // w=latitudeRadians. Sky-only stars / Milky Way / moon; Software never runs this branch.
    float4 CelestialParams;

    // AF2.5: xyz = direction toward the moon, w = lunar phase 0..1.
    float4 MoonDirPhase;

    // x=cloud layer base, y=top. Full-resolution depth protects foreground silhouettes.
    float4 CloudLayerParams;
    float4 AuthoredSkyZenith;
    float4 AuthoredSkyHorizon;
    float4 AuthoredSkySun;

};



Texture2D SceneColor : register(t0);

Texture2D<float> SceneDepth  : register(t1);

Texture2D<float> ShadowMapFar  : register(t2);

Texture2D<float> ShadowMapNear : register(t3);

// Written 1.0 by the forward pass (ForwardShaders.cs PS()) at every pixel where it already
// self-applied ray-marched fog because the draw never wrote scene depth (NoDepthWrite — held
// items, particles, the sun billboard). Those pixels' real depth is structurally invisible to
// this post-process (it would reconstruct world position from whatever is actually behind them),
// so this mask tells PS() below to skip re-applying fog there instead of double-fogging against
// the wrong (background) distance — which is what crushed near-camera objects/particles into
// solid fog regardless of density.
Texture2D FogSkipMask : register(t4);

Texture2D AoMap : register(t6);

// AF1.4 half-res screen-space contact shadow occlusion (0 = the ray reached the sun). t5 is
// reserved for Mid when CompositePost binds three cascades; Contact stays on t7.
Texture2D ContactMap : register(t7);

// AF1.5 half-res local-light inscatter, added below.
Texture2D LocalVolMap : register(t8);

// AF1.7 half-res HDR bloom pyramid result, added before exposure/grading/ACES.
Texture2D BloomMap : register(t9);

// AF2.1 packed transmittance / multi-scatter / sky-view atlas (256×64 RGBA8), sampled for sky pixels.
Texture2D AtmosphereLut : register(t10);

// AF2.3 half-res raymarched cloud inscatter (RGB) + opacity (A).
Texture2D CloudMap : register(t11);

SamplerState LinearClamp : register(s0);

SamplerComparisonState ShadowSamp : register(s1);



struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };



VSOut VS(uint id : SV_VertexID)

{

    VSOut o;

    float2 p = float2((id << 1) & 2, id & 2);

    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);

    // Full-screen triangle UVs: keep p unshifted so interpolation maps the viewport to [0,1].
    o.uv  = p;

    return o;

}



float LinearizeDepth(float z, float nearPlane, float farPlane)

{

    return (2.0 * nearPlane * farPlane) / max(farPlane + nearPlane - z * (farPlane - nearPlane), 0.0001);

}



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



// Interleaved Gradient Noise (Jimenez, 2014): a smooth, low-frequency dither value in [0,1)
// derived from real screen pixel coordinates. Used to jitter the ray march's per-step start
// offset so the limited step count doesn't band. Replaces a prior raw high-frequency hash
// seeded from UV (here) / mesh texture UV (ForwardShaders.cs, TerrainShader.cs), which produced
// visible salt-and-pepper grain — worst on long sky/horizon rays that only take a few steps.
float InterleavedGradientNoise(float2 pixelCoord)

{

    pixelCoord = floor(pixelCoord);

    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);

    return frac(magic.z * frac(dot(pixelCoord, magic.xy)));

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



// ── Unified volumetric fog model (full rewrite) ─────────────────────────────
// Replaces the old ""evaluate one scalar fog factor at the destination point""
// approach with real front-to-back Beer-Lambert ray marching: density (base
// atmosphere + placed fog volumes) is sampled at each step along the actual
// view ray, accumulating transmittance and inscattered light. This is what
// makes fog genuinely 3D — a placed volume or a height layer now affects
// every step a ray takes through it, not just whatever single surface pixel
// happens to be behind it — and it's also what lets fog correctly reach
// objects that never wrote scene depth (see ForwardShaders.cs/TerrainShader.cs,
// which ray-march from the camera to their own real position instead of
// relying on this post-process's depth-buffer reconstruction).
//
// IMPORTANT: ForwardShaders.cs and TerrainShader.cs each carry their own copy
// of FogDensityAt/RayMarchFog (these raw HLSL strings have no #include), used
// as their no-screen-space-coverage fallback. Keep all three copies in sync —
// same rule already applied to the EngineConstants cbuffer prefix above.

// Base atmosphere extinction coefficient (per-unit-length) at a world point:
// global density + horizontal noise breakup, attenuated by height falloff.
float FogDensityAt(float3 worldPos)
{
    float fogDensity = FogParams.w;
    float noiseStr   = EffectParams.w;
    float noiseAmp   = noiseStr * saturate(fogDensity / 0.004);
    float density    = fogDensity + (NoiseXZ(worldPos.xz) - 0.5) * noiseAmp;

    float heightBase    = FogParams2.x;
    float heightFalloff = max(FogParams2.y, 0.0001);
    float aerialBlend   = saturate(FogParams2.z);
    float belowBase = max(heightBase - worldPos.y, 0.0);
    float heightMul = exp(-heightFalloff * belowBase);
    density *= lerp(1.0, heightMul, aerialBlend);

    return max(density, 0.0);
}



int VolumetricStepCount()

{

    if (VolumetricParams.y < 0.5) return 6;

    if (VolumetricParams.y < 1.5) return 12;

    return 20;

}



// Sun visibility at a world point, sampled from the cascaded shadow maps. Returns 1.0 (fully
// lit) when shadows are off or the point falls outside both cascades, so this is always safe
// to call. Used to weight each volumetric fog ray-march step — points in direct sun accumulate
// more haze than shadowed points, which is what turns uniform fog into visible light shafts.
float SampleShadowAtPoint(float3 worldPos)

{

    if (ShadowParams.x < 0.5)

        return 1.0;



    if (ShadowCascadeParams.z > 0.5)
    {
        float4 posNear = mul(float4(worldPos, 1.0), LightViewProjectionNear);
        float3 nearNdc = posNear.xyz / max(posNear.w, 0.0001);
        if (nearNdc.z > 0.0 && nearNdc.z < 1.0)
        {
            float nearU =  nearNdc.x * 0.5 + 0.5;
            float nearV = -nearNdc.y * 0.5 + 0.5;
            if (nearU >= 0.0 && nearU <= 1.0 && nearV >= 0.0 && nearV <= 1.0)
                return lerp(1.0,
                    ShadowMapNear.SampleCmpLevelZero(ShadowSamp, float2(nearU, nearV), nearNdc.z - ShadowParams.y * 0.5),
                    saturate(ShadowCascadeParams.w) * saturate(LightDirEnabled.w));
        }
    }



    float4 posFar = mul(float4(worldPos, 1.0), LightViewProjection);
    float3 farNdc = posFar.xyz / max(posFar.w, 0.0001);
    if (farNdc.z <= 0.0 || farNdc.z >= 1.0)
        return 1.0;



    float farU =  farNdc.x * 0.5 + 0.5;
    float farV = -farNdc.y * 0.5 + 0.5;
    if (farU < 0.0 || farU > 1.0 || farV < 0.0 || farV > 1.0)
        return 1.0;



    return lerp(1.0,
        ShadowMapFar.SampleCmpLevelZero(ShadowSamp, float2(farU, farV), farNdc.z - ShadowParams.y),
        saturate(ShadowCascadeParams.w) * saturate(LightDirEnabled.w));

}



// Issue 6 Stage 1: per-shape ""inside"" factor in [0,1], 1 at the volume centre, 0 at/beyond
// its bounds. localPos is worldPos - volume centre. Shapes: 0=Box, 1=Sphere, 2=Ellipsoid,
// 3=HeightSlab (infinite in X/Z, bounded by extents.y in Y only).
float FogVolumeInsideFactor(float3 localPos, float3 extents, int shape)

{

    float3 e = max(extents, 0.0001);

    if (shape == 1)
    {
        float d = length(localPos) / e.x;
        return saturate(1.0 - d);
    }

    if (shape == 3)
    {
        float d = abs(localPos.y) / e.y;
        return saturate(1.0 - d);
    }

    float3 n = localPos / e;

    if (shape == 2)
    {
        float d = length(n);
        return saturate(1.0 - d);
    }

    // Box: Chebyshev (max-axis) distance so the falloff reaches 0 exactly at each face.
    float d = max(max(abs(n.x), abs(n.y)), abs(n.z));
    return saturate(1.0 - d);

}



// Accumulates all active placeable fog volumes at worldPos. Returns the density-weighted
// average volume colour via outColor and the (un-clamped-to-1) accumulated density via the
// return value, so the caller can both boost the existing distance/height fog factor and
// tint it toward whichever volume(s) the point sits inside.
float ComputeFogVolumes(float3 worldPos, out float3 outColor)

{

    float totalWeight = 0.0;

    float3 colorAccum = float3(0.0, 0.0, 0.0);

    int count = (int)FogVolumeCounts.x;

    [loop]
    for (int i = 0; i < 8; i++)
    {
        if (i >= count) break;

        FogVolumeGpu v = FogVolumes[i];
        float3 localPos = worldPos - v.CenterDensity.xyz;
        int shape = (int)v.ColorShape.w;
        float inside = FogVolumeInsideFactor(localPos, v.ExtentsFalloff.xyz, shape);
        if (inside <= 0.0)
            continue;

        float falloffCurve = max(v.ExtentsFalloff.w, 0.0001);
        float weight = pow(inside, falloffCurve) * saturate(v.CenterDensity.w);

        colorAccum += v.ColorShape.xyz * weight;
        totalWeight += weight;
    }

    outColor = totalWeight > 0.0001 ? colorAccum / totalWeight : float3(0.0, 0.0, 0.0);
    return totalWeight;

}



// Marches from cameraPos toward viewDir over rayLength, accumulating
// Beer-Lambert transmittance and shadow-aware inscattered light (placed fog
// volumes' own colour included). Returns transmittance via the return value
// (0 = scene colour fully replaced by fog, 1 = fog has no effect) and the
// inscattered colour via outInscatter. Composite at the call site with:
//   color = color * transmittance + outInscatter;
float RayMarchFog(float3 cameraPos, float3 viewDir, float rayLength, float3 lightDir,
                   float jitterSeed, out float3 outInscatter)
{
    outInscatter = float3(0.0, 0.0, 0.0);

    float maxDist = max(rayLength, 0.0);
    if (maxDist <= 0.001)
        return 1.0;

    int steps = VolumetricStepCount();
    float stepLen = maxDist / max(steps, 1);
    // jitterSeed is now expected to already be a [0,1) dither value from InterleavedGradientNoise
    // (computed at the call site from screen pixel coordinates), not a raw hash seed.
    float jitter = (jitterSeed - 0.5) * stepLen * 0.15;
    float t = max(stepLen * 0.5 + jitter, 0.0);

    float sunDot       = saturate(dot(viewDir, normalize(-lightDir)));
    float sunPreserve  = saturate(FogParams2.w);
    float horizon      = 1.0 - saturate(abs(viewDir.y));
    float horizonBoost = horizon * horizon * saturate(FogParams2.z) * 0.30;

    float transmittance = 1.0;

    [loop]
    for (int i = 0; i < 24; i++)
    {
        if (i >= steps) break;

        float3 samplePos = cameraPos + viewDir * t;

        float3 volColor;
        float volDensity = ComputeFogVolumes(samplePos, volColor);
        float density = FogDensityAt(samplePos) + volDensity + horizonBoost * 0.02;

        float vis = SampleShadowAtPoint(samplePos);
        // Shadowed steps still contribute some ambient haze (floor of 0.35) so fog never goes
        // pitch black; sun-visible steps contribute fully, producing visible light shafts.
        float lightAmt = lerp(0.35, 1.0, vis) * (1.0 - sunDot * sunPreserve);

        float3 stepColor = volDensity > 0.0001
            ? lerp(FogColor.rgb, volColor, saturate(volDensity / max(density, 0.0001)))
            : FogColor.rgb;

        float stepTransmittance = exp(-density * stepLen);
        // Front-to-back: each step's contribution is weighted by how much light already
        // survived to reach it (the running transmittance), which is the correct way to
        // accumulate in-scattering along a ray instead of just averaging samples.
        outInscatter += stepColor * lightAmt * (1.0 - stepTransmittance) * transmittance;
        transmittance *= stepTransmittance;

        t += stepLen;
    }

    return saturate(transmittance);
}



// ── AF1.6 smoke as extinction ───────────────────────────────────────────────
// Ported from ForestLight's segmentSphereLength/smokeTransmittance pair. Must stay in step with
// Genesis.Rendering.Primitives.SmokeExtinctionMath, which is the CPU reference the headless gate
// asserts against — these raw HLSL strings have no #include, so agreement is by hand as with the
// FogDensityAt/RayMarchFog copies above.

// Length of the part of segment a->b that lies inside the sphere; zero on a miss.
float SegmentSphereLength(float3 a, float3 b, float3 center, float radius)
{
    float3 d = b - a;
    float segmentLength = length(d);
    if (segmentLength < 0.0001 || radius <= 0.0) return 0.0;
    float3 dir = d / segmentLength;
    float3 oc = a - center;
    float bTerm = dot(oc, dir);
    float cTerm = dot(oc, oc) - radius * radius;
    float discriminant = bTerm * bTerm - cTerm;
    if (discriminant <= 0.0) return 0.0;
    float root = sqrt(discriminant);
    float t0 = clamp(-bTerm - root, 0.0, segmentLength);
    float t1 = clamp(-bTerm + root, 0.0, segmentLength);
    return max(0.0, t1 - t0);
}

// One Beer-Lambert term for every smoke sphere the ray crosses. Returns 1 when there is no smoke,
// so the composite is bit-identical to a frame with the feature switched off.
float SmokeTransmittance(float3 a, float3 b)
{
    int count = (int)SmokeParams.z;
    if (count <= 0) return 1.0;

    float opticalDepth = 0.0;
    [loop]
    for (int i = 0; i < 8; i++)
    {
        if (i >= count) break;
        opticalDepth += SegmentSphereLength(a, b, SmokePosRadius[i].xyz, SmokePosRadius[i].w)
                      * SmokeDensity[i].x;
    }

    return saturate(exp(-opticalDepth * SmokeParams.y));
}

// Narkowicz ACES filmic approximation. Rolls bright values toward white smoothly instead of
// hard-clipping, and gives the rest a more filmic-looking response. Operates on the HDR linear
// scene colour before it lands in the 8-bit backbuffer.
float3 ACESFilm(float3 x)
{
    float a = 2.51; float b = 0.03; float c = 2.43; float d = 0.59; float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

// Exact scene depth at this pixel. Depth reconstruction is sensitive to filtering, so this is a
// point fetch rather than a sampler read. Shared by the contact-shadow apply and the fog ray
// march so the WebGPU compatibility path below exists once.
float FetchSceneDepth(float2 uv, int2 pixel)
{
    return SceneDepth.Load(int3(pixel, 0));
}

float4 PS(VSOut IN) : SV_Target

{

    float4 scene = SceneColor.Sample(LinearClamp, IN.uv);

    float3 color = scene.rgb;

    float ao = AoParams.x > 0.5 ? AoMap.Sample(LinearClamp, IN.uv).r : 1.0;

    color.rgb *= ao;

    int2 pixel = int2(floor(IN.uv * ViewportParams.xy));

    // AF1.4 contact shadows. Deliberately *not* another fullscreen ambient darken: that would
    // double up with the GTAO multiply above wherever both find the same crease. The contact term
    // only removes direct sunlight, so it is gated on the pixel actually being sun-lit (the same
    // cascaded shadow lookup the volumetric fog uses) and scaled by a strength the renderer
    // already reduced when GTAO is on.
    if (ContactParams.x > 0.5)
    {
        float contactDepth = FetchSceneDepth(IN.uv, pixel);
        if (contactDepth < 0.9999)
        {
            float2 contactNdc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
            float4 contactWorldH = mul(float4(contactNdc, contactDepth, 1.0), InvViewProjection);
            float3 contactWorld = contactWorldH.xyz / max(contactWorldH.w, 0.0001);
            float sunVis = SampleShadowAtPoint(contactWorld);
            float occlusion = saturate(ContactMap.Sample(LinearClamp, IN.uv).r);
            color.rgb *= lerp(1.0, 1.0 - occlusion * ContactParams.y, saturate(sunVis));
        }
    }

    // AF1.6 smoke extinction. Applied before the fog march and independently of FogSkipMask: a
    // surface standing behind a plume is darker whether or not this pixel's fog was already
    // self-applied by the forward pass, and whether or not fog is enabled at all. The world
    // position is reconstructed here rather than reusing the fog block's because that block is
    // gated on FogParams.x, and smoke has to survive fog being off.
    if (SmokeParams.x > 0.5 && SmokeParams.z >= 1.0)
    {
        float smokeZ = FetchSceneDepth(IN.uv, pixel);
        bool smokeIsSky = smokeZ >= 0.9999;
        float2 smokeNdc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
        float4 smokeWorldH = mul(float4(smokeNdc, smokeIsSky ? 1.0 : smokeZ, 1.0), InvViewProjection);
        float3 smokeCameraPos = CameraPosPad.xyz;
        float3 smokeWorldPos = smokeWorldH.xyz / max(smokeWorldH.w, 0.0001);
        if (smokeIsSky)
        {
            // No surface to attenuate, so integrate the view ray over the same bounded haze depth
            // the sky fog march uses. Marching to the real far plane would put the far endpoint
            // hundreds of units past every plume and change nothing except precision.
            float3 smokeViewDir = normalize(smokeWorldPos - smokeCameraPos);
            smokeWorldPos = smokeCameraPos + smokeViewDir * min(ClipPlanes.y, 160.0);
        }
        color.rgb *= SmokeTransmittance(smokeWorldPos, smokeCameraPos);
    }

    // Exact (unfiltered) lookup — this is a binary flag, not something to bilinear-blend.
    float skipPostFog = FogSkipMask.Load(int3(pixel, 0)).r;

    if (FogParams.x > 0.5 && skipPostFog < 0.5)
    {

    float z = FetchSceneDepth(IN.uv, pixel);
    bool isSky = z >= 0.9999;

    float2 ndc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);

    float clipZ = isSky ? 1.0 : z;
    float4 clip = float4(ndc, clipZ, 1.0);

    float4 worldH = mul(clip, InvViewProjection);

    float3 cameraPos = CameraPosPad.xyz;
    float3 worldPos = worldH.xyz / max(worldH.w, 0.0001);
    float3 viewDir = normalize(worldPos - cameraPos);

    float linearZ = length(worldPos - cameraPos);
    if (isSky)
    {
        // When ray marching to the far clip plane for sky, 1000 units with 12 steps causes
        // stepLen of 83 units, severely undersampling noise and causing dark vertical bands.
        // Cap the sky ray-march distance to a smooth haze depth.
        linearZ = min(ClipPlanes.y, 160.0);
        worldPos = cameraPos + viewDir * linearZ;
    }

    float jitterSeed = InterleavedGradientNoise(float2(pixel));
    float3 inscatter;
    float transmittance = RayMarchFog(cameraPos, viewDir, linearZ, LightDirEnabled.xyz, jitterSeed, inscatter);
    color = lerp(color, color * transmittance + inscatter, saturate(FogColor.a));

    }

    // AF2.1 Atmosphere LUT — sky / far pixels only. Uses the current far colour (clear or fogged
    // background) as baseSky, then applies the skybox.frag transmittance / multi-scatter / sky-view
    // compose. Low/Software keep analytic clear + fog (this branch never runs when disabled).
    if (AtmosphereLutParams.x > 0.5)
    {
        float lutZ = FetchSceneDepth(IN.uv, pixel);
        if (lutZ >= 0.9999)
        {
            float2 lutNdc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
            float4 lutWorldH = mul(float4(lutNdc, 1.0, 1.0), InvViewProjection);
            float3 lutCam = CameraPosPad.xyz;
            float3 lutWorld = lutWorldH.xyz / max(lutWorldH.w, 0.0001);
            float3 lutViewDir = normalize(lutWorld - lutCam);
            float viewUp = saturate(lutViewDir.y * 0.5 + 0.5);
            float horizon = exp(-abs(lutViewDir.y) * 4.5);
            float sunHeight = AtmosphereLutParams.y;
            float daylight = AtmosphereLutParams.z;

            float3 transmittanceLut = AtmosphereLut.Sample(LinearClamp,
                float2(saturate(0.5 * (sunHeight + 1.0)) * 0.49, viewUp * 0.49)).rgb;
            float3 multiScatter = AtmosphereLut.Sample(LinearClamp,
                float2(0.505 + daylight * 0.115, 0.02 + viewUp * 0.46)).rgb;
            float3 skyView = AtmosphereLut.Sample(LinearClamp,
                float2(viewUp * 0.49, 0.51 + horizon * 0.47)).rgb;

            float3 sky = color.rgb;
            if (AuthoredSkyZenith.w > 0.5)
            {
                // The authoritative atmosphere supplies two sky colours independently of
                // mesh ambient. Preserve the sun/emissive contribution over the clear colour.
                float elevation = pow(saturate(lutViewDir.y), 0.45);
                sky = lerp(AuthoredSkyHorizon.rgb, AuthoredSkyZenith.rgb, elevation)
                    + max(color.rgb - AuthoredSkyHorizon.rgb, float3(0, 0, 0));
            }
            sky *= lerp(float3(0.82, 0.82, 0.82), transmittanceLut * 1.18, 0.38);
            sky += multiScatter * daylight * 0.22 + skyView * 0.12;
            if (AuthoredSkySun.w > 0.0)
            {
                // The sun is distant radiance transmitted through the atmosphere, not a
                // billboard self-fogged at an arbitrary mesh distance. Clouds composite later.
                float solarDot = saturate(dot(lutViewDir, AuthoredSkySun.xyz));
                float radius = radians(0.28);
                float disc = smoothstep(cos(radius * 1.2), cos(radius * 0.8), solarDot);
                float aureole = pow(solarDot, 1800.0) * 0.035;
                sky += SunColorIntensity.rgb * transmittanceLut * AuthoredSkySun.w * (disc + aureole);
            }
            color.rgb = sky;
        }
    }

    // AF2.5 celestial extras — sky-only, before LocalVol/CloudMap so clouds can occlude.
    // Software / disabled keep analytic clear (this branch never runs when CelestialParams.x is 0).
    if (CelestialParams.x > 0.5)
    {
        float celZ = FetchSceneDepth(IN.uv, pixel);
        if (celZ >= 0.9999)
        {
            float2 celNdc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
            float4 celWorldH = mul(float4(celNdc, 1.0, 1.0), InvViewProjection);
            float3 celCam = CameraPosPad.xyz;
            float3 celWorld = celWorldH.xyz / max(celWorldH.w, 0.0001);
            float3 viewDir = normalize(celWorld - celCam);
            float night = saturate(CelestialParams.y);
            float starIntensity = night * night;
            float sidereal = CelestialParams.z;
            float latitude = CelestialParams.w;

            float cs = cos(sidereal);
            float ss = sin(sidereal);
            float3 celestialRay = float3(
                viewDir.x * cs - viewDir.z * ss,
                viewDir.y,
                viewDir.x * ss + viewDir.z * cs);

            // Hash star layers on the rotated celestial sphere.
            float2 starUv = celestialRay.xz * 0.5 + 0.5;
            float2 cellA = floor(starUv * 640.0);
            float2 cellB = floor(starUv * 1100.0);
            float hA = frac(sin(dot(cellA, float2(127.1, 311.7))) * 43758.5453);
            float hB = frac(sin(dot(cellB, float2(269.5, 183.3))) * 43758.5453);
            float2 localA = frac(starUv * 640.0) - 0.5;
            float2 localB = frac(starUv * 1100.0) - 0.5;
            float starA = (1.0 - smoothstep(0.02, 0.06, length(localA))) * smoothstep(0.991, 1.0, hA);
            float starB = (1.0 - smoothstep(0.02, 0.055, length(localB))) * smoothstep(0.997, 1.0, hB) * 0.68;
            float3 starColour = lerp(float3(1.0, 0.64, 0.40), float3(0.58, 0.78, 1.0), hA);
            float horizonFade = smoothstep(-0.04, 0.20, viewDir.y);
            float3 celestial = starColour * (starA + starB) * starIntensity * horizonFade;

            // Soft exp Milky Way band; latitude tilts the galactic axis.
            float3 galactic = normalize(float3(0.12, 0.42, 0.9));
            float sLat = sin(latitude);
            float cLat = cos(latitude);
            float3 tilted = normalize(float3(
                galactic.x,
                galactic.y * cLat - galactic.z * sLat,
                galactic.y * sLat + galactic.z * cLat));
            float band = exp(-pow(abs(dot(celestialRay, tilted)) * 8.2, 1.35));
            celestial += float3(0.13, 0.18, 0.34) * band * (0.35 + 0.45 * starIntensity) * night * horizonFade;

            // Phase-aware moon disc + earthshine on the dark limb.
            float3 moonDir = normalize(MoonDirPhase.xyz);
            float moonDot = dot(viewDir, moonDir);
            float moonVisible = smoothstep(-0.035, 0.015, moonDir.y);
            float radius = radians(0.42);
            float mask = smoothstep(cos(radius * 1.05), cos(radius), moonDot) * moonVisible;
            if (mask > 0.001)
            {
                float3 upRef = abs(moonDir.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0);
                float3 tangent = normalize(cross(upRef, moonDir));
                float moonUvX = dot(viewDir, tangent) / max(sin(radius), 1e-5);
                float phase = clamp(MoonDirPhase.w, 0.02, 0.98);
                float lit = smoothstep(-0.075, 0.075, moonUvX + lerp(0.88, -0.88, phase));
                if (phase > 0.5)
                    lit = 1.0 - smoothstep(-0.075, 0.075, -moonUvX + lerp(-0.88, 0.88, phase));
                float earthshine = (1.0 - lit) * 0.16;
                celestial += float3(0.67, 0.76, 0.94) * 0.85 * mask * (lit + earthshine);
            }

            color.rgb += celestial;
        }
    }

    // AF1.5 local-light inscatter, added after fog rather than blended into it: these beams are
    // light arriving at the camera, not a medium the surface is seen through. Applied regardless
    // of skipPostFog on purpose — held items and particles self-apply their own fog and are
    // exactly the geometry a torch in the player's hand should be glowing on.
    if (LocalVolParams.x > 0.5)
        color.rgb += LocalVolMap.Sample(LinearClamp, IN.uv).rgb * LocalVolParams.y;

    // AF2.3 raymarched clouds — premultiplied over for authored skies, legacy additive otherwise.
    // FogVolumes still run above when authored.
    if (CloudCompositeParams.x > 0.5)
    {
        float4 cloud = CloudMap.Sample(LinearClamp, IN.uv);
        if (CloudLayerParams.z > 0.0)
        {
            float2 skyNdc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
            float4 skyWorldH = mul(float4(skyNdc, 1.0, 1.0), InvViewProjection);
            float3 ray = normalize(skyWorldH.xyz / max(skyWorldH.w, 0.0001) - CameraPosPad.xyz);
            float boundary = CameraPosPad.y > CloudLayerParams.y ? CloudLayerParams.y : CloudLayerParams.x;
            float distance = abs(ray.y) > 0.0001 ? max(0.0, (boundary - CameraPosPad.y) / ray.y) : 100000.0;
            if (CameraPosPad.y >= CloudLayerParams.x && CameraPosPad.y <= CloudLayerParams.y) distance = 0.0;
            // Aerial transmittance acts on premultiplied radiance and opacity together,
            // so distant cloud silhouettes converge smoothly into the horizon sky.
            cloud *= exp(-distance * CloudLayerParams.z);
        }
        float cloudDepth = FetchSceneDepth(IN.uv, pixel);
        if (cloudDepth < 0.99999)
        {
            float2 cloudNdc = float2(IN.uv.x * 2.0 - 1.0, 1.0 - IN.uv.y * 2.0);
            float4 cloudWorldH = mul(float4(cloudNdc, cloudDepth, 1.0), InvViewProjection);
            float surfaceY = cloudWorldH.y / max(cloudWorldH.w, 0.0001);
            // Bilinear cloud upsampling can borrow sky opacity across an object's edge.
            // A ray ending before the cloud slab must remain completely unaffected.
            if (max(CameraPosPad.y, surfaceY) < CloudLayerParams.x
                || min(CameraPosPad.y, surfaceY) > CloudLayerParams.y)
                cloud = float4(0, 0, 0, 0);
        }
        if (CloudCompositeParams.z > 0.5)
            color.rgb *= 1.0 - saturate(cloud.a);
        color.rgb += cloud.rgb * CloudCompositeParams.y;
    }

    // AF1.7 HDR bloom add (pre-fog scene extract in v1) → exposure → mild grade → vignette → ACES.
    // Defaults are identity: bloom off, exposure/contrast/saturation 1, vignette 0. ACES stays
    // the sole tonemap — nothing here stacks a second filmic curve.
    if (BloomParams.x > 0.5)
        color.rgb += BloomMap.Sample(LinearClamp, IN.uv).rgb * BloomParams.y;

    color.rgb *= ExposureParams.x;

    float mid = 0.18;
    float3 graded = (color.rgb - mid) * ExposureParams.y + mid;
    float luma = dot(graded, float3(0.2126, 0.7152, 0.0722));
    color.rgb = lerp(luma.xxx, graded, ExposureParams.z);

    if (VignetteParams.x > 0.001)
    {
        float2 d = IN.uv - 0.5;
        float dist = length(d);
        float t = saturate(dist * 1.41421356);
        float attenuation = 1.0 - t * t;
        color.rgb *= saturate(pow(max(attenuation, 0.0), saturate(VignetteParams.x)));
    }

    // Always-on filmic tonemap, independent of whether fog is enabled. The scene buffer is now
    // HDR (FP16), so bright highlights (sun disc, emissive fire/magic/embers, bright sky) roll
    // off naturally here instead of being hard-clipped at 1.0 like the old 8-bit pipeline.
    float3 mapped = ACESFilm(color);
    return float4(mapped, scene.a);
}
";
    }
}

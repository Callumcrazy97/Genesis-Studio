namespace Genesis.Rendering.Primitives
{
    internal static class TerrainShader
    {
        public const string Source = @"
// ── Constant buffers ─────────────────────────────────────────────────────────

cbuffer PerFrameConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 LightViewProjection;      // far cascade light VP
    row_major float4x4 LightViewProjectionNear;  // near cascade light VP
    float4             CameraPosTime;
};

struct PointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    // Three scalars rather than float3: std140 aligns a 3-component vector to 16, which would
    // place the pad at offset 48 instead of 36 and make EngineConstants inexpressible in GLSL.
    float Falloff; float _pad0; float _pad1; float _pad2;
};

// Issue 6 Stage 1: analytic placeable fog volume — same layout as FogPostShaders.cs's /
// ForwardShaders.cs's FogVolumeGpu (must match ForwardRenderer's FogVolumeData field-for-
// field). Needed here so terrain's own fog (below) can fold in placed fog volumes too,
// consistent with every other surface in the scene.
struct FogVolumeGpu { float4 CenterDensity; float4 ExtentsFalloff; float4 ColorShape; float4 KindPad; };

cbuffer EngineConstants : register(b1)
{
    float4     LightDirEnabled;
    float4     FogParams;
    float4     FogColor;
    float4     AmbientColor;       // hemisphere sky term (normal points up)
    float4     AmbientGroundColor; // hemisphere ground term (normal points down)
    float4     SunColorIntensity;
    float4     FogParams2;
    float4     ShadowParams;         // x=active, y=bias, z=texelFar, w=highQuality
    float4     EffectParams;
    float4     VolumetricParams;
    float4     ViewportParams;
    float4     ShadowCascadeParams;  // x=near split, y=mid split (mode=2) or texel (mode=1), z=mode, w=strength
    float4     PointLightCounts;     // x=numLights
    PointLight PointLights[8];
    // Trailing fields below are only needed now that terrain's own RayMarchFog (fog rewrite)
    // consults placed fog volumes too — must stay a valid *prefix* of the C# EngineCB struct
    // in exact field order, same rule as PointLights above and as ForwardShaders.cs.
    float4       FogVolumeCounts;    // x=numFogVolumes
    FogVolumeGpu FogVolumes[8];
};

cbuffer DrawConstants : register(b2)
{
    row_major float4x4 WorldMatrix;
    float4             MaterialColor;
    float4             MaterialParams;  // x=emissive, y=unlit, z=isFloor, w=useInstancing
    uint               InstanceOffset;
    float              NoFog;
    // NoDepthWrite (fog rewrite): mirrors ForwardShaders.cs's DrawConstants field-for-field so
    // both shaders share one cbuffer layout. Terrain always writes depth normally, so this is
    // always 0 in practice for terrain draws — kept here purely for layout parity.
    float              NoDepthWrite;
    float              _padDraw;
};

// ── Resources ────────────────────────────────────────────────────────────────

Texture2D                      SplatMap        : register(t1);
Texture2D<float>               ShadowMapFar    : register(t2);
// TBD: Texture2D for 4 splat channels
// Texture2D                      AlbedoR         : register(t3);
// Texture2D                      AlbedoG         : register(t4);
// Texture2D                      AlbedoB         : register(t5);
// Texture2D                      AlbedoA         : register(t6);

Texture2D<float>               ShadowMapNear   : register(t7);

SamplerState                   AlbedoSamp      : register(s0);
SamplerComparisonState         ShadowSamp      : register(s1);

// ── Vertex structs ───────────────────────────────────────────────────────────

struct VSIn
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float4 Color    : COLOR;
    float2 UV       : TEXCOORD0;
};

struct VSOut
{
    float4 SvPos       : SV_Position;
    float3 WorldPos    : TEXCOORD1;
    float3 Normal      : TEXCOORD2;
    float4 Color       : TEXCOORD3;
    float2 UV          : TEXCOORD4;
    float4 ShadowPos   : TEXCOORD5;  // far cascade
    float4 ShadowPosNr : TEXCOORD6;  // near cascade
};

// ── Vertex Shader ────────────────────────────────────────────────────────────

VSOut VS_Terrain(VSIn IN)
{
    VSOut OUT;
    
    float4 worldPos = mul(float4(IN.Position, 1.0), WorldMatrix);
    OUT.WorldPos = worldPos.xyz;
    OUT.SvPos = mul(worldPos, ViewProjection);
    
    float3x3 worldRot = (float3x3)WorldMatrix;
    OUT.Normal = normalize(mul(IN.Normal, worldRot));
    
    OUT.Color = IN.Color * MaterialColor;
    OUT.UV = IN.UV;
    
    OUT.ShadowPos   = mul(worldPos, LightViewProjection);
    OUT.ShadowPosNr = mul(worldPos, LightViewProjectionNear);
    
    return OUT;
}

// ── Pixel Shader ─────────────────────────────────────────────────────────────

float CalculateShadow(float4 shadowPos, float4 shadowPosNr, float3 worldPos, float NdotL)
{
    if (ShadowParams.x < 0.5) return 1.0;

    float depthFar = shadowPos.z / shadowPos.w;
    if (depthFar < 0.0 || depthFar > 1.0) return 1.0;

    bool useNear = (ShadowCascadeParams.z > 0.5) && (distance(worldPos, CameraPosTime.xyz) < ShadowCascadeParams.x);

    float4 sPos = useNear ? shadowPosNr : shadowPos;
    sPos.xyz /= sPos.w;

    if (sPos.x < 0.0 || sPos.x > 1.0 || sPos.y < 0.0 || sPos.y > 1.0 || sPos.z < 0.0 || sPos.z > 1.0)
        return 1.0;

    float texelSize = ShadowParams.z;
    // R7.11: ShadowParams.y carries texel-aware bias; keep prior N·L slope term.
    float bias = ShadowParams.y * (1.0 - NdotL);
    float cmp = sPos.z - bias;

    if (ShadowParams.w > 0.5) // PCF
    {
        float sum = 0.0;
        [unroll] for (int x = -1; x <= 1; x++)
        {
            [unroll] for (int y = -1; y <= 1; y++)
            {
                float2 shadowUv = sPos.xy + float2(x, y) * texelSize;
                sum += useNear
                    ? ShadowMapNear.SampleCmpLevelZero(ShadowSamp, shadowUv, cmp)
                    : ShadowMapFar.SampleCmpLevelZero(ShadowSamp, shadowUv, cmp);
            }
        }
        return sum / 9.0;
    }
    else
    {
        return useNear
            ? ShadowMapNear.SampleCmpLevelZero(ShadowSamp, sPos.xy, cmp)
            : ShadowMapFar.SampleCmpLevelZero(ShadowSamp, sPos.xy, cmp);
    }
}

// ── Unified volumetric fog model (full rewrite) ─────────────────────────────
// Textual duplicate of FogPostShaders.cs's / ForwardShaders.cs's ray-march model (no
// #include in these raw HLSL strings, so all three copies must stay in sync). Replaces the
// previous fog block here, which read FogParams.x/VolumetricParams.x/y/z/w as if they still
// held an old pre-rewrite layout (density/height-fog fields) when those slots had long since
// been repurposed elsewhere (FogParams.x is the fog-enabled flag, VolumetricParams.x/y are
// runVolumetricThisFrame/quality) — that mismatch made this block silently wrong on real
// frames, masked only because terrain is opaque and the screen-space post-process fog (the
// correct path) was already overriding its output.

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

// Interleaved Gradient Noise (Jimenez, 2014) — see FogPostShaders.cs for full comment. Used here
// (instead of a hash seeded from mesh texture UV) to dither the forward self-fog ray march's
// per-step start offset from real screen pixel coords.
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

// Sun visibility at an arbitrary world point, sampled from the cascaded shadow maps already
// bound for CalculateShadow above. Returns 1.0 (fully lit) when shadows are off or the point
// falls outside both cascades.
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

// Issue 6 Stage 1: per-shape ""inside"" factor in [0,1]. Shapes: 0=Box, 1=Sphere, 2=Ellipsoid,
// 3=HeightSlab. Unchanged from FogPostShaders.cs's copy.
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

    float d = max(max(abs(n.x), abs(n.y)), abs(n.z));
    return saturate(1.0 - d);
}

// Accumulates all active placeable fog volumes at worldPos. Unchanged from FogPostShaders.cs's
// copy — see there for full comments.
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

// Marches from cameraPos toward viewDir over rayLength, accumulating Beer-Lambert
// transmittance and shadow-aware inscattered light. Composite at the call site with:
//   color = color * transmittance + outInscatter;
// Unchanged from FogPostShaders.cs's copy — see there for full comments.
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
        float lightAmt = lerp(0.35, 1.0, vis) * (1.0 - sunDot * sunPreserve);

        float3 stepColor = volDensity > 0.0001
            ? lerp(FogColor.rgb, volColor, saturate(volDensity / max(density, 0.0001)))
            : FogColor.rgb;

        float stepTransmittance = exp(-density * stepLen);
        outInscatter += stepColor * lightAmt * (1.0 - stepTransmittance) * transmittance;
        transmittance *= stepTransmittance;

        t += stepLen;
    }

    return saturate(transmittance);
}

float4 PS_Terrain(VSOut IN) : SV_Target
{
    // Phase 1: Basic diffuse shading (no splatmap blending yet)
    float3 albedo = float3(0.34, 0.52, 0.30) * IN.Color.rgb; 
    
    float3 N = normalize(IN.Normal);
    float3 L = -normalize(LightDirEnabled.xyz);
    float NdotL = max(0.0, dot(N, L));

    float shadow = 1.0;
    if (LightDirEnabled.w > 0.5)
    {
        shadow = lerp(
            1.0,
            CalculateShadow(IN.ShadowPos, IN.ShadowPosNr, IN.WorldPos, NdotL),
            saturate(ShadowCascadeParams.w) * saturate(LightDirEnabled.w));
    }

    // Hemisphere ambient
    float upWeight = N.y * 0.5 + 0.5;
    float3 ambient = lerp(AmbientGroundColor.rgb, AmbientColor.rgb, upWeight);

    // Directional
    float3 direct = SunColorIntensity.rgb * SunColorIntensity.a * NdotL * shadow;

    // Point lights
    float3 pointLightContrib = float3(0, 0, 0);
    uint numLights = (uint)(PointLightCounts.w + 0.5);
    for (uint i = 0; i < numLights; i++)
    {
        float3 toLight = PointLights[i].Pos - IN.WorldPos;
        float dist = length(toLight);
        if (dist < PointLights[i].Radius)
        {
            float3 L_point = toLight / dist;
            float atten = 1.0 - (dist / PointLights[i].Radius);
            atten = pow(atten, max(PointLights[i].Falloff, 0.05));
            float ndotlPoint = max(0.0, dot(N, L_point));
            pointLightContrib += PointLights[i].Color * PointLights[i].Intensity * ndotlPoint * atten;
        }
    }

    float3 finalColor = lerp(
        albedo,
        albedo * (ambient + direct + pointLightContrib),
        saturate(LightDirEnabled.w));

    // Fog: self-applied ray-marched fog, same gating as ForwardShaders.cs — runs whenever fog is
    // on and this draw doesn't opt out, AND either the screen-space post-process isn't running
    // this frame (EffectParams.z < 0.5) or this draw didn't write depth (NoDepthWrite > 0.5,
    // always false for terrain in practice). On a normal frame terrain is opaque and the
    // screen-space post-process already fogs it correctly from the depth buffer, so this block
    // is mainly the no-post-process fallback path — but it's now using the real unified model
    // instead of the stale/wrong field-layout math that used to live here.
    if (NoFog < 0.5 && FogParams.x > 0.5 && (EffectParams.z < 0.5 || NoDepthWrite > 0.5))
    {
        float3 viewDir = normalize(IN.WorldPos - CameraPosTime.xyz);
        float rayLength = length(IN.WorldPos - CameraPosTime.xyz);
        float jitterSeed = InterleavedGradientNoise(IN.SvPos.xy);
        float3 inscatter;
        float transmittance = RayMarchFog(CameraPosTime.xyz, viewDir, rayLength, LightDirEnabled.xyz, jitterSeed, inscatter);
        finalColor = lerp(finalColor, finalColor * transmittance + inscatter, saturate(FogColor.a));
    }

    return float4(finalColor, 1.0);
}
";
    }
}

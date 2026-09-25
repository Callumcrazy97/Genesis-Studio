namespace Genesis.Rendering.Primitives
{
    internal static class ForwardShaders
    {
        public const string Source = @"
// ── Constant buffers ─────────────────────────────────────────────────────────

cbuffer PerFrameConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 LightViewProjection;      // far cascade light VP
    row_major float4x4 LightViewProjectionNear;  // near cascade light VP
    float4             CameraPosTime;
    row_major float4x4 LightViewProjectionMid;   // AF1.1 mid cascade (appended)
};

struct PointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    // Three scalars rather than float3: std140 aligns a 3-component vector to 16, which would
    // place the pad at offset 48 instead of 36 and make EngineConstants inexpressible in GLSL.
    float Falloff; float _pad0; float _pad1; float _pad2;
};

// Clustered buffer copy of PointLight (R7.5). Same packing as PointLight / PointLightData.
struct ClusterPointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    float Falloff; float _pad0; float _pad1; float _pad2;
};

// Issue 6 Stage 1: analytic placeable fog volume — same layout as FogPostShaders.cs's
// FogVolumeGpu (must match Genesis.Rendering.Primitives.ForwardRenderer's FogVolumeData
// field-for-field). Needed here too now that RayMarchFog (below) folds placed fog volumes
// into the forward pass's own self-fog path, not just the screen-space post-process's.
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
    float4     ShadowCascadeParams;  // x=near split, y=mid split (or texel when mode=1), z=mode(1=2csm,2=3csm), w=strength
    float4     PointLightCounts;     // x=numLights
    PointLight PointLights[8];
    // Trailing fields below are only needed now that the forward pass's own RayMarchFog (fog
    // rewrite) consults placed fog volumes too — must stay a valid *prefix* of the C# EngineCB
    // struct in exact field order, same rule as PointLights above and as FogPostShaders.cs.
    float4       FogVolumeCounts;    // x=numFogVolumes
    FogVolumeGpu FogVolumes[8];
    // Stylized / toon lighting — global per frame (see SceneEnvironment.Stylized*).
    float4       StylizedParams;     // x=enabled, y=toonSteps, z=diffuseWrap, w=saturation
    float4       StylizedParams2;    // x=specularStrength, y=rimStrength, zw=reserved
    float4       WeatherWindRain;    // world wind XZ, rain, enabled
    float4       WeatherSurface;     // wetness, temperature C, snow, reserved
};

cbuffer DrawConstants : register(b2)
{
    row_major float4x4 WorldMatrix;
    float4             MaterialColor;
    float4             MaterialParams;  // x=emissive, y=unlit, z=isFloor, w=useInstancing
    uint               InstanceOffset;
    float              NoFog;            // Issue 6: per-material receives-fog opt-out flag
    // NoDepthWrite (fog rewrite): set whenever this specific draw doesn't write scene depth
    // (held items, particles, etc. — see ForwardRenderer.cs Submit()/Flush()). The screen-space
    // post-process fog can't see these pixels at all (its depth-buffer reconstruction reads
    // whatever the sky/background left behind), so the pixel shader below must self-apply
    // RayMarchFog for them regardless of whether post-process fog is otherwise active.
    float              NoDepthWrite;
    // TerrainGround (terrain rendering redesign): repurposes the last true-padding float.
    // Set only by SandboxTerrainGround's own draw call (see ForwardRenderer.cs Submit()/Flush()
    // and SandboxTerrainGround.cs) — tells PS() below to replace the sampled albedo with the
    // procedural slope/noise-driven grass+dirt-path blend (TerrainAlbedo) instead of the flat
    // material tint. Never a user-facing toggle — automatically derived from which system issued
    // the draw call.
    float              TerrainGround;
    // Foliage billboards: unlit alpha-cutout vegetation (see MeshDrawFlags.Foliage).
    float              Foliage;
    // Explicit tail padding for the 96..111 row. HLSL would insert this implicitly (a float4 may
    // not straddle a 16-byte boundary, so MaterialSurface must start at 128), but C#'s DrawCB has
    // to spell it out or the raw blit in UploadDrawCB shifts everything below by 12 bytes. Declared
    // on both sides so the layouts stay literally comparable — see Issues.md NEXT-066.
    // Three scalars rather than a float3: the 12 bytes are identical, but std140 aligns a
    // 3-component vector to 16, which would place this at a non-conforming offset and make the
    // block impossible to express in the GLSL the OpenGL backend translates to.
    float              MaterialRowPad0;
    float              MaterialRowPad1;
    float              MaterialRowPad2;
    float4             MaterialSurface;       // normal scale, height scale, emission, clearcoat
    float4             MaterialDetail;        // subsurface, flow speed, flow strength, UV scale
    float4             SubsurfaceAndSteps;     // RGB tint, POM steps
    float4             MaterialFeatures;       // has ORM, height mode, has emission, extras + 2*flow
    uint               SkinMatrixOffset;
    float              GpuSkinning;
    float              NoReceiveShadow;
    float              SkinPad1;
};

// ── Resources ────────────────────────────────────────────────────────────────

struct InstanceData { row_major float4x4 World; float4 Color; float4 AtlasData; };
struct SkinMatrixData { row_major float4x4 Matrix; };
StructuredBuffer<InstanceData> Instances       : register(t0);
Texture2D                      AlbedoTex       : register(t1);
Texture2D<float>               ShadowMapFar    : register(t2);  // far cascade / legacy shadow
Texture2D                      NormalMap       : register(t3);  // optional per-mesh normals
Texture2DArray                 AlbedoArray     : register(t4);  // texture atlas (p3d)
Texture2D<float>               ShadowMapNear   : register(t5);  // near cascade shadow
StructuredBuffer<ClusterPointLight> ClusterLights : register(t6);
Texture2D                      OrmMap          : register(t7);
Texture2D                      HeightMap       : register(t8);
Texture2D                      EmissionMap     : register(t9);
Texture2D                      ExtrasMap       : register(t10);
Texture2D                      FlowMap         : register(t11);
StructuredBuffer<SkinMatrixData> SkinMatrices  : register(t12);
StructuredBuffer<uint>           TileLightIndices : register(t13);
Texture2D<float>               ShadowMapMid    : register(t14); // AF1.1 mid cascade
Texture2D<float>               OmniFace0       : register(t15); // AF1.3 omni slot 0 faces
Texture2D<float>               OmniFace1       : register(t16);
Texture2D<float>               OmniFace2       : register(t17);
Texture2D<float>               OmniFace3       : register(t18);
Texture2D<float>               OmniFace4       : register(t19);
Texture2D<float>               OmniFace5       : register(t20);
SamplerState                   AlbedoSamp      : register(s0);
SamplerComparisonState         ShadowSamp      : register(s1);

cbuffer OmniShadowConstants : register(b4)
{
    row_major float4x4 OmniFaceVP[6];
    float4             OmniLightPosFar; // xyz = light pos, w = far
    float4             OmniParams;      // x = active
};

// ── Vertex structs ───────────────────────────────────────────────────────────

struct VSIn
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float4 Color    : COLOR;
    float2 UV       : TEXCOORD0;
};

struct VSInSkinned
{
    float3 Position     : POSITION;
    float3 Normal       : NORMAL;
    float4 Color        : COLOR;
    float2 UV           : TEXCOORD0;
    float4 BlendWeights : BLENDWEIGHT;
    float4 BlendIndices : BLENDINDICES;
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
    float  AtlasLayer  : TEXCOORD7;  // texture array layer (0 = AlbedoTex)
    float4 ShadowPosMd : TEXCOORD8;  // AF1.1 mid cascade
};

// ── Vertex shaders ───────────────────────────────────────────────────────────

VSOut VS(VSIn IN, uint instanceId : SV_InstanceID)
{
    float4x4 world;
    float4   instColor;
    float    atlasLayer = 0.0;

    if (MaterialParams.w > 0.5)
    {
        uint idx  = instanceId + InstanceOffset;
        world     = Instances[idx].World;
        instColor = Instances[idx].Color;
        atlasLayer = Instances[idx].AtlasData.x;
        if (length(world[0]) < 0.001) world = WorldMatrix;
        if (length(instColor) < 0.001) instColor = float4(1, 1, 1, 1);
    }
    else
    {
        world     = WorldMatrix;
        instColor = float4(1, 1, 1, 1);
    }

    float3 localPos = IN.Position;
    if (MaterialFeatures.y > 2.5)
    {
        float uvScale = MaterialDetail.w > 0.0001 ? MaterialDetail.w : 1.0;
        float height = HeightMap.SampleLevel(AlbedoSamp, IN.UV * uvScale, 0).r - 0.5;
        localPos += IN.Normal * height * MaterialSurface.y;
    }

    // Terrain-editor foliage wind: keep blade roots anchored while the upper cards sway.
    // Height is normalised against a short meadow blade (~0.55m) so Play-mode wind is visible
    // on grassy fields, not only on 2m reeds. Driven by the shared 3D preview time.
    if (Foliage > 0.5)
    {
        float bladeT = saturate(localPos.y / 0.55);
        float bend = bladeT * bladeT;
        float phase = CameraPosTime.w * 2.65 + world._41 * 0.37 + world._43 * 0.29;
        float2 wind = WeatherWindRain.xy;
        float windStrength = WeatherWindRain.w > 0.5 ? saturate(length(wind) / 10.0) : 1.0;
        float2 direction = length(wind) > 0.001 ? normalize(wind) : float2(1, 0);
        float2 sway = direction * sin(phase) * 0.22 + float2(-direction.y, direction.x) * cos(phase * 0.83) * 0.10;
        localPos.xz += sway * bend * windStrength;
    }

    float4 worldPos  = mul(float4(localPos, 1.0), world);
    float3 worldNrm  = normalize(mul(float4(IN.Normal, 0.0), world).xyz);

    // Normal-offset shadow bias: push the sample point used for the shadow lookup
    // along the surface normal before projecting into light space. This kills
    // shadow-acne speckles on steep slopes (the terrain mountain) far more reliably
    // than depth bias alone, without introducing visible peter-panning at this scale.
    float3 shadowSamplePos = worldPos.xyz + worldNrm * 0.08;

    VSOut OUT;
    OUT.SvPos       = mul(worldPos, ViewProjection);
    OUT.WorldPos    = worldPos.xyz;
    OUT.Normal      = worldNrm;
    OUT.Color       = IN.Color * instColor;
    OUT.UV          = IN.UV;
    OUT.ShadowPos   = mul(float4(shadowSamplePos, 1.0), LightViewProjection);
    OUT.ShadowPosNr = mul(float4(shadowSamplePos, 1.0), LightViewProjectionNear);
    OUT.AtlasLayer  = atlasLayer;
    OUT.ShadowPosMd = mul(float4(shadowSamplePos, 1.0), LightViewProjectionMid);
    return OUT;
}

void SkinLocal(VSInSkinned IN, out float3 localPos, out float3 localNrm)
{
    float totalWeight = IN.BlendWeights.x + IN.BlendWeights.y + IN.BlendWeights.z + IN.BlendWeights.w;
    if (totalWeight <= 0.0001)
    {
        localPos = IN.Position;
        localNrm = IN.Normal;
        return;
    }

    float4 skinnedPos = 0;
    float3 skinnedNrm = 0;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float w = IN.BlendWeights[i];
        if (w <= 0.0001)
            continue;
        uint idx = (uint)round(IN.BlendIndices[i]) + SkinMatrixOffset;
        float4x4 skin = SkinMatrices[idx].Matrix;
        skinnedPos += mul(float4(IN.Position, 1.0), skin) * w;
        skinnedNrm += mul(float4(IN.Normal, 0.0), skin).xyz * w;
    }

    localPos = skinnedPos.xyz / max(totalWeight, 0.0001);
    localNrm = normalize(skinnedNrm);
}

VSOut VS_Skinned(VSInSkinned IN, uint instanceId : SV_InstanceID)
{
    float3 skinnedLocalPos;
    float3 skinnedLocalNrm;
    SkinLocal(IN, skinnedLocalPos, skinnedLocalNrm);

    float4x4 world;
    float4   instColor;
    float    atlasLayer = 0.0;

    if (MaterialParams.w > 0.5)
    {
        uint idx  = instanceId + InstanceOffset;
        world     = Instances[idx].World;
        instColor = Instances[idx].Color;
        atlasLayer = Instances[idx].AtlasData.x;
    }
    else
    {
        world     = WorldMatrix;
        instColor = float4(1, 1, 1, 1);
    }

    float3 localPos = skinnedLocalPos;
    if (MaterialFeatures.y > 2.5)
    {
        float uvScale = MaterialDetail.w > 0.0001 ? MaterialDetail.w : 1.0;
        float height = HeightMap.SampleLevel(AlbedoSamp, IN.UV * uvScale, 0).r - 0.5;
        localPos += skinnedLocalNrm * height * MaterialSurface.y;
    }

    float4 worldPos  = mul(float4(localPos, 1.0), world);
    float3 worldNrm  = normalize(mul(float4(skinnedLocalNrm, 0.0), world).xyz);
    float3 shadowSamplePos = worldPos.xyz + worldNrm * 0.08;

    VSOut OUT;
    OUT.SvPos       = mul(worldPos, ViewProjection);
    OUT.WorldPos    = worldPos.xyz;
    OUT.Normal      = worldNrm;
    OUT.Color       = IN.Color * instColor;
    OUT.UV          = IN.UV;
    OUT.ShadowPos   = mul(float4(shadowSamplePos, 1.0), LightViewProjection);
    OUT.ShadowPosNr = mul(float4(shadowSamplePos, 1.0), LightViewProjectionNear);
    OUT.AtlasLayer  = atlasLayer;
    OUT.ShadowPosMd = mul(float4(shadowSamplePos, 1.0), LightViewProjectionMid);
    return OUT;
}

float4 VS_Shadow(VSIn IN, uint instanceId : SV_InstanceID) : SV_Position
{
    float4x4 world;
    if (MaterialParams.w > 0.5)
    {
        uint idx = instanceId + InstanceOffset;
        world = Instances[idx].World;
    }
    else
    {
        world = WorldMatrix;
    }
    float4 worldPos = mul(float4(IN.Position, 1.0), world);
    return mul(worldPos, LightViewProjection);
}

float4 VS_ShadowMid(VSIn IN, uint instanceId : SV_InstanceID) : SV_Position
{
    float4x4 world;
    if (MaterialParams.w > 0.5)
    {
        uint idx = instanceId + InstanceOffset;
        world = Instances[idx].World;
    }
    else
    {
        world = WorldMatrix;
    }
    float4 worldPos = mul(float4(IN.Position, 1.0), world);
    return mul(worldPos, LightViewProjectionMid);
}

float4 VS_ShadowNear(VSIn IN, uint instanceId : SV_InstanceID) : SV_Position
{
    float4x4 world;
    if (MaterialParams.w > 0.5)
    {
        uint idx = instanceId + InstanceOffset;
        world = Instances[idx].World;
    }
    else
    {
        world = WorldMatrix;
    }
    float4 worldPos = mul(float4(IN.Position, 1.0), world);
    return mul(worldPos, LightViewProjectionNear);
}

// ── Helpers ──────────────────────────────────────────────────────────────────

float Hash21(float2 p)
{
    // Lattice integer hash. The old sin() hash of large values is not bit-stable on D3D
    // even with identical inputs, so fog noise shimmered between otherwise identical frames.
    uint n = asuint(floor(p.x)) * 1597334677u ^ asuint(floor(p.y)) * 3812015801u;
    n ^= n >> 16u;
    n *= 0x7feb352du;
    n ^= n >> 15u;
    n *= 0x846ca68bu;
    n ^= n >> 16u;
    return n * 2.3283064365386963e-10;
}

// Per-block atlas UV for greedy-merged voxel faces (Color.w < 0 encodes packed atlas bounds).
float2 VoxelFaceBlockUV(float3 wp, float3 n)
{
    if (n.y > 0.707) return float2(frac(wp.x), frac(wp.z));
    if (n.y < -0.707) return float2(frac(wp.x), 1.0 - frac(wp.z));
    if (n.x > 0.707) return float2(frac(wp.z), 1.0 - frac(wp.y));
    if (n.x < -0.707) return float2(1.0 - frac(wp.z), 1.0 - frac(wp.y));
    if (n.z > 0.707) return float2(1.0 - frac(wp.x), 1.0 - frac(wp.y));
    return float2(frac(wp.x), 1.0 - frac(wp.y));
}

// Interleaved Gradient Noise (Jimenez, 2014) — see FogPostShaders.cs for full comment. Used here
// (instead of a hash seeded from mesh texture UV, which correlated grain with texture tiling) to
// dither the forward self-fog ray march's per-step start offset from real screen pixel coords.
float InterleavedGradientNoise(float2 pixelCoord)
{
    pixelCoord = floor(pixelCoord);
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pixelCoord, magic.xy)));
}

float NoiseXZ(float2 xz)
{
    float2 uv = xz * 0.05;
    return Hash21(uv) * 0.5 + Hash21(uv * 2.3 + 17.0) * 0.35 + Hash21(uv * 5.1 - 4.0) * 0.15;
}

// Sample one shadow cascade with optional PCF. Keep the texture and sampler as global resources:
// older wgpu-native/Naga SPIR-V frontends cannot represent opaque image/sampler handles passed as
// ordinary function parameters even though Vulkan accepts the DXC output.
float SampleShadowMapFar(float4 shadowCoord, float bias, float texel, bool highQ)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;

    proj.x =  proj.x * 0.5 + 0.5;
    proj.y = -proj.y * 0.5 + 0.5;

    float sum   = 0.0;
    float count = 0.0;
    int   radius = highQ ? 1 : 0;
    [loop]
    for (int y = -radius; y <= radius; y++)
    {
        [loop]
        for (int x = -radius; x <= radius; x++)
        {
            float2 uv = proj.xy + float2(x, y) * texel;
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                sum += 1.0;
            else
                sum += ShadowMapFar.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
            count += 1.0;
        }
    }
    return sum / max(count, 1.0);
}

float SampleShadowMapNear(float4 shadowCoord, float bias, float texel, bool highQ)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;

    proj.x =  proj.x * 0.5 + 0.5;
    proj.y = -proj.y * 0.5 + 0.5;

    float sum   = 0.0;
    float count = 0.0;
    int   radius = highQ ? 1 : 0;
    [loop]
    for (int y = -radius; y <= radius; y++)
    {
        [loop]
        for (int x = -radius; x <= radius; x++)
        {
            float2 uv = proj.xy + float2(x, y) * texel;
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                sum += 1.0;
            else
                sum += ShadowMapNear.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
            count += 1.0;
        }
    }
    return sum / max(count, 1.0);
}

float SampleShadowMapMid(float4 shadowCoord, float bias, float texel, bool highQ)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;
    proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
    if (proj.z <= 0.0 || proj.z >= 1.0) return 1.0;
    if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0) return 1.0;

    int radius = highQ ? 2 : 1;
    float sum = 0.0;
    float count = 0.0;
    [loop]
    for (int y = -radius; y <= radius; y++)
    {
        [loop]
        for (int x = -radius; x <= radius; x++)
        {
            float2 uv = proj.xy + float2(x, y) * texel;
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                sum += 1.0;
            else
                sum += ShadowMapMid.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
            count += 1.0;
        }
    }
    return sum / max(count, 1.0);
}

// Cascaded shadow map: prefer near, then mid (AF1.1), then far.
float SampleShadowCSM(float4 shadowPosFar, float4 shadowPosNear, float4 shadowPosMid,
                      float3 normal, float3 lightDir)
{
    if (ShadowParams.x < 0.5)
        return 1.0;

    float ndotl  = saturate(dot(normalize(normal), normalize(-lightDir)));
    // R7.11: texel-aware bias from CPU in ShadowParams.y; slope from glancing N·L.
    float bias   = ShadowParams.y + ShadowParams.y * (1.0 - ndotl);
    bool  highQ  = ShadowParams.w > 0.5;
    float texel  = ShadowParams.z;

    // Try near cascade first (smaller bias — higher texel density).
    if (ShadowCascadeParams.z > 0.5)
    {
        float3 nearNdc = shadowPosNear.xyz / shadowPosNear.w;
        float nearBias = bias * 0.5;
        if (nearNdc.z > 0.0 && nearNdc.z < 1.0)
        {
            float nearU =  nearNdc.x * 0.5 + 0.5;
            float nearV = -nearNdc.y * 0.5 + 0.5;
            if (nearU >= 0.0 && nearU <= 1.0 && nearV >= 0.0 && nearV <= 1.0)
            {
                float nearTexel = ShadowCascadeParams.z < 1.5 ? ShadowCascadeParams.y : texel;
                return SampleShadowMapNear(shadowPosNear, nearBias, nearTexel, highQ);
            }
        }
    }

    // AF1.1 mid cascade when mode >= 2.
    if (ShadowCascadeParams.z > 1.5)
    {
        float3 midNdc = shadowPosMid.xyz / shadowPosMid.w;
        float midBias = bias * 0.75;
        if (midNdc.z > 0.0 && midNdc.z < 1.0)
        {
            float midU =  midNdc.x * 0.5 + 0.5;
            float midV = -midNdc.y * 0.5 + 0.5;
            if (midU >= 0.0 && midU <= 1.0 && midV >= 0.0 && midV <= 1.0)
                return SampleShadowMapMid(shadowPosMid, midBias, texel, highQ);
        }
    }

    // Fall back to far cascade.
    return SampleShadowMapFar(shadowPosFar, bias, texel, highQ);
}

// Compat overload for call sites that only pass far+near (terrain/fog self-shadow helpers).
float SampleShadowCSM(float4 shadowPosFar, float4 shadowPosNear,
    float3 normal, float3 lightDir)
{
    return SampleShadowCSM(shadowPosFar, shadowPosNear, shadowPosFar, normal, lightDir);
}

// Compute point-light contribution via the R7.5 screen-tile list (bounded per pixel).
float SampleOmniFaceCmp(int face, float2 uv, float cmp)
{
    if (face == 0) return OmniFace0.SampleCmpLevelZero(ShadowSamp, uv, cmp);
    if (face == 1) return OmniFace1.SampleCmpLevelZero(ShadowSamp, uv, cmp);
    if (face == 2) return OmniFace2.SampleCmpLevelZero(ShadowSamp, uv, cmp);
    if (face == 3) return OmniFace3.SampleCmpLevelZero(ShadowSamp, uv, cmp);
    if (face == 4) return OmniFace4.SampleCmpLevelZero(ShadowSamp, uv, cmp);
    return OmniFace5.SampleCmpLevelZero(ShadowSamp, uv, cmp);
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

float SampleOmniVisibility(float3 worldPos, float3 lightPos)
{
    float3 Lvec = worldPos - lightPos;
    float len = length(Lvec);
    if (len < 1e-5)
        return 1.0;
    float3 dir = Lvec / len;
    int face = OmniCubeFaceIndex(dir);
    float4 clip = mul(float4(worldPos, 1.0), OmniFaceVP[face]);
    float3 proj = clip.xyz / max(clip.w, 1e-5);
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;
    proj.x =  proj.x * 0.5 + 0.5;
    proj.y = -proj.y * 0.5 + 0.5;
    if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
        return 1.0;
    float bias = max(0.0015, 0.02 * (1.0 - saturate(len / max(OmniLightPosFar.w, 0.001))));
    return SampleOmniFaceCmp(face, proj.xy, proj.z - bias);
}

float3 ShadeOnePointLight(ClusterPointLight L, float3 worldPos, float3 n, float3 base)
{
    float3 toLight = L.Pos - worldPos;
    float dist     = length(toLight);
    float radius   = max(L.Radius, 0.001);
    if (dist >= radius) return float3(0, 0, 0);

    float attenuation = 1.0 - saturate(dist / radius);
    attenuation = pow(attenuation, max(L.Falloff, 0.05));
    float3 l   = normalize(toLight);
    float ndotl = saturate(dot(n, l));
    float3 lit = base * ndotl * L.Color * L.Intensity * attenuation;

    // AF1.3: FalloffPad.Y / _pad0 marks the omni slot-0 light when GPU maps are active.
    if (L._pad0 > 0.5 && OmniParams.x > 0.5)
        lit *= SampleOmniVisibility(worldPos, L.Pos);
    return lit;
}

float3 ComputePointLights(float3 worldPos, float3 n, float3 base, float4 svPos)
{
    int nLights = (int)PointLightCounts.x;
    int tileGrid = (int)PointLightCounts.y;
    int maxPerTile = (int)PointLightCounts.z;
    float3 result = float3(0, 0, 0);
    if (nLights <= 0) return result;

    if (tileGrid >= 2 && maxPerTile > 0)
    {
        float2 uv = svPos.xy * ViewportParams.zw;
        int tx = clamp((int)floor(uv.x * tileGrid), 0, tileGrid - 1);
        int ty = clamp((int)floor(uv.y * tileGrid), 0, tileGrid - 1);
        int baseIdx = (ty * tileGrid + tx) * maxPerTile;
        [loop]
        for (int i = 0; i < 32; i++)
        {
            if (i >= maxPerTile) break;
            uint li = TileLightIndices[baseIdx + i];
            if (li == 0xffffffffu) break;
            if (li >= (uint)nLights) continue;
            result += ShadeOnePointLight(ClusterLights[li], worldPos, n, base);
        }
        return result;
    }

    // Fallback: EngineCB PointLights[8] (fog/Software path still fills these).
    int cbLights = min((int)PointLightCounts.w, 8);
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        if (i >= cbLights) break;
        ClusterPointLight L;
        L.Pos = PointLights[i].Pos;
        L.Radius = PointLights[i].Radius;
        L.Color = PointLights[i].Color;
        L.Intensity = PointLights[i].Intensity;
        L.Falloff = PointLights[i].Falloff;
        L._pad0 = PointLights[i]._pad0;
        L._pad1 = PointLights[i]._pad1;
        L._pad2 = PointLights[i]._pad2;
        result += ShadeOnePointLight(L, worldPos, n, base);
    }
    return result;
}

// ── Unified volumetric fog model (full rewrite) ─────────────────────────────
// Textual duplicate of FogPostShaders.cs's ray-march model (no #include in these raw HLSL
// strings, so all three copies — here, TerrainShader.cs, FogPostShaders.cs — must stay in
// sync). The forward pass needs its own copy because it must be able to self-fog a draw that
// the screen-space post-process structurally cannot see (no scene depth was written for it),
// using the object's own real WorldPos/distance rather than a depth-buffer reconstruction.

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

// Sun visibility at an arbitrary world point (not just the surface this pixel shades),
// sampled from the cascaded shadow maps already bound for SampleShadowCSM above. Returns 1.0
// (fully lit) when shadows are off or the point falls outside both cascades.
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

    [unroll]
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

// ── Pixel shader ─────────────────────────────────────────────────────────────

// Two outputs: the lit scene colour, and a binary ""already self-fogged"" flag written 1.0
// whenever this pixel applies the forward self-fog branch below. FogPostShaders.cs's screen-
// space post pass samples that flag and skips re-fogging those pixels — without it, the post
// pass would reconstruct world position from whatever's actually BEHIND a NoDepthWrite draw
// (it never wrote scene depth) and apply a second, much heavier fog pass on top using that wrong
// background distance, crushing near-camera objects/particles to the fog colour regardless of
// density. See ForwardRenderer.cs MainPass/EnsureSceneTargets for the matching render target.
struct PSOut
{
    float4 Color       : SV_Target0;
    float  SkipPostFog : SV_Target1;
};

PSOut MakeOut(float4 c, float skip)
{
    PSOut o;
    o.Color = c;
    o.SkipPostFog = skip;
    return o;
}

// ── Terrain ground procedural shading (terrain rendering redesign) ─────────────
// Authored terrain stores normalized RGBA splat weights in vertex colour: grass, path/soil, rock
// and moss/biome. Sandbox terrain still supplies white vertices and keeps the procedural fallback.

float TerrainValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + float2(1.0, 0.0));
    float c = Hash21(i + float2(0.0, 1.0));
    float d = Hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float TerrainFbm(float2 p)
{
    float v = 0.0;
    float amp = 0.55;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        v += TerrainValueNoise(p) * amp;
        p *= 2.07;
        amp *= 0.55;
    }
    return v;
}

// Returns the blended albedo and writes the [0,1] dirt-path mask actually used (so the caller
// could in principle reuse it — not currently consumed outside this function, but kept as an out
// param to mirror the equivalent CPU-side estimate SandboxTerrainGround.cs uses to bias where it
// scatters grass/pebble instances, so the two stay conceptually in sync).
//
// Biome extension: when the terrain mesh bakes elevation into vertex color (.r in [0,1]), the
// blend becomes elevation-driven — deep sea → shallow → beach → grassland → forest → heather →
// bare rock → snow — with slope and noise still breaking up the bands. If vertex color is the
// default white (.r == 1) the original grass+dirt slope rule is preserved, so SandboxTerrainGround
// and any non-baked terrain render unchanged.
float3 TerrainAlbedo(float3 worldPos, float3 normal, float4 vertColor, float3 detailTexture, out float dirtMaskOut)
{
    // Slope: 1 = flat (facing straight up), 0 = vertical. Steeper ground shows more bare
    // dirt/rock — the same ""auto slope rule"" the Terrain Editor Redesign doc proposes a future
    // painted splat map would override on a per-layer basis.
    float slope = saturate(normal.y);

    // Large-scale, gently domain-warped noise carves meandering dirt-path patches through the
    // grass, standing in for an authored path spline until the terrain editor can paint one.
    float2 warp = float2(TerrainFbm(worldPos.xz * 0.015 + 11.0), TerrainFbm(worldPos.xz * 0.015 - 7.0));
    float2 pathUv = worldPos.xz * 0.05 + warp * 2.2;
    float pathNoise = TerrainFbm(pathUv);
    float dirtMask = smoothstep(0.46, 0.58, pathNoise);
    dirtMask = saturate(dirtMask + (1.0 - slope) * 1.6); // steep faces always lean dirt/rock
    dirtMaskOut = dirtMask;

    // Fine speckle for per-pixel colour variation, so the ground reads as textured even before
    // the instanced grass/pebble detail geometry is factored in on top.
    float speck = TerrainValueNoise(worldPos.xz * 1.7) * 0.5 + TerrainValueNoise(worldPos.xz * 5.3) * 0.5;

    // Painted authored terrain. Interpolated vertex weights remain normalized across triangles;
    // the tiled source texture contributes fine luminance without replacing the authored palette.
    float splatSum = dot(vertColor, float4(1.0, 1.0, 1.0, 1.0));
    if (abs(splatSum - 1.0) < 0.12)
    {
        float4 weights = saturate(vertColor) / max(splatSum, 0.0001);
        float3 grassPaint = lerp(float3(0.105, 0.245, 0.070), float3(0.315, 0.535, 0.155), speck);
        float3 pathPaint  = lerp(float3(0.235, 0.135, 0.070), float3(0.505, 0.355, 0.185), speck);
        float3 rockPaint  = lerp(float3(0.245, 0.255, 0.265), float3(0.515, 0.505, 0.480), speck);
        float3 mossPaint  = lerp(float3(0.105, 0.205, 0.085), float3(0.385, 0.455, 0.175), speck);
        float3 painted = grassPaint * weights.r + pathPaint * weights.g
            + rockPaint * weights.b + mossPaint * weights.a;
        painted = lerp(painted, rockPaint, smoothstep(0.62, 0.28, slope) * 0.72);
        float detailLuma = dot(detailTexture, float3(0.299, 0.587, 0.114));
        dirtMaskOut = weights.g;
        return painted * lerp(0.82, 1.18, detailLuma);
    }

    // Default (no elevation baked): keep the classic grass+dirt slope blend.
    // vertColor.r < 0.999 signals an elevation-baked campaign terrain.
    if (vertColor.r > 0.999)
    {
        float3 grassDark  = float3(0.149, 0.314, 0.094);
        float3 grassLight = float3(0.392, 0.608, 0.224);
        float3 dirtDark   = float3(0.302, 0.220, 0.137);
        float3 dirtLight  = float3(0.486, 0.380, 0.243);
        float3 grass = lerp(grassDark, grassLight, saturate(speck * 0.9 + 0.15));
        float3 dirt  = lerp(dirtDark,  dirtLight,  saturate(speck * 0.8 + 0.25));
        return lerp(grass, dirt, dirtMask);
    }

    // ── Elevation-driven biome blend (campaign map) ───────────────────────────
    // vertColor.r is normalized elevation in [0,1]. Add gentle noise so band edges are soft.
    float elev = vertColor.r;
    elev += (TerrainFbm(worldPos.xz * 0.01) - 0.5) * 0.06;
    elev = saturate(elev);

    // Biome palette (muted, painterly).
    float3 deepSea   = float3(0.043, 0.117, 0.215);   // deep blue
    float3 shallow   = float3(0.176, 0.345, 0.470);   // teal shallows
    float3 beach     = float3(0.764, 0.701, 0.509);   // sand
    float3 grassLow  = float3(0.392, 0.545, 0.262);   // lowland grass
    float3 grassHi   = float3(0.333, 0.470, 0.215);
    float3 forest    = float3(0.196, 0.333, 0.149);   // forest green (dappled below)
    float3 heather   = float3(0.376, 0.317, 0.270);   // highland heather
    float3 rock      = float3(0.404, 0.380, 0.352);   // bare rock
    float3 snow      = float3(0.898, 0.905, 0.913);   // snow caps

    float forestDapple = saturate(TerrainFbm(worldPos.xz * 0.08) * 1.2);

    float3 col = deepSea;
    col = lerp(col, shallow, smoothstep(0.04, 0.10, elev));
    col = lerp(col, beach,   smoothstep(0.10, 0.14, elev));
    col = lerp(col, grassLow, smoothstep(0.14, 0.22, elev));
    col = lerp(col, lerp(grassHi, forest, forestDapple), smoothstep(0.22, 0.45, elev));
    col = lerp(col, heather, smoothstep(0.45, 0.66, elev));
    col = lerp(col, rock,    smoothstep(0.66, 0.84, elev));
    col = lerp(col, snow,    smoothstep(0.84, 0.95, elev));

    // Steep faces always reveal rock, regardless of elevation band.
    col = lerp(col, rock, smoothstep(0.55, 0.30, slope));

    // Fine speckle to avoid flat bands.
    col *= (0.90 + speck * 0.18);

    return col;
}

PSOut PS(VSOut IN, bool isFront : SV_IsFrontFace)
{
    float debugMode  = EffectParams.y;
    float3 cameraPos = CameraPosTime.xyz;
    float3 n         = normalize(IN.Normal);
    if (!isFront) n = -n;

    float uvScale = MaterialDetail.w > 0.0001 ? MaterialDetail.w : 1.0;
    float2 materialUv = IN.UV * uvScale;
    if (MaterialFeatures.w >= 2.0)
    {
        float3 flow = FlowMap.Sample(AlbedoSamp, materialUv).rgb;
        float2 direction = flow.rg * 2.0 - 1.0;
        materialUv += direction * flow.b * MaterialDetail.z * MaterialDetail.y * CameraPosTime.w;
    }
    if (MaterialFeatures.y > 0.5 && MaterialFeatures.y < 2.5)
    {
        float3 dp1p = ddx(IN.WorldPos), dp2p = ddy(IN.WorldPos);
        float2 duv1p = ddx(IN.UV), duv2p = ddy(IN.UV);
        float3 tangentP = normalize(dp1p * duv2p.y - dp2p * duv1p.y);
        tangentP = normalize(tangentP - dot(tangentP, n) * n);
        float3 bitangentP = cross(n, tangentP);
        float3 toCameraP = normalize(cameraPos - IN.WorldPos);
        float3 viewTs = float3(dot(toCameraP, tangentP), dot(toCameraP, bitangentP), dot(toCameraP, n));
        if (MaterialFeatures.y < 1.5)
        {
            float h = HeightMap.Sample(AlbedoSamp, materialUv).r - 0.5;
            materialUv -= viewTs.xy / max(abs(viewTs.z), 0.15) * h * MaterialSurface.y;
        }
        else
        {
            int steps = (int)clamp(SubsurfaceAndSteps.w, 4.0, 64.0);
            float layer = 1.0 / steps;
            float2 delta = viewTs.xy / max(abs(viewTs.z), 0.15) * MaterialSurface.y / steps;
            float depth = 0.0; float2 probe = materialUv;
            [loop] for (int pi = 0; pi < 64; pi++)
            {
                if (pi >= steps || depth >= 1.0 - HeightMap.Sample(AlbedoSamp, probe).r) break;
                probe -= delta; depth += layer;
            }
            materialUv = probe;
        }
    }

    // Debug views
    if (debugMode > 0.5 && debugMode < 1.5)
        return MakeOut(float4(n * 0.5 + 0.5, 1.0), 0.0);
    if (debugMode > 1.5 && debugMode < 2.5)
    {
        // Reproject world position instead of reading a component pointer from SV_Position
        // (unsupported by older SPIR-V frontends). Depth belongs to the camera, not the sun.
        float4 cameraPosition = mul(float4(IN.WorldPos, 1.0), ViewProjection);
        float cameraDepth = saturate(cameraPosition.z / max(cameraPosition.w, 0.0001));
        float debugDepth = pow(saturate(1.0 - cameraDepth), 0.25);
        return MakeOut(float4(debugDepth, debugDepth, debugDepth, 1.0), 0.0);
    }
    if (debugMode > 2.5)
    {
        float3 proj = IN.ShadowPos.xyz / max(IN.ShadowPos.w, 0.0001);
        proj.x = proj.x * 0.5 + 0.5;
        proj.y = -proj.y * 0.5 + 0.5;
        if (proj.z > 0.0 && proj.z < 1.0 && proj.x >= 0.0 && proj.x <= 1.0
                          && proj.y >= 0.0 && proj.y <= 1.0)
        {
            // Keep this depth texture comparison-sampled in every code path. Mixing an ordinary
            // Load/Sample with SampleCmp in one SPIR-V module is rejected by older Naga versions.
            float visibility = ShadowMapFar.SampleCmpLevelZero(
                ShadowSamp, saturate(proj.xy), proj.z);
            return MakeOut(float4(visibility, visibility, visibility, 1.0), 0.0);
        }
        return MakeOut(float4(0.02, 0.02, 0.06, 1.0), 0.0);
    }

    // Normal map: derivative-based tangent space (no extra vertex attributes needed).
    //
    // Derivatives MUST be taken outside the per-texel flat-default branch.
    // Neighbouring pixels in a 2x2 quad can disagree on nmSample.z < 0.95 (ripple maps
    // sit right on that threshold), and ddx/ddy inside divergent control flow is undefined.
    // FXC (DX11) and DXC (DX12) disagree enough there to move golden tiles by ~8-19/255.
    float3 nmSample = NormalMap.Sample(AlbedoSamp, materialUv).rgb;
    float3 dp1Nm  = ddx(IN.WorldPos);
    float3 dp2Nm  = ddy(IN.WorldPos);
    float2 duv1Nm = ddx(IN.UV);
    float2 duv2Nm = ddy(IN.UV);
    // Detect the neutral 8-bit normal, rather than rejecting subtle relief whose blue
    // channel remains near one. Image Editor material maps commonly contain shallow detail.
    float2 normalOffset = nmSample.xy - float2(128.0 / 255.0, 128.0 / 255.0);
    if (dot(normalOffset, normalOffset) > 0.00003 || nmSample.z < 0.99)
    {
        float3 dpdu = dp1Nm * duv2Nm.y - dp2Nm * duv1Nm.y;
        float dpduLen2 = dot(dpdu, dpdu);
        if (dpduLen2 > 1e-12)
        {
            float3 t = normalize(dpdu - dot(dpdu, n) * n);
            float3 b = cross(n, t);
            float3 nm = nmSample * 2.0 - 1.0;
            nm.xy *= MaterialSurface.x > 0.0 ? MaterialSurface.x : 1.0;
            n = normalize(nm.x * t + nm.y * b + nm.z * n);
        }
    }

    float3 lightDir  = LightDirEnabled.xyz;
    float  lightingOn = LightDirEnabled.w * (1.0 - MaterialParams.y);
    float  emissive   = MaterialParams.x + EffectParams.x;
    float3 viewDir    = normalize(IN.WorldPos - cameraPos);

    // Sample albedo — atlas array or single texture.
    float4 tex;
    bool voxelTiled = IN.Color.w < 0.0;
    if (voxelTiled)
    {
        float2 atlasMin = IN.Color.rg;
        float2 atlasMax = float2(IN.Color.z, -IN.Color.w);
        float2 blockUV = VoxelFaceBlockUV(IN.WorldPos, n);
        float2 atlasUV = lerp(atlasMin, atlasMax, blockUV);
        if (IN.AtlasLayer > 0.5)
            tex = AlbedoArray.Sample(AlbedoSamp, float3(atlasUV, IN.AtlasLayer));
        else
            tex = AlbedoTex.Sample(AlbedoSamp, atlasUV);
    }
    else if (IN.AtlasLayer > 0.5)
        tex = AlbedoArray.Sample(AlbedoSamp, float3(materialUv, IN.AtlasLayer));
    else
        tex = AlbedoTex.Sample(AlbedoSamp, materialUv);

    // Alpha-tested cutout: opaque/masked surfaces keep the old firm threshold for vegetation.
    // Alpha-blended model surfaces are submitted with NoDepthWrite, so keep soft texture edges
    // by discarding only genuinely empty texels.
    float alphaCutoff = NoDepthWrite > 0.5 ? 0.01 : 0.35;
    clip(tex.a - alphaCutoff);

    float3 base = MaterialColor.rgb * (voxelTiled ? tex.rgb : IN.Color.rgb * tex.rgb);

    // Terrain ground: replace the flat tint with the procedural slope/noise grass+dirt blend
    // (or, when vertex color carries baked elevation, the elevation-driven biome blend).
    if (TerrainGround > 0.5 && MaterialFeatures.x < 0.5)
    {
        float terrainDirtMask;
        base = TerrainAlbedo(IN.WorldPos, n, IN.Color, tex.rgb, terrainDirtMask);
    }

    // Floor checkerboard — tiled albedo (2×2 texture, wrap UVs on the floor mesh).
    if (MaterialParams.z > 0.5)
    {
        float shade = AlbedoTex.Sample(AlbedoSamp, IN.UV).r;
        base *= lerp(0.55, 0.75, shade);
    }

    // Foliage billboards: skip Lambert — sideways normals must not black out grass/trees.
    if (Foliage > 0.5)
    {
        // Discard exported PNG backgrounds that kept alpha on near-black pixels.
        clip(dot(tex.rgb, float3(0.299, 0.587, 0.114)) - 0.07);
        float3 col = base * 1.05;
        float selfFogApplied = 0.0;
        // Match opaque draws: when screen-space FogPost is active, let it own volumetric fog
        // (FogSkip would otherwise permanently exclude these pixels from FogPost).
        if (FogParams.x > 0.5 && NoFog < 0.5 && (EffectParams.z < 0.5 || NoDepthWrite > 0.5))
        {
            float rayLength = length(IN.WorldPos - cameraPos);
            float jitterSeed = InterleavedGradientNoise(IN.SvPos.xy);
            float3 inscatter;
            float transmittance = RayMarchFog(cameraPos, viewDir, rayLength, lightDir, jitterSeed, inscatter);
            col = lerp(col, col * transmittance + inscatter, saturate(FogColor.a));
            selfFogApplied = 1.0;
        }
        return MakeOut(float4(col, MaterialColor.a * tex.a), selfFogApplied);
    }

    float3 l       = normalize(-lightDir);
    float ndotlRaw = dot(n, l);
    float ndotl    = saturate(ndotlRaw);

    float shadow  = NoReceiveShadow > 0.5
        ? 1.0
        : lerp(1.0,
            SampleShadowCSM(IN.ShadowPos, IN.ShadowPosNr, IN.ShadowPosMd, n, lightDir),
            saturate(ShadowCascadeParams.w) * saturate(LightDirEnabled.w));

    // ── Non-Lambertian surface response ──────────────────────────────────────
    // The engine's diffuse term used to be pure Lambert (saturate(N·L)). It is now a
    // Half-Lambert (Valve) wrapped diffuse plus a Blinn-Phong specular highlight and a
    // Fresnel rim term, so shading is view-dependent and surfaces (water, snow, ice,
    // polished ore, metal) read with a real highlight instead of flat matte.

    // Half-Lambert wrap softens the terminator: shaded faces retain a smooth gradient
    // toward the ambient floor rather than clamping flat at N·L = 0.
    float wrapAmt = (StylizedParams.x > 0.5) ? StylizedParams.z : 0.5;
    float wrap        = ndotlRaw * (1.0 - wrapAmt) + wrapAmt;
    float halfLambert = wrap * wrap;
    if (StylizedParams.x > 0.5 && StylizedParams.y > 1.5)
    {
        float steps = StylizedParams.y;
        halfLambert = floor(halfLambert * steps) / max(steps - 1.0, 1.0);
    }

    // Hemisphere ambient: blend sky/ground ambient terms by how much the normal
    // points up vs down, instead of one flat scalar. Keeps faces that the sun never
    // touches (the far side of the mountain, undersides) dim rather than pure black.
    float hemiBlend  = n.y * 0.5 + 0.5;
    float3 hemiAmbient = lerp(AmbientGroundColor.rgb, AmbientColor.rgb, hemiBlend);
    float3 orm = MaterialFeatures.x > 0.5 ? OrmMap.Sample(AlbedoSamp, materialUv).rgb : float3(1.0, 0.72, 0.0);
    float ao = orm.r;
    float wetness = WeatherWindRain.w > 0.5 ? saturate(WeatherSurface.x) * saturate(n.y) : 0;
    base *= lerp(1.0, 0.72, wetness);
    float roughness = lerp(clamp(orm.g, 0.04, 1.0), 0.16, wetness * 0.8);
    float metallic = saturate(orm.b);
    float3 ambient = hemiAmbient * base * ao;

    float3 sunRadiance = SunColorIntensity.rgb * SunColorIntensity.w;
    float3 diffuse     = base * (1.0 - metallic) * halfLambert * sunRadiance * shadow;

    float3 viewToCam = -viewDir;                 // viewDir = surfacePos - cameraPos
    float3 halfVec   = normalize(l + viewToCam);
    float  ndoth     = saturate(dot(n, halfVec));
    float ndotv = max(saturate(dot(n, viewToCam)), 0.001);
    float vdoth = saturate(dot(viewToCam, halfVec));
    float alphaR = roughness * roughness;
    float a2 = alphaR * alphaR;
    float denom = ndoth * ndoth * (a2 - 1.0) + 1.0;
    float D = a2 / max(3.14159265 * denom * denom, 0.0001);
    float k = (roughness + 1.0); k = k * k / 8.0;
    float Gv = ndotv / (ndotv * (1.0 - k) + k);
    float Gl = ndotl / (ndotl * (1.0 - k) + k);
    float3 F0 = lerp(float3(0.04,0.04,0.04), base, metallic);
    float3 F = F0 + (1.0 - F0) * pow(1.0 - vdoth, 5.0);
    float3 specular = sunRadiance * (D * Gv * Gl * F / max(4.0 * ndotv * max(ndotl,0.001), 0.001)) * ndotl * shadow;

    // Fresnel rim from the sky ambient — a subtle grazing-angle edge light that gives
    // silhouettes atmospheric pop (also non-Lambertian, view-dependent).
    float fresnel = pow(1.0 - saturate(dot(n, viewToCam)), 5.0);
    float rimStrength = (StylizedParams.x > 0.5) ? StylizedParams2.y : 0.18;
    float3 rim    = hemiAmbient * fresnel * rimStrength;

    float2 extras = MaterialFeatures.w >= 1.0 ? ExtrasMap.Sample(AlbedoSamp, materialUv).rg : float2(0,0);
    float clearcoat = extras.r * MaterialSurface.w;
    float coatSpec = pow(ndoth, lerp(32.0, 256.0, 1.0 - roughness)) * clearcoat * ndotl * shadow;
    float backLight = pow(saturate(dot(-n, l)), 2.0) * extras.g * MaterialDetail.x;
    float3 subsurface = SubsurfaceAndSteps.rgb * backLight * sunRadiance;
    float3 lit   = lerp(base, ambient + diffuse + specular + rim + coatSpec * sunRadiance + subsurface, lightingOn);
    // The command-test floor is deliberately lit by local point lights only (there is no hidden
    // sun in the reference scene). Keep a small material lift on its light squares so the
    // checkerboard remains readable in the dark ambient setup; this is not global ambient and it
    // does not flatten the sphere/pyramid light falloff or their contact shadows.
    if (MaterialParams.z > 0.5)
        lit += base * 0.20;
    float3 mappedEmission = MaterialFeatures.z > 0.5 ? EmissionMap.Sample(AlbedoSamp, materialUv).rgb * MaterialSurface.z : 0.0;
    float3 color = lit + emissive * base + mappedEmission;
    if (StylizedParams.x > 0.5 && StylizedParams.w > 1.0)
    {
        float lum = dot(color, float3(0.299, 0.587, 0.114));
        color = lerp(float3(lum, lum, lum), color, StylizedParams.w);
    }

    // Point lights obey the same global lighting master switch as sun/ambient lighting.
    color += ComputePointLights(IN.WorldPos, n, base, IN.SvPos)
        * (1.0 - MaterialParams.y) * saturate(LightDirEnabled.w);

    // Self-apply ray-marched fog when: fog is on, this material doesn't opt out (NoFog), and
    // either (a) the screen-space post-process isn't running this frame (EffectParams.z < 0.5 —
    // the original no-depth-buffer fallback case), or (b) this specific draw never wrote scene
    // depth (NoDepthWrite > 0.5) so the post-process structurally cannot see it regardless of
    // whether it's running. Case (b) is what fixes held/no-depth-write items going completely
    // unfogged even when right next to the camera.
    float selfFogApplied = 0.0;
    if (FogParams.x > 0.5 && NoFog < 0.5 && (EffectParams.z < 0.5 || NoDepthWrite > 0.5))
    {
        float rayLength = length(IN.WorldPos - cameraPos);
        float jitterSeed = InterleavedGradientNoise(IN.SvPos.xy);
        float3 inscatter;
        float transmittance = RayMarchFog(cameraPos, viewDir, rayLength, lightDir, jitterSeed, inscatter);
        color = lerp(color, color * transmittance + inscatter, saturate(FogColor.a));
        selfFogApplied = 1.0;
    }

    float vertAlpha = voxelTiled ? 1.0 : IN.Color.a;
    return MakeOut(float4(color, MaterialColor.a * vertAlpha * tex.a), selfFogApplied);
}
";
    }
}

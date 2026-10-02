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
// _pad0 = local shadow slot + 1 (0 = unshadowed), _pad1 = shadow far plane, _pad2 = spot cos(inner).
// SpotDir is the unit cone axis of a spot light and zero for a point light.
struct ClusterPointLight
{
    float3 Pos; float Radius;
    float3 Color; float Intensity;
    float Falloff; float _pad0; float _pad1; float _pad2;
    float3 SpotDir; float SpotCosOuter;
};

// Issue 6 Stage 1: analytic placeable fog volume — same layout as FogPostShaders.cs's
// FogVolumeGpu (must match Genesis.Rendering.Primitives.ForwardRenderer's FogVolumeData
// field-for-field). Placed volumes are evaluated by the froxel fog pass; the declaration stays so
// EngineConstants remains a valid prefix of the C# EngineCB.
struct FogVolumeGpu { float4 CenterDensity; float4 ExtentsFalloff; float4 ColorShape; float4 KindDirection; };

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
    // Trailing fields below must stay a valid *prefix* of the C# EngineCB struct in exact field
    // order, same rule as PointLights above and as FogPostShaders.cs.
    float4       FogVolumeCounts;    // x=numFogVolumes
    FogVolumeGpu FogVolumes[8];
    // Stylized / toon lighting — global per frame (see SceneEnvironment.Stylized*).
    float4       StylizedParams;     // x=enabled, y=toonSteps, z=diffuseWrap, w=saturation
    float4       StylizedParams2;    // x=specularStrength, y=rimStrength, z=scene depth is reversed, w=reserved
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
    // whatever the sky/background left behind), so the pixel shader below applies the froxel
    // fog itself for them regardless of whether post-process fog is otherwise active.
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
// Clustered light lists: per cluster an (offset, count) header, then the packed light indices.
StructuredBuffer<uint>           TileLightIndices : register(t13);
Texture2D<float>               ShadowMapMid    : register(t14); // AF1.1 mid cascade
Texture2D<float>               LocalShadowAtlas : register(t15); // point/spot light shadow atlas
SamplerState                   AlbedoSamp      : register(s0);
SamplerComparisonState         ShadowSamp      : register(s1);

// Local light shadow atlas: a row of six 512² tiles per slot (cube faces; a spot light uses face 0).
cbuffer LocalShadowConstants : register(b4)
{
    row_major float4x4 LocalShadowFaceVP[24]; // slot * 6 + face
    float4             LocalShadowSlots[4];   // xyz = light position, w = far plane
    float4             LocalShadowKinds;      // per slot: 0 = point, 1 = spot
    float4             LocalShadowParams;     // x = active, y = atlas rows, z = 1 / tile size
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

// Sun cascades are orthographic. Casters between the light and a cascade's near plane are
// flattened onto it instead of being clipped away, so tall or distant occluders keep their shadow
// (shadow pancaking). Omni faces are perspective (a non-zero w column) and are left untouched.
float4 PancakeShadowDepth(float4 clipPos, float4x4 lightViewProjection)
{
    float perspective = abs(lightViewProjection._14) + abs(lightViewProjection._24) + abs(lightViewProjection._34);
    if (perspective < 1e-5)
        clipPos.z = max(clipPos.z, 0.0);
    return clipPos;
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
    return PancakeShadowDepth(mul(worldPos, LightViewProjection), LightViewProjection);
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
    return PancakeShadowDepth(mul(worldPos, LightViewProjectionMid), LightViewProjectionMid);
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
    return PancakeShadowDepth(mul(worldPos, LightViewProjectionNear), LightViewProjectionNear);
}

// Fullscreen triangle at far depth: resets one local shadow atlas tile (the viewport limits it).
float4 VS_ShadowTileClear(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 1.0, 1.0);
}

// GPU-skinned shadow casters. Characters are drawn one caster at a time with their own bone
// palette (WorldMatrix per draw), so animated actors shadow the world like static geometry.
float4 SkinnedShadowWorld(VSInSkinned IN)
{
    float3 localPos;
    float3 localNrm;
    SkinLocal(IN, localPos, localNrm);
    return mul(float4(localPos, 1.0), WorldMatrix);
}

float4 VS_ShadowSkinned(VSInSkinned IN) : SV_Position
{
    return PancakeShadowDepth(mul(SkinnedShadowWorld(IN), LightViewProjection), LightViewProjection);
}

float4 VS_ShadowSkinnedMid(VSInSkinned IN) : SV_Position
{
    return PancakeShadowDepth(mul(SkinnedShadowWorld(IN), LightViewProjectionMid), LightViewProjectionMid);
}

float4 VS_ShadowSkinnedNear(VSInSkinned IN) : SV_Position
{
    return PancakeShadowDepth(mul(SkinnedShadowWorld(IN), LightViewProjectionNear), LightViewProjectionNear);
}

// ── Helpers ──────────────────────────────────────────────────────────────────

// sRGB transfer functions. VolumetricParams.z selects the colour pipeline: 0 = legacy gamma,
// 1 = linear lighting (the post composite encodes), 2 = linear lighting written straight to a
// display target (this shader encodes its own output).
float3 SrgbToLinear3(float3 c)
{
    c = max(c, 0.0);
    // step() instead of a vector ?: — DXC (HLSL 2021) rejects non-scalar ternary conditions.
    return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045));
}

float3 LinearToSrgb3(float3 c)
{
    c = saturate(c);
    return lerp(1.055 * pow(c, 1.0 / 2.4) - 0.055, c * 12.92, step(c, 0.0031308));
}

bool LinearColorPipeline() { return VolumetricParams.z > 0.5; }

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
static const float2 ShadowPoisson12[12] =
{
    float2(-0.326212, -0.405805), float2(-0.840144, -0.073580), float2(-0.695914,  0.457137),
    float2(-0.203345,  0.620716), float2( 0.962340, -0.194983), float2( 0.473434, -0.480026),
    float2( 0.519456,  0.767022), float2( 0.185461, -0.893124), float2( 0.507431,  0.064425),
    float2( 0.896420,  0.412458), float2(-0.321940, -0.932615), float2(-0.791559, -0.597705),
};

float SampleShadowMapFar(float4 shadowCoord, float bias, float texel, bool highQ)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;

    proj.x =  proj.x * 0.5 + 0.5;
    proj.y = -proj.y * 0.5 + 0.5;

    if (!highQ)
    {
        if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
            return 1.0;
        return ShadowMapFar.SampleCmpLevelZero(ShadowSamp, proj.xy, proj.z - bias);
    }
    // High quality: 12 Poisson taps, each a hardware bilinear comparison, instead of a 3x3 box.
    // The disc is fixed (not rotated per pixel) because without TAA rotation noise reads as grain.
    float sum = 0.0;
    [unroll]
    for (int i = 0; i < 12; i++)
    {
        float2 uv = proj.xy + ShadowPoisson12[i] * (texel * 1.6);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            sum += 1.0;
        else
            sum += ShadowMapFar.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
    }
    return sum / 12.0;
}

float SampleShadowMapNear(float4 shadowCoord, float bias, float texel, bool highQ)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;

    proj.x =  proj.x * 0.5 + 0.5;
    proj.y = -proj.y * 0.5 + 0.5;

    if (!highQ)
    {
        if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0)
            return 1.0;
        return ShadowMapNear.SampleCmpLevelZero(ShadowSamp, proj.xy, proj.z - bias);
    }
    // High quality: 12 Poisson taps, each a hardware bilinear comparison, instead of a 3x3 box.
    // The disc is fixed (not rotated per pixel) because without TAA rotation noise reads as grain.
    float sum = 0.0;
    [unroll]
    for (int i = 0; i < 12; i++)
    {
        float2 uv = proj.xy + ShadowPoisson12[i] * (texel * 1.6);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            sum += 1.0;
        else
            sum += ShadowMapNear.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
    }
    return sum / 12.0;
}

float SampleShadowMapMid(float4 shadowCoord, float bias, float texel, bool highQ)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;
    proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
    if (proj.z <= 0.0 || proj.z >= 1.0) return 1.0;
    if (proj.x < 0.0 || proj.x > 1.0 || proj.y < 0.0 || proj.y > 1.0) return 1.0;

    float sum = 0.0;
    if (!highQ)
    {
        [loop]
        for (int y = -1; y <= 1; y++)
        {
            [loop]
            for (int x = -1; x <= 1; x++)
            {
                float2 uv = proj.xy + float2(x, y) * texel;
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    sum += 1.0;
                else
                    sum += ShadowMapMid.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
            }
        }
        return sum / 9.0;
    }
    // High quality: the 12-tap Poisson disc, spread wider to match the old 5x5 footprint.
    [unroll]
    for (int i = 0; i < 12; i++)
    {
        float2 uv = proj.xy + ShadowPoisson12[i] * (texel * 2.4);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            sum += 1.0;
        else
            sum += ShadowMapMid.SampleCmpLevelZero(ShadowSamp, uv, proj.z - bias);
    }
    return sum / 12.0;
}

// 0 at a cascade's edge rising to 1 a short band inside it. Pixels in the band blend with the
// next cascade out, so the resolution change no longer shows as a hard seam.
float CascadeBlendWeight(float2 uv)
{
    float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
    return saturate(edge / 0.08);
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
    float nearVis = 1.0;
    float nearWeight = 0.0;
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
                nearVis = SampleShadowMapNear(shadowPosNear, nearBias, nearTexel, highQ);
                nearWeight = CascadeBlendWeight(float2(nearU, nearV));
                if (nearWeight >= 0.999)
                    return nearVis;
            }
        }
    }

    // AF1.1 mid cascade when mode >= 2; otherwise (or outside mid) the far cascade.
    float outerVis = 1.0;
    bool midCovered = false;
    if (ShadowCascadeParams.z > 1.5)
    {
        float3 midNdc = shadowPosMid.xyz / shadowPosMid.w;
        float midBias = bias * 0.75;
        if (midNdc.z > 0.0 && midNdc.z < 1.0)
        {
            float midU =  midNdc.x * 0.5 + 0.5;
            float midV = -midNdc.y * 0.5 + 0.5;
            if (midU >= 0.0 && midU <= 1.0 && midV >= 0.0 && midV <= 1.0)
            {
                midCovered = true;
                outerVis = SampleShadowMapMid(shadowPosMid, midBias, texel, highQ);
                float midWeight = CascadeBlendWeight(float2(midU, midV));
                if (midWeight < 0.999)
                    outerVis = lerp(SampleShadowMapFar(shadowPosFar, bias, texel, highQ), outerVis, midWeight);
            }
        }
    }
    if (!midCovered)
        outerVis = SampleShadowMapFar(shadowPosFar, bias, texel, highQ);

    return lerp(outerVis, nearVis, nearWeight);
}

// Compat overload for call sites that only pass far+near (terrain/fog self-shadow helpers).
float SampleShadowCSM(float4 shadowPosFar, float4 shadowPosNear,
    float3 normal, float3 lightDir)
{
    return SampleShadowCSM(shadowPosFar, shadowPosNear, shadowPosFar, normal, lightDir);
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

static const float2 LocalShadowTaps[4] =
{
    float2(-0.75, -0.25), float2(0.25, -0.75), float2(0.75, 0.25), float2(-0.25, 0.75),
};

// Visibility of worldPos from a local shadow slot: the cube face the pixel lies in for a point
// light, the single tile for a spot light. Four bilinear comparison taps are clamped inside the
// tile so filtering never reads a neighbouring face or light.
float SampleLocalShadow(int slot, float3 worldPos, float3 lightPos)
{
    float3 toPixel = worldPos - lightPos;
    float len = length(toPixel);
    if (len < 1e-5)
        return 1.0;
    float kind = dot(LocalShadowKinds, float4(slot == 0, slot == 1, slot == 2, slot == 3));
    int face = kind > 0.5 ? 0 : OmniCubeFaceIndex(toPixel / len);
    float4 clip = mul(float4(worldPos, 1.0), LocalShadowFaceVP[slot * 6 + face]);
    if (clip.w <= 1e-5)
        return 1.0;
    float3 proj = clip.xyz / clip.w;
    if (proj.z <= 0.0 || proj.z >= 1.0)
        return 1.0;
    float2 tileUv = float2(proj.x * 0.5 + 0.5, -proj.y * 0.5 + 0.5);
    if (tileUv.x < 0.0 || tileUv.x > 1.0 || tileUv.y < 0.0 || tileUv.y > 1.0)
        return 1.0;
    float bias = max(0.0015, 0.02 * (1.0 - saturate(len / max(LocalShadowSlots[slot].w, 0.001))));
    float texel = LocalShadowParams.z;
    float2 atlasScale = float2(1.0 / 6.0, 1.0 / max(LocalShadowParams.y, 1.0));
    float2 origin = float2(face, slot);
    float sum = 0.0;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float2 uv = clamp(tileUv + LocalShadowTaps[i] * texel, texel, 1.0 - texel);
        sum += LocalShadowAtlas.SampleCmpLevelZero(ShadowSamp, (origin + uv) * atlasScale, proj.z - bias);
    }
    return sum * 0.25;
}

// Point lights use the same GGX/Schlick response as the sun. They used to be diffuse-only (and
// ignored metalness), so indoors, lit only by lanterns, roughness and metal maps had no visible
// effect. specularWeight 0 keeps the diffuse-only response (foliage).
float3 ShadeOnePointLight(ClusterPointLight L, float3 worldPos, float3 n, float3 base,
                          float3 viewToCam, float roughness, float metallic, float specularWeight)
{
    float3 toLight = L.Pos - worldPos;
    float dist     = length(toLight);
    float radius   = max(L.Radius, 0.001);
    if (dist >= radius) return float3(0, 0, 0);

    float attenuation = 1.0 - saturate(dist / radius);
    attenuation = pow(attenuation, max(L.Falloff, 0.05));
    float3 l   = normalize(toLight);
    if (dot(L.SpotDir, L.SpotDir) > 0.25)
    {
        // Spot cone: full strength inside the inner angle, fading to nothing at the outer one.
        float coneCos = dot(-l, L.SpotDir);
        attenuation *= smoothstep(L.SpotCosOuter, max(L._pad2, L.SpotCosOuter + 1e-4), coneCos);
    }
    float ndotl = saturate(dot(n, l));
    float3 radiance = L.Color * L.Intensity * attenuation;
    float3 lit = base * (1.0 - metallic) * ndotl * radiance;
    if (specularWeight > 0.0 && ndotl > 0.0)
    {
        float3 h = normalize(l + viewToCam);
        float ndoth = saturate(dot(n, h));
        float ndotv = max(saturate(dot(n, viewToCam)), 0.001);
        float vdoth = saturate(dot(viewToCam, h));
        float alpha = roughness * roughness;
        float alpha2 = alpha * alpha;
        float denom = ndoth * ndoth * (alpha2 - 1.0) + 1.0;
        float D = alpha2 / max(3.14159265 * denom * denom, 0.0001);
        float k = (roughness + 1.0); k = k * k / 8.0;
        float G = (ndotv / (ndotv * (1.0 - k) + k)) * (ndotl / (ndotl * (1.0 - k) + k));
        float3 F0 = lerp(float3(0.04, 0.04, 0.04), base, metallic);
        float3 F = F0 + (1.0 - F0) * pow(1.0 - vdoth, 5.0);
        lit += radiance * (D * G * F / max(4.0 * ndotv * ndotl, 0.001)) * ndotl * specularWeight;
    }

    // FalloffPad.Y / _pad0 carries the light's local shadow slot + 1 when it holds one.
    if (L._pad0 > 0.5 && LocalShadowParams.x > 0.5)
        lit *= SampleLocalShadow((int)(L._pad0 - 0.5), worldPos, L.Pos);
    return lit;
}

float3 ComputePointLightsPbr(float3 worldPos, float3 n, float3 base, float4 svPos,
                             float3 viewToCam, float roughness, float metallic, float specularWeight)
{
    int nLights = (int)PointLightCounts.x;
    int tileGrid = (int)PointLightCounts.y;
    int maxPerTile = (int)PointLightCounts.z;
    float3 result = float3(0, 0, 0);
    if (nLights <= 0) return result;

    if (tileGrid >= 2 && maxPerTile > 0)
    {
        // Clusters: screen tile × depth slice. Slices are exponential in clip w over the fixed range
        // ClusteredLightDefaults uses on the CPU (0.25 to 1000, 24 slices).
        float2 uv = svPos.xy * ViewportParams.zw;
        int tx = clamp((int)floor(uv.x * tileGrid), 0, tileGrid - 1);
        int ty = clamp((int)floor(uv.y * tileGrid), 0, tileGrid - 1);
        float depthW = mul(float4(worldPos, 1.0), ViewProjection).w;
        const float sliceScale = 24.0 / log2(1000.0 / 0.25);
        int slice = clamp((int)floor(log2(max(depthW, 1e-4) / 0.25) * sliceScale), 0, 23);
        uint cluster = (uint)((slice * tileGrid + ty) * tileGrid + tx);
        uint offset = TileLightIndices[cluster * 2];
        uint count = min(TileLightIndices[cluster * 2 + 1], (uint)maxPerTile);
        [loop]
        for (uint i = 0; i < count; i++)
        {
            uint li = TileLightIndices[offset + i];
            if (li >= (uint)nLights) continue;
            result += ShadeOnePointLight(ClusterLights[li], worldPos, n, base, viewToCam, roughness, metallic, specularWeight);
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
        L.SpotDir = float3(0.0, 0.0, 0.0);  // the EngineCB copy has no cone: shaded as a point light
        L.SpotCosOuter = -1.0;
        result += ShadeOnePointLight(L, worldPos, n, base, viewToCam, roughness, metallic, specularWeight);
    }
    return result;
}

float3 ComputePointLights(float3 worldPos, float3 n, float3 base, float4 svPos)
{
    return ComputePointLightsPbr(worldPos, n, base, svPos, float3(0.0, 1.0, 0.0), 1.0, 0.0, 0.0);
}

// ── Volumetric fog ───────────────────────────────────────────────────────────
// Fog is the froxel volume (FroxelFogShaders). The post composite fogs every pixel with depth;
// this shader fogs only the draws the composite cannot see (NoDepthWrite) or frames without it.
" + FroxelFogShaders.ApplySource + @"

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
    float4 Color : SV_Target0;
    // r = fog flag (1 = already fogged here, 0.5 = engine-lit), gba = pre-fog ambient light, which
    // the composite's AO darkens instead of the whole pixel.
    float4 Aux   : SV_Target1;
};

PSOut MakeOut(float4 c, float skip, float3 ambient)
{
    PSOut o;
    if (VolumetricParams.z > 1.5)
        c.rgb = LinearToSrgb3(c.rgb);
    o.Color = c;
    o.Aux = float4(skip > 0.5 ? 1.0 : 0.5, ambient);
    return o;
}

PSOut MakeOut(float4 c, float skip)
{
    return MakeOut(c, skip, float3(0.0, 0.0, 0.0));
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
        // The view shows near as bright whichever way round the scene stores depth.
        if (StylizedParams2.z > 0.5) cameraDepth = 1.0 - cameraDepth;
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
    // Two-channel (BC5) normal maps sample blue as 0, which decodes to z = -1 and lights the
    // surface inside-out. A genuine RGB normal map never stores blue 0, so rebuild z from xy.
    if (nmSample.z < 0.5 / 255.0)
    {
        float2 nmXY = nmSample.xy * 2.0 - 1.0;
        nmSample.z = sqrt(saturate(1.0 - dot(nmXY, nmXY))) * 0.5 + 0.5;
    }
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
    // Albedo is authored in sRGB; the linear pipeline lights it in linear space.
    if (LinearColorPipeline())
        tex.rgb = SrgbToLinear3(tex.rgb);

    // Alpha-tested cutout: opaque/masked surfaces keep the old firm threshold for vegetation.
    // Alpha-blended model surfaces are submitted with NoDepthWrite, so keep soft texture edges
    // by discarding only genuinely empty texels.
    float alphaCutoff = NoDepthWrite > 0.5 ? 0.01 : 0.35;
    clip(tex.a - alphaCutoff);

    float3 base = MaterialColor.rgb * (voxelTiled ? tex.rgb : IN.Color.rgb * tex.rgb);
    // Tints (material colour, instance/vertex colour) are picked in sRGB. Terrain vertex colours
    // carry splat weights and voxel colours carry atlas coordinates, so neither is converted.
    if (LinearColorPipeline() && !voxelTiled && TerrainGround < 0.5)
    {
        float3 tint = MaterialColor.rgb * IN.Color.rgb;
        base *= SrgbToLinear3(tint) / max(tint, 1e-4);
    }

    // Terrain ground: replace the flat tint with the procedural slope/noise grass+dirt blend
    // (or, when vertex color carries baked elevation, the elevation-driven biome blend).
    if (TerrainGround > 0.5 && MaterialFeatures.x < 0.5)
    {
        float terrainDirtMask;
        // The procedural palette is authored in sRGB: blend in that space, then linearize.
        if (LinearColorPipeline())
            base = SrgbToLinear3(TerrainAlbedo(IN.WorldPos, n, IN.Color, LinearToSrgb3(tex.rgb), terrainDirtMask));
        else
            base = TerrainAlbedo(IN.WorldPos, n, IN.Color, tex.rgb, terrainDirtMask);
    }

    // Floor checkerboard — tiled albedo (2×2 texture, wrap UVs on the floor mesh).
    if (MaterialParams.z > 0.5)
    {
        float shade = AlbedoTex.Sample(AlbedoSamp, IN.UV).r;
        base *= lerp(0.55, 0.75, shade);
    }

    // Foliage cards: sideways/card normals must not black out grass and trees, so the sun term
    // uses a wrapped response around a normal bent toward up. Foliage used to be fully unlit, so it
    // ignored the sun, shadows, time of day and point lights and stayed full-bright at night.
    if (Foliage > 0.5)
    {
        // Discard exported PNG backgrounds that kept alpha on near-black pixels.
        clip(dot(tex.rgb, float3(0.299, 0.587, 0.114)) - 0.07);
        float3 foliageNormal = normalize(lerp(n, float3(0.0, 1.0, 0.0), 0.6));
        float3 toSun = normalize(-lightDir);
        float foliageDiffuse = saturate(dot(foliageNormal, toSun) * 0.6 + 0.4);
        float foliageShadow = NoReceiveShadow > 0.5
            ? 1.0
            : lerp(1.0, SampleShadowCSM(IN.ShadowPos, IN.ShadowPosNr, IN.ShadowPosMd, foliageNormal, lightDir),
                saturate(ShadowCascadeParams.w) * saturate(LightDirEnabled.w));
        float3 foliageAmbient = lerp(AmbientGroundColor.rgb, AmbientColor.rgb, 0.75);
        float3 litFoliage = base * (foliageAmbient + SunColorIntensity.rgb * SunColorIntensity.w * foliageDiffuse * foliageShadow)
            + ComputePointLights(IN.WorldPos, foliageNormal, base, IN.SvPos);
        float foliageLighting = saturate(LightDirEnabled.w) * (1.0 - MaterialParams.y);
        float3 col = lerp(base * 1.05, litFoliage, foliageLighting);
        float selfFogApplied = 0.0;
        // Match opaque draws: when screen-space FogPost is active, let it own volumetric fog
        // (FogSkip would otherwise permanently exclude these pixels from FogPost).
        if (FogParams.x > 0.5 && NoFog < 0.5 && (EffectParams.z < 0.5 || NoDepthWrite > 0.5))
        {
            float4 fog = FroxelFog(IN.SvPos.xy * ViewportParams.zw, cameraPos, IN.WorldPos,
                mul(float4(IN.WorldPos, 1.0), ViewProjection).w);
            col = lerp(col, col * fog.a + fog.rgb, saturate(FogColor.a));
            selfFogApplied = 1.0;
        }
        return MakeOut(float4(col, MaterialColor.a * tex.a), selfFogApplied,
            base * foliageAmbient * foliageLighting);
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
    // PBR materials (with an ORM map) outside stylized lighting use Lambert diffuse. The half-Lambert
    // wrap lights faces the shadow map treats as unlit, a light leak on physically based surfaces.
    if (StylizedParams.x < 0.5 && MaterialFeatures.x > 0.5)
        halfLambert = ndotl;

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
    if (LinearColorPipeline())
        mappedEmission = SrgbToLinear3(mappedEmission / max(MaterialSurface.z, 1e-4)) * MaterialSurface.z;
    float3 color = lit + emissive * base + mappedEmission;
    if (StylizedParams.x > 0.5 && StylizedParams.w > 1.0)
    {
        float lum = dot(color, float3(0.299, 0.587, 0.114));
        color = lerp(float3(lum, lum, lum), color, StylizedParams.w);
    }

    // Point lights obey the same global lighting master switch as sun/ambient lighting.
    color += ComputePointLightsPbr(IN.WorldPos, n, base, IN.SvPos, viewToCam, roughness, metallic, 1.0)
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
        float4 fog = FroxelFog(IN.SvPos.xy * ViewportParams.zw, cameraPos, IN.WorldPos,
            mul(float4(IN.WorldPos, 1.0), ViewProjection).w);
        color = lerp(color, color * fog.a + fog.rgb, saturate(FogColor.a));
        selfFogApplied = 1.0;
    }

    float vertAlpha = voxelTiled ? 1.0 : IN.Color.a;
    return MakeOut(float4(color, MaterialColor.a * vertAlpha * tex.a), selfFogApplied, ambient * lightingOn);
}
";
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;
using Genesis.Rendering.Lights;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Diagnostics;

namespace Genesis.Rendering.Primitives
{
    internal sealed partial class ForwardRenderer : IDisposable
    {
        // ── CB structs (must match HLSL layout exactly) ───────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct PerFrameCB
        {
            public Matrix4x4 ViewProjection;
            public Matrix4x4 LightViewProjection;      // far cascade (or only cascade when near is inactive)
            public Matrix4x4 LightViewProjectionNear;  // near cascade
            public Vector4   CameraPosTime;
            // AF1.1: appended so 2-cascade shaders that stop at CameraPosTime stay valid.
            public Matrix4x4 LightViewProjectionMid;
        }

        // One point light's GPU data (matches HLSL PointLight struct: 3 x float4 = 48 bytes).
        [StructLayout(LayoutKind.Sequential)]
        private struct PointLightData
        {
            public Vector4 PosRadius;      // xyz = world position, w = radius
            public Vector4 ColorIntensity; // xyz = color (linear), w = intensity
            // x = falloff; y = local shadow slot + 1 (0 = unshadowed); z = shadow far; w = spot cos(inner)
            public Vector4 FalloffPad;
        }

        // Issue 6 Stage 1: placeable analytic fog volume, evaluated in the screen-space fog
        // post pass (FogPostShaders.cs). 4×Vector4 per volume, mirroring the PointLightData
        // packing pattern below — C# LayoutKind.Sequential doesn't support fixed struct arrays,
        // so 8 volumes are explicit fields (V0..V7) rather than a real array.
        [StructLayout(LayoutKind.Sequential)]
        private struct FogVolumeData
        {
            public Vector4 CenterDensity;  // xyz=center, w=density
            public Vector4 ExtentsFalloff; // xyz=extents (half-size), w=falloff curve exponent
            public Vector4 ColorShape;     // xyz=color, w=shape enum (FogVolumeShape)
            public Vector4 KindDirection;  // x=kind enum (FogVolumeKind), yzw=cone direction
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EngineCB
        {
            public Vector4 LightDirEnabled;
            public Vector4 FogParams;
            public Vector4 FogColor;
            public Vector4 AmbientColor;
            public Vector4 AmbientGroundColor;
            public Vector4 SunColorIntensity;
            public Vector4 FogParams2;
            public Vector4 ShadowParams;        // x=active, y=bias, z=texelFar, w=highQ
            public Vector4 EffectParams;
            public Vector4 VolumetricParams;
            public Vector4 ViewportParams;
            public Vector4 ShadowCascadeParams; // x=near-split, y=mid-split, z=mode(1=2csm,2=3csm), w=strength
            public Vector4 PointLightCounts;    // x=numLights
            // 8 point lights – explicit fields because C# LayoutKind.Sequential doesn't support fixed struct arrays
            public PointLightData L0, L1, L2, L3, L4, L5, L6, L7;
            // Issue 6 Stage 1: fog volumes, appended after the point lights. Originally only
            // FogPostShaders.cs's screen-space pass needed these trailing fields, but the fog
            // rewrite's RayMarchFog is now textually duplicated into ForwardShaders.cs and
            // TerrainShader.cs too (so each shader's own self-fog path can fold in placed fog
            // volumes), so all three HLSL cbuffers now declare this same prefix through V7.
            public Vector4 FogVolumeCounts;     // x=numFogVolumes
            public FogVolumeData V0, V1, V2, V3, V4, V5, V6, V7;
            public Vector4 StylizedParams;
            public Vector4 StylizedParams2;
            public Vector4 WeatherWindRain;
            public Vector4 WeatherSurface;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FogPostCB
        {
            public Vector4   ClipPlanes;
            public Vector4   CameraPosPad;
            public Matrix4x4 InvViewProjection;
            // Far/near cascade light view-projections — needed so the volumetric fog ray-march
            // can sample the shadow maps per step and produce real sun shafts instead of
            // uniform haze (see SampleShadowAtPoint/ComputeVolumetricFog in FogPostShaders.cs).
            public Matrix4x4 LightViewProjection;
            public Matrix4x4 LightViewProjectionNear;
            public Matrix4x4 LightViewProjectionMid;
            // AF1.2: append-only — x=GTAO enabled, y=strength (1 = full).
            public Vector4   AoParams;
            // AF1.4: append-only — x=contact shadows enabled, y=strength, zw unused.
            public Vector4   ContactParams;
            // AF1.5: append-only — x=local volumetric scatter enabled, y=strength, zw unused.
            public Vector4   LocalVolParams;
            // AF1.6: append-only — x=smoke extinction enabled, y=extinction coefficient,
            // z=volume count, w unused. The eight volumes below are spelled out because
            // LayoutKind.Sequential has no fixed struct arrays; the HLSL side declares them as
            // float4[8] pairs, which pack to the same offsets (see ShaderLayoutAudit).
            public Vector4   SmokeParams;
            public Vector4   Smoke0PosRadius, Smoke1PosRadius, Smoke2PosRadius, Smoke3PosRadius;
            public Vector4   Smoke4PosRadius, Smoke5PosRadius, Smoke6PosRadius, Smoke7PosRadius;
            public Vector4   Smoke0Density, Smoke1Density, Smoke2Density, Smoke3Density;
            public Vector4   Smoke4Density, Smoke5Density, Smoke6Density, Smoke7Density;
            // AF1.7: append-only — x=bloom enabled, y=intensity, zw unused.
            public Vector4   BloomParams;
            // AF1.7: x=exposure, y=contrast, z=saturation, w unused.
            public Vector4   ExposureParams;
            // AF1.7: x=vignette strength, yzw unused.
            public Vector4   VignetteParams;
            // AF2.1: append-only — x=atmosphere LUT enabled, y=sunHeight, z=daylight, w unused.
            public Vector4   AtmosphereLutParams;
            // AF2.3: append-only — x=raymarched clouds enabled, y=intensity, zw unused.
            public Vector4   CloudCompositeParams;
            // AF2.5: append-only — x=enabled, y=nightFactor, z=siderealAngle, w=latitudeRadians.
            public Vector4   CelestialParams;
            // AF2.5: xyz = direction toward the moon, w = lunar phase 0..1.
            public Vector4   MoonDirPhase;
            // World Y interval used to reject upsampled clouds in front of nearer geometry.
            public Vector4   CloudLayerParams;
            public Vector4   AuthoredSkyZenith;
            public Vector4   AuthoredSkyHorizon;
            public Vector4   AuthoredSkySun;     // xyz=toward sun, w=solar disc radiance
            // Ink outline, append-only — x=opacity (0 = off), y=width in pixels, z=relative depth
            // step, w=crease threshold in radians. See InkOutlineSettings.
            public Vector4   InkParams;
            public Vector4   InkColor;           // rgb=display-space ink, w=radians per pixel
            public Vector4   InkFade;            // x=full-width distance, y=far distance, z=far opacity
        }

        // Matches BloomShaders.cbuffer BloomConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct BloomCB
        {
            // xy = source texel (1/w, 1/h), z = threshold (extract), w = add FineMap on upsample.
            public Vector4 Params;
        }

        // Matches RaymarchedCloudsShaders.cbuffer RaymarchedCloudsConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct RaymarchedCloudsCB
        {
            public Vector4   ClipPlanes;         // x=near, y=far, z=fullWidth, w=fullHeight
            public Matrix4x4 InvViewProjection;
            public Vector4   CameraPosPad;       // xyz = camera world position
            public Vector4   LightDirPad;        // xyz = unit direction toward the sun
            public Vector4   CloudParams;        // x=base, y=thickness, z=steps, w=intensity×density
            public Vector4   WeatherParams;      // x=halfExtent, y=enabled, z=coverageScale, w=density
            public Vector4   WeatherOrigin;      // xy = weather grid world XZ centre, zw=texture dimensions
            public Vector4   CloudSunColor;      // rgb=solar colour, w=authored lighting enabled
            public Vector4   CloudAmbientColor;
        }

        // Matches CloudTemporalShaders.cbuffer CloudTemporalConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct CloudTemporalCB
        {
            public Vector4   ClipPlanes;         // x=near, y=far, z=internalW, w=internalH
            public Matrix4x4 InvViewProjection;
            public Matrix4x4 PrevViewProjection;
            public Vector4   CameraPosPad;       // xyz = camera world position
            public Vector4   TemporalParams;     // x=baseBlend, y=reset, z=motion, w=depth
            public Vector4   UpsampleParams;     // xy=texel, z=enabled, w unused
        }

        // Matches GtaoShaders.cbuffer GtaoConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct GtaoCB
        {
            public Vector4   ClipPlanes;      // x=near, y=far, z=fullWidth, w=fullHeight
            public Matrix4x4 InvProjection;
            public Vector4   Params;          // x=time, y=horizontal blur flag, zw unused
        }

        // Matches ContactShadowShaders.cbuffer ContactShadowConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct ContactShadowCB
        {
            public Vector4   ClipPlanes;         // x=near, y=far, z=fullWidth, w=fullHeight
            public Matrix4x4 InvViewProjection;
            public Matrix4x4 ViewProjection;
            public Vector4   LightDirPad;        // xyz = unit direction toward the light
            public Vector4   Params;             // x=maxDistance, y=strength, z=time
        }

        // Matches LocalVolumetricShaders.cbuffer LocalVolumetricConstants (b0). The four light
        // slots are spelled out because LayoutKind.Sequential has no fixed struct arrays; the HLSL
        // side declares them as float4[4] pairs, which pack to the same offsets.
        [StructLayout(LayoutKind.Sequential)]
        private struct LocalVolumetricCB
        {
            public Vector4   ClipPlanes;         // x=near, y=far, z=fullWidth, w=fullHeight
            public Matrix4x4 InvViewProjection;
            public Vector4   CameraPosPad;       // xyz = camera world position
            public Vector4   Params;             // x=steps, y=strength, z=lightCount, w=maxRayDistance
            public Vector4   Light0PosRadius, Light1PosRadius, Light2PosRadius, Light3PosRadius;
            public Vector4   Light0ColorIntensity, Light1ColorIntensity;
            public Vector4   Light2ColorIntensity, Light3ColorIntensity;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DrawCB
        {
            public Matrix4x4 WorldMatrix;
            public Vector4   MaterialColor;
            public Vector4   MaterialParams;   // x=emissive, y=unlit, z=isFloor, w=useInstancing
            public uint      InstanceOffset;
            // NoFogFlag (Issue 6): repurposes what was a pure-padding float so the per-material
            // "receives fog" opt-out can ride the existing DrawCB upload without growing the
            // buffer. HLSL's DrawConstants cbuffer only needs to declare a matching prefix
            // through this field (see ForwardShaders.cs) — Pad1/Pad2 stay true padding.
            public float     NoFogFlag;
            // NoDepthWriteFlag (fog rewrite): repurposes the other former padding float.
            // Draws that don't write scene depth (held items, particles, etc.) are invisible to
            // the screen-space post-process fog's depth-buffer reconstruction, so the forward
            // pixel shader must self-apply ray-marched fog for them instead — this flag tells it
            // when that's the case. Derived automatically from MeshDrawFlags.NoDepthWrite /
            // whatever depth-stencil state a given draw actually binds (see Submit()/Flush() in
            // this file); never a user-facing toggle. Pad2 stays true padding.
            public float     NoDepthWriteFlag;
            // TerrainGroundFlag (terrain rendering redesign): repurposes the last true-padding
            // float. Set only by SandboxTerrainGround's own draw call (see Submit()/Flush() below
            // and SandboxTerrainGround.cs) — tells ForwardShaders.cs's PS() to replace the sampled
            // albedo with the procedural slope/noise-driven grass+dirt-path blend instead of the
            // flat material tint. Never a user-facing toggle.
            public float     TerrainGroundFlag;
            public float     FoliageFlag;
            // Explicit tail padding for the 96..111 constant row. HLSL will not let a float4 straddle
            // a 16-byte boundary, so DrawConstants.MaterialSurface starts at 128 — but C#'s Sequential
            // layout packs Vector4 on 4-byte alignment and would put SurfaceParams at 116, shifting
            // every field after it by 12 bytes relative to what the shader reads. UploadDrawCB blits
            // this struct raw, so the two layouts have to agree byte-for-byte. Declaring the pad on
            // both sides (rather than relying on HLSL's implicit rule) keeps the field lists 1:1 and
            // makes the hazard visible to whoever adds the next flag here. See Issues.md NEXT-066 and
            // the Render.Shader.ConstantBufferLayoutMatchesHlsl regression test.
            //
            // Three floats rather than one Vector3, mirroring the HLSL exactly: std140 aligns a
            // 3-component vector to 16 bytes, so the shader side had to be split for the OpenGL
            // backend's GLSL translation, and the field lists only stay 1:1 if this follows.
            public float     MaterialRowPad0;
            public float     MaterialRowPad1;
            public float     MaterialRowPad2;
            public Vector4   SurfaceParams;
            public Vector4   DetailParams;
            public Vector4   SubsurfaceColorSteps;
            public Vector4   MaterialFeatures; // ORM, height mode, emission, extras/flow
            public uint      SkinMatrixOffset;
            public float     GpuSkinningFlag;
            public float     NoReceiveShadowFlag;
            public float     SkinPad1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaterCB
        {
            public Vector4 DeepColorDepthFade;
            public Vector4 WaterParams;
            public Vector4 SkyHorizonFlow;
            public Vector4 SkyZenithPad;
            public Matrix4x4 ReflectionViewProjection;
            public Vector4 ReflectionParams;
            public Vector4 WeatherWindRain;

        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderParametersCB
        {
            public Vector4 Row0, Row1, Row2, Row3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InstanceGpu
        {
            public Matrix4x4 World;      // 64 bytes
            public Vector4   Color;      // 16 bytes
            public Vector4   AtlasData;  // 16 bytes — x=atlas layer (0 = AlbedoTex), yzw=reserved
        }

        // ── Mesh registry ─────────────────────────────────────────────────────────

        internal Action<Matrix4x4, Matrix4x4> ExternalParticles;

        internal bool TryGetParticleMesh(MeshHandle handle, out Genesis.Rendering.Particles.GpuParticleMesh mesh)
        {
            if (TryGetMesh(handle.Id, out MeshEntry entry) && !entry.IsSkinned && entry.VertexStride == 48)
            {
                mesh = new Genesis.Rendering.Particles.GpuParticleMesh(entry.VB, entry.IB,
                    entry.VertexStride, entry.IndexCount, GpuIndexFormat.UInt16);
                return true;
            }
            mesh = default;
            return false;
        }

        private struct MeshEntry
        {
            public GpuBufferHandle VB;
            public GpuBufferHandle IB;
            public int IndexCount;
            public Vector3 BoundsCenter;
            public float BoundsRadius;
            public int VertexStride;
            public int VertexCount;
            public bool IsSkinned;
            public bool IsReleased;
            /// <summary>Changes whenever the vertices do, so cached shadow tiles notice in-place edits.</summary>
            public int Revision;
        }

        private int _meshRevision;

        private struct SkinPaletteEntry
        {
            public GpuBufferHandle Buffer;
            public Matrix4x4[] Matrices;
            public int MatrixCount;
            public bool IsReleased;
        }

        // ── Batch ─────────────────────────────────────────────────────────────────

        private struct BatchKey : IEquatable<BatchKey>
        {
            public int MeshId, TextureId, NormalId, OrmId, HeightId, EmissionId, ExtrasId, FlowId;
            public int ShaderId;
            public Vector4 ShaderParams0, ShaderParams1, ShaderParams2, ShaderParams3;
            public AuthoredGpuTextures AuthoredTextures;
            public bool NoFog;
            public bool NoReceiveShadow;
            public bool TerrainGround;
            public MeshDrawFlags RasterOverride;
            public bool Foliage;
            public bool IsFloor;
            public MaterialHeightMode HeightMode;
            public Vector4 SurfaceParams, DetailParams, SubsurfaceColorSteps;
            public float Emissive;
            public bool Equals(BatchKey o)
            {
                if (MeshId != o.MeshId || TextureId != o.TextureId || NormalId != o.NormalId || 
                    OrmId != o.OrmId || HeightId != o.HeightId || EmissionId != o.EmissionId || 
                    ExtrasId != o.ExtrasId || FlowId != o.FlowId || ShaderId != o.ShaderId || 
                    NoFog != o.NoFog || NoReceiveShadow != o.NoReceiveShadow
                    || TerrainGround != o.TerrainGround || RasterOverride != o.RasterOverride
                    || Foliage != o.Foliage || IsFloor != o.IsFloor || HeightMode != o.HeightMode
                    || SurfaceParams != o.SurfaceParams || DetailParams != o.DetailParams
                    || SubsurfaceColorSteps != o.SubsurfaceColorSteps || Emissive != o.Emissive)
                    return false;
                
                if (ShaderId > 0)
                {
                    if (ShaderParams0 != o.ShaderParams0 || ShaderParams1 != o.ShaderParams1 || 
                        ShaderParams2 != o.ShaderParams2 || ShaderParams3 != o.ShaderParams3)
                        return false;
                    if (!AuthoredTextures.SameBindings(o.AuthoredTextures))
                        return false;
                }
                
                return true;
            }

            public override int GetHashCode() 
            { 
                var h = new HashCode();
                h.Add(MeshId); h.Add(TextureId); h.Add(NormalId); h.Add(OrmId);
                h.Add(HeightId); h.Add(EmissionId); h.Add(ExtrasId); h.Add(FlowId);
                h.Add(ShaderId);
                if (ShaderId > 0)
                {
                    h.Add(ShaderParams0); h.Add(ShaderParams1); h.Add(ShaderParams2); h.Add(ShaderParams3);
                    h.Add(AuthoredTextures.TexId0); h.Add(AuthoredTextures.TexId1);
                    h.Add(AuthoredTextures.TexId2); h.Add(AuthoredTextures.TexId3);
                }
                h.Add(NoFog); h.Add(NoReceiveShadow); h.Add(TerrainGround); h.Add(RasterOverride);
                h.Add(Foliage); h.Add(IsFloor); h.Add(HeightMode); h.Add(SurfaceParams); h.Add(DetailParams);
                h.Add(SubsurfaceColorSteps); h.Add(Emissive);
                return h.ToHashCode(); 
            }
        }

        private class Batch
        {
            public int SubmissionOrder;
            public int   MeshId;
            public int   TextureId;
            public GpuTextureHandle Texture;
            public GpuTextureHandle Normal; // invalid = use default flat normal
            public GpuTextureHandle Orm, Height, Emission, Extras, Flow;
            public MaterialHeightMode HeightMode;
            public Vector4 SurfaceParams, DetailParams, SubsurfaceColorSteps;
            public float Emissive;
            public bool  NoFog;
            public bool  NoReceiveShadow;
            public bool  TerrainGround;
            public MeshDrawFlags RasterOverride;
            public bool  Foliage;
            public bool  IsFloor;
            public RuntimeShaderHandle Shader;
            public Vector4 ShaderParams0, ShaderParams1, ShaderParams2, ShaderParams3;
            public AuthoredGpuTextures AuthoredTextures;
            public readonly List<InstanceGpu> Instances = new();
        }

        private struct SkinnedBatchKey : IEquatable<SkinnedBatchKey>
        {
            public int MeshId, TextureId, SkinPaletteId;
            public int NormalId, OrmId, EmissionId;
            public int ShaderId;
            public Vector4 ShaderParams0, ShaderParams1, ShaderParams2, ShaderParams3;
            public AuthoredGpuTextures AuthoredTextures;
            public bool NoFog;
            public bool NoReceiveShadow;
            public float Emissive;
            public MeshDrawFlags RasterOverride;
            public bool Equals(SkinnedBatchKey o) => MeshId == o.MeshId && TextureId == o.TextureId && SkinPaletteId == o.SkinPaletteId && NormalId == o.NormalId && OrmId == o.OrmId && EmissionId == o.EmissionId && ShaderId == o.ShaderId && ShaderParams0 == o.ShaderParams0 && ShaderParams1 == o.ShaderParams1 && ShaderParams2 == o.ShaderParams2 && ShaderParams3 == o.ShaderParams3 && AuthoredTextures.SameBindings(o.AuthoredTextures) && NoFog == o.NoFog && NoReceiveShadow == o.NoReceiveShadow && Emissive == o.Emissive && RasterOverride == o.RasterOverride;
            public override int GetHashCode() { var h = new HashCode(); h.Add(MeshId); h.Add(TextureId); h.Add(SkinPaletteId); h.Add(NormalId); h.Add(OrmId); h.Add(EmissionId); h.Add(ShaderId); h.Add(ShaderParams0); h.Add(ShaderParams1); h.Add(ShaderParams2); h.Add(ShaderParams3); h.Add(AuthoredTextures.TexId0); h.Add(AuthoredTextures.TexId1); h.Add(AuthoredTextures.TexId2); h.Add(AuthoredTextures.TexId3); h.Add(NoFog); h.Add(NoReceiveShadow); h.Add(Emissive); h.Add(RasterOverride); return h.ToHashCode(); }
        }

        private class SkinnedBatch
        {
            public int MeshId;
            public int TextureId;
            public int SkinPaletteId;
            public GpuTextureHandle Texture;
            public GpuTextureHandle Normal, Orm, Emission;
            public float Emissive;
            public bool NoFog;
            public bool NoReceiveShadow;
            public MeshDrawFlags RasterOverride;
            public RuntimeShaderHandle Shader;
            public Vector4 ShaderParams0, ShaderParams1, ShaderParams2, ShaderParams3;
            public AuthoredGpuTextures AuthoredTextures;
            public readonly List<InstanceGpu> Instances = new();
        }

        // ── Transparent (particle) batch ──────────────────────────────────────────
        // Transparent + additive draws that share the same quad mesh and texture are
        // batched here and rendered with a single DrawIndexedInstanced call each,
        // instead of one DrawIndexed per particle. The key includes every per-batch value so a
        // large batch never silently adopts the first particle's normal map or emissive strength.

        private struct TransBatchKey : IEquatable<TransBatchKey>
        {
            public int  MeshId, TextureId, NormalId;
            public byte BlendMode;
            public bool NoFog;
            public float Emissive;
            public bool Equals(TransBatchKey o) => MeshId == o.MeshId && TextureId == o.TextureId && NormalId == o.NormalId && BlendMode == o.BlendMode && NoFog == o.NoFog && Emissive == o.Emissive;
            public override int GetHashCode() => HashCode.Combine(MeshId, TextureId, NormalId, BlendMode, NoFog, Emissive);
        }

        private class TransBatch
        {
            public int   MeshId;
            public int   TextureId;
            public GpuTextureHandle Texture;
            public GpuTextureHandle Normal;
            public byte  BlendMode;
            public float Emissive;
            public bool  NoFog;
            public readonly List<InstanceGpu> Instances = new();
        }

        private struct ShadowBatchKey : IEquatable<ShadowBatchKey>
        {
            public int MeshId;
            public MeshDrawFlags RasterOverride;
            public bool Equals(ShadowBatchKey o) => MeshId == o.MeshId && RasterOverride == o.RasterOverride;
            public override int GetHashCode() => HashCode.Combine(MeshId, RasterOverride);
        }

        private class ShadowBatch
        {
            public int MeshId;
            public MeshDrawFlags RasterOverride;
            /// <summary>Every caster submitted this frame, with its world bounding sphere (xyz, radius).</summary>
            public readonly List<InstanceGpu> Casters = new();
            public readonly List<Vector4> CasterBounds = new();
            /// <summary>Per-caster content hash (mesh, mesh revision, raster state, transform) for shadow caching.</summary>
            public readonly List<ulong> CasterHashes = new();
            // Per-cascade lists, filled from Casters once the cascade matrices are final.
            public readonly List<InstanceGpu> FarInstances  = new();
            public readonly List<InstanceGpu> MidInstances  = new();
            public readonly List<InstanceGpu> NearInstances = new();
            /// <summary>Far-list casters inside the shadowed point light's sphere this frame.</summary>
            public readonly List<InstanceGpu> OmniInstances = new();
        }

        private enum ShadowCascadeKind : byte { Far, Mid, Near, Omni }

        private struct WorldMesh
        {
            public int MeshId;
            public GpuTextureHandle Texture;
            public Matrix4x4 World;
            public Vector4   Color;
            public bool IsFloor, Unlit, NoDepthWrite, NoDepthTest, Transparent, Additive, Multiply;
            public float Emissive;
            public bool NoFog;
            public bool NoReceiveShadow;
            public bool TerrainGround;
            public MeshDrawFlags RasterOverride;
            public int SkinPaletteId;
            /// <summary>Submission order, the tie-break that keeps equal-distance draws stable.</summary>
            public int Order;
        }

        private struct WaterMesh
        {
            public int MeshId;
            public Matrix4x4 World;
            public Vector4 Shallow;
            public Vector4 Deep;
            public Vector4 WaterParams;
            public Vector4 SkyHorizon;
            public Vector4 SkyZenith;
            public GpuTextureHandle Albedo;
        }

        // ── Backend-neutral GPU objects ────────────────────────────────────────────

        private readonly IGpuDevice _gpu;

        private byte[] _mainVertexShader = Array.Empty<byte>();
        private byte[] _skinnedVertexShader = Array.Empty<byte>();
        private GpuShaderProgramHandle _program;
        private GpuShaderProgramHandle _skinnedProgram;
        private GpuShaderProgramHandle _shadowProgram;
        private GpuShaderProgramHandle _shadowSkinnedProgram, _shadowSkinnedMidProgram, _shadowSkinnedNearProgram;

        /// <summary>One GPU-skinned mesh that casts shadows this frame.</summary>
        private struct SkinnedShadowCaster
        {
            public int MeshId;
            public int PaletteId;
            public Matrix4x4 World;
            public MeshDrawFlags RasterOverride;
            public bool Far, Near, Mid, Omni;
            public Vector3 Center;
            public float Radius;
        }

        private readonly List<SkinnedShadowCaster> _skinnedShadowCasters = new();
        private GpuShaderProgramHandle _shadowMidProgram;
        private GpuShaderProgramHandle _shadowNearProgram;
        private GpuShaderProgramHandle _waterProgram;
        private GpuShaderProgramHandle _fogProgram;
        private GpuShaderProgramHandle _gtaoProgram;
        private GpuShaderProgramHandle _gtaoBlurProgram;
        private GpuShaderProgramHandle _contactProgram;
        private GpuShaderProgramHandle _localVolProgram;
        private GpuShaderProgramHandle _bloomExtractProgram;
        private GpuShaderProgramHandle _bloomDownsampleProgram;
        private GpuShaderProgramHandle _bloomUpsampleProgram;
        private GpuShaderProgramHandle _raymarchedCloudsProgram;
        private GpuShaderProgramHandle _cloudTemporalProgram;
        private GpuShaderProgramHandle _cloudBilateralProgram;
        private GpuShaderProgramHandle _overrideProgram;
        private GpuShaderProgramHandle _overrideSkinnedProgram;
        private readonly Dictionary<int, (GpuShaderProgramHandle Static, GpuShaderProgramHandle Skinned)> _runtimePrograms = new();
        private readonly Dictionary<int, Genesis.Shared.Assets.ShaderMeshPassMode> _runtimePassModes = new();
        private int _nextRuntimeProgramId = 1;
        private GpuVertexLayoutHandle _layout;
        private GpuVertexLayoutHandle _layoutSkinned;

        private GpuBufferHandle _cbPerFrame;
        private GpuBufferHandle _cbEngine;
        private GpuBufferHandle _cbDraw;
        private GpuBufferHandle _cbWater;
        private GpuBufferHandle _cbFogPost;
        private GpuBufferHandle _cbGtao;
        private GpuBufferHandle _cbContact;
        private GpuBufferHandle _cbLocalVolumetric;
        private GpuBufferHandle _cbBloom;
        private GpuBufferHandle _cbRaymarchedClouds;
        private GpuBufferHandle _cbCloudTemporal;
        private GpuBufferHandle _cbShaderParameters;
        private GpuBufferHandle _cbOmni;

        // Instance buffers — main pass and shadow pass use separate buffers to avoid double upload.
        private GpuBufferHandle _instanceBuf;
        private GpuBufferHandle _shadowInstanceBuf;
        private GpuBufferHandle _clusterLightBuf;
        private GpuBufferHandle _tileLightIndexBuf;
        private const int MaxInstances = 32768;

        // Shadow maps: far + near (R7.11) + optional mid (AF1.1 when cascade count is 3)
        private const int ShadowMapSize     = 1024; // far cascade size (trimmed 2048→1536→1024: each step halves fill-rate; distant shadows soften slightly but are invisible at play distance)
        private const int ShadowMapSizeNear = 1024; // near cascade size (unchanged — keeps crisp close-range shadows)
        private const int ShadowMapSizeMid  = 1024;
        private GpuRenderTargetHandle _shadowTarget;
        private GpuRenderTargetHandle _shadowMidTarget;
        private GpuRenderTargetHandle _shadowNearTarget;
        private GpuTextureHandle _shadowTexture;
        private GpuTextureHandle _shadowMidTexture;
        private GpuTextureHandle _shadowNearTexture;

        // Local light shadows (point and spot) share one depth atlas: ForwardRenderer.LocalShadows.cs.

        // Flat normal map default (1×1 128,128,255 = world-space up) bound to t3 when no NormalMap set
        private GpuTextureHandle _flatNormalTexture;
        private GpuTextureHandle _checkerTexture;
        private GpuTextureHandle _waterNormalA;
        private GpuTextureHandle _waterNormalB;
        private GpuTextureHandle _sunTexture;

        // Samplers
        private GpuSamplerHandle _albedoSampler;
        private GpuSamplerHandle _materialSampler;
        private GpuSamplerHandle _shadowSampler;
        private GpuSamplerHandle _linearSampler;
        private GpuSamplerHandle _pointClampSampler;

        // Rasterizer states
        private GpuRasterState _rsSolid;
        private GpuRasterState _rsSolidCw;
        private GpuRasterState _rsCullFront;
        private GpuRasterState _rsCullFrontCw;
        private GpuRasterState _rsCullNone;
        private GpuRasterState _rsWireframe;
        private GpuRasterState _rsShadow;
        private GpuRasterState _rsShadowCw;
        private GpuRasterState _rsShadowCullFront;
        private GpuRasterState _rsShadowCullFrontCw;
        private GpuRasterState _rsShadowCullNone;

        // Depth-stencil states
        private GpuDepthState _dssDefault;
        private GpuDepthState _dssNoWrite;
        private GpuDepthState _dssNoTest;
        private GpuDepthState _dssFogOff;

        // Blend states
        private GpuBlendState _bsOpaque;
        private GpuBlendState _bsAlpha;
        private GpuBlendState _bsAdd;
        private GpuBlendState _bsMultiply;

        // Screen-space fog (scene color intermediate)
        private GpuRenderTargetHandle _sceneTarget;
        private GpuTextureHandle _sceneTexture;
        private GpuTextureHandle _fogSkipTexture;
        private GpuTextureHandle _sceneDepthTexture;
        // WebGPU: float colour copy of scene depth so Fog/GTAO can SampleLevel (depth is comparison-only).
        // R16Float (not R32Float): WebGPU marks R32Float as unfilterable, which rejects LinearClamp.
        private int _sceneW, _sceneH;

        // AF1.2 half-res GTAO + bilateral blur ping-pong
        private GpuRenderTargetHandle _aoTarget;
        private GpuRenderTargetHandle _aoBlurTarget;
        private GpuTextureHandle _aoTexture;
        private GpuTextureHandle _aoBlurTexture;
        private int _aoW, _aoH;

        // AF1.4 half-res screen-space contact shadows (no blur pass — the march is short enough
        // that a bilateral filter would only smear the contact away).
        private GpuRenderTargetHandle _contactTarget;
        private GpuTextureHandle _contactTexture;
        private int _contactW, _contactH;

        // AF1.5 half-res local-light volumetric scatter. Also unblurred: the beams are already
        // low-frequency, and the half-res bilinear upsample at composite is filter enough.
        private GpuRenderTargetHandle _localVolTarget;
        private GpuTextureHandle _localVolTexture;
        private int _localVolW, _localVolH;

        // AF2.3/AF2.4 raymarched clouds (RGBA16F inscatter + opacity). Full every frame when on.
        // Quality ladder scales internal resolution; optional temporal ping-pong + Cinematic upsample.
        private GpuRenderTargetHandle _raymarchedCloudsTarget;
        private GpuTextureHandle _raymarchedCloudsTexture;
        private readonly GpuRenderTargetHandle[] _cloudHistoryTargets = new GpuRenderTargetHandle[2];
        private readonly GpuTextureHandle[] _cloudHistoryTextures = new GpuTextureHandle[2];
        private int _cloudHistoryWrite;
        private GpuRenderTargetHandle _cloudUpsampleTarget;
        private GpuTextureHandle _cloudUpsampleTexture;
        private int _raymarchedCloudsW, _raymarchedCloudsH;
        private int _cloudUpsampleW, _cloudUpsampleH;
        private Matrix4x4 _prevCloudVP = Matrix4x4.Identity;
        private bool _cloudHistoryValid;
        private Vector3 _prevCloudCameraPos;
        private bool _prevCloudCameraPosValid;
        private GpuTextureHandle _weatherMapTexture;
        private int _weatherMapW, _weatherMapH;
        private float _weatherMapHalfExtent;
        private bool _weatherMapValidThisFrame;
        private Vector2 _weatherMapWorldCenter;
        private byte[] _weatherMapUploadedPixels = Array.Empty<byte>();
        /// <summary>Texture FogPost should sample this frame (march / temporal / upsample).</summary>
        private GpuTextureHandle _cloudCompositeTexture;

        // AF2.1 Atmosphere LUT (SkyService CPU half): 256×64 RGBA8 baked once at create.
        private GpuTextureHandle _atmosphereLutTexture;

        // AF1.7 HDR bloom pyramid: 4 downsample mips (half-res start) + 3 upsample destinations.
        // Final composite samples _bloomUpTextures[0] (or _bloomDownTextures[0] if only one mip).
        private readonly GpuRenderTargetHandle[] _bloomDownTargets =
            new GpuRenderTargetHandle[BloomGradingMath.BloomMipCount];
        private readonly GpuTextureHandle[] _bloomDownTextures =
            new GpuTextureHandle[BloomGradingMath.BloomMipCount];
        private readonly GpuRenderTargetHandle[] _bloomUpTargets =
            new GpuRenderTargetHandle[BloomGradingMath.BloomMipCount - 1];
        private readonly GpuTextureHandle[] _bloomUpTextures =
            new GpuTextureHandle[BloomGradingMath.BloomMipCount - 1];
        private readonly int[] _bloomW = new int[BloomGradingMath.BloomMipCount];
        private readonly int[] _bloomH = new int[BloomGradingMath.BloomMipCount];
        private int _bloomFullW, _bloomFullH;

        // MRT companion to scene colour: a single-channel mask, written 1.0 by ForwardShaders.cs's
        // PS() at any pixel where it already self-applied ray-marched fog (NoDepthWrite draws —
        // particles, the sun, held items — whose true depth the post-process can't see). Sampled
        // by CompositePost/FogPostShaders.cs to skip double-fogging those pixels against the
        // wrong background depth. Cleared to 0 each frame alongside scene colour.

        // Mesh registry
        private readonly List<MeshEntry> _meshes = new();  // index = MeshHandle.Id - 1
        private readonly Stack<int> _freeMeshIds = new();
        private readonly List<SkinPaletteEntry> _skinPalettes = new();
        private readonly HashSet<int> _submittedSkinPalettes = new();
        private readonly Stack<int> _freeSkinPaletteIds = new();
        public MeshHandle FloorMesh { get; private set; }
        public MeshHandle SunMesh   { get; private set; }
        public MeshHandle CubeMesh  { get; private set; }
        public MeshHandle SphereMesh { get; private set; }

        // Frame accumulators
        private readonly Dictionary<BatchKey, Batch> _batches   = new();
        private readonly List<Batch>                 _batchList = new();
        private readonly Stack<Batch>                _batchPool = new();
        private readonly Dictionary<SkinnedBatchKey, SkinnedBatch> _skinnedBatches = new();
        private readonly List<SkinnedBatch>          _skinnedBatchList = new();
        private readonly Stack<SkinnedBatch>         _skinnedBatchPool = new();
        private readonly Dictionary<ShadowBatchKey, ShadowBatch> _shadowBatches = new();
        private readonly List<ShadowBatch>           _shadowBatchList = new();
        private readonly Stack<ShadowBatch>          _shadowBatchPool = new();
        private readonly List<WorldMesh>             _worldMeshes = new();
        private readonly List<WorldMesh>             _viewModelMeshes = new();
        private readonly List<WaterMesh>               _waterMeshes = new();
        // Transparent/additive particle batches — rendered with DrawIndexedInstanced
        private readonly Dictionary<TransBatchKey, TransBatch> _transBatches    = new();
        private readonly List<TransBatch>                       _transBatchList  = new();
        private readonly Stack<TransBatch>                      _transBatchPool  = new();
        private int _skinnedInstOffset;
        private int _transInstOffset; // first index of transparent instances in the shared instance buffer

        // Current state
        private Mesh3DState _state     = Mesh3DState.Default;
        private Matrix4x4   _view      = Matrix4x4.Identity;
        private Matrix4x4   _proj      = Matrix4x4.Identity;
        private float       _nearPlane  = 0.1f;
        private bool        _shadowsActiveThisFrame;
        private bool        _screenFogActiveThisFrame;
        // AF1.6 gate for this frame — see Flush()/ShouldRunSmokeExtinction().
        private bool        _smokeExtinctionActiveThisFrame;
        // Automatic volumetric light-shaft gate for this frame — see Flush(). Not a user toggle.
        private bool        _runVolumetricThisFrame;
        private string      _lastFogModeLogged;
        private int         _lastWaterMeshCountLogged = -1;
        private Vector3     _cameraPos = Vector3.Zero;
        private float       _time;
        private Frustum     _frustum;
        private CascadeShadowFrame _cascade = CascadeShadowMath.Compute(
            0.1f, 200f, 80f, MathF.PI / 3f, 16f / 9f, 1024, 1024, 0.0015f);
        private Vector3     _sortCameraPos;
        private readonly Comparison<Batch>     _batchDistCompare;
        private readonly Comparison<WorldMesh>   _worldMeshDistCompare;
        private readonly Comparison<InstanceGpu> _transInstanceBackToFront;

        /// <summary>Clustered local-light buffer capacity (scene soft cap, R7.5).</summary>
        public const int PointLightCapacity = RenderCapacityDefaults.MaxSceneLocalLightCap;

        /// <summary>GPU instance buffer slots for opaque/transparent mesh batches.</summary>
        public const int InstanceCapacity = MaxInstances;

        private static int SoftMeshInstanceCap =>
            Math.Min(MaxInstances, RenderCapacityDefaults.MeshInstanceCap);

        // Point lights (cleared at BeginSubmitFrame, populated by AddPointLight; tiled at flush)
        private readonly ClusterPointLightGpu[] _pointLights = new ClusterPointLightGpu[PointLightCapacity];
        private int _pointLightCount;
        private readonly ClusteredLightGrid _clusteredLights = new();
        private int _clusterLightUploadCount;

        // Fog volumes (max 8, cleared at BeginSubmitFrame, populated by AddFogVolume — Issue 6 Stage 1)
        private readonly FogVolumeData[] _fogVolumes = new FogVolumeData[MaxFogVolumes];
        private int _fogVolumeCount;

        // AF1.6 smoke volumes (max 8, cleared at BeginSubmitFrame, populated by AddSmokeVolume /
        // SetSmokeVolumes from whoever owns the live particle sims).
        private readonly SmokeExtinctionMath.SmokeVolume[] _smokeVolumes =
            new SmokeExtinctionMath.SmokeVolume[SmokeExtinctionMath.MaxVolumes];
        private int _smokeVolumeCount;

        // Chunk bounds for large-world frustum rejection (keyed by ChunkId)
        private readonly Dictionary<int, (Vector3 Min, Vector3 Max)> _chunkBounds = new();

        // Stats
        public int LastDrawCalls;
        public int LastTriangles;
        public int LastItemsSubmitted;
        public int LastInstancesCulled;
        public int LastInstancesDrawn;
        public int LastBatchCount;
        public int LastFrameInstancesDrawn;
        public int LastFrameInstancesCulled;
        public int LastFrameBatchCount;
        public int LastFrameFoliageInstances;
        public int LastFrameFoliageBatches;
        public int LastFrameWorldMeshes;
        public int LastFramePointLights;
        public long LastFrameFoliageUploadBytes;
        private int _foliageInstances;
        private int _foliageBatches;
        /// <summary>Remaining Manual world-batch draws for this MainPass (unlimited when Auto).</summary>
        private int _worldDrawBudgetLeft;

        public bool HasPixelShaderOverride => _overrideProgram.IsValid;
        public bool HasRuntimeShaders => _runtimePrograms.Count > 0;

        public void SetPixelShaderOverride(byte[] pixelShader)
            => SetShaderProgramOverride(null, pixelShader);

        public void SetShaderProgramOverride(byte[] vertexShader, byte[] pixelShader)
        {
            if (pixelShader == null || pixelShader.Length == 0)
            {
                _gpu.ReleaseShaderProgram(_overrideProgram);
                _gpu.ReleaseShaderProgram(_overrideSkinnedProgram);
                _overrideProgram = GpuShaderProgramHandle.Invalid;
                _overrideSkinnedProgram = GpuShaderProgramHandle.Invalid;
                return;
            }

            GpuShaderProgramHandle replacement = GpuShaderProgramHandle.Invalid;
            GpuShaderProgramHandle skinnedReplacement = GpuShaderProgramHandle.Invalid;
            try
            {
                replacement = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = vertexShader is { Length: > 0 } ? vertexShader : _mainVertexShader,
                    PixelShader = pixelShader,
                    DebugName = "Forward preview override",
                });
                skinnedReplacement = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = _skinnedVertexShader,
                    PixelShader = pixelShader,
                    DebugName = "Forward skinned preview override",
                });
            }
            catch
            {
                _gpu.ReleaseShaderProgram(replacement);
                _gpu.ReleaseShaderProgram(skinnedReplacement);
                throw;
            }
            _gpu.ReleaseShaderProgram(_overrideProgram);
            _gpu.ReleaseShaderProgram(_overrideSkinnedProgram);
            _overrideProgram = replacement;
            _overrideSkinnedProgram = skinnedReplacement;
        }

        public RuntimeShaderHandle RegisterRuntimeShader(byte[] pixelShader)
            => RegisterRuntimeShaderProgram(null, pixelShader);

        public RuntimeShaderHandle RegisterRuntimeShaderProgram(byte[] vertexShader, byte[] pixelShader,
            byte[] skinnedVertexShader = null,
            Genesis.Shared.Assets.ShaderMeshPassMode mode = Genesis.Shared.Assets.ShaderMeshPassMode.Surface)
        {
            if (pixelShader == null || pixelShader.Length == 0) throw new ArgumentException("Runtime shader bytecode is empty.", nameof(pixelShader));
            GpuShaderProgramHandle staticProgram = GpuShaderProgramHandle.Invalid;
            GpuShaderProgramHandle skinnedProgram = GpuShaderProgramHandle.Invalid;
            try
            {
                staticProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = vertexShader is { Length: > 0 } ? vertexShader : _mainVertexShader, PixelShader = pixelShader, DebugName = "Forward.RuntimeShader" });
                skinnedProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = skinnedVertexShader is { Length: > 0 } ? skinnedVertexShader : _skinnedVertexShader, PixelShader = pixelShader, DebugName = "Forward.RuntimeShader.Skinned" });
                int id = _nextRuntimeProgramId++;
                _runtimePrograms.Add(id, (staticProgram, skinnedProgram));
                _runtimePassModes.Add(id, mode);
                return new RuntimeShaderHandle(id);
            }
            catch
            {
                _gpu.ReleaseShaderProgram(staticProgram);
                _gpu.ReleaseShaderProgram(skinnedProgram);
                throw;
            }
        }

        public void ReleaseRuntimeShader(RuntimeShaderHandle handle)
        {
            if (!handle.IsValid || !_runtimePrograms.Remove(handle.Id, out var programs)) return;
            _runtimePassModes.Remove(handle.Id);
            _gpu.ReleaseShaderProgram(programs.Static);
            _gpu.ReleaseShaderProgram(programs.Skinned);
        }

        // ── GPU timing (Phase 0) ───────────────────────────────────────────────────
        // Ring of (timestamp-disjoint + start/end timestamp) query sets. We record into slot
        // N each frame and read back the oldest slot, so results are ready without stalling
        // the pipeline. Pure measurement — does not affect any draw.
        private const int TimingRing = 3;
        private readonly GpuQueryHandle[] _timingQueries = new GpuQueryHandle[TimingRing];
        private GpuQueryHandle _activeTimingQuery;
        /// <summary>GPU milliseconds for the last resolved Flush (a few frames of latency).</summary>
        public double LastGpuMs { get; private set; }
        /// <summary>CPU Stopwatch cost of the last GTAO pass (0 when skipped).</summary>
        public double LastAoMs { get; private set; }
        /// <summary>CPU Stopwatch cost of the last contact-shadow pass (0 when skipped).</summary>
        public double LastContactShadowMs { get; private set; }
        /// <summary>CPU Stopwatch cost of the last local-volumetric pass (0 when skipped).</summary>
        public double LastLocalVolumetricMs { get; private set; }

        /// <summary>
        /// AF1.6 smoke-extinction cost. There is no separate GPU pass — smoke rides the existing
        /// fog composite — so this is the per-frame volume pack and constant-buffer upload only,
        /// and it is expected to be a small fraction of a millisecond. Still reported, because a
        /// cost F6 cannot see is a cost nobody notices growing.
        /// </summary>
        public double LastSmokeExtinctionMs { get; private set; }
        /// <summary>CPU Stopwatch cost of the last bloom pyramid (0 when skipped / Software).</summary>
        public double LastBloomMs { get; private set; }
        /// <summary>
        /// AF2.1 atmosphere LUT cost. Bake/upload is once at create; sampling is free in composite.
        /// Reports a tiny CPU sentinel while enabled so F6 shows <c>AL: … ms</c> rather than off.
        /// </summary>
        public double LastAtmosphereLutMs { get; private set; }
        /// <summary>CPU Stopwatch cost of the last AF2.3 raymarched cloud pass (0 when skipped).</summary>
        public double LastRaymarchedCloudsMs { get; private set; }
        /// <summary>
        /// AF2.5 celestial extras cost. Sampling is free in FogPost composite; reports a tiny CPU
        /// sentinel while enabled so F6 shows <c>CE: … ms</c> rather than off.
        /// </summary>
        public double LastCelestialExtrasMs { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────────

        public ForwardRenderer(IGpuDevice gpu)
        {
            _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
            _batchDistCompare = (a, b) =>
            {
                int distance = BatchDistanceSquared(_sortCameraPos, a).CompareTo(BatchDistanceSquared(_sortCameraPos, b));
                // List.Sort is not stable. Equal-depth authored passes must retain surface,
                // mask, silhouette, reset order, including scenes with many material batches.
                return distance != 0 ? distance : a.SubmissionOrder.CompareTo(b.SubmissionOrder);
            };
            _worldMeshDistCompare = (a, b) =>
            {
                int distance = WorldMeshDistanceSquared(_sortCameraPos, a).CompareTo(WorldMeshDistanceSquared(_sortCameraPos, b));
                return distance != 0 ? distance : a.Order.CompareTo(b.Order);
            };
            _transInstanceBackToFront = (a, b) =>
            {
                float da = Vector3.DistanceSquared(_sortCameraPos, a.World.Translation);
                float db = Vector3.DistanceSquared(_sortCameraPos, b.World.Translation);
                return db.CompareTo(da);
            };
            CompileShaders();
            CompileWaterShaders();
            CreateConstantBuffers();
            CreateWaterNormalMaps();
            CreateInstanceBuffer();
            CreateShadowMap();
            CreateFroxelResources();
            CreateSamplers();
            CreateRasterizerStates();
            CreateDepthStencilStates();
            CreateBlendStates();
            CreateSunTexture();
            CreateFlatNormalMap();
            CreateCheckerboardTexture();
            CreateFogPostResources();
            CreateGtaoResources();
            CreateContactShadowResources();
            CreateLocalVolumetricResources();
            CreateBloomResources();
            CreateAtmosphereLutResources();
            CreateRaymarchedCloudsResources();
            RegisterBuiltinMeshes();
        }

        // ── Shader compilation ────────────────────────────────────────────────────

        private byte[] CompileShader(string source, string entryPoint, GpuShaderStage stage)
        {
            return ShaderCompiler.CompileForBackend(
                source, entryPoint, stage, _gpu.ShaderBinaryFormat).Blob;
        }

        private void CompileShaders()
        {
            _mainVertexShader     = CompileShader(ForwardShaders.Source, "VS", GpuShaderStage.Vertex);
            _skinnedVertexShader  = CompileShader(ForwardShaders.Source, "VS_Skinned", GpuShaderStage.Vertex);
            byte[] vsShadBlob     = CompileShader(ForwardShaders.Source, "VS_Shadow", GpuShaderStage.Vertex);
            byte[] vsShadMidBlob  = CompileShader(ForwardShaders.Source, "VS_ShadowMid", GpuShaderStage.Vertex);
            byte[] vsShadNearBlob = CompileShader(ForwardShaders.Source, "VS_ShadowNear", GpuShaderStage.Vertex);
            byte[] psBlob         = CompileShader(ForwardShaders.Source, "PS", GpuShaderStage.Pixel);

            _program = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = _mainVertexShader, PixelShader = psBlob, DebugName = "Forward" });
            _skinnedProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = _skinnedVertexShader, PixelShader = psBlob, DebugName = "Forward skinned" });
            _shadowProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = vsShadBlob, DebugName = "Shadow far" });
            _shadowMidProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = vsShadMidBlob, DebugName = "Shadow mid" });
            _shadowNearProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = vsShadNearBlob, DebugName = "Shadow near" });
            // The CPU rasterizer has no skinned shadow path; animated actors stay shadowless there.
            // It has no local-light shadows either, so the atlas tile clear is GPU-only too.
            if (!string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase))
            {
                _shadowTileClearProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = CompileShader(ForwardShaders.Source, "VS_ShadowTileClear", GpuShaderStage.Vertex),
                    DebugName = "Local shadow tile clear",
                });
                _shadowSkinnedProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = CompileShader(ForwardShaders.Source, "VS_ShadowSkinned", GpuShaderStage.Vertex),
                    DebugName = "Shadow far skinned",
                });
                _shadowSkinnedMidProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = CompileShader(ForwardShaders.Source, "VS_ShadowSkinnedMid", GpuShaderStage.Vertex),
                    DebugName = "Shadow mid skinned",
                });
                _shadowSkinnedNearProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = CompileShader(ForwardShaders.Source, "VS_ShadowSkinnedNear", GpuShaderStage.Vertex),
                    DebugName = "Shadow near skinned",
                });
            }

            CreateInputLayout();
            CreateSkinnedInputLayout();
        }

        private void CreateInputLayout()
        {
            _layout = _gpu.CreateVertexLayout(new GpuVertexLayoutDesc
            {
                Elements = new[]
                {
                    Element("POSITION", GpuFormat.R32Float3, 0),
                    Element("NORMAL", GpuFormat.R32Float3, 12),
                    Element("COLOR", GpuFormat.R32Float4, 24),
                    Element("TEXCOORD", GpuFormat.R32Float2, 40),
                },
                SlotStrides = new[] { Marshal.SizeOf<MeshVertex>() },
                DebugName = "Forward mesh layout",
            }, _program);
        }

        private void CreateSkinnedInputLayout()
        {
            _layoutSkinned = _gpu.CreateVertexLayout(new GpuVertexLayoutDesc
            {
                Elements = new[]
                {
                    Element("POSITION", GpuFormat.R32Float3, 0),
                    Element("NORMAL", GpuFormat.R32Float3, 12),
                    Element("COLOR", GpuFormat.R32Float4, 24),
                    Element("TEXCOORD", GpuFormat.R32Float2, 40),
                    Element("BLENDWEIGHT", GpuFormat.R32Float4, 48),
                    Element("BLENDINDICES", GpuFormat.R32Float4, 64),
                },
                SlotStrides = new[] { Marshal.SizeOf<SkinnedMeshVertex>() },
                DebugName = "Forward skinned layout",
            }, _skinnedProgram);
        }

        private static GpuVertexElement Element(string semantic, GpuFormat format, int offset) =>
            new GpuVertexElement { Semantic = semantic, Format = format, Slot = 0, OffsetBytes = offset };

        private void CompileWaterShaders()
        {
            byte[] vsBlob = CompileShader(WaterShaders.Source, "VS_Water", GpuShaderStage.Vertex);
            byte[] psBlob = CompileShader(WaterShaders.Source, "PS_Water", GpuShaderStage.Pixel);
            _waterProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = vsBlob, PixelShader = psBlob, DebugName = "Water" });
        }

        private void CreateWaterNormalMaps()
        {
            const int size = 128;
            var pxA = new byte[size * size * 4];
            var pxB = new byte[size * size * 4];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float v = y / (float)size;

                    float h1 = MathF.Sin(u * MathF.Tau * 3f) * MathF.Cos(v * MathF.Tau * 2f);
                    float h2 = MathF.Cos(u * MathF.Tau * 5f + 1.2f) * MathF.Sin(v * MathF.Tau * 4f);
                    WriteNormalPixel(pxA, x, y, size, -h1 * 0.35f - h2 * 0.2f, -h2 * 0.25f + h1 * 0.1f);

                    float h3 = MathF.Sin(u * MathF.Tau * 7f + 0.5f) * MathF.Sin(v * MathF.Tau * 6f);
                    float h4 = MathF.Cos(u * MathF.Tau * 4f) * MathF.Cos(v * MathF.Tau * 8f + 2f);
                    WriteNormalPixel(pxB, x, y, size, -h3 * 0.28f, -h4 * 0.22f);
                }
            }

            _waterNormalA = CreateTexture(size, size, GpuFormat.R8G8B8A8UNorm, pxA, "Water normal A");
            _waterNormalB = CreateTexture(size, size, GpuFormat.R8G8B8A8UNorm, pxB, "Water normal B");
        }

        private static void WriteNormalPixel(byte[] px, int x, int y, int size, float dx, float dz)
        {
            var n = Vector3.Normalize(new Vector3(dx, dz, 1f));
            int i = (y * size + x) * 4;
            px[i + 0] = (byte)((n.X * 0.5f + 0.5f) * 255f);
            px[i + 1] = (byte)((n.Y * 0.5f + 0.5f) * 255f);
            px[i + 2] = (byte)((n.Z * 0.5f + 0.5f) * 255f);
            px[i + 3] = 255;
        }

        private GpuTextureHandle CreateTexture(
            int width, int height, GpuFormat format, ReadOnlySpan<byte> pixels, string debugName)
        {
            return _gpu.CreateTexture(new GpuTextureDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = format,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = debugName,
            }, pixels);
        }

        // ── Constant buffers ──────────────────────────────────────────────────────

        // Dynamic neutral constant buffers; the backend chooses its update strategy.
        private GpuBufferHandle MakeCB<T>(string debugName) where T : unmanaged
        {
            int size = AlignUp(Marshal.SizeOf<T>(), 16);
            return _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = size,
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer,
                DebugName = debugName,
            }, ReadOnlySpan<byte>.Empty);
        }

        // Dynamic CB: updated every draw call via Map(WriteDiscard) — no GPU pipeline stall
        private static int AlignUp(int value, int align) => (value + align - 1) & ~(align - 1);

        private void CreateConstantBuffers()
        {
            _cbPerFrame = MakeCB<PerFrameCB>("Forward per-frame constants");
            _cbEngine   = MakeCB<EngineCB>("Forward engine constants");
            _cbDraw     = MakeCB<DrawCB>("Forward draw constants");
            _cbWater    = MakeCB<WaterCB>("Water constants");
            _cbShaderParameters = MakeCB<ShaderParametersCB>("Authored shader parameters");
            _cbOmni = MakeCB<LocalShadowCB>("Local shadow constants");
        }

        // ── Instance buffer ───────────────────────────────────────────────────────

        private GpuBufferHandle CreateStructuredBuffer<T>(int capacity, string debugName) where T : unmanaged
        {
            int stride = Marshal.SizeOf<T>();
            return _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = stride * Math.Max(1, capacity),
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ShaderResource | GpuBindFlags.StructuredBuffer,
                StructureStride = stride,
                DebugName = debugName,
            }, ReadOnlySpan<byte>.Empty);
        }

        private GpuBufferHandle CreateSkinMatrixBuffer(int matrixCount)
        {
            return CreateStructuredBuffer<Matrix4x4>(matrixCount, "Skin palette");
        }

        private void CreateInstanceBuffer()
        {
            _instanceBuf = CreateStructuredBuffer<InstanceGpu>(MaxInstances, "Forward instances");
            _shadowInstanceBuf = CreateStructuredBuffer<InstanceGpu>(MaxInstances, "Shadow instances");
            _clusterLightBuf = CreateStructuredBuffer<ClusterPointLightGpu>(
                PointLightCapacity, "Clustered point lights");
            // Packed clustered light lists: (offset, count) per cluster, then the light indices.
            _tileLightIndexBuf = CreateStructuredBuffer<uint>(
                ClusteredLightDefaults.BufferWords,
                "Clustered light lists");
        }

        // ── Shadow map ────────────────────────────────────────────────────────────

        private GpuRenderTargetHandle CreateDepthOnlyTarget(int size, string debugName)
        {
            return _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = size,
                Height = size,
                ColorFormats = Array.Empty<GpuFormat>(),
                DepthFormat = GpuFormat.D32Float,
                DepthSampleable = true,
                DebugName = debugName,
            });
        }

        private void CreateShadowMap()
        {
            _shadowTarget = CreateDepthOnlyTarget(ShadowMapSize, "Shadow far target");
            _shadowMidTarget = CreateDepthOnlyTarget(ShadowMapSizeMid, "Shadow mid target");
            _shadowNearTarget = CreateDepthOnlyTarget(ShadowMapSizeNear, "Shadow near target");
            _shadowTexture = _gpu.GetRenderTargetDepthTexture(_shadowTarget);
            _shadowMidTexture = _gpu.GetRenderTargetDepthTexture(_shadowMidTarget);
            _shadowNearTexture = _gpu.GetRenderTargetDepthTexture(_shadowNearTarget);
        }

        private void CreateFlatNormalMap()
        {
            // 1×1 texture containing (128,128,255,255) — the RGBA encoding of a flat normal (0,0,1) in tangent space.
            byte[] px = { 128, 128, 255, 255 };
            _flatNormalTexture = CreateTexture(1, 1, GpuFormat.R8G8B8A8UNorm, px, "Flat normal");
        }

        private void CreateCheckerboardTexture()
        {
            // 2×2 repeating tile — floor UVs tile across the mesh; PS tints dark/light via .r.
            byte[] px =
            {
                255, 255, 255, 255,  140, 140, 140, 255,
                140, 140, 140, 255,  255, 255, 255, 255,
            };
            _checkerTexture = CreateTexture(2, 2, GpuFormat.R8G8B8A8UNorm, px, "Checkerboard");
        }

        // ── Samplers ──────────────────────────────────────────────────────────────

        private void CreateSamplers()
        {
            // Pixel textures retain point sampling; oblique PBR surfaces need anisotropic filtering.
            _albedoSampler = _gpu.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Point,
                AddressU = GpuAddressMode.Wrap,
                AddressV = GpuAddressMode.Wrap,
                AddressW = GpuAddressMode.Wrap,
                CompareOp = GpuCompare.Never,
                DebugName = "Forward albedo sampler",
            });
            _materialSampler = _gpu.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Anisotropic, MaxAnisotropy = Math.Clamp(_gpu.Capabilities.MaxAnisotropy, 1, 8), AddressU = GpuAddressMode.Wrap,
                AddressV = GpuAddressMode.Wrap, AddressW = GpuAddressMode.Wrap,
                CompareOp = GpuCompare.Never, DebugName = "Forward PBR sampler",
            });

            // Shadow: comparison + border=1
            _shadowSampler = _gpu.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Linear,
                AddressU = GpuAddressMode.Border,
                AddressV = GpuAddressMode.Border,
                AddressW = GpuAddressMode.Border,
                CompareOp = GpuCompare.LessEqual,
                BorderColorR = 1f, BorderColorG = 1f, BorderColorB = 1f, BorderColorA = 1f,
                DebugName = "Shadow comparison sampler",
            });

            // Non-comparison point clamp — WebGPU R16F shadow resolves + cloud temporal.
            _pointClampSampler = _gpu.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Point,
                AddressU = GpuAddressMode.Clamp,
                AddressV = GpuAddressMode.Clamp,
                AddressW = GpuAddressMode.Clamp,
                CompareOp = GpuCompare.Never,
                DebugName = "Point clamp sampler",
            });
        }

        // ── Rasterizer states ─────────────────────────────────────────────────────

        private void CreateRasterizerStates()
        {
            _rsSolid = new GpuRasterState
            {
                FillMode = GpuFillMode.Solid,
                CullMode = GpuCullMode.Back,
                FrontCounterClockwise = false,
                DepthClipEnabled = true,
            };
            _rsSolidCw = _rsSolid;
            _rsSolidCw.FrontCounterClockwise = true;
            _rsCullFront = _rsSolid;
            _rsCullFront.CullMode = GpuCullMode.Front;
            _rsCullFrontCw = _rsCullFront;
            _rsCullFrontCw.FrontCounterClockwise = true;
            _rsCullNone = _rsSolid;
            _rsCullNone.CullMode = GpuCullMode.None;
            _rsWireframe = new GpuRasterState
            {
                FillMode = GpuFillMode.Wireframe,
                CullMode = GpuCullMode.None,
                FrontCounterClockwise = false,
                DepthClipEnabled = true,
            };

            // Shadow variants mirror the visible pass. Depth bias handles acne; silently swapping
            // front/back here makes a per-object winding override cast the opposite silhouette.
            _rsShadow = new GpuRasterState
            {
                FillMode = GpuFillMode.Solid,
                CullMode = GpuCullMode.Back,
                FrontCounterClockwise = false,
                DepthBias = 400,
                SlopeScaledDepthBias = 1f,
                DepthClipEnabled = false,
            };
            _rsShadowCw = _rsShadow;
            _rsShadowCw.FrontCounterClockwise = true;
            _rsShadowCullFront = _rsShadow;
            _rsShadowCullFront.CullMode = GpuCullMode.Front;
            _rsShadowCullFrontCw = _rsShadowCullFront;
            _rsShadowCullFrontCw.FrontCounterClockwise = true;
            _rsShadowCullNone = _rsShadow;
            _rsShadowCullNone.CullMode = GpuCullMode.None;
        }

        // ── Depth-stencil states ──────────────────────────────────────────────────

        private void CreateDepthStencilStates()
        {
            _dssDefault = new GpuDepthState
            {
                TestEnabled = true,
                WriteEnabled = true,
                Compare = GpuCompare.Less,
            };
            _dssNoWrite = _dssDefault;
            _dssNoWrite.WriteEnabled = false;
            _dssNoTest = new GpuDepthState
            {
                TestEnabled = false,
                WriteEnabled = false,
                Compare = GpuCompare.Less,
            };
            _dssFogOff = GpuDepthState.Disabled;
            // Writes far depth over one local shadow atlas tile regardless of what it held.
            _dssAlways = new GpuDepthState
            {
                TestEnabled = true,
                WriteEnabled = true,
                Compare = GpuCompare.Always,
            };
        }

        // ── Blend states ──────────────────────────────────────────────────────────

        private void CreateBlendStates()
        {
            _bsOpaque = GpuBlendState.Opaque;

            // IndependentBlendEnable: these two states draw into the scene-colour target (slot 0,
            // genuinely alpha/additive-blended) AND the fog-skip-mask target (slot 1, MRT — see
            // fog-skip attachment) at the same time. Without independent blending, a backend would replicate
            // slot 0's alpha-blend formula onto slot 1 too, which would blend the mask's 0/1 flag
            // using the draw's alpha instead of writing it cleanly — corrupting its binary meaning
            // for anything but fully-opaque (alpha=1) transparent draws. Slot 1 here is a plain
            // overwrite so whichever NoDepthWrite draw touches a pixel last sets the flag exactly
            // as its shader computed it.
            _bsAlpha = new GpuBlendState
            {
                Enabled = true,
                SrcColor = GpuBlendFactor.SrcAlpha,
                DstColor = GpuBlendFactor.InvSrcAlpha,
                ColorOp = GpuBlendOp.Add,
                SrcAlpha = GpuBlendFactor.One,
                DstAlpha = GpuBlendFactor.Zero,
                AlphaOp = GpuBlendOp.Add,
                WriteR = true, WriteG = true, WriteB = true, WriteA = true,
                IndependentBlend = true,
                SecondaryEnabled = false,
                SecondarySrcColor = GpuBlendFactor.One,
                SecondaryDstColor = GpuBlendFactor.Zero,
                SecondaryColorOp = GpuBlendOp.Add,
                SecondarySrcAlpha = GpuBlendFactor.One,
                SecondaryDstAlpha = GpuBlendFactor.Zero,
                SecondaryAlphaOp = GpuBlendOp.Add,
                SecondaryWriteR = true, SecondaryWriteG = false,
                SecondaryWriteB = false, SecondaryWriteA = false,
            };

            _bsAdd = new GpuBlendState
            {
                Enabled = true,
                SrcColor = GpuBlendFactor.One,
                DstColor = GpuBlendFactor.One,
                ColorOp = GpuBlendOp.Add,
                SrcAlpha = GpuBlendFactor.One,
                DstAlpha = GpuBlendFactor.Zero,
                AlphaOp = GpuBlendOp.Add,
                WriteR = true, WriteG = true, WriteB = true, WriteA = true,
                IndependentBlend = true,
                SecondaryEnabled = false,
                SecondarySrcColor = GpuBlendFactor.One,
                SecondaryDstColor = GpuBlendFactor.Zero,
                SecondaryColorOp = GpuBlendOp.Add,
                SecondarySrcAlpha = GpuBlendFactor.One,
                SecondaryDstAlpha = GpuBlendFactor.Zero,
                SecondaryAlphaOp = GpuBlendOp.Add,
                SecondaryWriteR = true, SecondaryWriteG = false,
                SecondaryWriteB = false, SecondaryWriteA = false,
            };
            _bsMultiply = _bsAlpha;
            _bsMultiply.SrcColor = GpuBlendFactor.DestColor;
            _bsMultiply.DstColor = GpuBlendFactor.InvSrcAlpha;
            _bsMultiply.SrcAlpha = GpuBlendFactor.Zero;
            _bsMultiply.DstAlpha = GpuBlendFactor.One;
        }

        private void CreateSunTexture()
        {
            const int size = 128;
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size - 0.5f;
                    float dy = (y + 0.5f) / size - 0.5f;
                    float r  = MathF.Sqrt(dx * dx + dy * dy) * 2f;
                    float a  = MathF.Max(0f, 1f - r);
                    a = a * a * (3f - 2f * a);
                    float core = MathF.Max(0f, 1f - r * 1.35f);
                    int i = (y * size + x) * 4;
                    px[i + 0] = (byte)Math.Clamp(255 * (0.85f + 0.15f * core), 0, 255);
                    px[i + 1] = (byte)Math.Clamp(255 * (0.72f + 0.23f * core), 0, 255);
                    px[i + 2] = (byte)Math.Clamp(255 * (0.35f + 0.45f * core), 0, 255);
                    px[i + 3] = (byte)Math.Clamp(255 * a, 0, 255);
                }
            }

            _sunTexture = CreateTexture(size, size, GpuFormat.R8G8B8A8UNorm, px, "Sun billboard");
        }

        private void CreateFogPostResources()
        {
            byte[] vsBlob = CompileShader(FogPostShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] psBlob = CompileShader(FogPostShaders.Source, "PS", GpuShaderStage.Pixel);
            _fogProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = vsBlob, PixelShader = psBlob, DebugName = "Fog post" });

            _cbFogPost = MakeCB<FogPostCB>("Fog post constants");

            _linearSampler = _gpu.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Linear,
                AddressU = GpuAddressMode.Clamp,
                AddressV = GpuAddressMode.Clamp,
                AddressW = GpuAddressMode.Clamp,
                CompareOp = GpuCompare.Never,
                DebugName = "Fog linear sampler",
            });
        }

        private void CreateGtaoResources()
        {
            byte[] vsBlob = CompileShader(GtaoShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] psGtaoBlob = CompileShader(GtaoShaders.Source, "PS_Gtao", GpuShaderStage.Pixel);
            byte[] psBlurBlob = CompileShader(GtaoShaders.Source, "PS_Blur", GpuShaderStage.Pixel);
            _gtaoProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psGtaoBlob,
                DebugName = "GTAO",
            });
            _gtaoBlurProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psBlurBlob,
                DebugName = "GTAO blur",
            });
            _cbGtao = MakeCB<GtaoCB>("GTAO constants");
        }

        private void CreateContactShadowResources()
        {
            byte[] vsBlob = CompileShader(ContactShadowShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] psBlob = CompileShader(ContactShadowShaders.Source, "PS_Contact", GpuShaderStage.Pixel);
            _contactProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psBlob,
                DebugName = "Contact shadows",
            });
            _cbContact = MakeCB<ContactShadowCB>("Contact shadow constants");
        }

        private void CreateLocalVolumetricResources()
        {
            byte[] vsBlob = CompileShader(LocalVolumetricShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] psBlob = CompileShader(
                LocalVolumetricShaders.Source, "PS_LocalVol", GpuShaderStage.Pixel);
            _localVolProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psBlob,
                DebugName = "Local volumetric scatter",
            });
            _cbLocalVolumetric = MakeCB<LocalVolumetricCB>("Local volumetric constants");
        }

        private void CreateBloomResources()
        {
            byte[] vsBlob = CompileShader(BloomShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] psExtract = CompileShader(
                BloomShaders.Source, "PS_ThresholdExtract", GpuShaderStage.Pixel);
            byte[] psDown = CompileShader(
                BloomShaders.Source, "PS_Downsample", GpuShaderStage.Pixel);
            byte[] psUp = CompileShader(
                BloomShaders.Source, "PS_Upsample", GpuShaderStage.Pixel);
            _bloomExtractProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psExtract,
                DebugName = "Bloom threshold extract",
            });
            _bloomDownsampleProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psDown,
                DebugName = "Bloom downsample",
            });
            _bloomUpsampleProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psUp,
                DebugName = "Bloom upsample",
            });
            _cbBloom = MakeCB<BloomCB>("Bloom constants");
        }

        /// <summary>
        /// AF2.1 SkyService CPU half: bake the packed atmosphere atlas once and upload as an
        /// immutable RGBA8 texture. Sampling happens in FogPost when the flag is on; Software never
        /// binds it.
        /// </summary>
        private void CreateAtmosphereLutResources()
        {
            byte[] pixels = AtmosphereLutMath.BakeRgba8();
            _atmosphereLutTexture = CreateTexture(
                AtmosphereLutMath.Width,
                AtmosphereLutMath.Height,
                GpuFormat.R8G8B8A8UNorm,
                pixels,
                "Atmosphere LUT (AF2.1)");
        }

        private void CreateRaymarchedCloudsResources()
        {
            byte[] vsBlob = CompileShader(RaymarchedCloudsShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] psBlob = CompileShader(
                RaymarchedCloudsShaders.Source, "PS_Clouds", GpuShaderStage.Pixel);
            _raymarchedCloudsProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vsBlob,
                PixelShader = psBlob,
                DebugName = "Raymarched clouds (AF2.3)",
            });
            _cbRaymarchedClouds = MakeCB<RaymarchedCloudsCB>("Raymarched clouds constants");

            byte[] temporalVs = CompileShader(CloudTemporalShaders.Source, "VS", GpuShaderStage.Vertex);
            byte[] temporalPs = CompileShader(
                CloudTemporalShaders.Source, "PS_TemporalResolve", GpuShaderStage.Pixel);
            byte[] bilateralPs = CompileShader(
                CloudTemporalShaders.Source, "PS_BilateralUpsample", GpuShaderStage.Pixel);
            _cloudTemporalProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = temporalVs,
                PixelShader = temporalPs,
                DebugName = "Cloud temporal resolve (AF2.4)",
            });
            _cloudBilateralProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = temporalVs,
                PixelShader = bilateralPs,
                DebugName = "Cloud bilateral upsample (AF2.4)",
            });
            _cbCloudTemporal = MakeCB<CloudTemporalCB>("Cloud temporal constants");
        }

        private void EnsureBloomTargets(int fullW, int fullH)
        {
            if (_bloomDownTargets[0].IsValid && fullW == _bloomFullW && fullH == _bloomFullH)
                return;

            ReleaseBloomTargets();
            _bloomFullW = fullW;
            _bloomFullH = fullH;

            int w = Math.Max(1, fullW / 2);
            int h = Math.Max(1, fullH / 2);
            for (int i = 0; i < BloomGradingMath.BloomMipCount; i++)
            {
                _bloomW[i] = w;
                _bloomH[i] = h;
                _bloomDownTargets[i] = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = w,
                    Height = h,
                    ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = $"Bloom down mip {i}",
                });
                _bloomDownTextures[i] = _gpu.GetRenderTargetTexture(_bloomDownTargets[i], 0);
                if (i < BloomGradingMath.BloomMipCount - 1)
                {
                    _bloomUpTargets[i] = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                    {
                        Width = w,
                        Height = h,
                        ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                        DepthFormat = GpuFormat.Unknown,
                        DepthSampleable = false,
                        DebugName = $"Bloom up mip {i}",
                    });
                    _bloomUpTextures[i] = _gpu.GetRenderTargetTexture(_bloomUpTargets[i], 0);
                }

                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
        }

        private void ReleaseBloomTargets()
        {
            for (int i = 0; i < _bloomDownTargets.Length; i++)
            {
                if (_bloomDownTargets[i].IsValid)
                    _gpu.ReleaseRenderTarget(_bloomDownTargets[i]);
                _bloomDownTargets[i] = GpuRenderTargetHandle.Invalid;
                _bloomDownTextures[i] = GpuTextureHandle.Invalid;
            }

            for (int i = 0; i < _bloomUpTargets.Length; i++)
            {
                if (_bloomUpTargets[i].IsValid)
                    _gpu.ReleaseRenderTarget(_bloomUpTargets[i]);
                _bloomUpTargets[i] = GpuRenderTargetHandle.Invalid;
                _bloomUpTextures[i] = GpuTextureHandle.Invalid;
            }

            _bloomFullW = 0;
            _bloomFullH = 0;
        }

        private void EnsureLocalVolumetricTargets(int fullW, int fullH)
        {
            int w = Math.Max(1, fullW / 2);
            int h = Math.Max(1, fullH / 2);
            if (_localVolTarget.IsValid && w == _localVolW && h == _localVolH)
                return;

            if (_localVolTarget.IsValid) _gpu.ReleaseRenderTarget(_localVolTarget);
            _localVolTarget = GpuRenderTargetHandle.Invalid;
            _localVolTexture = GpuTextureHandle.Invalid;

            _localVolW = w;
            _localVolH = h;

            _localVolTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = w,
                Height = h,
                // RGBA16F for the same reason as the GTAO/contact targets, and because this one
                // genuinely needs the range: the inscatter is HDR and is added before ACES.
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = "Local volumetric half-res",
            });
            _localVolTexture = _gpu.GetRenderTargetTexture(_localVolTarget, 0);
        }

        private void EnsureRaymarchedCloudsTargets(int fullW, int fullH)
        {
            CloudQualityTier tier = CloudTemporalMath.ClampTier(_state.CloudQuality);
            float scale = CloudTemporalMath.ScaleFor(tier);
            (int w, int h) = CloudTemporalMath.ResolveInternalSize(fullW, fullH, scale);
            bool needHistory = _state.CloudTemporalEnabled;
            bool needUpsample = tier == CloudQualityTier.Cinematic;

            if (_raymarchedCloudsTarget.IsValid
                && w == _raymarchedCloudsW
                && h == _raymarchedCloudsH
                && (!needHistory || _cloudHistoryTargets[0].IsValid)
                && (needHistory || !_cloudHistoryTargets[0].IsValid)
                && (!needUpsample || (_cloudUpsampleTarget.IsValid
                    && _cloudUpsampleW == fullW
                    && _cloudUpsampleH == fullH))
                && (needUpsample || !_cloudUpsampleTarget.IsValid))
            {
                return;
            }

            ReleaseRaymarchedCloudsTargets();
            _cloudHistoryValid = false;
            _prevCloudCameraPosValid = false;

            _raymarchedCloudsW = w;
            _raymarchedCloudsH = h;

            _raymarchedCloudsTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = w,
                Height = h,
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = "Raymarched clouds internal",
            });
            _raymarchedCloudsTexture = _gpu.GetRenderTargetTexture(_raymarchedCloudsTarget, 0);

            if (needHistory)
            {
                for (int i = 0; i < 2; i++)
                {
                    _cloudHistoryTargets[i] = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                    {
                        Width = w,
                        Height = h,
                        ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                        DepthFormat = GpuFormat.Unknown,
                        DepthSampleable = false,
                        DebugName = $"Cloud temporal history {i}",
                    });
                    _cloudHistoryTextures[i] = _gpu.GetRenderTargetTexture(_cloudHistoryTargets[i], 0);
                }

                _cloudHistoryWrite = 0;
            }

            if (needUpsample)
            {
                _cloudUpsampleW = fullW;
                _cloudUpsampleH = fullH;
                _cloudUpsampleTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = fullW,
                    Height = fullH,
                    ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = "Cloud bilateral upsample",
                });
                _cloudUpsampleTexture = _gpu.GetRenderTargetTexture(_cloudUpsampleTarget, 0);
            }
        }

        private void ReleaseRaymarchedCloudsTargets()
        {
            if (_raymarchedCloudsTarget.IsValid)
                _gpu.ReleaseRenderTarget(_raymarchedCloudsTarget);
            _raymarchedCloudsTarget = GpuRenderTargetHandle.Invalid;
            _raymarchedCloudsTexture = GpuTextureHandle.Invalid;

            for (int i = 0; i < _cloudHistoryTargets.Length; i++)
            {
                if (_cloudHistoryTargets[i].IsValid)
                    _gpu.ReleaseRenderTarget(_cloudHistoryTargets[i]);
                _cloudHistoryTargets[i] = GpuRenderTargetHandle.Invalid;
                _cloudHistoryTextures[i] = GpuTextureHandle.Invalid;
            }

            if (_cloudUpsampleTarget.IsValid)
                _gpu.ReleaseRenderTarget(_cloudUpsampleTarget);
            _cloudUpsampleTarget = GpuRenderTargetHandle.Invalid;
            _cloudUpsampleTexture = GpuTextureHandle.Invalid;
            _cloudUpsampleW = 0;
            _cloudUpsampleH = 0;
            _raymarchedCloudsW = 0;
            _raymarchedCloudsH = 0;
            _cloudCompositeTexture = GpuTextureHandle.Invalid;
        }

        private void EnsureContactTargets(int fullW, int fullH)
        {
            int w = Math.Max(1, fullW / 2);
            int h = Math.Max(1, fullH / 2);
            if (_contactTarget.IsValid && w == _contactW && h == _contactH)
                return;

            if (_contactTarget.IsValid) _gpu.ReleaseRenderTarget(_contactTarget);
            _contactTarget = GpuRenderTargetHandle.Invalid;
            _contactTexture = GpuTextureHandle.Invalid;

            _contactW = w;
            _contactH = h;

            _contactTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = w,
                Height = h,
                // Same reasoning as the GTAO target: R16Float is not uniformly sampleable across
                // backends, so RGBA16F is the safe half-res format.
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = "Contact shadows half-res",
            });
            _contactTexture = _gpu.GetRenderTargetTexture(_contactTarget, 0);
        }

        private void EnsureAoTargets(int fullW, int fullH)
        {
            int w = Math.Max(1, fullW / 2);
            int h = Math.Max(1, fullH / 2);
            if (_aoTarget.IsValid && _aoBlurTarget.IsValid && w == _aoW && h == _aoH)
                return;

            if (_aoTarget.IsValid) _gpu.ReleaseRenderTarget(_aoTarget);
            if (_aoBlurTarget.IsValid) _gpu.ReleaseRenderTarget(_aoBlurTarget);
            _aoTarget = GpuRenderTargetHandle.Invalid;
            _aoBlurTarget = GpuRenderTargetHandle.Invalid;
            _aoTexture = GpuTextureHandle.Invalid;
            _aoBlurTexture = GpuTextureHandle.Invalid;

            _aoW = w;
            _aoH = h;

            var desc = new GpuRenderTargetDesc
            {
                Width = w,
                Height = h,
                // R16Float is not uniformly sampleable across backends; RGBA16F is the safe half-res path.
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = "GTAO half-res",
            };
            _aoTarget = _gpu.CreateRenderTarget(desc);
            desc.DebugName = "GTAO blur half-res";
            _aoBlurTarget = _gpu.CreateRenderTarget(desc);
            _aoTexture = _gpu.GetRenderTargetTexture(_aoTarget, 0);
            _aoBlurTexture = _gpu.GetRenderTargetTexture(_aoBlurTarget, 0);
        }

        private void EnsureSceneTargets(int w, int h)
        {
            if (_sceneTarget.IsValid && w == _sceneW && h == _sceneH)
                return;

            if (_sceneTarget.IsValid) _gpu.ReleaseRenderTarget(_sceneTarget);
            _sceneTarget = GpuRenderTargetHandle.Invalid;
            _sceneTexture = GpuTextureHandle.Invalid;
            _fogSkipTexture = GpuTextureHandle.Invalid;
            _sceneDepthTexture = GpuTextureHandle.Invalid;

            _sceneW = w;
            _sceneH = h;

            _sceneTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = w,
                Height = h,
                // HDR scene buffer (was 8-bit UNORM): lets bright highlights (sun disc, emissive
                // fire/magic, sky) exceed 1.0 instead of clipping, so the always-on tonemap pass
                // (FogPostShaders.PS) has real highlight data to roll off instead of crushed white.
                // Attachment 1: r = fog flag (1 self-fogged, 0.5 engine-lit, 0 unknown/authored),
                // gba = the pixel's pre-fog ambient term, so the composite's AO darkens only
                // indirect light. Transparent draws write r alone (see the secondary blend state).
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float, GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.D24UNormS8UInt,
                DepthSampleable = true,
                DebugName = "Forward HDR scene and fog/ambient MRT",
            });
            _sceneTexture = _gpu.GetRenderTargetTexture(_sceneTarget, 0);
            _fogSkipTexture = _gpu.GetRenderTargetTexture(_sceneTarget, 1);
            _sceneDepthTexture = _gpu.GetRenderTargetDepthTexture(_sceneTarget);

        }

        // ── Built-in meshes ───────────────────────────────────────────────────────

        private void RegisterBuiltinMeshes()
        {
            var (fv, fi) = MeshGeometry.BuildFloor(RenderColor.White, 500f, uvTile: 500f);
            FloorMesh = RegisterMesh(fv, fi);

            var (sv, si) = BuildSunQuad();
            SunMesh = RegisterMesh(sv, si);

            var (cv, ci) = MeshGeometry.BuildCube(RenderColor.White, 1f);
            CubeMesh = RegisterMesh(cv, ci);

            // Unit diameter: DrawSphere3D scales this by radius * 2.
            var (spv, spi) = MeshGeometry.BuildSphere(RenderColor.White, 0.5f);
            SphereMesh = RegisterMesh(spv, spi);
        }

        private static (MeshVertex[], ushort[]) BuildSunQuad()
        {
            float h = 0.5f; // EngineTest Primitives.Quad(1f)
            var v4 = new Vector4(1, 0.95f, 0.7f, 1);
            var verts = new MeshVertex[]
            {
                // EngineTest Primitives.Quad — LH clockwise-front faces toward -Z.
                new(){ Position = new(-h,-h,0), Normal = -Vector3.UnitZ, Color = v4, UV = new(0,1) },
                new(){ Position = new(-h, h,0), Normal = -Vector3.UnitZ, Color = v4, UV = new(0,0) },
                new(){ Position = new( h, h,0), Normal = -Vector3.UnitZ, Color = v4, UV = new(1,0) },
                new(){ Position = new( h,-h,0), Normal = -Vector3.UnitZ, Color = v4, UV = new(1,1) },
            };
            return (verts, new ushort[] { 0, 1, 2, 0, 2, 3 });
        }

        // ── Mesh registration ─────────────────────────────────────────────────────

        public MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
        {
            int stride = Marshal.SizeOf<MeshVertex>();
            ComputeBounds(vertices, out Vector3 boundsCenter, out float boundsRadius);
            var created = new MeshEntry
            {
                // Immutable, not Dynamic: D3D12's dynamic path is a per-frame upload ring, so a
                // write-once vertex buffer stored there is overwritten a few frames later and the
                // world collapses into stretched triangles. Skinned meshes already used Immutable.
                VB = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = stride * vertices.Length,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.VertexBuffer,
                    DebugName = "Forward mesh vertices",
                }, MemoryMarshal.AsBytes(vertices)),
                IB = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = sizeof(ushort) * indices.Length,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.IndexBuffer,
                    DebugName = "Forward mesh indices",
                }, MemoryMarshal.AsBytes(indices)),
                IndexCount = indices.Length,
                BoundsCenter = boundsCenter,
                BoundsRadius = boundsRadius,
                VertexStride = stride,
                VertexCount = vertices.Length,
                Revision = ++_meshRevision,
            };

            if (_freeMeshIds.Count > 0)
            {
                int recycledId = _freeMeshIds.Pop();
                _meshes[recycledId - 1] = created;
                return new MeshHandle(recycledId);
            }

            _meshes.Add(created);
            return new MeshHandle(_meshes.Count);
        }

        public MeshHandle RegisterSkinnedMesh(ReadOnlySpan<SkinnedMeshVertex> vertices, ReadOnlySpan<ushort> indices)
        {
            int stride = Marshal.SizeOf<SkinnedMeshVertex>();
            ComputeBounds(vertices, out Vector3 boundsCenter, out float boundsRadius);
            var created = new MeshEntry
            {
                VB = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = stride * vertices.Length,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.VertexBuffer,
                    DebugName = "Forward skinned vertices",
                }, MemoryMarshal.AsBytes(vertices)),
                IB = _gpu.CreateBuffer(new GpuBufferDesc
                {
                    SizeBytes = sizeof(ushort) * indices.Length,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.IndexBuffer,
                    DebugName = "Forward skinned indices",
                }, MemoryMarshal.AsBytes(indices)),
                IndexCount = indices.Length,
                BoundsCenter = boundsCenter,
                BoundsRadius = boundsRadius,
                VertexStride = stride,
                VertexCount = vertices.Length,
                Revision = ++_meshRevision,
                IsSkinned = true,
            };

            if (_freeMeshIds.Count > 0)
            {
                int recycledId = _freeMeshIds.Pop();
                _meshes[recycledId - 1] = created;
                return new MeshHandle(recycledId);
            }

            _meshes.Add(created);
            return new MeshHandle(_meshes.Count);
        }

        public SkinPaletteHandle CreateSkinPalette(int matrixCount)
        {
            if (matrixCount <= 0)
                return SkinPaletteHandle.Invalid;

            var entry = new SkinPaletteEntry
            {
                Buffer = CreateSkinMatrixBuffer(matrixCount),
                Matrices = new Matrix4x4[matrixCount],
                MatrixCount = matrixCount,
                IsReleased = false,
            };

            if (_freeSkinPaletteIds.Count > 0)
            {
                int recycledId = _freeSkinPaletteIds.Pop();
                _skinPalettes[recycledId - 1] = entry;
                return new SkinPaletteHandle(recycledId);
            }

            _skinPalettes.Add(entry);
            return new SkinPaletteHandle(_skinPalettes.Count);
        }

        public void ReleaseSkinPalette(SkinPaletteHandle handle)
        {
            if (!handle.IsValid)
                return;
            int idx = handle.Id - 1;
            if (idx < 0 || idx >= _skinPalettes.Count)
                return;
            ref SkinPaletteEntry entry = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_skinPalettes)[idx];
            if (entry.IsReleased)
                return;
            _gpu.ReleaseBuffer(entry.Buffer);
            entry = new SkinPaletteEntry { IsReleased = true };
            _freeSkinPaletteIds.Push(handle.Id);
        }

        public void UpdateSkinPalette(SkinPaletteHandle handle, ReadOnlySpan<Matrix4x4> matrices)
        {
            if (!TryGetSkinPalette(handle.Id, out SkinPaletteEntry entry) || matrices.Length == 0)
                return;
            int count = Math.Min(matrices.Length, entry.MatrixCount);
            matrices[..count].CopyTo(entry.Matrices);
        }

        public void ReleaseMesh(MeshHandle handle)
        {
            if (!handle.IsValid)
                return;

            if (handle.Id == FloorMesh.Id || handle.Id == SunMesh.Id
                || handle.Id == CubeMesh.Id || handle.Id == SphereMesh.Id)
                return;

            int idx = handle.Id - 1;
            if (idx < 0 || idx >= _meshes.Count)
                return;

            ref MeshEntry entry = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_meshes)[idx];
            if (entry.IsReleased)
                return;

            _gpu.ReleaseBuffer(entry.VB);
            _gpu.ReleaseBuffer(entry.IB);
            entry = new MeshEntry { IsReleased = true };
            _freeMeshIds.Push(handle.Id);
        }

        public void UpdateMesh(MeshHandle handle, ReadOnlySpan<MeshVertex> vertices)
        {
            if (!handle.IsValid) return;
            int idx = handle.Id - 1;
            if (idx < 0 || idx >= _meshes.Count) return;
            ref MeshEntry entry = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_meshes)[idx];
            if (entry.IsReleased || !entry.VB.IsValid) return;
            if (entry.IsSkinned) return;

            if (vertices.Length != entry.VertexCount)
                throw new ArgumentException("Mesh updates must preserve the registered vertex count.", nameof(vertices));
            // Registered meshes own persistent buffers. Discard-mapping only accepts dynamic
            // buffers on DX11/DX12 and silently left CPU animation frozen here. UpdateBuffer
            // preserves the persistent allocation across frames on every retained backend.
            _gpu.UpdateBuffer(entry.VB, MemoryMarshal.AsBytes(vertices));
            ComputeBounds(vertices, out entry.BoundsCenter, out entry.BoundsRadius);
            entry.Revision = ++_meshRevision;
        }

        private static void ComputeBounds(ReadOnlySpan<MeshVertex> vertices, out Vector3 center, out float radius)
        {
            if (vertices.Length == 0)
            {
                center = Vector3.Zero;
                radius = 0f;
                return;
            }

            Vector3 min = vertices[0].Position;
            Vector3 max = vertices[0].Position;
            for (int i = 1; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i].Position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            center = (min + max) * 0.5f;
            radius = 0f;
            for (int i = 0; i < vertices.Length; i++)
                radius = MathF.Max(radius, Vector3.Distance(vertices[i].Position, center));
        }

        private static void ComputeBounds(ReadOnlySpan<SkinnedMeshVertex> vertices, out Vector3 center, out float radius)
        {
            if (vertices.Length == 0)
            {
                center = Vector3.Zero;
                radius = 0f;
                return;
            }

            Vector3 min = vertices[0].Position;
            Vector3 max = vertices[0].Position;
            for (int i = 1; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i].Position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            center = (min + max) * 0.5f;
            radius = 0f;
            for (int i = 0; i < vertices.Length; i++)
                radius = MathF.Max(radius, Vector3.Distance(vertices[i].Position, center));
        }

        // ── Per-frame state ───────────────────────────────────────────────────────

        public void SetState(Mesh3DState state)
        {
            // Apply installation-wide master switches at the final renderer boundary. Editors and
            // runtime scenes are still free to disable a feature locally, but no caller can
            // accidentally bypass Preferences by rebuilding Mesh3DState after reading defaults.
            MeshLightingDefaults.Apply(ref state);
            if (_state.CloudBaseHeight != state.CloudBaseHeight || _state.CloudThickness != state.CloudThickness
                || _state.CloudCoverageScale != state.CloudCoverageScale || _state.CloudDensityScale != state.CloudDensityScale
                || _state.AuthoredSkyEnabled != state.AuthoredSkyEnabled
                || MathF.Abs(_state.SkyTimeOfDayHours - state.SkyTimeOfDayHours) > .1f)
                _cloudHistoryValid = false;
            _state = state;
            RefreshCascadeCache();
        }
        public void AddDeltaTime(float dt)       => _time += dt;

        private Vector3 _viewForward;

        public void SetCamera(Matrix4x4 view, Matrix4x4 proj)
        {
            _view = view;
            _proj = proj;

            // Row-vector perspective (Conventions.CreatePerspective / D3dMatrixHelper, LH): M33 = zf,
            // M34 = 1, M43 = -near*zf. System.Numerics RH projections used by editors have M34 = -1
            // and M43/M33 = near with the opposite sign, so the magnitude covers both. The old test
            // read M23 (always 0 here), leaving the near plane and FOV at their defaults.
            if (MathF.Abs(proj.M34) > 0.5f && MathF.Abs(proj.M33) > 1e-6f)
                _nearPlane = MathF.Max(0.01f, MathF.Abs(proj.M43 / proj.M33));

            Matrix4x4.Invert(view, out var inv);
            _cameraPos = new Vector3(inv.M41, inv.M42, inv.M43);
            _frustum   = new Frustum(_view * _proj);
            // The true viewing direction, for either handedness or projection: clip z (depth) grows
            // along it. Cascade placement used to trust Mesh3DState.CameraForward, which callers set
            // by hand; a caller that left it stale put every sun cascade behind the camera.
            Matrix4x4 viewProjection = _view * _proj;
            Vector3 depthAxis = new(viewProjection.M13, viewProjection.M23, viewProjection.M33);
            _viewForward = depthAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(depthAxis) : Vector3.Zero;
            // With planar water reflections, objects outside the main view can still appear in the
            // mirror. Culling used to be switched off for the entire scene whenever reflections were
            // enabled; instead also accept anything inside last frame's reflected frustum.
            _hasReflectionFrustum = false;
            if (_lastReflectionActive
                && PlanarReflectionMath.TryCreate(_view, _proj, _cameraPos, _lastReflectionPlaneHeight,
                    out Matrix4x4 reflectedView, out Matrix4x4 reflectedProjection, out _))
            {
                _reflectionFrustum = new Frustum(reflectedView * reflectedProjection);
                _hasReflectionFrustum = true;
            }
            RefreshCascadeCache();
        }

        private void RefreshCascadeCache()
        {
            float farPlane = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 200f;
            float fovY = EstimateVerticalFov(_proj);
            float aspect = EstimateAspect(_proj);
            _cascade = CascadeShadowMath.Compute(
                _nearPlane,
                farPlane,
                _state.ShadowOrthoSize > 0f ? _state.ShadowOrthoSize : 80f,
                fovY,
                aspect,
                ShadowMapSize,
                ShadowMapSizeNear,
                _state.ShadowBias > 0f ? _state.ShadowBias : 0.0015f,
                _state.ShadowCascadeCount >= 3 ? 3 : 2);
        }

        private static float EstimateVerticalFov(in Matrix4x4 proj)
        {
            // Perspective: M22 ≈ 1 / tan(fovY/2), identified by the w = ±z column (M34). Ortho falls back to 60°.
            if (MathF.Abs(proj.M34) > 0.5f && MathF.Abs(proj.M22) > 1e-5f)
                return Math.Clamp(2f * MathF.Atan(1f / MathF.Abs(proj.M22)), 0.2f, 2.8f);
            return MathF.PI / 3f;
        }

        private static float EstimateAspect(in Matrix4x4 proj)
        {
            if (MathF.Abs(proj.M11) > 1e-5f && MathF.Abs(proj.M22) > 1e-5f)
                return Math.Clamp(MathF.Abs(proj.M22 / proj.M11), 0.25f, 4f);
            return 16f / 9f;
        }

        // ── Dynamic lights ────────────────────────────────────────────────────────

        /// <summary>Add a point light for this frame (soft-capped by Scene Local Light Cap; excess dropped).</summary>
        public void AddPointLight(
            Vector3 position,
            Vector3 color,
            float radius,
            float intensity = 1f,
            float falloff = 2f)
        {
            if (_pointLightCount >= PointLightCapacity) return;
            _pointLights[_pointLightCount++] = new ClusterPointLightGpu
            {
                PosRadius      = new Vector4(position, radius),
                ColorIntensity = new Vector4(ToLinearColor(color), intensity),
                FalloffPad     = new Vector4(Math.Clamp(falloff, 0.05f, 16f), 0f, 0f, 0f),
            };
        }

        public void ClearPointLights() => _pointLightCount = 0;

        // ── Fog volumes (Issue 6 Stage 1) ────────────────────────────────────────

        /// <summary>Add a placeable analytic fog volume for this frame (max 8; excess silently ignored).</summary>
        public void AddFogVolume(FogVolume volume)
        {
            if (_fogVolumeCount >= MaxFogVolumes) return;
            _fogVolumes[_fogVolumeCount++] = new FogVolumeData
            {
                CenterDensity  = new Vector4(volume.Center, volume.Density),
                ExtentsFalloff = new Vector4(volume.Extents, volume.FalloffCurve),
                ColorShape     = new Vector4(ToLinearColor(volume.Color), (float)volume.Shape),
                KindDirection  = new Vector4(
                    (float)volume.Kind,
                    volume.Direction.X,
                    volume.Direction.Y,
                    volume.Direction.Z),
            };
        }

        public void ClearFogVolumes() => _fogVolumeCount = 0;

        // ── Smoke volumes (AF1.6) ────────────────────────────────────────────────

        /// <summary>Add one analytic smoke sphere for this frame (max 8; excess silently ignored).</summary>
        public void AddSmokeVolume(Vector3 center, float radius, float density)
        {
            if (_smokeVolumeCount >= SmokeExtinctionMath.MaxVolumes) return;
            if (radius <= 0f || density <= 0f) return;
            _smokeVolumes[_smokeVolumeCount++] =
                new SmokeExtinctionMath.SmokeVolume(center, radius, density);
        }

        /// <summary>Replace this frame's smoke volumes outright (the binning caller's fast path).</summary>
        public void SetSmokeVolumes(ReadOnlySpan<SmokeExtinctionMath.SmokeVolume> volumes)
        {
            _smokeVolumeCount = 0;
            int count = Math.Min(volumes.Length, SmokeExtinctionMath.MaxVolumes);
            for (int i = 0; i < count; i++)
            {
                if (volumes[i].Radius <= 0f || volumes[i].Density <= 0f) continue;
                _smokeVolumes[_smokeVolumeCount++] = volumes[i];
            }
        }

        public void ClearSmokeVolumes() => _smokeVolumeCount = 0;

        /// <summary>
        /// AF2.3: upload weather-map RGBA8 for this frame. Marks the map valid until the next
        /// <see cref="BeginSubmitFrame"/> (same submit→flush contract as smoke volumes).
        /// </summary>
        public void SetWeatherMapRgba8(
            ReadOnlySpan<byte> rgba, int width, int height, float worldHalfExtent, Vector2 worldCenter = default)
        {
            if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            {
                _weatherMapValidThisFrame = false;
                return;
            }

            EnsureWeatherMapTexture(width, height);
            if (!_weatherMapTexture.IsValid)
            {
                _weatherMapValidThisFrame = false;
                return;
            }

            float halfExtent = MathF.Max(worldHalfExtent, 1f);
            if (_weatherMapHalfExtent != halfExtent || _weatherMapWorldCenter != worldCenter)
                _cloudHistoryValid = false;
            // A paused scene still submits its map each frame. Keep its GPU texture and
            // update only changed bytes; origin/bounds remain independent of this cache.
            ReadOnlySpan<byte> pixels = rgba[..(width * height * 4)];
            if (!pixels.SequenceEqual(_weatherMapUploadedPixels))
            {
                _gpu.UpdateTexture(_weatherMapTexture, 0, 0, width, height, pixels);
                if (_weatherMapUploadedPixels.Length != pixels.Length) _weatherMapUploadedPixels = new byte[pixels.Length];
                pixels.CopyTo(_weatherMapUploadedPixels);
            }
            _weatherMapHalfExtent = halfExtent;
            _weatherMapWorldCenter = worldCenter;
            _weatherMapValidThisFrame = true;
        }

        private void EnsureWeatherMapTexture(int width, int height)
        {
            if (_weatherMapTexture.IsValid && width == _weatherMapW && height == _weatherMapH)
                return;

            if (_weatherMapTexture.IsValid)
                _gpu.ReleaseTexture(_weatherMapTexture);
            _weatherMapTexture = GpuTextureHandle.Invalid;
            _weatherMapW = width;
            _weatherMapH = height;
            _weatherMapUploadedPixels = Array.Empty<byte>();
            _cloudHistoryValid = false;

            // Dynamic: rewritten every frame from WeatherMapService when raymarched clouds are on.
            _weatherMapTexture = _gpu.CreateTexture(new GpuTextureDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = GpuFormat.R8G8B8A8UNorm,
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ShaderResource,
                DebugName = "Weather map (AF2.2/AF2.3)",
            }, ReadOnlySpan<byte>.Empty);
        }

        // ── Chunk bounds ──────────────────────────────────────────────────────────

        public void SetChunkBounds(int chunkId, Vector3 min, Vector3 max)
        {
            if (chunkId <= 0) return;
            _chunkBounds[chunkId] = (min, max);
            Genesis.Shared.Rendering.OccluderBoundsRegistry.Set(chunkId, min, max);
        }

        public void ClearChunkBounds()
        {
            _chunkBounds.Clear();
            Genesis.Shared.Rendering.OccluderBoundsRegistry.Clear();
        }

        // ── Frame boundary ─────────────────────────────────────────────────────────

        /// <summary>Clear pending submits from the previous frame (or after a skipped flush).</summary>
        public void BeginSubmitFrame()
        {
            _submitFrameId++;
            ClearAccumulators();
            _pointLightCount = 0;
            _fogVolumeCount = 0;
            _smokeVolumeCount = 0;
            // Weather map must be re-uploaded each frame when raymarched clouds are enabled.
            _weatherMapValidThisFrame = false;
        }

        // ── Submit ────────────────────────────────────────────────────────────────

        public void Submit(MeshHandle mesh, Matrix4x4 world, Vector4 color, int textureId, GpuTextureHandle texture,
            MeshDrawFlags flags, float emissive, MeshHandle shadowMesh = default,
            GpuTextureHandle normal = default, int chunkId = 0, float atlasLayer = 0f,
            Vector4 waterDeep = default, Vector4 waterParams = default,
            Vector4 skyHorizon = default, Vector4 skyZenith = default,
            int normalId = 0, int ormId = 0, int heightId = 0, int emissionId = 0, int extrasId = 0, int flowId = 0,
            GpuTextureHandle orm = default, GpuTextureHandle height = default,
            GpuTextureHandle emission = default, GpuTextureHandle extras = default,
            GpuTextureHandle flow = default,
            MaterialHeightMode heightMode = MaterialHeightMode.None,
            Vector4 surfaceParams = default, Vector4 detailParams = default, Vector4 subsurfaceColorSteps = default,
            SkinPaletteHandle skinPalette = default, RuntimeShaderHandle shader = default,
            Vector4 shaderParams0 = default, Vector4 shaderParams1 = default,
            Vector4 shaderParams2 = default, Vector4 shaderParams3 = default,
            AuthoredGpuTextures authoredTextures = default)
        {
            if (!mesh.IsValid) return;
            if (!TryGetMesh(mesh.Id, out MeshEntry meshEntrySource))
                return;

            LastItemsSubmitted++;

            if (RuntimePassMode(shader) != Genesis.Shared.Assets.ShaderMeshPassMode.Surface)
                flags |= MeshDrawFlags.NoShadow;

            bool isWater     = (flags & MeshDrawFlags.Water)       != 0;
            bool isFloor     = (flags & MeshDrawFlags.IsFloor)      != 0;
            bool noDepthWr   = (flags & MeshDrawFlags.NoDepthWrite) != 0;
            bool transparent = (flags & MeshDrawFlags.Transparent)  != 0;
            bool noShadow    = (flags & MeshDrawFlags.NoShadow)     != 0;
            bool additive    = (flags & MeshDrawFlags.Additive)     != 0;
            bool multiply    = (flags & MeshDrawFlags.Multiply)     != 0;
            bool noFog       = (flags & MeshDrawFlags.NoFog)        != 0;
            bool noReceiveShadow = (flags & MeshDrawFlags.NoReceiveShadow) != 0;
            bool terrainGround = (flags & MeshDrawFlags.TerrainGround) != 0;
            MeshDrawFlags rasterOverride = RasterOverrideForWorld(flags, world);
            bool noDepthTest = (flags & MeshDrawFlags.NoDepthTest)  != 0;
            bool foliage     = (flags & MeshDrawFlags.Foliage)      != 0;
            bool gpuSkinned  = meshEntrySource.IsSkinned && skinPalette.IsValid && TryGetSkinPalette(skinPalette.Id, out _);
            if (gpuSkinned) _submittedSkinPalettes.Add(skinPalette.Id);

            if (isWater)
            {
                _waterMeshes.Add(new WaterMesh
                {
                    MeshId = mesh.Id,
                    World = world,
                    Shallow = color,
                    Deep = waterDeep,
                    WaterParams = waterParams,
                    SkyHorizon = skyHorizon,
                    SkyZenith = skyZenith,
                    Albedo = texture,
                });
                LastInstancesDrawn++;
                return;
            }

            // Chunk-level frustum rejection: if the chunk is registered and fully outside frustum,
            // skip the entire submit (no per-instance test needed).
            if (chunkId > 0 && _state.FrustumCullingEnabled
                && _chunkBounds.TryGetValue(chunkId, out var chunkBox))
            {
                if (!_frustum.ContainsBox(chunkBox.Min, chunkBox.Max)
                    && !(_hasReflectionFrustum && _reflectionFrustum.ContainsBox(chunkBox.Min, chunkBox.Max)))
                {
                    LastInstancesCulled++;
                    return;
                }
            }

            bool visible = true;
            if (_state.FrustumCullingEnabled && !isFloor && !noDepthWr)
            {
                Vector3 worldCenter = Vector3.Transform(meshEntrySource.BoundsCenter, world);
                float worldRadius = meshEntrySource.BoundsRadius * MatrixScaleHelper.MaxScale(world);
                if (!_frustum.ContainsSphere(worldCenter, worldRadius)
                    && !(_hasReflectionFrustum && _reflectionFrustum.ContainsSphere(worldCenter, worldRadius)))
                {
                    LastInstancesCulled++;
                    visible = false;
                }
            }

            if (!visible && noShadow)
                return;

            var atlasData = new Vector4(atlasLayer, 0f, 0f, 0f);

            if (gpuSkinned)
            {
                if (!isFloor && !noDepthWr && !transparent && !additive)
                {
                    var sKey = new SkinnedBatchKey
                    {
                        MeshId = mesh.Id,
                        TextureId = textureId != 0 ? textureId : texture.Id,
                        SkinPaletteId = skinPalette.Id,
                        NormalId = normalId != 0 ? normalId : normal.Id,
                        OrmId = ormId != 0 ? ormId : orm.Id,
                        EmissionId = emissionId != 0 ? emissionId : emission.Id,
                        NoFog = noFog,
                        NoReceiveShadow = noReceiveShadow,
                        Emissive = emissive,
                        RasterOverride = rasterOverride,
                        ShaderId = shader.Id,
                        ShaderParams0 = shaderParams0, ShaderParams1 = shaderParams1,
                        ShaderParams2 = shaderParams2, ShaderParams3 = shaderParams3, AuthoredTextures = authoredTextures,
                    };
                    if (!_skinnedBatches.TryGetValue(sKey, out var sBatch))
                    {
                        sBatch = RentSkinnedBatch();
                        sBatch.MeshId = mesh.Id;
                        sBatch.TextureId = textureId;
                        sBatch.SkinPaletteId = skinPalette.Id;
                        sBatch.Texture = texture;
                        sBatch.Normal = normal;
                        sBatch.Orm = orm;
                        sBatch.Emission = emission;
                        sBatch.Emissive = emissive;
                        sBatch.NoFog = noFog;
                        sBatch.NoReceiveShadow = noReceiveShadow;
                        sBatch.RasterOverride = rasterOverride;
                        sBatch.Shader = shader;
                        sBatch.ShaderParams0 = shaderParams0;
                        sBatch.ShaderParams1 = shaderParams1;
                        sBatch.ShaderParams2 = shaderParams2;
                        sBatch.ShaderParams3 = shaderParams3;
                        sBatch.AuthoredTextures = authoredTextures;
                        _skinnedBatches[sKey] = sBatch;
                        _skinnedBatchList.Add(sBatch);
                    }
                    if (visible)
                    {
                        sBatch.Instances.Add(new InstanceGpu { World = world, Color = color, AtlasData = atlasData });
                        LastInstancesDrawn++;
                    }
                    // Skinned meshes used to return here without ever reaching the shadow lists, so
                    // every animated character was shadowless.
                    if (!noShadow && _shadowSkinnedProgram.IsValid)
                    {
                        // Cascade membership is decided in ClassifyShadowCasters once the light
                        // matrices for this frame are known.
                        _skinnedShadowCasters.Add(new SkinnedShadowCaster
                        {
                            MeshId = mesh.Id,
                            PaletteId = skinPalette.Id,
                            World = world,
                            RasterOverride = rasterOverride,
                            Center = Vector3.Transform(meshEntrySource.BoundsCenter, world),
                            Radius = meshEntrySource.BoundsRadius * MatrixScaleHelper.MaxScale(world),
                        });
                    }
                    return;
                }

                _worldMeshes.Add(new WorldMesh
                {
                    MeshId = mesh.Id,
                    Texture = texture,
                    World = world,
                    Color = color,
                    Unlit = noDepthWr,
                    NoDepthWrite = noDepthWr || additive || multiply,
                    Transparent = transparent,
                    Additive = additive,
                    Multiply = multiply,
                    Emissive = emissive,
                    NoFog = noFog,
                    NoReceiveShadow = noReceiveShadow,
                    TerrainGround = terrainGround,
                    RasterOverride = rasterOverride,
                    SkinPaletteId = skinPalette.Id,
                });
                LastInstancesDrawn++;
                return;
            }

            // Particle-style draws (transparent/additive + noShadow + noDepthWrite):
            // batch them for a single DrawIndexedInstanced call per unique (mesh, texture, blendMode).
            if ((transparent || additive || multiply) && noShadow && noDepthWr && !isFloor)
            {
                byte blendMode = multiply ? (byte)2 : additive ? (byte)1 : (byte)0;
                var tkey = new TransBatchKey { MeshId = mesh.Id,
                    TextureId = textureId != 0 ? textureId : texture.Id,
                    NormalId = normalId != 0 ? normalId : normal.Id, BlendMode = blendMode,
                    NoFog = noFog, Emissive = emissive };
                if (!_transBatches.TryGetValue(tkey, out var tb))
                {
                    tb = RentTransBatch();
                    tb.MeshId = mesh.Id;
                    tb.TextureId = textureId;
                    tb.Texture = texture;
                    tb.Normal = normal;
                    tb.BlendMode = blendMode;
                    tb.Emissive = emissive;
                    tb.NoFog = noFog;
                    _transBatches[tkey] = tb;
                    _transBatchList.Add(tb);
                }
                tb.Instances.Add(new InstanceGpu { World = world, Color = color, AtlasData = atlasData });
                LastInstancesDrawn++;
                return;
            }

            // View-model overlays (held item): drawn after post-process fog so they stay opaque.
            if (noDepthTest)
            {
                _viewModelMeshes.Add(new WorldMesh
                {
                    MeshId = mesh.Id,
                    Texture = texture,
                    World = world,
                    Color = color,
                    Unlit = true,
                    NoFog = true,
                    NoReceiveShadow = true,
                    Emissive = emissive > 0f ? emissive : 0.2f,
                    RasterOverride = rasterOverride,
                });
                LastInstancesDrawn++;
                return;
            }

            // Opaque IsFloor (PGSL DrawFloor3D, obstacle-course blocks, visual harness): instance by
            // mesh + textures + flags. Parking every floor mesh on _worldMeshes used one DrawIndexed
            // each (R7.7). Transparent / NoDepthWrite / Additive floors still park below.
            if (isFloor && !transparent && !additive && !noDepthWr)
            {
                var floorKey = new BatchKey
                {
                    MeshId = mesh.Id,
                    TextureId = textureId != 0 ? textureId : texture.Id,
                    NormalId = normalId != 0 ? normalId : normal.Id,
                    OrmId = ormId != 0 ? ormId : orm.Id,
                    HeightId = heightId != 0 ? heightId : height.Id,
                    EmissionId = emissionId != 0 ? emissionId : emission.Id,
                    ExtrasId = extrasId != 0 ? extrasId : extras.Id,
                    FlowId = flowId != 0 ? flowId : flow.Id,
                    ShaderId = shader.Id,
                    ShaderParams0 = shaderParams0, ShaderParams1 = shaderParams1,
                    ShaderParams2 = shaderParams2, ShaderParams3 = shaderParams3,
                    AuthoredTextures = authoredTextures,
                    NoFog = noFog,
                    NoReceiveShadow = noReceiveShadow,
                    TerrainGround = terrainGround,
                    RasterOverride = rasterOverride,
                    IsFloor = true,
                    HeightMode = heightMode,
                    SurfaceParams = surfaceParams,
                    DetailParams = detailParams,
                    SubsurfaceColorSteps = subsurfaceColorSteps,
                    Emissive = emissive,
                };
                if (!_batches.TryGetValue(floorKey, out var floorBatch))
                {
                    floorBatch = RentBatch();
                    floorBatch.MeshId = mesh.Id;
                    floorBatch.TextureId = textureId;
                    floorBatch.Texture = texture;
                    floorBatch.Normal = normal;
                    floorBatch.Orm = orm;
                    floorBatch.Height = height;
                    floorBatch.Emission = emission;
                    floorBatch.Extras = extras;
                    floorBatch.Flow = flow;
                    floorBatch.HeightMode = heightMode;
                    floorBatch.SurfaceParams = surfaceParams;
                    floorBatch.DetailParams = detailParams;
                    floorBatch.SubsurfaceColorSteps = subsurfaceColorSteps;
                    floorBatch.Emissive = emissive;
                    floorBatch.NoFog = noFog;
                    floorBatch.NoReceiveShadow = noReceiveShadow;
                    floorBatch.TerrainGround = terrainGround;
                    floorBatch.RasterOverride = rasterOverride;
                    floorBatch.IsFloor = true;
                    floorBatch.Shader = shader;
                    floorBatch.ShaderParams0 = shaderParams0;
                    floorBatch.ShaderParams1 = shaderParams1;
                    floorBatch.ShaderParams2 = shaderParams2;
                    floorBatch.ShaderParams3 = shaderParams3;
                    floorBatch.AuthoredTextures = authoredTextures;
                    _batches[floorKey] = floorBatch;
                    _batchList.Add(floorBatch);
                }

                var floorInst = new InstanceGpu { World = world, Color = color, AtlasData = atlasData };
                if (visible)
                {
                    floorBatch.Instances.Add(floorInst);
                    LastInstancesDrawn++;
                }

                if (!noShadow)
                {
                    int shadowMeshId = shadowMesh.IsValid ? shadowMesh.Id : mesh.Id;
                    if (!IsMeshValid(shadowMeshId))
                        shadowMeshId = mesh.Id;
                    var shKey = new ShadowBatchKey { MeshId = shadowMeshId, RasterOverride = rasterOverride };
                    if (!_shadowBatches.TryGetValue(shKey, out var shBatch))
                    {
                        shBatch = RentShadowBatch();
                        shBatch.MeshId = shadowMeshId;
                        shBatch.RasterOverride = rasterOverride;
                        _shadowBatches[shKey] = shBatch;
                        _shadowBatchList.Add(shBatch);
                    }
                    AddShadowCaster(shBatch, floorInst, meshEntrySource.BoundsCenter, meshEntrySource.BoundsRadius);
                }
                return;
            }

            // Leftover special-case draws: unlit world geometry, non-particle transparent objects.
            // IsFloor no longer parks here (see above). Particles stay on TransBatch.
            if (noDepthWr || transparent || additive || multiply)
            {
                _worldMeshes.Add(new WorldMesh
                {
                    MeshId       = mesh.Id,
                    Texture      = texture,
                    World        = world,
                    Color        = color,
                    IsFloor      = isFloor,
                    Unlit        = noDepthWr && !isFloor,
                    NoDepthWrite = noDepthWr || additive || multiply,
                    Transparent  = transparent,
                    Additive     = additive,
                    Multiply     = multiply,
                    Emissive     = emissive,
                    NoFog        = noFog,
                    NoReceiveShadow = noReceiveShadow,
                    TerrainGround = terrainGround,
                    RasterOverride = rasterOverride,
                });
                LastInstancesDrawn++;
                return;
            }

            // Foliage: instanced unlit alpha-cutout (grass/trees).
            if (foliage)
            {
                MeshDrawFlags foliageRasterOverride = FoliageRasterOverride(flags);
                var fKey = new BatchKey { MeshId = mesh.Id,
                    TextureId = textureId != 0 ? textureId : texture.Id, NoFog = noFog,
                    NormalId = normalId != 0 ? normalId : normal.Id, Emissive = emissive,
                    NoReceiveShadow = noReceiveShadow, Foliage = true, RasterOverride = foliageRasterOverride,
                    ShaderId = shader.Id, ShaderParams0 = shaderParams0, ShaderParams1 = shaderParams1,
                    ShaderParams2 = shaderParams2, ShaderParams3 = shaderParams3, AuthoredTextures = authoredTextures };
                if (!_batches.TryGetValue(fKey, out var fBatch))
                {
                    fBatch = RentBatch();
                    fBatch.MeshId = mesh.Id;
                    fBatch.TextureId = textureId;
                    fBatch.Texture = texture;
                    fBatch.Normal = normal;
                    fBatch.Emissive = emissive;
                    fBatch.NoFog = noFog;
                    fBatch.NoReceiveShadow = noReceiveShadow;
                    fBatch.Foliage = true;
                    fBatch.RasterOverride = foliageRasterOverride;
                    fBatch.Shader = shader;
                    fBatch.ShaderParams0 = shaderParams0;
                    fBatch.ShaderParams1 = shaderParams1;
                    fBatch.ShaderParams2 = shaderParams2;
                    fBatch.ShaderParams3 = shaderParams3;
                    fBatch.AuthoredTextures = authoredTextures;
                    _batches[fKey] = fBatch;
                    _batchList.Add(fBatch);
                }
                if (visible)
                {
                    fBatch.Instances.Add(new InstanceGpu { World = world, Color = color, AtlasData = atlasData });
                    LastInstancesDrawn++;
                }
                return;
            }

            var key = new BatchKey { MeshId = mesh.Id,
                TextureId = textureId != 0 ? textureId : texture.Id,
                NormalId = normalId != 0 ? normalId : normal.Id,
                OrmId = ormId != 0 ? ormId : orm.Id,
                HeightId = heightId != 0 ? heightId : height.Id,
                EmissionId = emissionId != 0 ? emissionId : emission.Id,
                ExtrasId = extrasId != 0 ? extrasId : extras.Id,
                FlowId = flowId != 0 ? flowId : flow.Id,
                ShaderId = shader.Id, ShaderParams0 = shaderParams0, ShaderParams1 = shaderParams1,
                ShaderParams2 = shaderParams2, ShaderParams3 = shaderParams3, AuthoredTextures = authoredTextures,
                NoFog = noFog, NoReceiveShadow = noReceiveShadow, TerrainGround = terrainGround,
                RasterOverride = rasterOverride, HeightMode = heightMode, SurfaceParams = surfaceParams,
                DetailParams = detailParams, SubsurfaceColorSteps = subsurfaceColorSteps, Emissive = emissive };
            if (!_batches.TryGetValue(key, out var batch))
            {
                batch = RentBatch();
                batch.MeshId = mesh.Id;
                batch.TextureId = textureId;
                batch.Texture = texture;
                batch.Normal = normal;
                batch.Orm = orm;
                batch.Height = height;
                batch.Emission = emission;
                batch.Extras = extras;
                batch.Flow = flow;
                batch.HeightMode = heightMode;
                batch.SurfaceParams = surfaceParams;
                batch.DetailParams = detailParams;
                batch.SubsurfaceColorSteps = subsurfaceColorSteps;
                batch.Emissive = emissive;
                batch.NoFog = noFog;
                batch.NoReceiveShadow = noReceiveShadow;
                batch.TerrainGround = terrainGround;
                batch.RasterOverride = rasterOverride;
                batch.Shader = shader;
                batch.ShaderParams0 = shaderParams0;
                batch.ShaderParams1 = shaderParams1;
                batch.ShaderParams2 = shaderParams2;
                batch.ShaderParams3 = shaderParams3;
                batch.AuthoredTextures = authoredTextures;
                _batches[key] = batch;
                _batchList.Add(batch);
            }

            var inst = new InstanceGpu { World = world, Color = color, AtlasData = atlasData };
            if (visible)
            {
                batch.Instances.Add(inst);
                LastInstancesDrawn++;
            }

            if (!noShadow)
            {
                int shadowMeshId = shadowMesh.IsValid ? shadowMesh.Id : mesh.Id;
                if (!IsMeshValid(shadowMeshId))
                    shadowMeshId = mesh.Id;
                var shKey = new ShadowBatchKey { MeshId = shadowMeshId, RasterOverride = rasterOverride };
                if (!_shadowBatches.TryGetValue(shKey, out var shBatch))
                {
                    shBatch = RentShadowBatch();
                    shBatch.MeshId = shadowMeshId;
                    shBatch.RasterOverride = rasterOverride;
                    _shadowBatches[shKey] = shBatch;
                    _shadowBatchList.Add(shBatch);
                }
                AddShadowCaster(shBatch, inst, meshEntrySource.BoundsCenter, meshEntrySource.BoundsRadius);
            }
        }

        /// <summary>
        /// Fast path for a pre-culled foliage field. The caller supplies one species/LOD batch;
        /// transforms are copied directly into the renderer instance buffer and become one
        /// DrawIndexedInstanced command on every backend.
        /// </summary>
        public void SubmitInstances(
            MeshHandle mesh,
            ReadOnlySpan<MeshInstanceData> instances,
            int textureId,
            GpuTextureHandle texture,
            MeshDrawFlags flags,
            float emissive,
            GpuTextureHandle normal = default,
            RuntimeShaderHandle shader = default,
            Vector4 shaderParams0 = default,
            Vector4 shaderParams1 = default,
            Vector4 shaderParams2 = default,
            Vector4 shaderParams3 = default,
            AuthoredGpuTextures authoredTextures = default)
        {
            if (!mesh.IsValid || instances.IsEmpty || !TryGetMesh(mesh.Id, out MeshEntry meshEntrySource)) return;
            if (RuntimePassMode(shader) != Genesis.Shared.Assets.ShaderMeshPassMode.Surface)
                flags |= MeshDrawFlags.NoShadow;

            bool transparent = (flags & MeshDrawFlags.Transparent) != 0;
            bool additive = (flags & MeshDrawFlags.Additive) != 0;
            bool multiply = (flags & MeshDrawFlags.Multiply) != 0;
            bool noShadow = (flags & MeshDrawFlags.NoShadow) != 0;
            bool noDepthWr = (flags & MeshDrawFlags.NoDepthWrite) != 0;
            bool isFloor = (flags & MeshDrawFlags.IsFloor) != 0;
            bool noFog = (flags & MeshDrawFlags.NoFog) != 0;

            // Particle TransBatch (same predicate as Submit): append InstanceGpu directly — no
            // per-particle MeshDrawCall / WorldMeshes parking.
            if ((transparent || additive || multiply) && noShadow && noDepthWr && !isFloor)
            {
                byte blendMode = multiply ? (byte)2 : additive ? (byte)1 : (byte)0;
                var tkey = new TransBatchKey
                {
                    MeshId = mesh.Id,
                    TextureId = textureId != 0 ? textureId : texture.Id,
                    NormalId = normal.Id,
                    BlendMode = blendMode,
                    NoFog = noFog,
                    Emissive = emissive,
                };
                if (!_transBatches.TryGetValue(tkey, out TransBatch tb))
                {
                    tb = RentTransBatch();
                    tb.MeshId = mesh.Id;
                    tb.TextureId = textureId;
                    tb.Texture = texture;
                    tb.Normal = normal;
                    tb.BlendMode = blendMode;
                    tb.Emissive = emissive;
                    tb.NoFog = noFog;
                    _transBatches[tkey] = tb;
                    _transBatchList.Add(tb);
                }

                for (int i = 0; i < instances.Length; i++)
                {
                    ref readonly MeshInstanceData instance = ref instances[i];
                    tb.Instances.Add(new InstanceGpu
                    {
                        World = instance.World,
                        Color = new Vector4(instance.Tint.R, instance.Tint.G, instance.Tint.B,
                            instance.Tint.A > 0f ? instance.Tint.A : 1f),
                    });
                }
                LastItemsSubmitted += instances.Length;
                LastInstancesDrawn += instances.Length;
                return;
            }

            bool foliage = (flags & MeshDrawFlags.Foliage) != 0;
            bool water = (flags & MeshDrawFlags.Water) != 0;
            bool noDepthTest = (flags & MeshDrawFlags.NoDepthTest) != 0;
            bool terrainGround = (flags & MeshDrawFlags.TerrainGround) != 0;
            bool noReceiveShadow = (flags & MeshDrawFlags.NoReceiveShadow) != 0;

            // Water / view-models / leftover NoDepthWrite / non-particle transparent keep Submit
            // (WorldMeshes or dedicated passes). R7.9 does not widen that parking.
            bool reflectedInstances = false;
            for (int i = 0; i < instances.Length && !reflectedInstances; i++)
                reflectedInstances = instances[i].World.GetDeterminant() < 0;
            if (water || noDepthTest || noDepthWr || transparent || additive || multiply || reflectedInstances)
            {
                for (int i = 0; i < instances.Length; i++)
                {
                    ref readonly MeshInstanceData instance = ref instances[i];
                    Vector4 color = new(instance.Tint.R, instance.Tint.G, instance.Tint.B,
                        instance.Tint.A > 0f ? instance.Tint.A : 1f);
                    Submit(mesh, instance.World, color, textureId, texture, flags, emissive,
                        normal: normal, shader: shader, shaderParams0: shaderParams0,
                        shaderParams1: shaderParams1, shaderParams2: shaderParams2,
                        shaderParams3: shaderParams3, authoredTextures: authoredTextures);
                }
                return;
            }

            // R7.9 Auto-Instancing Draw Queue: bulk-append into BatchKey (mesh + material fingerprint
            // + shader + flags) → one DrawIndexedInstanced. Same key DrawMesh already uses.
            MeshDrawFlags rasterOverride = foliage
                ? FoliageRasterOverride(flags)
                : RasterOverride(flags);
            var key = new BatchKey
            {
                MeshId = mesh.Id,
                TextureId = textureId != 0 ? textureId : texture.Id,
                NormalId = normal.Id,
                NoFog = noFog,
                NoReceiveShadow = noReceiveShadow,
                TerrainGround = terrainGround,
                RasterOverride = rasterOverride,
                Foliage = foliage,
                IsFloor = isFloor,
                Emissive = emissive,
                ShaderId = shader.Id,
                ShaderParams0 = shaderParams0,
                ShaderParams1 = shaderParams1,
                ShaderParams2 = shaderParams2,
                ShaderParams3 = shaderParams3,
                AuthoredTextures = authoredTextures,
            };
            if (!_batches.TryGetValue(key, out Batch batch))
            {
                batch = RentBatch();
                batch.MeshId = mesh.Id;
                batch.TextureId = textureId;
                batch.Texture = texture;
                batch.Normal = normal;
                batch.Emissive = emissive;
                batch.NoFog = noFog;
                batch.NoReceiveShadow = noReceiveShadow;
                batch.TerrainGround = terrainGround;
                batch.Foliage = foliage;
                batch.IsFloor = isFloor;
                batch.RasterOverride = rasterOverride;
                batch.Shader = shader;
                batch.ShaderParams0 = shaderParams0;
                batch.ShaderParams1 = shaderParams1;
                batch.ShaderParams2 = shaderParams2;
                batch.ShaderParams3 = shaderParams3;
                batch.AuthoredTextures = authoredTextures;
                _batches[key] = batch;
                _batchList.Add(batch);
            }

            for (int i = 0; i < instances.Length; i++)
            {
                ref readonly MeshInstanceData instance = ref instances[i];
                var inst = new InstanceGpu
                {
                    World = instance.World,
                    Color = new Vector4(instance.Tint.R, instance.Tint.G, instance.Tint.B,
                        instance.Tint.A > 0f ? instance.Tint.A : 1f),
                };
                batch.Instances.Add(inst);

                if (!noShadow && !foliage)
                {
                    var shKey = new ShadowBatchKey { MeshId = mesh.Id, RasterOverride = rasterOverride };
                    if (!_shadowBatches.TryGetValue(shKey, out ShadowBatch shBatch))
                    {
                        shBatch = RentShadowBatch();
                        shBatch.MeshId = mesh.Id;
                        shBatch.RasterOverride = rasterOverride;
                        _shadowBatches[shKey] = shBatch;
                        _shadowBatchList.Add(shBatch);
                    }
                    AddShadowCaster(shBatch, inst, meshEntrySource.BoundsCenter, meshEntrySource.BoundsRadius);
                }
            }

            LastItemsSubmitted += instances.Length;
            LastInstancesDrawn += instances.Length;
        }

        // ── GPU timing helpers (Phase 0) ───────────────────────────────────────────

        private void BeginGpuTiming()
        {
            ResolveGpuTiming();
            _activeTimingQuery = GpuQueryHandle.Invalid;
            for (int i = 0; i < _timingQueries.Length; i++)
            {
                if (_timingQueries[i].IsValid) continue;
                _activeTimingQuery = _gpu.BeginTimestampScope();
                _timingQueries[i] = _activeTimingQuery;
                break;
            }
        }

        private void EndGpuTiming()
        {
            if (!_activeTimingQuery.IsValid) return;
            _gpu.EndTimestampScope(_activeTimingQuery);
            _activeTimingQuery = GpuQueryHandle.Invalid;
        }

        // Non-blocking read of the oldest pending slot (the one about to be reused).
        private void ResolveGpuTiming()
        {
            for (int i = 0; i < _timingQueries.Length; i++)
            {
                GpuQueryHandle query = _timingQueries[i];
                if (!query.IsValid || query.Id == _activeTimingQuery.Id) continue;
                if (!_gpu.TryResolveTimestamp(query, out double milliseconds)) continue;
                LastGpuMs = milliseconds;
                _timingQueries[i] = GpuQueryHandle.Invalid;
            }
        }

        // ── Flush ─────────────────────────────────────────────────────────────────
        // Canonical 3D frame: ShadowPass (far/near cascades) → MainPass (sun + ambient +
        // point lights) → post (tonemap/fog) → view-models. Script Draw* only Submit→here.

        /// <summary>Shadow casters drawn last frame (far+near instance counts).</summary>
        public int LastShadowCasterCount { get; private set; }
        /// <summary>Soft budget for shadow batch instances; excess casters are dropped.</summary>
        public int ShadowBatchBudget { get; set; } = MaxInstances;

        public void Flush(GpuRenderTargetHandle target, GpuTextureHandle whiteTexture,
            int viewW = 0, int viewH = 0, GpuTextureHandle depthTexture = default,
            bool allowPostProcess = true)
        {
            if (!depthTexture.IsValid)
            {
                _froxelsActiveThisFrame = false;
                _screenFogActiveThisFrame = false;
                _smokeExtinctionActiveThisFrame = false;
                LastAoMs = 0;
                LastSmokeExtinctionMs = 0;
                LastBloomMs = 0;
                LastAtmosphereLutMs = 0;
                LastRaymarchedCloudsMs = 0;
                LastCelestialExtrasMs = 0;
                ClearAccumulators();
                return;
            }

            BeginGpuTiming();

            // Dynamic uploads are frame-local on DX12. An unchanged/paused pose must
            // survive upload-ring reuse, so re-upload only palettes submitted this frame.
            foreach (int paletteId in _submittedSkinPalettes)
            {
                if (!TryGetSkinPalette(paletteId, out SkinPaletteEntry palette)) continue;
                if (!_gpu.TryMapDiscard(palette.Buffer, out Span<byte> destination)) continue;
                MemoryMarshal.AsBytes(palette.Matrices.AsSpan()).CopyTo(destination);
                _gpu.Unmap(palette.Buffer);
            }
            _submittedSkinPalettes.Clear();

            _rtWidth  = viewW;
            _rtHeight = viewH;

            LastDrawCalls = 0;
            LastTriangles = 0;
            LastShadowCasterCount = 0;
            LastShadowCascadesRendered = 0;

            _shadowsActiveThisFrame = _state.ShadowsEnabled && _state.LightingEnabled && HasShadowCasters();
            if (_shadowsActiveThisFrame)
                ShadowPass();

            if (ShouldRunLocalShadows())
                LocalShadowPass();
            else
                SkipLocalShadows();

            // Froxel volumetric fog: after every shadow map it samples, before any pass that fogs a
            // surface (reflection, main, water and the post composite all read the integrated volume).
            {
                Matrix4x4 froxelFar = ComputeLightViewProj(ShadowCascadeKind.Far);
                Matrix4x4 froxelMid = _cascade.CascadeCount >= 3 ? ComputeLightViewProj(ShadowCascadeKind.Mid) : froxelFar;
                Matrix4x4 froxelNear = ComputeLightViewProj(ShadowCascadeKind.Near);
                FroxelFogPass(froxelFar, froxelNear, froxelMid);
            }

            SortBatchesByDistance(_cameraPos);
            LastBatchCount = _batchList.Count + _skinnedBatchList.Count;

            UploadAllInstances();
            RenderPlanarReflection(whiteTexture, viewW, viewH);

            // The HDR offscreen target + post composite (tonemap, fog, shadow-aware volumetric
            // light shafts) now runs by default whenever we have a valid viewport + depth to
            // reconstruct world position from — this is the primary fog path on a real frame, so
            // it is no longer gated behind a "screen-space fog" toggle (that toggle used to also
            // leave the legacy forward-pass fog active, double-applying fog when off — removed for
            // good here). The forward pass's own ray-marched fog (see ForwardShaders.cs) self-
            // applies in two cases instead of being a pure fallback: (1) there's no depth buffer
            // to reconstruct from at all, or (2) this specific draw is flagged NoDepthWriteFlag —
            // meaning it never wrote scene depth (held items, particles, etc.), so the post-process
            // structurally cannot see it and would otherwise leave it completely unfogged.
            bool canPost = allowPostProcess && viewW > 0 && viewH > 0;
            bool screenFog = _state.FogEnabled && canPost;
            _screenFogActiveThisFrame = screenFog;

            // Volumetric light-shaft ray-marching is automatic, not a manual toggle: it only pays
            // its (real) GPU cost on frames where shadow-casting occluders are actually present to
            // carve light shafts out of fog — exactly the condition under which it can produce a
            // visible result. Scenes/cameras with no shadow casters skip it entirely for free.
            bool runVolumetric = screenFog && _shadowsActiveThisFrame;
            _runVolumetricThisFrame = runVolumetric;
            string fogMode = !_state.FogEnabled
                ? "off (tonemap only)"
                : screenFog
                    ? (runVolumetric ? $"screen+volumetric(q{Math.Clamp(_state.VolumetricFogQuality, 0, 2)})" : "screen-space")
                    : "surface-fallback (no depth buffer)";
            if (!string.Equals(_lastFogModeLogged, fogMode, StringComparison.Ordinal))
            {
                _lastFogModeLogged = fogMode;
                RenderLog.Line($"Fog mode: {fogMode}");
            }
            if (canPost)
            {
                EnsureSceneTargets(viewW, viewH);
                MainPass(_sceneTarget, _sceneDepthTexture, whiteTexture, postProcessTarget: true);
                GpuTextureHandle postDepth = _sceneDepthTexture;
                bool runGtao = ShouldRunGtao(canPost);
                if (runGtao)
                    GtaoPass(postDepth, viewW, viewH);
                else
                    LastAoMs = 0;
                bool runContact = ShouldRunContactShadows(canPost);
                if (runContact)
                    ContactShadowPass(postDepth, viewW, viewH);
                else
                    LastContactShadowMs = 0;
                // Local lights now scatter inside the froxel volume (shadowed, fog-density aware),
                // which retires the separate half-resolution local-light pass.
                bool runLocalVol = false;
                LastLocalVolumetricMs = 0;
                // AF1.6 has no pass of its own — the volumes ride the composite's own cbuffer, so
                // all that happens here is deciding whether to pack them.
                _smokeExtinctionActiveThisFrame = ShouldRunSmokeExtinction(canPost);
                if (!_smokeExtinctionActiveThisFrame)
                    LastSmokeExtinctionMs = 0;
                bool runBloom = ShouldRunBloom(canPost);
                if (runBloom)
                    BloomPass(_sceneTexture, viewW, viewH);
                else
                    LastBloomMs = 0;
                bool runAtmosphereLut = ShouldRunAtmosphereLut(canPost);
                if (!runAtmosphereLut)
                    LastAtmosphereLutMs = 0;
                bool runCelestialExtras = ShouldRunCelestialExtras(canPost);
                if (!runCelestialExtras)
                    LastCelestialExtrasMs = 0;
                // AF2.3/AF2.4: full every frame when enabled — never a 3-in-4 skip. FogVolumes remain.
                bool runRaymarchedClouds = ShouldRunRaymarchedClouds(canPost)
                    && RaymarchedCloudsPass(postDepth, viewW, viewH);
                if (!runRaymarchedClouds)
                {
                    LastRaymarchedCloudsMs = 0;
                    _cloudCompositeTexture = GpuTextureHandle.Invalid;
                    if (!_state.CloudTemporalEnabled)
                        _cloudHistoryValid = false;
                }

                CompositePost(target, _sceneTexture, postDepth, viewW, viewH,
                    runGtao ? _aoTexture : GpuTextureHandle.Invalid, runGtao,
                    runContact ? _contactTexture : GpuTextureHandle.Invalid, runContact,
                    runLocalVol ? _localVolTexture : GpuTextureHandle.Invalid, runLocalVol,
                    runBloom ? BloomResultTexture() : GpuTextureHandle.Invalid, runBloom,
                    runAtmosphereLut,
                    runRaymarchedClouds && _cloudCompositeTexture.IsValid
                        ? _cloudCompositeTexture
                        : GpuTextureHandle.Invalid,
                    runRaymarchedClouds,
                    runCelestialExtras);
                DrawViewModelPass(target, whiteTexture);
            }
            else
            {
                LastAoMs = 0;
                LastContactShadowMs = 0;
                LastLocalVolumetricMs = 0;
                LastSmokeExtinctionMs = 0;
                LastBloomMs = 0;
                LastAtmosphereLutMs = 0;
                LastRaymarchedCloudsMs = 0;
                LastCelestialExtrasMs = 0;
                _smokeExtinctionActiveThisFrame = false;
                // No valid viewport/depth to reconstruct from — fall back to a direct draw.
                MainPass(target, depthTexture, whiteTexture, postProcessTarget: false);
                DrawViewModelPass(target, whiteTexture);
            }

            LastFrameInstancesDrawn  = LastInstancesDrawn;
            LastFrameInstancesCulled = LastInstancesCulled;
            LastFrameBatchCount      = LastBatchCount;
            LastFrameFoliageInstances = _foliageInstances;
            LastFrameFoliageBatches = _foliageBatches;
            LastFrameWorldMeshes = _worldMeshes.Count;
            LastFramePointLights = _pointLightCount;
            LastFrameFoliageUploadBytes = (long)_foliageInstances * 96L;

            EndGpuTiming();

            ClearAccumulators();
        }

        // Uploads opaque instances first, then transparent/particle instances into the same buffer.
        // _transInstOffset records where the transparent section begins so DrawTransparentBatches can
        // reference the correct instance indices without a separate GPU buffer.
        private void UploadAllInstances()
        {
            int opaqueTotal = 0;
            foreach (var b in _batchList) opaqueTotal += b.Instances.Count;
            int skinnedTotal = 0;
            foreach (var b in _skinnedBatchList) skinnedTotal += b.Instances.Count;
            int transTotal = 0;
            foreach (var b in _transBatchList) transTotal += b.Instances.Count;
            int total = opaqueTotal + skinnedTotal + transTotal;

            if (total == 0) { _skinnedInstOffset = 0; _transInstOffset = 0; return; }

            int cap = Math.Min(total, SoftMeshInstanceCap);
            int bytes = cap * Marshal.SizeOf<InstanceGpu>();

            if (!_gpu.TryMapDiscard(_instanceBuf, out Span<byte> mapped, bytes))
                return;

            Span<InstanceGpu> dst = MemoryMarshal.Cast<byte, InstanceGpu>(mapped);
            int written = 0;

            foreach (var b in _batchList)
                foreach (var inst in b.Instances)
                    if (written < cap) dst[written++] = inst;

            _skinnedInstOffset = written;

            foreach (var b in _skinnedBatchList)
                foreach (var inst in b.Instances)
                    if (written < cap) dst[written++] = inst;

            _transInstOffset = written;

            foreach (var b in _transBatchList)
                foreach (var inst in b.Instances)
                    if (written < cap) dst[written++] = inst;

            _gpu.Unmap(_instanceBuf);
        }

        // First-person held item etc. — after tonemap/fog so post-process cannot wash them out.
        private void DrawViewModelPass(GpuRenderTargetHandle target, GpuTextureHandle whiteTexture)
        {
            if (_viewModelMeshes.Count == 0) return;

            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[] { GpuAttachmentAction.Keep() },
                HasDepth = false,
                DebugName = "Forward view models",
            });
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetDepthState(_dssNoTest);
            _gpu.SetRasterState(_rsCullNone);

            BindCommonShaderState(shadowPass: false);
            _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
            // View models draw after the composite, straight into the display target: under the
            // linear pipeline the forward shader must encode their output to sRGB itself.
            _encodeOutputThisPass = LinearPipeline;
            UploadEngineCB();
            _encodeOutputThisPass = false;
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);
            _gpu.SetTexture(GpuShaderStage.Pixel, 3, _flatNormalTexture);

            foreach (WorldMesh wm in _viewModelMeshes)
            {
                if (!TryGetMesh(wm.MeshId, out MeshEntry mesh)) continue;

                SetMeshBuffers(ref mesh);
                _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                    wm.Texture.IsValid ? wm.Texture : whiteTexture);

                UploadDrawCB(wm.World, wm.Color,
                    emissive: wm.Emissive, unlit: 1f, isFloor: 0f, useInstancing: 0, instOffset: 0,
                    noFog: true, noDepthWrite: true);

                _gpu.DrawIndexed(mesh.IndexCount);
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
            }

            _gpu.EndRenderPass();
            _gpu.SetDepthState(_dssDefault);
            _gpu.SetRasterState(CurrentRasterizer());
        }

        // Draws all transparent/additive particle batches with DrawIndexedInstanced.
        // Called at the end of MainPass after all opaque and world-mesh draws.
        private void DrawTransparentBatches(GpuTextureHandle whiteTexture)
        {
            if (_transBatchList.Count == 0) return;

            // Water pass swaps VS/PS and PS constant-buffer slots; re-bind forward state before
            // instanced particles (rain/snow/bursts) so the instance buffer and draw CB are live.
            BindCommonShaderState(shadowPass: false);
            _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);

            _gpu.SetDepthState(_dssNoWrite);
            _gpu.SetRasterState(_rsCullNone);

            int instOffset = _transInstOffset;
            foreach (var b in _transBatchList)
            {
                if (b.Instances.Count == 0) continue;
                if (instOffset >= SoftMeshInstanceCap) break;
                if (_worldDrawBudgetLeft <= 0) break;
                if (!TryGetMesh(b.MeshId, out MeshEntry mesh)) { instOffset += b.Instances.Count; continue; }

                _gpu.SetBlendState(b.BlendMode == 2 ? _bsMultiply : b.BlendMode == 1 ? _bsAdd : _bsAlpha);
                SetMeshBuffers(ref mesh);

                _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                    b.Texture.IsValid ? b.Texture : whiteTexture);

                // Per-batch normal map (most particles use the flat default)
                _gpu.SetTexture(GpuShaderStage.Pixel, 3,
                    b.Normal.IsValid ? b.Normal : _flatNormalTexture);

                int drawCount = Math.Min(b.Instances.Count, SoftMeshInstanceCap - instOffset);
                // DrawTransparentBatches binds _dssNoWrite unconditionally for this whole pass
                // (see above) — every draw here genuinely doesn't write depth, so the forward
                // shader must self-apply fog rather than rely on the post-process depth read-back.
                UploadDrawCB(Matrix4x4.Identity, Vector4.One, b.Emissive,
                    unlit: 1f, isFloor: 0f, useInstancing: 1, instOffset: (uint)instOffset,
                    noFog: b.NoFog, noDepthWrite: true);
                _gpu.DrawIndexedInstanced(mesh.IndexCount, drawCount);

                _worldDrawBudgetLeft--;
                instOffset += b.Instances.Count;
                LastDrawCalls++;
                LastTriangles += (mesh.IndexCount / 3) * drawCount;
            }

            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetDepthState(_dssDefault);
            _gpu.SetRasterState(CurrentRasterizer());
        }

        private bool HasShadowCasters()
        {
            // The environment floor alone cannot shadow anything (nothing lies beneath it), so a
            // floor-only scene no longer pays for every cascade and omni face just to draw it.
            foreach (var b in _shadowBatchList)
                if (b.Casters.Count > 0) return true;
            return _skinnedShadowCasters.Count > 0;
        }

        private void AddShadowCaster(ShadowBatch batch, in InstanceGpu instance, Vector3 localCenter, float localRadius)
        {
            batch.Casters.Add(instance);
            batch.CasterBounds.Add(new Vector4(
                Vector3.Transform(localCenter, instance.World),
                localRadius * MatrixScaleHelper.MaxScale(instance.World)));
            int revision = TryGetMesh(batch.MeshId, out MeshEntry mesh) ? mesh.Revision : 0;
            ulong hash = FoldCasterSignature(1469598103934665603UL, batch.MeshId, revision, instance.World);
            batch.CasterHashes.Add((hash ^ (ulong)batch.RasterOverride) * 1099511628211UL);
        }

        // Static shadow caching: a cascade is re-rendered only when its light matrix or the set of
        // casters inside it changes. A still camera over a static scene re-renders no cascade.
        private readonly ulong[] _cascadeSignature = new ulong[3];
        private readonly bool[] _cascadeValid = new bool[3];
        private readonly ulong[] _cascadeSignatureScratch = new ulong[3];
        private readonly bool[] _cascadeDynamicScratch = new bool[3];

        /// <summary>Sun shadow cascades re-rendered in the last frame (0 when all were cached).</summary>
        public int LastShadowCascadesRendered { get; private set; }

        /// <summary>SplitMix64 finaliser; summing mixed hashes gives an order-independent set signature.</summary>
        private static ulong MixSignature(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }

        private static ulong MatrixSignature(in Matrix4x4 matrix) =>
            FoldCasterSignature(1469598103934665603UL, 0, 0, matrix);

        /// <summary>
        /// Sorts this frame's casters into the cascades whose light-space box they can shadow.
        /// Every caster used to be drawn into the far cascade, and the near and mid cascades chose
        /// casters by the distance from camera to centre, so large or tall occluders outside that
        /// radius lost their shadow where the near cascade took over.
        /// </summary>
        private void ClassifyShadowCasters(in Matrix4x4 lightVPFar, in Matrix4x4 lightVPMid, in Matrix4x4 lightVPNear)
        {
            bool mid = _cascade.CascadeCount >= 3;
            // Signatures start from each cascade's light matrix; the far cascade also draws the floor.
            _cascadeSignatureScratch[0] = MatrixSignature(lightVPFar);
            _cascadeSignatureScratch[1] = MatrixSignature(lightVPMid);
            _cascadeSignatureScratch[2] = MatrixSignature(lightVPNear);
            if (_state.ShowFloor && FloorMesh.IsValid)
                _cascadeSignatureScratch[0] += MixSignature(MatrixSignature(GetFloorWorldMatrix()));
            Array.Clear(_cascadeDynamicScratch);
            foreach (ShadowBatch batch in _shadowBatchList)
            {
                batch.FarInstances.Clear();
                batch.MidInstances.Clear();
                batch.NearInstances.Clear();
                for (int i = 0; i < batch.Casters.Count; i++)
                {
                    Vector4 bounds = batch.CasterBounds[i];
                    Vector3 center = new(bounds.X, bounds.Y, bounds.Z);
                    if (!CascadeShadowMath.CasterReachesCascade(lightVPFar, center, bounds.W)) continue;
                    InstanceGpu instance = batch.Casters[i];
                    ulong mixed = MixSignature(batch.CasterHashes[i]);
                    batch.FarInstances.Add(instance);
                    _cascadeSignatureScratch[0] += mixed;
                    if (mid && CascadeShadowMath.CasterReachesCascade(lightVPMid, center, bounds.W))
                    {
                        batch.MidInstances.Add(instance);
                        _cascadeSignatureScratch[1] += mixed;
                    }
                    if (CascadeShadowMath.CasterReachesCascade(lightVPNear, center, bounds.W))
                    {
                        batch.NearInstances.Add(instance);
                        _cascadeSignatureScratch[2] += mixed;
                    }
                }
            }

            var skinned = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_skinnedShadowCasters);
            for (int i = 0; i < skinned.Length; i++)
            {
                ref SkinnedShadowCaster caster = ref skinned[i];
                caster.Far = CascadeShadowMath.CasterReachesCascade(lightVPFar, caster.Center, caster.Radius);
                caster.Mid = caster.Far && mid
                    && CascadeShadowMath.CasterReachesCascade(lightVPMid, caster.Center, caster.Radius);
                caster.Near = caster.Far
                    && CascadeShadowMath.CasterReachesCascade(lightVPNear, caster.Center, caster.Radius);
                // Skinned casters animate without their transform changing: never cache over them.
                _cascadeDynamicScratch[0] |= caster.Far;
                _cascadeDynamicScratch[1] |= caster.Mid;
                _cascadeDynamicScratch[2] |= caster.Near;
            }
        }

        /// <summary>True when cascade <paramref name="index"/> must be re-rendered this frame; records it as rendered.</summary>
        private bool TakeDirtyCascade(int index)
        {
            bool dirty = !_cascadeValid[index]
                || _cascadeSignature[index] != _cascadeSignatureScratch[index]
                || _cascadeDynamicScratch[index];
            if (!dirty) return false;
            _cascadeValid[index] = true;
            _cascadeSignature[index] = _cascadeSignatureScratch[index];
            LastShadowCascadesRendered++;
            return true;
        }

        /// <summary>Draws this frame's GPU-skinned shadow casters into the bound shadow target.</summary>
        private void DrawSkinnedShadowCasters(ShadowCascadeKind cascade)
        {
            if (_skinnedShadowCasters.Count == 0 || !_shadowSkinnedProgram.IsValid) return;
            GpuShaderProgramHandle program = cascade switch
            {
                ShadowCascadeKind.Near => _shadowSkinnedNearProgram,
                ShadowCascadeKind.Mid => _shadowSkinnedMidProgram,
                _ => _shadowSkinnedProgram,
            };
            bool bound = false;
            MeshDrawFlags lastRaster = (MeshDrawFlags)(-1);
            foreach (SkinnedShadowCaster caster in _skinnedShadowCasters)
            {
                if (cascade == ShadowCascadeKind.Far && !caster.Far) continue;
                if (cascade == ShadowCascadeKind.Near && !caster.Near) continue;
                if (cascade == ShadowCascadeKind.Mid && !caster.Mid) continue;
                if (cascade == ShadowCascadeKind.Omni && !caster.Omni) continue;
                if (!TryGetMesh(caster.MeshId, out MeshEntry mesh)
                    || !TryGetSkinPalette(caster.PaletteId, out SkinPaletteEntry palette)) continue;
                if (!bound)
                {
                    _gpu.SetVertexLayout(_layoutSkinned);
                    _gpu.SetShaderProgram(program);
                    bound = true;
                }
                if (caster.RasterOverride != lastRaster)
                {
                    _gpu.SetRasterState(ShadowRasterizer(caster.RasterOverride));
                    lastRaster = caster.RasterOverride;
                }
                SetMeshBuffers(ref mesh);
                _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 12, palette.Buffer);
                UploadDrawCB(caster.World, Vector4.One, 0f, 0f, 0f, 0f, 0, gpuSkinning: true);
                _gpu.DrawIndexed(mesh.IndexCount);
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
                LastShadowCasterCount++;
            }
            if (!bound) return;
            _gpu.SetVertexLayout(_layout);
            BindCommonShaderState(shadowPass: true, cascade);
            _gpu.SetRasterState(_rsShadow);
        }

        private void SortBatchesByDistance(Vector3 cameraPos)
        {
            _sortCameraPos = cameraPos;
            if (_batchList.Count > 1)
            {
                for (int i = 0; i < _batchList.Count; i++) _batchList[i].SubmissionOrder = i;
                _batchList.Sort(_batchDistCompare);
            }

            if (_worldMeshes.Count > 1)
            {
                var worldMeshes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_worldMeshes);
                for (int i = 0; i < worldMeshes.Length; i++) worldMeshes[i].Order = i;
                _worldMeshes.Sort(_worldMeshDistCompare);
            }

            // Alpha-blended particle instances composite correctly only far-to-near. Additive and
            // multiply blends are order-independent, so they keep submission order.
            foreach (TransBatch batch in _transBatchList)
                if (batch.BlendMode == 0 && batch.Instances.Count is > 1 and <= 4096)
                    batch.Instances.Sort(_transInstanceBackToFront);
        }

        private float BatchDistanceSquared(Vector3 cameraPos, Batch batch)
        {
            if (!TryGetMesh(batch.MeshId, out MeshEntry mesh))
                return float.MaxValue;

            if (batch.Instances.Count > 0)
            {
                Vector3 center = Vector3.Transform(mesh.BoundsCenter, batch.Instances[0].World);
                return Vector3.DistanceSquared(cameraPos, center);
            }

            return Vector3.DistanceSquared(cameraPos, mesh.BoundsCenter);
        }

        private float WorldMeshDistanceSquared(Vector3 cameraPos, WorldMesh wm)
        {
            if (!TryGetMesh(wm.MeshId, out MeshEntry mesh))
                return float.MaxValue;

            Vector3 center = Vector3.Transform(mesh.BoundsCenter, wm.World);
            return Vector3.DistanceSquared(cameraPos, center);
        }

        // Reference geometry needs its own batch so it can bypass the authored preview program.
        private const MeshDrawFlags RasterOverrideMask = MeshDrawFlags.NoCull | MeshDrawFlags.EditorReference
            | MeshDrawFlags.CullFront | MeshDrawFlags.CullBack
            | MeshDrawFlags.FrontCounterClockwise | MeshDrawFlags.FrontClockwise;

        private static MeshDrawFlags RasterOverride(MeshDrawFlags flags) =>
            flags & RasterOverrideMask;

        private static MeshDrawFlags FoliageRasterOverride(MeshDrawFlags flags) =>
            RasterOverride(MeshDrawFlags.NoCull | (flags & MeshDrawFlags.EditorReference));

        private MeshDrawFlags RasterOverrideForWorld(MeshDrawFlags flags, Matrix4x4 world)
        {
            MeshDrawFlags raster = RasterOverride(flags);
            if (world.GetDeterminant() >= 0) return raster;
            bool ccw = (raster & MeshDrawFlags.FrontCounterClockwise) != 0
                || ((raster & MeshDrawFlags.FrontClockwise) == 0 && _state.FrontCounterClockwise);
            raster &= ~(MeshDrawFlags.FrontCounterClockwise | MeshDrawFlags.FrontClockwise);
            return raster | (ccw ? MeshDrawFlags.FrontClockwise : MeshDrawFlags.FrontCounterClockwise);
        }

        private GpuRasterState CurrentRasterizer(MeshDrawFlags rasterOverride = MeshDrawFlags.None)
        {
            if (_state.Wireframe)
                return _rsWireframe;

            bool counterClockwise = (rasterOverride & MeshDrawFlags.FrontCounterClockwise) != 0
                || ((rasterOverride & MeshDrawFlags.FrontClockwise) == 0
                    && _state.FrontCounterClockwise);
            // Asset winding is defined in world space. The runtime's LH projection reverses
            // screen winding relative to the editors' System.Numerics RH projection. Keep
            // camera/navigation conventions intact and translate the raster state here.
            bool leftHandedProjection = MathF.Abs(_proj.M34) > 0.0001f ? _proj.M34 > 0f : _proj.M33 > 0f;
            if (leftHandedProjection) counterClockwise = !counterClockwise;
            if (_reflectionPassActive) counterClockwise = !counterClockwise;
            if ((rasterOverride & MeshDrawFlags.NoCull) != 0)
                return _rsCullNone;

            bool explicitBack = (rasterOverride & MeshDrawFlags.CullBack) != 0;
            bool cullFront = (rasterOverride & MeshDrawFlags.CullFront) != 0
                || (!explicitBack && _state.CullFrontFaces);
            bool cullingEnabled = explicitBack || cullFront || _state.CullBackFaces;
            if (!cullingEnabled) return _rsCullNone;
            if (cullFront) return counterClockwise ? _rsCullFrontCw : _rsCullFront;
            return counterClockwise ? _rsSolidCw : _rsSolid;
        }

        private GpuRasterState ShadowRasterizer(MeshDrawFlags rasterOverride)
        {
            bool counterClockwise = (rasterOverride & MeshDrawFlags.FrontCounterClockwise) != 0
                || ((rasterOverride & MeshDrawFlags.FrontClockwise) == 0
                    && _state.FrontCounterClockwise);
            // Both directional and local-light shadow cameras use D3dMatrixHelper LH projections.
            counterClockwise = !counterClockwise;
            if ((rasterOverride & MeshDrawFlags.NoCull) != 0)
                return _rsShadowCullNone;
            bool explicitBack = (rasterOverride & MeshDrawFlags.CullBack) != 0;
            bool cullFront = (rasterOverride & MeshDrawFlags.CullFront) != 0
                || (!explicitBack && _state.CullFrontFaces);
            bool cullingEnabled = explicitBack || cullFront || _state.CullBackFaces;
            if (!cullingEnabled) return _rsShadowCullNone;
            if (cullFront) return counterClockwise ? _rsShadowCullFrontCw : _rsShadowCullFront;
            return counterClockwise ? _rsShadowCw : _rsShadow;
        }

        private GpuShaderProgramHandle CurrentForwardProgram(bool skinned, RuntimeShaderHandle runtimeShader = default, bool editorReference = false)
        {
            // Diagnostic views must use the diagnostic pixel shader, including authored materials
            // and the Shader Editor's preview override. Restore those programs in Shaded mode.
            if (_state.DebugView != RenderDebugView.Shaded || editorReference) return skinned ? _skinnedProgram : _program;
            if (skinned && _overrideSkinnedProgram.IsValid) return _overrideSkinnedProgram;
            if (!skinned && _overrideProgram.IsValid) return _overrideProgram;
            if (runtimeShader.IsValid && _runtimePrograms.TryGetValue(runtimeShader.Id, out var programs))
                return skinned ? programs.Skinned : programs.Static;
            return skinned ? _skinnedProgram : _program;
        }

        private void BindShaderParameters(RuntimeShaderHandle shader, Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3)
        {
            if (!shader.IsValid || !_runtimePrograms.ContainsKey(shader.Id)) return;
            _gpu.UpdateConstantBuffer(_cbShaderParameters, new ShaderParametersCB
            {
                Row0 = row0, Row1 = row1, Row2 = row2, Row3 = row3,
            });
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 5, _cbShaderParameters);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 5, _cbShaderParameters);
        }

        private Genesis.Shared.Assets.ShaderMeshPassMode RuntimePassMode(RuntimeShaderHandle shader) =>
            _runtimePassModes.TryGetValue(shader.Id, out var mode) ? mode : Genesis.Shared.Assets.ShaderMeshPassMode.Surface;

        private void ApplyRuntimeMeshPass(RuntimeShaderHandle shader, MeshDrawFlags rasterFlags)
        {
            var mode = RuntimePassMode(shader);
            GpuDepthState depth = _dssDefault;
            GpuBlendState blend = _bsOpaque;
            GpuRasterState raster = CurrentRasterizer(rasterFlags);
            if (mode != Genesis.Shared.Assets.ShaderMeshPassMode.Surface)
            {
                bool outline = mode == Genesis.Shared.Assets.ShaderMeshPassMode.StencilOutline;
                depth.WriteEnabled = false;
                // The surface has already written this exact depth. Less rejects the
                // mask/reset and leaves thin bevels exposed to the expanded back faces.
                depth.Compare = GpuCompare.LessEqual;
                depth.StencilEnabled = true;
                depth.StencilReadMask = 255;
                depth.StencilWriteMask = outline ? (byte)0 : (byte)255;
                depth.StencilReference = mode == Genesis.Shared.Assets.ShaderMeshPassMode.StencilReset ? (byte)0 : (byte)1;
                depth.StencilCompare = outline ? GpuCompare.NotEqual : GpuCompare.Always;
                depth.StencilPass = outline ? GpuStencilOperation.Keep : GpuStencilOperation.Replace;
                if (outline) raster.CullMode = GpuCullMode.Front;
                else
                {
                    blend.WriteR = blend.WriteG = blend.WriteB = blend.WriteA = false;
                    blend.IndependentBlend = true;
                    blend.SecondaryWriteR = blend.SecondaryWriteG = blend.SecondaryWriteB = blend.SecondaryWriteA = false;
                }
            }
            _gpu.SetDepthState(depth);
            _gpu.SetBlendState(blend);
            _gpu.SetRasterState(raster);
        }

        /// <summary>Bind VS CB slots and instance buffer (shadow pass uses its own buffer and VS).</summary>
        private void BindCommonShaderState(bool shadowPass, ShadowCascadeKind cascade = ShadowCascadeKind.Far)
        {
            _gpu.SetVertexLayout(_layout);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 2, _cbDraw);

            // Shadow pass reads from the dedicated shadow instance buffer; main pass from the main buffer.
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0,
                shadowPass ? _shadowInstanceBuf : _instanceBuf);

            if (!shadowPass)
            {
                _gpu.SetStructuredBuffer(GpuShaderStage.Pixel, 6, _clusterLightBuf);
                _gpu.SetStructuredBuffer(GpuShaderStage.Pixel, 13, _tileLightIndexBuf);
            }

            if (shadowPass)
            {
                _gpu.SetShaderProgram(cascade switch
                {
                    ShadowCascadeKind.Near => _shadowNearProgram,
                    ShadowCascadeKind.Mid => _shadowMidProgram,
                    _ => _shadowProgram,
                });
            }
        }

        private void UploadShadowInstances(ShadowCascadeKind cascade)
        {
            int total = 0;
            foreach (var b in _shadowBatchList)
                total += CascadeInstanceCount(b, cascade);
            if (total == 0) return;
            int cap = Math.Min(total, MaxInstances);
            int bytes = cap * Marshal.SizeOf<InstanceGpu>();

            if (!_gpu.TryMapDiscard(_shadowInstanceBuf, out Span<byte> mapped, bytes))
                return;

            Span<InstanceGpu> dst = MemoryMarshal.Cast<byte, InstanceGpu>(mapped);
            int written = 0;
            foreach (var b in _shadowBatchList)
            {
                var list = CascadeInstances(b, cascade);
                foreach (var inst in list)
                    if (written < cap) dst[written++] = inst;
            }

            _gpu.Unmap(_shadowInstanceBuf);
        }

        private static int CascadeInstanceCount(ShadowBatch b, ShadowCascadeKind cascade) =>
            cascade switch
            {
                ShadowCascadeKind.Near => b.NearInstances.Count,
                ShadowCascadeKind.Mid => b.MidInstances.Count,
                ShadowCascadeKind.Omni => b.OmniInstances.Count,
                _ => b.FarInstances.Count,
            };

        private static List<InstanceGpu> CascadeInstances(ShadowBatch b, ShadowCascadeKind cascade) =>
            cascade switch
            {
                ShadowCascadeKind.Near => b.NearInstances,
                ShadowCascadeKind.Mid => b.MidInstances,
                ShadowCascadeKind.Omni => b.OmniInstances,
                _ => b.FarInstances,
            };

        // ── Shadow pass ───────────────────────────────────────────────────────────

        private void ShadowPass()
        {
            Matrix4x4 lightVPFar  = ComputeLightViewProj(ShadowCascadeKind.Far);
            Matrix4x4 lightVPMid  = _cascade.CascadeCount >= 3
                ? ComputeLightViewProj(ShadowCascadeKind.Mid)
                : lightVPFar;
            Matrix4x4 lightVPNear = ComputeLightViewProj(ShadowCascadeKind.Near);
            int shadowBudget = Math.Max(1, ShadowBatchBudget);
            ClassifyShadowCasters(lightVPFar, lightVPMid, lightVPNear);

            _gpu.SetDepthState(_dssDefault);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 5);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 14);

            if (TakeDirtyCascade(0))
            {
                RenderOneShadowCascade(
                    ShadowCascadeKind.Far, _shadowTarget, ShadowMapSize, "Shadow far",
                    lightVPFar, lightVPFar, lightVPNear, lightVPMid, shadowBudget,
                    includeFloor: true);
            }

            if (_cascade.CascadeCount >= 3 && TakeDirtyCascade(1))
            {
                RenderOneShadowCascade(
                    ShadowCascadeKind.Mid, _shadowMidTarget, ShadowMapSizeMid, "Shadow mid",
                    lightVPMid, lightVPFar, lightVPNear, lightVPMid, shadowBudget,
                    includeFloor: false);
            }

            if (TakeDirtyCascade(2))
            {
                RenderOneShadowCascade(
                    ShadowCascadeKind.Near, _shadowNearTarget, ShadowMapSizeNear, "Shadow near",
                    lightVPNear, lightVPFar, lightVPNear, lightVPMid, shadowBudget,
                    includeFloor: false);
            }
        }

        private void RenderOneShadowCascade(
            ShadowCascadeKind cascade,
            GpuRenderTargetHandle target,
            int mapSize,
            string debugName,
            Matrix4x4 cascadeVp,
            Matrix4x4 lightVPFar,
            Matrix4x4 lightVPNear,
            Matrix4x4 lightVPMid,
            int shadowBudget,
            bool includeFloor,
            bool uploadInstances = true)
        {
            if (uploadInstances) UploadShadowInstances(cascade);

            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = _gpu.GetRenderTargetTexture(target, 0).IsValid
                    ? new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) }
                    : Array.Empty<GpuAttachmentAction>(),
                DepthAction = GpuAttachmentAction.Clear(1f, 0f, 0f, 0f),
                HasDepth = true,
                DebugName = debugName,
            });
            _gpu.SetViewport(0, 0, mapSize, mapSize);

            UploadPerFrame(cascadeVp, lightVPFar, lightVPNear, lightVPMid);
            _gpu.SetRasterState(_rsShadow);
            BindCommonShaderState(shadowPass: true, cascade);
            DrawShadowBatches(cascade, shadowBudget);

            if (includeFloor && _state.ShowFloor && FloorMesh.IsValid)
            {
                _gpu.SetRasterState(_rsShadowCullNone);
                if (TryGetMesh(FloorMesh.Id, out MeshEntry floorMesh))
                {
                    SetMeshBuffers(ref floorMesh);
                    UploadDrawCB(GetFloorWorldMatrix(),
                        Vector4.One, 0f, 0f, 1f, 0f, 0);
                    _gpu.DrawIndexed(floorMesh.IndexCount);
                    LastDrawCalls++; LastTriangles += floorMesh.IndexCount / 3;
                }
                _gpu.SetRasterState(_rsShadow);
            }
            _gpu.EndRenderPass();
        }

        /// <summary>Draws the cascade's (or local tile's) instanced casters, then its skinned casters.</summary>
        private void DrawShadowBatches(ShadowCascadeKind cascade, int shadowBudget)
        {
            int instOffset = 0;
            MeshDrawFlags lastShadowRaster = (MeshDrawFlags)(-1);
            foreach (var b in _shadowBatchList)
            {
                var list = CascadeInstances(b, cascade);
                if (list.Count == 0) continue;
                if (instOffset >= shadowBudget) break;
                if (!TryGetMesh(b.MeshId, out MeshEntry mesh)) { instOffset += list.Count; continue; }
                if (b.RasterOverride != lastShadowRaster)
                {
                    _gpu.SetRasterState(ShadowRasterizer(b.RasterOverride));
                    lastShadowRaster = b.RasterOverride;
                }
                SetMeshBuffers(ref mesh);
                int drawCount = Math.Min(list.Count, shadowBudget - instOffset);
                LastShadowCasterCount += drawCount;
                UploadDrawCB(Matrix4x4.Identity, Vector4.One, 0f, 0f, 0f, 1f, (uint)instOffset);
                _gpu.DrawIndexedInstanced(mesh.IndexCount, drawCount);
                instOffset += list.Count;
                LastDrawCalls++;
                LastTriangles += (mesh.IndexCount / 3) * drawCount;
            }

            DrawSkinnedShadowCasters(cascade);
        }

        private static Matrix4x4 ComputeOmniFaceViewProj(Vector3 lightPos, int face, float farPlane)
        {
            Vector3 dir = OmniShadowMath.FaceDirection(face);
            Vector3 up = OmniShadowMath.FaceUp(face);
            Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(lightPos, lightPos + dir, up);
            Matrix4x4 proj = D3dMatrixHelper.CreatePerspectiveLh(
                MathF.PI * 0.5f, 1f, OmniShadowMath.DefaultNearPlane, farPlane);
            return view * proj;
        }

        // ── Main pass ─────────────────────────────────────────────────────────────

        private int _rtWidth, _rtHeight;
        private GpuRenderTargetHandle _currentTarget;
        private GpuTextureHandle _currentDepthTexture;
        private bool _mainPassHdrMrt;

        private void MainPass(GpuRenderTargetHandle target, GpuTextureHandle depthTexture,
            GpuTextureHandle whiteTexture, bool postProcessTarget)
        {
            _currentTarget = target;
            _currentDepthTexture = depthTexture;
            _mainPassHdrMrt = postProcessTarget;
            // Shadow pass changes the viewport to 2048×2048 — restore it here.
            if (_rtWidth > 0 && _rtHeight > 0)
                _gpu.SetViewport(0, 0, _rtWidth, _rtHeight);

            Vector3 bg = ToLinearColor(_state.BackgroundColor);
            _encodeOutputThisPass = LinearPipeline && !postProcessTarget;
            GpuAttachmentAction[] colors = postProcessTarget
                ? new[]
                {
                    GpuAttachmentAction.Clear(bg.X, bg.Y, bg.Z, 1f),
                    GpuAttachmentAction.Clear(0f, 0f, 0f, 0f),
                }
                : new[] { GpuAttachmentAction.Keep() };
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                DepthTexture = depthTexture,
                ColorActions = colors,
                DepthAction = GpuAttachmentAction.Clear(1f, 0f, 0f, 0f),
                HasDepth = true,
                DebugName = postProcessTarget ? "Forward HDR main" : "Forward direct main",
            });

            _gpu.SetRasterState(CurrentRasterizer());
            BindCommonShaderState(shadowPass: false);

            _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);

            // t2: far cascade, t3: flat normal, t5: near cascade, t14: mid cascade (AF1.1)
            _gpu.SetTexture(GpuShaderStage.Pixel, 2,
                _shadowsActiveThisFrame ? _shadowTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 3, _flatNormalTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 5,
                _shadowsActiveThisFrame ? _shadowNearTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 14,
                _shadowsActiveThisFrame && _cascade.CascadeCount >= 3
                    ? _shadowMidTexture
                    : GpuTextureHandle.Invalid);

            // t15: local light shadow atlas (point and spot lights).
            if (_localShadowsActiveThisFrame && _localShadowAtlasTexture.IsValid)
                _gpu.SetTexture(GpuShaderStage.Pixel, 15, _localShadowAtlasTexture);
            else
                _gpu.ClearTexture(GpuShaderStage.Pixel, 15);

            // Always bind the local shadow CB (Params.x gates sampling) so inactive frames stay deterministic.
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 4, _cbOmni);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 4, _cbOmni);
            BindFroxelApply();

            // Bind samplers
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _albedoSampler);
            _gpu.SetSampler(GpuShaderStage.Pixel, 1, _shadowSampler);
            _gpu.SetSampler(GpuShaderStage.Vertex, 0, _albedoSampler);

            // Upload per-frame + engine CBs
            Matrix4x4 lightVPFar  = ComputeLightViewProj(ShadowCascadeKind.Far);
            Matrix4x4 lightVPMid  = _cascade.CascadeCount >= 3
                ? ComputeLightViewProj(ShadowCascadeKind.Mid)
                : lightVPFar;
            Matrix4x4 lightVPNear = ComputeLightViewProj(ShadowCascadeKind.Near);
            Matrix4x4 vp          = _view * _proj;
            UploadPerFrame(vp, lightVPFar, lightVPNear, lightVPMid);
            UploadEngineCB();

            _gpu.SetDepthState(_dssDefault);
            _gpu.SetBlendState(_bsOpaque);
            
            // Explicitly bind the 3D shader and layout before drawing environment, 
            // as SpriteBatcher or other renderers may have left their states bound.
            _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
            _gpu.SetVertexLayout(_layout);

            // Sun before floor so the infinite floor occludes the disc when looking at the ground.
            if (_state.ShowSunVisual && !_state.AuthoredSkyEnabled && SunMesh.IsValid)
                DrawEnvironmentSun(whiteTexture);

            if (_state.ShowFloor && FloorMesh.IsValid)
                DrawEnvironmentFloor(whiteTexture);

            // Ensure standard opaque states are restored for solid geometry.
            // DrawEnvironmentSun and DrawEnvironmentFloor mutate these, and if the floor
            // is disabled (e.g. for terrain), the sun's _dssNoWrite and _bsAdd states leak.
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetDepthState(_dssDefault);
            _gpu.SetRasterState(CurrentRasterizer());

            // R7.2: Manual mode budgets variable world batches only (not sun/floor/shadow/post/HUD).
            _worldDrawBudgetLeft = RenderCapacityDefaults.EffectiveWorldDrawBudget;

            // Instanced batches (opaque geometry)
            int instOffset = 0;
            MeshDrawFlags lastBatchRaster = 0;
            bool batchCullStateSet = false;
            foreach (var b in _batchList)
            {
                if (b.Instances.Count == 0) continue;
                if (instOffset >= SoftMeshInstanceCap) break;
                if (_worldDrawBudgetLeft <= 0) break;
                if (!TryGetMesh(b.MeshId, out MeshEntry mesh)) { instOffset += b.Instances.Count; continue; }
                SetMeshBuffers(ref mesh);
                _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false, b.Shader, (b.RasterOverride & MeshDrawFlags.EditorReference) != 0));
                BindShaderParameters(b.Shader, b.ShaderParams0, b.ShaderParams1, b.ShaderParams2, b.ShaderParams3);

                // Per-batch cull override (see MeshDrawFlags.NoCull): thin single-layer geometry
                // like grass blades sets this so it renders from both sides without resorting to
                // duplicate-winding triangles. Cached like the transparent pass below so the common
                // case (every other batch, cull-back) doesn't re-issue RSSetState every iteration.
                if (!batchCullStateSet || b.RasterOverride != lastBatchRaster)
                {
                    _gpu.SetRasterState(CurrentRasterizer(b.RasterOverride));
                    lastBatchRaster = b.RasterOverride;
                    batchCullStateSet = true;
                }

                _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                    b.IsFloor && _checkerTexture.IsValid && !b.Texture.IsValid
                        ? _checkerTexture
                        : (b.Texture.IsValid ? b.Texture : whiteTexture));

                // Bind per-batch normal map (t3); fall back to flat default
                _gpu.SetSampler(GpuShaderStage.Pixel, 0, b.Orm.IsValid ? _materialSampler : _albedoSampler);
                _gpu.SetTexture(GpuShaderStage.Pixel, 3,
                    b.Normal.IsValid ? b.Normal : _flatNormalTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 7, b.Orm);
                _gpu.SetTexture(GpuShaderStage.Pixel, 8, b.Height);
                _gpu.SetTexture(GpuShaderStage.Pixel, 9, b.Emission);
                _gpu.SetTexture(GpuShaderStage.Pixel, 10, b.Extras);
                _gpu.SetTexture(GpuShaderStage.Pixel, 11, b.Flow);
                _gpu.SetTexture(GpuShaderStage.Vertex, 8, b.Height);
                b.AuthoredTextures.Bind(_gpu);

                int drawCount = Math.Min(b.Instances.Count, SoftMeshInstanceCap - instOffset);
                ApplyRuntimeMeshPass(b.Shader, b.RasterOverride);
                // Foliage is lit in the shader's foliage branch (wrapped sun, shadows, ambient, point lights).
                float batchUnlit = 0f;
                UploadDrawCB(Matrix4x4.Identity, Vector4.One, b.Emissive, batchUnlit, b.IsFloor ? 1f : 0f, 1, (uint)instOffset,
                    noFog: b.NoFog, terrainGround: b.TerrainGround, foliage: b.Foliage,
                    surfaceParams: b.SurfaceParams, detailParams: b.DetailParams,
                    subsurfaceColorSteps: b.SubsurfaceColorSteps,
                    materialFeatures: new Vector4(b.Orm.IsValid ? 1f : 0f, (float)b.HeightMode,
                        b.Emission.IsValid ? 1f : 0f, (b.Extras.IsValid ? 1f : 0f) + (b.Flow.IsValid ? 2f : 0f)),
                    noReceiveShadow: b.NoReceiveShadow);
                _gpu.DrawIndexedInstanced(mesh.IndexCount, drawCount);
                _worldDrawBudgetLeft--;
                if (b.Foliage)
                {
                    _foliageInstances += drawCount;
                    _foliageBatches++;
                }
                instOffset += b.Instances.Count;
                LastDrawCalls++;
                LastTriangles += (mesh.IndexCount / 3) * drawCount;
            }

            // GPU-skinned opaque batches. Same instance buffer as static batches, but a skinned
            // vertex layout/shader and one bone-matrix palette per batch.
            int skinInstOffset = _skinnedInstOffset;
            bool skinnedBatchStateSet = false;
            bool skinnedRasterStateSet = false;
            MeshDrawFlags lastSkinnedRaster = 0;
            foreach (var b in _skinnedBatchList)
            {
                if (b.Instances.Count == 0) continue;
                if (skinInstOffset >= SoftMeshInstanceCap) break;
                if (_worldDrawBudgetLeft <= 0) break;
                if (!TryGetMesh(b.MeshId, out MeshEntry mesh)) { skinInstOffset += b.Instances.Count; continue; }
                if (!TryGetSkinPalette(b.SkinPaletteId, out SkinPaletteEntry skinPalette)) { skinInstOffset += b.Instances.Count; continue; }

                if (!skinnedBatchStateSet)
                {
                    _gpu.SetVertexLayout(_layoutSkinned);
                    skinnedBatchStateSet = true;
                }

                _gpu.SetShaderProgram(CurrentForwardProgram(skinned: true, b.Shader));
                BindShaderParameters(b.Shader, b.ShaderParams0, b.ShaderParams1, b.ShaderParams2, b.ShaderParams3);

                if (!skinnedRasterStateSet || b.RasterOverride != lastSkinnedRaster)
                {
                    _gpu.SetRasterState(CurrentRasterizer(b.RasterOverride));
                    lastSkinnedRaster = b.RasterOverride;
                    skinnedRasterStateSet = true;
                }

                SetMeshBuffers(ref mesh);
                _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                    b.Texture.IsValid ? b.Texture : whiteTexture);
                // Characters bind their own material maps. t3 used to keep whatever normal map the
                // last static batch bound (the shader always samples it), and ORM/emission were never
                // bound, so skinned meshes rendered with another material's normals and no PBR maps.
                _gpu.SetSampler(GpuShaderStage.Pixel, 0, b.Orm.IsValid ? _materialSampler : _albedoSampler);
                _gpu.SetTexture(GpuShaderStage.Pixel, 3, b.Normal.IsValid ? b.Normal : _flatNormalTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 7, b.Orm);
                _gpu.SetTexture(GpuShaderStage.Pixel, 9, b.Emission);
                b.AuthoredTextures.Bind(_gpu);
                _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 12, skinPalette.Buffer);

                int drawCount = Math.Min(b.Instances.Count, SoftMeshInstanceCap - skinInstOffset);
                ApplyRuntimeMeshPass(b.Shader, b.RasterOverride);
                UploadDrawCB(Matrix4x4.Identity, Vector4.One, b.Emissive, unlit: 0f, isFloor: 0f, useInstancing: 1,
                    instOffset: (uint)skinInstOffset, noFog: b.NoFog, gpuSkinning: true,
                    skinMatrixOffset: 0, noReceiveShadow: b.NoReceiveShadow,
                    materialFeatures: new Vector4(b.Orm.IsValid ? 1f : 0f, 0f, b.Emission.IsValid ? 1f : 0f, 0f));
                _gpu.DrawIndexedInstanced(mesh.IndexCount, drawCount);
                _worldDrawBudgetLeft--;
                skinInstOffset += b.Instances.Count;
                LastDrawCalls++;
                LastTriangles += (mesh.IndexCount / 3) * drawCount;
            }
            if (skinnedBatchStateSet)
            {
                _gpu.SetVertexLayout(_layout);
                _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
                _gpu.SetRasterState(CurrentRasterizer());
            }

            // Restore per-frame flat normal after opaque pass (world meshes and trans batches override per-item)
            {
                _gpu.SetSampler(GpuShaderStage.Pixel, 0, _albedoSampler);
                _gpu.SetTexture(GpuShaderStage.Pixel, 3, _flatNormalTexture);
            }

            // Opaque world-mesh pass: leftover parked items that are NOT transparent/additive.
            // Opaque IsFloor draws instance above (R7.7); this pass still covers skinned floors and
            // other non-instanced opaque leftovers that must write depth before water.
            {
                var opaqueSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_worldMeshes);
                bool opaqueStateSet = false;
                bool opaqueCullStateSet = false;
                bool opaqueShaderStateSet = false;
                MeshDrawFlags lastOpaqueRaster = 0;
                bool lastOpaqueSkinned = false;
                for (int i = 0; i < opaqueSpan.Length; i++)
                {
                    ref WorldMesh wm = ref opaqueSpan[i];
                    if (wm.Transparent || wm.Additive || wm.Multiply || wm.NoDepthTest)
                        continue; // transparent pass; view models drawn after post

                    if (_worldDrawBudgetLeft <= 0)
                        break;

                    if (!TryGetMesh(wm.MeshId, out MeshEntry mesh))
                        continue;

                    if (!opaqueStateSet)
                    {
                        _gpu.SetBlendState(_bsOpaque);
                        _gpu.SetDepthState(_dssDefault);
                        opaqueStateSet = true;
                    }

                    if (!opaqueCullStateSet || wm.RasterOverride != lastOpaqueRaster)
                    {
                        _gpu.SetRasterState(CurrentRasterizer(wm.RasterOverride));
                        lastOpaqueRaster = wm.RasterOverride;
                        opaqueCullStateSet = true;
                    }

                    SkinPaletteEntry skinPalette = default;
                    bool skinned = wm.SkinPaletteId > 0 && TryGetSkinPalette(wm.SkinPaletteId, out skinPalette);
                    if (!opaqueShaderStateSet || skinned != lastOpaqueSkinned)
                    {
                        _gpu.SetVertexLayout(skinned ? _layoutSkinned : _layout);
                        _gpu.SetShaderProgram(CurrentForwardProgram(skinned));
                        lastOpaqueSkinned = skinned;
                        opaqueShaderStateSet = true;
                    }
                    if (skinned)
                    {
                        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 12, skinPalette.Buffer);
                    }

                    SetMeshBuffers(ref mesh);
                    // Manually submitted floor meshes (including PGSL DrawFloor3D and the
                    // headless visual harness) do not carry a texture handle. Give them the same
                    // checkerboard used by the renderer-owned environment floor instead of the
                    // white fallback; otherwise a valid floor is rendered as a featureless dark
                    // plane and backend visual tests can miss it entirely.
                    GpuTextureHandle albedo = wm.IsFloor && _checkerTexture.IsValid
                        ? _checkerTexture
                        : (wm.Texture.IsValid ? wm.Texture : whiteTexture);
                    _gpu.SetTexture(GpuShaderStage.Pixel, 1, albedo);

                    // This pass always binds _dssDefault above (opaqueStateSet block), so even an
                    // item flagged wm.NoDepthWrite genuinely does write depth here — leave
                    // noDepthWrite at its default false so the forward shader doesn't double-fog
                    // a pixel the post-process can already see correctly.
                    UploadDrawCB(wm.World, wm.Color,
                        emissive: wm.Emissive, unlit: wm.Unlit ? 1f : 0f,
                        isFloor: wm.IsFloor ? 1f : 0f, useInstancing: 0, instOffset: 0, noFog: wm.NoFog,
                        terrainGround: wm.TerrainGround, gpuSkinning: skinned, skinMatrixOffset: 0,
                        noReceiveShadow: wm.NoReceiveShadow);

                    _gpu.DrawIndexed(mesh.IndexCount);
                    _worldDrawBudgetLeft--;
                    LastDrawCalls++;
                    LastTriangles += mesh.IndexCount / 3;
                }
                if (opaqueShaderStateSet && lastOpaqueSkinned)
                {
                    _gpu.SetVertexLayout(_layout);
                    _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
                }
            }

            DrawWaterPass(whiteTexture, depthTexture);

            // Transparent + additive world meshes (particles, voxel liquids, etc.)
            //
            // Particle bursts can push thousands of WorldMesh entries through here in a single
            // frame, all sharing the same blend mode / depth state / cull mode / mesh / texture
            // (only World/Color/Emissive legitimately differ per particle). Re-issuing the five
            // pipeline-state binds below on every entry was the dominant cost of particle-heavy
            // scenes, so we track what is already bound and only rebind when a value actually
            // changes. This is purely a state-cache: output is bit-for-bit identical to the
            // previous unconditional-rebind version.
            var worldSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_worldMeshes);

            // _worldMeshes is sorted near-to-far for the opaque pass above. Blending needs
            // far-to-near, so the transparent pass walks it backwards.
            bool boundOnce = false;
            byte lastBlendMode = byte.MaxValue;
            bool lastNoDepthWrite = false;
            bool lastCullNone = false;
            int  lastMeshId = -1;
            int lastTextureId = -1;

            for (int i = worldSpan.Length - 1; i >= 0; i--)
            {
                ref WorldMesh wm = ref worldSpan[i];
                if (wm.NoDepthTest || (!wm.Transparent && !wm.Additive && !wm.Multiply))
                    continue;

                if (_worldDrawBudgetLeft <= 0)
                    break;

                byte blendMode = wm.Multiply ? (byte)2 : wm.Additive ? (byte)1 : (byte)0;
                if (!boundOnce || blendMode != lastBlendMode)
                {
                    _gpu.SetBlendState(blendMode == 2 ? _bsMultiply : blendMode == 1 ? _bsAdd : _bsAlpha);
                    lastBlendMode = blendMode;
                }

                if (!boundOnce || wm.NoDepthWrite != lastNoDepthWrite)
                {
                    _gpu.SetDepthState(wm.NoDepthWrite ? _dssNoWrite : _dssDefault);
                    lastNoDepthWrite = wm.NoDepthWrite;
                }

                bool cullNone = wm.NoDepthWrite || wm.Transparent || wm.Additive || wm.Multiply;
                if (!boundOnce || cullNone != lastCullNone)
                {
                    _gpu.SetRasterState(CurrentRasterizer(
                        cullNone ? MeshDrawFlags.NoCull : wm.RasterOverride));
                    lastCullNone = cullNone;
                }

                if (!TryGetMesh(wm.MeshId, out MeshEntry mesh))
                    continue;

                if (!boundOnce || wm.MeshId != lastMeshId)
                {
                    SetMeshBuffers(ref mesh);
                    lastMeshId = wm.MeshId;
                }

                if (!boundOnce || wm.Texture.Id != lastTextureId)
                {
                    _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                        wm.Texture.IsValid ? wm.Texture : whiteTexture);
                    lastTextureId = wm.Texture.Id;
                }

                boundOnce = true;

                // wm.NoDepthWrite exactly matches the depth-stencil state just bound above
                // (lastNoDepthWrite), so it's a faithful "did this draw actually write depth" flag.
                UploadDrawCB(wm.World, wm.Color,
                    emissive: wm.Emissive, unlit: wm.Unlit ? 1f : 0f,
                    isFloor: 0f, useInstancing: 0, instOffset: 0,
                    noFog: wm.NoFog, noDepthWrite: wm.NoDepthWrite,
                    noReceiveShadow: wm.NoReceiveShadow);

                _gpu.DrawIndexed(mesh.IndexCount);
                _worldDrawBudgetLeft--;
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
            }

            _gpu.SetRasterState(CurrentRasterizer());

            // Transparent / additive particle instanced pass — one DrawIndexedInstanced per unique
            // (mesh, texture, blendMode) instead of one DrawIndexed per particle.
            DrawTransparentBatches(whiteTexture);
            if (!_reflectionPassActive) ExternalParticles?.Invoke(_view, _proj);

            // Unbind SRVs that were written during shadow pass so they can be used as DSV next frame
            _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 5);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 6);
            for (int t = 15; t <= 20; t++)
                _gpu.ClearTexture(GpuShaderStage.Pixel, t);
            _gpu.EndRenderPass();
        }

        private void DrawWaterPass(GpuTextureHandle whiteTexture, GpuTextureHandle depthTexture)
        {
            if (_reflectionPassActive) return;
            // Guard log (Issue 3 diagnostic): confirms water draws are actually reaching
            // the GPU when a lake/lava body is toggled on, instead of silently no-opping
            // on a missing shader handle or an empty mesh list. Logs only on change.
            if (_waterMeshes.Count != _lastWaterMeshCountLogged)
            {
                _lastWaterMeshCountLogged = _waterMeshes.Count;
                RenderLog.Line($"Water meshes: {_waterMeshes.Count} (shaders ready: {_waterProgram.IsValid})");
            }

            if (_waterMeshes.Count == 0 || !_waterProgram.IsValid)
                return;

            // Shore foam samples scene depth. That resource is also this pass's DSV, and D3D
            // either unbinds the SRV (always-deep water) or returns stale cache (tile flicker
            // in the golden scene). Fog post already samples depth after the scene pass ends;
            // water does the same: colour-only pass, depth as SRV, then restore depth for
            // transparents. Occlusion is a LessEqual discard in PS_Water.
            _gpu.EndRenderPass();
            BeginForwardColorPass(hasDepth: false, "Forward water");

            _gpu.SetBlendState(_bsAlpha);
            _gpu.SetDepthState(_dssNoTest);
            _gpu.SetRasterState(_rsCullNone);

            _gpu.SetShaderProgram(_waterProgram);

            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 2, _cbDraw);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 3, _cbWater);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 3, _cbWater);

            _gpu.SetTexture(GpuShaderStage.Pixel, 1, _waterNormalA);
            _gpu.SetTexture(GpuShaderStage.Pixel, 3, _waterNormalB);
            _gpu.SetTexture(GpuShaderStage.Pixel, 6, depthTexture);
            BindFroxelApply();
            _gpu.SetTexture(GpuShaderStage.Pixel, 7, _reflectionReady ? _reflectionTexture : whiteTexture);

            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _albedoSampler);
            _gpu.SetSampler(GpuShaderStage.Pixel, 1, _shadowSampler);

            var waterSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_waterMeshes);
            for (int i = 0; i < waterSpan.Length; i++)
            {
                ref WaterMesh wm = ref waterSpan[i];
                if (!TryGetMesh(wm.MeshId, out MeshEntry mesh))
                    continue;

                float planeHeight = Vector3.Transform(mesh.BoundsCenter, wm.World).Y;
                UploadWaterCB(wm.Deep, wm.WaterParams, wm.SkyHorizon, wm.SkyZenith,
                    _reflectionReady && Math.Abs(planeHeight - _reflectionPlaneHeight) < .05f);
                UploadDrawCB(wm.World, ToLinearColor(wm.Shallow), emissive: 0f, unlit: 0f, isFloor: 0f, useInstancing: 0, instOffset: 0);

                _gpu.SetTexture(GpuShaderStage.Pixel, 0,
                    wm.Albedo.IsValid ? wm.Albedo : whiteTexture);

                SetMeshBuffers(ref mesh);
                _gpu.DrawIndexed(mesh.IndexCount);
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
            }

            _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
            _gpu.SetDepthState(_dssDefault);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(CurrentRasterizer());

            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 3, GpuBufferHandle.Invalid);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 6);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 7);
            _gpu.SetTexture(GpuShaderStage.Pixel, 3, _flatNormalTexture);

            _gpu.EndRenderPass();
            BeginForwardColorPass(hasDepth: true, "Forward transparent");
        }

        private void BeginForwardColorPass(bool hasDepth, string debugName)
        {
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = _currentTarget,
                DepthTexture = hasDepth ? _currentDepthTexture : default,
                ColorActions = CurrentColorKeepActions(),
                DepthAction = GpuAttachmentAction.Keep(),
                HasDepth = hasDepth,
                DebugName = debugName,
            });
            if (_rtWidth > 0 && _rtHeight > 0)
                _gpu.SetViewport(0, 0, _rtWidth, _rtHeight);
        }

        private GpuAttachmentAction[] CurrentColorKeepActions() =>
            _mainPassHdrMrt
                ? new[] { GpuAttachmentAction.Keep(), GpuAttachmentAction.Keep() }
                : new[] { GpuAttachmentAction.Keep() };

        private void UploadWaterCB(Vector4 deep, Vector4 waterParams, Vector4 skyHorizon, Vector4 skyZenith, bool reflection)
        {
            var data = new WaterCB
            {
                DeepColorDepthFade = new Vector4(ToLinearColor(new Vector3(deep.X, deep.Y, deep.Z)), deep.W),
                WaterParams = waterParams,
                SkyHorizonFlow = new Vector4(ToLinearColor(new Vector3(skyHorizon.X, skyHorizon.Y, skyHorizon.Z)), skyHorizon.W),
                SkyZenithPad = new Vector4(ToLinearColor(new Vector3(skyZenith.X, skyZenith.Y, skyZenith.Z)), skyZenith.W),
                ReflectionViewProjection = _reflectionViewProjection,
                ReflectionParams = new Vector4(reflection ? 1 : 0, .015f, 0, 0),
                WeatherWindRain = _state.WeatherWindRain,
            };
            _gpu.UpdateConstantBuffer(_cbWater, data);
        }

        private void CompositePost(
            GpuRenderTargetHandle target,
            GpuTextureHandle sceneTexture,
            GpuTextureHandle depthTexture,
            int width, int height,
            GpuTextureHandle aoTexture = default,
            bool aoEnabled = false,
            GpuTextureHandle contactTexture = default,
            bool contactEnabled = false,
            GpuTextureHandle localVolTexture = default,
            bool localVolEnabled = false,
            GpuTextureHandle bloomTexture = default,
            bool bloomEnabled = false,
            bool atmosphereLutEnabled = false,
            GpuTextureHandle cloudTexture = default,
            bool cloudsEnabled = false,
            bool celestialExtrasEnabled = false)
        {
            if (!_fogProgram.IsValid || !sceneTexture.IsValid || !depthTexture.IsValid)
                return;

            float nearPlane = _nearPlane;
            float farPlane  = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            Matrix4x4 viewProj = _view * _proj;
            if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invVp))
                invVp = Matrix4x4.Identity;

            // Recompute the light VPs here (cheap CPU matrix math) so the composite pass can
            // sample the shadow maps for shadow-aware volumetric fog (light shafts).
            Matrix4x4 lightVPFar  = ComputeLightViewProj(ShadowCascadeKind.Far);
            Matrix4x4 lightVPMid  = _cascade.CascadeCount >= 3
                ? ComputeLightViewProj(ShadowCascadeKind.Mid)
                : lightVPFar;
            Matrix4x4 lightVPNear = ComputeLightViewProj(ShadowCascadeKind.Near);

            Vector3 skySun = _state.AuthoredSkyEnabled ? _state.SkySunDirection : _state.LightDirection;
            float skySeconds = _state.AuthoredSkyEnabled ? _state.SkyElapsedSeconds : _time;
            float sunHeight = AtmosphereLutMath.SunHeightFromLightDirection(skySun);
            float daylight = AtmosphereLutMath.DaylightFromSunHeight(sunHeight);
            Vector3 towardSun = skySun.LengthSquared() > 1e-6f ? -Vector3.Normalize(skySun) : Vector3.UnitY;

            // AF2.5/AF2.6: pack night / phase / moon from LightDirection + Mesh3DState climate.
            float dayOfYear = SkyAuthoringDefaults.ClampDayOfYear(
                _state.DayOfYear > 0f ? _state.DayOfYear : CelestialExtrasMath.DefaultDayOfYear);
            float nightFactor = CelestialExtrasMath.NightFactorFromLightDirection(skySun);
            float sidereal = CelestialExtrasMath.SiderealAngle(
                _state.AuthoredSkyEnabled ? _state.SkyTimeOfDayHours * 3600f : _time, dayOfYear);
            float latitudeRad = CelestialExtrasMath.LatitudeRadiansFromDegrees(
                SkyAuthoringDefaults.ClampLatitude(_state.LatitudeDegrees));
            float moonPhase = CelestialExtrasMath.MoonPhaseFromDayOfYear(dayOfYear);
            // Hours from elapsed renderer time (wrap to 0..24) for the lunar orbit offset.
            float hours = _state.AuthoredSkyEnabled ? _state.SkyTimeOfDayHours : (_time / 3600f) % 24f;
            if (hours < 0f) hours += 24f;
            Vector3 towardMoon = CelestialExtrasMath.MoonDirectionToward(
                skySun, dayOfYear, hours);

            var fogPost = new FogPostCB
            {
                ClipPlanes              = new Vector4(nearPlane, farPlane, width, height),
                CameraPosPad            = new Vector4(_cameraPos, skySeconds),
                InvViewProjection       = invVp,
                LightViewProjection     = lightVPFar,
                LightViewProjectionNear = lightVPNear,
                LightViewProjectionMid  = lightVPMid,
                AoParams                = new Vector4(aoEnabled ? 1f : 0f, 1f, 0f, 0f),
                ContactParams           = new Vector4(
                    contactEnabled ? 1f : 0f,
                    ContactShadowMath.EffectiveStrength(aoEnabled),
                    0f,
                    0f),
                LocalVolParams          = new Vector4(
                    localVolEnabled ? 1f : 0f,
                    LocalVolumetricMath.DefaultStrength,
                    0f,
                    0f),
                BloomParams             = new Vector4(
                    bloomEnabled ? 1f : 0f,
                    _state.BloomIntensity > 0f
                        ? _state.BloomIntensity
                        : BloomGradingMath.DefaultBloomIntensity,
                    0f,
                    0f),
                ExposureParams          = new Vector4(
                    _state.Exposure,
                    _state.Contrast,
                    _state.Saturation,
                    LinearPipeline ? 1f : 0f), // w: encode the tonemapped result to sRGB
                VignetteParams          = new Vector4(_state.VignetteStrength, 0f, 0f, 0f),
                AtmosphereLutParams     = new Vector4(
                    atmosphereLutEnabled ? 1f : 0f,
                    sunHeight,
                    daylight,
                    0f),
                CloudCompositeParams    = new Vector4(
                    cloudsEnabled ? 1f : 0f,
                    _state.AuthoredSkyEnabled ? 1f : RaymarchedCloudsMath.DefaultIntensity,
                    _state.AuthoredSkyEnabled ? 1f : 0f,
                    0f),
                CelestialParams         = new Vector4(
                    celestialExtrasEnabled ? 1f : 0f,
                    nightFactor,
                    sidereal,
                    latitudeRad),
                MoonDirPhase            = new Vector4(towardMoon, moonPhase),
                CloudLayerParams        = new Vector4(_state.CloudBaseHeight,
                    _state.CloudBaseHeight + _state.CloudThickness,
                    _state.AuthoredSkyEnabled ? MathF.Max(.0001f, _state.FogDensity * .008f) : 0f, 0f),
                AuthoredSkyZenith       = new Vector4(ToLinearColor(_state.SkyZenithColor), _state.AuthoredSkyEnabled ? 1f : 0f),
                AuthoredSkyHorizon      = new Vector4(ToLinearColor(_state.SkyHorizonColor), 0f),
                AuthoredSkySun          = new Vector4(towardSun,
                    _state.AuthoredSkyEnabled && _state.ShowSunVisual && sunHeight > 0f ? 8f * daylight : 0f),
            };
            PackSmokeVolumes(ref fogPost);
            PackInkOutline(ref fogPost, height);
            if (atmosphereLutEnabled)
            {
                // Upload is once at create; sampling is free in the composite. Report a tiny
                // sentinel so F6 shows AL on rather than "off".
                LastAtmosphereLutMs = Math.Max(0.01, LastAtmosphereLutMs);
            }
            if (celestialExtrasEnabled)
            {
                // Same sentinel pattern as Atmosphere LUT — no separate GPU pass.
                LastCelestialExtrasMs = Math.Max(0.01, LastCelestialExtrasMs);
            }
            _gpu.SetViewport(0, 0, width, height);
            Vector3 bg = _state.BackgroundColor;
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[] { GpuAttachmentAction.Clear(bg.X, bg.Y, bg.Z, 1f) },
                HasDepth = false,
                DebugName = "Forward tonemap and fog composite",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_fogProgram);

            // Upload inside the pass so WebGPU snapshots the uniform buffer the same way as
            // EngineCB (QueueWriteBuffer before BeginRenderPass can race with later draws).
            UploadEngineCB();
            _gpu.UpdateConstantBuffer(_cbFogPost, fogPost);

            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbFogPost);
            BindFroxelApply();

            // Unbind main-pass textures before sampling scene/depth in the composite pass.
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 5);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, sceneTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, depthTexture);

            // Shadow maps for shadow-aware volumetric fog (light shafts). Bound unconditionally;
            // the shader treats sample points as fully lit when ShadowParams.x (shadows active)
            // is 0, so this is a no-op when shadows are off. Mid is unused by Fog SampleShadowAtPoint.
            _gpu.SetTexture(GpuShaderStage.Pixel, 2,
                _shadowsActiveThisFrame ? _shadowTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 3,
                _shadowsActiveThisFrame ? _shadowNearTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 4, _fogSkipTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 5,
                _shadowsActiveThisFrame && _cascade.CascadeCount >= 3
                    ? _shadowMidTexture
                    : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 6,
                aoEnabled && aoTexture.IsValid ? aoTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 7,
                contactEnabled && contactTexture.IsValid ? contactTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 8,
                localVolEnabled && localVolTexture.IsValid ? localVolTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 9,
                bloomEnabled && bloomTexture.IsValid ? bloomTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 10,
                atmosphereLutEnabled && _atmosphereLutTexture.IsValid
                    ? _atmosphereLutTexture
                    : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 11,
                cloudsEnabled && cloudTexture.IsValid ? cloudTexture : GpuTextureHandle.Invalid);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetSampler(GpuShaderStage.Pixel, 1, _shadowSampler);

            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);

            for (int slot = 0; slot <= 11; slot++)
                _gpu.ClearTexture(GpuShaderStage.Pixel, slot);
            _gpu.EndRenderPass();
        }

        /// <summary>
        private bool ShouldRunGtao(bool canPost) =>
            _state.GtaoEnabled
            && canPost
            && _gtaoProgram.IsValid
            && _gtaoBlurProgram.IsValid
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        private bool ShouldRunBloom(bool canPost) =>
            _state.BloomEnabled
            && canPost
            && _bloomExtractProgram.IsValid
            && _bloomDownsampleProgram.IsValid
            && _bloomUpsampleProgram.IsValid
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        private bool ShouldRunAtmosphereLut(bool canPost) =>
            _state.AtmosphereLutEnabled
            && canPost
            && _atmosphereLutTexture.IsValid
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// AF2.5 sky-only celestial extras in FogPost. Software skip like Atmosphere LUT; never a
        /// separate pass and never touches the raymarched-cloud budget.
        /// </summary>
        private bool ShouldRunCelestialExtras(bool canPost) =>
            _state.CelestialExtrasEnabled
            && canPost
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        private bool ShouldRunRaymarchedClouds(bool canPost) =>
            _state.RaymarchedCloudsEnabled
            && canPost
            && _raymarchedCloudsProgram.IsValid
            && _weatherMapValidThisFrame
            && _weatherMapTexture.IsValid
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        private GpuTextureHandle BloomResultTexture()
        {
            // After upsample, mip 0 holds the composited pyramid; if upsample was skipped for a
            // degenerate size, fall back to the first downsample.
            if (_bloomUpTextures[0].IsValid)
                return _bloomUpTextures[0];
            return _bloomDownTextures[0];
        }

        private void BloomPass(GpuTextureHandle sceneTexture, int fullW, int fullH)
        {
            if (!sceneTexture.IsValid
                || !_bloomExtractProgram.IsValid
                || !_bloomDownsampleProgram.IsValid
                || !_bloomUpsampleProgram.IsValid)
            {
                LastBloomMs = 0;
                return;
            }

            var sw = Stopwatch.StartNew();
            EnsureBloomTargets(fullW, fullH);

            float threshold = _state.BloomThreshold > 0f
                ? _state.BloomThreshold
                : BloomGradingMath.DefaultBloomThreshold;

            // Mip 0: threshold extract from full-res HDR scene (pre-fog) into half-res.
            DrawBloomFullscreen(
                _bloomDownTargets[0], _bloomW[0], _bloomH[0],
                _bloomExtractProgram, sceneTexture, GpuTextureHandle.Invalid,
                sourceW: fullW, sourceH: fullH, threshold: threshold, addFine: false);

            // Further downsamples.
            for (int i = 1; i < BloomGradingMath.BloomMipCount; i++)
            {
                DrawBloomFullscreen(
                    _bloomDownTargets[i], _bloomW[i], _bloomH[i],
                    _bloomDownsampleProgram, _bloomDownTextures[i - 1], GpuTextureHandle.Invalid,
                    sourceW: _bloomW[i - 1], sourceH: _bloomH[i - 1],
                    threshold: 0f, addFine: false);
            }

            // Upsample coarse → fine, adding each downsample level's energy.
            GpuTextureHandle coarse = _bloomDownTextures[BloomGradingMath.BloomMipCount - 1];
            for (int i = BloomGradingMath.BloomMipCount - 2; i >= 0; i--)
            {
                DrawBloomFullscreen(
                    _bloomUpTargets[i], _bloomW[i], _bloomH[i],
                    _bloomUpsampleProgram, coarse, _bloomDownTextures[i],
                    sourceW: _bloomW[i + 1], sourceH: _bloomH[i + 1],
                    threshold: 0f, addFine: true);
                coarse = _bloomUpTextures[i];
            }

            LastBloomMs = sw.Elapsed.TotalMilliseconds;
        }

        private void DrawBloomFullscreen(
            GpuRenderTargetHandle target,
            int width,
            int height,
            GpuShaderProgramHandle program,
            GpuTextureHandle source,
            GpuTextureHandle fine,
            int sourceW,
            int sourceH,
            float threshold,
            bool addFine)
        {
            var cb = new BloomCB
            {
                Params = new Vector4(
                    1f / Math.Max(sourceW, 1),
                    1f / Math.Max(sourceH, 1),
                    threshold,
                    addFine ? 1f : 0f),
            };
            _gpu.UpdateConstantBuffer(_cbBloom, cb);
            _gpu.SetViewport(0, 0, width, height);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 1f) },
                HasDepth = false,
                DebugName = "Bloom pass",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(program);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbBloom);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, source);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, addFine && fine.IsValid ? fine : GpuTextureHandle.Invalid);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.EndRenderPass();
        }

        private void GtaoPass(GpuTextureHandle depthTexture, int fullW, int fullH)
        {
            if (!depthTexture.IsValid || !_gtaoProgram.IsValid || !_gtaoBlurProgram.IsValid)
            {
                LastAoMs = 0;
                return;
            }

            var sw = Stopwatch.StartNew();
            EnsureAoTargets(fullW, fullH);

            float nearPlane = _nearPlane;
            float farPlane = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            if (!Matrix4x4.Invert(_proj, out Matrix4x4 invProj))
                invProj = Matrix4x4.Identity;

            // Horizon/contact AO into half-res target.
            DrawGtaoFullscreen(
                _aoTarget, _aoW, _aoH, _gtaoProgram, depthTexture,
                GpuTextureHandle.Invalid, nearPlane, farPlane, fullW, fullH, invProj,
                horizontalBlur: false, clearToWhite: true);

            // Depth-aware bilateral blur: horizontal then vertical (ping-pong).
            DrawGtaoFullscreen(
                _aoBlurTarget, _aoW, _aoH, _gtaoBlurProgram, depthTexture,
                _aoTexture, nearPlane, farPlane, fullW, fullH, invProj,
                horizontalBlur: true, clearToWhite: false);
            DrawGtaoFullscreen(
                _aoTarget, _aoW, _aoH, _gtaoBlurProgram, depthTexture,
                _aoBlurTexture, nearPlane, farPlane, fullW, fullH, invProj,
                horizontalBlur: false, clearToWhite: false);

            sw.Stop();
            LastAoMs = sw.Elapsed.TotalMilliseconds;
        }

        private bool ShouldRunContactShadows(bool canPost) =>
            _state.ContactShadowsEnabled
            && canPost
            && _contactProgram.IsValid
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        private void ContactShadowPass(GpuTextureHandle depthTexture, int fullW, int fullH)
        {
            if (!depthTexture.IsValid || !_contactProgram.IsValid)
            {
                LastContactShadowMs = 0;
                return;
            }

            var sw = Stopwatch.StartNew();
            EnsureContactTargets(fullW, fullH);

            float nearPlane = _nearPlane;
            float farPlane = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            Matrix4x4 viewProj = _view * _proj;
            if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invVp))
                invVp = Matrix4x4.Identity;

            // _state.LightDirection is the direction light *travels*; the march has to follow the
            // direction toward the light (ForestLight's directionalL).
            Vector3 toLight = ContactShadowMath.DirectionTowardLight(_state.LightDirection);

            var cb = new ContactShadowCB
            {
                ClipPlanes = new Vector4(nearPlane, farPlane, fullW, fullH),
                InvViewProjection = invVp,
                ViewProjection = viewProj,
                LightDirPad = new Vector4(toLight, 0f),
                Params = new Vector4(
                    ContactShadowMath.DefaultMaxDistance,
                    ContactShadowMath.DefaultStrength,
                    _time,
                    0f),
            };
            _gpu.UpdateConstantBuffer(_cbContact, cb);
            _gpu.SetViewport(0, 0, _contactW, _contactH);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = _contactTarget,
                // Cleared to zero occlusion, so a pass that bails still composites as "no contact".
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 1f) },
                HasDepth = false,
                DebugName = "Contact shadows",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_contactProgram);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbContact);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, depthTexture);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.EndRenderPass();

            sw.Stop();
            LastContactShadowMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// AF1.6 gate. Software is skipped for the same reason GTAO / contact / local volumetrics
        /// are: the reference rasteriser has no business paying for an optional atmospheric term.
        /// </summary>
        private bool ShouldRunSmokeExtinction(bool canPost) =>
            _state.SmokeExtinctionEnabled
            && canPost
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Packs this frame's smoke spheres into the composite constant buffer. When the feature is
        /// off (or nothing submitted volumes) the params stay zeroed, so the shader's branch is not
        /// taken and the composite is bit-identical to a build without AF1.6.
        /// </summary>
        private void PackSmokeVolumes(ref FogPostCB fogPost)
        {
            if (!_smokeExtinctionActiveThisFrame || _smokeVolumeCount <= 0)
            {
                LastSmokeExtinctionMs = 0;
                return;
            }

            var sw = Stopwatch.StartNew();
            int count = Math.Min(_smokeVolumeCount, SmokeExtinctionMath.MaxVolumes);
            fogPost.SmokeParams = new Vector4(
                1f,
                SmokeExtinctionMath.DefaultExtinctionCoefficient,
                count,
                0f);

            for (int i = 0; i < count; i++)
            {
                SmokeExtinctionMath.SmokeVolume v = _smokeVolumes[i];
                var posRadius = new Vector4(v.Position, v.Radius);
                var density = new Vector4(v.Density, 0f, 0f, 0f);
                switch (i)
                {
                    case 0: fogPost.Smoke0PosRadius = posRadius; fogPost.Smoke0Density = density; break;
                    case 1: fogPost.Smoke1PosRadius = posRadius; fogPost.Smoke1Density = density; break;
                    case 2: fogPost.Smoke2PosRadius = posRadius; fogPost.Smoke2Density = density; break;
                    case 3: fogPost.Smoke3PosRadius = posRadius; fogPost.Smoke3Density = density; break;
                    case 4: fogPost.Smoke4PosRadius = posRadius; fogPost.Smoke4Density = density; break;
                    case 5: fogPost.Smoke5PosRadius = posRadius; fogPost.Smoke5Density = density; break;
                    case 6: fogPost.Smoke6PosRadius = posRadius; fogPost.Smoke6Density = density; break;
                    default: fogPost.Smoke7PosRadius = posRadius; fogPost.Smoke7Density = density; break;
                }
            }

            sw.Stop();
            LastSmokeExtinctionMs = sw.Elapsed.TotalMilliseconds;
        }

        private bool ShouldRunLocalVolumetrics(bool canPost) =>
            _state.LocalVolumetricsEnabled
            && canPost
            && _localVolProgram.IsValid
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// AF1.5 bounded local-light scatter. Runs in full every frame it runs at all — the budget
        /// is the cost control, never a 1-in-4 frame schedule. Returns false when there is nothing
        /// to scatter, so the composite can skip binding the map entirely.
        /// </summary>
        private bool LocalVolumetricPass(GpuTextureHandle depthTexture, int fullW, int fullH)
        {
            LastLocalVolumetricMs = 0;
            if (!depthTexture.IsValid || !_localVolProgram.IsValid || _pointLightCount <= 0)
                return false;

            int budget = LocalVolumetricMath.ClampBudget(
                RenderCapacityDefaults.LocalVolumetricLightBudget);
            if (budget <= 0)
                return false;

            // Same strongest-N ranking AF1.3 uses for the omnidirectional cubemap, so the light that gets
            // a shadow map is the light that gets a beam.
            Span<int> slots = stackalloc int[LocalVolumetricMath.MaxBudget];
            int selected = LocalVolumetricMath.SelectLights(
                _pointLights.AsSpan(0, _pointLightCount), budget, _cameraPos, slots);
            if (selected <= 0)
                return false;

            var sw = Stopwatch.StartNew();
            EnsureLocalVolumetricTargets(fullW, fullH);

            float nearPlane = _nearPlane;
            float farPlane = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            Matrix4x4 viewProj = _view * _proj;
            if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invVp))
                invVp = Matrix4x4.Identity;

            var cb = new LocalVolumetricCB
            {
                ClipPlanes = new Vector4(nearPlane, farPlane, fullW, fullH),
                InvViewProjection = invVp,
                CameraPosPad = new Vector4(_cameraPos, 0f),
                Params = new Vector4(
                    LocalVolumetricMath.StepCount,
                    LocalVolumetricMath.DefaultStrength,
                    selected,
                    LocalVolumetricMath.DefaultMaxDistance),
            };

            for (int i = 0; i < selected; i++)
            {
                ref ClusterPointLightGpu light = ref _pointLights[slots[i]];
                switch (i)
                {
                    case 0:
                        cb.Light0PosRadius = light.PosRadius;
                        cb.Light0ColorIntensity = light.ColorIntensity;
                        break;
                    case 1:
                        cb.Light1PosRadius = light.PosRadius;
                        cb.Light1ColorIntensity = light.ColorIntensity;
                        break;
                    case 2:
                        cb.Light2PosRadius = light.PosRadius;
                        cb.Light2ColorIntensity = light.ColorIntensity;
                        break;
                    default:
                        cb.Light3PosRadius = light.PosRadius;
                        cb.Light3ColorIntensity = light.ColorIntensity;
                        break;
                }
            }

            _gpu.UpdateConstantBuffer(_cbLocalVolumetric, cb);
            _gpu.SetViewport(0, 0, _localVolW, _localVolH);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = _localVolTarget,
                // Cleared to zero inscatter, so a pass that bails composites as "no beams".
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 1f) },
                HasDepth = false,
                DebugName = "Local volumetric scatter",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_localVolProgram);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbLocalVolumetric);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, depthTexture);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.EndRenderPass();

            sw.Stop();
            LastLocalVolumetricMs = sw.Elapsed.TotalMilliseconds;
            return true;
        }

        /// <summary>
        /// AF2.3/AF2.4 cloud raymarch at the quality-ladder internal resolution. Runs in full
        /// every frame it runs at all — never skip 3-in-4. Optional temporal resolve + Cinematic
        /// bilateral upsample. Returns false when the weather map was not uploaded this frame.
        /// </summary>
        private bool RaymarchedCloudsPass(GpuTextureHandle depthTexture, int fullW, int fullH)
        {
            LastRaymarchedCloudsMs = 0;
            _cloudCompositeTexture = GpuTextureHandle.Invalid;
            if (!depthTexture.IsValid
                || !_raymarchedCloudsProgram.IsValid
                || !_weatherMapValidThisFrame
                || !_weatherMapTexture.IsValid)
            {
                return false;
            }

            var sw = Stopwatch.StartNew();
            EnsureRaymarchedCloudsTargets(fullW, fullH);

            float nearPlane = _nearPlane;
            float farPlane = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            Matrix4x4 viewProj = _view * _proj;
            if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invVp))
                invVp = Matrix4x4.Identity;

            // Large camera jumps invalidate history (teleport / cut).
            if (_prevCloudCameraPosValid
                && Vector3.Distance(_cameraPos, _prevCloudCameraPos) > CloudTemporalMath.CameraJumpMetres)
            {
                _cloudHistoryValid = false;
            }

            // LightDirection in Mesh3DState is "from sun toward scene"; GPU wants toward sun.
            Vector3 towardSun = -Vector3.Normalize(_state.AuthoredSkyEnabled ? _state.SkySunDirection : _state.LightDirection);
            if (towardSun.LengthSquared() < 1e-8f)
                towardSun = new Vector3(0f, 1f, 0f);

            var cb = new RaymarchedCloudsCB
            {
                ClipPlanes = new Vector4(nearPlane, farPlane, fullW, fullH),
                InvViewProjection = invVp,
                CameraPosPad = new Vector4(_cameraPos, 0f),
                LightDirPad = new Vector4(towardSun, _state.AuthoredSkyEnabled
                    ? AtmosphereLutMath.DaylightFromSunHeight(towardSun.Y) : 1f),
                CloudParams = SkyAuthoringDefaults.ResolveCloudParams(
                    _state, RaymarchedCloudsMath.DefaultStepCount),
                WeatherParams = new Vector4(
                    _weatherMapHalfExtent,
                    1f,
                    SkyAuthoringDefaults.ResolveCoverageScale(_state),
                    SkyAuthoringDefaults.ClampDensityScale(_state.CloudDensityScale)),
                WeatherOrigin = new Vector4(_weatherMapWorldCenter, _weatherMapW, _weatherMapH),
                CloudSunColor = new Vector4(ToLinearColor(_state.SunColor), _state.AuthoredSkyEnabled ? 1f : 0f),
                CloudAmbientColor = new Vector4(ToLinearColor(_state.SkyZenithColor), 0f),
            };

            _gpu.UpdateConstantBuffer(_cbRaymarchedClouds, cb);
            _gpu.SetViewport(0, 0, _raymarchedCloudsW, _raymarchedCloudsH);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = _raymarchedCloudsTarget,
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) },
                HasDepth = false,
                DebugName = "Raymarched clouds",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_raymarchedCloudsProgram);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbRaymarchedClouds);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, _weatherMapTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, depthTexture);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.EndRenderPass();

            GpuTextureHandle resolved = _raymarchedCloudsTexture;
            bool temporal = _state.CloudTemporalEnabled
                && _cloudTemporalProgram.IsValid
                && _cloudHistoryTargets[0].IsValid
                && _cbCloudTemporal.IsValid;

            if (temporal)
            {
                int write = _cloudHistoryWrite & 1;
                int read = write ^ 1;
                bool reset = !_cloudHistoryValid || !_cloudHistoryTextures[read].IsValid;
                var tcb = new CloudTemporalCB
                {
                    ClipPlanes = new Vector4(
                        nearPlane, farPlane, _raymarchedCloudsW, _raymarchedCloudsH),
                    InvViewProjection = invVp,
                    PrevViewProjection = reset ? viewProj : _prevCloudVP,
                    CameraPosPad = new Vector4(_cameraPos, 0f),
                    TemporalParams = new Vector4(
                        CloudTemporalMath.DefaultBaseBlend,
                        reset ? 1f : 0f,
                        CloudTemporalMath.DefaultMotionThreshold,
                        CloudTemporalMath.DefaultDepthThreshold),
                    UpsampleParams = Vector4.Zero,
                };
                _gpu.UpdateConstantBuffer(_cbCloudTemporal, tcb);
                _gpu.SetViewport(0, 0, _raymarchedCloudsW, _raymarchedCloudsH);
                _gpu.BeginRenderPass(new GpuRenderPassDesc
                {
                    Target = _cloudHistoryTargets[write],
                    ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) },
                    HasDepth = false,
                    DebugName = "Cloud temporal resolve",
                });
                _gpu.SetDepthState(_dssFogOff);
                _gpu.SetBlendState(_bsOpaque);
                _gpu.SetRasterState(_rsCullNone);
                _gpu.SetShaderProgram(_cloudTemporalProgram);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbCloudTemporal);
                _gpu.SetTexture(GpuShaderStage.Pixel, 0, _raymarchedCloudsTexture);
                _gpu.SetTexture(
                    GpuShaderStage.Pixel,
                    1,
                    reset ? _raymarchedCloudsTexture : _cloudHistoryTextures[read]);
                _gpu.SetTexture(GpuShaderStage.Pixel, 2, depthTexture);
                _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
                _gpu.SetSampler(GpuShaderStage.Pixel, 1, _pointClampSampler);
                _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
                _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
                _gpu.Draw(3);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
                _gpu.EndRenderPass();

                resolved = _cloudHistoryTextures[write];
                _cloudHistoryWrite = read;
                _cloudHistoryValid = true;
            }
            else
            {
                _cloudHistoryValid = false;
            }

            CloudQualityTier tier = CloudTemporalMath.ClampTier(_state.CloudQuality);
            if (tier == CloudQualityTier.Cinematic
                && _cloudBilateralProgram.IsValid
                && _cloudUpsampleTarget.IsValid
                && resolved.IsValid)
            {
                var ucb = new CloudTemporalCB
                {
                    ClipPlanes = new Vector4(nearPlane, farPlane, fullW, fullH),
                    InvViewProjection = invVp,
                    PrevViewProjection = viewProj,
                    CameraPosPad = new Vector4(_cameraPos, 0f),
                    TemporalParams = Vector4.Zero,
                    UpsampleParams = new Vector4(
                        1f / Math.Max(_raymarchedCloudsW, 1),
                        1f / Math.Max(_raymarchedCloudsH, 1),
                        1f,
                        0f),
                };
                _gpu.UpdateConstantBuffer(_cbCloudTemporal, ucb);
                _gpu.SetViewport(0, 0, fullW, fullH);
                _gpu.BeginRenderPass(new GpuRenderPassDesc
                {
                    Target = _cloudUpsampleTarget,
                    ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) },
                    HasDepth = false,
                    DebugName = "Cloud bilateral upsample",
                });
                _gpu.SetDepthState(_dssFogOff);
                _gpu.SetBlendState(_bsOpaque);
                _gpu.SetRasterState(_rsCullNone);
                _gpu.SetShaderProgram(_cloudBilateralProgram);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbCloudTemporal);
                _gpu.SetTexture(GpuShaderStage.Pixel, 0, resolved);
                _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
                _gpu.SetSampler(GpuShaderStage.Pixel, 1, _pointClampSampler);
                _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
                _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
                _gpu.Draw(3);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
                _gpu.EndRenderPass();
                _cloudCompositeTexture = _cloudUpsampleTexture;
            }
            else
            {
                _cloudCompositeTexture = resolved;
            }

            _prevCloudVP = viewProj;
            _prevCloudCameraPos = _cameraPos;
            _prevCloudCameraPosValid = true;

            sw.Stop();
            LastRaymarchedCloudsMs = sw.Elapsed.TotalMilliseconds;
            return _cloudCompositeTexture.IsValid;
        }

        private void DrawGtaoFullscreen(
            GpuRenderTargetHandle target,
            int width,
            int height,
            GpuShaderProgramHandle program,
            GpuTextureHandle depthTexture,
            GpuTextureHandle aoMap,
            float nearPlane,
            float farPlane,
            int fullW,
            int fullH,
            Matrix4x4 invProj,
            bool horizontalBlur,
            bool clearToWhite)
        {
            var cb = new GtaoCB
            {
                ClipPlanes = new Vector4(nearPlane, farPlane, fullW, fullH),
                InvProjection = invProj,
                Params = new Vector4(_time, horizontalBlur ? 1f : 0f, 0f, 0f),
            };
            _gpu.UpdateConstantBuffer(_cbGtao, cb);
            _gpu.SetViewport(0, 0, width, height);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[]
                {
                    clearToWhite
                        ? GpuAttachmentAction.Clear(1f, 1f, 1f, 1f)
                        : GpuAttachmentAction.Keep(),
                },
                HasDepth = false,
                DebugName = clearToWhite ? "GTAO" : "GTAO blur",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(program);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbGtao);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, depthTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, aoMap);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.EndRenderPass();
        }

        // ── CB upload helpers ─────────────────────────────────────────────────────

        private void UploadPerFrame(
            Matrix4x4 vp, Matrix4x4 lightVPFar, Matrix4x4 lightVPNear, Matrix4x4 lightVPMid)
        {
            var data = new PerFrameCB
            {
                ViewProjection          = vp,
                LightViewProjection     = lightVPFar,
                LightViewProjectionNear = lightVPNear,
                CameraPosTime           = new Vector4(_cameraPos, _time),
                LightViewProjectionMid  = lightVPMid,
            };
            _gpu.UpdateConstantBuffer(_cbPerFrame, data);
        }

        // ── Colour pipeline ─────────────────────────────────────────────────────
        // Authored colours (sun, ambient, fog, sky, lights, tints) are picked in display (sRGB)
        // space. Under the linear pipeline they are converted once here so lighting adds and blends
        // light linearly; the composite (or a direct pass itself) encodes the result back to sRGB.
        private static bool LinearPipeline => Genesis.Rendering.Core.EngineRenderingDefaults.LinearColorPipeline;
        private static Vector3 ToLinearColor(Vector3 color) =>
            LinearPipeline ? Genesis.Rendering.Textures.SrgbColor.ToLinear(color) : color;
        private static Vector4 ToLinearColor(Vector4 color) =>
            LinearPipeline ? Genesis.Rendering.Textures.SrgbColor.ToLinear(color) : color;

        /// <summary>True while a pass writes straight to a display-referred target without the composite.</summary>
        private bool _encodeOutputThisPass;

        private void UploadEngineCB()
        {
            var   s          = _state;
            float shadowTexel     = 1f / ShadowMapSize;
            float lightingW  = s.LightingWeight > 0f ? s.LightingWeight : (s.LightingEnabled ? 1f : 0f);
            float cascadeMode = !_shadowsActiveThisFrame
                ? 0f
                : (_cascade.CascadeCount >= 3 ? 2f : 1f);

            PrepareClusteredLights(s.LightingEnabled);
            Span<PointLightData> cbLights = stackalloc PointLightData[RenderCapacityDefaults.EngineCbPointLightSlots];
            int cbCount = Math.Min(_clusterLightUploadCount, RenderCapacityDefaults.EngineCbPointLightSlots);
            for (int i = 0; i < cbCount; i++)
            {
                ref ClusterPointLightGpu src = ref _pointLights[i];
                cbLights[i] = new PointLightData
                {
                    PosRadius = src.PosRadius,
                    ColorIntensity = src.ColorIntensity,
                    FalloffPad = src.FalloffPad,
                };
            }

            var volumes = _fogVolumes;
            var data = new EngineCB
            {
                LightDirEnabled   = new Vector4(s.LightDirection, lightingW),
                FogParams         = new Vector4(s.FogEnabled ? 1 : 0, s.FogStart, s.FogEnd, s.FogDensity),
                FogColor          = ToLinearColor(s.FogColor),
                AmbientColor      = new Vector4(ToLinearColor(s.AmbientColor), 1),
                AmbientGroundColor = new Vector4(ToLinearColor(s.AmbientGroundColor), 1),
                SunColorIntensity = new Vector4(ToLinearColor(s.SunColor), s.SunIntensity),
                FogParams2        = new Vector4(s.FogHeightBase, s.FogHeightFalloff, s.FogAerialBlend, s.FogSunPreserve),
                ShadowParams      = new Vector4(
                    _shadowsActiveThisFrame ? 1 : 0,
                    _cascade.FarDepthBias > 0f ? _cascade.FarDepthBias : s.ShadowBias,
                    shadowTexel,
                    s.ShadowHighQuality ? 1f : 0f),
                EffectParams = new Vector4(
                    s.EmissiveIntensity,
                    (float)(int)s.DebugView,
                    _screenFogActiveThisFrame ? 1f : 0f,
                    s.FogNoiseStrength),
                VolumetricParams = new Vector4(
                    _runVolumetricThisFrame ? 1f : 0f,
                    Math.Clamp(s.VolumetricFogQuality, 0, 2),
                    // z: 0 = legacy gamma pipeline, 1 = linear (composite encodes), 2 = linear and
                    // this pass writes to a display target, so the shader encodes to sRGB itself.
                    LinearPipeline ? (_encodeOutputThisPass ? 2f : 1f) : 0f,
                    Math.Clamp(s.VolumetricTemporalBlend, 0f, 1f)),
                ViewportParams = new Vector4(
                    _rtWidth > 0 ? _rtWidth : 1,
                    _rtHeight > 0 ? _rtHeight : 1,
                    _rtWidth > 0 ? 1f / _rtWidth : 1f,
                    _rtHeight > 0 ? 1f / _rtHeight : 1f),
                ShadowCascadeParams = new Vector4(
                    _cascade.NearSplitDistance,
                    _cascade.CascadeCount >= 3 ? _cascade.MidSplitDistance : shadowTexel,
                    cascadeMode,
                    Math.Clamp(s.ShadowStrength, 0f, 1f)),
                // x=total clustered lights, y=tile grid, z=max per tile, w=legacy CB light count
                PointLightCounts = new Vector4(
                    s.LightingEnabled ? _clusterLightUploadCount : 0,
                    ClusteredLightDefaults.TileGridSize,
                    ClusteredLightDefaults.MaxLightsPerCluster,
                    cbCount),
                L0 = cbCount > 0 ? cbLights[0] : default,
                L1 = cbCount > 1 ? cbLights[1] : default,
                L2 = cbCount > 2 ? cbLights[2] : default,
                L3 = cbCount > 3 ? cbLights[3] : default,
                L4 = cbCount > 4 ? cbLights[4] : default,
                L5 = cbCount > 5 ? cbLights[5] : default,
                L6 = cbCount > 6 ? cbLights[6] : default,
                L7 = cbCount > 7 ? cbLights[7] : default,
                FogVolumeCounts = new Vector4(Math.Min(_fogVolumeCount, 8), 0, 0, 0),
                V0 = _fogVolumeCount > 0 ? volumes[0] : default,
                V1 = _fogVolumeCount > 1 ? volumes[1] : default,
                V2 = _fogVolumeCount > 2 ? volumes[2] : default,
                V3 = _fogVolumeCount > 3 ? volumes[3] : default,
                V4 = _fogVolumeCount > 4 ? volumes[4] : default,
                V5 = _fogVolumeCount > 5 ? volumes[5] : default,
                V6 = _fogVolumeCount > 6 ? volumes[6] : default,
                V7 = _fogVolumeCount > 7 ? volumes[7] : default,
                StylizedParams = new Vector4(
                    s.StylizedLightingEnabled ? 1f : 0f,
                    Math.Max(s.StylizedToonSteps, 1f),
                    Math.Clamp(s.StylizedDiffuseWrap, 0f, 1f),
                    Math.Max(s.StylizedSaturation, 0.5f)),
                StylizedParams2 = new Vector4(
                    Math.Max(s.StylizedSpecularStrength, 0f),
                    Math.Max(s.StylizedRimStrength, 0f),
                    0f, 0f),
                WeatherWindRain = s.WeatherWindRain,
                WeatherSurface = s.WeatherSurface,
            };
            _gpu.UpdateConstantBuffer(_cbEngine, data);
        }

        private long _submitFrameId;
        private long _lightGridFrameId = -1;
        private Matrix4x4 _lightGridViewProj;
        private int _lightGridWidth, _lightGridHeight, _lightGridCount = -1;
        private bool _lightGridLit;

        private void PrepareClusteredLights(bool lightingEnabled)
        {
            // UploadEngineCB runs for the main pass, the post composite and any reflection pass.
            // The tile grid only depends on this frame's lights, view and target size, so rebuilding
            // and re-uploading it for the composite repeated identical CPU binning and two uploads.
            Matrix4x4 gridViewProj = _view * _proj;
            if (_lightGridFrameId == _submitFrameId
                && _lightGridLit == lightingEnabled
                && _lightGridCount == _pointLightCount
                && _lightGridWidth == _rtWidth && _lightGridHeight == _rtHeight
                && _lightGridViewProj == gridViewProj)
            {
                return;
            }
            _lightGridFrameId = _submitFrameId;
            _lightGridLit = lightingEnabled;
            _lightGridCount = _pointLightCount;
            _lightGridWidth = _rtWidth;
            _lightGridHeight = _rtHeight;
            _lightGridViewProj = gridViewProj;

            _clusterLightUploadCount = 0;
            _clusteredLights.Clear();
            if (!lightingEnabled || _pointLightCount <= 0)
            {
                UploadEmptyClusterBuffers();
                return;
            }

            // Over the shaded-light cap, keep the strongest lights by camera weight. Submission used
            // to stop at the cap, so the lights dropped were whichever were submitted last.
            int count = TiledLightGrid.SelectStrongest(
                _pointLights.AsSpan(0, _pointLightCount), _pointLightCount,
                RenderCapacityDefaults.EffectiveShadedLightCap, _cameraPos);
            _clusterLightUploadCount = count;

            // FalloffPad.Y/Z (local shadow slot and far plane) were stamped by LocalShadowPass and
            // travel with each light through the selection above.

            Matrix4x4 viewProj = _view * _proj;
            _clusteredLights.Build(
                _pointLights.AsSpan(0, count),
                viewProj,
                _rtWidth > 0 ? _rtWidth : 1,
                _rtHeight > 0 ? _rtHeight : 1,
                _cameraPos);

            UploadClusterBuffer(count);
            UploadTileIndexBuffer();
        }

        private void UploadEmptyClusterBuffers()
        {
            UploadClusterBuffer(0);
            _clusteredLights.Clear();
            UploadTileIndexBuffer();
        }

        private void UploadClusterBuffer(int count)
        {
            int bytes = Math.Max(1, count) * Marshal.SizeOf<ClusterPointLightGpu>();
            if (!_gpu.TryMapDiscard(_clusterLightBuf, out Span<byte> mapped, bytes))
                return;
            if (count > 0)
            {
                MemoryMarshal.AsBytes(_pointLights.AsSpan(0, count)).CopyTo(mapped);
            }
            _gpu.Unmap(_clusterLightBuf);
        }

        private void UploadTileIndexBuffer()
        {
            ReadOnlySpan<uint> indices = _clusteredLights.Buffer;
            int bytes = indices.Length * sizeof(uint);
            if (!_gpu.TryMapDiscard(_tileLightIndexBuf, out Span<byte> mapped, bytes))
                return;
            MemoryMarshal.AsBytes(indices).CopyTo(mapped);
            _gpu.Unmap(_tileLightIndexBuf);
        }

        private void UploadDrawCB(Matrix4x4 world, Vector4 color,
            float emissive, float unlit, float isFloor, float useInstancing, uint instOffset,
            bool noFog = false, bool noDepthWrite = false, bool terrainGround = false, bool foliage = false,
            Vector4 surfaceParams = default, Vector4 detailParams = default,
            Vector4 subsurfaceColorSteps = default, Vector4 materialFeatures = default,
            bool gpuSkinning = false, uint skinMatrixOffset = 0,
            bool noReceiveShadow = false)
        {
            // _cbDraw is Dynamic — use Map(WriteDiscard) which never stalls the GPU pipeline.
            var data = new DrawCB
            {
                WorldMatrix       = world,
                MaterialColor     = color,
                MaterialParams    = new Vector4(emissive, unlit, isFloor, useInstancing),
                InstanceOffset    = instOffset,
                NoFogFlag         = noFog ? 1f : 0f,
                NoDepthWriteFlag  = noDepthWrite ? 1f : 0f,
                TerrainGroundFlag = terrainGround ? 1f : 0f,
                FoliageFlag       = foliage ? 1f : 0f,
                SurfaceParams     = surfaceParams,
                DetailParams      = detailParams,
                SubsurfaceColorSteps = subsurfaceColorSteps,
                MaterialFeatures  = materialFeatures,
                SkinMatrixOffset   = skinMatrixOffset,
                GpuSkinningFlag    = gpuSkinning ? 1f : 0f,
                NoReceiveShadowFlag = noReceiveShadow ? 1f : 0f,
            };
            _gpu.UpdateConstantBuffer(_cbDraw, data);
        }

        private Matrix4x4 GetFloorWorldMatrix()
        {
            if (!_state.FloorFollowsCamera)
                return Matrix4x4.Identity;
            return Matrix4x4.CreateTranslation(_cameraPos.X, 0f, _cameraPos.Z);
        }

        private void DrawEnvironmentFloor(GpuTextureHandle whiteTexture)
        {
            _gpu.SetRasterState(_state.Wireframe ? _rsWireframe : _rsCullNone);
            _gpu.SetDepthState(_dssDefault);
            _gpu.SetBlendState(_bsOpaque);

            // Builtin floor mesh is already 500×500 (BuildFloor). Follow camera XZ for runtime
            // infinite grids; model editor pins the floor at world origin so panning does not
            // slide geometry relative to the ground.
            var world = GetFloorWorldMatrix();

            if (!TryGetMesh(FloorMesh.Id, out MeshEntry mesh))
                return;
            SetMeshBuffers(ref mesh);

            _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                _checkerTexture.IsValid ? _checkerTexture : whiteTexture);

            var floorColor = new Vector4(_state.FloorColor, 1f);
            UploadDrawCB(world, floorColor, emissive: 0f, unlit: 0f, isFloor: 1f, useInstancing: 0, instOffset: 0);
            _gpu.DrawIndexed(mesh.IndexCount);
            LastDrawCalls++;
            LastTriangles += mesh.IndexCount / 3;
        }

        private void DrawEnvironmentSun(GpuTextureHandle whiteTexture)
        {
            Vector3 lightDir = _state.AuthoredSkyEnabled ? _state.SkySunDirection : _state.LightDirection;
            if (lightDir.LengthSquared() < 1e-6f)
                lightDir = Mesh3DState.GetDefaultSunDirection();

            // LightDirection is sun → scene; negative Y means sun is above the horizon.
            if (lightDir.Y > -0.02f)
                return;

            // Keep inside the camera far plane — at 450 with far=400 the quad is clipped when centered.
            float sunDistance = MathF.Min(450f, MathF.Max(_state.CameraFarPlane * 0.92f, 80f));
            Vector3 sunPos = _cameraPos - Vector3.Normalize(lightDir) * sunDistance;
            Vector3 forward = _state.CameraForward;
            if (forward.LengthSquared() < 1e-6f)
                forward = -Vector3.Normalize(lightDir);

            float sunSize = 14f * (sunDistance / 450f);
            var world = Matrix4x4.CreateScale(sunSize)
                * Matrix4x4.CreateBillboard(sunPos, _cameraPos, Vector3.UnitY, forward);

            _gpu.SetDepthState(_dssNoWrite);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetBlendState(_bsAdd);

            if (!TryGetMesh(SunMesh.Id, out MeshEntry mesh))
                return;
            SetMeshBuffers(ref mesh);

            _gpu.SetTexture(GpuShaderStage.Pixel, 1,
                _sunTexture.IsValid ? _sunTexture : whiteTexture);

            var sunColor = new Vector4(_state.SunColor, 1f);
            // This draw binds _dssNoWrite just above, so — same rule as every other call site in
            // this file — NoDepthWriteFlag must be set too: the sun billboard genuinely never
            // writes scene depth, so the screen-space post fog's depth-buffer reconstruction can't
            // see it, and the forward pixel shader must self-apply ray-marched fog for it instead.
            UploadDrawCB(world, sunColor, emissive: _state.SunIntensity * 0.35f, unlit: 1f, isFloor: 0f, useInstancing: 0, instOffset: 0, noDepthWrite: true);
            _gpu.DrawIndexed(mesh.IndexCount);
            LastDrawCalls++;
            LastTriangles += mesh.IndexCount / 3;

            _gpu.SetRasterState(CurrentRasterizer());
            _gpu.SetDepthState(_dssDefault);
        }

        // ── Light view-projection ─────────────────────────────────────────────────

        private Matrix4x4 ComputeLightViewProj(ShadowCascadeKind cascade)
        {
            Vector3 lightDir = Vector3.Normalize(_state.LightDirection);
            float extent;
            float texelWorld;
            float forwardOffset;
            switch (cascade)
            {
                case ShadowCascadeKind.Near:
                    extent = _cascade.NearExtent;
                    texelWorld = _cascade.NearTexelWorld;
                    forwardOffset = extent * 0.35f;
                    break;
                case ShadowCascadeKind.Mid:
                    extent = _cascade.MidExtent;
                    texelWorld = _cascade.MidTexelWorld;
                    forwardOffset = extent * 0.38f;
                    break;
                default:
                    extent = _cascade.FarExtent;
                    texelWorld = _cascade.FarTexelWorld;
                    forwardOffset = extent * 0.36f;
                    break;
            }

            int mapSize = cascade switch
            {
                ShadowCascadeKind.Near => ShadowMapSizeNear,
                ShadowCascadeKind.Mid => ShadowMapSizeMid,
                _ => ShadowMapSize,
            };
            if (texelWorld <= 0f)
                texelWorld = (2f * extent) / Math.Max(64, mapSize);

            // ForestLight-style: bias the focus along horizontal camera forward, then snap in
            // light space so static receivers do not crawl (AF1.1).
            Vector3 cameraForward = _viewForward.LengthSquared() > 0.5f ? _viewForward : _state.CameraForward;
            Vector3 horizontalForward = new(cameraForward.X, 0f, cameraForward.Z);
            if (horizontalForward.LengthSquared() < 1e-6f)
                horizontalForward = -Vector3.UnitZ;
            else
                horizontalForward = Vector3.Normalize(horizontalForward);

            Vector3 focus = _cameraPos + horizontalForward * forwardOffset;
            focus = CascadeShadowMath.SnapCascadeCentre(focus, lightDir, texelWorld);

            Vector3 lightEye = focus - lightDir * extent;
            Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.95f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            Matrix4x4 lightView = D3dMatrixHelper.CreateLookAtLh(lightEye, focus, up);
            Matrix4x4 lightProj = D3dMatrixHelper.CreateOrthographicLh(extent, extent, 0.5f, extent * 4f);
            return lightView * lightProj;
        }

        // ── Mesh buffer bind ──────────────────────────────────────────────────────

        private void SetMeshBuffers(ref MeshEntry m)
        {
            int stride = m.VertexStride > 0 ? m.VertexStride : Marshal.SizeOf<MeshVertex>();
            _gpu.SetVertexBuffer(0, m.VB, stride);
            _gpu.SetIndexBuffer(m.IB, GpuIndexFormat.UInt16);
        }

        private bool IsMeshValid(int id) => TryGetMesh(id, out _);

        private bool TryGetMesh(int id, out MeshEntry mesh)
        {
            if (id <= 0 || id > _meshes.Count)
            {
                mesh = default;
                return false;
            }

            mesh = _meshes[id - 1];
            return !mesh.IsReleased && mesh.IndexCount > 0 && mesh.VB.IsValid && mesh.IB.IsValid;
        }

        private bool TryGetSkinPalette(int id, out SkinPaletteEntry palette)
        {
            if (id <= 0 || id > _skinPalettes.Count)
            {
                palette = default;
                return false;
            }

            palette = _skinPalettes[id - 1];
            return !palette.IsReleased && palette.MatrixCount > 0 && palette.Buffer.IsValid;
        }

        // ── Accumulator clear ─────────────────────────────────────────────────────

        private Batch RentBatch()
        {
            Batch batch = _batchPool.Count > 0 ? _batchPool.Pop() : new Batch();
            batch.SubmissionOrder = 0;
            batch.MeshId = 0;
            batch.TextureId = 0;
            batch.Texture = default;
            batch.Normal = default;
            batch.Orm = default;
            batch.Height = default;
            batch.Emission = default;
            batch.Extras = default;
            batch.Flow = default;
            batch.HeightMode = default;
            batch.SurfaceParams = default;
            batch.DetailParams = default;
            batch.SubsurfaceColorSteps = default;
            batch.Emissive = 0;
            batch.NoFog = false;
            batch.NoReceiveShadow = false;
            batch.TerrainGround = false;
            batch.RasterOverride = default;
            batch.Foliage = false;
            batch.IsFloor = false;
            batch.Shader = default;
            batch.ShaderParams0 = default;
            batch.ShaderParams1 = default;
            batch.ShaderParams2 = default;
            batch.ShaderParams3 = default;
            batch.AuthoredTextures = default;
            batch.Instances.Clear();
            return batch;
        }

        private SkinnedBatch RentSkinnedBatch()
        {
            SkinnedBatch batch = _skinnedBatchPool.Count > 0 ? _skinnedBatchPool.Pop() : new SkinnedBatch();
            batch.MeshId = 0;
            batch.TextureId = 0;
            batch.SkinPaletteId = 0;
            batch.Texture = default;
            batch.Emissive = 0;
            batch.NoFog = false;
            batch.NoReceiveShadow = false;
            batch.RasterOverride = default;
            batch.Shader = default;
            batch.ShaderParams0 = default;
            batch.ShaderParams1 = default;
            batch.ShaderParams2 = default;
            batch.ShaderParams3 = default;
            batch.AuthoredTextures = default;
            batch.Instances.Clear();
            return batch;
        }

        private ShadowBatch RentShadowBatch()
        {
            ShadowBatch batch = _shadowBatchPool.Count > 0 ? _shadowBatchPool.Pop() : new ShadowBatch();
            batch.MeshId = 0;
            batch.RasterOverride = default;
            batch.Casters.Clear();
            batch.CasterBounds.Clear();
            batch.CasterHashes.Clear();
            batch.FarInstances.Clear();
            batch.MidInstances.Clear();
            batch.NearInstances.Clear();
            batch.OmniInstances.Clear();
            return batch;
        }

        private TransBatch RentTransBatch()
        {
            TransBatch batch = _transBatchPool.Count > 0 ? _transBatchPool.Pop() : new TransBatch();
            batch.MeshId = 0;
            batch.TextureId = 0;
            batch.Texture = default;
            batch.Normal = default;
            batch.BlendMode = 0;
            batch.Emissive = 0;
            batch.NoFog = false;
            batch.Instances.Clear();
            return batch;
        }

        private void ClearAccumulators()
        {
            _submittedSkinPalettes.Clear();
            foreach (var b in _batchList)
            {
                b.Instances.Clear();
                _batchPool.Push(b);
            }
            _batches.Clear();
            _batchList.Clear();
            foreach (var b in _skinnedBatchList)
            {
                b.Instances.Clear();
                _skinnedBatchPool.Push(b);
            }
            _skinnedBatches.Clear();
            _skinnedBatchList.Clear();
            foreach (var b in _shadowBatchList)
            {
                b.Casters.Clear();
                b.CasterBounds.Clear();
                b.CasterHashes.Clear();
                b.FarInstances.Clear();
                b.MidInstances.Clear();
                b.NearInstances.Clear();
                b.OmniInstances.Clear();
                _shadowBatchPool.Push(b);
            }
            _shadowBatches.Clear();
            _shadowBatchList.Clear();
            _skinnedShadowCasters.Clear();
            _worldMeshes.Clear();
            _viewModelMeshes.Clear();
            _waterMeshes.Clear();
            foreach (var b in _transBatchList)
            {
                b.Instances.Clear();
                _transBatchPool.Push(b);
            }
            _transBatches.Clear();
            _transBatchList.Clear();
            _skinnedInstOffset = 0;
            _transInstOffset = 0;
            LastItemsSubmitted = 0;
            LastInstancesCulled = 0;
            LastInstancesDrawn = 0;
            LastBatchCount = 0;
            _foliageInstances = 0;
            _foliageBatches = 0;
        }

        // ── Dispose ───────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_reflectionTarget.IsValid) _gpu.ReleaseRenderTarget(_reflectionTarget);
            _gpu.ReleaseVertexLayout(_layout);
            _gpu.ReleaseVertexLayout(_layoutSkinned);
            _gpu.ReleaseShaderProgram(_program);
            _gpu.ReleaseShaderProgram(_skinnedProgram);
            _gpu.ReleaseShaderProgram(_shadowProgram);
            _gpu.ReleaseShaderProgram(_shadowTileClearProgram);
            _gpu.ReleaseShaderProgram(_shadowNearProgram);
            if (_shadowSkinnedProgram.IsValid) _gpu.ReleaseShaderProgram(_shadowSkinnedProgram);
            if (_shadowSkinnedMidProgram.IsValid) _gpu.ReleaseShaderProgram(_shadowSkinnedMidProgram);
            if (_shadowSkinnedNearProgram.IsValid) _gpu.ReleaseShaderProgram(_shadowSkinnedNearProgram);
            _gpu.ReleaseShaderProgram(_waterProgram);
            _gpu.ReleaseShaderProgram(_fogProgram);
            _gpu.ReleaseShaderProgram(_gtaoProgram);
            _gpu.ReleaseShaderProgram(_gtaoBlurProgram);
            _gpu.ReleaseShaderProgram(_contactProgram);
            _gpu.ReleaseShaderProgram(_localVolProgram);
            _gpu.ReleaseShaderProgram(_bloomExtractProgram);
            _gpu.ReleaseShaderProgram(_bloomDownsampleProgram);
            _gpu.ReleaseShaderProgram(_bloomUpsampleProgram);
            _gpu.ReleaseShaderProgram(_raymarchedCloudsProgram);
            _gpu.ReleaseShaderProgram(_cloudTemporalProgram);
            _gpu.ReleaseShaderProgram(_cloudBilateralProgram);
            _gpu.ReleaseShaderProgram(_overrideProgram);
            _gpu.ReleaseShaderProgram(_overrideSkinnedProgram);

            _gpu.ReleaseBuffer(_cbPerFrame);
            _gpu.ReleaseBuffer(_cbEngine);
            _gpu.ReleaseBuffer(_cbDraw);
            _gpu.ReleaseBuffer(_cbWater);
            _gpu.ReleaseBuffer(_cbFogPost);
            _gpu.ReleaseBuffer(_cbGtao);
            _gpu.ReleaseBuffer(_cbContact);
            _gpu.ReleaseBuffer(_cbLocalVolumetric);
            _gpu.ReleaseBuffer(_cbBloom);
            _gpu.ReleaseBuffer(_cbRaymarchedClouds);
            _gpu.ReleaseBuffer(_cbCloudTemporal);
            _gpu.ReleaseBuffer(_cbShaderParameters);
            _gpu.ReleaseBuffer(_cbOmni);
            _gpu.ReleaseBuffer(_instanceBuf);
            _gpu.ReleaseBuffer(_shadowInstanceBuf);
            _gpu.ReleaseBuffer(_clusterLightBuf);
            _gpu.ReleaseBuffer(_tileLightIndexBuf);

            _gpu.ReleaseRenderTarget(_sceneTarget);
            _gpu.ReleaseRenderTarget(_aoTarget);
            _gpu.ReleaseRenderTarget(_aoBlurTarget);
            _gpu.ReleaseRenderTarget(_contactTarget);
            _gpu.ReleaseRenderTarget(_localVolTarget);
            ReleaseRaymarchedCloudsTargets();
            ReleaseBloomTargets();
            _gpu.ReleaseRenderTarget(_shadowTarget);
            _gpu.ReleaseRenderTarget(_shadowMidTarget);
            _gpu.ReleaseRenderTarget(_shadowNearTarget);
            ReleaseLocalShadowAtlas();
            ReleaseFroxelResources();
            _gpu.ReleaseTexture(_flatNormalTexture);
            _gpu.ReleaseTexture(_checkerTexture);
            _gpu.ReleaseTexture(_waterNormalA);
            _gpu.ReleaseTexture(_waterNormalB);
            _gpu.ReleaseTexture(_sunTexture);
            _gpu.ReleaseTexture(_atmosphereLutTexture);
            _gpu.ReleaseTexture(_weatherMapTexture);
            _gpu.ReleaseSampler(_albedoSampler);
            _gpu.ReleaseSampler(_materialSampler);
            _gpu.ReleaseSampler(_shadowSampler);
            _gpu.ReleaseSampler(_linearSampler);
            _gpu.ReleaseSampler(_pointClampSampler);

            foreach (var m in _meshes)
            {
                _gpu.ReleaseBuffer(m.VB);
                _gpu.ReleaseBuffer(m.IB);
            }
            foreach (var p in _skinPalettes)
                _gpu.ReleaseBuffer(p.Buffer);
            _meshes.Clear();
            _freeMeshIds.Clear();
            _skinPalettes.Clear();
            _freeSkinPaletteIds.Clear();
        }
    }
}

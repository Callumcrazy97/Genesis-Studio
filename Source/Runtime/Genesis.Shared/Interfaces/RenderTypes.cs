using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Shared.Materials;

namespace Genesis.Shared.Interfaces
{
    // ── Value types shared between IRenderController and all callers ─────────

    public readonly struct RenderColor
    {
        public readonly float R, G, B, A;
        public RenderColor(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
        public static RenderColor Black   => new(0,    0,    0);
        public static RenderColor White   => new(1,    1,    1);
        public static RenderColor Clear   => new(0,    0,    0, 0);
        public static RenderColor FromArgb(byte r, byte g, byte b, byte a = 255)
            => new(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    public readonly struct TextureHandle
    {
        public readonly int Id;
        public TextureHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static TextureHandle Invalid => new(0);
    }

    /// <summary>Renderer-owned, backend-compiled authored pixel shader.</summary>
    public readonly struct RuntimeShaderHandle
    {
        public readonly int Id;
        public RuntimeShaderHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static RuntimeShaderHandle Invalid => new(0);
    }

    /// <summary>
    /// Extra Texture2D bindings declared by an authored shader (t1+ on sprites, non-PBR slots on
    /// meshes). Slot 0 with an invalid handle is unused.
    /// </summary>
    public struct AuthoredShaderTextures
    {
        public const int Capacity = 4;
        public int Count;
        public int Slot0, Slot1, Slot2, Slot3;
        public TextureHandle Handle0, Handle1, Handle2, Handle3;

        public void Add(int slot, TextureHandle handle)
        {
            if (!handle.IsValid || Count >= Capacity) return;
            switch (Count)
            {
                case 0: Slot0 = slot; Handle0 = handle; break;
                case 1: Slot1 = slot; Handle1 = handle; break;
                case 2: Slot2 = slot; Handle2 = handle; break;
                case 3: Slot3 = slot; Handle3 = handle; break;
            }

            Count++;
        }

        public bool SameBindings(in AuthoredShaderTextures other) =>
            Count == other.Count
            && Slot0 == other.Slot0 && Handle0.Id == other.Handle0.Id
            && Slot1 == other.Slot1 && Handle1.Id == other.Handle1.Id
            && Slot2 == other.Slot2 && Handle2.Id == other.Handle2.Id
            && Slot3 == other.Slot3 && Handle3.Id == other.Handle3.Id;
    }

    public readonly struct RenderTargetHandle
    {
        public readonly int Id;
        public RenderTargetHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static RenderTargetHandle Invalid => new(0);
    }

    // ── 3D mesh types ─────────────────────────────────────────────────────────

    // GPU vertex layout: Position(12) + Normal(12) + Color(16) + UV(8) = 48 bytes.
    // Matches EngineTest Vertex3D exactly — do not reorder or pad fields.
    [StructLayout(LayoutKind.Sequential)]
    public struct MeshVertex
    {
        public Vector3 Position;   // POSITION  offset  0, 12 bytes
        public Vector3 Normal;     // NORMAL    offset 12, 12 bytes
        public Vector4 Color;      // COLOR     offset 24, 16 bytes
        public Vector2 UV;         // TEXCOORD0 offset 40,  8 bytes
    }

    // GPU skinned vertex layout:
    // MeshVertex prefix + JointWeights(16) + JointIndices(16) = 80 bytes.
    // Joint indices are stored as floats so the DX11 input layout can feed them through
    // BLENDINDICES without integer-format edge cases across tooling.
    [StructLayout(LayoutKind.Sequential)]
    public struct SkinnedMeshVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Color;
        public Vector2 UV;
        public Vector4 JointWeights;
        public Vector4 JointIndices;
    }

    public readonly struct MeshHandle
    {
        public readonly int Id;
        public MeshHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static MeshHandle Invalid => new(0);
    }

    public readonly struct SkinPaletteHandle
    {
        public readonly int Id;
        public SkinPaletteHandle(int id) { Id = id; }
        public bool IsValid => Id > 0;
        public static SkinPaletteHandle Invalid => new(0);
    }

    /// <summary>
    /// One mesh part to merge via <see cref="IRenderController.RegisterCombinedMesh"/>.
    /// Each part is transformed before indices are appended.
    /// </summary>
    public readonly struct MeshCombinePart
    {
        public readonly MeshVertex[] Vertices;
        public readonly ushort[] Indices;
        public readonly Matrix4x4 Transform;

        public MeshCombinePart(MeshVertex[] vertices, ushort[] indices, Matrix4x4 transform)
        {
            Vertices = vertices;
            Indices = indices;
            Transform = transform;
        }

        public bool IsEmpty => Vertices == null || Vertices.Length == 0 || Indices == null || Indices.Length == 0;
    }

    public enum BuiltinMeshKind
    {
        Floor = 0,
        Sun   = 1,
        Cube  = 2,
        Sphere = 3,
    }

    public enum RenderDebugView
    {
        Shaded     = 0,
        Normals    = 1,
        SceneDepth = 2,
        ShadowMap  = 3,
    }

    // Per-draw flags controlling VS/PS path and depth state.
    [Flags]
    public enum MeshDrawFlags
    {
        None         = 0,
        IsFloor      = 1,   // PS applies checkerboard / floor lighting; opaque floors instance (R7.7)
        NoDepthWrite = 2,   // depth test on but no depth writes (sun billboard, transparents)
        Transparent  = 4,   // alpha blend pass; routed through non-instanced world mesh path
        Emissive     = 8,   // per-draw emissive boost (lights / glow)
        NoShadow     = 16,  // skip directional shadow pass
        Additive     = 32,  // additive blend (embers, sparks, portals, magic) — always no depth write
        Water        = 64,  // stylized water surface pass (depth tint, fresnel, foam)
        NoFog        = 128, // opt out of distance/screen-space fog (terrain-and-rendering-fix-plan.md Issue 6:
                             // per-material "receives fog" flag — e.g. skybox-anchored or UI-anchored meshes
                             // that shouldn't desaturate into fog colour at range)
        TerrainGround = 256, // PS replaces sampled albedo with the procedural slope/noise-driven
                              // grass+dirt-path blend (see ForwardShaders.cs TerrainAlbedo). Set
                              // automatically by SandboxTerrainGround's own draw call only — never a
                              // user-facing toggle. Acts as the placeholder a future terrain editor's
                              // painted splat map can override per the Terrain Editor Redesign doc.
        NoCull       = 512, // disables backface culling for this draw's instanced batch (rasterizer
                             // cull-none instead of cull-back). For genuinely thin single-layer
                             // geometry that must read as solid from both sides (e.g. a crossed-quad
                             // grass blade) instead of vanishing edge-on. Not a user-facing toggle —
                             // set automatically by whichever system builds that geometry (see
                             // SandboxTerrainGround.cs's grass draw calls). Solid closed meshes
                             // (pebbles, props, terrain) should never set this; they already render
                             // correctly with normal single-sided culling.
        NoDepthTest  = 1024, // skip depth test/write — view-model overlays (first-person held item).
        /// <summary>
        /// Alpha-cutout vegetation billboard: unlit albedo × tint, no Lambert darkening on
        /// sideways normals. Use with <see cref="NoCull"/> for crossed quads.
        /// </summary>
        Foliage      = 2048,
        /// <summary>Per-draw override: cull front-facing triangles.</summary>
        CullFront    = 4096,
        /// <summary>Per-draw override: cull back-facing triangles.</summary>
        CullBack     = 8192,
        /// <summary>Per-draw override: counter-clockwise triangles are front-facing.</summary>
        FrontCounterClockwise = 16384,
        /// <summary>Per-draw override: clockwise triangles are front-facing.</summary>
        FrontClockwise = 32768,
        /// <summary>Render normally but do not sample scene shadows on this draw.</summary>
        NoReceiveShadow = 65536,
        /// <summary>Editor reference geometry keeps its own material during shader preview.</summary>
        EditorReference = 131072,
        /// <summary>Multiply blend for soot, shadow wisps and stylised dark particles.</summary>
        Multiply     = 262144,
    }

    /// <summary>Culling stored on authored resources. Default inherits Preferences - Rendering.</summary>
    public enum FaceCullingOverride
    {
        Default,
        Back,
        Front,
        None,
    }

    /// <summary>Front-face winding stored on authored resources. Default inherits Preferences - Rendering.</summary>
    public enum FrontFaceWindingOverride
    {
        Default,
        Clockwise,
        CounterClockwise,
    }

    /// <summary>Installation-wide raster defaults used by editors and the runtime player.</summary>
    public static class MeshRasterDefaults
    {
        public const string CullingEnvironmentVariable = "GENESIS_FACE_CULLING";
        public const string WindingEnvironmentVariable = "GENESIS_FRONT_FACE_WINDING";

        private static FaceCullingOverride _culling = ParseCulling(
            Environment.GetEnvironmentVariable(CullingEnvironmentVariable),
            FaceCullingOverride.Back);
        private static FrontFaceWindingOverride _winding = ParseWinding(
            Environment.GetEnvironmentVariable(WindingEnvironmentVariable),
            FrontFaceWindingOverride.CounterClockwise);

        public static FaceCullingOverride Culling => _culling;
        public static FrontFaceWindingOverride Winding => _winding;

        public static void Configure(
            FaceCullingOverride culling,
            FrontFaceWindingOverride winding)
        {
            _culling = culling == FaceCullingOverride.Default ? FaceCullingOverride.Back : culling;
            _winding = winding == FrontFaceWindingOverride.Default
                ? FrontFaceWindingOverride.CounterClockwise
                : winding;
        }

        public static FaceCullingOverride ParseCulling(
            string value,
            FaceCullingOverride fallback = FaceCullingOverride.Default) =>
            Enum.TryParse(value, ignoreCase: true, out FaceCullingOverride parsed)
                ? parsed
                : fallback;

        public static FrontFaceWindingOverride ParseWinding(
            string value,
            FrontFaceWindingOverride fallback = FrontFaceWindingOverride.Default) =>
            Enum.TryParse(value, ignoreCase: true, out FrontFaceWindingOverride parsed)
                ? parsed
                : fallback;

        public static void Apply(ref Mesh3DState state)
        {
            state.CullBackFaces = _culling != FaceCullingOverride.None;
            state.CullFrontFaces = _culling == FaceCullingOverride.Front;
            state.FrontCounterClockwise = _winding == FrontFaceWindingOverride.CounterClockwise;
        }

        /// <summary>Replaces only authored raster override bits, preserving all material flags.</summary>
        public static MeshDrawFlags ApplyOverride(
            MeshDrawFlags flags,
            FaceCullingOverride culling,
            FrontFaceWindingOverride winding)
        {
            const MeshDrawFlags cullingMask = MeshDrawFlags.NoCull | MeshDrawFlags.CullFront
                | MeshDrawFlags.CullBack;
            const MeshDrawFlags windingMask = MeshDrawFlags.FrontCounterClockwise
                | MeshDrawFlags.FrontClockwise;
            if (culling != FaceCullingOverride.Default)
            {
                flags &= ~cullingMask;
                flags |= culling switch
                {
                    FaceCullingOverride.None => MeshDrawFlags.NoCull,
                    FaceCullingOverride.Front => MeshDrawFlags.CullFront,
                    FaceCullingOverride.Back => MeshDrawFlags.CullBack,
                    _ => MeshDrawFlags.None,
                };
            }
            if (winding != FrontFaceWindingOverride.Default)
            {
                flags &= ~windingMask;
                flags |= winding == FrontFaceWindingOverride.CounterClockwise
                    ? MeshDrawFlags.FrontCounterClockwise
                    : MeshDrawFlags.FrontClockwise;
            }
            return flags;
        }

        public static FaceCullingOverride Resolve(
            FaceCullingOverride primary,
            FaceCullingOverride fallback) =>
            primary == FaceCullingOverride.Default ? fallback : primary;

        public static FrontFaceWindingOverride Resolve(
            FrontFaceWindingOverride primary,
            FrontFaceWindingOverride fallback) =>
            primary == FrontFaceWindingOverride.Default ? fallback : primary;
    }

    /// <summary>
    /// Installation-wide lighting defaults shared by Studio previews and the Player process.
    /// A scene may further disable lighting or shadows, but cannot bypass these master switches.
    /// </summary>
    public static class MeshLightingDefaults
    {
        public const string LightingEnvironmentVariable = "GENESIS_LIGHTING_ENABLED";
        public const string ShadowsEnvironmentVariable = "GENESIS_SHADOWS_ENABLED";
        public const string ShadowStrengthEnvironmentVariable = "GENESIS_SHADOW_STRENGTH";
        public const string ShadowCascadeCountEnvironmentVariable = "GENESIS_SHADOW_CASCADE_COUNT";
        public const string GtaoEnabledEnvironmentVariable = "GENESIS_GTAO_ENABLED";
        public const string ContactShadowsEnabledEnvironmentVariable = "GENESIS_CONTACT_SHADOWS_ENABLED";
        public const string LocalVolumetricsEnabledEnvironmentVariable = "GENESIS_LOCAL_VOLUMETRICS_ENABLED";
        public const string SmokeExtinctionEnabledEnvironmentVariable = "GENESIS_SMOKE_EXTINCTION_ENABLED";
        public const string BloomEnabledEnvironmentVariable = "GENESIS_BLOOM_ENABLED";
        public const string AtmosphereLutEnabledEnvironmentVariable = "GENESIS_ATMOSPHERE_LUT_ENABLED";
        public const string RaymarchedCloudsEnabledEnvironmentVariable =
            "GENESIS_RAYMARCHED_CLOUDS_ENABLED";
        public const string CloudTemporalEnabledEnvironmentVariable =
            "GENESIS_CLOUD_TEMPORAL_ENABLED";
        public const string CelestialExtrasEnabledEnvironmentVariable =
            "GENESIS_CELESTIAL_EXTRAS_ENABLED";
        public const string CloudQualityEnvironmentVariable = "GENESIS_CLOUD_QUALITY";

        private static bool _lightingEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(LightingEnvironmentVariable), true);
        private static bool _shadowsEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(ShadowsEnvironmentVariable), true);
        private static float _shadowStrength = ParseStrength(
            Environment.GetEnvironmentVariable(ShadowStrengthEnvironmentVariable), 1f);
        private static int _shadowCascadeCount = ParseCascadeCount(
            Environment.GetEnvironmentVariable(ShadowCascadeCountEnvironmentVariable), 2);
        private static bool _gtaoEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(GtaoEnabledEnvironmentVariable), false);
        private static bool _contactShadowsEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(ContactShadowsEnabledEnvironmentVariable), false);
        private static bool _localVolumetricsEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(LocalVolumetricsEnabledEnvironmentVariable), false);
        private static bool _smokeExtinctionEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(SmokeExtinctionEnabledEnvironmentVariable), false);
        private static bool _bloomEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(BloomEnabledEnvironmentVariable), false);
        private static bool _atmosphereLutEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(AtmosphereLutEnabledEnvironmentVariable), false);
        private static bool _raymarchedCloudsEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(RaymarchedCloudsEnabledEnvironmentVariable), false);
        private static bool _cloudTemporalEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(CloudTemporalEnabledEnvironmentVariable), false);
        private static bool _celestialExtrasEnabled = ParseBoolean(
            Environment.GetEnvironmentVariable(CelestialExtrasEnabledEnvironmentVariable), false);
        private static int _cloudQuality = ParseCloudQuality(
            Environment.GetEnvironmentVariable(CloudQualityEnvironmentVariable), 2);
        private static float _exposure = 1f;
        private static float _contrast = 1f;
        private static float _saturation = 1f;
        private static float _vignetteStrength = 0f;
        private static float _bloomThreshold = 1f;
        private static float _bloomIntensity = 0.04f;

        public static bool LightingEnabled => _lightingEnabled;
        public static bool ShadowsEnabled => _shadowsEnabled;
        public static float ShadowStrength => _shadowStrength;
        public static int ShadowCascadeCount => _shadowCascadeCount;
        public static bool GtaoEnabled => _gtaoEnabled;
        /// <summary>AF1.4 optional half-res screen-space contact shadows (default off).</summary>
        public static bool ContactShadowsEnabled => _contactShadowsEnabled;
        /// <summary>AF1.5 optional bounded local-light volumetric scatter (default off).</summary>
        public static bool LocalVolumetricsEnabled => _localVolumetricsEnabled;
        /// <summary>AF1.6 optional particle smoke as a transmittance term (default off).</summary>
        public static bool SmokeExtinctionEnabled => _smokeExtinctionEnabled;
        /// <summary>AF1.7 optional HDR bloom pyramid (default off — goldens stay stable).</summary>
        public static bool BloomEnabled => _bloomEnabled;
        /// <summary>AF2.1 optional atmosphere LUT sky compose (default off — goldens stay stable).</summary>
        public static bool AtmosphereLutEnabled => _atmosphereLutEnabled;
        /// <summary>AF2.3 optional half-res raymarched clouds (default off — goldens stay stable).</summary>
        public static bool RaymarchedCloudsEnabled => _raymarchedCloudsEnabled;
        /// <summary>AF2.4 optional cloud temporal reprojection (default off — goldens stay stable).</summary>
        public static bool CloudTemporalEnabled => _cloudTemporalEnabled;
        /// <summary>
        /// AF2.5 optional FogPost sky-only celestial extras (stars / Milky Way / moon). Default off —
        /// goldens stay stable. Software skips; never touches the cloud march budget.
        /// </summary>
        public static bool CelestialExtrasEnabled => _celestialExtrasEnabled;
        /// <summary>
        /// AF2.4 cloud quality ladder 0..3 (Performance/Balanced/High/Cinematic). Default 2 = High
        /// (0.50 internal scale — same half-res as AF2.3).
        /// </summary>
        public static int CloudQuality => _cloudQuality;
        /// <summary>AF1.7 exposure scalar (default 1 = identity).</summary>
        public static float Exposure => _exposure;
        /// <summary>AF1.7 contrast (default 1 = identity).</summary>
        public static float Contrast => _contrast;
        /// <summary>AF1.7 saturation (default 1 = identity).</summary>
        public static float Saturation => _saturation;
        /// <summary>AF1.7 vignette strength (default 0 = off).</summary>
        public static float VignetteStrength => _vignetteStrength;
        /// <summary>AF1.7 bloom luma threshold (only matters when bloom is on).</summary>
        public static float BloomThreshold => _bloomThreshold;
        /// <summary>AF1.7 bloom add intensity (only matters when bloom is on).</summary>
        public static float BloomIntensity => _bloomIntensity;

        public static void Configure(
            bool lightingEnabled,
            bool shadowsEnabled,
            float shadowStrength,
            int shadowCascadeCount = 2,
            bool gtaoEnabled = false,
            bool contactShadowsEnabled = false,
            bool localVolumetricsEnabled = false,
            bool smokeExtinctionEnabled = false,
            bool bloomEnabled = false,
            float exposure = 1f,
            float contrast = 1f,
            float saturation = 1f,
            float vignetteStrength = 0f,
            float bloomThreshold = 1f,
            float bloomIntensity = 0.04f,
            bool atmosphereLutEnabled = false,
            bool raymarchedCloudsEnabled = false,
            bool cloudTemporalEnabled = false,
            int cloudQuality = 2,
            bool celestialExtrasEnabled = false)
        {
            _lightingEnabled = lightingEnabled;
            _shadowsEnabled = shadowsEnabled;
            _shadowStrength = Math.Clamp(shadowStrength, 0f, 1f);
            _shadowCascadeCount = shadowCascadeCount >= 3 ? 3 : 2;
            _gtaoEnabled = gtaoEnabled;
            _contactShadowsEnabled = contactShadowsEnabled;
            _localVolumetricsEnabled = localVolumetricsEnabled;
            _smokeExtinctionEnabled = smokeExtinctionEnabled;
            _bloomEnabled = bloomEnabled;
            _atmosphereLutEnabled = atmosphereLutEnabled;
            _raymarchedCloudsEnabled = raymarchedCloudsEnabled;
            _cloudTemporalEnabled = cloudTemporalEnabled;
            _cloudQuality = cloudQuality is >= 0 and <= 3 ? cloudQuality : 2;
            _celestialExtrasEnabled = celestialExtrasEnabled;
            _exposure = Math.Clamp(exposure, 0.05f, 8f);
            _contrast = Math.Clamp(contrast, 0.05f, 4f);
            _saturation = Math.Clamp(saturation, 0f, 4f);
            _vignetteStrength = Math.Clamp(vignetteStrength, 0f, 1f);
            _bloomThreshold = Math.Clamp(bloomThreshold, 0f, 16f);
            _bloomIntensity = Math.Clamp(bloomIntensity, 0f, 2f);
        }

        public static void Apply(ref Mesh3DState state)
        {
            state.LightingEnabled &= _lightingEnabled;
            state.LightingWeight = state.LightingEnabled
                ? (state.LightingWeight > 0f ? state.LightingWeight : 1f)
                : 0f;
            state.ShadowsEnabled &= _shadowsEnabled && state.LightingEnabled;
            state.ShadowStrength = Math.Clamp(state.ShadowStrength, 0f, 1f) * _shadowStrength;
            state.ShadowCascadeCount = state.ShadowCascadeCount >= 3 || _shadowCascadeCount >= 3
                ? 3
                : 2;
            // Installation master: prefs can force GTAO on; scene may also request it.
            state.GtaoEnabled = state.GtaoEnabled || _gtaoEnabled;
            state.ContactShadowsEnabled = state.ContactShadowsEnabled || _contactShadowsEnabled;
            state.LocalVolumetricsEnabled =
                state.LocalVolumetricsEnabled || _localVolumetricsEnabled;
            state.SmokeExtinctionEnabled =
                state.SmokeExtinctionEnabled || _smokeExtinctionEnabled;
            state.BloomEnabled = state.BloomEnabled || _bloomEnabled;
            state.AtmosphereLutEnabled = state.AtmosphereLutEnabled || _atmosphereLutEnabled;
            // Authored rooms own cloud visibility and quality. Legacy callers still inherit
            // installation defaults; enabling a global smoke-test switch must not undo a room edit.
            if (!state.AuthoredSkyEnabled)
            {
                state.RaymarchedCloudsEnabled =
                    state.RaymarchedCloudsEnabled || _raymarchedCloudsEnabled;
                state.CloudTemporalEnabled =
                    state.CloudTemporalEnabled || _cloudTemporalEnabled;
            }
            state.CelestialExtrasEnabled =
                state.CelestialExtrasEnabled || _celestialExtrasEnabled;
            // Installation quality wins when set; scene may already carry a tier.
            state.CloudQuality = !state.AuthoredSkyEnabled && _cloudQuality is >= 0 and <= 3
                ? _cloudQuality
                : (state.CloudQuality is >= 0 and <= 3 ? state.CloudQuality : 2);
            state.Exposure = _exposure;
            state.Contrast = _contrast;
            state.Saturation = _saturation;
            state.VignetteStrength = _vignetteStrength;
            state.BloomThreshold = _bloomThreshold;
            state.BloomIntensity = _bloomIntensity;
        }

        private static bool ParseBoolean(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (bool.TryParse(value, out bool parsed)) return parsed;
            return value.Trim() switch
            {
                "1" => true,
                "0" => false,
                _ => fallback,
            };
        }

        private static float ParseStrength(string value, float fallback) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? Math.Clamp(parsed, 0f, 1f)
                : fallback;

        private static int ParseCascadeCount(string value, int fallback)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return fallback >= 3 ? 3 : 2;
            return parsed >= 3 ? 3 : 2;
        }

        private static int ParseCloudQuality(string value, int fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback is >= 0 and <= 3 ? fallback : 2;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed is >= 0 and <= 3)
            {
                return parsed;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "performance" or "perf" or "p" => 0,
                "balanced" or "b" => 1,
                "high" or "h" => 2,
                "cinematic" or "cine" or "c" => 3,
                _ => fallback is >= 0 and <= 3 ? fallback : 2,
            };
        }
    }

    /// <summary>
    /// Installation-wide buffer caps shared by Studio previews and the Player (R7.2).
    /// Soft caps clamp what each flush uploads; GPU buffers stay sized to the hardware maxima.
    /// Local lights fill a structured buffer up to <see cref="SceneLocalLightCap"/>; each screen
    /// tile evaluates at most <see cref="MaxLightsPerTile"/> (R7.5).
    /// </summary>
    public static class RenderCapacityDefaults
    {
        public const string SpriteInstanceCapEnvironmentVariable = "GENESIS_SPRITE_INSTANCE_CAP";
        public const string MeshInstanceCapEnvironmentVariable = "GENESIS_MESH_INSTANCE_CAP";
        public const string SceneLocalLightCapEnvironmentVariable = "GENESIS_SCENE_LOCAL_LIGHT_CAP";
        public const string OmniShadowBudgetEnvironmentVariable = "GENESIS_OMNI_SHADOW_BUDGET";
        public const string LocalVolumetricLightBudgetEnvironmentVariable =
            "GENESIS_LOCAL_VOLUMETRIC_LIGHT_BUDGET";
        public const string DrawCallModeEnvironmentVariable = "GENESIS_DRAW_CALL_MODE";
        public const string WorldDrawBudgetEnvironmentVariable = "GENESIS_WORLD_DRAW_BUDGET";

        public const int HardwareSpriteInstanceCap = 32768;
        public const int HardwareMeshInstanceCap = 32768;
        /// <summary>Legacy EngineCB fog/Software slots that still carry the strongest eight lights.</summary>
        public const int EngineCbPointLightSlots = 8;
        /// <summary>Max light indices evaluated per screen tile (R7.5).</summary>
        public const int MaxLightsPerTile = 32;
        /// <summary>Screen tile grid edge length (16×16 = 256 tiles).</summary>
        public const int LightTileGridSize = 16;
        public const int MinInstanceCap = 256;
        public const int MinSceneLocalLightCap = 8;
        public const int MaxSceneLocalLightCap = 1000;
        public const int DefaultSceneLocalLightCap = 256;
        /// <summary>AF1.3: omnidirectional shadow-map slots (independent of scene local light cap).</summary>
        public const int MinOmniShadowBudget = 0;
        public const int MaxOmniShadowBudget = 4;
        public const int DefaultOmniShadowBudget = 1;
        /// <summary>
        /// AF1.5: local lights that get a volumetric beam this frame, ranked by the same camera
        /// weight as the omni budget so the local light that gets a cubemap also gets
        /// a beam. Parallel to <see cref="DefaultOmniShadowBudget"/> and equally not one per torch.
        /// </summary>
        public const int MinLocalVolumetricLightBudget = 0;
        public const int MaxLocalVolumetricLightBudget = 4;
        public const int DefaultLocalVolumetricLightBudget = 1;
        public const int MinWorldDrawBudget = 1;
        public const int MaxWorldDrawBudget = 1000;
        public const string DrawCallModeAuto = "Auto";
        public const string DrawCallModeManual = "Manual";

        private static int _spriteInstanceCap = ParseInt(
            Environment.GetEnvironmentVariable(SpriteInstanceCapEnvironmentVariable),
            HardwareSpriteInstanceCap, MinInstanceCap, HardwareSpriteInstanceCap);
        private static int _meshInstanceCap = ParseInt(
            Environment.GetEnvironmentVariable(MeshInstanceCapEnvironmentVariable),
            HardwareMeshInstanceCap, MinInstanceCap, HardwareMeshInstanceCap);
        private static int _sceneLocalLightCap = ParseInt(
            Environment.GetEnvironmentVariable(SceneLocalLightCapEnvironmentVariable),
            DefaultSceneLocalLightCap, MinSceneLocalLightCap, MaxSceneLocalLightCap);
        private static int _omniShadowBudget = ParseInt(
            Environment.GetEnvironmentVariable(OmniShadowBudgetEnvironmentVariable),
            DefaultOmniShadowBudget, MinOmniShadowBudget, MaxOmniShadowBudget);
        private static int _localVolumetricLightBudget = ParseInt(
            Environment.GetEnvironmentVariable(LocalVolumetricLightBudgetEnvironmentVariable),
            DefaultLocalVolumetricLightBudget,
            MinLocalVolumetricLightBudget,
            MaxLocalVolumetricLightBudget);
        private static string _drawCallMode = ParseDrawCallMode(
            Environment.GetEnvironmentVariable(DrawCallModeEnvironmentVariable));
        private static int _worldDrawBudget = ParseInt(
            Environment.GetEnvironmentVariable(WorldDrawBudgetEnvironmentVariable),
            MaxWorldDrawBudget, MinWorldDrawBudget, MaxWorldDrawBudget);

        public static int SpriteInstanceCap => _spriteInstanceCap;
        public static int MeshInstanceCap => _meshInstanceCap;
        public static int SceneLocalLightCap => _sceneLocalLightCap;
        /// <summary>Lights accepted into the clustered buffer this frame (scene soft cap).</summary>
        public static int EffectiveShadedLightCap => _sceneLocalLightCap;
        /// <summary>AF1.3 omnidirectional shadow-map budget (not a map per local light).</summary>
        public static int OmniShadowBudget => _omniShadowBudget;
        /// <summary>AF1.5 local-light volumetric scatter light budget.</summary>
        public static int LocalVolumetricLightBudget => _localVolumetricLightBudget;
        public static string DrawCallMode => _drawCallMode;
        public static bool IsManualWorldDrawBudget =>
            string.Equals(_drawCallMode, DrawCallModeManual, StringComparison.OrdinalIgnoreCase);
        public static int WorldDrawBudget => _worldDrawBudget;
        /// <summary>Remaining variable world draws allowed this flush; unlimited when Auto.</summary>
        public static int EffectiveWorldDrawBudget =>
            IsManualWorldDrawBudget ? _worldDrawBudget : int.MaxValue;

        public static void Configure(
            int spriteInstanceCap,
            int meshInstanceCap,
            int sceneLocalLightCap,
            string drawCallMode,
            int worldDrawBudget,
            int omniShadowBudget = DefaultOmniShadowBudget,
            int localVolumetricLightBudget = DefaultLocalVolumetricLightBudget)
        {
            _spriteInstanceCap = Math.Clamp(spriteInstanceCap, MinInstanceCap, HardwareSpriteInstanceCap);
            _meshInstanceCap = Math.Clamp(meshInstanceCap, MinInstanceCap, HardwareMeshInstanceCap);
            _sceneLocalLightCap = Math.Clamp(
                sceneLocalLightCap, MinSceneLocalLightCap, MaxSceneLocalLightCap);
            _omniShadowBudget = Math.Clamp(
                omniShadowBudget, MinOmniShadowBudget, MaxOmniShadowBudget);
            _localVolumetricLightBudget = Math.Clamp(
                localVolumetricLightBudget,
                MinLocalVolumetricLightBudget,
                MaxLocalVolumetricLightBudget);
            _drawCallMode = ParseDrawCallMode(drawCallMode);
            _worldDrawBudget = Math.Clamp(worldDrawBudget, MinWorldDrawBudget, MaxWorldDrawBudget);
        }

        private static string ParseDrawCallMode(string value) =>
            string.Equals(value, DrawCallModeManual, StringComparison.OrdinalIgnoreCase)
                ? DrawCallModeManual
                : DrawCallModeAuto;

        private static int ParseInt(string value, int fallback, int min, int max)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return fallback;
            return Math.Clamp(parsed, min, max);
        }
    }

    // All Engine.* lighting/fog/cull settings that affect the 3D batch this frame.
    public struct Mesh3DState
    {
        /// <summary>World wind XZ (m/s), local rain 0..1, and an enabled flag.</summary>
        public Vector4 WeatherWindRain;
        /// <summary>Local wetness, temperature C, accumulated snow, reserved.</summary>
        public Vector4 WeatherSurface;
        public bool    LightingEnabled;
        public float   LightingWeight;
        /// Directional light direction: sunlight travel direction (from sun toward the scene).
        public Vector3 LightDirection;    // world-space, same convention as EngineTest GetSunLightDirection
        public Vector3 SunColor;
        public float   SunIntensity;
        /// Hemisphere ambient, "sky" term — applied where the surface normal points up.
        public Vector3 AmbientColor;
        /// Hemisphere ambient, "ground" term — applied where the surface normal points
        /// down. Keeps shadowed/downward faces from reading as pure black (Issue 4/5).
        public Vector3 AmbientGroundColor;
        public bool    FogEnabled;
        public float   FogStart;
        public float   FogEnd;
        public Vector4 FogColor;
        public float   EmissiveIntensity;
        public bool    FrustumCullingEnabled;
        public bool    CullBackFaces;
        public bool    CullFrontFaces;
        public bool    FrontCounterClockwise;
        public bool    ShadowsEnabled;
        public float   ShadowStrength;
        public float   ShadowBias;
        public float   ShadowOrthoSize;
        /// <summary>Directional cascade count: 2 (default, R7.11) or 3 (AF1.1 far envelope).</summary>
        public int     ShadowCascadeCount;
        /// <summary>AF1.2 optional half-res GTAO (default off).</summary>
        public bool    GtaoEnabled;
        /// <summary>
        /// AF1.4 optional short-range screen-space contact shadows (default off). Applied as a
        /// sun-gated multiply at composite, never as a second ambient darken over GTAO.
        /// </summary>
        public bool    ContactShadowsEnabled;
        /// <summary>AF1.5 optional half-res local-light volumetric scatter (default off).</summary>
        public bool    LocalVolumetricsEnabled;
        /// <summary>
        /// AF1.6 optional particle smoke as extinction (default off). Alpha-blended emitters only:
        /// additive fire and embers add light rather than absorbing it, so they never contribute.
        /// </summary>
        public bool    SmokeExtinctionEnabled;
        /// <summary>AF1.7 optional HDR bloom pyramid (default off).</summary>
        public bool    BloomEnabled;
        /// <summary>AF2.1 optional atmosphere LUT sky compose (default off).</summary>
        public bool    AtmosphereLutEnabled;
        /// <summary>Use the room climate clock and solar direction, independently of moon lighting.</summary>
        public bool    AuthoredSkyEnabled;
        public Vector3 SkyZenithColor;
        public Vector3 SkyHorizonColor;
        public Vector3 SkySunDirection;
        public float   SkyTimeOfDayHours;
        public float   SkyElapsedSeconds;
        /// <summary>AF2.3 optional half-res raymarched clouds (default off).</summary>
        public bool    RaymarchedCloudsEnabled;
        /// <summary>AF2.4 optional temporal reprojection for raymarched clouds (default off).</summary>
        public bool    CloudTemporalEnabled;
        /// <summary>
        /// AF2.5 optional FogPost sky-only celestial extras (stars / Milky Way / moon; default off).
        /// </summary>
        public bool    CelestialExtrasEnabled;
        /// <summary>
        /// AF2.4 cloud quality 0..3 (Performance/Balanced/High/Cinematic). Default 2 = High (0.50).
        /// </summary>
        public int     CloudQuality;
        /// <summary>AF2.6 calendar day-of-year for celestial extras (default 215).</summary>
        public float   DayOfYear;
        /// <summary>AF2.6 observer latitude in degrees for celestial extras (default 53.9).</summary>
        public float   LatitudeDegrees;
        /// <summary>AF2.6 raymarched cloud slab base height in metres (default 180).</summary>
        public float   CloudBaseHeight;
        /// <summary>AF2.6 raymarched cloud slab thickness in metres (default 85).</summary>
        public float   CloudThickness;
        /// <summary>AF2.6 weather-map coverage multiplier (default 1 = identity).</summary>
        public float   CloudCoverageScale;
        /// <summary>
        /// AF2.6 cloud density → raymarch intensity multiplier (default 1 = identity).
        /// </summary>
        public float   CloudDensityScale;
        /// <summary>AF1.7 exposure scalar before ACES (default 1 = identity).</summary>
        public float   Exposure;
        /// <summary>AF1.7 contrast around mid-grey (default 1 = identity).</summary>
        public float   Contrast;
        /// <summary>AF1.7 saturation (default 1 = identity).</summary>
        public float   Saturation;
        /// <summary>AF1.7 vignette strength 0..1 (default 0 = off).</summary>
        public float   VignetteStrength;
        /// <summary>AF1.7 bloom luma threshold (only matters when bloom is on).</summary>
        public float   BloomThreshold;
        /// <summary>AF1.7 bloom add intensity (only matters when bloom is on).</summary>
        public float   BloomIntensity;
        public float   FogDensity;
        public float   FogHeightBase;
        public float   FogHeightFalloff;
        public float   FogAerialBlend;
        public float   FogSunPreserve;
        public float   FogNoiseStrength;
        public bool    FogScreenSpace;
        public bool    VolumetricFogEnabled;
        /// 0=Low, 1=Medium, 2=High
        public int     VolumetricFogQuality;
        /// Higher values bias toward stable analytic fog (less temporal shimmer).
        public float   VolumetricTemporalBlend;
        public bool    ShadowHighQuality;
        public bool    Wireframe;
        public float   CameraFarPlane;
        public RenderDebugView DebugView;

        /// <summary>Infinite floor follows camera XZ (EngineTest DrawFloor).</summary>
        public bool    ShowFloor;
        /// <summary>When false the floor stays at world origin (model editor grounding).</summary>
        public bool    FloorFollowsCamera;
        public Vector3 FloorColor;
        /// <summary>Sun billboard in sky (EngineTest DrawSunBillboard).</summary>
        public bool    ShowSunVisual;
        public Vector3 CameraForward;
        public Vector3 BackgroundColor;

        // ── Stylized / toon lighting (global per frame) ───────────────────────────
        public bool  StylizedLightingEnabled;
        /// <summary>Cel bands (1 = smooth Half-Lambert, 3–5 = painterly steps).</summary>
        public float StylizedToonSteps;
        /// <summary>Diffuse wrap (0 = Lambert, 0.5 = Half-Lambert).</summary>
        public float StylizedDiffuseWrap;
        /// <summary>Specular intensity multiplier.</summary>
        public float StylizedSpecularStrength;
        /// <summary>Fresnel rim multiplier.</summary>
        public float StylizedRimStrength;
        /// <summary>Albedo saturation boost (1 = neutral).</summary>
        public float StylizedSaturation;

        public static Mesh3DState Default
        {
            get
            {
                Mesh3DState state = new Mesh3DState
                {
            LightingEnabled       = true,
            LightDirection        = GetDefaultSunDirection(),
            SunColor              = new Vector3(1f, 0.95f, 0.82f),
            SunIntensity          = 1.1f,
            AmbientColor          = SkyModel.Default.AmbientSky,
            AmbientGroundColor    = SkyModel.Default.AmbientGround,
            FogEnabled            = false,
            FogStart              = 35f,
            FogEnd                = 120f,
            FogColor              = SkyModel.Default.FogColor,
            EmissiveIntensity     = 0f,
            FrustumCullingEnabled = false,
            CullBackFaces         = true,
            CullFrontFaces        = false,
            FrontCounterClockwise = true,
            ShadowsEnabled        = true,
            ShadowStrength        = 1f,
            ShadowBias            = 0.0015f,
            ShadowOrthoSize       = 80f,
            ShadowCascadeCount    = 2,
            GtaoEnabled           = false,
            ContactShadowsEnabled = false,
            LocalVolumetricsEnabled = false,
            SmokeExtinctionEnabled = false,
            BloomEnabled          = false,
            AtmosphereLutEnabled  = false,
            RaymarchedCloudsEnabled = false,
            CloudTemporalEnabled  = false,
            CelestialExtrasEnabled = false,
            CloudQuality          = 2,
            DayOfYear             = SkyAuthoringDefaults.DefaultDayOfYear,
            LatitudeDegrees       = SkyAuthoringDefaults.DefaultLatitudeDegrees,
            CloudBaseHeight       = SkyAuthoringDefaults.DefaultCloudBaseHeight,
            CloudThickness        = SkyAuthoringDefaults.DefaultCloudThickness,
            CloudCoverageScale    = SkyAuthoringDefaults.DefaultCloudCoverageScale,
            CloudDensityScale     = SkyAuthoringDefaults.DefaultCloudDensityScale,
            Exposure              = 1f,
            Contrast              = 1f,
            Saturation            = 1f,
            VignetteStrength      = 0f,
            BloomThreshold        = 1f,
            BloomIntensity        = 0.04f,
            FogDensity            = 0.018f,
            FogHeightBase         = 0f,
            FogHeightFalloff      = 0.08f,
            FogAerialBlend        = 0.65f,
            FogSunPreserve        = 0.85f,
            FogNoiseStrength      = 0.12f,
            FogScreenSpace        = false,
            VolumetricFogEnabled  = false,
            VolumetricFogQuality  = 1,
            VolumetricTemporalBlend = 0.8f,
            ShadowHighQuality     = true,
            Wireframe             = false,
            DebugView             = RenderDebugView.Shaded,
            ShowFloor             = false,
            FloorFollowsCamera    = true,
            ShowSunVisual         = true,
            FloorColor            = new Vector3(0.32f, 0.34f, 0.38f),
            BackgroundColor       = new Vector3(SkyModel.Default.BackgroundColor.X, SkyModel.Default.BackgroundColor.Y, SkyModel.Default.BackgroundColor.Z),
                };
                MeshRasterDefaults.Apply(ref state);
                return state;
            }
        }

        public static Vector3 GetDefaultSunDirection()
        {
            float yaw   = 135f * (MathF.PI / 180f);
            float pitch = 42f  * (MathF.PI / 180f);
            return Vector3.Normalize(new Vector3(
                MathF.Cos(pitch) * MathF.Sin(yaw),
                -MathF.Sin(pitch),
                MathF.Cos(pitch) * MathF.Cos(yaw)));
        }
    }

    // ── Draw call structs ─────────────────────────────────────────────────────

    public struct SpriteDrawCall
    {
        /// <summary>Force smooth sampling for this call, without changing a pixel-art room's sampler.
        /// Default false preserves the inherited room/global sampling contract.</summary>
        public bool SmoothSampling;
        public TextureHandle Texture;
        public float X, Y;
        public float Width, Height;
        public float OriginX, OriginY;
        public float Rotation;    // degrees
        public float ScaleX, ScaleY;
        public float Alpha;
        public RenderColor Tint;
        public int   Depth;
        /// <summary>Atlas sub-rect (u0,v0,u1,v1). Zero means full texture (0,0,1,1).</summary>
        public Vector4 UvRect;
        /// <summary>Screen-space clipping x,y,width,height. Nonpositive width/height means no clip.
        /// Stored with the command so deferred batches cannot lose a room viewport's boundary.</summary>
        public Vector4 ClipRect;
        public RuntimeShaderHandle Shader;
        public Vector4 ShaderParams0, ShaderParams1, ShaderParams2, ShaderParams3;
        public AuthoredShaderTextures AuthoredTextures;
    }

    public struct MeshDrawCall
    {
        public MeshHandle    Mesh;
        public SkinPaletteHandle SkinPalette;
        /// <summary>When invalid, shadow pass uses <see cref="Mesh"/>.</summary>
        public MeshHandle    ShadowMesh;
        public TextureHandle Texture;    // Invalid → white 1×1 default at draw time
        public TextureHandle NormalMap;  // Invalid → flat (128,128,255) default
        public TextureHandle OrmMap;
        public TextureHandle HeightMap;
        public TextureHandle EmissionMap;
        public TextureHandle ExtrasMap;
        public TextureHandle FlowMap;
        public MaterialHeightMode HeightMode;
        public Vector4 SurfaceParams;          // normal, height, emission, clearcoat
        public Vector4 DetailParams;           // subsurface, flow speed, flow strength, UV scale
        public Vector4 SubsurfaceColorSteps;   // RGB tint, POM max steps
        public Matrix4x4     World;
        public RenderColor   Tint;
        public MeshDrawFlags Flags;
        public float         Alpha;       // 1 = opaque
        public float         Emissive;    // additive glow multiplier on base colour
        /// <summary>
        /// Optional chunk identifier for large-world frustum culling.
        /// Set via <see cref="IRenderController.SetChunkBounds"/> before use.
        /// 0 means no chunk association (per-instance frustum test runs instead).
        /// </summary>
        public int           ChunkId;
        /// <summary>Texture array atlas layer index (0 = use Texture instead).</summary>
        public float         AtlasLayer;
        /// <summary>Deep water tint when <see cref="Flags"/> includes <see cref="MeshDrawFlags.Water"/>.</summary>
        public RenderColor   DeepTint;
        /// <summary>Water shader params: X=flow speed, Y=fresnel, Z=foam width, W=wave amplitude.</summary>
        public Vector4       WaterParams;
        /// <summary>Fake sky horizon RGB + river flow scale in W.</summary>
        public Vector4       SkyHorizon;
        /// <summary>Fake sky zenith RGB.</summary>
        public Vector4       SkyZenith;
        public RuntimeShaderHandle Shader;
        public Vector4 ShaderParams0, ShaderParams1, ShaderParams2, ShaderParams3;
        public AuthoredShaderTextures AuthoredTextures;
    }

    /// <summary>
    /// Per-instance data for a renderer-owned mesh batch. Material, shader and render state live
    /// on the accompanying <see cref="MeshDrawCall"/> template, so large authored fields can be
    /// submitted without manufacturing one complete draw call per instance.
    /// </summary>
    public readonly struct MeshInstanceData
    {
        public readonly Matrix4x4 World;
        public readonly RenderColor Tint;

        public MeshInstanceData(Matrix4x4 world, RenderColor tint)
        {
            World = world;
            Tint = tint;
        }
    }

    /// <summary>A point (omnidirectional) light for dynamic scene lighting.</summary>
    public struct PointLight3D
    {
        public Vector3 Position;
        public Vector3 Color;
        public float   Radius;
        public float   Intensity;
        public float   Falloff;

        public PointLight3D(
            Vector3 position,
            Vector3 color,
            float radius,
            float intensity = 1f,
            float falloff = 2f)
        {
            Position  = position;
            Color     = color;
            Radius    = radius;
            Intensity = intensity;
            Falloff   = falloff;
        }
    }

    // ── Stats & enums ──────────────────────────────────────────────────────────

    public struct RenderStats
    {
        public int DrawCalls;
        public int Triangles;
        public int TextureSwitches;
        public int DrawCalls2D;
        public int DrawCalls3D;
        public int Triangles3D;
        public int ItemsSubmitted;
        public int InstancesCulled;
        public int InstancesDrawn;
        public int SpriteInstances;
        public int MeshInstances;
        public int SpriteInstanceCap;
        public int MeshInstanceCap;
        public int LightsUsed;
        public int LightsCap;
        public int Batches;
        public int FoliageInstances;
        public int FoliageBatches;
        public long FoliageUploadBytes;
        /// <summary>
        /// Non-instanced parked 3D draws from the last frame (transparent leftovers, NoDepthWrite,
        /// skinned specials). Opaque <see cref="MeshDrawFlags.IsFloor"/> no longer contributes here.
        /// </summary>
        public int WorldMeshes;
        public double GpuMs;
        /// <summary>AF1.2 GTAO pass cost in milliseconds (0 when disabled / Software).</summary>
        public double AoMs;
        /// <summary>AF1.4 contact-shadow pass cost in milliseconds (0 when disabled / Software).</summary>
        public double ContactShadowMs;
        /// <summary>AF1.5 local-volumetric pass cost in milliseconds (0 when disabled / Software).</summary>
        public double LocalVolumetricMs;
        /// <summary>
        /// AF1.6 smoke-extinction cost in milliseconds (0 when disabled / Software). There is no
        /// separate GPU pass — this is the per-frame volume bin and constant-buffer pack only.
        /// </summary>
        public double SmokeExtinctionMs;
        /// <summary>AF1.7 bloom pyramid cost in milliseconds (0 when disabled / Software).</summary>
        public double BloomMs;
        /// <summary>
        /// AF2.1 atmosphere LUT cost in milliseconds (0 when disabled / Software). Bake/upload is
        /// once; sampling is free in composite — may be a tiny CPU sentinel when enabled.
        /// </summary>
        public double AtmosphereLutMs;
        /// <summary>AF2.3 raymarched cloud pass cost in milliseconds (0 when disabled / Software).</summary>
        public double RaymarchedCloudsMs;
        /// <summary>
        /// AF2.5 celestial extras cost in milliseconds (0 when disabled / Software). Sampling is free
        /// in FogPost composite — may be a tiny CPU sentinel when enabled.
        /// </summary>
        public double CelestialExtrasMs;
    }

    public enum BlendMode   { Alpha, Additive, Multiply, None }
    public enum SamplerFilter { Point, Linear, Anisotropic }
    /// <summary>Legacy shared backend identity; runtime selection is owned by Genesis.Rendering.Core.</summary>
    public enum RenderBackend { SilkNetDx11, Direct3D12 }
}

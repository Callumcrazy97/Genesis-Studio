using System;
using System.Globalization;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Post-process anti-aliasing of the 3D image. It runs on the tonemapped picture after the held
    /// items (view models) are drawn, and before a project's post effects and the GUI, so the HUD
    /// and text are never softened. Off by default: a game's picture is unchanged until it asks.
    /// </summary>
    public enum AntiAliasingMode
    {
        /// <summary>No anti-aliasing: the image as the forward pass drew it.</summary>
        Off = 0,

        /// <summary>
        /// Fast approximate anti-aliasing: one full-screen pass that finds each high-contrast step
        /// and blends across it. Cheapest; softens fine texture detail a little.
        /// </summary>
        Fxaa = 1,

        /// <summary>
        /// Subpixel morphological anti-aliasing (1x, horizontal and vertical edge patterns): finds
        /// edges, measures each staircase and blends by the area the true edge would cover. Sharper
        /// than FXAA; three passes.
        /// </summary>
        Smaa = 2,
    }

    /// <summary>Names of <see cref="AntiAliasingMode"/> as scripts and project files write them.</summary>
    public static class AntiAliasingModes
    {
        /// <summary>The script name of a mode: "off", "fxaa" or "smaa".</summary>
        public static string Name(AntiAliasingMode mode) => mode switch
        {
            AntiAliasingMode.Fxaa => "fxaa",
            AntiAliasingMode.Smaa => "smaa",
            _ => "off",
        };

        /// <summary>
        /// Reads "off", "fxaa" or "smaa" in any case ("none", "false" and "0" mean off; "1" is FXAA,
        /// "2" is SMAA). False for anything else.
        /// </summary>
        public static bool TryParse(string text, out AntiAliasingMode mode)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "off": case "none": case "false": case "0": mode = AntiAliasingMode.Off; return true;
                case "fxaa": case "1": mode = AntiAliasingMode.Fxaa; return true;
                case "smaa": case "smaa1x": case "smaa 1x": case "2": mode = AntiAliasingMode.Smaa; return true;
                default: mode = AntiAliasingMode.Off; return false;
            }
        }

        internal static AntiAliasingMode ParseOr(string text, AntiAliasingMode fallback) =>
            TryParse(text, out AntiAliasingMode mode) ? mode : fallback;
    }

    /// <summary>A graphics quality preset, as a game's Graphics menu offers it.</summary>
    public enum RenderQualityTier
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3,
    }

    /// <summary>What one <see cref="RenderQualityTier"/> sets.</summary>
    public readonly record struct RenderQualitySettings(
        bool Gtao,
        bool ContactShadows,
        int ShadowCascades,
        int ShadowResolution,
        int PointLightShadows,
        bool LocalVolumetrics,
        bool Bloom,
        int VolumetricFogQuality,
        int CloudQuality,
        AntiAliasingMode AntiAliasing);

    /// <summary>
    /// Quality tiers: one call sets ambient occlusion, sun shadow cascades and resolution, point-light
    /// shadows, local volumetric light, bloom, volumetric fog and cloud quality and anti-aliasing
    /// together. Each setting can be changed on its own afterwards (Engine.Rendering.*).
    /// </summary>
    /// <remarks>
    /// Nothing changes until a game applies a tier. Once one has been applied, the engine-wide values
    /// of the settings a tier covers decide for every room: a tier (or a later Engine.Rendering
    /// change) can then turn off a room's own ambient occlusion or third cascade, as a player's
    /// Graphics choice should. Rooms still decide whether they have clouds or fog at all; a tier only
    /// sets how finely they are drawn.
    /// </remarks>
    public static class RenderQuality
    {
        private static RenderQualityTier? _applied;

        /// <summary>The settings a tier applies.</summary>
        public static RenderQualitySettings For(RenderQualityTier tier) => tier switch
        {
            RenderQualityTier.Low => new(
                Gtao: false, ContactShadows: false, ShadowCascades: 2, ShadowResolution: 512, PointLightShadows: 0,
                LocalVolumetrics: false, Bloom: false, VolumetricFogQuality: 0, CloudQuality: 0,
                AntiAliasing: AntiAliasingMode.Fxaa),
            RenderQualityTier.Medium => new(
                Gtao: true, ContactShadows: false, ShadowCascades: 2, ShadowResolution: 1024, PointLightShadows: 1,
                LocalVolumetrics: false, Bloom: true, VolumetricFogQuality: 1, CloudQuality: 1,
                AntiAliasing: AntiAliasingMode.Fxaa),
            RenderQualityTier.High => new(
                Gtao: true, ContactShadows: true, ShadowCascades: 3, ShadowResolution: 1024, PointLightShadows: 2,
                LocalVolumetrics: true, Bloom: true, VolumetricFogQuality: 1, CloudQuality: 2,
                AntiAliasing: AntiAliasingMode.Smaa),
            _ => new(
                Gtao: true, ContactShadows: true, ShadowCascades: 3, ShadowResolution: 2048, PointLightShadows: 4,
                LocalVolumetrics: true, Bloom: true, VolumetricFogQuality: 2, CloudQuality: 3,
                AntiAliasing: AntiAliasingMode.Smaa),
        };

        /// <summary>The script name of a tier: "low", "medium", "high" or "ultra".</summary>
        public static string Name(RenderQualityTier tier) => tier switch
        {
            RenderQualityTier.Low => "low",
            RenderQualityTier.Medium => "medium",
            RenderQualityTier.High => "high",
            _ => "ultra",
        };

        /// <summary>Reads "low", "medium", "high" or "ultra" (or 0 to 3) in any case; false for anything else.</summary>
        public static bool TryParse(string text, out RenderQualityTier tier)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "low": case "0": tier = RenderQualityTier.Low; return true;
                case "medium": case "med": case "1": tier = RenderQualityTier.Medium; return true;
                case "high": case "2": tier = RenderQualityTier.High; return true;
                case "ultra": case "3": tier = RenderQualityTier.Ultra; return true;
                default: tier = RenderQualityTier.High; return false;
            }
        }

        /// <summary>Applies a tier to the engine-wide rendering settings, from the next frame.</summary>
        public static void Apply(RenderQualityTier tier)
        {
            RenderQualitySettings s = For(tier);
            MeshLightingDefaults.Configure(
                MeshLightingDefaults.LightingEnabled,
                MeshLightingDefaults.ShadowsEnabled,
                MeshLightingDefaults.ShadowStrength,
                s.ShadowCascades,
                s.Gtao,
                s.ContactShadows,
                s.LocalVolumetrics,
                MeshLightingDefaults.SmokeExtinctionEnabled,
                s.Bloom,
                MeshLightingDefaults.Exposure,
                MeshLightingDefaults.Contrast,
                MeshLightingDefaults.Saturation,
                MeshLightingDefaults.VignetteStrength,
                MeshLightingDefaults.BloomThreshold,
                MeshLightingDefaults.BloomIntensity,
                MeshLightingDefaults.AtmosphereLutEnabled,
                MeshLightingDefaults.RaymarchedCloudsEnabled,
                MeshLightingDefaults.CloudTemporalEnabled,
                s.CloudQuality,
                MeshLightingDefaults.CelestialExtrasEnabled);
            RenderCapacityDefaults.Configure(
                RenderCapacityDefaults.SpriteInstanceCap,
                RenderCapacityDefaults.MeshInstanceCap,
                RenderCapacityDefaults.SceneLocalLightCap,
                RenderCapacityDefaults.DrawCallMode,
                RenderCapacityDefaults.WorldDrawBudget,
                s.PointLightShadows,
                RenderCapacityDefaults.LocalVolumetricLightBudget);
            MeshLightingDefaults.ShadowResolution = s.ShadowResolution;
            MeshLightingDefaults.VolumetricFogQuality = s.VolumetricFogQuality;
            MeshLightingDefaults.AntiAliasing = s.AntiAliasing;
            MeshLightingDefaults.QualityTierApplied = true;
            _applied = tier;
        }

        /// <summary>
        /// The tier last applied while every setting it covers is still what it set; "custom" once
        /// one of them has been changed; empty before any tier has been applied.
        /// </summary>
        public static string Current
        {
            get
            {
                if (_applied is not RenderQualityTier tier) return string.Empty;
                return Matches(For(tier)) ? Name(tier) : "custom";
            }
        }

        /// <summary>The settings the tiers cover, as they are now.</summary>
        public static RenderQualitySettings Effective => new(
            MeshLightingDefaults.GtaoEnabled,
            MeshLightingDefaults.ContactShadowsEnabled,
            MeshLightingDefaults.ShadowCascadeCount,
            MeshLightingDefaults.ShadowResolution > 0 ? MeshLightingDefaults.ShadowResolution : MeshLightingDefaults.DefaultShadowResolution,
            RenderCapacityDefaults.OmniShadowBudget,
            MeshLightingDefaults.LocalVolumetricsEnabled,
            MeshLightingDefaults.BloomEnabled,
            MeshLightingDefaults.VolumetricFogQuality,
            MeshLightingDefaults.CloudQuality,
            MeshLightingDefaults.AntiAliasing);

        private static bool Matches(RenderQualitySettings wanted) => Effective == wanted;

        /// <summary>
        /// Forgets any applied tier and puts the settings it covers back to the engine's defaults
        /// (everything off, two cascades, 1024 shadows, one point-light shadow, fog quality from the
        /// room, high clouds, no anti-aliasing). For tests and tools; games pick a tier instead.
        /// </summary>
        public static void Reset()
        {
            _applied = null;
            MeshLightingDefaults.QualityTierApplied = false;
            MeshLightingDefaults.Configure(
                MeshLightingDefaults.LightingEnabled,
                MeshLightingDefaults.ShadowsEnabled,
                MeshLightingDefaults.ShadowStrength,
                2,
                false,
                false,
                false,
                MeshLightingDefaults.SmokeExtinctionEnabled,
                false,
                MeshLightingDefaults.Exposure,
                MeshLightingDefaults.Contrast,
                MeshLightingDefaults.Saturation,
                MeshLightingDefaults.VignetteStrength,
                MeshLightingDefaults.BloomThreshold,
                MeshLightingDefaults.BloomIntensity,
                MeshLightingDefaults.AtmosphereLutEnabled,
                MeshLightingDefaults.RaymarchedCloudsEnabled,
                MeshLightingDefaults.CloudTemporalEnabled,
                2,
                MeshLightingDefaults.CelestialExtrasEnabled);
            RenderCapacityDefaults.Configure(
                RenderCapacityDefaults.SpriteInstanceCap,
                RenderCapacityDefaults.MeshInstanceCap,
                RenderCapacityDefaults.SceneLocalLightCap,
                RenderCapacityDefaults.DrawCallMode,
                RenderCapacityDefaults.WorldDrawBudget,
                RenderCapacityDefaults.DefaultOmniShadowBudget,
                RenderCapacityDefaults.LocalVolumetricLightBudget);
            MeshLightingDefaults.ShadowResolution = 0;
            MeshLightingDefaults.VolumetricFogQuality = -1;
            MeshLightingDefaults.AntiAliasing = AntiAliasingMode.Off;
        }
    }

    // Anti-aliasing, shadow resolution, fog quality and the quality-tier switch: kept apart from the
    // long Configure list, so callers that reconfigure lighting do not reset them.
    public static partial class MeshLightingDefaults
    {
        public const string AntiAliasingEnvironmentVariable = "GENESIS_ANTI_ALIASING";
        public const string ShadowResolutionEnvironmentVariable = "GENESIS_SHADOW_RESOLUTION";

        /// <summary>The sun shadow map size, in texels per side, when nothing asks for another.</summary>
        public const int DefaultShadowResolution = 1024;
        public const int MinShadowResolution = 512;
        public const int MaxShadowResolution = 4096;

        private static AntiAliasingMode _antiAliasing = AntiAliasingModes.ParseOr(
            Environment.GetEnvironmentVariable(AntiAliasingEnvironmentVariable), AntiAliasingMode.Off);
        private static int _shadowResolution = NormalizeShadowResolution(
            int.TryParse(Environment.GetEnvironmentVariable(ShadowResolutionEnvironmentVariable),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested) ? requested : 0);
        private static int _volumetricFogQuality = -1;

        /// <summary>
        /// Post-process anti-aliasing of the 3D image (Off by default). A viewport whose own state
        /// asks for anti-aliasing keeps its choice; otherwise this applies.
        /// </summary>
        public static AntiAliasingMode AntiAliasing
        {
            get => _antiAliasing;
            set => _antiAliasing = Enum.IsDefined(value) ? value : AntiAliasingMode.Off;
        }

        /// <summary>
        /// Sun shadow map size in texels per side for every cascade: 0 keeps the engine's 1024;
        /// otherwise a power of two from 512 to 4096 (other values are rounded to one).
        /// </summary>
        public static int ShadowResolution
        {
            get => _shadowResolution;
            set => _shadowResolution = NormalizeShadowResolution(value);
        }

        /// <summary>Volumetric fog grid quality 0 (low) to 2 (high) for every room; -1 leaves each room's own.</summary>
        public static int VolumetricFogQuality
        {
            get => _volumetricFogQuality;
            set => _volumetricFogQuality = Math.Clamp(value, -1, 2);
        }

        /// <summary>
        /// True once a quality tier has been applied: from then on the engine-wide ambient
        /// occlusion, contact shadow, local volumetric, bloom, cascade and cloud quality settings
        /// decide for every room instead of adding to what a room turns on.
        /// </summary>
        public static bool QualityTierApplied { get; internal set; }

        /// <summary>0 for none or the default; otherwise the nearest power of two from 512 to 4096.</summary>
        public static int NormalizeShadowResolution(int value)
        {
            if (value <= 0) return 0;
            int clamped = Math.Clamp(value, MinShadowResolution, MaxShadowResolution);
            int lower = MinShadowResolution;
            while (lower * 2 <= clamped) lower *= 2;
            int upper = Math.Min(lower * 2, MaxShadowResolution);
            return clamped - lower <= upper - clamped ? lower : upper;
        }

        private static void ApplyQuality(ref Mesh3DState state)
        {
            if (QualityTierApplied)
            {
                state.GtaoEnabled = _gtaoEnabled;
                state.ContactShadowsEnabled = _contactShadowsEnabled;
                state.LocalVolumetricsEnabled = _localVolumetricsEnabled;
                state.BloomEnabled = _bloomEnabled;
                state.ShadowCascadeCount = _shadowCascadeCount >= 3 ? 3 : 2;
                state.CloudQuality = _cloudQuality is >= 0 and <= 3 ? _cloudQuality : 2;
            }

            if (_volumetricFogQuality >= 0)
                state.VolumetricFogQuality = _volumetricFogQuality;
            if (_shadowResolution > 0)
                state.ShadowMapResolution = _shadowResolution;
            if (state.AntiAliasing == AntiAliasingMode.Off)
                state.AntiAliasing = _antiAliasing;
        }
    }
}

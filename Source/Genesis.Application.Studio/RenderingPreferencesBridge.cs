using System.Collections.Generic;
using System.Globalization;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Studio;

internal static class RenderingPreferencesBridge
{
    /// <summary>
    /// Applies every preference that affects rendering, including the ones that are not on the
    /// Rendering page.
    /// </summary>
    /// <remarks>
    /// "Use VSync in editor previews" lives under Runtime but is a rendering decision, and routing
    /// it anywhere else is how it ended up saved-but-ignored. One entry point owns preferences →
    /// engine, so a setting cannot be persisted by a page nobody wired to anything.
    /// </remarks>
    public static void Apply(GenesisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Apply(settings.Rendering);
        EditorPreviewSettings.Configure(settings.Runtime.VSyncInPreview);
        Genesis.Runtime.Debugger.RuntimeDiagnostics.Enabled = settings.Runtime.EnableRuntimeDiagnostics;
        Genesis.Application.Core.Projects.ResourceBackupService.Enabled = settings.Editing.CreateBackups;
        Genesis.Application.Core.Projects.ResourceBackupService.RetentionDays =
            settings.Editing.BackupRetentionDays;
    }

    public static void Apply(RenderingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EngineRenderingDefaults.WaterReflections = settings.WaterReflections;
        RenderBackendSelection.Configure(RenderBackendCatalog.ParseSettingsValue(settings.Backend));
        MeshRasterDefaults.Configure(
            MeshRasterDefaults.ParseCulling(settings.FaceCulling, FaceCullingOverride.Back),
            MeshRasterDefaults.ParseWinding(
                settings.FrontFaceWinding,
                FrontFaceWindingOverride.CounterClockwise));
        MeshLightingDefaults.Configure(
            settings.LightingEnabled,
            settings.ShadowsEnabled,
            settings.ShadowStrength,
            settings.ShadowCascadeCount,
            settings.GtaoEnabled,
            settings.ContactShadowsEnabled,
            settings.LocalVolumetricsEnabled,
            settings.SmokeExtinctionEnabled,
            settings.BloomEnabled,
            settings.Exposure,
            settings.Contrast,
            settings.Saturation,
            settings.VignetteStrength,
            settings.BloomThreshold,
            settings.BloomIntensity,
            settings.AtmosphereLutEnabled,
            settings.RaymarchedCloudsEnabled,
            settings.CloudTemporalEnabled,
            settings.CloudQuality,
            settings.CelestialExtrasEnabled);
        RenderCapacityDefaults.Configure(
            settings.SpriteInstanceCap,
            settings.MeshInstanceCap,
            settings.SceneLocalLightCap,
            settings.DrawCallMode,
            settings.WorldDrawBudget,
            settings.OmniShadowBudget,
            settings.LocalVolumetricLightBudget);
    }

    /// <summary>
    /// Applies the open project's own engine defaults.
    /// </summary>
    /// <remarks>
    /// Fog describes the game, so it is stored in the project file and applied when a project is
    /// opened — not in this machine's preferences, where it neither travelled with the project nor
    /// survived opening a different one.
    /// </remarks>
    public static void ApplyProject(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Rendering ??= new ProjectRenderingSettings();

        // A project that names a backend overrides this machine's preference. Empty means the
        // project has no opinion, and the installation default set in Apply() stands — so an
        // unset value must not be parsed, since every unrecognised string resolves to Direct3D 11.
        if (!string.IsNullOrWhiteSpace(manifest.Rendering.Backend))
        {
            RenderBackendSelection.Configure(
                RenderBackendCatalog.ParseSettingsValue(manifest.Rendering.Backend));
        }

        // FogAlpha must be passed explicitly: the four-argument overload keeps whatever alpha the
        // engine already had, which is how the designer's chosen alpha went nowhere (NEXT-106).
        EngineRenderingDefaults.ConfigureFog(
            manifest.Rendering.FogEnabled,
            manifest.Rendering.FogColorHex,
            manifest.Rendering.FogStart,
            manifest.Rendering.FogEnd,
            manifest.Rendering.FogAlpha);

        if (manifest.Rendering.GtaoEnabled
            || manifest.Rendering.ContactShadowsEnabled
            || manifest.Rendering.LocalVolumetricsEnabled
            || manifest.Rendering.SmokeExtinctionEnabled
            || manifest.Rendering.BloomEnabled
            || manifest.Rendering.AtmosphereLutEnabled
            || manifest.Rendering.RaymarchedCloudsEnabled
            || manifest.Rendering.CloudTemporalEnabled
            || manifest.Rendering.CelestialExtrasEnabled)
        {
            MeshLightingDefaults.Configure(
                MeshLightingDefaults.LightingEnabled,
                MeshLightingDefaults.ShadowsEnabled,
                MeshLightingDefaults.ShadowStrength,
                MeshLightingDefaults.ShadowCascadeCount,
                MeshLightingDefaults.GtaoEnabled || manifest.Rendering.GtaoEnabled,
                MeshLightingDefaults.ContactShadowsEnabled
                    || manifest.Rendering.ContactShadowsEnabled,
                MeshLightingDefaults.LocalVolumetricsEnabled
                    || manifest.Rendering.LocalVolumetricsEnabled,
                MeshLightingDefaults.SmokeExtinctionEnabled
                    || manifest.Rendering.SmokeExtinctionEnabled,
                MeshLightingDefaults.BloomEnabled || manifest.Rendering.BloomEnabled,
                MeshLightingDefaults.Exposure,
                MeshLightingDefaults.Contrast,
                MeshLightingDefaults.Saturation,
                MeshLightingDefaults.VignetteStrength,
                MeshLightingDefaults.BloomThreshold,
                MeshLightingDefaults.BloomIntensity,
                MeshLightingDefaults.AtmosphereLutEnabled
                    || manifest.Rendering.AtmosphereLutEnabled,
                MeshLightingDefaults.RaymarchedCloudsEnabled
                    || manifest.Rendering.RaymarchedCloudsEnabled,
                MeshLightingDefaults.CloudTemporalEnabled
                    || manifest.Rendering.CloudTemporalEnabled,
                manifest.Rendering.CloudQuality is >= 0 and <= 3
                    ? manifest.Rendering.CloudQuality
                    : MeshLightingDefaults.CloudQuality,
                MeshLightingDefaults.CelestialExtrasEnabled
                    || manifest.Rendering.CelestialExtrasEnabled);
        }
    }

    /// <summary>
    /// The environment F5 hands to <c>GenesisEngine.exe</c>: this machine's backend, and the
    /// project's own engine defaults.
    /// </summary>
    public static IDictionary<string, string> BuildPlayerEnvironment(
        RenderingSettings settings,
        ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Rendering ??= new ProjectRenderingSettings();
        manifest.Runtime ??= new ProjectRuntimeSettings();

        Dictionary<string, string> environment = EngineRenderingDefaults.BuildEnvironment(
            RenderBackendCatalog.ParseSettingsValue(settings.Backend),
            manifest.Rendering.FogEnabled,
            manifest.Rendering.FogColorHex,
            manifest.Rendering.FogStart,
            manifest.Rendering.FogEnd,
            manifest.Rendering.FogAlpha);

        environment[EngineRenderingDefaults.AllowEscapeEnvironmentVariable] =
            manifest.Runtime.AllowEscapeToClose ? "1" : "0";
        environment[EngineRenderingDefaults.WaterReflectionsEnvironmentVariable] = settings.WaterReflections ? "1" : "0";
        environment[MeshRasterDefaults.CullingEnvironmentVariable] =
            MeshRasterDefaults.ParseCulling(settings.FaceCulling, FaceCullingOverride.Back).ToString();
        environment[MeshRasterDefaults.WindingEnvironmentVariable] =
            MeshRasterDefaults.ParseWinding(
                settings.FrontFaceWinding,
                FrontFaceWindingOverride.CounterClockwise).ToString();
        environment[MeshLightingDefaults.LightingEnvironmentVariable] =
            settings.LightingEnabled ? "1" : "0";
        environment[MeshLightingDefaults.ShadowsEnvironmentVariable] =
            settings.ShadowsEnabled ? "1" : "0";
        environment[MeshLightingDefaults.ShadowStrengthEnvironmentVariable] =
            Math.Clamp(settings.ShadowStrength, 0f, 1f).ToString(CultureInfo.InvariantCulture);
        environment[MeshLightingDefaults.ShadowCascadeCountEnvironmentVariable] =
            (settings.ShadowCascadeCount >= 3 ? 3 : 2).ToString(CultureInfo.InvariantCulture);
        environment[MeshLightingDefaults.GtaoEnabledEnvironmentVariable] =
            settings.GtaoEnabled || (manifest.Rendering?.GtaoEnabled ?? false) ? "1" : "0";
        environment[MeshLightingDefaults.ContactShadowsEnabledEnvironmentVariable] =
            settings.ContactShadowsEnabled || (manifest.Rendering?.ContactShadowsEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.LocalVolumetricsEnabledEnvironmentVariable] =
            settings.LocalVolumetricsEnabled
            || (manifest.Rendering?.LocalVolumetricsEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.SmokeExtinctionEnabledEnvironmentVariable] =
            settings.SmokeExtinctionEnabled
            || (manifest.Rendering?.SmokeExtinctionEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.BloomEnabledEnvironmentVariable] =
            settings.BloomEnabled || (manifest.Rendering?.BloomEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.AtmosphereLutEnabledEnvironmentVariable] =
            settings.AtmosphereLutEnabled || (manifest.Rendering?.AtmosphereLutEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.RaymarchedCloudsEnabledEnvironmentVariable] =
            settings.RaymarchedCloudsEnabled
            || (manifest.Rendering?.RaymarchedCloudsEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.CloudTemporalEnabledEnvironmentVariable] =
            settings.CloudTemporalEnabled
            || (manifest.Rendering?.CloudTemporalEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.CelestialExtrasEnabledEnvironmentVariable] =
            settings.CelestialExtrasEnabled
            || (manifest.Rendering?.CelestialExtrasEnabled ?? false)
                ? "1"
                : "0";
        environment[MeshLightingDefaults.CloudQualityEnvironmentVariable] =
            (manifest.Rendering?.CloudQuality is >= 0 and <= 3
                ? manifest.Rendering.CloudQuality
                : settings.CloudQuality is >= 0 and <= 3 ? settings.CloudQuality : 2)
                .ToString(CultureInfo.InvariantCulture);
        environment[RenderCapacityDefaults.SpriteInstanceCapEnvironmentVariable] =
            settings.SpriteInstanceCap.ToString(CultureInfo.InvariantCulture);
        environment[RenderCapacityDefaults.MeshInstanceCapEnvironmentVariable] =
            settings.MeshInstanceCap.ToString(CultureInfo.InvariantCulture);
        environment[RenderCapacityDefaults.SceneLocalLightCapEnvironmentVariable] =
            settings.SceneLocalLightCap.ToString(CultureInfo.InvariantCulture);
        environment[RenderCapacityDefaults.OmniShadowBudgetEnvironmentVariable] =
            Math.Clamp(
                settings.OmniShadowBudget,
                RenderCapacityDefaults.MinOmniShadowBudget,
                RenderCapacityDefaults.MaxOmniShadowBudget)
                .ToString(CultureInfo.InvariantCulture);
        environment[RenderCapacityDefaults.LocalVolumetricLightBudgetEnvironmentVariable] =
            Math.Clamp(
                settings.LocalVolumetricLightBudget,
                RenderCapacityDefaults.MinLocalVolumetricLightBudget,
                RenderCapacityDefaults.MaxLocalVolumetricLightBudget)
                .ToString(CultureInfo.InvariantCulture);
        environment[RenderCapacityDefaults.DrawCallModeEnvironmentVariable] =
            string.Equals(settings.DrawCallMode, RenderCapacityDefaults.DrawCallModeManual,
                StringComparison.OrdinalIgnoreCase)
                ? RenderCapacityDefaults.DrawCallModeManual
                : RenderCapacityDefaults.DrawCallModeAuto;
        environment[RenderCapacityDefaults.WorldDrawBudgetEnvironmentVariable] =
            settings.WorldDrawBudget.ToString(CultureInfo.InvariantCulture);
        return environment;
    }

    public static string BackendStatusText(RenderingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RenderBackendOption requested = RenderBackendCatalog.ParseSettingsValue(settings.Backend);
        RenderBackendDescriptor requestedInfo = RenderBackendCatalog.Describe(requested);
        RenderBackendDescriptor effectiveInfo = RenderBackendCatalog.Describe(RenderBackendSelection.EffectiveBackend);
        return requested == RenderBackendSelection.EffectiveBackend
            ? $"EMBER  ·  {effectiveInfo.ShortName}"
            : $"EMBER  ·  {effectiveInfo.ShortName}  ({requestedInfo.ShortName} REQUESTED)";
    }
}

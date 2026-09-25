using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// AF1.8 installation-wide rendering quality toggles. Authors write
/// <c>Engine.Rendering.GtaoEnabled = true;</c> (and peers); values land on
/// <see cref="MeshLightingDefaults"/> / <see cref="RenderCapacityDefaults"/>, not
/// <see cref="PgslContext"/>.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("GtaoEnabled", "Engine.Rendering.GtaoEnabled",
        "Enable GTAO (AO — half-res ambient occlusion)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool GtaoEnabled
    {
        get => MeshLightingDefaults.GtaoEnabled;
        set => ReconfigureLighting(gtaoEnabled: value);
    }

    [PgslCommand("ContactShadowsEnabled", "Engine.Rendering.ContactShadowsEnabled",
        "Enable contact shadows (CS — screen-space)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool ContactShadowsEnabled
    {
        get => MeshLightingDefaults.ContactShadowsEnabled;
        set => ReconfigureLighting(contactShadowsEnabled: value);
    }

    [PgslCommand("LocalVolumetricsEnabled", "Engine.Rendering.LocalVolumetricsEnabled",
        "Enable local volumetric scatter (LV)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool LocalVolumetricsEnabled
    {
        get => MeshLightingDefaults.LocalVolumetricsEnabled;
        set => ReconfigureLighting(localVolumetricsEnabled: value);
    }

    [PgslCommand("SmokeExtinctionEnabled", "Engine.Rendering.SmokeExtinctionEnabled",
        "Enable smoke extinction (SE)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool SmokeExtinctionEnabled
    {
        get => MeshLightingDefaults.SmokeExtinctionEnabled;
        set => ReconfigureLighting(smokeExtinctionEnabled: value);
    }

    [PgslCommand("BloomEnabled", "Engine.Rendering.BloomEnabled",
        "Enable HDR bloom (BL)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool BloomEnabled
    {
        get => MeshLightingDefaults.BloomEnabled;
        set => ReconfigureLighting(bloomEnabled: value);
    }

    [PgslCommand("AtmosphereLutEnabled", "Engine.Rendering.AtmosphereLutEnabled",
        "Enable atmosphere LUT sky-view (AL)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool AtmosphereLutEnabled
    {
        get => MeshLightingDefaults.AtmosphereLutEnabled;
        set => ReconfigureLighting(atmosphereLutEnabled: value);
    }

    [PgslCommand("RaymarchedCloudsEnabled", "Engine.Rendering.RaymarchedCloudsEnabled",
        "Enable raymarched 3D clouds (CL)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool RaymarchedCloudsEnabled
    {
        get => MeshLightingDefaults.RaymarchedCloudsEnabled;
        set => ReconfigureLighting(raymarchedCloudsEnabled: value);
    }

    [PgslCommand("CloudTemporalEnabled", "Engine.Rendering.CloudTemporalEnabled",
        "Enable cloud temporal reprojection (CT)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool CloudTemporalEnabled
    {
        get => MeshLightingDefaults.CloudTemporalEnabled;
        set => ReconfigureLighting(cloudTemporalEnabled: value);
    }

    [PgslCommand("CelestialExtrasEnabled", "Engine.Rendering.CelestialExtrasEnabled",
        "Enable celestial extras stars/MW/moon (CE)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool CelestialExtrasEnabled
    {
        get => MeshLightingDefaults.CelestialExtrasEnabled;
        set => ReconfigureLighting(celestialExtrasEnabled: value);
    }

    [PgslCommand("CloudQuality", "Engine.Rendering.CloudQuality",
        "Cloud quality ladder 0..3 (P/B/H/C)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static int CloudQuality
    {
        get => MeshLightingDefaults.CloudQuality;
        set => ReconfigureLighting(cloudQuality: value);
    }

    [PgslCommand("CascadeCount", "Engine.Rendering.CascadeCount",
        "Directional shadow cascade count (2 or 3)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static int CascadeCount
    {
        get => MeshLightingDefaults.ShadowCascadeCount;
        set => ReconfigureLighting(shadowCascadeCount: value);
    }

    [PgslCommand("LocalVolumetricLightBudget", "Engine.Rendering.LocalVolumetricLightBudget",
        "Local volumetric light budget (0–4, LV)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static int LocalVolumetricLightBudget
    {
        get => RenderCapacityDefaults.LocalVolumetricLightBudget;
        set => ReconfigureCapacity(localVolumetricLightBudget: value);
    }

    [PgslCommand("OmniShadowBudget", "Engine.Rendering.OmniShadowBudget",
        "Omni-shadow cubemap budget (0–4)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static int OmniShadowBudget
    {
        get => RenderCapacityDefaults.OmniShadowBudget;
        set => ReconfigureCapacity(omniShadowBudget: value);
    }

    [PgslCommand("Exposure", "Engine.Rendering.Exposure",
        "HDR exposure scalar (1 = identity)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static float Exposure
    {
        get => MeshLightingDefaults.Exposure;
        set => ReconfigureLighting(exposure: value);
    }

    /// <summary>
    /// Re-apply <see cref="MeshLightingDefaults"/> with a single field overridden so sibling AF
    /// toggles are not wiped.
    /// </summary>
    private static void ReconfigureLighting(
        bool? lightingEnabled = null,
        bool? shadowsEnabled = null,
        float? shadowStrength = null,
        int? shadowCascadeCount = null,
        bool? gtaoEnabled = null,
        bool? contactShadowsEnabled = null,
        bool? localVolumetricsEnabled = null,
        bool? smokeExtinctionEnabled = null,
        bool? bloomEnabled = null,
        float? exposure = null,
        float? contrast = null,
        float? saturation = null,
        float? vignetteStrength = null,
        float? bloomThreshold = null,
        float? bloomIntensity = null,
        bool? atmosphereLutEnabled = null,
        bool? raymarchedCloudsEnabled = null,
        bool? cloudTemporalEnabled = null,
        int? cloudQuality = null,
        bool? celestialExtrasEnabled = null)
    {
        MeshLightingDefaults.Configure(
            lightingEnabled ?? MeshLightingDefaults.LightingEnabled,
            shadowsEnabled ?? MeshLightingDefaults.ShadowsEnabled,
            shadowStrength ?? MeshLightingDefaults.ShadowStrength,
            shadowCascadeCount ?? MeshLightingDefaults.ShadowCascadeCount,
            gtaoEnabled ?? MeshLightingDefaults.GtaoEnabled,
            contactShadowsEnabled ?? MeshLightingDefaults.ContactShadowsEnabled,
            localVolumetricsEnabled ?? MeshLightingDefaults.LocalVolumetricsEnabled,
            smokeExtinctionEnabled ?? MeshLightingDefaults.SmokeExtinctionEnabled,
            bloomEnabled ?? MeshLightingDefaults.BloomEnabled,
            exposure ?? MeshLightingDefaults.Exposure,
            contrast ?? MeshLightingDefaults.Contrast,
            saturation ?? MeshLightingDefaults.Saturation,
            vignetteStrength ?? MeshLightingDefaults.VignetteStrength,
            bloomThreshold ?? MeshLightingDefaults.BloomThreshold,
            bloomIntensity ?? MeshLightingDefaults.BloomIntensity,
            atmosphereLutEnabled ?? MeshLightingDefaults.AtmosphereLutEnabled,
            raymarchedCloudsEnabled ?? MeshLightingDefaults.RaymarchedCloudsEnabled,
            cloudTemporalEnabled ?? MeshLightingDefaults.CloudTemporalEnabled,
            cloudQuality ?? MeshLightingDefaults.CloudQuality,
            celestialExtrasEnabled ?? MeshLightingDefaults.CelestialExtrasEnabled);
    }

    /// <summary>
    /// Re-apply <see cref="RenderCapacityDefaults"/> changing only the requested budget.
    /// </summary>
    private static void ReconfigureCapacity(
        int? omniShadowBudget = null,
        int? localVolumetricLightBudget = null)
    {
        RenderCapacityDefaults.Configure(
            RenderCapacityDefaults.SpriteInstanceCap,
            RenderCapacityDefaults.MeshInstanceCap,
            RenderCapacityDefaults.SceneLocalLightCap,
            RenderCapacityDefaults.DrawCallMode,
            RenderCapacityDefaults.WorldDrawBudget,
            omniShadowBudget ?? RenderCapacityDefaults.OmniShadowBudget,
            localVolumetricLightBudget ?? RenderCapacityDefaults.LocalVolumetricLightBudget);
    }
}

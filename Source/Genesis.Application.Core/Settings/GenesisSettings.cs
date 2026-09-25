namespace Genesis.Application.Core.Settings;

public sealed class GenesisSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public EditingSettings Editing { get; set; } = new();

    public RuntimeSettings Runtime { get; set; } = new();

    public RenderingSettings Rendering { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public List<RecentProject> RecentProjects { get; set; } = [];
}

public sealed class GeneralSettings
{
    public bool ShowSplashScreen { get; set; } = true;

    public bool ReopenLastProject { get; set; }

    public bool ConfirmDestructiveActions { get; set; } = true;

    public bool CheckForExternalChanges { get; set; } = true;
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "Dark";

    /// <summary>Whether <see cref="Theme"/> names a flat palette or an image.</summary>
    public string ThemeMode { get; set; } = AppearanceThemeModes.Colour;

    public string Density { get; set; } = "Comfortable";

    public float InterfaceScale { get; set; } = 1.0f;

    public int CodeFontSize { get; set; } = 11;

    public bool UseAnimations { get; set; } = true;
}

public static class AppearanceThemeModes
{
    public const string Automatic = "Automatic";

    public const string Colour = "Colour";

    public const string Image = "Image";
}

public sealed class EditingSettings
{
    public bool AutoSave { get; set; } = true;

    public int AutoSaveMinutes { get; set; } = 5;

    public bool CreateBackups { get; set; } = true;

    public int BackupRetentionDays { get; set; } = 14;
}

public sealed class RuntimeSettings
{
    public string BuildConfiguration { get; set; } = "Debug";

    public string PlayerArchitecture { get; set; } = "win-x64";

    public bool VSyncInPreview { get; set; } = true;

    public bool PauseWhenStudioLosesFocus { get; set; }

    public bool EnableRuntimeDiagnostics { get; set; } = true;

    /// <summary>Default Texture Group atlas resolution (2048 default, or 4096).</summary>
    public int DefaultTextureGroupSize { get; set; } = 2048;

    public string DefaultTextureGroupName { get; set; } = "Default Texture Group";

    public string DefaultAudioGroupName { get; set; } = "Default Audio Group";

    public List<TextureGroupDefinition> TextureGroups { get; set; } =
    [
        new TextureGroupDefinition { Name = "Default Texture Group", AtlasSize = 2048, IsDefault = true }
    ];

    public List<AudioGroupDefinition> AudioGroups { get; set; } =
    [
        new AudioGroupDefinition { Name = "Default Audio Group", IsDefault = true }
    ];
}

public sealed class TextureGroupDefinition
{
    public string Name { get; set; } = "Default Texture Group";
    public int AtlasSize { get; set; } = 2048;
    public bool IsDefault { get; set; }
}

public sealed class AudioGroupDefinition
{
    public string Name { get; set; } = "Default Audio Group";
    public bool IsDefault { get; set; }
}

public sealed class RenderingSettings
{
    public bool WaterReflections { get; set; }
    /// <summary>Persisted enum name from Genesis.Rendering.Core.RenderBackendOption.</summary>
    public string Backend { get; set; } = "SilkNetDx11";

    /// <summary>Installation default: Back, Front, or None.</summary>
    public string FaceCulling { get; set; } = "Back";

    /// <summary>Installation default: Clockwise or CounterClockwise.</summary>
    public string FrontFaceWinding { get; set; } = "CounterClockwise";

    /// <summary>Master switch for directional, ambient, and object-emitted lights.</summary>
    public bool LightingEnabled { get; set; } = true;

    /// <summary>Master switch for shadow-map rendering and shadow sampling.</summary>
    public bool ShadowsEnabled { get; set; } = true;

    /// <summary>Global multiplier for visible shadow darkness, in the inclusive 0..1 range.</summary>
    public float ShadowStrength { get; set; } = 1f;

    /// <summary>Directional shadow cascade count: 2 (default) or 3 (AF1.1 far envelope).</summary>
    public int ShadowCascadeCount { get; set; } = 2;

    /// <summary>AF1.2 optional half-resolution GTAO (default off).</summary>
    public bool GtaoEnabled { get; set; }

    /// <summary>AF1.4 optional half-resolution screen-space contact shadows (default off).</summary>
    public bool ContactShadowsEnabled { get; set; }

    /// <summary>AF1.5 optional bounded local-light volumetric scatter (default off).</summary>
    public bool LocalVolumetricsEnabled { get; set; }

    /// <summary>AF1.6 optional particle smoke as a Beer-Lambert transmittance term (default off).</summary>
    public bool SmokeExtinctionEnabled { get; set; }

    /// <summary>AF1.7 optional HDR bloom pyramid (default off — keeps DX11/DX12 goldens stable).</summary>
    public bool BloomEnabled { get; set; }

    /// <summary>AF2.1 optional atmosphere LUT sky compose (default off — keeps goldens stable).</summary>
    public bool AtmosphereLutEnabled { get; set; }

    /// <summary>AF2.3 optional half-res raymarched clouds (default off — keeps goldens stable).</summary>
    public bool RaymarchedCloudsEnabled { get; set; }

    /// <summary>AF2.4 optional cloud temporal reprojection (default off — keeps goldens stable).</summary>
    public bool CloudTemporalEnabled { get; set; }

    /// <summary>
    /// AF2.5 optional FogPost sky-only celestial extras (stars / Milky Way / moon; default off).
    /// </summary>
    public bool CelestialExtrasEnabled { get; set; }

    /// <summary>
    /// AF2.4 cloud quality 0..3 (Performance/Balanced/High/Cinematic). Default 2 = High (0.50 scale).
    /// </summary>
    public int CloudQuality { get; set; } = 2;

    /// <summary>AF1.7 exposure scalar before ACES (default 1 = identity).</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>AF1.7 contrast around mid-grey (default 1 = identity).</summary>
    public float Contrast { get; set; } = 1f;

    /// <summary>AF1.7 saturation (default 1 = identity).</summary>
    public float Saturation { get; set; } = 1f;

    /// <summary>AF1.7 vignette strength 0..1 (default 0 = off).</summary>
    public float VignetteStrength { get; set; }

    /// <summary>AF1.7 bloom luma threshold (only matters when bloom is on).</summary>
    public float BloomThreshold { get; set; } = 1f;

    /// <summary>AF1.7 bloom add intensity (only matters when bloom is on).</summary>
    public float BloomIntensity { get; set; } = 0.04f;

    /// <summary>Soft cap for 2D sprite instances uploaded per flush (not an ECS entity count).</summary>
    public int SpriteInstanceCap { get; set; } = 32768;

    /// <summary>Soft cap for 3D mesh instances uploaded per instanced pass.</summary>
    public int MeshInstanceCap { get; set; } = 32768;

    /// <summary>
    /// Authoring cap for local point lights this frame (8–1000, default 256). R7.5 clusters them
    /// into screen tiles (≤32 evaluated per pixel).
    /// </summary>
    public int SceneLocalLightCap { get; set; } = 256;

    /// <summary>
    /// AF1.3 omnidirectional shadow cubemap slots (0–4, default 1). Independent of
    /// <see cref="SceneLocalLightCap"/> — never a shadow map per torch.
    /// </summary>
    public int OmniShadowBudget { get; set; } = 1;

    /// <summary>
    /// AF1.5 local lights that get a volumetric beam (0–4, default 1). Ranked by the same camera
    /// weight as <see cref="OmniShadowBudget"/> — never a beam per torch.
    /// </summary>
    public int LocalVolumetricLightBudget { get; set; } = 1;

    /// <summary>Auto = unlimited world batches; Manual = enforce <see cref="WorldDrawBudget"/>.</summary>
    public string DrawCallMode { get; set; } = "Auto";

    /// <summary>Variable world-batch draw budget when <see cref="DrawCallMode"/> is Manual (1–1000).</summary>
    public int WorldDrawBudget { get; set; } = 1000;
}

public sealed class ShortcutSettings
{
    public Dictionary<string, string> Bindings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["File.NewProject"] = "Ctrl+Shift+N",
            ["File.OpenProject"] = "Ctrl+Shift+O",
            ["File.SaveAll"] = "Ctrl+Shift+S",
            ["Edit.Cut"] = "Ctrl+X",
            ["Edit.Copy"] = "Ctrl+C",
            ["Edit.Paste"] = "Ctrl+V",
            ["Edit.Rename"] = "F2",
            ["Edit.Delete"] = "Delete",
            ["Resource.NewFolder"] = "Ctrl+Alt+N",
            ["Run.Play"] = "F5",
            ["Run.Debug"] = "F6",
            ["Build.Export"] = "Ctrl+B",
            ["Help.Open"] = "F1",
        };
}

public sealed class RecentProject
{
    public required string Name { get; set; }

    public required string ProjectFile { get; set; }

    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;

    public bool IsPinned { get; set; }
}

using Genesis.Application.Core.Settings;

namespace Genesis.Application.Core.Projects;

public sealed class ProjectManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required string ProjectId { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public string ProjectIcon { get; set; } = string.Empty;

    public int ProjectIconFps { get; set; } = 15;

    public string Template { get; set; } = "Blank";

    /// <summary>Template-owned content revision used for narrow, backup-first template hotfixes.</summary>
    public int TemplateRevision { get; set; }

    public int ResourceNamesVersion { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public string StartRoom { get; set; } = "Start";

    public string DefaultNamespace { get; set; } = "Game";

    public List<string> EnabledPacks { get; set; } = ["core", "rendering", "physics"];

    /// <summary>How the built game behaves. Travels with the project, not the machine.</summary>
    public ProjectRuntimeSettings Runtime { get; set; } = new();

    /// <summary>Engine rendering defaults for this game, inherited by Rooms unless overridden.</summary>
    public ProjectRenderingSettings Rendering { get; set; } = new();
}

/// <summary>
/// Settings that belong to the game rather than to whoever is editing it.
/// </summary>
/// <remarks>
/// The distinction that decides where a setting lives: would you expect it to follow the project
/// to another machine, or to stay with this installation? "Can the player press Escape to quit"
/// is a decision about the game, so it is stored in the project file and ships with it. Which GPU
/// backend this Studio uses is a decision about this machine, so it stays in preferences.
/// </remarks>
public sealed class ProjectRuntimeSettings
{
    /// <summary>Whether pressing Escape closes the running game.</summary>
    public bool AllowEscapeToClose { get; set; } = true;

    /// <summary>Display name of the Default Texture Group (always <see cref="TextureGroupCatalog.DefaultName"/>).</summary>
    public string DefaultTextureGroupName { get; set; } = TextureGroupCatalog.DefaultName;

    /// <summary>Atlas edge length for the Default Texture Group (2048 or 4096).</summary>
    public int DefaultTextureGroupSize { get; set; } = TextureGroupCatalog.DefaultAtlasSize;

    /// <summary>
    /// Named atlas groups for this project. Always includes Default; custom groups are authored in
    /// the Image Viewer. Player stitches each group at start (R7.4).
    /// </summary>
    public List<TextureGroupDefinition> TextureGroups { get; set; } =
    [
        new TextureGroupDefinition
        {
            Name = TextureGroupCatalog.DefaultName,
            AtlasSize = TextureGroupCatalog.DefaultAtlasSize,
            IsDefault = true,
        }
    ];
}

/// <summary>Engine-level rendering defaults saved with the project.</summary>
public sealed class ProjectRenderingSettings
{
    public bool FogEnabled { get; set; }

    public string FogColorHex { get; set; } = "#9EC7F0";

    /// <summary>Fog depth/start. 3D: camera distance. 2D: authored Z/layer depth.</summary>
    public float FogStart { get; set; } = 35f;

    /// <summary>Persisted fog end; the designer UI exposes FogEnd - FogStart as thickness.</summary>
    public float FogEnd { get; set; } = 120f;

    /// <summary>Maximum fog blend opacity, 0..1.</summary>
    public float FogAlpha { get; set; } = 1f;

    /// <summary>
    /// Rendering backend this project drives the GPU with. Empty follows the installation setting.
    /// </summary>
    /// <remarks>
    /// Stored per project rather than only per installation because a project may depend on what a
    /// particular backend does — and because opening someone else's project should not silently
    /// re-render it through whichever backend this machine happens to prefer. An empty value is
    /// meaningfully different from a named one: it means "no opinion", not Direct3D 11.
    /// </remarks>
    public string Backend { get; set; } = string.Empty;

    /// <summary>AF1.2 project toggle for half-res GTAO (optional, default off).</summary>
    public bool GtaoEnabled { get; set; }

    /// <summary>AF1.4 project toggle for screen-space contact shadows (optional, default off).</summary>
    public bool ContactShadowsEnabled { get; set; }

    /// <summary>AF1.5 project toggle for local-light volumetric scatter (optional, default off).</summary>
    public bool LocalVolumetricsEnabled { get; set; }

    /// <summary>AF1.6 project toggle for particle smoke as extinction (optional, default off).</summary>
    public bool SmokeExtinctionEnabled { get; set; }

    /// <summary>AF1.7 project toggle for HDR bloom (optional, default off).</summary>
    public bool BloomEnabled { get; set; }

    /// <summary>AF2.1 project toggle for atmosphere LUT sky-view (optional, default off).</summary>
    public bool AtmosphereLutEnabled { get; set; }

    /// <summary>AF2.3 project toggle for raymarched 3D clouds (optional, default off).</summary>
    public bool RaymarchedCloudsEnabled { get; set; }

    /// <summary>AF2.4 project toggle for cloud temporal reprojection (optional, default off).</summary>
    public bool CloudTemporalEnabled { get; set; }

    /// <summary>
    /// AF2.5 project toggle for FogPost celestial extras (stars / Milky Way / moon; optional, default off).
    /// </summary>
    public bool CelestialExtrasEnabled { get; set; }

    /// <summary>
    /// AF2.4 project cloud quality 0..3 (Performance/Balanced/High/Cinematic). Default 2 = High.
    /// </summary>
    public int CloudQuality { get; set; } = 2;

    /// <summary>AF1.5 project override for the local volumetric light budget (0–4, default 1).</summary>
    public int LocalVolumetricLightBudget { get; set; } = 1;
}

public sealed record ProjectSession(
    string RootPath,
    string ProjectFile,
    ProjectManifest Manifest)
{
    public string AssetsPath => Path.Combine(RootPath, "Assets");

    public string ProjectSettingsPath => Path.Combine(RootPath, "ProjectSettings");

    public string InternalPath => Path.Combine(RootPath, ".genesis");
}

public sealed record ProjectValidationIssue(
    ProjectValidationSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

public enum ProjectValidationSeverity
{
    Information,
    Warning,
    Error,
}

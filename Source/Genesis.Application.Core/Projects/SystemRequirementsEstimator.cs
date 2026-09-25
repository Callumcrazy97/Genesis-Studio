using Genesis.Application.Core.Settings;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Core.Projects;

/// <summary>
/// One atlas sheet the canned example scene would stitch (R7.4 groups).
/// </summary>
public readonly record struct SystemRequirementsAtlasGroup(string Name, int AtlasSize);

/// <summary>
/// Inputs for <see cref="SystemRequirementsEstimator"/> — project/prefs knobs, never live PC metrics.
/// </summary>
public sealed class SystemRequirementsInput
{
    public RenderBackendOption Backend { get; init; } = RenderBackendOption.SilkNetDx11;
    public bool LightingEnabled { get; init; } = true;
    public bool ShadowsEnabled { get; init; } = true;
    public float ShadowStrength { get; init; } = 1f;
    public int SpriteInstanceCap { get; init; } = RenderCapacityDefaults.HardwareSpriteInstanceCap;
    public int MeshInstanceCap { get; init; } = RenderCapacityDefaults.HardwareMeshInstanceCap;
    public int SceneLocalLightCap { get; init; } = RenderCapacityDefaults.DefaultSceneLocalLightCap;
    public string DrawCallMode { get; init; } = RenderCapacityDefaults.DrawCallModeAuto;
    public int WorldDrawBudget { get; init; } = RenderCapacityDefaults.MaxWorldDrawBudget;
    public IReadOnlyList<SystemRequirementsAtlasGroup> TextureGroups { get; init; } =
        [new(TextureGroupCatalog.DefaultName, TextureGroupCatalog.DefaultAtlasSize)];
}

/// <summary>Read-only estimate for Preferences → Project → System requirements (R7.8).</summary>
public sealed class SystemRequirementsReport
{
    public required string ExampleSceneName { get; init; }
    public required string GpuModelLabel { get; init; }
    public required string BackendDisplayName { get; init; }
    public required string LightingPath { get; init; }
    public required string CpuLoadBand { get; init; }
    public required int EstimatedRamMb { get; init; }
    public required int EstimatedBuiltDiskMb { get; init; }
    public required int ExpectedFps { get; init; }
    public required int SceneLocalLightCap { get; init; }
    public required int SpriteInstanceCap { get; init; }
    public required int MeshInstanceCap { get; init; }
    public required string DrawBudgetSummary { get; init; }
    public required int TextureGroupCount { get; init; }
    public required int MaxAtlasSize { get; init; }
    public required bool ShadowsEnabled { get; init; }
    public required string SummaryText { get; init; }
}

/// <summary>
/// Example-scene system-requirements estimator. Pure formulas over authored caps — not a bench of
/// the authoring PC (R7.8).
/// </summary>
public static class SystemRequirementsEstimator
{
    public const string ExampleSceneName = "Canned outdoor showcase";
    public const string MidRangeGpuModel = "mid-range GPU (GeForce RTX 4060-class)";

    public static SystemRequirementsInput FromPreferences(
        RenderingSettings rendering,
        ProjectManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(rendering);

        string? projectBackend = manifest?.Rendering?.Backend;
        // Callers that already resolved the effective backend (Preferences combo) pass it on
        // RenderingSettings.Backend; only fall back to the project override when empty.
        string backendValue = string.IsNullOrWhiteSpace(rendering.Backend)
            ? (projectBackend ?? string.Empty)
            : rendering.Backend;
        RenderBackendOption backend = RenderBackendCatalog.ParseSettingsValue(backendValue);

        List<SystemRequirementsAtlasGroup> groups = [];
        if (manifest is not null)
        {
            TextureGroupCatalog.ApplyToManifest(manifest);
            foreach (TextureGroupDefinition group in manifest.Runtime!.TextureGroups)
            {
                groups.Add(new SystemRequirementsAtlasGroup(
                    group.Name,
                    TextureGroupCatalog.NormalizeAtlasSize(group.AtlasSize)));
            }
        }

        if (groups.Count == 0)
        {
            groups.Add(new SystemRequirementsAtlasGroup(
                TextureGroupCatalog.DefaultName,
                TextureGroupCatalog.DefaultAtlasSize));
        }

        return new SystemRequirementsInput
        {
            Backend = backend,
            LightingEnabled = rendering.LightingEnabled,
            ShadowsEnabled = rendering.ShadowsEnabled,
            ShadowStrength = rendering.ShadowStrength,
            SpriteInstanceCap = rendering.SpriteInstanceCap,
            MeshInstanceCap = rendering.MeshInstanceCap,
            SceneLocalLightCap = rendering.SceneLocalLightCap,
            DrawCallMode = rendering.DrawCallMode,
            WorldDrawBudget = rendering.WorldDrawBudget,
            TextureGroups = groups,
        };
    }

    public static SystemRequirementsReport Estimate(SystemRequirementsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int spriteCap = Math.Clamp(
            input.SpriteInstanceCap,
            RenderCapacityDefaults.MinInstanceCap,
            RenderCapacityDefaults.HardwareSpriteInstanceCap);
        int meshCap = Math.Clamp(
            input.MeshInstanceCap,
            RenderCapacityDefaults.MinInstanceCap,
            RenderCapacityDefaults.HardwareMeshInstanceCap);
        int lightCap = Math.Clamp(
            input.SceneLocalLightCap,
            RenderCapacityDefaults.MinSceneLocalLightCap,
            RenderCapacityDefaults.MaxSceneLocalLightCap);
        int worldBudget = Math.Clamp(
            input.WorldDrawBudget,
            RenderCapacityDefaults.MinWorldDrawBudget,
            RenderCapacityDefaults.MaxWorldDrawBudget);
        bool manualBudget = string.Equals(
            input.DrawCallMode,
            RenderCapacityDefaults.DrawCallModeManual,
            StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<SystemRequirementsAtlasGroup> groups = input.TextureGroups.Count > 0
            ? input.TextureGroups
            : [new(TextureGroupCatalog.DefaultName, TextureGroupCatalog.DefaultAtlasSize)];

        int maxAtlas = TextureGroupCatalog.DefaultAtlasSize;
        double atlasSheetUnits = 0;
        foreach (SystemRequirementsAtlasGroup group in groups)
        {
            int size = TextureGroupCatalog.NormalizeAtlasSize(group.AtlasSize);
            maxAtlas = Math.Max(maxAtlas, size);
            // 2048² sheet = 1 unit; 4096² = 4 units (RGBA8 footprint scale).
            atlasSheetUnits += (size / 2048.0) * (size / 2048.0);
        }

        bool software = input.Backend == RenderBackendOption.Software;
        string lightingPath = software
            ? "Forward (Software; strongest-8 EngineCB)"
            : "Clustered tiled (≤32 lights/tile)";

        // Canned outdoor showcase weights — scale with authored knobs, not host hardware.
        double lightFactor = lightCap / (double)RenderCapacityDefaults.DefaultSceneLocalLightCap;
        double meshFactor = meshCap / 8192.0;
        double spriteFactor = spriteCap / 16384.0;
        double shadowFactor = input.LightingEnabled && input.ShadowsEnabled
            ? 0.82 - 0.12 * Math.Clamp(input.ShadowStrength, 0f, 1f)
            : input.LightingEnabled ? 0.94 : 1.08;
        double budgetFactor = manualBudget
            ? 1.0 + (1.0 - worldBudget / (double)RenderCapacityDefaults.MaxWorldDrawBudget) * 0.08
            : 1.0;
        double backendFpsScale = BackendFpsScale(input.Backend);

        double fps = 96.0 * backendFpsScale * shadowFactor * budgetFactor
            / Math.Max(0.55, 0.55 + 0.28 * lightFactor + 0.12 * meshFactor + 0.05 * spriteFactor
                + 0.04 * atlasSheetUnits);
        int expectedFps = Math.Clamp((int)Math.Round(fps), software ? 8 : 24, software ? 45 : 144);

        int ramMb = 420
            + (int)Math.Round(lightCap * 0.35)
            + (int)Math.Round(meshCap / 48.0)
            + (int)Math.Round(spriteCap / 96.0)
            + (int)Math.Round(atlasSheetUnits * 48.0)
            + (input.ShadowsEnabled ? 64 : 0)
            + (software ? 128 : 48);
        ramMb = Math.Clamp(ramMb, 512, 8192);

        int diskMb = 160
            + (int)Math.Round(atlasSheetUnits * 36.0)
            + groups.Count * 8
            + (int)Math.Round(meshCap / 256.0)
            + (input.ShadowsEnabled ? 24 : 0);
        diskMb = Math.Clamp(diskMb, 180, 4096);

        double cpuScore = (software ? 6.0 : 1.2)
            + lightFactor * 1.4
            + meshFactor * 0.8
            + spriteFactor * 0.3
            + atlasSheetUnits * 0.25
            + (input.ShadowsEnabled ? 0.6 : 0.0)
            + (manualBudget && worldBudget < 200 ? -0.3 : 0.0);
        string cpuBand = cpuScore switch
        {
            < 2.5 => "Low",
            < 4.0 => "Moderate",
            < 6.5 => "High",
            _ => "Very high",
        };

        string drawBudget = manualBudget
            ? $"Manual world-draw budget {worldBudget}"
            : "Auto (unlimited world batches)";

        RenderBackendDescriptor descriptor = RenderBackendCatalog.Describe(input.Backend);
        string summary =
            $"{ExampleSceneName} on {descriptor.DisplayName} · {lightingPath}"
            + $"{Environment.NewLine}CPU load: {cpuBand} · RAM ≈ {ramMb} MB · Built game ≈ {diskMb} MB"
            + $"{Environment.NewLine}Expected FPS ≈ {expectedFps} on a {MidRangeGpuModel}"
            + $"{Environment.NewLine}Lights {lightCap} · Sprites {spriteCap} · Meshes {meshCap} · {drawBudget}"
            + $"{Environment.NewLine}Texture groups {groups.Count} (max atlas {maxAtlas}) · Shadows "
            + (input.ShadowsEnabled && input.LightingEnabled ? "on" : "off");

        return new SystemRequirementsReport
        {
            ExampleSceneName = ExampleSceneName,
            GpuModelLabel = MidRangeGpuModel,
            BackendDisplayName = descriptor.DisplayName,
            LightingPath = lightingPath,
            CpuLoadBand = cpuBand,
            EstimatedRamMb = ramMb,
            EstimatedBuiltDiskMb = diskMb,
            ExpectedFps = expectedFps,
            SceneLocalLightCap = lightCap,
            SpriteInstanceCap = spriteCap,
            MeshInstanceCap = meshCap,
            DrawBudgetSummary = drawBudget,
            TextureGroupCount = groups.Count,
            MaxAtlasSize = maxAtlas,
            ShadowsEnabled = input.LightingEnabled && input.ShadowsEnabled,
            SummaryText = summary,
        };
    }

    private static double BackendFpsScale(RenderBackendOption backend) => backend switch
    {
        RenderBackendOption.SilkNetDx11 => 1.00,
        RenderBackendOption.Direct3D12 => 1.05,
        RenderBackendOption.Vulkan => 1.00,
        RenderBackendOption.OpenGL => 0.92,
        RenderBackendOption.Software => 0.22,
        _ => 1.00,
    };
}

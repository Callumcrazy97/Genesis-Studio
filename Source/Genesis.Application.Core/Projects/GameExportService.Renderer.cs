using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Rendering.Core;

namespace Genesis.Application.Core.Projects;

public static partial class GameExportService
{
    /// <summary>The renderers an export can name in place of the project's own setting.</summary>
    public static IReadOnlyList<RenderBackendOption> RendererChoices { get; } =
    [
        RenderBackendOption.SilkNetDx11,
        RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan,
        RenderBackendOption.OpenGL,
    ];

    /// <summary>
    /// The renderer an exported game starts with when the export keeps the project's own setting:
    /// the one the project names (<see cref="ProjectRenderingSettings.Backend"/>), else Direct3D 11.
    /// </summary>
    /// <remarks>
    /// An empty setting follows the installation's preference, and an exported game has no Studio
    /// installation to follow: the Player's own default, Direct3D 11, stands. A name this engine does
    /// not know also leaves that default, as it does in the Player.
    /// </remarks>
    public static RenderBackendOption ProjectRenderer(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return RenderBackendCatalog.ParseSettingsValue(manifest.Rendering?.Backend ?? string.Empty);
    }

    /// <summary>
    /// Names <paramref name="renderer"/> as <c>rendering.backend</c> in the exported copy of the project
    /// file, which the Player reads when the game starts. The project's own file is left as it is, and
    /// so is every other value in the copy.
    /// </summary>
    private static void WriteExportedRenderer(string projectRoot, string projectFile, string staging, RenderBackendOption renderer)
    {
        string exported = Path.Combine(staging, Path.GetRelativePath(projectRoot, Path.GetFullPath(projectFile)));
        if (!File.Exists(exported))
            throw new InvalidOperationException(
                "The project file was left out of the export, so the game cannot carry the chosen renderer.");

        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(exported)) as JsonObject
                ?? throw new InvalidDataException("The exported project file does not hold a project.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The exported project file could not be read: " + error.Message, error);
        }

        if (manifest["rendering"] is not JsonObject rendering)
            manifest["rendering"] = rendering = new JsonObject();
        rendering["backend"] = RenderBackendCatalog.Describe(renderer).SettingsValue;
        File.WriteAllText(exported, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

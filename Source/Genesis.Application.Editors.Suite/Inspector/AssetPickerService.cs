using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Inspector;

public sealed record AssetPickerRequest(
    string ProjectRoot,
    ResourceKind Kind,
    string? CurrentValue = null,
    string? Title = null,
    Func<ProjectAssetEntry, Control?>? ParameterPanelFactory = null,
    bool AllowNone = false,
    ImageUsage RequiredImageUsage = ImageUsage.None);

/// <summary>One typed entry point for every editor resource-reference field.</summary>
public static class AssetPickerService
{
    private const int RecentLimit = 12;
    private static readonly object RecentGate = new();
    private static readonly Dictionary<string, List<string>> Recent =
        new(StringComparer.OrdinalIgnoreCase);

    public static ProjectAssetEntry? PickAsset(AssetPickerRequest request, IWin32Window? parent = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<ProjectAssetEntry> entries = request.Kind == ResourceKind.Image
            && request.RequiredImageUsage != ImageUsage.None
                ? ProjectAssetIndex.EnumerateImagesForUsage(request.ProjectRoot, request.RequiredImageUsage)
                : ProjectAssetIndex.Enumerate(request.ProjectRoot, request.Kind);
        string key = RecentKey(request.ProjectRoot, request.Kind);
        string[] recent;
        lock (RecentGate)
        {
            recent = Recent.TryGetValue(key, out List<string>? values) ? values.ToArray() : [];
        }

        using AssetPickerModal modal = new(request, entries, recent);
        if (modal.ShowDialog(parent) != DialogResult.OK || modal.SelectedAsset is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(modal.SelectedAsset.Reference))
        {
            Remember(key, modal.SelectedAsset.Reference);
        }
        return modal.SelectedAsset;
    }

    public static ProjectAssetEntry? PickImage(string projectRoot, IWin32Window? parent = null, string? current = null) =>
        PickAsset(new AssetPickerRequest(projectRoot, ResourceKind.Image, current), parent);

    public static ProjectAssetEntry? PickBackgroundImage(string projectRoot, IWin32Window? parent = null, string? current = null) =>
        PickAsset(new AssetPickerRequest(projectRoot, ResourceKind.Image, current, "Select Background Image",
            RequiredImageUsage: ImageUsage.Background), parent);

    public static ProjectAssetEntry? PickModel(string projectRoot, IWin32Window? parent = null, string? current = null) =>
        PickAsset(new AssetPickerRequest(projectRoot, ResourceKind.Model, current), parent);

    public static ProjectAssetEntry? PickSound(string projectRoot, IWin32Window? parent = null, string? current = null) =>
        PickAsset(new AssetPickerRequest(projectRoot, ResourceKind.Audio, current), parent);

    public static ProjectAssetEntry? PickShader(string projectRoot, IWin32Window? parent = null, string? current = null) =>
        PickAsset(new AssetPickerRequest(projectRoot, ResourceKind.Shader, current), parent);

    public static ProjectAssetEntry? PickObject(string projectRoot, IWin32Window? parent = null, string? current = null) =>
        PickAsset(new AssetPickerRequest(projectRoot, ResourceKind.GameObject, current), parent);

    /// <summary>Compatibility wrapper returning the canonical resource name.</summary>
    public static string? PromptForAsset(string projectRoot, ResourceKind kind, IWin32Window parent)
    {
        return PickAsset(new AssetPickerRequest(projectRoot, kind), parent)?.Reference;
    }

    private static string RecentKey(string projectRoot, ResourceKind kind) =>
        Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + "|" + kind;

    private static void Remember(string key, string path)
    {
        lock (RecentGate)
        {
            if (!Recent.TryGetValue(key, out List<string>? values))
            {
                values = [];
                Recent[key] = values;
            }
            values.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
            values.Insert(0, path);
            if (values.Count > RecentLimit) values.RemoveRange(RecentLimit, values.Count - RecentLimit);
        }
    }
}

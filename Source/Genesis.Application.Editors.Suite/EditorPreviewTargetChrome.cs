using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;

namespace Genesis.Application.Editors.Suite;

/// <summary>Shared Model / Image / Object preview-target picker for Suite editor toolbars.</summary>
internal static class EditorPreviewTargetChrome
{
    public enum PreviewTargetKind
    {
        None,
        Terrain,
        Model,
        Image,
        Object,
    }

    public sealed class PreviewTargetBinding
    {
        public required Func<PreviewTargetKind> ReadKind { get; init; }
        public required Func<string> ReadPath { get; init; }
        public required Action<PreviewTargetKind, string> Write { get; init; }
        public required string ProjectRoot { get; init; }
        public IWin32Window? Owner { get; init; }
    }

    public sealed class PreviewTargetControls
    {
        public required ThemedComboBox KindCombo { get; init; }
        public required ToolStripButton BrowseButton { get; init; }
        public required ToolStripLabel PathLabel { get; init; }

        public void Sync(PreviewTargetKind kind, string path)
    {
        if (!KindCombo.Items.Contains(kind))
            KindCombo.Items.Add(kind);
        KindCombo.SelectedItem = kind;
        string display = string.IsNullOrWhiteSpace(path)
            ? "(none)"
            : EditorViewportChrome.DisplayAssetName(path);
        PathLabel.Text = display;
        PathLabel.ToolTipText = string.IsNullOrWhiteSpace(path) ? "No preview target" : path;
    }
    }

    public static PreviewTargetControls AddTo(ToolStrip toolbar, PreviewTargetBinding binding)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(binding);

        toolbar.Items.Add(new ToolStripLabel("Target")
        {
            ForeColor = EditorChrome.Muted,
            Margin = new Padding(8, 0, 4, 0),
        });

        ThemedComboBox kindCombo = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FormattingEnabled = true,
            Width = 88,
        };
        kindCombo.Format += (_, args) =>
        {
            if (args.ListItem is PreviewTargetKind.None)
                args.Value = "Free";
        };
        foreach (PreviewTargetKind kind in Enum.GetValues<PreviewTargetKind>())
            kindCombo.Items.Add(kind);

        ToolStripControlHost kindHost = new(kindCombo)
        {
            AutoSize = false,
            Margin = new Padding(0, 4, 4, 0),
            Size = new Size(92, 28),
        };
        toolbar.Items.Add(kindHost);

        ToolStripLabel pathLabel = new("(none)")
        {
            AutoSize = false,
            ForeColor = EditorChrome.Text,
            Margin = new Padding(0, 2, 4, 0),
            Width = 140,
        };
        toolbar.Items.Add(pathLabel);

        ToolStripButton browse = EditorChrome.ToolButton(
            "Browse…",
            "Pick a Terrain, Model, Image, or Object to preview against this editor",
            () => Browse(binding, kindCombo, pathLabel));
        toolbar.Items.Add(browse);

        ToolStripButton clear = EditorChrome.ToolButton(
            "Clear",
            "Clear the preview target",
            () =>
            {
                binding.Write(PreviewTargetKind.None, string.Empty);
                SyncLabel(pathLabel, string.Empty);
                kindCombo.SelectedItem = PreviewTargetKind.None;
            });
        toolbar.Items.Add(clear);

        PreviewTargetControls controls = new()
        {
            KindCombo = kindCombo,
            BrowseButton = browse,
            PathLabel = pathLabel,
        };

        bool syncing = false;
        kindCombo.SelectedIndexChanged += (_, _) =>
        {
            if (syncing) return;
            if (kindCombo.SelectedItem is not PreviewTargetKind kind) return;
            if (kind == PreviewTargetKind.None)
            {
                binding.Write(PreviewTargetKind.None, string.Empty);
                SyncLabel(pathLabel, string.Empty);
                return;
            }

            string path = binding.ReadPath();
            if (binding.ReadKind() != kind)
                path = string.Empty;
            binding.Write(kind, path);
            SyncLabel(pathLabel, path);
        };

        syncing = true;
        try { controls.Sync(binding.ReadKind(), binding.ReadPath()); }
        finally { syncing = false; }
        return controls;
    }

    private static void Browse(
        PreviewTargetBinding binding,
        ThemedComboBox kindCombo,
        ToolStripLabel pathLabel)
    {
        PreviewTargetKind kind = kindCombo.SelectedItem is PreviewTargetKind selected
            ? selected
            : PreviewTargetKind.Model;
        if (kind == PreviewTargetKind.None)
            kind = PreviewTargetKind.Model;

        ProjectAssetEntry? picked = kind switch
        {
            PreviewTargetKind.Terrain => AssetPickerService.PickAsset(
                new AssetPickerRequest(binding.ProjectRoot, Genesis.Application.Core.Resources.ResourceKind.Terrain, binding.ReadPath(), "Choose Terrain Preview Target"), binding.Owner),
            PreviewTargetKind.Image => AssetPickerService.PickImage(
                binding.ProjectRoot, binding.Owner, binding.ReadPath()),
            PreviewTargetKind.Object => AssetPickerService.PickObject(
                binding.ProjectRoot, binding.Owner, binding.ReadPath()),
            _ => AssetPickerService.PickModel(
                binding.ProjectRoot, binding.Owner, binding.ReadPath()),
        };
        if (picked is null) return;

        kindCombo.SelectedItem = kind;
        binding.Write(kind, picked.FullPath);
        SyncLabel(pathLabel, picked.FullPath);
    }

    private static void SyncLabel(ToolStripLabel label, string path)
    {
        label.Text = string.IsNullOrWhiteSpace(path) ? "(none)" : EditorViewportChrome.DisplayAssetName(path);
        label.ToolTipText = string.IsNullOrWhiteSpace(path) ? "No preview target" : path;
    }

    public static PreviewTargetKind ParseKind(string? kind) =>
        Enum.TryParse(kind, ignoreCase: true, out PreviewTargetKind parsed)
            ? parsed
            : string.Equals(kind, "GameObject", StringComparison.OrdinalIgnoreCase)
                ? PreviewTargetKind.Object
                : PreviewTargetKind.None;

    public static string FormatKind(PreviewTargetKind kind) => kind switch
    {
        PreviewTargetKind.None => string.Empty,
        PreviewTargetKind.Object => "Object",
        _ => kind.ToString(),
    };
}

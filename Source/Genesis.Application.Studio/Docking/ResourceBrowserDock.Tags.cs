using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;

namespace Genesis.Application.Studio.Docking;

/// <summary>Library classification, exact tag filters and session-local metadata undo.</summary>
public sealed partial class ResourceBrowserDock
{
    private readonly ResourceLibraryTagHistory _tagHistory = new();
    private readonly ToolStripDropDownButton _tagFilterButton = new("All tags")
    { Name = "ResourceLibraryTagFilter", ToolTipText = "Filter library tags. Gameplay and animation tags are separate." };
    private string? _requiredLibraryTag;
    private bool _untaggedOnly;

    internal void SetLibraryTagFilter(string? tag, bool untaggedOnly = false)
    {
        _requiredLibraryTag = tag;
        _untaggedOnly = untaggedOnly;
        _tagFilterButton.Text = untaggedOnly ? "Untagged" : tag is null ? "All tags" : "Tag: " + tag;
        RenderTree();
    }

    private void BuildTagFilterMenu()
    {
        foreach (ToolStripItem item in _tagFilterButton.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _tagFilterButton.DropDownItems.Clear();
        ToolStripMenuItem all = new("All tags") { Checked = _requiredLibraryTag is null && !_untaggedOnly };
        all.Click += (_, _) => SetLibraryTagFilter(null);
        ToolStripMenuItem empty = new("Untagged resources") { Checked = _untaggedOnly };
        empty.Click += (_, _) => SetLibraryTagFilter(null, untaggedOnly: true);
        _tagFilterButton.DropDownItems.Add(all); _tagFilterButton.DropDownItems.Add(empty);
        _tagFilterButton.DropDownItems.Add(new ToolStripSeparator());
        if (_treeSnapshot is null) return;
        var counts = EnumerateResources(_treeSnapshot).Where(item => !item.IsFolder)
            .SelectMany(item => item.LibraryTags).GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Tag: group.Key, Count: group.Count()))
            .OrderBy(group => group.Tag, StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
        foreach (var row in counts)
        {
            string tag = row.Tag;
            ToolStripMenuItem choice = new($"{tag} ({row.Count})")
            { Checked = !_untaggedOnly && string.Equals(_requiredLibraryTag, tag, StringComparison.OrdinalIgnoreCase) };
            choice.Click += (_, _) => SetLibraryTagFilter(tag);
            _tagFilterButton.DropDownItems.Add(choice);
        }
        _tagFilterButton.DropDownItems.Add(new ToolStripSeparator());
        _tagFilterButton.DropDownItems.Add("Search tags…", null, (_, _) =>
        { _search.Focus(); _search.Text = "tag:"; _search.SelectionStart = _search.Text.Length; });
    }

    private void EditLibraryTags()
    {
        if (SelectedResource is not { IsFolder: false } resource) return;
        ResourceLibraryTagSnapshot expected;
        try { expected = ResourceLibraryTags.Capture(_resources.AssetsRoot, resource.FullPath, resource.AssetId); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, error.Message, "Library tags unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string[] vocabulary = _treeSnapshot is null ? [] : EnumerateResources(_treeSnapshot)
            .SelectMany(item => item.LibraryTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using ResourceLibraryTagsDialog dialog = new(resource.Name, expected.Tags, vocabulary);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (ResourceLibraryTags.Equal(expected.Tags, dialog.SelectedTags)) return;
        ExecuteGuarded(() => ApplyLibraryTagEdit(expected, dialog.SelectedTags).ResourcePath,
            "Library tags saved for " + resource.Name + ".");
    }

    internal ResourceLibraryTagSnapshot ApplyLibraryTagEdit(ResourceLibraryTagSnapshot expected, IEnumerable<string> tags)
    {
        ResourceLibraryTagSnapshot changed = _resources.SetLibraryTags(expected, tags);
        _tagHistory.Record(expected, changed);
        return changed;
    }

    internal ResourceLibraryTagSnapshot RestoreLibraryTagEdit(bool redo)
    {
        ResourceNames.Invalidate(_resources.Project.RootPath);
        var catalog = ResourceNames.For(_resources.Project.RootPath);
        string? Resolve(Guid id) => catalog.Entries.FirstOrDefault(entry => entry.AssetId == id)?.FullPath;
        return redo ? _tagHistory.Redo(_resources.AssetsRoot, Resolve) : _tagHistory.Undo(_resources.AssetsRoot, Resolve);
    }

    private void UndoLibraryTags(bool redo)
    {
        ExecuteGuarded(() => RestoreLibraryTagEdit(redo).ResourcePath,
            redo ? "Library tag edit redone." : "Library tag edit undone.");
    }

    private static string TagSummary(ResourceItem resource) => resource.IsFolder || resource.LibraryTags.Count == 0
        ? string.Empty : "\nLibrary tags: " + string.Join(", ", resource.LibraryTags);
}

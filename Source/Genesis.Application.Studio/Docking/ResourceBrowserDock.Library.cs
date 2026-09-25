using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Docking;

/// <summary>Bookmark views and in-place tree reconciliation; independent of resource serialization.</summary>
public sealed partial class ResourceBrowserDock
{
    private readonly ResourcePickerPreferences _preferences;
    private readonly ToolStripDropDownButton _scopeButton = new("All assets");
    private readonly ToolStripDropDownButton _typeButton = new("All types");
    private readonly ToolStripButton _favouriteButton = new("☆") { Enabled = false };
    private readonly ToolStripLabel _resultCount = new();
    private readonly Dictionary<ResourceBrowserScope, ToolStripMenuItem> _scopeItems = [];
    private readonly Dictionary<ResourceKind, ToolStripMenuItem> _typeItems = [];
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _visibleNodes = new(StringComparer.OrdinalIgnoreCase);
    private ResourceBrowserScope _scope;
    private ResourceKind? _kindFilter;
    private bool _updatingTree;
    private string? _selectionKey;
    private string? _lastNotifiedIdentity;
    private int _resourceOperationDepth;
    private long _snapshotGeneration;
    private long _renderedPreferenceRevision = -1;

    internal TreeView BrowserTree => _tree;
    internal ResourceBrowserScope LibraryScope => _scope;
    private int _visibleResourceCount;
    internal int VisibleResourceCount => _visibleResourceCount;
    internal ToolStripButton FavouriteButton => _favouriteButton;
    internal long SnapshotGeneration => _snapshotGeneration;

    public event EventHandler? ResourceSelectionCleared;
    public event EventHandler? FiltersResetRequested;

    private ToolStrip BuildLibraryBar()
    {
        ToolStrip strip = new()
        {
            Name = "ResourceLibraryViews", GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(6, 0, 6, 5), BackColor = ThemeService.Palette.Surface,
            Renderer = ThemeService.CreateToolStripRenderer(),
        };
        _scopeButton.Name = "ResourceLibraryScope";
        _scopeButton.ToolTipText = "All assets, shared favourites or recently opened/selected resources";
        AddScope("All assets", ResourceBrowserScope.All, ResourceBrowserCommand.ShowAll);
        AddScope("Favourites", ResourceBrowserScope.Favourites, ResourceBrowserCommand.ShowFavourites);
        AddScope("Recent", ResourceBrowserScope.Recent, ResourceBrowserCommand.ShowRecent);
        _scopeButton.DropDownItems.Add(new ToolStripSeparator());
        _scopeButton.DropDownItems.Add("Clear recent history", null, (_, _) => Execute(ResourceBrowserCommand.ClearRecent));
        _scopeButton.DropDownOpening += (_, _) => RefreshBookmarks();
        _typeButton.Name = "ResourceLibraryType";
        _typeButton.ToolTipText = "Filter by resource type, even without a search term";
        _typeButton.DropDownItems.Add("All types", null, (_, _) => SetKindFilter(null));
        _typeButton.DropDownItems.Add(new ToolStripSeparator());
        foreach (ResourceDefinition definition in ResourceDefinitions.All)
        {
            ResourceKind kind = definition.Kind;
            if (_typeItems.ContainsKey(kind)) continue;
            ToolStripMenuItem item = new(definition.DisplayName);
            item.Click += (_, _) => SetKindFilter(kind);
            _typeItems.Add(kind, item); _typeButton.DropDownItems.Add(item);
        }
        _favouriteButton.Name = "ResourceLibraryFavourite";
        _favouriteButton.ToolTipText = "Add selected resource to Favourites (does not modify the resource)";
        _favouriteButton.Click += (_, _) => Execute(ResourceBrowserCommand.ToggleFavourite);
        _resultCount.Name = "ResourceLibraryCount";
        strip.Items.Add(_scopeButton); strip.Items.Add(_typeButton);
        _tagFilterButton.DropDownOpening += (_, _) => BuildTagFilterMenu();
        strip.Items.Add(_tagFilterButton);
        ToolStripButton reset = new("Reset") { Name = "ResourceLibraryReset", ToolTipText = "Show all resources and clear every browser/Finder filter" };
        reset.Click += (_, _) => Execute(ResourceBrowserCommand.ResetFilters);
        strip.Items.Add(_favouriteButton); strip.Items.Add(reset); strip.Items.Add(_resultCount);
        return strip;
    }

    private void AddScope(string title, ResourceBrowserScope scope, ResourceBrowserCommand command)
    {
        ToolStripMenuItem item = new(title) { Checked = scope == ResourceBrowserScope.All };
        item.Click += (_, _) => Execute(command);
        _scopeItems.Add(scope, item); _scopeButton.DropDownItems.Add(item);
    }

    internal void SetScope(ResourceBrowserScope scope)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        _preferences.Reload();
        _scope = scope;
        _scopeButton.Text = scope switch { ResourceBrowserScope.Favourites => "Favourites", ResourceBrowserScope.Recent => "Recent", _ => "All assets" };
        foreach ((ResourceBrowserScope key, ToolStripMenuItem item) in _scopeItems) item.Checked = key == scope;
        RenderTree();
    }

    internal void SetKindFilter(ResourceKind? kind)
    {
        _kindFilter = kind;
        _typeButton.Text = kind is { } value ? ResourceDefinitions.Get(value).DisplayName : "All types";
        foreach ((ResourceKind key, ToolStripMenuItem item) in _typeItems) item.Checked = key == kind;
        RenderTree();
    }

    internal void RefreshBookmarks(bool render = true)
    {
        if (IsDisposed || _browserDisposed) return;
        if (!_preferences.Reload())
        {
            _scopeButton.ToolTipText = "Preferences could not be refreshed: " + _preferences.LastError;
            return;
        }
        _scopeButton.ToolTipText = "All assets, shared favourites or recently opened/selected resources";
        if (_renderedPreferenceRevision != _preferences.SnapshotRevision && render && _treeSnapshot is not null) RenderTree();
        else UpdateFavouriteButton();
    }

    /// <summary>Called only after the shell successfully opens/activates an editor, not on highlight.</summary>
    internal void RememberOpened(ResourceItem resource)
    {
        if (resource.IsFolder || IsDisposed) return;
        if (!_preferences.Remember(resource.AssetId, resource.Name))
        { StatusMessage?.Invoke(this, "Opened " + resource.Name + "; recent history was not saved: " + _preferences.LastError); return; }
        if (_scope == ResourceBrowserScope.Recent) RenderTree();
    }

    private void ToggleFavourite()
    {
        if (SelectedResource is not { IsFolder: false } resource) return;
        if (!_preferences.Reload()) { StatusMessage?.Invoke(this, _preferences.LastError); return; }
        bool add = !_preferences.IsFavourite(resource.AssetId, resource.Name);
        if (!_preferences.SetFavourite(resource.AssetId, resource.Name, add))
        { StatusMessage?.Invoke(this, "Favourite was not saved: " + _preferences.LastError); return; }
        RenderTree();
        StatusMessage?.Invoke(this, resource.Name + (add ? " added to Favourites." : " removed from Favourites."));
    }

    internal void ResetLibraryFilters()
    {
        _finderTerm = string.Empty; _finderResults.Clear();
        _kindFilter = null; _typeButton.Text = "All types";
        _requiredLibraryTag = null; _untaggedOnly = false; _tagFilterButton.Text = "All tags";
        foreach (ToolStripMenuItem item in _typeItems.Values) item.Checked = false;
        _search.Text = string.Empty;
        SetScope(ResourceBrowserScope.All);
        FiltersResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRecentResources()
    {
        if (!_preferences.ClearRecent()) { StatusMessage?.Invoke(this, _preferences.LastError); return; }
        RenderTree(); StatusMessage?.Invoke(this, "Recent resource history cleared. Favourites are unchanged.");
    }

    private static IEnumerable<ResourceItem> EnumerateResources(ResourceItem root)
    {
        yield return root;
        foreach (ResourceItem child in root.Children)
            foreach (ResourceItem item in EnumerateResources(child)) yield return item;
    }

    private void OnResourceServiceChanged(object? sender, ResourceChangedEventArgs args)
    {
        if (IsDisposed) return;
        _pathToSelect = args.Path;
        if (_resourceOperationDepth > 0) return;
        RefreshTree(); FinderInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void RememberDisclosure(TreeNode? node, bool expanded)
    {
        if (_updatingTree || _scope != ResourceBrowserScope.All || _kindFilter is not null
            || _requiredLibraryTag is not null || _untaggedOnly
            || _search.Text.Trim().Length > 0 || _finderTerm.Length > 0 || node?.Tag is not ResourceItem item) return;
        string key = ResourceBrowserProjection.Key(item);
        if (expanded) _expandedFolders.Add(key); else _expandedFolders.Remove(key);
    }

    private void RenderTree()
    {
        if (_treeSnapshot is null || IsDisposed || _browserDisposed) return;
        ResourceBrowserQuery query = new(_scope, _kindFilter, _search.Text,
            _finderTerm.Length > 0 ? new HashSet<string>(_finderResults.Keys, StringComparer.OrdinalIgnoreCase) : null,
            _requiredLibraryTag, _untaggedOnly);
        ResourceBrowserView view = ResourceBrowserProjection.Build(_treeSnapshot, query, _preferences);
        string? wantedPath = _pathToSelect; _pathToSelect = null;
        if (!string.IsNullOrWhiteSpace(wantedPath))
        {
            ResourceItem? selected = EnumerateResources(_treeSnapshot).FirstOrDefault(item =>
                string.Equals(item.FullPath, wantedPath, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) _selectionKey = ResourceBrowserProjection.Key(selected);
        }
        string? topKey = _tree.TopNode?.Tag is ResourceItem top ? ResourceBrowserProjection.Key(top) : null;
        _updatingTree = true; _tree.BeginUpdate();
        try
        {
            _visibleNodes.Clear();
            Reconcile(_tree.Nodes, [view.Root]);
            TreeNode root = _tree.Nodes[0];
            root.Text = _scope switch { ResourceBrowserScope.Favourites => "Favourites", ResourceBrowserScope.Recent => "Recent", _ => _treeSnapshot.Name };
            if (!root.IsExpanded) root.Expand();
            RestoreDisclosure(root, view.Filtered);
            TreeNode? selection = _selectionKey is not null ? _visibleNodes.GetValueOrDefault(_selectionKey) : null;
            if (!ReferenceEquals(_tree.SelectedNode, selection)) _tree.SelectedNode = selection;
            // Ordinary refresh must not jump to the selected row or expand its collapsed parent.
            if (!string.IsNullOrWhiteSpace(wantedPath)) _tree.SelectedNode?.EnsureVisible();
            else if (topKey is not null && _visibleNodes.TryGetValue(topKey, out TreeNode? anchor)
                && !ReferenceEquals(_tree.TopNode, anchor) && HasExpandedParents(anchor))
                _tree.TopNode = anchor;
            _renderedPreferenceRevision = _preferences.SnapshotRevision;
            _visibleResourceCount = view.VisibleResources;
            _resultCount.Text = ResourceLibraryQuery.Parse(_search.Text).Error is not null
                ? "Check filter" : $"{view.VisibleResources} / {view.TotalResources}";
            _resultCount.ToolTipText = ResourceLibraryQuery.Parse(_search.Text).Error ?? (view.VisibleResources == 0
                ? "No matching resources. Change scope/type or clear the name/Finder filters."
                : "Visible resources / total project resources");
        }
        finally { _tree.EndUpdate(); _updatingTree = false; }
        NotifyBrowserSelection(); _tree.Invalidate();
    }

    private static bool HasExpandedParents(TreeNode node)
    {
        for (TreeNode? parent = node.Parent; parent is not null; parent = parent.Parent)
            if (!parent.IsExpanded) return false;
        return true;
    }

    private void Reconcile(TreeNodeCollection nodes, IReadOnlyList<ResourceBrowserRow> rows)
    {
        Dictionary<string, TreeNode> old = nodes.Cast<TreeNode>()
            .Where(node => node.Tag is ResourceItem)
            .ToDictionary(node => ResourceBrowserProjection.Key((ResourceItem)node.Tag!), StringComparer.OrdinalIgnoreCase);
        HashSet<string> wanted = rows.Select(row => ResourceBrowserProjection.Key(row.Resource)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode stale in nodes.Cast<TreeNode>().Where(node => node.Tag is not ResourceItem item
            || !wanted.Contains(ResourceBrowserProjection.Key(item))).ToArray()) stale.Remove();
        for (int i = 0; i < rows.Count; i++)
        {
            ResourceBrowserRow row = rows[i]; ResourceItem item = row.Resource;
            string key = ResourceBrowserProjection.Key(item);
            TreeNode node = old.GetValueOrDefault(key) ?? new TreeNode();
            if (node.TreeView != _tree || node.Index != i)
            { node.Remove(); nodes.Insert(i, node); }
            if (node.Text != item.Name) node.Text = item.Name;
            node.Name = item.FullPath; node.Tag = item;
            node.ToolTipText = item.IsProtectedRoot && item.AllowedResourceKind is { } allowed
                ? $"Protected root — {ResourceDefinitions.Get(allowed).DisplayName} resources only. Cannot be renamed, moved or deleted."
                : FinderToolTip(item) + TagSummary(item);
            _visibleNodes[key] = node;
            Reconcile(node.Nodes, row.Children);
        }
    }

    private void RestoreDisclosure(TreeNode node, bool filtered)
    {
        foreach (TreeNode child in node.Nodes)
        {
            if (child.Tag is ResourceItem item && item.IsFolder)
            {
                bool expanded = filtered || _expandedFolders.Contains(ResourceBrowserProjection.Key(item));
                if (expanded && !child.IsExpanded) child.Expand();
                else if (!expanded && child.IsExpanded) child.Collapse(true);
            }
            RestoreDisclosure(child, filtered);
        }
    }

    private void NotifyBrowserSelection()
    {
        UpdateFavouriteButton();
        ResourceItem? item = SelectedResource;
        string? identity = item is null ? null : ResourceBrowserProjection.Key(item) + "\n" + item.Name + "\n" + item.FullPath;
        if (string.Equals(identity, _lastNotifiedIdentity, StringComparison.Ordinal)) return;
        _lastNotifiedIdentity = identity;
        if (item is null) ResourceSelectionCleared?.Invoke(this, EventArgs.Empty);
        else ResourceSelected?.Invoke(this, new ResourceSelectedEventArgs(item));
    }

    private void UpdateFavouriteButton()
    {
        ResourceItem? item = SelectedResource;
        _favouriteButton.Enabled = item is { IsFolder: false };
        bool favourite = item is { IsFolder: false } && _preferences.IsFavourite(item.AssetId, item.Name);
        _favouriteButton.Text = favourite ? "★" : "☆";
        _favouriteButton.ToolTipText = favourite ? "Remove selected resource from Favourites" : "Add selected resource to Favourites";
    }
}

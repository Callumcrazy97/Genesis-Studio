using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using DrawingImage = System.Drawing.Image;

namespace Genesis.Application.Editors.Suite.Inspector;

/// <summary>
/// Shared, typed asset browser. Results are virtualized and emit canonical resource names,
/// and can host action-specific parameters without each editor inventing another picker form.
/// </summary>
public sealed partial class AssetPickerModal : DpiAwareForm
{
    private readonly AssetPickerRequest _request;
    private readonly IReadOnlyList<ProjectAssetEntry> _allAssets;
    private readonly HashSet<string> _recent;
    private readonly ResourcePickerPreferences _preferences;
    private readonly Button _favourite = new();
    private bool _filtering;
    private string? _selectedReference;
    private readonly TextBox _searchBox = new();
    private readonly TreeView _folders = new();
    private readonly ListView _results = new();
    private readonly ImageList _thumbnails = new();
    private readonly Label _resultCount = new();
    private readonly PictureBox _preview = new();
    private readonly Label _previewPlaceholder = new();
    private readonly Label _assetName = new();
    private readonly Label _assetPath = new();
    private readonly Label _libraryTags = new();
    private readonly ToolTip _assetToolTip = new();
    private readonly Panel _parameterHost = new();
    private readonly Button _select = new();
    private readonly Button _none = new();
    private readonly Button _cancel = new();
    private readonly List<ProjectAssetEntry> _filtered = [];
    private readonly Dictionary<string, int> _thumbnailIndices = new(StringComparer.OrdinalIgnoreCase);
    private string _scope = "$all";

    public AssetPickerModal(
        AssetPickerRequest request,
        IReadOnlyList<ProjectAssetEntry> availableAssets,
        IEnumerable<string>? recentPaths = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        // A picker is a typed reference editor even when an extension supplies a mixed
        // project index. Scope both folder discovery and every result mode to this kind.
        _allAssets = (availableAssets ?? throw new ArgumentNullException(nameof(availableAssets)))
            .Where(entry => entry.Kind == _request.Kind
                && (_request.RequiredImageUsage == ImageUsage.None
                    || ProjectAssetIndex.SupportsImageUsage(entry, _request.RequiredImageUsage)))
            .ToArray();
        _recent = new HashSet<string>(recentPaths ?? [], StringComparer.OrdinalIgnoreCase);
        _preferences = new ResourcePickerPreferences(request.ProjectRoot);

        ResourceDefinition? definition = ResourceDefinitions.All.FirstOrDefault(item => item.Kind == request.Kind);
        Text = request.Title ?? $"Select {definition?.DisplayName ?? request.Kind.ToString()}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1040, 650);
        MinimumSize = new Size(780, 500);
        BackColor = Color.FromArgb(20, 20, 24);
        ForeColor = EditorChrome.Text;

        SplitContainer browser = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 190,
            SplitterWidth = 1,
            BackColor = Color.FromArgb(48, 48, 56),
        };
        BuildFolderPanel(browser.Panel1, definition);

        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.None,
            SplitterDistance = 520,
            SplitterWidth = 1,
            BackColor = Color.FromArgb(48, 48, 56),
        };
        split.Panel1.Padding = new Padding(12);
        split.Panel2.Padding = new Padding(12);
        BuildResultsPanel(split.Panel1);
        InitializePreviewLoading();
        BuildPreviewPanel(split.Panel2, definition);

        Panel actions = BuildActions();
        browser.Panel2.Controls.Add(split);
        Controls.Add(browser);
        Controls.Add(actions);
        // Docked siblings are laid out back-to-front; Fill must remain z-order zero so the
        // bottom action bar reserves its strip instead of being covered by the split view.
        browser.BringToFront();

        AcceptButton = _select;
        CancelButton = _cancel;

        FilterAssets();
        Shown += (_, _) =>
        {
            _searchBox.Focus();
            if (_thumbnailQueue.Count > 0) _thumbnailTimer.Start();
            SelectCurrentValue();
            if (_pendingPreview is not null && !_previewBusy) ProcessSelectionPreview();
        };
    }

    public ProjectAssetEntry? SelectedAsset { get; private set; }

    public IReadOnlyList<ProjectAssetEntry> FilteredAssets => _filtered.ToArray();

    /// <summary>Programmatic filter used by accessibility hosts and headless regression checks.</summary>
    public void SetFilter(string query) => _searchBox.Text = query ?? string.Empty;

    private void BuildFolderPanel(Control parent, ResourceDefinition? definition)
    {
        parent.BackColor = Color.FromArgb(30, 30, 36);
        parent.Padding = new Padding(8, 12, 8, 8);
        Label title = new()
        {
            Dock = DockStyle.Top,
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Height = 32,
            Text = definition?.DisplayName?.ToUpperInvariant() ?? "PROJECT ASSETS",
        };
        _folders.BorderStyle = BorderStyle.None;
        _folders.Dock = DockStyle.Fill;
        _folders.BackColor = Color.FromArgb(30, 30, 36);
        _folders.ForeColor = EditorChrome.Text;
        _folders.FullRowSelect = true;
        _folders.HideSelection = false;
        _folders.ShowLines = false;
        _folders.ShowPlusMinus = true;
        _folders.ItemHeight = 28;
        TreeNode recent = new("Recent Assets") { Name = "$recent", Tag = "$recent" };
        TreeNode all = new("All Assets") { Name = "$all", Tag = "$all" };
        _folders.Nodes.Add(new TreeNode("Favourites") { Name = "$favourites", Tag = "$favourites" });
        _folders.Nodes.Add(recent);
        _folders.Nodes.Add(all);
        foreach (string folder in _allAssets.Select(ProjectFolder)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            AddFolderNode(all, folder);
        }
        all.Expand();
        _folders.SelectedNode = all;
        _folders.AfterSelect += (_, args) =>
        {
            _scope = args.Node?.Tag as string ?? "$all";
            FilterAssets();
        };
        parent.Controls.Add(_folders);
        parent.Controls.Add(title);
    }

    private static void AddFolderNode(TreeNode root, string folder)
    {
        string accumulated = string.Empty;
        TreeNodeCollection nodes = root.Nodes;
        foreach (string part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;
            TreeNode? node = nodes.Cast<TreeNode>().FirstOrDefault(candidate =>
                candidate.Text.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = new TreeNode(part) { Tag = accumulated };
                nodes.Add(node);
            }
            nodes = node.Nodes;
        }
    }

    private void BuildResultsPanel(Control parent)
    {
        Panel searchHost = new()
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(0, 0, 0, 10),
        };
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.PlaceholderText = "Search names / tags (tag:forest, -tag:ui)…";
        _assetToolTip.SetToolTip(_searchBox, "Match names, folders and library tags. tag:forest requires that tag; -tag:ui excludes it. Use tag:\"night sky\" for spaces.");
        _searchBox.BackColor = Color.FromArgb(30, 30, 36);
        _searchBox.ForeColor = EditorChrome.Text;
        _searchBox.TextChanged += (_, _) => FilterAssets();
        _searchBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Down || _filtered.Count == 0) return;
            _results.Focus();
            SelectIndex(0);
            args.Handled = true;
        };
        searchHost.Controls.Add(_searchBox);

        _resultCount.AutoEllipsis = true;
        _resultCount.Dock = DockStyle.Bottom;
        _resultCount.Height = 28;
        _resultCount.ForeColor = EditorChrome.Muted;
        _resultCount.Padding = new Padding(3, 7, 0, 0);

        _results.Dock = DockStyle.Fill;
        _results.View = View.LargeIcon;
        _results.FullRowSelect = true;
        _results.HideSelection = false;
        _results.MultiSelect = false;
        _results.VirtualMode = true;
        _results.ShowItemToolTips = true;
        _results.BorderStyle = BorderStyle.FixedSingle;
        _results.BackColor = Color.FromArgb(30, 30, 36);
        _results.ForeColor = EditorChrome.Text;
        _results.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _results.AccessibleName = "Resource results";
        _thumbnails.ColorDepth = ColorDepth.Depth32Bit;
        _thumbnails.ImageSize = new Size(48, 48);
        _results.LargeImageList = _thumbnails;
        _results.SmallImageList = _thumbnails;
        _results.Columns.Add("Asset", 210);
        _results.Columns.Add("Folder", 220);
        _results.RetrieveVirtualItem += (_, args) =>
        {
            if (args.ItemIndex < 0 || args.ItemIndex >= _filtered.Count)
            {
                args.Item = new ListViewItem(string.Empty);
                return;
            }
            ProjectAssetEntry entry = _filtered[args.ItemIndex];
            ListViewItem item = new(entry.DisplayName) { Tag = entry, ToolTipText = entry.Reference
                + (entry.LibraryTags.Count > 0 ? "\nLibrary tags: " + string.Join(", ", entry.LibraryTags) : string.Empty) };
            if (_thumbnailIndices.TryGetValue(entry.Reference, out int imageIndex))
            {
                item.ImageIndex = imageIndex;
                TouchThumbnail(entry.Reference);
            }
            else QueueThumbnail(entry);
            item.SubItems.Add(ProjectFolder(entry));
            args.Item = item;
        };
        _results.SelectedIndexChanged += (_, _) => { if (!_filtering) RefreshSelection(); };
        _results.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left && _results.HitTest(args.Location).Item is not null) CommitSelection();
        };
        _results.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                CommitSelection();
                args.SuppressKeyPress = true;
            }
            else if (args.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
            }
        };

        FlowLayoutPanel viewModes = new()
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 34,
            Padding = new Padding(0, 0, 0, 6),
            WrapContents = false,
        };
        Button grid = new() { Text = "Grid", Width = 58, Height = 28 };
        Button list = new() { Text = "List", Width = 58, Height = 28 };
        _favourite.Text = "☆ Favourite";
        _favourite.Width = 110; _favourite.Height = 28; _favourite.Enabled = false;
        _favourite.AccessibleName = "Toggle selected resource favourite";
        _favourite.Click += (_, _) => ToggleFavourite();
        _assetToolTip.SetToolTip(_favourite, "Add/remove this resource from project favourites (Ctrl+D).");
        EditorChrome.StyleField(_favourite);
        EditorChrome.StyleField(grid);
        EditorChrome.StyleField(list);
        grid.Click += (_, _) => _results.View = View.LargeIcon;
        list.Click += (_, _) => _results.View = View.Details;
        viewModes.Controls.Add(list);
        viewModes.Controls.Add(grid);
        viewModes.Controls.Add(_favourite);
        parent.Controls.Add(_results);
        parent.Controls.Add(_resultCount);
        parent.Controls.Add(viewModes);
        parent.Controls.Add(searchHost);
    }

    private void BuildPreviewPanel(Control parent, ResourceDefinition? definition)
    {
        Panel previewSurface = new()
        {
            Dock = DockStyle.Top,
            Height = 230,
            Padding = new Padding(10),
            BackColor = EditorChrome.Surface,
        };
        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = EditorChrome.Canvas;
        _previewPlaceholder.Dock = DockStyle.Fill;
        _previewPlaceholder.Font = new Font(EditorChrome.BaseFont.FontFamily, 30f, FontStyle.Regular);
        _previewPlaceholder.ForeColor = EditorChrome.Muted;
        _previewPlaceholder.Text = definition?.IconGlyph ?? "◇";
        _previewPlaceholder.TextAlign = ContentAlignment.MiddleCenter;
        previewSurface.Controls.Add(_preview);
        previewSurface.Controls.Add(_previewPlaceholder);

        _assetName.AutoEllipsis = true;
        _assetName.Dock = DockStyle.Top;
        _assetName.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        _assetName.Height = 34;
        _assetName.Padding = new Padding(0, 10, 0, 0);
        _assetPath.AutoEllipsis = true;
        _assetPath.Dock = DockStyle.Top;
        _assetPath.ForeColor = EditorChrome.Muted;
        _assetPath.Height = 44;
        _assetPath.Padding = new Padding(0, 4, 0, 8);

        _parameterHost.Dock = DockStyle.Fill;
        _parameterHost.AutoScroll = true;
        _parameterHost.BackColor = EditorChrome.Canvas;

        _libraryTags.Dock = DockStyle.Top;
        _libraryTags.AutoEllipsis = true;
        _libraryTags.Height = 64;
        _libraryTags.ForeColor = EditorChrome.Muted;
        _libraryTags.AccessibleName = "Selected resource library tags";
        parent.Controls.Add(_parameterHost);
        parent.Controls.Add(_libraryTags);
        parent.Controls.Add(_assetPath);
        parent.Controls.Add(_assetName);
        parent.Controls.Add(previewSurface);
    }

    private Panel BuildActions()
    {
        Panel host = new()
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = EditorChrome.Surface,
        };
        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        _select.Text = "Select";
        _select.AutoSize = true;
        _select.Enabled = false;
        _select.Click += (_, _) => CommitSelection();
        _cancel.Text = "Cancel";
        _cancel.AutoSize = true;
        _cancel.DialogResult = DialogResult.Cancel;
        _none.Text = "None";
        _none.AutoSize = true;
        _none.Visible = _request.AllowNone;
        _none.Click += (_, _) =>
        {
            SelectedAsset = new ProjectAssetEntry("None", string.Empty, string.Empty, _request.Kind);
            DialogResult = DialogResult.OK;
            Close();
        };
        EditorChrome.StyleField(_select);
        EditorChrome.StyleField(_cancel);
        EditorChrome.StyleField(_none);
        buttons.Controls.Add(_select);
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_none);
        host.Controls.Add(buttons);
        return host;
    }

    private void FilterAssets()
    {
        if (_filtering || IsDisposed) return;
        int oldIndex = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : -1;
        string? selected = oldIndex >= 0 && oldIndex < _filtered.Count ? _filtered[oldIndex].Reference : null;
        string query = _searchBox.Text.Trim();
        ResourceLibraryQuery parsed = ResourceLibraryQuery.Parse(query);
        _filtering = true;
        _results.BeginUpdate();
        try
        {
            // Clear native indices BEFORE changing their backing list. A shrinking search must
            // not preview or accept the previous item at a now-reused index.
            _results.SelectedIndices.Clear();
            _results.VirtualListSize = 0;
            ResetThumbnailRequests();
            _filtered.Clear();
            _filtered.AddRange(_allAssets.Where(MatchesScope)
                .Select(entry => (Entry: entry, Score: query.Length == 0 ? (IsRecent(entry) ? 0 : 20)
                    : parsed.Score(entry.DisplayName, entry.LibraryTags, ProjectFolder(entry), fuzzy: true)))
                .Where(result => result.Score >= 0)
                .OrderBy(result => result.Score).ThenBy(result => RecentRank(result.Entry))
                .ThenBy(result => result.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(result => result.Entry));
            _filteredIndices.Clear();
            for (int i = 0; i < _filtered.Count; i++) _filteredIndices[_filtered[i].Reference] = i;
            _results.VirtualListSize = _filtered.Count;
        }
        finally { _results.EndUpdate(); _filtering = false; }
        UpdateResultCount();
        int next = selected is not null && _filteredIndices.TryGetValue(selected, out int index) ? index
            : query.Length > 0 && _filtered.Count > 0 ? 0 : -1;
        if (next >= 0) SelectIndex(next); else ClearPreview();
        _results.Invalidate();
    }

    private bool IsRecent(ProjectAssetEntry entry) => _recent.Contains(entry.Reference)
        || _preferences.IsRecent(entry.AssetId, entry.Reference);

    private bool MatchesScope(ProjectAssetEntry entry)
    {
        if (_scope == "$all") return true;
        if (_scope == "$recent") return IsRecent(entry);
        if (_scope == "$favourites") return _preferences.IsFavourite(entry.AssetId, entry.Reference);
        string folder = ProjectFolder(entry);
        return folder.Equals(_scope, StringComparison.OrdinalIgnoreCase)
               || folder.StartsWith(_scope + "/", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateResultCount()
    {
        _resultCount.Text = ResourceLibraryQuery.Parse(_searchBox.Text).Error ?? ($"{_filtered.Count:N0} {_request.Kind} resource(s)"
            + (_preferences.LastError.Length > 0 ? " · preferences unavailable" : string.Empty));
        _assetToolTip.SetToolTip(_resultCount, _preferences.LastError);
    }

    private void ToggleFavourite()
    {
        int index = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : -1;
        if (index < 0 || index >= _filtered.Count) return;
        ProjectAssetEntry entry = _filtered[index];
        _preferences.SetFavourite(entry.AssetId, entry.Reference, !_preferences.IsFavourite(entry.AssetId, entry.Reference));
        if (_scope == "$favourites") FilterAssets();
        else { UpdateFavouriteButton(entry); UpdateResultCount(); }
    }

    private void UpdateFavouriteButton(ProjectAssetEntry entry)
    {
        _favourite.Enabled = true;
        _favourite.Text = _preferences.IsFavourite(entry.AssetId, entry.Reference) ? "★ Favourite" : "☆ Favourite";
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.D)) { ToggleFavourite(); return true; }
        if (keyData == (Keys.Control | Keys.F)) { _searchBox.Focus(); _searchBox.SelectAll(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private static Bitmap CreateKindThumbnail(ResourceKind kind)
    {
        Bitmap bitmap = new(48, 48);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(20, 20, 24));
        using SolidBrush fill = new(Color.FromArgb(42, 50, 61));
        using GraphicsPath path = new();
        path.AddArc(2, 2, 14, 14, 180, 90);
        path.AddArc(32, 2, 14, 14, 270, 90);
        path.AddArc(32, 32, 14, 14, 0, 90);
        path.AddArc(2, 32, 14, 14, 90, 90);
        path.CloseFigure();
        graphics.FillPath(fill, path);
        string glyph = ResourceDefinitions.All.FirstOrDefault(item => item.Kind == kind)?.IconGlyph ?? "◇";
        using Font font = new("Segoe UI Symbol", 18f);
        TextRenderer.DrawText(graphics, glyph, font, new Rectangle(2, 2, 44, 44),
            Color.FromArgb(55, 201, 232), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return bitmap;
    }

    private int RecentRank(ProjectAssetEntry entry) => _preferences.RecentRank(entry.AssetId, entry.Reference);

    private void SelectCurrentValue()
    {
        if (string.IsNullOrWhiteSpace(_request.CurrentValue)) return;
        int index = _filtered.FindIndex(entry =>
            string.Equals(entry.Reference, _request.CurrentValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.FullPath, _request.CurrentValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.DisplayName, _request.CurrentValue, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) SelectIndex(index);
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= _filtered.Count) return;
        _results.SelectedIndices.Clear();
        _results.SelectedIndices.Add(index);
        _results.EnsureVisible(index);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        int index = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : -1;
        if (index < 0 || index >= _filtered.Count)
        {
            _select.Enabled = false;
            ClearPreview();
            return;
        }

        ProjectAssetEntry entry = _filtered[index];
        _select.Enabled = true;
        UpdateFavouriteButton(entry);
        if (string.Equals(_selectedReference, entry.Reference, StringComparison.OrdinalIgnoreCase)) return;
        _selectedReference = entry.Reference;
        _assetName.Text = entry.DisplayName;
        _assetPath.Text = ResourceDefinitions.Get(entry.Kind).DisplayName;
        _assetPath.AccessibleDescription = entry.Reference;
        _libraryTags.Text = entry.LibraryTags.Count == 0 ? "Library tags: none" : "Library tags: " + string.Join(", ", entry.LibraryTags);
        _assetToolTip.SetToolTip(_libraryTags, _libraryTags.Text + "\nEdit via Assets → right-click → Edit Library Tags.");
        _assetToolTip.SetToolTip(_assetName, entry.Reference);
        _assetToolTip.SetToolTip(_assetPath, entry.Reference);

        QueueSelectionPreview(entry);

        _parameterHost.SuspendLayout();
        foreach (Control control in _parameterHost.Controls.Cast<Control>().ToArray()) control.Dispose();
        _parameterHost.Controls.Clear();
        Control? parameters = _request.ParameterPanelFactory?.Invoke(entry);
        if (parameters is not null)
        {
            parameters.Dock = DockStyle.Top;
            _parameterHost.Controls.Add(parameters);
        }
        _parameterHost.ResumeLayout(performLayout: true);
    }

    private void ClearPreview()
    {
        _selectedReference = null;
        _select.Enabled = false;
        _favourite.Enabled = false;
        _favourite.Text = "☆ Favourite";
        _pendingPreview = null;
        _previewGeneration++;
        DrawingImage? old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
        _preview.Visible = false;
        _previewPlaceholder.Visible = true;
        _assetName.Text = "Select a resource";
        _assetPath.Text = string.Empty;
        _libraryTags.Text = string.Empty;
        _assetToolTip.SetToolTip(_libraryTags, string.Empty);
        _assetToolTip.SetToolTip(_assetName, string.Empty);
        _assetToolTip.SetToolTip(_assetPath, string.Empty);
        foreach (Control control in _parameterHost.Controls.Cast<Control>().ToArray()) control.Dispose();
        _parameterHost.Controls.Clear();
    }

    private void CommitSelection()
    {
        int index = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : -1;
        if (index < 0 || index >= _filtered.Count) return;
        SelectedAsset = _filtered[index];
        _preferences.Remember(SelectedAsset.AssetId, SelectedAsset.Reference);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposePreviewLoading();
            DrawingImage? image = _preview.Image;
            _preview.Image = null;
            image?.Dispose();
            _assetToolTip.Dispose();
            _thumbnails.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string ProjectFolder(ProjectAssetEntry entry)
    {
        string? folder = Path.GetDirectoryName(entry.ProjectRelativePath)?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(folder) ? "Assets" : folder;
    }
}

/// <summary>Small shared thumbnail cache; callers receive clones and may dispose them freely.</summary>
public static class AssetPickerPreviewCache
{
    private const int MaximumEntries = 96;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string projectRoot, ProjectAssetEntry entry, Size bounds)
    {
        if (entry.Kind == ResourceKind.Audio)
            return GetAudioWaveform(projectRoot, entry, bounds);
        string? source = entry.Kind switch
        {
            ResourceKind.Image => ProjectAssetIndex.ResolveSpriteImage(projectRoot, entry.Reference),
            ResourceKind.GameObject => ObjectResourceReader.ResolveImageFile(entry.FullPath, projectRoot),
            ResourceKind.Model or ResourceKind.Shader =>
                ResourceAssociates.FindPrimaryImage(entry.FullPath),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;

        long stamp = File.GetLastWriteTimeUtc(source).Ticks;
        string key = $"{source}|{stamp}|{bounds.Width}x{bounds.Height}";
        lock (Gate)
        {
            if (!Images.TryGetValue(key, out Bitmap? cached))
            {
                cached = LoadScaled(source, bounds);
                if (cached is null) return null;
                if (Images.Count >= MaximumEntries)
                {
                    KeyValuePair<string, Bitmap> oldest = Images.First();
                    Images.Remove(oldest.Key);
                    oldest.Value.Dispose();
                }
                Images[key] = cached;
            }
            return new Bitmap(cached);
        }
    }

    private static Bitmap? GetAudioWaveform(string projectRoot, ProjectAssetEntry entry, Size bounds)
    {
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(entry.FullPath));
            if (!document.RootElement.TryGetProperty("source", out System.Text.Json.JsonElement sourceNode)
                || sourceNode.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            string relative = sourceNode.GetString() ?? string.Empty;
            string source = Path.GetFullPath(Path.Combine(projectRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(source)) return null;
            using FileStream stream = File.Open(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 44) return null;
            Bitmap result = new(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.FromArgb(20, 20, 24));
            using Pen center = new(Color.FromArgb(55, 65, 76));
            using Pen wave = new(Color.FromArgb(55, 201, 232), 1.5f);
            int middle = result.Height / 2;
            graphics.DrawLine(center, 0, middle, result.Width, middle);
            const int start = 44;
            long sampleLength = Math.Max(1, stream.Length - start);
            byte[] samples = new byte[64];
            for (int x = 0; x < result.Width; x++)
            {
                long from = start + (x * sampleLength / result.Width);
                stream.Position = Math.Clamp(from, start, stream.Length - 1);
                int read = stream.Read(samples, 0, (int)Math.Min(samples.Length, stream.Length - stream.Position));
                int peak = 0;
                for (int index = 0; index < read; index++)
                    peak = Math.Max(peak, Math.Abs(samples[index] - 128));
                int height = (int)(peak / 128f * (middle - 2));
                graphics.DrawLine(wave, x, middle - height, x, middle + height);
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static Bitmap? LoadScaled(string path, Size bounds)
    {
        try
        {
            using DrawingImage source = DrawingImage.FromFile(path);
            float scale = Math.Min(1f, Math.Min(
                bounds.Width / (float)Math.Max(1, source.Width),
                bounds.Height / (float)Math.Max(1, source.Height)));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            Bitmap result = new(width, height);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            return result;
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or OutOfMemoryException or ExternalException)
        {
            // GDI+ reports non-image / corrupt files as ExternalException ("object could not be
            // created… invalid input"), not ArgumentException. Fresh Images with no PNG must not
            // crash the Inspector when selected after create.
            return null;
        }
    }
}

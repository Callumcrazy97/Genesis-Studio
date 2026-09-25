using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Resources;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Docking;

public sealed partial class ResourceBrowserDock : GenesisDockContent
{
    private const string ResourceDragFormat = "Genesis.Application.ResourcePath";

    private readonly ResourceService _resources;
    private readonly SettingsService _settings;
    private readonly ResourceThumbnailCache _thumbnails;
    private readonly TreeView _tree;
    private readonly ToolStripTextBox _search;
    private readonly ToolStrip _finderBar;
    private ToolStripItem? _hostedFinderSearch;
    private ToolStripItem? _hostedFinderFilter;
    private readonly ContextMenuStrip _contextMenu;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _watcherDebounce;
    private readonly Dictionary<string, ResourceSearchResult> _finderResults =
        new(StringComparer.OrdinalIgnoreCase);
    private ResourceItem? _treeSnapshot;
    private string _finderTerm = string.Empty;
    private string? _pathToSelect;
    private bool _browserDisposed;

    public ResourceBrowserDock(ResourceService resources, SettingsService settings)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _thumbnails = new ResourceThumbnailCache(_resources.Project.RootPath);
        _preferences = new ResourcePickerPreferences(_resources.Project.RootPath);
        Text = "Assets";
        TabText = "Assets";
        DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float;
        ShowHint = DockState.DockLeft;

        ToolStrip toolbar = new()
        {
            BackColor = ThemeService.Palette.Surface,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(6, 5, 6, 5),
            Renderer = ThemeService.CreateToolStripRenderer(),
        };

        ToolStripDropDownButton add = new("＋ New")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        add.DropDownItems.Add("Resources"); // Keep the dropdown available before its first opening.
        add.DropDownOpening += (_, _) => PopulateNewMenu(add.DropDownItems);

        toolbar.Items.Add(add);
        ToolStripButton import = new("Import…")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Import images, audio, models, or resource files into Assets",
        };
        import.Click += (_, _) => ImportAssets();
        toolbar.Items.Add(import);
        toolbar.Items.Add(new ToolStripSeparator());
        _search = new ToolStripTextBox
        {
            AutoSize = false,
            BorderStyle = BorderStyle.None,
            Name = "AssetTreeNameFilter",
            Size = new Size(150, 28),
            ToolTipText = "Filter names and library tags. tag:forest requires a tag; -tag:ui excludes it. Use quotes for spaces.",
        };
        _search.TextBox.PlaceholderText = "Filter names / tags…";
        _search.TextChanged += (_, _) => RenderTree();
        toolbar.Items.Add(_search);

        ToolStripButton refresh = new("↻")
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Refresh assets",
        };
        refresh.Click += (_, _) => RefreshAndInvalidateFinder();
        toolbar.Items.Add(refresh);

        _finderBar = new ToolStrip
        {
            BackColor = ThemeService.Palette.Surface,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(6, 0, 6, 5),
            Renderer = ThemeService.CreateToolStripRenderer(),
        };

        // Added before the command strip on purpose. Top-docked siblings stack by z-order, and the
        // one added first ends up furthest from the edge — so this puts New/Import at the top and
        // the Finder directly above the tree its results land in.
        Controls.Add(BuildLibraryBar());
        Controls.Add(_finderBar);
        Controls.Add(toolbar);

        _tree = new TreeView
        {
            AllowDrop = true,
            BackColor = ThemeService.Palette.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            DrawMode = TreeViewDrawMode.OwnerDrawText,
            ForeColor = ThemeService.Palette.Text,
            FullRowSelect = true,
            HideSelection = false,
            HotTracking = true,
            Indent = 19,
            ItemHeight = DpiLayout.Scale(this, 27),
            LabelEdit = true,
            PathSeparator = "/",
            ShowLines = false,
            ShowPlusMinus = true,
            ShowRootLines = false,
            ShowNodeToolTips = true,
        };
        _tree.DrawNode += DrawNode;
        _tree.AfterSelect += (_, args) =>
        {
            if (args.Node is not null)
            {
                OnSelectionChanged(args.Node);
            }
        };
        _tree.NodeMouseDoubleClick += OnNodeDoubleClick;
        _tree.AfterExpand += (_, args) => RememberDisclosure(args.Node, true);
        _tree.AfterCollapse += (_, args) => RememberDisclosure(args.Node, false);
        _tree.Enter += (_, _) => RefreshBookmarks();
        _tree.BeforeLabelEdit += (_, args) =>
        {
            if (args.Node?.Tag is ResourceItem item && IsAssetsRoot(item)) args.CancelEdit = true;
        };
        _tree.AfterLabelEdit += RenameAfterEdit;
        _tree.KeyDown += TreeKeyDown;
        _tree.ItemDrag += BeginDrag;
        _tree.DragEnter += OnTreeDragEnter;
        _tree.DragOver += OnTreeDragOver;
        _tree.DragDrop += OnTreeDragDrop;
        _tree.NodeMouseClick += SelectRightClickedNode;
        Controls.Add(_tree);
        _tree.BringToFront();

        _contextMenu = BuildContextMenu();
        _tree.ContextMenuStrip = _contextMenu;
        _resources.Changed += OnResourceServiceChanged;
        InitializeVisiblePreviews();
        _watcherDebounce = new System.Threading.Timer(
            _ => QueueExternalRefresh(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _watcher = new FileSystemWatcher(_resources.AssetsRoot)
        {
            EnableRaisingEvents = _settings.Current.General.CheckForExternalChanges,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size,
        };
        _watcher.Changed += ExternalResourceChanged;
        _watcher.Created += ExternalResourceChanged;
        _watcher.Deleted += ExternalResourceChanged;
        _watcher.Renamed += ExternalResourceChanged;
        _settings.SettingsChanged += OnSettingsChanged;

        ThemeService.Apply(this);
        RefreshTree();
    }

    public ResourceItem? SelectedResource =>
        _tree.SelectedNode?.Tag as ResourceItem;

    public TreeNode? SelectedNode => _tree.SelectedNode;

    internal ResourceItem ResourceTreeSnapshot =>
        _treeSnapshot ?? throw new InvalidOperationException("The Assets tree has not been built yet.");

    internal int FinderResultCount => _finderResults.Count;

    internal IReadOnlyCollection<ResourceSearchResult> FinderResults => _finderResults.Values;

    public event EventHandler<ResourceSelectedEventArgs>? ResourceSelected;

    public event EventHandler<ResourceSelectedEventArgs>? ResourceOpenRequested;

    public event EventHandler<string>? StatusMessage;

    internal event EventHandler? FinderInvalidated;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_browserDisposed)
        {
            _browserDisposed = true;
            _settings.SettingsChanged -= OnSettingsChanged;
            _resources.Changed -= OnResourceServiceChanged;
            DisposeVisiblePreviews();
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= ExternalResourceChanged;
            _watcher.Created -= ExternalResourceChanged;
            _watcher.Deleted -= ExternalResourceChanged;
            _watcher.Renamed -= ExternalResourceChanged;
            _watcher.Dispose();
            _watcherDebounce.Dispose();
            _thumbnails.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _watcher.EnableRaisingEvents = _settings.Current.General.CheckForExternalChanges;
        }
        catch (ObjectDisposedException)
        {
        }

        _tree.ItemHeight = DpiLayout.Scale(_tree, ThemeService.TreeItemHeight);
        _tree.Invalidate();
    }

    public void RefreshTree()
    {
        if (_browserDisposed || IsDisposed) return;
        _treeSnapshot = _resources.BuildTree();
        _snapshotGeneration++;
        ResetVisiblePreviews();
        RenderTree();
    }

    /// <summary>
    /// Projects local/Finder filters over the immutable snapshot without traversing the filesystem.
    /// Snapshot rebuilds belong only to resource mutations, watchers and explicit refresh commands.
    /// </summary>
    internal void ApplyFinderResults(string term, ResourceSearchResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _finderTerm = term.Trim();
        _finderResults.Clear();
        foreach (ResourceSearchResult result in response.Results)
        {
            _finderResults[Path.GetFullPath(result.Resource.FullPath)] = result;
        }

        _pathToSelect = response.Results.FirstOrDefault()?.Resource.FullPath ?? string.Empty;
        RenderTree();
    }

    internal void ClearFinderResults()
    {
        if (_finderTerm.Length == 0 && _finderResults.Count == 0)
        {
            return;
        }

        _finderTerm = string.Empty;
        _finderResults.Clear();
        RenderTree();
    }

    internal void FocusResults() => _tree.Focus();

    internal void OpenSelectedResult()
    {
        if (_finderTerm.Length > 0
            && _tree.SelectedNode?.Tag is ResourceItem selected
            && !_finderResults.ContainsKey(Path.GetFullPath(selected.FullPath)))
        {
            return;
        }

        OpenNode(_tree.SelectedNode);
    }

    /// <summary>
    /// Takes the shell's Finder controls and shows them on their own row above the tree.
    /// </summary>
    /// <remarks>
    /// The Finder searches the project and projects its hits into this tree, so the top-right corner
    /// of the window was the wrong place to drive it from — the control and the thing it controls
    /// were at opposite ends of the screen. Only the parenting moves: the shell still builds these
    /// items, owns their handlers, debouncing, cancellation and filter state, and still reads them
    /// through <c>FinderSearchBox</c> / <c>FinderFilterButton</c>. Rebuilding the Finder here would
    /// have meant reimplementing all of it.
    ///
    /// They get a row of their own rather than joining the New/Import strip, which already overflows
    /// at the dock's default width — the local name filter is in its overflow menu right now.
    /// </remarks>
    public void HostFinderControls(ToolStripItem search, ToolStripItem filter)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(filter);

        _hostedFinderSearch = search;
        _hostedFinderFilter = filter;

        // Right alignment put them against the command bar's far edge; on a row of their own they
        // read left to right like everything else in this panel.
        search.Alignment = ToolStripItemAlignment.Left;
        filter.Alignment = ToolStripItemAlignment.Right;

        _finderBar.Items.Add(search);
        _finderBar.Items.Add(filter);
        ThemeFinderSearchBox(search);

        _finderBar.Resize += (_, _) => StretchFinderSearch();
        StretchFinderSearch();
    }

    /// <summary>Paints the Finder's text box in theme colours instead of the system default.</summary>
    /// <remarks>
    /// A <see cref="ToolStripTextBox"/> hosts a real <see cref="TextBox"/>, but as a
    /// <see cref="ToolStripItem"/> rather than a child control — so <c>ThemeService.Apply</c>, which
    /// walks <c>Control.Controls</c>, has never reached it. It stayed the system's white default,
    /// which went unnoticed on the command bar and is glaring against this dark panel.
    ///
    /// Applied after the item is parented, and again whenever the hosted control is recreated:
    /// <see cref="ToolStripControlHost"/> re-syncs its child's colours from the owning strip when it
    /// changes parent, discarding anything set beforehand.
    /// </remarks>
    private static void ThemeFinderSearchBox(ToolStripItem item)
    {
        if (item is not ToolStripTextBox box)
        {
            return;
        }

        Apply();
        box.TextBox.HandleCreated += (_, _) => Apply();

        void Apply()
        {
            box.BackColor = ThemeService.Palette.SurfaceRaised;
            box.ForeColor = ThemeService.Palette.Text;
            box.TextBox.BackColor = ThemeService.Palette.SurfaceRaised;
            box.TextBox.ForeColor = ThemeService.Palette.Text;
        }
    }

    /// <summary>Grows the Finder's box to whatever width the dock currently has.</summary>
    /// <remarks>
    /// A <see cref="ToolStripTextBox"/> has no fill mode, so without this it keeps the fixed 260px it
    /// was given for the command bar: too wide for a narrowed dock, and leaving dead space in a
    /// widened one. The floor stops it collapsing to nothing when the dock is dragged very narrow.
    /// </remarks>
    private void StretchFinderSearch()
    {
        if (_hostedFinderSearch is null)
        {
            return;
        }

        int filterWidth = _hostedFinderFilter?.Width ?? 0;
        int available = _finderBar.DisplayRectangle.Width - filterWidth - 10;
        _hostedFinderSearch.Width = Math.Max(90, available);
    }

    private void RefreshAndInvalidateFinder()
    {
        RefreshTree();
        FinderInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public bool CanExecute(ResourceBrowserCommand command)
    {
        ResourceItem? selected = SelectedResource;
        return command switch
        {
            ResourceBrowserCommand.Refresh or ResourceBrowserCommand.Import
                or ResourceBrowserCommand.ShowAll or ResourceBrowserCommand.ShowFavourites
                or ResourceBrowserCommand.ShowRecent or ResourceBrowserCommand.ResetFilters
                or ResourceBrowserCommand.ClearRecent => true,
            ResourceBrowserCommand.ToggleFavourite or ResourceBrowserCommand.EditLibraryTags => selected is { IsFolder: false },
            ResourceBrowserCommand.UndoLibraryTags => _tagHistory.CanUndo,
            ResourceBrowserCommand.RedoLibraryTags => _tagHistory.CanRedo,
            ResourceBrowserCommand.NewFolder => ResourceFolderPolicy.GetRoot(_resources.Project, SelectedFolder()) is not null,
            // This is an inexpensive availability hint. Paste itself performs full transfer validation.
            ResourceBrowserCommand.Paste => _resources.Clipboard.HasItems
                && ResourceFolderPolicy.GetRoot(_resources.Project, SelectedFolder()) is not null,
            ResourceBrowserCommand.Cut or ResourceBrowserCommand.Copy or ResourceBrowserCommand.Duplicate
                or ResourceBrowserCommand.Rename or ResourceBrowserCommand.Delete => selected is not null && !IsAssetsRoot(selected),
            _ => false,
        };
    }

    public void Execute(ResourceBrowserCommand command)
    {
        if (!CanExecute(command)) return;
        switch (command)
        {
            case ResourceBrowserCommand.EditLibraryTags: EditLibraryTags(); break;
            case ResourceBrowserCommand.UndoLibraryTags: UndoLibraryTags(redo: false); break;
            case ResourceBrowserCommand.RedoLibraryTags: UndoLibraryTags(redo: true); break;
            case ResourceBrowserCommand.ResetFilters: ResetLibraryFilters(); break;
            case ResourceBrowserCommand.ToggleFavourite: ToggleFavourite(); break;
            case ResourceBrowserCommand.ShowAll: SetScope(ResourceBrowserScope.All); break;
            case ResourceBrowserCommand.ShowFavourites: SetScope(ResourceBrowserScope.Favourites); break;
            case ResourceBrowserCommand.ShowRecent: SetScope(ResourceBrowserScope.Recent); break;
            case ResourceBrowserCommand.ClearRecent: ClearRecentResources(); break;
            case ResourceBrowserCommand.NewFolder:
                CreateFolder();
                break;
            case ResourceBrowserCommand.Cut:
                SetClipboard(ResourceClipboardOperation.Cut);
                break;
            case ResourceBrowserCommand.Copy:
                SetClipboard(ResourceClipboardOperation.Copy);
                break;
            case ResourceBrowserCommand.Paste:
                Paste();
                break;
            case ResourceBrowserCommand.Duplicate:
                Duplicate();
                break;
            case ResourceBrowserCommand.Rename:
                BeginRename();
                break;
            case ResourceBrowserCommand.Delete:
                DeleteSelected();
                break;
            case ResourceBrowserCommand.Refresh:
                RefreshAndInvalidateFinder();
                break;
            case ResourceBrowserCommand.Import:
                ImportAssets();
                break;
        }
    }

    public bool SelectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_tree.Nodes.Count == 0)
        {
            return false;
        }

        TreeNode? node = FindNodeByPath(_tree.Nodes[0], path);
        if (node is null)
        {
            return false;
        }

        _tree.SelectedNode = node;
        node.EnsureVisible();
        return true;
    }

    public bool ExecuteShortcut(Keys keyData)
    {
        ResourceBrowserCommand? command = keyData switch
        {
            Keys.Control | Keys.Z => ResourceBrowserCommand.UndoLibraryTags,
            Keys.Control | Keys.Y => ResourceBrowserCommand.RedoLibraryTags,
            Keys.Control | Keys.Shift | Keys.Z => ResourceBrowserCommand.RedoLibraryTags,
            Keys.F2 => ResourceBrowserCommand.Rename,
            Keys.Delete => ResourceBrowserCommand.Delete,
            Keys.Control | Keys.C => ResourceBrowserCommand.Copy,
            Keys.Control | Keys.X => ResourceBrowserCommand.Cut,
            Keys.Control | Keys.V => ResourceBrowserCommand.Paste,
            Keys.Control | Keys.D => ResourceBrowserCommand.Duplicate,
            Keys.Control | Keys.Alt | Keys.N => ResourceBrowserCommand.NewFolder,
            _ => null,
        };

        if (!command.HasValue)
        {
            return false;
        }

        Execute(command.Value);
        return true;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = new()
        {
            BackColor = ThemeService.Palette.SurfaceRaised,
            ForeColor = ThemeService.Palette.Text,
            Renderer = ThemeService.CreateToolStripRenderer(),
        };

        ToolStripMenuItem create = new("New");
        create.DropDownItems.Add("Resources");
        create.DropDownOpening += (_, _) => PopulateNewMenu(create.DropDownItems);

        menu.Items.Add(create);
        menu.Items.Add("Import…", null, (_, _) => ImportAssets());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => OpenNode(_tree.SelectedNode));
        menu.Items.Add(ContextItem("Rename", Keys.F2, BeginRename));
        menu.Items.Add(ContextItem("Duplicate", Keys.Control | Keys.D, Duplicate));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(ContextItem(
            "Cut",
            Keys.Control | Keys.X,
            () => SetClipboard(ResourceClipboardOperation.Cut)));
        menu.Items.Add(ContextItem(
            "Copy",
            Keys.Control | Keys.C,
            () => SetClipboard(ResourceClipboardOperation.Copy)));
        menu.Items.Add(ContextItem("Paste", Keys.Control | Keys.V, Paste));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(ContextItem("Delete", Keys.Delete, DeleteSelected));
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem favourite = new("Add to Favourites");
        favourite.Click += (_, _) => Execute(ResourceBrowserCommand.ToggleFavourite);
        menu.Items.Add(favourite);
        ToolStripMenuItem tags = new("Edit Library Tags…");
        tags.Click += (_, _) => Execute(ResourceBrowserCommand.EditLibraryTags);
        menu.Items.Add(tags);
        ToolStripMenuItem undoTags = ContextItem("Undo Library Tag Edit", Keys.Control | Keys.Z,
            () => Execute(ResourceBrowserCommand.UndoLibraryTags));
        ToolStripMenuItem redoTags = ContextItem("Redo Library Tag Edit", Keys.Control | Keys.Y,
            () => Execute(ResourceBrowserCommand.RedoLibraryTags));
        menu.Items.Add(undoTags); menu.Items.Add(redoTags);
        menu.Opening += (_, args) =>
        {
            ResourceItem? selected = SelectedResource;
            if (selected is null)
            {
                args.Cancel = true;
                return;
            }

            RefreshBookmarks(render: false);
            favourite.Enabled = !selected.IsFolder;
            tags.Enabled = !selected.IsFolder;
            undoTags.Enabled = CanExecute(ResourceBrowserCommand.UndoLibraryTags);
            redoTags.Enabled = CanExecute(ResourceBrowserCommand.RedoLibraryTags);
            favourite.Text = _preferences.IsFavourite(selected.AssetId, selected.Name) ? "Remove from Favourites" : "Add to Favourites";
            bool root = IsAssetsRoot(selected);
            menu.Items[3].Enabled = !selected.IsFolder;
            menu.Items[4].Enabled = !root;
            menu.Items[5].Enabled = !root;
            menu.Items[7].Enabled = !root;
            menu.Items[8].Enabled = !root;
            menu.Items[9].Enabled = _resources.Clipboard.HasItems &&
                _resources.Clipboard.SourcePaths.All(path => _resources.CanTransfer(path, SelectedFolder()));
            menu.Items[11].Enabled = !root;
        };
        return menu;
    }

    private void PopulateNewMenu(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        items.Clear();
        ResourceRootFolder? root = ResourceFolderPolicy.GetRoot(_resources.Project, SelectedFolder());
        ToolStripItem folder = items.Add("Folder", null, (_, _) => CreateFolder());
        folder.Enabled = root is not null;
        folder.ToolTipText = "Create a subfolder inside the selected resource type.";
        items.Add(new ToolStripSeparator());
        foreach (ResourceDefinition definition in ResourceDefinitions.Creatable)
        {
            if (root is not null && definition.Kind != root.Kind) continue;
            ResourceDefinition captured = definition;
            string label = captured.Kind == ResourceKind.Image ? "Sprite / Image" : captured.DisplayName;
            items.Add($"{captured.IconGlyph}  {label}", null, (_, _) => CreateResource(captured.Kind));
        }
    }

    private void ExternalResourceChanged(object sender, FileSystemEventArgs e)
    {
        if (IsDisposed) return;
        if (e.FullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            ResourceLibraryTags.Invalidate(e.FullPath[..^5]);
        if (Path.GetFileName(e.FullPath).StartsWith(".tags-", StringComparison.OrdinalIgnoreCase)) return;
        try { _watcherDebounce.Change(250, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void QueueExternalRefresh()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                RefreshTree();
                FinderInvalidated?.Invoke(this, EventArgs.Empty);
                StatusMessage?.Invoke(this, "Assets refreshed after external changes.");
            });
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Skipped external asset refresh during handle teardown: {exception.Message}");
        }
    }

    private static ToolStripMenuItem ContextItem(string text, Keys shortcut, Action action)
    {
        ToolStripMenuItem item = new(text)
        {
            ShortcutKeys = shortcut,
            ShowShortcutKeys = true,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is null || _previewsDisposed)
        {
            return;
        }

        ResourceItem? item = e.Node.Tag as ResourceItem;
        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        bool hovered = (e.State & TreeNodeStates.Hot) != 0;
        Color background = selected
            ? Color.FromArgb(55, ThemeService.Palette.Accent)
            : hovered
                ? ThemeService.Palette.SurfaceHover
                : ThemeService.Palette.Surface;
        Rectangle row = new(0, e.Bounds.Y, _tree.ClientSize.Width, e.Bounds.Height);
        using SolidBrush rowBrush = new(background);
        e.Graphics.FillRectangle(rowBrush, row);

        int textX = e.Bounds.X;
        if (item is not null)
        {
            Image icon = _thumbnails.GetCachedIcon(item);
            int iconY = e.Bounds.Y + ((e.Bounds.Height - icon.Height) / 2);
            e.Graphics.DrawImage(icon, textX, iconY, icon.Width, icon.Height);
            textX += icon.Width + 6;
        }
        else
        {
            TextRenderer.DrawText(
                e.Graphics,
                "·",
                Font,
                new Rectangle(textX, e.Bounds.Y, 22, e.Bounds.Height),
                ThemeService.Palette.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            textX += 24;
        }

        string name = e.Node.Text;
        if (item is { IsFolder: false } && _preferences.IsFavourite(item.AssetId, item.Name))
            name = "★ " + name;
        TextRenderer.DrawText(
            e.Graphics,
            name,
            Font,
            new Rectangle(textX, e.Bounds.Y, row.Width - textX - 8, e.Bounds.Height),
            selected ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        if (item is not null
            && _finderResults.TryGetValue(Path.GetFullPath(item.FullPath), out ResourceSearchResult? result))
        {
            int nameWidth = TextRenderer.MeasureText(
                name,
                Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int detailX = textX + nameWidth + 10;
            if (detailX < row.Right - 36)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    $"· {result.MatchLabel}",
                    Font,
                    new Rectangle(detailX, e.Bounds.Y, row.Right - detailX - 8, e.Bounds.Height),
                    ThemeService.Palette.TextMuted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }

    private string FinderToolTip(ResourceItem item)
    {
        if (_finderResults.TryGetValue(item.FullPath, out ResourceSearchResult? result))
            return item.Name + Environment.NewLine + "Matched " + result.MatchLabel.ToLowerInvariant() + ".";
        return item.Name;
    }

    private void OnSelectionChanged(TreeNode node)
    {
        if (_updatingTree) return;
        _selectionKey = node.Tag is ResourceItem selected ? ResourceBrowserProjection.Key(selected) : null;
        NotifyBrowserSelection();
    }

    private void OnNodeDoubleClick(object? sender, TreeNodeMouseClickEventArgs args)
    {
        if (args.Node?.Tag is not ResourceItem item || item.IsFolder)
        {
            // Folders: let TreeView perform its single expand/collapse. Calling Toggle()
            // here double-fires and immediately collapses the folder again.
            return;
        }

        ResourceOpenRequested?.Invoke(this, new ResourceSelectedEventArgs(item));
    }

    private void OpenNode(TreeNode? node)
    {
        if (node?.Tag is not ResourceItem item)
        {
            return;
        }

        if (item.IsFolder)
        {
            if (node.IsExpanded)
            {
                node.Collapse();
            }
            else
            {
                node.Expand();
            }

            return;
        }

        ResourceOpenRequested?.Invoke(this, new ResourceSelectedEventArgs(item));
    }

    private void ImportAssets()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Import assets",
            Multiselect = true,
            Filter =
                "Supported assets|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tga;*.wav;*.mp3;*.ogg;*.flac;*.fbx;*.obj;*.gltf;*.glb;*.dae;*.blend;*.image.json;*.audio.json;*.model.json;*.object.json;*.shader.json;*.pgsl;*.md|" +
                "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tga|" +
                "Audio|*.wav;*.mp3;*.ogg;*.flac|" +
                "Models|*.fbx;*.obj;*.gltf;*.glb;*.dae;*.blend|" +
                "All files|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames.Length == 0)
        {
            return;
        }

        string destination = SelectedFolder();
        ExecuteGuarded(
            () =>
            {
                IReadOnlyList<string> imported = _resources.ImportFiles(destination, dialog.FileNames);
                _thumbnails.Invalidate();
                return imported.LastOrDefault() ?? destination;
            },
            $"Imported {dialog.FileNames.Length} asset(s).");
    }

    private void CreateFolder()
    {
        string destination = SelectedFolder();
        using NamePromptDialog dialog = new("New Folder", "Folder name", "New Folder");
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ExecuteGuarded(
            () =>
            {
                string path = _resources.CreateFolder(destination, dialog.Value);
                return path;
            },
            "Folder created.");
    }

    private void CreateResource(ResourceKind kind)
    {
        ResourceDefinition definition = ResourceDefinitions.Get(kind);
        string destination = SelectedFolder();
        if (ResourceFolderPolicy.GetRoot(_resources.Project, destination) is null)
            destination = ResourceFolderPolicy.RootFor(_resources.Project, kind);
        using NamePromptDialog dialog = new(
            $"New {definition.DisplayName}",
            $"{definition.DisplayName} name",
            $"New {definition.DisplayName}");
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ExecuteGuarded(
            () =>
            {
                string path = _resources.CreateResource(destination, kind, dialog.Value);
                return path;
            },
            $"{definition.DisplayName} created.");
    }

    private void BeginRename()
    {
        ResourceItem? selected = SelectedResource;
        if (selected is null || IsAssetsRoot(selected))
        {
            return;
        }

        _tree.SelectedNode?.BeginEdit();
    }

    private void RenameAfterEdit(object? sender, NodeLabelEditEventArgs e)
    {
        if (e.Node is null || e.CancelEdit || string.IsNullOrWhiteSpace(e.Label) ||
            e.Node.Tag is not ResourceItem item)
        {
            return;
        }

        e.CancelEdit = true;
        string label = e.Label!;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            ExecuteGuarded(() => _resources.Rename(item.FullPath, label), "Resource renamed.");
        }));
    }

    private void SetClipboard(ResourceClipboardOperation operation)
    {
        ResourceItem? selected = SelectedResource;
        if (selected is null || IsAssetsRoot(selected))
        {
            return;
        }

        _resources.SetClipboard(operation, [selected.FullPath]);
        StatusMessage?.Invoke(
            this,
            operation == ResourceClipboardOperation.Copy
                ? $"Copied {selected.Name}."
                : $"Cut {selected.Name}.");
    }

    private void Paste()
    {
        string destination = SelectedFolder();
        ExecuteGuarded(
            () =>
            {
                IReadOnlyList<string> pasted = _resources.Paste(destination);
                return pasted.LastOrDefault() ?? destination;
            },
            "Resources pasted.");
    }

    private void Duplicate()
    {
        ResourceItem? selected = SelectedResource;
        if (selected is null || IsAssetsRoot(selected))
        {
            return;
        }

        ExecuteGuarded(
            () =>
            {
                string path = _resources.Duplicate(selected.FullPath);
                return path;
            },
            "Resource duplicated.");
    }

    private void DeleteSelected()
    {
        ResourceItem? selected = SelectedResource;
        if (selected is null || IsAssetsRoot(selected))
        {
            return;
        }

        if (_settings.Current.General.ConfirmDestructiveActions)
        {
            DialogResult answer = MessageBox.Show(
                this,
                $"Move '{selected.Name}' to the project trash?",
                "Delete resource",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        ExecuteGuarded(
            () =>
            {
                string path = _resources.MoveToTrash(selected.FullPath);
                return path;
            },
            "Resource moved to project trash.");
    }

    private string SelectedFolder()
    {
        ResourceItem? selected = SelectedResource;
        if (selected is null)
        {
            return _resources.AssetsRoot;
        }

        if (selected.IsFolder)
        {
            return selected.FullPath;
        }

        return Path.GetDirectoryName(selected.FullPath) ?? _resources.AssetsRoot;
    }

    private void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ExecuteShortcut(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void BeginDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is not TreeNode node ||
            node.Tag is not ResourceItem item ||
            IsAssetsRoot(item))
        {
            return;
        }

        DataObject data = new();
        data.SetData(ResourceDragFormat, item.FullPath);
        DoDragDrop(data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    private static void OnTreeDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(ResourceDragFormat) == true
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        Point client = _tree.PointToClient(new Point(e.X, e.Y));
        TreeNode? target = _tree.GetNodeAt(client);
        if (target is null)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        _tree.SelectedNode = target;
        string? source = e.Data?.GetData(ResourceDragFormat) as string;
        if (source is null || !_resources.CanTransfer(source, SelectedFolder()))
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        e.Effect = (e.KeyState & 8) != 0
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
    }

    private void OnTreeDragDrop(object? sender, DragEventArgs e)
    {
        string? source = e.Data?.GetData(ResourceDragFormat) as string;
        if (string.IsNullOrWhiteSpace(source) || e.Effect == DragDropEffects.None)
        {
            return;
        }

        string destination = SelectedFolder();
        if (e.Effect == DragDropEffects.Copy)
        {
            ExecuteGuarded(() =>
            {
                string path = _resources.Copy(source, destination);
                return path;
            }, "Resource copied.");
        }
        else
        {
            ExecuteGuarded(() =>
            {
                string path = _resources.Move(source, destination);
                return path;
            }, "Resource moved.");
        }
    }

    private void SelectRightClickedNode(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _tree.SelectedNode = e.Node;
        }
    }

    private void ExecuteGuarded(Func<string> operation, string successMessage)
    {
        try
        {
            _resourceOperationDepth++;
            try { _pathToSelect = operation(); }
            finally { _resourceOperationDepth--; }
            RefreshTree();
            FinderInvalidated?.Invoke(this, EventArgs.Empty);
            StatusMessage?.Invoke(this, successMessage);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Resource operation failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            StatusMessage?.Invoke(this, exception.Message);
        }
    }

    private static bool IsAssetsRoot(ResourceItem item) =>
        item.IsFolder && (item.IsProtectedRoot || string.IsNullOrEmpty(item.RelativePath));

    private static TreeNode? FindNodeByPath(TreeNode node, string path)
    {
        if (node.Tag is ResourceItem item &&
            string.Equals(
                Path.GetFullPath(item.FullPath),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (TreeNode child in node.Nodes)
        {
            TreeNode? found = FindNodeByPath(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}

public sealed class ResourceSelectedEventArgs(ResourceItem resource) : EventArgs
{
    public ResourceItem Resource { get; } = resource;
}

public enum ResourceBrowserCommand
{
    NewFolder,
    Cut,
    Copy,
    Paste,
    Duplicate,
    Rename,
    Delete,
    Refresh,
    Import,
    ToggleFavourite,
    ShowAll,
    ShowFavourites,
    ShowRecent,
    ClearRecent,
    ResetFilters,
    EditLibraryTags,
    UndoLibraryTags,
    RedoLibraryTags,
}

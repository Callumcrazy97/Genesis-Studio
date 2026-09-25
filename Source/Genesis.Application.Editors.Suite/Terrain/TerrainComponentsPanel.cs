using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Left-hand inventory of everything authored on a terrain: paint layers, paths, water, foliage,
/// points of interest, and placed entities nested by type.
/// </summary>
public sealed class TerrainComponentsPanel : Panel
{
    public enum ComponentKind
    {
        Layer,
        Path,
        Water,
        Foliage,
        PointOfInterest,
        Entity,
    }

    public sealed record ComponentSelection(ComponentKind Kind, string Id, string Label);

    private readonly TreeView _tree = new();
    private readonly ToolStrip _tools = EditorChrome.MakeToolbar();
    private readonly TextBox _filter = new();
    private readonly ToolStripDropDownButton _add = new("+")
    {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        ToolTipText = "Add a terrain component",
    };
    private TerrainComponentInventory _inventory = new([], [], [], "No foliage", [], []);
    private string _filterText = "";
    private readonly ImageList _icons = new() { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
    private readonly List<Bitmap> _iconSources = [];

    public TerrainComponentsPanel()
    {
        BackColor = Color.FromArgb(35, 38, 41);
        Dock = DockStyle.Fill;
        MinimumSize = new Size(220, 0);

        Affects = new TerrainAffectsStrip();
        Affects.Changed += (_, _) => AffectsChanged?.Invoke(this, EventArgs.Empty);

        _tree.Dock = DockStyle.Fill;
        _tree.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_tree);
        _tree.ImageList = _icons;
        _tree.BackColor = Color.FromArgb(31, 34, 37);
        _tree.BorderStyle = BorderStyle.None;
        _tree.Font = new Font("Segoe UI", 10f);
        _tree.ForeColor = EditorChrome.Text;
        _tree.FullRowSelect = true;
        _tree.HideSelection = false;
        _tree.ItemHeight = DpiLayout.Scale(this, 28);
        _tree.ShowLines = false;
        _tree.ShowPlusMinus = true;
        _tree.ShowRootLines = true;
        _tree.AfterSelect += (_, _) =>
        {
            if (_tree.SelectedNode?.Tag is ComponentSelection selection)
            {
                SelectionChanged?.Invoke(this, selection);
            }
            else if (_tree.SelectedNode?.Tag is TerrainAffects affects)
            {
                Affects.Value = affects;
                CategorySelected?.Invoke(this, affects);
            }
        };
        _tree.NodeMouseDoubleClick += (_, _) => RequestEdit();
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Node?.Tag is ComponentSelection { Kind: ComponentKind.Entity } selection)
                AssetActivated?.Invoke(this, selection);
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RequestDelete();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                RequestEdit();
                e.Handled = true;
            }
        };
        _tree.MouseUp += OnTreeMouseUp;

        _filter.BorderStyle = BorderStyle.None;
        _filter.Dock = DockStyle.Fill;
        _filter.PlaceholderText = "Search terrain objects…";
        EditorChrome.StyleField(_filter);
        _filter.TextChanged += (_, _) =>
        {
            _filterText = _filter.Text;
            Rebuild(_inventory);
        };
        Panel search = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 8, 10, 6), BackColor = EditorChrome.Surface };
        search.Controls.Add(_filter);
        var searchLabel = new Label { Text = "Search", Dock = DockStyle.Left, Width = 58, Font = new Font("Segoe UI", 10f),
            BackColor = search.BackColor, ForeColor = Color.Silver, TextAlign = ContentAlignment.MiddleLeft };
        search.Controls.Add(searchLabel);
        searchLabel.Click += (_, _) => _filter.Focus();

        _add.DropDownItems.Add("Paint Layer…", null, (_, _) => AddRequested?.Invoke(this, ComponentKind.Layer));
        _add.DropDownItems.Add("Path Network…", null, (_, _) => AddRequested?.Invoke(this, ComponentKind.Path));
        _add.DropDownItems.Add("Water Body…", null, (_, _) => AddRequested?.Invoke(this, ComponentKind.Water));
        _add.DropDownItems.Add("Scatter Foliage…", null, (_, _) => AddRequested?.Invoke(this, ComponentKind.Foliage));
        _add.DropDownItems.Add("Point of Interest…", null, (_, _) => AddRequested?.Invoke(this, ComponentKind.PointOfInterest));
        var objects = new ToolStripMenuItem("Terrain object");
        foreach (TerrainEntityType type in Enum.GetValues<TerrainEntityType>())
        {
            TerrainEntityType captured = type;
            objects.DropDownItems.Add(type == TerrainEntityType.Fluid ? "Water…" : type + "…", null,
                (_, _) => ObjectTypeRequested?.Invoke(this, captured));
        }
        _add.DropDownItems.Insert(0, objects);
        _add.DropDownItems.Insert(1, new ToolStripSeparator());
        _tools.GripStyle = ToolStripGripStyle.Hidden;
        _tools.Dock = DockStyle.Top;
        _tools.Items.Add(_add);
        _add.Text = "+ Add";
        _tools.Items.Add(EditorChrome.ToolButton("/", "Edit the selected terrain object", RequestEdit));
        _tools.Items.Add(EditorChrome.ToolButton("−", "Delete the selected terrain object", RequestDelete));
        _add.Text = "+";
        _tools.AutoSize = false; _tools.Height = 38; _tools.Padding = new Padding(3);
        _tools.BackColor = BackColor;
        foreach (ToolStripItem action in _tools.Items)
        {
            action.AutoSize = false; action.Width = 82; action.Height = 30;
            action.Font = new Font("Segoe UI", 12f); action.ForeColor = Color.White;
            action.Margin = new Padding(2, 0, 2, 0);
            action.BackColor = action == _add ? Color.FromArgb(44, 112, 155) : action.Text == "−" ? Color.FromArgb(146, 53, 47) : Color.FromArgb(57, 61, 65);
        }

        Label header = EditorChrome.SectionLabel("Terrain Wizard");
        header.Text = "Available Assets & Instances";
        header.Height = 30; header.Font = new Font("Segoe UI", 10.5f); header.BackColor = BackColor; header.ForeColor = Color.WhiteSmoke;
        Affects.Visible = false;
        Controls.Add(_tree);
        Controls.Add(Affects);
        Controls.Add(_tools);
        Controls.Add(search);
        Controls.Add(header);
    }

    public TerrainAffectsStrip Affects { get; }

    public event EventHandler<ComponentSelection>? SelectionChanged;
    public event EventHandler<ComponentSelection>? AssetActivated;
    public event EventHandler<ComponentKind>? AddRequested;
    public event EventHandler<ComponentSelection>? EditRequested;
    public event EventHandler<ComponentSelection>? DuplicateRequested;
    public event EventHandler<ComponentSelection>? DeleteRequested;
    public event EventHandler? AffectsChanged;
    public event EventHandler<TerrainAffects>? CategorySelected;
    public event EventHandler<TerrainEntityType>? ObjectTypeRequested;

    public ComponentSelection? CurrentSelection =>
        _tree.SelectedNode?.Tag as ComponentSelection;

    public void SetIcons(IReadOnlyDictionary<string, string> paths)
    {
        _icons.Images.Clear();
        foreach (var image in _iconSources) image.Dispose();
        _iconSources.Clear();
        var blank = new Bitmap(24, 24);
        _iconSources.Add(blank);
        _icons.Images.Add("default", blank);
        foreach (var category in new[] { ("Terrain", "▲", Color.YellowGreen), ("Foliage", "❧", Color.MediumSeaGreen),
            ("Water", "♦", Color.DeepSkyBlue), ("Paths", "∿", Color.Tan), ("Environment", "◇", Color.BurlyWood), ("Objects", "⬡", Color.LightSlateGray) })
        {
            var icon = new Bitmap(24, 24); _iconSources.Add(icon);
            using var graphics = Graphics.FromImage(icon); using var font = new Font("Segoe UI Symbol", 15f);
            TextRenderer.DrawText(graphics, category.Item2, font, new Rectangle(0, 0, 24, 24), category.Item3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            _icons.Images.Add(category.Item1, icon);
        }
        foreach (var pair in paths)
        {
            try { using var source = new Bitmap(pair.Value); var bitmap = new Bitmap(source, 24, 24); _iconSources.Add(bitmap); _icons.Images.Add(pair.Key, bitmap); }
            catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException) { }
        }
    }

    public void Rebuild(TerrainComponentInventory inventory)
    {
        _inventory = inventory;
        string? selectedId = CurrentSelection?.Id;
        ComponentKind? selectedKind = CurrentSelection?.Kind;
        HashSet<string> expanded = _tree.Nodes.Cast<TreeNode>().Where(node => node.IsExpanded).Select(node => node.Text).ToHashSet();
        string? topGroup = _tree.TopNode?.Text;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        TreeNode layers = AddGroup("Terrain", TerrainAffects.Paint);
        foreach ((string id, string label) in inventory.Layers)
            AddLeaf(layers, label, new ComponentSelection(ComponentKind.Layer, id, label));

        TreeNode paths = AddGroup("Paths", TerrainAffects.Paths);
        foreach ((string id, string label) in inventory.Paths)
            AddLeaf(paths, label, new ComponentSelection(ComponentKind.Path, id, label));

        TreeNode water = AddGroup("Water", TerrainAffects.Water);
        foreach ((string id, string label) in inventory.WaterBodies)
            AddLeaf(water, label, new ComponentSelection(ComponentKind.Water, id, label));

        TreeNode foliage = AddGroup("Foliage", TerrainAffects.Foliage);

        TreeNode points = AddGroup("Environment", TerrainAffects.POI);
        foreach ((string id, string label) in inventory.PointsOfInterest)
            AddLeaf(points, label, new ComponentSelection(ComponentKind.PointOfInterest, id, label));

        TreeNode entities = AddGroup("Objects", TerrainAffects.Objects);
        Dictionary<TerrainEntityType, TreeNode> typeNodes = new()
        {
            [TerrainEntityType.Terrain] = layers, [TerrainEntityType.Foliage] = foliage,
            [TerrainEntityType.Fluid] = water, [TerrainEntityType.Environment] = points,
            [TerrainEntityType.Object] = entities, [TerrainEntityType.Tree] = foliage,
        };

        foreach ((string id, string label, TerrainEntityType type) in inventory.Entities)
            AddLeaf(typeNodes[type], label, new ComponentSelection(ComponentKind.Entity, id, label));

        ExpandIfHasChildren(layers);
        ExpandIfHasChildren(paths);
        ExpandIfHasChildren(water);
        ExpandIfHasChildren(foliage);
        ExpandIfHasChildren(points);
        entities.Expand();
        foreach (TreeNode folder in typeNodes.Values)
        {
            if (folder.Nodes.Count > 0)
            {
                ExpandIfHasChildren(folder);
            }
        }

        foreach (TreeNode group in _tree.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(_filterText) || expanded.Contains(group.Text)) group.Expand();
            else group.Collapse();
        }
        foreach (string caption in new[] { "Terrain", "Foliage", "Water", "Paths", "Environment", "Objects" }.Reverse())
        {
            TreeNode? node = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(item => item.Text == caption);
            if (node is null) continue;
            node.Remove();
            if (caption != "Objects" || node.Nodes.Count > 0) _tree.Nodes.Insert(0, node);
        }

        _tree.EndUpdate();
        if (selectedKind is { } kind && !string.IsNullOrWhiteSpace(selectedId))
        {
            Select(kind, selectedId);
        }
        if (topGroup is not null && _tree.Nodes.Cast<TreeNode>().FirstOrDefault(node => node.Text == topGroup) is { } top) _tree.TopNode = top;
    }

    public void Select(ComponentKind kind, string id)
    {
        TreeNode? match = FindNode(_tree.Nodes, kind, id);
        if (match is not null)
        {
            _tree.SelectedNode = match;
        }
    }

    private static TreeNode? FindNode(TreeNodeCollection nodes, ComponentKind kind, string id)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is ComponentSelection selection
                && selection.Kind == kind
                && string.Equals(selection.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            TreeNode? nested = FindNode(node.Nodes, kind, id);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private TreeNode AddGroup(string caption, TerrainAffects affects)
    {
        TreeNode node = new(caption)
        {
            ForeColor = EditorChrome.Muted,
            Tag = affects,
            ImageKey = caption,
            SelectedImageKey = caption,
        };
        _tree.Nodes.Add(node);
        return node;
    }

    private void AddLeaf(TreeNode group, string caption, ComponentSelection selection)
    {
        if (!PassesFilter(caption) && !PassesFilter(selection.Kind.ToString()) && !PassesFilter(group.Text))
        {
            return;
        }

        group.Nodes.Add(new TreeNode(caption)
        {
            ForeColor = EditorChrome.Text,
            Tag = selection,
            ImageKey = _icons.Images.ContainsKey(selection.Id) ? selection.Id : group.ImageKey,
            SelectedImageKey = _icons.Images.ContainsKey(selection.Id) ? selection.Id : group.ImageKey,
        });
    }

    private bool PassesFilter(string text) =>
        string.IsNullOrWhiteSpace(_filterText)
        || text.Contains(_filterText, StringComparison.OrdinalIgnoreCase);

    private void RequestEdit()
    {
        if (CurrentSelection is { } selection)
        {
            EditRequested?.Invoke(this, selection);
        }
    }

    private void RequestDuplicate()
    {
        if (CurrentSelection is { } selection)
        {
            DuplicateRequested?.Invoke(this, selection);
        }
    }

    private void RequestDelete()
    {
        if (CurrentSelection is { } selection)
        {
            DeleteRequested?.Invoke(this, selection);
        }
    }

    private void OnTreeMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        TreeNode? node = _tree.GetNodeAt(e.Location);
        if (node?.Tag is not ComponentSelection selection)
        {
            return;
        }

        _tree.SelectedNode = node;
        ContextMenuStrip menu = new();
        menu.Items.Add("Edit", null, (_, _) => EditRequested?.Invoke(this, selection));
        menu.Items.Add("Duplicate", null, (_, _) => DuplicateRequested?.Invoke(this, selection));
        menu.Items.Add("Delete", null, (_, _) => DeleteRequested?.Invoke(this, selection));
        menu.Show(_tree, e.Location);
    }

    private static void ExpandIfHasChildren(TreeNode node)
    {
        if (node.Nodes.Count > 0) node.Expand();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Dispose();
            foreach (var image in _iconSources) image.Dispose();
            _iconSources.Clear();
        }
        base.Dispose(disposing);
    }
}

public sealed record TerrainComponentInventory(
    IReadOnlyList<(string Id, string Label)> Layers,
    IReadOnlyList<(string Id, string Label)> Paths,
    IReadOnlyList<(string Id, string Label)> WaterBodies,
    string FoliageSummary,
    IReadOnlyList<(string Id, string Label)> PointsOfInterest,
    IReadOnlyList<(string Id, string Label, TerrainEntityType Type)> Entities);

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>Individual room instances follow their authored parents. A separate asset shelf arms
/// placement. Refresh preserves selection, expansion, scrolling and keyboard focus.</summary>
public sealed class RoomObjectsPanel : Panel
{
    private sealed record InstanceDrag(string Id);
    private readonly RoomEditorControl _editor;
    private readonly TextBox _searchBox;
    private readonly RoomInstanceTreeView _tree;
    private readonly TreeNode _root = new("Room instances") { Name = "room-root" };
    private readonly ListBox _assets;
    private readonly ThemedComboBox _layerCombo;
    private readonly CheckBox _chkSnap;
    private readonly CheckBox _chkAlign;
    private readonly NumericUpDown _numRotation;
    private readonly NumericUpDown _numScaleX;
    private readonly NumericUpDown _numScaleY;
    private readonly NumericUpDown _numScaleZ;
    private readonly Label _scaleZLabel;
    private readonly Panel _placementBody;
    private readonly Button _placementToggle;
    private readonly TableLayoutPanel _content;
    private readonly ToolTip _toolTip = new();
    private readonly List<ProjectAssetEntry> _projectObjects = [];
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _instanceNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _layerNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownLayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedIds = new(StringComparer.OrdinalIgnoreCase) { "room-root" };
    private bool _syncing;
    private bool _placementExpanded;
    private bool _instancesMode;

    public void SetInstancesMode(bool instances)
    {
        _instancesMode = instances;
        SuspendLayout();
        for (int row = 0; row < _content.RowCount; row++)
        {
            bool visible = row < 2 || (instances ? row is 2 or 3 : row is 5 or 6);
            if (_content.GetControlFromPosition(0, row) is Control control) control.Visible = visible;
            _content.RowStyles[row].SizeType = !visible ? SizeType.Absolute
                : row == (instances ? 2 : 5) ? SizeType.Percent : SizeType.AutoSize;
            _content.RowStyles[row].Height = !visible ? 0 : row == (instances ? 2 : 5) ? 100 : 0;
        }
        if (_content.GetControlFromPosition(0, 0) is Label heading) heading.Text = instances ? "ROOM INSTANCES" : "OBJECT ASSETS";
        _searchBox.PlaceholderText = instances ? "Search instances…" : "Find objects…";
        SizeObjectSections();
        ResumeLayout(true);
    }
    private Point? _assetDragStart;
    private TreeNode? _dropNode;
    private int _dropEdge;
    private readonly RoomObjectThumbnailRenderer _modelThumbnails = new();
    private readonly HashSet<string> _thumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modelThumbnailKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _thumbnailTimer = new() { Interval = 100 };
    private int _thumbnailGeneration;
    public int ModelThumbnailCount => _modelThumbnailKeys.Count;
    public string ThumbnailError { get; private set; } = string.Empty;

    public event Action<string>? ObjectArmed;
    public event Action<RoomNode>? InstanceSelected;

    public RoomObjectsPanel(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill; BackColor = EditorChrome.Surface; Padding = new Padding(8); Name = "RoomObjectsPanel"; AutoScroll = true;
        _searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search instances and assets…",
            Name = "RoomObjectSearch", BorderStyle = BorderStyle.FixedSingle };
        EditorChrome.StyleField(_searchBox);
        _searchBox.TextChanged += (_, _) => { RefreshRoomInstances(); RefreshAssetShelf(); };
        _tree = new RoomInstanceTreeView { Dock = DockStyle.Fill, Name = "RoomInstanceHierarchy",
            BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text, Font = EditorChrome.BaseFont,
            BorderStyle = BorderStyle.None, HideSelection = false, ShowLines = false, ShowPlusMinus = true,
            ShowNodeToolTips = true, FullRowSelect = true, Indent = 18, ItemHeight = 32, AllowDrop = true,
            LabelEdit = true, DrawMode = TreeViewDrawMode.OwnerDrawAll };
        _tree.Nodes.Add(_root);
        _tree.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_tree);
        _tree.DrawNode += DrawInstance;
        _tree.HandleAdornment = HandleHierarchyAdornment;
        _tree.BeforeSelect += (_, e) =>
        {
            if (_syncing) return;
            if (e.Node?.Tag is RoomNode node) e.Cancel = !_editor.CanInspectNodeInActiveContext(node);
            else if (e.Node?.Tag is RoomLayer) e.Cancel = _editor.Navigation.CurrentSection != RoomNavSection.Objects;
        };
        _tree.AfterSelect += (_, e) =>
        {
            if (_syncing) return;
            if (e.Node?.Tag is RoomNode node) InstanceSelected?.Invoke(node);
            else if (e.Node?.Tag is RoomLayer layer)
            {
                _editor.Placement.TargetLayerId = layer.Id;
                RefreshLayers();
                _editor.ActiveRoomEditContextChanged();
                _editor.Select(null);
            }
            else if (ReferenceEquals(e.Node, _root)) _editor.Select(null);
        };
        _tree.AfterExpand += (_, e) => { if (!_syncing && e.Node is not null) _expandedIds.Add(e.Node.Name); };
        _tree.AfterCollapse += (_, e) => { if (!_syncing && e.Node is not null) _expandedIds.Remove(e.Node.Name); };
        _tree.NodeMouseClick += OnInstanceClick;
        _tree.BeforeLabelEdit += (_, e) => e.CancelEdit = e.Node?.Tag switch
        { RoomNode node => !_editor.CanEditNodeInActiveContext(node),
            RoomLayer => _editor.Navigation.CurrentSection != RoomNavSection.Objects, _ => true };
        _tree.AfterLabelEdit += (_, e) =>
        {
            e.CancelEdit = true;
            if (e.Node?.Tag is RoomNode node && !string.IsNullOrWhiteSpace(e.Label))
            { _editor.SetNodeName(node, e.Label.Trim()); RefreshRoomInstances(); }
            else if (e.Node?.Tag is RoomLayer layer && !string.IsNullOrWhiteSpace(e.Label))
            { _editor.RenameRoomLayer(layer, e.Label.Trim()); RefreshRoomInstances(); RefreshLayers(); }
        };
        _tree.ItemDrag += (_, e) =>
        {
            if (e.Item is TreeNode { Tag: RoomNode node } && _editor.CanEditNodeInActiveContext(node))
                _tree.DoDragDrop(new InstanceDrag(node.Id), DragDropEffects.Move);
        };
        _tree.DragEnter += DragOverInstance; _tree.DragOver += DragOverInstance; _tree.DragDrop += DropInstance;
        _tree.DragLeave += (_, _) => ClearDropIndicator();
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteCurrent(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { _tree.SelectedNode?.BeginEdit(); e.Handled = true; }
        };
        _assets = new ListBox { Dock = DockStyle.Fill, Name = "RoomObjectAssetShelf",
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 66, BorderStyle = BorderStyle.None,
            IntegralHeight = false, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text, Font = EditorChrome.BaseFont };
        _assets.DrawItem += DrawAsset;
        _assets.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_assets);
        HandleCreated += (_, _) => { EditorScrollHost.ApplyDarkScrollTheme(this); _thumbnailTimer.Start(); };
        _thumbnailTimer.Tick += (_, _) => UpdateModelThumbnails();
        _assets.SelectedIndexChanged += (_, _) =>
        { if (!_syncing && _assets.SelectedItem is ProjectAssetEntry entry) ObjectArmed?.Invoke(entry.FullPath); };
        _assets.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _assetDragStart = e.Location; };
        _assets.MouseUp += (_, _) => _assetDragStart = null;
        _assets.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _assetDragStart is not Point start
                || Math.Abs(e.X - start.X) + Math.Abs(e.Y - start.Y) < 10
                || _assets.SelectedItem is not ProjectAssetEntry entry) return;
            _assetDragStart = null; _assets.DoDragDrop(entry.FullPath, DragDropEffects.Copy);
        };
        _toolTip.SetToolTip(_assets, "Choose an object, then click in the room to place it.");
        _toolTip.SetToolTip(_tree, "Drag between rows to reorder; onto a row to parent. Drop on a layer to unparent and move there.");

        var content = _content = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty, Padding = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var style in new[] { new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize),
            new RowStyle(SizeType.Percent, 56), new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize),
            new RowStyle(SizeType.Percent, 44), new RowStyle(SizeType.AutoSize) }) content.RowStyles.Add(style);
        content.Controls.Add(Header("ROOM OBJECTS"), 0, 0);
        var searchHost = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 6) };
        searchHost.Controls.Add(_searchBox); content.Controls.Add(searchHost, 0, 1);
        content.Controls.Add(_tree, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 4, 0, 6) };
        actions.Controls.Add(ActionButton("Add…", () => _editor.AddGameObjectFromPicker()));
        actions.Controls.Add(ActionButton("Duplicate", () => _editor.DuplicateSelected()));
        actions.Controls.Add(ActionButton("Delete", DeleteCurrent));
        actions.Controls.Add(ActionButton("+ Layer", () =>
        {
            RoomLayer layer = _editor.AddRoomLayer(); RefreshRoomInstances(); RefreshLayers();
            if (_layerNodes.TryGetValue(layer.Id, out TreeNode? item)) { _tree.SelectedNode = item; item.BeginEdit(); }
        }));
        content.Controls.Add(actions, 0, 3); content.Controls.Add(Header("OBJECT ASSETS"), 0, 4); content.Controls.Add(_assets, 0, 5);

        _layerCombo = new ThemedComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = "Name", Name = "RoomPlacementLayer" };
        _layerCombo.Format += (_, e) => { if (e.ListItem is RoomLayer layer) e.Value = layer.Name; };
        _layerCombo.SelectedIndexChanged += (_, _) =>
        { if (!_syncing && _layerCombo.SelectedItem is RoomLayer layer)
            { _editor.Placement.TargetLayerId = layer.Id; _editor.ActiveRoomEditContextChanged(); } };
        _chkSnap = new CheckBox { Text = "Snap to grid", AutoSize = true, ForeColor = EditorChrome.Text };
        _chkAlign = new CheckBox { Text = "Align to terrain", AutoSize = true, ForeColor = EditorChrome.Text };
        _chkSnap.CheckedChanged += (_, _) => { if (!_syncing) _editor.SetSnapEnabled(_chkSnap.Checked); };
        _chkAlign.CheckedChanged += (_, _) => { if (!_syncing) _editor.SetAlignToTerrainNormal(_chkAlign.Checked); };
        _numRotation = MakeNumeric(-360, 360, 1);
        _numRotation.ValueChanged += (_, _) => { if (!_syncing) _editor.Placement.PlacementRotation = (float)_numRotation.Value; };
        _numScaleX = MakeNumeric(-100, 100, 2); _numScaleY = MakeNumeric(-100, 100, 2); _numScaleZ = MakeNumeric(-100, 100, 2);
        _numScaleX.ValueChanged += (_, _) => UpdateScale(); _numScaleY.ValueChanged += (_, _) => UpdateScale();
        _numScaleZ.ValueChanged += (_, _) => { if (!_syncing) _editor.Placement.PlacementScaleZ = (float)_numScaleZ.Value; };
        var scale = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        scale.Controls.Add(new Label { Text = "X", AutoSize = true, ForeColor = EditorChrome.Muted, Padding = new Padding(0, 5, 0, 0) });
        scale.Controls.Add(_numScaleX);
        scale.Controls.Add(new Label { Text = "Y", AutoSize = true, ForeColor = EditorChrome.Muted, Padding = new Padding(0, 5, 0, 0) });
        scale.Controls.Add(_numScaleY);
        _scaleZLabel = new Label { Text = "Z", AutoSize = true, ForeColor = EditorChrome.Muted, Padding = new Padding(0, 5, 0, 0) };
        scale.Controls.Add(_scaleZLabel); scale.Controls.Add(_numScaleZ);
        var placement = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4, 6, 4, 4) };
        placement.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); placement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddPlacementRow("Layer", _layerCombo); AddPlacementRow("", _chkSnap); AddPlacementRow("", _chkAlign);
        AddPlacementRow("Rotation", _numRotation); AddPlacementRow("Scale", scale);
        _placementBody = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false };
        _placementBody.Controls.Add(placement);
        _placementToggle = ActionButton("▸ Placement options", () => SetPlacementOptionsExpanded(!PlacementOptionsExpanded));
        _placementToggle.Dock = DockStyle.Top; _placementToggle.TextAlign = ContentAlignment.MiddleLeft;
        var placementSection = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Name = "RoomPlacementOptions", Margin = Padding.Empty };
        placementSection.Controls.Add(_placementBody); placementSection.Controls.Add(_placementToggle);
        content.Controls.Add(placementSection, 0, 6); Controls.Add(content); RefreshLayers();
        SizeChanged += (_, _) => SizeObjectSections();
        SizeObjectSections();

        void AddPlacementRow(string label, Control control)
        {
            int row = placement.RowCount++; placement.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            placement.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = EditorChrome.Muted,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
            placement.Controls.Add(control, 1, row);
        }
    }

    public TreeView InstanceHierarchy => _tree;
    public int HierarchyStructureUpdateCount { get; private set; }
    public int HierarchyRefreshCount { get; private set; }
    public ListBox ObjectAssetShelf => _assets;
    public bool PlacementOptionsExpanded => _placementExpanded;
    public void SetSearch(string query) => _searchBox.Text = query ?? string.Empty;
    public void SetPlacementOptionsExpanded(bool expanded)
    {
        _placementExpanded = expanded; _placementBody.Visible = expanded;
        _placementToggle.Text = expanded ? "▾ Placement options" : "▸ Placement options";
        if (expanded) RefreshLayers();
        SizeObjectSections();
    }

    private void SizeObjectSections()
    {
        if (_content is null) return;
        // Keep useful instance and asset lists when optional placement fields expand.
        // The outer panel scrolls on short windows instead of reducing either list to zero.
        _content.Height = Math.Max(ClientSize.Height - Padding.Vertical, !_instancesMode && _placementExpanded ? 500 : 260);
    }

    public void RefreshObjects(IReadOnlyList<ProjectAssetEntry> objects)
    {
        _modelThumbnails.Reset(++_thumbnailGeneration);
        _thumbnailRequests.Clear(); _modelThumbnailKeys.Clear(); ThumbnailError = string.Empty;
        _projectObjects.Clear(); _projectObjects.AddRange(objects);
        foreach (Bitmap thumbnail in _thumbnails.Values) thumbnail.Dispose(); _thumbnails.Clear();
        RefreshAssetShelf(); RefreshRoomInstances(); RefreshLayers();
    }

    public RoomLayer? ActiveObjectLayer => _layerCombo.SelectedItem as RoomLayer;

    public void SyncSelection()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            TreeNode? selected = _editor.SelectedNode is { } node
                && _instanceNodes.TryGetValue(node.Id, out TreeNode? entry) ? entry : null;
            // An active layer (or the root) is a navigation selection, not a RoomNode.
            // A value/Inspector refresh must not deselect it or expand another branch.
            if (selected is null && _editor.SelectedNode is null
                && (ReferenceEquals(_tree.SelectedNode, _root)
                    || _tree.SelectedNode?.Tag is RoomLayer layer
                        && string.Equals(layer.Id, _editor.Placement.TargetLayerId, StringComparison.OrdinalIgnoreCase)))
                return;
            if (!ReferenceEquals(_tree.SelectedNode, selected)) _tree.SelectedNode = selected;
        }
        finally { _syncing = false; }
    }

    public void RefreshRoomInstances()
    {
        if (_syncing || _tree.IsDisposed) return;
        HierarchyRefreshCount++;
        string? selected = _editor.SelectedNode?.Id ?? _tree.SelectedNode?.Name;
        string? top = _tree.TopNode?.Name;
        string query = _searchBox.Text.Trim();
        Dictionary<string, RoomNode> nodes = _editor.Room.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> included = nodes.Values.Where(node => query.Length == 0
            || node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || node.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.GameObject?.Prefab?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in included.ToArray())
        {
            HashSet<string> chain = new(StringComparer.OrdinalIgnoreCase) { id }; RoomNode current = nodes[id];
            while (nodes.TryGetValue(current.ParentId, out RoomNode? parent) && chain.Add(parent.Id))
            { included.Add(parent.Id); current = parent; }
        }
        _syncing = true; _tree.BeginUpdate();
        try
        {
            // Keep native TreeNode handles for unchanged items. Never clear and repopulate
            // the hierarchy for a value edit, selection, disclosure, visibility or lock click.
            Dictionary<TreeNode, List<TreeNode>> children = new() { [_root] = [] };
            HashSet<string> layers = new(StringComparer.OrdinalIgnoreCase);
            foreach (RoomLayer layer in _editor.Room.Layers.OrderBy(layer => layer.Order))
            {
                layers.Add(layer.Id);
                if (!_layerNodes.TryGetValue(layer.Id, out TreeNode? item))
                    _layerNodes[layer.Id] = item = new TreeNode { Name = "layer:" + layer.Id };
                item.Text = layer.Name; item.Tag = layer;
                item.ToolTipText = $"Layer {layer.Name} · order {layer.Order}" + (layer.Locked ? " · locked" : "");
                children[_root].Add(item); children[item] = [];
                if (_knownLayers.Add(layer.Id)) _expandedIds.Add(item.Name);
            }
            foreach (RoomNode node in _editor.Room.Nodes.Where(node => included.Contains(node.Id)))
            {
                if (!_instanceNodes.TryGetValue(node.Id, out TreeNode? item))
                    _instanceNodes[node.Id] = item = new TreeNode { Name = node.Id };
                item.Text = node.Name; item.Tag = node;
                item.ToolTipText = $"{node.Name} · {node.Kind}" + (node.Enabled ? "" : " · hidden")
                    + (_editor.IsNodeLocked(node) ? " · locked" : "");
                children[item] = [];
            }
            foreach (RoomNode node in _editor.Room.Nodes.Where(node => included.Contains(node.Id)))
            {
                TreeNode owner = _root;
                if (included.Contains(node.ParentId) && _instanceNodes.TryGetValue(node.ParentId, out TreeNode? parent)
                    && CanParent(node, (RoomNode)parent.Tag!)) owner = parent;
                else if (_layerNodes.TryGetValue(node.LayerId, out TreeNode? layer) && layers.Contains(node.LayerId)) owner = layer;
                children[owner].Add(_instanceNodes[node.Id]);
            }
            bool changed = false;
            foreach (KeyValuePair<TreeNode, List<TreeNode>> branch in children)
                changed |= ReconcileChildren(branch.Key.Nodes, branch.Value);
            foreach (string id in _instanceNodes.Keys.Where(id => !included.Contains(id)).ToArray()) _instanceNodes.Remove(id);
            foreach (string id in _layerNodes.Keys.Where(id => !layers.Contains(id)).ToArray()) _layerNodes.Remove(id);
            if (changed) HierarchyStructureUpdateCount++;
            _root.Text = query.Length == 0 ? $"Room instances · {nodes.Count}" : $"Matching instances · {included.Count}";
            foreach (TreeNode item in children.Keys)
            {
                bool expanded = _expandedIds.Contains(item.Name) || query.Length > 0;
                if (expanded && !item.IsExpanded) item.Expand();
                else if (!expanded && item.IsExpanded) item.Collapse(ignoreChildren: true);
            }
            TreeNode? selectedNode = FindTreeNode(selected);
            if (!ReferenceEquals(_tree.SelectedNode, selectedNode)) _tree.SelectedNode = selectedNode;
            if (FindTreeNode(top) is { TreeView: not null } topNode && topNode.IsVisible) _tree.TopNode = topNode;
        }
        finally { _tree.EndUpdate(); _syncing = false; }
        _tree.Invalidate();
    }

    private TreeNode? FindTreeNode(string? id) => id == _root.Name ? _root
        : id?.StartsWith("layer:", StringComparison.Ordinal) == true ? _layerNodes.GetValueOrDefault(id[6..])
        : _instanceNodes.GetValueOrDefault(id ?? string.Empty);

    private static bool ReconcileChildren(TreeNodeCollection actual, IReadOnlyList<TreeNode> desired)
    {
        bool changed = false;
        HashSet<TreeNode> wanted = new(desired);
        for (int i = actual.Count - 1; i >= 0; i--)
            if (!wanted.Contains(actual[i])) { actual.RemoveAt(i); changed = true; }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < actual.Count && ReferenceEquals(actual[i], desired[i])) continue;
            desired[i].Remove(); actual.Insert(i, desired[i]); changed = true;
        }
        return changed;
    }

    public void RefreshLayers()
    {
        bool before = _syncing; _syncing = true;
        try
        {
            string? selected = _editor.Placement.TargetLayerId ?? (_layerCombo.SelectedItem as RoomLayer)?.Id;
            _layerCombo.Items.Clear(); foreach (RoomLayer layer in _editor.Room.Layers) _layerCombo.Items.Add(layer);
            _layerCombo.SelectedItem = _editor.Room.Layers.FirstOrDefault(layer => layer.Id == selected)
                ?? _editor.Room.Layers.FirstOrDefault(layer => !layer.Locked) ?? _editor.Room.Layers.FirstOrDefault();
            _chkSnap.Checked = _editor.Room.Settings.SnapEnabled; _chkAlign.Checked = _editor.AlignToTerrainNormal;
            bool threeD = _editor.Room.Dimension == RoomDimension.ThreeD;
            _chkAlign.Visible = threeD; _numScaleZ.Visible = threeD; _scaleZLabel.Visible = threeD;
            _numRotation.Value = Math.Clamp((decimal)_editor.Placement.PlacementRotation, _numRotation.Minimum, _numRotation.Maximum);
            _numScaleX.Value = Math.Clamp((decimal)_editor.Placement.PlacementScale.X, _numScaleX.Minimum, _numScaleX.Maximum);
            _numScaleY.Value = Math.Clamp((decimal)_editor.Placement.PlacementScale.Y, _numScaleY.Minimum, _numScaleY.Maximum);
            _numScaleZ.Value = Math.Clamp((decimal)_editor.Placement.PlacementScaleZ, _numScaleZ.Minimum, _numScaleZ.Maximum);
        }
        finally { _syncing = before; }
    }

    public bool ReparentInstance(string childId, string? parentId)
    {
        RoomNode? child = _editor.Room.Nodes.FirstOrDefault(node => node.Id == childId);
        RoomNode? parent = _editor.Room.Nodes.FirstOrDefault(node => node.Id == parentId);
        if (child is null || (parentId is { Length: > 0 } && parent is null)
            || parent is not null && _editor.IsNodeLocked(parent) || !CanParent(child, parent)) return false;
        bool changed = _editor.SetNodeParent(child, parent);
        if (changed) { if (parent is not null) _expandedIds.Add(parent.Id); RefreshRoomInstances(); } return changed;
    }

    public bool MoveInstanceToLayer(string nodeId, string layerId)
    {
        RoomNode? node = _editor.Room.Nodes.FirstOrDefault(item => item.Id == nodeId);
        RoomLayer? layer = _editor.Room.Layers.FirstOrDefault(item => item.Id == layerId);
        if (node is null || layer is null || layer.Locked || _editor.IsNodeLocked(node)) return false;
        bool changed = _editor.MoveHierarchyInstance(node, null, layer);
        if (changed) { _expandedIds.Add("layer:" + layer.Id); RefreshRoomInstances(); RefreshLayers(); }
        return changed;
    }

    private void DeleteCurrent()
    {
        if (_tree.SelectedNode?.Tag is RoomLayer layer)
        { _editor.RemoveRoomLayer(layer); RefreshRoomInstances(); RefreshLayers(); }
        else _editor.DeleteSelected();
    }

    private void MoveLayer(RoomLayer layer, int direction)
    {
        RoomLayer[] layers = _editor.Room.Layers.OrderBy(item => item.Order).ToArray();
        int index = Array.IndexOf(layers, layer), neighbour = index + direction;
        if (index < 0 || neighbour < 0 || neighbour >= layers.Length) return;
        _editor.SetLayerOrder(layer, layers[neighbour].Order + direction);
        RefreshRoomInstances(); RefreshLayers();
    }

    private bool CanParent(RoomNode child, RoomNode? parent)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { child.Id };
        while (parent is not null)
        { if (!visited.Add(parent.Id)) return false; string next = parent.ParentId; parent = _editor.Room.Nodes.FirstOrDefault(node => node.Id == next); }
        return true;
    }

    private void DragOverInstance(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        ClearDropIndicator();
        if (e.Data?.GetData(typeof(InstanceDrag)) is not InstanceDrag drag) return;
        RoomNode? child = _editor.Room.Nodes.FirstOrDefault(node => node.Id == drag.Id);
        Point point = _tree.PointToClient(new Point(e.X, e.Y));
        TreeNode? target = _tree.GetNodeAt(point);
        if (target is null) return;
        if (target?.Tag is RoomLayer { Locked: true }) return;
        int edge = DropEdge(target!, point.Y);
        RoomNode? targetNode = target?.Tag as RoomNode;
        RoomNode? parent = edge == 0 ? targetNode : _editor.Room.Nodes.FirstOrDefault(node => node.Id == targetNode?.ParentId);
        if (child is null || child == targetNode || !_editor.CanEditNodeInActiveContext(child)
            || targetNode is not null && !_editor.CanEditNodeInActiveContext(targetNode)
            || target?.Tag is RoomLayer destination && !ReferenceEquals(destination, ActiveObjectLayer)
            || !CanParent(child, parent)) return;
        e.Effect = DragDropEffects.Move;
        _dropNode = target; _dropEdge = edge; _tree.Invalidate();
    }
    private void DropInstance(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
        if (e.Data?.GetData(typeof(InstanceDrag)) is not InstanceDrag drag) return;
        DropHierarchyAt(drag.Id, _tree.PointToClient(new Point(e.X, e.Y)));
    }

    public bool DropHierarchyAt(string nodeId, Point point)
    {
        TreeNode? item = _tree.GetNodeAt(point);
        if (item is null) return false;
        if (item.Tag is RoomLayer layer) return MoveInstanceToLayer(nodeId, layer.Id);
        if (item.Tag is not RoomNode target) return ReparentInstance(nodeId, null);
        int edge = DropEdge(item, point.Y);
        if (edge == 0) return ReparentInstance(nodeId, target.Id);
        RoomNode? node = _editor.Room.Nodes.FirstOrDefault(candidate => candidate.Id == nodeId);
        RoomNode? parent = _editor.Room.Nodes.FirstOrDefault(candidate => candidate.Id == target.ParentId);
        RoomLayer? targetLayer = _editor.Room.Layers.FirstOrDefault(candidate => candidate.Id == target.LayerId);
        if (node is null || targetLayer is null) return false;
        bool changed = _editor.MoveHierarchyInstance(node, parent, targetLayer, target, edge > 0);
        if (changed) RefreshRoomInstances();
        return changed;
    }

    private int DropEdge(TreeNode node, int y) => node.Tag is not RoomNode ? 0
        : y - node.Bounds.Top < _tree.ItemHeight / 4 ? -1
        : y - node.Bounds.Top >= _tree.ItemHeight * 3 / 4 ? 1 : 0;

    private void ClearDropIndicator()
    {
        if (_dropNode is null) return;
        _dropNode = null; _dropEdge = 0; _tree.Invalidate();
    }

    private Rectangle DisclosureBounds(TreeNode node) => new(
        Math.Max(0, node.Bounds.Left - _tree.Indent), node.Bounds.Top, _tree.Indent, _tree.ItemHeight);

    internal bool HandleHierarchyAdornment(Point point, bool doubleClick = false)
    {
        TreeViewHitTestInfo hit = _tree.HitTest(point);
        TreeNode? item = hit.Node;
        if (item is null) return false;
        if (item.Nodes.Count > 0 && (DisclosureBounds(item).Contains(point)
            || (hit.Location & TreeViewHitTestLocations.PlusMinus) != 0))
        {
            if (!doubleClick)
            {
                if (item.IsExpanded) item.Collapse(ignoreChildren: true); else item.Expand();
                _tree.Invalidate();
            }
            return true;
        }
        bool eye = EyeBounds(item.Bounds).Contains(point), padlock = LockBounds(item.Bounds).Contains(point);
        if (!eye && !padlock) return false;
        if (doubleClick) return true;
        if (item.Tag is RoomLayer layer)
        {
            if (eye) _editor.SetLayerVisibility(layer, !layer.Enabled);
            else _editor.SetLayerLocked(layer, !layer.Locked);
        }
        else if (item.Tag is RoomNode node && _editor.CanInspectNodeInActiveContext(node))
        {
            if (eye) _editor.SetNodeEnabled(node, !node.Enabled);
            else _editor.SetNodeLocked(node, !node.Locked);
        }
        _tree.Invalidate();
        return true;
    }

    private void OnInstanceClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        // Left clicks are either native selection or an adornment handled before native input.
        // In particular, selecting a layer is not a request to rebuild the tree.
        if (e.Node is null || e.Button != MouseButtons.Right) return;
        if (e.Node.Tag is RoomLayer layer)
        {
            if (_editor.Navigation.CurrentSection != RoomNavSection.Objects) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Rename layer", null, (_, _) => e.Node.BeginEdit());
            menu.Items.Add("Move layer up", null, (_, _) => MoveLayer(layer, -1));
            menu.Items.Add("Move layer down", null, (_, _) => MoveLayer(layer, 1));
            menu.Items.Add("Delete layer", null, (_, _) => { _editor.RemoveRoomLayer(layer); RefreshRoomInstances(); RefreshLayers(); })
                .Enabled = !layer.Locked && _editor.Room.Layers.Count > 1;
            menu.Closed += (_, _) => menu.Dispose(); menu.Show(_tree, e.Location);
        }
        else if (e.Node.Tag is RoomNode node && _editor.CanInspectNodeInActiveContext(node))
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Rename", null, (_, _) => e.Node.BeginEdit()).Enabled = _editor.CanEditNodeInActiveContext(node);
            menu.Items.Add("Move to root", null, (_, _) => ReparentInstance(node.Id, null))
                .Enabled = _editor.CanEditNodeInActiveContext(node) && node.ParentId.Length > 0;
            menu.Closed += (_, _) => menu.Dispose(); menu.Show(_tree, e.Location);
        }
    }
    private Rectangle EyeBounds(Rectangle row) => new(Math.Max(0, _tree.ClientSize.Width - 56), row.Top, 28, _tree.ItemHeight);
    private Rectangle LockBounds(Rectangle row) => new(Math.Max(0, _tree.ClientSize.Width - 28), row.Top, 28, _tree.ItemHeight);

    private void DrawInstance(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is null) return;
        bool selected = e.Node == _tree.SelectedNode;
        Rectangle row = new(e.Node.Bounds.Left, e.Bounds.Top, Math.Max(0, _tree.ClientSize.Width - e.Node.Bounds.Left), _tree.ItemHeight);
        using SolidBrush background = new(selected ? EditorChrome.Raised : _tree.BackColor);
        e.Graphics.FillRectangle(background, new Rectangle(0, row.Top, _tree.ClientSize.Width, row.Height));
        if (e.Node == _dropNode)
        {
            using Pen indicator = new(EditorChrome.Accent, 2);
            if (_dropEdge == 0) e.Graphics.DrawRectangle(indicator, 1, row.Top + 1, _tree.ClientSize.Width - 3, row.Height - 3);
            else
            {
                int y = _dropEdge < 0 ? row.Top + 1 : row.Bottom - 2;
                e.Graphics.DrawLine(indicator, row.Left, y, _tree.ClientSize.Width - 2, y);
            }
        }
        if (e.Node.Nodes.Count > 0)
        {
            int x = row.Left - 10, y = row.Top + row.Height / 2;
            Point[] arrow = e.Node.IsExpanded
                ? [new(x - 4, y - 2), new(x + 4, y - 2), new(x, y + 3)]
                : [new(x - 2, y - 4), new(x - 2, y + 4), new(x + 3, y)];
            using SolidBrush glyph = new(EditorChrome.Muted);
            e.Graphics.FillPolygon(glyph, arrow);
        }
        if (e.Node.Tag is not (RoomNode or RoomLayer))
        { TextRenderer.DrawText(e.Graphics, e.Node.Text, EditorChrome.SmallFont, row, EditorChrome.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); return; }
        RoomNode? node = e.Node.Tag as RoomNode;
        RoomLayer? layer = e.Node.Tag as RoomLayer;
        int width = Math.Max(0, EyeBounds(row).Left - row.Left - 4);
        bool enabled = node?.Enabled ?? layer!.Enabled;
        Color text = enabled ? (layer is null ? EditorChrome.Text : EditorChrome.Accent) : EditorChrome.Muted;
        TextRenderer.DrawText(e.Graphics, node?.Name ?? layer!.Name, EditorChrome.BaseFont, new Rectangle(row.Left, row.Top, width, row.Height), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle eye = EyeBounds(row), padlock = LockBounds(row);
        using Pen eyePen = new(enabled ? EditorChrome.Text : EditorChrome.Muted, 1.3f);
        e.Graphics.DrawEllipse(eyePen, eye.Left + 6, eye.Top + 11, 16, 10); e.Graphics.FillEllipse(eyePen.Brush, eye.Left + 12, eye.Top + 14, 4, 4);
        if (!enabled) e.Graphics.DrawLine(eyePen, eye.Left + 5, eye.Top + 23, eye.Left + 23, eye.Top + 9);
        bool locked = node is null ? layer!.Locked : _editor.IsNodeLocked(node);
        using Pen lockPen = new(locked ? EditorChrome.Warning : EditorChrome.Muted, 1.3f);
        e.Graphics.DrawRectangle(lockPen, padlock.Left + 8, padlock.Top + 14, 12, 10);
        e.Graphics.DrawArc(lockPen, padlock.Left + (locked ? 10 : 15), padlock.Top + 7, 8, 12, 180, 180);
    }

    private void RefreshAssetShelf()
    {
        string? selected = (_assets.SelectedItem as ProjectAssetEntry)?.FullPath; int top = _assets.TopIndex;
        bool before = _syncing; _syncing = true; _assets.BeginUpdate();
        try
        {
            _assets.Items.Clear(); string query = _searchBox.Text.Trim();
            foreach (ProjectAssetEntry entry in _projectObjects.Where(entry => query.Length == 0 || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))) _assets.Items.Add(entry);
            _assets.SelectedItem = _assets.Items.OfType<ProjectAssetEntry>().FirstOrDefault(entry => entry.FullPath == selected);
            if (_assets.Items.Count > 0) _assets.TopIndex = Math.Clamp(top, 0, _assets.Items.Count - 1);
        }
        finally { _assets.EndUpdate(); _syncing = before; }
    }

    private void DrawAsset(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _assets.Items[e.Index] is not ProjectAssetEntry entry) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using SolidBrush background = new(selected ? EditorChrome.Raised : _assets.BackColor); e.Graphics.FillRectangle(background, e.Bounds);
        Bitmap thumbnail = Thumbnail(entry); e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(thumbnail, new Rectangle(e.Bounds.Left + 5, e.Bounds.Top + 5, 56, 56));
        Rectangle name = new(e.Bounds.Left + 70, e.Bounds.Top + 7, Math.Max(1, e.Bounds.Width - 78), 50);
        TextRenderer.DrawText(e.Graphics, entry.DisplayName, EditorChrome.BaseFont, name, EditorChrome.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (selected) { using Pen accent = new(EditorChrome.Accent, 2); e.Graphics.DrawLine(accent, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom); }
    }

    private Bitmap Thumbnail(ProjectAssetEntry entry)
    {
        if (_thumbnails.TryGetValue(entry.FullPath, out Bitmap? cached)) return cached;
        try
        {
            string? raster = ObjectResourceReader.ResolveImageFile(entry.FullPath, _editor.ProjectRoot);
            if (raster is not null && File.Exists(raster))
            {
                using FileStream stream = File.OpenRead(raster); using System.Drawing.Image source = System.Drawing.Image.FromStream(stream);
                Bitmap thumbnail = new(56, 56); using Graphics graphics = Graphics.FromImage(thumbnail); graphics.Clear(EditorChrome.Canvas);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                float fit = 50f / Math.Max(source.Width, source.Height), width = source.Width * fit, height = source.Height * fit;
                graphics.DrawImage(source, (56f - width) / 2, (56f - height) / 2, width, height);
                return _thumbnails[entry.FullPath] = thumbnail;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.ExternalException or Newtonsoft.Json.JsonException) { }
        Bitmap fallback = new(56, 56);
        using (Graphics graphics = Graphics.FromImage(fallback))
        {
            graphics.Clear(EditorChrome.Canvas); graphics.SmoothingMode = SmoothingMode.AntiAlias; using Pen pen = new(EditorChrome.Accent, 2);
            Point[] cube = [new(28, 7), new(47, 17), new(47, 39), new(28, 49), new(9, 39), new(9, 17)];
            graphics.DrawPolygon(pen, cube); graphics.DrawLine(pen, 9, 17, 28, 28); graphics.DrawLine(pen, 47, 17, 28, 28); graphics.DrawLine(pen, 28, 28, 28, 49);
        }
        return _thumbnails[entry.FullPath] = fallback;
    }

    private void UpdateScale() { if (!_syncing) _editor.Placement.PlacementScale = new Vector2((float)_numScaleX.Value, (float)_numScaleY.Value); }
    private void UpdateModelThumbnails()
    {
        while (_modelThumbnails.TryTake(out var result) && result is not null)
        {
            if (result.Generation != _thumbnailGeneration) continue;
            if (result.Error is not null)
            {
                ThumbnailError = result.Error;
                _toolTip.SetToolTip(_assets, "Model preview unavailable: " + result.Error);
            }
            if (result.Pixels is null) continue;
            Bitmap image = new(RoomObjectThumbnailRenderer.Size, RoomObjectThumbnailRenderer.Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), System.Drawing.Imaging.ImageLockMode.WriteOnly, image.PixelFormat);
            try { System.Runtime.InteropServices.Marshal.Copy(result.Pixels, 0, data.Scan0, result.Pixels.Length); }
            finally { image.UnlockBits(data); }
            if (_thumbnails.Remove(result.ObjectPath, out Bitmap? previous)) previous.Dispose();
            _thumbnails[result.ObjectPath] = image;
            _modelThumbnailKeys.Add(result.ObjectPath);
            _assets.Invalidate();
        }
        if (!Visible || !IsHandleCreated || _assets.Items.Count == 0) return;
        int first = Math.Max(0, _assets.TopIndex);
        int last = Math.Min(_assets.Items.Count, first + Math.Max(1, _assets.Height / _assets.ItemHeight) + 1);
        for (int index = first; index < last; index++)
        {
            if (_assets.Items[index] is ProjectAssetEntry entry && !_thumbnailRequests.Contains(entry.FullPath)
                && _modelThumbnails.Submit(new(entry.FullPath, _editor.ProjectRoot, Handle, _thumbnailGeneration)))
                _thumbnailRequests.Add(entry.FullPath);
        }
    }
    private static Label Header(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 28,
        Font = EditorChrome.SmallFont, ForeColor = EditorChrome.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private static Button ActionButton(string text, Action action)
    {
        Button button = new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(68, 32),
            Padding = new Padding(9, 3, 9, 3), Margin = new Padding(0, 0, 6, 4), FlatStyle = FlatStyle.Flat };
        EditorChrome.StyleField(button); button.Click += (_, _) => action(); return button;
    }
    private static NumericUpDown MakeNumeric(decimal min, decimal max, int decimals)
    {
        NumericUpDown numeric = new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : 0.25m,
            Width = 70, Margin = new Padding(0, 3, 3, 3) }; EditorChrome.StyleField(numeric); return numeric;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _thumbnailTimer.Stop(); _thumbnailTimer.Dispose(); _modelThumbnails.Dispose(); _toolTip.Dispose(); foreach (Bitmap thumbnail in _thumbnails.Values) thumbnail.Dispose(); _thumbnails.Clear(); }
        base.Dispose(disposing);
    }
}

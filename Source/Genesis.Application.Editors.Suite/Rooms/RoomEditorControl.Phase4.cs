using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Phase 4 room-workflow surface: dense-scene outliner, multi-selection, grouped edits,
/// clipboard operations, layer safety/order, and exact game-camera preview.
/// </summary>
public sealed partial class RoomEditorControl
{
    private const string RoomClipboardPrefix = "GENESIS_ROOM_NODES_V1\n";
    private static string? s_roomClipboardJson;

    private readonly List<RoomNode> _selection = [];
    private readonly ListView _outliner = new();
    private readonly Dictionary<string, RoomTransform> _dragStartTransforms = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingOutliner;

    /// <summary>True once the designer has clicked or typed in the outliner since it was filled.</summary>
    private bool _outlinerUserInput;
    private bool _syncingLayers;
    private bool _handlingLayerCheck;
    private bool _handlingOutlinerCheck;
    private bool _phase4RefreshQueued;
    private int _pasteGeneration;
    private bool _tileStrokeActive;
    private bool _tileStrokeErase;
    private RoomNode? _tileStrokeNode;
    private List<RoomTileCell>? _tileStrokeBefore;
    private Vector2 _tileStrokeLastWorld;

    private readonly record struct CameraBookmark(
        float Camera2DX,
        float Camera2DY,
        float Zoom2D,
        Vector3 Target,
        float Yaw,
        float Pitch,
        float Distance,
        float FieldOfView,
        float Near,
        float Far);

    private CameraBookmark? _cameraBookmark;

    public IReadOnlyList<RoomNode> SelectedNodes => _selection;

    public ListView SceneOutliner => _outliner;

    public bool IsGameCameraPreview => GameCameraPreviewState is not null;

    public RoomCameraState? GameCameraPreviewState { get; private set; }

    private Panel BuildPhase4OutlinerPanel(Button addLayer)
    {
        _outliner.BackColor = EditorChrome.Surface;
        _outliner.Name = "RoomSceneOutliner";
        _outliner.BorderStyle = BorderStyle.None;
        _outliner.CheckBoxes = true;
        _outliner.Dock = DockStyle.Fill;
        _outliner.Font = EditorChrome.BaseFont;
        _outliner.ForeColor = EditorChrome.Text;
        _outliner.FullRowSelect = true;
        _outliner.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _outliner.HideSelection = false;
        _outliner.LabelEdit = true;
        _outliner.MultiSelect = true;
        _outliner.ShowGroups = true;
        _outliner.ShowItemToolTips = true;
        _outliner.View = View.Details;
        _outliner.Columns.Add("Instance", 126);
        _outliner.Columns.Add("Kind", 68);
        _outliner.Columns.Add("Depth", 54, HorizontalAlignment.Right);
        _outliner.ItemChecked += OnOutlinerItemChecked;
        _outliner.SelectedIndexChanged += OnOutlinerSelectionChanged;
        _outliner.AfterLabelEdit += OnOutlinerAfterLabelEdit;

        // A checkbox in this list can only be operated by a click or the space bar, so an
        // ItemChecked that arrives with no prior input on the list did not come from the designer.
        // See OnOutlinerItemChecked for why that distinction matters.
        _outliner.MouseDown += (_, _) => _outlinerUserInput = true;
        _outliner.KeyDown += (_, _) => _outlinerUserInput = true;

        ContextMenuStrip menu = new();
        menu.Items.Add("Copy", null, (_, _) => CopySelected());
        menu.Items.Add("Paste", null, (_, _) => PasteCopied());
        menu.Items.Add("Duplicate", null, (_, _) => DuplicateSelectionNodes());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => DeleteSelectionNodes());
        _outliner.ContextMenuStrip = menu;

        _layerList.Dock = DockStyle.Fill;
        _layerList.Height = 70;
        _layerList.SelectedIndexChanged += (_, _) => RefreshOutliner();

        addLayer.Dock = DockStyle.None;
        addLayer.Text = "Add Layer";
        addLayer.Width = 82;

        Button removeLayer = OutlinerButton("Remove Layer", 92, (_, _) => RemoveSelectedLayer());
        Button lockLayer = OutlinerButton("Lock Layer", 78, (_, _) => ToggleSelectedLayerLock());
        Button frontLayer = OutlinerButton("Bring Front", 84, (_, _) => MoveSelectedLayer(-100));
        Button backLayer = OutlinerButton("Send Back", 78, (_, _) => MoveSelectedLayer(100));
        // Wrapping, not clipping. These five buttons need ~306px of run width; the panel they live
        // in was 300, so with WrapContents = false the last labels were simply cut in half ("Fron",
        // "Bac"). Wrapping degrades to a second row instead of silently truncating, which also keeps
        // the panel usable if a future theme's font metrics run wider than today's.
        FlowLayoutPanel buttons = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = new Padding(4, 2, 0, 0),
            WrapContents = true,
        };
        buttons.Controls.Add(addLayer);
        buttons.Controls.Add(removeLayer);
        buttons.Controls.Add(lockLayer);
        buttons.Controls.Add(frontLayer);
        buttons.Controls.Add(backLayer);

        TableLayoutPanel layout = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));
        // 38: wrapped button labels ("Remove Layer", "Bring Front") need a second row on narrow panels.
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.Controls.Add(EditorChrome.SectionLabel("Layers — check to show; lock to protect"), 0, 0);
        layout.Controls.Add(_layerList, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(_outliner, 0, 3);

        Panel host = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Bottom,
            Height = 304,
        };
        host.Controls.Add(layout);
        return host;
    }

    private static Button OutlinerButton(string text, int width, EventHandler click)
    {
        Button button = new() { Height = 25, Text = text, Width = width };
        EditorChrome.StyleField(button);
        button.Click += click;
        return button;
    }

    private string LayerDisplayText(RoomLayer layer) =>
        $"{(layer.Locked ? "[LOCK] " : string.Empty)}{layer.Order,5}  {layer.Name}";

    private void RefreshOutliner()
    {
        if (_outliner.IsDisposed)
        {
            return;
        }

        HashSet<string> selectedIds = new(_selection.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        _syncingOutliner = true;
        try
        {
            _outliner.BeginUpdate();
            _outliner.Items.Clear();
            _outliner.Groups.Clear();

            IEnumerable<RoomLayer> layers = _room.Layers.OrderBy(l => l.Order).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
            foreach (RoomLayer layer in layers)
            {
                string state = layer.Enabled ? "visible" : "hidden";
                if (layer.Locked) state += ", locked";
                ListViewGroup group = new($"{layer.Order,5}  {layer.Name} ({state})") { Tag = layer };
                _outliner.Groups.Add(group);

                foreach (RoomNode node in _room.Nodes
                    .Where(n => string.Equals(n.LayerId, layer.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(NodeDepth)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                {
                    ListViewItem item = new(node.Name, group)
                    {
                        Checked = node.Enabled,
                        ForeColor = layer.Locked ? EditorChrome.Muted : EditorChrome.Text,
                        Tag = node,
                        ToolTipText = $"{node.Kind} · depth {EffectiveNodeDepth(node)}" + (layer.Locked ? " · layer locked" : string.Empty),
                    };
                    item.SubItems.Add(node.Kind.ToString());
                    item.SubItems.Add(EffectiveNodeDepth(node).ToString());
                    item.Selected = selectedIds.Contains(node.Id);
                    _outliner.Items.Add(item);
                }
            }

            _outliner.EndUpdate();
        }
        finally
        {
            // The items just added are the editor's, so nothing they raise — now or when the list
            // gets its handle — is a designer edit until they touch the list again.
            _outlinerUserInput = false;
            _syncingOutliner = false;
        }

        _navigation?.ObjectsPanel?.RefreshRoomInstances();
    }

    private void SyncOutlinerSelection()
    {
        if (_outliner.IsDisposed)
        {
            return;
        }

        HashSet<string> selectedIds = new(_selection.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        _syncingOutliner = true;
        try
        {
            foreach (ListViewItem item in _outliner.Items)
            {
                item.Selected = item.Tag is RoomNode node && selectedIds.Contains(node.Id);
            }
        }
        finally
        {
            _syncingOutliner = false;
        }

        _navigation?.ObjectsPanel?.SyncSelection();
    }

    private void OnOutlinerSelectionChanged(object? sender, EventArgs e)
    {
        if (_syncingOutliner)
        {
            return;
        }

        List<RoomNode> nodes = _outliner.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<RoomNode>()
            .ToList();
        SetSelection(nodes, syncOutliner: false);
    }

    /// <summary>
    /// Applies a visibility tick — but only one the designer actually made.
    /// </summary>
    /// <remarks>
    /// <see cref="ListView"/> raises <c>ItemChecked</c> for state it applies itself, including the
    /// checked flags of items added before its handle existed, which it defers until the handle is
    /// created — after the populating loop has returned and any "I am syncing" flag has been
    /// dropped. Those phantom events were read as the designer toggling instances: opening a room
    /// pushed "Show 'Sky'"-style entries onto its undo stack and marked the room modified, so
    /// closing asked whether to save a file nobody had touched.
    ///
    /// The guard is a fact rather than a timing window: a checkbox in this list can only be
    /// operated with the mouse or the space bar, so an event with no prior input on the list is not
    /// the designer's. <see cref="RefreshOutliner"/> clears the flag because the items it is about
    /// to raise events for are its own, not theirs.
    /// </remarks>
    private void OnOutlinerItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (!_outlinerUserInput)
        {
            return;
        }

        if (!_syncingOutliner && e.Item.Tag is RoomNode node)
        {
            _handlingOutlinerCheck = true;
            try
            {
                SetNodeEnabled(node, e.Item.Checked);
                RefreshPhase4Ui();
            }
            finally
            {
                _handlingOutlinerCheck = false;
            }
        }
    }

    private void OnOutlinerAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        if (e.CancelEdit || string.IsNullOrWhiteSpace(e.Label) || e.Item < 0 || e.Item >= _outliner.Items.Count)
        {
            return;
        }

        if (_outliner.Items[e.Item].Tag is RoomNode node)
        {
            SetNodeName(node, e.Label.Trim());
        }
    }

    private void SetSelection(IEnumerable<RoomNode> nodes, bool syncOutliner = true)
    {
        // Node selection and individual tile selection are mutually exclusive. SelectTileCell
        // intentionally calls this first and then establishes its cell selection.
        ClearTileCellSelection();
        List<RoomNode> next = nodes
            .Where(node => _room.Nodes.Contains(node) && CanInspectNodeInActiveContext(node))
            .DistinctBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _selection.Clear();
        _selection.AddRange(next);
        _selected = _selection.LastOrDefault();
        if (syncOutliner)
        {
            SyncOutlinerSelection();
        }

        SyncInspector();
        UpdateStatus(null);
        _viewport?.Invalidate();
        NotifyRoomInspectorStateChanged();
    }

    /// <summary>Selects, adds, or toggles a node without losing the rest of the selection.</summary>
    public void Select(RoomNode node, bool additive, bool toggle = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        List<RoomNode> next = additive || toggle ? [.. _selection] : [];
        int index = next.FindIndex(n => string.Equals(n.Id, node.Id, StringComparison.OrdinalIgnoreCase));
        if (toggle && index >= 0)
        {
            next.RemoveAt(index);
        }
        else if (index < 0)
        {
            next.Add(node);
        }

        SetSelection(next);
    }

    private IReadOnlyList<RoomNode> EditableSelection() =>
        _selection.Where(CanEditNodeInActiveContext).ToList();

    public RoomLayer? LayerFor(RoomNode node) =>
        _room.Layers.FirstOrDefault(layer => string.Equals(layer.Id, node.LayerId, StringComparison.OrdinalIgnoreCase));

    public bool IsNodeLocked(RoomNode node) => node.Locked || LayerFor(node)?.Locked == true;

    /// <summary>Kind-specific authored depth, before the containing layer's additive order.</summary>
    public int NodeDepth(RoomNode node) => node.Kind switch
    {
        RoomNodeKind.Background => node.Background?.Depth ?? node.Order,
        RoomNodeKind.TileLayer => node.TileLayer?.Depth ?? node.Order,
        _ => node.Order,
    };

    public int EffectiveNodeDepth(RoomNode node) => (LayerFor(node)?.Order ?? 0) + NodeDepth(node);

    public bool SetNodeName(RoomNode node, string name)
    {
        if (!CanEditNodeInActiveContext(node)) return false;
        string before = node.Name;
        string after = string.IsNullOrWhiteSpace(name) ? node.Kind.ToString() : name.Trim();
        if (string.Equals(before, after, StringComparison.Ordinal)) return false;
        node.Name = after;
        PushEdit(
            $"Rename '{before}'",
            () => { node.Name = after; RefreshPhase4Ui(); },
            () => { node.Name = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetNodeEnabled(RoomNode node, bool enabled)
    {
        if (!CanEditNodeInActiveContext(node) || node.Enabled == enabled) return false;
        bool before = node.Enabled;
        node.Enabled = enabled;
        PushEdit(
            $"{(enabled ? "Show" : "Hide")} '{node.Name}'",
            () => { node.Enabled = enabled; RefreshPhase4Ui(); },
            () => { node.Enabled = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetNodeDepth(RoomNode node, int depth)
    {
        if (!CanEditNodeInActiveContext(node)) return false;
        int before = NodeDepth(node);
        int after = Math.Clamp(depth, -100_000, 100_000);
        if (before == after) return false;
        WriteNodeDepth(node, after);
        PushEdit(
            $"Set '{node.Name}' depth",
            () => { WriteNodeDepth(node, after); RefreshPhase4Ui(); },
            () => { WriteNodeDepth(node, before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    private static void WriteNodeDepth(RoomNode node, int value)
    {
        switch (node.Kind)
        {
            case RoomNodeKind.Background when node.Background is not null: node.Background.Depth = value; break;
            case RoomNodeKind.TileLayer when node.TileLayer is not null: node.TileLayer.Depth = value; break;
            default: node.Order = value; break;
        }
    }

    public bool SetSelectionLayer(RoomLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        List<(RoomNode Node, string Before)> changes = EditableSelection()
            .Where(node => !string.Equals(node.LayerId, layer.Id, StringComparison.OrdinalIgnoreCase))
            .Select(node => (node, node.LayerId))
            .ToList();
        if (changes.Count == 0 || layer.Locked) return false;
        foreach ((RoomNode node, _) in changes) node.LayerId = layer.Id;
        PushEdit(
            $"Move {changes.Count} instance(s) to '{layer.Name}'",
            () => { foreach ((RoomNode node, _) in changes) node.LayerId = layer.Id; RefreshPhase4Ui(); },
            () => { foreach ((RoomNode node, string before) in changes) node.LayerId = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetSelectionEnabled(bool enabled)
    {
        List<(RoomNode Node, bool Before)> changes = EditableSelection()
            .Where(node => node.Enabled != enabled)
            .Select(node => (node, node.Enabled))
            .ToList();
        if (changes.Count == 0) return false;
        foreach ((RoomNode node, _) in changes) node.Enabled = enabled;
        PushEdit(
            $"{(enabled ? "Show" : "Hide")} {changes.Count} instance(s)",
            () => { foreach ((RoomNode node, _) in changes) node.Enabled = enabled; RefreshPhase4Ui(); },
            () => { foreach ((RoomNode node, bool before) in changes) node.Enabled = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public RoomLayer AddRoomLayer(string? name = null)
    {
        int order = _room.Layers.Count == 0 ? 0 : _room.Layers.Max(layer => layer.Order) + 100;
        RoomLayer layer = new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Layer {_room.Layers.Count + 1}" : name.Trim(),
            Order = order,
        };
        _room.Layers.Add(layer);
        PushEdit(
            $"Add layer '{layer.Name}'",
            () => { if (!_room.Layers.Contains(layer)) _room.Layers.Add(layer); RefreshPhase4Ui(); },
            () => { _room.Layers.Remove(layer); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return layer;
    }

    public bool SetLayerVisibility(RoomLayer layer, bool enabled)
    {
        if (layer.Enabled == enabled) return false;
        bool before = layer.Enabled;
        layer.Enabled = enabled;
        PushEdit(
            $"Layer '{layer.Name}' {(enabled ? "shown" : "hidden")}",
            () => { layer.Enabled = enabled; RefreshPhase4Ui(); },
            () => { layer.Enabled = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetLayerLocked(RoomLayer layer, bool locked)
    {
        if (layer.Locked == locked) return false;
        bool before = layer.Locked;
        layer.Locked = locked;
        PushEdit(
            $"Layer '{layer.Name}' {(locked ? "locked" : "unlocked")}",
            () => { layer.Locked = locked; RefreshPhase4Ui(); },
            () => { layer.Locked = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetLayerOrder(RoomLayer layer, int order)
    {
        int after = Math.Clamp(order, -100_000, 100_000);
        int before = layer.Order;
        if (before == after) return false;
        layer.Order = after;
        PushEdit(
            $"Set layer '{layer.Name}' depth",
            () => { layer.Order = after; RefreshPhase4Ui(); },
            () => { layer.Order = before; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    private RoomLayer? SelectedLayer =>
        _layerList.SelectedIndex >= 0 && _layerList.SelectedIndex < _room.Layers.Count
            ? _room.Layers[_layerList.SelectedIndex]
            : null;

    private string PlacementLayerId()
    {
        RoomLayer? selected = SelectedLayer;
        if (selected is { Locked: false }) return selected.Id;
        return (_room.Layers.FirstOrDefault(layer => !layer.Locked) ?? _room.Layers[0]).Id;
    }

    private void ToggleSelectedLayerLock()
    {
        RoomLayer? layer = SelectedLayer;
        if (layer is null)
        {
            UpdateStatus("Choose a layer first.");
            return;
        }

        SetLayerLocked(layer, !layer.Locked);
    }

    private void MoveSelectedLayer(int delta)
    {
        RoomLayer? layer = SelectedLayer;
        if (layer is null)
        {
            UpdateStatus("Choose a layer first.");
            return;
        }

        SetLayerOrder(layer, layer.Order + delta);
    }

    private void RemoveSelectedLayer()
    {
        RoomLayer? layer = SelectedLayer;
        if (layer is null || _room.Layers.Count <= 1)
        {
            UpdateStatus("A room must keep at least one layer.");
            return;
        }

        int index = _room.Layers.IndexOf(layer);
        RoomLayer fallback = _room.Layers.First(candidate => !ReferenceEquals(candidate, layer));
        List<RoomNode> moved = _room.Nodes.Where(node => node.LayerId == layer.Id).ToList();
        foreach (RoomNode node in moved) node.LayerId = fallback.Id;
        _room.Layers.Remove(layer);
        PushEdit(
            $"Remove layer '{layer.Name}'",
            () =>
            {
                _room.Layers.Remove(layer);
                foreach (RoomNode node in moved) node.LayerId = fallback.Id;
                RefreshPhase4Ui();
            },
            () =>
            {
                _room.Layers.Insert(Math.Clamp(index, 0, _room.Layers.Count), layer);
                foreach (RoomNode node in moved) node.LayerId = layer.Id;
                RefreshPhase4Ui();
            });
        RefreshPhase4Ui();
    }

    public bool CopySelected()
    {
        List<RoomNode> editable = EditableSelection().ToList();
        if (editable.Count == 0 || editable.Any(node => node.Kind != RoomNodeKind.GameObject)) return false;
        string json = JsonConvert.SerializeObject(editable, Formatting.None);
        s_roomClipboardJson = json;
        try
        {
            Clipboard.SetText(RoomClipboardPrefix + json);
        }
        catch (ExternalException)
        {
            // The in-process clipboard remains available if another process owns Windows clipboard.
        }
        catch (ThreadStateException)
        {
            // Headless callers can be non-STA; keep the in-process clipboard in that case.
        }

        UpdateStatus($"Copied {_selection.Count} instance(s).");
        return true;
    }

    /// <summary>True when there are room instances available to paste.</summary>
    /// <remarks>
    /// Asked by the Edit menu to decide whether Paste is enabled, so it looks in both places
    /// <see cref="PasteCopied"/> does: the in-process buffer first, then Windows' clipboard for a
    /// copy made in another Room Editor window.
    /// </remarks>
    public static bool HasRoomClipboardContent()
    {
        if (s_roomClipboardJson is not null) return true;
        try
        {
            return Clipboard.GetText().StartsWith(RoomClipboardPrefix, StringComparison.Ordinal);
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (ThreadStateException)
        {
            return false;
        }
    }

    public IReadOnlyList<RoomNode> PasteCopied()
    {
        string? json = s_roomClipboardJson;
        if (json is null)
        {
            try
            {
                string text = Clipboard.GetText();
                if (text.StartsWith(RoomClipboardPrefix, StringComparison.Ordinal))
                {
                    json = text[RoomClipboardPrefix.Length..];
                }
            }
            catch (ExternalException)
            {
            }
            catch (ThreadStateException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(json)) return [];
        List<RoomNode>? templates;
        try
        {
            templates = JsonConvert.DeserializeObject<List<RoomNode>>(json);
        }
        catch (JsonException)
        {
            UpdateStatus("Clipboard does not contain valid Genesis room instances.");
            return [];
        }

        if (templates is not { Count: > 0 }) return [];
        return InsertClones(templates, "Paste", ++_pasteGeneration);
    }

    private IReadOnlyList<RoomNode> DuplicateSelectionNodes()
    {
        List<RoomNode> templates = EditableSelection().Where(node => node.Kind == RoomNodeKind.GameObject).Select(CloneNode).ToList();
        if (templates.Count == 0) return [];
        return InsertClones(templates, "Duplicate", 1);
    }

    public IReadOnlyList<RoomNode> DuplicateSelection() => DuplicateSelectionNodes();

    public int DeleteSelection() => DeleteSelectionNodes();

    private IReadOnlyList<RoomNode> InsertClones(IReadOnlyList<RoomNode> templates, string action, int offsetMultiplier)
    {
        if (!CanPlaceInActiveContext(RoomNodeKind.GameObject) || templates.Any(node => node.Kind != RoomNodeKind.GameObject)) return [];
        Dictionary<string, string> idMap = templates.ToDictionary(node => node.Id, _ => RoomAsset.NewId(), StringComparer.OrdinalIgnoreCase);
        float grid = (_viewport.Mode2D ? MathF.Max(1f, _room.Settings.GridSize) : GridWorldSize3D()) * Math.Max(1, offsetMultiplier);
        RoomLayer fallback = _room.Layers.FirstOrDefault(layer => !layer.Locked) ?? _room.Layers[0];
        List<RoomNode> copies = [];
        foreach (RoomNode template in templates)
        {
            RoomNode copy = CloneNode(template);
            copy.Id = idMap[template.Id];
            copy.Name = template.Name + " copy";
            if (idMap.TryGetValue(copy.ParentId, out string? remappedParent)) copy.ParentId = remappedParent;
            else if (!string.IsNullOrEmpty(copy.ParentId) && _room.Nodes.All(node => node.Id != copy.ParentId)) copy.ParentId = string.Empty;
            copy.LayerId = _navigation.ObjectsPanel.ActiveObjectLayer?.Id ?? fallback.Id;
            copies.Add(copy);
        }

        _room.Nodes.AddRange(copies);
        HashSet<string> copiedIds = copies.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RoomNode copy in copies.Where(node => !RoomHierarchyTransforms.HasAncestor(_room, node, copiedIds)))
        {
            RoomTransform world = GetNodeWorldTransform(copy);
            world.X += grid;
            if (_viewport.Mode2D) world.Y += grid; else world.Z += grid;
            SetNodeWorldTransform(copy, world);
        }
        SetSelection(copies);
        PushEdit(
            $"{action} {copies.Count} instance(s)",
            () =>
            {
                foreach (RoomNode copy in copies) if (!_room.Nodes.Contains(copy)) _room.Nodes.Add(copy);
                SetSelection(copies);
                RefreshPhase4Ui();
            },
            () =>
            {
                foreach (RoomNode copy in copies) _room.Nodes.Remove(copy);
                SetSelection([]);
                RefreshPhase4Ui();
            });
        RefreshPhase4Ui();
        return copies;
    }

    private static RoomNode CloneNode(RoomNode node) =>
        JObject.FromObject(node).ToObject<RoomNode>() ?? throw new InvalidDataException("Could not clone room node.");

    private bool PaintTileAtWorldCore(float worldX, float worldY, bool erase, bool journal)
    {
        if (_activeTileLayer?.TileLayer is not { } layer
            || _activeTileSet is null
            || !CanEditNodeInActiveContext(_activeTileLayer) || ActiveTool != RoomTool.Paint)
        {
            return false;
        }

        if (!Matrix4x4.Invert(GetNodeWorldMatrix(_activeTileLayer), out Matrix4x4 inverse)) return false;
        Vector3 local = Vector3.Transform(new Vector3(worldX, worldY, 0), inverse);
        int cellX = (int)MathF.Floor(local.X / Math.Max(1, layer.CellWidth));
        int cellY = (int)MathF.Floor(local.Y / Math.Max(1, layer.CellHeight));
        RoomTileCell? existing = layer.Cells.FirstOrDefault(cell => cell.X == cellX && cell.Y == cellY);
        if (erase)
        {
            if (existing is null) return false;
            layer.Cells.Remove(existing);
            if (journal)
            {
                PushEdit(
                    "Erase tile",
                    () => { layer.Cells.RemoveAll(cell => cell.X == cellX && cell.Y == cellY); RefreshPhase4Ui(); },
                    () => { layer.Cells.Add(CloneTile(existing)); RefreshPhase4Ui(); });
            }
            return true;
        }

        int columns = TileSetColumns();
        int tileX = columns > 0 ? _activeTileIndex % columns : 0;
        int tileY = columns > 0 ? _activeTileIndex / columns : 0;
        if (existing is not null && existing.TileX == tileX && existing.TileY == tileY) return false;
        RoomTileCell? replaced = existing is null ? null : CloneTile(existing);
        if (existing is not null) layer.Cells.Remove(existing);
        RoomTileCell painted = new() { X = cellX, Y = cellY, TileX = tileX, TileY = tileY };
        layer.Cells.Add(painted);
        if (journal)
        {
            PushEdit(
                "Paint tile",
                () =>
                {
                    layer.Cells.RemoveAll(cell => cell.X == cellX && cell.Y == cellY);
                    layer.Cells.Add(CloneTile(painted));
                    RefreshPhase4Ui();
                },
                () =>
                {
                    layer.Cells.RemoveAll(cell => cell.X == cellX && cell.Y == cellY);
                    if (replaced is not null) layer.Cells.Add(CloneTile(replaced));
                    RefreshPhase4Ui();
                });
        }
        return true;
    }

    private void BeginTileStroke(Vector2 world, bool erase)
    {
        if (!IsTilePainting || _activeTileLayer?.TileLayer is not { } layer) return;
        _tileStrokeActive = true;
        _tileStrokeErase = erase;
        _tileStrokeNode = _activeTileLayer;
        _tileStrokeBefore = CloneTiles(layer.Cells);
        _tileStrokeLastWorld = world;
        PaintTileAtWorldCore(world.X, world.Y, erase, journal: false);
        _viewport.Invalidate();
    }

    private void ContinueTileStroke(Vector2 world)
    {
        if (!_tileStrokeActive || _tileStrokeNode?.TileLayer is not { } layer) return;
        float dx = world.X - _tileStrokeLastWorld.X;
        float dy = world.Y - _tileStrokeLastWorld.Y;
        int steps = Math.Max(
            1,
            (int)MathF.Ceiling(MathF.Max(
                MathF.Abs(dx) / Math.Max(1, layer.CellWidth),
                MathF.Abs(dy) / Math.Max(1, layer.CellHeight))));
        for (int step = 1; step <= steps; step++)
        {
            float amount = step / (float)steps;
            Vector2 sample = Vector2.Lerp(_tileStrokeLastWorld, world, amount);
            PaintTileAtWorldCore(sample.X, sample.Y, _tileStrokeErase, journal: false);
        }
        _tileStrokeLastWorld = world;
        _viewport.Invalidate();
    }

    private void CommitTileStroke()
    {
        if (!_tileStrokeActive || _tileStrokeNode?.TileLayer is not { } layer || _tileStrokeBefore is null)
        {
            ClearTileStroke();
            return;
        }

        List<RoomTileCell> before = CloneTiles(_tileStrokeBefore);
        List<RoomTileCell> after = CloneTiles(layer.Cells);
        RoomNode node = _tileStrokeNode;
        bool erase = _tileStrokeErase;
        ClearTileStroke();
        if (TilesEqual(before, after)) return;

        void Write(IReadOnlyList<RoomTileCell> cells)
        {
            if (node.TileLayer is null) return;
            node.TileLayer.Cells.Clear();
            node.TileLayer.Cells.AddRange(CloneTiles(cells));
            RefreshPhase4Ui();
        }

        PushEdit(
            erase ? "Erase tile stroke" : "Paint tile stroke",
            () => Write(after),
            () => Write(before));
        RefreshPhase4Ui();
    }

    private void ClearTileStroke()
    {
        _tileStrokeActive = false;
        _tileStrokeNode = null;
        _tileStrokeBefore = null;
    }

    private static RoomTileCell CloneTile(RoomTileCell cell) => new()
    {
        X = cell.X,
        Y = cell.Y,
        TileX = cell.TileX,
        TileY = cell.TileY,
        FlipX = cell.FlipX,
        FlipY = cell.FlipY,
        OffsetX = cell.OffsetX,
        OffsetY = cell.OffsetY,
        ScaleX = cell.ScaleX,
        ScaleY = cell.ScaleY,
        Rotation = cell.Rotation,
    };

    private static List<RoomTileCell> CloneTiles(IEnumerable<RoomTileCell> cells) =>
        cells.Select(CloneTile).ToList();

    private static bool TilesEqual(IReadOnlyList<RoomTileCell> left, IReadOnlyList<RoomTileCell> right)
    {
        if (left.Count != right.Count) return false;
        // Cell order is render order once independently moved tiles can overlap, so it is part of
        // the authored state as well as each tile's transform.
        return left.Zip(right)
            .All(pair => pair.First.X == pair.Second.X
                && pair.First.Y == pair.Second.Y
                && pair.First.TileX == pair.Second.TileX
                && pair.First.TileY == pair.Second.TileY
                && pair.First.FlipX == pair.Second.FlipX
                && pair.First.FlipY == pair.Second.FlipY
                && pair.First.OffsetX.Equals(pair.Second.OffsetX)
                && pair.First.OffsetY.Equals(pair.Second.OffsetY)
                && pair.First.ScaleX.Equals(pair.Second.ScaleX)
                && pair.First.ScaleY.Equals(pair.Second.ScaleY)
                && pair.First.Rotation.Equals(pair.Second.Rotation));
    }

    private int DeleteSelectionNodes()
    {
        List<RoomNode> deleting = EditableSelection().ToList();
        if (deleting.Count == 0)
        {
            if (_selection.Count > 0) UpdateStatus("The selected layer is locked.");
            return 0;
        }

        HashSet<string> ids = new(deleting.Select(node => node.Id), StringComparer.OrdinalIgnoreCase);
        List<(RoomNode Node, int Index)> locations = deleting
            .Select(node => (node, _room.Nodes.IndexOf(node)))
            .OrderBy(pair => pair.Item2)
            .ToList();
        List<(RoomNode Node, string Parent, RoomTransform Local, RoomTransform World)> detachedChildren = _room.Nodes
            .Where(node => !ids.Contains(node.Id) && ids.Contains(node.ParentId))
            .Select(node => (node, node.ParentId, CloneTransform(node.Transform), GetNodeWorldTransform(node)))
            .ToList();
        string cameraBefore = _room.ActiveGameCameraId;
        RoomNode? tileBefore = _activeTileLayer;
        List<RoomNode> selectionBefore = [.. _selection];

        void ApplyDelete()
        {
            foreach (var entry in detachedChildren)
            { entry.Node.ParentId = string.Empty; CopyTransform(entry.World, entry.Node.Transform); }
            foreach (RoomNode node in deleting) _room.Nodes.Remove(node);
            if (ids.Contains(_room.ActiveGameCameraId)) _room.ActiveGameCameraId = string.Empty;
            if (_activeTileLayer is not null && ids.Contains(_activeTileLayer.Id)) _activeTileLayer = null;
            SetSelection([]);
            RefreshSceneViews();
            RefreshPhase4Ui();
        }

        void RevertDelete()
        {
            foreach ((RoomNode node, int index) in locations)
            {
                if (!_room.Nodes.Contains(node)) _room.Nodes.Insert(Math.Clamp(index, 0, _room.Nodes.Count), node);
            }
            foreach (var entry in detachedChildren)
            { entry.Node.ParentId = entry.Parent; CopyTransform(entry.Local, entry.Node.Transform); }
            _room.ActiveGameCameraId = cameraBefore;
            _activeTileLayer = tileBefore;
            SetSelection(selectionBefore);
            RefreshSceneViews();
            RefreshPhase4Ui();
        }

        ApplyDelete();
        PushEdit($"Delete {deleting.Count} instance(s)", ApplyDelete, RevertDelete);
        return deleting.Count;
    }

    private void CaptureDragSelectionTransforms()
    {
        _dragStartTransforms.Clear();
        _dragStartWorldTransforms.Clear();
        foreach (RoomNode node in EditableSelection())
        {
            _dragStartTransforms[node.Id] = CloneTransform(node.Transform);
            _dragStartWorldTransforms[node.Id] = GetNodeWorldTransform(node);
        }
    }

    private Dictionary<string, RoomTransform> SnapshotSelectionTransforms() => EditableSelection()
        .ToDictionary(node => node.Id, node => CloneTransform(node.Transform), StringComparer.OrdinalIgnoreCase);

    private static RoomTransform CloneTransform(RoomTransform transform) => new()
    {
        X = transform.X, Y = transform.Y, Z = transform.Z,
        RotationX = transform.RotationX, RotationY = transform.RotationY, RotationZ = transform.RotationZ,
        ScaleX = transform.ScaleX, ScaleY = transform.ScaleY, ScaleZ = transform.ScaleZ,
    };

    private void ApplyTransformSnapshot(IReadOnlyDictionary<string, RoomTransform> snapshot)
    {
        foreach (RoomNode node in _room.Nodes)
        {
            if (snapshot.TryGetValue(node.Id, out RoomTransform? transform)) CopyTransform(transform, node.Transform);
        }
        RefreshCameraPreviewIfNeeded();
        SyncInspector();
        _viewport.Invalidate();
    }

    private void ApplyGroupTransformFromPrimary(RoomTransform primaryBefore, RoomTransform primaryAfter)
    {
        ApplyHierarchyGroupDelta(primaryBefore, primaryAfter, _dragStartWorldTransforms);
    }
    private static float SafeRatio(float value, float original) =>
        MathF.Abs(original) < .0001f ? 1f : value / original;

    private void PushTransformSnapshotEdit(
        string label,
        IReadOnlyDictionary<string, RoomTransform> before,
        IReadOnlyDictionary<string, RoomTransform> after)
    {
        PushEdit(
            label,
            () => ApplyTransformSnapshot(after),
            () => ApplyTransformSnapshot(before));
    }

    private static bool TransformSnapshotsEqual(
        IReadOnlyDictionary<string, RoomTransform> before,
        IReadOnlyDictionary<string, RoomTransform> after)
    {
        if (before.Count != after.Count) return false;
        foreach ((string id, RoomTransform value) in before)
        {
            if (!after.TryGetValue(id, out RoomTransform? other)) return false;
            if (!value.Position.AsSpan().SequenceEqual(other.Position)
                || !value.Rotation.AsSpan().SequenceEqual(other.Rotation)
                || !value.Scale.AsSpan().SequenceEqual(other.Scale)) return false;
        }

        return true;
    }

    private void ApplyInspectorSelectionTransform(Action<RoomTransform> apply)
    {
        if (_syncingInspector || _selected is null || !CanEditNodeInActiveContext(_selected)) return;
        Dictionary<string, RoomTransform> before = SnapshotSelectionTransforms();
        if (!before.TryGetValue(_selected.Id, out RoomTransform? primaryBefore)) return;
        Dictionary<string, RoomTransform> worldBefore = EditableSelection().ToDictionary(node => node.Id, GetNodeWorldTransform);
        apply(_selected.Transform);
        RoomTransform primaryWorldAfter = GetNodeWorldTransform(_selected);
        CopyTransform(primaryBefore, _selected.Transform);
        ApplyHierarchyGroupDelta(worldBefore[_selected.Id], primaryWorldAfter, worldBefore);
        Dictionary<string, RoomTransform> after = SnapshotSelectionTransforms();
        if (TransformSnapshotsEqual(before, after)) return;
        PushTransformSnapshotEdit($"Edit {_selection.Count} transform(s)", before, after);
        RefreshCameraPreviewIfNeeded();
        RefreshPhase4Ui();
    }

    public bool MoveSelectionBy(Vector3 delta)
    {
        IReadOnlyList<RoomNode> editable = SelectionTransformRoots();
        if (editable.Count == 0) return false;
        Dictionary<string, RoomTransform> before = SnapshotSelectionTransforms();
        foreach (RoomNode node in editable)
        {
            RoomTransform world = GetNodeWorldTransform(node);
            world.X += delta.X; world.Y += delta.Y; world.Z += delta.Z;
            SetNodeWorldTransform(node, world);
        }
        Dictionary<string, RoomTransform> after = SnapshotSelectionTransforms();
        if (TransformSnapshotsEqual(before, after)) return false;
        PushTransformSnapshotEdit($"Move {editable.Count} instance(s)", before, after);
        RefreshPhase4Ui();
        return true;
    }

    /// <summary>Drops each selected 3D object onto the highest visible surface below it.</summary>
    public bool SnapSelectionToFloor()
    {
        return SnapSelectionToSurfaceOrFloor();
    }

    private void ApplySceneCameraView(RoomNode? node)
    {
        if (node is null)
        {
            GameCameraPreviewState = null;
            _viewport.CameraOverrideFactory = null;
            if (_cameraBookmark is CameraBookmark bookmark)
            {
                _viewport.Camera2DX = bookmark.Camera2DX;
                _viewport.Camera2DY = bookmark.Camera2DY;
                _viewport.Zoom2D = bookmark.Zoom2D;
                _viewport.Camera.Target = bookmark.Target;
                _viewport.Camera.Yaw = bookmark.Yaw;
                _viewport.Camera.Pitch = bookmark.Pitch;
                _viewport.Camera.Distance = bookmark.Distance;
                _viewport.FieldOfViewDegrees = bookmark.FieldOfView;
                _viewport.NearPlane = bookmark.Near;
                _viewport.FarPlane = bookmark.Far;
            }
            _cameraBookmark = null;
            _viewport.NavigationEnabled = true;
            UpdateStatus("Scene view: the editor camera.");
            _viewport.Invalidate();
            return;
        }

        _cameraBookmark ??= new CameraBookmark(
            _viewport.Camera2DX,
            _viewport.Camera2DY,
            _viewport.Zoom2D,
            _viewport.Camera.Target,
            _viewport.Camera.Yaw,
            _viewport.Camera.Pitch,
            _viewport.Camera.Distance,
            _viewport.FieldOfViewDegrees,
            _viewport.NearPlane,
            _viewport.FarPlane);

        RoomCameraState? state = new RoomSceneBuilder(ProjectRoot).ResolveCameraState(_room, node);
        if (state is null)
        {
            UpdateStatus($"'{node.Name}' is not a game object camera.");
            return;
        }

        GameCameraPreviewState = state;
        if (_room.Dimension == RoomDimension.TwoD)
        {
            _viewport.CameraOverrideFactory = null;
            _viewport.Camera2DX = state.Position.X;
            _viewport.Camera2DY = state.Position.Y;
            _viewport.Zoom2D = state.Zoom2D;
        }
        else
        {
            if (!state.HasCameraComponent)
            {
                GameCameraPreviewState = null;
                _viewport.CameraOverrideFactory = null;
                UpdateStatus($"'{node.Name}' has no Camera3D component to preview.");
                return;
            }

            _viewport.FieldOfViewDegrees = state.FieldOfViewDegrees;
            _viewport.NearPlane = state.NearPlane;
            _viewport.FarPlane = state.FarPlane;
            _viewport.CameraOverrideFactory = () => BuildCameraOverride(state);
        }

        _viewport.NavigationEnabled = false;
        UpdateStatus($"Game Camera preview: '{node.Name}'. Choose Scene to return to editing.");
        _viewport.Invalidate();
    }

    private EditorCameraOverride BuildCameraOverride(RoomCameraState state) =>
        BuildCameraOverride(state, _viewport);

    private EditorCameraOverride BuildCameraOverride(RoomCameraState state, EditorViewport3D surface)
    {
        float aspect = surface.SurfaceWidth / (float)Math.Max(1, surface.SurfaceHeight);
        Vector3 forward = MathUtil.DirectionFromYawPitch(state.YawRadians, state.PitchRadians);
        Matrix4x4 view = MathUtil.LookAtLH(state.Position, state.Position + forward, Vector3.UnitY);
        Matrix4x4 projection = MathUtil.PerspectiveFovLH(
            state.FieldOfViewDegrees * MathF.PI / 180f,
            aspect,
            state.NearPlane,
            state.FarPlane);
        return new EditorCameraOverride(view, projection, state.Position, forward);
    }

    /// <summary>
    /// Editor orbit stays on the main viewport; the designated or Scene-selected camera appears
    /// in the shared top-right inset. Full-viewport <see cref="PreviewGameCamera"/> is unchanged.
    /// </summary>
    public void ShowGameCameraInInset()
    {
        RoomNode? camera = SceneViewNode ?? ActiveGameCamera;
        if (camera is null)
        {
            UpdateStatus("Set a game camera, or pick one in Scene, to preview it in the inset.");
            return;
        }

        RoomCameraState? state = new RoomSceneBuilder(ProjectRoot).ResolveCameraState(_room, camera);
        if (state is null)
        {
            UpdateStatus($"'{camera.Name}' is not a game object camera.");
            return;
        }

        if (_room.Dimension == RoomDimension.TwoD)
        {
            _viewport.PinSecondaryFromCurrentView($"Game camera · {camera.Name}");
            if (_viewport.SecondaryViewport is EditorViewport3D feed)
            {
                feed.Mode2D = true;
                feed.Camera2DX = state.Position.X;
                feed.Camera2DY = state.Position.Y;
                feed.Zoom2D = state.Zoom2D;
            }

            UpdateStatus($"Inset: '{camera.Name}' (editor view stays free).");
            return;
        }

        if (!state.HasCameraComponent)
        {
            UpdateStatus($"'{camera.Name}' has no Camera3D component to preview.");
            return;
        }

        _viewport.ShowAuthoredSecondaryCamera(
            () => BuildCameraOverride(state, _viewport.SecondaryViewport ?? _viewport),
            $"Game camera · {camera.Name}");
        UpdateStatus($"Inset: '{camera.Name}' (editor view stays free).");
    }

    /// <summary>
    /// CAM-2: click a viewport frustum marker to show that slot's POV in the second-camera inset.
    /// </summary>
    public void ShowViewportInInset(int index)
    {
        if (_room.Viewports is null || index < 0 || index >= _room.Viewports.Count)
        {
            UpdateStatus("No viewport slot to preview.");
            return;
        }

        RoomViewport viewport = _room.Viewports[index];
        if (!viewport.Enabled)
        {
            UpdateStatus($"Viewport {index + 1} is disabled — enable it to preview its frustum.");
            return;
        }

        if (_room.Dimension == RoomDimension.TwoD)
        {
            _viewport.PinSecondaryFromCurrentView($"Viewport {index + 1}");
            if (_viewport.SecondaryViewport is EditorViewport3D feed)
            {
                feed.Mode2D = true;
                feed.Camera2DX = viewport.SourceX + viewport.SourceWidth * 0.5f;
                feed.Camera2DY = viewport.SourceY + viewport.SourceHeight * 0.5f;
                float zoom = feed.SurfaceWidth / MathF.Max(1f, viewport.SourceWidth);
                feed.Zoom2D = Math.Clamp(zoom, 0.05f, 40f);
            }

            UpdateStatus($"Inset: viewport {index + 1} (editor view stays free).");
            return;
        }

        _viewport.ShowAuthoredSecondaryCamera(
            () =>
            {
                ResolveViewportFrustumPose(
                    index, _room.Viewports[index],
                    out Vector3 eye, out Vector3 forward,
                    out float fov, out float near, out float far, out _);
                EditorViewport3D surface = _viewport.SecondaryViewport ?? _viewport;
                float aspect = surface.SurfaceWidth / (float)Math.Max(1, surface.SurfaceHeight);
                Matrix4x4 view = MathUtil.LookAtLH(eye, eye + forward, Vector3.UnitY);
                Matrix4x4 projection = MathUtil.PerspectiveFovLH(
                    fov * MathF.PI / 180f, aspect, near, far);
                return new EditorCameraOverride(view, projection, eye, forward);
            },
            $"Viewport {index + 1}");
        UpdateStatus($"Inset: viewport {index + 1} (editor view stays free).");
    }

    private void DrawBackgrounds3D(IRenderController renderer)
    {
        Vector3 cameraPosition = GameCameraPreviewState?.Position ?? _viewport.Camera.Eye;
        Vector3 cameraForward = GameCameraPreviewState is RoomCameraState state
            ? MathUtil.DirectionFromYawPitch(state.YawRadians, state.PitchRadians)
            : _viewport.Camera.Forward;

        foreach (RoomNode node in _room.Nodes
            .Where(candidate => candidate.Kind == RoomNodeKind.Background
                && candidate.Enabled
                && candidate.Supports(RoomDimension.ThreeD)
                && candidate.Background is not null
                && LayerFor(candidate)?.Enabled != false)
            .OrderByDescending(EffectiveNodeDepth))
        {
            RoomTransform nodeWorld = GetNodeWorldTransform(node); RoomBackgroundData background = node.Background!;
            if (background.Mode == RoomBackgroundMode.TwoD) continue;
            string? image = ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, background.Asset);
            if (image is null || !_textures.TryGet(renderer, image, out TextureHandle texture, out _, out _)) continue;

            Vector3 position = new(nodeWorld.X, nodeWorld.Y, nodeWorld.Z);
            Matrix4x4 world;
            MeshDrawFlags flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoDepthWrite;
            if (!background.DepthTest && background.Mode != RoomBackgroundMode.Sky) flags |= MeshDrawFlags.NoDepthTest;
            if (background.Mode == RoomBackgroundMode.Sky)
            {
                position = cameraPosition + cameraForward * 160f;
                world = Matrix4x4.CreateScale(420f, 240f, 1f)
                    * Matrix4x4.CreateBillboard(position, cameraPosition, Vector3.UnitY, cameraForward);
            }
            else if (background.Mode == RoomBackgroundMode.Billboard)
            {
                world = Matrix4x4.CreateScale(nodeWorld.ScaleX, nodeWorld.ScaleY, 1f)
                    * Matrix4x4.CreateBillboard(position, cameraPosition, Vector3.UnitY, cameraForward);
            }
            else
            {
                world = Matrix4x4.CreateScale(nodeWorld.ScaleX, nodeWorld.ScaleY, 1f)
                    * Matrix4x4.CreateFromYawPitchRoll(
                        nodeWorld.RotationY * MathF.PI / 180f,
                        nodeWorld.RotationX * MathF.PI / 180f,
                        nodeWorld.RotationZ * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(position);
            }

            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _unitQuad,
                Texture = texture,
                World = world,
                Tint = TintToRenderColor(background.TintArgb),
                Alpha = background.Opacity,
                Flags = flags,
            });
        }
    }

    public bool PreviewGameCamera()
    {
        RoomNode? camera = ActiveGameCamera;
        if (camera is null) return false;
        for (int i = 0; i < _sceneViewBox.Items.Count; i++)
        {
            if (_sceneViewBox.Items[i] is SceneViewChoice choice && choice.NodeId == camera.Id)
            {
                _sceneViewBox.SelectedIndex = i;
                if (_sceneViewBox.SelectedIndex == i) ApplySceneCameraView(camera);
                return IsGameCameraPreview;
            }
        }

        ApplySceneCameraView(camera);
        return IsGameCameraPreview;
    }

    public void ExitGameCameraPreview()
    {
        if (_sceneViewBox.Items.Count > 0) _sceneViewBox.SelectedIndex = 0;
        ApplySceneCameraView(null);
    }

    private void RefreshCameraPreviewIfNeeded()
    {
        if (GameCameraPreviewState is null) return;
        RoomNode? node = SceneViewNode;
        if (node is null)
        {
            ExitGameCameraPreview();
            return;
        }

        ApplySceneCameraView(node);
    }

    private void RefreshPhase4Ui() => QueueRoomUiRefresh(checkStructure: true);

    protected override void OnJournalChanged()
    {
        base.OnJournalChanged();
        RefreshPhase4Ui();
    }
}

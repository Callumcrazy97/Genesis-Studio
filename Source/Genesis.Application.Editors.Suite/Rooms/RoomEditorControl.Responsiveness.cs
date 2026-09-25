using System.Drawing;
using System.Numerics;
using System.Text;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// The document and viewport update at input rate. Only inspector values are coalesced, and
/// navigation controls rebuild only when their topology changes, never for a scalar transform.
/// Editing context is a capability check, not a rendering filter: inactive layers remain visible.
/// </summary>
public sealed partial class RoomEditorControl
{
    private System.Windows.Forms.Timer? _roomUiTimer;
    private bool _roomUiStructureCheck;
    private bool _roomUiFlushing;
    private string _roomUiTopology = string.Empty;
    private string _roomEditContext = string.Empty;
    private readonly Dictionary<string, RoomImageMetadata> _roomImageMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TileSetInfo?> _roomTilesetMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JObject> _roomPrefabInspectorSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, object>> _roomPgslInspectorFields = new(StringComparer.OrdinalIgnoreCase);
    private sealed record RoomImageMetadata(string? ImagePath, int Width, int Height);

    public int RoomInspectorValueRefreshCount { get; private set; }
    public int RoomStructureRefreshCount { get; private set; }
    public int RoomMetadataReadCount { get; private set; }

    private void QueueRoomUiRefresh(bool checkStructure = false)
    {
        if (IsDisposed || Disposing || _roomUiFlushing) return;
        _phase4RefreshQueued = true;
        _roomUiStructureCheck |= checkStructure;
        if (!IsHandleCreated) return;
        if (_roomUiTimer is null)
        {
            _roomUiTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _roomUiTimer.Tick += (_, _) => FlushPendingRoomUiRefresh();
        }
        if (!_roomUiTimer.Enabled) _roomUiTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_phase4RefreshQueued) QueueRoomUiRefresh(_roomUiStructureCheck);
    }

    /// <summary>Flushes the same pending UI work as the timer; useful after a batch edit and in tests.</summary>
    public void FlushPendingRoomUiRefresh()
    {
        _roomUiTimer?.Stop();
        if (!_phase4RefreshQueued || _roomUiFlushing || IsDisposed || Disposing) return;
        if (_handlingLayerCheck || _handlingOutlinerCheck) { _roomUiTimer?.Start(); return; }
        bool checkStructure = _roomUiStructureCheck;
        _phase4RefreshQueued = false;
        _roomUiStructureCheck = false;
        _roomUiFlushing = true;
        try
        {
            bool structural = false;
            if (checkStructure)
            {
                string topology = RoomUiTopology();
                structural = !string.Equals(_roomUiTopology, topology, StringComparison.Ordinal);
                _roomUiTopology = topology;
            }
            if (structural)
            {
                RoomStructureRefreshCount++;
                RefreshLayers();
                RefreshOutliner();
                RefreshWorkspaceTerrains();
                ActiveRoomEditContextChanged();
                if (_selection.Any(node => !CanInspectNodeInActiveContext(node)))
                    SetSelection(_selection.Where(CanInspectNodeInActiveContext).ToArray());
                SyncInspector();
            }
            else
            {
                RoomInspectorValueRefreshCount++;
                _inspector?.InspectNode(_selected);
                _navigation.ObjectsPanel.SyncSelection();
            }
            RefreshCameraPreviewIfNeeded();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            _viewport?.Invalidate();
        }
        finally { _roomUiFlushing = false; }
    }

    private string RoomUiTopology()
    {
        // No positions, cells, shader values or generated JSON. Those are value refreshes.
        StringBuilder key = new();
        key.Append(_room.Dimension).Append('|').Append(_room.Nodes.Count).Append('|');
        foreach (RoomLayer layer in _room.Layers)
            key.Append(layer.Id).Append(':').Append(layer.Name).Append(':').Append(layer.Order)
                .Append(':').Append(layer.Enabled).Append(':').Append(layer.Locked).Append(';');
        foreach (RoomNode node in _room.Nodes)
            key.Append(node.Id).Append(':').Append(node.Name).Append(':').Append(node.Kind)
                .Append(':').Append(node.LayerId).Append(':').Append(node.ParentId).Append(':').Append(node.Order)
                .Append(':').Append(node.Enabled).Append(':').Append(node.Locked)
                .Append(':').Append(node.GameObject?.Prefab).Append(':').Append(node.TileLayer?.Tileset)
                .Append(':').Append(node.Background?.Asset).Append(';');
        return key.ToString();
    }

    /// <summary>Only the active resource tab AND its selected layer may own the Inspector.</summary>
    public bool CanInspectNodeInActiveContext(RoomNode node)
    {
        if (_navigation is null) return false;
        return node.Kind switch
        {
            RoomNodeKind.GameObject => _navigation.CurrentSection == RoomNavSection.Objects
                && string.Equals(node.LayerId, _navigation.ObjectsPanel.ActiveObjectLayer?.Id,
                    StringComparison.OrdinalIgnoreCase),
            RoomNodeKind.TileLayer => !ViewMode3D && _navigation.CurrentSection == RoomNavSection.Tilesets
                && ReferenceEquals(node, _navigation.TilesetsPanel.ActiveTileLayer),
            RoomNodeKind.Background => _navigation.CurrentSection == RoomNavSection.Backgrounds
                && ReferenceEquals(node, _navigation.BackgroundsPanel.ActiveBackgroundLayer),
            RoomNodeKind.Terrain => ViewMode3D && _navigation.CurrentSection == RoomNavSection.Tilesets
                && ReferenceEquals(node, _workspaceTerrains?.SelectedItem),
            _ => false,
        };
    }

    public bool CanEditNodeInActiveContext(RoomNode node) =>
        CanInspectNodeInActiveContext(node) && !IsNodeLocked(node);

    private bool CanPlaceInActiveContext(RoomNodeKind kind) => kind switch
    {
        RoomNodeKind.GameObject => _navigation.CurrentSection == RoomNavSection.Objects
            && _navigation.ObjectsPanel.ActiveObjectLayer is { Locked: false, Enabled: true },
        RoomNodeKind.Terrain => _navigation.CurrentSection == RoomNavSection.Tilesets && ViewMode3D,
        _ => false,
    };

    /// <summary>Called by tabs and explicit layer pickers, not by viewport hit tests.</summary>
    public void ActiveRoomEditContextChanged()
    {
        if (_navigation is null) return;
        string next = _navigation.CurrentSection + ":" + (_navigation.CurrentSection switch
        {
            RoomNavSection.Objects => _navigation.ObjectsPanel.ActiveObjectLayer?.Id,
            RoomNavSection.Tilesets => ViewMode3D ? (_workspaceTerrains?.SelectedItem as RoomNode)?.Id
                : _navigation.TilesetsPanel.ActiveTileLayer?.Id,
            RoomNavSection.Backgrounds => _navigation.BackgroundsPanel.ActiveBackgroundLayer?.Id,
            _ => string.Empty,
        });
        if (next == _roomEditContext) return;
        _roomEditContext = next;
        CancelActiveRoomGesture();
        if (_pendingPlacementPath is not null) CancelPlacement();
        _activeTileLayer = _navigation.CurrentSection == RoomNavSection.Tilesets
            ? _navigation.TilesetsPanel.ActiveTileLayer : null;
        if (_navigation.CurrentSection == RoomNavSection.Objects)
            _placement.TargetLayerId = _navigation.ObjectsPanel.ActiveObjectLayer?.Id;
        _hover = null;
        if (_selected is not null && !CanInspectNodeInActiveContext(_selected)) SetSelection([]);
        _transformToolActive = false;
        ActiveTool = RoomTool.Select;
        SyncToolbar();
        QueueRoomUiRefresh();
        _viewport?.Invalidate();
    }

    private void CancelActiveRoomGesture()
    {
        // Restore before clearing selection. The new tab can already be active when this runs.
        CancelTileTransformDrag();
        CancelTransformDrag();
        if (_tileStrokeActive && _tileStrokeNode?.TileLayer is { } layer && _tileStrokeBefore is not null)
        {
            layer.Cells.Clear();
            layer.Cells.AddRange(CloneTiles(_tileStrokeBefore));
        }
        ClearTileStroke();
        if (_viewport is not null) _viewport.NavigationEnabled = !IsGameCameraPreview;
    }

    private IEnumerable<RoomNode> EnumerateEditableHitNodes() => _room.Nodes.Where(node =>
        (node.Kind is RoomNodeKind.GameObject or RoomNodeKind.Background)
        && CanEditNodeInActiveContext(node) && node.Enabled && node.Supports(_room.Dimension)
        && LayerFor(node)?.Enabled != false);

    private bool HitTestEditableNode2D(RoomNode node, Vector2 world) => HitTest2D(node, world);

    private RoomImageMetadata GetRoomImageMetadata(string reference)
    {
        if (_roomImageMetadata.TryGetValue(reference, out RoomImageMetadata? metadata)) return metadata;
        RoomMetadataReadCount++;
        string? path = ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, reference);
        int width = 32, height = 32;
        if (path is not null && File.Exists(path)) ReadPngSize(path, out width, out height);
        metadata = new RoomImageMetadata(path, Math.Max(1, width), Math.Max(1, height));
        _roomImageMetadata[reference] = metadata;
        return metadata;
    }

    private TileSetInfo? GetRoomTilesetMetadata(string reference)
    {
        if (_roomTilesetMetadata.TryGetValue(reference, out TileSetInfo? info)) return info;
        RoomMetadataReadCount++;
        string? path = ResolveTileSetPath(reference);
        info = path is not null ? TileSetInfo.Load(path) : null;
        _roomTilesetMetadata[reference] = info;
        return info;
    }

    internal bool ConfigureTileLayerTileset(RoomNode node, string reference, TileSetInfo? info)
    {
        if (node.TileLayer is not { } layer || !CanEditNodeInActiveContext(node)) return false;
        var before = (layer.Tileset, layer.CellWidth, layer.CellHeight, layer.Margin, layer.Separation);
        var after = (reference, info?.TileWidth ?? layer.CellWidth, info?.TileHeight ?? layer.CellHeight,
            info?.Margin ?? layer.Margin, info?.Separation ?? layer.Separation);
        if (before.Equals(after)) return false;
        void Apply((string Reference, int Width, int Height, int Margin, int Separation) value)
        {
            layer.Tileset = value.Reference;
            layer.CellWidth = value.Width; layer.CellHeight = value.Height;
            layer.Margin = value.Margin; layer.Separation = value.Separation;
            RefreshPhase4Ui();
        }
        Apply(after);
        PushEdit("Change tile layer tileset", () => Apply(after), () => Apply(before));
        return true;
    }

    internal void InvalidateBackgroundViewport()
    {
        // Background controls are direct authoring controls, so their visual feedback must not
        // wait for a coalesced Inspector/tree refresh. Invalidate both the render host and its
        // owning viewport; game-camera inset state is refreshed through the same authored room.
        if (IsGameCameraPreview) RefreshCameraPreviewIfNeeded();
        _viewport?.Host?.Invalidate();
        _viewport?.Invalidate();
    }

    private void InvalidateRoomMetadata()
    {
        _roomImageMetadata.Clear();
        _roomTilesetMetadata.Clear();
        _roomPrefabInspectorSources.Clear();
        _roomPgslInspectorFields.Clear();
        _contextVisualKeys.Clear();
        _navigation?.BackgroundsPanel.InvalidatePreview();
    }

    private Vector2 NodeEditingCenter2D(RoomNode node, RoomTransform transform, float width, float height)
    {
        if (node.Kind != RoomNodeKind.Background) return new Vector2(transform.X, transform.Y);
        if (node.Background?.Layout == RoomBackgroundLayout.StretchView)
            return new Vector2(_viewport.Camera2DX, _viewport.Camera2DY);
        return new Vector2(transform.X + width * .5f, transform.Y + height * .5f);
    }

    private bool IsRoomRectVisible(float x, float y, float width, float height, float degrees)
    {
        float angle = degrees * MathF.PI / 180f;
        float halfX = (MathF.Abs(MathF.Cos(angle)) * width + MathF.Abs(MathF.Sin(angle)) * height) * .5f;
        float halfY = (MathF.Abs(MathF.Sin(angle)) * width + MathF.Abs(MathF.Cos(angle)) * height) * .5f;
        float viewX = _viewport.SurfaceWidth / MathF.Max(.0001f, _viewport.Zoom2D) * .5f + 2f;
        float viewY = _viewport.SurfaceHeight / MathF.Max(.0001f, _viewport.Zoom2D) * .5f + 2f;
        return MathF.Abs(x - _viewport.Camera2DX) <= halfX + viewX
            && MathF.Abs(y - _viewport.Camera2DY) <= halfY + viewY;
    }
}

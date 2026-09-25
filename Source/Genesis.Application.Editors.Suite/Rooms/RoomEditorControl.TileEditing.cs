using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Classic per-tile editing for 2D rooms. Painting remains a fast grid workflow, but once the
/// Select tool is active an individual painted tile can be picked, inspected, moved, resized,
/// rotated, flipped and deleted without converting it to a game object.
/// </summary>
public sealed partial class RoomEditorControl
{
    private enum TileDragKind { None, Move, Scale, Rotate }

    private readonly record struct TileCellSnapshot(
        int X, int Y, int TileX, int TileY,
        bool FlipX, bool FlipY,
        float OffsetX, float OffsetY, float ScaleX, float ScaleY, float Rotation);

    private RoomNode? _selectedTileLayer;
    private RoomTileCell? _selectedTileCell;
    private TileDragKind _tileDragKind;
    private int _tileDragHandle = -1;
    private TileCellSnapshot _tileDragBefore;
    private Vector2 _tileDragStartLocal;
    private Matrix4x4 _tileDragInverse;

    public RoomTileCell? SelectedTileCell => TileSelectionValid ? _selectedTileCell : null;
    public RoomNode? SelectedTileLayer => TileSelectionValid ? _selectedTileLayer : null;

    private bool TileSelectionValid =>
        _selectedTileLayer?.TileLayer is { } layer
        && _selectedTileCell is not null
        && layer.Cells.Contains(_selectedTileCell)
        && _room.Nodes.Contains(_selectedTileLayer)
        && CanInspectNodeInActiveContext(_selectedTileLayer);

    private void ClearTileCellSelection(bool refresh = false)
    {
        if (_tileDragKind != TileDragKind.None && _selectedTileCell is not null)
            ApplySnapshot(_selectedTileCell, _tileDragBefore);
        _selectedTileLayer = null;
        _selectedTileCell = null;
        _tileDragKind = TileDragKind.None;
        _tileDragHandle = -1;
        if (!refresh) return;
        SyncInspector();
        NotifyRoomInspectorStateChanged();
        _viewport?.Invalidate();
    }

    private void SelectTileCell(RoomNode layer, RoomTileCell cell)
    {
        if (!CanInspectNodeInActiveContext(layer) || layer.TileLayer is null || !layer.TileLayer.Cells.Contains(cell)) return;
        if (!ReferenceEquals(_selected, layer) || _selection.Count != 1)
            SetSelection([layer]);
        _selectedTileLayer = layer;
        _selectedTileCell = cell;
        SyncInspector();
        UpdateStatus($"Tile {cell.TileX},{cell.TileY} · grid {cell.X},{cell.Y} — drag to move; R resize; E rotate; Delete removes");
        _viewport?.Invalidate();
        NotifyRoomInspectorStateChanged();
    }

    /// <summary>Test/automation hook that follows the same hit test used by a designer click.</summary>
    public bool SelectTileAtWorld(float worldX, float worldY)
    {
        if (ViewMode3D) return false;
        Point client = ClientFromWorld2D(new Vector2(worldX, worldY));
        if (!TryHitTileCell(client, out RoomNode? layer, out RoomTileCell? cell)) return false;
        SelectTileCell(layer!, cell!);
        return true;
    }

    private bool TryHitTileCell(Point client, out RoomNode? hitLayer, out RoomTileCell? hitCell)
    {
        hitLayer = null;
        hitCell = null;
        if (!_viewport.Mode2D || !ShowTileLayers) return false;

        Vector2 world = _viewport.ControlToWorld2D(client);
        RoomNode? activeLayer = _navigation.TilesetsPanel.ActiveTileLayer;
        if (activeLayer is null || !CanEditNodeInActiveContext(activeLayer)) return false;
        RoomNode[] candidates = [activeLayer];

        foreach (RoomNode node in candidates)
        {
            if (!node.Enabled || !node.Supports(RoomDimension.TwoD) || IsNodeLocked(node)) continue;
            if (LayerFor(node) is { Enabled: false }) continue;
            if (!Matrix4x4.Invert(GetNodeWorldMatrix(node), out Matrix4x4 inverse)) continue;
            Vector3 local = Vector3.Transform(new Vector3(world, 0), inverse);
            RoomTileLayerData tiles = node.TileLayer!;
            for (int index = tiles.Cells.Count - 1; index >= 0; index--)
            {
                RoomTileCell candidate = tiles.Cells[index];
                if (!TileCellContainsLocalPoint(tiles, candidate, local.X, local.Y)) continue;
                hitLayer = node;
                hitCell = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool TileCellContainsLocalPoint(RoomTileLayerData layer, RoomTileCell cell, float x, float y)
    {
        Vector2 center = TileCellLocalCenter(layer, cell);
        Vector2 delta = new(x - center.X, y - center.Y);
        float radians = -cell.Rotation * MathF.PI / 180f;
        Vector2 unrotated = RotateVector(delta, radians);
        float halfX = layer.CellWidth * MathF.Max(0.001f, MathF.Abs(cell.ScaleX)) * .5f;
        float halfY = layer.CellHeight * MathF.Max(0.001f, MathF.Abs(cell.ScaleY)) * .5f;
        return MathF.Abs(unrotated.X) <= halfX && MathF.Abs(unrotated.Y) <= halfY;
    }

    private static Vector2 TileCellLocalCenter(RoomTileLayerData layer, RoomTileCell cell) => new(
        (cell.X + .5f) * layer.CellWidth + cell.OffsetX,
        (cell.Y + .5f) * layer.CellHeight + cell.OffsetY);

    private Vector2 TileCellWorldCenter(RoomNode node, RoomTileCell cell)
    {
        RoomTileLayerData layer = node.TileLayer!;
        Vector2 center = TileCellLocalCenter(layer, cell);
        Vector3 world = Vector3.Transform(new Vector3(center, 0), GetNodeWorldMatrix(node));
        return new Vector2(world.X, world.Y);
    }

    private Vector2[] TileCellWorldCorners(RoomNode node, RoomTileCell cell)
    {
        RoomTileLayerData layer = node.TileLayer!;
        Vector2 center = TileCellLocalCenter(layer, cell);
        float halfX = layer.CellWidth * MathF.Max(0.001f, MathF.Abs(cell.ScaleX)) * .5f;
        float halfY = layer.CellHeight * MathF.Max(0.001f, MathF.Abs(cell.ScaleY)) * .5f;
        float radians = cell.Rotation * MathF.PI / 180f;
        Vector2[] corners =
        [
            new(-halfX, -halfY), new(halfX, -halfY), new(halfX, halfY), new(-halfX, halfY),
        ];
        Matrix4x4 matrix = GetNodeWorldMatrix(node);
        for (int index = 0; index < corners.Length; index++)
        {
            Vector2 local = center + RotateVector(corners[index], radians);
            Vector3 world = Vector3.Transform(new Vector3(local, 0), matrix);
            corners[index] = new Vector2(world.X, world.Y);
        }
        return corners;
    }

    private Vector2 TileRotateHandleWorld(RoomNode node, RoomTileCell cell)
    {
        Vector2[] corners = TileCellWorldCorners(node, cell);
        Vector2 topMid = (corners[0] + corners[1]) * .5f;
        Vector2 center = (corners[0] + corners[2]) * .5f;
        Vector2 up = topMid - center;
        return up.LengthSquared() < 0.0001f
            ? topMid + new Vector2(0, -26f / MathF.Max(.001f, _viewport.Zoom2D))
            : topMid + Vector2.Normalize(up) * (26f / MathF.Max(.001f, _viewport.Zoom2D));
    }

    private bool TryBeginTileGizmoDrag(Point client)
    {
        if (!TileSelectionValid || !TransformGizmoVisible || IsNodeLocked(_selectedTileLayer!)) return false;
        RoomNode layer = _selectedTileLayer!;
        RoomTileCell cell = _selectedTileCell!;
        PointF surface = _viewport.ControlToSurface(client);
        Vector2 pointer = new(surface.X, surface.Y);
        Vector2[] corners = TileCellWorldCorners(layer, cell);

        if (Gizmo == GizmoKind.Rotate)
        {
            Vector2 handle = _viewport.World2DToSurface(TileRotateHandleWorld(layer, cell));
            if (Vector2.Distance(pointer, handle) <= 12f)
            {
                BeginTileDrag(TileDragKind.Rotate, client, -1);
                return true;
            }
        }
        else if (Gizmo == GizmoKind.Scale)
        {
            Vector2[] handles = ScaleHandles2D(corners);
            for (int index = 0; index < handles.Length; index++)
            {
                Vector2 handle = _viewport.World2DToSurface(handles[index]);
                if (MathF.Abs(pointer.X - handle.X) <= 8f && MathF.Abs(pointer.Y - handle.Y) <= 8f)
                {
                    BeginTileDrag(TileDragKind.Scale, client, index);
                    return true;
                }
            }
        }
        return false;
    }

    private void BeginTileBodyDrag(Point client) => BeginTileDrag(TileDragKind.Move, client, -1);

    private void BeginTileDrag(TileDragKind kind, Point client, int handle)
    {
        if (!TileSelectionValid || IsNodeLocked(_selectedTileLayer!)) return;
        RoomNode layer = _selectedTileLayer!;
        if (!Matrix4x4.Invert(GetNodeWorldMatrix(layer), out Matrix4x4 inverse)) return;
        Vector2 world = _viewport.ControlToWorld2D(client);
        Vector3 local = Vector3.Transform(new Vector3(world, 0), inverse);
        _tileDragKind = kind;
        _tileDragHandle = handle;
        _tileDragBefore = Snapshot(_selectedTileCell!);
        _tileDragInverse = inverse;
        _tileDragStartLocal = new Vector2(local.X, local.Y);
        _viewport.NavigationEnabled = false;
    }

    private void UpdateTileDrag(Point client, Keys modifiers)
    {
        if (_tileDragKind == TileDragKind.None || !TileSelectionValid) return;
        RoomNode node = _selectedTileLayer!;
        RoomTileCell cell = _selectedTileCell!;
        RoomTileLayerData layer = node.TileLayer!;
        Vector2 world = _viewport.ControlToWorld2D(client);
        Vector3 local3 = Vector3.Transform(new Vector3(world, 0), _tileDragInverse);
        Vector2 local = new(local3.X, local3.Y);

        if (_tileDragKind == TileDragKind.Move)
        {
            Vector2 delta = local - _tileDragStartLocal;
            float left = _tileDragBefore.X * layer.CellWidth + _tileDragBefore.OffsetX + delta.X;
            float top = _tileDragBefore.Y * layer.CellHeight + _tileDragBefore.OffsetY + delta.Y;
            if (_room.Settings.SnapEnabled && (modifiers & Keys.Shift) == 0)
            {
                float snap = MathF.Max(1f, _room.Settings.GridSize);
                left = MathF.Round(left / snap) * snap;
                top = MathF.Round(top / snap) * snap;
            }
            SetTileTopLeft(layer, cell, left, top);
        }
        else if (_tileDragKind == TileDragKind.Scale)
        {
            Vector2 center = new(
                (_tileDragBefore.X + .5f) * layer.CellWidth + _tileDragBefore.OffsetX,
                (_tileDragBefore.Y + .5f) * layer.CellHeight + _tileDragBefore.OffsetY);
            float radians = -_tileDragBefore.Rotation * MathF.PI / 180f;
            Vector2 start = RotateVector(_tileDragStartLocal - center, radians);
            Vector2 current = RotateVector(local - center, radians);
            bool scaleX = _tileDragHandle is 0 or 1 or 2 or 3 or 5 or 7;
            bool scaleY = _tileDragHandle is 0 or 1 or 2 or 3 or 4 or 6;
            float sx = MathF.Max(.05f, _tileDragBefore.ScaleX);
            float sy = MathF.Max(.05f, _tileDragBefore.ScaleY);
            if (scaleX && MathF.Abs(start.X) > .001f)
                sx = Math.Clamp(_tileDragBefore.ScaleX * MathF.Abs(current.X / start.X), .05f, 64f);
            if (scaleY && MathF.Abs(start.Y) > .001f)
                sy = Math.Clamp(_tileDragBefore.ScaleY * MathF.Abs(current.Y / start.Y), .05f, 64f);
            if ((modifiers & Keys.Shift) != 0)
            {
                float uniform = MathF.Max(sx / MathF.Max(.05f, _tileDragBefore.ScaleX), sy / MathF.Max(.05f, _tileDragBefore.ScaleY));
                sx = Math.Clamp(_tileDragBefore.ScaleX * uniform, .05f, 64f);
                sy = Math.Clamp(_tileDragBefore.ScaleY * uniform, .05f, 64f);
            }
            cell.ScaleX = sx;
            cell.ScaleY = sy;
        }
        else if (_tileDragKind == TileDragKind.Rotate)
        {
            Vector2 center = new(
                (_tileDragBefore.X + .5f) * layer.CellWidth + _tileDragBefore.OffsetX,
                (_tileDragBefore.Y + .5f) * layer.CellHeight + _tileDragBefore.OffsetY);
            float start = MathF.Atan2(_tileDragStartLocal.Y - center.Y, _tileDragStartLocal.X - center.X);
            float current = MathF.Atan2(local.Y - center.Y, local.X - center.X);
            float degrees = _tileDragBefore.Rotation + (current - start) * 180f / MathF.PI;
            if (_room.Settings.SnapEnabled && (modifiers & Keys.Shift) == 0)
                degrees = MathF.Round(degrees / 5f) * 5f;
            cell.Rotation = degrees;
        }

        _viewport.Invalidate();
        QueueRoomUiRefresh();
    }

    private bool CommitTileDrag()
    {
        if (_tileDragKind == TileDragKind.None || !TileSelectionValid)
        {
            _tileDragKind = TileDragKind.None;
            return false;
        }
        RoomTileCell cell = _selectedTileCell!;
        TileCellSnapshot before = _tileDragBefore;
        TileCellSnapshot after = Snapshot(cell);
        TileDragKind kind = _tileDragKind;
        _tileDragKind = TileDragKind.None;
        _tileDragHandle = -1;
        if (before.Equals(after)) return false;
        string label = kind switch
        {
            TileDragKind.Scale => "Resize tile",
            TileDragKind.Rotate => "Rotate tile",
            _ => "Move tile",
        };
        PushEdit(label,
            () => { ApplySnapshot(cell, after); RefreshPhase4Ui(); },
            () => { ApplySnapshot(cell, before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    private bool CancelTileTransformDrag()
    {
        if (_tileDragKind == TileDragKind.None || _selectedTileCell is null) return false;
        ApplySnapshot(_selectedTileCell!, _tileDragBefore);
        _tileDragKind = TileDragKind.None;
        _tileDragHandle = -1;
        SyncInspector();
        _viewport.NavigationEnabled = !IsGameCameraPreview;
        _viewport.Invalidate();
        return true;
    }

    private bool DeleteSelectedTileCell()
    {
        if (!TileSelectionValid || IsNodeLocked(_selectedTileLayer!)) return false;
        RoomNode node = _selectedTileLayer!;
        RoomTileLayerData layer = node.TileLayer!;
        RoomTileCell cell = _selectedTileCell!;
        int index = layer.Cells.IndexOf(cell);
        if (index < 0) return false;
        layer.Cells.RemoveAt(index);
        ClearTileCellSelection();
        PushEdit("Delete tile",
            () => { layer.Cells.Remove(cell); ClearTileCellSelection(); RefreshPhase4Ui(); },
            () => { if (!layer.Cells.Contains(cell)) layer.Cells.Insert(Math.Clamp(index, 0, layer.Cells.Count), cell); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    private static TileCellSnapshot Snapshot(RoomTileCell cell) => new(
        cell.X, cell.Y, cell.TileX, cell.TileY, cell.FlipX, cell.FlipY,
        cell.OffsetX, cell.OffsetY, cell.ScaleX, cell.ScaleY, cell.Rotation);

    private static void ApplySnapshot(RoomTileCell cell, TileCellSnapshot value)
    {
        cell.X = value.X; cell.Y = value.Y; cell.TileX = value.TileX; cell.TileY = value.TileY;
        cell.FlipX = value.FlipX; cell.FlipY = value.FlipY;
        cell.OffsetX = value.OffsetX; cell.OffsetY = value.OffsetY;
        cell.ScaleX = value.ScaleX; cell.ScaleY = value.ScaleY; cell.Rotation = value.Rotation;
    }

    private static void SetTileTopLeft(RoomTileLayerData layer, RoomTileCell cell, float left, float top)
    {
        float width = Math.Max(1, layer.CellWidth), height = Math.Max(1, layer.CellHeight);
        int gridX = (int)MathF.Floor(left / width);
        int gridY = (int)MathF.Floor(top / height);
        cell.X = gridX; cell.Y = gridY;
        cell.OffsetX = left - gridX * width;
        cell.OffsetY = top - gridY * height;
        if (MathF.Abs(cell.OffsetX) < .0001f) cell.OffsetX = 0;
        if (MathF.Abs(cell.OffsetY) < .0001f) cell.OffsetY = 0;
    }

    private bool SetSelectedTileSnapshot(string label, Func<TileCellSnapshot, TileCellSnapshot> mutate)
    {
        if (!TileSelectionValid || IsNodeLocked(_selectedTileLayer!)) return false;
        RoomTileCell cell = _selectedTileCell!;
        TileCellSnapshot before = Snapshot(cell);
        TileCellSnapshot after = mutate(before);
        if (before.Equals(after)) return false;
        ApplySnapshot(cell, after);
        PushEdit(label,
            () => { ApplySnapshot(cell, after); RefreshPhase4Ui(); },
            () => { ApplySnapshot(cell, before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    private bool SetSelectedTileWidth(float width) => TileSelectionValid && SetSelectedTileSnapshot("Resize tile width", before =>
    {
        float scale = Math.Clamp(width / Math.Max(1f, _selectedTileLayer!.TileLayer!.CellWidth), .05f, 64f);
        return before with { ScaleX = scale };
    });

    private bool SetSelectedTileHeight(float height) => TileSelectionValid && SetSelectedTileSnapshot("Resize tile height", before =>
    {
        float scale = Math.Clamp(height / Math.Max(1f, _selectedTileLayer!.TileLayer!.CellHeight), .05f, 64f);
        return before with { ScaleY = scale };
    });

    private bool DrawSelectedTileOverlay(IRenderController renderer)
    {
        if (!TileSelectionValid || !_viewport.Mode2D) return false;
        RoomNode node = _selectedTileLayer!;
        RoomTileCell cell = _selectedTileCell!;
        Vector2[] corners = TileCellWorldCorners(node, cell);
        RenderColor accent = ToRenderColor(EditorChrome.Accent, 1f);
        for (int index = 0; index < 4; index++)
        {
            Vector2 a = _viewport.World2DToSurface(corners[index]);
            Vector2 b = _viewport.World2DToSurface(corners[(index + 1) % 4]);
            renderer.DrawLine(a.X, a.Y, b.X, b.Y, accent, 2f, depth: -8999);
        }
        if (!TransformGizmoVisible) return true;

        if (Gizmo == GizmoKind.Scale)
        {
            foreach (Vector2 handleWorld in ScaleHandles2D(corners))
            {
                Vector2 handle = _viewport.World2DToSurface(handleWorld);
                renderer.DrawRect(handle.X - 4f, handle.Y - 4f, 8f, 8f, RenderColor.White, filled: true, depth: -9000);
                renderer.DrawRect(handle.X - 4f, handle.Y - 4f, 8f, 8f, accent, filled: false, depth: -9001);
            }
        }
        else if (Gizmo == GizmoKind.Rotate)
        {
            Vector2 center = _viewport.World2DToSurface(TileCellWorldCenter(node, cell));
            Vector2 handle = _viewport.World2DToSurface(TileRotateHandleWorld(node, cell));
            renderer.DrawLine(center.X, center.Y, handle.X, handle.Y, accent, 1.5f, depth: -9000);
            DrawCircle(renderer, handle, 6f, RenderColor.White, accent);
        }
        else if (Gizmo == GizmoKind.Move)
        {
            EditorTransformGizmo.Draw2DAxes(_viewport, renderer, TileCellWorldCenter(node, cell), includeZ: false);
        }
        return true;
    }

    private object? TileInspectorObject() => TileSelectionValid
        ? new TileCellInspector(this, _selectedTileLayer!, _selectedTileCell!)
        : null;

    private sealed class TileCellInspector
    {
        private readonly RoomEditorControl _editor;
        private readonly RoomNode _node;
        private readonly RoomTileCell _cell;
        private RoomTileLayerData Layer => _node.TileLayer!;
        public TileCellInspector(RoomEditorControl editor, RoomNode node, RoomTileCell cell) { _editor = editor; _node = node; _cell = cell; }

        [Category("Tile"), ReadOnly(true)] public string LayerName => _node.Name;
        [Category("Tile"), DisplayName("Grid X")] public int GridX { get => _cell.X; set => _editor.SetSelectedTileSnapshot("Move tile X", b => b with { X = value }); }
        [Category("Tile"), DisplayName("Grid Y")] public int GridY { get => _cell.Y; set => _editor.SetSelectedTileSnapshot("Move tile Y", b => b with { Y = value }); }
        [Category("Tile"), DisplayName("Source tile X")] public int TileX { get => _cell.TileX; set => _editor.SetSelectedTileSnapshot("Change source tile X", b => b with { TileX = Math.Max(0, value) }); }
        [Category("Tile"), DisplayName("Source tile Y")] public int TileY { get => _cell.TileY; set => _editor.SetSelectedTileSnapshot("Change source tile Y", b => b with { TileY = Math.Max(0, value) }); }

        [Category("Transform"), DisplayName("Offset X (px)")] public float OffsetX { get => _cell.OffsetX; set => _editor.SetSelectedTileSnapshot("Move tile X", b => b with { OffsetX = value }); }
        [Category("Transform"), DisplayName("Offset Y (px)")] public float OffsetY { get => _cell.OffsetY; set => _editor.SetSelectedTileSnapshot("Move tile Y", b => b with { OffsetY = value }); }
        [Category("Transform"), DisplayName("Width (px)")] public float Width { get => Layer.CellWidth * MathF.Max(.001f, _cell.ScaleX); set => _editor.SetSelectedTileWidth(MathF.Max(1, value)); }
        [Category("Transform"), DisplayName("Height (px)")] public float Height { get => Layer.CellHeight * MathF.Max(.001f, _cell.ScaleY); set => _editor.SetSelectedTileHeight(MathF.Max(1, value)); }
        [Category("Transform"), DisplayName("Rotation (°)")] public float Rotation { get => _cell.Rotation; set => _editor.SetSelectedTileSnapshot("Rotate tile", b => b with { Rotation = value }); }
        [Category("Transform")] public bool FlipX { get => _cell.FlipX; set => _editor.SetSelectedTileSnapshot("Flip tile X", b => b with { FlipX = value }); }
        [Category("Transform")] public bool FlipY { get => _cell.FlipY; set => _editor.SetSelectedTileSnapshot("Flip tile Y", b => b with { FlipY = value }); }
        [Category("Collision"), ReadOnly(true)] public bool CollisionEnabled => Layer.CollisionEnabled;
    }

    private void AddSelectedTileLiveValues(List<ResourceInspectorLiveValue> values)
    {
        if (!TileSelectionValid) return;
        RoomNode node = _selectedTileLayer!;
        RoomTileLayerData layer = node.TileLayer!;
        RoomTileCell cell = _selectedTileCell!;
        string group = $"{node.Name} · Selected tile";
        values.Add(new(group, "Selection.TileCell.Layer", "Layer", node.Name, ReadOnly: true));
        values.Add(Number(group, "Selection.TileCell.GridX", "Grid X", cell.X, -1_000_000, 1_000_000, 1, 0));
        values.Add(Number(group, "Selection.TileCell.GridY", "Grid Y", cell.Y, -1_000_000, 1_000_000, 1, 0));
        values.Add(Number(group, "Selection.TileCell.TileX", "Source tile X", cell.TileX, 0, 1_000_000, 1, 0));
        values.Add(Number(group, "Selection.TileCell.TileY", "Source tile Y", cell.TileY, 0, 1_000_000, 1, 0));
        values.Add(Number(group, "Selection.TileCell.OffsetX", "Offset X", cell.OffsetX, -1_000_000, 1_000_000, .25m, 2));
        values.Add(Number(group, "Selection.TileCell.OffsetY", "Offset Y", cell.OffsetY, -1_000_000, 1_000_000, .25m, 2));
        values.Add(Number(group, "Selection.TileCell.Width", "Width", layer.CellWidth * cell.ScaleX, 1, 100_000, .25m, 2));
        values.Add(Number(group, "Selection.TileCell.Height", "Height", layer.CellHeight * cell.ScaleY, 1, 100_000, .25m, 2));
        values.Add(Number(group, "Selection.TileCell.Rotation", "Rotation", cell.Rotation, -360_000, 360_000, 1, 2));
        values.Add(new(group, "Selection.TileCell.FlipX", "Flip X", cell.FlipX));
        values.Add(new(group, "Selection.TileCell.FlipY", "Flip Y", cell.FlipY));
    }

    private bool TryApplySelectedTileLiveValue(string path, object? value)
    {
        if (!TileSelectionValid || !path.StartsWith("Selection.TileCell.", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.EndsWith(".GridX", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Move tile X", b => b with { X = Convert.ToInt32(value, CultureInfo.InvariantCulture) });
        if (path.EndsWith(".GridY", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Move tile Y", b => b with { Y = Convert.ToInt32(value, CultureInfo.InvariantCulture) });
        if (path.EndsWith(".TileX", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Change source tile X", b => b with { TileX = Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture)) });
        if (path.EndsWith(".TileY", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Change source tile Y", b => b with { TileY = Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture)) });
        if (path.EndsWith(".OffsetX", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Move tile X", b => b with { OffsetX = Convert.ToSingle(value, CultureInfo.InvariantCulture) });
        if (path.EndsWith(".OffsetY", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Move tile Y", b => b with { OffsetY = Convert.ToSingle(value, CultureInfo.InvariantCulture) });
        if (path.EndsWith(".Width", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileWidth(Convert.ToSingle(value, CultureInfo.InvariantCulture));
        if (path.EndsWith(".Height", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileHeight(Convert.ToSingle(value, CultureInfo.InvariantCulture));
        if (path.EndsWith(".Rotation", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Rotate tile", b => b with { Rotation = Convert.ToSingle(value, CultureInfo.InvariantCulture) });
        if (path.EndsWith(".FlipX", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Flip tile X", b => b with { FlipX = Convert.ToBoolean(value, CultureInfo.InvariantCulture) });
        if (path.EndsWith(".FlipY", StringComparison.OrdinalIgnoreCase)) return SetSelectedTileSnapshot("Flip tile Y", b => b with { FlipY = Convert.ToBoolean(value, CultureInfo.InvariantCulture) });
        return false;
    }

    private void AddSelectedTileContextValues(List<ResourceInspectorLiveValue> values)
    {
        if (!TileSelectionValid) return;
        RoomNode node = _selectedTileLayer!;
        RoomTileLayerData layer = node.TileLayer!;
        RoomTileCell cell = _selectedTileCell!;
        values.Add(new("Selected tile", "Selection.TileCell.Layer", "Layer", node.Name, ReadOnly: true));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.GridX", "Grid X", cell.X, 1, 0));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.GridY", "Grid Y", cell.Y, 1, 0));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.TileX", "Source tile X", cell.TileX, 1, 0));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.TileY", "Source tile Y", cell.TileY, 1, 0));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.OffsetX", "Offset X (px)", cell.OffsetX, .25m, 2));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.OffsetY", "Offset Y (px)", cell.OffsetY, .25m, 2));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.Width", "Width (px)", layer.CellWidth * cell.ScaleX, .25m, 2));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.Height", "Height (px)", layer.CellHeight * cell.ScaleY, .25m, 2));
        values.Add(SimpleNumber("Selected tile", "Selection.TileCell.Rotation", "Rotation (°)", cell.Rotation, 1m, 2));
        values.Add(new("Selected tile", "Selection.TileCell.FlipX", "Flip X", cell.FlipX));
        values.Add(new("Selected tile", "Selection.TileCell.FlipY", "Flip Y", cell.FlipY));
        values.Add(new("Selected tile", "Selection.TileCell.Collision", "Layer collision", layer.CollisionEnabled, ReadOnly: true));
    }
}

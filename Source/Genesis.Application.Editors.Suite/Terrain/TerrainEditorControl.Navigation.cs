using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.World.Terrain;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private string? _placementEntityPath;
    private List<TerrainPlacedEntity>? _objectDragBefore;
    private Point _objectDragMouse;
    private Vector3 _objectDragOrigin;
    private Vector3? _objectDragDirection;
    private Keys _objectDragModifiers;

    private ToolStripMenuItem BuildNavigationMenu()
    {
        var menu = new ToolStripMenuItem("Control method");
        foreach (var mode in new[] { EditorCameraControlMethod.Free, EditorCameraControlMethod.Orbit })
        {
            var item = new ToolStripMenuItem(mode == EditorCameraControlMethod.Free ? "Free camera" : "Orbit");
            item.Click += (_, _) => { _viewport.ControlMethod = mode; UpdateStatus(); };
            menu.DropDownOpening += (_, _) => item.Checked = _viewport.ControlMethod == mode;
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private bool MoveObjectWithWheel(Point client, int delta, Keys modifiers)
    {
        if (_viewport.Mode2D || (modifiers & (Keys.Control | Keys.Shift)) == 0 || SelectedPlacedEntity() is not { } placed) return false;
        if (_objectDragBefore is not null || _componentGizmoDragging) return true;
        List<TerrainPlacedEntity> before = ClonePlaced(_nature.PlacedEntities);
        float step = _viewportSession.SnapToGrid ? _viewportSession.GridCellSize
            : MathF.Max(.1f, Vector3.Distance(_viewport.Camera.Eye, placed.Position) * .025f);
        Vector3 direction = (modifiers & Keys.Control) != 0 ? -_viewport.Camera.Forward : Vector3.UnitY;
        placed.Position += direction * (delta / 120f) * step;
        ApplyPlaced(before, ClonePlaced(_nature.PlacedEntities), "Move terrain object");
        return true;
    }

    private bool BeginObjectBodyDrag(Point client)
    {
        if (ActiveMode is not (TerrainEditorMode.Select or TerrainEditorMode.Entities)) return false;
        if (_placementEntityPath is { } asset && PickTerrain(client, out Vector3 ground))
        {
            PlaceTerrainEntity(asset, ground.X, ground.Z);
            if ((ModifierKeys & Keys.Control) == 0) _placementEntityPath = null;
            RefreshActiveToolCard();
            return true;
        }
        var picked = PickScene(client);
        TerrainPlacedEntity? hit = picked.Kind == TerrainComponentsPanel.ComponentKind.Entity ? _nature.PlacedEntities.FirstOrDefault(item => item.Id == picked.Id) : null;
        if (hit is null) return false;
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Entity, hit.Id);
        _objectDragBefore = ClonePlaced(_nature.PlacedEntities);
        _objectDragMouse = client; _objectDragOrigin = hit.Position;
        _objectDragDirection = null; _objectDragModifiers = ModifierKeys;
        _viewport.NavigationEnabled = false;
        return true;
    }

    private Vector3 DragPlanePoint(Point point)
    {
        var ray = _viewport.PickRay(point);
        Vector3 normal = _viewport.Camera.Forward;
        float divisor = Vector3.Dot(ray.Direction, normal);
        return MathF.Abs(divisor) < .00001f ? _objectDragOrigin
            : ray.Origin + ray.Direction * (Vector3.Dot(_objectDragOrigin - ray.Origin, normal) / divisor);
    }

    private bool MoveObjectBody(Point client)
    {
        if (_objectDragBefore is null || SelectedPlacedEntity() is not { } placed) return false;
        Vector3 movement = DragPlanePoint(client) - DragPlanePoint(_objectDragMouse);
        bool constrained = (_objectDragModifiers & (Keys.Control | Keys.Shift)) != 0;
        if (constrained && _objectDragDirection is null)
        {
            Vector2 pixels = new(client.X - _objectDragMouse.X, client.Y - _objectDragMouse.Y);
            if (pixels.Length() < 6 || movement.LengthSquared() < .000001f) return true;
            _objectDragDirection = Vector3.Normalize(movement);
            if ((_objectDragModifiers & Keys.Control) != 0)
            {
                Vector3 screenOrigin = _viewport.WorldToSurface(_objectDragOrigin);
                _objectDragDirection = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }.OrderByDescending(axis =>
                {
                    Vector3 tip = _viewport.WorldToSurface(_objectDragOrigin + axis);
                    Vector2 projected = new(tip.X - screenOrigin.X, tip.Y - screenOrigin.Y);
                    return projected.LengthSquared() < .0001f ? -1 : MathF.Abs(Vector2.Dot(Vector2.Normalize(projected), Vector2.Normalize(pixels)));
                }).First();
            }
        }
        if (_objectDragDirection is { } direction)
        {
            Vector3 a = _viewport.WorldToSurface(_objectDragOrigin), b = _viewport.WorldToSurface(_objectDragOrigin + direction);
            Vector2 projected = new(b.X - a.X, b.Y - a.Y);
            PointF current = _viewport.ControlToSurface(client), start = _viewport.ControlToSurface(_objectDragMouse);
            float distance = Vector2.Dot(new(current.X - start.X, current.Y - start.Y), projected) / MathF.Max(.0001f, projected.LengthSquared());
            if (_viewportSession.SnapToGrid) distance = MathF.Round(distance / _viewportSession.GridCellSize) * _viewportSession.GridCellSize;
            movement = direction * distance;
        }
        placed.Position = _objectDragOrigin + movement;
        if (!constrained && _viewportSession.SnapToGrid) placed.Position = _viewportSession.Snap(placed.Position);
        _viewport.Invalidate();
        return true;
    }

    private bool EndObjectBodyDrag(bool cancel = false)
    {
        if (_objectDragBefore is not { } before) return false;
        _objectDragBefore = null; _viewport.NavigationEnabled = true;
        if (cancel) { _nature.PlacedEntities = ClonePlaced(before); RefreshComponentsPanel(); }
        else if (before.Count != _nature.PlacedEntities.Count || before.Where((item, index) => item.Position != _nature.PlacedEntities[index].Position).Any())
            ApplyPlaced(before, ClonePlaced(_nature.PlacedEntities), "Move terrain object");
        _viewport.Invalidate();
        return true;
    }

    private void DrawTerrainObjectMarkers(IRenderController renderer)
    {
        foreach (var placed in _nature.PlacedEntities)
        {
            Vector3 p = _viewport.WorldToSurface(placed.Position);
            if (p.Z is <= 0 or >= 1) continue;
            var color = new RenderColor(.45f, .75f, 1f, .95f);
            renderer.DrawLine(p.X - 7, p.Y, p.X, p.Y - 7, color, 2);
            renderer.DrawLine(p.X, p.Y - 7, p.X + 7, p.Y, color, 2);
            renderer.DrawLine(p.X + 7, p.Y, p.X, p.Y + 7, color, 2);
            renderer.DrawLine(p.X, p.Y + 7, p.X - 7, p.Y, color, 2);
            if (placed.Id == _selectedComponentId)
            {
                string name = TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity))?.Name ?? "Terrain object";
                renderer.DrawText(name, p.X + 12, p.Y - 8, 12, color);
            }
        }
        if (_placementEntityPath is not null && _cursorValid)
        {
            Vector3 p = _viewport.WorldToSurface(_cursorWorld);
            if (p.Z is > 0 and < 1)
                renderer.DrawText("Click to place · Esc cancels", p.X + 12, p.Y, 12, RenderColor.White);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (EditorInputGuard.IsTextEntryFocused() || EditorInputGuard.IsLabelEditing(EditorInputGuard.FocusedControl()))
            return base.ProcessCmdKey(ref msg, keyData);
        if (keyData == Keys.Escape) { _placementEntityPath = null; _pendingTerrain = null; _pendingTerrainPosition = null; RefreshActiveToolCard(); _viewport.Invalidate(); if (_drawingSection is not null) { CancelSectionDrawing(); return true; } if (EndObjectBodyDrag(cancel: true)) return true; }
        if (keyData == Keys.Delete && _componentsPanel.CurrentSelection is { } selection) { DeleteComponent(selection); return true; }
        if (keyData == Keys.F) { FrameTerrain(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

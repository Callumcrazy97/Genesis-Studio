using System.Drawing;
using System.Linq;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private bool _componentGizmoDragging;
    private int _componentGizmoAxis = -1;
    private Vector3 _componentDragStart;
    private Vector3 _componentDragAxisWorld;
    private Vector2 _componentDragAxisScreenDir;
    private float _componentDragWorldPerPixel;
    private List<Vector3>? _pathDragStartPoints;
    private List<TerrainPlacedEntity>? _componentObjectBefore;

    private float _componentDragStartYaw;
    private float _componentDragStartPitch;
    private float _componentDragStartRoll;
    private float _componentDragStartScale;
    private float _componentDragStartSizeX;
    private float _componentDragStartSizeZ;

    private void DrawComponentGizmo(IRenderController renderer)
    {
        if (SelectedComponentGizmoOrigin() is not Vector3 origin)
        {
            return;
        }

        EditorTransformGizmo.Draw3D(
            _viewport,
            renderer,
            origin,
            EditorTransformGizmo.Axes(_viewportSession.GizmoSpace, SelectedGizmoEuler()),
            ComponentGizmoWorldLength(),
            _viewportSession.GizmoMode,
            includeZ: true);
    }

    private void DrawSelectionBounds(IRenderController renderer)
    {
        if (SelectedComponentBounds() is not (Vector3 min, Vector3 max))
        {
            return;
        }

        EditorBoundsOverlay.DrawAabb(_viewport, renderer, min, max);
    }

    private (Vector3 Min, Vector3 Max)? SelectedComponentBounds()
    {
        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && SelectedWater() is { } water)
        {
            float halfX = water.SizeX * 0.5f;
            float halfZ = water.SizeZ * 0.5f;
            float bottom = water.SurfaceHeight - water.PhysicsDepth;
            return (
                new Vector3(water.Center.X - halfX, bottom, water.Center.Z - halfZ),
                new Vector3(water.Center.X + halfX, water.SurfaceHeight, water.Center.Z + halfZ));
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            float half = MathF.Max(0.6f, placed.Scale * 1.2f);
            Vector3 p = placed.Position + new Vector3(0, half, 0);
            return (p - new Vector3(half), p + new Vector3(half));
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.PointOfInterest
            && SelectedPoint() is { } point)
        {
            float radius = MathF.Max(0.8f, point.DiscoveryRadius * 0.25f);
            Vector3 p = point.Position with { Y = _terrain.SampleHeight(point.Position.X, point.Position.Z) + 0.35f };
            return (
                new Vector3(p.X - radius, p.Y - 0.4f, p.Z - radius),
                new Vector3(p.X + radius, p.Y + 1.2f, p.Z + radius));
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Path
            && SelectedPath() is { Points.Count: > 0 } path)
        {
            Vector3 min = path.Points[0];
            Vector3 max = path.Points[0];
            foreach (Vector3 pathPoint in path.Points)
            {
                min = Vector3.Min(min, pathPoint);
                max = Vector3.Max(max, pathPoint);
            }

            float pad = MathF.Max(0.5f, path.Width * 0.5f);
            min -= new Vector3(pad, 0.2f, pad);
            max += new Vector3(pad, 0.8f, pad);
            return (min, max);
        }

        return null;
    }

    private Vector3 SelectedGizmoEuler()
    {
        if (SelectedPlacedEntity() is { } placed)
        {
            _componentObjectBefore = ClonePlaced(_nature.PlacedEntities);
            return new Vector3(placed.Pitch, placed.Yaw, placed.Roll);
        }

        return default;
    }

    private bool TryBeginComponentGizmoDrag(Point client)
    {
        if (SelectedComponentGizmoOrigin() is not Vector3 origin)
        {
            return false;
        }

        PointF surface = _viewport.ControlToSurface(client);
        float axisLength = ComponentGizmoWorldLength();
        Vector3 euler = SelectedGizmoEuler();
        if (EditorTransformGizmo.HitTest3D(
                _viewport,
                surface,
                origin,
                axisLength,
                _viewportSession.GizmoMode,
                _viewportSession.GizmoSpace,
                euler,
                includeZ: true) is not EditorGizmoHit hit)
        {
            return false;
        }

        Vector3[] axes = EditorTransformGizmo.Axes(_viewportSession.GizmoSpace, euler);
        Vector3 originSurface = _viewport.WorldToSurface(origin);
        Vector3 tipSurface = _viewport.WorldToSurface(origin + axes[hit.AxisIndex] * axisLength);
        Vector2 screenDir = new(tipSurface.X - originSurface.X, tipSurface.Y - originSurface.Y);
        float screenLen = screenDir.Length();
        if (screenLen < 1f)
        {
            return false;
        }

        _componentGizmoDragging = true;
        _componentGizmoAxis = hit.AxisIndex;
        _componentDragStart = origin;
        _componentDragAxisWorld = axes[hit.AxisIndex];
        _componentDragAxisScreenDir = screenDir / screenLen;
        _componentDragWorldPerPixel = axisLength / screenLen;
        _pathDragStartPoints = _selectedComponentKind == TerrainComponentsPanel.ComponentKind.Path
            && SelectedPath() is { } path
            ? [.. path.Points]
            : null;
        if (SelectedPlacedEntity() is { } placed)
        {
            _componentDragStartYaw = placed.Yaw;
            _componentDragStartPitch = placed.Pitch;
            _componentDragStartRoll = placed.Roll;
            _componentDragStartScale = placed.Scale;
        }

        if (SelectedWater() is { } water)
        {
            _componentDragStartSizeX = water.SizeX;
            _componentDragStartSizeZ = water.SizeZ;
            _componentDragStartScale = water.PhysicsDepth;
        }

        _viewport.NavigationEnabled = false;
        return true;
    }

    private void ApplyComponentGizmoDrag(Point client)
    {
        if (!_componentGizmoDragging)
        {
            return;
        }

        PointF surface = _viewport.ControlToSurface(client);
        Vector3 originSurface = _viewport.WorldToSurface(_componentDragStart);
        Vector2 along = new(surface.X - originSurface.X, surface.Y - originSurface.Y);
        float worldDelta = Vector2.Dot(along, _componentDragAxisScreenDir) * _componentDragWorldPerPixel;
        switch (_viewportSession.GizmoMode)
        {
            case EditorGizmoMode.Rotate:
                ApplyComponentGizmoRotate(worldDelta);
                break;
            case EditorGizmoMode.Scale:
                ApplyComponentGizmoScale(worldDelta);
                break;
            default:
                Vector3 next = _componentDragStart + _componentDragAxisWorld * worldDelta;
                if (_viewportSession.SnapToGrid)
                {
                    next = _viewportSession.Snap(next);
                }

                ApplyComponentGizmoPosition(next);
                break;
        }

        _viewport.Invalidate(true);
        UpdateStatus();
    }

    private void EndComponentGizmoDrag()
    {
        if (!_componentGizmoDragging)
        {
            return;
        }

        _componentGizmoDragging = false;
        _componentGizmoAxis = -1;
        _pathDragStartPoints = null;
        _viewport.NavigationEnabled = true;
        if (_componentObjectBefore is { } before)
        {
            _componentObjectBefore = null;
            ApplyPlaced(before, ClonePlaced(_nature.PlacedEntities), "Transform terrain object");
            return;
        }
        MarkDirty();
        RefreshComponentsPanel();
    }

    private void ApplyComponentGizmoPosition(Vector3 next)
    {
        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.PointOfInterest
            && SelectedPoint() is { } point)
        {
            float y = _componentGizmoAxis == 1
                ? next.Y
                : _terrain.SampleHeight(next.X, next.Z);
            point.Position = new Vector3(next.X, y, next.Z);
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && SelectedWater() is { } water)
        {
            Vector3 previous = new(water.Center.X, water.SurfaceHeight, water.Center.Z);
            Vector3 position = new(next.X, _componentGizmoAxis == 1 ? next.Y : water.SurfaceHeight, next.Z);
            Vector3 delta = position - previous;
            water.Center += delta;
            water.SurfaceHeight = position.Y;
            water.TranslateGeometry(delta);

            _natureMeshDirty = true;
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Path
            && SelectedPath() is { } path
            && _pathDragStartPoints is { Count: > 0 } startPoints)
        {
            Vector3 delta = next - _componentDragStart;
            for (int i = 0; i < path.Points.Count && i < startPoints.Count; i++)
            {
                Vector3 shifted = startPoints[i] + delta;
                path.Points[i] = shifted with { Y = _terrain.SampleHeight(shifted.X, shifted.Z) + 0.03f };
            }

            _pathNetwork = new TerrainPathNetwork(_nature.Paths);
            _natureMeshDirty = true;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            placed.Position = next;
        }
    }

    private void ApplyComponentGizmoRotate(float worldDelta)
    {
        float degrees = worldDelta * 12f;
        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            if (_componentGizmoAxis == 0)
            {
                placed.Pitch = _componentDragStartPitch + degrees;
            }
            else if (_componentGizmoAxis == 2)
            {
                placed.Roll = _componentDragStartRoll + degrees;
            }
            else
            {
                placed.Yaw = _componentDragStartYaw + degrees;
            }
        }
    }

    private void ApplyComponentGizmoScale(float worldDelta)
    {
        float factor = Math.Clamp(1f + worldDelta * 0.08f, 0.05f, 8f);
        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            placed.Scale = Math.Clamp(_componentDragStartScale * factor, 0.05f, 64f);
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && SelectedWater() is { } water)
        {
            if (_componentGizmoAxis == 1)
            {
                water.PhysicsDepth = Math.Clamp(_componentDragStartScale * factor, 0.1f, 10000f);
            }
            else if (_componentGizmoAxis == 0)
            {
                water.SizeX = Math.Clamp(_componentDragStartSizeX * factor, 0.5f, 10000f);
            }
            else
            {
                water.SizeZ = Math.Clamp(_componentDragStartSizeZ * factor, 0.5f, 10000f);
            }

            _natureMeshDirty = true;
        }
    }

    private Vector3? SelectedComponentGizmoOrigin()
    {
        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.PointOfInterest
            && SelectedPoint() is { } point)
        {
            return point.Position with { Y = _terrain.SampleHeight(point.Position.X, point.Position.Z) + 0.35f };
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && SelectedWater() is { } water)
        {
            return water.Center with { Y = water.SurfaceHeight };
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Path
            && SelectedPath() is { Points.Count: > 0 } path)
        {
            Vector3 mid = path.Points[path.Points.Count / 2];
            return mid with { Y = _terrain.SampleHeight(mid.X, mid.Z) + 0.35f };
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            return placed.Position;
        }

        return null;
    }

    private TerrainPlacedEntity? SelectedPlacedEntity() =>
        _nature.PlacedEntities.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase));

    private TerrainPointOfInterest? SelectedPoint() =>
        _nature.PointsOfInterest.FirstOrDefault(point =>
            string.Equals(point.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase));

    private TerrainWaterDefinition? SelectedWater() =>
        _nature.WaterBodies.FirstOrDefault(body =>
            string.Equals(body.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase));

    private TerrainPathDefinition? SelectedPath() =>
        _nature.Paths.FirstOrDefault(path =>
            string.Equals(path.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase));

    private float ComponentGizmoWorldLength() => MathF.Max(2f, _viewport.Camera.Distance * 0.12f);
}

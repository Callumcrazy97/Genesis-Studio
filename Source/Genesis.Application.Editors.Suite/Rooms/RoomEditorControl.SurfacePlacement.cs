using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private RoomTransform? _placementGhost3D;
    private RoomSurfaceHit? _placementSurface;
    private Vector3? _dragSurfaceStart;
    private (int X, int Z, string Resource)? _lastSurfacePlacement;

    public float MetricGridSize => _room.Settings.MetricGridSize
        ?? MathF.Max(.125f, _room.Settings.GridSize / PixelsPerUnit3D);
    public bool SnapToTerrain => _room.Settings.SnapToTerrain;
    public bool AlignToTerrainNormal => _room.Settings.AlignToTerrainNormal;
    public RoomTransform? PlacementGhostTransform3D => _placementGhost3D;
    public RoomSurfaceHit? PlacementSurface => _placementSurface;

    public void SetMetricGridSize(float metres)
    {
        if (!float.IsFinite(metres)) return;
        float? before = _room.Settings.MetricGridSize;
        float after = Math.Clamp(metres, .01f, 4096f);
        if (before == after) return;
        void Apply(float? value) { _room.Settings.MetricGridSize = value; RefreshSurfacePlacementSettings(); }
        Apply(after);
        PushEdit("3D grid increment", () => Apply(after), () => Apply(before));
    }

    public void SetSnapToTerrain(bool enabled)
    {
        bool before = SnapToTerrain;
        if (before == enabled) return;
        void Apply(bool value) { _room.Settings.SnapToTerrain = value; RefreshSurfacePlacementSettings(); }
        Apply(enabled);
        PushEdit("Snap to terrain", () => Apply(enabled), () => Apply(before));
    }

    public void SetAlignToTerrainNormal(bool enabled)
    {
        bool before = AlignToTerrainNormal;
        if (before == enabled) return;
        void Apply(bool value) { _room.Settings.AlignToTerrainNormal = value; RefreshSurfacePlacementSettings(); }
        Apply(enabled);
        PushEdit("Align to terrain normal", () => Apply(enabled), () => Apply(before));
    }

    private void RefreshSurfacePlacementSettings()
    {
        ClearSurfacePlacementPreview();
        SyncToolbar();
        SyncInspector();
        NotifyRoomInspectorStateChanged();
        _viewport?.Host?.Invalidate();
    }

    public bool TryRaycastTerrain(Vector3 origin, Vector3 direction, out RoomSurfaceHit hit)
    {
        hit = default;
        if (!ViewMode3D || !ShowTerrain) return false;
        bool found = false;
        float nearest = float.MaxValue;
        foreach (RoomNode terrain in EnumerateVisibleTerrainNodes())
        {
            if (TerrainPreviewFor(terrain)?.Asset is not { } asset) continue;
            if (RoomSurfacePlacement.Raycast(asset, GetNodeWorldMatrix(terrain), origin, direction,
                out RoomSurfaceHit candidate, nearest))
            {
                hit = candidate;
                nearest = candidate.Distance;
                found = true;
            }
        }
        return found;
    }

    /// <summary>Resolves the uppermost authored terrain surface at a world horizontal coordinate.</summary>
    public bool TryGetTerrainSurface(float x, float z, out RoomSurfaceHit hit)
    {
        hit = default;
        if (!ViewMode3D || !ShowTerrain) return false;
        bool found = false;
        float highest = float.NegativeInfinity;
        foreach (RoomNode terrain in EnumerateVisibleTerrainNodes())
        {
            if (TerrainPreviewFor(terrain)?.Asset is not { } asset) continue;
            Matrix4x4 world = GetNodeWorldMatrix(terrain);
            float ceiling = float.NegativeInfinity;
            for (int mask = 0; mask < 8; mask++)
            {
                Vector3 corner = new(asset.OriginX + ((mask & 1) == 0 ? 0 : (asset.ResolutionX - 1) * asset.CellSize),
                    (mask & 2) == 0 ? asset.MinHeight : asset.MaxHeight,
                    asset.OriginZ + ((mask & 4) == 0 ? 0 : (asset.ResolutionZ - 1) * asset.CellSize));
                ceiling = MathF.Max(ceiling, Vector3.Transform(corner, world).Y);
            }
            if (RoomSurfacePlacement.Raycast(asset, world, new Vector3(x, ceiling + 1, z), -Vector3.UnitY,
                out RoomSurfaceHit candidate) && candidate.Normal.Y > .0001f && candidate.Position.Y > highest)
            {
                highest = candidate.Position.Y;
                hit = candidate;
                found = true;
            }
        }
        return found;
    }

    private bool TryPlacementPosition3D(Point client, out Vector3 position)
    {
        position = default;
        bool terrainPlacement = _pendingPlacementKind == RoomNodeKind.Terrain;
        (Vector3 origin, Vector3 direction) = _viewport.PickRay(client);
        if (!terrainPlacement && SnapToTerrain && TryRaycastTerrain(origin, direction, out RoomSurfaceHit hit))
        {
            Vector2 snapped = SnapPoint(new Vector2(hit.Position.X, hit.Position.Z));
            if (TryGetTerrainSurface(snapped.X, snapped.Y, out RoomSurfaceHit gridHit)) hit = gridHit;
            else if (_room.Settings.SnapEnabled) return false;
            position = hit.Position;
            return true;
        }
        if (!_viewport.RayToGround(client, 0, out Vector3 ground)) return false;
        Vector2 grid = SnapPoint(new Vector2(ground.X, ground.Z));
        position = new Vector3(grid.X, 0, grid.Y);
        return true;
    }

    private void UpdateSurfacePlacementPreview(Point client)
    {
        ClearSurfacePlacementPreview();
        if (!ViewMode3D || ActiveTool != RoomTool.Place || _pendingPlacementPath is null
            || !TryPlacementPosition3D(client, out Vector3 position)) return;
        RoomNode preview = new()
        {
            Kind = _pendingPlacementKind,
            GameObject = _pendingPlacementKind == RoomNodeKind.GameObject
                ? new RoomGameObjectData { Prefab = ResourceNames.Name(ProjectRoot, _pendingPlacementPath) } : null!,
        };
        preview.Transform.X = position.X; preview.Transform.Y = position.Y; preview.Transform.Z = position.Z;
        ApplyPlacementDefaults(preview.Transform);
        if (preview.Kind == RoomNodeKind.GameObject && SnapToTerrain
            && TryGetTerrainSurface(position.X, position.Z, out RoomSurfaceHit surface))
        {
            ApplySurfaceContact(preview, surface, AlignToTerrainNormal);
            _placementSurface = surface;
        }
        else if (preview.Kind == RoomNodeKind.GameObject)
            ApplySurfaceContact(preview, new RoomSurfaceHit(position, Vector3.UnitY, 0), false);
        _placementGhost3D = preview.Transform;
        _viewportCoordsLabel.Text = $"X: {preview.Transform.X:0.##} m  Y: {preview.Transform.Y:0.##} m  Z: {preview.Transform.Z:0.##} m";
    }

    private void ClearSurfacePlacementPreview()
    {
        _placementGhost3D = null;
        _placementSurface = null;
    }

    private void DrawSurfacePlacementPreview(IRenderController renderer)
    {
        if (!ViewMode3D || ActiveTool != RoomTool.Place || _placementGhost3D is null || _pendingPlacementPath is null) return;
        RoomNode preview = new()
        {
            Kind = _pendingPlacementKind, Transform = _placementGhost3D,
            GameObject = new RoomGameObjectData { Prefab = ResourceNames.Name(ProjectRoot, _pendingPlacementPath) },
        };
        GetPlacementBounds(preview, out Vector3 min, out Vector3 max);
        Matrix4x4 world = RoomSurfacePlacement.Transform(_placementGhost3D);
        var color = new RenderColor(.25f, .75f, 1f, .85f);
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int mask = 0; mask < 8; mask++)
            corners[mask] = Vector3.Transform(new Vector3((mask & 1) == 0 ? min.X : max.X,
                (mask & 2) == 0 ? min.Y : max.Y, (mask & 4) == 0 ? min.Z : max.Z), world);
        for (int mask = 0; mask < 8; mask++)
            for (int axis = 1; axis <= 4; axis *= 2)
                if ((mask & axis) == 0) DrawGizmoLine3D(renderer, corners[mask], corners[mask | axis], color);
        if (_placementSurface is not RoomSurfaceHit surface) return;
        Vector3 point = surface.Position + surface.Normal * .01f;
        Matrix4x4 tangent = RoomSurfacePlacement.SurfaceRotation(surface.Normal, _placementGhost3D.RotationY);
        float radius = MathF.Max(.15f, MetricGridSize * .35f);
        for (int segment = 0; segment < 24; segment++)
        {
            float angleA = segment * MathF.Tau / 24, angleB = (segment + 1) * MathF.Tau / 24;
            Vector3 a = point + Vector3.TransformNormal(new Vector3(MathF.Cos(angleA), 0, MathF.Sin(angleA)) * radius, tangent);
            Vector3 b = point + Vector3.TransformNormal(new Vector3(MathF.Cos(angleB), 0, MathF.Sin(angleB)) * radius, tangent);
            DrawGizmoLine3D(renderer, a, b, color);
        }
        DrawGizmoLine3D(renderer, point, point + surface.Normal * radius, color);
    }

    private void DrawPlacementModelPreview(IRenderController renderer)
    {
        if (!ViewMode3D || ActiveTool != RoomTool.Place || _placementGhost3D is null
            || _pendingPlacementPath is null || _pendingPlacementKind != RoomNodeKind.GameObject) return;
        RoomNode preview = new()
        {
            Id = "$placement-preview", Kind = RoomNodeKind.GameObject, Transform = _placementGhost3D,
            GameObject = new RoomGameObjectData { Prefab = ResourceNames.Name(ProjectRoot, _pendingPlacementPath) },
        };
        NodeVisual visual = VisualFor(preview);
        Matrix4x4 placement = RoomSurfacePlacement.Transform(_placementGhost3D);
        Matrix4x4 modelWorld = Matrix4x4.CreateScale(visual.ModelScaleX, visual.ModelScaleY, visual.ModelScaleZ) * placement;
        var queue = Genesis.Runtime.Modeling.ModelRenderQueue.Rent();
        if (!visual.HasFlow && !string.IsNullOrWhiteSpace(visual.ModelAsset)
            && _runtimeModelPreview.Enqueue(queue, ProjectRoot, visual.ModelAsset, visual.MaterialAsset, modelWorld,
                new Genesis.Runtime.ECS.Components.Draw3DComponent { Visible = true, CastShadows = false, ReceiveShadows = true },
                new Genesis.Runtime.ECS.Components.ModelRendererComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = false, ReceiveShadows = true },
                new Genesis.Runtime.Modeling.RuntimeModelAnimationState(visual.AnimationClip, 0, visual.AnimationFps, visual.AnimationLoop), renderer))
        {
            queue.Transform(static draw => { draw.Alpha *= .55f; draw.Flags |= MeshDrawFlags.Transparent | MeshDrawFlags.NoShadow; return draw; });
            queue.Draw(renderer);
            return;
        }
        if (visual.ImagePath is not null && !visual.HasModel
            && _textures.TryGet(renderer, visual.ImagePath, out TextureHandle texture, out int width, out int height))
        {
            float h = height / PixelsPerUnit3D;
            renderer.DrawMesh(new MeshDrawCall { Mesh = _unitQuad, Texture = texture,
                World = Matrix4x4.CreateScale(width / PixelsPerUnit3D, h, 1) * Matrix4x4.CreateTranslation(0, h * .5f, 0) * placement,
                Tint = RenderColor.White, Alpha = .55f * visual.Alpha, Flags = MeshDrawFlags.Transparent | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull });
            return;
        }
        PlacedModelMesh? mesh = visual.ModelPath is not null ? ModelMeshFor(renderer, visual.ModelPath) : null;
        renderer.DrawMesh(new MeshDrawCall { Mesh = mesh?.Mesh ?? _unitCube, Texture = mesh?.FlowTexture ?? TextureHandle.Invalid,
            World = mesh is not null ? modelWorld : Matrix4x4.CreateTranslation(0, .5f, 0) * placement,
            Tint = mesh is not null ? RenderColor.White : visual.Tint, Alpha = .55f,
            Flags = MeshDrawFlags.Transparent | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull });
    }

    private bool ContinueSurfacePlacement(Point client)
    {
        if (_pendingPlacementPath is null || !TryPlacementPosition3D(client, out Vector3 position)) return false;
        float cell = _room.Settings.SnapEnabled ? MetricGridSize : .01f;
        var key = ((int)MathF.Round(position.X / cell), (int)MathF.Round(position.Z / cell), _pendingPlacementPath);
        if (_lastSurfacePlacement == key) return false;
        _lastSurfacePlacement = key;
        AddPlacedNode(position);
        return true;
    }

    private bool SnapNodeToTerrain(RoomNode node)
    {
        RoomTransform world = GetNodeWorldTransform(node);
        if (node.Kind != RoomNodeKind.GameObject || IsNodeLocked(node)
            || !TryGetTerrainSurface(world.X, world.Z, out RoomSurfaceHit surface)) return false;
        return ApplySurfaceContact(node, surface, AlignToTerrainNormal);
    }

    private bool ApplySurfaceContact(RoomNode node, RoomSurfaceHit surface, bool align)
    {
        RoomTransform transform = GetNodeWorldTransform(node);
        if (align)
        {
            Vector3 euler = ModelPoseWorkflow.EulerDegrees(Quaternion.CreateFromRotationMatrix(
                RoomSurfacePlacement.SurfaceRotation(surface.Normal, transform.RotationY)));
            transform.RotationX = euler.X; transform.RotationY = euler.Y; transform.RotationZ = euler.Z;
        }
        Matrix4x4 basis = Matrix4x4.CreateScale(transform.ScaleX, transform.ScaleY, transform.ScaleZ)
            * Matrix4x4.CreateFromYawPitchRoll(transform.RotationY * MathF.PI / 180,
                transform.RotationX * MathF.PI / 180, transform.RotationZ * MathF.PI / 180);
        GetPlacementBounds(node, out Vector3 min, out Vector3 max);
        transform.Y = surface.Position.Y + RoomSurfacePlacement.ContactOffset(min, max, basis, surface.Normal);
        return SetNodeWorldTransform(node, transform);
    }

    private void GetPlacementBounds(RoomNode node, out Vector3 min, out Vector3 max)
    {
        NodeVisual visual = VisualFor(node);
        if (visual.HasModel && !string.IsNullOrWhiteSpace(visual.ModelAsset)
            && _runtimeModelPreview.TryGetBounds(ProjectRoot, visual.ModelAsset, out min, out max, includePivot: true))
        {
            (min, max) = TransformBounds(min, max, Matrix4x4.CreateScale(visual.ModelScaleX, visual.ModelScaleY, visual.ModelScaleZ));
            return;
        }
        if (visual.ImagePath is not null && !visual.HasModel)
        {
            min = new Vector3(-visual.Width / (PixelsPerUnit3D * 2), 0, 0);
            max = new Vector3(visual.Width / (PixelsPerUnit3D * 2), visual.Height / PixelsPerUnit3D, 0);
            return;
        }
        min = new Vector3(-.5f, 0, -.5f);
        max = new Vector3(.5f, 1, .5f);
    }

    /// <summary>Explicit surface alignment, shared by End and the contextual inspector.</summary>
    public bool SnapSelectionToTerrain()
    {
        if (!ViewMode3D) return false;
        Dictionary<string, RoomTransform> before = SnapshotSelectionTransforms();
        foreach (RoomNode node in SelectionTransformRoots()) SnapNodeToTerrain(node);
        Dictionary<string, RoomTransform> after = SnapshotSelectionTransforms();
        if (TransformSnapshotsEqual(before, after)) return false;
        PushTransformSnapshotEdit("Snap selection to terrain", before, after);
        RefreshPhase4Ui();
        return true;
    }

    private bool SnapSelectionToSurfaceOrFloor()
    {
        if (!ViewMode3D) return false;
        IReadOnlyList<RoomNode> editable = SelectionTransformRoots().Where(node => node.Kind == RoomNodeKind.GameObject).ToList();
        if (editable.Count == 0) return false;
        Dictionary<string, RoomTransform> before = SnapshotSelectionTransforms();
        foreach (RoomNode node in editable)
        {
            if (SnapNodeToTerrain(node)) continue;
            RoomTransform world = GetNodeWorldTransform(node);
            float start = world.Y + .001f;
            float height = start >= 0 ? 0 : float.NegativeInfinity;
            foreach (RoomNode other in EnumerateVisibleNodes())
            {
                if (other.Kind != RoomNodeKind.GameObject || ReferenceEquals(other, node) || editable.Contains(other)) continue;
                GetPlacementBounds(other, out Vector3 localMin, out Vector3 localMax);
                (Vector3 min, Vector3 max) = TransformBounds(localMin, localMax, GetNodeWorldMatrix(other));
                if (world.X >= min.X && world.X <= max.X
                    && world.Z >= min.Z && world.Z <= max.Z && max.Y <= start)
                    height = MathF.Max(height, max.Y);
            }
            if (float.IsFinite(height))
                ApplySurfaceContact(node, new RoomSurfaceHit(new Vector3(world.X, height, world.Z), Vector3.UnitY, 0), false);
        }
        Dictionary<string, RoomTransform> after = SnapshotSelectionTransforms();
        if (TransformSnapshotsEqual(before, after)) return false;
        PushTransformSnapshotEdit("Snap selection to floor", before, after);
        RefreshPhase4Ui();
        return true;
    }

    private void BeginSurfaceDrag(Point client)
    {
        _dragSurfaceStart = null;
        if (ViewMode3D && TryPlacementPosition3D(client, out Vector3 surface)) _dragSurfaceStart = surface;
    }

    private void MoveBodyOnSurface(Point client, RoomTransform start, RoomTransform transform)
    {
        if (_dragSurfaceStart is not Vector3 origin || !TryPlacementPosition3D(client, out Vector3 surface)) return;
        Vector2 snapped = SnapPoint(new Vector2(start.X + surface.X - origin.X, start.Z + surface.Z - origin.Z));
        transform.X = snapped.X; transform.Z = snapped.Y;
    }

    private void SnapDraggedSelection()
    {
        if (!SnapToTerrain || !ViewMode3D || _drag is not (DragKind.Move or DragKind.Axis3D)) return;
        // Vertical-axis dragging deliberately adjusts clearance; horizontal motion follows terrain.
        if (_drag == DragKind.Axis3D && MathF.Abs(_dragAxisWorld.Y) > .999f) return;
        foreach (RoomNode node in SelectionTransformRoots()) SnapNodeToTerrain(node);
    }
}

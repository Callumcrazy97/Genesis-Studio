using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelEditorControl
{
    private readonly Dictionary<EditorGizmoMode, ToolStripButton> _meshGizmoButtons = [];
    private ModelPrimitiveKind? _pendingPrimitive;
    private GModelAsset? _pendingPrimitiveAsset;
    private Vector3 _pendingPrimitivePosition;
    private bool _pendingPrimitiveValid;
    private EditorGizmoMode _meshGizmoMode = EditorGizmoMode.Move;
    private bool _meshGizmoDragging;
    private int _meshGizmoAxis = -1;
    private int _meshGizmoMesh = -1;
    private Vector3 _meshGizmoOrigin;
    private Vector3 _meshGizmoAxisWorld;
    private Vector2 _meshGizmoAxisScreen;
    private PointF _meshGizmoDragStartSurface;
    private float _meshGizmoWorldPerPixel;
    private GModelAsset? _meshGizmoBefore;

    private void ArmPrimitivePlacement(ModelPrimitiveKind kind)
    {
        CancelAuthoringGesture();
        CancelPushPull();
        _pendingPrimitive = kind;
        _pendingPrimitiveValid = false;
        _pendingPrimitiveAsset = BuildPrimitivePreview(kind);
        _tool = kind switch
        {
            ModelPrimitiveKind.Cube => ModelAuthoringTool.Cube,
            ModelPrimitiveKind.Sphere => ModelAuthoringTool.Sphere,
            ModelPrimitiveKind.Cylinder or ModelPrimitiveKind.Capsule or ModelPrimitiveKind.Cone => ModelAuthoringTool.Cylinder,
            _ => ModelAuthoringTool.SquareFace,
        };
        SetMode(ModelEditorMode.Compose);
        AllowSpin(false);
        foreach ((ModelPrimitiveKind candidate, Button button) in _primitiveButtons)
            button.BackColor = candidate == kind ? EditorChrome.Accent : EditorChrome.Raised;
        UpdateToolHeader();
        Status.Text = $"{PrimitiveName(kind)} placement armed · move over the viewport and click to place · Esc cancels";
        Surface.Invalidate(true);
    }

    private static GModelAsset BuildPrimitivePreview(ModelPrimitiveKind kind)
    {
        ModelPart part = new()
        {
            Primitive = kind,
            Name = PrimitiveName(kind),
            Color = [200f / 255f, 200f / 255f, 200f / 255f],
        };
        (MeshVertex[] vertices, ushort[] sourceIndices) = ModelPartBuilder.Bake([part]);
        ushort[] indices = kind == ModelPrimitiveKind.Quad ? MakeDoubleSided(sourceIndices) : sourceIndices;
        GModelAsset asset = new()
        {
            Name = PrimitiveName(kind) + " Placement Preview",
            Materials = [CreateNeutralMaterial()],
            Meshes = [new GModelMesh { Name = PrimitiveName(kind), Vertices = vertices, Indices = indices, MaterialIndex = 0 }],
        };
        asset.RecalculateBounds();
        return asset;
    }

    private bool UpdatePrimitivePlacement(Point client)
    {
        if (_pendingPrimitive is null) return false;
        _pendingPrimitiveValid = TryPrimitivePlacementPoint(client, out _pendingPrimitivePosition);
        Surface.Invalidate(true);
        return true;
    }

    private bool TryPrimitivePlacementPoint(Point client, out Vector3 point)
    {
        if (TryPickSurface(client, out Vector3 surface))
        {
            point = SnapPlacement(surface);
            return true;
        }

        (Vector3 Origin, Vector3 Direction) ray = Surface.PickRay(client);
        float denominator = Vector3.Dot(ray.Direction, Vector3.UnitY);
        point = default;
        if (MathF.Abs(denominator) < 1e-5f) return false;
        float distance = -ray.Origin.Y / denominator;
        if (distance <= 0f) return false;
        point = SnapPlacement(ray.Origin + ray.Direction * distance + Asset.Pivot.Position);
        return true;
    }

    private Vector3 SnapPlacement(Vector3 point)
    {
        if (!_snapToGrid) return point;
        float step = MathF.Max(.001f, _gridSnap);
        return new Vector3(
            MathF.Round(point.X / step) * step,
            MathF.Round(point.Y / step) * step,
            MathF.Round(point.Z / step) * step);
    }

    private bool TryCommitPrimitivePlacement(Point client)
    {
        if (_pendingPrimitive is not { } kind) return false;
        if (!_pendingPrimitiveValid && !TryPrimitivePlacementPoint(client, out _pendingPrimitivePosition)) return true;
        Vector3 position = _pendingPrimitivePosition;
        CancelPrimitivePlacement();
        AddPartAt(kind, position);
        _tool = ModelAuthoringTool.Select;
        UpdateToolHeader();
        SetMeshGizmoMode(EditorGizmoMode.Move);
        Status.Text = $"Placed {PrimitiveName(kind)} at {position.X:0.###}, {position.Y:0.###}, {position.Z:0.###} · drag a gizmo axis to transform";
        return true;
    }

    private bool CancelPrimitivePlacement()
    {
        if (_pendingPrimitive is null) return false;
        _pendingPrimitive = null;
        _pendingPrimitiveAsset = null;
        _pendingPrimitiveValid = false;
        foreach (Button button in _primitiveButtons.Values) button.BackColor = EditorChrome.Raised;
        UpdateToolHeader();
        Surface.Invalidate(true);
        return true;
    }

    private void DrawPrimitivePlacementGhost(IRenderController renderer)
    {
        if (!_pendingPrimitiveValid || _pendingPrimitiveAsset is null) return;
        PreviewRenderer.DrawAssetGhost(
            _pendingPrimitiveAsset,
            ProjectRoot,
            Matrix4x4.CreateTranslation(_pendingPrimitivePosition - Asset.Pivot.Position),
            default,
            new RenderColor(.18f, .72f, 1f),
            .42f,
            renderer);
    }

    private void SetMeshGizmoMode(EditorGizmoMode mode)
    {
        _meshGizmoMode = mode;
        foreach ((EditorGizmoMode candidate, ToolStripButton button) in _meshGizmoButtons)
            button.Checked = candidate == mode;
        Status.Text = EditorTransformGizmo.StatusHint(mode, EditorGizmoSpace.World, _snapToGrid);
        Surface.Invalidate(true);
    }

    private void DrawMeshSelectionOverlay(IRenderController renderer)
    {
        if (_pendingPrimitive is not null || SelectedMeshBounds() is not (Vector3 min, Vector3 max)) return;
        EditorBoundsOverlay.DrawAabb(Surface, renderer, min, max);
        Vector3 center = (min + max) * .5f;
        EditorTransformGizmo.Draw3D(
            Surface,
            renderer,
            center,
            MeshGizmoLength(min, max),
            _meshGizmoMode,
            includeZ: true,
            activeAxis: _meshGizmoDragging ? _meshGizmoAxis : null);
    }

    private (Vector3 Min, Vector3 Max)? SelectedMeshBounds()
    {
        int index = _groups.SelectedIndex;
        if ((uint)index >= Asset.Meshes.Count) return null;
        MeshVertex[] vertices = Vertices(Asset.Meshes[index]);
        if (vertices.Length == 0) return null;
        Vector3 min = vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Min) - Asset.Pivot.Position;
        Vector3 max = vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Max) - Asset.Pivot.Position;
        return (min, max);
    }

    private static float MeshGizmoLength(Vector3 min, Vector3 max) =>
        Math.Clamp(Vector3.Distance(min, max) * .38f, .35f, 10000f);

    private bool TryBeginMeshGizmoDrag(Point client)
    {
        if (_pendingPrimitive is not null || SelectedMeshBounds() is not (Vector3 min, Vector3 max)) return false;
        Vector3 origin = (min + max) * .5f;
        float length = MeshGizmoLength(min, max);
        PointF surface = Surface.ControlToSurface(client);
        EditorGizmoHit? hit = EditorTransformGizmo.HitTest3D(
            Surface, surface, origin, length, _meshGizmoMode, includeZ: true);
        if (hit is null) return false;
        Vector3 axis = EditorTransformGizmo.Axes(EditorGizmoSpace.World, default)[hit.Value.AxisIndex];
        if (!EditorTransformGizmo.TryProjectAxisToSurface(Surface, origin, axis, length, out Vector2 direction, out float screenLength))
            return false;
        _meshGizmoDragging = true;
        _meshGizmoAxis = hit.Value.AxisIndex;
        _meshGizmoMesh = _groups.SelectedIndex;
        _meshGizmoOrigin = origin + Asset.Pivot.Position;
        _meshGizmoAxisWorld = axis;
        _meshGizmoAxisScreen = direction;
        _meshGizmoDragStartSurface = surface;
        _meshGizmoWorldPerPixel = length / screenLength;
        _meshGizmoBefore = ModelPoseWorkflow.Copy(Asset);
        Surface.NavigationEnabled = false;
        Surface.Host.Capture = true;
        return true;
    }

    private void UpdateMeshGizmoDrag(Point client)
    {
        if (!_meshGizmoDragging || _meshGizmoBefore is null || (uint)_meshGizmoMesh >= _meshGizmoBefore.Meshes.Count) return;
        PointF surface = Surface.ControlToSurface(client);
        Vector2 offset = new(surface.X - _meshGizmoDragStartSurface.X, surface.Y - _meshGizmoDragStartSurface.Y);
        float amount = Vector2.Dot(offset, _meshGizmoAxisScreen) * _meshGizmoWorldPerPixel;
        GModelAsset preview = ModelPoseWorkflow.Copy(_meshGizmoBefore);
        GModelMesh mesh = preview.Meshes[_meshGizmoMesh];
        MeshVertex[] vertices = Vertices(mesh);
        Vector3 center = _meshGizmoOrigin;

        if (_meshGizmoMode == EditorGizmoMode.Move)
        {
            Vector3 next = center + _meshGizmoAxisWorld * amount;
            if (_snapToGrid) next = SnapPlacement(next);
            Vector3 delta = next - center;
            for (int index = 0; index < vertices.Length; index++) vertices[index].Position += delta;
        }
        else if (_meshGizmoMode == EditorGizmoMode.Scale)
        {
            float factor = Math.Clamp(1f + amount / MathF.Max(.1f, MeshGizmoLength(preview.Bounds.Min, preview.Bounds.Max)), .02f, 100f);
            Vector3 scale = Vector3.One;
            if (_meshGizmoAxis == 0) scale.X = factor;
            else if (_meshGizmoAxis == 1) scale.Y = factor;
            else scale.Z = factor;
            for (int index = 0; index < vertices.Length; index++)
            {
                vertices[index].Position = center + (vertices[index].Position - center) * scale;
                Vector3 normal = vertices[index].Normal / scale;
                vertices[index].Normal = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitY;
            }
        }
        else
        {
            float radians = amount * MathF.PI / MathF.Max(.1f, MeshGizmoLength(preview.Bounds.Min, preview.Bounds.Max));
            Matrix4x4 rotation = Matrix4x4.CreateFromAxisAngle(_meshGizmoAxisWorld, radians);
            for (int index = 0; index < vertices.Length; index++)
            {
                vertices[index].Position = center + Vector3.Transform(vertices[index].Position - center, rotation);
                vertices[index].Normal = Vector3.Normalize(Vector3.TransformNormal(vertices[index].Normal, rotation));
            }
        }

        SetVertices(mesh, vertices);
        preview.RecalculateBounds();
        Asset = preview;
        _previewChanged = true;
        _filteredPreview = null;
        Surface.Invalidate(true);
        Status.Text = $"{_meshGizmoMode} {"XYZ"[_meshGizmoAxis]} · {amount:0.###}";
    }

    private void FinishMeshGizmoDrag()
    {
        if (!_meshGizmoDragging) return;
        GModelAsset? before = _meshGizmoBefore;
        GModelAsset after = ModelPoseWorkflow.Copy(Asset);
        ClearMeshGizmoDrag();
        if (before is null) return;
        MarkDirty();
        RefreshAssetPresentation();
        RefreshGroups();
        PushEdit($"{_meshGizmoMode} mesh", () => ReplaceAsset(ModelPoseWorkflow.Copy(after)), () => ReplaceAsset(ModelPoseWorkflow.Copy(before)));
    }

    private bool CancelMeshGizmoDrag()
    {
        if (!_meshGizmoDragging) return false;
        GModelAsset? before = _meshGizmoBefore;
        ClearMeshGizmoDrag();
        if (before is not null) ReplaceAsset(ModelPoseWorkflow.Copy(before));
        Status.Text = "Mesh transform cancelled.";
        return true;
    }

    private void ClearMeshGizmoDrag()
    {
        _meshGizmoDragging = false;
        _meshGizmoAxis = -1;
        _meshGizmoMesh = -1;
        _meshGizmoBefore = null;
        Surface.NavigationEnabled = true;
        Surface.Host.Capture = false;
    }
}

using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private readonly RuntimeModelAssetRegistry _pickModels = new();
    private static float? TriangleHit(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
        => TriangleHit(origin, direction, a, b, c, out _);

    private static float? TriangleHit(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out Vector2 barycentric)
    {
        barycentric = Vector2.Zero;
        Vector3 edge = b - a, edge2 = c - a, p = Vector3.Cross(direction, edge2);
        float det = Vector3.Dot(edge, p); if (Math.Abs(det) < 1e-8f) return null;
        Vector3 offset = origin - a; float u = Vector3.Dot(offset, p) / det; if (u < 0 || u > 1) return null;
        Vector3 q = Vector3.Cross(offset, edge); float v = Vector3.Dot(direction, q) / det; if (v < 0 || u + v > 1) return null;
        barycentric = new(u, v);
        float distance = Vector3.Dot(edge2, q) / det; return distance > 0 ? distance : null;
    }
    private (TerrainComponentsPanel.ComponentKind? Kind, string? Id) PickScene(Point point)
    {
        var ray = _viewport.PickRay(point);
        float nearest = PickTerrain(point, out var ground) ? Vector3.Distance(ray.Origin, ground) : float.MaxValue;
        TerrainComponentsPanel.ComponentKind? kind = null; string? id = null;
        foreach (var water in _nature.WaterBodies)
        {
            if (Math.Abs(ray.Direction.Y) < 1e-7f) continue;
            float t = (water.SurfaceHeight - ray.Origin.Y) / ray.Direction.Y; var p = ray.Origin + ray.Direction * t;
            if (t > 0 && t < nearest && water.ContainsHorizontal(p.X, p.Z))
            { nearest = t; kind = TerrainComponentsPanel.ComponentKind.Water; id = water.Id; }
        }
        foreach (var placed in _nature.PlacedEntities)
        {
            var document = TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity));
            var component = document?.Components.FirstOrDefault(c => c.Enabled && c.Type == TerrainEntityComponentKinds.Model);
            if (component is null)
            {
                var sprite = document?.Components.FirstOrDefault(c => c.Enabled && c.Type == TerrainEntityComponentKinds.Texture);
                if (sprite is null) continue;
                float spriteScale = float.TryParse(sprite.Get("Scale", "1"), System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : 1;
                int frame = EntityTextureFrame(sprite);
                if (_entityImages.CpuGeometry(ProjectRoot, sprite.Get("Texture"), frame, sprite.Get("Mode")) is not { } geometry) continue;
                Matrix4x4 placement = Matrix4x4.CreateScale(placed.Scale)
                    * Matrix4x4.CreateFromYawPitchRoll(placed.Yaw * MathF.PI / 180, placed.Pitch * MathF.PI / 180, placed.Roll * MathF.PI / 180)
                    * Matrix4x4.CreateTranslation(placed.Position);
                Matrix4x4 transform = Matrix4x4.CreateScale(spriteScale) * TerrainTextureGeometry.Facing(sprite.Get("Mode"), placement, _viewport.Camera.Eye) * placement;
                if (!Matrix4x4.Invert(transform, out Matrix4x4 spriteInverse)) continue;
                Vector3 spriteOrigin = Vector3.Transform(ray.Origin, spriteInverse), spriteDirection = Vector3.TransformNormal(ray.Direction, spriteInverse);
                for (int triangle = 0; triangle < geometry.Indices.Length; triangle += 3)
                {
                    var a = geometry.Vertices[geometry.Indices[triangle]];
                    var b = geometry.Vertices[geometry.Indices[triangle + 1]];
                    var c = geometry.Vertices[geometry.Indices[triangle + 2]];
                    if (TriangleHit(spriteOrigin, spriteDirection, a.Position, b.Position, c.Position, out Vector2 uv) is not { } distance || distance >= nearest) continue;
                    Vector2 imageUv = a.UV * (1 - uv.X - uv.Y) + b.UV * uv.X + c.UV * uv.Y;
                    if (!_entityImages.OpaqueAt(ProjectRoot, sprite.Get("Texture"), frame, imageUv)) continue;
                    nearest = distance; kind = TerrainComponentsPanel.ComponentKind.Entity; id = placed.Id;
                }
                continue;
            }
            string path = component.Get("Model"); if (string.IsNullOrWhiteSpace(path)) continue;
            if (path.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase)) path = ResolveEntityFullPath(path);
            var model = _pickModels.Load(ProjectRoot, path);
            float scale = float.TryParse(component.Get("Scale", "1"), System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 1;
            var world = Matrix4x4.CreateScale(placed.Scale * scale) * Matrix4x4.CreateFromYawPitchRoll(placed.Yaw * MathF.PI / 180, placed.Pitch * MathF.PI / 180, placed.Roll * MathF.PI / 180) * Matrix4x4.CreateTranslation(placed.Position);
            if (!Matrix4x4.Invert(world, out var inverse)) continue;
            Vector3 origin = Vector3.Transform(ray.Origin, inverse), direction = Vector3.TransformNormal(ray.Direction, inverse);
            foreach (var mesh in model.Meshes)
            {
                var indices = mesh.Indices;
                Vector3 P(int i) => (mesh.IsSkinned ? mesh.SkinnedVertices[i].Position : mesh.Vertices[i].Position) - (model.Pivot?.Position ?? Vector3.Zero);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                    if (TriangleHit(origin, direction, P(indices[i]), P(indices[i + 1]), P(indices[i + 2])) is { } t && t < nearest)
                    { nearest = t; kind = TerrainComponentsPanel.ComponentKind.Entity; id = placed.Id; }
            }
        }
        return (kind, id);
    }
    private void SelectSceneAt(Point point)
    {
        var hit = PickScene(point);
        if (hit.Kind is { } kind && hit.Id is { } id) _componentsPanel.Select(kind, id);
        else { _selectedComponentKind = null; _selectedComponentId = null; RebuildSelectionInspector(); }
        RefreshActiveToolCard(); _viewport.Invalidate();
    }
}

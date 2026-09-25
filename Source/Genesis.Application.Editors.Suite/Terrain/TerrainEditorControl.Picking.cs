using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private readonly RuntimeModelAssetRegistry _pickModels = new();
    private static float? TriangleHit(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 edge = b - a, edge2 = c - a, p = Vector3.Cross(direction, edge2);
        float det = Vector3.Dot(edge, p); if (Math.Abs(det) < 1e-8f) return null;
        Vector3 offset = origin - a; float u = Vector3.Dot(offset, p) / det; if (u < 0 || u > 1) return null;
        Vector3 q = Vector3.Cross(offset, edge); float v = Vector3.Dot(direction, q) / det; if (v < 0 || u + v > 1) return null;
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
                Vector3 toward = _viewport.Camera.Eye - placed.Position;
                var facing = sprite.Get("Mode") == nameof(TerrainEntityTextureMode.Billboard2D) ? Matrix4x4.CreateRotationY(MathF.Atan2(toward.X, toward.Z)) : Matrix4x4.Identity;
                var transform = Matrix4x4.CreateScale(placed.Scale * spriteScale) * facing * Matrix4x4.CreateFromYawPitchRoll(placed.Yaw * MathF.PI / 180, placed.Pitch * MathF.PI / 180, placed.Roll * MathF.PI / 180) * Matrix4x4.CreateTranslation(placed.Position);
                Vector3 P(float x, float y) => Vector3.Transform(new(x, y, 0), transform);
                foreach (var t in new[] { TriangleHit(ray.Origin, ray.Direction, P(-.5f, 0), P(.5f, 0), P(.5f, 1)), TriangleHit(ray.Origin, ray.Direction, P(-.5f, 0), P(.5f, 1), P(-.5f, 1)) })
                    if (t is { } distance && distance < nearest) { nearest = distance; kind = TerrainComponentsPanel.ComponentKind.Entity; id = placed.Id; }
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
                Vector3 P(int i) => mesh.IsSkinned ? mesh.SkinnedVertices[i].Position : mesh.Vertices[i].Position;
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

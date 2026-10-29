using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets.Painting;

/// <summary>Where a brush ray meets the model: the part, its triangle, that triangle's material and texture coordinate.</summary>
public readonly record struct ModelPaintHit(int Mesh, int Triangle, int Material, Vector3 Position, Vector2 Uv);

/// <summary>
/// CPU picking for texture painting, against the mesh data the Model editor already holds: the
/// nearest triangle a ray crosses over all parts, the UV interpolated at the crossing, and the
/// material that triangle draws with (its own per-face material when the part has them).
/// </summary>
public static class ModelPaintPicker
{
    /// <summary>
    /// The nearest hit along the ray. <paramref name="vertices"/> gives each part's vertices in the
    /// ray's space, or null to leave that part out (a hidden part is not painted through).
    /// </summary>
    public static bool TryPick(GModelAsset asset, Func<int, MeshVertex[]?> vertices, Vector3 origin, Vector3 direction, out ModelPaintHit hit)
    {
        ArgumentNullException.ThrowIfNull(asset);
        hit = default;
        float best = float.MaxValue;
        bool found = false;
        for (int index = 0; index < asset.Meshes.Count; index++)
        {
            GModelMesh mesh = asset.Meshes[index];
            MeshVertex[]? source = vertices(index);
            if (source is null || mesh.Indices is not { Length: >= 3 }) continue;
            if (!ModelSurfaceBrush.RaycastUv(source, mesh.Indices, origin, direction, out Vector3 point, out Vector2 uv, out int triangle)) continue;
            float distance = Vector3.DistanceSquared(origin, point);
            if (distance >= best) continue;
            best = distance;
            found = true;
            hit = new ModelPaintHit(index, triangle, MaterialOf(mesh, triangle), point, uv);
        }
        return found;
    }

    /// <summary>The material a triangle draws with, as the renderer batches it.</summary>
    public static int MaterialOf(GModelMesh mesh, int triangle) =>
        mesh.TriangleMaterialIndices is { } faces && faces.Length == mesh.Indices.Length / 3 && (uint)triangle < (uint)faces.Length
            ? Math.Max(0, faces[triangle])
            : mesh.MaterialIndex;

    /// <summary>
    /// True when at least one triangle covers some texture area. A part without UVs has every
    /// vertex at one texture point: there is nowhere on an image for its strokes to go.
    /// </summary>
    public static bool HasUvs(MeshVertex[] vertices, ushort[] indices)
    {
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            if (indices[i] >= vertices.Length || indices[i + 1] >= vertices.Length || indices[i + 2] >= vertices.Length) continue;
            Vector2 a = vertices[indices[i]].UV, b = vertices[indices[i + 1]].UV, c = vertices[indices[i + 2]].UV;
            float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (float.IsFinite(area) && MathF.Abs(area) > 1e-10f) return true;
        }
        return false;
    }

    /// <summary>The pixel a texture coordinate falls on, repeats wrapped as the renderer samples them (row 0 at the top).</summary>
    public static Point Pixel(Vector2 uv, int width, int height)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        return new Point(
            Math.Clamp((int)MathF.Floor(u * width), 0, width - 1),
            Math.Clamp((int)MathF.Floor(v * height), 0, height - 1));
    }
}

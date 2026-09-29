using System.Numerics;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Editable, UV-linked geometry from the first frame of a saved Image.</summary>
internal static class ModelImageGeometry
{
    public static GModelAsset Build(ImageMaterialPixels image, string reference, float width, float depth, int detail, int opacityCutoff)
    {
        if (!float.IsFinite(width) || !float.IsFinite(depth) || width <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and depth must be positive.");
        if (detail is < 2 or > 64) throw new ArgumentOutOfRangeException(nameof(detail));
        if (opacityCutoff is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(opacityCutoff));
        int longest = Math.Max(image.Width, image.Height);
        int columns = Math.Max(1, Math.Min(image.Width, (int)MathF.Round(detail * image.Width / (float)longest)));
        int rows = Math.Max(1, Math.Min(image.Height, (int)MathF.Round(detail * image.Height / (float)longest)));
        float height = width * image.Height / image.Width;
        float dx = width / columns, dy = height / rows, halfDepth = depth / 2;
        List<MeshVertex> vertices = [];
        List<ushort> indices = [];
        bool Opaque(int x, int y)
        {
            if (x < 0 || y < 0 || x >= columns || y >= rows) return false;
            int px = Math.Min(image.Width - 1, (int)((x + .5f) * image.Width / columns));
            int py = Math.Min(image.Height - 1, (int)((y + .5f) * image.Height / rows));
            return image.Albedo[(py * image.Width + px) * 4 + 3] >= opacityCutoff;
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2? sideUv = null)
        {
            ushort start = checked((ushort)vertices.Count);
            foreach (Vector3 point in new[] { a, b, c, d })
                vertices.Add(new MeshVertex
                {
                    Position = point, Normal = normal,
                    Color = sideUv.HasValue ? new Vector4(.82f, .82f, .82f, 1) : Vector4.One,
                    UV = sideUv ?? new Vector2(point.X / width + .5f, .5f - point.Y / height),
                });
            indices.AddRange([start, (ushort)(start + 2), (ushort)(start + 1),
                start, (ushort)(start + 3), (ushort)(start + 2)]);
        }
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            if (!Opaque(x, y)) continue;
            float left = -width / 2 + x * dx, right = left + dx;
            float top = height / 2 - y * dy, bottom = top - dy;
            Vector2 uv = new((x + .5f) / columns, (y + .5f) / rows);
            Quad(new(left, bottom, halfDepth), new(right, bottom, halfDepth), new(right, top, halfDepth), new(left, top, halfDepth), Vector3.UnitZ);
            Quad(new(right, bottom, -halfDepth), new(left, bottom, -halfDepth), new(left, top, -halfDepth), new(right, top, -halfDepth), -Vector3.UnitZ);
            if (!Opaque(x - 1, y)) Quad(new(left, bottom, -halfDepth), new(left, bottom, halfDepth), new(left, top, halfDepth), new(left, top, -halfDepth), -Vector3.UnitX, uv);
            if (!Opaque(x + 1, y)) Quad(new(right, bottom, halfDepth), new(right, bottom, -halfDepth), new(right, top, -halfDepth), new(right, top, halfDepth), Vector3.UnitX, uv);
            if (!Opaque(x, y - 1)) Quad(new(left, top, halfDepth), new(right, top, halfDepth), new(right, top, -halfDepth), new(left, top, -halfDepth), Vector3.UnitY, uv);
            if (!Opaque(x, y + 1)) Quad(new(left, bottom, -halfDepth), new(right, bottom, -halfDepth), new(right, bottom, halfDepth), new(left, bottom, halfDepth), -Vector3.UnitY, uv);
        }
        if (vertices.Count == 0)
            throw new InvalidOperationException("No pixels remain. Lower minimum opacity, increase detail or choose another Image.");
        string name = Path.GetFileNameWithoutExtension(reference);
        GModelAsset result = new()
        {
            Name = name,
            Materials = [new GModelMaterial { Name = name + " Image", AlbedoTexture = reference, DoubleSided = true }],
            Meshes = [new GModelMesh
            {
                Name = name, Vertices = vertices.ToArray(), Indices = indices.ToArray(), SmoothShading = false,
                Metadata = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["genesis.sourceImage"] = reference,
                    ["genesis.imageOpacityCutoff"] = opacityCutoff.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            }],
        };
        result.RecalculateBounds();
        return result;
    }
}

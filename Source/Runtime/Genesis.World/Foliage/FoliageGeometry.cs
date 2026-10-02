using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.World.Foliage;

/// <summary>Small backend-neutral near/far meshes shared by every foliage instance.</summary>
public static class FoliageGeometry
{
    public static int TriangleCount(FoliageSpecies species, bool nearLod) => species switch
    {
        FoliageSpecies.Sapling => nearLod ? 40 : 16,
        FoliageSpecies.Shrub => nearLod ? 40 : 8,
        FoliageSpecies.Fern => nearLod ? 14 : 8,
        FoliageSpecies.Wildflower => nearLod ? 21 : 10,
        FoliageSpecies.Reed => nearLod ? 21 : 3,
        FoliageSpecies.TallGrass => nearLod ? 24 : 3,
        _ => nearLod ? 21 : 3,
    };

    public static MeshData Build(FoliageSpecies species, bool nearLod)
    {
        Vector4 color = SpeciesColor(species);
        return species switch
        {
            FoliageSpecies.Sapling => BuildSapling(nearLod, color),
            FoliageSpecies.Shrub => BuildShrub(nearLod, color),
            FoliageSpecies.Fern => BuildFern(nearLod, color),
            FoliageSpecies.Wildflower => BuildWildflowers(nearLod, color),
            FoliageSpecies.Reed => BuildBlades(nearLod ? 7 : 3, 1.35f, color, nearLod),
            FoliageSpecies.TallGrass => BuildBlades(nearLod ? 8 : 3, 0.9f, color, nearLod),
            _ => BuildBlades(nearLod ? 7 : 3, 0.72f, color, nearLod),
        };
    }

    /// <summary>
    /// A tuft of grass. Near the camera each blade is a tapering strip that bends outward towards
    /// its tip; far away it is one triangle. Every blade is dark at the root and lighter at the
    /// tip, as a real tuft is where its own blades shade it. It used to be flat-coloured straight
    /// spikes, which stood out of a meadow like nails.
    /// </summary>
    private static MeshData BuildBlades(int count, float height, Vector4 color, bool near)
    {
        var vertices = new List<MeshVertex>(count * (near ? 5 : 3));
        var indices = new List<ushort>(count * (near ? 9 : 3));
        Vector4 dark = Shade(color, 0.52f), middle = Shade(color, 0.82f), light = Shade(color, 1.06f);
        for (int blade = 0; blade < count; blade++)
        {
            float angle = blade * 2.3999632f;
            float radius = 0.07f + 0.17f * MathF.Sqrt((blade + 1f) / count);
            Vector3 outward = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            Vector3 root = outward * radius;
            Vector3 side = new(-MathF.Sin(angle), 0f, MathF.Cos(angle));
            float half = 0.035f + (blade % 3) * 0.008f;
            float h = height * (0.78f + (blade % 4) * 0.065f);
            // Leaning the normal up lets a blade take the sky's light instead of going black edge-on.
            Vector3 normal = Vector3.Normalize(outward + Vector3.UnitY * 0.7f);
            ushort start = checked((ushort)vertices.Count);
            if (!near)
            {
                AddVertex(vertices, root - side * half, normal, dark, 0f, 1f);
                AddVertex(vertices, root + side * half, normal, dark, 1f, 1f);
                AddVertex(vertices, root + Vector3.UnitY * h + outward * h * 0.12f, normal, light, 0.5f, 0f);
                indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
                continue;
            }

            Vector3 bend = root + Vector3.UnitY * h * 0.55f + outward * h * 0.05f;
            Vector3 tip = root + Vector3.UnitY * h + outward * h * 0.22f;
            AddVertex(vertices, root - side * half, normal, dark, 0f, 1f);
            AddVertex(vertices, root + side * half, normal, dark, 1f, 1f);
            AddVertex(vertices, bend + side * half * 0.62f, normal, middle, 1f, 0.45f);
            AddVertex(vertices, bend - side * half * 0.62f, normal, middle, 0f, 0.45f);
            AddVertex(vertices, tip, normal, light, 0.5f, 0f);
            indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
            indices.Add(start); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 3));
            indices.Add((ushort)(start + 3)); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 4));
        }
        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static Vector4 Shade(Vector4 color, float amount) =>
        new(MathF.Min(1f, color.X * amount), MathF.Min(1f, color.Y * amount), MathF.Min(1f, color.Z * amount), color.W);

    private static void AddVertex(List<MeshVertex> vertices, Vector3 position, Vector3 normal, Vector4 color, float u, float v) =>
        vertices.Add(new MeshVertex { Position = position, Normal = normal, Color = color, UV = new Vector2(u, v) });

    private static MeshData BuildFern(bool near, Vector4 color)
    {
        int fronds = near ? 7 : 4;
        var vertices = new List<MeshVertex>(fronds * 4);
        var indices = new List<ushort>(fronds * 6);
        for (int frond = 0; frond < fronds; frond++)
        {
            float angle = frond * MathF.Tau / fronds;
            Vector3 direction = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            Vector3 side = new(-direction.Z, 0f, direction.X);
            float reach = 0.48f + (frond % 3) * 0.06f;
            float rise = 0.34f + (frond % 2) * 0.09f;
            Vector3 root = direction * 0.04f + Vector3.UnitY * 0.04f;
            Vector3 middle = direction * reach * 0.58f + Vector3.UnitY * (rise + 0.16f);
            Vector3 tip = direction * reach + Vector3.UnitY * rise;
            AddQuad(vertices, indices,
                root,
                middle + side * 0.105f,
                tip,
                middle - side * 0.105f,
                Vector3.UnitY,
                color);
        }
        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    /// <summary>
    /// A bush as a mass of leaves: one rounded clump with a smaller one on its shoulder. It used
    /// to be a handful of flat diamonds, which read as paper cut-outs beside a modelled tree.
    /// </summary>
    private static MeshData BuildShrub(bool near, Vector4 color)
    {
        var vertices = new List<MeshVertex>(24);
        var indices = new List<ushort>(120);
        if (near)
        {
            AddClump(vertices, indices, new Vector3(0f, 0.42f, 0f), new Vector3(0.55f, 0.42f, 0.50f), fine: true, color, 3);
            AddClump(vertices, indices, new Vector3(0.22f, 0.64f, -0.12f), new Vector3(0.36f, 0.30f, 0.34f), fine: true, color, 7);
        }
        else
        {
            AddClump(vertices, indices, new Vector3(0f, 0.46f, 0f), new Vector3(0.58f, 0.48f, 0.54f), fine: false, color, 3);
        }

        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static readonly Vector3[] ClumpFine = BuildIcosahedron();
    private static readonly ushort[] ClumpFineFaces =
    [
        0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
        3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
    ];
    private static readonly Vector3[] ClumpCoarse =
    [
        Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ,
    ];
    private static readonly ushort[] ClumpCoarseFaces =
    [
        0, 4, 2, 0, 2, 5, 0, 5, 3, 0, 3, 4, 1, 2, 4, 1, 5, 2, 1, 3, 5, 1, 4, 3,
    ];

    private static Vector3[] BuildIcosahedron()
    {
        float t = (1f + MathF.Sqrt(5f)) * 0.5f;
        Vector3[] points =
        [
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        ];
        for (int i = 0; i < points.Length; i++) points[i] = Vector3.Normalize(points[i]);
        return points;
    }

    /// <summary>
    /// A rounded mass of leaves: twenty faces near, eight far, each corner pushed in or out a
    /// little so no two clumps look turned on a lathe. It is darker underneath, where a real bush
    /// shades itself, and its normals lean upward so the top catches the sky.
    /// </summary>
    private static void AddClump(List<MeshVertex> vertices, List<ushort> indices, Vector3 centre, Vector3 radii,
        bool fine, Vector4 color, int pattern)
    {
        Vector3[] points = fine ? ClumpFine : ClumpCoarse;
        ushort[] faces = fine ? ClumpFineFaces : ClumpCoarseFaces;
        ushort start = checked((ushort)vertices.Count);
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 point = points[i];
            // A fixed scramble of the corner's number: the same clump every time it is built.
            uint hash = unchecked((uint)(i * 374761393 + pattern * 668265263));
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            float wobble = 0.86f + ((hash >> 8) & 0xFFFF) / 65535f * 0.28f;
            float shade = 0.70f + 0.36f * (point.Y * 0.5f + 0.5f);
            vertices.Add(new MeshVertex
            {
                Position = centre + point * radii * wobble,
                Normal = Vector3.Normalize(point + Vector3.UnitY * 0.35f),
                Color = new Vector4(color.X * shade, color.Y * shade, color.Z * shade, color.W),
                UV = new Vector2(point.X * 0.5f + 0.5f, 0.5f - point.Y * 0.5f),
            });
        }

        foreach (ushort corner in faces) indices.Add((ushort)(start + corner));
    }

    /// <summary>
    /// A few flowers on thin stems. Each head is a shallow cup of petals round a yellow centre,
    /// tipped away from the middle of the plant so it shows from the side as well as from above.
    /// The heads used to be two crossed diamonds, which read as flat pink kites.
    /// </summary>
    private static MeshData BuildWildflowers(bool near, Vector4 flowerColor)
    {
        int flowers = near ? 3 : 2;
        int petals = near ? 5 : 4;
        var vertices = new List<MeshVertex>(flowers * (petals + 7));
        var indices = new List<ushort>(flowers * (petals + 2) * 3);
        Vector4 stemColor = new(0.20f, 0.46f, 0.16f, 1f);
        Vector4 heart = new(0.95f, 0.80f, 0.26f, flowerColor.W);
        for (int flower = 0; flower < flowers; flower++)
        {
            float angle = flower * 2.3999632f;
            Vector3 outward = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            Vector3 root = outward * 0.15f;
            Vector3 side = new(-MathF.Sin(angle), 0f, MathF.Cos(angle));
            float height = 0.48f + 0.09f * flower;
            Vector3 crown = root + Vector3.UnitY * height + outward * 0.04f;
            AddTriangle(vertices, indices, root - side * 0.018f, root + side * 0.018f,
                crown, Vector3.Normalize(outward + Vector3.UnitY * 0.7f), stemColor);
            if (near)
            {
                // One leaf low on the stem.
                Vector3 joint = root + Vector3.UnitY * height * 0.3f;
                AddTriangle(vertices, indices, joint, joint + side * 0.11f + Vector3.UnitY * 0.05f,
                    joint + side * 0.05f + outward * 0.05f + Vector3.UnitY * 0.01f, Vector3.UnitY, Shade(stemColor, 1.15f));
            }

            Vector3 facing = Vector3.Normalize(Vector3.UnitY + outward * 0.6f);
            Vector3 across = Vector3.Normalize(Vector3.Cross(facing, side));
            Vector3 along = Vector3.Cross(across, facing);
            const float reach = 0.115f;
            ushort centre = checked((ushort)vertices.Count);
            AddVertex(vertices, crown, facing, heart, 0.5f, 0.5f);
            for (int petal = 0; petal < petals; petal++)
            {
                float turn = petal * MathF.Tau / petals + flower * 0.7f;
                Vector3 offset = across * MathF.Cos(turn) + along * MathF.Sin(turn);
                AddVertex(vertices, crown + offset * reach + facing * reach * 0.28f, facing, flowerColor,
                    0.5f + MathF.Cos(turn) * 0.5f, 0.5f + MathF.Sin(turn) * 0.5f);
            }

            for (int petal = 0; petal < petals; petal++)
            {
                indices.Add(centre);
                indices.Add((ushort)(centre + 1 + petal));
                indices.Add((ushort)(centre + 1 + (petal + 1) % petals));
            }
        }
        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static MeshData BuildRadialCards(int cards, float height, Vector4 color)
    {
        var vertices = new List<MeshVertex>(cards * 4);
        var indices = new List<ushort>(cards * 6);
        for (int card = 0; card < cards; card++)
        {
            float angle = card * MathF.PI / cards;
            Vector3 side = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            Vector3 normal = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, side));
            AddQuad(vertices, indices, -side * 0.48f, side * 0.48f,
                side * 0.32f + Vector3.UnitY * height, -side * 0.32f + Vector3.UnitY * height,
                normal, color);
        }
        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static MeshData BuildSapling(bool near, Vector4 leafColor)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<ushort>();
        int sides = near ? 6 : 4;
        Vector4 bark = new(0.28f, 0.18f, 0.09f, 1f);
        for (int sideIndex = 0; sideIndex < sides; sideIndex++)
        {
            float a0 = sideIndex * MathF.Tau / sides, a1 = (sideIndex + 1) * MathF.Tau / sides;
            Vector3 p0 = new(MathF.Cos(a0) * 0.08f, 0f, MathF.Sin(a0) * 0.08f);
            Vector3 p1 = new(MathF.Cos(a1) * 0.08f, 0f, MathF.Sin(a1) * 0.08f);
            Vector3 p2 = new(MathF.Cos(a1) * 0.045f, 1.45f, MathF.Sin(a1) * 0.045f);
            Vector3 p3 = new(MathF.Cos(a0) * 0.045f, 1.45f, MathF.Sin(a0) * 0.045f);
            Vector3 normal = Vector3.Normalize(new Vector3(MathF.Cos((a0 + a1) * 0.5f), 0f, MathF.Sin((a0 + a1) * 0.5f)));
            AddQuad(vertices, indices, p0, p1, p2, p3, normal, bark);
        }
        // A young tree's crown: one rounded mass of leaves and a smaller one above it.
        if (near)
        {
            AddClump(vertices, indices, new Vector3(0f, 1.62f, 0f), new Vector3(0.50f, 0.46f, 0.48f), fine: true, leafColor, 11);
            AddClump(vertices, indices, new Vector3(0.08f, 2.04f, -0.05f), new Vector3(0.30f, 0.30f, 0.28f), fine: false, leafColor, 13);
        }
        else
        {
            AddClump(vertices, indices, new Vector3(0f, 1.72f, 0f), new Vector3(0.50f, 0.56f, 0.48f), fine: false, leafColor, 11);
        }

        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static void AddLeafDiamond(
        List<MeshVertex> vertices,
        List<ushort> indices,
        Vector3 center,
        Vector3 side,
        float width,
        float height,
        Vector4 color)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, side));
        AddQuad(vertices, indices,
            center - Vector3.UnitY * height * 0.5f,
            center + side * width * 0.5f,
            center + Vector3.UnitY * height * 0.5f,
            center - side * width * 0.5f,
            normal,
            color);
    }

    private static void AddTriangle(
        List<MeshVertex> vertices,
        List<ushort> indices,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 normal,
        Vector4 color)
    {
        ushort start = checked((ushort)vertices.Count);
        vertices.Add(new MeshVertex { Position = a, Normal = normal, Color = color, UV = new Vector2(0f, 1f) });
        vertices.Add(new MeshVertex { Position = b, Normal = normal, Color = color, UV = new Vector2(1f, 1f) });
        vertices.Add(new MeshVertex { Position = c, Normal = normal, Color = color, UV = new Vector2(0.5f, 0f) });
        indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
    }

    private static void AddQuad(
        List<MeshVertex> vertices, List<ushort> indices,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector4 color)
    {
        ushort start = checked((ushort)vertices.Count);
        vertices.Add(new MeshVertex { Position = a, Normal = normal, Color = color, UV = new Vector2(0f, 1f) });
        vertices.Add(new MeshVertex { Position = b, Normal = normal, Color = color, UV = new Vector2(1f, 1f) });
        vertices.Add(new MeshVertex { Position = c, Normal = normal, Color = color, UV = new Vector2(1f, 0f) });
        vertices.Add(new MeshVertex { Position = d, Normal = normal, Color = color, UV = new Vector2(0f, 0f) });
        indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
        indices.Add(start); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 3));
    }

    private static Vector4 SpeciesColor(FoliageSpecies species) => species switch
    {
        FoliageSpecies.MeadowGrass => new(0.29f, 0.58f, 0.20f, 1f),
        FoliageSpecies.TallGrass => new(0.30f, 0.50f, 0.17f, 1f),
        FoliageSpecies.Fern => new(0.16f, 0.42f, 0.18f, 1f),
        FoliageSpecies.Shrub => new(0.20f, 0.38f, 0.13f, 1f),
        FoliageSpecies.Sapling => new(0.18f, 0.48f, 0.16f, 1f),
        FoliageSpecies.Wildflower => new(0.72f, 0.38f, 0.66f, 1f),
        FoliageSpecies.Reed => new(0.42f, 0.57f, 0.23f, 1f),
        _ => Vector4.One,
    };
}

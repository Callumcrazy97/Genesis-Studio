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
        FoliageSpecies.Sapling => nearLod ? 28 : 16,
        FoliageSpecies.Shrub => nearLod ? 22 : 10,
        FoliageSpecies.Fern => nearLod ? 14 : 8,
        FoliageSpecies.Wildflower => nearLod ? 15 : 10,
        FoliageSpecies.Reed => nearLod ? 7 : 3,
        FoliageSpecies.TallGrass => nearLod ? 8 : 3,
        _ => nearLod ? 7 : 3,
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
            FoliageSpecies.Reed => BuildBlades(nearLod ? 7 : 3, 1.35f, color),
            FoliageSpecies.TallGrass => BuildBlades(nearLod ? 8 : 3, 1.05f, color),
            _ => BuildBlades(nearLod ? 7 : 3, 0.72f, color),
        };
    }

    private static MeshData BuildBlades(int count, float height, Vector4 color)
    {
        var vertices = new List<MeshVertex>(count * 3);
        var indices = new List<ushort>(count * 3);
        for (int blade = 0; blade < count; blade++)
        {
            float angle = blade * 2.3999632f;
            float radius = 0.07f + 0.17f * MathF.Sqrt((blade + 1f) / count);
            Vector3 root = new(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            Vector3 side = new(-MathF.Sin(angle), 0f, MathF.Cos(angle));
            float half = 0.035f + (blade % 3) * 0.008f;
            float h = height * (0.78f + (blade % 4) * 0.065f);
            AddTriangle(vertices, indices, root - side * half, root + side * half,
                root + Vector3.UnitY * h + side * half * 0.08f,
                Vector3.Normalize(Vector3.Cross(Vector3.UnitY, side)), color);
        }
        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

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

    private static MeshData BuildShrub(bool near, Vector4 color)
    {
        int leaves = near ? 11 : 5;
        var vertices = new List<MeshVertex>(leaves * 4);
        var indices = new List<ushort>(leaves * 6);
        for (int leaf = 0; leaf < leaves; leaf++)
        {
            float angle = leaf * 2.3999632f;
            float ring = 0.15f + 0.20f * (leaf % 3);
            Vector3 radial = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            Vector3 center = radial * ring + Vector3.UnitY * (0.30f + 0.16f * (leaf % 4));
            Vector3 side = new(-radial.Z, 0f, radial.X);
            AddLeafDiamond(vertices, indices, center, side,
                0.30f + 0.035f * (leaf % 3), 0.38f + 0.04f * (leaf % 2), color);
        }
        return new MeshData { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
    }

    private static MeshData BuildWildflowers(bool near, Vector4 flowerColor)
    {
        int flowers = near ? 3 : 2;
        var vertices = new List<MeshVertex>(flowers * 11);
        var indices = new List<ushort>(flowers * 15);
        Vector4 stemColor = new(0.20f, 0.46f, 0.16f, 1f);
        for (int flower = 0; flower < flowers; flower++)
        {
            float angle = flower * 2.3999632f;
            Vector3 root = new(MathF.Cos(angle) * 0.15f, 0f, MathF.Sin(angle) * 0.15f);
            Vector3 side = new(-MathF.Sin(angle), 0f, MathF.Cos(angle));
            float height = 0.48f + 0.09f * flower;
            Vector3 crown = root + Vector3.UnitY * height;
            AddTriangle(vertices, indices, root - side * 0.018f, root + side * 0.018f,
                crown, Vector3.Normalize(Vector3.Cross(Vector3.UnitY, side)), stemColor);
            AddLeafDiamond(vertices, indices, crown, side, 0.18f, 0.16f, flowerColor);
            AddLeafDiamond(vertices, indices, crown, Vector3.Normalize(Vector3.Cross(side, Vector3.UnitY)),
                0.18f, 0.16f, flowerColor);
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
        int leaves = near ? 8 : 4;
        for (int leaf = 0; leaf < leaves; leaf++)
        {
            float angle = leaf * 2.3999632f;
            Vector3 radial = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
            Vector3 side = new(-radial.Z, 0f, radial.X);
            Vector3 center = radial * (0.18f + 0.15f * (leaf % 3))
                + Vector3.UnitY * (1.28f + 0.23f * (leaf % 4));
            AddLeafDiamond(vertices, indices, center, side,
                0.42f + 0.05f * (leaf % 2), 0.52f, leafColor);
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
        FoliageSpecies.TallGrass => new(0.34f, 0.55f, 0.18f, 1f),
        FoliageSpecies.Fern => new(0.16f, 0.42f, 0.18f, 1f),
        FoliageSpecies.Shrub => new(0.20f, 0.38f, 0.13f, 1f),
        FoliageSpecies.Sapling => new(0.18f, 0.48f, 0.16f, 1f),
        FoliageSpecies.Wildflower => new(0.72f, 0.38f, 0.66f, 1f),
        FoliageSpecies.Reed => new(0.42f, 0.57f, 0.23f, 1f),
        _ => Vector4.One,
    };
}

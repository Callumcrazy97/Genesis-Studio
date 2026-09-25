#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Genesis.Runtime.Navigation;

/// <summary>One convex quad per walkable heightfield cell; adjacency is derived from shared edges.</summary>
public sealed class NavMeshData
{
    public int Version { get; set; } = 1;
    public int Width { get; set; }
    public int Depth { get; set; }
    public float OriginX { get; set; }
    public float OriginZ { get; set; }
    public float CellSize { get; set; } = 1;
    public float MaxStep { get; set; } = .45f;
    public float MaxSlopeDegrees { get; set; } = 45;
    public float[] Heights { get; set; } = [];
    public bool[] Walkable { get; set; } = [];
    public int Count => Width * Depth;
    public void Validate()
    {
        if (Version != 1 || Width <= 0 || Depth <= 0 || (long)Width * Depth > 1_048_576
            || Heights == null || Walkable == null || Heights.Length != Count || Walkable.Length != Count || !float.IsFinite(CellSize) || CellSize <= 0
            || !float.IsFinite(OriginX) || !float.IsFinite(OriginZ) || !float.IsFinite(MaxStep) || MaxStep < 0
            || !float.IsFinite(MaxSlopeDegrees) || MaxSlopeDegrees < 0 || MaxSlopeDegrees >= 90)
            throw new InvalidDataException("Invalid or oversized navigation mesh.");
        foreach (float height in Heights) if (!float.IsFinite(height)) throw new InvalidDataException("Invalid navigation height.");
    }
    public Vector3 Center(int cell) => new(OriginX + (cell % Width + .5f) * CellSize,
        Heights[cell], OriginZ + (cell / Width + .5f) * CellSize);
    public int Cell(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return -1;
        float x = (position.X - OriginX) / CellSize, z = (position.Z - OriginZ) / CellSize;
        return x < 0 || z < 0 || x >= Width || z >= Depth ? -1 : (int)z * Width + (int)x;
    }
    public IEnumerable<int> Neighbors(int cell)
    {
        int x = cell % Width, z = cell / Width;
        if (x > 0 && Connected(cell, cell - 1)) yield return cell - 1;
        if (x + 1 < Width && Connected(cell, cell + 1)) yield return cell + 1;
        if (z > 0 && Connected(cell, cell - Width)) yield return cell - Width;
        if (z + 1 < Depth && Connected(cell, cell + Width)) yield return cell + Width;
    }
    public bool Connected(int a, int b) => a >= 0 && b >= 0 && a < Count && b < Count && Walkable[a] && Walkable[b]
        && Math.Abs(Heights[a] - Heights[b]) <= MaxStep + CellSize * MathF.Tan(MaxSlopeDegrees * MathF.PI / 180);
    public void Save(string path)
    {
        Validate();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static NavMeshData Load(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Navigation sidecar is too large.");
        var data = JsonSerializer.Deserialize<NavMeshData>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty navmesh.");
        data.Validate(); return data;
    }
}

public readonly record struct NavigationObstacle(Vector3 Min, Vector3 Max);

public static class NavMeshBuilder
{
    public static NavMeshData Build(float originX, float originZ, int width, int depth, float cellSize,
        Func<float, float, float> sampleHeight, IReadOnlyList<NavigationObstacle>? obstacles = null,
        float agentRadius = .35f, float agentHeight = 1.8f, float maxStep = .45f, float maxSlope = 45)
    {
        if (width <= 0 || depth <= 0 || (long)width * depth > 1_048_576 || !float.IsFinite(cellSize) || cellSize <= 0
            || !float.IsFinite(agentRadius) || agentRadius < 0 || !float.IsFinite(agentHeight) || agentHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid navigation bounds or agent dimensions.");
        var data = new NavMeshData { OriginX = originX, OriginZ = originZ, Width = width, Depth = depth,
            CellSize = cellSize, MaxStep = maxStep, MaxSlopeDegrees = maxSlope,
            Heights = new float[width * depth], Walkable = new bool[width * depth] };
        data.Validate();
        float gradientLimit = MathF.Tan(maxSlope * MathF.PI / 180);
        for (int i = 0; i < data.Count; i++)
        {
            Vector3 p = data.Center(i);
            float h = sampleHeight(p.X, p.Z);
            float half = cellSize / 2;
            float a = sampleHeight(p.X - half, p.Z), b = sampleHeight(p.X + half, p.Z);
            float c = sampleHeight(p.X, p.Z - half), d = sampleHeight(p.X, p.Z + half);
            bool valid = float.IsFinite(h) && float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c) && float.IsFinite(d);
            data.Heights[i] = valid ? h : 0;
            float gradient = MathF.Sqrt((b - a) * (b - a) + (d - c) * (d - c)) / cellSize;
            bool walkable = valid && gradient <= gradientLimit;
            if (walkable && obstacles != null)
                foreach (NavigationObstacle obstacle in obstacles)
                    if (p.X + half + agentRadius >= obstacle.Min.X && p.X - half - agentRadius <= obstacle.Max.X
                        && p.Z + half + agentRadius >= obstacle.Min.Z && p.Z - half - agentRadius <= obstacle.Max.Z
                        && h + agentHeight > obstacle.Min.Y && h + maxStep < obstacle.Max.Y) { walkable = false; break; }
            data.Walkable[i] = walkable;
        }
        return data;
    }
}

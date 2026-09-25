using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Terrain;

public enum TerrainCreationSource { Region, Lasso, Heightmap, Code }
public enum TerrainCodeSurface { Heightfield, Volume }

public sealed class TerrainCreationRecipe
{
    public string Name { get; set; } = "Terrain section";
    public TerrainCreationSource Source { get; set; }
    public TerrainCodeSurface Surface { get; set; }
    public float Width { get; set; } = 1000;
    public float Length { get; set; } = 1000;
    public float Spacing { get; set; } = 8;
    public float MinHeight { get; set; } = -100;
    public float MaxHeight { get; set; } = 200;
    public int Seed { get; set; } = 1337;
    public string Image { get; set; } = "";
    public string Code { get; set; } = HillsCode;
    public float[][] Boundary { get; set; } = [];
    public const string HillsCode = "// Coordinates x,z are metres from the centre. u,v are 0..1.\n// Optional: TerrainSize(4000, 4000); TerrainSpacing(16);\nheight = 60 * noise(x * 0.008, z * 0.008)\n       + 14 * sin(x * 0.025);";
    public const string RavineCode = "TerrainSize(2000, 2000);\nTerrainSpacing(8);\nriver = z - 80 * sin(x * 0.004);\nheight = 50 + 25 * noise(x * 0.006, z * 0.006)\n       - 80 * exp(-river * river / 1600);";
    public const string CaveCode = "// Density < 0 is solid. A tunnel cuts through the hillside.\nTerrainSize(160, 160);\nTerrainSpacing(4);\nhill = 32 + 8 * noise(x * 0.03, z * 0.03);\ntunnel = 12 - sqrt((y - 12) * (y - 12) + z * z);\ndensity = max(y - hill, tunnel);";
}

public sealed record TerrainCreationResult(TerrainCreationRecipe Recipe, GModelAsset Model, byte[] Preview, int PreviewSize, string Summary)
{
    public float[,]? Heights { get; init; }
}

internal sealed class TerrainSectionMeshBuilder
{
    public GModelAsset Model { get; } = new() { Name = "Terrain section", Materials = [new GModelMaterial { Name = "Untextured terrain", BaseColor = new Vector4(.72f, .74f, .76f, 1) }] };
    private readonly List<MeshVertex> _vertices = [];
    private readonly List<ushort> _indices = [];
    private int _triangles;
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, float width, float length)
    {
        if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12f) return;
        if (++_triangles > 600000) throw new InvalidOperationException("This terrain exceeds 600,000 triangles. Increase sample spacing or reduce the volume.");
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc) < 0) { (b, c) = (c, b); (nb, nc) = (nc, nb); }
        if (_vertices.Count > 60000) Flush();
        foreach (var pair in new[] { (a, na), (b, nb), (c, nc) })
        {
            _indices.Add((ushort)_vertices.Count);
            _vertices.Add(new MeshVertex { Position = pair.Item1, Normal = pair.Item2, Color = Vector4.One,
                UV = new Vector2(pair.Item1.X / Math.Max(width, .01f) + .5f, pair.Item1.Z / Math.Max(length, .01f) + .5f) });
        }
    }
    public GModelAsset Finish(string name)
    {
        Flush(); Model.Name = name; Model.RecalculateBounds();
        if (Model.Meshes.Count == 0) throw new InvalidOperationException("The recipe produced no surface. Check the height range or density expression.");
        return Model;
    }
    private void Flush()
    {
        if (_vertices.Count == 0) return;
        Model.Meshes.Add(new GModelMesh { Name = "Terrain chunk " + Model.Meshes.Count, Vertices = _vertices.ToArray(), Indices = _indices.ToArray() });
        _vertices.Clear(); _indices.Clear();
    }
}

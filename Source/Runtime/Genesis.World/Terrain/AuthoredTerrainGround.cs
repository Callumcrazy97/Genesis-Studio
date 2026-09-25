using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.World.Terrain;

/// <summary>
/// Renders a <see cref="TerrainAsset"/> as chunked painted heightfield meshes. Chunking is required
/// because Genesis mesh indices are 16-bit: a 513×513 terrain contains 263,169 vertices and cannot
/// be represented by one mesh. Each vertex normally carries the authored RGBA splat weights so the
/// terrain shader can reproduce grass, path, rock and biome paint in Studio and F5.
/// </summary>
public sealed class AuthoredTerrainGround : IDisposable
{
    private const int MaximumChunkCells = 128;
    private readonly TerrainAsset _asset;
    private IRenderController _render;
    private readonly List<MeshHandle> _meshes = [];
    private TextureHandle _albedo = TextureHandle.Invalid;
    private float _uvScale = 6f;
    private bool _biome;
    public MeshDrawCall? SurfaceMaterial { get; set; }

    public AuthoredTerrainGround(TerrainAsset asset) => _asset = asset ?? throw new ArgumentNullException(nameof(asset));

    public TerrainAsset Asset => _asset;

    public int MeshCount => _meshes.Count;

    /// <summary>
    /// When true, the next <see cref="RebuildMesh"/> bakes normalised elevation into vertex Color.r
    /// and the draw is submitted with the TerrainGround flag so the shader applies biome blending.
    /// </summary>
    public bool BiomeShading { get => _biome; set => _biome = value; }

    public float SampleHeight(float wx, float wz) => _asset.SampleHeight(wx, wz);

    public void Bind(IRenderController render, TextureHandle albedo, float uvScale = 6f)
    {
        _render = render;
        _albedo = albedo;
        _uvScale = uvScale;
        RebuildMesh();
    }

    public void RebuildMesh()
    {
        if (_render == null) return;
        ReleaseMeshes();

        int resX = _asset.ResolutionX;
        int resZ = _asset.ResolutionZ;
        int cellsX = Math.Max(0, resX - 1);
        int cellsZ = Math.Max(0, resZ - 1);
        for (int startZ = 0; startZ < cellsZ; startZ += MaximumChunkCells)
        {
            int chunkCellsZ = Math.Min(MaximumChunkCells, cellsZ - startZ);
            for (int startX = 0; startX < cellsX; startX += MaximumChunkCells)
            {
                int chunkCellsX = Math.Min(MaximumChunkCells, cellsX - startX);
                _meshes.Add(BuildChunk(startX, startZ, chunkCellsX, chunkCellsZ));
            }
        }
    }

    private MeshHandle BuildChunk(int startX, int startZ, int cellsX, int cellsZ)
    {
        int pointsX = cellsX + 1;
        int pointsZ = cellsZ + 1;
        var verts = new MeshVertex[pointsX * pointsZ];
        var inds = new ushort[cellsX * cellsZ * 6];

        for (int localZ = 0; localZ < pointsZ; localZ++)
        {
            int z = startZ + localZ;
            for (int localX = 0; localX < pointsX; localX++)
            {
                int x = startX + localX;
                float wx = _asset.OriginX + x * _asset.CellSize;
                float wz = _asset.OriginZ + z * _asset.CellSize;
                float wy = _asset.GetHeight(x, z);
                float u = x / (float)Math.Max(1, _asset.ResolutionX - 1) * _uvScale;
                float v = z / (float)Math.Max(1, _asset.ResolutionZ - 1) * _uvScale;
                (byte r, byte g, byte b, byte a) = _asset.GetSplat(x, z);
                Vector4 color;
                if (_biome)
                {
                    float elevation = (wy - _asset.MinHeight) / Math.Max(0.0001f, _asset.MaxHeight - _asset.MinHeight);
                    color = new Vector4(Math.Clamp(elevation, 0f, 1f), 0f, 0f, 0f);
                }
                else
                {
                    const float byteToUnit = 1f / 255f;
                    color = new Vector4(r * byteToUnit, g * byteToUnit, b * byteToUnit, a * byteToUnit);
                    float weight = color.X + color.Y + color.Z + color.W;
                    color = weight > 0.0001f ? color / weight : new Vector4(1f, 0f, 0f, 0f);
                }

                if (SurfaceMaterial.HasValue) color = Vector4.One;
                verts[localZ * pointsX + localX] = new MeshVertex
                {
                    Position = new Vector3(wx, wy, wz),
                    Normal = ComputeNormal(x, z),
                    Color = color,
                    UV = new Vector2(u, v),
                };
            }
        }

        int idx = 0;
        for (int z = 0; z < cellsZ; z++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                int i00 = z * pointsX + x;
                int i10 = i00 + 1;
                int i01 = i00 + pointsX;
                int i11 = i01 + 1;
                inds[idx++] = (ushort)i00; inds[idx++] = (ushort)i01; inds[idx++] = (ushort)i10;
                inds[idx++] = (ushort)i10; inds[idx++] = (ushort)i01; inds[idx++] = (ushort)i11;
            }
        }

        return _render.RegisterMesh(verts, inds);
    }

    private Vector3 ComputeNormal(int x, int z)
    {
        float hL = _asset.GetHeight(Math.Max(x - 1, 0), z);
        float hR = _asset.GetHeight(Math.Min(x + 1, _asset.ResolutionX - 1), z);
        float hN = _asset.GetHeight(x, Math.Max(z - 1, 0));
        float hS = _asset.GetHeight(x, Math.Min(z + 1, _asset.ResolutionZ - 1));
        return Vector3.Normalize(new Vector3(hL - hR, _asset.CellSize * 2f, hN - hS));
    }

    public void AppendDrawCalls(MeshDrawCall[] buffer, ref int count, MeshDrawFlags extraFlags = MeshDrawFlags.None)
    {
        foreach (MeshHandle mesh in _meshes)
        {
            if (!mesh.IsValid || count >= buffer.Length) break;
            var draw = SurfaceMaterial ?? new MeshDrawCall
            {
                Mesh = mesh,
                Texture = _albedo,
                World = Matrix4x4.Identity,
                Tint = RenderColor.White,
                Alpha = 1f,
                Flags = MeshDrawFlags.TerrainGround | extraFlags,
            };
            draw.Mesh = mesh;
            draw.Flags |= extraFlags;
            buffer[count++] = draw;
        }
    }

    private void ReleaseMeshes()
    {
        if (_render != null)
            foreach (MeshHandle mesh in _meshes)
                if (mesh.IsValid) _render.ReleaseMesh(mesh);
        _meshes.Clear();
    }

    public void Dispose()
    {
        ReleaseMeshes();
        _render = null;
    }
}

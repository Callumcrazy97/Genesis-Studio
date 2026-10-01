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
    /// <summary>Cell layout of each mesh in <see cref="_meshes"/>, for partial vertex updates.</summary>
    private readonly List<(int StartX, int StartZ, int CellsX, int CellsZ)> _chunks = [];
    private int _builtResolutionX, _builtResolutionZ;
    private TextureHandle _albedo = TextureHandle.Invalid;
    private float _uvScale = 6f;
    private bool _biome;
    private MeshDrawCall? _surfaceMaterial;
    /// <summary>Set for terrains large enough to need level of detail; null draws every chunk.</summary>
    private readonly TerrainLodGround _lod;
    private TerrainLodView _lastView;
    private bool _hasView;

    public MeshDrawCall? SurfaceMaterial
    {
        get => _surfaceMaterial;
        set { _surfaceMaterial = value; if (_lod != null) _lod.SurfaceMaterial = value; }
    }

    public AuthoredTerrainGround(TerrainAsset asset)
    {
        _asset = asset ?? throw new ArgumentNullException(nameof(asset));
        _lod = TerrainLodGround.Applies(asset) ? new TerrainLodGround(asset) : null;
    }

    public TerrainAsset Asset => _asset;

    /// <summary>
    /// Draw calls to reserve. With level of detail this is an upper bound, not a resident count.
    /// </summary>
    public int MeshCount => _lod != null ? (_render != null ? _lod.MaximumDrawCalls : 0) : _meshes.Count;

    /// <summary>True when the terrain is drawn as distance-dependent nodes instead of every chunk.</summary>
    public bool UsesLevelOfDetail => _lod != null;

    /// <summary>Counts from the last level-of-detail selection (all zero for small terrains).</summary>
    public TerrainLodStatistics LodStatistics => _lod?.Statistics ?? default;

    /// <summary>
    /// Waits until a large terrain has uploaded every mesh a view needs. Detail normally streams
    /// in over a few frames; captures and tests call this to see the finished result.
    /// </summary>
    public void SettleLevelOfDetail(in TerrainLodView view, int timeoutMilliseconds = 30000)
    {
        if (_lod == null || _render == null) return;
        _lastView = view;
        _hasView = true;
        _lod.WaitUntilSettled(view, timeoutMilliseconds);
    }

    /// <summary>
    /// When true, the next <see cref="RebuildMesh"/> bakes normalised elevation into vertex Color.r
    /// and the draw is submitted with the TerrainGround flag so the shader applies biome blending.
    /// </summary>
    public bool BiomeShading { get => _biome; set { _biome = value; if (_lod != null) _lod.BiomeShading = value; } }

    public float SampleHeight(float wx, float wz) => _asset.SampleHeight(wx, wz);

    public void Bind(IRenderController render, TextureHandle albedo, float uvScale = 6f)
    {
        _render = render;
        _albedo = albedo;
        _uvScale = uvScale;
        if (_lod != null)
        {
            _lod.SurfaceMaterial = _surfaceMaterial;
            _lod.BiomeShading = _biome;
            _lod.Bind(render, albedo, uvScale);
            return;
        }

        RebuildMesh();
    }

    public void RebuildMesh()
    {
        if (_render == null) return;
        if (_lod != null) { _lod.Invalidate(); return; }
        ReleaseMeshes();

        int resX = _asset.ResolutionX;
        int resZ = _asset.ResolutionZ;
        int cellsX = Math.Max(0, resX - 1);
        int cellsZ = Math.Max(0, resZ - 1);
        _builtResolutionX = resX;
        _builtResolutionZ = resZ;
        for (int startZ = 0; startZ < cellsZ; startZ += MaximumChunkCells)
        {
            int chunkCellsZ = Math.Min(MaximumChunkCells, cellsZ - startZ);
            for (int startX = 0; startX < cellsX; startX += MaximumChunkCells)
            {
                int chunkCellsX = Math.Min(MaximumChunkCells, cellsX - startX);
                _meshes.Add(BuildChunk(startX, startZ, chunkCellsX, chunkCellsZ));
                _chunks.Add((startX, startZ, chunkCellsX, chunkCellsZ));
            }
        }
    }

    /// <summary>
    /// Refreshes only the chunks whose vertices a height/paint edit inside the inclusive sample
    /// rectangle can change (normals read one sample either side). A sculpt dab used to rebuild and
    /// re-register every chunk of the terrain on each mouse move.
    /// </summary>
    public void RebuildRegion(int minX, int minZ, int maxX, int maxZ)
    {
        if (_render == null) return;
        if (_lod != null) { _lod.InvalidateRegion(minX, minZ, maxX, maxZ); return; }
        if (_meshes.Count == 0 || _meshes.Count != _chunks.Count
            || _builtResolutionX != _asset.ResolutionX || _builtResolutionZ != _asset.ResolutionZ)
        {
            RebuildMesh();
            return;
        }
        minX -= 1; minZ -= 1; maxX += 1; maxZ += 1;
        for (int i = 0; i < _chunks.Count; i++)
        {
            var chunk = _chunks[i];
            if (chunk.StartX > maxX || chunk.StartX + chunk.CellsX < minX
                || chunk.StartZ > maxZ || chunk.StartZ + chunk.CellsZ < minZ) continue;
            if (!_meshes[i].IsValid) { RebuildMesh(); return; }
            _render.UpdateMesh(_meshes[i], BuildChunkVertices(chunk.StartX, chunk.StartZ, chunk.CellsX, chunk.CellsZ));
        }
    }

    private MeshHandle BuildChunk(int startX, int startZ, int cellsX, int cellsZ)
    {
        int pointsX = cellsX + 1;
        var verts = BuildChunkVertices(startX, startZ, cellsX, cellsZ);
        var inds = new ushort[cellsX * cellsZ * 6];

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

    private MeshVertex[] BuildChunkVertices(int startX, int startZ, int cellsX, int cellsZ)
    {
        int pointsX = cellsX + 1;
        int pointsZ = cellsZ + 1;
        var verts = new MeshVertex[pointsX * pointsZ];

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

        return verts;
    }

    private Vector3 ComputeNormal(int x, int z)
    {
        float hL = _asset.GetHeight(Math.Max(x - 1, 0), z);
        float hR = _asset.GetHeight(Math.Min(x + 1, _asset.ResolutionX - 1), z);
        float hN = _asset.GetHeight(x, Math.Max(z - 1, 0));
        float hS = _asset.GetHeight(x, Math.Min(z + 1, _asset.ResolutionZ - 1));
        return Vector3.Normalize(new Vector3(hL - hR, _asset.CellSize * 2f, hN - hS));
    }

    /// <summary>
    /// Appends the ground as seen from a camera. Large terrains choose their detail from the view;
    /// small ones ignore it and draw every chunk.
    /// </summary>
    public void AppendDrawCalls(MeshDrawCall[] buffer, ref int count, in TerrainLodView view,
        MeshDrawFlags extraFlags = MeshDrawFlags.None)
    {
        _lastView = view;
        _hasView = true;
        AppendDrawCalls(buffer, ref count, extraFlags);
    }

    public void AppendDrawCalls(MeshDrawCall[] buffer, ref int count, MeshDrawFlags extraFlags = MeshDrawFlags.None)
    {
        if (_lod != null)
        {
            // Without a camera the terrain is seen as from far above its centre: coarse and complete.
            TerrainLodView view = _hasView ? _lastView : TerrainLodView.From(new Vector3(
                _asset.OriginX + (_asset.ResolutionX - 1) * _asset.CellSize * 0.5f,
                _asset.MaxHeight + (_asset.ResolutionX + _asset.ResolutionZ) * _asset.CellSize,
                _asset.OriginZ + (_asset.ResolutionZ - 1) * _asset.CellSize * 0.5f));
            _lod.AppendDrawCalls(view, buffer, ref count, extraFlags);
            return;
        }

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
        _chunks.Clear();
    }

    public void Dispose()
    {
        ReleaseMeshes();
        _lod?.Dispose();
        _render = null;
    }
}

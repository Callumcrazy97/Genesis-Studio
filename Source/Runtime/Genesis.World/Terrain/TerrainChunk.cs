using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Genesis.Shared.Interfaces;

namespace Genesis.World.Terrain;

/// <summary>
/// A single renderable and collidable chunk of the TerrainAsset.
/// Extracts a sub-grid of heights to build meshes.
/// </summary>
public sealed class TerrainChunk : IDisposable
{
    private readonly TerrainAsset _asset;
    private readonly int _startX;
    private readonly int _startZ;
    private readonly int _gridPointsX;
    private readonly int _gridPointsZ;

    private IRenderController _render;
    private MeshHandle _mesh = MeshHandle.Invalid;
    
    private Simulation _simulation;
    private BufferPool _bufferPool;
    private StaticHandle _staticHandle;
    private TypedIndex _shapeIndex;
    private bool _colliderBuilt;

    public TerrainChunk(TerrainAsset asset, int startX, int startZ, int gridPointsX, int gridPointsZ)
    {
        _asset = asset;
        _startX = startX;
        _startZ = startZ;
        _gridPointsX = gridPointsX;
        _gridPointsZ = gridPointsZ;
    }

    public void Bind(IRenderController render, Simulation simulation = null, BufferPool bufferPool = null)
    {
        DisposeGpu();
        DisposePhysics();

        _render = render ?? throw new ArgumentNullException(nameof(render));
        
        BuildRenderMesh();

        if (simulation != null && bufferPool != null)
        {
            _simulation = simulation;
            _bufferPool = bufferPool;
            BuildPhysicsMesh();
        }
    }

    public void Rebuild()
    {
        if (_render == null) return;
        
        DisposeGpu();
        DisposePhysics();
        
        BuildRenderMesh();
        if (_simulation != null && _bufferPool != null)
            BuildPhysicsMesh();
    }

    private Vector3 ComputeNormal(int x, int z)
    {
        float hL = _asset.GetHeight(Math.Max(0, x - 1), z);
        float hR = _asset.GetHeight(Math.Min(_asset.ResolutionX - 1, x + 1), z);
        float hN = _asset.GetHeight(x, Math.Max(0, z - 1));
        float hS = _asset.GetHeight(x, Math.Min(_asset.ResolutionZ - 1, z + 1));
        return Vector3.Normalize(new Vector3(hL - hR, _asset.CellSize * 2f, hN - hS));
    }

    private void BuildRenderMesh()
    {
        int vertexCount = _gridPointsX * _gridPointsZ;
        int quadCountX = _gridPointsX - 1;
        int quadCountZ = _gridPointsZ - 1;
        var vertices = new MeshVertex[vertexCount];
        var indices = new ushort[quadCountX * quadCountZ * 6];

        for (int z = 0; z < _gridPointsZ; z++)
        {
            for (int x = 0; x < _gridPointsX; x++)
            {
                int globalX = _startX + x;
                int globalZ = _startZ + z;

                int i = z * _gridPointsX + x;
                float wx = _asset.OriginX + globalX * _asset.CellSize;
                float wz = _asset.OriginZ + globalZ * _asset.CellSize;
                float wy = _asset.GetHeight(globalX, globalZ);

                // Mapping UVs to 0-1 for the whole terrain for splatmapping
                float nx = globalX / (float)(_asset.ResolutionX - 1);
                float nz = globalZ / (float)(_asset.ResolutionZ - 1);

                vertices[i] = new MeshVertex
                {
                    Position = new Vector3(wx, wy, wz),
                    Normal = ComputeNormal(globalX, globalZ),
                    Color = Vector4.One,
                    UV = new Vector2(nx, nz),
                };
            }
        }

        int idx = 0;
        for (int z = 0; z < quadCountZ; z++)
        {
            for (int x = 0; x < quadCountX; x++)
            {
                ushort a = (ushort)(z * _gridPointsX + x);
                ushort b = (ushort)(a + 1);
                ushort c = (ushort)(a + _gridPointsX);
                ushort d = (ushort)(c + 1);

                // Counter-clockwise from above, matching the upward normals and renderer defaults.
                indices[idx++] = a;
                indices[idx++] = d;
                indices[idx++] = b;
                indices[idx++] = a;
                indices[idx++] = c;
                indices[idx++] = d;
            }
        }

        _mesh = _render.RegisterMesh(vertices, indices);
    }

    private void BuildPhysicsMesh()
    {
        if (_colliderBuilt || _simulation == null || _bufferPool == null)
            return;

        int vertexCount = _gridPointsX * _gridPointsZ;
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int x = i % _gridPointsX;
            int z = i / _gridPointsX;
            int globalX = _startX + x;
            int globalZ = _startZ + z;

            vertices[i] = new Vector3(
                _asset.OriginX + globalX * _asset.CellSize,
                _asset.GetHeight(globalX, globalZ),
                _asset.OriginZ + globalZ * _asset.CellSize);
        }

        int quadCountX = _gridPointsX - 1;
        int quadCountZ = _gridPointsZ - 1;
        int triCount = quadCountX * quadCountZ * 2;
        _bufferPool.Take<Triangle>(triCount, out Buffer<Triangle> triangles);

        int tri = 0;
        for (int z = 0; z < quadCountZ; z++)
        {
            for (int x = 0; x < quadCountX; x++)
            {
                int a = z * _gridPointsX + x;
                int b = a + 1;
                int c = a + _gridPointsX;
                int d = c + 1;

                // Bepu uses right-handed coordinates and one-sided triangles. Clockwise winding
                // as seen from above makes the solid side face upward.
                triangles[tri++] = new Triangle(vertices[a], vertices[b], vertices[d]);
                triangles[tri++] = new Triangle(vertices[a], vertices[d], vertices[c]);
            }
        }

        var mesh = new Mesh(triangles, Vector3.One, _bufferPool);
        _shapeIndex = _simulation.Shapes.Add(mesh);
        _staticHandle = _simulation.Statics.Add(new StaticDescription(Vector3.Zero, _shapeIndex));
        _colliderBuilt = true;
    }

    public void AppendDrawCalls(MeshDrawCall[] buffer, ref int count, Vector3 cameraPos)
    {
        if (!_mesh.IsValid || count >= buffer.Length)
            return;

        buffer[count++] = new MeshDrawCall
        {
            Mesh = _mesh,
            World = Matrix4x4.Identity,
            Tint = new RenderColor(0.8f, 0.8f, 0.8f, 1f), // TBD splatmap shader will override
            Alpha = 1f,
            ChunkId = 0,
        };
    }

    private void DisposeGpu()
    {
        if (_mesh.IsValid && _render != null)
            _render.ReleaseMesh(_mesh);
        _mesh = MeshHandle.Invalid;
    }

    private void DisposePhysics()
    {
        if (!_colliderBuilt || _simulation == null || _bufferPool == null)
            return;

        _simulation.Statics.Remove(_staticHandle);
        _simulation.Shapes.RemoveAndDispose(_shapeIndex, _bufferPool);
        _colliderBuilt = false;
    }

    public void Dispose()
    {
        DisposeGpu();
        DisposePhysics();
        _render = null;
    }
}

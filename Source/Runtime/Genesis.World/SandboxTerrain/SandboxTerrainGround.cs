using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using Genesis.Shared.Interfaces;

namespace Genesis.World.SandboxTerrain;

/// <summary>
/// Single static sandbox ground: one heightfield, one GPU mesh, one physics mesh.
/// All vertices are authored in world space (identity draw matrix, no chunk streaming).
/// </summary>
public sealed class SandboxTerrainGround : IDisposable
{
    // Was 65x65 at a 2-unit cell. Halving the cell size (same -64..+64 world extent, so the
    // grid point count doubles in each axis) shrinks the maximum height change any single quad
    // can span for a given underlying slope. That matters because back-face culling (a global,
    // engine-wide setting — terrain deliberately does not override it) means a grazing ray that
    // ducks past a near quad's edge on a steep local slope can otherwise punch through to the
    // culled far side of a farther quad and keep going, reading as a seam that "sees inside" the
    // terrain shell from one viewing direction only (fine from the opposite side, where the same
    // slope faces the camera convexly instead). Finer resolution is the actual fix for that —
    // not a per-mesh culling exception, which would make terrain silently disagree with the
    // Culling > Face setting everything else in the engine respects.
    //
    // Bumped again here (129x129/1-unit -> 161x161/0.8-unit, same -64..+64 extent): the new
    // procedural ground shading (TerrainAlbedo in ForwardShaders.cs) and the slope-based
    // foliage placement below both read the *vertex* normal/height field at runtime, so finer
    // geometry gives both a more accurate slope to react to, on top of the original grazing-ray
    // fix this comment already describes.
    private const int GridPointsX = 161;
    private const int GridPointsZ = 161;
    private const float CellSize = 0.8f;
    private const float OriginX = -64f;
    private const float OriginZ = -64f;
    private const int HeightSeed = 5107;

    // ── Foliage (grass + pebble instancing) ─────────────────────────────────────────
    // Stand-in for the future terrain editor's painted Foliage layer (see Terrain Editor
    // Redesign doc): density/placement rules are hard-coded here instead of being read from a
    // mask, but route through the exact same per-instance StructuredBuffer the editor's
    // foliage system is expected to feed later. Never a user-facing toggle — always on for
    // this ground.
    private const int FoliageSeed = 9001;
    private const float GrassCellSize = 1.6f;
    private const float PebbleCellSize = 3.2f;
    private const float FoliageExtent = 60f; // stay inside the ±64 mesh extent, clear of the skirt

    /// <summary>
    /// Flat terrain height before rolling/noise/lake/mountain modifiers. Exposed so
    /// callers (e.g. lake placement) can position a water surface relative to the
    /// undisturbed ground instead of guessing a hard-coded world Y (Issue 3).
    /// </summary>
    public const float BaseHeight = 2.0f;

    private readonly float[] _heights = new float[GridPointsX * GridPointsZ];

    private IRenderController _render;
    private MeshHandle _mesh = MeshHandle.Invalid;
    private MeshHandle _grassMesh = MeshHandle.Invalid;
    private MeshHandle _pebbleMesh = MeshHandle.Invalid;
    private readonly List<(Matrix4x4 World, RenderColor Tint)> _grassInstances = new();
    private readonly List<(Matrix4x4 World, RenderColor Tint)> _pebbleInstances = new();
    private Simulation _simulation;
    private BufferPool _bufferPool;
    private StaticHandle _staticHandle;
    private TypedIndex _shapeIndex;
    private bool _colliderBuilt;

    public Vector3 LakeCenter { get; set; } = new(0f, 0f, 6f);
    public float LakeRadius { get; set; } = 22f;
    public float LakeDepth { get; set; } = 3.5f;
    public bool LakeCarved { get; set; }

    public Vector3 MountainCenter { get; set; } = new(-20f, 0f, -8f);
    public float MountainRadius { get; set; } = 18f;
    public float MountainHeight { get; set; } = 10f;
    public bool MountainRaised { get; set; }

    public void Bind(IRenderController render, Simulation simulation = null, BufferPool bufferPool = null)
    {
        DisposeGpu();
        DisposePhysics();

        _render = render ?? throw new ArgumentNullException(nameof(render));
        _render.ClearChunkBounds();

        BuildHeights();
        BuildRenderMesh();
        BuildFoliage();

        if (simulation != null && bufferPool != null)
        {
            _simulation = simulation;
            _bufferPool = bufferPool;
            BuildPhysicsMesh();
        }
    }

    /// <summary>Rebuilds GPU mesh and physics collider after lake/mountain toggles.</summary>
    public void Rebuild()
    {
        if (_render == null)
            return;

        DisposeGpu();
        DisposePhysics();
        BuildHeights();
        BuildRenderMesh();
        BuildFoliage();

        if (_simulation != null && _bufferPool != null)
            BuildPhysicsMesh();
    }

    public float SampleHeight(float worldX, float worldZ)
    {
        if (_heights.Length == 0)
            return 0f;

        float gx = (worldX - OriginX) / CellSize;
        float gz = (worldZ - OriginZ) / CellSize;

        if (gx < 0f || gz < 0f || gx > GridPointsX - 1 || gz > GridPointsZ - 1)
            return ComputeHeight(worldX, worldZ);

        int x0 = (int)MathF.Floor(gx);
        int z0 = (int)MathF.Floor(gz);
        int x1 = Math.Min(x0 + 1, GridPointsX - 1);
        int z1 = Math.Min(z0 + 1, GridPointsZ - 1);
        float tx = gx - x0;
        float tz = gz - z0;

        float h00 = _heights[z0 * GridPointsX + x0];
        float h10 = _heights[z0 * GridPointsX + x1];
        float h01 = _heights[z1 * GridPointsX + x0];
        float h11 = _heights[z1 * GridPointsX + x1];

        float hx0 = h00 + (h10 - h00) * tx;
        float hx1 = h01 + (h11 - h01) * tx;
        return hx0 + (hx1 - hx0) * tz;
    }

    public void AppendDrawCalls(MeshDrawCall[] buffer, ref int count, Vector3 cameraPos)
    {
        _ = cameraPos;

        if (_mesh.IsValid && count < buffer.Length)
        {
            // Tint is white: the procedural grass/dirt-path blend (TerrainAlbedo in
            // ForwardShaders.cs, enabled by the TerrainGround flag) now supplies the actual
            // ground colour per-pixel, replacing the old flat single-tint look.
            buffer[count++] = new MeshDrawCall
            {
                Mesh = _mesh,
                World = Matrix4x4.Identity,
                Tint = RenderColor.White,
                Alpha = 1f,
                ChunkId = 0,
                Flags = MeshDrawFlags.TerrainGround,
            };
        }

        if (_grassMesh.IsValid)
        {
            for (int i = 0; i < _grassInstances.Count && count < buffer.Length; i++)
            {
                var inst = _grassInstances[i];
                buffer[count++] = new MeshDrawCall
                {
                    Mesh = _grassMesh,
                    World = inst.World,
                    Tint = inst.Tint,
                    Alpha = 1f,
                    // Grass blades are thin single-layer triangles (see BuildGrassMesh) that need
                    // to read as solid from any horizontal angle, not just from the one side their
                    // winding faces. NoCull disables backface culling for this draw's batch instead
                    // of duplicating each triangle with reversed winding (the previous approach,
                    // which relied on backface culling discarding the wrong copy and was wasted
                    // geometry at best). Automatically set here only — never a user-facing toggle.
                    Flags = MeshDrawFlags.NoCull,
                };
            }
        }

        if (_pebbleMesh.IsValid)
        {
            for (int i = 0; i < _pebbleInstances.Count && count < buffer.Length; i++)
            {
                var inst = _pebbleInstances[i];
                buffer[count++] = new MeshDrawCall
                {
                    Mesh = _pebbleMesh,
                    World = inst.World,
                    Tint = inst.Tint,
                    Alpha = 1f,
                };
            }
        }
    }

    public void Dispose()
    {
        DisposeGpu();
        DisposePhysics();

        if (_grassMesh.IsValid && _render != null)
            _render.ReleaseMesh(_grassMesh);
        if (_pebbleMesh.IsValid && _render != null)
            _render.ReleaseMesh(_pebbleMesh);
        _grassMesh = MeshHandle.Invalid;
        _pebbleMesh = MeshHandle.Invalid;
        _grassInstances.Clear();
        _pebbleInstances.Clear();

        _render = null;
    }

    private void BuildHeights()
    {
        for (int z = 0; z < GridPointsZ; z++)
        {
            for (int x = 0; x < GridPointsX; x++)
            {
                float wx = OriginX + x * CellSize;
                float wz = OriginZ + z * CellSize;
                _heights[z * GridPointsX + x] = ComputeHeight(wx, wz);
            }
        }
    }

    private float ComputeHeight(float worldX, float worldZ)
    {
        float rolling = MathF.Sin(worldX * 0.07f) * MathF.Cos(worldZ * 0.06f) * 1.8f;
        float detail = Noise.Fbm2(HeightSeed, worldX * 0.035f, worldZ * 0.035f, octaves: 3) * 2.2f;
        float height = BaseHeight + rolling + detail;

        if (LakeCarved)
        {
            float dx = worldX - LakeCenter.X;
            float dz = worldZ - LakeCenter.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < LakeRadius)
            {
                // Smoothstep profile instead of t*t (matches the mountain raise below):
                // t*t has continuous slope but a curvature kink right at the rim, which
                // reads as a faint crease where the lake bed meets the surrounding ground —
                // one of the reported "seams." Smoothstep is C1 *and* flatter near both
                // ends, removing the kink.
                float t = 1f - dist / LakeRadius;
                float s = t * t * (3f - 2f * t);
                height -= LakeDepth * s;
            }
        }

        if (MountainRaised)
        {
            float mdx = worldX - MountainCenter.X;
            float mdz = worldZ - MountainCenter.Z;
            float mdist = MathF.Sqrt(mdx * mdx + mdz * mdz);
            if (mdist < MountainRadius)
            {
                // Smoothstep profile instead of t*t: zero slope at both the apex and
                // the rim, so the mountain reads as a rounded hill rather than a
                // conical spike (t*t has a sharp, non-smooth point at the center).
                float t = 1f - mdist / MountainRadius;
                float s = t * t * (3f - 2f * t);
                height += MountainHeight * s;
            }
        }

        return height;
    }

    /// <summary>
    /// Surface slope at an arbitrary world position (1 = flat, 0 = vertical), via the same
    /// central-difference formula <see cref="ComputeNormal"/> uses on the baked grid, but
    /// evaluated continuously through <see cref="ComputeHeight"/> so foliage scattering isn't
    /// limited to grid-point resolution.
    /// </summary>
    private float ComputeSlopeY(float worldX, float worldZ)
    {
        const float eps = 0.5f;
        float hL = ComputeHeight(worldX - eps, worldZ);
        float hR = ComputeHeight(worldX + eps, worldZ);
        float hN = ComputeHeight(worldX, worldZ - eps);
        float hS = ComputeHeight(worldX, worldZ + eps);
        Vector3 normal = Vector3.Normalize(new Vector3(hL - hR, eps * 2f, hN - hS));
        return normal.Y;
    }

    private static float SmoothStep01(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// True if foliage may be placed at this world position; also estimates the same
    /// "dirt path" bias the procedural ground shader (TerrainAlbedo in ForwardShaders.cs)
    /// computes per-pixel, so placement loosely agrees with what the ground will actually
    /// look like at that spot (not a pixel-exact match — this runs on the CPU once at build
    /// time, the shader runs per-pixel with a slightly richer domain-warped noise field).
    /// </summary>
    private bool IsFoliageAllowed(float worldX, float worldZ, bool allowSteep, out float dirtBias)
    {
        dirtBias = 0f;

        if (LakeCarved)
        {
            float dx = worldX - LakeCenter.X;
            float dz = worldZ - LakeCenter.Z;
            float margin = LakeRadius + 1.5f;
            if (dx * dx + dz * dz < margin * margin)
                return false;
        }

        float slopeY = ComputeSlopeY(worldX, worldZ);
        float minSlope = allowSteep ? 0.55f : 0.78f;
        if (slopeY < minSlope)
            return false;

        float pathNoise = Noise.Fbm2(FoliageSeed + 50, worldX * 0.05f, worldZ * 0.05f, octaves: 4);
        float pathMask = SmoothStep01(0.46f, 0.58f, pathNoise);
        dirtBias = Math.Clamp(pathMask + (1f - slopeY) * 1.6f, 0f, 1f);
        return true;
    }

    private void BuildFoliage()
    {
        if (!_grassMesh.IsValid)
        {
            var (gv, gi) = BuildGrassMesh();
            _grassMesh = _render.RegisterMesh(gv, gi);
        }
        if (!_pebbleMesh.IsValid)
        {
            var (pv, pi) = BuildPebbleMesh();
            _pebbleMesh = _render.RegisterMesh(pv, pi);
        }

        ScatterGrass();
        ScatterPebbles();
    }

    private void ScatterGrass()
    {
        _grassInstances.Clear();

        int steps = (int)(FoliageExtent * 2f / GrassCellSize);
        for (int gz = 0; gz < steps; gz++)
        {
            for (int gx = 0; gx < steps; gx++)
            {
                float cellWx = -FoliageExtent + gx * GrassCellSize;
                float cellWz = -FoliageExtent + gz * GrassCellSize;

                // Thin out a regular grid into something less mechanical-looking.
                float keep = Noise.Value(FoliageSeed, gx, gz);
                if (keep > 0.62f)
                    continue;

                float jx = (Noise.Value(FoliageSeed + 101, gx, gz) - 0.5f) * GrassCellSize * 0.9f;
                float jz = (Noise.Value(FoliageSeed + 202, gx, gz) - 0.5f) * GrassCellSize * 0.9f;
                float wx = cellWx + jx;
                float wz = cellWz + jz;

                if (!IsFoliageAllowed(wx, wz, allowSteep: false, out float dirtBias))
                    continue;
                // Thin grass out where the ground reads as bare dirt path.
                if (dirtBias > 0.55f && Noise.Value(FoliageSeed + 303, gx, gz) < dirtBias)
                    continue;

                float wy = SampleHeight(wx, wz);
                float rotY = Noise.Value(FoliageSeed + 404, gx, gz) * MathF.PI * 2f;
                float scale = 0.75f + Noise.Value(FoliageSeed + 505, gx, gz) * 0.6f;

                Matrix4x4 world = Matrix4x4.CreateScale(scale) *
                                   Matrix4x4.CreateRotationY(rotY) *
                                   Matrix4x4.CreateTranslation(wx, wy, wz);

                float shade = 0.85f + Noise.Value(FoliageSeed + 606, gx, gz) * 0.3f;
                var tint = new RenderColor(0.45f * shade, 0.62f * shade, 0.28f * shade, 1f);

                _grassInstances.Add((world, tint));
            }
        }
    }

    private void ScatterPebbles()
    {
        _pebbleInstances.Clear();

        int steps = (int)(FoliageExtent * 2f / PebbleCellSize);
        for (int pz = 0; pz < steps; pz++)
        {
            for (int px = 0; px < steps; px++)
            {
                float cellWx = -FoliageExtent + px * PebbleCellSize;
                float cellWz = -FoliageExtent + pz * PebbleCellSize;

                float jx = (Noise.Value(FoliageSeed + 707, px, pz) - 0.5f) * PebbleCellSize * 0.9f;
                float jz = (Noise.Value(FoliageSeed + 808, px, pz) - 0.5f) * PebbleCellSize * 0.9f;
                float wx = cellWx + jx;
                float wz = cellWz + jz;

                if (!IsFoliageAllowed(wx, wz, allowSteep: true, out float dirtBias))
                    continue;

                // Pebbles mostly cluster into dirt-path patches, with a light scattering
                // through grass too so open lawns aren't completely bare.
                float keep = Noise.Value(FoliageSeed + 909, px, pz);
                float chance = MathF.Max(dirtBias, 0.18f);
                if (keep > chance)
                    continue;

                float wy = SampleHeight(wx, wz);
                float rotY = Noise.Value(FoliageSeed + 1010, px, pz) * MathF.PI * 2f;
                float scale = 0.5f + Noise.Value(FoliageSeed + 1111, px, pz) * 0.9f;

                Matrix4x4 world = Matrix4x4.CreateScale(scale) *
                                   Matrix4x4.CreateRotationY(rotY) *
                                   Matrix4x4.CreateTranslation(wx, wy, wz);

                float shade = 0.8f + Noise.Value(FoliageSeed + 1212, px, pz) * 0.4f;
                var tint = new RenderColor(0.42f * shade, 0.36f * shade, 0.30f * shade, 1f);

                _pebbleInstances.Add((world, tint));
            }
        }
    }

    /// <summary>
    /// A small clump of 3 single-triangle blades at 60° apart (base-left, base-right, tip —
    /// not a quad, so the silhouette actually tapers like a blade instead of reading as a
    /// flat banner). Each blade has a real outward-facing normal computed from its own
    /// plane (previously hard-coded to world-up for every vertex, which made every blade
    /// get lit identically to flat ground regardless of which way it actually faced — a
    /// big part of why these looked washed-out/flat). Visibility from any horizontal angle
    /// comes from <see cref="MeshDrawFlags.NoCull"/> on the draw call (see
    /// AppendDrawCalls), not from duplicating triangles with reversed winding, so there's
    /// exactly one triangle per blade here, no redundant geometry. Tinted per-instance via
    /// <see cref="MeshDrawCall.Tint"/> for colour variation; base/tip vertex colours add a
    /// shadowed-root-to-sunlit-tip gradient on top of that, in the same dark-to-light green
    /// range as the procedural ground (TerrainAlbedo in ForwardShaders.cs) so blades and
    /// ground read as one consistent palette instead of grass looking pasted on top.
    /// </summary>
    private static (MeshVertex[] Vertices, ushort[] Indices) BuildGrassMesh()
    {
        const float halfWidth = 0.07f;
        const float height = 0.5f;
        const float lean = 0.12f; // slight forward bend so blades don't read as perfectly rigid cards

        var verts = new List<MeshVertex>();
        var inds = new List<ushort>();
        Vector4 baseColor = new Vector4(0.55f, 0.55f, 0.48f, 1f);
        Vector4 tipColor  = new Vector4(0.95f, 0.95f, 0.82f, 1f);

        void AddBlade(Vector3 right)
        {
            Vector3 p0 = -right * halfWidth;
            Vector3 p1 = right * halfWidth;
            Vector3 p2 = new Vector3(0f, height, 0f) + right * lean;

            Vector3 n = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));

            ushort start = (ushort)verts.Count;
            verts.Add(new MeshVertex { Position = p0, Normal = n, Color = baseColor, UV = new Vector2(0, 1) });
            verts.Add(new MeshVertex { Position = p1, Normal = n, Color = baseColor, UV = new Vector2(1, 1) });
            verts.Add(new MeshVertex { Position = p2, Normal = n, Color = tipColor,  UV = new Vector2(0.5f, 0) });
            inds.Add(start); inds.Add((ushort)(start + 1)); inds.Add((ushort)(start + 2));
        }

        AddBlade(Vector3.UnitX);
        AddBlade(Vector3.Normalize(new Vector3(0.5f, 0f, 0.866f)));
        AddBlade(Vector3.Normalize(new Vector3(-0.5f, 0f, 0.866f)));

        return (verts.ToArray(), inds.ToArray());
    }

    /// <summary>
    /// Tiny flattened octahedron — reads as a smooth rounded pebble at this scale. Each face
    /// is now a single triangle with the correct outward winding (previously every face was
    /// duplicated with both windings under the assumption the engine's winding convention
    /// for this shape was unknown; verified against the same convention BuildRenderMesh's
    /// terrain quads use — Cross(p1-p0, p2-p0) gives the outward/front-facing direction for
    /// this engine's cull-back rasterizer state — so the duplicate was always dead, culled
    /// geometry, not a correctness issue, just twice the triangles for no benefit). Vertex
    /// colours are baked top-lit/bottom-shadowed instead of flat white: at this small a
    /// scale and triangle count, uniform colour made every facet read as an evenly-bright
    /// faceted "gem" rather than a rounded stone. Multiplied by the per-instance grey/brown
    /// Tint from <see cref="ScatterPebbles"/>, same as the grass base/tip gradient above.
    /// </summary>
    private static (MeshVertex[] Vertices, ushort[] Indices) BuildPebbleMesh()
    {
        const float radius = 0.22f;
        const float halfHeight = 0.08f;

        Vector3 top = new Vector3(0f, halfHeight, 0f);
        Vector3 bottom = new Vector3(0f, -halfHeight, 0f);
        Vector3[] ring =
        {
            new Vector3(radius, 0f, 0f),
            new Vector3(0f, 0f, radius),
            new Vector3(-radius, 0f, 0f),
            new Vector3(0f, 0f, -radius),
        };

        Vector4 topColor    = new Vector4(1.00f, 1.00f, 0.95f, 1f);
        Vector4 ringColor   = new Vector4(0.75f, 0.72f, 0.68f, 1f);
        Vector4 bottomColor = new Vector4(0.42f, 0.40f, 0.38f, 1f);

        var verts = new List<MeshVertex>();
        var inds = new List<ushort>();

        void AddTri(Vector3 p0, Vector3 p1, Vector3 p2, Vector4 c0, Vector4 c1, Vector4 c2)
        {
            Vector3 n = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            ushort start = (ushort)verts.Count;
            verts.Add(new MeshVertex { Position = p0, Normal = n, Color = c0, UV = Vector2.Zero });
            verts.Add(new MeshVertex { Position = p1, Normal = n, Color = c1, UV = Vector2.Zero });
            verts.Add(new MeshVertex { Position = p2, Normal = n, Color = c2, UV = Vector2.Zero });
            inds.Add(start); inds.Add((ushort)(start + 1)); inds.Add((ushort)(start + 2));
        }

        for (int i = 0; i < 4; i++)
        {
            Vector3 r0 = ring[i];
            Vector3 r1 = ring[(i + 1) % 4];
            // (top, r1, r0) / (bottom, r0, r1): the outward winding for each — reversed from
            // the single "primary" order the old both-windings version happened to list
            // first, which was actually the inward (culled) copy for both top and bottom.
            AddTri(top, r1, r0, topColor, ringColor, ringColor);
            AddTri(bottom, r0, r1, bottomColor, ringColor, ringColor);
        }

        return (verts.ToArray(), inds.ToArray());
    }

    // How far below each boundary vertex the perimeter skirt drops (see BuildRenderMesh).
    // Comfortably exceeds the terrain's full height range (BaseHeight ± rolling/detail/lake
    // carve/mountain raise, roughly -6..+16) so no plausible camera angle sees under it.
    private const float SkirtDepth = 45f;

    private void BuildRenderMesh()
    {
        int vertexCount = GridPointsX * GridPointsZ;
        int quadCountX = GridPointsX - 1;
        int quadCountZ = GridPointsZ - 1;

        // Perimeter skirt vertex layout, appended after the main grid: north row (z=0,
        // GridPointsX points), south row (z=max), west column (x=0, GridPointsZ points),
        // east column (x=max).
        int northOffset = vertexCount;
        int southOffset = northOffset + GridPointsX;
        int westOffset  = southOffset + GridPointsX;
        int eastOffset  = westOffset + GridPointsZ;
        int totalVertexCount = eastOffset + GridPointsZ;

        int mainIndexCount  = quadCountX * quadCountZ * 6;
        int skirtQuadCount  = quadCountX * 2 + quadCountZ * 2;
        int skirtIndexCount = skirtQuadCount * 12; // double-sided: 4 tris/quad, 3 indices/tri

        var vertices = new MeshVertex[totalVertexCount];
        var indices = new ushort[mainIndexCount + skirtIndexCount];

        for (int z = 0; z < GridPointsZ; z++)
        {
            for (int x = 0; x < GridPointsX; x++)
            {
                int i = z * GridPointsX + x;
                float wx = OriginX + x * CellSize;
                float wz = OriginZ + z * CellSize;
                float wy = _heights[i];
                float nx = (x / (float)(GridPointsX - 1));
                float nz = (z / (float)(GridPointsZ - 1));

                vertices[i] = new MeshVertex
                {
                    Position = new Vector3(wx, wy, wz),
                    Normal = ComputeNormal(x, z),
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
                ushort a = (ushort)(z * GridPointsX + x);
                ushort b = (ushort)(a + 1);
                ushort c = (ushort)(a + GridPointsX);
                ushort d = (ushort)(c + 1);

                // Match BuildFloor winding (0,2,1) and (0,3,2) for upward-facing +Y normals.
                indices[idx++] = a;
                indices[idx++] = d;
                indices[idx++] = b;
                indices[idx++] = a;
                indices[idx++] = c;
                indices[idx++] = d;
            }
        }

        // --- Perimeter skirt -------------------------------------------------------
        // The grid above is a zero-thickness sheet that simply stops at its ±64-unit
        // border. Any view ray that grazes past that border, or dips slightly below the
        // mesh plane near it, sees straight through to nothing — reported as terrain
        // looking "see-through"/duvet-like at its edges. The post-composite fog pass also
        // hits a sharp jump there, between real terrain depth and its far-clip-plane sky
        // fallback, which read as a bright single-pixel seam along the mesh's silhouette
        // (the white horizon line). Dropping a wall of geometry SkirtDepth below each
        // boundary vertex — well below any height the camera could plausibly see under —
        // closes both gaps with real, depth-continuous geometry instead of a hard edge.
        // The skirt only needs to occlude, not shade correctly from one particular side,
        // so each quad is emitted with both windings.
        for (int x = 0; x < GridPointsX; x++)
        {
            vertices[northOffset + x] = SkirtVertex(vertices[x].Position);
            vertices[southOffset + x] = SkirtVertex(vertices[(GridPointsZ - 1) * GridPointsX + x].Position);
        }
        for (int z = 0; z < GridPointsZ; z++)
        {
            vertices[westOffset + z] = SkirtVertex(vertices[z * GridPointsX].Position);
            vertices[eastOffset + z] = SkirtVertex(vertices[z * GridPointsX + (GridPointsX - 1)].Position);
        }

        idx = AppendSkirtQuads(indices, idx, GridPointsX, x => (ushort)x, x => (ushort)(northOffset + x), false);
        idx = AppendSkirtQuads(indices, idx, GridPointsX, x => (ushort)((GridPointsZ - 1) * GridPointsX + x), x => (ushort)(southOffset + x), true);
        idx = AppendSkirtQuads(indices, idx, GridPointsZ, z => (ushort)(z * GridPointsX), z => (ushort)(westOffset + z), false);
        idx = AppendSkirtQuads(indices, idx, GridPointsZ, z => (ushort)(z * GridPointsX + (GridPointsX - 1)), z => (ushort)(eastOffset + z), true);

        _mesh = _render.RegisterMesh(vertices, indices);
    }

    private static MeshVertex SkirtVertex(Vector3 top) => new MeshVertex
    {
        Position = new Vector3(top.X, top.Y - SkirtDepth, top.Z),
        Normal = Vector3.UnitY, // occlusion-only geometry; shading fidelity doesn't matter here.
        Color = Vector4.One,
        UV = Vector2.Zero,
    };

    /// <summary>
    /// Appends a double-sided quad strip along one perimeter edge: <paramref name="topAt"/>
    /// maps 0..count-1 to the existing top-row vertex index, <paramref name="bottomAt"/> to
    /// the corresponding new skirt-bottom vertex index.
    /// </summary>
    private static int AppendSkirtQuads(ushort[] indices, int idx, int count, Func<int, ushort> topAt, Func<int, ushort> bottomAt, bool reverseWinding)
    {
        for (int i = 0; i < count - 1; i++)
        {
            ushort t0 = topAt(i);
            ushort t1 = topAt(i + 1);
            ushort b0 = bottomAt(i);
            ushort b1 = bottomAt(i + 1);

            if (reverseWinding)
            {
                indices[idx++] = t0; indices[idx++] = b0; indices[idx++] = t1;
                indices[idx++] = t1; indices[idx++] = b0; indices[idx++] = b1;
            }
            else
            {
                indices[idx++] = t0; indices[idx++] = t1; indices[idx++] = b0;
                indices[idx++] = t1; indices[idx++] = b1; indices[idx++] = b0;
            }
        }
        return idx;
    }

    private Vector3 ComputeNormal(int x, int z)
    {
        float hL = _heights[z * GridPointsX + Math.Max(0, x - 1)];
        float hR = _heights[z * GridPointsX + Math.Min(GridPointsX - 1, x + 1)];
        float hN = _heights[Math.Max(0, z - 1) * GridPointsX + x];
        float hS = _heights[Math.Min(GridPointsZ - 1, z + 1) * GridPointsX + x];
        return Vector3.Normalize(new Vector3(hL - hR, CellSize * 2f, hN - hS));
    }

    private void BuildPhysicsMesh()
    {
        if (_colliderBuilt || _simulation == null || _bufferPool == null)
            return;

        int vertexCount = GridPointsX * GridPointsZ;
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int x = i % GridPointsX;
            int z = i / GridPointsX;
            vertices[i] = new Vector3(
                OriginX + x * CellSize,
                _heights[i],
                OriginZ + z * CellSize);
        }

        int quadCountX = GridPointsX - 1;
        int quadCountZ = GridPointsZ - 1;
        int triCount = quadCountX * quadCountZ * 2;
        _bufferPool.Take<Triangle>(triCount, out Buffer<Triangle> triangles);

        int tri = 0;
        for (int z = 0; z < quadCountZ; z++)
        {
            for (int x = 0; x < quadCountX; x++)
            {
                int a = z * GridPointsX + x;
                int b = a + 1;
                int c = a + GridPointsX;
                int d = c + 1;

                // Physics winding matches the render mesh winding (a,d,b)/(a,c,d). BepuPhysics
                // one-sided triangles are solid on the counter-clockwise face in left-handed
                // coordinates (this engine uses FrontCounterClockwise=false). The earlier
                // "(a,b,d)/(a,d,c) reversed" comment was incorrect — that winding flipped the
                // solid side downward and let the player fall through the terrain from above.
                triangles[tri++] = new Triangle(vertices[a], vertices[b], vertices[d]);
                triangles[tri++] = new Triangle(vertices[a], vertices[d], vertices[c]);
            }
        }

        var mesh = new Mesh(triangles, Vector3.One, _bufferPool);
        _shapeIndex = _simulation.Shapes.Add(mesh);
        _staticHandle = _simulation.Statics.Add(new StaticDescription(Vector3.Zero, _shapeIndex));
        _colliderBuilt = true;
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
}

using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.World;
using Genesis.World.Terrain;

namespace Genesis.Application.Runtime;

/// <summary>
/// Large terrain scene: a multi-kilometre heightfield drawn through the level-of-detail ground
/// renderer with a long far plane. Tests look across it from chosen cameras and read back how
/// many nodes and triangles the view needed.
/// </summary>
public sealed partial class RuntimeViewportHarness
{
    private AuthoredTerrainGround? _largeGround;
    private TerrainAsset? _largeTerrain;
    private IRenderController? _largeGroundRenderer;
    private Vector3 _largeEye, _largeTarget;
    private float _largeDetail = 1f;
    private MeshDrawCall[] _largeDraws = [];

    /// <summary>Far plane used by the large terrain scene, in metres.</summary>
    public const float LargeTerrainFarPlane = 16000f;

    /// <summary>
    /// A deterministic island for scale tests: a mountainous interior falling to a beach and sea
    /// bed at the edge, painted by height and slope (grass, dirt, rock, snow).
    /// </summary>
    public static TerrainAsset BuildIslandTerrain(int resolution, float cellSize, int seed = 4471)
    {
        float half = (resolution - 1) * cellSize * 0.5f;
        TerrainAsset terrain = new(resolution, resolution, cellSize, -half, -half, -80f, 900f);
        Parallel.For(0, resolution, z =>
        {
            for (int x = 0; x < resolution; x++)
            {
                float wx = -half + x * cellSize, wz = -half + z * cellSize;
                float radial = MathF.Sqrt(wx * wx + wz * wz) / half;
                float coast = radial + Noise.Fbm2(seed + 11, wx * 0.0006f, wz * 0.0006f, 4) * 0.22f;
                float land = Math.Clamp(1.15f - coast * 1.25f, 0f, 1f);
                land = land * land * (3f - 2f * land);
                float hills = Noise.Fbm2(seed, wx * 0.0012f, wz * 0.0012f, 6) * 0.5f + 0.5f;
                float ridge = 1f - MathF.Abs(Noise.Fbm2(seed + 5, wx * 0.0005f, wz * 0.0005f, 6));
                float mountains = MathF.Pow(Math.Clamp(ridge, 0f, 1f), 3f) * Math.Clamp(land * 1.6f - 0.35f, 0f, 1f);
                float height = -60f + land * (90f + hills * 110f) + mountains * 640f;
                terrain.SetHeight(x, z, height);
            }
        });

        Parallel.For(0, resolution, z =>
        {
            for (int x = 0; x < resolution; x++)
            {
                float height = terrain.GetHeight(x, z);
                float dx = terrain.GetHeight(Math.Min(x + 1, resolution - 1), z) - terrain.GetHeight(Math.Max(x - 1, 0), z);
                float dz = terrain.GetHeight(x, Math.Min(z + 1, resolution - 1)) - terrain.GetHeight(x, Math.Max(z - 1, 0));
                float slope = MathF.Sqrt(dx * dx + dz * dz) / (2f * cellSize);
                float rock = Math.Clamp((slope - 0.55f) * 3f, 0f, 1f);
                float snow = Math.Clamp((height - 520f) / 90f, 0f, 1f) * (1f - rock * 0.6f);
                float sand = Math.Clamp((14f - height) / 10f, 0f, 1f);
                float grass = Math.Clamp(1f - rock - snow - sand, 0f, 1f);
                float total = MathF.Max(0.001f, grass + sand + rock + snow);
                terrain.SetSplat(x, z,
                    (byte)(grass / total * 255f), (byte)(sand / total * 255f),
                    (byte)(rock / total * 255f), (byte)(snow / total * 255f));
            }
        });
        return terrain;
    }

    /// <summary>
    /// Draws <paramref name="terrain"/> from a camera once its detail has finished streaming in.
    /// </summary>
    public (ImageMetrics Metrics, RenderStats Stats, TerrainLodStatistics Lod) CaptureLargeTerrain(
        string outputFile, TerrainAsset terrain, Vector3 eye, Vector3 target, float detail = 1f)
    {
        EnsureReady();
        IRenderController renderer = _viewport.Renderer ?? throw new InvalidOperationException("Renderer is unavailable.");
        if (!ReferenceEquals(_largeTerrain, terrain) || !ReferenceEquals(_largeGroundRenderer, renderer))
        {
            _largeGround?.Dispose();
            _largeGround = new AuthoredTerrainGround(terrain);
            _largeGround.Bind(renderer, TextureHandle.Invalid, 1f);
            _largeTerrain = terrain;
            _largeGroundRenderer = renderer;
            _largeDraws = new MeshDrawCall[_largeGround.MeshCount + 16];
        }

        _largeEye = eye;
        _largeTarget = target;
        _largeDetail = detail;
        _mode = CaptureMode.LargeTerrain;
        _largeGround!.SettleLevelOfDetail(LargeTerrainView());
        ImageMetrics metrics = Capture(outputFile, minimumUniqueColors: 4);
        return (metrics, _lastStats, _largeGround.LodStatistics);
    }

    /// <summary>Pixel of a world point in the large terrain scene's camera.</summary>
    public System.Drawing.Point ProjectLargeTerrainPoint(Vector3 world)
    {
        (Matrix4x4 view, Matrix4x4 projection) = LargeTerrainCamera();
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        return new System.Drawing.Point(
            (int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * _viewport.ClientWidth),
            (int)MathF.Round((0.5f - clip.Y / clip.W * 0.5f) * _viewport.ClientHeight));
    }

    private (Matrix4x4 View, Matrix4x4 Projection) LargeTerrainCamera() =>
        (Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(_largeEye, _largeTarget, Vector3.UnitY),
         Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
             MathF.PI / 3f,
             _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
             0.5f,
             LargeTerrainFarPlane));

    private TerrainLodView LargeTerrainView()
    {
        (Matrix4x4 view, Matrix4x4 projection) = LargeTerrainCamera();
        return new TerrainLodView(_largeEye, view * projection, _largeDetail);
    }

    private void RenderLargeTerrain(IRenderController renderer)
    {
        if (_largeGround is null) return;
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.55f, 0.72f, 0.92f, 1f);
        (Matrix4x4 view, Matrix4x4 projection) = LargeTerrainCamera();
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = Vector3.Normalize(new Vector3(-0.45f, -0.75f, 0.35f));
        state.SunColor = new Vector3(1f, 0.96f, 0.88f);
        state.SunIntensity = 1.25f;
        state.AmbientColor = new Vector3(0.42f, 0.48f, 0.56f);
        state.AmbientGroundColor = new Vector3(0.22f, 0.2f, 0.17f);
        state.FrustumCullingEnabled = true;
        state.ShadowsEnabled = false;
        state.ShowSunVisual = false;
        state.ShowFloor = false;
        state.FogEnabled = false;
        state.BackgroundColor = new Vector3(0.55f, 0.72f, 0.92f);
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();

        int count = 0;
        _largeGround.AppendDrawCalls(_largeDraws, ref count, LargeTerrainView(), MeshDrawFlags.NoShadow);
        for (int i = 0; i < count; i++) renderer.DrawMesh(_largeDraws[i]);

        SetCaptureBadge(
            "ENGINE LARGE TERRAIN",
            "Quadtree level of detail across a multi-kilometre heightfield.");
    }
}

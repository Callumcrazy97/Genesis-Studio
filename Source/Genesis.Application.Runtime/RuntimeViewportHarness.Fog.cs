using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

/// <summary>
/// Volumetric fog scene: a camera looking down a foggy corridor at a far wall, with dark panels at
/// ground level and high up, a no-depth-write twin of the low panel, and an optional roof slab
/// casting the sun's shadow across the near fog. Tests compare fog amounts at known points.
/// </summary>
public sealed partial class RuntimeViewportHarness
{
    private bool _fogEnabled;
    private bool _fogShadows;
    private bool _fogRoof;

    public static readonly Vector3 FogLowPanel = new(-5f, 1f, 45f);
    public static readonly Vector3 FogHighPanel = new(5f, 16f, 45f);
    public static readonly Vector3 FogSurfacePanel = new(-5f, 4f, 45f);
    public static readonly Vector3 FogPostPanel = new(5f, 4f, 45f);

    /// <summary>A point on the far wall seen through the fog under the roof slab.</summary>
    public static readonly Vector3 FogUnderRoof = new(0f, 2f, 120f);

    public ImageMetrics CaptureFogScene(string outputFile, bool fog, bool shadows, bool roof)
    {
        EnsureReady();
        EnsureCube();
        _fogEnabled = fog;
        _fogShadows = shadows;
        _fogRoof = roof;
        _mode = CaptureMode.Fog;
        return Capture(outputFile, minimumUniqueColors: 2);
    }

    public Point ProjectFogPoint(Vector3 world)
    {
        (Matrix4x4 view, Matrix4x4 projection) = FogCamera();
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        return new Point(
            (int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * _viewport.ClientWidth),
            (int)MathF.Round((0.5f - clip.Y / clip.W * 0.5f) * _viewport.ClientHeight));
    }

    private (Matrix4x4 View, Matrix4x4 Projection) FogCamera() =>
        (Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(
             new Vector3(0f, 6f, -10f), new Vector3(0f, 6f, 40f), Vector3.UnitY),
         Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
             MathF.PI / 3f,
             _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
             0.1f,
             400f));

    private void RenderFogScene(IRenderController renderer)
    {
        EnsureCube();
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.3f, 0.35f, 0.45f, 1f);
        (Matrix4x4 view, Matrix4x4 projection) = FogCamera();
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        // High sun slightly behind the camera, so the roof's shadow falls across the near fog.
        state.LightDirection = Vector3.Normalize(new Vector3(0.1f, -1f, 0.25f));
        state.SunColor = new Vector3(1f, 0.97f, 0.9f);
        state.SunIntensity = 1.2f;
        state.AmbientColor = new Vector3(0.25f, 0.28f, 0.32f);
        state.AmbientGroundColor = new Vector3(0.12f);
        state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = _fogShadows;
        state.ShadowOrthoSize = 60f;
        state.ShowSunVisual = false;
        state.ShowFloor = false;
        state.BackgroundColor = new Vector3(0.3f, 0.35f, 0.45f);
        state.FogEnabled = _fogEnabled;
        state.FogColor = new Vector4(0.75f, 0.78f, 0.82f, 1f);
        state.FogDensity = 0.012f;
        state.FogNoiseStrength = 0f;
        state.FogHeightBase = 0f;
        state.FogHeightFalloff = 0.12f;
        state.FogAerialBlend = 1f;
        state.VolumetricTemporalBlend = 1f; // stable analytic volume: captures are deterministic
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();

        void Box(Vector3 centre, Vector3 size, RenderColor tint, MeshDrawFlags flags) =>
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube,
                World = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre),
                Tint = tint,
                Alpha = 1f,
                Flags = flags | MeshDrawFlags.NoCull,
            });

        RenderColor dark = new(0.08f, 0.08f, 0.1f);
        Box(new Vector3(0f, -0.5f, 70f), new Vector3(80f, 1f, 180f), new RenderColor(0.3f, 0.3f, 0.3f), MeshDrawFlags.NoShadow);
        Box(new Vector3(0f, 20f, 150f), new Vector3(120f, 40f, 1f), dark, MeshDrawFlags.NoShadow);
        Box(FogLowPanel, new Vector3(4f, 2f, 0.5f), dark, MeshDrawFlags.NoShadow);
        Box(FogHighPanel, new Vector3(4f, 2f, 0.5f), dark, MeshDrawFlags.NoShadow);
        Box(FogPostPanel, new Vector3(3f, 2f, 0.5f), dark, MeshDrawFlags.NoShadow);
        // Same material, but drawn without depth: the forward pass fogs it itself.
        Box(FogSurfacePanel, new Vector3(3f, 2f, 0.5f), dark, MeshDrawFlags.NoShadow | MeshDrawFlags.NoDepthWrite);
        if (_fogRoof)
            Box(new Vector3(0f, 12f, 40f), new Vector3(60f, 1f, 60f), dark, MeshDrawFlags.None);

        SetCaptureBadge("ENGINE VOLUMETRIC FOG", "Froxel fog: height falloff, shafts, consistent surfaces.");
    }
}

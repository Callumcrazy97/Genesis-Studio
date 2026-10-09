using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

/// <summary>
/// A lit stage for material and shader checks: a grey floor under a sun the caller aims (with or
/// without shadows, with an optional lamp), seen from a fixed camera, and whatever the caller
/// draws on it each frame. Tests project world points to pixels to compare colours.
/// </summary>
public sealed partial class RuntimeViewportHarness
{
    private Action<IRenderController>? _litSceneDraw;
    private Vector3 _litSceneSun = Vector3.Normalize(new Vector3(0.3f, -0.7f, 1f));
    private bool _litSceneShadows;
    private PointLight3D? _litSceneLamp;

    /// <summary>The camera of <see cref="CaptureLitScene"/>.</summary>
    public static readonly Vector3 LitSceneEye = new(0f, 2.6f, -6.5f);

    public static readonly Vector3 LitSceneTarget = new(0f, 0.8f, 0f);

    /// <summary>The unit cube mesh, for draws on the lit stage.</summary>
    public MeshHandle LitSceneCube
    {
        get
        {
            EnsureReady();
            EnsureCube();
            return _cube;
        }
    }

    /// <param name="sunDirection">The direction the sunlight travels.</param>
    /// <param name="draw">Called in every frame the capture renders, after the floor.</param>
    public ImageMetrics CaptureLitScene(string outputFile, Vector3 sunDirection, bool shadows,
        Action<IRenderController> draw, PointLight3D? lamp = null)
    {
        EnsureReady();
        EnsureCube();
        _litSceneSun = sunDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(sunDirection) : -Vector3.UnitY;
        _litSceneShadows = shadows;
        _litSceneLamp = lamp;
        _litSceneDraw = draw;
        _mode = CaptureMode.LitScene;
        try
        {
            return Capture(outputFile, minimumUniqueColors: 2);
        }
        finally
        {
            _litSceneDraw = null;
        }
    }

    public Point ProjectLitScenePoint(Vector3 world)
    {
        (Matrix4x4 view, Matrix4x4 projection) = LitSceneCamera();
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        return new Point(
            (int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * _viewport.ClientWidth),
            (int)MathF.Round((0.5f - clip.Y / clip.W * 0.5f) * _viewport.ClientHeight));
    }

    /// <summary>The lit stage's camera, for passes (culling, the object draw pass) that need it.</summary>
    public (Matrix4x4 View, Matrix4x4 Projection) LitSceneCamera() =>
        (Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(LitSceneEye, LitSceneTarget, Vector3.UnitY),
         Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
             MathF.PI * 50f / 180f,
             _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
             0.1f,
             100f));

    private void RenderLitScene(IRenderController renderer)
    {
        EnsureCube();
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.42f, 0.52f, 0.66f, 1f);
        (Matrix4x4 view, Matrix4x4 projection) = LitSceneCamera();
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = _litSceneSun;
        state.SunColor = new Vector3(1f, 0.97f, 0.92f);
        state.SunIntensity = 1.3f;
        state.AmbientColor = new Vector3(0.30f, 0.33f, 0.38f);
        state.AmbientGroundColor = new Vector3(0.12f, 0.12f, 0.13f);
        state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = _litSceneShadows;
        state.ShadowOrthoSize = 12f;
        state.ShowSunVisual = false;
        state.ShowFloor = false;
        state.FogEnabled = false;
        state.BackgroundColor = new Vector3(0.42f, 0.52f, 0.66f);
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();
        if (_litSceneLamp is PointLight3D lamp)
            renderer.AddPointLight(lamp.Position, lamp.Color, lamp.Radius, lamp.Intensity, lamp.Falloff);

        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = Matrix4x4.CreateScale(14f, 0.5f, 14f) * Matrix4x4.CreateTranslation(0f, -0.25f, 0f),
            Tint = new RenderColor(0.55f, 0.56f, 0.58f),
            Alpha = 1f,
            Flags = MeshDrawFlags.None,
        });
        _litSceneDraw?.Invoke(renderer);
    }
}

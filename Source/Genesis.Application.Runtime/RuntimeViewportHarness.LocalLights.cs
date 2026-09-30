using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

/// <summary>
/// Local-light shadow scene: a dark, sunless room lit only by point lights on a ring and/or one
/// downward spot light, each with an occluder placed so its shadow falls on a known floor point.
/// Tests compare captures with and without the occluders at those points.
/// </summary>
public sealed partial class RuntimeViewportHarness
{
    private int _localPointLights;
    private bool _localSpot;
    private bool _localCasters;

    /// <summary>Ring positions of the scene's point lights; the matching occluder sits one unit inward.</summary>
    public static Vector3 LocalLightPosition(int index) => index switch
    {
        0 => new Vector3(3f, 1.2f, 0f),
        1 => new Vector3(0f, 1.2f, 3f),
        2 => new Vector3(-3f, 1.2f, 0f),
        _ => new Vector3(0f, 1.2f, -3f),
    };

    /// <summary>Floor point shadowed by point light <paramref name="index"/>'s occluder (two units inward).</summary>
    public static Vector3 LocalShadowPoint(int index) => LocalLightPosition(index) * new Vector3(1f / 3f, 0f, 1f / 3f);

    public static readonly Vector3 SpotLightPosition = new(0f, 4f, 0f);

    /// <summary>
    /// A floor point in the spot occluder's shadow that the camera can see past the occluder
    /// (the cube sits off-axis at x = 1 so it does not hide its own shadow from this view).
    /// </summary>
    public static readonly Vector3 SpotShadowPoint = new(1.55f, 0f, 0f);

    /// <summary>
    /// Renders the local-light scene. <paramref name="pointLights"/> ring lights (0–4) each get an
    /// occluder pillar between the light and the room centre; <paramref name="spotLight"/> adds a
    /// downward 25° spot with a small cube under it. <paramref name="casters"/> false removes every
    /// occluder, so the same floor points can be measured lit.
    /// </summary>
    public (ImageMetrics Metrics, RenderStats Stats) CaptureLocalLights(
        string outputFile, int pointLights, bool spotLight, bool casters)
    {
        EnsureReady();
        EnsureCube();
        _localPointLights = Math.Clamp(pointLights, 0, 4);
        _localSpot = spotLight;
        _localCasters = casters;
        _mode = CaptureMode.LocalLights;
        ImageMetrics metrics = Capture(outputFile, minimumUniqueColors: 2);
        return (metrics, _lastStats);
    }

    /// <summary>Pixel of a world point in the local-light scene's camera.</summary>
    public Point ProjectLocalLightsPoint(Vector3 world)
    {
        (Matrix4x4 view, Matrix4x4 projection) = LocalLightsCamera();
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        float x = clip.X / clip.W;
        float y = clip.Y / clip.W;
        return new Point(
            (int)MathF.Round((x * 0.5f + 0.5f) * _viewport.ClientWidth),
            (int)MathF.Round((0.5f - y * 0.5f) * _viewport.ClientHeight));
    }

    // The engine's own left-handed convention: with it, SV_IsFrontFace (and so the lit side of the
    // double-sided floor) needs no winding override.
    private (Matrix4x4 View, Matrix4x4 Projection) LocalLightsCamera() =>
        (Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(
             new Vector3(0f, 10f, 6f), new Vector3(0f, 0f, 0.5f), Vector3.UnitY),
         Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
             MathF.PI / 3f,
             _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
             0.1f,
             100f));

    private void RenderLocalLights(IRenderController renderer)
    {
        EnsureCube();
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.01f, 0.01f, 0.015f, 1f);
        (Matrix4x4 view, Matrix4x4 projection) = LocalLightsCamera();
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = -Vector3.UnitY;
        state.SunIntensity = 0f;
        state.AmbientColor = new Vector3(0.02f);
        state.AmbientGroundColor = new Vector3(0.02f);
        state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = true;
        state.ShowSunVisual = false;
        state.ShowFloor = false;
        state.BackgroundColor = new Vector3(0.01f, 0.01f, 0.015f);
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();

        // Receiver only: a light grey slab under the whole room.
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = Matrix4x4.CreateScale(16f, 0.2f, 16f) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f),
            Tint = new RenderColor(0.8f, 0.8f, 0.8f),
            Alpha = 1f,
            Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull,
        });

        for (int i = 0; i < _localPointLights; i++)
        {
            Vector3 light = LocalLightPosition(i);
            renderer.AddPointLight(light, new Vector3(1f, 0.9f, 0.75f), radius: 6f, intensity: 3f);
            if (!_localCasters) continue;
            Vector3 pillar = light * new Vector3(2f / 3f, 0f, 2f / 3f) + new Vector3(0f, 1f, 0f);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube,
                World = Matrix4x4.CreateScale(0.4f, 2f, 0.4f) * Matrix4x4.CreateTranslation(pillar),
                Tint = new RenderColor(0.5f, 0.5f, 0.55f),
                Alpha = 1f,
                Flags = MeshDrawFlags.NoCull,
            });
        }

        if (_localSpot)
        {
            renderer.AddSpotLight(
                SpotLightPosition, -Vector3.UnitY, new Vector3(0.8f, 0.9f, 1f),
                radius: 8f, intensity: 4f, innerAngleDegrees: 15f, outerAngleDegrees: 25f);
            if (_localCasters)
            {
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = _cube,
                    World = Matrix4x4.CreateScale(0.6f) * Matrix4x4.CreateTranslation(1f, 1f, 0f),
                    Tint = new RenderColor(0.5f, 0.5f, 0.55f),
                    Alpha = 1f,
                    Flags = MeshDrawFlags.NoCull,
                });
            }
        }

        SetCaptureBadge(
            "ENGINE LOCAL LIGHT SHADOWS",
            "Point and spot lights share the local shadow atlas.");
    }
}

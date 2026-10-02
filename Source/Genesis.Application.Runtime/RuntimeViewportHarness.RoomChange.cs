using System.Numerics;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

public sealed partial class RuntimeViewportHarness
{
    private float _roomChangeProgress;
    private float _roomChangeStrength;

    /// <summary>
    /// The screen a room change shows while it is spread over frames, drawn the way the Player
    /// draws it: a plain red wall stands in for the room, then the cover goes over it at the given
    /// strength and, at full strength, the engine's progress bar and text go on the cover. At full
    /// strength none of the red may show; at part strength it shows through evenly.
    /// </summary>
    public ImageMetrics CaptureRoomChangeScreen(string outputFile, float progress, float strength)
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.RoomChangeScreen;
        _roomChangeProgress = progress;
        _roomChangeStrength = strength;
        return Capture(outputFile, minimumUniqueColors: 1);
    }

    private void RenderRoomChangeScene(IRenderController renderer)
    {
        renderer.SetRoomFog(RoomFogState.Disabled); renderer.Set3DFrameActive(true);
        renderer.Clear(0f, 0f, 0.2f, 1);
        float aspect = _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight);
        renderer.SetCamera3D(
            Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY),
            Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3, aspect, 0.1f, 500f));
        var state = Mesh3DState.Default;
        // Unlit and unfogged: every pixel of the wall is exactly red.
        state.LightingEnabled = false;
        state.ShadowsEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
        state.FogEnabled = false;
        state.FrustumCullingEnabled = false;
        renderer.SetMesh3DState(state); renderer.ClearPointLights();
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube, World = Matrix4x4.CreateScale(400f, 400f, 1f) * Matrix4x4.CreateTranslation(0f, 0f, 20f),
            Tint = new RenderColor(1f, 0f, 0f), Alpha = 1,
            Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
        });
    }

    private void ComposeRoomChangeScreen(IRenderController renderer) =>
        RoomChangeScreen.Compose(renderer, renderer.PixelWidth, renderer.PixelHeight,
            new RoomChangeProgress("Capture", _roomChangeProgress), _roomChangeStrength);
}

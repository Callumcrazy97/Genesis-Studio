using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

public sealed partial class RuntimeViewportHarness
{
    /// <summary>How far away the two surfaces of <see cref="CaptureDepthPrecision"/> cross.</summary>
    public const float DepthPrecisionDistance = 6000f;

    /// <summary>How fast the green surface comes towards the camera across the view: metres nearer per metre to the left.</summary>
    public const float DepthPrecisionSlope = 0.02f;

    /// <summary>
    /// Two flat surfaces six kilometres from a camera whose near plane is ten centimetres and far
    /// plane sixteen kilometres. The red one faces the camera squarely; the green one is turned a
    /// little, so it is in front on the left of the picture and behind on the right, and they
    /// cross down the middle. A depth buffer that can tell them apart shows green on the left,
    /// red on the right and a straight join at the centre. One that cannot shows the join well to
    /// one side, wherever its precision runs out.
    /// </summary>
    public ImageMetrics CaptureDepthPrecision(string outputFile)
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.DepthPrecision;
        return Capture(outputFile, minimumUniqueColors: 2);
    }

    private void RenderDepthPrecision(IRenderController renderer)
    {
        renderer.SetRoomFog(RoomFogState.Disabled); renderer.Set3DFrameActive(true);
        renderer.Clear(0f, 0f, 0.2f, 1);
        float aspect = _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight);
        renderer.SetCamera3D(
            Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY),
            Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3, aspect, 0.1f, 16000f));
        var state = Mesh3DState.Default;
        // Unlit and unfogged: a pixel is exactly the colour of the surface that won the depth test.
        state.LightingEnabled = false;
        state.ShadowsEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
        state.FogEnabled = false;
        state.FrustumCullingEnabled = false;
        state.CameraFarPlane = 16000f;
        renderer.SetMesh3DState(state); renderer.ClearPointLights();
        const MeshDrawFlags flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog;
        Matrix4x4 slab = Matrix4x4.CreateScale(15000f, 9000f, 1f);
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube, World = slab * Matrix4x4.CreateTranslation(0f, 0f, DepthPrecisionDistance),
            Tint = new RenderColor(1f, 0f, 0f), Alpha = 1, Flags = flags,
        });
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = slab * Matrix4x4.CreateRotationY(MathF.Atan(DepthPrecisionSlope)) * Matrix4x4.CreateTranslation(0f, 0f, DepthPrecisionDistance),
            Tint = new RenderColor(0f, 1f, 0f), Alpha = 1, Flags = flags,
        });
        SetCaptureBadge("ENGINE DEPTH PRECISION", "Two surfaces crossing 6 km away, near plane 10 cm.");
    }
}

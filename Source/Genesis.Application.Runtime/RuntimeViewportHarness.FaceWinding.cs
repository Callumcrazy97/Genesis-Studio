using System.Numerics;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

public sealed partial class RuntimeViewportHarness
{
    private MeshHandle _faceProbe;
    private FrontFaceWindingOverride _probeWinding;
    private bool _probeReflection, _probePerspective, _probeRuntimeCamera;
    private RenderDebugView _probeDebug;
    private bool _probeWireframe;
    private float _probeDistance = 4;
    private bool _twoSidedRightHanded;

    /// <summary>
    /// A two-sided (NoCull) slab lit by an overhead sun, seen from above through the engine's
    /// left-handed camera or an editor-style System.Numerics right-handed one. Both must light
    /// its top face; the forward shader flips back-face normals, so a wrong front-face winding
    /// leaves the slab ambient-only.
    /// </summary>
    public ImageMetrics CaptureTwoSidedLighting(string outputFile, bool rightHanded)
    {
        EnsureReady();
        EnsureCube();
        _twoSidedRightHanded = rightHanded;
        _mode = CaptureMode.TwoSidedLighting;
        return Capture(outputFile, minimumUniqueColors: 1);
    }

    private void RenderTwoSidedLighting(IRenderController renderer)
    {
        renderer.SetRoomFog(RoomFogState.Disabled); renderer.Set3DFrameActive(true);
        renderer.Clear(.05f, .05f, .08f, 1);
        Vector3 eye = new(0, 6, -6);
        float aspect = _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight);
        if (_twoSidedRightHanded)
            renderer.SetCamera3D(Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, aspect, .1f, 50));
        else
            renderer.SetCamera3D(Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(eye, Vector3.Zero, Vector3.UnitY),
                Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3, aspect, .1f, 50));
        var state = Mesh3DState.Default;
        state.LightingEnabled = true; state.LightingWeight = 1;
        state.LightDirection = Vector3.Normalize(new Vector3(0, -1, .2f));
        state.SunColor = Vector3.One; state.SunIntensity = 1.2f;
        state.AmbientColor = new Vector3(.08f); state.AmbientGroundColor = new Vector3(.04f);
        state.ShadowsEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
        state.FrustumCullingEnabled = false;
        renderer.SetMesh3DState(state); renderer.ClearPointLights();
        renderer.DrawMesh(new MeshDrawCall { Mesh = _cube, World = Matrix4x4.CreateScale(8, .1f, 8),
            Tint = new RenderColor(.7f, .7f, .7f), Alpha = 1, Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog });
        SetCaptureBadge("ENGINE TWO-SIDED LIGHTING", "A NoCull slab lit from overhead under either camera convention.");
    }

    /// <summary>Closed cube: the near/outside face is green and the far/inside face is red.</summary>
    public ImageMetrics CaptureFaceWinding(string outputFile, FrontFaceWindingOverride winding, bool reflected = false, bool perspective = false, bool runtimeCamera = false,
        RenderDebugView debug = RenderDebugView.Shaded, bool wireframe = false, float distance = 4)
    {
        EnsureReady();
        if (!_faceProbe.IsValid)
        {
            var (vertices, indices) = MeshGeometry.BuildCube(RenderColor.White, 2);
            for (int i = 0; i < vertices.Length; i++)
                vertices[i].Color = vertices[i].Normal.Z > .5f ? new Vector4(0, 1, 0, 1)
                    : vertices[i].Normal.Z < -.5f ? new Vector4(1, 0, 0, 1) : new Vector4(0, 0, 1, 1);
            _faceProbe = Renderer.RegisterMesh(vertices, indices);
        }
        _probeWinding = winding; _probeReflection = reflected; _probePerspective = perspective; _probeRuntimeCamera = runtimeCamera;
        _probeDebug = debug; _probeWireframe = wireframe; _probeDistance = distance;
        _mode = CaptureMode.FaceWinding;
        return Capture(outputFile, minimumUniqueColors: 2);
    }

    private void RenderFaceWinding(IRenderController renderer)
    {
        renderer.SetRoomFog(RoomFogState.Disabled); renderer.Set3DFrameActive(true);
        renderer.Clear(.02f, .02f, .02f, 1);
        if (_probeRuntimeCamera)
        {
            var camera = new Genesis.Runtime.Scene.Camera3D
            {
                Position = new Vector3(0, 0, 4), Pitch = 0, Yaw = 0,
                FieldOfView = MathF.PI / 3, AspectRatio = 1, NearPlane = .1f, FarPlane = 20,
            };
            renderer.SetCamera3D(camera.ViewMatrix, _probePerspective ? camera.ProjectionMatrix
                : Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateOrthographicLh(4, 4, .1f, 20));
        }
        else
            renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0, 0, _probeDistance), Vector3.Zero, Vector3.UnitY),
                _probePerspective ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1, .1f, 20)
                    : Matrix4x4.CreateOrthographic(4, 4, .1f, 20));
        var state = Mesh3DState.Default;
        MeshRasterDefaults.Apply(ref state);
        state.LightingEnabled = false; state.ShadowsEnabled = false; state.ShowSunVisual = false;
        state.DebugView = _probeDebug; state.Wireframe = _probeWireframe;
        state.FrustumCullingEnabled = false; renderer.SetMesh3DState(state); renderer.ClearPointLights();
        renderer.DrawMesh(new MeshDrawCall { Mesh = _faceProbe, World = Matrix4x4.CreateScale(_probeReflection ? -1 : 1, 1, 1),
            Tint = RenderColor.White, Alpha = 1, Emissive = 1,
            Flags = MeshRasterDefaults.ApplyOverride(MeshDrawFlags.Emissive | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                FaceCullingOverride.Back, _probeWinding) });
    }
}

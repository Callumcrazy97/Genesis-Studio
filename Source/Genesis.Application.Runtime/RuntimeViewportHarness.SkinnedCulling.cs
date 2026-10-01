using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

/// <summary>
/// Skinned culling scene: one small skinned mesh modelled at head height, a pose that seats it half
/// a metre lower, and a camera one metre from the seated position with frustum culling on. The
/// bind position is off screen, so the mesh is only drawn if it is culled by where its pose puts it.
/// </summary>
public sealed partial class RuntimeViewportHarness
{
    private MeshHandle _skinnedCullMesh;
    private SkinPaletteHandle _skinnedCullPalette;
    private bool _skinnedCullPosed;

    /// <summary>Where the mesh was modelled (its bind pose).</summary>
    public static readonly Vector3 SkinnedBindCentre = new(0f, 1.7f, 0f);

    /// <summary>Where the seated pose moves it, and where the camera looks.</summary>
    public static readonly Vector3 SkinnedSeatedCentre = new(0f, 1.2f, 0f);

    /// <summary>Unlit colour of the skinned mesh.</summary>
    public static readonly Vector3 SkinnedMeshColour = new(0.95f, 0.55f, 0.1f);

    /// <summary>
    /// Renders the scene with the mesh in its bind pose (<paramref name="posed"/> false, off screen)
    /// or seated in front of the camera (<paramref name="posed"/> true).
    /// </summary>
    public (ImageMetrics Metrics, RenderStats Stats) CaptureSkinnedCulling(string outputFile, bool posed)
    {
        EnsureReady();
        _skinnedCullPosed = posed;
        _mode = CaptureMode.SkinnedCulling;
        ImageMetrics metrics = Capture(outputFile, minimumUniqueColors: 1);
        return (metrics, _lastStats);
    }

    /// <summary>Pixel of a world point in the skinned culling scene's camera.</summary>
    public Point ProjectSkinnedCullingPoint(Vector3 world)
    {
        (Matrix4x4 view, Matrix4x4 projection) = SkinnedCullingCamera();
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        return new Point(
            (int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * _viewport.ClientWidth),
            (int)MathF.Round((0.5f - clip.Y / clip.W * 0.5f) * _viewport.ClientHeight));
    }

    // A 30 degree lens one metre away sees about 0.27 m above and below the seated position, so the
    // bind position half a metre higher is outside the view.
    private (Matrix4x4 View, Matrix4x4 Projection) SkinnedCullingCamera() =>
        (Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(
             SkinnedSeatedCentre - Vector3.UnitZ, SkinnedSeatedCentre, Vector3.UnitY),
         Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
             MathF.PI / 6f,
             _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
             0.05f,
             100f));

    private void EnsureSkinnedCullingMesh(IRenderController renderer)
    {
        if (_skinnedCullMesh.IsValid && _skinnedCullPalette.IsValid)
        {
            return;
        }

        // A cube 0.24 m across, every vertex weighted wholly to joint 1.
        const float half = 0.12f;
        SkinnedMeshVertex[] vertices = new SkinnedMeshVertex[8];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 corner = new((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half, (i & 4) == 0 ? -half : half);
            vertices[i] = new SkinnedMeshVertex
            {
                Position = SkinnedBindCentre + corner,
                Normal = Vector3.Normalize(corner),
                Color = Vector4.One,
                JointIndices = new Vector4(1f, 0f, 0f, 0f),
                JointWeights = new Vector4(1f, 0f, 0f, 0f),
            };
        }

        ushort[] indices =
        [
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6,
            0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7,
            0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        ];
        _skinnedCullMesh = renderer.RegisterSkinnedMesh(vertices, indices);
        _skinnedCullPalette = renderer.CreateSkinPalette(2);
    }

    private void RenderSkinnedCulling(IRenderController renderer)
    {
        EnsureSkinnedCullingMesh(renderer);
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.02f, 0.03f, 0.06f, 1f);
        (Matrix4x4 view, Matrix4x4 projection) = SkinnedCullingCamera();
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = false;
        state.FrustumCullingEnabled = true; // the subject of the scene
        state.ShadowsEnabled = false;
        state.ShowSunVisual = false;
        state.ShowFloor = false;
        state.BackgroundColor = new Vector3(0.02f, 0.03f, 0.06f);
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();

        Matrix4x4[] palette =
        [
            Matrix4x4.Identity,
            _skinnedCullPosed
                ? Matrix4x4.CreateTranslation(SkinnedSeatedCentre - SkinnedBindCentre)
                : Matrix4x4.Identity,
        ];
        renderer.UpdateSkinPalette(_skinnedCullPalette, palette);
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _skinnedCullMesh,
            SkinPalette = _skinnedCullPalette,
            World = Matrix4x4.Identity,
            Tint = new RenderColor(SkinnedMeshColour.X, SkinnedMeshColour.Y, SkinnedMeshColour.Z),
            Alpha = 1f,
            Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow,
        });

        SetCaptureBadge(
            "ENGINE SKINNED CULLING",
            "A posed skinned mesh is culled by where its pose puts it.");
    }
}

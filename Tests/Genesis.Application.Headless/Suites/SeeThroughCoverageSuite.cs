using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Core;
using Genesis.Rendering.Meshes;
using Genesis.Runtime;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// See-through surfaces in a room drawn through its 3D viewports (C2, Nuvema Town's dark sea).
/// </summary>
/// <remarks>
/// Nuvema Town's sea is an opaque sliding sea under a half-opaque copy of it, both parts of one
/// skinned map model, and the room draws through two viewports. On Direct3D and OpenGL the sea came
/// out at half brightness: a see-through draw replaced the picture's alpha with its own, and each
/// viewport's finished picture is laid on the window by that alpha, so half of the black behind the
/// view showed through. On Vulkan the sea looked right only because the see-through parts of a
/// skinned model were not drawn at all (read through the unskinned vertex layout, whose stride
/// Vulkan takes for the buffer's). This draws the same arrangement from engine primitives: a blue
/// wall, a skinned see-through white layer over one quarter of it (modelled off screen and posed
/// into view) and an unskinned one over another.
/// </remarks>
internal static class SeeThroughCoverageSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    [
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    ];

    private const int Width = 640, Height = 480;

    // Window pixels: the wall alone, under the skinned layer, under the unskinned layer.
    private static readonly Point WallOnly = new(160, 120), UnderSkinned = new(480, 120), UnderPlain = new(160, 360);

    public static void ViewportKeepsCoverage(HeadlessContext ctx)
    {
        var results = new List<(RenderBackendOption Backend, Color Wall, Color Skinned, Color Plain)>();
        foreach (RenderBackendOption backend in GpuBackends)
        {
            (Color wall, Color skinned, Color plain) = Render(ctx, backend);
            results.Add((backend, wall, skinned, plain));
            HeadlessHarness.Assert(skinned.R > wall.R + 40,
                $"{backend}: the posed see-through part of a skinned model was not drawn over the wall (wall {wall}, under it {skinned}).");
            HeadlessHarness.Assert(plain.R > wall.R + 40,
                $"{backend}: the see-through layer was not drawn over the wall (wall {wall}, under it {plain}).");
            // White over blue can only brighten. Half coverage halved it towards the black behind the view.
            HeadlessHarness.Assert(Luma(skinned) > Luma(wall) + 25 && Luma(plain) > Luma(wall) + 25,
                $"{backend}: a see-through layer darkened the view (wall {wall}, skinned layer {skinned}, plain layer {plain}).");
            HeadlessHarness.Assert(Distance(skinned, plain) <= 12,
                $"{backend}: the skinned and unskinned see-through layers differ ({skinned} against {plain}).");
        }

        // Every backend shows the same picture as Direct3D 11, within a tone curve's rounding.
        var reference = results[0];
        foreach (var result in results.Skip(1))
            HeadlessHarness.Assert(Distance(result.Skinned, reference.Skinned) <= 16 && Distance(result.Plain, reference.Plain) <= 16,
                $"{result.Backend} differs from {reference.Backend} under the see-through layers ({result.Skinned}/{result.Plain} against {reference.Skinned}/{reference.Plain}).");
    }

    private static (Color Wall, Color Skinned, Color Plain) Render(HeadlessContext ctx, RenderBackendOption backend)
    {
        using var host = UnattendedWindowing.NewHost(Width, Height);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(backend);
        renderer.Initialize(host.Handle, Width, Height);
        using var scene = new RuntimeScene();
        RoomAsset room = RoomAsset.Create("See-through", RoomDimension.ThreeD);
        room.Environment.DynamicSky = false;
        RoomSceneBuilder.ApplySceneSettings(scene, room);
        scene.Environment.FogEnabled = false;
        using var presentation = new RoomRenderSubsystem("", room);
        for (int index = 0; index < room.Viewports.Count; index++) room.Viewports[index].Enabled = index == 0;
        RoomViewport port = room.Viewports[0];
        port.PortX = 0; port.PortY = 0; port.PortWidth = Width; port.PortHeight = Height;
        port.SourceX = 0; port.SourceY = 0; port.SourceZ = 6; port.FieldOfView = 45;
        presentation.SetViewportPosition(0, new Vector3(0, 0, 6));

        var (cubeVertices, cubeIndices) = MeshGeometry.BuildCube(RenderColor.White, 1f);
        MeshHandle cube = renderer.RegisterMesh(cubeVertices, cubeIndices);
        // A quad over the top right quarter of the view, modelled fifty metres above it and posed
        // down into it by joint 1.
        const float lift = 50f;
        SkinnedMeshVertex Corner(float x, float y) => new()
        {
            Position = new Vector3(x, y + lift, 0f), Normal = Vector3.UnitZ, Color = Vector4.One,
            UV = new Vector2(x / 10f, y / 10f),
            JointIndices = new Vector4(1f, 0f, 0f, 0f), JointWeights = new Vector4(1f, 0f, 0f, 0f),
        };
        MeshHandle layer = renderer.RegisterSkinnedMesh([Corner(0.2f, 0.2f), Corner(10f, 0.2f), Corner(10f, 10f), Corner(0.2f, 10f)], [0, 1, 2, 0, 2, 3]);
        SkinPaletteHandle palette = renderer.CreateSkinPalette(2);
        try
        {
            renderer.BeginFrame();
            renderer.Clear(0.01f, 0.01f, 0.01f, 1f);
            HeadlessHarness.Assert(presentation.RenderViewports3D(scene, renderer, () =>
            {
                renderer.SetCamera3D(scene.Camera3D.ViewMatrix, scene.Camera3D.ProjectionMatrix);
                Mesh3DState state = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new(), false, RenderDebugView.Shaded);
                state.LightingEnabled = false; state.ShadowsEnabled = false; state.FogEnabled = false;
                renderer.SetMesh3DState(state);
                renderer.Set3DFrameActive(true);
                renderer.UpdateSkinPalette(palette, [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, -lift, 0f)]);
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = cube, World = Matrix4x4.CreateScale(30f, 30f, 0.1f) * Matrix4x4.CreateTranslation(0f, 0f, -2f),
                    Tint = new RenderColor(0.1f, 0.35f, 0.8f), Alpha = 1f, Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                });
                const MeshDrawFlags seeThrough = MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog;
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = layer, SkinPalette = palette, World = Matrix4x4.Identity,
                    Tint = RenderColor.White, Alpha = 0.5f, Flags = seeThrough | MeshDrawFlags.NoCull,
                });
                // The same layer unskinned, over the bottom left quarter: a box seen from the front only.
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = cube, World = Matrix4x4.CreateScale(9.6f, 9.6f, 0.02f) * Matrix4x4.CreateTranslation(-5f, -5f, 0f),
                    Tint = RenderColor.White, Alpha = 0.5f, Flags = seeThrough,
                });
            }), "The room's 3D viewport was not rendered.");
            renderer.EndFrame();
            HeadlessHarness.Assert(renderer.TryReadFramePixels(out int width, out int height, out byte[] pixels), "Could not read the window.");
            string name = $"see-through-coverage-{backend.ToString().ToLowerInvariant()}.png";
            using (Bitmap image = new(width, height, PixelFormat.Format32bppArgb))
            {
                BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
                try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { image.UnlockBits(data); }
                image.Save(Path.Combine(ctx.Captures, name));
            }
            ctx.Report.Images.Add(new ImageResult("See-through layers in a room viewport", name, width, height, 0, 0));
            renderer.Present();
            Color At(Point p)
            {
                int offset = (p.Y * width + p.X) * 4;
                return Color.FromArgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            }
            return (At(WallOnly), At(UnderSkinned), At(UnderPlain));
        }
        finally
        {
            renderer.ReleaseMesh(cube);
            renderer.ReleaseMesh(layer);
            renderer.ReleaseSkinPalette(palette);
        }
    }

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}

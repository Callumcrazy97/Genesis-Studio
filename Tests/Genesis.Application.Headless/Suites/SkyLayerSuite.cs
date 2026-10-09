using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The sky layer (DrawMeshSetSky): a script mesh drawn there sits behind the world (terrain in front
/// of it covers it, where the same mesh drawn in the world covers the terrain), follows the camera,
/// is not clipped however far out it is placed, and draws nothing differently for a game that does
/// not use it; on every renderer.
/// </summary>
internal static class SkyLayerSuite
{
    private const int Width = 640, Height = 360;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "SkyLayer");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.SkyLayer.OptionReachesTheDrawAndResets", () =>
        {
            PgslContext context = new();
            PgslContext? previous = PgslCommands.BindContext(context);
            try
            {
                PgslCommands.DrawMeshSetSky(true);
                HeadlessHarness.Assert(context.MeshDrawOptions.Sky, "DrawMeshSetSky(true) did not set the draw option.");
                PgslCommands.DrawMeshResetState();
                HeadlessHarness.Assert(!context.MeshDrawOptions.Sky, "DrawMeshResetState did not clear the sky layer.");
                // With no surface (outside a Draw event) the draws do nothing and do not throw.
                PgslCommands.DrawMeshSetSky(true);
                PgslCommands.DrawMesh3D(12345, 0, 0, 10, "");
            }
            finally { PgslCommands.BindContext(previous); }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.SkyLayer.BehindTheWorldAroundTheCameraOnEveryBackend", () =>
        {
            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            PgslContext scriptContext = new() { RoomWidth = Width, RoomHeight = Height };
            int square = 0, wall = 0;
            float cameraX = 0f;
            bool useSky = true;
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false;
                state.LightingEnabled = false; state.ShadowsEnabled = false;
                state.BackgroundColor = new Vector3(0.2f, 0.4f, 0.9f);
                state.CameraFarPlane = 100f;
                renderer.SetMesh3DState(state);
                renderer.Clear(0.2f, 0.4f, 0.9f);
                renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(
                    Matrix4x4.CreateLookAt(new Vector3(cameraX, 1, 0), new Vector3(cameraX, 1, 10), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 100f));
                ScriptMeshes.BeginFrame();
                scriptContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, is3DActive: true);
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    PgslCommands.DrawMeshResetState();
                    PgslCommands.DrawMeshSetCull(false);
                    PgslCommands.DrawMeshSetShadows(false, false);
                    // The land: a green wall 20 ahead whose top is at eye height, so it covers the
                    // lower half of the view.
                    PgslCommands.DrawMesh3D(wall, cameraX, 0, 20, "");
                    // A red square straight ahead in the sky layer: half above the land, half behind it.
                    if (useSky) PgslCommands.DrawMeshSetSky(true);
                    PgslCommands.DrawMesh3DTransform(square, 0, 0, 10, 1, 1, 1, 0, "");
                    // The same far out past the far plane, a hundred times the size, up and to the
                    // left (the camera looks along +z, so +x is on the left of the screen).
                    PgslCommands.DrawMesh3DTransform(square, 300, 150, 1000, 100, 100, 100, 0, "");
                    PgslCommands.DrawMeshSetSky(false);
                    // A square in the world, 10 ahead and to the right: in front of the land.
                    PgslCommands.DrawMesh3D(square, cameraX - 3, 1, 10, "");
                }
                finally { PgslCommands.BindContext(previous); }
            };
            game.Controls.Add(viewport); GateSuite.ShowHost(game);

            PgslContext? setup = PgslCommands.BindContext(scriptContext);
            try
            {
                ScriptMeshes.Reset();
                square = (int)PgslCommands.MeshCreate();
                PgslCommands.MeshAddQuad(square, -1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0, 0, 0, -1, 0, 0, 1, 1, 255, 0, 0, 1);
                wall = (int)PgslCommands.MeshCreate();
                PgslCommands.MeshAddQuad(wall, -60, -60, 0, 60, -60, 0, 60, 1, 0, -60, 1, 0, 0, 0, -1, 0, 0, 1, 1, 0, 200, 0, 1);
            }
            finally { PgslCommands.BindContext(setup); }

            List<string> failures = [];
            List<string> readings = [];
            try
            {
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                    cameraX = 0f; useSky = true;
                    using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                    cameraX = 5f;
                    using Bitmap? moved = viewport.ReadbackFrameToBitmap(2);
                    cameraX = 0f; useSky = false;
                    using Bitmap? world = viewport.ReadbackFrameToBitmap(2);
                    useSky = true;
                    if (frame is null || moved is null || world is null || viewport.RenderFaultCount != 0)
                    { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                    string name = backend.ShortName.ToLowerInvariant();
                    frame.Save(Path.Combine(ctx.Captures, "sky-layer-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    ctx.Report.Images.Add(ImageResult.From("Runtime.SkyLayer." + backend.ShortName, "sky-layer-" + name + ".png", VisualCapture.Measure(frame)));
                    if (backend.Backend == RenderBackendOption.SilkNetDx11)
                    {
                        moved.Save(Path.Combine(ctx.Captures, "sky-layer-moved-dx11.png"), System.Drawing.Imaging.ImageFormat.Png);
                        world.Save(Path.Combine(ctx.Captures, "sky-layer-off-dx11.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }

                    void Expect(Bitmap image, string what, int x, int y, Func<Color, bool> test)
                    {
                        Color pixel = image.GetPixel(x, y);
                        readings.Add($"{backend.ShortName} {what}={pixel.R},{pixel.G},{pixel.B}");
                        if (!test(pixel)) failures.Add($"{backend.ShortName}: {what} at {x},{y} is {pixel}");
                    }
                    static bool Red(Color c) => c.R > 120 && c.R > c.G * 2 && c.R > c.B * 2;
                    static bool Green(Color c) => c.G > 90 && c.G > c.R * 2 && c.G > c.B * 2;
                    static bool Sky(Color c) => c.B > 120 && c.B > c.R * 1.5 && c.B > c.G;
                    // The sky square shows above the land and is hidden by it below the horizon (row 180).
                    Expect(frame, "skySquareAboveLand", 320, 165, Red);
                    Expect(frame, "landCoversSkySquare", 320, 200, Green);
                    // The world square 10 ahead covers the land.
                    Expect(frame, "worldSquareOverLand", 413, 200, Red);
                    // Far out and huge, drawn the same as near and small: not clipped by the far plane.
                    Expect(frame, "farSkySquare", 227, 133, Red);
                    Expect(frame, "skyAroundIt", 100, 40, Sky);
                    // The camera moved 5 to the right: the sky square stays straight ahead.
                    Expect(moved, "skySquareFollowsCamera", 320, 165, Red);
                    Expect(moved, "landStillCovers", 320, 200, Green);
                    // Off: the same draws are world meshes, the near square in front of the land.
                    Expect(world, "withoutSkyNearSquareOverLand", 320, 200, Red);
                }
            }
            finally
            {
                ScriptMeshes.Reset();
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "sky-layer-readings.txt"), readings);
            Console.WriteLine("SkyLayer readings: " + string.Join(", ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });
    }
}

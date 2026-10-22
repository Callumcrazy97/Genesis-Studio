using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Texture streaming by distance: a 2048 texture on a cube starts at its 512 level, takes its full
/// size when the camera comes up to the cube, and goes back to 512 once it is far away and the
/// textures are over budget, on each GPU renderer.
/// </summary>
internal static class TextureStreamingSuite
{
    private const int Width = 640, Height = 360;

    private sealed class Scene
    {
        public string Path = "";
        public TextureHandle Texture;
        public float Distance = 40f;
    }

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.TextureStreaming.DetailFollowsDistanceAndTheBudget", () =>
        {
            string folder = Path.Combine(ctx.Workspace, "TextureStreaming");
            Directory.CreateDirectory(folder);
            var scene = new Scene { Path = Path.Combine(folder, "checks-2048.png") };
            using (var picture = new Bitmap(2048, 2048, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(picture))
                {
                    g.Clear(Color.White);
                    for (int y = 0; y < 2048; y += 64)
                        for (int x = (y / 64 % 2) * 64; x < 2048; x += 128)
                            g.FillRectangle(Brushes.DarkSlateBlue, x, y, 64, 64);
                }
                picture.Save(scene.Path, ImageFormat.Png);
            }

            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false;
                state.LightingEnabled = false; state.ShadowsEnabled = false;
                renderer.SetMesh3DState(state);
                renderer.Clear(0f, 0f, 0f);
                renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(
                    Matrix4x4.CreateLookAt(new Vector3(0f, 0f, -scene.Distance), Vector3.Zero, Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, Width / (float)Height, 0.05f, 200f));
                if (!scene.Texture.IsValid)
                    scene.Texture = renderer.LoadTextureInBackground(scene.Path, TextureColorSpace.Srgb, out _);
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = renderer.GetBuiltinMesh(BuiltinMeshKind.Cube),
                    Texture = scene.Texture,
                    World = Matrix4x4.Identity,
                    Tint = new RenderColor(1f, 1f, 1f, 1f),
                });
            };
            game.Controls.Add(viewport);
            GateSuite.ShowHost(game);

            List<string> failures = [];
            List<string> readings = [];
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                if (backend.Backend == RenderBackendOption.Software) continue;
                scene.Texture = TextureHandle.Invalid;
                scene.Distance = 40f;
                viewport.BackendOverride = backend.Backend;
                GateSuite.Pump(3, 15);
                var renderer = viewport.Renderer as GpuRenderController;
                if (renderer == null) { failures.Add(backend.ShortName + ": no GPU renderer"); continue; }
                renderer.TextureStreamingBudgetBytes = 1L << 40;
                renderer.TextureStreamingDropSeconds = 0;

                // Renders until the texture's level is what is wanted (it is read on a worker), or a time limit.
                int LevelAfter(Func<int, bool> reached)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    int level = -1;
                    while (clock.Elapsed.TotalSeconds < 20)
                    {
                        viewport.ReadbackFrameToBitmap(1)?.Dispose();
                        level = renderer.StreamedTextureLevel(scene.Texture);
                        if (reached(level)) break;
                        Thread.Sleep(10);
                    }
                    return level;
                }

                int start = LevelAfter(level => level >= 0);
                scene.Distance = 1.3f;
                int near = LevelAfter(level => level == 0);
                using (Bitmap? close = viewport.ReadbackFrameToBitmap(2))
                    close?.Save(Path.Combine(ctx.Captures, $"stream-near-{backend.ShortName.ToLowerInvariant()}.png"), ImageFormat.Png);
                long nearBytes = renderer.StreamedTextureBytes;
                scene.Distance = 40f;
                renderer.TextureStreamingBudgetBytes = 0;
                int far = LevelAfter(level => level == 2);
                readings.Add($"{backend.ShortName}: start {start}, near {near} ({nearBytes / 1024} KB), far over budget {far} ({renderer.StreamedTextureBytes / 1024} KB)");
                if (start != 2) failures.Add($"{backend.ShortName}: the 2048 texture started at level {start}, not 2 (512)");
                if (near != 0) failures.Add($"{backend.ShortName}: close up the texture stayed at level {near}, not 0 (2048)");
                if (far != 2) failures.Add($"{backend.ShortName}: far away over budget the texture is at level {far}, not 2");
                if (viewport.RenderFaultCount != 0) failures.Add($"{backend.ShortName}: render faults: {viewport.LastRenderException}");
            }
            Console.WriteLine("Texture streaming readings: " + string.Join(" | ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join("; ", failures) + " | " + string.Join(" | ", readings));
        });
    }
}

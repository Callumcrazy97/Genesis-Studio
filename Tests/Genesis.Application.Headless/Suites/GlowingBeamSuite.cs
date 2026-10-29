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
/// DrawBeam3D: a thin glowing line between two points, drawn unlit and added to what is behind it
/// (tracers, lightning, lasers), on every renderer. A full-strength beam shows its colour, a half-strength
/// one about half of it, two beams that cross add up, and the pixels beside the beam are untouched.
/// </summary>
internal static class GlowingBeamSuite
{
    private const int Width = 640, Height = 360;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "GlowingBeam");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GlowingBeam.AddsItsColourToTheSceneOnEveryBackend", () =>
        {
            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            PgslContext scriptContext = new() { RoomWidth = Width, RoomHeight = Height };
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 1, 0), new Vector3(0, 1, 10), Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 100f);
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                // Colours as they are: the filmic curve turns a bright orange cream, as it does anything bright.
                state.NoToneMapping = true;
                state.BackgroundColor = new Vector3(0.02f, 0.03f, 0.08f);
                state.CameraFarPlane = 100f;
                renderer.SetMesh3DState(state);
                renderer.Clear(0.02f, 0.03f, 0.08f);
                renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(view, projection);
                scriptContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, is3DActive: true);
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    PgslCommands.DrawSetColorRgb(255, 200, 80);
                    PgslCommands.DrawSetAlpha(1);
                    // A full-strength beam across the middle of the view.
                    PgslCommands.DrawBeam3D(-3, 1, 8, 3, 1, 8, 0.2);
                    // Half strength, higher up, and a half-strength upright beam crossing it.
                    PgslCommands.DrawSetAlpha(0.5);
                    PgslCommands.DrawBeam3D(-3, 2, 8, 3, 2, 8, 0.2);
                    PgslCommands.DrawBeam3D(-1.5f, -1, 8, -1.5f, 4, 8, 0.2);
                    // A slanted beam, to see that it lies along its two ends.
                    PgslCommands.DrawSetAlpha(1);
                    PgslCommands.DrawSetColorRgb(80, 200, 255);
                    PgslCommands.DrawBeam3D(1, -0.5f, 8, 3, -0.5f + 2.5f, 8, 0.2);
                }
                finally { PgslCommands.BindContext(previous); }
            };
            game.Controls.Add(viewport); GateSuite.ShowHost(game);

            (int X, int Y) Screen(float x, float y, float z)
            {
                Vector4 clip = Vector4.Transform(new Vector4(x, y, z, 1f), view * projection);
                return ((int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * Width), (int)MathF.Round((-clip.Y / clip.W * 0.5f + 0.5f) * Height));
            }

            List<string> failures = [];
            List<string> readings = [];
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                if (frame is null || viewport.RenderFaultCount != 0) { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                string name = backend.ShortName.ToLowerInvariant();
                frame.Save(Path.Combine(ctx.Captures, "glowing-beam-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                ctx.Report.Images.Add(ImageResult.From("Runtime.GlowingBeam." + backend.ShortName, "glowing-beam-" + name + ".png", VisualCapture.Measure(frame)));

                Color At(string what, (int X, int Y) at)
                {
                    Color pixel = frame.GetPixel(Math.Clamp(at.X, 0, Width - 1), Math.Clamp(at.Y, 0, Height - 1));
                    readings.Add($"{backend.ShortName} {what}@{at.X},{at.Y}={pixel.R},{pixel.G},{pixel.B}");
                    return pixel;
                }
                Color full = At("full", Screen(-2.6f, 1, 8));
                Color half = At("half", Screen(-2.6f, 2, 8));
                Color upright = At("upright", Screen(-1.5f, 3.2f, 8));
                Color crossing = At("crossing", Screen(-1.5f, 2, 8));
                Color slanted = At("slanted", Screen(2f, 0.75f, 8));
                Color beside = At("beside", Screen(2.2f, 1.6f, 8));
                Color below = At("belowSlant", Screen(2f, -0.2f, 8));
                string where = backend.ShortName + ": ";
                // The beam's colour (255, 200, 80), unlit: orange, and bright whatever the light.
                if (!(full.R > 200 && full.G > 130 && full.G < full.R && full.B < full.G && full.B > 20)) failures.Add(where + "the full beam is not orange: " + full);
                // Half of the colour added once over the dark background: about (132, 108, 60) on every backend, the
                // Software one included (it used to weight the colour by alpha twice and read 67).
                if (!(Math.Abs(half.R - 132) <= 20 && Math.Abs(half.G - 108) <= 20 && half.R < full.R - 40 && half.G < full.G - 25))
                    failures.Add($"{where}the half-strength beam is {half}, not about (132, 108, 60); the full one is {full}");
                // The pixels between the beams are the dark background.
                if (!(beside.R < 40 && beside.G < 40)) failures.Add(where + "the scene beside the beams is not dark: " + beside);
                // Two half-strength beams crossing add up, and are brighter than either alone.
                if (!(crossing.R > half.R + 30 && crossing.R > upright.R + 30)) failures.Add($"{where}crossing beams {crossing} do not add up over {half} and {upright}");
                // The slanted beam is blue-ish, along its two ends, and not below it.
                if (!(slanted.B > 150 && slanted.B > slanted.R * 1.5)) failures.Add(where + "the slanted beam is not along its ends: " + slanted);
                if (!(below.B < 80)) failures.Add(where + "the slanted beam's neighbour is not dark: " + below);
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "glowing-beam-readings.txt"), readings);
            Console.WriteLine("GlowingBeam readings: " + string.Join(", ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });
    }
}

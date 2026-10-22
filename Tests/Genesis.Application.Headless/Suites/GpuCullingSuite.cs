using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// GPU culling of instanced batches: a row of 400 coloured cubes, most of them out of view, drawn
/// with and without culling on each GPU renderer. The pictures must be the same, and the renderer
/// must say it culled the batch on the GPU (the software renderer never does).
/// </summary>
internal static class GpuCullingSuite
{
    private const int Width = 640, Height = 360;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.GpuCulling.InstancesOutOfViewAreSkippedAndThePictureIsTheSame", () =>
        {
            bool before = RenderCapacityDefaults.GpuCulling;
            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            // Four rows of 100 cubes, 4 m apart, from 200 m left to 196 m right of the camera and 20 to
            // 32 m ahead: about one in eight is in view (the view is 33 to 53 m wide there).
            var instances = new MeshInstanceData[400];
            for (int i = 0; i < instances.Length; i++)
            {
                int column = i % 100, row = i / 100;
                Matrix4x4 world = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(-200f + column * 4f, -1f, 20f + row * 4f);
                instances[i] = new MeshInstanceData(world, new RenderColor(0.3f + (column % 20) * 0.035f, 0.9f - row * 0.15f, 0.5f, 1f));
            }
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false;
                state.LightingEnabled = false; state.ShadowsEnabled = false;
                state.BackgroundColor = Vector3.Zero;
                state.CameraFarPlane = 200f;
                renderer.SetMesh3DState(state);
                renderer.Clear(0f, 0f, 0f);
                renderer.Set3DFrameActive(true);
                // Looking along +Z from the origin, 50 degrees across.
                renderer.SetCamera3D(
                    Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0f, 0f, 10f), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 50f / 180f, Width / (float)Height, 0.1f, 200f));
                var template = new MeshDrawCall { Mesh = renderer.GetBuiltinMesh(BuiltinMeshKind.Cube), Tint = new RenderColor(1f, 1f, 1f, 1f) };
                renderer.DrawMeshInstances(template, instances);
            };
            game.Controls.Add(viewport);
            GateSuite.ShowHost(game);

            List<string> failures = [];
            List<string> readings = [];
            try
            {
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    viewport.BackendOverride = backend.Backend;
                    GateSuite.Pump(3, 15);
                    string name = backend.ShortName.ToLowerInvariant();

                    (Bitmap? Image, int Culled) Frame(bool culling)
                    {
                        RenderCapacityDefaults.GpuCulling = culling;
                        Bitmap? image = viewport.ReadbackFrameToBitmap(3);
                        image?.Save(Path.Combine(ctx.Captures, $"gpu-cull-{(culling ? "on" : "off")}-{name}.png"), ImageFormat.Png);
                        return (image, viewport.Renderer?.GetStats().GpuCulledBatches ?? -1);
                    }

                    (Bitmap? whole, int wholeCulled) = Frame(false);
                    (Bitmap? culled, int culledBatches) = Frame(true);
                    // The count of instances in view is read back a few frames late.
                    viewport.ReadbackFrameToBitmap(6)?.Dispose();
                    RenderStats stats = viewport.Renderer?.GetStats() ?? default;
                    int visible = stats.GpuCullVisibleInstances, tested = stats.GpuCullCandidateInstances;
                    using (whole)
                    using (culled)
                    {
                        if (whole is null || culled is null || viewport.RenderFaultCount != 0)
                        {
                            failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException);
                            continue;
                        }
                        int lit = 0, different = 0;
                        for (int y = 0; y < Math.Min(whole.Height, culled.Height); y += 2)
                        {
                            for (int x = 0; x < Math.Min(whole.Width, culled.Width); x += 2)
                            {
                                Color a = whole.GetPixel(x, y), b = culled.GetPixel(x, y);
                                if (a.R + a.G + a.B > 30) lit++;
                                if (Math.Abs(a.R - b.R) > 2 || Math.Abs(a.G - b.G) > 2 || Math.Abs(a.B - b.B) > 2) different++;
                            }
                        }
                        readings.Add($"{backend.ShortName}: culled batches {wholeCulled} off / {culledBatches} on, in view {visible} of {tested}, lit samples {lit}, different {different}");
                        if (backend.Backend != RenderBackendOption.Software && (tested != instances.Length || visible < 20 || visible > 100))
                            failures.Add($"{backend.ShortName}: the GPU kept {visible} of {tested} cubes, where about 50 of 400 are in view");
                        if (lit < 300) failures.Add($"{backend.ShortName}: the cubes did not draw ({lit} lit samples)");
                        if (different > 0) failures.Add($"{backend.ShortName}: the picture changed with GPU culling ({different} samples)");
                        if (wholeCulled != 0) failures.Add($"{backend.ShortName}: culled {wholeCulled} batches with GPU culling off");
                        bool software = backend.Backend == RenderBackendOption.Software;
                        if (software ? culledBatches != 0 : culledBatches < 1)
                            failures.Add($"{backend.ShortName}: reports {culledBatches} batches culled on the GPU");
                    }
                }
            }
            finally
            {
                RenderCapacityDefaults.GpuCulling = before;
            }
            Console.WriteLine("GPU culling readings: " + string.Join(" | ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join("; ", failures) + " | " + string.Join(" | ", readings));
        });
    }
}

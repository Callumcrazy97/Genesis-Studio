using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;
using Genesis.World.Water;

namespace Genesis.Application.Headless.Suites;

internal static class FogNoiseRuntimeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            HeadlessHarness.RunCase(ctx.Report, "Render.Fog.NoisePreservesForeground." + backend.ShortName, () => Check(ctx, backend));
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All.Where(item => item.Backend != RenderBackendOption.Software))
            HeadlessHarness.RunCase(ctx.Report, "Render.Fog.WaterMistRespectsFootprint." + backend.ShortName, () => Check(ctx, backend, waterMist: true));
    }

    private static void Check(HeadlessContext ctx, RenderBackendDescriptor backend, bool waterMist = false)
    {
        using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 480, 320);
        // This positive-noise region catches fog that adds unitless noise as absolute density.
        Vector3 eye = new(128.57f, -8, -142.85f), center = eye + new Vector3(0, 16, 24);
        Vector3[] corners = [center + new Vector3(-12, -20, 0), center + new Vector3(12, -20, 0),
            center + new Vector3(12, 20, 0), center + new Vector3(-12, 20, 0)];
        MeshHandle panel = renderer.RegisterMesh(corners.Select(position => new MeshVertex
        { Position = position, Normal = -Vector3.UnitZ, Color = new(.05f, .7f, .1f, 1) }).ToArray(), [0, 1, 2, 0, 2, 3]);
        try
        {
            foreach (bool depthWrite in new[] { false, true })
            {
                byte[] Frame(bool fog, bool nearby = false)
                {
                    Mesh3DState state = Mesh3DState.Default;
                    state.FogEnabled = fog; state.FogScreenSpace = true; state.VolumetricFogEnabled = true;
                    state.FogDensity = waterMist ? 0 : .0032f; state.FogNoiseStrength = waterMist ? 0 : 1; state.FogAerialBlend = 0; state.FogSunPreserve = 0;
                    state.FogColor = new(.85f, .85f, .85f, 1); state.ShowFloor = false; state.ShowSunVisual = false;
                    state.LightingEnabled = false; state.CameraFarPlane = 2000;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(Matrix4x4.CreateLookAt(eye, eye + Vector3.UnitZ, Vector3.UnitY),
                        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .12f, 2000));
                    renderer.BeginFrame(); renderer.Clear(.03f, .04f, .05f);
                    if (waterMist && fog)
                    {
                        WaterBody pond = new() { Center = nearby ? eye : Vector3.Zero, SurfaceY = eye.Y,
                            SizeX = 64, SizeZ = 64, GroundMistHeight = 64, GroundMistDensity = .3f };
                        Assert(WaterDrawSystem.TryBuildGroundMist(pond, out FogVolume mist), "The authored pond did not supply mist.");
                        renderer.AddFogVolume(mist);
                    }
                    renderer.DrawMesh(new MeshDrawCall { Mesh = panel, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1,
                        Flags = MeshDrawFlags.Foliage | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow
                            | (depthWrite ? MeshDrawFlags.None : MeshDrawFlags.NoDepthWrite) });
                    renderer.EndFrame();
                    Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels) && width == 480 && height == 320,
                        "No native fog frame was available.");
                    string name = (waterMist ? "water-mist-" : "fog-foreground-") + backend.ShortName + (depthWrite ? "-post" : "-surface")
                        + (nearby ? "-near" : fog ? waterMist ? "-distant" : "-noise" : "-clear");
                    using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
                    try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
                    bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", VisualCapture.Measure(bitmap)));
                    renderer.Present();
                    return pixels;
                }
                byte[] clear = Frame(false), noisy = Frame(true);
                int foreground = 0, excessive = 0;
                for (int index = 0; index < clear.Length; index += 4)
                {
                    if (clear[index + 1] < 90 || clear[index + 1] < clear[index + 2] * 2 || clear[index + 1] < clear[index] * 2) continue;
                    foreground++;
                    if (noisy[index + 1] < clear[index + 1] * .85f || noisy[index + 1] < noisy[index] * 2
                        || noisy[index + 1] < noisy[index + 2] * 2) excessive++;
                }
                Assert(foreground > 20_000 && excessive == 0,
                    $"Light authored fog washed out a 24-metre foreground surface ({excessive}/{foreground} pixels; depthWrite={depthWrite}).");
                if (waterMist)
                {
                    byte[] local = Frame(true, nearby: true); int affected = 0;
                    for (int index = 0; index < clear.Length; index += 4)
                        if (clear[index + 1] > clear[index + 2] * 2 && clear[index + 1] > clear[index] * 2
                            && (local[index + 1] < local[index] * 2 || local[index + 1] < local[index + 2] * 2)) affected++;
                    Assert(affected > 20_000, "Local pond mist was disabled instead of remaining bounded around its water surface.");
                }
            }
        }
        finally { renderer.ReleaseMesh(panel); }
    }

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}

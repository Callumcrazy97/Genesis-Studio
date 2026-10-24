using System.Drawing;
using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Automatic reflections and bounced light (Engine.Lighting.*): a red wall behind the camera, lit by
/// the sun, is seen in a mirror-smooth metal box and tints the shaded face of a white one, on every
/// GPU renderer; with both turned off neither shows it.
/// </summary>
internal static class LightProbeSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    // The sun travels away from the scene, towards the wall behind the camera, so the wall's face is
    // lit and the boxes' faces towards the camera are in their own shade.
    private static readonly Vector3 SunTowardsWall = Vector3.Normalize(new Vector3(0.2f, -0.6f, -1f));
    private static readonly Vector3 Mirror = new(-1.2f, 0.8f, 0f), Matte = new(1.2f, 0.8f, 0f);

    public static void Run(HeadlessContext ctx) =>
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.LightProbe.ReflectionsAndBouncedLightOnAllBackends", () => ReflectionsAndBounce(ctx));

    private static Action<IRenderController> Scene(MeshHandle cube) => r =>
    {
        void Box(Vector3 centre, Vector3 size, RenderColor tint, Vector4 factors = default) =>
            r.DrawMesh(new MeshDrawCall
            {
                Mesh = cube, Tint = tint, Alpha = 1f, MaterialFactors = factors,
                World = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre),
            });
        Box(new Vector3(0f, 2f, -10f), new Vector3(16f, 6f, 0.4f), new RenderColor(0.95f, 0.08f, 0.06f));
        Box(Mirror, new Vector3(1.2f), new RenderColor(0.85f, 0.85f, 0.85f), new Vector4(1f, 0.04f, 0f, 1f));
        Box(Matte, new Vector3(1.2f), new RenderColor(0.9f, 0.9f, 0.9f));
    };

    private static void ReflectionsAndBounce(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var report = new List<string>();
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                string File(string what) => Path.Combine(ctx.Captures, $"light-probe-{name}-{what}.png");
                using (RuntimeViewportHarness harness = new())
                {
                    MeshHandle cube = harness.LitSceneCube;
                    LightProbes.Reflections = LightProbes.BouncedLight = false;
                    harness.CaptureLitScene(File("off"), SunTowardsWall, shadows: true, Scene(cube));
                    LightProbes.ResetForRoom();
                    harness.CaptureLitScene(File("on"), SunTowardsWall, shadows: true, Scene(cube));

                    Point mirror = harness.ProjectLitScenePoint(Mirror + new Vector3(0f, 0f, -0.6f));
                    Point matte = harness.ProjectLitScenePoint(Matte + new Vector3(0f, 0f, -0.6f));
                    using Bitmap off = new(File("off")), on = new(File("on"));
                    double mirrorOff = Redness(off, mirror), mirrorOn = Redness(on, mirror);
                    double matteOff = Redness(off, matte), matteOn = Redness(on, matte);
                    report.Add($"{backend}: mirror redness {mirrorOff:F1} -> {mirrorOn:F1}, white box {matteOff:F1} -> {matteOn:F1}");
                    HeadlessHarness.Assert(mirrorOn > mirrorOff + 40,
                        $"{backend}: the metal box does not mirror the red wall behind the camera (redness {mirrorOff:F1} off, {mirrorOn:F1} on).");
                    HeadlessHarness.Assert(matteOn > matteOff + 6,
                        $"{backend}: the white box's shaded face does not pick up the red wall's bounced light (redness {matteOff:F1} off, {matteOn:F1} on).");
                }
            }
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            LightProbes.ResetForRoom();
            File.WriteAllLines(Path.Combine(ctx.Captures, "light-probe-backends.txt"), report);
        }
    }

    // How much redder than grey a 5 x 5 patch is: red less the mean of green and blue.
    private static double Redness(Bitmap image, Point centre)
    {
        double total = 0;
        int count = 0;
        for (int y = centre.Y - 2; y <= centre.Y + 2; y++)
            for (int x = centre.X - 2; x <= centre.X + 2; x++)
            {
                if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                Color c = image.GetPixel(x, y);
                total += c.R - (c.G + c.B) / 2.0;
                count++;
            }
        return count == 0 ? 0 : total / count;
    }
}

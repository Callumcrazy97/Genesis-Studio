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

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.LightProbe.ReflectionsAndBouncedLightOnAllBackends", () => ReflectionsAndBounce(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Camera.DepthOfFieldBlursWhatIsOutOfFocusOnAllBackends", () => DepthOfFieldBlur(ctx));
    }

    // A box in focus near the camera and one far behind it: with Engine.Camera.Focus on, the far
    // box's edge goes soft and the near one's stays sharp.
    private static readonly Vector3 NearBox = new(-1.6f, 0.8f, -3f), FarBox = new(2.2f, 1.2f, 7f);

    private static void DepthOfFieldBlur(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var report = new List<string>();
        Action<IRenderController> scene = r => { };
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                string File(string what) => Path.Combine(ctx.Captures, $"depth-of-field-{name}-{what}.png");
                using RuntimeViewportHarness harness = new();
                MeshHandle cube = harness.LitSceneCube;
                scene = r =>
                {
                    r.DrawMesh(new MeshDrawCall { Mesh = cube, Tint = new RenderColor(0.95f, 0.95f, 0.95f), Alpha = 1f,
                        World = Matrix4x4.CreateScale(1f) * Matrix4x4.CreateTranslation(NearBox) });
                    r.DrawMesh(new MeshDrawCall { Mesh = cube, Tint = new RenderColor(0.95f, 0.95f, 0.95f), Alpha = 1f,
                        World = Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateTranslation(FarBox) });
                };
                Vector3 sun = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f));
                DepthOfField.Enabled = false;
                harness.CaptureLitScene(File("off"), sun, shadows: false, scene);
                DepthOfField.Enabled = true;
                DepthOfField.Distance = Vector3.Distance(RuntimeViewportHarness.LitSceneEye, NearBox);
                DepthOfField.Range = 1f;
                DepthOfField.Blur = 14f;
                harness.CaptureLitScene(File("on"), sun, shadows: false, scene);

                using Bitmap off = new(File("off")), on = new(File("on"));
                double Edge(Bitmap image, Vector3 box, float half) => Sharpness(image,
                    harness.ProjectLitScenePoint(box + new Vector3(-half - 0.6f, 0f, -half)),
                    harness.ProjectLitScenePoint(box + new Vector3(-half + 0.6f, 0f, -half)));
                double farOff = Edge(off, FarBox, 0.8f), farOn = Edge(on, FarBox, 0.8f);
                double nearOff = Edge(off, NearBox, 0.5f), nearOn = Edge(on, NearBox, 0.5f);
                report.Add($"{backend}: far edge {farOff:F1} -> {farOn:F1}, near edge {nearOff:F1} -> {nearOn:F1}");
                HeadlessHarness.Assert(farOn < farOff * 0.6,
                    $"{backend}: the box out of focus did not blur (sharpest step across its edge {farOff:F1} without, {farOn:F1} with depth of field).");
                HeadlessHarness.Assert(nearOn > nearOff * 0.75,
                    $"{backend}: the box in focus went soft (sharpest step {nearOff:F1} without, {nearOn:F1} with depth of field).");
            }
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            DepthOfField.Reset();
            File.WriteAllLines(Path.Combine(ctx.Captures, "depth-of-field-backends.txt"), report);
        }
    }

    // The largest brightness step between neighbouring pixels on the line from a to b.
    private static double Sharpness(Bitmap image, Point a, Point b)
    {
        double largest = 0;
        int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        double last = double.NaN;
        for (int i = 0; i <= steps; i++)
        {
            int x = a.X + (b.X - a.X) * i / Math.Max(1, steps), y = a.Y + (b.Y - a.Y) * i / Math.Max(1, steps);
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
            Color c = image.GetPixel(x, y);
            double luma = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            if (!double.IsNaN(last)) largest = Math.Max(largest, Math.Abs(luma - last));
            last = luma;
        }
        return largest;
    }

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
        // As a game has them (the Player turns them on; editor views keep the flat look).
        LightProbes.InGame = true;
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
            LightProbes.InGame = false;
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

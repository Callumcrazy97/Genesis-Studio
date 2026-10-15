using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Runtime;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.SilkNet.Vulkan;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// What the renderers draw far from the camera, compared with each other: a long view of boxes out
/// to 900 m under a 1 km far plane (<see cref="RuntimeViewportHarness.CaptureFarView"/>), drawn with
/// the engine's material and with project mesh shaders, with fog and sky off and on. Also how the
/// Vulkan swap chain follows vsync.
/// </summary>
internal static class DistantViewSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    // How far one renderer's box may be from Direct3D 11's, per channel out of 255. The renderers
    // agree to a few levels on these flat boxes; a box drawn black or in the wrong colour is off by
    // a hundred.
    private const int Tolerance = 12;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Distance.FarPixelsMatchAcrossRenderers", () => FarPixels(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Vulkan.VSyncChoosesThePresentModeAndRebuildsOnlyOnChange", VSync);
        HeadlessHarness.RunCase(ctx.Report, "Render.Vulkan.SamplerBorderIsTheColourAskedFor", () =>
        {
            // The shadow sampler's border is white: off the map is lit, on every backend.
            Check(VulkanGpuDevice.BorderFor(new GpuSamplerDesc { BorderColorR = 1, BorderColorG = 1, BorderColorB = 1, BorderColorA = 1 })
                == Silk.NET.Vulkan.BorderColor.FloatOpaqueWhite, "A white border is not white on Vulkan.");
            Check(VulkanGpuDevice.BorderFor(new GpuSamplerDesc { BorderColorA = 1 }) == Silk.NET.Vulkan.BorderColor.FloatOpaqueBlack,
                "An opaque black border is not opaque black on Vulkan.");
            Check(VulkanGpuDevice.BorderFor(default) == Silk.NET.Vulkan.BorderColor.FloatTransparentBlack,
                "A sampler that names no border is not transparent black on Vulkan, as on Direct3D.");
        });
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private static readonly (string Name, bool Fog, bool Sky, bool Linear, bool Shadows)[] Views =
    {
        ("plain", false, false, false, false),
        ("fog", true, false, false, false),
        ("fog-sky", true, true, false, false),
        ("linear-fog-sky", true, true, true, false),
        // Sun shadows on with fog: the volumetric fog runs, as in a game with shadow casters.
        ("linear-fog-sky-shadows", true, true, true, true),
    };

    private static readonly string[] Columns = { "engine-lit", "fading shader", "flat shader" };

    private static void FarPixels(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        bool previousLinear = EngineRenderingDefaults.LinearColorPipeline;
        var report = new List<string>();
        // view -> backend -> [row, column] colour
        var seen = new Dictionary<string, Dictionary<RenderBackendOption, Color[,]>>();
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                if (!RenderBackendSelection.IsAvailable(backend))
                {
                    report.Add($"{backend}: not available on this machine, not compared");
                    continue;
                }

                RenderBackendSelection.Configure(backend);
                using RuntimeViewportHarness harness = new(960, 540);
                foreach ((string name, bool fog, bool sky, bool linear, bool shadows) in Views)
                {
                    string file = Path.Combine(ctx.Captures, $"far-view-{backend.ToString().ToLowerInvariant()}-{name}.png");
                    // A project whose colour space is Linear (as a voxel game's is) lights in linear light.
                    EngineRenderingDefaults.LinearColorPipeline = linear;
                    harness.CaptureFarView(file, fog, sky, shadows);
                    using Bitmap picture = new(file);
                    int rows = RuntimeViewportHarness.FarViewDistances.Length;
                    var colours = new Color[rows, Columns.Length];
                    for (int row = 0; row < rows; row++)
                        for (int column = 0; column < Columns.Length; column++)
                            colours[row, column] = Mean(picture,
                                harness.ProjectFarViewPoint(RuntimeViewportHarness.FarViewPoint(row, column)));
                    if (!seen.TryGetValue(name, out var byBackend)) seen[name] = byBackend = new();
                    byBackend[backend] = colours;
                    report.Add($"{backend} {name}: " + Describe(colours));
                }
            }
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            EngineRenderingDefaults.LinearColorPipeline = previousLinear;
            File.WriteAllLines(Path.Combine(ctx.Captures, "far-view.txt"), report);
        }

        Check(seen.TryGetValue("plain", out var plain) && plain.ContainsKey(RenderBackendOption.SilkNetDx11),
            "Direct3D 11, the reference, did not draw the far view.");

        foreach ((string name, _, _, _, _) in Views)
        {
            Dictionary<RenderBackendOption, Color[,]> byBackend = seen[name];
            Color[,] reference = byBackend[RenderBackendOption.SilkNetDx11];
            int rows = reference.GetLength(0);

            // What the scene is made to show, on the reference: by the far rows the fading shader
            // has become the fog colour (pale blue, after the composite's tone curve), and nothing
            // is black at any distance.
            for (int row = 0; row < rows; row++)
            {
                float distance = RuntimeViewportHarness.FarViewDistances[row];
                Color faded = reference[row, 1];
                if (distance >= RuntimeViewportHarness.FarViewShaderFogEnd)
                    Check(Luma(faded) > 100 && faded.B > faded.R + 10,
                        $"{name}: Direct3D 11's fading shader at {distance} m is {faded}, not the engine's pale blue fog colour.");
                Check(Luma(reference[row, 0]) > 20 && Luma(faded) > 20 && Luma(reference[row, 2]) > 20,
                    $"{name}: Direct3D 11 drew a box at {distance} m black ({Describe(reference)}).");
            }

            foreach ((RenderBackendOption backend, Color[,] colours) in byBackend)
            {
                if (backend == RenderBackendOption.SilkNetDx11) continue;
                for (int row = 0; row < rows; row++)
                    for (int column = 0; column < Columns.Length; column++)
                    {
                        Color expected = reference[row, column], actual = colours[row, column];
                        Check(Near(actual, expected, Tolerance),
                            $"{name}: {backend} draws the {Columns[column]} box {RuntimeViewportHarness.FarViewDistances[row]} m away "
                            + $"as {actual}; Direct3D 11 draws {expected}. See far-view-{backend.ToString().ToLowerInvariant()}-{name}.png.");
                    }
            }
        }
    }

    private static string Describe(Color[,] colours)
    {
        var parts = new List<string>();
        for (int row = 0; row < colours.GetLength(0); row++)
        {
            var cells = new List<string>();
            for (int column = 0; column < colours.GetLength(1); column++)
            {
                Color c = colours[row, column];
                cells.Add($"{c.R},{c.G},{c.B}");
            }
            parts.Add($"{RuntimeViewportHarness.FarViewDistances[row]}m [{string.Join(" | ", cells)}]");
        }
        return string.Join("; ", parts);
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static Color Mean(Bitmap bitmap, Point centre)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = Math.Clamp(centre.X + dx, 0, bitmap.Width - 1), y = Math.Clamp(centre.Y + dy, 0, bitmap.Height - 1);
                Color c = bitmap.GetPixel(x, y);
                r += c.R; g += c.G; b += c.B; n++;
            }
        return Color.FromArgb(r / n, g / n, b / n);
    }

    // ── Vulkan vsync ─────────────────────────────────────────────────────────

    private static void VSync()
    {
        if (!RenderBackendSelection.IsVulkanAvailable())
        {
            Console.WriteLine("      SKIPPED: no Vulkan adapter on this machine.");
            return;
        }

        using Form host = GateSuite.NewHost(320, 180);
        GateSuite.ShowHost(host);
        using GpuRenderController renderer = new(RenderControllerFactory.CreateDevice(RenderBackendOption.Vulkan));
        renderer.Initialize(host.Handle, 320, 180);
        var swapChain = renderer.SwapChain as VulkanSwapChainAdapter;
        Check(swapChain is not null, $"The Vulkan renderer's swap chain is a {renderer.SwapChain?.GetType().Name ?? "null"}.");

        void Frames(bool vsync, int count)
        {
            renderer.SetVSync(vsync);
            for (int i = 0; i < count; i++)
            {
                renderer.Set3DFrameActive(false);
                renderer.SetCamera2D(160, 90, 1, 0);
                renderer.BeginFrame();
                renderer.Clear(0.1f, 0.2f, 0.3f, 1f);
                renderer.EndFrame();
                renderer.Present();
            }
        }

        Frames(vsync: false, 4);
        int builtOff = swapChain!.BuildCount;
        string offMode = swapChain.PresentModeName;
        Check(!swapChain.VSync && offMode is "MAILBOX" or "IMMEDIATE" or "FIFO",
            $"With vsync off the swap chain presents with {offMode} (vsync {swapChain.VSync}).");

        Frames(vsync: true, 6);
        Check(swapChain.VSync && swapChain.PresentModeName == "FIFO",
            $"WindowSetVSync(true) left the swap chain presenting with {swapChain.PresentModeName}.");
        int builtOn = swapChain.BuildCount;
        Check(builtOn == builtOff + (offMode == "FIFO" ? 0 : 1),
            $"Turning vsync on built the swap chain {builtOn - builtOff} times (expected once).");

        Frames(vsync: true, 6);
        Check(swapChain.BuildCount == builtOn,
            $"Presenting with vsync on, unchanged, rebuilt the swap chain {swapChain.BuildCount - builtOn} times.");

        Frames(vsync: false, 6);
        Check(!swapChain.VSync && swapChain.PresentModeName == offMode,
            $"WindowSetVSync(false) left the swap chain presenting with {swapChain.PresentModeName} (expected {offMode}).");
        Check(swapChain.BuildCount == builtOn + (offMode == "FIFO" ? 0 : 1),
            $"Turning vsync off again built the swap chain {swapChain.BuildCount - builtOn} times (expected once).");
        Console.WriteLine($"      vsync off: {offMode}, on: FIFO; {swapChain.BuildCount} builds in all");
    }
}

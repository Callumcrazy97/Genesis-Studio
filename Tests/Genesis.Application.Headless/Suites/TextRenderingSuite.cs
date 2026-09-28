using System.Drawing;
using System.Numerics;
using System.Security.Cryptography;
using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class TextRenderingSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "TextRendering");
        string project = Path.Combine(context.Workspace, "AuthoredFont");
        string fonts = Path.Combine(project, "Assets", "Fonts");
        Directory.CreateDirectory(fonts);
        string payload = Path.Combine(fonts, "Meadow.ttf");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf"), payload, true);
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            HeadlessHarness.RunCase(context.Report, "Acceptance.Text.AuthoredFont." + backend.ShortName, () =>
            {
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf"), payload, true);
                using RenderParityHarness harness = new(backend.Backend);
                string file = "authored-font-" + backend.ShortName + ".png";
                string path = Path.Combine(context.Captures, file);
                var metrics = harness.CaptureWithOverlay(path, canvas =>
                {
                    PgslRenderDrawSurface surface = new(null, new OverlayHudCanvas(canvas, 640, 360),
                        640, 360, projectPath: project);
                    surface.DrawText("MUSHROOM MEADOW", "Consolas", 22, Color.White, new Rectangle(20, 26, 450, 40));
                    surface.DrawText("MUSHROOM MEADOW", "Consolas", 22, Color.White, new Rectangle(20, 86, 450, 40));
                    surface.DrawText("MUSHROOM MEADOW", "Segoe UI", 22, Color.White, new Rectangle(20, 146, 450, 40));
                    surface.DrawText("MUSHROOM MEADOW", "Assets/Fonts/Meadow.ttf", 22, Color.White, new Rectangle(20, 206, 450, 40));
                });
                HeadlessHarness.Assert(harness.ActiveBackend.Equals(backend.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || harness.ActiveBackend.Equals(backend.ShortName, StringComparison.OrdinalIgnoreCase),
                    "Text probe silently changed backend: " + harness.ActiveBackend);
                using Bitmap bitmap = new(path);
                string first = Pixels(bitmap, 26, out int firstTop);
                string repeated = Pixels(bitmap, 86, out int repeatedTop);
                string proportional = Pixels(bitmap, 146, out _);
                string imported = Pixels(bitmap, 206, out _);
                HeadlessHarness.Assert(firstTop >= 0 && firstTop < 24 && firstTop == repeatedTop && first == repeated,
                    $"Identical text has different placement or pixels: top {firstTop}/{repeatedTop}, {first}/{repeated}.");
                HeadlessHarness.Assert(first != proportional, "DrawSetFont/Ui.Font was discarded; both families have identical rendered pixels.");
                HeadlessHarness.Assert(first == imported, "The project-relative TTF payload did not produce its actual font pixels.");
                context.Report.Images.Add(new ImageResult("Authored fonts " + backend.ShortName, file,
                    metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"), payload, true);
                string updatedFile = "authored-font-updated-" + backend.ShortName + ".png";
                string updatedPath = Path.Combine(context.Captures, updatedFile);
                var updatedMetrics = harness.CaptureWithOverlay(updatedPath, canvas =>
                {
                    PgslRenderDrawSurface surface = new(null, new OverlayHudCanvas(canvas, 640, 360),
                        640, 360, projectPath: project);
                    surface.DrawText("MUSHROOM MEADOW", "Assets/Fonts/Meadow.ttf", 22, Color.White,
                        new Rectangle(20, 206, 450, 40));
                });
                using Bitmap updated = new(updatedPath);
                HeadlessHarness.Assert(Pixels(updated, 206, out _) == proportional,
                    "The running renderer retained stale font metrics or glyphs after the payload changed.");
                context.Report.Images.Add(new ImageResult("Updated font " + backend.ShortName, updatedFile,
                    updatedMetrics.Width, updatedMetrics.Height, updatedMetrics.UniqueSampledColors, updatedMetrics.AverageLuminance));
            });
        }
    }

    private static string Pixels(Bitmap bitmap, int row, out int top)
    {
        byte[] pixels = new byte[450 * 50];
        top = -1;
        for (int y = 0; y < 50; y++)
        for (int x = 0; x < 450; x++)
        {
            Color pixel = bitmap.GetPixel(20 + x, row + y);
            byte value = (byte)(pixel.R > 180 && pixel.G > 180 && pixel.B > 180 ? 255 : 0);
            pixels[y * 450 + x] = value;
            if (value > 0 && top < 0) top = y;
        }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}

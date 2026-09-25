using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Sectioned Image Editor coverage. Uses fixed fixture pixels and hand-computed expected
/// results so a no-op filter cannot silently pass.
/// </summary>
internal static class ImageEditorSuite
{
    public static void RunFeatureSections(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Image");
        ResourceService resources = HeadlessHarness.Require(ctx.Resources, "Resource service");

        RunReshadeSections(ctx.Report);
        RunToolSections(ctx.Report, resources);
        RunEffectsColourAndGenerations(ctx.Report, resources);
        RunAnimationAndRigging(ctx.Report, resources);
        ImageAuthoringSuite.Run(ctx);
        RunInteractionSmoke(ctx.Report, resources, ctx.Captures, ctx.Report);
    }

    private static void RunReshadeSections(TestReport report)
    {
        const int w = 4;
        const int h = 4;

        // Fixture: opaque mid-grey top-left 2x2, bright white bottom-right 2x2, rest transparent.
        byte[] source = new byte[w * h * 4];
        FillRect(source, w, 0, 0, 2, 2, Color.FromArgb(255, 100, 100, 100));
        FillRect(source, w, 2, 2, 2, 2, Color.FromArgb(255, 240, 240, 240));

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.WindWaker", () =>
        {
            byte[] actual = ImageAdvancedOperations.WindWaker(source, strength: 70);
            // 100 * 1.7 = 170; 240 * 1.7 clamped = 255
            byte[] expected = (byte[])source.Clone();
            FillRect(expected, w, 0, 0, 2, 2, Color.FromArgb(255, 170, 170, 170));
            FillRect(expected, w, 2, 2, 2, 2, Color.FromArgb(255, 255, 255, 255));
            HeadlessHarness.AssertPixelsEqual(actual, expected, "WindWaker");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.Catalog.WindWakerSlider", () =>
        {
            Dictionary<string, object> identityParams = new(StringComparer.Ordinal) { ["strength"] = 0 };
            Dictionary<string, object> strongParams = new(StringComparer.Ordinal) { ["strength"] = 70 };
            byte[] identity = ImageEffectCatalog.WindWaker.Apply(source, w, h, identityParams);
            byte[] strong = ImageEffectCatalog.WindWaker.Apply(source, w, h, strongParams);
            HeadlessHarness.AssertPixelsEqual(identity, source, "WindWaker strength 0 should be identity");
            byte[] expected = (byte[])source.Clone();
            FillRect(expected, w, 0, 0, 2, 2, Color.FromArgb(255, 170, 170, 170));
            FillRect(expected, w, 2, 2, 2, 2, Color.FromArgb(255, 255, 255, 255));
            HeadlessHarness.AssertPixelsEqual(strong, expected, "WindWaker catalog strength 70");
            HeadlessHarness.Assert(
                ImageEffectCatalog.WindWaker.Parameters.Count == 1
                && ImageEffectCatalog.WindWaker.Parameters[0].Name == "strength",
                "Wind Waker catalog must expose a strength slider parameter.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.Catalog.BloomThresholdDiffers", () =>
        {
            // Mid luminance hotspot: passes threshold 20 (floor≈51) but not 95 (floor≈242).
            byte[] mid = new byte[8 * 8 * 4];
            FillRect(mid, 8, 0, 0, 8, 8, Color.FromArgb(255, 20, 20, 20));
            FillRect(mid, 8, 3, 3, 2, 2, Color.FromArgb(255, 120, 120, 120));
            byte[] low = ImageEffectCatalog.Bloom.Apply(
                mid, 8, 8, new Dictionary<string, object> { ["threshold"] = 20 });
            byte[] high = ImageEffectCatalog.Bloom.Apply(
                mid, 8, 8, new Dictionary<string, object> { ["threshold"] = 95 });
            HeadlessHarness.Assert(!low.SequenceEqual(high),
                "Bloom threshold 20 vs 95 must produce different pixels for mid-luma hotspot.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.CrtScanlines", () =>
        {
            byte[] actual = ImageAdvancedOperations.CrtScanlines(source, w, h, strength: 50);
            // Odd rows darkened by 0.75; even rows unchanged.
            byte[] expected = (byte[])source.Clone();
            for (int y = 1; y < h; y += 2)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (expected[i + 3] == 0) continue;
                    expected[i] = (byte)Math.Clamp((int)MathF.Round(expected[i] * 0.75f), 0, 255);
                    expected[i + 1] = (byte)Math.Clamp((int)MathF.Round(expected[i + 1] * 0.75f), 0, 255);
                    expected[i + 2] = (byte)Math.Clamp((int)MathF.Round(expected[i + 2] * 0.75f), 0, 255);
                }
            }
            HeadlessHarness.AssertPixelsEqual(actual, expected, "CrtScanlines");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.Vibrance", () =>
        {
            byte[] saturated = new byte[4 * 4 * 4];
            FillRect(saturated, 4, 0, 0, 4, 4, Color.FromArgb(255, 200, 40, 40));
            byte[] identity = ImageAdvancedOperations.Vibrance(saturated, amount: 100);
            HeadlessHarness.AssertPixelsEqual(identity, saturated, "Vibrance identity@100");
            byte[] boosted = ImageAdvancedOperations.Vibrance(saturated, amount: 120);
            Color before = RasterOperations.GetPixel(saturated, 4, 4, 1, 1);
            Color after = RasterOperations.GetPixel(boosted, 4, 4, 1, 1);
            HeadlessHarness.Assert(after.R >= before.R, "Vibrance should not reduce red on a red pixel.");
            HeadlessHarness.Assert(
                !(after.R == before.R && after.G == before.G && after.B == before.B),
                "Vibrance@120 must change the pixel (got identical colour).");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.N64Filter", () =>
        {
            byte[] checker = BuildChecker(8, 8, Color.Black, Color.White);
            byte[] actual = ImageAdvancedOperations.N64Filter(checker, 8, 8);
            HeadlessHarness.Assert(actual.Length == checker.Length, "N64 length mismatch.");
            // Softening: centre of black cell should pick up some white bleed (not pure 0).
            Color soft = RasterOperations.GetPixel(actual, 8, 8, 1, 1);
            HeadlessHarness.Assert(soft.A > 0, "N64 produced transparent centre.");
            int luma = soft.R + soft.G + soft.B;
            HeadlessHarness.Assert(luma > 0, "N64 should soften pure black checker cell.");
            HeadlessHarness.Assert(luma < 765, "N64 should not turn black cell fully white.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.Bloom", () =>
        {
            byte[] dark = new byte[8 * 8 * 4];
            FillRect(dark, 8, 0, 0, 8, 8, Color.FromArgb(255, 20, 20, 20));
            FillRect(dark, 8, 3, 3, 2, 2, Color.FromArgb(255, 255, 255, 255));
            byte[] bloomed = ImageAdvancedOperations.Bloom(dark, 8, 8, threshold: 80);
            Color neighbour = RasterOperations.GetPixel(bloomed, 8, 8, 2, 2);
            Color originalNeighbour = RasterOperations.GetPixel(dark, 8, 8, 2, 2);
            int before = originalNeighbour.R + originalNeighbour.G + originalNeighbour.B;
            int after = neighbour.R + neighbour.G + neighbour.B;
            HeadlessHarness.Assert(after > before, "Bloom should brighten pixels near the hot spot.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.Emboss", () =>
        {
            byte[] gradient = new byte[8 * 8 * 4];
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                byte v = (byte)(x * 32);
                int i = (y * 8 + x) * 4;
                gradient[i] = gradient[i + 1] = gradient[i + 2] = v;
                gradient[i + 3] = 255;
            }

            byte[] embossed = ImageAdvancedOperations.Emboss(gradient, 8, 8);
            Color left = RasterOperations.GetPixel(embossed, 8, 8, 1, 4);
            Color right = RasterOperations.GetPixel(embossed, 8, 8, 6, 4);
            HeadlessHarness.Assert(
                left.R != right.R || left.G != right.G,
                "Emboss on a horizontal gradient must differentiate left vs right.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.Outline", () =>
        {
            byte[] sprite = new byte[8 * 8 * 4];
            FillRect(sprite, 8, 2, 2, 4, 4, Color.FromArgb(255, 0, 180, 0));
            byte[] outlined = ImageAdvancedOperations.ReshadeOutline(
                sprite, 8, 8, Color.Black, thickness: 1, tolerance: 10);
            Color edge = RasterOperations.GetPixel(outlined, 8, 8, 1, 2);
            HeadlessHarness.Assert(edge.A > 0 && edge.R == 0 && edge.G == 0 && edge.B == 0,
                $"Expected black outline at (1,2), got {edge}.");
            Color core = RasterOperations.GetPixel(outlined, 8, 8, 3, 3);
            HeadlessHarness.Assert(core.G > 100, $"Expected green core preserved, got {core}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.GameBoy", () =>
        {
            Color[] palette =
            [
                Color.FromArgb(15, 56, 15),
                Color.FromArgb(48, 98, 48),
                Color.FromArgb(139, 172, 15),
                Color.FromArgb(155, 188, 15),
            ];
            byte[] actual = ImageAdvancedOperations.ReshadePalette(source, palette, preserveLuminance: false);
            Color sample = RasterOperations.GetPixel(actual, w, h, 0, 0);
            HeadlessHarness.Assert(
                palette.Any(c => c.R == sample.R && c.G == sample.G && c.B == sample.B),
                $"Game Boy sample {sample} is not in the palette.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.WarmRamp", () =>
        {
            Color[] palette =
            [
                Color.FromArgb(34, 24, 31),
                Color.FromArgb(117, 46, 61),
                Color.FromArgb(218, 94, 65),
                Color.FromArgb(255, 205, 117),
            ];
            byte[] actual = ImageAdvancedOperations.ReshadePalette(source, palette, preserveLuminance: false);
            Color sample = RasterOperations.GetPixel(actual, w, h, 3, 3);
            HeadlessHarness.Assert(
                palette.Any(c => c.R == sample.R && c.G == sample.G && c.B == sample.B),
                $"Warm Ramp sample {sample} is not in the palette.");
        });
    }

    private static void RunToolSections(TestReport report, ResourceService resources)
    {
        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Draw.Pencil", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Draw Pencil", 16, 16, Color.Transparent);
            editor.Control.SetForegroundColor(Color.Red);
            editor.Control.DrawStroke(new Point(2, 2), new Point(10, 2), Color.Red, size: 1);
            Color painted = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 16, 16, 5, 2);
            HeadlessHarness.Assert(painted.R > 200 && painted.G < 40 && painted.B < 40,
                $"Pencil expected red at (5,2), got {painted}.");
            Color empty = RasterOperations.GetPixel(editor.Workspace.CurrentLayer.Pixels, 16, 16, 5, 8);
            HeadlessHarness.Assert(empty.A == 0, $"Pencil should not paint (5,8), got {empty}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Draw.Brush", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Draw Brush", 32, 32, Color.Transparent);
            editor.Control.SetActiveTool(ImageToolKind.Brush);
            editor.Control.SetBrushSize(6);
            editor.Control.SetForegroundColor(Color.Blue);
            editor.Control.DrawStroke(new Point(8, 8), new Point(20, 8), Color.Blue, size: 6);
            Color painted = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 32, 32, 14, 8);
            HeadlessHarness.Assert(painted.B > 200 && painted.A > 0, $"Brush expected blue, got {painted}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Draw.Eraser", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Draw Eraser", 16, 16, Color.Lime);
            ImageLayerBuffer layer = editor.Workspace.CurrentLayer!;
            Color before = RasterOperations.GetPixel(layer.Pixels, 16, 16, 4, 4);
            HeadlessHarness.Assert(before.G == 255 && before.R == 0 && before.A == 255,
                $"Expected lime fill, got {before}.");
            RasterOperations.Clear(layer.Pixels, 16, 16, Color.Transparent);
            Color after = RasterOperations.GetPixel(layer.Pixels, 16, 16, 4, 4);
            HeadlessHarness.Assert(after.A == 0, $"Expected cleared pixel, got {after}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Draw.Line", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Draw Line", 24, 24, Color.Transparent);
            editor.Control.DrawStroke(new Point(0, 0), new Point(23, 23), Color.Magenta, size: 1);
            Color mid = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 24, 24, 12, 12);
            HeadlessHarness.Assert(mid.A > 0 && mid.R > 200 && mid.B > 200,
                $"Line midpoint expected magenta, got {mid}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Fill.FloodFill", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Fill Flood", 16, 16, Color.Black);
            editor.Control.FloodFillAt(new Point(0, 0), Color.Yellow);
            Color c = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 16, 16, 8, 8);
            HeadlessHarness.Assert(c.R > 200 && c.G > 200 && c.B < 80, $"Flood fill expected yellow, got {c}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Fill.Rectangle", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Fill Rect", 32, 32, Color.Transparent);
            editor.Control.DrawFilledRectangle(new Rectangle(4, 4, 8, 8), Color.Cyan);
            Color inside = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 32, 32, 6, 6);
            Color outside = RasterOperations.GetPixel(editor.Workspace.CurrentLayer.Pixels, 32, 32, 20, 20);
            HeadlessHarness.Assert(inside.G > 200 && inside.B > 200, $"Rect inside expected cyan, got {inside}.");
            HeadlessHarness.Assert(outside.A == 0, $"Rect outside should stay empty, got {outside}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Shapes.FilledRectangle", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Shapes Rect", 20, 20, Color.White);
            editor.Control.DrawFilledRectangle(new Rectangle(2, 2, 6, 6), Color.Orange);
            Color c = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 20, 20, 4, 4);
            HeadlessHarness.Assert(c.R > 200 && c.G > 100, $"Shape rect expected orange, got {c}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Selection.SelectAllCopyPaste", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Selection Copy", 16, 16, Color.Purple);
            editor.Control.SelectAll();
            HeadlessHarness.Assert(editor.Control.CanCutOrCopy, "SelectAll should enable cut/copy.");
            HeadlessHarness.Assert(editor.Workspace.Selection.HasSelection, "Selection mask missing.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Transform.GridSnap", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Transform Grid", 32, 32, Color.Transparent);
            editor.Control.ConfigureGrid(true, 8, 8);
            editor.Control.SetSnapToGrid(true);
            HeadlessHarness.Assert(editor.Control.Canvas.Overlay.ShowGrid, "Grid overlay should be enabled.");
            HeadlessHarness.Assert(editor.Control.Canvas.Overlay.GridWidth == 8, "Grid width should be 8.");
        });
    }

    private static void RunEffectsColourAndGenerations(TestReport report, ResourceService resources)
    {
        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Colour.HueDesaturateLevels", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Colour Effects", 8, 8, Color.FromArgb(255, 200, 50, 50));
            byte[] before = (byte[])editor.Workspace.CurrentLayer!.Pixels.Clone();
            editor.Control.ApplyColourEffectForTests("Adjust hue", pixels =>
                ImageAdvancedOperations.AdjustHsv(pixels, 15f, 1f, 1f));
            byte[] afterHue = (byte[])editor.Workspace.CurrentLayer.Pixels.Clone();
            HeadlessHarness.Assert(!before.SequenceEqual(afterHue), "Hue +15 must change pixels.");
            editor.Control.ApplyColourEffectForTests("Desaturate", pixels =>
                ImageAdvancedOperations.AdjustHsv(pixels, 0f, 0f, 1f));
            Color grey = RasterOperations.GetPixel(editor.Workspace.CurrentLayer.Pixels, 8, 8, 2, 2);
            HeadlessHarness.Assert(
                Math.Abs(grey.R - grey.G) < 8 && Math.Abs(grey.G - grey.B) < 8,
                $"Desaturate expected near-grey, got {grey}.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Generations.OutlineNoiseDither", () =>
        {
            byte[] solid = new byte[8 * 8 * 4];
            FillRect(solid, 8, 2, 2, 4, 4, Color.White);
            byte[] outlined = ImageAdvancedOperations.GenerateOutline(
                solid, 8, 8, Color.Red, radius: 1, outsideOnly: true);
            Color edge = RasterOperations.GetPixel(outlined, 8, 8, 1, 2);
            HeadlessHarness.Assert(edge.R > 200 && edge.A > 0, $"GenerateOutline expected red edge, got {edge}.");

            byte[] noise = ImageAdvancedOperations.GenerateNoise(
                8, 8, seed: 42, scale: 0.4f, Color.Black, Color.White);
            HeadlessHarness.Assert(noise.Any(b => b != 0 && b != 255) || noise.Distinct().Count() > 2,
                "Seeded noise should not be a flat fill.");

            byte[] dithered = ImageAdvancedOperations.OrderedDither(
                solid, 8, 8, [Color.Black, Color.White]);
            HeadlessHarness.Assert(dithered.Length == solid.Length, "Dither length mismatch.");
        });
    }

    private static void RunAnimationAndRigging(TestReport report, ResourceService resources)
    {
        HeadlessHarness.RunCase(report, "Editor.Image.Animation.AddFrame", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Anim Frames", 16, 16, Color.Blue);
            int before = editor.Workspace.Frames.Count;
            editor.Control.AddFrameForTests(duplicate: true);
            HeadlessHarness.Assert(
                editor.Workspace.Frames.Count == before + 1,
                "Duplicate frame did not increase frame count.");
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Tools.Rigging.AddRootBone", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Rig Bone", 32, 32, Color.Transparent);
            int before = editor.Control.Session.Document.Armature?.Bones.Count ?? 0;
            editor.Control.AddRootBoneForTests();
            int after = editor.Control.Session.Document.Armature?.Bones.Count ?? 0;
            HeadlessHarness.Assert(after == before + 1, "AddRootBone did not add a bone.");
        });
    }

    private static void RunInteractionSmoke(
        TestReport report,
        ResourceService resources,
        string captures,
        TestReport imageReport)
    {
        HeadlessHarness.RunCase(report, "Editor.Image.Interaction.ToolWorkflow", () =>
        {
            using EditorSession editor = CreateEditor(resources, "Tool Workflow", 32, 32, Color.RoyalBlue);
            editor.Control.ConfigureGrid(true, 8, 8, Color.FromArgb(180, 80, 200, 255));
            editor.Control.SetSnapToGrid(true);
            editor.Control.DrawFilledRectangle(new Rectangle(12, 12, 8, 8), Color.DeepPink);
            editor.Control.FloodFillAt(new Point(0, 0), Color.White);
            Color center = RasterOperations.GetPixel(editor.Workspace.CompositeCurrentFrame(), 32, 32, 15, 15);
            Color corner = RasterOperations.GetPixel(editor.Workspace.CompositeCurrentFrame(), 32, 32, 1, 1);
            HeadlessHarness.Assert(center.R > 200 && center.G < 120 && center.B > 120,
                $"Expected pink centre, got {center}.");
            HeadlessHarness.Assert(corner.R > 200 && corner.G > 200 && corner.B > 200,
                $"Expected white corner, got {corner}.");

            Form host = new()
            {
                Text = "Image Editor — Tool Workflow",
                ClientSize = new Size(960, 640),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
            };
            editor.Control.Dock = DockStyle.Fill;
            host.Controls.Add(editor.Control);
            GateSuite.ShowHost(host);
            
            editor.Control.Canvas.SyncClientSize();
            editor.Control.Canvas.FitToView();
            for (int i = 0; i < 6; i++)
            {
                WinFormsApplication.DoEvents();
                Thread.Sleep(30);
            }

            using Bitmap? readback = editor.Control.Canvas.ReadbackFrameToBitmap(settleFrames: 4);
            HeadlessHarness.Assert(readback is not null, "Editor workflow readback returned null.");
            string capturePath = Path.Combine(captures, "15-image-editor-tool-workflow.png");
            readback!.Save(capturePath, System.Drawing.Imaging.ImageFormat.Png);
            imageReport.Images.Add(ImageResult.From(
                "Image Editor Tool Workflow",
                "15-image-editor-tool-workflow.png",
                Measure(readback)));
            host.Close();
            host.Dispose();
        });

        HeadlessHarness.RunCase(report, "Editor.Image.Effects.Reshades.EditorApplyWindWaker", () =>
        {
            using EditorSession editor = CreateEditor(
                resources, "Editor WindWaker", 4, 4, Color.FromArgb(255, 100, 100, 100));
            editor.Control.ApplyReshadeForTests("Wind Waker", pixels =>
                ImageAdvancedOperations.WindWaker(pixels, strength: 70));
            Color c = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 4, 4, 1, 1);
            HeadlessHarness.Assert(c.R == 170 && c.G == 170 && c.B == 170,
                $"Editor WindWaker expected (170,170,170), got {c}.");
            HeadlessHarness.Assert(editor.Control.Undo(), "WindWaker should be undoable.");
        });
    }

    private static EditorSession CreateEditor(
        ResourceService resources,
        string name,
        int width,
        int height,
        Color fill)
    {
        string spritePath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, name);
        ImageDocumentSession session = new(
            ImageDocumentSerializer.LoadAtomic(spritePath).Document,
            spritePath);
        ImageWorkspace workspace = ImageWorkspace.CreateBlank(width, height, fill);
        ImageEditorControl control = new(session, workspace);
        return new EditorSession(control, workspace);
    }

    private static void FillRect(byte[] rgba, int width, int x0, int y0, int w, int h, Color color)
    {
        int height = rgba.Length / (width * 4);
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
            int i = (y * width + x) * 4;
            rgba[i] = color.R;
            rgba[i + 1] = color.G;
            rgba[i + 2] = color.B;
            rgba[i + 3] = color.A;
        }
    }

    private static byte[] BuildChecker(int width, int height, Color a, Color b)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Color c = ((x / 2) + (y / 2)) % 2 == 0 ? a : b;
            int i = (y * width + x) * 4;
            rgba[i] = c.R;
            rgba[i + 1] = c.G;
            rgba[i + 2] = c.B;
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    private static ImageMetrics Measure(Bitmap bitmap)
    {
        HashSet<int> colors = [];
        long luminance = 0;
        int opaque = 0;
        int samples = 0;
        int stepX = Math.Max(1, bitmap.Width / 48);
        int stepY = Math.Max(1, bitmap.Height / 48);
        for (int y = 0; y < bitmap.Height; y += stepY)
        for (int x = 0; x < bitmap.Width; x += stepX)
        {
            Color color = bitmap.GetPixel(x, y);
            colors.Add(color.ToArgb());
            samples++;
            if (color.A < 16) continue;
            opaque++;
            luminance += (int)(0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B);
        }

        return new ImageMetrics(
            bitmap.Width,
            bitmap.Height,
            colors.Count,
            samples == 0 ? 0 : opaque / (double)samples,
            samples == 0 ? 0 : luminance / (double)samples);
    }

    private sealed class EditorSession : IDisposable
    {
        public EditorSession(ImageEditorControl control, ImageWorkspace workspace)
        {
            Control = control;
            Workspace = workspace;
        }

        public ImageEditorControl Control { get; }
        public ImageWorkspace Workspace { get; }

        public void Dispose() => Control.Dispose();
    }
}

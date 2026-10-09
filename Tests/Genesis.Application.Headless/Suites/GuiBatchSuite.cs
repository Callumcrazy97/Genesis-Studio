using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// GUI drawing in bulk (DrawRectanglesFromList, DrawSpritePartsFromList): a batch draws the very
/// pixels the single commands draw, in call order with the drawing around it, on every renderer;
/// the commands shrug off bad lists; and what 1,200 and 5,000 rectangles cost a real Draw GUI
/// event drawn one call each against one batch (script and overlay time, draw calls), written to
/// gui-batch-cost.txt.
/// </summary>
internal static class GuiBatchSuite
{
    private const string Quads = "Batch Quads";
    private const int Width = 640, Height = 360;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "GuiBatch");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        ResourceService resources = ctx.Resources ?? throw new InvalidOperationException("No resource service.");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GuiBatch.ListCommandsAreLenient", () =>
        {
            PgslRecordingDrawSurface surface = new();
            PgslContext context = new() { RoomWidth = Width, RoomHeight = Height, DrawSurface = surface };
            PgslContext? previous = PgslCommands.BindContext(context);
            try
            {
                HeadlessHarness.Assert(PgslCommands.DrawRectanglesFromList(12345) == 0 && surface.TotalCalls == 0,
                    "A list that does not exist drew something.");
                double list = PgslCommands.DsListCreate();
                // A good entry, one with no width, one not a number, one whose numbers are text, an
                // invisible one, and three numbers left over.
                double[] good = [10, 20, 30, 40, 255, 128, 0, 1];
                foreach (double value in good) PgslCommands.DsListAdd(list, value);
                foreach (double value in new double[] { 10, 20, 0, 40, 255, 0, 0, 1 }) PgslCommands.DsListAdd(list, value);
                foreach (double value in new double[] { double.NaN, 20, 5, 5, 255, 0, 0, 1 }) PgslCommands.DsListAdd(list, value);
                foreach (string value in new[] { "1", "2", "3", "4", "0", "0", "255", "0.5" }) PgslCommands.DsListAddString(list, value);
                foreach (double value in new double[] { 1, 2, 3, 4, 255, 255, 255, 0 }) PgslCommands.DsListAdd(list, value);
                foreach (double value in new double[] { 1, 2, 3 }) PgslCommands.DsListAdd(list, value);
                double drawn = PgslCommands.DrawRectanglesFromList(list, 100, 50, 2, 3);
                HeadlessHarness.Assert(drawn == 2 && surface.Rectangles.Count == 2,
                    $"Expected the good entry and the text one, drew {drawn} ({surface.Rectangles.Count} recorded).");
                PgslRecordingDrawSurface.RectRecord first = surface.Rectangles[0], second = surface.Rectangles[1];
                HeadlessHarness.Assert(first is { X: 120, Y: 110, W: 60, H: 120, Filled: true }
                    && first.Color == Color.FromArgb(255, 255, 128, 0),
                    "The entry was not placed, scaled or coloured as given: " + first);
                HeadlessHarness.Assert(second.Color == Color.FromArgb(127, 0, 0, 255) && second.W == 6 && second.H == 12,
                    "Numbers held as text were not read like DsListGet reads them: " + second);
                double parts = PgslCommands.DsListCreate();
                // A good part placed at 5, 6 and scaled by 2, then one with no width.
                foreach (double value in new double[] { 3, 0.25, 0, 0.5, 1, 5, 6, 7, 8, 0.75, 0, 0, 0, 1, 1, 5, 6, 0, 8, 1 })
                    PgslCommands.DsListAdd(parts, value);
                HeadlessHarness.Assert(PgslCommands.DrawSpritePartsFromList("Some Image", parts, 1, 2, 2, 2) == 1 && surface.Sprites.Count == 1,
                    $"Expected one image part, recorded {surface.Sprites.Count}.");
                PgslRecordingDrawSurface.SpriteRecord part = surface.Sprites[0];
                HeadlessHarness.Assert(part.Frame == 3 && part.Destination == new RectangleF(11, 14, 14, 16)
                    && part.Source == RectangleF.FromLTRB(0.25f, 0, 0.5f, 1) && Math.Abs(part.Alpha - 0.75f) < 1e-6,
                    "The image part was not read in DrawSpritePart's order: " + part);
                HeadlessHarness.Assert(PgslCommands.DrawSpritePartsFromList("", parts) == 0, "Parts of an unnamed image were drawn.");
                HeadlessHarness.Assert(PgslCommands.DrawRectanglesFromList(list, double.NaN, 0, 1, 1) == 0
                    && PgslCommands.DrawRectanglesFromList(list, 0, 0, double.PositiveInfinity, 1) == 0,
                    "A position or scale that is not a number drew something.");
                PgslCommands.BindContext(new PgslContext());
                HeadlessHarness.Assert(PgslCommands.DrawRectanglesFromList(list) == 0, "A list was drawn without a surface or from another instance.");
            }
            finally { PgslCommands.BindContext(previous); }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GuiBatch.BatchesDrawWhatSingleCallsDrawOnEveryBackend", () =>
        {
            string art = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, Quads);
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(art).Document, art, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.Save(session, ImageWorkspace.FromRgba(QuadrantPixels(), 16, 16));
            ResourceCatalog.Invalidate(project.RootPath);

            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            viewport.OnRender += renderer =>
            {
                renderer.Clear(0f, 0f, 0f); renderer.Set3DFrameActive(false);
                renderer.SetCamera2D(renderer.PixelWidth / 2f, renderer.PixelHeight / 2f, 1, 0);
            };
            viewport.OnPostFrame += renderer => renderer.ComposeOverlay(canvas =>
            {
                OverlayHudCanvas hud = new(canvas, renderer.PixelWidth, renderer.PixelHeight);
                PgslRenderDrawSurface surface = new(renderer, hud, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, isGui: true);
                PgslContext context = new() { RoomWidth = renderer.PixelWidth, RoomHeight = renderer.PixelHeight, DrawSurface = surface };
                PgslContext? previous = PgslCommands.BindContext(context);
                string previousProject = PgslCommands.ProjectPath;
                PgslCommands.ProjectPath = project.RootPath;
                try { DrawScene(); }
                finally { PgslCommands.BindContext(previous); PgslCommands.ProjectPath = previousProject; }
            });
            game.Controls.Add(viewport); GateSuite.ShowHost(game);

            List<string> failures = [];
            List<string> readings = [];
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                if (frame is null || viewport.RenderFaultCount != 0 || frame.Width < Width || frame.Height < Height)
                { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                string name = backend.ShortName.ToLowerInvariant();
                frame.Save(Path.Combine(ctx.Captures, "gui-batch-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                ctx.Report.Images.Add(ImageResult.From("Runtime.GuiBatch." + backend.ShortName, "gui-batch-" + name + ".png", VisualCapture.Measure(frame)));

                // The left half is drawn one command at a time, the right half by the batches: the
                // same pixels, 320 to the right.
                int differing = 0;
                Point firstDifference = Point.Empty;
                for (int y = 0; y < 240; y++)
                    for (int x = 0; x < 300; x++)
                    {
                        Color single = frame.GetPixel(x, y), batch = frame.GetPixel(x + 320, y);
                        if (Math.Abs(single.R - batch.R) > 1 || Math.Abs(single.G - batch.G) > 1 || Math.Abs(single.B - batch.B) > 1)
                        {
                            if (differing++ == 0) firstDifference = new Point(x, y);
                        }
                    }
                readings.Add($"{backend.ShortName} differing={differing}");
                if (differing > 0)
                    failures.Add($"{backend.ShortName}: {differing} pixels differ between single commands and batches, first at {firstDifference}: "
                        + frame.GetPixel(firstDifference.X, firstDifference.Y) + " against " + frame.GetPixel(firstDifference.X + 320, firstDifference.Y));

                void Check(string what, int x, int y, int r, int g, int b, int tolerance = 3)
                {
                    Color pixel = frame.GetPixel(x, y);
                    readings.Add($"{backend.ShortName} {what}={pixel.R},{pixel.G},{pixel.B}");
                    if (Math.Abs(pixel.R - r) > tolerance || Math.Abs(pixel.G - g) > tolerance || Math.Abs(pixel.B - b) > tolerance)
                        failures.Add($"{backend.ShortName}: {what} at {x},{y} is {pixel}, expected {r},{g},{b}");
                }
                foreach (int offset in new[] { 0, 320 })
                {
                    string side = offset == 0 ? "single" : "batch";
                    Check(side + "Red", offset + 25, 25, 255, 0, 0);
                    // Green at alpha 127/255 over red.
                    Check(side + "GreenOverRed", offset + 70, 70, 128, 127, 0);
                    Check(side + "Blue", offset + 30, 110, 0, 0, 255);
                    // Ten-pixel cells laid out in a list and scaled by 10.
                    Check(side + "CellA", offset + 205, 25, 255, 255, 0);
                    Check(side + "CellB", offset + 225, 45, 0, 255, 255);
                    // Image parts: the red top-left quarter, then the green top-right one.
                    Check(side + "PartRed", offset + 40, 170, 255, 0, 0, 6);
                    Check(side + "PartGreen", offset + 90, 170, 0, 255, 0, 6);
                    Check(side + "PartBlueHalf", offset + 140, 170, 0, 0, 127, 8);
                }
                // Order across a batch: grey single, white batch over it, red single over that.
                Check("orderGreyUnder", 22, 300, 128, 128, 128);
                Check("orderWhiteBatchOverGrey", 45, 300, 255, 255, 255);
                Check("orderRedOverBatch", 75, 300, 200, 0, 0);
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "gui-batch-readings.txt"), readings);
            Console.WriteLine("GuiBatch readings: " + string.Join(", ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GuiBatch.CostOfThousandsOfRectanglesInADrawGuiEvent", () =>
        {
            string script = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.PgslScript), ResourceKind.PgslScript, "Batch Bench");
            File.WriteAllText(script, """
                // count rectangles of 8 numbers each, on a 100-wide grid of 6-pixel cells.
                function BatchBenchFill(list, i0, i1) {
                    for (var i = i0; i < i1; i = i + 1) {
                        DsListAdd(list, (i % 100) * 6); DsListAdd(list, Floor(i / 100) * 6); DsListAdd(list, 5); DsListAdd(list, 5);
                        DsListAdd(list, (i * 37) % 256); DsListAdd(list, (i * 91) % 256); DsListAdd(list, (i * 53) % 256); DsListAdd(list, 1);
                    }
                }
                function BatchBenchMake(count) {
                    var list = DsListCreate();
                    for (var q = 0; q < count; q = q + 200) { BatchBenchFill(list, q, Min(count, q + 200)); }
                    return list;
                }
                // As a game draws a map one rectangle at a time (250 a call, under the VM's cap).
                function BatchBenchRun(list, q0, q1) {
                    for (var q = q0; q < q1; q = q + 8) {
                        DrawSetColorRgb(DsListGet(list, q + 4), DsListGet(list, q + 5), DsListGet(list, q + 6));
                        DrawSetAlpha(DsListGet(list, q + 7));
                        var px = DsListGet(list, q); var py = DsListGet(list, q + 1);
                        DrawRectangle(px, py, px + DsListGet(list, q + 2), py + DsListGet(list, q + 3));
                    }
                }
                function BatchBenchSingles(list) {
                    var n = DsListSize(list);
                    for (var q = 0; q < n; q = q + 2000) { BatchBenchRun(list, q, Min(n, q + 2000)); }
                }
                """);
            ResourceCatalog.Invalidate(project.RootPath);

            Type bufferedType = typeof(Genesis.Runtime.GenesisRuntimeHost).GetNestedType("BufferedHudCanvas", System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The game's buffered GUI canvas was not found.");
            IHudCanvas buffered = (IHudCanvas)Activator.CreateInstance(bufferedType, nonPublic: true)!;

            using Form gameHost = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            gameHost.Controls.Add(viewport); GateSuite.ShowHost(gameHost);
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            viewport.BackendOverride = RenderBackendOption.SilkNetDx11; GateSuite.Pump(3, 15);
            using (Bitmap? warm = viewport.ReadbackFrameToBitmap(2)) { }
            var renderer = viewport.Renderer ?? throw new InvalidOperationException("No renderer.");

            List<string> lines = [];
            List<string> failures = [];
            Dictionary<string, Bitmap> pictures = [];
            foreach (int count in new[] { 1200, 5000 })
                foreach (bool batch in new[] { false, true })
                {
                    string label = (batch ? "batch" : "single") + count;
                    string objectPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject), ResourceKind.GameObject, "Bench " + label);
                    File.WriteAllText(objectPath, $$"""
                        { "name": "Bench {{label}}", "components": [
                          { "id": "cmp-script", "type": "ScriptComponent", "enabled": true, "props": { "ScriptClass": "Bench {{label}}" } } ] }
                        """);
                    ObjectEventStore.Save(objectPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Create"] = $"bench = BatchBenchMake({count});\n",
                        ["DrawGui"] = batch ? "DrawRectanglesFromList(bench);\n" : "BatchBenchSingles(bench);\n",
                    });
                    ResourceCatalog.Invalidate(project.RootPath);

                    RoomAsset room = RoomAsset.Create("Bench Room " + label, RoomDimension.TwoD);
                    room.Nodes.Add(new RoomNode
                    {
                        Kind = RoomNodeKind.GameObject, Name = "Bench", Transform = new() { X = 0, Y = 0 },
                        GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) },
                    });
                    using RuntimeScene scene = new("Bench " + label) { Input = new Genesis.Runtime.Input.InputState() };
                    ProjectGameContext game = new(project.RootPath, scene, renderer, null, room, null);
                    ScriptHostSystem scripts = new(); scripts.SetContext(game);
                    IGameContext? oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
                    PgslContext? oldContext = PgslCommands.BindContext(new PgslContext());
                    PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
                    double scriptMs = 0, overlayMs = 0;
                    int measured = 0, drawCalls = -1;
                    bool measuring = false;
                    Action<IRenderController> clear = current =>
                    {
                        current.Clear(0f, 0f, 0f); current.Set3DFrameActive(false);
                        current.SetCamera2D(current.PixelWidth / 2f, current.PixelHeight / 2f, 1, 0);
                    };
                    // GenesisRuntimeHost.ComposeGameplayOverlay, in short, timed in its two halves.
                    Action<IRenderController> overlay = current =>
                    {
                        bufferedType.GetMethod("Reset")!.Invoke(buffered, [current.PixelWidth, current.PixelHeight]);
                        long started = Stopwatch.GetTimestamp();
                        scripts.DispatchPgslGuiDraw(current, buffered);
                        long scripted = Stopwatch.GetTimestamp();
                        current.FlushOverlaySprites();
                        current.ComposeOverlay(canvas => bufferedType.GetMethod("Replay")!.Invoke(buffered,
                            [new OverlayHudCanvas(canvas, current.PixelWidth, current.PixelHeight)]));
                        long composed = Stopwatch.GetTimestamp();
                        if (!measuring) return;
                        scriptMs += Stopwatch.GetElapsedTime(started, scripted).TotalMilliseconds;
                        overlayMs += Stopwatch.GetElapsedTime(scripted, composed).TotalMilliseconds;
                        measured++;
                        drawCalls = current.GetStats().DrawCalls2D;
                    };
                    try
                    {
                        new RoomSceneBuilder(project.RootPath, scripts).Build(scene, room);
                        scripts.Update(1f / 60);
                        HeadlessHarness.Assert(scripts.RecentDiagnostics.Count == 0,
                            label + ": the bench script failed: " + string.Join(';', scripts.RecentDiagnostics.Select(d => d.EventName + ": " + d.Message)));
                        viewport.OnRender += clear;
                        viewport.OnPostFrame += overlay;
                        using (Bitmap? warmUp = viewport.ReadbackFrameToBitmap(4)) { }
                        measuring = true;
                        Bitmap? picture = viewport.ReadbackFrameToBitmap(24);
                        measuring = false;
                        if (picture is null || viewport.RenderFaultCount != 0)
                        { failures.Add(label + ": no frame " + viewport.LastRenderException); continue; }
                        pictures[label] = picture;
                        if (count == 1200) picture.Save(Path.Combine(ctx.Captures, "gui-batch-cost-" + label + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        HeadlessHarness.Assert(scripts.RecentDiagnostics.Count == 0,
                            label + ": the Draw GUI event failed: " + string.Join(';', scripts.RecentDiagnostics.Select(d => d.EventName + ": " + d.Message)));
                        lines.Add($"{count} rectangles, {(batch ? "one DrawRectanglesFromList" : "one DrawRectangle each")}: "
                            + $"Draw GUI event {scriptMs / measured:F2} ms, overlay replay and submit {overlayMs / measured:F2} ms, "
                            + $"total {(scriptMs + overlayMs) / measured:F2} ms a frame; {drawCalls} GUI draw calls (DX11, mean of {measured} frames)");
                    }
                    finally
                    {
                        viewport.OnRender -= clear;
                        viewport.OnPostFrame -= overlay;
                        PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath;
                    }
                }

            // The batch must look exactly like the single calls it replaces.
            foreach (int count in new[] { 1200, 5000 })
            {
                if (!pictures.TryGetValue("single" + count, out Bitmap? single) || !pictures.TryGetValue("batch" + count, out Bitmap? batch)) continue;
                int differing = 0;
                for (int y = 0; y < Math.Min(single.Height, batch.Height); y += 2)
                    for (int x = 0; x < Math.Min(single.Width, batch.Width); x += 2)
                        if (single.GetPixel(x, y).ToArgb() != batch.GetPixel(x, y).ToArgb()) differing++;
                if (differing > 0) failures.Add($"{count}: the batch drew {differing} sampled pixels differently from single calls");
                // The first rectangle is black; the second, at 6, 0, is 37, 91, 53.
                Color second = single.GetPixel(8, 2);
                if (Math.Abs(second.R - 37) > 2 || Math.Abs(second.G - 91) > 2 || Math.Abs(second.B - 53) > 2)
                    failures.Add($"{count}: the second rectangle is {second}, not 37, 91, 53");
            }
            foreach (Bitmap picture in pictures.Values) picture.Dispose();
            File.WriteAllLines(Path.Combine(ctx.Captures, "gui-batch-cost.txt"), lines);
            foreach (string line in lines) Console.WriteLine("GuiBatch cost: " + line);
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });
    }

    /// <summary>16 x 16: red, green, blue and white quarters (top-left, top-right, bottom-left, bottom-right).</summary>
    private static byte[] QuadrantPixels()
    {
        byte[] rgba = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                (byte r, byte g, byte b) = (x < 8, y < 8) switch
                {
                    (true, true) => ((byte)255, (byte)0, (byte)0),
                    (false, true) => ((byte)0, (byte)255, (byte)0),
                    (true, false) => ((byte)0, (byte)0, (byte)255),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                int at = ((y * 16) + x) * 4;
                rgba[at] = r; rgba[at + 1] = g; rgba[at + 2] = b; rgba[at + 3] = 255;
            }
        return rgba;
    }

    /// <summary>Left half one command at a time; right half the same through the batch commands.</summary>
    private static void DrawScene()
    {
        // Rectangles: red, green at half alpha over it, blue; then cells of a 10-pixel grid.
        (double X, double Y, double W, double H, int R, int G, int B, double A)[] rects =
        [
            (20, 20, 60, 60, 255, 0, 0, 1),
            (50, 50, 60, 60, 0, 255, 0, 0.5),
            (20, 100, 100, 20, 0, 0, 255, 1),
        ];
        (double X, double Y, double W, double H, int R, int G, int B, double A)[] cells =
        [
            (20, 2, 1, 1, 255, 255, 0, 1),
            (22, 4, 2, 1, 0, 255, 255, 1),
        ];
        foreach (var r in rects) Single(r.X, r.Y, r.W, r.H, r.R, r.G, r.B, r.A);
        foreach (var c in cells) Single(c.X * 10, c.Y * 10, c.W * 10, c.H * 10, c.R, c.G, c.B, c.A);
        PgslCommands.DrawSetAlpha(1);
        PgslCommands.DrawSpritePart(Quads, 0, 0, 0, 0.5, 0.5, 20, 150, 40, 40, 1);
        PgslCommands.DrawSpritePart(Quads, 0, 0.5, 0, 1, 0.5, 70, 150, 40, 40, 1);
        PgslCommands.DrawSpritePart(Quads, 0, 0, 0.5, 0.5, 1, 120, 150, 40, 40, 0.5);

        PgslCommands.DrawRectanglesFromList(List(rects), 320, 0);
        PgslCommands.DrawRectanglesFromList(List(cells), 320, 0, 10, 10);
        double parts = PgslCommands.DsListCreate();
        foreach (double value in new double[]
                 {
                     0, 0, 0, 0.5, 0.5, 20, 150, 40, 40, 1,
                     0, 0.5, 0, 1, 0.5, 70, 150, 40, 40, 1,
                     0, 0, 0.5, 0.5, 1, 120, 150, 40, 40, 0.5,
                 })
            PgslCommands.DsListAdd(parts, value);
        PgslCommands.DrawSpritePartsFromList(Quads, parts, 320, 0);

        // A batch in call order between two single rectangles.
        Single(10, 280, 40, 40, 128, 128, 128, 1);
        PgslCommands.DrawRectanglesFromList(List([(40, 280, 40, 40, 255, 255, 255, 1)]));
        Single(70, 280, 40, 40, 200, 0, 0, 1);
    }

    private static void Single(double x, double y, double w, double h, int r, int g, int b, double a)
    {
        PgslCommands.DrawSetColorRgb(r, g, b);
        PgslCommands.DrawSetAlpha(a);
        PgslCommands.DrawRectangle(x, y, x + w, y + h);
    }

    private static double List((double X, double Y, double W, double H, int R, int G, int B, double A)[] entries)
    {
        double list = PgslCommands.DsListCreate();
        foreach (var e in entries)
            foreach (double value in new[] { e.X, e.Y, e.W, e.H, e.R, e.G, e.B, e.A })
                PgslCommands.DsListAdd(list, value);
        return list;
    }
}

using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Textures a script paints (TextureCreate, TextureSetPixels, TextureSetRegion,
/// TextureFillRectanglesFromList, DrawTexture, DrawTexturePart, TextureDestroy): the pixels as given,
/// sharp when scaled, changed regions reaching the GPU, drawn in the GUI and in the 2D world on every
/// renderer, freed on destroy and with the game; bad arguments harmless; and what a 5,000-rectangle
/// map costs a frame painted once into a texture (script-textures-cost.txt).
/// </summary>
internal static class ScriptTextureSuite
{
    private const int Width = 640, Height = 360;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "ScriptTextures");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        ResourceService resources = ctx.Resources ?? throw new InvalidOperationException("No resource service.");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ScriptTextures.PixelsAreSetAsGivenAndBadArgumentsAreHarmless", () =>
        {
            ScriptTextures.Reset();
            PgslContext? previous = PgslCommands.BindContext(new PgslContext());
            try
            {
                foreach ((double w, double h) in new[] { (0.0, 4.0), (4.0, -1.0), (5000.0, 4.0), (double.NaN, 4.0), (4.0, double.PositiveInfinity) })
                    HeadlessHarness.Assert(PgslCommands.TextureCreate(w, h) == 0, $"TextureCreate({w}, {h}) made a texture.");
                double texture = PgslCommands.TextureCreate(4, 3);
                HeadlessHarness.Assert(texture > 0 && PgslCommands.TextureWidth(texture) == 4 && PgslCommands.TextureHeight(texture) == 3
                    && PgslCommands.TextureExists(texture), "A 4 x 3 texture was not made: " + texture);
                HeadlessHarness.Assert(Pixel(texture, 3, 2) == (0, 0, 0, 0), "A new texture is not transparent.");

                double list = PgslCommands.DsListCreate();
                foreach (double value in new double[] { 255, 0, 0, 1, 0, 255, 0, 0.5, 300, -4, 12.7, 2 })
                    PgslCommands.DsListAdd(list, value);
                HeadlessHarness.Assert(PgslCommands.TextureSetPixels(texture, list) == 3, "TextureSetPixels did not set the three pixels it was given.");
                HeadlessHarness.Assert(Pixel(texture, 0, 0) == (255, 0, 0, 255) && Pixel(texture, 1, 0) == (0, 255, 0, 127)
                    && Pixel(texture, 2, 0) == (255, 0, 12, 255) && Pixel(texture, 3, 0) == (0, 0, 0, 0),
                    "Pixels were not stored as DrawSetColorRgb and DrawSetAlpha round them: "
                    + Pixel(texture, 0, 0) + Pixel(texture, 1, 0) + Pixel(texture, 2, 0) + Pixel(texture, 3, 0));

                // A region half outside the texture: only the pixels inside are set.
                double blue = PgslCommands.DsListCreate();
                for (int i = 0; i < 4; i++) foreach (double value in new double[] { 0, 0, 255, 1 }) PgslCommands.DsListAdd(blue, value);
                HeadlessHarness.Assert(PgslCommands.TextureSetRegion(texture, 3, 1, 2, 2, blue) == 2
                    && Pixel(texture, 3, 1) == (0, 0, 255, 255) && Pixel(texture, 3, 2) == (0, 0, 255, 255) && Pixel(texture, 2, 1) == (0, 0, 0, 0),
                    "TextureSetRegion did not set just the pixels inside the texture.");

                double rects = PgslCommands.DsListCreate();
                foreach (double value in new double[] { 0, 1, 2, 2, 10, 20, 30, 1, -5, -5, 6, 6, 0, 0, 0, 0, 0, 0, 0, 5, 1, 1, 1, 1 })
                    PgslCommands.DsListAdd(rects, value);
                HeadlessHarness.Assert(PgslCommands.TextureFillRectanglesFromList(texture, rects) == 2
                    && Pixel(texture, 0, 1) == (10, 20, 30, 255) && Pixel(texture, 1, 2) == (10, 20, 30, 255),
                    "TextureFillRectanglesFromList did not paint and then clear as listed.");
                // The second reaches in from -5, -5 to 1, 1: just the corner pixel, made transparent.
                HeadlessHarness.Assert(Pixel(texture, 0, 0) == (0, 0, 0, 0) && Pixel(texture, 1, 0) == (0, 255, 0, 127)
                    && Pixel(texture, 0, 1) == (10, 20, 30, 255),
                    "A rectangle reaching past the texture did not replace just the pixels inside it, alpha included: "
                    + Pixel(texture, 0, 0) + Pixel(texture, 1, 0) + Pixel(texture, 0, 1));

                // Garbage: no list, no texture, a destroyed texture, numbers that are not numbers.
                HeadlessHarness.Assert(PgslCommands.TextureSetPixels(9999, list) == 0 && PgslCommands.TextureSetPixels(texture, 9999) == 0
                    && PgslCommands.TextureSetRegion(texture, double.NaN, 0, 2, 2, blue) == 0
                    && PgslCommands.TextureSetRegion(texture, 0, 0, 1e12, 1e12, blue) == 4
                    && PgslCommands.TextureFillRectanglesFromList(-3, rects) == 0,
                    "A bad texture, list or rectangle was not refused.");
                PgslCommands.DrawTexture(texture, 0, 0, 1, 1, 0, 1);
                PgslCommands.DrawTexturePart(texture, 0, 0, 1, 1, 0, 0, 10, 10, 1);
                PgslCommands.TextureDestroy(texture);
                PgslCommands.TextureDestroy(texture);
                HeadlessHarness.Assert(!PgslCommands.TextureExists(texture) && PgslCommands.TextureWidth(texture) == 0
                    && PgslCommands.TextureSetPixels(texture, list) == 0, "A destroyed texture still answers.");

                // The game's pixel budget: textures past it are refused rather than exhausting memory.
                List<double> big = [];
                for (int i = 0; i < 6; i++) big.Add(PgslCommands.TextureCreate(4096, 4096));
                HeadlessHarness.Assert(big.Count(id => id > 0) == 4, $"Expected four 4096 x 4096 textures within the budget, made {big.Count(id => id > 0)}.");
                foreach (double id in big) PgslCommands.TextureDestroy(id);
                HeadlessHarness.Assert(PgslCommands.TextureCreate(4096, 4096) > 0, "Destroying textures did not give their pixels back to the budget.");
                ScriptTextures.Reset();
                HeadlessHarness.Assert(ScriptTextures.Count == 0, "Reset did not free every texture.");
            }
            finally { PgslCommands.BindContext(previous); ScriptTextures.Reset(); }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ScriptTextures.DrawnInTheGuiAndThe2DWorldOnEveryBackend", () =>
        {
            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            PgslContext scriptContext = new() { RoomWidth = Width, RoomHeight = Height };
            double map = 0, checker = 0;
            bool destroyedDrawn = false;
            TextureHandle mapHandle = TextureHandle.Invalid;
            void WithSurface(IRenderController renderer, PgslRenderDrawSurface surface, Action draw)
            {
                scriptContext.DrawSurface = surface;
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                string previousProject = PgslCommands.ProjectPath;
                PgslCommands.ProjectPath = project.RootPath;
                try { draw(); }
                finally { PgslCommands.BindContext(previous); PgslCommands.ProjectPath = previousProject; }
            }
            viewport.OnRender += renderer =>
            {
                renderer.Clear(0f, 0f, 0f); renderer.Set3DFrameActive(false);
                renderer.SetCamera2D(renderer.PixelWidth / 2f, renderer.PixelHeight / 2f, 1, 0);
                // A 2D Draw event: world coordinates, here the screen's.
                WithSurface(renderer, new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight, projectPath: project.RootPath),
                    () => PgslCommands.DrawTexture(map, 400, 220, 2, 2, 0, 1));
            };
            viewport.OnPostFrame += renderer => renderer.ComposeOverlay(canvas =>
            {
                OverlayHudCanvas hud = new(canvas, renderer.PixelWidth, renderer.PixelHeight);
                WithSurface(renderer, new PgslRenderDrawSurface(renderer, hud, renderer.PixelWidth, renderer.PixelHeight, projectPath: project.RootPath, isGui: true), () =>
                {
                    // A 32 x 16 map at 4x, its right half again at 4x, a checker at 8x, and one that is destroyed.
                    PgslCommands.DrawTexture(map, 20, 20, 4, 4, 0, 1);
                    PgslCommands.DrawTexturePart(map, 0.5, 0, 1, 1, 200, 20, 64, 64, 1);
                    PgslCommands.DrawTexture(checker, 20, 120, 8, 8, 0, 1);
                    if (destroyedDrawn) PgslCommands.DrawTexture(9999, 300, 120, 8, 8, 0, 1);
                    mapHandle = ScriptTextures.Resolve((int)map, renderer, out _);
                });
            });
            game.Controls.Add(viewport); GateSuite.ShowHost(game);

            List<string> failures = [];
            List<string> readings = [];
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                ScriptTextures.Reset();
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    map = PgslCommands.TextureCreate(32, 16);
                    checker = PgslCommands.TextureCreate(4, 1);
                    double paint = PgslCommands.DsListCreate();
                    // Left half red, right half green at half alpha; the checker black, white, black, white.
                    foreach (double value in new double[] { 0, 0, 16, 16, 255, 0, 0, 1, 16, 0, 16, 16, 0, 255, 0, 0.5 })
                        PgslCommands.DsListAdd(paint, value);
                    PgslCommands.TextureFillRectanglesFromList(map, paint);
                    double cells = PgslCommands.DsListCreate();
                    foreach (double value in new double[] { 0, 0, 0, 1, 255, 255, 255, 1, 0, 0, 0, 1, 255, 255, 255, 1 })
                        PgslCommands.DsListAdd(cells, value);
                    PgslCommands.TextureSetPixels(checker, cells);
                }
                finally { PgslCommands.BindContext(previous); }

                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using Bitmap? first = viewport.ReadbackFrameToBitmap(3);
                // A region changed after the texture reached the GPU: a blue square in the red half.
                previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    double square = PgslCommands.DsListCreate();
                    for (int i = 0; i < 16; i++) foreach (double value in new double[] { 0, 0, 255, 1 }) PgslCommands.DsListAdd(square, value);
                    PgslCommands.TextureSetRegion(map, 4, 4, 4, 4, square);
                }
                finally { PgslCommands.BindContext(previous); }
                using Bitmap? changed = viewport.ReadbackFrameToBitmap(2);
                if (first is null || changed is null || viewport.RenderFaultCount != 0)
                { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                string name = backend.ShortName.ToLowerInvariant();
                changed.Save(Path.Combine(ctx.Captures, "script-textures-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                ctx.Report.Images.Add(ImageResult.From("Runtime.ScriptTextures." + backend.ShortName, "script-textures-" + name + ".png", VisualCapture.Measure(changed)));

                void Check(Bitmap image, string what, int x, int y, int r, int g, int b, int tolerance = 3)
                {
                    Color pixel = image.GetPixel(x, y);
                    readings.Add($"{backend.ShortName} {what}={pixel.R},{pixel.G},{pixel.B}");
                    if (Math.Abs(pixel.R - r) > tolerance || Math.Abs(pixel.G - g) > tolerance || Math.Abs(pixel.B - b) > tolerance)
                        failures.Add($"{backend.ShortName}: {what} at {x},{y} is {pixel}, expected {r},{g},{b}");
                }
                // GUI: 32 x 16 at 4x is 128 x 64 from 20, 20.
                Check(first, "guiRed", 40, 70, 255, 0, 0);
                Check(first, "guiGreenHalf", 120, 50, 0, 127, 0);
                Check(first, "guiNotYetBlue", 40, 40, 255, 0, 0);
                Check(changed, "guiChangedBlue", 40, 40, 0, 0, 255);
                Check(changed, "guiRedAroundTheChange", 60, 60, 255, 0, 0);
                Check(changed, "partGreenHalf", 230, 50, 0, 127, 0);
                // Sharp when scaled: each 8 x 8 cell of the checker is one colour right to its edge.
                Check(changed, "checkerBlackEdge", 20 + 7, 124, 0, 0, 0, 0);
                Check(changed, "checkerWhiteEdge", 20 + 8, 124, 255, 255, 255, 0);
                Check(changed, "checkerWhiteFarEdge", 20 + 15, 124, 255, 255, 255, 0);
                Check(changed, "checkerBlackAgain", 20 + 16, 124, 0, 0, 0, 0);
                // The 2D world: at 2x from 400, 220; the blue square included.
                Check(changed, "worldRed", 410, 250, 255, 0, 0);
                Check(changed, "worldBlue", 412, 232, 0, 0, 255);
                Check(changed, "worldGreenHalf", 450, 230, 0, 127, 0);

                // Destroying frees the GPU copy; a texture that is not there draws nothing.
                TextureHandle handle = mapHandle;
                bool liveBefore = viewport.Renderer?.IsTextureLive(handle) == true;
                previous = PgslCommands.BindContext(scriptContext);
                try { PgslCommands.TextureDestroy(map); }
                finally { PgslCommands.BindContext(previous); }
                destroyedDrawn = true;
                using Bitmap? after = viewport.ReadbackFrameToBitmap(1);
                destroyedDrawn = false;
                if (!liveBefore || viewport.Renderer?.IsTextureLive(handle) != false)
                    failures.Add($"{backend.ShortName}: the GPU copy was not {(liveBefore ? "freed by TextureDestroy" : "made")}");
                if (after is not null) Check(after, "destroyedDrawsNothing", 40, 70, 0, 0, 0);
                ScriptTextures.Reset();
                if (viewport.Renderer?.IsTextureLive(handle) == true) failures.Add(backend.ShortName + ": Reset left a texture on the GPU");
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "script-textures-readings.txt"), readings);
            Console.WriteLine("ScriptTextures readings: " + string.Join(", ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ScriptTextures.CostOfAMapPaintedOnceAndDrawnEveryFrame", () =>
        {
            Type bufferedType = typeof(GenesisRuntimeHost).GetNestedType("BufferedHudCanvas", System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The game's buffered GUI canvas was not found.");
            IHudCanvas buffered = (IHudCanvas)Activator.CreateInstance(bufferedType, nonPublic: true)!;
            string objectPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject), ResourceKind.GameObject, "Texture Map Bench");
            File.WriteAllText(objectPath, """
                { "name": "Texture Map Bench", "components": [
                  { "id": "cmp-script", "type": "ScriptComponent", "enabled": true, "props": { "ScriptClass": "Texture Map Bench" } } ] }
                """);
            ObjectEventStore.Save(objectPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Create"] = "runs = DsListCreate();\nmap = TextureCreate(600, 300);\n",
                ["DrawGui"] = "DrawTexture(map, 0, 0, 1, 1, 0, 1);\n",
            });
            ResourceCatalog.Invalidate(project.RootPath);

            using Form gameHost = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            gameHost.Controls.Add(viewport); GateSuite.ShowHost(gameHost);
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            viewport.BackendOverride = RenderBackendOption.SilkNetDx11; GateSuite.Pump(3, 15);
            using (Bitmap? warm = viewport.ReadbackFrameToBitmap(2)) { }
            var renderer = viewport.Renderer ?? throw new InvalidOperationException("No renderer.");

            RoomAsset room = RoomAsset.Create("Texture Map Bench Room", RoomDimension.TwoD);
            room.Nodes.Add(new RoomNode
            {
                Kind = RoomNodeKind.GameObject, Name = "Bench", Transform = new() { X = 0, Y = 0 },
                GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) },
            });
            using RuntimeScene scene = new("Texture Map Bench") { Input = new Genesis.Runtime.Input.InputState() };
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
            List<string> lines = [];
            try
            {
                new RoomSceneBuilder(project.RootPath, scripts).Build(scene, room);
                scripts.Update(1f / 60);
                PgslBehavior behavior = scripts.Instances.OfType<PgslBehavior>().Single();
                HeadlessHarness.Assert(behavior.HasGuiDrawScript && scripts.RecentDiagnostics.Count == 0,
                    "The bench object did not compile: " + string.Join(';', scripts.RecentDiagnostics));
                IReadOnlyDictionary<string, object> variables = behavior.GetVariablesSnapshot();
                double runs = Convert.ToDouble(variables["runs"]), map = Convert.ToDouble(variables["map"]);
                // The map's 5,000 rectangles go into the instance's list (as the game keeps its runs),
                // then are painted into the texture once: the cost of a rebuild.
                PgslContext instanceContext = behavior.Context;
                PgslContext? previous = PgslCommands.BindContext(instanceContext);
                double paintMs;
                try
                {
                    for (int i = 0; i < 5000; i++)
                        foreach (double value in new double[] { (i % 100) * 6, (i / 100) * 6, 5, 5, (i * 37) % 256, (i * 91) % 256, (i * 53) % 256, 1 })
                            PgslCommands.DsListAdd(runs, value);
                    var paint = Stopwatch.StartNew();
                    double painted = PgslCommands.TextureFillRectanglesFromList(map, runs);
                    paintMs = paint.Elapsed.TotalMilliseconds;
                    HeadlessHarness.Assert(painted == 5000, "Not every rectangle was painted: " + painted);
                }
                finally { PgslCommands.BindContext(previous); }

                viewport.OnRender += clear;
                viewport.OnPostFrame += overlay;
                using (Bitmap? warmUp = viewport.ReadbackFrameToBitmap(4)) { }
                measuring = true;
                using Bitmap? picture = viewport.ReadbackFrameToBitmap(24);
                measuring = false;
                HeadlessHarness.Assert(picture is not null && viewport.RenderFaultCount == 0, "No frame: " + viewport.LastRenderException);
                picture!.Save(Path.Combine(ctx.Captures, "script-textures-map.png"), System.Drawing.Imaging.ImageFormat.Png);
                // The first rectangle (colour 0, 0, 0 at 0, 0 is black, so the second: 37, 91, 53 at 6, 0).
                Color second = picture.GetPixel(8, 2);
                HeadlessHarness.Assert(Math.Abs(second.R - 37) <= 2 && Math.Abs(second.G - 91) <= 2 && Math.Abs(second.B - 53) <= 2,
                    "The painted map did not show its second rectangle: " + second);
                lines.Add($"5000 rectangles painted into a 600 x 300 texture once: {paintMs:F2} ms; then each frame one DrawTexture: "
                    + $"Draw GUI event {scriptMs / measured:F3} ms, overlay replay and submit {overlayMs / measured:F3} ms, "
                    + $"{drawCalls} GUI draw calls (DX11, mean of {measured} frames)");
            }
            finally
            {
                viewport.OnRender -= clear;
                viewport.OnPostFrame -= overlay;
                PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath;
                ScriptTextures.Reset();
            }
            File.WriteAllLines(Path.Combine(ctx.Captures, "script-textures-cost.txt"), lines);
            foreach (string line in lines) Console.WriteLine("ScriptTextures cost: " + line);
        });
    }

    private static (int R, int G, int B, int A) Pixel(double texture, int x, int y)
    {
        uint rgba = ScriptTextures.ReadPixel((int)texture, x, y);
        return ((int)(rgba >> 24), (int)((rgba >> 16) & 255), (int)((rgba >> 8) & 255), (int)(rgba & 255));
    }
}

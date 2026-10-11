using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// What a frame of script sprite draws allocates. A HUD that draws an image part per character
/// (GenesisCraft's) makes thousands of sprite draws a frame, and each used to leave an
/// ObjectDrawAssetEntry, its two dictionaries, a closure and a delegate behind. 2,000 sprites a frame
/// go through DrawSpritePart, DrawSprite, DrawSpriteScaled and DrawSpritePartsFromList into the
/// game's buffered GUI canvas (with 50 DrawText labels, then replayed onto the overlay), 2,000 into
/// a 2D Draw event's frame queue and 100 straight to the renderer (a GUI with no canvas); a steady
/// frame must allocate next to nothing on each path, and the GUI sprites still land where they were
/// drawn. Bytes per frame go to sprite-draw-alloc.txt.
/// </summary>
internal static class SpriteDrawAllocationSuite
{
    private const string Quads = "Alloc Quads";
    private const int Width = 640, Height = 360;
    private const int GuiParts = 1000, GuiSprites = 500, GuiScaled = 250, GuiListCalls = 25, PartsPerList = 10;
    private const int GuiDraws = GuiParts + GuiSprites + GuiScaled + (GuiListCalls * PartsPerList);
    private const int GuiTexts = 50, WorldDraws = 2000, DirectDraws = 100;
    private const int WarmFrames = 4, MeasuredFrames = 9;

    /// <summary>Bytes a steady frame may allocate on each path; such a frame used to allocate hundreds of KB.</summary>
    private const long FrameBound = 1024;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "SpriteDrawAllocation");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        ResourceService resources = ctx.Resources ?? throw new InvalidOperationException("No resource service.");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.SpriteDraws.SteadyFrameOfTwoThousandSpriteDrawsAllocatesAlmostNothing", () =>
        {
            string art = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, Quads);
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(art).Document, art, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.Save(session, ImageWorkspace.FromRgba(QuadrantPixels(), 16, 16));
            ResourceCatalog.Invalidate(project.RootPath);

            // An exported game's asset policy: a resolved frame is trusted from then on, so no
            // measured frame stops to look at the disk (Studio's fallback check does, once a second).
            int previousInterval = RuntimeAssetPolicy.FramePathIntervalMilliseconds;
            RuntimeAssetPolicy.DisablePolling();
            RuntimeAssetPolicy.Invalidate();

            Type bufferedType = typeof(Genesis.Runtime.GenesisRuntimeHost).GetNestedType("BufferedHudCanvas", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The game's buffered GUI canvas was not found.");
            IHudCanvas buffered = (IHudCanvas)Activator.CreateInstance(bufferedType, nonPublic: true)!;
            MethodInfo reset = bufferedType.GetMethod("Reset")!;
            MethodInfo replay = bufferedType.GetMethod("Replay")!;
            // Called through a delegate, as the game calls it: reflection's Invoke allocates its own.
            Action<IHudCanvas> replayInto = (Action<IHudCanvas>)Delegate.CreateDelegate(typeof(Action<IHudCanvas>), buffered, replay);
            FrameRenderQueue worldQueue = new();

            // One instance's context for the whole run, so its list of image parts lasts.
            PgslContext context = new() { RoomWidth = Width, RoomHeight = Height };
            PgslContext? previousContext = PgslCommands.BindContext(context);
            string previousProject = PgslCommands.ProjectPath;
            PgslCommands.ProjectPath = project.RootPath;
            double parts = PgslCommands.DsListCreate();
            for (int k = 0; k < PartsPerList; k++)
            {
                int q = k % 4;
                double u = (q % 2) * 0.5, v = (q / 2) * 0.5;
                foreach (double value in new[] { 0, u, v, u + 0.5, v + 0.5, k * 10, 0, 8, 8, 1 })
                    PgslCommands.DsListAdd(parts, value);
            }

            List<long> guiBytes = [], textBytes = [], replayBytes = [], worldBytes = [], directBytes = [];
            List<string> failures = [];
            int worldQueued = -1;
            bool measuring = false;
            Action<IRenderController> clear = current =>
            {
                current.Clear(0f, 0f, 0f); current.Set3DFrameActive(false);
                current.SetCamera2D(current.PixelWidth / 2f, current.PixelHeight / 2f, 1, 0);
            };
            // GenesisRuntimeHost.ComposeGameplayOverlay in short, with a 2D Draw event's queue and a
            // canvas-less GUI beside it; only the draw commands themselves are measured.
            Action<IRenderController> overlay = current =>
            {
                reset.Invoke(buffered, [current.PixelWidth, current.PixelHeight]);
                PgslRenderDrawSurface gui = new(current, buffered, current.PixelWidth, current.PixelHeight,
                    projectPath: project.RootPath, isGui: true);
                PgslRenderDrawSurface world = new(current, null, current.PixelWidth, current.PixelHeight,
                    worldQueue, project.RootPath);
                PgslRenderDrawSurface direct = new(current, null, current.PixelWidth, current.PixelHeight,
                    projectPath: project.RootPath, isGui: true);
                PgslContext? outer = PgslCommands.BindContext(context);
                string outerProject = PgslCommands.ProjectPath;
                PgslCommands.ProjectPath = project.RootPath;
                try
                {
                    context.DrawSurface = gui;
                    long started = GC.GetAllocatedBytesForCurrentThread();
                    DrawGuiSprites(parts);
                    long guiAllocated = GC.GetAllocatedBytesForCurrentThread() - started;

                    started = GC.GetAllocatedBytesForCurrentThread();
                    DrawGuiTexts();
                    long textAllocated = GC.GetAllocatedBytesForCurrentThread() - started;

                    context.DrawSurface = world;
                    started = GC.GetAllocatedBytesForCurrentThread();
                    DrawOffscreenSprites(WorldDraws);
                    long worldAllocated = GC.GetAllocatedBytesForCurrentThread() - started;

                    context.DrawSurface = direct;
                    started = GC.GetAllocatedBytesForCurrentThread();
                    DrawOffscreenSprites(DirectDraws);
                    long directAllocated = GC.GetAllocatedBytesForCurrentThread() - started;

                    if (measuring)
                    {
                        guiBytes.Add(guiAllocated);
                        textBytes.Add(textAllocated);
                        worldBytes.Add(worldAllocated);
                        directBytes.Add(directAllocated);
                        worldQueued = worldQueue.SpriteCount;
                    }
                }
                finally
                {
                    context.DrawSurface = null;
                    PgslCommands.BindContext(outer);
                    PgslCommands.ProjectPath = outerProject;
                    worldQueue.Reset();
                }
                current.FlushOverlaySprites();
                current.ComposeOverlay(canvas =>
                {
                    OverlayHudCanvas hud = new(canvas, current.PixelWidth, current.PixelHeight);
                    long started = GC.GetAllocatedBytesForCurrentThread();
                    replayInto(hud);
                    long replayed = GC.GetAllocatedBytesForCurrentThread() - started;
                    if (measuring) replayBytes.Add(replayed);
                });
            };

            using Form host = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            host.Controls.Add(viewport); GateSuite.ShowHost(host);
            try
            {
                viewport.BackendOverride = RenderBackendOption.SilkNetDx11; GateSuite.Pump(3, 15);
                viewport.OnRender += clear;
                viewport.OnPostFrame += overlay;
                using (Bitmap? warm = viewport.ReadbackFrameToBitmap(WarmFrames)) { }
                measuring = true;
                using Bitmap? frame = viewport.ReadbackFrameToBitmap(MeasuredFrames);
                measuring = false;
                if (frame is null || viewport.RenderFaultCount != 0 || frame.Width < Width || frame.Height < Height)
                    throw new InvalidOperationException("No frame: " + viewport.LastRenderException);
                frame.Save(Path.Combine(ctx.Captures, "sprite-draw-alloc-dx11.png"), System.Drawing.Imaging.ImageFormat.Png);
                ctx.Report.Images.Add(ImageResult.From("Runtime.SpriteDraws.DX11", "sprite-draw-alloc-dx11.png", VisualCapture.Measure(frame)));

                // The image parts land where they were drawn, each the quarter it was cut from.
                void Check(string what, int x, int y, int r, int g, int b)
                {
                    Color pixel = frame.GetPixel(x, y);
                    if (Math.Abs(pixel.R - r) > 6 || Math.Abs(pixel.G - g) > 6 || Math.Abs(pixel.B - b) > 6)
                        failures.Add($"{what} at {x},{y} is {pixel.R},{pixel.G},{pixel.B}, expected {r},{g},{b}");
                }
                Check("first part (red)", 4, 4, 255, 0, 0);
                Check("second part (green)", 12, 4, 0, 255, 0);
                Check("third part (blue)", 20, 4, 0, 0, 255);
                Check("fourth part (white)", 28, 4, 255, 255, 255);
                Check("part 41 (green)", 12, 12, 0, 255, 0);
                Check("last part (white)", 39 * 8 + 4, 24 * 8 + 4, 255, 255, 255);
                Check("first listed part (red)", 4, 276, 255, 0, 0);
                Check("second listed part (green)", 14, 276, 0, 255, 0);
                Check("a later list's part (blue)", 120 + 24, 272 + 16 + 4, 0, 0, 255);
            }
            finally
            {
                viewport.OnRender -= clear;
                viewport.OnPostFrame -= overlay;
                PgslCommands.BindContext(previousContext);
                PgslCommands.ProjectPath = previousProject;
                RuntimeAssetPolicy.SetFramePathInterval(previousInterval);
            }

            if (guiBytes.Count < MeasuredFrames / 2) failures.Add($"only {guiBytes.Count} frames were measured");
            if (worldQueued != WorldDraws) failures.Add($"the 2D Draw queue held {worldQueued} sprites, not {WorldDraws}");
            List<string> lines =
            [
                Describe($"GUI canvas, {GuiDraws} sprites", guiBytes, GuiDraws),
                Describe($"GUI canvas, {GuiTexts} texts", textBytes, GuiTexts),
                Describe($"GUI replayed onto the overlay, {GuiDraws} sprites and {GuiTexts} texts", replayBytes, GuiDraws + GuiTexts),
                Describe($"2D Draw queue, {WorldDraws} sprites", worldBytes, WorldDraws),
                Describe($"straight to the renderer, {DirectDraws} sprites", directBytes, DirectDraws),
            ];
            File.WriteAllLines(Path.Combine(ctx.Logs, "sprite-draw-alloc.txt"), lines);
            File.WriteAllLines(Path.Combine(ctx.Captures, "sprite-draw-alloc.txt"), lines);
            foreach (string line in lines) Console.WriteLine("SpriteDrawAlloc: " + line);
            foreach ((string path, List<long> bytes) in new[]
                     {
                         ("GUI canvas", guiBytes), ("GUI text", textBytes), ("overlay replay", replayBytes),
                         ("2D Draw queue", worldBytes), ("renderer", directBytes),
                     })
                if (bytes.Count > 0 && Median(bytes) > FrameBound)
                    failures.Add($"{path}: a steady frame allocated {Median(bytes)} bytes (bound {FrameBound})");
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures.Concat(lines)));
        });
    }

    /// <summary>The game's GUI: 1,000 image parts in a 40 x 25 grid of 8-pixel cells, 500 whole sprites, 250 scaled ones and 25 lists of 10 parts.</summary>
    private static void DrawGuiSprites(double parts)
    {
        for (int i = 0; i < GuiParts; i++)
        {
            int q = i % 4;
            double u = (q % 2) * 0.5, v = (q / 2) * 0.5;
            PgslCommands.DrawSpritePart(Quads, 0, u, v, u + 0.5, v + 0.5, (i % 40) * 8, (i / 40) * 8, 8, 8, 1);
        }
        for (int i = 0; i < GuiSprites; i++)
            PgslCommands.DrawSprite(Quads, 0, 344 + ((i % 25) * 11), 16 + ((i / 25) * 8));
        for (int i = 0; i < GuiScaled; i++)
            PgslCommands.DrawSpriteScaled(Quads, 8 + ((i % 50) * 12), 216 + ((i / 50) * 10), 0, 0.5, 0.5, 0, 1);
        for (int c = 0; c < GuiListCalls; c++)
            PgslCommands.DrawSpritePartsFromList(Quads, parts, (c % 5) * 120, 272 + ((c / 5) * 16));
    }

    private static readonly string[] Labels = ["Stone", "Dirt 64", "Health 20/20", "x 123 y 64 z -40", "Inventory"];

    /// <summary>A HUD's labels along the bottom: 10 places, each drawn 5 times.</summary>
    private static void DrawGuiTexts()
    {
        for (int i = 0; i < GuiTexts; i++)
            PgslCommands.DrawText(4 + ((i % 10) * 63), 346, Labels[i % Labels.Length]);
    }

    /// <summary>Sprites, scaled and turned sprites and image parts, all off the screen.</summary>
    private static void DrawOffscreenSprites(int count)
    {
        for (int i = 0; i < count; i++)
        {
            switch (i % 3)
            {
                case 0: PgslCommands.DrawSprite(Quads, 0, -200 - (i % 7), -200); break;
                case 1: PgslCommands.DrawSpriteScaled(Quads, -200, -200 - (i % 5), 0, 0.5, 0.5, 15, 1); break;
                default: PgslCommands.DrawSpritePart(Quads, 0, 0, 0, 0.5, 0.5, -200, -200, 8, 8, 1); break;
            }
        }
    }

    private static long Median(List<long> values)
    {
        List<long> sorted = [.. values];
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static string Describe(string what, List<long> bytes, int draws) => bytes.Count == 0
        ? what + ": not measured"
        : $"{what}: median {Median(bytes)} bytes a frame ({(double)Median(bytes) / draws:F1} a draw), "
          + $"least {bytes.Min()}, most {bytes.Max()}, over {bytes.Count} frames";

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
}

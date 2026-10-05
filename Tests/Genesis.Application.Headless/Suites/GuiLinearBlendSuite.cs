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
/// GUI draws blended in linear light (DrawSetBlendLinear and the project's Blend GUI in linear
/// light): a white panel at 8% over black reads about 80/255 instead of 20/255, a half-white panel
/// over mid grey the linear mix, a switch mid-frame changes only the draws after it, in order, and
/// the default blending is what it always was. Drawn through the GUI overlay on every renderer.
/// </summary>
internal static class GuiLinearBlendSuite
{
    private const string Grey = "Linear Grey";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "GuiLinearBlend");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        ResourceService resources = ctx.Resources ?? throw new InvalidOperationException("No resource service.");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GuiLinearBlend.CommandAndProjectSettingReachTheSurface", () =>
        {
            PgslRecordingDrawSurface surface = new();
            PgslContext context = new() { RoomWidth = 640, RoomHeight = 360, DrawSurface = surface };
            PgslContext? previous = PgslCommands.BindContext(context);
            try
            {
                HeadlessHarness.Assert(!PgslCommands.DrawGetBlendLinear(), "A surface starts blending in linear light.");
                PgslCommands.DrawSetBlendLinear(true);
                HeadlessHarness.Assert(surface.BlendLinear && PgslCommands.DrawGetBlendLinear(),
                    "DrawSetBlendLinear(true) did not reach the surface or DrawGetBlendLinear does not report it.");
                PgslCommands.DrawSetBlendLinear(false);
                HeadlessHarness.Assert(!surface.BlendLinear && !PgslCommands.DrawGetBlendLinear(), "DrawSetBlendLinear(false) did not switch it off.");
            }
            finally { PgslCommands.BindContext(previous); }

            // The setting is saved in the manifest's rendering section, off unless set.
            HeadlessHarness.Assert(!project.Manifest.Rendering.BlendGuiInLinearLight
                && !ProjectPaths.ReadBlendGuiInLinearLight(project.RootPath), "A new project blends its GUI in linear light.");
            project.Manifest.Rendering.BlendGuiInLinearLight = true;
            new ProjectService().Save(project);
            try
            {
                HeadlessHarness.Assert(ProjectPaths.ReadBlendGuiInLinearLight(project.RootPath),
                    "The runtime does not read Blend GUI in linear light from the saved project.");
                ProjectSession reopened = new ProjectService().OpenProject(project.RootPath);
                HeadlessHarness.Assert(reopened.Manifest.Rendering.BlendGuiInLinearLight, "Blend GUI in linear light did not survive save and reopen.");
            }
            finally
            {
                project.Manifest.Rendering.BlendGuiInLinearLight = false;
                new ProjectService().Save(project);
            }
            HeadlessHarness.Assert(!ProjectPaths.ReadBlendGuiInLinearLight(project.RootPath), "Turning the setting off was not saved.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GuiLinearBlend.OverlayBlendsInLinearLightOnEveryBackend", () =>
        {
            string art = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, Grey);
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(art).Document, art, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.Save(session, ImageWorkspace.CreateBlank(16, 16, Color.FromArgb(255, 128, 128, 128)));
            ResourceCatalog.Invalidate(project.RootPath);

            using Form game = GateSuite.NewHost(640, 360);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            bool linearScene = true;
            viewport.OnRender += renderer =>
            {
                renderer.Clear(0f, 0f, 0f); renderer.Set3DFrameActive(false);
                renderer.SetCamera2D(renderer.PixelWidth / 2f, renderer.PixelHeight / 2f, 1, 0);
            };
            // As the game does: the GUI goes to the overlay after the frame's own drawing.
            viewport.OnPostFrame += renderer => renderer.ComposeOverlay(canvas =>
            {
                OverlayHudCanvas hud = new(canvas, renderer.PixelWidth, renderer.PixelHeight);
                PgslRenderDrawSurface surface = new(renderer, hud, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, isGui: true);
                PgslContext context = new() { RoomWidth = renderer.PixelWidth, RoomHeight = renderer.PixelHeight, DrawSurface = surface };
                PgslContext? previous = PgslCommands.BindContext(context);
                string previousProject = PgslCommands.ProjectPath;
                PgslCommands.ProjectPath = project.RootPath;
                try { DrawScene(linearScene); }
                finally
                {
                    surface.SetBlendLinear(false);
                    PgslCommands.BindContext(previous); PgslCommands.ProjectPath = previousProject;
                }
            });
            game.Controls.Add(viewport); GateSuite.ShowHost(game);

            List<string> failures = [];
            List<string> readings = [];
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                linearScene = true;
                using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                linearScene = false;
                using Bitmap? plain = viewport.ReadbackFrameToBitmap(3);
                if (frame is null || plain is null || viewport.RenderFaultCount != 0 || frame.Width < 640 || frame.Height < 360)
                { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                string name = backend.ShortName.ToLowerInvariant();
                frame.Save(Path.Combine(ctx.Captures, "gui-linear-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                plain.Save(Path.Combine(ctx.Captures, "gui-linear-plain-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                ctx.Report.Images.Add(ImageResult.From("Runtime.GuiLinearBlend." + backend.ShortName, "gui-linear-" + name + ".png", VisualCapture.Measure(frame)));

                void Check(Bitmap image, string what, int x, int y, int low, int high)
                {
                    Color pixel = image.GetPixel(x, y);
                    readings.Add(backend.ShortName + " " + what + "=" + pixel.R);
                    if (pixel.R < low || pixel.R > high || Math.Abs(pixel.R - pixel.G) > 1 || Math.Abs(pixel.R - pixel.B) > 1)
                        failures.Add(backend.ShortName + ": " + what + " is " + pixel + ", expected " + low + "-" + high);
                }

                // Over black, white at 8% (alpha 20/255): 20 stored, about 79 in linear light.
                Check(frame, "white8%Default", 50, 50, 19, 21);
                Check(frame, "white8%Linear", 150, 50, 76, 82);
                // Over grey 128 (0.216 linear), white at 50% (alpha 127/255): 191 stored, 204 in linear light.
                Check(frame, "grey128", 250, 95, 127, 129);
                Check(frame, "white50%OverGreyDefault", 250, 50, 189, 193);
                Check(frame, "white50%OverGreyLinear", 350, 50, 201, 207);
                // A switch mid-frame changes only the draws after it: default, linear, default.
                Check(frame, "switchBefore", 50, 170, 19, 21);
                Check(frame, "switchOn", 150, 170, 76, 82);
                Check(frame, "switchAfter", 250, 170, 19, 21);
                // Call order across passes: a linear half-white, then a default half-black over its right half.
                Check(frame, "linearHalfWhite", 330, 170, 185, 190);
                Check(frame, "thenDefaultHalfBlack", 375, 170, 91, 96);
                // An image is decoded as well: grey 128 at 50% over black is 64 stored, 92 in linear light.
                Check(frame, "imageDefault", 470, 50, 62, 66);
                Check(frame, "imageLinear", 570, 50, 89, 95);
                // Text in linear light still draws.
                Rectangle ink = Ink(frame, new Rectangle(440, 140, 200, 60));
                if (ink.Width < 10) failures.Add(backend.ShortName + ": linear-light text did not draw " + ink);

                // With linear light never asked for, the same scene blends as stored everywhere.
                Check(plain, "plainWhite8%", 150, 50, 19, 21);
                Check(plain, "plainWhite50%OverGrey", 350, 50, 189, 193);
                Check(plain, "plainSwitchOn", 150, 170, 19, 21);
                Check(plain, "plainImage", 570, 50, 62, 66);
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "gui-linear-readings.txt"), readings);
            Console.WriteLine("GuiLinearBlend readings: " + string.Join(", ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.GuiLinearBlend.DrawGuiEventStartsFromTheProjectSettingThroughTheGameOverlay", () =>
        {
            // A real Object's Draw GUI event, run by the script host as the game runs it: into the
            // host's buffered GUI canvas, replayed onto the overlay in order.
            string objectPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject), ResourceKind.GameObject, "Linear Gui");
            File.WriteAllText(objectPath, """
                { "name": "Linear Gui", "components": [
                  { "id": "cmp-script", "type": "ScriptComponent", "enabled": true, "props": { "ScriptClass": "Linear Gui" } } ] }
                """);
            ObjectEventStore.Save(objectPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Create"] = "startLinear = -1;\n",
                ["DrawGui"] = """
                    if (DrawGetBlendLinear()) { startLinear = 1; } else { startLinear = 0; }
                    DrawSetColorRgb(255, 255, 255);
                    DrawSetAlpha(0.08);
                    DrawRectangle(10, 10, 90, 90);
                    DrawSetBlendLinear(false);
                    DrawRectangle(110, 10, 190, 90);
                    DrawSetBlendLinear(true);
                    DrawRectangle(210, 10, 290, 90);
                    """,
            });
            ResourceCatalog.Invalidate(project.RootPath);

            Type bufferedType = typeof(Genesis.Runtime.GenesisRuntimeHost).GetNestedType("BufferedHudCanvas", System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The game's buffered GUI canvas was not found.");
            IHudCanvas buffered = (IHudCanvas)Activator.CreateInstance(bufferedType, nonPublic: true)!;

            using Form gameHost = GateSuite.NewHost(640, 360);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            gameHost.Controls.Add(viewport); GateSuite.ShowHost(gameHost);
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            List<string> failures = [];
            List<string> readings = [];
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using (Bitmap? warm = viewport.ReadbackFrameToBitmap(3)) { }
                var renderer = viewport.Renderer ?? throw new InvalidOperationException("No renderer.");
                RoomAsset room = RoomAsset.Create("Linear GUI Room", RoomDimension.TwoD);
                room.Nodes.Add(new RoomNode
                {
                    Kind = RoomNodeKind.GameObject, Name = "Linear GUI", Transform = new() { X = 20, Y = 20 },
                    GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) },
                });
                using RuntimeScene scene = new("Linear GUI") { Input = new Genesis.Runtime.Input.InputState() };
                ProjectGameContext game = new(project.RootPath, scene, renderer, null, room, null);
                ScriptHostSystem scripts = new(); scripts.SetContext(game);
                IGameContext? oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
                PgslContext? oldContext = PgslCommands.BindContext(new PgslContext());
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
                bool projectLinear = true;
                Action<Genesis.Shared.Interfaces.IRenderController> clear = current =>
                {
                    current.Clear(0f, 0f, 0f); current.Set3DFrameActive(false);
                    current.SetCamera2D(current.PixelWidth / 2f, current.PixelHeight / 2f, 1, 0);
                };
                // GenesisRuntimeHost.ComposeGameplayOverlay, in short.
                Action<Genesis.Shared.Interfaces.IRenderController> overlay = current =>
                {
                    scripts.BlendGuiInLinearLight = projectLinear;
                    bufferedType.GetMethod("Reset")!.Invoke(buffered, [current.PixelWidth, current.PixelHeight]);
                    scripts.DispatchPgslGuiDraw(current, buffered);
                    current.FlushOverlaySprites();
                    current.ComposeOverlay(canvas => bufferedType.GetMethod("Replay")!.Invoke(buffered,
                        [new OverlayHudCanvas(canvas, current.PixelWidth, current.PixelHeight)]));
                };
                try
                {
                    new RoomSceneBuilder(project.RootPath, scripts).Build(scene, room);
                    scripts.Update(1f / 60);
                    PgslBehavior behavior = scripts.Instances.OfType<PgslBehavior>().Single();
                    HeadlessHarness.Assert(behavior.HasGuiDrawScript && scripts.RecentDiagnostics.Count == 0,
                        "The Draw GUI event did not compile: " + string.Join(';', scripts.RecentDiagnostics));
                    viewport.OnRender += clear;
                    viewport.OnPostFrame += overlay;
                    using Bitmap? linear = viewport.ReadbackFrameToBitmap(2);
                    object? startedLinear = behavior.GetVariablesSnapshot().GetValueOrDefault("startLinear");
                    projectLinear = false;
                    using Bitmap? stored = viewport.ReadbackFrameToBitmap(2);
                    object? startedStored = behavior.GetVariablesSnapshot().GetValueOrDefault("startLinear");
                    if (linear is null || stored is null || viewport.RenderFaultCount != 0)
                    { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                    if (backend.Backend == RenderBackendOption.SilkNetDx11)
                        linear.Save(Path.Combine(ctx.Captures, "gui-linear-event-dx11.png"), System.Drawing.Imaging.ImageFormat.Png);

                    void Check(Bitmap image, string what, int x, int low, int high)
                    {
                        int value = image.GetPixel(x, 50).R;
                        readings.Add(backend.ShortName + " " + what + "=" + value);
                        if (value < low || value > high) failures.Add(backend.ShortName + ": " + what + " is " + value + ", expected " + low + "-" + high);
                    }
                    // Project setting on: the event starts linear, switches off, then on again.
                    Check(linear, "settingOnFirst", 50, 76, 82);
                    Check(linear, "settingOnAfterOff", 150, 19, 21);
                    Check(linear, "settingOnAfterOn", 250, 76, 82);
                    // Setting off: the event starts as stored; its own DrawSetBlendLinear(true) still counts.
                    Check(stored, "settingOffFirst", 50, 19, 21);
                    Check(stored, "settingOffAfterOff", 150, 19, 21);
                    Check(stored, "settingOffAfterOn", 250, 76, 82);
                    if (Convert.ToDouble(startedLinear) != 1 || Convert.ToDouble(startedStored) != 0)
                        failures.Add(backend.ShortName + ": DrawGetBlendLinear at the start of the event read " + startedLinear + " then " + startedStored);
                }
                finally
                {
                    viewport.OnRender -= clear;
                    viewport.OnPostFrame -= overlay;
                    PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath;
                }
            }
            File.AppendAllLines(Path.Combine(ctx.Logs, "gui-linear-readings.txt"), readings);
            Console.WriteLine("GuiLinearBlend event readings: " + string.Join(", ", readings));
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });
    }

    /// <summary>The scene; with <paramref name="linear"/> false every DrawSetBlendLinear(true) is left out.</summary>
    private static void DrawScene(bool linear)
    {
        void Blend(bool on) { if (!on || linear) PgslCommands.DrawSetBlendLinear(on); }
        void Rect(double x, double y, double w, double h, Color color, double alpha)
        {
            PgslCommands.DrawSetColor(color);
            PgslCommands.DrawSetAlpha(alpha);
            PgslCommands.DrawRectangle(x, y, x + w, y + h);
        }

        // Row 1: 8% white over black, default then linear; grey backdrop with half white, default then linear.
        Rect(10, 10, 80, 80, Color.White, 0.08);
        Blend(true);
        Rect(110, 10, 80, 80, Color.White, 0.08);
        Blend(false);
        Rect(200, 0, 200, 100, Color.FromArgb(128, 128, 128), 1);
        Rect(210, 10, 80, 80, Color.White, 0.5);
        Blend(true);
        Rect(310, 10, 80, 80, Color.White, 0.5);
        Blend(false);

        // Images: grey 128 at half alpha over black, default then linear.
        PgslCommands.DrawSpritePart(Grey, 0, 0, 0, 1, 1, 430, 10, 80, 80, 0.5);
        Blend(true);
        PgslCommands.DrawSpritePart(Grey, 0, 0, 0, 1, 1, 530, 10, 80, 80, 0.5);
        Blend(false);

        // Row 2: the switch mid-frame, then the order of a linear and a default draw that overlap.
        Rect(10, 130, 80, 80, Color.White, 0.08);
        Blend(true);
        Rect(110, 130, 80, 80, Color.White, 0.08);
        Blend(false);
        Rect(210, 130, 80, 80, Color.White, 0.08);
        Blend(true);
        Rect(310, 130, 80, 80, Color.White, 0.5);
        Blend(false);
        Rect(350, 130, 40, 80, Color.Black, 0.5);

        // Text in linear light.
        Blend(true);
        PgslCommands.DrawSetColor(Color.White);
        PgslCommands.DrawSetAlpha(1);
        PgslCommands.DrawTextScaled(450, 150, "LINEAR", 28);
        Blend(false);
    }

    private static Rectangle Ink(Bitmap frame, Rectangle area)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = area.Top; y < Math.Min(area.Bottom, frame.Height); y++)
            for (int x = area.Left; x < Math.Min(area.Right, frame.Width); x++)
            {
                if (frame.GetPixel(x, y).R < 200) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}

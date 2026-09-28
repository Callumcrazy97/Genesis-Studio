using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void Ui(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, "Gate HUD");
        string sprite = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, "HUD Icon");
        ImageDocumentSession imageSession = new(ImageDocumentSerializer.LoadAtomic(sprite).Document, sprite, ImageDocumentAccess.Editor);
        ImageWorkspace pixels = ImageWorkspace.CreateBlank(32, 32, Color.FromArgb(255, 20, 220, 40));
        ImageWorkspaceStorage.Save(imageSession, pixels);
        using Form host = GateSuite.NewHost(1280, 780);
        using UiEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        string panel = string.Empty, image = string.Empty, progress = string.Empty, label = string.Empty;
        HeadlessHarness.Step("create UI elements with undo, redo and live property editing", () =>
        {
            int original = editor.Document.Elements.Count;
            editor.AddElement(UiElementType.Panel);
            panel = editor.Document.Elements.Last().Id;
            editor.Undo();
            HeadlessHarness.Assert(editor.Document.Elements.Count == original, "Adding an element cannot be undone.");
            editor.Redo();
            HeadlessHarness.Assert(editor.Document.Elements.Count == original + 1, "Redo lost the UI element.");
            editor.SelectElement(panel);
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Ui.Id", "RootPanel"), "The live Inspector cannot name a UI element.");
            panel = "RootPanel";
            editor.TryApplyInspectorValue("Ui.X", 0);
            editor.TryApplyInspectorValue("Ui.Y", 0);
            editor.TryApplyInspectorValue("Ui.Width", 1280);
            editor.TryApplyInspectorValue("Ui.Height", 720);
            editor.AddElement(UiElementType.Image);
            image = editor.Document.Elements.Last().Id;
            editor.TryApplyInspectorValue("Ui.Image", "HUD Icon");
            editor.TryApplyInspectorValue("Ui.X", 40);
            editor.TryApplyInspectorValue("Ui.Y", 40);
            editor.TryApplyInspectorValue("Ui.Width", 160);
            editor.TryApplyInspectorValue("Ui.Height", 80);
            editor.AddElement(UiElementType.ProgressBar);
            progress = editor.Document.Elements.Last().Id;
            editor.TryApplyInspectorValue("Ui.X", 250);
            editor.TryApplyInspectorValue("Ui.Y", 40);
            editor.TryApplyInspectorValue("Ui.Value", 25);
            editor.TryApplyInspectorValue("Ui.Maximum", 100);
            editor.AddElement(UiElementType.Text);
            label = editor.Document.Elements.Last().Id;
            editor.TryApplyInspectorValue("Ui.Text", "Authored text");
            editor.TryApplyInspectorValue("Ui.X", 600);
            editor.TryApplyInspectorValue("Ui.Y", 140);
            string originalFont = editor.Document.Elements.Last().Font;
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Ui.Font", "Consolas"), "Font family is not editable through the live Inspector.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Document.Elements.Single(item => item.Id == label).Font == originalFont, "Undo did not restore the authored font.");
            editor.Redo();
            editor.Save();
            UiAssetDocument saved = UiAssetDocument.Load(path);
            HeadlessHarness.Assert(saved.Elements.Single(item => item.Id == image).Image == "HUD Icon"
                && saved.Elements.Single(item => item.Id == progress).Value == 25
                && saved.Elements.Single(item => item.Id == label).Font == "Consolas",
                "UI save did not retain the typed image reference, font family and progress value.");
            using UiEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(reopened.SelectElement(image)
                && reopened.GetLiveInspectorValues().Any(value => value.PropertyPath == "Ui.Image" && value.AssetKind == ResourceKind.Image),
                "The reopened UI cannot expose its selected element and typed image binding.");
        });
        HeadlessHarness.Step("each UI element exposes only properties that affect its appearance", () =>
        {
            editor.SelectElement(panel);
            string[] properties = editor.GetLiveInspectorValues().Select(value => value.PropertyPath).ToArray();
            HeadlessHarness.Assert(properties.Contains("Ui.Background") && !properties.Contains("Ui.Text") && !properties.Contains("Ui.Image")
                && !properties.Contains("Ui.Value") && !editor.TryApplyInspectorValue("Ui.Value", 42),
                "A panel still offers irrelevant text, image or progress inputs.");
            editor.SelectElement(image);
            HeadlessHarness.Assert(editor.GetLiveInspectorValues().Any(value => value.PropertyPath == "Ui.Opacity")
                && !editor.TryApplyInspectorValue("Ui.Font", "Consolas"), "An image offers text properties or lacks opacity.");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Ui.Opacity", .5f), "Image opacity cannot be authored.");
            editor.Save();
            UiElement savedImage = UiAssetDocument.Load(path).Elements.Single(item => item.Id == image);
            HeadlessHarness.Assert(Convert.ToUInt32(savedImage.Foreground.TrimStart('#'), 16) >> 24 == 128,
                "Image opacity was lost when saving the resource.");
            editor.TryApplyInspectorValue("Ui.Opacity", 1);
            editor.SelectElement(progress);
            HeadlessHarness.Assert(editor.GetLiveInspectorValues().Any(value => value.PropertyPath == "Ui.Foreground" && value.Label == "Border colour")
                && editor.TryApplyInspectorValue("Ui.Accent", "#FF22CC88") && !editor.TryApplyInspectorValue("Ui.Accent", "bad colour"),
                "Progress colours are missing or invalid colour values were accepted.");
            editor.SelectElement(panel);
            editor.TryApplyInspectorValue("Ui.Anchor", "Stretch");
            editor.TryApplyInspectorValue("Ui.Width", 0); editor.TryApplyInspectorValue("Ui.Height", 0);
            editor.Save();
            using (UiEditorControl reopened = new(path, project.RootPath))
                HeadlessHarness.Assert(reopened.Document.Elements.Single(item => item.Id == panel) is { Anchor: UiAnchor.Stretch, Width: 0, Height: 0 },
                    "A full-canvas stretch with zero right/bottom insets did not survive save/reopen.");
            editor.Undo(); editor.Undo(); editor.Undo();
            editor.Save();
        });
        HeadlessHarness.Step("zoom anchors to the pointer, pan preserves the document, and Fit restores the complete canvas", () =>
        {
            Control canvas = SurfaceControls(editor).Single(control => control.GetType().Name == "UiDesignCanvas");
            MethodInfo bounds = canvas.GetType().GetMethod("CanvasRect", BindingFlags.Instance | BindingFlags.NonPublic)!;
            RectangleF Rect() => (RectangleF)bounds.Invoke(canvas, null)!;
            RectangleF original = Rect();
            Point pointer = new((int)(original.X + original.Width * .6f), (int)(original.Y + original.Height * .7f));
            float u = (pointer.X - original.X) / original.Width, v = (pointer.Y - original.Y) / original.Height;
            void Mouse(string method, Point point, MouseButtons buttons, int delta = 0) => canvas.GetType()
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [new MouseEventArgs(buttons, 1, point.X, point.Y, delta)]);
            Mouse("OnMouseWheel", pointer, MouseButtons.None, 480);
            RectangleF zoomed = Rect();
            HeadlessHarness.Assert(zoomed.Width > original.Width * 2 && Math.Abs(zoomed.X + u * zoomed.Width - pointer.X) < .01f
                && Math.Abs(zoomed.Y + v * zoomed.Height - pointer.Y) < .01f, "Zoom moved the authored point away from the pointer.");
            Mouse("OnMouseDown", pointer, MouseButtons.Middle);
            Mouse("OnMouseMove", new Point(pointer.X + 40, pointer.Y - 20), MouseButtons.Middle);
            Mouse("OnMouseUp", new Point(pointer.X + 40, pointer.Y - 20), MouseButtons.Middle);
            RectangleF panned = Rect();
            HeadlessHarness.Assert(Math.Abs(panned.X - zoomed.X - 40) < .01f && Math.Abs(panned.Y - zoomed.Y + 20) < .01f && !editor.IsDirty,
                "Panning changed the document or failed to move the canvas.");
            editor.SelectElement(panel);
            UiElement selected = editor.Document.Elements.Single(item => item.Id == panel);
            float beforeX = selected.X;
            Point start = new((int)(panned.X + panned.Width * .7f), (int)(panned.Y + panned.Height * .8f));
            Mouse("OnMouseDown", start, MouseButtons.Left);
            Mouse("OnMouseMove", new Point(start.X + 30, start.Y), MouseButtons.Left);
            Mouse("OnMouseUp", new Point(start.X + 30, start.Y), MouseButtons.Left);
            HeadlessHarness.Assert(Math.Abs(selected.X - beforeX - 30 * editor.Document.DesignWidth / panned.Width) < .01f,
                "Dragging at a zoomed scale changed the wrong design distance.");
            editor.Undo(); editor.Save();
            SurfaceControls(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>()).Single(button => button.Text == "Fit").PerformClick();
            HeadlessHarness.Assert(Rect() == original, "Fit did not reset the view's zoom and pan.");
        });
        HeadlessHarness.Step("enlarged element properties stay readable and sidebar choices survive resizing", () =>
        {
            try
            {
                ThemeService.ApplySettings(new GenesisSettings { Appearance = { InterfaceScale = 2 } });
                Genesis.Application.Studio.Docking.SuiteChromeBridge.Push(); ThemeService.Apply(host); GateSuite.Pump(4, 20);
                Control details = (Control)typeof(UiEditorControl).GetField("_detailsDock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                ToolStripComboBox resolution = SurfaceControls(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripComboBox>()).Single();
                HeadlessHarness.Assert(resolution.ComboBox.Font.Size >= Genesis.Application.Editors.Suite.EditorChrome.BaseFont.Size
                    && resolution.Owner!.Height >= resolution.ComboBox.PreferredHeight,
                    "The canvas selector stays small or clips its text at enlarged interface scale.");
                ToolStripMenuItem toggle = editor.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(menu => menu.Text == "Options")
                    .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Inspector");
                if (!details.Visible) toggle.PerformClick();
                editor.SelectElement(image); GateSuite.Pump(4, 20);
                NumericUpDown x = (NumericUpDown)typeof(UiEditorControl).GetField("_x", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                NumericUpDown y = (NumericUpDown)typeof(UiEditorControl).GetField("_y", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                HeadlessHarness.Assert(x.Width > 160 && x.Height >= x.Font.Height && x.PointToScreen(new Point(x.Width, 0)).X + 6 <= y.PointToScreen(Point.Empty).X,
                    "Enlarged coordinate inputs are tiny or touch each other: " + x.Bounds + "; " + y.Bounds
                    + "; font=" + x.Font.Height + "; gap=" + (y.PointToScreen(Point.Empty).X - x.PointToScreen(new Point(x.Width, 0)).X));
                Button pick = SurfaceControls(details).OfType<Button>().Single(button => button.Text == "Pick…");
                HeadlessHarness.Assert(pick.Height >= pick.Font.Height + 6 && pick.Parent!.ClientRectangle.Contains(pick.Bounds),
                    "The scaled image picker is clipped by its row: " + pick.Bounds + "; font=" + pick.Font.Height + "; row=" + pick.Parent?.ClientRectangle);
                host.ClientSize = new Size(1380, 800); GateSuite.Pump(3, 20);
                HeadlessHarness.Assert(details.Visible, "Resizing discarded the Inspector toggle.");
                string capture = "ui-image-inspector-scale200.png";
                ctx.Report.Images.Add(ImageResult.From("Editor.UI.ImageProperties", capture,
                    VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
                toggle.PerformClick(); host.ClientSize = new Size(1480, 900); GateSuite.Pump(3, 20);
                HeadlessHarness.Assert(!details.Visible, "Resizing reopened an explicitly hidden Inspector.");
            }
            finally { ThemeService.ApplySettings(new GenesisSettings()); Genesis.Application.Studio.Docking.SuiteChromeBridge.Push(); ThemeService.Apply(host); }
        });
        HeadlessHarness.Step("real image pixels appear in the canvas and a drag is one undoable edit", () =>
        {
            Control canvas = SurfaceControls(editor).Single(control => control.GetType().Name == "UiDesignCanvas");
            using Bitmap bitmap = new(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
            int green = 0;
            for (int y = 0; y < bitmap.Height; y += 3)
                for (int x = 0; x < bitmap.Width; x += 3)
                { Color colour = bitmap.GetPixel(x, y); if (colour.G > 170 && colour.R < 70 && colour.B < 90) green++; }
            HeadlessHarness.Assert(green > 20, "The canvas still shows an image placeholder instead of selected sprite pixels.");
            ImageWorkspaceStorage.Save(imageSession, ImageWorkspace.CreateBlank(32, 32, Color.FromArgb(255, 20, 40, 220)));
            editor.HandleAssetChanges(new ProjectAssetChangeSet(project.RootPath, [sprite], [path], generation: 1));
            using Bitmap updated = new(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(updated, canvas.ClientRectangle);
            int blue = 0;
            for (int y = 0; y < updated.Height; y += 3)
                for (int x = 0; x < updated.Width; x += 3)
                { Color colour = updated.GetPixel(x, y); if (colour.B > 170 && colour.R < 70 && colour.G < 90) blue++; }
            HeadlessHarness.Assert(blue > 20 && editor.AssetRefreshGeneration == 1,
                "Saving artwork in the Image Editor did not update the retained UI preview.");
            editor.SelectElement(panel);
            UiElement selected = editor.Document.Elements.Single(item => item.Id == panel);
            float originalX = selected.X;
            MethodInfo canvasRect = canvas.GetType().GetMethod("CanvasRect", BindingFlags.Instance | BindingFlags.NonPublic)!;
            RectangleF rect = (RectangleF)canvasRect.Invoke(canvas, null)!;
            Point start = new((int)(rect.X + rect.Width * .75f), (int)(rect.Y + rect.Height * .75f));
            void Mouse(string method, Point point, MouseButtons buttons) => canvas.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(canvas, [new MouseEventArgs(buttons, 1, point.X, point.Y, 0)]);
            Mouse("OnMouseDown", start, MouseButtons.Left);
            Mouse("OnMouseMove", new Point(start.X + 15, start.Y), MouseButtons.Left);
            Mouse("OnMouseMove", new Point(start.X + 30, start.Y), MouseButtons.Left);
            Mouse("OnMouseUp", new Point(start.X + 30, start.Y), MouseButtons.Left);
            HeadlessHarness.Assert(selected.X > originalX, "Dragging did not move the selected panel.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Document.Elements.Single(item => item.Id == panel).X == originalX,
                "Undo restored only the final mouse move instead of the complete gesture.");
            editor.Save();
        });
        HeadlessHarness.Step("gameplay UI drawing, hit testing and visibility consume the saved layout", () =>
        {
            PgslRecordingDrawSurface surface = new();
            PgslContext context = new() { RoomWidth = 1280, RoomHeight = 720, DrawSurface = surface };
            PgslContext? previousContext = PgslCommands.BindContext(context);
            string previousPath = PgslCommands.ProjectPath;
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                PgslCommands.DrawUi("Gate HUD");
                HeadlessHarness.Assert(surface.Texts.Any(text => text.Text == "Authored text" && text.Font == "Consolas"),
                    "Gameplay UI drawing discarded its saved font family.");
                var icon = surface.Sprites.Single(call => call.Sprite == "HUD Icon");
                HeadlessHarness.Assert(icon.Destination == new RectangleF(40, 40, 160, 80),
                    "Gameplay drawing ignored the UI image's authored rectangle.");
                HeadlessHarness.Assert(PgslCommands.UiHitTest("Gate HUD", 100, 70) == image, "Runtime hit testing disagrees with UI image placement.");
                PgslCommands.UiSetVisible("Gate HUD", image, false);
                surface.Reset();
                PgslCommands.DrawUi("Gate HUD");
                HeadlessHarness.Assert(surface.Sprites.Count == 0 && PgslCommands.UiHitTest("Gate HUD", 100, 70) == panel,
                    "Instance visibility overrides did not affect both drawing and interaction.");
                PgslCommands.UiSetValue("Gate HUD", progress, 50);
                surface.Reset();
                PgslCommands.DrawUi("Gate HUD");
                HeadlessHarness.Assert(surface.Rectangles.Any(call => call.Filled && call.X == 250 && call.W == 160),
                    "The runtime progress value override did not update its visible fill.");
            }
            finally { PgslCommands.BindContext(previousContext); PgslCommands.ProjectPath = previousPath; }
        });
        HeadlessHarness.Step("the concrete runtime renderer submits UI image bounds and real texture pixels", () =>
        {
            using EditorInteractionRenderProbe renderer = new();
            PgslRenderDrawSurface draw = new(renderer, null, 1280, 720, projectPath: project.RootPath, isGui: true);
            draw.DrawSpriteRectangle("HUD Icon", new RectangleF(40, 40, 160, 80), 0, Color.White, 1);
            var call = renderer.Sprites.Single();
            HeadlessHarness.Assert(call.X == 40 && call.Y == 40 && call.Width == 160 && call.Height == 80
                && call.OriginX == 0 && call.OriginY == 0 && call.Depth == -10000,
                "The real UI draw path reverted to the gameplay sprite's size, origin or layer.");
            byte[] texture = renderer.Textures[call.Texture.Id];
            HeadlessHarness.Assert(texture[2] > 170 && texture[0] < 70 && texture[1] < 90,
                "The renderer submitted stale or missing image pixels after cross-editor Save.");
        });
        HeadlessHarness.Step("saved UI artwork, progress colours and centred button text render on every exact backend", () =>
        {
            editor.AddElement(UiElementType.Button);
            editor.TryApplyInspectorValue("Ui.Id", "PlayButton");
            editor.TryApplyInspectorValue("Ui.Text", "Play"); editor.TryApplyInspectorValue("Ui.Font", "Consolas");
            editor.TryApplyInspectorValue("Ui.FontSize", 24);
            editor.TryApplyInspectorValue("Ui.X", 900); editor.TryApplyInspectorValue("Ui.Y", 560);
            editor.TryApplyInspectorValue("Ui.Width", 220); editor.TryApplyInspectorValue("Ui.Height", 80);
            editor.Save();
            using Form game = GateSuite.NewHost(640, 360);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            viewport.OnRender += renderer =>
            {
                renderer.Clear(.03f, .04f, .05f); renderer.Set3DFrameActive(false);
                renderer.SetCamera2D(renderer.PixelWidth / 2f, renderer.PixelHeight / 2f, 1, 0);
                PgslContext gameContext = new()
                {
                    RoomWidth = renderer.PixelWidth, RoomHeight = renderer.PixelHeight,
                    DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight, projectPath: project.RootPath, isGui: true),
                };
                PgslContext? previous = PgslCommands.BindContext(gameContext);
                string previousProject = PgslCommands.ProjectPath;
                PgslCommands.ProjectPath = project.RootPath;
                try { PgslCommands.DrawUi("Gate HUD"); }
                finally { PgslCommands.BindContext(previous); PgslCommands.ProjectPath = previousProject; }
            };
            game.Controls.Add(viewport); GateSuite.ShowHost(game);
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                string expected = backend.Backend switch { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
                HeadlessHarness.Assert(frame is not null && viewport.Renderer?.BackendName == expected && viewport.RenderFaultCount == 0,
                    "The UI renderer fell back or faulted: " + backend.ShortName + "; " + viewport.LastRenderException);
                Color imagePixel = frame!.GetPixel(40, 35), progressPixel = frame.GetPixel(135, 25);
                HeadlessHarness.Assert(imagePixel.B > 180 && imagePixel.R < 70 && imagePixel.G < 90
                    && progressPixel.G > 170 && progressPixel.R < 80 && progressPixel.B < 170,
                    "The runtime lost the saved Image pixels or progress accent on " + backend.ShortName + ": " + imagePixel + "; " + progressPixel);
                int minX = 640, maxX = -1, minY = 360, maxY = -1;
                for (int y = 282; y < 318; y++)
                for (int x = 452; x < 558; x++)
                {
                    Color pixel = frame.GetPixel(x, y);
                    if (pixel.R < 210 || pixel.G < 210 || pixel.B < 210) continue;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                HeadlessHarness.Assert(maxX > minX && Math.Abs((minX + maxX) / 2f - 505) < 3 && Math.Abs((minY + maxY) / 2f - 300) < 3,
                    "Button text is missing or is not centred in its saved bounds on " + backend.ShortName + ": " + minX + "," + maxX + "; " + minY + "," + maxY);
                string capture = "ui-gameplay-" + backend.ShortName.ToLowerInvariant() + ".png";
                frame.Save(Path.Combine(ctx.Captures, capture), System.Drawing.Imaging.ImageFormat.Png);
                ctx.Report.Images.Add(ImageResult.From("Editor.UI.Gameplay." + backend.ShortName, capture, VisualCapture.Measure(frame)));
            }
        });
        HeadlessHarness.Step("invalid layouts and unreadable source preserve the last good file", () =>
        {
            byte[] before = File.ReadAllBytes(path);
            UiAssetDocument invalid = UiAssetDocument.Load(path);
            invalid.Elements.Single(item => item.Id == panel).ParentId = panel;
            bool rejected = false;
            try { UiAssetDocument.Save(path, invalid); } catch (InvalidDataException) { rejected = true; }
            HeadlessHarness.Assert(rejected && File.ReadAllBytes(path).SequenceEqual(before), "A parent cycle corrupted the UI resource.");
            string corrupt = Path.Combine(Path.GetDirectoryName(path)!, "Corrupt.ui");
            File.WriteAllText(corrupt, "broken json");
            using UiEditorControl unreadable = new(corrupt, project.RootPath);
            rejected = false;
            try { unreadable.Save(); } catch (InvalidDataException) { rejected = true; }
            HeadlessHarness.Assert(rejected && File.ReadAllText(corrupt) == "broken json", "An unreadable resource was silently replaced.");
        });
        UiNoviceWorkflow(ctx, fixture);
    }
}

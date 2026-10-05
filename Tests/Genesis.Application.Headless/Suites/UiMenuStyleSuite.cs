using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A User Interface resource that is a game menu: rounded and gradient boxes with borders, aligned
/// and spaced text, hover/pressed looks, a slider and a toggle the pointer changes and PGSL reads,
/// and contained/covered/cropped images. Authored through the UI Editor's Inspector, drawn by the
/// runtime on every renderer and checked pixel by pixel; an older UI file must draw as before.
/// </summary>
internal static class UiMenuStyleSuite
{
    private const string Ui = "Menu Style";
    private static readonly Color Backdrop = Color.FromArgb(8, 10, 13);

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "UiMenuStyle");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        ResourceService resources = ctx.Resources ?? throw new InvalidOperationException("No resource service.");
        string path = string.Empty;

        HeadlessHarness.RunCase(ctx.Report, "Editor.UI.MenuStyle.InspectorAuthorsEveryStyleAndItSurvivesSaving", () =>
        {
            path = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, Ui);
            string art = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, "Menu Art");
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(art).Document, art, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.Save(session, ImageWorkspace.CreateBlank(64, 32, Color.FromArgb(255, 240, 200, 40)));

            using Form host = GateSuite.NewHost(1360, 900);
            using UiEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.Document.DesignWidth = 640; editor.Document.DesignHeight = 360;
            void Set(string property, object value) =>
                HeadlessHarness.Assert(editor.TryApplyInspectorValue(property, value), "The Inspector refused " + property + " = " + value + ".");
            void Element(UiElementType type, string id, float x, float y, float width, float height)
            {
                editor.AddElement(type);
                Set("Ui.Id", id); Set("Ui.X", x); Set("Ui.Y", y); Set("Ui.Width", width); Set("Ui.Height", height);
            }

            Element(UiElementType.Panel, "Card", 20, 20, 200, 120);
            Set("Ui.CornerRadius", 30); Set("Ui.Fill", "VerticalGradient");
            Set("Ui.Background", "#FFFF2020"); Set("Ui.GradientEnd", "#FF2020FF");
            Element(UiElementType.Panel, "Bar", 240, 20, 200, 60);
            Set("Ui.Fill", "HorizontalGradient"); Set("Ui.Background", "#FF20FF20"); Set("Ui.GradientEnd", "#FF000000");
            Set("Ui.CornerRadius", 10); Set("Ui.BorderWidth", 4); Set("Ui.BorderColor", "#FFFFFF00");
            Element(UiElementType.Button, "Play", 240, 100, 200, 60);
            Set("Ui.Text", "PLAY"); Set("Ui.FontSize", 24); Set("Ui.Font", "Consolas");
            Set("Ui.TextAlign", "Center"); Set("Ui.TextVerticalAlign", "Middle"); Set("Ui.LetterSpacing", 6);
            Set("Ui.Background", "#FF303030"); Set("Ui.BorderWidth", 0); Set("Ui.CornerRadius", 12);
            Set("Ui.Hover.Background", "#FF20C040"); Set("Ui.Pressed.Background", "#FF2040C0"); Set("Ui.Disabled.Foreground", "#FF808080");
            Element(UiElementType.Slider, "Volume", 20, 180, 300, 30);
            Set("Ui.Maximum", 10); Set("Ui.Minimum", 0); Set("Ui.Step", 1); Set("Ui.Value", 5);
            Set("Ui.Accent", "#FFFF8000");
            Element(UiElementType.Toggle, "Sound", 20, 230, 220, 36);
            Set("Ui.Text", "Sound"); Set("Ui.Accent", "#FF20C0FF");
            Element(UiElementType.Text, "Corner", 340, 180, 280, 60);
            Set("Ui.Text", "R"); Set("Ui.FontSize", 28); Set("Ui.TextAlign", "Right"); Set("Ui.TextVerticalAlign", "Bottom");
            Set("Ui.Foreground", "#FFFFFFFF");
            Element(UiElementType.Image, "Contained", 460, 20, 100, 100);
            Set("Ui.Image", "Menu Art"); Set("Ui.ImageFit", "Contain");
            Element(UiElementType.Image, "Covered", 460, 240, 100, 100);
            Set("Ui.Image", "Menu Art"); Set("Ui.ImageFit", "Cover");
            Element(UiElementType.Image, "Cropped", 340, 260, 100, 50);
            Set("Ui.Image", "Menu Art"); Set("Ui.ImageFit", "Crop");
            Set("Ui.CropX", .5); Set("Ui.CropY", 0); Set("Ui.CropWidth", .5); Set("Ui.CropHeight", 1);

            editor.SelectElement("Play");
            string[] buttonProperties = editor.GetLiveInspectorValues().Select(value => value.PropertyPath).ToArray();
            HeadlessHarness.Assert(new[] { "Ui.CornerRadius", "Ui.BorderWidth", "Ui.Fill", "Ui.GradientEnd", "Ui.TextAlign", "Ui.LetterSpacing",
                    "Ui.Hover.Background", "Ui.Pressed.Foreground", "Ui.Selected.BorderColor", "Ui.Disabled.Background", "Ui.Enabled" }
                    .All(buttonProperties.Contains) && !buttonProperties.Contains("Ui.ImageFit") && !buttonProperties.Contains("Ui.Step"),
                "A button's Inspector lacks a style or offers image/slider inputs: " + string.Join(", ", buttonProperties));
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue("Ui.Hover.Background", "green") && !editor.TryApplyInspectorValue("Ui.Fill", "Diagonal")
                && !editor.TryApplyInspectorValue("Ui.CornerRadius", -3), "Invalid style values were accepted.");
            editor.SelectElement("Contained");
            HeadlessHarness.Assert(editor.GetLiveInspectorValues().Any(value => value.PropertyPath == "Ui.ImageFit")
                && !editor.TryApplyInspectorValue("Ui.LetterSpacing", 3), "An image lacks fit modes or accepts text spacing.");
            editor.SelectElement("Volume");
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue("Ui.Minimum", 20), "A slider accepted a minimum above its maximum.");

            editor.Save();
            UiAssetDocument saved = UiAssetDocument.Load(path);
            UiElement play = saved.Elements.Single(item => item.Id == "Play");
            UiElement volume = saved.Elements.Single(item => item.Id == "Volume");
            UiElement covered = saved.Elements.Single(item => item.Id == "Covered");
            HeadlessHarness.Assert(play is { CornerRadius: 12, BorderWidth: 0, TextAlign: UiHorizontalAlign.Center, LetterSpacing: 6 }
                && play.Hover?.Background == "#FF20C040" && play.Pressed?.Background == "#FF2040C0" && play.Selected is null
                && volume is { Type: UiElementType.Slider, Minimum: 0, Maximum: 10, Step: 1, Value: 5 }
                && covered.ImageFit == UiImageFit.Cover && saved.Elements.Single(item => item.Id == "Bar").Fill == UiFill.HorizontalGradient,
                "Saving lost menu styles, state looks, slider range or image fit.");

            editor.SelectElement("Play");
            ComboBox states = FindAll<ComboBox>(editor).Single(combo => combo.Items.Contains("Hover") && combo.Items.Contains("Pressed"));
            Control canvas = FindAll<Control>(editor).Single(control => control.GetType().Name == "UiDesignCanvas");
            static bool Green(Color pixel) => pixel.G > 160 && pixel.R < 80 && pixel.B > 40 && pixel.B < 110;
            int normalGreen;
            using (Bitmap normal = new(canvas.Width, canvas.Height))
            {
                canvas.DrawToBitmap(normal, canvas.ClientRectangle);
                normalGreen = Count(normal, Green);
            }
            states.SelectedItem = "Hover"; GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(editor.PreviewState == "Hover", "The Inspector's Look list did not switch the canvas preview state.");
            using (Bitmap hover = new(canvas.Width, canvas.Height))
            {
                canvas.DrawToBitmap(hover, canvas.ClientRectangle);
                int hoverGreen = Count(hover, Green);
                HeadlessHarness.Assert(hoverGreen > normalGreen + 200,
                    "The canvas does not show the selected button's hover look: " + normalGreen + " -> " + hoverGreen);
            }
            Control details = (Control)typeof(UiEditorControl).GetField("_detailsDock",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
            if (!details.Visible)
                editor.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(menu => menu.Text == "Options")
                    .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Inspector").PerformClick();
            host.ClientSize = new Size(1500, 900); GateSuite.Pump(4, 20);
            Control cornerInput = FindAll<NumericUpDown>(details).First(number => number.Value == 12);
            details.Controls.OfType<ScrollableControl>().FirstOrDefault()?.ScrollControlIntoView(cornerInput);
            GateSuite.Pump(3, 20);
            Control centre = canvas.Parent!;
            Control textRow = FindAll<TextBox>(details).Single(box => box.Text == "PLAY").Parent!;
            Control sizeRow = FindAll<NumericUpDown>(details).First(number => number.Value == 60).Parent!.Parent!.Parent!;
            HeadlessHarness.Assert(details.Visible && cornerInput.Visible && centre.Right <= details.Left && textRow.Top > sizeRow.Top && textRow.Top < cornerInput.Parent!.Top,
                "The Inspector hides the corner radius, lies over the canvas or reorders rows: " + centre.Bounds + "; " + details.Bounds
                + "; text " + textRow.Bounds + " size " + sizeRow.Bounds);
            string capture = "ui-menu-style-editor.png";
            ctx.Report.Images.Add(ImageResult.From("Editor.UI.MenuStyle", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
            details.Controls.OfType<ScrollableControl>().FirstOrDefault()?.ScrollControlIntoView(states);
            GateSuite.Pump(3, 20);
            string stateCapture = "ui-menu-style-states.png";
            ctx.Report.Images.Add(ImageResult.From("Editor.UI.MenuStyle.States", stateCapture,
                VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, stateCapture))));
            states.SelectedItem = "Normal"; GateSuite.Pump(2, 20);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.UI.MenuStyle.OldUiFilesDrawAsBefore", () =>
        {
            string legacy = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, "Legacy Menu");
            File.WriteAllText(legacy, """
                { "schemaVersion": 1, "designWidth": 640, "designHeight": 360, "elements": [
                  { "id": "Back", "type": "Panel", "x": 10, "y": 10, "width": 300, "height": 200, "background": "#FF102030" },
                  { "id": "Go", "type": "Button", "x": 20, "y": 20, "width": 120, "height": 40, "text": "Go", "accent": "#FFFF0000" },
                  { "id": "Hp", "type": "ProgressBar", "x": 20, "y": 80, "width": 200, "height": 20, "value": 50, "maximum": 100 },
                  { "id": "Label", "type": "Text", "x": 20, "y": 120, "width": 200, "height": 30, "text": "Hello" } ] }
                """);
            UiAssetDocument document = UiAssetDocument.Load(legacy);
            HeadlessHarness.Assert(document.Elements.All(element => element is { CornerRadius: 0, BorderWidth: null, Fill: UiFill.Solid,
                TextAlign: UiHorizontalAlign.Auto, LetterSpacing: 0, ImageFit: UiImageFit.Stretch, Enabled: true, Hover: null }),
                "An older UI file did not load with the plain defaults.");
            PgslRecordingDrawSurface surface = Draw("Legacy Menu", project.RootPath, null);
            var fills = surface.Rectangles.Where(call => call.Filled).ToArray();
            var outlines = surface.Rectangles.Where(call => !call.Filled).ToArray();
            HeadlessHarness.Assert(fills.Length == 4 && outlines.Length == 2
                && fills[0] == new PgslRecordingDrawSurface.RectRecord(10, 10, 300, 200, true, Color.FromArgb(255, 16, 32, 48))
                && outlines[0] == new PgslRecordingDrawSurface.RectRecord(20, 20, 120, 40, false, Color.FromArgb(255, 255, 0, 0))
                && fills[3] == new PgslRecordingDrawSurface.RectRecord(20, 80, 100, 20, true, Color.FromArgb(255, 108, 140, 255))
                && surface.Texts.Count == 2 && surface.Texts[0] is { Text: "Go", Centered: true } && surface.Texts[1] is { Text: "Hello", Centered: false }
                && surface.Texts.All(text => text.UiBounds is not null && text.Tracking == 0),
                "An older UI file no longer draws the same calls: " + string.Join("; ", surface.Rectangles) + " | " + string.Join("; ", surface.Texts));
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.UI.MenuStyle.ImagesContainCoverAndCrop", () =>
        {
            string previous = PgslCommands.ProjectPath;
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                HeadlessHarness.Assert(PgslCommands.SpriteWidth("Menu Art") == 64 && PgslCommands.SpriteHeight("Menu Art") == 32,
                    "The test image is not 64 x 32: " + PgslCommands.SpriteWidth("Menu Art") + " x " + PgslCommands.SpriteHeight("Menu Art"));
            }
            finally { PgslCommands.ProjectPath = previous; }
            PgslRecordingDrawSurface surface = Draw(Ui, project.RootPath, null);
            var contained = surface.Sprites.Single(call => call.Destination?.X == 460 && call.Destination?.Y == 45);
            var covered = surface.Sprites.Single(call => call.Destination == new RectangleF(460, 240, 100, 100));
            var cropped = surface.Sprites.Single(call => call.Destination == new RectangleF(340, 260, 100, 50));
            HeadlessHarness.Assert(contained.Destination == new RectangleF(460, 45, 100, 50) && contained.Source is null
                && covered.Source is RectangleF cover && Math.Abs(cover.X - .25f) < .001f && Math.Abs(cover.Width - .5f) < .001f && cover.Height == 1
                && cropped.Source == new RectangleF(.5f, 0, .5f, 1),
                "Image fit placed the picture wrongly: " + string.Join("; ", surface.Sprites));
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.UI.MenuStyle.SliderAndToggleFollowThePointerAndPgslReadsThem", () =>
        {
            InputState input = new();
            IGameContext? oldGame = PgslCommands.ActiveGameContext;
            string oldPath = PgslCommands.ProjectPath;
            PgslContext context = new() { RoomWidth = 640, RoomHeight = 360, DrawSurface = new PgslRecordingDrawSurface() };
            PgslContext? oldContext = PgslCommands.BindContext(context);
            PgslCommands.ActiveGameContext = new NullGameContext { Input = input, ProjectPath = project.RootPath };
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                // Volume runs from x 20 to 320, knob radius 15: travel 35..305. 80% is x 251.
                input.OnMouseMove(251, 195); input.OnMouseDown(MouseButton.Left);
                PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetValue(Ui, "Volume") == 8 && PgslCommands.UiValueChanged(Ui, "Volume")
                    && !PgslCommands.UiValueChanged(Ui, "Volume"), "Pressing on the slider did not set and report 8: " + PgslCommands.UiGetValue(Ui, "Volume"));
                PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(!PgslCommands.UiValueChanged(Ui, "Volume"), "A second update in the same frame reported another change.");
                input.NextFrame(); input.OnMouseMove(89, 400);
                PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetValue(Ui, "Volume") == 2, "Dragging off the slider did not keep moving it: " + PgslCommands.UiGetValue(Ui, "Volume"));
                input.NextFrame(); input.OnMouseUp(MouseButton.Left); PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(!PgslCommands.UiClicked(Ui, "Volume") && PgslCommands.UiGetHovered(Ui) == string.Empty,
                    "Releasing away from the slider counted as a click or left it hovered.");

                input.NextFrame(); input.OnMouseMove(40, 248); PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetHovered(Ui) == "Sound", "The toggle is not hovered: " + PgslCommands.UiGetHovered(Ui));
                input.OnMouseDown(MouseButton.Left); PgslCommands.UiUpdate(Ui);
                input.NextFrame(); input.OnMouseUp(MouseButton.Left); PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetValue(Ui, "Sound") == 1 && PgslCommands.UiClicked(Ui, "Sound")
                    && PgslCommands.UiValueChanged(Ui, "Sound"), "Clicking the toggle did not switch it on and report it.");

                PgslCommands.UiSetEnabled(Ui, "Play", false);
                input.NextFrame(); input.OnMouseMove(300, 130); PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetHovered(Ui) == string.Empty, "A disabled button still takes the pointer.");
                PgslCommands.UiSetEnabled(Ui, "Play", true); PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetHovered(Ui) == "Play", "Re-enabling the button did not let the pointer hover it.");
            }
            finally
            {
                PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath;
            }

            // The same through a PGSL script: a press at 30% of the slider, read back by the script.
            InputState scriptInput = new();
            scriptInput.OnMouseMove(116, 195); scriptInput.OnMouseDown(MouseButton.Left);
            PgslCommands.ActiveGameContext = new NullGameContext { Input = scriptInput, ProjectPath = project.RootPath };
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string>
                {
                    ["Create"] = """
                        UiUpdate("Menu Style");
                        volume = UiGetValue("Menu Style", "Volume");
                        changed = 0;
                        if (UiValueChanged("Menu Style", "Volume")) { changed = 1; }
                        hovered = UiGetHovered("Menu Style");
                        """,
                }, frames: 0, roomWidth: 640, roomHeight: 360);
                HeadlessHarness.Assert(result.Ok && result.Numbers.TryGetValue("volume", out double volume) && volume == 3
                    && result.Numbers.TryGetValue("changed", out double changed) && changed == 1
                    && result.Strings.TryGetValue("hovered", out string? hovered) && hovered == "Volume",
                    "PGSL could not read the slider: " + string.Join("; ", result.Errors) + " | "
                    + string.Join(", ", result.Numbers.Select(pair => pair.Key + "=" + pair.Value)) + " | " + string.Join(", ", result.Strings));
            }
            finally { PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.UI.MenuStyle.StylesRenderOnEveryBackend", () =>
        {
            InputState input = new();
            IGameContext? oldGame = PgslCommands.ActiveGameContext;
            using Form game = GateSuite.NewHost(640, 360);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            PgslContext gameContext = new() { RoomWidth = 640, RoomHeight = 360 };
            viewport.OnRender += renderer =>
            {
                renderer.Clear(.03f, .04f, .05f); renderer.Set3DFrameActive(false);
                renderer.SetCamera2D(renderer.PixelWidth / 2f, renderer.PixelHeight / 2f, 1, 0);
                gameContext.RoomWidth = renderer.PixelWidth; gameContext.RoomHeight = renderer.PixelHeight;
                gameContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight, projectPath: project.RootPath, isGui: true);
                PgslContext? previous = PgslCommands.BindContext(gameContext);
                string previousProject = PgslCommands.ProjectPath;
                PgslCommands.ProjectPath = project.RootPath;
                try { PgslCommands.DrawUi(Ui); }
                finally { PgslCommands.BindContext(previous); PgslCommands.ProjectPath = previousProject; }
            };
            game.Controls.Add(viewport); GateSuite.ShowHost(game);
            PgslCommands.ActiveGameContext = new NullGameContext { Input = input, ProjectPath = project.RootPath };
            List<string> failures = [];
            try
            {
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                    input.OnMouseMove(5, 5);
                    using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                    if (frame is null || viewport.RenderFaultCount != 0 || frame.Width < 640 || frame.Height < 360)
                    { failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException); continue; }
                    string capture = "ui-menu-style-" + backend.ShortName.ToLowerInvariant() + ".png";
                    frame.Save(Path.Combine(ctx.Captures, capture), System.Drawing.Imaging.ImageFormat.Png);
                    ctx.Report.Images.Add(ImageResult.From("Runtime.UI.MenuStyle." + backend.ShortName, capture, VisualCapture.Measure(frame)));

                    void Check(bool condition, string what) { if (!condition) failures.Add(backend.ShortName + ": " + what); }
                    Color corner = frame.GetPixel(21, 21), cardTop = frame.GetPixel(120, 24), cardBottom = frame.GetPixel(120, 135);
                    Check(Near(corner, Backdrop, 12), "rounded corner is not transparent " + corner);
                    Check(cardTop.R > 180 && cardTop.B < 90, "gradient top is not red " + cardTop);
                    Check(cardBottom.B > 180 && cardBottom.R < 90, "gradient bottom is not blue " + cardBottom);
                    Color barLeft = frame.GetPixel(250, 50), barRight = frame.GetPixel(428, 50), border = frame.GetPixel(241, 50);
                    Check(barLeft.G > 170 && barRight.G < 60 && barLeft.G > barRight.G + 100, "horizontal gradient runs the wrong way " + barLeft + " " + barRight);
                    Check(border.R > 200 && border.G > 200 && border.B < 80, "border is not yellow " + border);
                    Rectangle ink = Ink(frame, new Rectangle(244, 104, 192, 52), pixel => pixel.R > 200 && pixel.G > 200 && pixel.B > 200);
                    Check(ink.Width > 40 && Math.Abs(ink.X + ink.Width / 2f - 340) <= 3 && Math.Abs(ink.Y + ink.Height / 2f - 130) <= 4,
                        "button text is not centred: " + ink);
                    Rectangle right = Ink(frame, new Rectangle(340, 180, 280, 60), pixel => pixel.R > 200 && pixel.G > 200 && pixel.B > 200);
                    Check(right.Width > 4 && right.Right >= 612 && right.Bottom >= 228, "right/bottom text is not at the corner: " + right);
                    Color normal = frame.GetPixel(250, 106);
                    Check(Near(normal, Color.FromArgb(48, 48, 48), 14), "button is not its normal grey " + normal);
                    Color knob = frame.GetPixel(170, 195), sliderFill = frame.GetPixel(60, 195), toggleOff = frame.GetPixel(75, 248);
                    Check(Near(toggleOff, Color.FromArgb(58, 66, 80), 14), "toggle is not drawn off " + toggleOff);
                    Check(knob.R > 220 && knob.G > 220 && knob.B > 220, "slider knob is not at the middle " + knob);
                    Check(sliderFill.R > 200 && sliderFill.G > 90 && sliderFill.G < 170 && sliderFill.B < 60, "slider fill is not orange " + sliderFill);
                    Color contained = frame.GetPixel(510, 70), letterbox = frame.GetPixel(510, 30);
                    Check(contained.R > 200 && contained.G > 160 && contained.B < 90 && Near(letterbox, Backdrop, 12),
                        "contained image is not letterboxed " + contained + " " + letterbox);

                    input.OnMouseMove(300, 130);
                    using Bitmap? hovered = viewport.ReadbackFrameToBitmap(3);
                    Color hover = hovered?.GetPixel(250, 106) ?? Color.Empty;
                    Check(hover.G > 160 && hover.R < 80 && hover.B < 110, "button did not take its hover look under the pointer " + hover);
                    if (backend.Backend == RenderBackendOption.SilkNetDx11 && hovered is not null)
                    {
                        string hoverCapture = "ui-menu-style-hover-dx11.png";
                        hovered.Save(Path.Combine(ctx.Captures, hoverCapture), System.Drawing.Imaging.ImageFormat.Png);
                        ctx.Report.Images.Add(ImageResult.From("Runtime.UI.MenuStyle.Hover", hoverCapture, VisualCapture.Measure(hovered)));
                    }
                    input.OnMouseMove(5, 5);
                }
            }
            finally { PgslCommands.ActiveGameContext = oldGame; }
            HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
        });
    }

    private static PgslRecordingDrawSurface Draw(string ui, string projectRoot, IGameContext? game)
    {
        PgslRecordingDrawSurface surface = new();
        PgslContext context = new() { RoomWidth = 640, RoomHeight = 360, DrawSurface = surface };
        PgslContext? previousContext = PgslCommands.BindContext(context);
        IGameContext? previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ProjectPath = projectRoot;
        PgslCommands.ActiveGameContext = game;
        try { PgslCommands.DrawUi(ui); }
        finally { PgslCommands.BindContext(previousContext); PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousPath; }
        return surface;
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static Rectangle Ink(Bitmap frame, Rectangle area, Func<Color, bool> match)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = area.Top; y < Math.Min(area.Bottom, frame.Height); y++)
            for (int x = area.Left; x < Math.Min(area.Right, frame.Width); x++)
            {
                if (!match(frame.GetPixel(x, y))) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static int Count(Bitmap bitmap, Func<Color, bool> match)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
            for (int x = 0; x < bitmap.Width; x += 2)
                if (match(bitmap.GetPixel(x, y))) count++;
        return count;
    }

    private static IEnumerable<T> FindAll<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (T nested in FindAll<T>(child)) yield return nested;
        }
    }
}

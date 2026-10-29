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

        // The Inspector's Layout, Spacing and Padding rows: edited in the Inspector's own controls, undone and
        // redone, saved and reopened; the canvas and the game then place the children the same way.
        HeadlessHarness.RunCase(ctx.Report, "Editor.UI.Inspector.LayoutSpacingAndPaddingEditUndoSaveReopenAndPlaceTheGamesChildren", () =>
        {
            const string name = "Inspector Layout";
            string layoutPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, name);
            using Form host = GateSuite.NewHost(1500, 900);
            using UiEditorControl editor = new(layoutPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.Document.DesignWidth = 640; editor.Document.DesignHeight = 360;
            void Set(string property, object value) =>
                HeadlessHarness.Assert(editor.TryApplyInspectorValue(property, value), "The Inspector refused " + property + " = " + value + ".");
            editor.AddElement(UiElementType.Panel);
            Set("Ui.Id", "Box"); Set("Ui.X", 20); Set("Ui.Y", 20); Set("Ui.Width", 300); Set("Ui.Height", 200);
            foreach ((string id, float height) in new[] { ("A", 30f), ("B", 50f) })
            {
                editor.AddElement(UiElementType.Button);
                Set("Ui.Id", id); Set("Ui.Parent", "Box"); Set("Ui.Width", 100); Set("Ui.Height", height);
            }
            editor.SelectElement("Box"); GateSuite.Pump(3, 20);
            ComboBox layout = Named<ComboBox>(editor, "UiLayout");
            NumericUpDown spacing = Named<NumericUpDown>(editor, "UiSpacing"), padding = Named<NumericUpDown>(editor, "UiPadding");
            HeadlessHarness.Assert(layout.Parent!.Visible && spacing.Parent!.Visible && padding.Parent!.Visible,
                "A Panel's Inspector does not show Layout, Spacing and Padding.");
            UiElement Box() => editor.Document.Elements.Single(item => item.Id == "Box");
            layout.SelectedItem = UiLayoutMode.Vertical; spacing.Value = 12; padding.Value = 5; GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(Box() is { Layout: UiLayoutMode.Vertical, Spacing: 12, Padding: 5 },
                $"The Inspector's controls did not write the document: {Box().Layout}, {Box().Spacing}, {Box().Padding}.");

            // Undo takes back the last edit alone, redo puts it back, and the controls follow.
            editor.Undo(); GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(Box() is { Layout: UiLayoutMode.Vertical, Spacing: 12, Padding: 0 } && padding.Value == 0 && spacing.Value == 12,
                $"Undo did not take back the padding alone: {Box().Padding} (the control shows {padding.Value}).");
            editor.Redo(); GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(Box().Padding == 5 && padding.Value == 5 && layout.SelectedItem is UiLayoutMode.Vertical,
                $"Redo did not bring the padding back: {Box().Padding} (the control shows {padding.Value}).");

            // The canvas places the children as the game will: 5 in, one after another, 12 apart.
            Control canvas = FindAll<Control>(editor).Single(control => control.GetType().Name == "UiDesignCanvas");
            Type canvasType = canvas.GetType();
            RectangleF area = (RectangleF)canvasType.GetMethod("CanvasRect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(canvas, null)!;
            var rects = (Dictionary<string, RectangleF>)canvasType.GetMethod("BuildElementRects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(canvas, new object[] { area })!;
            float sx = area.Width / 640f, sy = area.Height / 360f;
            RectangleF box = rects["Box"], a = rects["A"], b = rects["B"];
            HeadlessHarness.Assert(Math.Abs((a.X - box.X) / sx - 5) < .6f && Math.Abs((a.Y - box.Y) / sy - 5) < .6f
                    && Math.Abs((b.X - a.X) / sx) < .6f && Math.Abs((b.Y - a.Y) / sy - 42) < .6f,
                $"The canvas does not lay the children out: A at {(a.X - box.X) / sx:0.#},{(a.Y - box.Y) / sy:0.#}, B {(b.Y - a.Y) / sy:0.#} below A (5, 5 and 42 expected).");
            Control details = (Control)typeof(UiEditorControl).GetField("_detailsDock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
            HeadlessHarness.Assert(details.Visible, "The Inspector is not shown.");
            details.Controls.OfType<ScrollableControl>().FirstOrDefault()?.ScrollControlIntoView(padding);
            GateSuite.Pump(3, 20);
            string capture = "ui-inspector-layout.png";
            ctx.Report.Images.Add(ImageResult.From("Editor.UI.Inspector.Layout", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));

            // Padding also takes a button's text in from its box (in the game, and so in the first save).
            editor.Save();
            string[] bounds = [];
            WithGame(project.RootPath, (_, surface) =>
            {
                HeadlessHarness.Assert(PgslCommands.UiHitTest(name, 30, 30) == "A" && PgslCommands.UiHitTest(name, 30, 70) == "B" && PgslCommands.UiHitTest(name, 30, 60) == "Box",
                    $"The game does not place the children as laid out: {PgslCommands.UiHitTest(name, 30, 30)}, {PgslCommands.UiHitTest(name, 30, 70)}, {PgslCommands.UiHitTest(name, 30, 60)} (A, B, Box expected).");
                PgslCommands.DrawUi(name);
                bounds = surface.Texts.Select(text => text.UiBounds?.ToString() ?? "-").ToArray();
            });
            editor.SelectElement("A"); Set("Ui.Padding", 6); editor.Save();
            WithGame(project.RootPath, (_, surface) =>
            {
                PgslCommands.DrawUi(name);
                HeadlessHarness.Assert(bounds.Contains("{X=25,Y=25,Width=100,Height=30}")
                        && surface.Texts.Any(text => text.UiBounds is { X: 31, Y: 31, Width: 88, Height: 18 }),
                    "Padding did not take the button's text in from its box: " + string.Join("; ", bounds) + " then " + string.Join("; ", surface.Texts.Select(text => text.UiBounds?.ToString() ?? "-")));
            });

            // Reopened, the saved layout shows in the Inspector's controls and in its live values.
            using Form reopenedHost = GateSuite.NewHost(1500, 900);
            using UiEditorControl reopened = new(layoutPath, project.RootPath);
            reopenedHost.Controls.Add(reopened);
            GateSuite.ShowHost(reopenedHost);
            reopened.SelectElement("Box"); GateSuite.Pump(3, 20);
            var live = reopened.GetLiveInspectorValues().ToDictionary(value => value.PropertyPath, value => value.Value);
            HeadlessHarness.Assert(Named<ComboBox>(reopened, "UiLayout").SelectedItem is UiLayoutMode.Vertical && Named<NumericUpDown>(reopened, "UiSpacing").Value == 12
                    && Named<NumericUpDown>(reopened, "UiPadding").Value == 5 && Equals(live["Ui.Layout"], "Vertical") && Convert.ToDouble(live["Ui.Spacing"]) == 12
                    && Convert.ToDouble(live["Ui.Padding"]) == 5 && reopened.Document.Elements.Single(item => item.Id == "A").Padding == 6,
                "Reopening lost the layout, spacing or padding.");
        });

        // The Style row: the styles of the UI's theme are offered, one is chosen (and undone), and the canvas and
        // the game draw the element with it.
        HeadlessHarness.RunCase(ctx.Report, "Editor.UI.Inspector.StyleNamesAThemeStyleAndTheCanvasAndTheGameDrawIt", () =>
        {
            const string name = "Inspector Style";
            string stylePath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, name);
            UiAssetDocument seed = new() { DesignWidth = 640, DesignHeight = 360,
                Styles = new() { ["Primary"] = new UiNamedStyle { Background = "#FF112233" }, ["Danger"] = new UiNamedStyle { Background = "#FFAA2211" } } };
            seed.Elements.Add(new UiElement { Id = "Go", Type = UiElementType.Button, X = 20, Y = 20, Width = 160, Height = 60, Text = "Go" });
            seed.Elements.Add(new UiElement { Id = "Plain", Type = UiElementType.Panel, X = 300, Y = 20, Width = 100, Height = 60 });
            UiAssetDocument.Save(stylePath, seed);

            using Form host = GateSuite.NewHost(1500, 900);
            using UiEditorControl editor = new(stylePath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.SelectElement("Go"); GateSuite.Pump(3, 20);
            ComboBox style = Named<ComboBox>(editor, "UiStyle");
            HeadlessHarness.Assert(style.Parent!.Visible && style.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(["", "Danger", "Primary"]),
                "The Style row does not offer the theme's styles: " + string.Join(", ", style.Items.Cast<object>()));
            Control canvas = FindAll<Control>(editor).Single(control => control.GetType().Name == "UiDesignCanvas");
            int Primary()
            {
                using Bitmap picture = new(canvas.Width, canvas.Height);
                canvas.DrawToBitmap(picture, canvas.ClientRectangle);
                return Count(picture, pixel => Near(pixel, Color.FromArgb(0x11, 0x22, 0x33), 5));
            }
            int before = Primary();
            style.Text = "Primary"; GateSuite.Pump(3, 20);
            UiElement Go() => editor.Document.Elements.Single(item => item.Id == "Go");
            int after = Primary();
            HeadlessHarness.Assert(Go().Style == "Primary" && after > before + 300, $"The canvas does not draw the button in its style: {before} -> {after} matching pixels, style '{Go().Style}'.");
            editor.Undo(); GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(Go().Style.Length == 0 && style.Text.Length == 0 && Primary() <= before + 20, "Undo did not take the style back.");
            editor.Redo(); GateSuite.Pump(3, 20);
            style.SelectedItem = "Danger"; GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(Go().Style == "Danger", "Choosing a style from the list did not set it: '" + Go().Style + "'.");
            Control details = (Control)typeof(UiEditorControl).GetField("_detailsDock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
            details.Controls.OfType<ScrollableControl>().FirstOrDefault()?.ScrollControlIntoView(style);
            GateSuite.Pump(3, 20);
            string capture = "ui-inspector-style.png";
            ctx.Report.Images.Add(ImageResult.From("Editor.UI.Inspector.Style", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
            editor.Save();

            using Form reopenedHost = GateSuite.NewHost(1500, 900);
            using UiEditorControl reopened = new(stylePath, project.RootPath);
            reopenedHost.Controls.Add(reopened);
            GateSuite.ShowHost(reopenedHost);
            reopened.SelectElement("Go"); GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(reopened.Document.Elements.Single(item => item.Id == "Go").Style == "Danger" && Named<ComboBox>(reopened, "UiStyle").Text == "Danger"
                    && reopened.Document.Styles is { Count: 2 } && reopened.GetLiveInspectorValues().Single(value => value.PropertyPath == "Ui.Style").Value is "Danger",
                "Reopening lost the style or the theme's styles.");

            WithGame(project.RootPath, (_, surface) =>
            {
                PgslCommands.DrawUi(name);
                bool Drew(float x, float y, int r, int g, int b) => surface.Rectangles.Any(rect => rect.Filled && rect.X == x && rect.Y == y
                    && rect.Color.R == r && rect.Color.G == g && rect.Color.B == b);
                HeadlessHarness.Assert(Drew(20, 20, 0xAA, 0x22, 0x11) && Drew(300, 20, 22, 27, 34),
                    "The game did not draw the button in its Danger style and the unstyled panel in its own colours: " + string.Join("; ", surface.Rectangles));
            });
        });

        // The Placeholder and Max length rows of a Text input (and its Padding): written, undone, saved and
        // reopened, then typed into in the game.
        HeadlessHarness.RunCase(ctx.Report, "Editor.UI.Inspector.PlaceholderAndMaxLengthOfATextInputEditUndoSaveReopenAndLimitTyping", () =>
        {
            const string name = "Inspector Input";
            string inputPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, name);
            using Form host = GateSuite.NewHost(1500, 900);
            using UiEditorControl editor = new(inputPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.Document.DesignWidth = 640; editor.Document.DesignHeight = 360;
            void Set(string property, object value) =>
                HeadlessHarness.Assert(editor.TryApplyInspectorValue(property, value), "The Inspector refused " + property + " = " + value + ".");
            editor.AddElement(UiElementType.TextInput);
            Set("Ui.Id", "Name"); Set("Ui.X", 20); Set("Ui.Y", 20); Set("Ui.Width", 200); Set("Ui.Height", 40);
            GateSuite.Pump(3, 20);
            TextBox placeholder = Named<TextBox>(editor, "UiPlaceholder");
            NumericUpDown maxLength = Named<NumericUpDown>(editor, "UiMaxLength"), padding = Named<NumericUpDown>(editor, "UiPadding");
            HeadlessHarness.Assert(placeholder.Parent!.Visible && maxLength.Parent!.Visible && padding.Parent!.Visible && placeholder.Text == "Type here" && maxLength.Value == 0,
                "A new Text input's Inspector lacks Placeholder and Max length, or they do not start at 'Type here' and 0.");
            UiElement Input() => editor.Document.Elements.Single(item => item.Id == "Name");
            placeholder.Text = "Your name"; maxLength.Value = 6; padding.Value = 4; GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(Input() is { Placeholder: "Your name", MaxLength: 6, Padding: 4 }, $"The controls did not write the document: '{Input().Placeholder}', {Input().MaxLength}, {Input().Padding}.");
            editor.Undo(); editor.Undo(); GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(Input() is { Placeholder: "Your name", MaxLength: 0, Padding: 0 } && maxLength.Value == 0,
                $"Two undos did not take back the padding and the length alone: {Input().MaxLength}, {Input().Padding}.");
            editor.Redo(); editor.Redo(); GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(Input() is { MaxLength: 6, Padding: 4 } && maxLength.Value == 6 && padding.Value == 4, "Redo did not bring the length and padding back.");
            Control details = (Control)typeof(UiEditorControl).GetField("_detailsDock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
            details.Controls.OfType<ScrollableControl>().FirstOrDefault()?.ScrollControlIntoView(maxLength);
            GateSuite.Pump(3, 20);
            string capture = "ui-inspector-text-input.png";
            ctx.Report.Images.Add(ImageResult.From("Editor.UI.Inspector.TextInput", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
            editor.Save();

            using Form reopenedHost = GateSuite.NewHost(1500, 900);
            using UiEditorControl reopened = new(inputPath, project.RootPath);
            reopenedHost.Controls.Add(reopened);
            GateSuite.ShowHost(reopenedHost);
            reopened.SelectElement("Name"); GateSuite.Pump(3, 20);
            var live = reopened.GetLiveInspectorValues().ToDictionary(value => value.PropertyPath, value => value.Value);
            HeadlessHarness.Assert(Named<TextBox>(reopened, "UiPlaceholder").Text == "Your name" && Named<NumericUpDown>(reopened, "UiMaxLength").Value == 6
                    && Equals(live["Ui.Placeholder"], "Your name") && Convert.ToInt32(live["Ui.MaxLength"]) == 6,
                "Reopening lost the placeholder or the length.");

            WithGame(project.RootPath, (input, surface) =>
            {
                // Empty, the box shows its hint, 8 + 4 pixels in from the sides and 4 from the top and bottom.
                PgslCommands.DrawUi(name);
                HeadlessHarness.Assert(surface.Texts.Any(text => text.Text == "Your name" && text.UiBounds is { X: 32, Y: 24, Width: 176, Height: 32 }),
                    "The game does not show the placeholder inside the padding: " + string.Join("; ", surface.Texts.Select(text => text.Text + " " + text.UiBounds)));
                input.NextFrame(); input.OnMouseMove(100, 40); input.OnMouseDown(MouseButton.Left); PgslCommands.UiUpdate(name);
                input.NextFrame(); input.OnMouseUp(MouseButton.Left); PgslCommands.UiUpdate(name);
                foreach (char c in "abcdefghij") input.OnChar(c);
                PgslCommands.UiUpdate(name);
                HeadlessHarness.Assert(PgslCommands.UiGetText(name, "Name") == "abcdef", "The text box took '" + PgslCommands.UiGetText(name, "Name") + "', not the first 6 letters.");
                surface.Texts.Clear(); PgslCommands.DrawUi(name);
                HeadlessHarness.Assert(surface.Texts.Any(text => text.Text.StartsWith("abcdef", StringComparison.Ordinal)) && !surface.Texts.Any(text => text.Text == "Your name"),
                    "The game still shows the hint, or the text, after typing: " + string.Join("; ", surface.Texts.Select(text => text.Text)));
            });
        });

        // Each new row is offered to the element types it means something for, and to no others, in the
        // Inspector's controls and in its live values; values the document would refuse are refused.
        HeadlessHarness.RunCase(ctx.Report, "Editor.UI.Inspector.NewRowsAreOfferedOnlyToTheElementsTheyApplyTo", () =>
        {
            string rowsPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.UserInterface), ResourceKind.UserInterface, "Inspector Rows");
            using Form host = GateSuite.NewHost(1500, 900);
            using UiEditorControl editor = new(rowsPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            foreach (UiElementType type in Enum.GetValues<UiElementType>())
            {
                editor.AddElement(type);
                HeadlessHarness.Assert(editor.TryApplyInspectorValue("Ui.Id", type.ToString()), "Could not name the " + type + ".");
            }
            Control Row(string controlName) => FindAll<Control>(editor).Single(control => control.Name == controlName).Parent!;
            UiElementType[] containers = [UiElementType.Panel, UiElementType.ScrollList];
            UiElementType[] padded = [UiElementType.Panel, UiElementType.ScrollList, UiElementType.Text, UiElementType.Button, UiElementType.Toggle, UiElementType.TextInput];
            var problems = new List<string>();
            foreach (UiElementType type in Enum.GetValues<UiElementType>())
            {
                editor.SelectElement(type.ToString()); GateSuite.Pump(2, 15);
                HashSet<string> offered = editor.GetLiveInspectorValues().Select(value => value.PropertyPath).ToHashSet();
                (string Property, string Row, object Good, bool Expected)[] rows =
                [
                    ("Ui.Layout", "UiLayout", "Vertical", containers.Contains(type)), ("Ui.Spacing", "UiSpacing", 4, containers.Contains(type)),
                    ("Ui.Padding", "UiPadding", 3, padded.Contains(type)), ("Ui.Style", "UiStyle", "Anything", true),
                    ("Ui.Placeholder", "UiPlaceholder", "Hint", type == UiElementType.TextInput), ("Ui.MaxLength", "UiMaxLength", 5, type == UiElementType.TextInput),
                ];
                foreach ((string property, string row, object good, bool expected) in rows)
                {
                    if (offered.Contains(property) != expected) problems.Add($"{type}: live value {property} {(expected ? "missing" : "offered")}");
                    if (Row(row).Visible != expected) problems.Add($"{type}: row {row} {(expected ? "hidden" : "shown")}");
                    if (editor.TryApplyInspectorValue(property, good) != expected) problems.Add($"{type}: {property} {(expected ? "refused" : "accepted")}");
                }
            }
            HeadlessHarness.Assert(problems.Count == 0, string.Join("; ", problems));

            editor.SelectElement("Panel");
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue("Ui.Layout", "Diagonal") && !editor.TryApplyInspectorValue("Ui.Spacing", -1)
                    && !editor.TryApplyInspectorValue("Ui.Padding", "wide"), "A layout, spacing or padding the document would refuse was accepted.");
            editor.SelectElement("TextInput");
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue("Ui.MaxLength", -1) && !editor.TryApplyInspectorValue("Ui.MaxLength", 3.5), "A max length the document would refuse was accepted.");
            editor.Save();
            UiAssetDocument saved = UiAssetDocument.Load(rowsPath);
            HeadlessHarness.Assert(saved.Elements.Single(item => item.Id == "Panel") is { Layout: UiLayoutMode.Vertical, Spacing: 4, Padding: 3 }
                    && saved.Elements.Single(item => item.Id == "TextInput") is { Placeholder: "Hint", MaxLength: 5, Padding: 3, Style: "Anything" },
                "Saving lost the rows' values.");
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

    /// <summary>Runs a body with a game context that has a pointer and keys, and a surface that records what the UI draws (640 x 360).</summary>
    private static void WithGame(string projectRoot, Action<InputState, PgslRecordingDrawSurface> body)
    {
        InputState input = new();
        PgslRecordingDrawSurface surface = new();
        IGameContext? oldGame = PgslCommands.ActiveGameContext;
        string oldPath = PgslCommands.ProjectPath;
        PgslContext? oldContext = PgslCommands.BindContext(new PgslContext { RoomWidth = 640, RoomHeight = 360, DrawSurface = surface });
        PgslCommands.ActiveGameContext = new NullGameContext { Input = input, ProjectPath = projectRoot };
        PgslCommands.ProjectPath = projectRoot;
        try { body(input, surface); }
        finally { PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
    }

    private static T Named<T>(Control root, string name) where T : Control => FindAll<T>(root).Single(control => control.Name == name);

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

using System.Drawing;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The User Interface toolkit: a Scroll List lays out and clips its buttons and scrolls by the
/// wheel and the keys; the arrows move the focus (into view) and Enter presses; arrows move a
/// focused slider; a Text Input takes typing up to its length and reports Enter; a named style
/// colours a button and UiSetTheme swaps it for another UI's.
/// </summary>
internal static class UiToolkitSuite
{
    private const string Ui = "Toolkit";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.UI.Toolkit.ListsFocusTypingAndThemes", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "UiToolkit");
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Toolkit Game", "Blank");
            string folder = Path.Combine(project.AssetsPath, "User Interfaces");
            Directory.CreateDirectory(folder);
            var ui = new UiAssetDocument { DesignWidth = 640, DesignHeight = 360 };
            ui.Elements.Add(new UiElement { Id = "List", Type = UiElementType.ScrollList, X = 20, Y = 20, Width = 200, Height = 100,
                Layout = UiLayoutMode.Vertical, Spacing = 10, Padding = 5 });
            for (int i = 1; i <= 5; i++)
                ui.Elements.Add(new UiElement { Id = "Item" + i, ParentId = "List", Type = UiElementType.Button, Width = 180, Height = 40, Order = i, Text = "Item " + i });
            ui.Elements.Add(new UiElement { Id = "Name", Type = UiElementType.TextInput, X = 300, Y = 20, Width = 200, Height = 40, Placeholder = "Your name", MaxLength = 8, Order = 10 });
            ui.Elements.Add(new UiElement { Id = "Volume", Type = UiElementType.Slider, X = 300, Y = 100, Width = 200, Height = 30, Minimum = 0, Maximum = 10, Step = 1, Value = 5, Order = 11 });
            ui.Elements.Add(new UiElement { Id = "Play", Type = UiElementType.Button, X = 300, Y = 160, Width = 120, Height = 40, Style = "Primary", Order = 12 });
            ui.Styles = new() { ["Primary"] = new UiNamedStyle { Background = "#FF112233" } };
            UiAssetDocument.Save(Path.Combine(folder, "Toolkit.ui.json"), ui);
            var dark = new UiAssetDocument { DesignWidth = 640, DesignHeight = 360 };
            dark.Styles = new() { ["Primary"] = new UiNamedStyle { Background = "#FF445566" } };
            UiAssetDocument.Save(Path.Combine(folder, "Dark.ui.json"), dark);
            ResourceCatalog.Invalidate(project.RootPath);

            InputState input = new();
            IGameContext? oldGame = PgslCommands.ActiveGameContext;
            string oldPath = PgslCommands.ProjectPath;
            var surface = new PgslRecordingDrawSurface();
            PgslContext context = new() { RoomWidth = 640, RoomHeight = 360, DrawSurface = surface };
            PgslContext? oldContext = PgslCommands.BindContext(context);
            PgslCommands.ActiveGameContext = new NullGameContext { Input = input, ProjectPath = project.RootPath };
            PgslCommands.ProjectPath = project.RootPath;
            void Press(Key key) { input.NextFrame(); input.OnKeyDown(key); PgslCommands.UiUpdate(Ui); input.NextFrame(); input.OnKeyUp(key); }
            try
            {
                // Laid out one under another inside the list (5 px in, 10 apart); the third is below what the list shows.
                HeadlessHarness.Assert(PgslCommands.UiHitTest(Ui, 30, 30) == "Item1" && PgslCommands.UiHitTest(Ui, 30, 80) == "Item2",
                    $"The list's buttons are not laid out: {PgslCommands.UiHitTest(Ui, 30, 30)}, {PgslCommands.UiHitTest(Ui, 30, 80)}.");
                HeadlessHarness.Assert(PgslCommands.UiHitTest(Ui, 30, 130) == string.Empty, "A button the list does not show could be hit.");

                // The wheel scrolls the list under the pointer.
                input.OnMouseMove(60, 60); input.OnWheel(-1); PgslCommands.UiUpdate(Ui);
                HeadlessHarness.Assert(PgslCommands.UiGetScroll(Ui, "List") == 48 && PgslCommands.UiHitTest(Ui, 30, 82) == "Item3",
                    $"The wheel scrolled the list to {PgslCommands.UiGetScroll(Ui, "List")} (expected 48), showing {PgslCommands.UiHitTest(Ui, 30, 82)}.");

                // Keys move the focus down the list, scrolling it into view, and Enter presses it.
                input.OnMouseMove(600, 340);
                PgslCommands.UiSetFocus(Ui, "Item1");
                for (int i = 0; i < 4; i++) Press(Key.Down);
                HeadlessHarness.Assert(PgslCommands.UiGetFocused(Ui) == "Item5" && PgslCommands.UiGetScroll(Ui, "List") == 145,
                    $"Four presses of Down focused {PgslCommands.UiGetFocused(Ui)} at scroll {PgslCommands.UiGetScroll(Ui, "List")} (expected Item5 at 145).");
                Press(Key.Enter);
                HeadlessHarness.Assert(PgslCommands.UiClicked(Ui, "Item5"), "Enter did not press the focused button.");
                Press(Key.Right);
                HeadlessHarness.Assert(PgslCommands.UiGetFocused(Ui) == "Play", "Right did not move the focus to the nearest button that way: " + PgslCommands.UiGetFocused(Ui));

                // Left and right move a focused slider by its step.
                PgslCommands.UiSetFocus(Ui, "Volume");
                Press(Key.Right); Press(Key.Left); Press(Key.Left);
                HeadlessHarness.Assert(PgslCommands.UiGetValue(Ui, "Volume") == 4, "The keys moved the slider to " + PgslCommands.UiGetValue(Ui, "Volume") + ", not 4.");

                // A click gives the text box the focus; it takes typing up to 8 characters, Backspace, and Enter.
                input.NextFrame(); input.OnMouseMove(350, 40); input.OnMouseDown(MouseButton.Left); PgslCommands.UiUpdate(Ui);
                input.NextFrame(); input.OnMouseUp(MouseButton.Left); PgslCommands.UiUpdate(Ui);
                foreach (char c in "Ada") input.OnChar(c);
                PgslCommands.UiUpdate(Ui);
                Press(Key.Backspace);
                foreach (char c in "123456789") input.OnChar(c);
                PgslCommands.UiUpdate(Ui);
                Press(Key.Enter);
                HeadlessHarness.Assert(PgslCommands.UiGetFocused(Ui) == "Name" && PgslCommands.UiGetText(Ui, "Name") == "Ad123456" && PgslCommands.UiSubmitted(Ui, "Name"),
                    $"The text box holds '{PgslCommands.UiGetText(Ui, "Name")}' with focus on {PgslCommands.UiGetFocused(Ui)} (expected 'Ad123456', submitted).");

                // The named style colours the button; the theme swaps it.
                bool Drew(int r, int g, int b) => surface.Rectangles.Any(rect => rect.Filled && rect.X == 300 && rect.Y == 160
                    && rect.Color.R == r && rect.Color.G == g && rect.Color.B == b);
                surface.Rectangles.Clear(); PgslCommands.DrawUi(Ui);
                bool own = Drew(0x11, 0x22, 0x33);
                PgslCommands.UiSetTheme(Ui, "Dark");
                surface.Rectangles.Clear(); PgslCommands.DrawUi(Ui);
                bool themed = Drew(0x44, 0x55, 0x66);
                HeadlessHarness.Assert(own && themed, $"The Play button's style was drawn {own} and its theme {themed}.");
            }
            finally
            {
                PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath;
            }
        });
    }
}

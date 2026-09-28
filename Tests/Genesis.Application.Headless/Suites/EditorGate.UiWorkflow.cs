using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void UiNoviceWorkflow(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ResourceService resources = fixture.Resources(fixture.Blank);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(fixture.Blank, ResourceKind.UserInterface), ResourceKind.UserInterface, "Novice menu");
        using Form host = GateSuite.NewHost(1280, 780);
        using UiEditorControl editor = new(path, fixture.Blank.RootPath);
        host.Controls.Add(editor); GateSuite.ShowHost(host);
        string? created = null;
        HeadlessHarness.Step("empty UI starts with a discoverable HUD/menu workflow and one history entry", () =>
        {
            HeadlessHarness.Assert(SurfaceControls(editor).Single(control => control.Name == "UiStartingGuide").Visible
                && !editor.IsDirty && editor.CommandBar.IsSaveVisible && editor.CommandBar.IsDocumentStateVisible,
                "An empty UI has no visible starting guidance or cannot retain clean/pinned document controls.");
            string[] primary = editor.CommandBar.Items.Cast<ToolStripItem>().Where(item => item.Available && item.Alignment != ToolStripItemAlignment.Right)
                .Select(item => item.Text ?? string.Empty).ToArray();
            HeadlessHarness.Assert(primary.SequenceEqual(new[] { "Design", "Add element", "Use in game", "Options" }),
                "The UI primary toolbar duplicates document or element actions: " + string.Join(',', primary));
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "UiStarterMenu").PerformClick();
            HeadlessHarness.Assert(editor.Document.Elements.Count == 4 && editor.SelectElement("Play") && editor.IsDirty,
                "The actual title-menu starting button did not add an editable layout.");
            editor.Undo(); HeadlessHarness.Assert(editor.Document.Elements.Count == 0, "Starting a menu cannot be undone as one edit.");
            editor.Redo(); HeadlessHarness.Assert(editor.Document.Elements.Count == 4 && editor.SelectElement("Play"), "Starting-layout redo lost its elements or selection.");
            bool rejected = false;
            try { editor.ApplyStartingLayout("HUD"); } catch (InvalidOperationException) { rejected = true; }
            HeadlessHarness.Assert(rejected && editor.Document.Elements.Count == 4, "A starting layout replaced existing work.");
            editor.TryApplyInspectorValue("Ui.Font", "Consolas"); editor.TryApplyInspectorValue("Ui.FontSize", 28);
            editor.Save();
            using UiEditorControl reopened = new(path, fixture.Blank.RootPath);
            HeadlessHarness.Assert(!reopened.IsDirty && reopened.Document.Elements.Single(element => element.Id == "Play").Font == "Consolas",
                "The starting layout or contextual edits did not save/reopen cleanly.");
            foreach (string preset in new[] { "HUD", "Pause" })
            {
                string scratchPath = resources.CreateResource(ResourceFolderPolicy.RootFor(fixture.Blank, ResourceKind.UserInterface), ResourceKind.UserInterface, "Starting " + preset);
                using UiEditorControl scratch = new(scratchPath, fixture.Blank.RootPath);
                scratch.ApplyStartingLayout(preset); scratch.Save();
                UiAssetDocument saved = UiAssetDocument.Load(scratchPath);
                HeadlessHarness.Assert(preset == "HUD" ? saved.Elements.Any(element => element.Id == "Health" && element.Maximum == 100)
                    : saved.Elements.Any(element => element.Id == "Resume" && element.Type == UiElementType.Button), "A starting example was not saved as real editable elements.");
            }
        });
        HeadlessHarness.Step("corner resizing retains the top-left for every anchor and edits Stretch insets with one undo", () =>
        {
            editor.SelectElement("Menu");
            Control canvas = SurfaceControls(editor).Single(control => control.GetType().Name == "UiDesignCanvas");
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            RectangleF CanvasRect() => (RectangleF)canvas.GetType().GetMethod("CanvasRect", flags)!.Invoke(canvas, null)!;
            RectangleF ElementRect() => ((Dictionary<string, RectangleF>)canvas.GetType().GetMethod("BuildElementRects", flags)!.Invoke(canvas, [CanvasRect()])!)["Menu"];
            void Mouse(string method, Point point) => canvas.GetType().GetMethod(method, flags)!.Invoke(canvas,
                [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
            foreach (UiAnchor anchor in Enum.GetValues<UiAnchor>())
            {
                editor.TryApplyInspectorValue("Ui.Anchor", anchor.ToString());
                editor.TryApplyInspectorValue("Ui.X", 40); editor.TryApplyInspectorValue("Ui.Y", 50);
                editor.TryApplyInspectorValue("Ui.Width", anchor == UiAnchor.Stretch ? 180 : 440);
                editor.TryApplyInspectorValue("Ui.Height", anchor == UiAnchor.Stretch ? 150 : 320);
                string before = UiAssetDocument.Serialize(editor.Document);
                RectangleF original = ElementRect(); Point corner = new((int)original.Right + 2, (int)original.Bottom + 2);
                Mouse("OnMouseDown", corner); Mouse("OnMouseMove", new Point(corner.X + 18, corner.Y + 12)); Mouse("OnMouseUp", new Point(corner.X + 18, corner.Y + 12));
                RectangleF resized = ElementRect();
                HeadlessHarness.Assert(Math.Abs(resized.X - original.X) < .01 && Math.Abs(resized.Y - original.Y) < .01
                    && Math.Abs(resized.Width - original.Width - 18) < .01 && Math.Abs(resized.Height - original.Height - 12) < .01,
                    "Corner resize moved the anchored top-left or reversed the dragged edge for " + anchor + ": " + original + " → " + resized);
                editor.Undo(); HeadlessHarness.Assert(UiAssetDocument.Serialize(editor.Document) == before, "An anchored resize was not one undoable gesture: " + anchor);
                editor.Redo(); HeadlessHarness.Assert(Math.Abs(ElementRect().Width - resized.Width) < .01, "Resize redo lost its anchored geometry.");
                editor.Document.Validate();
            }
            editor.TryApplyInspectorValue("Ui.Anchor", "Center"); editor.TryApplyInspectorValue("Ui.X", 0); editor.TryApplyInspectorValue("Ui.Y", 0);
            editor.TryApplyInspectorValue("Ui.Width", 440); editor.TryApplyInspectorValue("Ui.Height", 320); editor.Save();
        });
        HeadlessHarness.Step("Use in game creates normal saved Object events without replacing existing resources", () =>
        {
            editor.OpenLinkedResourceRequested += (_, resourcePath) => created = resourcePath;
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
            SurfaceControls(editor).OfType<TextBox>().Single(input => input.Name == "UiObjectName").Text = "Novice GUI controller";
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "UiCreateObject").PerformClick();
            HeadlessHarness.Assert(created is not null && File.Exists(created), "The actual Use in game button did not create or open its Object.");
            Dictionary<string, string> events = ObjectEventStore.Load(created!);
            HeadlessHarness.Assert(events.Count == 3 && events["DrawGui"].Contains("DrawUi(", StringComparison.Ordinal)
                && events["Step"].Contains("UiMouseX()", StringComparison.Ordinal), "The created Object has no executable draw/interaction events.");
            byte[] original = File.ReadAllBytes(created!);
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "UiCreateObject").PerformClick();
            HeadlessHarness.Assert(File.ReadAllBytes(created!).SequenceEqual(original)
                && SurfaceControls(editor).OfType<Label>().Single(label => label.Name == "UiCreateResult").Text.Contains("already", StringComparison.OrdinalIgnoreCase),
                "Creating another GUI Object with the same name replaced work or failed silently.");
            string capture = "ui-use-in-game.png";
            ctx.Report.Images.Add(ImageResult.From("UI Use in game", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
        });
        HeadlessHarness.Step("the created GUI Object executes real draw/click events and saved layout refresh on all five backends", () =>
        {
            using Form gameHost = GateSuite.NewHost(640, 360);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            gameHost.Controls.Add(viewport); GateSuite.ShowHost(gameHost);
            VMEngine.Initialize();
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                editor.SelectElement("Title"); editor.TryApplyInspectorValue("Ui.Text", "My adventure"); editor.Save();
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using (Bitmap? warm = viewport.ReadbackFrameToBitmap(3)) { }
                var renderer = viewport.Renderer ?? throw new InvalidOperationException("No GUI renderer.");
                string expected = backend.Backend switch { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
                HeadlessHarness.Assert(renderer.BackendName == expected, "The GUI used another backend.");
                RoomAsset room = RoomAsset.Create("GUI Room", RoomDimension.TwoD); room.Settings.Width = 4096; room.Settings.Height = 2048;
                RoomNode node = new() { Kind = RoomNodeKind.GameObject, Name = "GUI controller", Transform = new() { X = 1900, Y = 1700 },
                    GameObject = new() { Prefab = ResourceNames.Name(fixture.Blank.RootPath, created!) } };
                room.Nodes.Add(node);
                using RuntimeScene scene = new("Actual GUI workflow") { Input = new InputState() };
                ProjectGameContext game = new(fixture.Blank.RootPath, scene, renderer, null, room, null);
                ScriptHostSystem scripts = new(); scripts.SetContext(game);
                var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
                PgslContext? oldContext = PgslCommands.BindContext(new PgslContext());
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = fixture.Blank.RootPath;
                Action<Genesis.Shared.Interfaces.IRenderController> draw = current =>
                {
                    current.Clear(.03f, .04f, .05f); current.Set3DFrameActive(false);
                    current.SetCamera2D(current.PixelWidth / 2f, current.PixelHeight / 2f, 1, 0);
                    scripts.DispatchPgslGuiDraw(current, null);
                };
                try
                {
                    new RoomSceneBuilder(fixture.Blank.RootPath, scripts).Build(scene, room);
                    PgslBehavior behavior = scripts.Instances.OfType<PgslBehavior>().Single();
                    HeadlessHarness.Assert(behavior.HasGuiDrawScript && scripts.RecentDiagnostics.Count == 0, "The generated GUI Object failed to compile Create/Step/Draw GUI: " + string.Join(';', scripts.RecentDiagnostics));
                    scene.Input.OnMouseMove(300, 180); scene.Input.OnMouseDown(MouseButton.Left); scripts.Update(1f / 60);
                    scene.Input.NextFrame(); scene.Input.OnMouseUp(MouseButton.Left);
                    HeadlessHarness.Assert(Convert.ToString(behavior.GetVariablesSnapshot()["uiLastHit"]) == "Play",
                        "The real Step event missed its GUI button in a larger Room: hit=" + behavior.GetVariablesSnapshot()["uiLastHit"]
                        + "; viewport=" + renderer.PixelWidth + "x" + renderer.PixelHeight + "; mouse=" + PgslCommands.UiMouseX() + "," + PgslCommands.UiMouseY()
                        + "; direct=" + PgslCommands.UiHitTest("Novice menu", 300, 180) + "; faults=" + string.Join(';', scripts.RecentDiagnostics));
                    PgslRecordingDrawSurface before = new(); scripts.DispatchPgslGuiDraw(null, null, before);
                    HeadlessHarness.Assert(before.Texts.Any(call => call.Text == "Clicked") && before.Texts.Any(call => call.Text == "My adventure"),
                        "Actual click/draw execution failed to update the button.");
                    editor.TryApplyInspectorValue("Ui.Text", "Saved live title"); editor.Save();
                    PgslRecordingDrawSurface live = new(); scripts.DispatchPgslGuiDraw(null, null, live);
                    HeadlessHarness.Assert(live.Texts.Any(call => call.Text == "Saved live title") && live.Texts.Any(call => call.Text == "Clicked"),
                        "A UI Editor Save did not refresh live gameplay or lost the instance's click override.");
                    byte[] valid = File.ReadAllBytes(path); File.WriteAllText(path, "{ broken");
                    PgslRecordingDrawSurface retained = new(); scripts.DispatchPgslGuiDraw(null, null, retained);
                    HeadlessHarness.Assert(retained.Texts.Any(call => call.Text == "Saved live title"), "Invalid live UI source discarded the last good layout.");
                    File.WriteAllBytes(path, valid);
                    viewport.OnRender += draw;
                    using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
                    HeadlessHarness.Assert(frame is not null && viewport.RenderFaultCount == 0, "The GUI physical frame failed: " + viewport.LastRenderException);
                    Color panel = frame!.GetPixel(230, 130), outside = frame.GetPixel(20, 20);
                    HeadlessHarness.Assert(panel.R > outside.R + 8 && panel.B > outside.B + 15,
                        "The real GUI drew in Room coordinates instead of at its centred window location: " + panel + "; " + outside);
                    int whites = 0;
                    for (int y = 162; y < 194; y++) for (int x = 226; x < 414; x++)
                    { Color pixel = frame.GetPixel(x, y); if (pixel.R > 180 && pixel.G > 180 && pixel.B > 180) whites++; }
                    HeadlessHarness.Assert(whites > 40, "Clicked text has no physical pixels on " + expected);
                    string capture = "ui-created-object-" + backend.ShortName.ToLowerInvariant() + ".png";
                    frame.Save(Path.Combine(ctx.Captures, capture)); ctx.Report.Images.Add(ImageResult.From("Actual GUI Object · " + expected, capture, VisualCapture.Measure(frame)));
                    HeadlessHarness.Assert(scripts.RecentDiagnostics.Count == 0, "Runtime GUI events reported faults: " + string.Join(';', scripts.RecentDiagnostics));
                }
                finally { viewport.OnRender -= draw; PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
            }
        });
    }
}

using System.Text;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class UiEditorControl
{
    private EditorCommandBar? _commandBar;
    private FlowLayoutPanel? _workflowGuide;
    private bool _showWorkflowGuide;
    private ToolStripButton? _designButton, _gameButton;
    public EditorCommandBar CommandBar => _commandBar!;

    private ToolStrip BuildUiWorkflowToolbar()
    {
        EditorCommandBar bar = _commandBar = EditorChrome.MakeToolbar();
        _designButton = EditorChrome.ToolButton("Design", "Arrange UI elements on the canvas", ShowUiDesign, toggle: true);
        ToolStripDropDownButton add = new("Add element") { ForeColor = EditorChrome.Text };
        foreach ((UiElementType type, string caption) in new[]
        {
            (UiElementType.Panel, "Panel — group and background"), (UiElementType.Text, "Text — score or title"),
            (UiElementType.Image, "Image — saved sprite artwork"), (UiElementType.Button, "Button — clickable menu item"),
            (UiElementType.ProgressBar, "Progress bar — health or loading"),
            (UiElementType.Slider, "Slider — volume or brightness"), (UiElementType.Toggle, "Toggle — on/off option"),
        }) add.DropDownItems.Add(EditorDocumentMenuChrome.Item(caption, "Add and select this element", () => AddElement(type)));
        _gameButton = EditorChrome.ToolButton("Use in game", "Create a saved GUI Object and learn how to draw and interact", ShowUiGameGuide, toggle: true);
        ToolStripDropDownButton options = new("Options") { ForeColor = EditorChrome.Text };
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Starting layouts", "Start an empty UI with a HUD or menu", ShowUiStartingGuide));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Elements", "Show or hide the element hierarchy", () => { ShowUiDesign(); ToggleSidebar(true); }));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Inspector", "Show or hide properties for the selected element", () => { ShowUiDesign(); ToggleSidebar(false); }));
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Undo", "Undo the last UI edit (Ctrl+Z)", Undo));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Redo", "Redo the last UI edit (Ctrl+Y)", Redo));
        bar.Items.AddRange([_designButton, add, _gameButton, options]);
        bar.Items.Add(EditorChrome.ToolButton("Save", "Save this UI (Ctrl+S)", Save));
        return bar;
    }

    private ToolStrip BuildUiCanvasToolbar()
    {
        ToolStrip bar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden,
            BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text, Name = "UiCanvasToolbar" };
        bar.Items.Add(new ToolStripLabel("Canvas"));
        ToolStripComboBox resolutions = _resolutions = new() { AutoSize = false, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        resolutions.Items.AddRange(["1280 × 720", "1920 × 1080", "2560 × 1440", "1080 × 1920"]);
        resolutions.ComboBox.FontChanged += (_, _) =>
        {
            int width = resolutions.Items.Cast<string>().Max(text => TextRenderer.MeasureText(text, resolutions.ComboBox.Font).Width) + 36;
            resolutions.Width = Math.Max(150, width); resolutions.DropDownWidth = resolutions.Width;
        };
        resolutions.SelectedIndex = ResolutionIndex();
        resolutions.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || resolutions.SelectedIndex < 0 || resolutions.SelectedIndex >= _resolutionSizes.Count) return;
            Size size = _resolutionSizes[resolutions.SelectedIndex];
            _document.DesignWidth = size.Width; _document.DesignHeight = size.Height;
            _canvas.FitView(); JournalChange("Change UI canvas size");
        };
        bar.Items.Add(resolutions);
        bar.Items.Add(EditorChrome.ToolButton("Fit", "Fit the complete UI canvas", _canvas.FitView));
        return bar;
    }

    private void InitializeUiWorkflow()
    {
        if (_commandBar?.HistoryCommand is { } history) history.Visible = false;
        if (_document.Elements.Count == 0) ShowUiStartingGuide(); else RefreshUiWorkflow();
    }

    private void ShowUiDesign()
    {
        _showWorkflowGuide = false;
        if (_workflowGuide is not null) _workflowGuide.Visible = false;
        _uiWorkflow?.SetCurrent("Design");
        RefreshUiWorkflow(); ApplyResponsiveLayout();
    }

    private FlowLayoutPanel PrepareUiGuide(string name)
    {
        if (_workflowGuide is null)
        {
            _workflowGuide = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = EditorChrome.Surface,
                Padding = new Padding(16, 12, 16, 32) };
            _workspace.Controls.Add(_workflowGuide);
            _workflowGuide.SizeChanged += (_, _) => LayoutUiGuide();
        }
        foreach (Control child in _workflowGuide.Controls.Cast<Control>().ToArray()) child.Dispose();
        _workflowGuide.Name = name; _workflowGuide.AutoScrollPosition = Point.Empty;
        _showWorkflowGuide = true; _workflowGuide.Visible = true; _workflowGuide.BringToFront();
        ApplyResponsiveLayout(); RefreshUiWorkflow(); return _workflowGuide;
    }

    private static Label UiGuideText(string text, bool heading = false) => new()
    {
        Text = text, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted,
        Tag = heading ? "UiGuideHeading" : "UiGuideText", Margin = new Padding(0, 0, 0, 10),
    };

    private static Button UiGuideButton(string caption, string name, Action action)
    {
        Button button = new() { Text = caption, Name = name, Margin = new Padding(0, 0, 0, 10) };
        EditorChrome.StyleField(button); button.Click += (_, _) => action(); return button;
    }

    private void ShowUiStartingGuide()
    {
        FlowLayoutPanel guide = PrepareUiGuide("UiStartingGuide");
        _uiWorkflow?.SetCurrent("Start");
        guide.Controls.Add(UiGuideText("Build a HUD or menu", true));
        guide.Controls.Add(UiGuideText("The UI Editor creates screen layouts for gameplay: score, health, buttons, menus and saved Image artwork. Find it under Assets → New → User Interface. Start here, then arrange elements on the canvas and use the selected element's Inspector."));
        guide.Controls.Add(UiGuideText(_document.Elements.Count == 0 ? "Choose a starting layout" : "This UI already has elements. Starting layouts need an empty resource; create a new User Interface to try one.", true));
        foreach ((string preset, string caption) in new[] { ("HUD", "Score and health HUD"), ("Menu", "Title menu"), ("Pause", "Pause menu") })
        {
            Button create = UiGuideButton(caption, "UiStarter" + preset, () => ApplyStartingLayout(preset));
            create.Enabled = !_loadFailed && _document.Elements.Count == 0; guide.Controls.Add(create);
        }
        guide.Controls.Add(UiGuideButton("Open blank canvas", "UiOpenDesign", ShowUiDesign));
        guide.Controls.Add(UiGuideText("1. Design", true));
        guide.Controls.Add(UiGuideText("Add element creates Text, Image, Button, Panel, Progress bar, Slider or Toggle. Drag an element to move it; drag its corner handle to resize. Use the mouse wheel to zoom, middle drag to pan, and Fit to see the whole canvas."));
        guide.Controls.Add(UiGuideText("2. Anchor and style", true));
        guide.Controls.Add(UiGuideText("The Inspector shows only fields that affect the selected element. Parent groups elements; Anchor positions them within that parent. Stretch uses left/top position and right/bottom insets. Image picks saved artwork, so Image Editor saves update the preview. Corner radius, border, gradient fill, text alignment and letter spacing style a box; Look (state) sets its hover, pressed, selected and disabled colours."));
        guide.Controls.Add(UiGuideText("3. Use in game", true));
        guide.Controls.Add(UiGuideText("Save, then choose Use in game. Create a normal GUI Object, place one instance in a Room, and Run. Buttons need gameplay actions; the created Object includes a working click example that you can edit in its Step event."));
        LayoutUiGuide();
    }

    public void ApplyStartingLayout(string preset)
    {
        if (_loadFailed) throw new InvalidOperationException("Repair the original UI resource before changing its layout.");
        if (_document.Elements.Count != 0) throw new InvalidOperationException("Starting layouts need an empty User Interface; existing elements were retained.");
        if (preset is not ("HUD" or "Menu" or "Pause")) throw new ArgumentException("Choose HUD, Menu or Pause.", nameof(preset));
        if (preset == "HUD")
        {
            _document.Elements.AddRange([
                new() { Id = "Hud", Type = UiElementType.Panel, X = 24, Y = 24, Width = 360, Height = 120 },
                new() { Id = "Score", ParentId = "Hud", Type = UiElementType.Text, X = 16, Y = 12, Width = 328, Height = 36, Text = "SCORE  0", FontSize = 24 },
                new() { Id = "Health", ParentId = "Hud", Type = UiElementType.ProgressBar, X = 16, Y = 64, Width = 328, Height = 28, Value = 100, Maximum = 100, Accent = "#FF40CC88" },
            ]);
        }
        else
        {
            bool pause = preset == "Pause";
            _document.Elements.AddRange([
                new() { Id = "Menu", Type = UiElementType.Panel, Anchor = UiAnchor.Center, X = 0, Y = 0, Width = 440, Height = 320, Background = "#EE161B22" },
                new() { Id = "Title", ParentId = "Menu", Type = UiElementType.Text, X = 32, Y = 28, Width = 376, Height = 60, Text = pause ? "Paused" : "My adventure", FontSize = 36 },
                new() { Id = pause ? "Resume" : "Play", ParentId = "Menu", Type = UiElementType.Button, X = 32, Y = 124, Width = 376, Height = 64, Text = pause ? "Resume" : "Play", FontSize = 24 },
                new() { Id = "Back", ParentId = "Menu", Type = UiElementType.Button, X = 32, Y = 216, Width = 376, Height = 64, Text = "Back", FontSize = 24 },
            ]);
        }
        NormalizeOrders(); _canvas.SelectedElement = _document.Elements.First();
        RefreshHierarchy(); RefreshInspector(); JournalChange("Add " + preset + " starting layout");
        ShowUiDesign(); _canvas.FitView();
    }

    private void ShowUiGameGuide()
    {
        FlowLayoutPanel guide = PrepareUiGuide("UiUseInGame");
        _uiWorkflow?.SetCurrent("UseInGame");
        guide.Controls.Add(UiGuideButton("Back to Design", "UiBackToDesign", ShowUiDesign));
        guide.Controls.Add(UiGuideText("Use this UI in gameplay", true));
        guide.Controls.Add(UiGuideText("1. Save a layout with at least one element. The canvas is your design resolution; gameplay scales it to the GUI window. Image bindings use saved project resources, so artwork and UI saves update the running draw pass."));
        guide.Controls.Add(UiGuideText("2. Create a GUI Object", true));
        TextBox name = new() { Name = "UiObjectName", Text = ResourceDisplayName.Format(ResourcePath) + " GUI" };
        EditorChrome.StyleField(name); guide.Controls.Add(Field("Object name", name));
        Label result = UiGuideText(string.Empty); result.Name = "UiCreateResult";
        guide.Controls.Add(UiGuideButton("Create GUI Object", "UiCreateObject", () =>
        {
            try { result.Text = "Created " + ResourceDisplayName.Format(CreateUiObject(name.Text)) + ". Place one instance in a Room and Run."; }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException) { result.Text = exception.Message; }
            LayoutUiGuide();
        }));
        guide.Controls.Add(result);
        guide.Controls.Add(UiGuideText("3. Place one instance in a Room and Run", true));
        guide.Controls.Add(UiGuideText("Drag the new Object from Assets into the Room. Its Draw GUI event draws this saved layout independently of the world camera. Its Step event records the last clicked element in uiLastHit. For the first Button, it changes the label to Clicked as a working example; replace that action with starting your game or resuming play. A layout alone does not start or pause a game."));
        guide.Controls.Add(UiGuideText("For an existing Object", true));
        TextBox example = new() { Name = "UiGameplayExample", ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Horizontal,
            Text = $"// Draw GUI event\r\nDrawUi({UiPgslString(ResourceNames.Name(ProjectRoot, ResourcePath))});\r\n// Step event: UiHitTest(asset, UiMouseX(), UiMouseY())\r\n// Update: UiSetText(asset, id, text) / UiSetValue(asset, id, value)" };
        EditorChrome.StyleField(example); guide.Controls.Add(example);
        guide.Controls.Add(UiGuideText("Edit the created Object's events to add gameplay. UiSetVisible hides an element and its children; UiSetValue updates health/progress; UiSetText updates score/title. IDs come from the element Inspector. These overrides belong to that Object instance and survive saved layout refreshes. Duplicate GUI Objects would draw the layout twice."));
        LayoutUiGuide();
    }

    private static string UiPgslString(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    public string CreateUiObject(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        if (_document.Elements.Count == 0) throw new InvalidOperationException("Add an element or choose a starting layout before creating a GUI Object.");
        if (File.Exists(ProjectAssetIndex.ResolveReference(ProjectRoot, validName, ResourceKind.GameObject)))
            throw new InvalidOperationException("An Object named '" + validName + "' already exists. Choose another name or place the existing Object in your Room.");
        Save();
        string asset = UiPgslString(ResourceNames.Name(ProjectRoot, ResourcePath));
        string button = _document.Elements.FirstOrDefault(element => element.Type == UiElementType.Button)?.Id ?? string.Empty;
        string step = $"if (MousePressed(MbLeft)) {{\n    uiLastHit = UiHitTest({asset}, UiMouseX(), UiMouseY());\n";
        if (button.Length > 0) step += $"    if (uiLastHit == {UiPgslString(button)}) {{ UiSetText({asset}, {UiPgslString(button)}, \"Clicked\"); }}\n";
        step += "}";
        JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
        document["schemaVersion"] = 3; document["dimension"] = "TwoD"; document["solid"] = false;
        new ObjectCompositionModel(document).SetProperty("ScriptComponent", "ScriptClass", validName);
        document["events"] = new JArray("Create", "Step", "DrawGui");
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, ResourceReferenceRewriter.Normalize(ProjectRoot, path, document.ToString(Newtonsoft.Json.Formatting.Indented)), new UTF8Encoding(false));
        ObjectEventStore.Save(path, new Dictionary<string, string>
        {
            ["Create"] = "uiLastHit = \"\";", ["Step"] = step, ["DrawGui"] = $"DrawUi({asset});",
        });
        ResourceNames.Invalidate(ProjectRoot); RequestOpenLinkedResource(path); return path;
    }

    private void RefreshUiWorkflow()
    {
        if (_designButton is not null) _designButton.Checked = !_showWorkflowGuide;
        if (_gameButton is not null) _gameButton.Checked = _showWorkflowGuide && _workflowGuide?.Name == "UiUseInGame";
        if (_commandBar?.HistoryCommand is { } history) history.Visible = false;
        UpdateCanvasStatus();
    }

    private void LayoutUiGuide()
    {
        if (_workflowGuide is not { } guide || !guide.Visible) return;
        int width = Math.Max(100, guide.ClientSize.Width - guide.Padding.Horizontal - 24);
        foreach (Control field in guide.Controls)
        {
            field.Width = width;
            if (field is Label label)
            {
                label.Font = label.Tag as string == "UiGuideHeading" ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
            }
            else if (field is Button) { field.Font = EditorChrome.BaseFont; field.Height = field.Font.Height + 18; }
            else if (field is TextBox { Multiline: true }) { field.Font = EditorChrome.CodeFont; field.Height = field.Font.Height * 5 + 18; }
            else if (field.Tag is UiFieldLayout or UiPairLayout) LayoutProperty(field, width);
        }
        guide.AutoScrollMinSize = new Size(0, guide.Controls.Cast<Control>().Where(control => control.Visible)
            .Select(control => control.Bottom - guide.AutoScrollPosition.Y + control.Margin.Bottom + guide.Padding.Bottom + 16).DefaultIfEmpty(0).Max());
    }
}

using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.UI;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

public partial class ModelViewerControl : IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private TableLayoutPanel? _viewerRoot;
    private readonly Label _workflowHint = new()
    {
        Name = "ModelWorkflowHint", Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6),
        Text = "1. Import a model, or Create Model. 2. Choose an animation below and Play. 3. Save and Use in game.",
    };
    private bool _workflowConfigured, _layingOutWorkflow, _showModelDetails, _sizingModelGuide;
    private float _sidebarLogicalWidth;
    private FlowLayoutPanel? _modelGameGuide;
    private WorkflowBar? _modelWorkflow;

    /// <summary>The guided steps shown under the command bar (replaces the old one-line hint).</summary>
    public WorkflowBar? ModelWorkflow => _modelWorkflow;
    private readonly Dictionary<Control, bool> _beforeGameGuide = [];
    // Font sizes are capped for readability, while section geometry still follows the
    // full interface scale. Never narrow a scaled section back to the capped font ratio.
    private float ModelInterfaceScale => Math.Max(1, DeviceDpi / 96f * Math.Max(
        EditorChrome.BaseFont.SizeInPoints / 9.5f,
        ModelDescendants(LeftPanel).OfType<CollapsibleSection>().Select(section => section.InterfaceScale).DefaultIfEmpty(1).Max()));

    public event EventHandler? InspectorStateChanged;

    private static EditorCommandBar MakeModelCommands()
    {
        EditorCommandBar bar = EditorChrome.MakeToolbar();
        bar.Name = "ModelViewerCommands"; bar.Dock = DockStyle.Fill;
        return bar;
    }

    private void ConfigureModelWorkflow()
    {
        if (_workflowConfigured) return;
        _workflowConfigured = true;
        bool composer = this is ModelEditorControl { IsComposer: true };
        ToolStripItem[] previous = Commands.Items.Cast<ToolStripItem>().ToArray();
        Commands.ResetItems();
        ToolStripDropDownButton options = new("Options") { Name = "ModelWorkflowOptions" };
        ToolStripDropDownItem viewing = Menus.Items.OfType<ToolStripDropDownButton>().Single(menu => menu.Text == "View");
        foreach (ToolStripDropDownButton menu in Menus.Items.OfType<ToolStripDropDownButton>().ToArray())
        {
            ToolStripDropDownItem destination = menu == viewing ? viewing : new ToolStripMenuItem(menu.Text);
            foreach (ToolStripItem item in menu.DropDownItems.Cast<ToolStripItem>().ToArray())
            {
                if (item.Text is "Save" or "Import Model…" or "Open Model Editor" or "Frame model"
                    || !composer && menu.Text == "Animation") { menu.DropDownItems.Remove(item); item.Dispose(); continue; }
                if (destination != menu) destination.DropDownItems.Add(item);
            }
            if (destination != viewing && destination.DropDownItems.Count > 0) options.DropDownItems.Add(destination);
        }
        viewing.Text = "View and camera";
        Menus.Visible = false;
        ToolStripItem import = previous.Single(item => item.Name == "ImportModelFiles");
        Commands.Items.Add(import);
        if (composer)
        {
            foreach (ToolStripItem item in previous.Where(item => item.Name?.StartsWith("ModelGizmo", StringComparison.Ordinal) == true))
                Commands.Items.Add(item);
            _workflowHint.Text = "1. Choose a tool on the left. 2. Work in the view. 3. Save and Use in game. Play animations below.";
        }
        else if (_openModelEditor is not null) Commands.Items.Add(_openModelEditor);
        ToolStripItem? selectionFrame = composer ? previous.FirstOrDefault(item => item.Text == "Frame  F") : null;
        if (selectionFrame is not null) { selectionFrame.Text = "Frame"; Commands.Items.Add(selectionFrame); }
        else Commands.Items.Add(EditorChrome.ToolButton("Frame", "Fit the model in the view (F)", FrameModel));
        ToolStripButton sprites = EditorChrome.ToolButton("2D sprites\u2026", "Convert to 2D sprites: render this model from every direction into an Image", ShowSpriteConversion);
        sprites.Name = "ModelConvertToSprites"; Commands.Items.Add(sprites);
        Commands.Items.Add(EditorChrome.ToolButton("Use in game", "Create an Object using this saved model and animation", ShowModelGameGuide));
        foreach (ToolStripItem item in previous)
        {
            if (item.Owner == Commands) continue;
            if (item is ToolStripDropDownButton || item == _spinButton) viewing.DropDownItems.Add(item);
            else item.Dispose();
        }
        options.DropDownItems.Add(viewing);
        if (composer)
        {
            ToolStripMenuItem details = new("Model details panel") { CheckOnClick = true };
            details.CheckedChanged += (_, _) => { _showModelDetails = details.Checked; ApplyInterfaceLayout(); };
            options.DropDownItems.Add(details);
        }
        Commands.Items.Add(options); Commands.BindDocument(this);
        DirtyChanged += (_, _) => InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        InstallModelWorkflowBar(import);
    }

    /// <summary>
    /// Puts the workflow bar in the hint's row of the root table, so the view keeps its height
    /// and the command bar stays directly inside the table.
    /// </summary>
    private void InstallModelWorkflowBar(ToolStripItem import)
    {
        if (_viewerRoot is null || _modelWorkflow is not null) return;
        _modelWorkflow = new WorkflowBar("ModelWorkflow", CreateModelWorkflowSteps(import))
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _viewerRoot.Controls.Remove(_workflowHint);
        _viewerRoot.Controls.Add(_modelWorkflow, 0, 2);
    }

    /// <summary>The viewer's steps; the Model editor replaces them with its tool pages.</summary>
    private protected virtual IReadOnlyList<WorkflowStep> CreateModelWorkflowSteps(ToolStripItem import) =>
    [
        new("Import", "Import", "Import a model file, or choose Edit to build one from shapes.", () => import.PerformClick()),
        new("Edit", "Edit", "Open the Model editor to shape, texture and rig this model.", RequestCompose),
        new("Animate", "Animate", "Choose an animation below and press Play to check it.", () => _clips.Focus()),
        new("UseInGame", "Use in game", "Create an Object that uses this model and animation.", ShowModelGameGuide),
    ];

    public override void ApplyInterfaceLayout()
    {
        if (_layingOutWorkflow || !_workflowConfigured || _viewerRoot is null) return;
        _layingOutWorkflow = true;
        try
        {
            float scale = ModelInterfaceScale;
            _viewerRoot.RowStyles[0].Height = 0;
            _viewerRoot.RowStyles[1].Height = Math.Max(44 * scale, EditorChrome.BaseFont.Height + 22);
            _workflowHint.Font = EditorChrome.SmallFont;
            _workflowHint.ForeColor = EditorChrome.Muted; _workflowHint.BackColor = EditorChrome.Surface;
            _viewerRoot.RowStyles[2].Height = _modelWorkflow is not null
                ? Math.Max(WorkflowBar.LogicalHeight * scale, EditorChrome.BaseFont.Height + 18)
                : TextRenderer.MeasureText(_workflowHint.Text, _workflowHint.Font,
                    new Size(Math.Max(120, ClientSize.Width - _workflowHint.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + _workflowHint.Padding.Vertical;
            _viewerRoot.RowStyles[4].Height = AnimationFrameCount > 0
                ? Math.Max(180, 120 + EditorChrome.BaseFont.Height * 4)
                : Math.Max(94 * scale, EditorChrome.BaseFont.Height * 2 + 48);
            _viewerRoot.RowStyles[5].Height = EditorChrome.SmallFont.Height + 14;
            bool composer = this is ModelEditorControl { IsComposer: true };
            float desired = (_sidebarLogicalWidth > 0 ? _sidebarLogicalWidth : composer ? 356 : 270) * scale;
            Body.ColumnStyles[0].Width = Math.Min(desired, Math.Max(200, Body.ClientSize.Width * (composer ? .6f : .45f)));
            if (Body.ColumnCount == 3)
            {
                // The gameplay guide spans every column. Position lookup can return that guide
                // while the details panel is hidden, so target the panel's own column instead.
                Control? details = Body.Controls.Cast<Control>().FirstOrDefault(control => control != _modelGameGuide
                    && Body.GetColumn(control) == 2 && Body.GetColumnSpan(control) == 1);
                if (details is not null) details.Visible = _showModelDetails && _modelGameGuide?.Visible != true;
                Body.ColumnStyles[2].Width = _showModelDetails ? Math.Min(292 * scale, Math.Max(200, Body.Width - 420)) : 0;
                if (_modelGameGuide?.Visible != true)
                {
                    LeftPanel.Visible = !_showModelDetails;
                    if (_showModelDetails) Body.ColumnStyles[0].Width = 0;
                }
            }
            _hierarchy.ItemHeight = _materials.ItemHeight = EditorChrome.BaseFont.Height + 12;
            _sourceInfo.Font = _details.Font = EditorChrome.BaseFont;
            LayoutSidebar();
            if (composer) ((ModelEditorControl)this).LayoutToolNavigation();
            foreach (FlowLayoutPanel page in ModelDescendants(LeftPanel).OfType<FlowLayoutPanel>())
            foreach (CollapsibleSection section in page.Controls.OfType<CollapsibleSection>())
                section.Width = Math.Max((int)(270 * scale), page.ClientSize.Width - page.Padding.Horizontal - 24);
            ToolStrip? transport = ModelDescendants(this).OfType<ToolStrip>().FirstOrDefault(strip => strip.Name == "ModelViewerTransport");
            if (transport is not null)
            {
                transport.Font = EditorChrome.BaseFont;
                _clips.Font = EditorChrome.BaseFont;
                _clips.Width = Math.Min((int)(320 * scale), Math.Max(120, transport.Width / 3));
                _clips.DropDownWidth = Math.Max(_clips.Width, (int)(320 * scale));
                if (transport.Parent is TableLayoutPanel playback)
                {
                    playback.RowStyles[0].Height = EditorChrome.BaseFont.Height + 24;
                    playback.ColumnStyles[1].Width = Math.Min(210 * scale, playback.Width * .3f);
                }
            }
            if (_modelGameGuide?.Visible == true) SizeModelGuide();
        }
        finally { _layingOutWorkflow = false; }
    }

    private protected void ShowModelGameGuide()
    {
        _modelWorkflow?.SetCurrent("UseInGame");
        _modelGameGuide ??= BuildModelGameGuide();
        foreach (TextBox code in _modelGameGuide.Controls.OfType<TextBox>().Where(control => control.Name == "ModelGameplayCode"))
            code.Text = ModelGameplaySource();
        if (_modelGameGuide.Visible && _modelGameGuide.Parent == Body) { SizeModelGuide(); return; }
        _beforeGameGuide.Clear();
        foreach (Control child in Body.Controls)
            if (child != _modelGameGuide) { _beforeGameGuide[child] = child.Visible; child.Visible = false; }
        if (_modelGameGuide.Parent != Body)
        {
            Body.Controls.Add(_modelGameGuide, 0, 0); Body.SetColumnSpan(_modelGameGuide, Body.ColumnCount);
        }
        _modelGameGuide.Visible = true; _modelGameGuide.BringToFront(); SizeModelGuide();
    }

    private FlowLayoutPanel BuildModelGameGuide()
    {
        FlowLayoutPanel page = new() { Name = "ModelUseInGame", Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), BackColor = EditorChrome.Surface };
        AddButton("Back to model", () =>
        {
            page.Visible = false;
            foreach ((Control control, bool visible) in _beforeGameGuide) control.Visible = visible;
            ApplyInterfaceLayout();
        }, "ModelBackToView");
        AddText("Use a saved model and animation", true);
        AddText("Models contain meshes, materials, a skeleton and animation clips. Two-dimensional sprites and sprite rigs are authored in Image Editor. This workflow creates a 3D Object for a 3D Room; it is optional for a 2D game.");
        AddText("1. Import or create, then save", true);
        AddText("Import / Replace keeps a project-owned copy of the model. Edit Model opens the modelling tools. Rig opens binding, poses and animation authoring. Choose a clip in the bottom timeline to inspect its actual movement.");
        AddText("2. Create an Object", true);
        TextBox name = new() { Name = "ModelObjectName", Text = ResourceNames.Name(ProjectRoot, ResourcePath) + " Object" };
        EditorChrome.StyleField(name); page.Controls.Add(name);
        AddText("The Object references this Model. The currently selected animation starts through its Create event. Save Model changes to update live Object/Room previews and gameplay.");
        Label result = AddText(string.Empty);
        AddButton("Create model Object", () =>
        {
            try { result.Text = "Created " + ResourceNames.Name(ProjectRoot, CreateModelObject(name.Text)) + ". Place it in a 3D Room, then Run."; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { result.Text = error.Message; }
            SizeModelGuide();
        }, "ModelCreateObject");
        AddText("3. Control it from PGSL", true);
        AddText("Put these commands in the Object's Create event. Use ModelAnimationSetSpeed, ModelAnimationSetTime or ModelAnimationStop in other events. Code mode offers type suggestions and the current argument signature below the code.");
        TextBox code = new() { Name = "ModelGameplayCode", ReadOnly = true, Multiline = true, WordWrap = false,
            ScrollBars = ScrollBars.Horizontal, Text = ModelGameplaySource() };
        EditorChrome.StyleField(code); page.Controls.Add(code);
        AddText("Use a saved resource name, not an external source filename. The Object and Room share the Model Editor's canonical meshes, rig and clips.");
        page.SizeChanged += (_, _) => SizeModelGuide();
        return page;

        Label AddText(string text, bool heading = false)
        {
            Label label = new() { Text = text, Name = heading ? "ModelGuideHeading" : "ModelGuideText", Margin = new Padding(0, 0, 0, 12) };
            page.Controls.Add(label); return label;
        }
        void AddButton(string text, Action clicked, string controlName)
        {
            Button button = new() { Text = text, Name = controlName }; EditorChrome.StyleField(button);
            button.Click += (_, _) => clicked(); page.Controls.Add(button);
        }
    }

    private void SizeModelGuide()
    {
        if (_modelGameGuide is null || _sizingModelGuide) return;
        _sizingModelGuide = true;
        try
        {
            int width = Math.Max(160, _modelGameGuide.ClientSize.Width - _modelGameGuide.Padding.Horizontal - 24);
            foreach (Control control in _modelGameGuide.Controls)
            {
                control.Width = width; control.Font = control.Name == "ModelGuideHeading" ? EditorChrome.HeadingFont : EditorChrome.BaseFont;
                if (control is Label label) label.Height = TextRenderer.MeasureText(label.Text, label.Font,
                    new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
                else if (control is TextBox { Multiline: true }) control.Height = control.Font.Height * 3 + 28;
                else control.Height = control.Font.Height + 18;
            }
        }
        finally { _sizingModelGuide = false; }
    }

    private string ModelGameplaySource() => "ModelSet(" + JsonSerializer.Serialize(ResourceNames.Name(ProjectRoot, ResourcePath)) + ");" + Environment.NewLine
        + (ActiveClip.Length == 0 ? "// Choose a clip in the Model timeline to start an animation." + Environment.NewLine
            : "ModelAnimationPlay(" + JsonSerializer.Serialize(ActiveClip) + ", true, 0.15);" + Environment.NewLine);

    public string CreateModelObject(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        if (!Asset.HasRenderableMeshes) throw new InvalidOperationException("Import or create model geometry before creating an Object.");
        Save();
        if (IsDirty) throw new InvalidOperationException("Save the Model before creating an Object.");
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        using ObjectEditorControl editor = new(path, ProjectRoot);
        editor.SetVisualDimension(true);
        editor.Composition.SetAsset("ModelRendererComponent", ResourceNames.Name(ProjectRoot, ResourcePath));
        editor.Composition.SetProperty("ModelAnimatorComponent", "ClipName", ActiveClip);
        editor.Composition.SetProperty("ModelAnimatorComponent", "Playing", false);
        editor.SetEventBody("Create", ModelGameplaySource()); editor.Save();
        RequestOpenLinkedResource(path); return path;
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
    [
        new("Model pivot", "Model.Pivot.X", "X", Asset.Pivot.Position.X),
        new("Model pivot", "Model.Pivot.Y", "Y", Asset.Pivot.Position.Y),
        new("Model pivot", "Model.Pivot.Z", "Z", Asset.Pivot.Position.Z),
        new("Model information", "Model.Meshes", "Meshes", Asset.Meshes.Count, true),
        new("Model information", "Model.Joints", "Joints", Asset.Rig.Bones.Count, true),
        new("Model information", "Model.Animations", "Animation clips", Asset.Animations.Count, true),
        new("Preview", "Preview.Animation", "Selected animation", ActiveClip.Length == 0 ? "None" : ActiveClip, true),
    ];

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) => TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        if (_importing || !propertyPath.StartsWith("Model.Pivot.", StringComparison.Ordinal)
            || !float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number)) return false;
        var before = Asset.Pivot.Position; var after = before;
        switch (propertyPath) { case "Model.Pivot.X": after.X = number; break; case "Model.Pivot.Y": after.Y = number; break; case "Model.Pivot.Z": after.Z = number; break; default: return false; }
        if (before == after) return true;
        void Set(System.Numerics.Vector3 position)
        {
            Asset.Pivot.Position = position; RefreshAssetPresentation(); OnMotionImported();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        }
        Set(after); PushEdit("Change model pivot", () => Set(after), () => Set(before)); return true;
    }

    private static IEnumerable<Control> ModelDescendants(Control control)
    {
        foreach (Control child in control.Controls) { yield return child; foreach (Control nested in ModelDescendants(child)) yield return nested; }
    }
}

using System.Text;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ShaderEditorControl
{
    private Panel? _shaderQuickSetup;
    private FlowLayoutPanel? _shaderQuickHeader;
    private ThemedComboBox? _shaderQuickPreset;
    private Label? _shaderQuickDescription;
    private Label? _shaderQuickTargetLabel;
    private FlowLayoutPanel? _shaderGameGuide;
    private ToolStripButton? _shaderQuickButton;
    private ToolStripButton? _shaderCodeButton;
    private bool _syncingShaderQuick;
    private string _savedShaderSnapshot = string.Empty;

    private Panel BuildShaderQuickSetup()
    {
        Panel page = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = EditorChrome.Surface, Name = "ShaderQuickSetup" };
        _shaderQuickSetup = page;
        _shaderQuickHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = EditorChrome.Surface, Padding = new Padding(10, 8, 10, 0),
        };
        ShaderWorkflowText(_shaderQuickHeader, "Quick setup", heading: true);
        ShaderWorkflowText(_shaderQuickHeader, "Shaders change appearance. Choose an effect, preview it on a resource, then tune the values below. Save and Use in game when ready.");
        _shaderQuickPreset = new ThemedComboBox { Name = "ShaderQuickPreset", AccessibleName = "Starting shader effect" };
        EditorChrome.StyleField(_shaderQuickPreset);
        _shaderQuickPreset.Items.Add("Custom code");
        _shaderQuickPreset.Items.AddRange(_presets.Select(preset => preset.Name).Cast<object>().ToArray());
        _shaderQuickPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingShaderQuick || _shaderQuickPreset.SelectedIndex <= 0) return;
            SelectPreset(_shaderQuickPreset.SelectedItem!.ToString()!);
            SelectShaderWorkspaceMode("Preview");
        };
        ShaderWorkflowField(_shaderQuickHeader, "Effect", _shaderQuickPreset);
        _shaderQuickDescription = ShaderWorkflowText(_shaderQuickHeader, string.Empty);
        _targetAssetCombo.Enabled = true;
        _shaderQuickTargetLabel = ShaderWorkflowField(_shaderQuickHeader, "Preview resource", _targetAssetCombo);
        page.Controls.Add(_parameters);
        page.Controls.Add(_shaderQuickHeader);
        UpdateShaderQuickFields();
        return page;
    }

    private Control BuildShaderPreviewSettings()
    {
        FlowLayoutPanel page = MakeShaderWorkflowPage("ShaderPreviewSettings");
        ShaderWorkflowText(page, "Preview settings", heading: true);
        ShaderWorkflowText(page, "The preview type chooses the drawing pipeline. It does not attach the shader to a game resource. Changing the type preserves your code; choose a compatible effect or edit Code.");
        ShaderWorkflowField(page, "Preview type", _targetTypeCombo);
        ShaderWorkflowField(page, "Terrain component", _terrainComponentCombo);
        Button browse = new() { Text = "Choose preview resource…", AutoSize = true, Name = "ShaderBrowsePreviewResource" };
        EditorChrome.StyleField(browse); browse.Click += (_, _) => PickTargetAsset(); page.Controls.Add(browse);
        return page;
    }

    private FlowLayoutPanel MakeShaderWorkflowPage(string name)
    {
        FlowLayoutPanel page = new()
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(12), BackColor = EditorChrome.Surface, Name = name,
        };
        page.SizeChanged += (_, _) => SizeShaderWorkflowPage(page);
        return page;
    }

    private static Label ShaderWorkflowText(FlowLayoutPanel page, string text, bool heading = false)
    {
        Label label = new() { Text = text, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted,
            Name = heading ? "ShaderWorkflowHeading" : "ShaderWorkflowText", Margin = new Padding(0, 0, 0, 8) };
        page.Controls.Add(label); return label;
    }

    private static Label ShaderWorkflowField(FlowLayoutPanel page, string text, Control field)
    {
        Panel row = new() { Margin = new Padding(0, 0, 0, 8), BackColor = EditorChrome.Surface, Tag = field };
        Label label = new() { Text = text, Dock = DockStyle.Top, ForeColor = EditorChrome.Muted };
        field.Dock = DockStyle.Bottom;
        row.Controls.Add(field); row.Controls.Add(label); page.Controls.Add(row);
        return label;
    }

    private void SizeShaderWorkflowPage(FlowLayoutPanel page)
    {
        int width = Math.Max(120, page.ClientSize.Width - page.Padding.Horizontal - (page.AutoScroll ? 22 : 0));
        foreach (Control control in page.Controls)
        {
            control.Width = width;
            if (control is Label label)
            {
                label.Font = label.Name == "ShaderWorkflowHeading" ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak).Height + 4;
            }
            else if (control is Panel row && row.Tag is Control field)
            {
                Label caption = row.Controls.OfType<Label>().Single(); caption.Font = EditorChrome.SmallFont;
                caption.Height = caption.Font.Height + 6;
                field.Font = EditorChrome.BaseFont;
                row.Height = caption.Height + field.PreferredSize.Height + 4;
            }
            else if (control is TextBox { Multiline: true }) control.Height = Math.Max(80, control.Font.Height * 4 + 12);
            else if (control is Button) control.Height = Math.Max(34, control.Font.Height + 14);
        }
        if (page == _shaderQuickHeader)
            page.Height = page.Padding.Vertical + page.Controls.Cast<Control>().Where(control => control.Visible)
                .Sum(control => control.Height + control.Margin.Vertical);
    }

    private void UpdateShaderQuickFields()
    {
        if (_shaderQuickPreset is null) return;
        _syncingShaderQuick = true;
        try { _shaderQuickPreset.SelectedIndex = Math.Max(0, _shaderQuickPreset.Items.IndexOf(_document.Preset)); }
        finally { _syncingShaderQuick = false; }
        _shaderQuickDescription!.Text = _presets.FirstOrDefault(preset => preset.Name == _document.Preset)?.Description
            ?? "Custom source. Edit Code to change the effect; exposed values remain editable here.";
        _shaderQuickTargetLabel!.Text = "Preview resource · " + _document.TargetType;
        _shaderQuickTargetLabel.Parent!.Visible = _document.TargetType != ShaderTargetType.Fullscreen;
        if (_terrainComponentCombo.Parent?.Tag == _terrainComponentCombo)
            _terrainComponentCombo.Parent.Visible = _document.TargetType == ShaderTargetType.Terrain;
        if (_shaderQuickHeader is not null) SizeShaderWorkflowPage(_shaderQuickHeader);
    }

    private void ShowShaderGameGuide()
    {
        if (_shaderGameGuide is null) return;
        foreach (Control control in _shaderGameGuide.Controls.Cast<Control>().ToArray()) control.Dispose();
        Button back = new() { Text = "Back to Quick setup", AutoSize = true, Name = "ShaderBackToQuickSetup" };
        EditorChrome.StyleField(back); back.Click += (_, _) => SelectShaderWorkspaceMode("Preview");
        _shaderGameGuide.Controls.Add(back);
        ShaderWorkflowText(_shaderGameGuide, "Use this shader in a game", heading: true);
        ShaderWorkflowText(_shaderGameGuide, "Preview resource only chooses what you see here. An Object must reference the saved shader for it to appear in gameplay.");
        ShaderWorkflowText(_shaderGameGuide, "1. Choose an Image", heading: true);
        ShaderWorkflowText(_shaderGameGuide, "For a 2D sprite, return to Quick setup, choose an Image effect and a project Image as its preview resource. The built-in checker is a preview sample; it cannot be placed in a Room.");
        ShaderWorkflowText(_shaderGameGuide, "2. Create a shaded Object", heading: true);
        ShaderWorkflowText(_shaderGameGuide, "This saves your valid shader and creates an ordinary 2D Object referencing that Image and Shader, then opens it. The Image stays editable in the Image Editor.");
        TextBox name = new() { Text = ResourceDisplayName.Format(ResourcePath) + " Object", Name = "ShaderObjectName",
            AccessibleName = "Name for the shaded Object" };
        EditorChrome.StyleField(name); _shaderGameGuide.Controls.Add(name);
        Label result = new() { ForeColor = EditorChrome.Muted, Name = "ShaderObjectCreationResult" };
        Button create = new() { Text = "Create shaded Object", AutoSize = true, Name = "ShaderCreateObject" };
        EditorChrome.StyleField(create);
        create.Click += (_, _) =>
        {
            try { result.Text = "Created " + ResourceDisplayName.Format(CreateShaderObject(name.Text)) + ". Place it in a Room, then Run."; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { result.Text = error.Message; _statusLabel.Text = error.Message; }
            SizeShaderWorkflowPage(_shaderGameGuide);
        };
        _shaderGameGuide.Controls.Add(create); _shaderGameGuide.Controls.Add(result);
        ShaderWorkflowText(_shaderGameGuide, "3. Place it in a Room and Run", heading: true);
        ShaderWorkflowText(_shaderGameGuide, "Drag the new Object from Assets into your Room. Save changes to the Shader or Image to update live previews and the running game. To shade an existing Object, add its Shader component and choose this Shader resource.");
        ShaderWorkflowText(_shaderGameGuide, "Custom shader effects use DX11, DX12, Vulkan or OpenGL. Software draws the original Image without custom HLSL; choose a hardware renderer from the bottom-right menu to see this effect.");
        SelectShaderWorkspaceMode("Use in game"); SizeShaderWorkflowPage(_shaderGameGuide);
    }

    public string CreateShaderObject(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        if (_document.TargetType != ShaderTargetType.Image || _document.Pipeline != ShaderAssetPipeline.Sprite)
            throw new InvalidOperationException("Choose an Image effect and a project Image in Quick setup to create a 2D shaded Object.");
        string? image = ProjectAssetIndex.ResolveReference(ProjectRoot, _document.PreviewAsset, ResourceKind.Image);
        if (string.IsNullOrEmpty(_document.PreviewAsset) || string.IsNullOrEmpty(image) || !File.Exists(image))
            throw new InvalidOperationException("Choose a project Image in Quick setup. The built-in checker is only a preview sample.");
        CompileNow();
        if (!LastCompileSucceeded) throw new InvalidOperationException("Fix the shader's compile errors before creating an Object. Your draft is retained in Code.");
        Save();
        if (IsDirty) throw new InvalidOperationException("The Shader could not be saved. Resolve its diagnostics before creating an Object.");
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
        document["dimension"] = "TwoD";
        ObjectCompositionModel composition = new(document);
        composition.SetAsset("SpriteComponent", ResourceNames.Name(ProjectRoot, image));
        composition.SetAsset("ShaderComponent", ResourceNames.Name(ProjectRoot, ResourcePath));
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, ResourceReferenceRewriter.Normalize(ProjectRoot, path,
            document.ToString(Newtonsoft.Json.Formatting.Indented)), new UTF8Encoding(false));
        ResourceNames.Invalidate(ProjectRoot); RequestOpenLinkedResource(path); return path;
    }

    private void RefreshShaderDirtyState()
    {
        if (CaptureDocument() == _savedShaderSnapshot) AcceptSave(); else MarkDirty();
    }
}

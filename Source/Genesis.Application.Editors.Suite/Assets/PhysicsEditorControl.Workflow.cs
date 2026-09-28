using System.Text;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Physics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Spatial;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class PhysicsEditorControl
{
    private FlowLayoutPanel? _physicsQuickSetup;
    private FlowLayoutPanel? _physicsGameGuide;
    private ThemedComboBox? _physicsDimensionChoice;
    private ThemedComboBox? _physicsBodyChoice;
    private Button? _physicsImageButton;
    private ToolStripButton? _physicsQuickButton;
    private ToolStripButton? _physicsCodeButton;

    private void BuildPhysicsWorkflow()
    {
        _physicsQuickSetup = PhysicsWorkflowPage("PhysicsQuickSetup");
        _physicsQuickSetup.VisibleChanged += (_, _) => { if (_physicsQuickSetup.Visible) ResetPhysicsQuickScroll(); };
        Load += (_, _) => ResetPhysicsQuickScroll();
        PhysicsWorkflowText(_physicsQuickSetup, "Quick setup", true);
        PhysicsWorkflowText(_physicsQuickSetup, "Choose 2D, a body and a sprite. Play tests the material. Use in game creates an Object you can drag into a Room.");
        _physicsDimensionChoice = new ThemedComboBox { Name = "PhysicsDimensionChoice" };
        _physicsDimensionChoice.Items.AddRange(["2D sprites", "3D bodies"]);
        _physicsDimensionChoice.SelectedIndexChanged += (_, _) => { if (!_syncing) SetPreview2D(_physicsDimensionChoice.SelectedIndex == 0); };
        PhysicsWorkflowField(_physicsQuickSetup, "Dimension", _physicsDimensionChoice);
        PhysicsWorkflowField(_physicsQuickSetup, "Starting preset", _presetCombo);
        _physicsBodyChoice = new ThemedComboBox { Name = "PhysicsBodyChoice" };
        _physicsBodyChoice.Items.AddRange([PhysicsBodyKind.Dynamic, PhysicsBodyKind.Static, PhysicsBodyKind.Kinematic]);
        _physicsBodyChoice.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _physicsBodyChoice.SelectedItem is PhysicsBodyKind body)
                SetPhysicsValue(() => _document.BodyType = body);
        };
        PhysicsWorkflowField(_physicsQuickSetup, "Body behaviour in gameplay", _physicsBodyChoice);
        PhysicsWorkflowText(_physicsQuickSetup, "Dynamic falls. Static stays fixed. Kinematic follows scripted velocity. The sample props test material; body behaviour applies in gameplay.");
        _physicsImageButton = new Button { Name = "PhysicsChooseImage", Text = "Choose sprite Image…", AutoEllipsis = true };
        EditorChrome.StyleField(_physicsImageButton);
        _physicsImageButton.Click += (_, _) =>
        {
            ProjectAssetEntry? selected = AssetPickerService.PickAsset(new AssetPickerRequest(ProjectRoot, ResourceKind.Image,
                _document.PreviewAssetPath, "Choose Physics Sprite", RequiredImageUsage: ImageUsage.Sprite), FindForm());
            if (selected is not null) ChooseSpriteImage(selected.Reference);
        };
        _physicsQuickSetup.Controls.Add(_physicsImageButton);
        foreach (CollapsibleSection section in _physicsInspectorStack!.Controls.OfType<CollapsibleSection>().Take(2).ToArray())
            _physicsQuickSetup.Controls.Add(section);
        _physicsLeftPages["Preview"] = _physicsQuickSetup;
        _physicsLeftPageHost!.Controls.Add(_physicsQuickSetup);
        Panel settings = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        settings.Controls.Add(_physicsInspectorStack);
        settings.Controls.Add(EditorChrome.SectionLabel("SANDBOX WORLD SETTINGS"));
        _physicsLeftPages["Properties"].Dispose();
        _physicsLeftPages["Properties"] = settings; _physicsLeftPageHost.Controls.Add(settings);
        foreach (CollapsibleSection section in _physicsInspectorStack.Controls.OfType<CollapsibleSection>())
            section.HeaderText = "Preview world and body layer";
        _physicsGameGuide = PhysicsWorkflowPage("PhysicsUseInGame");
        _physicsLeftPages["Use in game"] = _physicsGameGuide; _physicsLeftPageHost.Controls.Add(_physicsGameGuide);
        FlowLayoutPanel target = PhysicsWorkflowPage("PhysicsPreviewResource");
        PhysicsWorkflowText(target, "Preview resource", true);
        PhysicsWorkflowText(target, "Choose a resource to inspect in the sandbox. This preview choice does not attach Physics to an existing Object. Use in game explains the saved link.");
        PhysicsWorkflowField(target, "Resource type", _previewTargetControls.KindCombo);
        Button browse = new() { Text = "Choose preview resource…", Name = "PhysicsBrowsePreview" };
        EditorChrome.StyleField(browse); browse.Click += (_, _) => _previewTargetControls.BrowseButton.PerformClick();
        target.Controls.Add(browse);
        _physicsLeftPages["Preview resource"] = target; _physicsLeftPageHost.Controls.Add(target);
        SelectPhysicsWorkspaceMode("Preview", _physicsRail!);
    }

    private void ResetPhysicsQuickScroll()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            if (IsDisposed || _physicsQuickSetup?.Visible != true) return;
            _physicsQuickSetup.AutoScrollPosition = Point.Empty;
        });
    }

    private ToolStrip BuildPhysicsPreviewToolbar()
    {
        ToolStrip bar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text, Name = "PhysicsPreviewToolbar" };
        _toolbarPlay = EditorChrome.ToolButton(_paused ? "Play" : "Pause", "Run or pause the material sandbox", () => SetPhysicsPaused(!_paused));
        bar.Items.Add(_toolbarPlay);
        bar.Items.Add(EditorChrome.ToolButton("Step", "Pause and advance one physics frame", () => { SetPhysicsPaused(true); StepSandbox(1f / 60); }));
        bar.Items.Add(EditorChrome.ToolButton("Restart", "Restart the sample bodies", RebuildSandbox));
        _toolbarSpeed = new ToolStripLabel("1×") { ForeColor = EditorChrome.Muted }; bar.Items.Add(_toolbarSpeed);
        return bar;
    }

    private FlowLayoutPanel PhysicsWorkflowPage(string name)
    {
        FlowLayoutPanel page = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12), Name = name };
        page.SizeChanged += (_, _) => { SizePhysicsWorkflowPage(page); ApplyPhysicsLayout(); };
        return page;
    }

    private static void PhysicsWorkflowText(FlowLayoutPanel page, string text, bool heading = false)
    {
        page.Controls.Add(new Label { Text = text, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted,
            Name = heading ? "PhysicsWorkflowHeading" : "PhysicsWorkflowText", Margin = new Padding(0, 0, 0, 8) });
    }

    private static void PhysicsWorkflowField(FlowLayoutPanel page, string caption, Control field)
    {
        Panel row = new() { Tag = field, Margin = new Padding(0, 0, 0, 8) };
        Label label = new() { Text = caption, Dock = DockStyle.Top, ForeColor = EditorChrome.Muted };
        EditorChrome.StyleField(field); field.Dock = DockStyle.Bottom;
        row.Controls.Add(field); row.Controls.Add(label); page.Controls.Add(row);
    }

    private static void SizePhysicsWorkflowPage(FlowLayoutPanel page)
    {
        int width = Math.Max(120, page.ClientSize.Width - page.Padding.Horizontal - 22);
        foreach (Control control in page.Controls)
        {
            control.Width = width;
            if (control is Label label)
            {
                label.Font = label.Name == "PhysicsWorkflowHeading" ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4;
            }
            else if (control is Panel row && row.Tag is Control field)
            {
                Label caption = row.Controls.OfType<Label>().Single(); caption.Font = EditorChrome.SmallFont; caption.Height = caption.Font.Height + 6;
                field.Font = EditorChrome.BaseFont;
                int fieldHeight = field is ComboBox combo ? combo.PreferredHeight : field.PreferredSize.Height;
                if (field is ThemedComboBox themed) fieldHeight = Math.Max(fieldHeight, themed.ItemHeight + 8);
                row.Height = caption.Height + fieldHeight + 4;
            }
            else if (control is Button) { control.Font = EditorChrome.BaseFont; control.Height = control.Font.Height + 18; }
            else if (control is TextBox { Multiline: true }) { control.Font = EditorChrome.CodeFont; control.Height = control.Font.Height * 4 + 16; }
        }
    }

    private void UpdatePhysicsQuickFields()
    {
        if (_physicsQuickSetup is null) return;
        bool wasSyncing = _syncing; _syncing = true;
        try
        {
            _physicsDimensionChoice!.SelectedIndex = _document.Dimension == PhysicsDimension.TwoD ? 0 : 1;
            _physicsBodyChoice!.SelectedItem = _document.BodyType;
            _physicsImageButton!.Text = string.IsNullOrWhiteSpace(_document.PreviewAssetPath)
                ? "Choose sprite Image…" : "Sprite: " + ResourceDisplayName.Format(_document.PreviewAssetPath);
            _physicsImageButton.Visible = _document.Dimension == PhysicsDimension.TwoD;
        }
        finally { _syncing = wasSyncing; }
        foreach (FlowLayoutPanel page in _physicsLeftPages.Values.OfType<FlowLayoutPanel>()) SizePhysicsWorkflowPage(page);
    }

    public bool ChooseSpriteImage(string reference)
    {
        string? path = ProjectAssetIndex.ResolveReference(ProjectRoot, reference, ResourceKind.Image);
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !ImageDocumentSerializer.LoadAtomic(path).Document.Usage.Supports(ImageUsage.Sprite)) return false;
        SetPreviewTarget(EditorPreviewTargetChrome.PreviewTargetKind.Image, ResourceNames.Name(ProjectRoot, path));
        UpdatePhysicsQuickFields(); return true;
    }

    private bool DrawPhysicsSpritePreview(IRenderController renderer, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, float width, float height, bool selected)
    {
        if (_document.PreviewAssetKind != "Image" || string.IsNullOrWhiteSpace(_document.PreviewAssetPath)) return false;
        var image = SpriteCollisionBounds.Load(ProjectRoot, _document.PreviewAssetPath);
        if (image is null) return false;
        RectangleF bounds = SpriteCollisionBounds.Resolve(image, 0, 0, 0, 1, 1);
        float sx = width / Math.Max(1, bounds.Width), sy = height / Math.Max(1, bounds.Height);
        float angle = -2 * MathF.Atan2(rotation.Z, rotation.W);
        System.Numerics.Vector2 offset = new((bounds.Left + bounds.Width / 2) * sx, (bounds.Top + bounds.Height / 2) * sy);
        offset = System.Numerics.Vector2.Transform(offset, System.Numerics.Matrix3x2.CreateRotation(angle));
        TransformComponent sprite = new() { X = position.X * PhysicsPixelsPerMetre - offset.X, Y = -position.Y * PhysicsPixelsPerMetre - offset.Y,
            ScaleX = sx, ScaleY = sy, ScaleZ = 1, Rotation = angle * 180 / MathF.PI };
        FrameRenderQueue queue = new();
        ObjectDrawPass.QueueSprite2D(ProjectRoot, queue, renderer, new ObjectDrawAssetEntry { Image = _document.PreviewAssetPath },
            sprite, new Draw2DComponent { Visible = true, Depth = -100 }, _document.PreviewAssetPath, 0, 1, 0, 0, 1);
        queue.Flush(renderer, includeMeshes: false);
        if (selected) renderer.DrawText("Selected sprite", 12, 32, EditorChrome.SmallFont.SizeInPoints, new RenderColor(.45f, .72f, 1));
        return true;
    }

    private void ShowPhysicsGameGuide()
    {
        if (_physicsGameGuide is null) return;
        foreach (Control control in _physicsGameGuide.Controls.Cast<Control>().ToArray()) control.Dispose();
        Button back = new() { Text = "Back to Quick setup", Name = "PhysicsBackToQuickSetup" };
        EditorChrome.StyleField(back); back.Click += (_, _) => SelectPhysicsWorkspaceMode("Preview", _physicsRail!); _physicsGameGuide.Controls.Add(back);
        PhysicsWorkflowText(_physicsGameGuide, "Use this Physics in a 2D game", true);
        PhysicsWorkflowText(_physicsGameGuide, "1. In Quick setup, choose 2D and a sprite Image. Dynamic bodies fall; Static bodies make platforms; Kinematic bodies move with PhysicsSetVelocity. Friction, bounce, density, shape, gravity scale, rotation lock and collision layers are saved on the body.");
        PhysicsWorkflowText(_physicsGameGuide, "2. Create your Object", true);
        TextBox name = new() { Text = ResourceDisplayName.Format(ResourcePath) + " sprite", Name = "PhysicsObjectName" };
        PhysicsWorkflowField(_physicsGameGuide, "Object name", name);
        Button create = new() { Text = "Create sprite Object", Name = "PhysicsCreateObject" }; EditorChrome.StyleField(create);
        Label result = new() { ForeColor = EditorChrome.Muted, Name = "PhysicsCreateResult" };
        create.Click += (_, _) =>
        {
            try { result.Text = "Created " + ResourceDisplayName.Format(CreatePhysicsObject(name.Text)) + ". Place this Object in a Room."; }
            catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException) { result.Text = error.Message; }
            SizePhysicsWorkflowPage(_physicsGameGuide);
        };
        _physicsGameGuide.Controls.Add(create); _physicsGameGuide.Controls.Add(result);
        PhysicsWorkflowText(_physicsGameGuide, "3. Drag the Object from Assets into a Room and Run", true);
        PhysicsWorkflowText(_physicsGameGuide, "Enable collision on the Room's tile layer and mark solid tiles in Image. Sprite pivots match Room placement. One metre is 32 pixels; 2D positions and velocity commands use pixels, with positive Y down. Room Environment owns game gravity; sandbox gravity, damping and sleeping settings only tune this preview.");
        PhysicsWorkflowText(_physicsGameGuide, "In the Object's Step event", true);
        TextBox example = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = "// Move right at 96 pixels per second\r\nPhysicsSetVelocity(InstanceSelf(), 96,\r\n    PhysicsGetVelocityY(InstanceSelf()), 0);\r\n// Check PhysicsIsGrounded(InstanceSelf(), 6) before jumping.", Name = "PhysicsGameplayExample" };
        EditorChrome.StyleField(example); _physicsGameGuide.Controls.Add(example);
        PhysicsWorkflowText(_physicsGameGuide, "Save Physics or Image changes to update the linked Object and Room previews. The sample sandbox is a material test; Use in game creates the saved links used by gameplay.");
        SelectPhysicsWorkspaceMode("Use in game", _physicsRail!); SizePhysicsWorkflowPage(_physicsGameGuide);
    }

    public string CreatePhysicsObject(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        if (_document.Dimension != PhysicsDimension.TwoD) throw new InvalidOperationException("Choose 2D in Quick setup to create a sprite Object.");
        string? image = ProjectAssetIndex.ResolveReference(ProjectRoot, _document.PreviewAssetPath, ResourceKind.Image);
        if (string.IsNullOrEmpty(image) || !File.Exists(image) || !ImageDocumentSerializer.LoadAtomic(image).Document.Usage.Supports(ImageUsage.Sprite))
            throw new InvalidOperationException("Choose a sprite Image in Quick setup. The sample sprite cannot be placed in a Room.");
        Save();
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent); document["dimension"] = "TwoD";
        ObjectCompositionModel composition = new(document);
        composition.SetAsset("SpriteComponent", ResourceNames.Name(ProjectRoot, image));
        composition.SetAsset("PhysicsComponent", ResourceNames.Name(ProjectRoot, ResourcePath));
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, ResourceReferenceRewriter.Normalize(ProjectRoot, path, document.ToString(Newtonsoft.Json.Formatting.Indented)), new UTF8Encoding(false));
        ResourceNames.Invalidate(ProjectRoot); RequestOpenLinkedResource(path); return path;
    }
}

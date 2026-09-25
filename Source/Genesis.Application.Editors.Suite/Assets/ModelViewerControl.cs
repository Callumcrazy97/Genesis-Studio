using System.Diagnostics;
using System.Text.Json.Nodes;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

public enum ModelPreviewShading { Textured, Untextured, Wireframe }

/// <summary>Dedicated model inspection surface. View settings never change the source document.</summary>
public partial class ModelViewerControl : EditorSurfaceControl
{
    protected GModelAsset Asset;
    protected readonly EditorViewport3D Surface = new() { Dock = DockStyle.Fill, FloorStyle = EditorFloorStyle.GridOnly };
    protected readonly TableLayoutPanel Body = new() { Name = "ModelViewerBody", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    protected readonly Panel LeftPanel = new() { Name = "ModelViewerLeftPanel", Dock = DockStyle.Fill };
    protected readonly ToolStrip Menus = Strip("ModelViewerMenus", 32);
    protected readonly ToolStrip Commands = Strip("ModelViewerCommands", 44);
    protected readonly Label Status = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 5, 8, 2), AutoEllipsis = true };
    protected readonly RuntimeModelRenderSystem PreviewRenderer = new();
    private readonly TreeView _hierarchy = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, FullRowSelect = true, ItemHeight = 26 };
    private readonly ListBox _materials = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = 27 };
    private readonly Label _details = new() { Dock = DockStyle.Fill, Padding = new Padding(12), AutoEllipsis = true };
    private readonly ToolStripComboBox _clips = new() { Name = "ModelViewerClips", DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 260, DropDownWidth = 420 };
    private readonly ToolStripButton _play = new("Play"), _loopButton = new("Loop") { CheckOnClick = true, Checked = true };
    private readonly ModelFrameRuler _timeline = new() { Name = "ModelViewerTimeline", Dock = DockStyle.Fill, Minimum = 0, Maximum = 1 };
    private readonly Label _frameLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(8) };
    private readonly ToolStripButton _spinButton = new("Spin") { CheckOnClick = true, Padding = new Padding(6, 4, 6, 4) };
    private readonly ToolStripDropDownButton _shadingButton = new("Textured") { Padding = new Padding(6, 4, 6, 4) };
    private readonly ToolStripDropDownButton _lightingButton = new("Lighting") { Padding = new Padding(6, 4, 6, 4) };
    private readonly ToolStripDropDownButton _projectionButton = new("Perspective") { Padding = new Padding(6, 4, 6, 4) };
    private FlowLayoutPanel? _sidebarFlow;
    private CollapsibleSection? _sourceSection;
    private CollapsibleSection? _hierarchySection;
    private CollapsibleSection? _materialsSection;
    private CollapsibleSection? _detailsSection;
    private readonly Label _sourceInfo = new() { Name = "ModelSourceInfo", Dock = DockStyle.Fill, Padding = new Padding(10) };
    private bool _layingOutSidebar;
    private readonly Label _empty = new() { Name = "EmptyModelHint", AutoSize = false, Size = new Size(300, 72), TextAlign = ContentAlignment.MiddleCenter };
    private ToolStripButton? _openModelEditor;
    protected bool EmptyModelHintEnabled { get; set; } = true;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 16 };
    private long _lastTick;
    private bool _playing, _syncing, _orthographic, _lighting = true, _shadows = true, _showGrid = true, _gpuDirty;
    private float _time, _spin, _ambient = .55f, _keyIntensity = 1.4f, _lightAngle = 35;
    private string _clip = "";
    private MeshHandle _gridMesh;
    private bool _gridDirty = true;
    private bool _gridSizeInitialized;
    private float _gridSize = 1;
    private Vector3 _gridRotation;
    private Color _gridColor = Color.FromArgb(95, 110, 135);
    private ModelPreviewShading _shading;
    private readonly OrientationWidget _orientation;
    private bool _importing;
    public event EventHandler? ComposeRequested;

    public ModelViewerControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        Asset = StudioModelResourceLoader.Load(resourcePath);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in new[] { new RowStyle(SizeType.Absolute, 32), new RowStyle(SizeType.Absolute, 44), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 210), new RowStyle(SizeType.Absolute, 27) }) root.RowStyles.Add(row);
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270)); Body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Body.Controls.Add(LeftPanel, 0, 0); Body.Controls.Add(Surface, 1, 0);
        root.Controls.Add(Menus, 0, 0); root.Controls.Add(Commands, 0, 1); root.Controls.Add(Body, 0, 2);
        root.Controls.Add(BuildPlayback(), 0, 3); root.Controls.Add(Status, 0, 4);
        foreach (Control control in root.Controls) control.Margin = Padding.Empty;
        foreach (Control control in Body.Controls) control.Margin = Padding.Empty;
        Controls.Add(root);
        BuildMenus(); BuildSidebar();
        Surface.SceneStateFactory = CreateSceneState;
        Surface.DrawScene += DrawPreview;
        Surface.CameraOverrideFactory = CameraOverride;
        Surface.SelectionWorldPoint = () => (ModelBounds.Min + ModelBounds.Max) * .5f;
        _orientation = new OrientationWidget(Surface) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        Surface.Controls.Add(_orientation); _orientation.Visible = false;
        Surface.DrawOverlay += _orientation.Draw;
        Surface.Host.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _orientation.SelectAt(e.Location); };
        _empty.Text = "No model loaded\nUse Import Model\u2026 to choose a model file.";
        _empty.ForeColor = EditorChrome.Muted; _empty.BackColor = EditorChrome.Canvas;
        Surface.Controls.Add(_empty); _empty.BringToFront();
        Surface.SizeChanged += (_, _) => { _orientation.Location = new Point(Math.Max(0, Surface.Width - 108), 10); _empty.Location = new Point(Math.Max(0, (Surface.Width - 300) / 2), Math.Max(0, (Surface.Height - 72) / 2)); };
        _clock.Tick += (_, _) => TickPreview();
        HandleCreated += (_, _) => { _lastTick = Stopwatch.GetTimestamp(); _clock.Start(); BeginInvoke(FrameModel); };
        Disposed += (_, _) => { _clock.Stop(); _clock.Dispose(); if (Surface.Host.Renderer is { } renderer) { PreviewRenderer.InvalidateAssets(renderer); if (_gridMesh.IsValid) renderer.ReleaseMesh(_gridMesh); } };
        RefreshAssetPresentation(); ApplyTheme();
    }

    public EditorViewport3D Viewport => Surface;
    public GModelAsset PreviewAsset => Asset;
    public ModelPreviewShading Shading => _shading;
    public bool LightingEnabled => _lighting;
    public bool IsPlaying => _playing;
    public bool Loop { get => _loopButton.Checked; set => _loopButton.Checked = value; }
    public string ActiveClip => _clip;
    public int CurrentFrame => (int)MathF.Floor(_time * (SelectedClip?.Fps ?? 30) + .0001f);
    public float AnimationTime => _time;
    public int AnimationFrameCount => SelectedClip?.Frames.Count ?? 0;
    public IReadOnlyList<string> ClipNames => Asset.Animations.Select(c => c.Name).ToArray();
    public void SetSpin(bool enabled) { _spinButton.Checked = enabled; if (!enabled) _spin = 0; }
    protected void AllowSpin(bool enabled) { _spinButton.Enabled = enabled; if (!enabled) SetSpin(false); }
    public void PlayClip(string name, bool play = true) { SelectClip(name); SetPlaying(play); }
    public void SetAnimationTime(float time) { _time = Math.Max(0, time); UpdatePlayback(); }
    public float GroundHeight => Surface.FloorHeight;
    public (Vector3 Min, Vector3 Max) ModelBounds => (Asset.Bounds.Min - Asset.Pivot.Position, Asset.Bounds.Max - Asset.Pivot.Position);
    protected GModelAnimationClip? SelectedClip => Asset.Animations.FirstOrDefault(c => c.Name == _clip);
    protected bool ModelImportInProgress => _importing;
    public override void Save() { if (!_importing && IsDirty) { PersistModelChanges(Asset); AcceptSave(); } }
    public override void Undo() { if (!_importing) base.Undo(); }
    public override void Redo() { if (!_importing) base.Redo(); }
    public void RequestCompose() { if (IsDirty) Save(); ComposeRequested?.Invoke(this, EventArgs.Empty); }
    public bool SelectOrientationAt(Point point) => _orientation.SelectAt(point);
    protected bool IsOrientationHit(Point point) => _orientation.HitTest(point);

    private void BuildMenus()
    {
        var file = Menu("File");
        file.DropDownItems.Add(new ToolStripMenuItem("Save", null, (_, _) => Save()) { ShortcutKeys = Keys.Control | Keys.S });
        file.DropDownItems.Add("Import Model\u2026", null, (_, _) => ChooseImport());
        file.DropDownItems.Add("Open Model Editor", null, (_, _) => RequestCompose());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Locate Blender\u2026", null, (_, _) => LocateBlender());
        Menus.Items.Add(file);
        var view = Menu("View");
        view.DropDownItems.Add("Frame model", null, (_, _) => FrameModel());
        view.DropDownItems.Add("Grid settings\u2026", null, (_, _) => ShowGridSettings());
        view.DropDownItems.Add("Second camera", null, (_, _) => Surface.PinSecondaryFromCurrentView());
        view.DropDownItems.Add("Close second camera", null, (_, _) => Surface.ClearSecondaryCamera());
        var axes = new ToolStripMenuItem("3D orientation gizmo") { Checked = true, CheckOnClick = true };
        axes.CheckedChanged += (_, _) => _orientation.Enabled = axes.Checked; view.DropDownItems.Add(axes);
        Menus.Items.Add(view);
        Editor3DViewMenu.AddTo(view, () => Surface, new EditorViewMenuChrome.ToggleBinding
        {
            Read = () => _shading == ModelPreviewShading.Wireframe,
            Write = value => SetShading(value ? ModelPreviewShading.Wireframe : ModelPreviewShading.Textured),
        });
        var animation = Menu("Animation");
        animation.DropDownItems.Add("Play / Pause", null, (_, _) => SetPlaying(!_playing));
        animation.DropDownItems.Add("Stop", null, (_, _) => { SetPlaying(false); SetFrame(0); });
        Menus.Items.Add(animation);
        var help = Menu("Help");
        help.DropDownItems.Add("Viewer controls", null, (_, _) => MessageBox.Show(this, "Right drag: orbit\nMiddle drag: pan\nWheel: zoom\nF: frame model\nSpace: play / pause\n\nUse the axis widget to switch viewpoints. View settings do not edit the model.", "Model Viewer"));
        Menus.Items.Add(help);
        Commands.Items.Add(new ToolStripLabel("MODEL VIEWER") { Visible = false });
        var import = Button("Import / Replace\u2026", ChooseImport); import.Name = "ImportModelFiles"; Commands.Items.Add(import);
        BuildMotionImportMenu();
        Commands.Items.Add(Button("Frame", FrameModel));
        Commands.Items.Add(_spinButton);
        foreach (ModelPreviewShading shading in Enum.GetValues<ModelPreviewShading>())
            _shadingButton.DropDownItems.Add(shading.ToString(), null, (_, _) => SetShading(shading));
        Commands.Items.Add(_shadingButton);
        _projectionButton.DropDownItems.Add("Perspective", null, (_, _) => SetOrthographic(false));
        _projectionButton.DropDownItems.Add("Orthographic", null, (_, _) => SetOrthographic(true));
        Commands.Items.Add(_projectionButton);
        var camera = Menu("Camera", showArrow: true);
        foreach (string name in new[] { "Front", "Back", "Left", "Right", "Top", "Bottom", "Three-quarter" })
            camera.DropDownItems.Add(name, null, (_, _) => SetCameraView(name));
        Commands.Items.Add(camera);
        foreach (string preset in new[] { "Studio", "Soft", "Unlit" }) _lightingButton.DropDownItems.Add(preset, null, (_, _) => SetLightingPreset(preset));
        _lightingButton.DropDownItems.Add("Lighting settings\u2026", null, (_, _) => ShowLightingSettings());
        Commands.Items.Add(_lightingButton);
        var grid = Menu("Ground", showArrow: true);
        foreach (EditorFloorStyle style in Enum.GetValues<EditorFloorStyle>()) grid.DropDownItems.Add(style.StatusLabel(), null, (_, _) => SetGround(style));
        grid.DropDownItems.Add("Grid settings\u2026", null, (_, _) => ShowGridSettings());
        Commands.Items.Add(grid);
        _openModelEditor = Button("Edit Model", RequestCompose); _openModelEditor.Name = "OpenModelEditor"; _openModelEditor.Alignment = ToolStripItemAlignment.Right; _openModelEditor.BackColor = EditorChrome.Accent;
        Commands.Items.Insert(3, _openModelEditor); Commands.Items.Insert(4, new ToolStripSeparator()); _openModelEditor.Alignment = ToolStripItemAlignment.Left;
    }

    private void BuildSidebar()
    {
        _sidebarFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        _sourceSection = new CollapsibleSection("Source", 50);
        _sourceInfo.Text = ResourceDisplayName.Format(ResourcePath) + "\nModel asset";
        _sourceSection.Content.Controls.Add(_sourceInfo);
        _hierarchySection = new CollapsibleSection("Hierarchy / Meshes", 120); _hierarchySection.Content.Controls.Add(_hierarchy);
        _materialsSection = new CollapsibleSection("Materials", 60); _materialsSection.Content.Controls.Add(_materials);
        _detailsSection = new CollapsibleSection("Model properties", 200); _detailsSection.Content.Controls.Add(_details);
        _sidebarFlow.Controls.AddRange([_sourceSection, _hierarchySection, _materialsSection, _detailsSection]);
        LeftPanel.Controls.Add(_sidebarFlow);
        AddSidebarDivider();
        _materials.SelectedIndexChanged += (_, _) => RefreshDetails();

        void UpdateSectionWidths()
        {
            LayoutSidebar();
        }

        _sidebarFlow.ClientSizeChanged += (_, _) => UpdateSectionWidths();
        _sourceSection.StateChanged += (_, _) => UpdateSectionWidths();
        _hierarchySection.StateChanged += (_, _) => UpdateSectionWidths();
        _materialsSection.StateChanged += (_, _) => UpdateSectionWidths();
        _detailsSection.StateChanged += (_, _) => UpdateSectionWidths();
        _sourceInfo.FontChanged += (_, _) => LayoutSidebar();
        _details.FontChanged += (_, _) => LayoutSidebar();
        UpdateSectionWidths();
    }

    private Control BuildPlayback()
    {
        var panel = new TableLayoutPanel { Name = "ModelViewerPlayback", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8, 2, 8, 4) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var transport = Strip("ModelViewerTransport", 40);
        transport.Items.Add(new ToolStripLabel("ANIMATION")); transport.Items.Add(_clips);
        transport.Items.Add(Button("|◀", () => SetFrame(0))); transport.Items.Add(Button("◀", () => SetFrame(CurrentFrame - 1)));
        transport.Items.Add(_play); _play.Click += (_, _) => SetPlaying(!_playing);
        transport.Items.Add(Button("Stop", () => { SetPlaying(false); SetFrame(0); }));
        transport.Items.Add(Button("▶", () => SetFrame(CurrentFrame + 1))); transport.Items.Add(_loopButton);
        _clips.SelectedIndexChanged += (_, _) => { if (!_syncing) SelectClip(_clips.SelectedIndex <= 0 ? "" : _clips.Text); };
        _timeline.ValueChanged += (_, _) => { if (!_syncing) SetFrame(_timeline.Value); };
        panel.Controls.Add(transport, 0, 0); panel.SetColumnSpan(transport, 2);
        panel.Controls.Add(_timeline, 0, 1); panel.Controls.Add(_frameLabel, 1, 1);
        var framed = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2), BorderStyle = BorderStyle.FixedSingle };
        framed.Controls.Add(panel); var title = ImageEditorChrome.MakeSectionTitle("ANIMATION TIMELINE"); framed.Controls.Add(title); return framed;
    }

    protected void RefreshAssetPresentation()
    {
        string previousClip = _clip;
        Asset.RecalculateBounds();
        Surface.FloorHeight = Asset.HasRenderableMeshes ? ModelBounds.Min.Y : 0;
        _gridDirty = _gpuDirty = true;
        _hierarchy.BeginUpdate(); _hierarchy.Nodes.Clear();
        var nodes = Asset.Nodes.Select(n => new TreeNode(n.Name)).ToArray();
        for (int i = 0; i < nodes.Length; i++)
        {
            int parent = Asset.Nodes[i].ParentIndex;
            if (parent >= 0 && parent < nodes.Length && parent != i && !HasAncestor(parent, i)) nodes[parent].Nodes.Add(nodes[i]); else _hierarchy.Nodes.Add(nodes[i]);
        }
        var assigned = new HashSet<int>();
        for (int i = 0; i < nodes.Length; i++) foreach (int mesh in Asset.Nodes[i].MeshIndices.Where(m => m >= 0 && m < Asset.Meshes.Count))
        { nodes[i].Nodes.Add(Asset.Meshes[mesh].Name); assigned.Add(mesh); }
        for (int i = 0; i < Asset.Meshes.Count; i++) if (!assigned.Contains(i)) _hierarchy.Nodes.Add(Asset.Meshes[i].Name);
        _hierarchy.ExpandAll(); _hierarchy.EndUpdate();
        _materials.Items.Clear(); foreach (var material in Asset.Materials) _materials.Items.Add(material.Name);
        _syncing = true; _clips.Items.Clear(); _clips.Items.Add("None"); foreach (var clip in Asset.Animations) _clips.Items.Add(clip.Name);
        _clips.SelectedIndex = 0; _syncing = false; SelectClip(previousClip);
        _empty.Visible = EmptyModelHintEnabled && !Asset.HasRenderableMeshes;
        if (_openModelEditor is not null)
            _openModelEditor.Text = Asset.HasRenderableMeshes ? "Edit Model" : "Create Model";
        _moreOptions.Enabled = Asset.HasRenderableMeshes;
        RefreshDetails();
        bool HasAncestor(int node, int sought)
        {
            var visited = new HashSet<int>();
            while (node >= 0 && node < Asset.Nodes.Count && visited.Add(node)) { node = Asset.Nodes[node].ParentIndex; if (node == sought) return true; }
            return false;
        }
    }
    protected void InvalidateModelGeometry()
    {
        Asset.RecalculateBounds(); Surface.FloorHeight = ModelBounds.Min.Y; _gridDirty = _gpuDirty = true;
        _timeline.ResetPreviews();
    }

    private void RefreshDetails()
    {
        Vector3 size = ModelBounds.Max - ModelBounds.Min;
        _details.Text = $"MODEL INFORMATION\n\n{Asset.Meshes.Count} meshes · {Asset.Materials.Count} materials\n{Asset.Meshes.Sum(m => m.IsSkinned ? m.SkinnedVertices.Length : m.Vertices.Length):N0} vertices\n{Asset.Meshes.Sum(m => m.Indices.Length / 3):N0} triangles\n{Asset.Animations.Count} animations · {Asset.Rig.Bones.Count} joints\n\nDimensions\nX {size.X:0.###}   Y {size.Y:0.###}   Z {size.Z:0.###}\n\nSource\n{ResourceDisplayName.Format(StudioModelResourceLoader.ResolveSource(ResourcePath))}";
        if (_materials.SelectedIndex >= 0 && _materials.SelectedIndex < Asset.Materials.Count)
        {
            var material = Asset.Materials[_materials.SelectedIndex];
            _details.Text += $"\n\n{material.Name}\nRoughness {material.RoughnessFactor:0.##} · Metallic {material.MetallicFactor:0.##}\n{(string.IsNullOrEmpty(material.AlbedoTexture) ? "No colour texture" : ResourceDisplayName.Format(material.AlbedoTexture))}";
        }
        LayoutSidebar();
    }

    public void SetShading(ModelPreviewShading shading) { _shading = shading; _shadingButton.Text = shading.ToString(); Surface.Invalidate(true); }
    public void SetGround(EditorFloorStyle style) { Surface.FloorStyle = style; _showGrid = style == EditorFloorStyle.GridOnly; Surface.Invalidate(true); }
    public void SetOrthographic(bool enabled) { _orthographic = enabled; _projectionButton.Text = enabled ? "Orthographic" : "Perspective"; Surface.Invalidate(true); }
    public void SetLightingPreset(string name)
    {
        Surface.ViewSettings.Lighting = Surface.ViewSettings.Shadows = null;
        _lighting = name != "Unlit"; _shadows = name == "Studio"; _ambient = name == "Soft" ? .85f : .55f; _keyIntensity = name == "Soft" ? .65f : 1.4f;
        _lightingButton.Text = name == "Unlit" ? "Lighting: off" : "Lighting"; Surface.Invalidate(true);
    }
    public void SetCameraView(string name)
    {
        _spinButton.Checked = false; _spin = 0;
        (float yaw, float pitch) = name switch { "Front" => (MathF.PI, 0), "Back" => (0, 0), "Left" => (MathF.PI / 2, 0), "Right" => (-MathF.PI / 2, 0), "Top" => (MathF.PI, -MathF.PI / 2 + .01f), "Bottom" => (MathF.PI, MathF.PI / 2 - .01f), _ => (MathF.PI - .6f, -.35f) };
        Surface.Camera.Yaw = yaw; Surface.Camera.Pitch = pitch; Surface.Invalidate(true);
    }
    public void FrameModel()
    {
        var (min, max) = ModelBounds;
        float radius = Math.Max(.1f, Vector3.Distance(min, max) * .5f);
        Surface.Camera.Target = (min + max) * .5f;
        float aspect = Math.Max(.3f, Surface.ClientSize.Width / (float)Math.Max(1, Surface.ClientSize.Height));
        float angle = Math.Min(.5f, MathF.Atan(MathF.Tan(.5f) * aspect));
        Surface.Camera.Distance = radius * 1.16f / MathF.Sin(angle);
        Surface.NearPlane = Math.Max(.0001f, radius / 1000); Surface.FarPlane = Math.Max(100, radius * 50);
        if (!_gridSizeInitialized) { _gridSize = MathF.Pow(10, MathF.Ceiling(MathF.Log10(Math.Max(.001f, radius)))) / 2; _gridSizeInitialized = true; }
        _gridDirty = true; Surface.Invalidate(true);
    }
    private EditorCameraOverride CameraOverride()
    {
        if (!_orthographic) return new EditorCameraOverride(Surface.Camera.View, Matrix4x4.CreatePerspectiveFieldOfView(Surface.FieldOfViewDegrees * MathF.PI / 180,
            Math.Max(.01f, Surface.Width / (float)Math.Max(1, Surface.Height)), Surface.NearPlane, Surface.FarPlane), Surface.Camera.Eye, Surface.Camera.Forward);
        float height = Math.Max(.01f, Surface.Camera.Distance);
        return new EditorCameraOverride(Surface.Camera.View, Matrix4x4.CreateOrthographic(height * Surface.Width / Math.Max(1, Surface.Height), height, Surface.NearPlane, Surface.FarPlane), Surface.Camera.Eye, Surface.Camera.Forward);
    }

    public void SelectClip(string name)
    {
        _clip = Asset.Animations.Any(c => c.Name == name) ? name : ""; _time = 0; SetPlaying(false);
        _syncing = true; _clips.SelectedIndex = string.IsNullOrEmpty(_clip) ? 0 : _clips.Items.IndexOf(_clip); _syncing = false;
        UpdatePlayback();
    }
    public void SetPlaying(bool playing) { _playing = playing && SelectedClip is { Frames.Count: > 0 }; _play.Text = _playing ? "Pause" : "Play"; _lastTick = Stopwatch.GetTimestamp(); }
    public void SetFrame(int frame) { _time = Math.Clamp(frame, 0, Math.Max(0, (SelectedClip?.Frames.Count ?? 1) - 1)) / Math.Max(1, SelectedClip?.Fps ?? 30); UpdatePlayback(); Surface.Invalidate(true); }
    public void AdvancePreview(float seconds)
    {
        if (!_playing || SelectedClip is not { } clip) return;
        _time += Math.Max(0, seconds);
        float duration = clip.Frames.Count / Math.Max(1, clip.Fps);
        if (_time >= duration) { if (Loop) _time %= Math.Max(.001f, duration); else { _time = (clip.Frames.Count - 1) / Math.Max(1, clip.Fps); SetPlaying(false); } }
        UpdatePlayback();
    }
    private void UpdatePlayback()
    {
        _syncing = true;
        int count = SelectedClip?.Frames.Count ?? 0;
        _play.Enabled = count > 0; _timeline.Enabled = count > 0; _timeline.Maximum = Math.Max(0, count - 1);
        _timeline.SetSource(Asset, SelectedClip, ProjectRoot);
        _timeline.Value = Math.Clamp(CurrentFrame, 0, _timeline.Maximum);
        _timeline.PoseFrames = Asset.PoseAnimations.FirstOrDefault(a => a.Id == SelectedClip?.PoseAnimationId)?.Keys.Select(k => k.Frame - 1).ToArray() ?? []; _timeline.Invalidate();
        _frameLabel.Text = count == 0 ? "No animation" : $"Frame {Math.Min(count, CurrentFrame + 1)} / {count}\n{SelectedClip!.Fps:0.#} FPS \u00B7 {_time:0.00}s";
        _syncing = false;
    }
    private void TickPreview()
    {
        long now = Stopwatch.GetTimestamp(); float elapsed = (float)Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds; _lastTick = now;
        AdvancePreview(Math.Clamp(elapsed, 0, .1f));
        if (_spinButton.Checked) _spin += elapsed * .4f;
        _orientation.Invalidate();
        if (!_importing) Status.Text = DateTime.UtcNow < _motionFeedbackUntil ? LastMotionImportMessage : $"{(IsDirty ? "Unsaved \u00B7 " : "")}{(_orthographic ? "Orthographic" : "Perspective")} \u00B7 {_shading} \u00B7 RMB orbit \u00B7 MMB pan \u00B7 Wheel zoom \u00B7 Ground at {GroundHeight:0.###}";
    }

    protected virtual void DrawPreview(IRenderController renderer)
    {
        if (_gpuDirty) { PreviewRenderer.InvalidateAssets(renderer); _gpuDirty = false; }
        DrawGrid(renderer);
        if (!Asset.HasRenderableMeshes) return;
        var center = (ModelBounds.Min + ModelBounds.Max) * .5f;
        Matrix4x4 world = Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationY(_spin) * Matrix4x4.CreateTranslation(center);
        PreviewRenderer.DrawAsset(Asset, ProjectRoot, world, new RuntimeModelAnimationState(_clip, _time, SelectedClip?.Fps ?? 30, Loop, ignoreTextures: _shading != ModelPreviewShading.Textured), renderer);
    }
    protected virtual Mesh3DState CreateSceneState()
    {
        var state = EditorSceneLighting.Create(Surface.FloorStyle.DrawsPlate(), _shadows);
        state.BackgroundColor = new Vector3(EditorChrome.Canvas.R, EditorChrome.Canvas.G, EditorChrome.Canvas.B) / 255;
        state.FogEnabled = state.FogScreenSpace = false; state.FloorFollowsCamera = false;
        state.Wireframe = _shading == ModelPreviewShading.Wireframe; state.LightingEnabled = _lighting;
        state.AmbientColor = new Vector3(_ambient); state.AmbientGroundColor = new Vector3(_ambient * .65f);
        state.SunIntensity = _keyIntensity;
        float angle = _lightAngle * MathF.PI / 180;
        state.LightDirection = Vector3.Normalize(new Vector3(MathF.Sin(angle), -1, MathF.Cos(angle)));
        return state;
    }

    private void DrawGrid(IRenderController renderer)
    {
        if (!_showGrid) return;
        if (_gridDirty || !_gridMesh.IsValid)
        {
            if (_gridMesh.IsValid) renderer.ReleaseMesh(_gridMesh);
            var vertices = new List<MeshVertex>(); var indices = new List<ushort>();
            float extent = _gridSize * 12, thickness = _gridSize * .003f;
            var color = new Vector4(_gridColor.R / 255f, _gridColor.G / 255f, _gridColor.B / 255f, .65f);
            var center = (ModelBounds.Min + ModelBounds.Max) * .5f;
            for (int i = -12; i <= 12; i++)
            {
                Quad(i * _gridSize - thickness, -extent, i * _gridSize + thickness, extent);
                Quad(-extent, i * _gridSize - thickness, extent, i * _gridSize + thickness);
            }
            _gridMesh = renderer.RegisterMesh(vertices.ToArray(), indices.ToArray()); _gridDirty = false;
            void Quad(float x0, float z0, float x1, float z1)
            {
                int start = vertices.Count;
                foreach (var point in new[] { new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1) })
                {
                    Vector3 radians = _gridRotation * (MathF.PI / 180);
                    Vector3 rotated = Vector3.Transform(point, Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z));
                    vertices.Add(new MeshVertex { Position = rotated + new Vector3(center.X, GroundHeight - _gridSize * .002f, center.Z), Normal = Vector3.UnitY, Color = color });
                }
                foreach (int index in new[] { 0, 2, 1, 0, 3, 2 }) indices.Add((ushort)(start + index));
            }
        }
        renderer.DrawMesh(new MeshDrawCall { Mesh = _gridMesh, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = .7f, Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog });
    }

    private async void ChooseImport()
    {
        using var dialog = new OpenFileDialog { Title = "Import Model", Filter = ModelSourceConversion.FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _importing = true; Commands.Enabled = Menus.Enabled = false; Status.Text = "Importing model\u2026"; UseWaitCursor = true;
        try { var imported = await Task.Run(() => PrepareImport(dialog.FileName)); if (!IsDisposed) CommitImport(imported.Asset, imported.Source); }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Model import failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (!IsDisposed) { _importing = false; Commands.Enabled = Menus.Enabled = true; UseWaitCursor = false; } }
    }
    public string ImportExternalModel(string source)
    {
        var imported = PrepareImport(source); CommitImport(imported.Asset, imported.Source); return ResourcePath;
    }
    private (GModelAsset Asset, string Source) PrepareImport(string source)
    {
        string data = ResourceAssociates.GetModelDataDirectory(ResourcePath);
        string folder = System.IO.Path.Combine(data, "import-" + Guid.NewGuid().ToString("N"));
        string copied = ResourceService.CopyModelSource(source, folder);
        string draft = System.IO.Path.Combine(folder, "validation.model.json");
        try
        {
            File.WriteAllText(draft, new JsonObject { ["schemaVersion"] = 5, ["source"] = System.IO.Path.GetFileName(copied), ["parts"] = new JsonArray() }.ToJsonString());
            var asset = StudioModelResourceLoader.Load(draft);
            if (asset.ImportRequired || !asset.HasRenderableMeshes) throw new InvalidDataException(string.IsNullOrWhiteSpace(asset.ImportMessage) ? "The file contains no renderable geometry." : asset.ImportMessage);
            return (asset, System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(ResourcePath)!, copied).Replace('\\', '/'));
        }
        finally { if (File.Exists(draft)) File.Delete(draft); string canonical = StudioModelResourceLoader.CanonicalPath(draft); if (File.Exists(canonical)) File.Delete(canonical); }
    }
    private void CommitImport(GModelAsset imported, string source)
    {
        string canonical = StudioModelResourceLoader.CanonicalPath(ResourcePath);
        byte[]? previous = File.Exists(canonical) ? File.ReadAllBytes(canonical) : null;
        string original = File.ReadAllText(ResourcePath);
        var document = JsonNode.Parse(original)!.AsObject();
        document["schemaVersion"] = 5; document["source"] = source; document["parts"] = new JsonArray();
        document.Remove("primitive"); document["rig"] = imported.Rig.IsValid ? "Canonical" : null;
        document["animations"] = new JsonArray(imported.Animations.Select(c => (JsonNode?)JsonValue.Create(c.Name)).ToArray());
        try { ResourceBackupService.BackupBeforeOverwrite(canonical); StudioModelResourceLoader.SaveCanonical(ResourcePath, imported); WriteResourceText(document.ToJsonString(new() { WriteIndented = true })); }
        catch { if (previous is null) File.Delete(canonical); else File.WriteAllBytes(canonical, previous); File.WriteAllText(ResourcePath, original); throw; }
        Asset = imported; ClearEditHistory(); AcceptSave(); RefreshAssetPresentation(); OnModelImported(); FrameModel();
    }
    protected virtual void OnModelImported() { }
    private void LocateBlender()
    {
        using var dialog = new OpenFileDialog { Title = "Locate Blender", Filter = "Blender|blender.exe" };
        if (dialog.ShowDialog(this) == DialogResult.OK) ModelSourceConversion.ConfigureBlender(dialog.FileName);
    }
    private void ShowGridSettings() => SettingsWindow("3D Grid", [
        ("Cell size", .001m, 10000m, (decimal)_gridSize, value => { _gridSize = (float)value; _gridDirty = true; }),
        ("Rotation X", -180m, 180m, (decimal)_gridRotation.X, value => { _gridRotation.X = (float)value; _gridDirty = true; }),
        ("Rotation Y", -180m, 180m, (decimal)_gridRotation.Y, value => { _gridRotation.Y = (float)value; _gridDirty = true; }),
        ("Rotation Z", -180m, 180m, (decimal)_gridRotation.Z, value => { _gridRotation.Z = (float)value; _gridDirty = true; })], true);
    private void ShowLightingSettings() => SettingsWindow("Lighting", [
        ("Key intensity", 0m, 8m, (decimal)_keyIntensity, value => _keyIntensity = (float)value),
        ("Ambient", 0m, 2m, (decimal)_ambient, value => _ambient = (float)value),
        ("Light direction", -180m, 180m, (decimal)_lightAngle, value => _lightAngle = (float)value)], false);
    private void SettingsWindow(string title, (string Label, decimal Min, decimal Max, decimal Value, Action<decimal> Set)[] values, bool colour)
    {
        using var form = new Form { Text = title, ClientSize = new Size(350, 124 + values.Length * 40), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
        foreach (var value in values)
        {
            var row = new FlowLayoutPanel { Width = 320, Height = 34 };
            row.Controls.Add(new Label { Text = value.Label, Width = 145, Height = 28 });
            var number = new NumericUpDown { Minimum = value.Min, Maximum = value.Max, DecimalPlaces = 3, Value = Math.Clamp(value.Value, value.Min, value.Max), Width = 140, Increment = value.Max > 100 ? 1 : .05m };
            EditorChrome.StyleField(number); number.ValueChanged += (_, _) => { value.Set(number.Value); Surface.Invalidate(true); }; row.Controls.Add(number); layout.Controls.Add(row);
        }
        if (colour) { var pick = new Button { Text = "Grid colour\u2026", Width = 290, Height = 30 }; pick.Click += (_, _) => { using var dialog = new ColorDialog { Color = _gridColor }; if (dialog.ShowDialog(form) == DialogResult.OK) { _gridColor = dialog.Color; _gridDirty = true; } }; layout.Controls.Add(pick); }
        else { var shadows = new CheckBox { Text = "Shadows", Checked = _shadows, AutoSize = true }; shadows.CheckedChanged += (_, _) => _shadows = shadows.Checked; layout.Controls.Add(shadows); }
        var close = new Button { Text = "Done", Width = 290, Height = 32, DialogResult = DialogResult.OK }; layout.Controls.Add(close); form.AcceptButton = close; form.CancelButton = close; form.Controls.Add(layout); form.ShowDialog(this);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_importing) return true;
        if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
        if (keyData == Keys.F) { FrameModel(); return true; }
        if (keyData == Keys.Space) { SetPlaying(!_playing); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnChromeChanged() { base.OnChromeChanged(); ApplyTheme(); }
    private void ApplyTheme()
    {
        Menus.BackColor = EditorChrome.Raised; Menus.ForeColor = EditorChrome.Text; Menus.Font = EditorChrome.BaseFont;
        Commands.BackColor = EditorChrome.Raised; Commands.ForeColor = EditorChrome.Text; Commands.Font = EditorChrome.BaseFont;
        foreach (Control control in new Control[] { LeftPanel, _hierarchy, _materials, _details, Status }) { control.BackColor = EditorChrome.Surface; control.ForeColor = EditorChrome.Text; control.Font = EditorChrome.BaseFont; }
        _timeline.BackColor = EditorChrome.Surface;
    }
    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes) { base.OnAssetDependenciesChanged(changes); Asset = StudioModelResourceLoader.Load(ResourcePath); RefreshAssetPresentation(); }
    protected static ToolStrip Strip(string name, int height)
    {
        var strip = ImageEditorChrome.MakeCommandStrip(); strip.Name = name; strip.Dock = DockStyle.Fill; strip.Height = height;
        strip.Font = EditorChrome.BaseFont;
        strip.Padding = height <= 32 ? new Padding(8, 2, 8, 2) : new Padding(8, 4, 8, 4);
        return strip;
    }
    protected static ToolStripDropDownButton Menu(string title, bool showArrow = false) =>
        new(title)
        {
            ForeColor = EditorChrome.Text,
            ShowDropDownArrow = showArrow,
            Padding = showArrow ? new Padding(6, 4, 6, 4) : new Padding(6, 2, 6, 2),
            Margin = new Padding(1, 0, 1, 0),
        };
    protected static ToolStripButton Button(string title, Action click)
    {
        var button = new ToolStripButton(title) { Overflow = ToolStripItemOverflow.Never, Padding = new Padding(6, 4, 6, 4), Margin = new Padding(1, 0, 1, 0) };
        button.Click += (_, _) => click(); return button;
    }
}

using System.Drawing;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Assets;

public enum ModelEditorRole { Viewer, Composer }
public enum ModelEditorMode { Compose, Sculpt, Paint, Mesh, Rig, Animate }

/// <summary>Model authoring rebuilt around the dedicated Viewer and retained animation workspace.</summary>
public sealed partial class ModelEditorControl : ModelViewerControl
{
    private readonly ListBox _groups = new() { Name = "ModelMeshGroups", Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly ThemedComboBox _material = new() { Name = "ModelMaterialPicker", Width = 250 };
    private readonly Label _currentTool = new() { Width = 230, Height = 42, Text = "Select mesh", Padding = new Padding(0, 8, 0, 0) };
    private readonly Button _colour = new() { Text = "Brush colour…", Width = 235, Height = 36 };
    private readonly TextBox _groupName = new() { Width = 242 };
    private readonly CheckBox _visibleGroup = new() { Text = "Show selected group", AutoSize = true, Checked = true };
    private NumericUpDown? _radiusInput, _strengthInput;
    private readonly List<ModelPart> _parts = [];
    private readonly RuntimeModelRenderSystem _miniRenderer = new();
    private readonly EditorViewport3D _mini = new() { Dock = DockStyle.Fill, NavigationEnabled = false, FloorStyle = EditorFloorStyle.None };
    private readonly HashSet<int> _hiddenGroups = [];
    private ModelEditorMode _mode;
    private ModelBrushKind _brush = ModelBrushKind.Draw;
    private Vector4 _paintColour = new(.85f, .2f, .3f, 1);
    private float _radius = .3f, _strength = .08f;
    private bool _stroke, _syncingGroups, _previewChanged = true, _onion;
    private MeshVertex[][]? _beforeStroke;
    private int _strokeMesh = -1;
    private bool _didStroke;
    private GModelAsset? _filteredPreview;

    public ModelEditorControl(string path, string projectRoot, ModelEditorRole role = ModelEditorRole.Composer) : base(path, projectRoot)
    {
        Role = role; _parts.AddRange(ModelAssetLoader.LoadParts(path));
        if (role == ModelEditorRole.Viewer) return;
        EmptyModelHintEnabled = false;
        RefreshAssetPresentation();
        var oldOpen = Commands.Items.Cast<ToolStripItem>().Single(i => i.Name == "OpenModelEditor"); Commands.Items.Remove(oldOpen); oldOpen.Dispose();
        Commands.Items[0].Text = "◆  " + ResourceDisplayName.Format(ResourcePath);
        Commands.Items[0].ToolTipText = ResourcePath;
        var save = Button("Save", Save); save.Alignment = ToolStripItemAlignment.Right; save.BackColor = EditorChrome.Accent; Commands.Items.Add(save);
        var file = (ToolStripDropDownButton)Menus.Items[0];
        var redundantOpen = file.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "Open Model Editor"); file.DropDownItems.Remove(redundantOpen); redundantOpen.Dispose();
        var edit = Menu("Edit"); edit.DropDownItems.Add("Undo", null, (_, _) => Undo()); edit.DropDownItems.Add("Redo", null, (_, _) => Redo());
        edit.DropDownItems.Add("Transform selected mesh…", null, (_, _) => ShowTransformWindow());
        edit.DropDownItems.Add("Delete selected mesh", null, (_, _) => DeleteSelectedPart()); Menus.Items.Insert(1, edit);
        var create = Menu("Create"); foreach (ModelPrimitiveKind kind in Enum.GetValues<ModelPrimitiveKind>()) create.DropDownItems.Add(kind.ToString(), null, (_, _) => ArmPrimitivePlacement(kind)); Menus.Items.Insert(2, create);
        var animation = Menus.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Animation"); animation.DropDownItems.Clear();
        foreach (var page in new[] { ("Rigging…", 0), ("Posing…", 1), ("Animation…", 2) }) animation.DropDownItems.Add(page.Item1, null, (_, _) => OpenAnimation(page.Item2));
        BuildModeCommandBar(); BuildToolbox(); BuildAuthoringInspector();
        _groups.DrawMode = DrawMode.OwnerDrawFixed; _groups.ItemHeight = 28;
        _groups.DrawItem += (_, e) =>
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? EditorChrome.Accent : EditorChrome.Surface); e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index >= 0 && e.Index < _groups.Items.Count)
            {
                Rectangle textBounds = Rectangle.Inflate(e.Bounds, -8, 0);
                textBounds.Width -= 28;
                TextRenderer.DrawText(e.Graphics, _groups.Items[e.Index]?.ToString(), EditorChrome.BaseFont, textBounds,
                    selected ? Color.White : EditorChrome.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                string eye = _hiddenGroups.Contains(e.Index) ? "○" : "●";
                TextRenderer.DrawText(e.Graphics, eye, EditorChrome.BaseFont,
                    new Rectangle(e.Bounds.Right - 30, e.Bounds.Top, 24, e.Bounds.Height),
                    _hiddenGroups.Contains(e.Index) ? EditorChrome.Muted : Color.FromArgb(102, 219, 255),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        };
        Surface.Host.MouseDown += (_, e) => PointerDown(e.Location, e.Button);
        Surface.Host.MouseMove += (_, e) => PointerMove(e.Location, e.Button);
        Surface.Host.MouseLeave += (_, _) => _hover = null;
        Surface.Host.MouseUp += (_, e) => PointerUp(e.Location, e.Button);
        Surface.DrawOverlay += DrawBrushCursor;
        Surface.DrawOverlay += DrawAuthoringOverlay;
        Surface.DrawOverlay += DrawOrganicSelectionOverlay;
        Surface.DrawOverlay += DrawMeshSelectionOverlay;
        Surface.MiddleButtonPans = true;
        RefreshGroups();
        Disposed += (_, _) =>
        {
            if (_mini.Host.Renderer is { } renderer) _miniRenderer.InvalidateAssets(renderer);
            _frameAnimation?.Stop();
            _frameAnimation?.Dispose();
        };
    }

    public ModelEditorRole Role { get; }
    public bool IsViewer => Role == ModelEditorRole.Viewer;
    public bool IsComposer => !IsViewer;
    public ModelEditorMode Mode => _mode;
    public GModelAsset RiggedAsset => Asset;
    public int BakedVertexCount => Asset.Meshes.Sum(m => m.IsSkinned ? m.SkinnedVertices.Length : m.Vertices.Length);
    public int BakedTriangleCount => Asset.Meshes.Sum(m => m.Indices.Length / 3);
    public int CanonicalMeshCount => Asset.Meshes.Count;
    public int SourceNodeCount => Asset.Nodes.Count;
    public int MaterialCount => Asset.Materials.Count;
    public int AnimationClipCount => Asset.Animations.Count;
    public bool HasPersistedSkin => Asset.Rig.IsValid && Asset.Meshes.Any(m => m.IsSkinned);
    public string CanonicalModelPath => StudioModelResourceLoader.CanonicalPath(ResourcePath);
    public string ExternalSourcePath => StudioModelResourceLoader.ResolveSource(ResourcePath);
    public IReadOnlyList<ModelPart> Parts => _parts;
    public ModelPart? SelectedPart => _groups.SelectedIndex >= 0 && _groups.SelectedIndex < _parts.Count ? _parts[_groups.SelectedIndex] : null;
    public int DisplayedPartCount => Asset.Meshes.Count;
    public int OutlinerNodeCount => _groups.Items.Count;
    public string OutlinerSelectionText => _groups.SelectedItem?.ToString() ?? "";
    public MeshVertex[] BakedVerticesForTest => Asset.Meshes.SelectMany(Vertices).ToArray();
    public ushort[] BakedIndicesForTest => Asset.Meshes.FirstOrDefault()?.Indices ?? [];
    public (Vector3 Min, Vector3 Max) BakedBounds() => ModelBounds;
    public void FrameModelForTest() => FrameModel();
    public string ImportAndOpenModel(string path) => ImportExternalModel(path);
    protected override void OnModelImported() { _parts.Clear(); _hiddenGroups.Clear(); _selection.Clear(); _previewChanged = true; _filteredPreview = null; if (IsComposer) { RefreshGroups(); RefreshMorphInspector(); RefreshSocketInspector(); } }
    protected override void OnMotionImported() { _previewChanged = true; _filteredPreview = null; if (IsComposer) RefreshGroups(); }

    private void FitMini() { _mini.Camera.Target = (ModelBounds.Min + ModelBounds.Max) * .5f; _mini.Camera.Distance = Math.Max(.1f, Vector3.Distance(ModelBounds.Min, ModelBounds.Max) * 1.6f); _mini.Camera.Yaw = MathF.PI - .6f; _mini.Camera.Pitch = -.25f; }
    private void RefreshGroups()
    {
        _syncingGroups = true; int selected = _groups.SelectedIndex; _groups.Items.Clear(); _groups.Items.AddRange(Asset.Meshes.Select(m => (object)m.Name).ToArray());
        if (_groups.Items.Count > 0) _groups.SelectedIndex = Math.Clamp(selected, 0, _groups.Items.Count - 1);
        _material.Items.Clear(); _material.Items.AddRange(Asset.Materials.Select(m => (object)m.Name).ToArray()); _syncingGroups = false; SyncSelectedGroup(); FitMini();
    }
    private void SyncSelectedGroup()
    {
        if (_groups.SelectedIndex < 0 || _groups.SelectedIndex >= Asset.Meshes.Count) return;
        _syncingGroups = true; var mesh = Asset.Meshes[_groups.SelectedIndex]; _groupName.Text = mesh.Name;
        _visibleGroup.Checked = !_hiddenGroups.Contains(_groups.SelectedIndex);
        _material.SelectedIndex = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < _material.Items.Count ? mesh.MaterialIndex : -1; _syncingGroups = false;
        RefreshMaterialInspector();
        RefreshOrganicInspector();
    }
    private void RenameGroup() => RenameSelectedGroup(_groupName.Text);
    public void RenameSelectedGroup(string name) { if (_groups.SelectedIndex >= 0 && !string.IsNullOrWhiteSpace(name)) ChangeAsset("Rename group", () => Asset.Meshes[_groups.SelectedIndex].Name = name.Trim()); }
    public void AddMaterial(string name)
    {
        ChangeAsset("New material", () => { Asset.Materials.Add(new GModelMaterial { Name = name }); if (_groups.SelectedIndex >= 0) SetMaterial(Asset.Materials.Count - 1); });
    }
    public void AssignMaterial(int index) { if (_groups.SelectedIndex >= 0 && index >= 0 && index < Asset.Materials.Count) ChangeAsset("Assign material", () => SetMaterial(index)); }
    private void SetMaterial(int index) { var mesh = Asset.Meshes[_groups.SelectedIndex]; mesh.MaterialIndex = index; mesh.TriangleMaterialIndices = []; }
    private void ChooseColour()
    {
        using var picker = new ColorDialog { Color = Color.FromArgb((int)(_paintColour.X * 255), (int)(_paintColour.Y * 255), (int)(_paintColour.Z * 255)), FullOpen = true };
        if (picker.ShowDialog(this) == DialogResult.OK) SetPaintColor(new Vector4(picker.Color.R / 255f, picker.Color.G / 255f, picker.Color.B / 255f, 1));
    }
    private void ChooseTexture()
    {
        if (_groups.SelectedIndex < 0) return;
        ProjectAssetEntry? picked = Genesis.Application.Editors.Suite.Inspector.AssetPickerService.PickAsset(
            new Genesis.Application.Editors.Suite.Inspector.AssetPickerRequest(ProjectRoot, Genesis.Application.Core.Resources.ResourceKind.Image,
                null, "Choose Model Material Image"), FindForm());
        if (picked is null) return;
        string? pixels = ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, picked.Reference);
        if (string.IsNullOrWhiteSpace(pixels)) return;
        try { AssignSelectedTexture(pixels); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Assign texture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    public void AssignSelectedTexture(string source)
    {
        if (_groups.SelectedIndex < 0) throw new InvalidOperationException("Select a mesh first.");
        if (!File.Exists(source)) throw new FileNotFoundException("The selected texture no longer exists.", source);
        string folder = System.IO.Path.Combine(Genesis.Application.Core.Resources.ResourceAssociates.GetModelDataDirectory(ResourcePath), "Textures"); Directory.CreateDirectory(folder);
        string output = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(source)); File.Copy(source, output);
        ChangeAsset("Assign texture", () =>
        {
            int material = EnsureSelectedMaterial();
            Asset.Materials[material].AlbedoTexture = System.IO.Path.GetRelativePath(ProjectRoot, output).Replace('\\', '/');
        });
    }
    private void EnsureMaterial() { if (Asset.Materials.Count == 0) Asset.Materials.Add(CreateNeutralMaterial()); foreach (var mesh in Asset.Meshes) mesh.MaterialIndex = Math.Clamp(mesh.MaterialIndex, 0, Asset.Materials.Count - 1); }
    private int EnsureEditableMaterial(string name)
    {
        int selected = _groups.SelectedIndex >= 0 && _groups.SelectedIndex < Asset.Meshes.Count
            ? Asset.Meshes[_groups.SelectedIndex].MaterialIndex : -1;
        if (selected >= 0 && selected < Asset.Materials.Count && IsNeutralMaterial(Asset.Materials[selected]))
        {
            Asset.Materials[selected].Name = string.IsNullOrWhiteSpace(name) ? "Material" : name;
            return selected;
        }
        Asset.Materials.Add(CreateNeutralMaterial(string.IsNullOrWhiteSpace(name) ? "Material" : name));
        return Asset.Materials.Count - 1;
    }
    private int EnsureSelectedMaterial()
    {
        int mesh = _groups.SelectedIndex;
        if (mesh < 0 || mesh >= Asset.Meshes.Count) throw new InvalidOperationException("Select a mesh first.");
        int material = Asset.Meshes[mesh].MaterialIndex;
        if (material >= 0 && material < Asset.Materials.Count) return material;
        material = NeutralMaterialIndex();
        Asset.Meshes[mesh].MaterialIndex = material;
        Asset.Meshes[mesh].TriangleMaterialIndices = [];
        return material;
    }
    private int NeutralMaterialIndex()
    {
        string name = "Default_PBR";
        int suffix = 2;
        while (Asset.Materials.Any(material => string.Equals(material.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = "Default_PBR " + suffix++;
        Asset.Materials.Add(CreateNeutralMaterial(name));
        return Asset.Materials.Count - 1;
    }
    private static bool IsNeutralMaterial(GModelMaterial material) =>
        string.IsNullOrWhiteSpace(material.AlbedoTexture)
        && string.IsNullOrWhiteSpace(material.NormalTexture)
        && string.IsNullOrWhiteSpace(material.MetallicRoughnessTexture)
        && string.IsNullOrWhiteSpace(material.EmissiveTexture);
    private static GModelMaterial CreateNeutralMaterial(string name = "Default_PBR") => new()
    {
        Name = name,
        BaseColor = new Vector4(200f / 255f, 200f / 255f, 200f / 255f, 1f),
        MetallicFactor = 0f,
        RoughnessFactor = .72f,
    };
    private static ushort[] MakeDoubleSided(IReadOnlyList<ushort> source)
    {
        ushort[] result = new ushort[source.Count * 2];
        for (int index = 0; index + 2 < source.Count; index += 3)
        {
            result[index] = source[index];
            result[index + 1] = source[index + 1];
            result[index + 2] = source[index + 2];
            int reverse = source.Count + index;
            result[reverse] = source[index];
            result[reverse + 1] = source[index + 2];
            result[reverse + 2] = source[index + 1];
        }
        return result;
    }
    public void SetMode(ModelEditorMode mode)
    {
        FinishStroke(); AllowSpin(mode is not (ModelEditorMode.Paint or ModelEditorMode.Sculpt)); _mode = mode; SelectClip("");
        if (mode == ModelEditorMode.Paint) SetShading(ModelPreviewShading.Textured);
        RefreshToolboxVisibility(mode);
        UpdateToolHeader();
        Surface.NavigationEnabled = true;
    }

    private void BuildModeCommandBar()
    {
        if (Commands.Items.Count > 0) Commands.Items[0].Visible = true;
        ToolStripItem? oldFrame = Commands.Items.Cast<ToolStripItem>().FirstOrDefault(item => item.Text == "Frame");
        if (oldFrame is not null) { Commands.Items.Remove(oldFrame); oldFrame.Dispose(); }
        int insert = Math.Min(1, Commands.Items.Count);
        _cameraControlButton = new ToolStripDropDownButton("Camera: Orbit")
        {
            Name = "ModelCameraControl",
            ToolTipText = "Choose orbit or free-flight viewport navigation",
        };
        foreach ((EditorCameraControlMethod mode, string caption) in new[]
                 {
                     (EditorCameraControlMethod.Orbit, "Orbit"),
                     (EditorCameraControlMethod.Free, "Free Camera"),
                 })
        {
            EditorCameraControlMethod captured = mode;
            _cameraControlButton.DropDownItems.Add(caption, null, (_, _) => SetCameraControl(captured));
        }
        Commands.Items.Insert(insert++, _cameraControlButton);
        Commands.Items.Insert(insert++, Button("Frame  F", FrameSelection));
        Commands.Items.Insert(insert++, GizmoButton("Move  W", EditorGizmoMode.Move));
        Commands.Items.Insert(insert++, GizmoButton("Rotate  R", EditorGizmoMode.Rotate));
        Commands.Items.Insert(insert++, GizmoButton("Scale  S", EditorGizmoMode.Scale));
        Commands.Items.Insert(insert++, new ToolStripSeparator());
        ToolStripButton playback = Button("▶ / ⏸", () => SetPlaying(!IsPlaying));
        playback.ToolTipText = "Play or pause the selected animation clip (Space)";
        Commands.Items.Insert(insert++, playback);
        ToolStripButton stop = Button("■", () => { SetPlaying(false); SetFrame(0); });
        stop.ToolTipText = "Stop playback and return to the first frame";
        Commands.Items.Insert(insert, stop);
        SetMode(ModelEditorMode.Compose);
        SetMeshGizmoMode(EditorGizmoMode.Move);

        ToolStripButton GizmoButton(string caption, EditorGizmoMode mode)
        {
            ToolStripButton button = Button(caption, () => SetMeshGizmoMode(mode));
            button.Name = "ModelGizmo" + mode;
            button.CheckOnClick = false;
            _meshGizmoButtons[mode] = button;
            return button;
        }
    }

    private void SetCameraControl(EditorCameraControlMethod method)
    {
        Surface.ControlMethod = method;
        Surface.MiddleButtonPans = true;
        if (_cameraControlButton is not null) _cameraControlButton.Text = method == EditorCameraControlMethod.Free ? "Camera: Free" : "Camera: Orbit";
        Status.Text = method == EditorCameraControlMethod.Free
            ? "Free Camera · RMB look · MMB pan · wheel travel · WASD fly"
            : "Orbit · RMB orbit · MMB pan · wheel zoom";
    }
    public void SetBrush(ModelBrushKind brush) => _brush = brush;
    public void SetBrushRadius(float radius) { _radius = Math.Clamp(radius, .001f, 1000); if (_radiusInput is not null) _radiusInput.Value = (decimal)_radius; }
    public void SetBrushStrength(float strength) { _strength = Math.Clamp(strength, .001f, 1); if (_strengthInput is not null) _strengthInput.Value = (decimal)_strength; }
    public void SetGroupVisible(int index, bool visible)
    {
        if (index < 0 || index >= Asset.Meshes.Count) return;
        if (visible) _hiddenGroups.Remove(index); else _hiddenGroups.Add(index);
        _filteredPreview = null; _previewChanged = true; SyncSelectedGroup();
    }
    public void SetPaintColor(Vector4 colour) { _paintColour = Vector4.Clamp(colour, Vector4.Zero, Vector4.One); _colour.BackColor = Color.FromArgb((int)(_paintColour.X * 255), (int)(_paintColour.Y * 255), (int)(_paintColour.Z * 255)); }
    public void SelectPart(int index) { if (index >= 0 && index < _groups.Items.Count) _groups.SelectedIndex = index; }
    public void PointerDown(Point point, MouseButtons button)
    {
        if (button != MouseButtons.Left || IsOrientationHit(point)) return;
        if (TryCommitPrimitivePlacement(point)) return;
        if (TryBeginMeshGizmoDrag(point)) return;
        if (_mode == ModelEditorMode.Mesh && _tool is ModelAuthoringTool.Push or ModelAuthoringTool.Pull)
        {
            BeginPushPull(point);
            return;
        }
        if (BeginAuthoringGesture(point)) return;
        if (_mode == ModelEditorMode.Mesh)
        {
            SelectMeshElementAt(point, ModifierKeys);
            return;
        }
        if (!TryPickMesh(point, out int mesh, out _)) return;
        SelectPart(mesh);
        Surface.Host.Capture = true;
        if (_mode is not (ModelEditorMode.Sculpt or ModelEditorMode.Paint)) return;
        _strokeMesh = mesh; _beforeStroke = Asset.Meshes.Select(Vertices).ToArray();
        _beforeDynamicStroke = _dynamicTopology && _mode == ModelEditorMode.Sculpt ? ModelPoseWorkflow.Copy(Asset) : null;
        _stroke = true; _didStroke = false; Surface.NavigationEnabled = false; ApplyPointerBrush(point);
    }
    private void ApplyPointerBrush(Point point) { if (TryPickMesh(point, out _, out Vector3 hit)) _didStroke |= BrushAt(hit) > 0; }
    public bool TryPickSurface(Point point, out Vector3 hit) => TryPickMesh(point, out _, out hit);
    private bool TryPickMesh(Point point, out int meshIndex, out Vector3 hit)
    {
        var ray = Surface.PickRay(point); Vector3 origin = ray.Origin + Asset.Pivot.Position; float best = float.MaxValue; meshIndex = -1; hit = default;
        for (int i = 0; i < Asset.Meshes.Count; i++)
        {
            if (_hiddenGroups.Contains(i)) continue;
            var mesh = Asset.Meshes[i]; if (!ModelSurfaceBrush.Raycast(Vertices(mesh), mesh.Indices, origin, ray.Direction, out var candidate)) continue;
            float distance = Vector3.DistanceSquared(origin, candidate); if (distance >= best) continue;
            best = distance; hit = candidate; meshIndex = i;
        }
        return meshIndex >= 0;
    }
    public int BrushAt(Vector3 center)
    {
        int index = _stroke ? _strokeMesh : _groups.SelectedIndex; if (index < 0 || index >= Asset.Meshes.Count) return 0;
        GModelAsset? topologyBefore = !_stroke && _dynamicTopology && _mode == ModelEditorMode.Sculpt
            ? ModelPoseWorkflow.Copy(Asset) : null;
        int refined = 0;
        if (_dynamicTopology && _mode == ModelEditorMode.Sculpt)
        {
            try { refined = ModelPartBuilder.RefineUnderBrush(Asset.Meshes[index], center, _radius, _detailSize); }
            catch (InvalidOperationException exception) { Status.Text = exception.Message; }
            if (refined > 0) _selection.Clear();
        }
        var vertices = Asset.Meshes.Select(Vertices).ToArray();
        var before = _stroke ? null : vertices.Select(v => (MeshVertex[])v.Clone()).ToArray();
        int affected = ModelConnectedBrush.Apply(vertices, Asset.Meshes.Select(m => m.Indices).ToArray(), _mode == ModelEditorMode.Paint ? ModelBrushKind.Paint : _brush,
            center, _radius, _strength, _paintColour, (m,v) => !_hiddenGroups.Contains(m) && (_selection.Count == 0 || _selection.Contains((m,v))));
        if (affected == 0 && refined == 0) return 0;
        for (int m = 0; m < vertices.Length; m++) SetVertices(Asset.Meshes[m], vertices[m]);
        NotifyMeshChanged();
        if (!_stroke && topologyBefore is not null)
        {
            GModelAsset after = ModelPoseWorkflow.Copy(Asset);
            PushEdit("Dynamic topology sculpt", () => ReplaceAsset(ModelPoseWorkflow.Copy(after)), () => ReplaceAsset(ModelPoseWorkflow.Copy(topologyBefore)));
        }
        else if (!_stroke && before is not null) JournalVertices(before, vertices);
        return affected + refined;
    }
    public void FinishStroke()
    {
        if (!_stroke) return;
        _stroke = false; Surface.NavigationEnabled = true; Surface.Host.Capture = false;
        if (_didStroke && _beforeDynamicStroke is not null)
        {
            GModelAsset before = _beforeDynamicStroke;
            GModelAsset after = ModelPoseWorkflow.Copy(Asset);
            PushEdit("Dynamic topology sculpt", () => ReplaceAsset(ModelPoseWorkflow.Copy(after)), () => ReplaceAsset(ModelPoseWorkflow.Copy(before)));
        }
        else if (_didStroke && _beforeStroke is not null) JournalVertices(_beforeStroke, Asset.Meshes.Select(Vertices).ToArray());
        _beforeDynamicStroke = null;
        _beforeStroke = null; _strokeMesh = -1;
    }
    private void JournalVertices(MeshVertex[][] before, MeshVertex[][] after)
    {
        var committed = after.Select(v => (MeshVertex[])v.Clone()).ToArray();
        PushEdit(_mode == ModelEditorMode.Paint ? "Colouring stroke" : "Sculpt stroke", () => Restore(committed), () => Restore(before));
        void Restore(MeshVertex[][] values) { for (int m = 0; m < values.Length; m++) SetVertices(Asset.Meshes[m], values[m]); NotifyMeshChanged(); }
    }
    private void NotifyMeshChanged()
    {
        InvalidateModelGeometry(); _previewChanged = true; _filteredPreview = null;
        MarkDirty(); Surface.Invalidate(true);
    }
    private static MeshVertex[] Vertices(GModelMesh mesh) => mesh.IsSkinned ? mesh.SkinnedVertices.Select(v => new MeshVertex { Position = v.Position, Normal = v.Normal, UV = v.UV, Color = v.Color }).ToArray() : (MeshVertex[])mesh.Vertices.Clone();
    private static void SetVertices(GModelMesh mesh, MeshVertex[] vertices)
    {
        mesh.Vertices = (MeshVertex[])vertices.Clone();
        if (!mesh.IsSkinned) return;
        for (int i = 0; i < Math.Min(vertices.Length, mesh.SkinnedVertices.Length); i++) { mesh.SkinnedVertices[i].Position = vertices[i].Position; mesh.SkinnedVertices[i].Normal = vertices[i].Normal; mesh.SkinnedVertices[i].UV = vertices[i].UV; mesh.SkinnedVertices[i].Color = vertices[i].Color; }
    }
    protected override void DrawPreview(IRenderController renderer)
    {
        if (_previewChanged)
        {
            PreviewRenderer.InvalidateAssets(renderer);
            _previewChanged = false;
        }
        if (_onion && Asset.Animations.FirstOrDefault(c => c.Name == ActiveClip) is { } clip)
        {
            float fps = Math.Max(1, clip.Fps);
            PreviewRenderer.DrawAssetGhost(Asset, ProjectRoot, Matrix4x4.Identity, new RuntimeModelAnimationState(ActiveClip, Math.Max(0, CurrentFrame - 1) / fps, fps, Loop), new RenderColor(.2f,.7f,1), .2f, renderer);
            PreviewRenderer.DrawAssetGhost(Asset, ProjectRoot, Matrix4x4.Identity, new RuntimeModelAnimationState(ActiveClip, (CurrentFrame + 1) / fps, fps, Loop), new RenderColor(1,.4f,.2f), .2f, renderer);
        }
        var source = Asset; try { Asset = VisibleAsset(); base.DrawPreview(renderer); } finally { Asset = source; }
        DrawPrimitivePlacementGhost(renderer);
    }
    private GModelAsset VisibleAsset()
    {
        if (_hiddenGroups.Count == 0) return Asset;
        _filteredPreview ??= ModelPoseWorkflow.Copy(Asset);
        _filteredPreview.Meshes = Asset.Meshes.Where((_, index) => !_hiddenGroups.Contains(index)).ToList();
        return _filteredPreview;
    }
    public ModelPart AddPart(ModelPrimitiveKind kind) => AddPartAt(kind, Vector3.Zero);

    private ModelPart AddPartAt(ModelPrimitiveKind kind, Vector3 position)
    {
        var part = new ModelPart
        {
            Primitive = kind,
            Name = (kind == ModelPrimitiveKind.Quad ? "Plane" : kind.ToString()) + " " + (Asset.Meshes.Count + 1),
            Color = [200f / 255f, 200f / 255f, 200f / 255f],
        };
        ChangeAsset("Create " + kind, () =>
        {
            int material = NeutralMaterialIndex();
            var (vertices, sourceIndices) = ModelPartBuilder.Bake([part]);
            for (int index = 0; index < vertices.Length; index++) vertices[index].Position += position;
            ushort[] indices = kind == ModelPrimitiveKind.Quad ? MakeDoubleSided(sourceIndices) : sourceIndices;
            Asset.Meshes.Add(new GModelMesh { Name = part.Name, Vertices = vertices, Indices = indices, MaterialIndex = material });
            _parts.Add(part);
        });
        SelectPart(Asset.Meshes.Count - 1);
        return part;
    }
    public void DeleteSelectedPartForTest()
    {
        int index = _groups.SelectedIndex; if (index < 0) return;
        ClearElementSelection();
        ChangeAsset("Delete mesh", () =>
        {
            Asset.Meshes.RemoveAt(index); if (index < _parts.Count) _parts.RemoveAt(index);
            foreach (var node in Asset.Nodes) node.MeshIndices = node.MeshIndices.Where(i => i != index).Select(i => i > index ? i - 1 : i).ToList();
            foreach (var lod in Asset.Lods) lod.MeshIndices = lod.MeshIndices.Where(i => i != index).Select(i => i > index ? i - 1 : i).ToList();
            _hiddenGroups.Clear();
        });
    }
    private void DeleteSelectedPart() => DeleteSelectedPartForTest();
    private void ChangeAsset(string label, Action change)
    {
        string clip = ActiveClip;
        var beforeParts = _parts.ToArray(); var before = ModelPoseWorkflow.Copy(Asset);
        change(); Asset.RecalculateBounds(); var after = ModelPoseWorkflow.Copy(Asset); var afterParts = _parts.ToArray();
        ReplaceAsset(after); MarkDirty();
        PushEdit(label, () => Restore(after, afterParts), () => Restore(before, beforeParts));
        void Restore(GModelAsset asset, ModelPart[] parts) { _parts.Clear(); _parts.AddRange(parts); _hiddenGroups.Clear(); ReplaceAsset(ModelPoseWorkflow.Copy(asset)); SelectClip(clip); }
    }
    private void ReplaceAsset(GModelAsset asset) { Asset = asset; _previewChanged = true; _filteredPreview = null; RefreshAssetPresentation(); if (IsComposer) { RefreshGroups(); RefreshMorphInspector(); RefreshSocketInspector(); RefreshOrganicInspector(); } }
    public GModelAsset CaptureAnimationAsset() => ModelPoseWorkflow.Copy(Asset);
    public ModelAnimationStudioDialog CreateAnimationStudio(int page = 0) => new(ResourcePath, ProjectRoot, Asset, page);
    private void OpenAnimation(int page)
    {
        try { using var dialog = CreateAnimationStudio(page); if (dialog.ShowDialog(this) == DialogResult.OK) ApplyAnimationWorkspace(dialog.Result, dialog.SelectedClip); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Model animation", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    public void ApplyAnimationWorkspace(GModelAsset asset, string clip)
    {
        if (IsViewer) throw new InvalidOperationException("Open the Model Editor to apply animation changes.");
        var before = CaptureAnimationAsset(); string previousClip = ActiveClip; var after = ModelPoseWorkflow.Copy(asset);
        ReplaceAsset(ModelPoseWorkflow.Copy(after)); SelectClip(clip);
        PushEdit("Rig, poses and animation", () => { ReplaceAsset(ModelPoseWorkflow.Copy(after)); SelectClip(clip); }, () => { ReplaceAsset(ModelPoseWorkflow.Copy(before)); SelectClip(previousClip); });
    }
    public override void Save()
    {
        if (ModelImportInProgress) return;
        if (IsViewer) { base.Save(); return; }
        FinishStroke();
        PersistModelChanges(Asset); AcceptSave();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ModelImportInProgress) return true;
        if (EditorInputGuard.IsTextEntryFocused() || EditorInputGuard.IsLabelEditing(EditorInputGuard.FocusedControl()))
            return base.ProcessCmdKey(ref msg, keyData);
        if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { FinishStroke(); Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
        if (Surface.ControlMethod == EditorCameraControlMethod.Free && Surface.Host.ContainsFocus)
        {
            if (keyData == Keys.W) { MoveFreeCamera(1, 0); return true; }
            if (keyData == Keys.S) { MoveFreeCamera(-1, 0); return true; }
            if (keyData == Keys.A) { MoveFreeCamera(0, -1); return true; }
            if (keyData == Keys.D) { MoveFreeCamera(0, 1); return true; }
        }
        if (Surface.ControlMethod != EditorCameraControlMethod.Free && keyData == Keys.W) { SetMeshGizmoMode(EditorGizmoMode.Move); return true; }
        if (Surface.ControlMethod != EditorCameraControlMethod.Free && keyData == Keys.R) { SetMeshGizmoMode(EditorGizmoMode.Rotate); return true; }
        if (Surface.ControlMethod != EditorCameraControlMethod.Free && keyData == Keys.S) { SetMeshGizmoMode(EditorGizmoMode.Scale); return true; }
        if (_mode == ModelEditorMode.Mesh && keyData == Keys.E) { ExtrudeSelectedFaces(); return true; }
        if (_mode == ModelEditorMode.Mesh && keyData == Keys.I) { InsetSelectedFaces(); return true; }
        if (_mode == ModelEditorMode.Mesh && keyData == Keys.B) { BevelSelectedEdges(); return true; }
        if (_mode == ModelEditorMode.Mesh && keyData == (Keys.Control | Keys.R)) { LoopCutSelectedEdge(); return true; }
        if (keyData == Keys.Delete) { DeleteSelectionOrPart(); return true; }
        if (keyData == (Keys.Control | Keys.A)) { SelectAllElements(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.I)) { InvertElementSelection(); return true; }
        if (keyData == Keys.F) { FrameSelection(); return true; }
        if (keyData == Keys.Escape)
        {
            if (CancelPrimitivePlacement()) return true;
            if (CancelMeshGizmoDrag()) return true;
            if (CancelPushPull()) return true;
            CancelAuthoringGesture(); CancelStroke(); ClearElementSelection();
            if (_toolPageHost?.ActiveMode == nameof(ModelToolPage.Select)) SelectTool(ModelAuthoringTool.Region);
            else { _tool = ModelAuthoringTool.Select; UpdateToolHeader(); }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveFreeCamera(float forwardAmount, float rightAmount)
    {
        Vector3 forward = Surface.Camera.Forward;
        Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
        else right = Vector3.Normalize(right);
        float step = MathF.Max(.08f, Surface.Camera.Distance * .065f);
        if ((ModifierKeys & Keys.Shift) != 0) step *= 3f;
        Surface.Camera.Target += (forward * forwardAmount + right * rightAmount) * step;
        Surface.Invalidate(true);
    }
    private static Label Heading(string text) => new() { Text = text, Width = 240, Height = 29, Padding = new Padding(0, 8, 0, 0), ForeColor = EditorChrome.Muted };
    private static Button Tool(string text, Action action) { var button = new Button { Text = text, Width = 235, Height = 32 }; EditorChrome.StyleField(button); button.Click += (_, _) => action(); return button; }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, Action<decimal> changed)
    {
        var number = new NumericUpDown { Width = 235, Minimum = min, Maximum = max, DecimalPlaces = 3, Increment = .025m, Value = value }; EditorChrome.StyleField(number); number.ValueChanged += (_, _) => changed(number.Value); return number;
    }
}

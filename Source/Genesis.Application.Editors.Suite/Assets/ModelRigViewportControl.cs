using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Retained rig/pose/animation canvas, isolated from model geometry authoring.</summary>
public sealed partial class ModelRigViewportControl : EditorSurfaceControl
{
    private readonly EditorViewport3D _viewport;
    private readonly ListBox _partList;
    private readonly Panel _inspector = new();
    private readonly Label _statusLabel;
    private readonly RuntimeModelRenderSystem _runtimeModelPreview = new();
    private GModelAsset? _rigged;
    private readonly ModelEditorMode _mode = ModelEditorMode.Animate;
    private bool _showBones = true, _spinEnabled, _assetChanged = true;
    internal bool _meshDirty;
    private float _animTime, _spin;
    private bool _animPlaying;
    private long _animationTimestamp;
    private string _clip = NoAnimationClipName;
    private bool IsComposer => true;
    private bool IsViewer => false;
    internal Func<float, Matrix4x4>? PreviewWorldTransform { get; set; }

    public ModelRigViewportControl(string path, string projectRoot) : base(path, projectRoot)
    {
        Dock = DockStyle.Fill;
        _rigged = StudioModelResourceLoader.Load(path);
        _viewport = new EditorViewport3D { Dock = DockStyle.Fill, FloorStyle = EditorFloorStyle.GridOnly };
        _viewport.SceneStateFactory = () =>
        {
            _viewport.FloorHeight = BakedBounds().Min.Y;
            var state = EditorSceneLighting.Create(false); state.BackgroundColor = new Vector3(EditorChrome.Canvas.R, EditorChrome.Canvas.G, EditorChrome.Canvas.B) / 255;
            state.FogEnabled = state.FogScreenSpace = false; return state;
        };
        _partList = new ListBox { Name = "ModelPartList", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = 28, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text };
        _partList.SelectedIndexChanged += (_, _) => SyncAnimationInspector();
        _statusLabel = EditorChrome.MakeStatusBar();
        InitializeAnimationAuthoring();
        var sidebar = new Panel { Name = "LegacyRigHierarchy", Dock = DockStyle.Left, Width = 190 }; sidebar.Controls.Add(_partList); sidebar.Controls.Add(EditorChrome.SectionLabel("JOINTS / HIERARCHY"));
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); center.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        _animationPanel.Dock = DockStyle.Fill;
        center.Controls.Add(_viewport, 0, 1); center.Controls.Add(_animationPanel, 0, 2);
        var toolbar = EditorChrome.MakeToolbar();
        toolbar.Items.Add(EditorChrome.ToolButton("Frame", "Fit the model", FrameModel));
        EditorViewportChrome.Attach(toolbar, new EditorViewportChrome.Options { Viewport = _viewport, GetIs2D = () => _viewport.Mode2D, SetIs2D = value => _viewport.Mode2D = value });
        foreach (var label in toolbar.Items.OfType<ToolStripLabel>().Where(item => item.Text == "View").ToArray()) { toolbar.Items.Remove(label); label.Dispose(); }
        center.Controls.Add(toolbar, 0, 0);
        foreach (Control control in center.Controls) control.Margin = Padding.Empty;
        Controls.Add(center); Controls.Add(sidebar); Controls.Add(_statusLabel);
        _viewport.DrawScene += DrawModel; _viewport.DrawOverlay += DrawSkeletonOverlay; _viewport.DrawOverlay += DrawJointStroke;
        _viewport.Host.MouseDown += (_, e) => { if (!HandleJointDrawing(e, true) && !HandleDirectPose(e, true)) HandleAnimationViewportMouse(e, true); };
        _viewport.Host.MouseMove += (_, e) => { if (!HandleJointDrawing(e, false) && !HandleDirectPose(e, false)) HandleAnimationViewportMouse(e, false); };
        _viewport.Host.MouseUp += (_, e) => { FinishJointDrawing(e); FinishDirectPose(e); EndAnimationGizmoDrag(e); };
        HandleCreated += (_, _) => BeginInvoke(FrameModel);
        Disposed += (_, _) => { if (_viewport.Host.Renderer is { } renderer) _runtimeModelPreview.InvalidateAssets(renderer); };
        RefreshPartList(0); SyncAnimationAuthoringUi();
    }

    public EditorViewport3D Viewport => _viewport;
    public GModelAsset? RiggedAsset => _rigged;
    public int SourceNodeCount => _rigged?.Nodes.Count ?? 0;
    public int CanonicalMeshCount => _rigged?.Meshes.Count ?? 0;
    public int MaterialCount => _rigged?.Materials.Count ?? 0;
    public int AnimationClipCount => _rigged?.Animations.Count ?? 0;
    public IReadOnlyList<string> ClipNames => _rigged?.Animations.Select(c => c.Name).ToArray() ?? [];
    public bool HasPersistedSkin => _rigged?.Rig.IsValid == true && _rigged.Meshes.Any(m => m.IsSkinned);
    public string ActiveClip => _clip;
    public float AnimationTime => _animTime;
    public bool ShowBones { get => _showBones; set => _showBones = value; }
    public void SetMode(ModelEditorMode mode) { SyncAnimationAuthoringUi(); }
    public void SetSpin(bool enabled) => _spinEnabled = enabled;
    public void FrameModelForTest() => FrameModel();
    public (Vector3 Min, Vector3 Max) BakedBounds() => _rigged is null ? (Vector3.Zero, Vector3.One)
        : (_rigged.Bounds.Min - _rigged.Pivot.Position, _rigged.Bounds.Max - _rigged.Pivot.Position);
    public override void Save() { if (!_animationDraft && _rigged is not null) { StudioModelResourceLoader.SaveCanonical(ResourcePath, _rigged); AcceptSave(); } }
    public void PlayClip(string name, bool play = true)
    {
        _clip = name; _animTime = 0; _animPlaying = play; _animationTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        SyncAnimationAuthoringUi(); UpdateStatus();
    }
    public void SetAnimationTime(float seconds) { _animTime = Math.Max(0, seconds); SyncAnimationTimelinePosition(); SyncAnimationInspector(); UpdateStatus(); }
    private void RefreshPartList(int index)
    {
        _partList.BeginUpdate(); _partList.Items.Clear(); ResetCanonicalHierarchyMapping();
        if (_rigged?.Rig.IsValid == true) PopulateRigHierarchy(); else PopulateCanonicalHierarchy();
        _partList.EndUpdate(); if (_partList.Items.Count > 0) _partList.SelectedIndex = Math.Clamp(index, 0, _partList.Items.Count - 1);
        SyncAnimationAuthoringUi(); UpdateStatus();
    }
    private void UpdateStatus() => _statusLabel.Text = $"{_clip} · {_rigged?.Rig.Bones.Count ?? 0} joints · RMB orbit · MMB pan · wheel zoom";
    private void DrawModel(IRenderController renderer)
    {
        if (_rigged is null) return;
        if (_assetChanged) { _runtimeModelPreview.InvalidateAssets(renderer); _assetChanged = false; }
        if (_animPlaying)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            AdvanceAnimationTime(Math.Clamp((float)System.Diagnostics.Stopwatch.GetElapsedTime(_animationTimestamp, now).TotalSeconds, 0, .25f));
            _animationTimestamp = now;
        }
        if (_spinEnabled) _spin += .004f;
        var world = Matrix4x4.CreateRotationY(_spin) * (PreviewWorldTransform?.Invoke(_animTime) ?? Matrix4x4.Identity);
        DrawOnionSkins(renderer, world);
        var clip = ActiveAnimationClip;
        _runtimeModelPreview.DrawAsset(PreviewRigLayout ? RigLayoutPreview() : _rigged, ProjectRoot, world,
            new RuntimeModelAnimationState(_clip, _animTime, clip?.Fps ?? 30, clip?.Loop ?? true), renderer);
    }
    private void FrameModel()
    {
        (Vector3 min, Vector3 max) = BakedBounds();
        if (!PreviewRigLayout && _mode == ModelEditorMode.Animate && _rigged?.Rig.IsValid == true && ActiveAnimationClip is { } clip)
        {
            var locals = GModelPrimitiveFactory.EvaluateAnimatedLocals(_rigged, new RuntimeModelAnimationState(_clip, _animTime, clip.Fps, clip.Loop));
            var palette = GModelPrimitiveFactory.EvaluateSkinPalette(_rigged.Rig, locals);
            var pivot = _rigged.Pivot?.Position ?? Vector3.Zero;
            foreach (var mesh in _rigged.Meshes.Where(m => m.IsSkinned))
            foreach (var vertex in mesh.SkinnedVertices)
            {
                Vector3 point = Vector3.Zero; float total = 0;
                for (int i = 0; i < 4; i++)
                {
                    float weight = i switch { 0 => vertex.JointWeights.X, 1 => vertex.JointWeights.Y, 2 => vertex.JointWeights.Z, _ => vertex.JointWeights.W };
                    int bone = (int)(i switch { 0 => vertex.JointIndices.X, 1 => vertex.JointIndices.Y, 2 => vertex.JointIndices.Z, _ => vertex.JointIndices.W });
                    if (weight <= 0 || bone < 0 || bone >= palette.Length) continue;
                    point += Vector3.Transform(vertex.Position, palette[bone]) * weight; total += weight;
                }
                point = (total > 0 ? point / total : vertex.Position) - pivot;
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
        }
        Vector3 center = (min + max) * 0.5f;
        float radius = MathF.Max(0.5f, Vector3.Distance(min, max) * 0.5f);
        _viewport.Camera.Target = center;
        float aspect = Math.Max(.4f, _viewport.ClientSize.Width / (float)Math.Max(1, _viewport.ClientSize.Height));
        float verticalHalfAngle = Math.Clamp(_viewport.FieldOfViewDegrees, 10f, 150f) * MathF.PI / 360f;
        float horizontalHalfAngle = MathF.Atan(MathF.Tan(verticalHalfAngle) * aspect);
        _viewport.Camera.Distance = radius * 1.1f / MathF.Sin(Math.Min(verticalHalfAngle, horizontalHalfAngle));
    }

    private void DrawSkeletonOverlay(IRenderController renderer)
    {
        if (_mode != ModelEditorMode.Animate || _rigged == null) return;

        Matrix4x4 world = AnimationPreviewWorld();
        Matrix4x4[] bones = ModelRigBridge.BoneWorldTransforms(_rigged, _clip, _animTime);
        if (bones.Length == 0) return;

        if (_showBones)
        {
            RenderColor boneColor = new(1f, 0.78f, 0.20f);
            RenderColor jointColor = new(0.35f, 0.9f, 1f);

            for (int i = 0; i < bones.Length; i++)
            {
                Vector3 position = Vector3.Transform(bones[i].Translation, world);
                Vector3 screen = _viewport.WorldToSurface(position);
                if (screen.Z is < 0f or > 1f) continue;

                int parent = _rigged!.Rig.Bones[i].ParentIndex;
                if (parent >= 0 && parent < bones.Length)
                {
                    Vector3 parentScreen = _viewport.WorldToSurface(Vector3.Transform(bones[parent].Translation, world));
                    if (parentScreen.Z is >= 0f and <= 1f)
                    {
                        renderer.DrawLine(parentScreen.X, parentScreen.Y, screen.X, screen.Y, boneColor, 2.5f, depth: -9000);
                    }
                }

                renderer.DrawRect(screen.X - 3f, screen.Y - 3f, 6f, 6f, jointColor, filled: true, depth: -9001);
            }
        }

        if (_animationDraft) DrawDirectHandles(renderer,bones,world); else DrawAnimationAuthoringOverlay(renderer, bones, world);
    }

}

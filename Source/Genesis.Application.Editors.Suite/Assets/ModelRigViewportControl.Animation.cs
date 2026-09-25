using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public enum ModelAnimationGizmoKind
{
    Move,
    Rotate,
    Scale,
}

/// <summary>Animation timeline, pose editing, onion skins and transform gizmos for Model Editor.</summary>
public sealed partial class ModelRigViewportControl
{
    private const string NoAnimationClipName = "[None]";

    private Panel _animationPanel = null!;
    private Panel _animationInspector = null!;
    private ThemedComboBox _animationClipBox = null!;
    private Button _animationPlayButton = null!;
    private TrackBar _animationTimeline = null!;
    private ModelFrameStrip _modelFrameStrip = null!;
    private ModelBoneCurveView _boneCurveView = null!;
    private ThemedComboBox _curveChannelBox = null!;
    private FlowLayoutPanel _animationPrimaryRow = null!;
    private Label _animationFrameLabel = null!;
    private NumericUpDown _animationFps = null!;
    private CheckBox _animationLoop = null!;
    private CheckBox _onionSkinCheck = null!;
    private NumericUpDown _onionFrameCount = null!;
    private CheckBox _bonesCheck = null!;
    private CheckBox _motionTrailCheck = null!;
    private FlowLayoutPanel _animationPoseTools = null!;
    private readonly NumericUpDown[] _bonePosition = new NumericUpDown[3];
    private readonly NumericUpDown[] _boneRotation = new NumericUpDown[3];
    private readonly NumericUpDown[] _boneScale = new NumericUpDown[3];
    private Label _animationNodeTitle = null!;
    private Label _animationNodeFrame = null!;
    private readonly List<int> _outlinerNodeIndices = [];
    private bool _syncingAnimation;
    private bool _onionSkinEnabled = true;
    private int _onionFrames = 1;
    private bool _showMotionTrail = true;
    private ModelAnimationGizmoKind _animationGizmo = ModelAnimationGizmoKind.Rotate;
    private Matrix4x4[]? _copiedPose;
    private bool _gizmoDragging;
    private int _gizmoAxis = -1;
    private int _gizmoBone = -1;
    private Point _gizmoDragStart;
    private Vector2 _gizmoDragAxisScreenDir;
    private Matrix4x4 _gizmoStartLocal;
    private Matrix4x4[]? _curvePoseBefore;
    private int _curveEditFrame = -1;

    public bool AnimationControlsVisible => _animationPanel.Visible;
    public int AnimationFrameCount => ActiveAnimationClip?.Frames.Count ?? 0;
    public int CurrentAnimationFrame => CurrentFrameIndex(ActiveAnimationClip);
    public int SelectedAnimationBoneIndex
    {
        get
        {
            int displayIndex = _partList.SelectedIndex;
            return displayIndex >= 0 && displayIndex < _outlinerNodeIndices.Count
                ? _outlinerNodeIndices[displayIndex]
                : -1;
        }
    }
    public string SelectedAnimationBoneName
    {
        get
        {
            int index = SelectedAnimationBoneIndex;
            return index >= 0 && _rigged?.Rig.Bones is { } bones && index < bones.Count
                ? bones[index].Name
                : string.Empty;
        }
    }
    public bool OnionSkinEnabled => _onionSkinEnabled;
    public int OnionSkinFrameCount => _onionFrames;
    public bool MotionTrailEnabled => _showMotionTrail;
    public ModelAnimationGizmoKind AnimationGizmo => _animationGizmo;
    public bool AnimationUsesSharedGizmo => true;
    public EditorGizmoMode AnimationSharedGizmoMode => ToEditorGizmoMode(_animationGizmo);
    public bool AnimationInspectorVisible => _animationInspector.Visible;

    private GModelAnimationClip? ActiveAnimationClip => _rigged?.Animations.FirstOrDefault(clip =>
        string.Equals(clip.Name, _clip, StringComparison.OrdinalIgnoreCase));

    private void InitializeAnimationAuthoring()
    {
        _animationPanel = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Bottom,
            Height = 124,
            Padding = EditorChrome.CommandBarPadding,
            Visible = false,
        };

        FlowLayoutPanel primary = AnimationRow(34);
        _animationPrimaryRow = primary;
        primary.Controls.Add(AnimationCaption("CLIP"));
        _animationClipBox = new ThemedComboBox
        {
            Name = "ModelAnimationClipPicker",
            Width = 180,
        };
        EditorChrome.StyleField(_animationClipBox);
        _animationClipBox.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingAnimation || _animationClipBox.SelectedItem is not string name) return;
            PlayClip(name == NoAnimationClipName ? string.Empty : name, play: false);
        };
        primary.Controls.Add(_animationClipBox);
        _animationPlayButton = AnimationButton("▶ Play", 68, ToggleAnimationPlayback);
        primary.Controls.Add(_animationPlayButton);
        primary.Controls.Add(AnimationButton("◀", 38, () => StepAnimationFrame(-1)));
        primary.Controls.Add(AnimationButton("▶", 38, () => StepAnimationFrame(1)));
        primary.Controls.Add(AnimationButton("■", 38, () => { _animPlaying = false; SetAnimationTime(0); }));
        _animationFrameLabel = AnimationCaption("Frame 0 / 0", 150);
        primary.Controls.Add(_animationFrameLabel);
        primary.Controls.Add(AnimationCaption("FPS", 28));
        _animationFps = AnimationNumber(1m, 240m, 0, 30m, 54);
        _animationFps.ValueChanged += (_, _) =>
        {
            if (_syncingAnimation || ActiveAnimationClip is not { } clip) return;
            clip.Fps = (float)_animationFps.Value;
            MarkDirty();
            SyncAnimationTimelinePosition();
        };
        primary.Controls.Add(_animationFps);
        _animationLoop = AnimationCheck("Loop", true);
        _animationLoop.CheckedChanged += (_, _) =>
        {
            if (_syncingAnimation || ActiveAnimationClip is not { } clip) return;
            clip.Loop = _animationLoop.Checked;
            MarkDirty();
        };
        primary.Controls.Add(_animationLoop);

        FlowLayoutPanel tools = AnimationRow(66);
        _animationPoseTools = tools;
        tools.Controls.Add(AnimationCaption("POSE TOOLS", 72));
        _onionSkinCheck = AnimationCheck("Onion", true);
        _onionSkinCheck.CheckedChanged += (_, _) => SetOnionSkin(_onionSkinCheck.Checked, _onionFrames);
        tools.Controls.Add(_onionSkinCheck);
        tools.Controls.Add(AnimationCaption("±", 14));
        _onionFrameCount = AnimationNumber(1m, 3m, 0, 1m, 42);
        _onionFrameCount.ValueChanged += (_, _) => SetOnionSkin(_onionSkinEnabled, (int)_onionFrameCount.Value);
        tools.Controls.Add(_onionFrameCount);
        _bonesCheck = AnimationCheck("Skeleton", true);
        _bonesCheck.CheckedChanged += (_, _) => ShowBones = _bonesCheck.Checked;
        tools.Controls.Add(_bonesCheck);
        _motionTrailCheck = AnimationCheck("Motion trail", true);
        _motionTrailCheck.CheckedChanged += (_, _) => _showMotionTrail = _motionTrailCheck.Checked;
        tools.Controls.Add(_motionTrailCheck);
        tools.Controls.Add(AnimationButton("Move", 64, () => SetAnimationGizmo(ModelAnimationGizmoKind.Move)));
        tools.Controls.Add(AnimationButton("Rotate", 70, () => SetAnimationGizmo(ModelAnimationGizmoKind.Rotate)));
        tools.Controls.Add(AnimationButton("Scale", 64, () => SetAnimationGizmo(ModelAnimationGizmoKind.Scale)));
        var followChildren = AnimationCheck("Follow children", true);
        followChildren.CheckedChanged += (_, _) => FollowAnimationChildren = followChildren.Checked;
        tools.Controls.Add(followChildren);
        tools.Controls.Add(AnimationButton("Duplicate", 90, DuplicateCurrentFrame));
        tools.Controls.Add(AnimationButton("Delete", 76, DeleteCurrentFrame));

        Panel timelineHost = new()
        {
            BackColor = EditorChrome.Raised,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 2, 6, 2),
        };
        _animationTimeline = new TrackBar
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 34,
            LargeChange = 5,
            Maximum = 0,
            Minimum = 0,
            SmallChange = 1,
            TickFrequency = 5,
        };
        _animationTimeline.ValueChanged += (_, _) =>
        {
            if (_syncingAnimation || ActiveAnimationClip is not { } clip) return;
            _animPlaying = false;
            _animTime = _animationTimeline.Value / MathF.Max(1f, clip.Fps);
            _meshDirty = true;
            SyncAnimationTimelinePosition();
            SyncAnimationInspector();
            UpdateStatus();
        };
        timelineHost.Controls.Add(_animationTimeline);
        _animationTimeline.Visible = false;
        TableLayoutPanel timelineLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = EditorChrome.Raised,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        timelineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timelineLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        timelineLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        timelineLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _modelFrameStrip = new ModelFrameStrip { Dock = DockStyle.Fill, Name = "ModelFrameTimeline" };
        _modelFrameStrip.FrameSelected += frame => _animationTimeline.Value = Math.Clamp(frame, 0, _animationTimeline.Maximum);

        FlowLayoutPanel curveHeader = AnimationRow(28);
        curveHeader.Dock = DockStyle.Fill;
        curveHeader.Controls.Add(AnimationCaption("BONE CURVE", 78));
        _curveChannelBox = new ThemedComboBox { Width = 132 };
        foreach (string channel in ModelCurveChannels.Names) _curveChannelBox.Items.Add(channel);
        _curveChannelBox.SelectedIndex = 0;
        EditorChrome.StyleField(_curveChannelBox);
        _curveChannelBox.SelectedIndexChanged += (_, _) => _boneCurveView?.Invalidate();
        curveHeader.Controls.Add(_curveChannelBox);
        curveHeader.Controls.Add(AnimationCaption("Drag a point to edit the selected bone", 260));

        _boneCurveView = new ModelBoneCurveView(ReadSelectedBoneCurve)
        {
            Dock = DockStyle.Fill,
            Name = "ModelBoneCurveEditor",
        };
        _boneCurveView.FrameSelected += frame => _animationTimeline.Value = Math.Clamp(frame, 0, _animationTimeline.Maximum);
        _boneCurveView.EditBegan += BeginCurveEdit;
        _boneCurveView.ValueEdited += ApplyCurveEdit;
        _boneCurveView.EditEnded += EndCurveEdit;
        timelineLayout.Controls.Add(curveHeader, 0, 0);
        timelineLayout.Controls.Add(_modelFrameStrip, 0, 1);
        timelineLayout.Controls.Add(_boneCurveView, 0, 2);
        timelineHost.Controls.Add(timelineLayout);
        timelineLayout.BringToFront();

        _animationPanel.Controls.Add(timelineHost);
        _animationPanel.Controls.Add(tools);
        _animationPanel.Controls.Add(primary);
        _animationPanel.SizeChanged += (_, _) => LayoutAnimationPresentation();

        BuildAnimationInspector();
        SyncAnimationAuthoringUi();
    }

    private void BuildAnimationInspector()
    {
        _animationInspector = new Panel
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            Padding = EditorChrome.PanelInsets,
            Visible = false,
        };
        FlowLayoutPanel flow = new()
        {
            AutoScroll = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            Height = 760,
            WrapContents = false,
        };
        _animationNodeTitle = new Label
        {
            AutoEllipsis = true,
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 244,
        };
        _animationNodeFrame = new Label
        {
            BackColor = EditorChrome.Raised,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 54,
            Padding = new Padding(8),
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 244,
        };
        flow.Controls.Add(AnimationInspectorHeading("ANIMATION NODE"));
        flow.Controls.Add(_animationNodeTitle);
        flow.Controls.Add(_animationNodeFrame);
        flow.Controls.Add(AnimationInspectorHeading("GIZMO"));
        flow.Controls.Add(AnimationInspectorButtonRow(
            ("Move", () => SetAnimationGizmo(ModelAnimationGizmoKind.Move)),
            ("Rotate", () => SetAnimationGizmo(ModelAnimationGizmoKind.Rotate)),
            ("Scale", () => SetAnimationGizmo(ModelAnimationGizmoKind.Scale))));

        BuildTrsSection(flow, "POSITION", _bonePosition, -1000m, 1000m, 3, 0m);
        BuildTrsSection(flow, "ROTATION", _boneRotation, -360m, 360m, 2, 0m);
        BuildTrsSection(flow, "SCALE", _boneScale, 0.001m, 100m, 3, 1m);
        foreach (NumericUpDown input in _bonePosition.Concat(_boneRotation).Concat(_boneScale))
            input.ValueChanged += (_, _) => ApplyAnimationInspectorTransform();

        flow.Controls.Add(AnimationInspectorHeading("FRAME POSE"));
        flow.Controls.Add(AnimationInspectorButtonRow(
            ("Copy", CopyCurrentPose),
            ("Paste", PasteCurrentPose),
            ("Bind", ResetSelectedBoneToBindPose)));
        Label hint = new()
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 82,
            Text = "LMB selects a node or drags the active 3D gizmo.\nRMB orbits · Shift+MMB pans · wheel zooms.",
            Width = 244,
        };
        flow.Controls.Add(hint);
        _animationInspector.Controls.Add(flow);
        _inspector.Controls.Add(_animationInspector);
    }

    private static FlowLayoutPanel AnimationRow(int top)
        => new()
        {
            AutoScroll = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 32,
            Padding = new Padding(0, 2, 0, 0),
            Top = top,
            WrapContents = true,
        };

    private static Label AnimationCaption(string text, int width = 38)
        => new()
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 26,
            Margin = new Padding(3, 3, 3, 0),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = width,
        };

    private static Button AnimationButton(string text, int width, Action action)
    {
        Button button = new()
        {
            Font = EditorChrome.SmallFont,
            Height = 26,
            Text = text,
            AutoEllipsis = false,
            Width = width,
            Margin = new Padding(2),
        };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => action();
        return button;
    }

    private static NumericUpDown AnimationNumber(
        decimal minimum, decimal maximum, int decimals, decimal value, int width)
    {
        NumericUpDown input = new()
        {
            DecimalPlaces = decimals,
            Height = 26,
            Maximum = maximum,
            Minimum = minimum,
            Value = Math.Clamp(value, minimum, maximum),
            Width = width,
            Margin = new Padding(2),
        };
        EditorChrome.StyleField(input);
        return input;
    }

    private static CheckBox AnimationCheck(string text, bool value)
        => new()
        {
            AutoSize = true,
            Checked = value,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Text,
            Height = 26,
            Margin = new Padding(5, 5, 3, 0),
            Text = text,
        };

    private static Label AnimationInspectorHeading(string text)
        => new()
        {
            AutoSize = false,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Muted,
            Height = 24,
            Margin = new Padding(0, 8, 0, 0),
            Text = text,
            TextAlign = ContentAlignment.BottomLeft,
            Width = 244,
        };

    private static FlowLayoutPanel AnimationInspectorButtonRow(params (string Text, Action Action)[] buttons)
    {
        FlowLayoutPanel row = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 31,
            Margin = Padding.Empty,
            Width = 244,
            WrapContents = false,
        };
        int[] widths = buttons.Length == 3 ? [60, 68, 60] : Enumerable.Repeat(64, buttons.Length).ToArray();
        for (int index = 0; index < buttons.Length; index++)
            row.Controls.Add(AnimationButton(buttons[index].Text, widths[index], buttons[index].Action));
        return row;
    }

    private static void BuildTrsSection(
        FlowLayoutPanel owner,
        string title,
        NumericUpDown[] destination,
        decimal minimum,
        decimal maximum,
        int decimals,
        decimal value)
    {
        owner.Controls.Add(AnimationInspectorHeading(title));
        Panel rows = new()
        {
            BackColor = Color.Transparent,
            Height = 30,
            Margin = Padding.Empty,
            Width = 244,
        };
        string[] axes = ["X", "Y", "Z"];
        for (int i = 0; i < 3; i++)
        {
            Label axis = new()
            {
                AutoSize = false,
                Font = EditorChrome.SmallFont,
                ForeColor = EditorChrome.Muted,
                Location = new Point(i * 82, 0),
                Size = new Size(18, 26),
                Text = axes[i],
                TextAlign = ContentAlignment.MiddleLeft,
            };
            destination[i] = AnimationNumber(minimum, maximum, decimals, value, 62);
            destination[i].Location = new Point(i * 82 + 18, 0);
            destination[i].Margin = Padding.Empty;
            destination[i].AccessibleName = $"{title} {axes[i]}";
            rows.Controls.Add(axis);
            rows.Controls.Add(destination[i]);
        }
        owner.Controls.Add(rows);
    }

    private void ResetCanonicalHierarchyMapping() => _outlinerNodeIndices.Clear();

    private void PopulateCanonicalHierarchy()
    {
        if (_rigged?.Nodes is not { Count: > 0 } nodes) return;
        bool[] visited = new bool[nodes.Count];
        IEnumerable<int> roots = Enumerable.Range(0, nodes.Count)
            .Where(index => nodes[index].ParentIndex < 0 || nodes[index].ParentIndex >= nodes.Count);
        foreach (int root in roots) AddNode(root, 0);
        for (int i = 0; i < nodes.Count; i++) if (!visited[i]) AddNode(i, 0);

        void AddNode(int index, int depth)
        {
            if (index < 0 || index >= nodes.Count || visited[index]) return;
            visited[index] = true;
            GModelNode node = nodes[index];
            bool animated = BoneChangesInActiveClip(index);
            string icon = animated ? "●" : node.MeshIndices.Count > 0 ? "◆" : "◇";
            _partList.Items.Add($"{new string(' ', Math.Min(depth, 8) * 2)}{icon}  {node.Name}");
            _outlinerNodeIndices.Add(index);
            foreach (int child in Enumerable.Range(0, nodes.Count).Where(i => nodes[i].ParentIndex == index))
                AddNode(child, depth + 1);
        }
    }

    private void PopulateRigHierarchy()
    {
        if (_rigged?.Rig.Bones is not { Count: > 0 } bones) return;
        bool[] visited = new bool[bones.Count];
        foreach (int root in Enumerable.Range(0, bones.Count)
                     .Where(index => bones[index].ParentIndex < 0 || bones[index].ParentIndex >= bones.Count))
            AddBone(root, 0);
        for (int i = 0; i < bones.Count; i++) if (!visited[i]) AddBone(i, 0);

        void AddBone(int index, int depth)
        {
            if (index < 0 || index >= bones.Count || visited[index]) return;
            visited[index] = true;
            _partList.Items.Add($"{new string(' ', Math.Min(depth, 8) * 2)}●  {bones[index].Name}");
            _outlinerNodeIndices.Add(index);
            foreach (int child in Enumerable.Range(0, bones.Count).Where(i => bones[i].ParentIndex == index))
                AddBone(child, depth + 1);
        }
    }

    private bool BoneChangesInActiveClip(int boneIndex)
    {
        GModelAnimationClip? clip = ActiveAnimationClip;
        if (clip?.Frames is not { Count: > 1 }) return false;
        Matrix4x4 first = LocalAt(clip.Frames[0], boneIndex);
        return clip.Frames.Skip(1).Any(frame => !MatrixNearlyEqual(first, LocalAt(frame, boneIndex)));
    }

    public bool SelectAnimationNode(string name)
    {
        SelectedRigElementIsBone = false;
        if (_rigged?.Rig.Bones is null || string.IsNullOrWhiteSpace(name)) return false;
        int bone = _rigged.Rig.Bones.FindIndex(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        int display = _outlinerNodeIndices.IndexOf(bone);
        if (display < 0) return false;
        _partList.SelectedIndex = display;
        SyncAnimationInspector();
        return true;
    }

    public void SetAnimationGizmo(ModelAnimationGizmoKind kind)
    {
        _animationGizmo = kind;
        SyncAnimationInspector();
        UpdateStatus();
    }

    public void SetOnionSkin(bool enabled, int frameCount = 1)
    {
        _onionSkinEnabled = enabled;
        _onionFrames = Math.Clamp(frameCount, 1, 3);
        if (_onionSkinCheck is not null)
        {
            _syncingAnimation = true;
            _onionSkinCheck.Checked = enabled;
            _onionFrameCount.Value = _onionFrames;
            _syncingAnimation = false;
        }
    }

    public void SetMotionTrail(bool enabled)
    {
        _showMotionTrail = enabled;
        if (_motionTrailCheck is not null) _motionTrailCheck.Checked = enabled;
    }

    private void SyncAnimationAuthoringUi()
    {
        if (_animationPanel is null) return;
        _syncingAnimation = true;
        try
        {
            _animationPanel.Visible = true;
            if (_animationPoseTools is not null)
            {
                _animationPoseTools.Visible = !_animationDraft && IsComposer && _mode == ModelEditorMode.Animate;
            }
            string[] names = _rigged?.Animations.Select(clip => clip.Name).ToArray() ?? [];
            string[] choices = names.Length == 0 ? [NoAnimationClipName] : names;
            if (!_animationClipBox.Items.Cast<string>().SequenceEqual(choices, StringComparer.Ordinal))
            {
                _animationClipBox.Items.Clear();
                _animationClipBox.Items.AddRange(choices);
            }
            int selected;
            if (names.Length == 0)
            {
                selected = 0;
                _clip = NoAnimationClipName;
                _animPlaying = false;
            }
            else
            {
                selected = Array.FindIndex(names, name => string.Equals(name, _clip, StringComparison.OrdinalIgnoreCase));
                if (selected < 0)
                {
                    selected = 0;
                    _clip = names[0];
                    _animTime = 0f;
                }
            }
            _animationClipBox.SelectedIndex = selected;
            if (ActiveAnimationClip is { } clip)
            {
                _animationTimeline.Maximum = Math.Max(0, clip.Frames.Count - 1);
                _animationFps.Value = Math.Clamp((decimal)clip.Fps, _animationFps.Minimum, _animationFps.Maximum);
                _animationLoop.Checked = clip.Loop;
                _animationPlayButton.Enabled = true;
                _animationFps.Enabled = true;
                _animationLoop.Enabled = true;
            }
            else
            {
                _animationTimeline.Maximum = 0;
                _animationPlayButton.Enabled = false;
                _animationFps.Enabled = false;
                _animationLoop.Enabled = false;
            }
            _onionSkinCheck.Checked = _onionSkinEnabled;
            _onionFrameCount.Value = _onionFrames;
            _bonesCheck.Checked = _showBones;
            _motionTrailCheck.Checked = _showMotionTrail;
            SyncAnimationTimelinePositionCore();
        }
        finally
        {
            _syncingAnimation = false;
        }
        SyncAnimationInspector();
        LayoutAnimationPresentation();
    }

    private void SyncAnimationTimelinePosition()
    {
        if (_animationTimeline is null) return;
        bool wasSyncing = _syncingAnimation;
        _syncingAnimation = true;
        try { SyncAnimationTimelinePositionCore(); }
        finally { _syncingAnimation = wasSyncing; }
    }

    private void SyncAnimationTimelinePositionCore()
    {
        GModelAnimationClip? clip = ActiveAnimationClip;
        int frame = CurrentFrameIndex(clip);
        if (frame >= _animationTimeline.Minimum && frame <= _animationTimeline.Maximum)
            _animationTimeline.Value = frame;
        _animationFrameLabel.Text = clip is null
            ? $"Animation: {NoAnimationClipName}"
            : $"Frame {frame + 1} / {clip.Frames.Count}  ·  {_animTime:0.00}s";
        _animationPlayButton.Text = _animPlaying ? "❚❚ Pause" : "▶ Play";
        if (_modelFrameStrip is not null)
        {
            _modelFrameStrip.FrameCount = clip?.Frames.Count ?? 0;
            _modelFrameStrip.SelectedFrame = frame;
            _modelFrameStrip.PoseFrames = _rigged?.PoseAnimations.FirstOrDefault(a => a.Id == clip?.PoseAnimationId)?.Keys.Select(k => k.Frame - 1).ToArray() ?? [];
            _modelFrameStrip.Invalidate();
        }
        _boneCurveView?.Invalidate();
    }

    private ModelCurveSnapshot ReadSelectedBoneCurve()
    {
        GModelAnimationClip? clip = ActiveAnimationClip;
        int bone = SelectedAnimationBoneIndex;
        int channel = Math.Max(0, _curveChannelBox?.SelectedIndex ?? 0);
        if (clip is not { Frames.Count: > 0 } || bone < 0)
            return new ModelCurveSnapshot([], CurrentAnimationFrame, ModelCurveChannels.Names[channel]);

        float[] values = new float[clip.Frames.Count];
        for (int frame = 0; frame < clip.Frames.Count; frame++)
        {
            Matrix4x4 local = LocalAt(clip.Frames[frame], bone);
            Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 position);
            Vector3 degrees = QuaternionToEulerDegrees(rotation);
            values[frame] = ModelCurveChannels.Read(channel, position, degrees, scale);
        }
        return new ModelCurveSnapshot(values, CurrentFrameIndex(clip), ModelCurveChannels.Names[channel]);
    }

    private void BeginCurveEdit(int frame)
    {
        if (ActiveAnimationClip is not { Frames.Count: > 0 } clip) return;
        frame = Math.Clamp(frame, 0, clip.Frames.Count - 1);
        _animationTimeline.Value = frame;
        _curveEditFrame = frame;
        _curvePoseBefore = (Matrix4x4[])clip.Frames[frame].LocalBoneTransforms.Clone();
    }

    private void ApplyCurveEdit(int frame, float value)
    {
        if (ActiveAnimationClip is not { Frames.Count: > 0 } clip) return;
        int bone = SelectedAnimationBoneIndex;
        if (bone < 0 || frame < 0 || frame >= clip.Frames.Count) return;
        Matrix4x4[] locals = (Matrix4x4[])clip.Frames[frame].LocalBoneTransforms.Clone();
        if (bone >= locals.Length) return;
        Decompose(locals[bone], out Vector3 scale, out Quaternion rotation, out Vector3 position);
        Vector3 degrees = QuaternionToEulerDegrees(rotation);
        ModelCurveChannels.Write(Math.Max(0, _curveChannelBox.SelectedIndex), value, ref position, ref degrees, ref scale);
        Vector3 radians = degrees * (MathF.PI / 180f);
        locals[bone] = Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z)
            * Matrix4x4.CreateTranslation(position);
        RestoreAnimationPose(clip, frame, locals);
        _animTime = frame / MathF.Max(1f, clip.Fps);
        MarkDirty();
        SyncAnimationTimelinePosition();
        UpdateStatus();
    }

    private void EndCurveEdit()
    {
        if (_curvePoseBefore is null || _curveEditFrame < 0 || ActiveAnimationClip is not { } clip
            || _curveEditFrame >= clip.Frames.Count)
        {
            _curvePoseBefore = null;
            _curveEditFrame = -1;
            return;
        }
        Matrix4x4[] after = (Matrix4x4[])clip.Frames[_curveEditFrame].LocalBoneTransforms.Clone();
        RecordPoseEdit(clip, _curveEditFrame, _curvePoseBefore, after);
        _curvePoseBefore = null;
        _curveEditFrame = -1;
    }

    private static class ModelCurveChannels
    {
        internal static readonly string[] Names = ["Position X", "Position Y", "Position Z", "Rotation X", "Rotation Y", "Rotation Z", "Scale X", "Scale Y", "Scale Z"];

        internal static float Read(int channel, Vector3 position, Vector3 rotation, Vector3 scale) => channel switch
        {
            0 => position.X, 1 => position.Y, 2 => position.Z,
            3 => rotation.X, 4 => rotation.Y, 5 => rotation.Z,
            6 => scale.X, 7 => scale.Y, _ => scale.Z,
        };

        internal static void Write(int channel, float value, ref Vector3 position, ref Vector3 rotation, ref Vector3 scale)
        {
            switch (channel)
            {
                case 0: position.X = value; break;
                case 1: position.Y = value; break;
                case 2: position.Z = value; break;
                case 3: rotation.X = value; break;
                case 4: rotation.Y = value; break;
                case 5: rotation.Z = value; break;
                case 6: scale.X = value; break;
                case 7: scale.Y = value; break;
                default: scale.Z = value; break;
            }
        }
    }

    private void SyncAnimationInspector()
    {
        if (_animationInspector is null) return;
        int boneIndex = SelectedAnimationBoneIndex;
        GModelAnimationClip? clip = ActiveAnimationClip;
        bool visible = IsComposer && _mode == ModelEditorMode.Animate
            && _rigged?.Rig.IsValid == true
            && boneIndex >= 0
            && boneIndex < _rigged.Rig.Bones.Count
            && clip is { Frames.Count: > 0 };
        _animationInspector.Visible = visible;
        if (!visible) return;
        _animationInspector.BringToFront();

        bool wasSyncing = _syncingAnimation;
        _syncingAnimation = true;
        try
        {
            int frameIndex = CurrentFrameIndex(clip);
            Matrix4x4 local = LocalAt(clip!.Frames[frameIndex], boneIndex);
            Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
            Vector3 degrees = QuaternionToEulerDegrees(rotation);
            SetNumber(_bonePosition[0], translation.X);
            SetNumber(_bonePosition[1], translation.Y);
            SetNumber(_bonePosition[2], translation.Z);
            SetNumber(_boneRotation[0], degrees.X);
            SetNumber(_boneRotation[1], degrees.Y);
            SetNumber(_boneRotation[2], degrees.Z);
            SetNumber(_boneScale[0], scale.X);
            SetNumber(_boneScale[1], scale.Y);
            SetNumber(_boneScale[2], scale.Z);
            _animationNodeTitle.Text = FriendlyAnimationName(_rigged!.Rig.Bones[boneIndex].Name);
            _animationNodeFrame.Text = $"Frame {frameIndex + 1} of {clip.Frames.Count}\n{FriendlyAnimationName(clip.Name)} · {_animationGizmo}";
        }
        finally
        {
            _syncingAnimation = wasSyncing;
        }
    }

    private static void SetNumber(NumericUpDown input, float value)
        => input.Value = Math.Clamp((decimal)(float.IsFinite(value) ? value : 0f), input.Minimum, input.Maximum);

    private void ToggleAnimationPlayback()
    {
        if (IsComposer && _mode != ModelEditorMode.Animate) SetMode(ModelEditorMode.Animate);
        if (_animPlaying) PauseAnimation(); else ResumeAnimation();
        SyncAnimationTimelinePosition();
    }

    private void StepAnimationFrame(int delta)
    {
        if (ActiveAnimationClip is not { Frames.Count: > 0 } clip) return;
        _animPlaying = false;
        int target = CurrentFrameIndex(clip) + delta;
        target = clip.Loop
            ? ((target % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count
            : Math.Clamp(target, 0, clip.Frames.Count - 1);
        _animTime = target / MathF.Max(1f, clip.Fps);
        _meshDirty = true;
        SyncAnimationTimelinePosition();
        SyncAnimationInspector();
        UpdateStatus();
    }

    private void AdvanceAnimationTime(float seconds)
    {
        if (ActiveAnimationClip is not { Frames.Count: > 0 } clip) return;
        _animTime += MathF.Max(0f, seconds);
        float duration = clip.Frames.Count / MathF.Max(1f, clip.Fps);
        if (_animTime >= duration)
        {
            if (clip.Loop) _animTime %= duration;
            else
            {
                _animTime = MathF.Max(0f, (clip.Frames.Count - 1) / MathF.Max(1f, clip.Fps));
                _animPlaying = false;
            }
        }
        SyncAnimationTimelinePosition();
    }

    private int CurrentFrameIndex(GModelAnimationClip? clip)
    {
        if (clip?.Frames is not { Count: > 0 }) return 0;
        int frame = (int)MathF.Floor(_animTime * MathF.Max(1f, clip.Fps));
        return clip.Loop
            ? ((frame % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count
            : Math.Clamp(frame, 0, clip.Frames.Count - 1);
    }

    private void ApplyAnimationInspectorTransform()
    {
        if (_syncingAnimation || ActiveAnimationClip is not { } clip) return;
        int bone = SelectedAnimationBoneIndex;
        if (bone < 0) return;
        Vector3 translation = new(
            (float)_bonePosition[0].Value, (float)_bonePosition[1].Value, (float)_bonePosition[2].Value);
        Vector3 degrees = new(
            (float)_boneRotation[0].Value, (float)_boneRotation[1].Value, (float)_boneRotation[2].Value);
        Vector3 scale = new(
            (float)_boneScale[0].Value, (float)_boneScale[1].Value, (float)_boneScale[2].Value);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(
            Degrees(degrees.Y), Degrees(degrees.X), Degrees(degrees.Z));
        ApplyFrameLocal(clip, CurrentFrameIndex(clip), bone,
            Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation));
    }

    public bool RotateSelectedAnimationNode(Vector3 deltaDegrees)
    {
        if (!IsComposer || !PoseEditingEnabled) return false;
        int bone = SelectedAnimationBoneIndex;
        if (ActiveAnimationClip is not { } clip || bone < 0)
            return false;
        int frame = CurrentFrameIndex(clip);
        Matrix4x4 local = LocalAt(clip.Frames[frame], bone);
        Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
        Quaternion delta = Quaternion.CreateFromYawPitchRoll(
            Degrees(deltaDegrees.Y), Degrees(deltaDegrees.X), Degrees(deltaDegrees.Z));
        ApplyFrameLocal(clip, frame, bone,
            Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation * delta))
            * Matrix4x4.CreateTranslation(translation));
        SyncAnimationInspector();
        return true;
    }

    private void ApplyFrameLocal(GModelAnimationClip clip, int frameIndex, int boneIndex, Matrix4x4 local)
    {
        EnsureFrameBoneCount(clip, frameIndex);
        var before = (Matrix4x4[])clip.Frames[frameIndex].LocalBoneTransforms.Clone();
        var source = _gizmoDragging && _gizmoPoseBefore is not null ? _gizmoPoseBefore : before;
        var after = ModelPoseWorkflow.Transform(_rigged!, source, boneIndex, local, PropagateAnimationEdit);
        clip.Frames[frameIndex].LocalBoneTransforms = after;
        for (int bone = 0; bone < after.Length; bone++)
            if (after[bone] != before[bone]) SynchronizeEditedTrack(clip, frameIndex, bone, after[bone]);
        if (!_gizmoDragging) RecordPoseEdit(clip, frameIndex, before, after);
        _meshDirty = true;
        MarkDirty();
        WorkingPoseChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureFrameBoneCount(GModelAnimationClip clip, int frameIndex)
    {
        int bones = _rigged?.Rig.Bones.Count ?? 0;
        Matrix4x4[] locals = clip.Frames[frameIndex].LocalBoneTransforms ?? [];
        if (locals.Length >= bones) return;
        Array.Resize(ref locals, bones);
        for (int i = 0; i < locals.Length; i++)
            if (locals[i] == default)
                locals[i] = _rigged!.Rig.Bones[i].BindLocal;
        clip.Frames[frameIndex].LocalBoneTransforms = locals;
    }

    private void SynchronizeEditedTrack(GModelAnimationClip clip, int frameIndex, int boneIndex, Matrix4x4 local)
    {
        Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
        GModelAnimationTrack? track = clip.Tracks.FirstOrDefault(candidate => candidate.BoneIndex == boneIndex);
        if (track is null)
        {
            track = new GModelAnimationTrack { BoneIndex = boneIndex };
            clip.Tracks.Add(track);
        }
        GModelTrsKey? key = track.Keys.FirstOrDefault(candidate => candidate.Frame == frameIndex);
        if (key is null)
        {
            key = new GModelTrsKey { Frame = frameIndex };
            track.Keys.Add(key);
            track.Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        }
        key.Translation = translation;
        key.Rotation = rotation;
        key.Scale = scale;
    }

    private void CopyCurrentPose()
    {
        if (ActiveAnimationClip is not { } clip) return;
        _copiedPose = (Matrix4x4[])clip.Frames[CurrentFrameIndex(clip)].LocalBoneTransforms.Clone();
    }

    private void PasteCurrentPose()
    {
        if (_copiedPose is null || ActiveAnimationClip is not { } clip) return;
        int frame = CurrentFrameIndex(clip);
        if (_copiedPose.Length != _rigged!.Rig.Bones.Count) return;
        var before = CaptureWorkingPose();
        RestoreAnimationPose(clip, frame, _copiedPose);
        RecordPoseEdit(clip, frame, before, (Matrix4x4[])_copiedPose.Clone());
        MarkDirty();
    }

    private void ResetSelectedBoneToBindPose()
    {
        int bone = SelectedAnimationBoneIndex;
        if (ActiveAnimationClip is not { } clip || bone < 0) return;
        ApplyFrameLocal(clip, CurrentFrameIndex(clip), bone, _rigged!.Rig.Bones[bone].BindLocal);
        SyncAnimationInspector();
    }

    public void DuplicateCurrentFrame()
    {
        if (ActiveAnimationClip is not { Frames.Count: > 0 } clip) return;
        int frame = CurrentFrameIndex(clip);
        Matrix4x4[] copy = (Matrix4x4[])clip.Frames[frame].LocalBoneTransforms.Clone();
        clip.Frames.Insert(frame + 1, new GModelAnimationFrame { LocalBoneTransforms = copy });
        ShiftTrackKeys(clip, frame + 1, 1);
        for (int bone = 0; bone < copy.Length; bone++)
            SynchronizeEditedTrack(clip, frame + 1, bone, copy[bone]);
        _animTime = (frame + 1) / MathF.Max(1f, clip.Fps);
        MarkDirty();
        SyncAnimationAuthoringUi();
    }

    public void DeleteCurrentFrame()
    {
        if (ActiveAnimationClip is not { Frames.Count: > 1 } clip) return;
        int frame = CurrentFrameIndex(clip);
        clip.Frames.RemoveAt(frame);
        foreach (GModelAnimationTrack track in clip.Tracks)
        {
            track.Keys.RemoveAll(key => key.Frame == frame);
            foreach (GModelTrsKey key in track.Keys.Where(key => key.Frame > frame)) key.Frame--;
        }
        _animTime = Math.Min(frame, clip.Frames.Count - 1) / MathF.Max(1f, clip.Fps);
        MarkDirty();
        SyncAnimationAuthoringUi();
    }

    private static void ShiftTrackKeys(GModelAnimationClip clip, int fromFrame, int amount)
    {
        foreach (GModelAnimationTrack track in clip.Tracks)
            foreach (GModelTrsKey key in track.Keys.Where(key => key.Frame >= fromFrame).OrderByDescending(key => key.Frame))
                key.Frame += amount;
    }

    private void DrawOnionSkins(IRenderController renderer, Matrix4x4 world)
    {
        if (!IsComposer || _mode != ModelEditorMode.Animate || !_onionSkinEnabled
            || _rigged is null || ActiveAnimationClip is not { Frames.Count: > 1 } clip) return;
        int current = CurrentFrameIndex(clip);
        for (int offset = _onionFrames; offset >= 1; offset--)
        {
            int previous = OnionFrame(clip, current - offset);
            int next = OnionFrame(clip, current + offset);
            float alpha = 0.34f / offset;
            if (previous != current)
                _runtimeModelPreview.DrawAssetGhost(
                    _rigged, ProjectRoot, world,
                    new RuntimeModelAnimationState(_clip, previous / MathF.Max(1f, clip.Fps), clip.Fps, clip.Loop),
                    new RenderColor(0.18f, 0.82f, 1f), alpha, renderer);
            if (next != current && next != previous)
                _runtimeModelPreview.DrawAssetGhost(
                    _rigged, ProjectRoot, world,
                    new RuntimeModelAnimationState(_clip, next / MathF.Max(1f, clip.Fps), clip.Fps, clip.Loop),
                    new RenderColor(1f, 0.42f, 0.16f), alpha, renderer);
        }
    }

    private static int OnionFrame(GModelAnimationClip clip, int frame)
        => clip.Loop
            ? ((frame % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count
            : Math.Clamp(frame, 0, clip.Frames.Count - 1);

    private void DrawAnimationAuthoringOverlay(
        IRenderController renderer,
        Matrix4x4[] currentBones,
        Matrix4x4 previewWorld)
    {
        if (!IsComposer) return;
        int selected = SelectedAnimationBoneIndex;
        if (selected < 0 || selected >= currentBones.Length || _rigged is null) return;
        float size = GizmoWorldSize();
        Matrix4x4 selectedWorld = currentBones[selected] * previewWorld;
        Vector3 origin = selectedWorld.Translation;
        Vector3 originScreen = _viewport.WorldToSurface(origin);
        if (originScreen.Z is < 0f or > 1f) return;

        DrawOnionPoseGuides(renderer, selected, previewWorld, size);
        DrawMotionTrail(renderer, selected, previewWorld, size);
        EditorTransformGizmo.Draw3D(
            _viewport,
            renderer,
            origin,
            AnimationGizmoAxes(selectedWorld),
            size,
            AnimationSharedGizmoMode,
            includeZ: true,
            activeAxis: _gizmoDragging ? _gizmoAxis : null);

        renderer.DrawRect(originScreen.X - 6f, originScreen.Y - 6f, 12f, 12f,
            new RenderColor(1f, 0.9f, 0.25f), filled: false, depth: -9450);
        renderer.DrawText($"{SelectedAnimationBoneName}  ·  {_animationGizmo}  ·  F{CurrentAnimationFrame + 1}",
            originScreen.X + 11f, originScreen.Y - 27f, 13f, new RenderColor(1f, 0.94f, 0.72f));
    }

    private void DrawMotionTrail(IRenderController renderer, int bone, Matrix4x4 previewWorld, float size)
    {
        if (!_showMotionTrail || ActiveAnimationClip is not { Frames.Count: > 1 } clip || _rigged is null) return;
        int current = CurrentFrameIndex(clip);
        Vector3? previous = null;
        int radius = Math.Min(8, clip.Frames.Count - 1);
        for (int offset = -radius; offset <= radius; offset++)
        {
            int frame = OnionFrame(clip, current + offset);
            Matrix4x4[] locals = clip.Frames[frame].LocalBoneTransforms;
            Matrix4x4[] worlds = GModelPrimitiveFactory.ComputeWorldTransforms(_rigged.Rig.Bones, locals);
            if (bone >= worlds.Length) continue;
            Vector3 orientationTip = worlds[bone].Translation
                + Vector3.TransformNormal(Vector3.UnitY * size * 0.65f, worlds[bone]);
            Vector3 screen = _viewport.WorldToSurface(Vector3.Transform(orientationTip, previewWorld));
            if (previous is { } from && from.Z is >= 0f and <= 1f && screen.Z is >= 0f and <= 1f)
                renderer.DrawLine(from.X, from.Y, screen.X, screen.Y,
                    new RenderColor(0.88f, 0.35f, 1f, 0.9f), 2f, depth: -9350);
            if (screen.Z is >= 0f and <= 1f)
                renderer.DrawRect(screen.X - 2f, screen.Y - 2f, 4f, 4f,
                    new RenderColor(0.88f, 0.35f, 1f), filled: true, depth: -9351);
            previous = screen;
        }
    }

    private void DrawOnionPoseGuides(
        IRenderController renderer,
        int bone,
        Matrix4x4 previewWorld,
        float size)
    {
        if (!_onionSkinEnabled || _rigged is null || ActiveAnimationClip is not { Frames.Count: > 1 } clip)
            return;

        int current = CurrentFrameIndex(clip);
        for (int offset = _onionFrames; offset >= 1; offset--)
        {
            DrawGuide(OnionFrame(clip, current - offset),
                new RenderColor(0.18f, 0.82f, 1f, 0.92f), $"−{offset}");
            DrawGuide(OnionFrame(clip, current + offset),
                new RenderColor(1f, 0.42f, 0.16f, 0.92f), $"+{offset}");
        }

        void DrawGuide(int frame, RenderColor color, string label)
        {
            if (frame == current) return;
            Matrix4x4[] worlds = GModelPrimitiveFactory.ComputeWorldTransforms(
                _rigged.Rig.Bones, clip.Frames[frame].LocalBoneTransforms);
            if (bone >= worlds.Length) return;
            Matrix4x4 pose = worlds[bone] * previewWorld;
            Vector3 origin = pose.Translation;
            Vector3 originScreen = _viewport.WorldToSurface(origin);
            if (originScreen.Z is < 0f or > 1f) return;
            Vector3 direction = Vector3.TransformNormal(Vector3.UnitY, pose);
            if (direction.LengthSquared() < 0.0001f) return;
            Vector3 end = origin + Vector3.Normalize(direction) * size * 1.08f;
            Vector3 endScreen = _viewport.WorldToSurface(end);
            if (endScreen.Z is < 0f or > 1f) return;
            renderer.DrawLine(originScreen.X, originScreen.Y, endScreen.X, endScreen.Y,
                color, 2.6f, depth: -9380);
            renderer.DrawRect(endScreen.X - 3f, endScreen.Y - 3f, 6f, 6f,
                color, filled: true, depth: -9381);
            renderer.DrawText(label, endScreen.X + 5f, endScreen.Y - 5f, 11f, color);
        }
    }

    private bool HandleAnimationViewportMouse(MouseEventArgs e, bool pressed)
    {
        if (_animationDraft) return false;
        if (!IsComposer || !PoseEditingEnabled || _rigged?.Rig.IsValid != true || ActiveAnimationClip is null) return false;
        if (pressed && e.Button == MouseButtons.Left)
        {
            _animPlaying = false;
            if (TryHitGizmo(e.Location, out int axis))
            {
                _gizmoDragging = true;
                _gizmoAxis = axis;
                _gizmoBone = SelectedAnimationBoneIndex;
                _gizmoDragStart = e.Location;
                _gizmoStartLocal = CurrentSelectedLocal();
                _gizmoPoseBefore = CaptureWorkingPose();
                _viewport.NavigationEnabled = false;
                return true;
            }
            if (TrySelectBone(e.Location)) return true;
        }
        else if (!pressed && _gizmoDragging && e.Button == MouseButtons.Left)
        {
            ApplyGizmoDrag(e.Location);
            return true;
        }
        return false;
    }

    private void EndAnimationGizmoDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_gizmoDragging) return;
        if (_gizmoPoseBefore is not null && ActiveAnimationClip is { } clip)
            RecordPoseEdit(clip, CurrentFrameIndex(clip), _gizmoPoseBefore, CaptureWorkingPose());
        _gizmoPoseBefore = null;
        _gizmoDragging = false;
        _gizmoAxis = -1;
        _gizmoBone = -1;
        _gizmoDragAxisScreenDir = default;
        _viewport.NavigationEnabled = true;
        SyncAnimationInspector();
    }

    private bool TrySelectBone(Point point)
    {
        if (_rigged is null) return false;
        Matrix4x4[] worlds = ModelRigBridge.BoneWorldTransforms(_rigged, _clip, _animTime);
        Matrix4x4 previewWorld = AnimationPreviewWorld();
        PointF surfacePoint = _viewport.ControlToSurface(point);
        float nearest = 12f * 12f;
        int match = -1;
        for (int bone = 0; bone < worlds.Length; bone++)
        {
            Vector3 screen = _viewport.WorldToSurface(Vector3.Transform(worlds[bone].Translation, previewWorld));
            if (screen.Z is < 0f or > 1f) continue;
            float dx = screen.X - surfacePoint.X;
            float dy = screen.Y - surfacePoint.Y;
            float distance = dx * dx + dy * dy;
            if (distance < nearest) { nearest = distance; match = bone; }
        }
        int display = _outlinerNodeIndices.IndexOf(match);
        if (display < 0) return false;
        _partList.SelectedIndex = display;
        SyncAnimationInspector();
        return true;
    }

    private bool TryHitGizmo(Point point, out int axis)
    {
        axis = -1;
        int bone = SelectedAnimationBoneIndex;
        if (_rigged is null || bone < 0) return false;
        Matrix4x4[] worlds = ModelRigBridge.BoneWorldTransforms(_rigged, _clip, _animTime);
        if (bone >= worlds.Length) return false;
        Matrix4x4 previewWorld = AnimationPreviewWorld();
        Matrix4x4 selectedWorld = worlds[bone] * previewWorld;
        Vector3 origin = selectedWorld.Translation;
        Vector3[] axes = AnimationGizmoAxes(selectedWorld);
        PointF surface = _viewport.ControlToSurface(point);
        if (EditorTransformGizmo.HitTest3D(
                _viewport,
                surface,
                origin,
                axes,
                GizmoWorldSize(),
                AnimationSharedGizmoMode,
                includeZ: true,
                threshold: 11f) is not EditorGizmoHit hit)
        {
            return false;
        }

        if (!EditorTransformGizmo.TryProjectAxisToSurface(
                _viewport,
                origin,
                axes[hit.AxisIndex],
                GizmoWorldSize(),
                out _gizmoDragAxisScreenDir,
                out _))
        {
            return false;
        }

        axis = hit.AxisIndex;
        return true;
    }

    private void ApplyGizmoDrag(Point point)
    {
        if (_rigged is null || ActiveAnimationClip is not { } clip || _gizmoBone < 0 || _gizmoAxis < 0) return;
        PointF startSurface = _viewport.ControlToSurface(_gizmoDragStart);
        PointF currentSurface = _viewport.ControlToSurface(point);
        Vector2 deltaPixels = new(currentSurface.X - startSurface.X, currentSurface.Y - startSurface.Y);
        if (_gizmoDragAxisScreenDir.LengthSquared() < 0.5f) return;
        float pixels = Vector2.Dot(deltaPixels, _gizmoDragAxisScreenDir);
        Vector3[] localAxes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Vector3 localAxis = localAxes[Math.Clamp(_gizmoAxis, 0, 2)];

        Decompose(_gizmoStartLocal, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
        if (_animationGizmo == ModelAnimationGizmoKind.Move)
            translation += Vector3.Transform(localAxis, rotation) * (pixels * GizmoWorldSize() / 80f);
        else if (_animationGizmo == ModelAnimationGizmoKind.Rotate)
            rotation = Quaternion.Normalize(rotation * Quaternion.CreateFromAxisAngle(localAxis, pixels * 0.012f));
        else
        {
            float factor = MathF.Max(0.05f, 1f + pixels * 0.012f);
            if (_gizmoAxis == 0) scale.X *= factor;
            else if (_gizmoAxis == 1) scale.Y *= factor;
            else scale.Z *= factor;
        }
        ApplyFrameLocal(clip, CurrentFrameIndex(clip), _gizmoBone,
            Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation));
        SyncAnimationInspector();
    }

    private static EditorGizmoMode ToEditorGizmoMode(ModelAnimationGizmoKind kind) => kind switch
    {
        ModelAnimationGizmoKind.Move => EditorGizmoMode.Move,
        ModelAnimationGizmoKind.Scale => EditorGizmoMode.Scale,
        _ => EditorGizmoMode.Rotate,
    };

    private static Vector3[] AnimationGizmoAxes(Matrix4x4 boneWorld)
    {
        return
        [
            NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, boneWorld), Vector3.UnitX),
            NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, boneWorld), Vector3.UnitY),
            NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, boneWorld), Vector3.UnitZ),
        ];
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-6f ? Vector3.Normalize(value) : fallback;

    private Matrix4x4 CurrentSelectedLocal()
    {
        int bone = SelectedAnimationBoneIndex;
        if (ActiveAnimationClip is not { } clip || bone < 0)
            return Matrix4x4.Identity;
        return LocalAt(clip.Frames[CurrentFrameIndex(clip)], bone);
    }

    private float GizmoWorldSize()
    {
        Vector3 size = _rigged?.Bounds?.Size ?? Vector3.One;
        return Math.Clamp(size.Length() * 0.13f, 0.25f, 4f);
    }

    private static Matrix4x4 LocalAt(GModelAnimationFrame frame, int bone)
        => frame.LocalBoneTransforms is { } locals && bone >= 0 && bone < locals.Length
            ? locals[bone]
            : Matrix4x4.Identity;

    private static void Decompose(
        Matrix4x4 matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
    {
        if (!Matrix4x4.Decompose(matrix, out scale, out rotation, out translation))
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            translation = matrix.Translation;
        }
    }

    private static Vector3 QuaternionToEulerDegrees(Quaternion q)
        => ModelPoseWorkflow.EulerDegrees(q);

    private static float Degrees(float degrees) => degrees * MathF.PI / 180f;
    private static float RadiansToDegrees(float radians) => radians * 180f / MathF.PI;

    private static bool MatrixNearlyEqual(Matrix4x4 a, Matrix4x4 b)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(a.M11 - b.M11) < epsilon && MathF.Abs(a.M12 - b.M12) < epsilon
            && MathF.Abs(a.M13 - b.M13) < epsilon && MathF.Abs(a.M14 - b.M14) < epsilon
            && MathF.Abs(a.M21 - b.M21) < epsilon && MathF.Abs(a.M22 - b.M22) < epsilon
            && MathF.Abs(a.M23 - b.M23) < epsilon && MathF.Abs(a.M24 - b.M24) < epsilon
            && MathF.Abs(a.M31 - b.M31) < epsilon && MathF.Abs(a.M32 - b.M32) < epsilon
            && MathF.Abs(a.M33 - b.M33) < epsilon && MathF.Abs(a.M34 - b.M34) < epsilon
            && MathF.Abs(a.M41 - b.M41) < epsilon && MathF.Abs(a.M42 - b.M42) < epsilon
            && MathF.Abs(a.M43 - b.M43) < epsilon && MathF.Abs(a.M44 - b.M44) < epsilon;
    }

    private string FriendlyAnimationName(string name)
    {
        string prefix = (_rigged?.Name ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(prefix)
            && name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
                ? name[(prefix.Length + 1)..]
                : name;
    }

    private void ApplyAnimationAuthoringChrome()
    {
        if (_animationPanel is null) return;
        _animationPanel.BackColor = EditorChrome.Surface;
        _animationInspector.BackColor = EditorChrome.Surface;
        _animationNodeTitle.ForeColor = EditorChrome.Text;
        _animationNodeFrame.BackColor = EditorChrome.Raised;
        _animationNodeFrame.ForeColor = EditorChrome.Muted;
    }
}

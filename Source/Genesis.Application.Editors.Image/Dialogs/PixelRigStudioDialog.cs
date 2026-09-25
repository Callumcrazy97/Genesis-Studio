using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>A single rig workspace with named poses and pose-to-frame assignments.</summary>
public sealed partial class PixelRigStudioDialog : DpiAwareForm
{
    private enum RigDragHandle { None, Start, Centre, End }
    private readonly ImageViewportControl _canvas = new();
    private readonly ListBox _library = new() { Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly ListBox _poses = new() { Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly ListBox _animations = new() { Name = "RigAnimations", Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly DataGridView _keys = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, RowHeadersVisible = false };
    private readonly TextBox _name = new() { Width = 180, Text = "New rig" };
    private readonly TextBox _poseName = new() { Width = 180, Text = "New pose" };
    private readonly TextBox _animationName = new() { Width = 180, Text = "Animation" };
    private readonly NumericUpDown _fps = new() { Minimum = 1, Maximum = 120, Value = 30, Width = 70 };
    private readonly CheckBox _loop = new() { Text = "Loop", Checked = true, AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10), AutoEllipsis = true };
    private readonly ImageRigPages _tabs = new() { Dock = DockStyle.Fill };
    private readonly byte[] _source;
    private readonly int _width, _height;
    private readonly string _layerId, _frameId;
    private readonly Action<ImagePixelRig> _save;
    private readonly Action<string> _delete;
    private readonly Action<byte[]> _apply;
    private readonly Func<ImagePixelRig, ImagePoseAnimation, CancellationToken, Task> _generate;
    private ImagePixelRig _rig = new();
    private List<ImagePixelBone> _pose = [];
    private List<ImagePixelJoint> _poseJoints = [];
    private PixelRigRasterizer? _rasterizer;
    private CancellationTokenSource? _renderCancel;
    private CancellationTokenSource? _generationCancel;
    private readonly System.Windows.Forms.Timer _playback = new() { Interval = 33 };
    private int _playFrame = 1;
    private bool _createBones = true, _createJoints, _dragging, _dirty;
    private bool _discardChanges, _closeAfterCancellation;
    private bool _refreshingLibrary;
    private bool _refreshingLists;
    private bool _fitPending = true;
    private string? _selectedBone;
    private string? _selectedJoint;
    private RigDragHandle _dragHandle;
    private PointF _dragStart;
    private List<ImagePixelBone> _beforeDrag = [];
    private List<ImagePixelJoint> _beforeJointDrag = [];
    private long _surfaceVersion;
    private ImagePoseAnimation? _activeAnimation;
    private readonly Button _drawBones;
    private readonly Button _drawJoints;
    private readonly Button _cancelWork;

    public PixelRigStudioDialog(IReadOnlyList<ImagePixelRig> rigs, byte[] source, int width, int height, string layerId, string frameId,
        Action<ImagePixelRig> save, Action<string> delete, Action<byte[]> apply,
        Func<ImagePixelRig, ImagePoseAnimation, CancellationToken, Task> generate, int initialTab = 0)
    {
        Text = "Rigging · Rigs, poses and animations"; ClientSize = new Size(1120,780); MinimumSize = new Size(900,660);
        StartPosition = FormStartPosition.CenterParent;
        _source = (byte[])source.Clone(); _width = width; _height = height; _layerId = layerId; _frameId = frameId;
        _save = save; _delete = delete; _apply = apply; _generate = generate;
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = ClientSize, SplitterDistance = 750, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_canvas); split.Panel2.Controls.Add(_tabs); Controls.Add(split);
        BuildPoseControls(split.Panel1);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62 };
        _cancelWork = Action("Cancel work", CancelWork); _cancelWork.Dock = DockStyle.Right; _cancelWork.Width = 120;
        _status.Dock = DockStyle.Fill; footer.Controls.Add(_status); footer.Controls.Add(_cancelWork); Controls.Add(footer);
        var rigTab = Page("1 · Rig", _library);
        rigTab.Controls.Add(Bar(_name, Action("New rig", NewRig), Action("Save rig", SaveRig), Action("Delete rig", DeleteRig)));
        _drawBones = Action("Draw bones", () => { _createBones = true; _createJoints = false; _tabs.SelectedIndex = 0; Describe(); });
        _drawJoints = Action("Draw joints", () => { _createBones = false; _createJoints = true; _tabs.SelectedIndex = 0; Describe(); });
        rigTab.Controls.Add(Bar(_drawBones, _drawJoints, Action("Select / pose", () => { _createBones = false; _createJoints = false; Describe(); }),
            Action("Edit skeleton", () => { _rasterizer = null; _rig.BindPixels = []; _pose = PixelRigRasterizer.Copy(_rig.Bones); _poseJoints = PixelRigRasterizer.Copy(_rig.Joints); _createBones = true; _createJoints = false; _dirty = true; RenderPose(); Describe(); }),
            Action("Delete bone", DeleteBone), Action("Delete joint", DeleteJoint), Action("Rig pixels", Bind), Action("Apply pose", ApplyPose)));
        var poseTab = Page("2 · Pose", _poses);
        poseTab.Controls.Add(Bar(_poseName, Action("Save pose", SavePose), Action("Load pose", LoadPose), Action("Delete pose", DeletePose), Action("Apply pose", ApplyPose)));
        var animationTab = Page("3 · Animate", _animations);
        _animations.Dock = DockStyle.Top; _animations.Height = 110;
        _keys.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Frame", Width = 65 });
        _keys.Columns.Add(new DataGridViewComboBoxColumn { FlatStyle = FlatStyle.Flat, HeaderText = "Saved pose", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DisplayMember = "Name", ValueMember = "Id" });
        Shown += (_,_) =>
        {
            _keys.EnableHeadersVisualStyles = false; _keys.BackgroundColor = ImageEditorChrome.Canvas; _keys.GridColor = ImageEditorChrome.Border;
            _keys.DefaultCellStyle.BackColor = ImageEditorChrome.Surface; _keys.DefaultCellStyle.ForeColor = ImageEditorChrome.Text;
            _keys.DefaultCellStyle.SelectionBackColor = ImageEditorChrome.Hover; _keys.DefaultCellStyle.SelectionForeColor = ImageEditorChrome.Text;
            _keys.ColumnHeadersDefaultCellStyle.BackColor = ImageEditorChrome.Raised; _keys.ColumnHeadersDefaultCellStyle.ForeColor = ImageEditorChrome.Text;
            _keys.RowTemplate.Height = 30;
        };
        _keys.DataError += (_,e) => { e.ThrowException = false; _status.Text = "Choose an existing saved pose for this frame."; };
        animationTab.Controls.Add(_keys); _keys.BringToFront();
        animationTab.Controls.Add(Bar(_animationName,new Label { Text = "FPS", AutoSize = true, Padding = new Padding(0,6,0,0) },_fps,_loop,Action("New animation", NewAnimation),Action("Add pose frame", AddKey),
            Action("Remove frame", () => { if (_keys.CurrentRow != null) _keys.Rows.Remove(_keys.CurrentRow); }),
            Action("Save animation", SaveAnimation),Action("Delete animation", DeleteAnimation),Action("Play / stop", ToggleAnimation),Action("Generate frames", Generate)));
        _library.SelectedIndexChanged += (_, _) => { if (!_refreshingLibrary && _library.SelectedItem is ImagePixelRig rig) { var selected = PixelRigRasterizer.Copy(rig); if (_dirty) SaveRig(); LoadRig(selected); } };
        _name.TextChanged += (_,_) => _dirty = true;
        _poses.DoubleClick += (_, _) => LoadPose();
        _animations.SelectedIndexChanged += (_, _) => { if (!_refreshingLists && _animations.SelectedItem is ImagePoseAnimation animation) LoadAnimation(animation); };
        _tabs.SelectedIndexChanged += (_, _) => { _createBones = _tabs.SelectedIndex == 0 && _rig.BindPixels.Length == 0; _createJoints = false; Describe(); };
        _canvas.CanvasPointerDown += (_, e) => PointerDown(e);
        _canvas.CanvasPointerMove += (_, e) => PointerMove(e);
        _canvas.CanvasPointerUp += (_, e) => PointerUp(e);
        _canvas.HandleCreated += (_, _) => { _fitPending = true; _canvas.FitToView(); };
        Shown += (_,_) => { split.SplitterDistance = Math.Max(400,split.Width-370); _fitPending = true; RenderPose(); };
        _canvas.Overlay.ShowOrigin = false; _canvas.Overlay.ShowCollision = false;
        _playback.Tick += (_, _) =>
        {
            if (_activeAnimation == null || _activeAnimation.Keys.Count == 0) return;
            int frame = _playFrame++;
            _pose = PixelRigRasterizer.Interpolate(_rig,_activeAnimation,frame);
            _poseJoints = PixelRigRasterizer.InterpolateJoints(_rig,_activeAnimation,frame);
            RenderPose();
            if (_playFrame > _activeAnimation.Keys.Max(k => k.Frame)) { if (_activeAnimation.Loop) _playFrame = 1; else _playback.Stop(); }
        };
        foreach (var rig in rigs) _library.Items.Add(PixelRigRasterizer.Copy(rig));
        if (_library.Items.Count > 0) _library.SelectedIndex = 0; else NewRig();
        _tabs.SelectedIndex = Math.Clamp(initialTab,0,2);
        ThemeMessageBox.ApplyTheme?.Invoke(this);
        FormClosing += (_, e) =>
        {
            if (_generationCancel != null)
            {
                _closeAfterCancellation = true; _generationCancel.Cancel(); e.Cancel = true; return;
            }
            _renderCancel?.Cancel();
            if (_dirty && !_discardChanges) SaveRig();
        };
    }

    private Panel Page(string title, Control list)
    {
        var page = new Panel { Text = title, Padding = new Padding(8) }; page.Controls.Add(list); _tabs.AddPage(page); return page;
    }
    private static FlowLayoutPanel Bar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0,5,0,5) };
        panel.Controls.AddRange(controls); return panel;
    }
    private static Button Action(string title, System.Action action)
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(80,30) };
        ImageEditorChrome.StyleButton(button); button.Click += (_, _) => action(); return button;
    }
    private void Describe() => _status.Text = _createJoints
        ? "Click a joint centre, drag outward to set its circumference, then release. Esc returns to Select / pose."
        : _createBones
            ? "Drag from joint to tip to create a bone. Start at an existing tip to parent it. Esc switches to selecting bones. Click Rig pixels when the skeleton is ready."
            : _editJoints.Checked ? "Joints: drag the centre to move connected endpoints; drag the ring to change its fill radius. Pin joint keeps its centre fixed."
            : $"{_followChildren.Text}. {(_poseMode.SelectedIndex switch { 1=>"Drag a handle to move the bone.", 2=>"Drag a tip to rotate around the opposite tip; the middle uses its attached joint.", 3=>"Drag a tip along the bone to extend or shrink it.", _=>"Tips rotate; the middle moves." })} Shift-drag snaps to a joint. Save pose keeps your changes; Esc cancels a drag.";

    private void CancelWork()
    {
        _discardChanges = true;
        _renderCancel?.Cancel();
        if (_generationCancel != null)
        {
            _closeAfterCancellation = true;
            _status.Text = "Cancelling work…";
            _generationCancel.Cancel();
            return;
        }
        _dirty = false;
        DialogResult = DialogResult.Cancel;
        Close();
    }
    private void NewRig()
    {
        if (_dirty) SaveRig();
        LoadRig(new ImagePixelRig { Width = _width, Height = _height, LayerId = _layerId, SourceFrameId = _frameId });
        _createBones = true; _createJoints = false; _tabs.SelectedIndex = 0; Describe();
    }
    private void LoadRig(ImagePixelRig rig)
    {
        _poseUndo.Clear(); _poseRedo.Clear();
        _playback.Stop(); _fitPending = true; _rig = PixelRigRasterizer.Copy(rig); _pose = PixelRigRasterizer.Copy(_rig.Bones); _poseJoints = PixelRigRasterizer.Copy(_rig.Joints); _name.Text = _rig.Name;
        bool sourceTransparencyChanged = PixelRigRasterizer.RefreshSourceTransparency(
            _rig, _source, _width, _height, _layerId, _frameId);
        _rasterizer = null;
        _activeAnimation = null; _keys.Rows.Clear(); _animationName.Text = "Animation"; _poseName.Text = "New pose";
        _fps.Value = 30; _loop.Checked = true;
        _selectedBone = _pose.FirstOrDefault()?.Id; _selectedJoint = null; _dirty = sourceTransparencyChanged; _createBones = _rig.BindPixels.Length == 0; _createJoints = false; RefreshLists(); RenderPose(); Describe();
        if (sourceTransparencyChanged)
            _status.Text = "Updated this rig to match transparent pixels deleted from its source frame. The repair saves when this window closes.";
    }
    private void SaveRig()
    {
        _rig.Name = string.IsNullOrWhiteSpace(_name.Text) ? "New rig" : _name.Text.Trim();
        if (_rig.BindPixels.Length == 0)
        {
            _rig.Bones = PixelRigRasterizer.Copy(_pose);
            _rig.Joints = PixelRigRasterizer.Copy(_poseJoints);
            ReconcilePoseBones();
        }
        else
        {
            // Keep editing preferences without replacing bind-pose coordinates with a posed skeleton.
            foreach(var rest in _rig.Joints)
                if(_poseJoints.FirstOrDefault(j=>j.Id==rest.Id) is { } joint) { rest.Pinned=joint.Pinned; rest.Radius=joint.Radius; }
        }
        _save(PixelRigRasterizer.Copy(_rig)); _dirty = false;
        RefreshLibraryEntry();
        _status.Text = "Saved " + _rig.Name;
    }
    private void RefreshLibraryEntry()
    {
        _refreshingLibrary = true;
        var existing = _library.Items.Cast<ImagePixelRig>().FirstOrDefault(r => r.Id == _rig.Id);
        if (existing != null) _library.Items[_library.Items.IndexOf(existing)] = PixelRigRasterizer.Copy(_rig);
        else _library.Items.Add(PixelRigRasterizer.Copy(_rig));
        _refreshingLibrary = false;
    }
    private void DeleteRig()
    {
        _delete(_rig.Id); var existing = _library.Items.Cast<ImagePixelRig>().FirstOrDefault(r => r.Id == _rig.Id);
        _dirty = false; if (existing != null) _library.Items.Remove(existing); NewRig();
    }
    private void DeleteBone()
    {
        if (_selectedBone == null) return;
        if (_rig.BindPixels.Length > 0) { _status.Text = "Choose Edit skeleton before deleting bound bones."; return; }
        RecordCurrentPose();
        _pose.RemoveAll(b => b.Id == _selectedBone); foreach (var bone in _pose.Where(b => b.ParentId == _selectedBone)) bone.ParentId = null;
        _dirty = true; RenderPose();
    }
    private void DeleteJoint()
    {
        if (_selectedJoint == null) return;
        if (_rig.BindPixels.Length > 0) { _status.Text = "Choose Edit skeleton before changing joints on a bound rig."; return; }
        RecordCurrentPose();
        string id = _selectedJoint;
        _poseJoints.RemoveAll(joint => joint.Id == id);
        foreach (var bone in _pose)
        {
            if (bone.StartJointId == id) bone.StartJointId = null;
            if (bone.CentreJointId == id) bone.CentreJointId = null;
            if (bone.EndJointId == id) bone.EndJointId = null;
        }
        _selectedJoint = null; _dirty = true; RenderPose();
    }
    private async void Bind()
    {
        if (_generationCancel != null) return;
        if (_pose.Count == 0) { _status.Text = "Draw at least one bone first."; return; }
        _renderCancel?.Cancel();
        var before = PixelRigRasterizer.Copy(_rig);
        var previousRasterizer = _rasterizer;
        _rig.Width = _width; _rig.Height = _height; _rig.LayerId = _layerId; _rig.SourceFrameId = _frameId;
        _rig.BindPixels = (byte[])_source.Clone(); _rig.Bones = PixelRigRasterizer.Copy(_pose); _rig.Joints = PixelRigRasterizer.Copy(_poseJoints);
        _generationCancel = new CancellationTokenSource(); var token = _generationCancel.Token;
        _poseControls.Enabled = _tabs.Enabled = _canvas.Enabled = false; _status.Text = "Binding pixels to bones…";
        try { _rasterizer = await Task.Run(() => new PixelRigRasterizer(_rig,token),token); token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) { _rig = before; _rasterizer = previousRasterizer; _status.Text = "Binding cancelled."; return; }
        catch (ArgumentException error) { System.Diagnostics.Trace.TraceError("Bind pixels: " + error); _rig = before; _rasterizer = previousRasterizer; _status.Text = error.Message; return; }
        finally { CompleteCancellableWork(); }
        _createBones = false;
        ReconcilePoseBones();
        var rest = _rig.Poses.FirstOrDefault(p => p.Name == "Default");
        if (rest == null) _rig.Poses.Insert(0,new ImagePixelPose { Name = "Default", Bones = PixelRigRasterizer.Copy(_pose), Joints = PixelRigRasterizer.Copy(_poseJoints) });
        else { rest.Bones = PixelRigRasterizer.Copy(_pose); rest.Joints = PixelRigRasterizer.Copy(_poseJoints); }
        _poseUndo.Clear(); _poseRedo.Clear();
        _dirty = true; RefreshLists(); SaveRig(); RenderPose(); Describe();
    }
    private void ReconcilePoseBones()
    {
        foreach (var pose in _rig.Poses)
        {
            pose.Bones = _rig.Bones.Select(rest =>
            {
                var bone = PixelRigRasterizer.Copy(pose.Bones.FirstOrDefault(b => b.Id == rest.Id) ?? rest);
                bone.ParentId = rest.ParentId;
                bone.StartJointId = rest.StartJointId; bone.CentreJointId = rest.CentreJointId; bone.EndJointId = rest.EndJointId;
                return bone;
            }).ToList();
            pose.Joints = _rig.Joints.Select(rest => PixelRigRasterizer.Copy(pose.Joints.FirstOrDefault(joint => joint.Id == rest.Id) ?? rest)).ToList();
        }
    }
    private void SavePose()
    {
        if (_rig.BindPixels.Length == 0) { _status.Text = "Click Rig pixels before saving poses."; return; }
        string name = _poseName.Text.Trim(); if (name.Length == 0) return;
        var pose = _rig.Poses.FirstOrDefault(p => p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        if (pose == null) { pose = new ImagePixelPose { Name = name }; _rig.Poses.Add(pose); }
        pose.Bones = PixelRigRasterizer.Copy(_pose); pose.Joints = PixelRigRasterizer.Copy(_poseJoints); _dirty = true; RefreshLists(); SaveRig();
    }
    private void LoadPose()
    {
        if (_poses.SelectedItem is not ImagePixelPose pose) return;
        _pose = PixelRigRasterizer.Copy(pose.Bones); _poseJoints = pose.Joints.Count == 0 ? PixelRigRasterizer.Copy(_rig.Joints) : PixelRigRasterizer.Copy(pose.Joints);
        _poseName.Text = pose.Name; _createBones = false; _createJoints = false; RenderPose();
    }
    private void DeletePose()
    {
        if (_poses.SelectedItem is not ImagePixelPose pose) return;
        if (_rig.Animations.Any(a => a.Keys.Any(k => k.PoseId == pose.Id))) { _status.Text = "This pose is assigned to animation frames. Remove those assignments before deleting it."; return; }
        _rig.Poses.Remove(pose); _dirty = true; RefreshLists(); SaveRig();
    }
    private async void ApplyPose()
    {
        if (_generationCancel != null) return;
        if (_rig.BindPixels.Length == 0) { _status.Text = "Rig the pixels first."; return; }
        _playback.Stop(); _generationCancel = new CancellationTokenSource();
        _poseControls.Enabled = _tabs.Enabled = _canvas.Enabled = false; _status.Text = "Rendering pose…";
        var renderer = _rasterizer; var pose = PixelRigRasterizer.Copy(_pose); var joints=PixelRigRasterizer.Copy(_poseJoints); var token = _generationCancel.Token;
        var rigSnapshot=renderer==null ? PixelRigRasterizer.Copy(_rig) : null;
        try
        {
            var pixels = await Task.Run(() => (renderer??new PixelRigRasterizer(rigSnapshot!,token)).Render(pose,token,joints),token);
            token.ThrowIfCancellationRequested();
            _apply(pixels); SaveRig();
            _status.Text = "Pose applied to the active layer. Undo is available in the Image Editor.";
        }
        catch (OperationCanceledException) { _status.Text = "Apply pose cancelled."; }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Apply rig pose: " + error); _status.Text = error.Message; }
        finally { CompleteCancellableWork(); }
    }
    private void RefreshLists()
    {
        string? poseId = (_poses.SelectedItem as ImagePixelPose)?.Id;
        string? animationId = _activeAnimation?.Id;
        _refreshingLists = true;
        foreach (DataGridViewRow row in _keys.Rows.Cast<DataGridViewRow>().ToArray())
            if (row.Cells[1].Value is string id && _rig.Poses.All(p => p.Id != id)) _keys.Rows.Remove(row);
        _poses.Items.Clear(); foreach (var pose in _rig.Poses) _poses.Items.Add(pose);
        _animations.Items.Clear(); foreach (var animation in _rig.Animations) _animations.Items.Add(animation);
        ((DataGridViewComboBoxColumn)_keys.Columns[1]).DataSource = _rig.Poses.ToList();
        _poses.SelectedIndex = _rig.Poses.FindIndex(p => p.Id == poseId);
        _animations.SelectedIndex = _rig.Animations.FindIndex(a => a.Id == animationId);
        _refreshingLists = false;
    }
    private void NewAnimation()
    {
        _playback.Stop(); _animations.SelectedIndex = -1;
        _activeAnimation = new ImagePoseAnimation(); _animationName.Text = "Animation"; _fps.Value = 30; _loop.Checked = true;
        _keys.Rows.Clear(); AddKey();
    }
    private void AddKey()
    {
        if (_rig.Poses.Count == 0) { _status.Text = "Save a pose before assigning animation frames."; return; }
        _keys.EndEdit();
        int lastFrame = 0;
        foreach (DataGridViewRow row in _keys.Rows)
        {
            if (!int.TryParse(Convert.ToString(row.Cells[0].Value),out int value) || value is < 1 or > 600)
            { _status.Text = "Correct the existing frame numbers before adding a pose frame."; return; }
            lastFrame = Math.Max(lastFrame,value);
        }
        int frame = lastFrame == 0 ? 1 : lastFrame + 30;
        if (frame > 600) { _status.Text = "Animations support frame numbers from 1 to 600."; return; }
        _keys.Rows.Add(frame,_rig.Poses[0].Id);
    }
    private void LoadAnimation(ImagePoseAnimation animation)
    {
        _playback.Stop();
        _activeAnimation = animation; _animationName.Text = animation.Name; _fps.Value = Math.Clamp(animation.FramesPerSecond,1,120); _loop.Checked = animation.Loop;
        _keys.Rows.Clear(); foreach (var key in animation.Keys.OrderBy(k => k.Frame)) _keys.Rows.Add(key.Frame,key.PoseId);
    }
    private bool ReadAnimation()
    {
        _keys.EndEdit(); var keys = new List<ImagePoseFrame>();
        foreach (DataGridViewRow row in _keys.Rows)
        {
            if (!int.TryParse(Convert.ToString(row.Cells[0].Value),out int frame) || frame < 1 || frame > 600 || row.Cells[1].Value is not string id || _rig.Poses.All(p => p.Id != id))
            { _status.Text = "Every row needs a frame from 1 to 600 and a saved pose."; return false; }
            keys.Add(new ImagePoseFrame { Frame = frame, PoseId = id });
        }
        if (keys.Count == 0 || keys.Select(k => k.Frame).Distinct().Count() != keys.Count || string.IsNullOrWhiteSpace(_animationName.Text))
        { _status.Text = "Name the animation and assign poses to distinct frame numbers."; return false; }
        _activeAnimation ??= new ImagePoseAnimation();
        _activeAnimation.Name = _animationName.Text.Trim(); _activeAnimation.Keys = keys; _activeAnimation.FramesPerSecond = (int)_fps.Value; _activeAnimation.Loop = _loop.Checked;
        if (!_rig.Animations.Contains(_activeAnimation)) _rig.Animations.Add(_activeAnimation);
        _dirty = true;
        return true;
    }
    private void SaveAnimation() { if (!ReadAnimation()) return; _dirty = true; SaveRig(); RefreshLists(); }
    private void DeleteAnimation()
    {
        if (_activeAnimation == null) return; _rig.Animations.Remove(_activeAnimation); _activeAnimation = null; _keys.Rows.Clear(); _dirty = true; SaveRig(); RefreshLists();
    }
    private void ToggleAnimation()
    {
        if (_playback.Enabled) { _playback.Stop(); return; } if (!ReadAnimation()) return;
        _playFrame = 1; _playback.Interval = Math.Max(8,1000/(int)_fps.Value); _playback.Start();
    }
    private async void Generate()
    {
        if (_generationCancel != null) { _status.Text = "Wait for the current operation or cancel it first."; return; }
        if (!ReadAnimation()) { ShowOperationError("Generate frames", _status.Text); return; }
        // A renderer is an asynchronous preview cache, not the saved rig's readiness state.
        // Generation builds its own renderer and must also work before the first preview finishes.
        if (_rig.BindPixels.Length == 0 || _rig.Bones.Count == 0)
        {
            ShowOperationError("Generate frames", "This rig has no bound pixels. Open Rigging and click Rig pixels first.");
            return;
        }
        _playback.Stop(); _generationCancel = new CancellationTokenSource(); _poseControls.Enabled = _tabs.Enabled = _canvas.Enabled = false; _status.Text = "Generating sprite frames… Cancel work keeps the document unchanged.";
        try
        {
            _rig.Name = string.IsNullOrWhiteSpace(_name.Text) ? "New rig" : _name.Text.Trim();
            await _generate(_rig,_activeAnimation!,_generationCancel.Token);
            _dirty = false; RefreshLibraryEntry(); RefreshLists();
            _status.Text = $"Generated {_activeAnimation!.Keys.Max(key => key.Frame)} frames for '{_activeAnimation.Name}'. Close this window to see the selected animation in the timeline.";
        }
        catch (OperationCanceledException) { _status.Text = "Generation cancelled."; }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Pixel rig generation: " + error); ShowOperationError("Generate frames", error.Message); }
        finally { CompleteCancellableWork(); }
    }

    private void ShowOperationError(string operation, string message)
    {
        _status.Text = message;
        if (!Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
            ThemeMessageBox.Show(this, message, operation, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void CompleteCancellableWork()
    {
        _generationCancel?.Dispose(); _generationCancel = null;
        if (!IsDisposed) _poseControls.Enabled = _tabs.Enabled = _canvas.Enabled = true;
        if (_closeAfterCancellation && !IsDisposed)
            BeginInvoke(new System.Action(() => { _dirty = false; DialogResult = DialogResult.Cancel; Close(); }));
    }
    private void PointerDown(ImageCanvasPointerEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _playback.Stop(); _dragStart = e.ImagePoint; _beforeDrag = PixelRigRasterizer.Copy(_pose); _beforeJointDrag = PixelRigRasterizer.Copy(_poseJoints); _dragging = true;
        Vector2 point = new(e.ImagePoint.X,e.ImagePoint.Y);
        _dragHandle = RigDragHandle.None;
        if (_createJoints && _rig.BindPixels.Length == 0)
        {
            if (_poseJoints.Count >= 128) { _dragging = false; _status.Text = "A rig supports up to 128 joints."; return; }
            var joint = new ImagePixelJoint { Name = "Joint " + (_poseJoints.Count+1), Centre = PixelRigRasterizer.Value(point), Radius = .25 };
            _poseJoints.Add(joint); _selectedJoint = joint.Id; _selectedBone = null;
        }
        else if (_createBones && _rig.BindPixels.Length == 0)
        {
            if (_pose.Count >= 128) { _dragging = false; _status.Text = "A rig supports up to 128 bones."; return; }
            var startJoint = FindSnapJoint(point);
            if (startJoint != null) point = PixelRigRasterizer.Vector(startJoint.Centre);
            var parent = _pose.LastOrDefault(b => (startJoint != null && b.EndJointId == startJoint.Id) || Vector2.Distance(PixelRigRasterizer.Vector(b.End),point) < 5/_canvas.Zoom);
            var bone = new ImagePixelBone { Name = "Bone " + (_pose.Count+1), ParentId = parent?.Id,
                StartJointId = startJoint?.Id, Start = parent == null ? PixelRigRasterizer.Value(point) : PixelRigRasterizer.Copy(parent.End), End = PixelRigRasterizer.Value(point) };
            _pose.Add(bone); _selectedBone = bone.Id; _selectedJoint = startJoint?.Id; _dragHandle = RigDragHandle.End;
        }
        else if (_editJoints.Checked)
        {
            var joint=HitJointRing(point) ?? HitJointArea(point);
            _selectedJoint=joint?.Id; _selectedBone=null; _dragging=joint!=null;
            _jointResize=joint!=null && Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre))>Math.Max(2,6/_canvas.Zoom)
                && Math.Abs(Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre))-(float)joint.Radius)<=Math.Max(2,6/_canvas.Zoom);
        }
        else
        {
            bool shift = (e.Modifiers & Keys.Shift) != 0;
            var hit = HitBone(point);
            if (hit.Bone != null)
            {
                _selectedBone = hit.Bone.Id; _dragHandle = hit.Handle; _selectedJoint = null;
            }
            else
            {
                var joint = !shift ? HitJointRing(point) ?? HitJointArea(point) : null;
                _selectedJoint = joint?.Id; _selectedBone = null; _dragging = false;
            }
        }
        Describe(); RenderPose();
    }

    private (ImagePixelBone? Bone, RigDragHandle Handle) HitBone(Vector2 point)
    {
        float handleRadius = Math.Max(1.5f,8/_canvas.Zoom);
        foreach (var bone in _pose.OrderBy(b => b.Id==_selectedBone && SegmentDistance(point,b)<=handleRadius ? 0 : 1).ThenBy(b => SegmentDistance(point,b)))
        {
            Vector2 start = PixelRigRasterizer.Vector(bone.Start), end = PixelRigRasterizer.Vector(bone.End), centre = (start+end)/2;
            var distances = new[] { (Vector2.Distance(point,start),RigDragHandle.Start), (Vector2.Distance(point,end),RigDragHandle.End), (Vector2.Distance(point,centre),RigDragHandle.Centre) };
            var nearest = distances.OrderBy(value => value.Item1).First();
            if (nearest.Item1 <= handleRadius) return (bone,nearest.Item2);
            if (SegmentDistance(point,bone) <= Math.Max(2,6/_canvas.Zoom)) return (bone,RigDragHandle.Centre);
        }
        return (null,RigDragHandle.None);
    }

    private ImagePixelJoint? HitJointRing(Vector2 point) => _poseJoints
        .OrderBy(joint => Math.Abs(Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre))-(float)joint.Radius))
        .FirstOrDefault(joint => Math.Abs(Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre))-(float)joint.Radius) <= Math.Max(1.5f,5/_canvas.Zoom));
    private ImagePixelJoint? HitJointArea(Vector2 point) => _poseJoints
        .OrderBy(joint => Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre)))
        .FirstOrDefault(joint => Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre)) <= Math.Max((float)joint.Radius,6/_canvas.Zoom));
    private ImagePixelJoint? FindSnapJoint(Vector2 point, bool generous = false) => _poseJoints
        .OrderBy(joint => Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre)))
        .FirstOrDefault(joint => Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre)) <= Math.Max((float)joint.Radius,(generous ? 12 : 5)/_canvas.Zoom));
    private static float SegmentDistance(Vector2 point, ImagePixelBone bone)
    {
        var start = PixelRigRasterizer.Vector(bone.Start); var delta = PixelRigRasterizer.Vector(bone.End)-start;
        float t = Math.Clamp(Vector2.Dot(point-start,delta)/Math.Max(.001f,delta.LengthSquared()),0,1); return Vector2.Distance(point,start+delta*t);
    }
    private void PointerMove(ImageCanvasPointerEventArgs e)
    {
        if (!_dragging) return;
        Vector2 point = new(e.ImagePoint.X,e.ImagePoint.Y);
        if (_createJoints && _rig.BindPixels.Length == 0 && _selectedJoint != null)
        {
            var joint = _poseJoints.First(j => j.Id == _selectedJoint);
            joint.Radius = Math.Max(.25,Vector2.Distance(PixelRigRasterizer.Vector(joint.Centre),point));
        }
        else if (_createBones && _rig.BindPixels.Length == 0 && _selectedBone != null)
        {
            var target = FindSnapJoint(point);
            var bone = _pose.First(b => b.Id == _selectedBone);
            bone.End = PixelRigRasterizer.Value(target == null ? point : PixelRigRasterizer.Vector(target.Centre));
            bone.EndJointId = target?.Id; _selectedJoint = target?.Id;
        }
        else if (_editJoints.Checked && _selectedJoint!=null)
        {
            _pose=PixelRigRasterizer.Copy(_beforeDrag); _poseJoints=PixelRigRasterizer.Copy(_beforeJointDrag);
            var joint=_poseJoints.First(j=>j.Id==_selectedJoint);
            if(_jointResize) joint.Radius=Math.Max(.5,Vector2.Distance(point,PixelRigRasterizer.Vector(joint.Centre)));
            else PixelRigPoseEditor.MoveJoint(_pose,_poseJoints,joint.Id,PixelRigRasterizer.Vector(joint.Centre)+point-new Vector2(_dragStart.X,_dragStart.Y));
        }
        else if (_selectedBone != null)
        {
            _pose = PixelRigRasterizer.Copy(_beforeDrag);
            _poseJoints = PixelRigRasterizer.Copy(_beforeJointDrag);
            var bone = _pose.First(b => b.Id == _selectedBone);
            Vector2 start = PixelRigRasterizer.Vector(bone.Start), end = PixelRigRasterizer.Vector(bone.End);
            Vector2 drag = new(_dragStart.X,_dragStart.Y);
            var affected = PixelRigPoseEditor.AffectedBones(_pose,bone.Id,_followChildren.SelectedIndex==1);
            if ((e.Modifiers & Keys.Shift) != 0)
            {
                var target = FindSnapJoint(point,true);
                if (target != null) { AttachToJoint(bone,target,point,affected); PixelRigPoseEditor.Constrain(_pose,_poseJoints); }
                else _selectedJoint = null;
                _dirty = true; RenderPose(); return;
            }
            Matrix3x2 transform;
            string? pivotJointId = null;
            bool resize=_poseMode.SelectedIndex==3;
            bool rotate=_poseMode.SelectedIndex==2 || (_poseMode.SelectedIndex==0 && _dragHandle is RigDragHandle.Start or RigDragHandle.End);
            if (resize || rotate)
            {
                bool startHandle = _dragHandle == RigDragHandle.Start;
                Vector2 pivot = startHandle ? end : start, handle = startHandle ? start : end;
                pivotJointId = startHandle ? bone.EndJointId : bone.StartJointId;
                if (_dragHandle==RigDragHandle.Centre)
                {
                    var anchor=BonePivot(bone); pivot=anchor.Point; pivotJointId=anchor.Joint; handle=drag;
                }
                var pivotJoint = _poseJoints.FirstOrDefault(joint => joint.Id == pivotJointId);
                if (pivotJoint != null) pivot = PixelRigRasterizer.Vector(pivotJoint.Centre);
                if(resize)
                {
                    Vector2 axis=Vector2.Normalize(handle-pivot);
                    if(!float.IsFinite(axis.X)) axis=Vector2.UnitX;
                    float scale=Math.Clamp(Vector2.Dot(point-pivot,axis)/Math.Max(.001f,Vector2.Distance(handle,pivot)),.025f,40f);
                    transform=Matrix3x2.CreateTranslation(-pivot)*Matrix3x2.CreateScale(scale)*Matrix3x2.CreateTranslation(pivot);
                }
                else
                {
                    float angle = MathF.Atan2(point.Y-pivot.Y,point.X-pivot.X)-MathF.Atan2(handle.Y-pivot.Y,handle.X-pivot.X);
                    transform = Matrix3x2.CreateTranslation(-pivot)*Matrix3x2.CreateRotation(angle)*Matrix3x2.CreateTranslation(pivot);
                }
            }
            else
            {
                transform = Matrix3x2.CreateTranslation(point-drag);
            }
            PixelRigPoseEditor.Transform(_pose,_poseJoints,bone.Id,transform,_followChildren.SelectedIndex==1,pivotJointId);
        }
        _dirty = true; RenderPose();
    }

    private HashSet<string> DescendantsOf(string boneId)
    {
        var affected = new HashSet<string> { boneId };
        for (int index = 0; index < _pose.Count; index++)
            foreach (var child in _pose.Where(bone => bone.ParentId != null && affected.Contains(bone.ParentId))) affected.Add(child.Id);
        return affected;
    }

    private HashSet<string> JointIdsFor(HashSet<string> boneIds) => _pose.Where(bone => boneIds.Contains(bone.Id))
        .SelectMany(bone => new[] { bone.StartJointId,bone.CentreJointId,bone.EndJointId }).Where(id => id != null).Select(id => id!).ToHashSet();

    private void AttachToJoint(ImagePixelBone bone, ImagePixelJoint target, Vector2 point, HashSet<string> affected)
    {
        Vector2 centre = PixelRigRasterizer.Vector(target.Centre);
        if (_dragHandle == RigDragHandle.Start) { bone.Start = PixelRigRasterizer.Value(centre); bone.StartJointId = target.Id; }
        else if (_dragHandle == RigDragHandle.End) { bone.End = PixelRigRasterizer.Value(centre); bone.EndJointId = target.Id; }
        else
        {
            Vector2 middle = (PixelRigRasterizer.Vector(bone.Start)+PixelRigRasterizer.Vector(bone.End))/2;
            Matrix3x2 move = Matrix3x2.CreateTranslation(centre-middle);
            foreach (var child in _pose.Where(candidate => affected.Contains(candidate.Id)))
            { child.Start = PixelRigRasterizer.Value(Vector2.Transform(PixelRigRasterizer.Vector(child.Start),move)); child.End = PixelRigRasterizer.Value(Vector2.Transform(PixelRigRasterizer.Vector(child.End),move)); }
            foreach (var joint in _poseJoints.Where(joint => joint.Id != target.Id && !joint.Pinned && JointIdsFor(affected).Contains(joint.Id)))
                joint.Centre = PixelRigRasterizer.Value(Vector2.Transform(PixelRigRasterizer.Vector(joint.Centre),move));
            bone = _pose.First(candidate => candidate.Id == bone.Id); bone.CentreJointId = target.Id;
        }
        if (_dragHandle == RigDragHandle.Start)
            bone.ParentId = _pose.LastOrDefault(candidate => candidate.Id != bone.Id && candidate.EndJointId == target.Id && !DescendantsOf(bone.Id).Contains(candidate.Id))?.Id ?? bone.ParentId;
        if (_dragHandle == RigDragHandle.End)
            foreach (var child in _pose.Where(candidate => candidate.Id != bone.Id && candidate.StartJointId == target.Id && !DescendantsOf(candidate.Id).Contains(bone.Id))) child.ParentId = bone.Id;
        _selectedJoint = target.Id;
    }

    private void ConstrainAffectedBones(HashSet<string> affected)
    {
        foreach (var bone in _pose.Where(candidate => affected.Contains(candidate.Id)))
        {
            var start = _poseJoints.FirstOrDefault(joint => joint.Id == bone.StartJointId);
            var end = _poseJoints.FirstOrDefault(joint => joint.Id == bone.EndJointId);
            var centre = _poseJoints.FirstOrDefault(joint => joint.Id == bone.CentreJointId);
            if (start != null) bone.Start = PixelRigRasterizer.Copy(start.Centre);
            if (end != null) bone.End = PixelRigRasterizer.Copy(end.Centre);
            if (centre != null)
            {
                Vector2 middle = (PixelRigRasterizer.Vector(bone.Start)+PixelRigRasterizer.Vector(bone.End))/2;
                Vector2 delta = PixelRigRasterizer.Vector(centre.Centre)-middle;
                bone.Start = PixelRigRasterizer.Value(PixelRigRasterizer.Vector(bone.Start)+delta);
                bone.End = PixelRigRasterizer.Value(PixelRigRasterizer.Vector(bone.End)+delta);
            }
        }
    }
    private void PointerUp(ImageCanvasPointerEventArgs e)
    {
        if (!_dragging) return; PointerMove(e); _dragging = false;
        _poseJoints.RemoveAll(joint => joint.Radius < .5);
        _pose.RemoveAll(b => Vector2.Distance(PixelRigRasterizer.Vector(b.Start),PixelRigRasterizer.Vector(b.End)) < .25f);
        var before=new PoseEdit(_beforeDrag,_beforeJointDrag);
        if(System.Text.Json.JsonSerializer.Serialize(before)!=System.Text.Json.JsonSerializer.Serialize(SnapshotPose()))
        { _poseUndo.Push(before); _poseRedo.Clear(); }
        RenderPose();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData==(Keys.Control|Keys.Z) && ActiveControl is not TextBoxBase) { UndoPoseEdit(); return true; }
        if (keyData==(Keys.Control|Keys.Y) && ActiveControl is not TextBoxBase) { RedoPoseEdit(); return true; }
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg,keyData);
        if (_dragging) { _pose = _beforeDrag; _poseJoints = _beforeJointDrag; _dragging = false; _canvas.Capture = false; RenderPose(); return true; }
        if (_createBones || _createJoints) { _createBones = false; _createJoints = false; Describe(); return true; }
        Close(); return true;
    }
    private async void RenderPose()
    {
        if (IsDisposed) return;
        RefreshPoseControls();
        _renderCancel?.Cancel(); _renderCancel?.Dispose(); var cancel = _renderCancel = new CancellationTokenSource(); var token = cancel.Token;
        var affected=PreviewAffectedBones();
        _canvas.Overlay.BoneSegments = _pose.Select(b => new ImageBoneOverlaySegment { Start = PixelRigRasterizer.Vector(b.Start), End = PixelRigRasterizer.Vector(b.End), Selected = b.Id == _selectedBone, Affected=affected.Contains(b.Id) }).ToList();
        _canvas.Overlay.JointCircles = _poseJoints.Select(joint => new ImageJointOverlayCircle
        {
            Centre = PixelRigRasterizer.Vector(joint.Centre), Radius = (float)joint.Radius,
            Selected = joint.Id == _selectedJoint, Preview = _createJoints && _dragging && joint.Id == _selectedJoint, Pinned=joint.Pinned
        }).ToList();
        try
        {
            var bones = PixelRigRasterizer.Copy(_pose); var joints=PixelRigRasterizer.Copy(_poseJoints); var renderer = _rasterizer;
            if (renderer == null && _rig.BindPixels.Length > 0)
            {
                var rig = _rig; renderer = await Task.Run(() => new PixelRigRasterizer(rig,token),token);
                if (token.IsCancellationRequested || IsDisposed) return; _rasterizer = renderer;
            }
            byte[] pixels = renderer == null ? _source : await Task.Run(() => renderer.Render(bones,token,joints),token);
            if (IsDisposed || token.IsCancellationRequested) return;
            _canvas.SetSurface(pixels,_rig.Width,_rig.Height,++_surfaceVersion); _canvas.MarkSurfaceDirty();
            if (_fitPending && _canvas.IsHandleCreated) { _canvas.FitToView(); _fitPending = false; }
        }
        catch (OperationCanceledException) { /* A newer pointer position replaces this preview. */ }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Rig preview: " + error); if (!IsDisposed) _status.Text = error.Message; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _poseTips.Dispose(); _playback.Dispose(); _renderCancel?.Cancel(); _renderCancel?.Dispose(); _renderCancel = null; _generationCancel?.Cancel(); }
        base.Dispose(disposing);
    }
}

using System.Drawing;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Image Editor's Rig → Pose → Animate workflow, using the real 3D viewport and skinning path.</summary>
public sealed partial class ModelAnimationStudioDialog : DpiAwareForm
{
    private const string ActiveRigMetadataKey = "genesis.editor.activeRig";
    private GModelAsset _asset;
    private readonly GModelAnimationClip _working = new() { Name = "Working pose", Fps = 30, Loop = false };
    private readonly ModelRigViewportControl _preview;
    private readonly ImageRigPages _tabs;
    private readonly Panel[] _page = new Panel[3];
    private readonly Label _status = new() { Dock = DockStyle.Fill, Padding = new Padding(14, 9, 8, 4), AutoEllipsis = true };
    private readonly Label _guide = new() { Dock = DockStyle.Top, Height = 78, Padding = new Padding(12), AutoEllipsis = true };
    private readonly ListBox _rigs = List("SavedModelRigs");
    private readonly ListBox _poses = List("SavedModelPoses");
    private readonly ListBox _animations = List("SavedModelAnimations");
    private readonly TextBox _rigName = Field("My rig"), _poseName = Field("New pose"), _animationName = Field("Animation");
    private readonly NumericUpDown _frames = Number(1, 10000, 60), _fps = Number(1, 240, 30);
    private readonly CheckBox _loop = new() { Text = "Loop", AutoSize = true, Checked = true };
    private readonly DataGridView _keys = new() { Name = "ModelPoseAssignments", Dock = DockStyle.Fill, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly string _initialClip;
    private GModelPoseAnimation _animation = new();
    private Matrix4x4[] _poseLocals = [];
    private bool _refreshing, _settingPreview, _layoutDirty;
    private int _selectedPage = -1;
    public string SelectedClip { get; private set; } = "";
    public ModelRigViewportControl Preview => _preview;
    public string StatusText => _status.Text;
    public int SelectedPage => _selectedPage;

    public ModelAnimationStudioDialog(string resourcePath, string projectRoot, GModelAsset source, int initialPage = 0)
    {
        _asset = ModelPoseWorkflow.Copy(source);
        RestoreRigName();
        _initialClip = _asset.Animations.FirstOrDefault()?.Name ?? "";
        SelectedClip = _initialClip;
        while (_asset.Animations.Any(c => c.Name == _working.Name)) _working.Name += " ·";
        Text = "Rigging · Rigs, poses and animations";
        ClientSize = new Size(1440, 920); MinimumSize = new Size(1080, 720);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = EditorChrome.Canvas; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;

        _preview = new ModelRigViewportControl(resourcePath, projectRoot);
        _preview.ConfigureAnimationDraft();
        _preview.AdoptAnimationAsset(_asset, _initialClip);
        _preview.WorkingPoseChanged += (_, _) =>
        {
            if (_settingPreview) return;
            _poseLocals = _preview.CaptureWorkingPose();
            if (_selectedPage == 0) { _layoutDirty = true; SetStatus("Skeleton layout changed. Bind To Mesh to pose it, or Apply to model to keep it unbound."); }
            else SetStatus((_preview.DirectGestureDescription.Length > 0 ? _preview.DirectGestureDescription + ". " : "Working pose changed. ") + "Save as new or Update pose to keep it in the library.");
        };
        _preview.BoneDrawn += (start, end) => Run(() => DrawBone(start, end));
        _preview.JointPlaced += (point, radius) => Run(() => PlaceJoint(point, radius));
        _preview.PlacementFeedback += SetStatus;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 362));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(_preview, 0, 0);
        var side = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(1) };
        body.Controls.Add(side, 1, 0);
        _tabs = new ImageRigPages { Name = "ModelAnimationSteps", Dock = DockStyle.Fill };
        side.Controls.Add(_tabs);
        BuildRigPage(); BuildPosePage(); BuildAnimationPage();
        for (int i = 0; i < 3; i++) { _page[i].Text = new[] { "1 · Rig", "2 · Pose", "3 · Animate" }[i]; _tabs.AddPage(_page[i]); }
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex != _selectedPage) Run(() => GoToPage(_tabs.SelectedIndex)); };

        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 78, WrapContents = true, Padding = new Padding(8), BackColor = ImageEditorChrome.Surface };
        var action = Combo(126); action.Name = "RigTransformMode"; action.Items.AddRange(["Move / rotate", "Move", "Rotate", "Resize"]); action.SelectedIndex = 0;
        action.SelectedIndexChanged += (_, _) => _preview.DirectPoseMode = action.SelectedIndex;
        var affect = Combo(145); affect.Name = "RigPropagation"; affect.Items.AddRange(["Only this bone", "Connected chain"]); affect.SelectedIndex = 0;
        affect.SelectedIndexChanged += (_, _) => _preview.FollowAnimationChildren = affect.SelectedIndex == 1;
        _preview.FollowAnimationChildren = false;
        var plane = Combo(72); plane.Name = "RigDragPlane"; plane.Items.AddRange(["View", "XY", "XZ", "YZ"]); plane.SelectedIndex = 0;
        plane.SelectedIndexChanged += (_, _) =>
        {
            _preview.JointDrawingPlane = plane.Text;
            if (plane.Text == "View") return;
            _preview.Viewport.Camera.Yaw = plane.Text == "YZ" ? -MathF.PI / 2 : MathF.PI;
            _preview.Viewport.Camera.Pitch = plane.Text == "XZ" ? -MathF.PI / 2 + .01f : 0;
        };
        tools.Controls.AddRange([Caption("Action",44),action,Caption("Affect",44),affect,Caption("Plane",40),plane,
            Button("Undo pose", _preview.Undo), Button("Redo pose", _preview.Redo), Button("Reset pose", ShowRestPose)]);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = EditorChrome.Raised };
        var apply = Button("Apply to model", ApplyToModel); apply.Name = "ApplyModelAnimation"; apply.Dock = DockStyle.Right; apply.Width = 148;
        var cancel = Button("Cancel work", CancelWork); cancel.Name = "CancelModelAnimation"; cancel.Dock = DockStyle.Right; cancel.Width = 122;
        footer.Controls.Add(_status); footer.Controls.Add(cancel); footer.Controls.Add(apply);
        var canvasPanel = new Panel { Dock = DockStyle.Fill }; body.Controls.Remove(_preview); canvasPanel.Controls.Add(_preview); canvasPanel.Controls.Add(tools); body.Controls.Add(canvasPanel,0,0);
        Controls.Add(body); Controls.Add(footer);
        RefreshLibraries();
        SelectEntry(_rigs, _rigName.Text);
        if (_asset.Rig.IsValid) _poseLocals = ModelPoseWorkflow.BindPose(_asset);
        GoToPage(Math.Clamp(initialPage, 0, 2));
        Shown += (_, _) => { _preview.FrameModelForTest(); _keys.ClearSelection(); };
    }

    public GModelAsset Result
    {
        get
        {
            var result = ModelPoseWorkflow.Copy(_asset);
            result.Animations.RemoveAll(c => c.Name == _working.Name);
            return result;
        }
    }

    public void CancelWork() { DialogResult = DialogResult.Cancel; Close(); }

    public void ApplyToModel()
    {
        _preview.CancelJointDrawing();
        _preview.CancelAnimationGesture();
        if (_layoutDirty) CommitUnboundLayout();
        SaveRig();
        // Keep edited recipes too, even when the user plans to generate them later.
        if (_selectedPage == 2 && _keys.Rows.Count > 0) SaveAnimation();
        if (SelectedClip == _working.Name) SelectedClip = _initialClip;
        DialogResult = DialogResult.OK; Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Delete && !TextInputFocused())
        {
            Run(DeleteSelection);
            return true;
        }
        if (keyData == Keys.Escape)
        {
            if (_preview.CancelJointDrawing()) return true;
            if (!_preview.CancelAnimationGesture()) CancelWork();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Z)) { _preview.Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { _preview.Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void GoToPage(int index)
    {
        if (_layoutDirty && index != 0) { _tabs.SelectedIndex = _selectedPage; throw new InvalidOperationException("Click Bind To Mesh to finish the skeleton layout first."); }
        if (_selectedPage == 2 && index != 2 && _keys.Rows.Count > 0) SaveAnimation();
        _selectedPage = index;
        _preview.DrawChildJoints = false;
        _preview.PoseEditingEnabled = index != 2;
        _preview.PreviewRigLayout = index == 0;
        var propagation = (ComboBox)Controls.Find("RigPropagation", true).Single();
        if (index == 0) propagation.SelectedIndex = 0;
        propagation.Enabled = index != 0;
        _tabs.SelectedIndex = index;
        _preview.DrawJointCircles = false;
        _guide.Text = index switch
        {
            0 => "RIG THE MESH\nDraw on the mesh to place joints inside it.\nConnect with bones, then click Bind To Mesh.",
            1 => "POSE THE MESH\nDrag a bone's middle to move; drag its tip to rotate.\nAffect selects one bone or its descendants. View follows the camera.",
            _ => "SET POSES AT FRAMES\nAssign poses, then Generate frames.\nApply to model adds the clip to its timeline.",
        };
        SetStatus(_guide.Text.Replace("\n", " "));
        if (index == 0) ShowRestPose();
        else if (index == 1) ShowPose(_poseLocals.Length == _asset.Rig.Bones.Count ? _poseLocals : ModelPoseWorkflow.BindPose(_asset));
        RefreshLibraries();
    }

    private void BuildRigPage()
    {
        _page[0] = Page();
        var flow = Stack();
        flow.Name = "ModelRigOptions";
        flow.Controls.Add(RigSection("Rig Management", 202,
            Row(Button("New", NewSkeleton), Button("Load", UseSavedRig), Button("Save Rig", () =>
            {
                if (!_asset.Rig.IsValid) throw new InvalidOperationException("Draw a bone or joint before saving a rig.");
                SaveRig();
                SetStatus($"Rig saved as '{_rigName.Text}'. Apply to model to keep it with this model.");
            }), Button("Delete", DeleteRig)), Row(Button("Templates…", OpenRigWizard)), _rigs));
        flow.Controls.Add(RigSection("Drawing", 54,
            Row(Button("Draw Bone", BeginDrawingBones), Button("Draw Joint", BeginDrawingJoints),
                Button("Select", () => { _preview.CancelJointDrawing(); SetStatus("Select a bone or joint. Delete removes the selected element; drag to adjust it."); }))));
        flow.Controls.Add(RigSection("Binding", 54, Row(Button("Bind To Mesh", BindMesh), Button("Unbind", UnbindMesh))));
        _page[0].Controls.Add(flow);
    }

    private static CollapsibleSection RigSection(string title, int height, params Control[] children)
    {
        var section = new CollapsibleSection(title, height, 332) { Name = "ModelRig" + title.Replace(" ", "") };
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = children.Length, Padding = new Padding(4) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < children.Length; i++)
        {
            stack.RowStyles.Add(children[i] is FlowLayoutPanel ? new RowStyle(SizeType.Absolute, 40) : new RowStyle(SizeType.Percent, 100));
            children[i].Dock = DockStyle.Fill; stack.Controls.Add(children[i], 0, i);
        }
        section.Content.Controls.Add(stack); return section;
    }

    public void BeginDrawingBones()
    {
        _preview.CancelJointDrawing();
        _preview.DrawJointCircles = false; _preview.DrawChildJoints = true;
        SetStatus("Drag on the mesh: both bone ends sit inside the clicked parts. Start or finish on a joint to connect. Esc returns to Select / pose.");
    }

    public void BeginDrawingJoints()
    {
        _preview.CancelJointDrawing();
        _preview.DrawChildJoints = false; _preview.DrawJointCircles = true;
        SetStatus("Click the mesh to centre a joint inside that part, then drag outward to size its circle. Orbit to check the placement.");
    }

    private void BuildPosePage()
    {
        _page[1] = Page(); _page[1].Controls.Add(_poses);
        var flow = Stack();
        flow.Controls.Add(Row(_poseName, Button("Save pose", () => SaveCurrentPose(_poseName.Text)), Button("Load pose", LoadPose),
            Button("Update pose", UpdatePose), Button("Delete pose", DeletePose), Button("Rename", RenamePose)));
        _page[1].Controls.Add(flow);
        _poses.DoubleClick += (_, _) => Run(LoadPose);
        _poses.SelectedIndexChanged += (_, _) => { if (!_refreshing && _poses.SelectedItem is Entry entry) _poseName.Text = entry.Name; };
    }

    private void BuildAnimationPage()
    {
        _page[2] = Page();
        _animations.Dock = DockStyle.None; _animations.Width = 326; _animations.Height = 94;
        _animations.SelectedIndexChanged += (_, _) =>
        {
            if (!_refreshing && _animations.SelectedItem is Entry entry) Run(() => LoadAnimation(entry.Id));
        };
        var top = Stack(); top.Controls.Add(Caption("POSE ANIMATIONS")); top.Controls.Add(_animations);
        top.Controls.Add(Row(Button("New animation", NewAnimation), Button("Delete animation", DeleteAnimation)));
        top.Controls.Add(_animationName);
        top.Controls.Add(Row(Caption("Frames", 48), _frames, Caption("FPS", 30), _fps, _loop));
        top.Controls.Add(Row(Button("Add pose frame", AddPoseFrame), Button("Remove frame", () => { if (_keys.CurrentRow is { } row) _keys.Rows.Remove(row); })));
        _keys.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Frame", Width = 52 });
        _keys.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Saved pose", FlatStyle = FlatStyle.Flat, DisplayMember = "Name", ValueMember = "Id", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _keys.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "To next", Width = 78, FlatStyle = FlatStyle.Flat, DataSource = Enum.GetNames<GModelPoseInterpolation>() });
        _keys.BackgroundColor = EditorChrome.Canvas; _keys.BorderStyle = BorderStyle.None;
        _keys.EnableHeadersVisualStyles = false; _keys.GridColor = EditorChrome.Border;
        _keys.DefaultCellStyle.BackColor = EditorChrome.Surface; _keys.DefaultCellStyle.ForeColor = EditorChrome.Text;
        _keys.DefaultCellStyle.SelectionBackColor = EditorChrome.Hover; _keys.DefaultCellStyle.SelectionForeColor = EditorChrome.Text;
        _keys.ColumnHeadersDefaultCellStyle.BackColor = EditorChrome.Raised; _keys.ColumnHeadersDefaultCellStyle.ForeColor = EditorChrome.Text;
        _keys.RowTemplate.Height = 30;
        _keys.DataError += (_, e) => { e.ThrowException = false; SetStatus("Choose an existing pose and a valid frame number."); };
        var bottom = Stack(); bottom.Dock = DockStyle.Bottom;
        bottom.Controls.Add(Row(Button("Save animation", SaveAnimation), Button("Generate frames", GenerateFrames)));
        bottom.Controls.Add(Row(Button("Play / pause", TogglePlayback), Button("Stop", () => _preview.PlayClip(SelectedClip, false))));
        bottom.Controls.Add(Hint("Linear and Smooth blend XYZ and quaternion rotation. Hold keeps a pose until the next key. Regenerate replaces this clip's baked frames."));
        _page[2].Controls.Add(_keys); _page[2].Controls.Add(top); _page[2].Controls.Add(bottom);
    }

    private void ResetRig(GModelRig rig)
    {
        _asset.Rig = ModelPoseWorkflow.Copy(rig);
        _asset.RigWizard = null;
        _asset.Poses.Clear(); _asset.PoseAnimations.Clear(); _asset.Animations.Clear();
        _animation = new(); _keys.Rows.Clear(); SelectedClip = "";
        _poseLocals = ModelPoseWorkflow.BindPose(_asset); _layoutDirty = true;
        _preview.AdoptAnimationAsset(_asset, "");
        ShowPose(_poseLocals); RefreshLibraries();
    }

    public void NewSkeleton()
    {
        SaveRig();
        ResetRig(new GModelRig());
        _asset.Metadata.Remove(ActiveRigMetadataKey);
        _rigName.Text = "Rig " + (_asset.RigLibrary.Count + 1);
        while (_asset.RigLibrary.Any(r => r.Name == _rigName.Text)) _rigName.Text += " copy";
        _preview.DrawChildJoints = true; _preview.DrawJointCircles = false;
        SetStatus("Drag to draw a bone, or choose Draw joints. No template is created.");
    }

    public int PlaceJoint(Vector3 point, float radius)
    {
        if (!_asset.Rig.IsValid) _poseLocals = [];
        int index = FindJoint(point);
        if (index < 0) { AddJointAt(-1, point); index = _asset.Rig.Bones.Count - 1; }
        _asset.Rig.Bones[index].JointRadius = Math.Max(.001f,radius);
        _preview.SelectAnimationNode(_asset.Rig.Bones[index].Name); _layoutDirty = true;
        SaveRig();
        SetStatus("Joint placed. Draw bones between joints, then click Bind To Mesh.");
        return index;
    }
    private int FindJoint(Vector3 point)
    {
        var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(_asset.Rig.Bones,_poseLocals);
        float snap = Math.Max(.015f,_asset.Bounds.Size.Length()*.02f);
        int closest = -1; float best = float.MaxValue;
        for (int i = 0; i < worlds.Length; i++)
        {
            float distance = Vector3.Distance(worlds[i].Translation, point);
            if (distance <= Math.Max(snap, _asset.Rig.Bones[i].JointRadius) && distance < best)
            { closest = i; best = distance; }
        }
        return closest;
    }

    public void AddJoint()
    {
        if (!_asset.Rig.IsValid) { NewSkeleton(); return; }
        int parent = _preview.SelectedAnimationBoneIndex;
        var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(_asset.Rig.Bones, _poseLocals);
        var origin = parent >= 0 ? worlds[parent].Translation : Vector3.Zero;
        AddJointAt(parent, origin + new Vector3(0, Math.Max(.1f, _asset.Bounds.Size.Y * .1f), 0));
    }

    public void AddJointAt(int parent, Vector3 worldPosition)
    {
        if (parent < -1 || parent >= _asset.Rig.Bones.Count) throw new InvalidOperationException("Choose a parent joint.");
        if (!float.IsFinite(worldPosition.LengthSquared())) throw new InvalidOperationException("Use a finite joint position.");
        string name = "Joint " + (_asset.Rig.Bones.Count + 1);
        while (_asset.Rig.Bones.Any(b => b.Name == name)) name += " copy";
        var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(_asset.Rig.Bones, _poseLocals);
        var inverse = Matrix4x4.Identity;
        if (parent >= 0 && !Matrix4x4.Invert(worlds[parent], out inverse)) throw new InvalidOperationException("The parent transform has zero scale.");
        var local = Matrix4x4.CreateTranslation(Vector3.Transform(worldPosition, inverse));
        _asset.Rig.Bones.Add(new GModelBone { Name = name, ParentIndex = parent, BindLocal = local });
        _poseLocals = [.. _poseLocals, local];
        foreach (var pose in _asset.Poses) pose.LocalBoneTransforms = [.. pose.LocalBoneTransforms, local];
        foreach (var clip in _asset.Animations.Where(c => c != _working))
            foreach (var frame in clip.Frames) frame.LocalBoneTransforms = [.. frame.LocalBoneTransforms, local];
        _layoutDirty = true;
        _preview.AdoptAnimationAsset(_asset, _working.Name); ShowPose(_poseLocals);
        _preview.SelectAnimationNode(name); RefreshLibraries();
        SetStatus("Child joint created. Move it to the next pivot, then Bind To Mesh.");
    }

    private void RenameJoint()
    {
        int index = _preview.SelectedAnimationBoneIndex;
        if (index < 0) throw new InvalidOperationException("Select a joint first.");
        using var dialog = new Form { Text = "Joint name", ClientSize = new Size(340, 90), StartPosition = FormStartPosition.CenterParent };
        var name = Field(_asset.Rig.Bones[index].Name); name.Dock = DockStyle.Top;
        var ok = new Button { Text = "Rename", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        dialog.Controls.Add(name); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(name.Text)) return;
        if (_asset.Rig.Bones.Where((_, i) => i != index).Any(b => b.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A joint already uses this name.");
        _asset.Rig.Bones[index].Name = name.Text.Trim();
        _preview.AdoptAnimationAsset(_asset, _working.Name); _preview.SelectAnimationNode(name.Text.Trim());
    }

    public void DeleteJoint()
    {
        DeleteRigElement(removeJoint: true);
    }

    public void BindMesh()
    {
        ModelPoseWorkflow.ValidatePose(_asset, _poseLocals);
        for (int i = 0; i < _poseLocals.Length; i++) _asset.Rig.Bones[i].BindLocal = _poseLocals[i];
        // Authored skeletons must never be regenerated by the template migration loader.
        _asset.Rig.TemplateName = "";
        _asset.SkinBindingMode = GModelSkinBindingMode.Smooth4;
        GModelPrimitiveFactory.RebuildInverseBindMatrices(_asset.Rig);
        GModelPrimitiveFactory.BindSkinToMesh(_asset);
        ModelRigWizardWorkflow.SynchronizeLandmarks(_asset, bound: true);
        _layoutDirty = false;
        SaveRig();
        // Adopt invalidates the GPU cache; duplicating every mesh here is unnecessary.
        _preview.AdoptAnimationAsset(_asset, _working.Name); ShowPose(_poseLocals);
        if (_asset.Poses.Count == 0) SaveCurrentPose("Rest pose");
        GoToPage(1);
        SetStatus($"Bound {_asset.Meshes.Count} mesh(es) to {_asset.Rig.Bones.Count} joints. Continue to Posing.");
    }

    private void RestoreRigName()
    {
        if (_asset.Rig.IsValid)
        {
            if (_asset.Metadata.TryGetValue(ActiveRigMetadataKey, out string? activeName)
                && _asset.RigLibrary.Any(r => r.Name == activeName))
            {
                _rigName.Text = activeName;
                return;
            }
            // Older models have no active-name metadata. Reuse a matching saved rig.
            var match = _asset.RigLibrary.FirstOrDefault(r => r.Rig.Bones.Count == _asset.Rig.Bones.Count
                && r.Rig.Bones.Zip(_asset.Rig.Bones).All(pair => pair.First.Name == pair.Second.Name
                    && pair.First.ParentIndex == pair.Second.ParentIndex && pair.First.BindLocal == pair.Second.BindLocal
                    && pair.First.JointRadius == pair.Second.JointRadius));
            if (match is not null) { _rigName.Text = match.Name; return; }
        }
        while (_asset.RigLibrary.Any(r => r.Name.Equals(_rigName.Text, StringComparison.OrdinalIgnoreCase)))
            _rigName.Text += " copy";
    }

    private void SaveRig()
    {
        if (!_asset.Rig.IsValid) { _asset.Metadata.Remove(ActiveRigMetadataKey); return; }
        var rig = new GModelRigPreset { Name = _rigName.Text.Trim(), Rig = ModelPoseWorkflow.Copy(_asset.Rig), Binding = _asset.SkinBindingMode };
        if (_layoutDirty) for (int i = 0; i < _poseLocals.Length; i++) rig.Rig.Bones[i].BindLocal = _poseLocals[i];
        GModelPrimitiveFactory.RebuildInverseBindMatrices(rig.Rig);
        int index = _asset.RigLibrary.FindIndex(r => r.Name.Equals(rig.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _asset.RigLibrary.Add(rig); else _asset.RigLibrary[index] = rig;
        _asset.Metadata[ActiveRigMetadataKey] = rig.Name;
        RefreshLibraries(); SelectEntry(_rigs, rig.Name);
    }
    private void UseSavedRig()
    {
        if (_rigs.SelectedItem is not Entry entry) throw new InvalidOperationException("Select a saved rig.");
        SaveRig();
        var preset = _asset.RigLibrary.First(r => r.Name == entry.Id);
        _rigName.Text = preset.Name;
        ResetRig(preset.Rig); _asset.SkinBindingMode = preset.Binding;
        UnbindMesh(); SelectEntry(_rigs, preset.Name); SetStatus("Rig loaded. Adjust it, then Bind To Mesh.");
    }
    private void DeleteRig()
    {
        if (_rigs.SelectedItem is Entry entry)
        {
            _asset.RigLibrary.RemoveAll(r => r.Name == entry.Id);
            if (_rigName.Text == entry.Name) { _asset.Metadata.Remove(ActiveRigMetadataKey); ResetRig(new GModelRig()); UnbindMesh(); }
        }
        RefreshLibraries();
    }

    private void ShowRestPose() => ShowPose(ModelPoseWorkflow.BindPose(_asset));
    private void ShowPose(Matrix4x4[] locals)
    {
        _poseLocals = (Matrix4x4[])locals.Clone();
        if (!_asset.Rig.IsValid) return;
        _settingPreview = true;
        try
        {
            if (!_asset.Animations.Contains(_working)) _asset.Animations.Add(_working);
            _preview.ShowWorkingPose(_working, _poseLocals);
        }
        finally { _settingPreview = false; }
    }

    public GModelPose SaveCurrentPose(string name)
    {
        if (_layoutDirty) throw new InvalidOperationException("Bind To Mesh before saving poses.");
        var pose = ModelPoseWorkflow.SavePose(_asset, name, _poseLocals);
        RefreshLibraries(); SelectEntry(_poses, pose.Id); SetStatus("Saved pose: " + pose.Name); return pose;
    }
    private GModelPose SelectedPose() => _poses.SelectedItem is Entry entry && _asset.Poses.FirstOrDefault(p => p.Id == entry.Id) is { } pose
        ? pose : throw new InvalidOperationException("Select a saved pose first.");
    private void UpdatePose()
    {
        var selected = SelectedPose(); ModelPoseWorkflow.SavePose(_asset, _poseName.Text, _poseLocals, selected.Id);
        RefreshLibraries(); SetStatus("Pose updated. Regenerate animations that use it to update their frames.");
    }
    private void RenamePose()
    {
        var selected = SelectedPose(); ModelPoseWorkflow.SavePose(_asset, _poseName.Text, selected.LocalBoneTransforms, selected.Id); RefreshLibraries();
    }
    private void LoadPose() { var pose = SelectedPose(); ShowPose(pose.LocalBoneTransforms); SetStatus("Loaded " + pose.Name); }
    private void DeletePose() { ModelPoseWorkflow.DeletePose(_asset, SelectedPose().Id); RefreshLibraries(); }

    private void NewAnimation()
    {
        if (_keys.Rows.Count > 0) SaveAnimation();
        _animation = new(); _animationName.Text = "Animation"; _keys.Rows.Clear(); _frames.Value = 60; _fps.Value = 30; _loop.Checked = true;
        _animations.ClearSelected();
    }
    private void LoadAnimation(string id)
    {
        if (_keys.Rows.Count > 0 && _animation.Id != id) SaveAnimation();
        _animation = ModelPoseWorkflow.Copy(_asset.PoseAnimations.First(a => a.Id == id));
        _animationName.Text = _animation.Name; _frames.Value = Math.Clamp(_animation.FrameCount, 1, 10000);
        _fps.Value = Math.Clamp((decimal)_animation.Fps, 1, 240); _loop.Checked = _animation.Loop;
        _keys.Rows.Clear();
        foreach (var key in _animation.Keys) _keys.Rows.Add(key.Frame, key.PoseId, key.Interpolation.ToString());
        var generated = _asset.Animations.FirstOrDefault(c => c.PoseAnimationId == id);
        if (generated is not null) { SelectedClip = generated.Name; _preview.PlayClip(SelectedClip, false); }
    }
    private void AddPoseFrame()
    {
        if (_asset.Poses.Count == 0) throw new InvalidOperationException("Save a pose on the Pose step first.");
        int frame = _keys.Rows.Count == 0 ? 1 : Math.Min((int)_frames.Value, _keys.Rows.Cast<DataGridViewRow>().Max(r => int.TryParse(r.Cells[0].Value?.ToString(), out int f) ? f : 1) + 30);
        _keys.Rows.Add(frame, _poses.SelectedItem is Entry entry ? entry.Id : _asset.Poses[0].Id, "Linear");
    }
    private GModelPoseAnimation ReadAnimation()
    {
        _keys.EndEdit();
        var animation = ModelPoseWorkflow.Copy(_animation);
        animation.Name = _animationName.Text.Trim(); animation.FrameCount = (int)_frames.Value; animation.Fps = (float)_fps.Value; animation.Loop = _loop.Checked;
        animation.Keys.Clear();
        foreach (DataGridViewRow row in _keys.Rows)
        {
            if (!int.TryParse(row.Cells[0].Value?.ToString(), out int frame) || frame < 1 || frame > animation.FrameCount)
                throw new InvalidOperationException("Use whole frame numbers within the animation length.");
            string id = row.Cells[1].Value?.ToString() ?? "";
            if (!_asset.Poses.Any(p => p.Id == id)) throw new InvalidOperationException("Choose a saved pose for each frame.");
            if (!Enum.TryParse<GModelPoseInterpolation>(row.Cells[2].Value?.ToString(), out var interpolation))
                throw new InvalidOperationException("Choose Linear, Smooth or Hold for each transition.");
            animation.Keys.Add(new GModelPoseKey { Frame = frame, PoseId = id, Interpolation = interpolation });
        }
        if (animation.Name.Length == 0 || animation.Keys.Select(k => k.Frame).Distinct().Count() != animation.Keys.Count)
            throw new InvalidOperationException("Enter an animation name and use each frame number once.");
        return animation;
    }
    private void SaveAnimation()
    {
        var animation = ReadAnimation();
        if (_asset.PoseAnimations.Any(a => a.Id != animation.Id && a.Name.Equals(animation.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another animation uses this name.");
        int index = _asset.PoseAnimations.FindIndex(a => a.Id == animation.Id);
        if (index < 0) _asset.PoseAnimations.Add(animation); else _asset.PoseAnimations[index] = animation;
        _animation = animation; RefreshLibraries(); SetStatus("Animation recipe saved. Generate frames to update the playable clip.");
    }
    public void GenerateFrames()
    {
        var animation = ReadAnimation();
        var clip = ModelPoseWorkflow.Generate(_asset, animation); _animation = animation;
        SelectedClip = clip.Name; _preview.PlayClip(clip.Name, false); RefreshLibraries();
        SetStatus($"Generated {clip.Frames.Count} frames for {clip.Name}. Apply to model adds this clip to the main timeline.");
    }
    private void TogglePlayback()
    {
        if (!_asset.Animations.Any(c => c.Name == SelectedClip && c != _working)) throw new InvalidOperationException("Generate frames first.");
        if (_preview.IsAnimationPlaying) _preview.PauseAnimation();
        else if (_preview.ActiveClip == SelectedClip) _preview.ResumeAnimation();
        else _preview.PlayClip(SelectedClip, true);
    }
    private void DeleteAnimation()
    {
        _asset.PoseAnimations.RemoveAll(a => a.Id == _animation.Id); _asset.Animations.RemoveAll(c => c.PoseAnimationId == _animation.Id);
        _keys.Rows.Clear(); _animation = new(); _animationName.Text = "Animation"; SelectedClip = ""; ShowPose(_poseLocals); RefreshLibraries();
    }

    private void RefreshLibraries()
    {
        _refreshing = true;
        try
        {
            Fill(_rigs, _asset.RigLibrary.Select(r => new Entry(r.Name, r.Name)));
            Fill(_poses, _asset.Poses.Select(p => new Entry(p.Id, p.Name)));
            Fill(_animations, _asset.PoseAnimations.Select(a => new Entry(a.Id, a.Name)));
            ((DataGridViewComboBoxColumn)_keys.Columns[1]).DataSource = _asset.Poses.Select(p => new Entry(p.Id, p.Name)).ToList();
        }
        finally { _refreshing = false; }

    }

    private sealed record Entry(string Id, string Name) { public override string ToString() => Name; }
    private static void Fill(ListBox list, IEnumerable<Entry> items)
    {
        string? id = (list.SelectedItem as Entry)?.Id;
        list.BeginUpdate(); list.Items.Clear(); list.Items.AddRange(items.Cast<object>().ToArray()); SelectEntry(list, id); list.EndUpdate();
    }
    private static void SelectEntry(ListBox list, string? id)
    {
        for (int i = 0; i < list.Items.Count; i++) if (list.Items[i] is Entry entry && entry.Id == id) { list.SelectedIndex = i; return; }
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetStatus(ex.Message); System.Media.SystemSounds.Beep.Play(); }
    }
    private void SetStatus(string text) => _status.Text = text;
    private Button Button(string text, Action action)
    {
        var button = new Button { Name = "ModelAction" + text.Replace(" ", ""), Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(65, 30), Margin = new Padding(2, 3, 4, 3) };
        EditorChrome.StyleField(button); button.Click += (_, _) => Run(action); return button;
    }
    private static TextBox Field(string text) { var box = new TextBox { Width = 326, Text = text }; EditorChrome.StyleField(box); return box; }
    private static ThemedComboBox Combo(int width) { var combo = new ThemedComboBox { Width = width }; EditorChrome.StyleField(combo); return combo; }
    private static NumericUpDown Number(int min, int max, int value) { var number = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 62 }; EditorChrome.StyleField(number); return number; }
    private static ListBox List(string name) => new() { Name = name, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text, IntegralHeight = false };
    private static Panel Page() => new() { Padding = new Padding(10, 0, 10, 6), BackColor = EditorChrome.Surface };
    private static FlowLayoutPanel Stack() => new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private static FlowLayoutPanel Row(params Control[] controls) { var row = new FlowLayoutPanel { AutoSize = true, Width = 332, WrapContents = true, Margin = Padding.Empty }; row.Controls.AddRange(controls); return row; }
    private static Label Caption(string text, int width = 320) => new() { Text = text, Width = width, Height = 26, TextAlign = ContentAlignment.MiddleLeft, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont };
    private static Label Hint(string text) => new() { Text = text, Width = 326, Height = 54, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Padding = new Padding(2, 5, 2, 0) };
}

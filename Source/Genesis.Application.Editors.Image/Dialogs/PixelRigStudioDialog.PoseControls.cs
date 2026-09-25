using System.Numerics;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Editors.Image.Dialogs;

public sealed partial class PixelRigStudioDialog
{
    private readonly ComboBox _poseMode = new() { Name="RigTransformMode", DropDownStyle=ComboBoxStyle.DropDownList, Width=135 };
    private readonly ComboBox _followChildren = new() { Name="RigPropagation", DropDownStyle=ComboBoxStyle.DropDownList, Width=145 };
    private readonly ComboBox _bonePicker = new() { Name="RigBonePicker", DropDownStyle=ComboBoxStyle.DropDownList, Width=140, DisplayMember="Name" };
    private readonly CheckBox _editJoints = new() { Text="Edit joints", AutoSize=true };
    private readonly CheckBox _pinJoint = new() { Text="Pin joint", AutoSize=true };
    private readonly CheckBox _fillGaps = new() { Name="RigJointInfill", Text="Fill joint gaps", AutoSize=true, Checked=true };
    private readonly NumericUpDown _boneLength = new() { Name="RigBoneLength", Minimum=.25m, Maximum=8192, DecimalPlaces=2, Width=75 };
    private readonly NumericUpDown _boneAngle = new() { Name="RigBoneAngle", Minimum=-360, Maximum=360, DecimalPlaces=1, Width=70 };
    private readonly NumericUpDown _jointRadius = new() { Name="RigJointRadius", Minimum=.5m, Maximum=4096, DecimalPlaces=1, Width=65 };
    private readonly Label _selectionHint = new() { AutoSize=true, MaximumSize=new Size(680,0), Padding=new Padding(4), Text="Select a bone on the canvas or from the list." };
    private readonly ToolTip _poseTips = new();
    private Panel _poseControls = null!;
    private FlowLayoutPanel _boneOptions = null!, _jointOptions = null!;
    private Button _undoPose = null!, _redoPose = null!;
    private bool _refreshingPoseControls, _jointResize;
    private sealed record PoseEdit(List<ImagePixelBone> Bones,List<ImagePixelJoint> Joints);
    private readonly Stack<PoseEdit> _poseUndo = new(), _poseRedo = new();

    private void BuildPoseControls(Panel container)
    {
        _poseMode.Items.AddRange(["Move / rotate","Move","Rotate","Resize"]); _poseMode.SelectedIndex=0;
        _followChildren.Items.AddRange(["Only this bone","Connected chain"]); _followChildren.SelectedIndex=0;
        var rows = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1, RowCount=4, Padding=new Padding(4), BackColor=ImageEditorChrome.Surface };
        _poseControls=rows;
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int row=0;row<4;row++) rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        static Label Caption(string text) => new() { Text=text, AutoSize=true, Padding=new Padding(0,5,0,0) };
        rows.Controls.Add(Bar(Caption("Action"),_poseMode,Caption("Affect"),_followChildren,_editJoints));
        _boneOptions=new FlowLayoutPanel { AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, WrapContents=false, Margin=Padding.Empty };
        _boneOptions.Controls.AddRange([Caption("Length"),_boneLength,Caption("Angle"),_boneAngle]);
        _jointOptions=new FlowLayoutPanel { AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, WrapContents=false, Margin=Padding.Empty };
        _jointOptions.Controls.AddRange([Caption("Radius"),_jointRadius,_pinJoint]);
        rows.Controls.Add(Bar(_bonePicker,_boneOptions,_jointOptions));
        _undoPose=Action("Undo pose",UndoPoseEdit); _redoPose=Action("Redo pose",RedoPoseEdit);
        rows.Controls.Add(Bar(_undoPose,_redoPose,Action("Reset pose",ResetPoseEdit),Action("Detach bone",DetachSelectedBone),_fillGaps));
        rows.Controls.Add(_selectionHint); container.Controls.Add(_poseControls);
        // Dock the fill viewport after the header has consumed its space. Native GPU child
        // windows otherwise paint over sibling controls even when those siblings are brought forward.
        _canvas.BringToFront();
        _poseTips.SetToolTip(_poseMode,"Move translates. Rotate pivots around an attached joint or the opposite tip. Resize changes length. Move / rotate uses tips to rotate and the middle to move.");
        _poseTips.SetToolTip(_followChildren,"Only this bone keeps other tips in place; shared endpoints still follow their joint. Connected chain transforms descendants too. Pinned joints stay fixed.");
        _poseTips.SetToolTip(_fillGaps,"Fill empty pixels inside joint circles from the original artwork. Transparent source pixels stay transparent. Adjust Radius in Edit joints.");
        _poseMode.SelectedIndexChanged+=(_,_)=>{ StopDrawingTools(); Describe(); };
        _followChildren.SelectedIndexChanged+=(_,_)=>{ Describe(); RenderPose(); };
        _editJoints.CheckedChanged+=(_,_)=>{ StopDrawingTools(); Describe(); RenderPose(); };
        _bonePicker.SelectedIndexChanged+=(_,_)=>
        {
            if (_refreshingPoseControls || _bonePicker.SelectedItem is not ImagePixelBone bone) return;
            _selectedBone=bone.Id; _selectedJoint=null; _editJoints.Checked=false; StopDrawingTools(); RenderPose(); Describe();
        };
        _boneLength.ValueChanged+=(_,_)=>ChangeBoneNumeric(length:true);
        _boneAngle.ValueChanged+=(_,_)=>ChangeBoneNumeric(length:false);
        _jointRadius.ValueChanged+=(_,_)=>
        {
            if (_refreshingPoseControls || _selectedJoint==null) return;
            RecordCurrentPose(); _poseJoints.First(j=>j.Id==_selectedJoint).Radius=(double)_jointRadius.Value;
            _dirty=true; RenderPose();
        };
        _pinJoint.CheckedChanged+=(_,_)=>
        {
            if (_refreshingPoseControls || _selectedJoint==null) return;
            RecordCurrentPose(); _poseJoints.First(j=>j.Id==_selectedJoint).Pinned=_pinJoint.Checked; _dirty=true; RenderPose();
        };
        _fillGaps.CheckedChanged+=(_,_)=>
        {
            if (_refreshingPoseControls) return;
            _rig.FillJointGaps=_fillGaps.Checked; _rasterizer=null; _dirty=true; RenderPose();
        };
    }

    private void StopDrawingTools() { _createBones=false; _createJoints=false; }
    private PoseEdit SnapshotPose() => new(PixelRigRasterizer.Copy(_pose),PixelRigRasterizer.Copy(_poseJoints));
    private void RecordCurrentPose() { _poseUndo.Push(SnapshotPose()); _poseRedo.Clear(); }
    private void UndoPoseEdit()
    {
        if (_poseUndo.Count==0) return;
        _poseRedo.Push(SnapshotPose()); RestorePose(_poseUndo.Pop());
    }
    private void RedoPoseEdit()
    {
        if (_poseRedo.Count==0) return;
        _poseUndo.Push(SnapshotPose()); RestorePose(_poseRedo.Pop());
    }
    private void RestorePose(PoseEdit state)
    { _pose=state.Bones; _poseJoints=state.Joints; _dirty=true; RenderPose(); }
    private void ResetPoseEdit()
    {
        RecordCurrentPose(); _pose=PixelRigRasterizer.Copy(_rig.Bones); _poseJoints=PixelRigRasterizer.Copy(_rig.Joints);
        _dirty=true; StopDrawingTools(); RenderPose();
    }
    private void DetachSelectedBone()
    {
        var bone=_pose.FirstOrDefault(b=>b.Id==_selectedBone); if(bone==null) return;
        RecordCurrentPose(); bone.StartJointId=bone.EndJointId=bone.CentreJointId=bone.ParentId=null;
        _dirty=true; RenderPose(); _status.Text="Detached this bone. Its position is unchanged. Shift-drag to attach it again; Undo pose restores the connections.";
    }
    private (Vector2 Point,string? Joint) BonePivot(ImagePixelBone bone)
    {
        string? id=bone.CentreJointId ?? bone.StartJointId ?? bone.EndJointId;
        var joint=_poseJoints.FirstOrDefault(j=>j.Id==id);
        return (joint==null ? PixelRigRasterizer.Vector(bone.Start) : PixelRigRasterizer.Vector(joint.Centre),id);
    }
    private HashSet<string> PreviewAffectedBones()
    {
        var bone=_pose.FirstOrDefault(b=>b.Id==_selectedBone); if(bone==null) return [];
        string? pivot=null;
        if(_poseMode.SelectedIndex is 2 or 3 || (_poseMode.SelectedIndex==0 && _dragHandle is RigDragHandle.Start or RigDragHandle.End))
            pivot=_dragHandle==RigDragHandle.Start ? bone.EndJointId : _dragHandle==RigDragHandle.End ? bone.StartJointId : BonePivot(bone).Joint;
        return PixelRigPoseEditor.AffectedBones(_pose,bone.Id,_followChildren.SelectedIndex==1,pivot);
    }
    private void ChangeBoneNumeric(bool length)
    {
        var bone=_pose.FirstOrDefault(b=>b.Id==_selectedBone);
        if(_refreshingPoseControls || bone==null) return;
        RecordCurrentPose(); var pivot=BonePivot(bone);
        Vector2 delta=PixelRigRasterizer.Vector(bone.End)-PixelRigRasterizer.Vector(bone.Start);
        Matrix3x2 edit=length ? Matrix3x2.CreateScale((float)_boneLength.Value/Math.Max(.001f,delta.Length()))
            : Matrix3x2.CreateRotation((float)_boneAngle.Value*MathF.PI/180-MathF.Atan2(delta.Y,delta.X));
        PixelRigPoseEditor.Transform(_pose,_poseJoints,bone.Id,Matrix3x2.CreateTranslation(-pivot.Point)*edit*Matrix3x2.CreateTranslation(pivot.Point),_followChildren.SelectedIndex==1,pivot.Joint);
        _dirty=true; RenderPose();
    }
    private void RefreshPoseControls()
    {
        _refreshingPoseControls=true;
        try
        {
            _bonePicker.Items.Clear(); foreach(var bone in _pose) _bonePicker.Items.Add(bone);
            _bonePicker.SelectedIndex=_pose.FindIndex(b=>b.Id==_selectedBone);
            var selected=_pose.FirstOrDefault(b=>b.Id==_selectedBone);
            var joint=_poseJoints.FirstOrDefault(j=>j.Id==_selectedJoint);
            _jointOptions.Visible=_editJoints.Checked||joint!=null;
            _boneOptions.Visible=!_jointOptions.Visible;
            _boneLength.Enabled=_boneAngle.Enabled=selected!=null;
            if(selected!=null)
            {
                Vector2 delta=PixelRigRasterizer.Vector(selected.End)-PixelRigRasterizer.Vector(selected.Start);
                _boneLength.Value=Math.Clamp((decimal)delta.Length(),_boneLength.Minimum,_boneLength.Maximum);
                _boneAngle.Value=Math.Clamp((decimal)(MathF.Atan2(delta.Y,delta.X)*180/MathF.PI),-360,360);
            }
            _jointRadius.Enabled=_pinJoint.Enabled=joint!=null;
            if(joint!=null) { _jointRadius.Value=Math.Clamp((decimal)joint.Radius,_jointRadius.Minimum,_jointRadius.Maximum); _pinJoint.Checked=joint.Pinned; }
            _fillGaps.Checked=_rig.FillJointGaps;
            if(_drawBones!=null) _drawBones.Enabled=_rig.BindPixels.Length==0;
            if(_drawJoints!=null) _drawJoints.Enabled=_rig.BindPixels.Length==0;
            _undoPose.Enabled=_poseUndo.Count>0; _redoPose.Enabled=_poseRedo.Count>0;
            _selectionHint.Text=joint!=null ? $"{joint.Name} · {(joint.Pinned ? "Pinned centre" : "Free centre")} · circle controls the infill area"
                : selected!=null ? $"{selected.Name} · {(_followChildren.SelectedIndex==1 ? PreviewAffectedBones().Count+" bones follow" : "Other tips stay in place; shared joints stay joined") }"
                : "Choose a bone from the list or canvas. Enable Edit joints to move, resize or pin a joint.";
        }
        finally { _refreshingPoseControls=false; }
    }
}

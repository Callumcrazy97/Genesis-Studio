using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelRigViewportControl
{
    private bool _animationDraft;
    private GModelAsset? _rigLayoutPreview;
    internal bool PreviewRigLayout { get; set; }
    private bool _animationFollowChildren = true;
    private Matrix4x4[]? _gizmoPoseBefore;
    public event EventHandler? WorkingPoseChanged;
    public bool FollowAnimationChildren { get => _animationFollowChildren; set => _animationFollowChildren = value; }
    private bool PropagateAnimationEdit => !PreviewRigLayout && _animationFollowChildren;
    public bool IsAnimationPlaying => _animPlaying;
    internal bool PoseEditingEnabled { get; set; } = true;
    public void PauseAnimation() { _animPlaying = false; SyncAnimationTimelinePosition(); }
    public void ResumeAnimation()
    {
        if (ActiveAnimationClip is not { Frames.Count: > 0 } clip) return;
        if (!clip.Loop && CurrentFrameIndex(clip) >= clip.Frames.Count - 1) _animTime = 0;
        _animationTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _animPlaying = true; SyncAnimationTimelinePosition();
    }

    public GModelAsset CaptureAnimationAsset()
    {
        return ModelPoseWorkflow.Copy(_rigged ?? new GModelAsset());
    }

    public ModelAnimationStudioDialog CreateAnimationStudio(int page = 0)
        => new(ResourcePath, ProjectRoot, _rigged ?? new GModelAsset(), page);

    private void OpenAnimationStudio(int page)
    {
        try
        {
            using var studio = CreateAnimationStudio(page);
            if (studio.ShowDialog(this) == DialogResult.OK) ApplyAnimationWorkspace(studio.Result, studio.SelectedClip);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Model animation", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void ApplyAnimationWorkspace(GModelAsset asset, string clip)
    {
        var before = CaptureAnimationAsset();
        var after = ModelPoseWorkflow.Copy(asset);
        string previousClip = _clip;
        AdoptAnimationAsset(ModelPoseWorkflow.Copy(after), clip);
        MarkDirty();
        PushEdit("Rig, poses and animations", () => AdoptAnimationAsset(ModelPoseWorkflow.Copy(after), clip),
            () => AdoptAnimationAsset(ModelPoseWorkflow.Copy(before), previousClip));
    }

    internal void AdoptAnimationAsset(GModelAsset asset, string clip)
    {
        if (_animationDraft) ClearEditHistory();
        _rigLayoutPreview = null;
        _jointPlacementSurface = null;
        _rigged = asset;
        _assetChanged = true;
        RefreshPartList(0);
        PlayClip(clip, play: false);
    }

    private GModelAsset RigLayoutPreview()
    {
        if (_rigLayoutPreview is not null) return _rigLayoutPreview;
        _rigLayoutPreview = ModelPoseWorkflow.Copy(_rigged!);
        foreach (var mesh in _rigLayoutPreview.Meshes) mesh.IsSkinned = false;
        return _rigLayoutPreview;
    }

    internal void ConfigureAnimationDraft()
    {
        _animationDraft = true;
        Controls.Find("LegacyRigHierarchy",true).Single().Visible = false;
        SetSpin(false);
        SetOnionSkin(false);
        SetMotionTrail(false);
    }

    internal Control TakePoseInspector()
    {
        _inspector.Controls.Remove(_animationInspector);
        _animationNodeFrame.Visible = false;
        var flow = _animationInspector.Controls.OfType<FlowLayoutPanel>().Single();
        bool hideNext = false;
        foreach (Control control in flow.Controls)
        {
            if (hideNext) { control.Visible = false; hideNext = false; }
            if (control.Text == "GIZMO") { control.Visible = false; hideNext = true; }
        }
        return _animationInspector;
    }

    private Matrix4x4 AnimationPreviewWorld() => Matrix4x4.CreateTranslation(-(_rigged?.Pivot?.Position ?? Vector3.Zero))
        * Matrix4x4.CreateRotationY(_spin) * (PreviewWorldTransform?.Invoke(_animTime) ?? Matrix4x4.Identity);

    public Matrix4x4[] CaptureWorkingPose() => ActiveAnimationClip is { Frames.Count: > 0 } clip
        ? (Matrix4x4[])clip.Frames[CurrentFrameIndex(clip)].LocalBoneTransforms.Clone()
        : _rigged is null ? [] : ModelPoseWorkflow.BindPose(_rigged);

    internal void ShowWorkingPose(GModelAnimationClip working, Matrix4x4[] locals)
    {
        working.Frames = [new GModelAnimationFrame { LocalBoneTransforms = (Matrix4x4[])locals.Clone() }];
        working.Tracks.Clear();
        PlayClip(working.Name, false);
        _meshDirty = true;
    }

    public bool CancelAnimationGesture()
    {
        if (CancelDirectPose()) return true;
        if (!_gizmoDragging || _gizmoPoseBefore is null || ActiveAnimationClip is not { } clip) return false;
        RestoreAnimationPose(clip, CurrentFrameIndex(clip), _gizmoPoseBefore);
        _gizmoPoseBefore = null;
        _gizmoDragging = false;
        _gizmoAxis = _gizmoBone = -1;
        _viewport.NavigationEnabled = true;
        return true;
    }

    private void RestoreAnimationPose(GModelAnimationClip clip, int frame, Matrix4x4[] locals)
    {
        clip.Frames[frame].LocalBoneTransforms = (Matrix4x4[])locals.Clone();
        for (int bone = 0; bone < locals.Length; bone++) SynchronizeEditedTrack(clip, frame, bone, locals[bone]);
        _meshDirty = true;
        SyncAnimationInspector();
        WorkingPoseChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecordPoseEdit(GModelAnimationClip clip, int frame, Matrix4x4[] before, Matrix4x4[] after)
    {
        if (before.SequenceEqual(after)) return;
        PushEdit("Pose joint", () => RestoreAnimationPose(clip, frame, after), () => RestoreAnimationPose(clip, frame, before));
    }
}

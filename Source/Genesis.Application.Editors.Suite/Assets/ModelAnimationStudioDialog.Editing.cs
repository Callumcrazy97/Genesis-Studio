using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelAnimationStudioDialog
{
    private bool TextInputFocused()
    {
        Control? focused = this;
        while (focused is ContainerControl container && container.ActiveControl is { } active) focused = active;
        return focused is TextBoxBase or UpDownBase or ComboBox or DataGridView;
    }

    public void DeleteSelection() => DeleteRigElement(!_preview.SelectedRigElementIsBone);

    private void DeleteRigElement(bool removeJoint)
    {
        _preview.CancelJointDrawing(); _preview.CancelAnimationGesture();
        int selected = _preview.SelectedAnimationBoneIndex;
        var bones = _asset.Rig.Bones;
        if (selected < 0 || selected >= bones.Count) { SetStatus("Select a bone or joint to delete."); return; }
        if (!removeJoint && bones[selected].ParentIndex < 0) return;
        int[] kept = Enumerable.Range(0, bones.Count).Where(i => !removeJoint || i != selected).ToArray();
        int[] parents = kept.Select(i =>
        {
            int parent = bones[i].ParentIndex;
            return removeJoint ? (parent == selected ? -1 : Array.IndexOf(kept, parent)) : (i == selected ? -1 : parent);
        }).ToArray();
        Matrix4x4[] Convert(Matrix4x4[] locals)
        {
            var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(bones, locals);
            return kept.Select((old, index) =>
            {
                if (parents[index] < 0) return worlds[old];
                if (!Matrix4x4.Invert(worlds[kept[parents[index]]], out var inverse))
                    throw new InvalidOperationException("The joint transform has zero scale.");
                return worlds[old] * inverse;
            }).ToArray();
        }
        var layout = Convert(_poseLocals); var bind = Convert(ModelPoseWorkflow.BindPose(_asset));
        var poses = _asset.Poses.Select(p => Convert(p.LocalBoneTransforms)).ToArray();
        var clips = _asset.Animations.Select(c => c.Frames.Select(f => Convert(f.LocalBoneTransforms)).ToArray()).ToArray();
        var surviving = kept.Select(i => bones[i]).ToList();
        for (int i = 0; i < surviving.Count; i++) { surviving[i].ParentIndex = parents[i]; surviving[i].BindLocal = bind[i]; }
        _asset.Rig.Bones = surviving;
        _asset.RigWizard = null; // A changed topology needs a new semantic mapping.
        for (int i = 0; i < poses.Length; i++) _asset.Poses[i].LocalBoneTransforms = poses[i];
        for (int i = 0; i < clips.Length; i++)
        {
            _asset.Animations[i].Tracks.Clear();
            for (int f = 0; f < clips[i].Length; f++) _asset.Animations[i].Frames[f].LocalBoneTransforms = clips[i][f];
        }
        _poseLocals = layout; _layoutDirty = true;
        if (surviving.Count == 0) { _asset.Poses.Clear(); _asset.PoseAnimations.Clear(); _asset.Animations.Clear(); _keys.Rows.Clear(); }
        ClearBinding();
        GModelPrimitiveFactory.RebuildInverseBindMatrices(_asset.Rig);
        _preview.AdoptAnimationAsset(_asset, _working.Name); ShowPose(layout); RefreshLibraries();
        SetStatus(removeJoint ? "Joint and its attached bones deleted. Other joints stay in place. Bind To Mesh when ready."
            : "Bone deleted. Its joints and other branches stay in place. Bind To Mesh when ready.");
    }

    private void ClearBinding()
    {
        foreach (var mesh in _asset.Meshes) { mesh.IsSkinned = false; mesh.SkinnedVertices = []; }
        _asset.LastSkinDiagnostics = new();
        if (_asset.RigWizard is { } setup) setup.Bound = false;
    }

    private void CommitUnboundLayout()
    {
        if (_asset.Rig.IsValid) ModelPoseWorkflow.ValidatePose(_asset, _poseLocals);
        for (int i = 0; i < _poseLocals.Length; i++) _asset.Rig.Bones[i].BindLocal = _poseLocals[i];
        _asset.Rig.TemplateName = "";
        GModelPrimitiveFactory.RebuildInverseBindMatrices(_asset.Rig);
        ModelRigWizardWorkflow.SynchronizeLandmarks(_asset, bound: false);
        ClearBinding(); _layoutDirty = false;
    }

    public void UnbindMesh()
    {
        _preview.CancelJointDrawing(); _preview.CancelAnimationGesture();
        if (_layoutDirty) CommitUnboundLayout(); else ClearBinding();
        _preview.AdoptAnimationAsset(_asset, _working.Name);
        GoToPage(0);
        SetStatus("Mesh unbound. The rig and saved poses are kept; Bind To Mesh to animate the mesh again.");
    }
}

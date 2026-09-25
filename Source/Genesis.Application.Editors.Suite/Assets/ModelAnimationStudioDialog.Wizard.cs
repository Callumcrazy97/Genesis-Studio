using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelAnimationStudioDialog
{
    public ModelRigWizardDialog CreateRigWizard()
    {
        var source = Result;
        if (_layoutDirty)
        {
            for (int i = 0; i < _poseLocals.Length; i++) source.Rig.Bones[i].BindLocal = _poseLocals[i];
            GModelPrimitiveFactory.RebuildInverseBindMatrices(source.Rig);
            foreach (var mesh in source.Meshes) { mesh.IsSkinned = false; mesh.SkinnedVertices = []; }
            ModelRigWizardWorkflow.SynchronizeLandmarks(source, bound: false);
        }
        return new(_preview.ResourcePath, _preview.ProjectRoot, source);
    }
    private void OpenRigWizard()
    {
        using var wizard = CreateRigWizard();
        if (wizard.ShowDialog(this) == DialogResult.OK) ApplyRigWizardResult(wizard.Result, wizard.SelectedClip);
    }
    public void ApplyRigWizardResult(GModelAsset result, string clip)
    {
        _preview.CancelAnimationGesture(); _preview.CancelJointDrawing();
        _asset = ModelPoseWorkflow.Copy(result); _layoutDirty = false;
        _poseLocals = ModelPoseWorkflow.BindPose(_asset); _keys.Rows.Clear(); _animation = new();
        SelectedClip = clip; _selectedPage = -1;
        _preview.AdoptAnimationAsset(_asset, clip); RefreshLibraries(); GoToPage(0);
        SetStatus(_asset.Meshes.Any(m => m.IsSkinned)
            ? "Rig ready. Adjust joints, pose, or Apply to model."
            : "Rig ready. Adjust joints, then Bind To Mesh, or Apply to model to keep it unbound.");
    }
}

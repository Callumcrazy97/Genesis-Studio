using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Image.Dialogs;

public sealed partial class PixelRigStudioDialog
{
    private WorkflowBar? _rigWorkflow;

    /// <summary>The rigging steps: Bones › Bind › Pose › Animate, ticked off as the rig progresses.</summary>
    public WorkflowBar? RigWorkflow => _rigWorkflow;

    /// <summary>
    /// A step bar across the top of the studio. The first tab carries eight buttons (draw bones,
    /// draw joints, edit, delete, rig pixels, apply pose…) and nothing said which order they go
    /// in; the bar names the four stages, ticks off the finished ones and says what to do next.
    /// </summary>
    private void BuildRigWorkflowBar()
    {
        _rigWorkflow = new WorkflowBar("RigWorkflow",
        [
            new("Bones", "Bones", "Drag from joint to tip on the sprite to draw each bone. Add joints where limbs bend.", ShowBonesStep)
            {
                IsDone = () => _rig.Bones.Count > 0,
            },
            new("Bind", "Bind", "Press Rig pixels to attach the sprite's pixels to its bones.", ShowBindStep)
            {
                IsDone = () => _rig.BindPixels.Length > 0,
            },
            new("Pose", "Pose", "Drag bones to pose the sprite, then Save pose. Make one pose per key moment.", () => _tabs.SelectedIndex = 1)
            {
                IsDone = () => _rig.Poses.Count > 0,
            },
            new("Animate", "Animate", "Add saved poses as frames, then Play. Generate frames fills in the steps between them.", () => _tabs.SelectedIndex = 2)
            {
                IsDone = () => _animations.Items.Count > 0,
            },
        ]);
        Controls.Add(_rigWorkflow);

        // Nearly every rig operation reports through the status line, so it is the one place that
        // always knows when progress may have changed.
        _status.TextChanged += (_, _) => RefreshRigWorkflow();
        RefreshRigWorkflow();
    }

    private void ShowBonesStep()
    {
        _tabs.SelectedIndex = 0;
        _createBones = _rig.BindPixels.Length == 0;
        _createJoints = false;
        Describe();
    }

    private void ShowBindStep()
    {
        _tabs.SelectedIndex = 0;
        _createBones = false;
        _createJoints = false;
        Describe();
    }

    private void RefreshRigWorkflow()
    {
        if (_rigWorkflow is null)
        {
            return;
        }

        _rigWorkflow.SetCurrent(_tabs.SelectedIndex switch
        {
            1 => "Pose",
            2 => "Animate",
            _ => _rig.Bones.Count > 0 && _rig.BindPixels.Length == 0 && !_createBones ? "Bind" : "Bones",
        });
        _rigWorkflow.RefreshProgress();
    }
}

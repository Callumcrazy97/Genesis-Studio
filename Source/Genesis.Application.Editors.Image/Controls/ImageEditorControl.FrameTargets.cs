using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private int[]? _operationFrames;
    private ImageFrameTargetControl AttachFrameTargets(Form dialog,string? note=null)=>
        ImageFrameTargetControl.Attach(dialog,_workspace.SelectedFrameIndex,_workspace.Frames.Count,note);

    private int[] TargetFrameIndices(bool allByDefault=false)=>_operationFrames ??
        (allByDefault ? Enumerable.Range(0,_workspace.Frames.Count).ToArray() : [_workspace.SelectedFrameIndex]);

    private void InFrameScope(int[] indices,Action operation)
    {
        if(indices.Length==0) throw new InvalidOperationException("There are no frames in the selected target range.");
        int[]? before=_operationFrames;
        try { _operationFrames=indices.Distinct().ToArray(); operation(); }
        finally { _operationFrames=before; }
    }

    private void ApplyDialogFrames(ImageFrameTargetControl target,Action operation)=>
        TryCanvasOperation(()=>InFrameScope(target.FrameIndices,operation));

    private void PromptFrameOperation(string title,Action operation,string? note=null)
    {
        using var dialog=new DpiAwareForm{Text=title,ClientSize=new Size(630,70),StartPosition=FormStartPosition.CenterParent};
        var apply=SectionActionButton("Apply",12,18);apply.DialogResult=DialogResult.OK;
        var cancel=SectionActionButton("Cancel",110,18);cancel.DialogResult=DialogResult.Cancel;
        dialog.Controls.AddRange([apply,cancel]);dialog.AcceptButton=apply;dialog.CancelButton=cancel;
        var target=AttachFrameTargets(dialog,note);
        if(dialog.ShowDialog(this)==DialogResult.OK) ApplyDialogFrames(target,operation);
    }

    private ImageLayerBuffer? MatchingLayer(ImageFrameBuffer frame,ImageLayerBuffer reference)=>
        frame.Layers.FirstOrDefault(l=>l.Id==reference.Id)
        ?? frame.Layers.FirstOrDefault(l=>l.Name==reference.Name && l.Channel==reference.Channel);

    public void ApplyEffect(ImageEffectDefinition definition,IReadOnlyDictionary<string,object> parameters,ImageFrameSelection targets)=>
        InFrameScope(targets.Resolve(_workspace.SelectedFrameIndex,_workspace.Frames.Count),()=>ApplyEffect(definition,parameters));

    public void ApplyFrameOperation(string name,Func<byte[],byte[]> operation,ImageFrameSelection targets)=>
        InFrameScope(targets.Resolve(_workspace.SelectedFrameIndex,_workspace.Frames.Count),()=>ApplyOperation(name,operation));
}

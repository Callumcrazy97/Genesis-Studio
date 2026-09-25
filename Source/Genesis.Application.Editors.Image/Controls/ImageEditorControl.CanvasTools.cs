using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private bool _showNineSliceGuides = true;
    public bool RemoveBlankSpace()
    {
        CommitFloatingSelection();
        Rectangle bounds = ImageCanvasTransforms.OpaqueBounds(_workspace,TargetFrameIndices(allByDefault:true));
        if (bounds.IsEmpty || bounds == new Rectangle(0,0,_workspace.Width,_workspace.Height)) return false;
        CropCanvasWithMetadata(bounds,"Remove blank space"); return true;
    }

    private void CropCanvasWithMetadata(Rectangle bounds, string description)
    {
        int w = _workspace.Width,h = _workspace.Height;
        byte[] Pixels(byte[] p) => ImageCanvasTransforms.Remap(p,w,h,bounds.Size,q => new PointF(q.X+bounds.X,q.Y+bounds.Y));
        ImageNineSlice slices = _session.Document.NineSlice.Clone();
        slices.Left = Math.Max(0,slices.Left-bounds.Left); slices.Top = Math.Max(0,slices.Top-bounds.Top);
        slices.Right = Math.Max(0,slices.Right-(w-bounds.Right)); slices.Bottom = Math.Max(0,slices.Bottom-(h-bounds.Bottom));
        ClampSlices(slices,bounds.Size);
        TransformCanvas(description,bounds.Size,Pixels,p => new PointF(p.X-bounds.X,p.Y-bounds.Y),slices);
    }

    public void RotateCanvas(double degrees, bool expand = true)
    {
        if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        degrees %= 360; if (Math.Abs(degrees) < .0001) return;
        int w = _workspace.Width,h = _workspace.Height;
        Size size = ImageCanvasTransforms.RotatedSize(w,h,degrees,expand);
        ImageCanvasTransforms.ValidateSize(size.Width,size.Height);
        ImageNineSlice slices = _session.Document.NineSlice.Clone();
        if (expand && Math.Abs(degrees/90-Math.Round(degrees/90)) < .00001)
            for (int i = 0; i < ((int)Math.Round(degrees/90)%4+4)%4; i++)
            {
                (slices.Left,slices.Top,slices.Right,slices.Bottom) = (slices.Bottom,slices.Left,slices.Top,slices.Right);
                (slices.HorizontalEdges,slices.VerticalEdges) = (slices.VerticalEdges,slices.HorizontalEdges);
            }
        else { slices.Enabled = false; slices.Left = slices.Top = slices.Right = slices.Bottom = 0; }
        TransformCanvas("Rotate canvas",size,p => ImageCanvasTransforms.Rotate(p,w,h,degrees,expand),p => ImageCanvasTransforms.RotatePoint(p,w,h,size,degrees),slices,degrees);
    }

    public void SetNineSlice(ImageNineSlice slices)
    {
        ImageCanvasTransforms.ValidateSlices(slices,_workspace.Width,_workspace.Height);
        var before = _session.Document.NineSlice; var after = slices.Clone();
        EditStructure("Set nine-slice guides",() => _session.Document.NineSlice=after,() => _session.Document.NineSlice=before);
    }

    public void ApplyNineSlice(ImageNineSlice slices, int width, int height)
    {
        int w = _workspace.Width,h = _workspace.Height; Size size = new(width,height);
        var settings = slices.Clone();
        byte[] Pixels(byte[] p) => ImageCanvasTransforms.NineSlice(p,w,h,size,settings);
        PointF Map(PointF p) => new(ImageCanvasTransforms.MapSliceCoordinate(p.X,w,width,settings.Left,settings.Right),ImageCanvasTransforms.MapSliceCoordinate(p.Y,h,height,settings.Top,settings.Bottom));
        TransformCanvas("Resize with nine-slice",size,Pixels,Map,settings);
    }

    private static void ClampSlices(ImageNineSlice slices, Size size)
    {
        slices.Left = Math.Clamp(slices.Left,0,size.Width-1); slices.Right = Math.Clamp(slices.Right,0,size.Width-slices.Left-1);
        slices.Top = Math.Clamp(slices.Top,0,size.Height-1); slices.Bottom = Math.Clamp(slices.Bottom,0,size.Height-slices.Top-1);
    }

    private void TransformCanvas(string description, Size size, Func<byte[],byte[]> pixels, Func<PointF,PointF> map, ImageNineSlice slices, double rotation = 0)
    {
        ImageCanvasTransforms.ValidateSize(size.Width,size.Height); CommitFloatingSelection(); StopPlayback();
        var targets=TargetFrameIndices(allByDefault:true);
        long layerCount=targets.Sum(i=>(long)_workspace.Frames[i].Layers.Count);
        if(layerCount*((long)size.Width*size.Height*4+(long)_workspace.Width*_workspace.Height*8)>536_870_912)
            throw new InvalidOperationException("This transform exceeds the 512 MB edit budget. Choose fewer frames or a smaller output size.");
        if(targets.Length!=_workspace.Frames.Count)
        {
            // Frames share one canvas. A partial range transforms its artwork inside that
            // canvas; other frames and global rig/guide coordinates retain their geometry.
            var affected=targets.SelectMany(i=>_workspace.Frames[i].Layers).Where(l=>!l.Locked).ToArray();
            var before=affected.Select(l=>l.Pixels).ToArray();
            var transformed=before.Select(p=>ImageCanvasTransforms.Remap(pixels(p),size.Width,size.Height,new Size(_workspace.Width,_workspace.Height),q=>q)).ToArray();
            EditStructure(description,()=>{for(int i=0;i<affected.Length;i++) affected[i].Pixels=transformed[i];},
                ()=>{for(int i=0;i<affected.Length;i++) affected[i].Pixels=before[i];},before.Sum(p=>p.LongLength)*2);
            return;
        }
        if ((long)size.Width*size.Height > 4_194_304 && _session.Document.PixelRigs.Any(r => r.Width == _workspace.Width && r.Height == _workspace.Height))
            throw new InvalidOperationException("This image has saved pixel rigs. Choose dimensions within their four-million-pixel limit.");
        var metadata = new CanvasMetadataChange(_session.Document,new Size(_workspace.Width,_workspace.Height),size,map,pixels,rotation);
        metadata.Set(() => _session.Document.NineSlice,v => _session.Document.NineSlice=v,slices);
        var layers = _workspace.Frames.SelectMany(f => f.Layers).ToArray();
        // Prepare before changing either pixels or metadata; a failed allocation leaves the document intact.
        var after = layers.Select(l => pixels(l.Pixels)).ToArray();
        _session.Execute(new WorkspaceCanvasCommand(description,_workspace,() =>
        {
            _workspace.ResizeCanvas(size.Width,size.Height,Point.Empty);
            for (int i = 0; i < layers.Length; i++) layers[i].Pixels=after[i];
            _workspace.Selection.Clear();
        },metadata.Apply,metadata.Undo));
        SynchronizeLists(); RefreshCanvas(); FitCanvas();
    }

    private void PromptRotateCanvas()
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive) return;
        using var dialog = new ImageRotateDialog(_workspace.CompositeCurrentFrame(),_workspace.Width,_workspace.Height);
        var target=AttachFrameTargets(dialog,CanvasFrameScopeNote);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ApplyDialogFrames(target,() => RotateCanvas(dialog.Degrees,dialog.ExpandCanvas));
    }

    private void PromptNineSlice()
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive) return;
        using var dialog = new ImageNineSliceDialog(_workspace.CompositeCurrentFrame(),_workspace.Width,_workspace.Height,_session.Document.NineSlice);
        var target=AttachFrameTargets(dialog,CanvasFrameScopeNote+" Guides are shared sprite metadata; the target applies to Resize pixels.");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ApplyDialogFrames(target,() =>
        {
            if (dialog.ResizePixels) ApplyNineSlice(dialog.Settings,dialog.OutputSize.Width,dialog.OutputSize.Height);
            else SetNineSlice(dialog.Settings);
        });
    }

    private void TryCanvasOperation(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { ThemeMessageBox.Show(this,error.Message,"Canvas operation",MessageBoxButtons.OK,MessageBoxIcon.Information); }
    }

    private const string CanvasFrameScopeNote="All Frames changes the shared canvas. Partial ranges keep its size; transformed pixels outside it are clipped.";
    private void PromptRemoveBlankSpace()
    {
        using var dialog=new DpiAwareForm { Text="Remove blank space",ClientSize=new Size(640,100),StartPosition=FormStartPosition.CenterParent };
        var apply=SectionActionButton("Remove blank space",16,42); apply.DialogResult=DialogResult.OK;
        var cancel=SectionActionButton("Cancel",200,42); cancel.DialogResult=DialogResult.Cancel;
        dialog.Controls.AddRange([SectionLabel("Trim to the artwork bounds of the selected frames.",16,12),apply,cancel]);
        dialog.AcceptButton=apply; dialog.CancelButton=cancel;
        var target=AttachFrameTargets(dialog,CanvasFrameScopeNote);
        if(dialog.ShowDialog(this)==DialogResult.OK) ApplyDialogFrames(target,()=>RemoveBlankSpace());
    }
}

using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    public byte[] ScaleArtworkPreview(double scaleX, double scaleY, double anchorX = .5, double anchorY = .5)
    {
        if (_workspace.CurrentLayer == null) return [];
        return ScalePixels(_workspace.CurrentLayer.Pixels,scaleX,scaleY,anchorX,anchorY,_workspace.Selection);
    }

    private byte[] ScalePixels(byte[] source, double sx, double sy, double ax, double ay, ImageSelectionMask? selection)
    {
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || sx < .01 || sy < .01 || sx > 16 || sy > 16) throw new ArgumentOutOfRangeException(nameof(sx));
        int w = _workspace.Width, h = _workspace.Height;
        Rectangle bounds = selection?.HasSelection == true ? selection.GetBounds() : new Rectangle(0,0,w,h);
        byte[] result = (byte[])source.Clone();
        double px = bounds.Left+bounds.Width*ax, py = bounds.Top+bounds.Height*ay;
        for (int y = bounds.Top; y < bounds.Bottom; y++) for (int x = bounds.Left; x < bounds.Right; x++)
            if (selection?.Contains(x,y) != false) Array.Clear(result,(y*w+x)*4,4);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int ox = (int)Math.Floor(px+(x+.5-px)/sx), oy = (int)Math.Floor(py+(y+.5-py)/sy);
            if (!bounds.Contains(ox,oy) || selection?.Contains(ox,oy) == false) continue;
            var colour = RasterOperations.GetPixel(source,w,h,ox,oy);
            RasterOperations.SetPixel(result,w,h,x,y,colour);
        }
        return result;
    }

    public void ScaleArtwork(double x, double y, double anchorX = .5, double anchorY = .5, bool allFrames = false, bool allLayers = false)
    {
        CommitFloatingSelection();
        var frames = TargetFrameIndices(allFrames).Select(i=>_workspace.Frames[i]).ToArray();
        var layers = frames.SelectMany(f => allLayers ? f.Layers : f.Layers.Where(l=>l==MatchingLayer(f,_workspace.CurrentLayer!))).Where(l => !l.Locked).ToArray();
        if(layers.Sum(l=>l.Pixels.LongLength)*3>536_870_912) throw new InvalidOperationException("This transform exceeds the 512 MB edit budget. Choose fewer frames or layers.");
        var before = layers.Select(l => (byte[])l.Pixels.Clone()).ToArray();
        var after = layers.Select(l => ScalePixels(l.Pixels,x,y,anchorX,anchorY,allFrames || allLayers ? null : _workspace.Selection)).ToArray();
        EditStructure("Scale artwork", () => { for (int i=0;i<layers.Length;i++) Buffer.BlockCopy(after[i],0,layers[i].Pixels,0,after[i].Length); },
            () => { for (int i=0;i<layers.Length;i++) Buffer.BlockCopy(before[i],0,layers[i].Pixels,0,before[i].Length); }, before.Sum(p => p.LongLength)*2);
        _workspace.Selection.Clear(); RefreshCanvas();
    }

    private void PromptScaleArtwork()
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive || _workspace.CurrentLayer == null) return;
        using var dialog = new DpiAwareForm { Text = "Scale artwork", ClientSize = new Size(650,480), StartPosition = FormStartPosition.CenterParent };
        var preview = new CompositeFramePreviewControl { Dock = DockStyle.Fill };
        var fields = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 220, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        var x = Number(1,1600,100); var y = Number(1,1600,100); x.Width = y.Width = 170;
        var uniform = new CheckBox { Text = "Uniform scaling", Checked = true, AutoSize = true };
        var anchor = new ImageThemedComboBox { Width = 180 }; anchor.Items.AddRange(["Top left","Top centre","Top right","Middle left","Centre","Middle right","Bottom left","Bottom centre","Bottom right"]); anchor.SelectedIndex = 4;
        var allLayers = new CheckBox { Text = "All unlocked layers", AutoSize = true };
        var ok = SectionActionButton("Apply"); ok.DialogResult = DialogResult.OK;
        var cancel = SectionActionButton("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        fields.Controls.AddRange([new Label { Text = "X scale (%)",AutoSize = true },x,new Label { Text = "Y scale (%)",AutoSize = true },y,uniform,
            new Label { Text = "Keep this anchor fixed",AutoSize = true },anchor,allLayers,new Label { Text = "Canvas size stays fixed. Pixels outside it are clipped.",Width = 190,Height = 50 },ok,cancel]);
        bool syncing = false;
        void Update(bool fromX)
        {
            if (syncing) return; syncing = true;
            if (uniform.Checked) { if (fromX) y.Value = x.Value; else x.Value = y.Value; }
            syncing = false;
            preview.SetFrame(ScaleArtworkPreview((double)x.Value/100,(double)y.Value/100,anchor.SelectedIndex%3/2d,anchor.SelectedIndex/3/2d),_workspace.Width,_workspace.Height);
        }
        x.ValueChanged += (_,_) => Update(true); y.ValueChanged += (_,_) => Update(false); anchor.SelectedIndexChanged += (_,_) => Update(true);
        uniform.CheckedChanged += (_,_) => Update(true);
        dialog.Controls.Add(preview); dialog.Controls.Add(fields); dialog.AcceptButton = ok; dialog.CancelButton = cancel; Update(true);
        var target=AttachFrameTargets(dialog);
        if (dialog.ShowDialog(this) == DialogResult.OK) ApplyDialogFrames(target,()=>ScaleArtwork((double)x.Value/100,(double)y.Value/100,anchor.SelectedIndex%3/2d,anchor.SelectedIndex/3/2d,allLayers:allLayers.Checked));
    }

    private void PromptStampEffect()
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive || _workspace.CurrentLayer == null) return;
        using var dialog = new DpiAwareForm { Text = "Stamp tile", ClientSize = new Size(620,400), StartPosition = FormStartPosition.CenterParent };
        var preview = new CompositeFramePreviewControl { Dock = DockStyle.Fill };
        var fields = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 200, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10) };
        int tw = Math.Max(1,_session.Document.Usage.Tileset.TileWidth), th = Math.Max(1,_session.Document.Usage.Tileset.TileHeight);
        var tile = Number(0,Math.Max(0,(_workspace.Width/tw)*(_workspace.Height/th)-1),0);
        var x = Number(0,_workspace.Width-1,0); var y = Number(0,_workspace.Height-1,0);
        var ok = SectionActionButton("Apply stamp"); ok.DialogResult = DialogResult.OK; var cancel = SectionActionButton("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        fields.Controls.AddRange([new Label {Text="Source tile index",AutoSize=true},tile,new Label {Text="Destination X",AutoSize=true},x,new Label {Text="Destination Y",AutoSize=true},y,ok,cancel]);
        byte[] source = (byte[])_workspace.CompositeCurrentFrame().Clone(); byte[] result = [];
        void Update() { result = (byte[])_workspace.CurrentLayer.Pixels.Clone(); ImageToolOperations.StampTile(result,_workspace.Width,_workspace.Height,source,_workspace.Width,_workspace.Height,tw,th,(int)tile.Value,new Point((int)x.Value,(int)y.Value),_workspace.Selection); preview.SetFrame(result,_workspace.Width,_workspace.Height); }
        tile.ValueChanged += (_,_) => Update(); x.ValueChanged += (_,_) => Update(); y.ValueChanged += (_,_) => Update();
        dialog.Controls.Add(preview); dialog.Controls.Add(fields); dialog.AcceptButton=ok; dialog.CancelButton=cancel; Update();
        var target=AttachFrameTargets(dialog,"The source tile is stamped onto each target frame's own artwork.");
        if (dialog.ShowDialog(this)==DialogResult.OK) ApplyDialogFrames(target,()=>ApplyOperation("Stamp tile",pixels =>
        { ImageToolOperations.StampTile(pixels,_workspace.Width,_workspace.Height,source,_workspace.Width,_workspace.Height,tw,th,(int)tile.Value,new Point((int)x.Value,(int)y.Value),_workspace.Selection); return pixels; }));
    }
}

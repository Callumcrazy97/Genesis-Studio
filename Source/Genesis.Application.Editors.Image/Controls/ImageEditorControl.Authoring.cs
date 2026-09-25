using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    // Structural commands retain the same cel objects: earlier pixel commands still target them.
    private void EditStructure(string name, Action apply, Action undo, long bytes = 0)
    {
        CommitFloatingSelection();
        StopPlayback();
        _session.Execute(new StructuralImageCommand(name,
            _ => { apply(); _workspace.Touch(); SyncDocument(); },
            _ => { undo(); _workspace.Touch(); SyncDocument(); }, bytes));
        SynchronizeLists();
        RefreshCanvas();
    }

    public void SetFrameDuration(int milliseconds)
    {
        ImageFrameBuffer? frame = _workspace.CurrentFrame;
        if (frame == null) return;
        int value = Math.Clamp(milliseconds, 1, 60_000);
        int old = frame.DurationMilliseconds;
        if (old == value) return;
        EditStructure("Change frame duration", () => frame.DurationMilliseconds = value,
            () => frame.DurationMilliseconds = old);
    }

    public void RenameLayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        ChangeLayerAcrossFrames("Rename layer", layer => layer.Name = name.Trim());
    }

    public void SetLayerVisible(bool visible) => ChangeLayerAcrossFrames("Layer visibility", layer => layer.Visible = visible);
    public void SetLayerLocked(bool locked) => ChangeLayerAcrossFrames("Layer lock", layer => layer.Locked = locked);

    private void ChangeLayerAcrossFrames(string name, Action<ImageLayerBuffer> change)
    {
        int index = _workspace.SelectedLayerIndex;
        var layers = _workspace.Frames.Where(frame => index >= 0 && index < frame.Layers.Count)
            .Select(frame => frame.Layers[index]).ToArray();
        var before = layers.Select(layer => (layer.Name, layer.Visible, layer.Locked, layer.Opacity, layer.BlendMode, layer.Channel)).ToArray();
        EditStructure(name, () => { foreach (var layer in layers) change(layer); }, () =>
        {
            for (int i = 0; i < layers.Length; i++)
            {
                layers[i].Name = before[i].Name;
                layers[i].Visible = before[i].Visible;
                layers[i].Locked = before[i].Locked;
                layers[i].Opacity = before[i].Opacity;
                layers[i].BlendMode = before[i].BlendMode;
                layers[i].Channel = before[i].Channel;
            }
        });
    }

    public void DuplicateLayer()
    {
        int index = _workspace.SelectedLayerIndex;
        Guid id = Guid.NewGuid();
        var additions = _workspace.Frames.Where(frame => index >= 0 && index < frame.Layers.Count).Select(frame =>
        {
            ImageLayerBuffer source = frame.Layers[index];
            return (Frame: frame, Layer: new ImageLayerBuffer
            {
                Id = id,
                Name = source.Name + " Copy",
                Visible = source.Visible,
                Opacity = source.Opacity,
                BlendMode = source.BlendMode,
                Channel = source.Channel,
                Pixels = (byte[])source.Pixels.Clone(),
            });
        }).ToArray();
        if (additions.Length == 0) return;
        EditStructure("Duplicate layer", () =>
        {
            foreach (var item in additions) item.Frame.Layers.Insert(index + 1, item.Layer);
            _workspace.SelectedLayerIndex = index + 1;
        }, () =>
        {
            foreach (var item in additions) item.Frame.Layers.Remove(item.Layer);
            _workspace.SelectedLayerIndex = index;
        }, additions.Sum(item => item.Layer.Pixels.LongLength));
    }

    public void ImportFrames(string path)
    {
        var decoded = Genesis.Shared.Assets.ImageAssetDecoder.DecodeFrames(path);
        if (decoded.Width != _workspace.Width || decoded.Height != _workspace.Height)
            throw new InvalidOperationException("Imported frames must match the canvas dimensions. Resize the canvas or import a matching image.");
        int oldIndex = _workspace.SelectedFrameIndex;
        ImageLayerBuffer[] structure = _workspace.Frames[0].Layers.ToArray();
        var frames = decoded.Frames.Select((source, index) =>
        {
            ImageFrameBuffer frame = new() { Name = $"Imported {index + 1}", DurationMilliseconds = Math.Max(1, source.DurationMilliseconds) };
            foreach (var layer in structure)
            {
                var cel = layer.Clone();
                cel.Pixels = new byte[_workspace.Width * _workspace.Height * 4];
                frame.Layers.Add(cel);
            }
            int colour = Array.FindIndex(structure, layer => layer.Channel == ImageMaterialChannel.Color);
            if (colour < 0) throw new InvalidOperationException("Add a colour layer before importing frames.");
            frame.Layers[colour].Pixels = (byte[])source.Rgba.Clone();
            return frame;
        }).ToArray();
        EditStructure("Import frames", () =>
        {
            _workspace.Frames.AddRange(frames);
            _workspace.SelectedFrameIndex = _workspace.Frames.Count - 1;
        }, () =>
        {
            foreach (var frame in frames) _workspace.Frames.Remove(frame);
            _workspace.SelectedFrameIndex = oldIndex;
        }, frames.Sum(frame => frame.Layers.Sum(layer => layer.Pixels.LongLength)));
    }

    public void ResizeCanvas(int width, int height, bool scalePixels)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw new ArgumentOutOfRangeException(nameof(width), "Canvas dimensions must be between 1 and 8192 pixels.");
        if (width == _workspace.Width && height == _workspace.Height) return;
        int oldWidth = _workspace.Width,oldHeight = _workspace.Height;
        float sx = scalePixels ? width/(float)oldWidth : 1, sy = scalePixels ? height/(float)oldHeight : 1;
        var slices = _session.Document.NineSlice.Clone();
        slices.Left = (int)Math.Round(slices.Left*sx); slices.Right = (int)Math.Round(slices.Right*sx);
        slices.Top = (int)Math.Round(slices.Top*sy); slices.Bottom = (int)Math.Round(slices.Bottom*sy);
        Size size = new(width,height); ClampSlices(slices,size);
        TransformCanvas(scalePixels ? "Resize sprite" : "Resize canvas",size,
            p => ImageCanvasTransforms.Remap(p,oldWidth,oldHeight,size,q => new PointF(q.X/sx,q.Y/sy)),
            p => new PointF(p.X*sx,p.Y*sy),slices);
    }

    private void PromptResize(bool scalePixels)
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive) return;
        using DpiAwareForm dialog = new()
        {
            Text = scalePixels ? "Resize sprite — nearest neighbour" : "Canvas size — top-left anchor",
            ClientSize = new Size(340, 150),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var width = Number(1, 8192, _workspace.Width);
        var height = Number(1, 8192, _workspace.Height);
        width.SetBounds(120, 16, 180, 28); height.SetBounds(120, 52, 180, 28);
        var ok = SectionActionButton("Apply", 214, 102); ok.DialogResult = DialogResult.OK;
        var cancel = SectionActionButton("Cancel", 122, 102); cancel.DialogResult = DialogResult.Cancel;
        dialog.Controls.AddRange([SectionLabel("Width", 16, 20), SectionLabel("Height", 16, 56), width, height, ok, cancel]);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        var target=AttachFrameTargets(dialog,CanvasFrameScopeNote);
        if (dialog.ShowDialog(this) == DialogResult.OK) ApplyDialogFrames(target,() => ResizeCanvas((int)width.Value, (int)height.Value, scalePixels));
    }

    private void PromptRenameLayer() { _layerName.Focus(); _layerName.SelectAll(); }

    public void MirrorLayer(bool horizontal)
    {
        ApplyOperation(horizontal ? "Mirror layer horizontally" : "Mirror layer vertically", source =>
        {
            int w = _workspace.Width, h = _workspace.Height;
            Rectangle bounds = _workspace.Selection.HasSelection ? _workspace.Selection.GetBounds() : new Rectangle(0, 0, w, h);
            byte[] result = (byte[])source.Clone();
            for (int y = bounds.Top; y < bounds.Bottom; y++)
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    int sx = horizontal ? bounds.Right - 1 - (x - bounds.Left) : x;
                    int sy = horizontal ? y : bounds.Bottom - 1 - (y - bounds.Top);
                    Buffer.BlockCopy(source, (sy * w + sx) * 4, result, (y * w + x) * 4, 4);
                }
            return result;
        });
    }

    public void RotateCanvasClockwise() => RotateCanvas(90);

    public bool MergeLayerDown()
    {
        int index = _workspace.SelectedLayerIndex;
        if (index <= 0 || _workspace.Frames.Any(frame => index >= frame.Layers.Count)) return false;
        var pairs = _workspace.Frames.Select(frame => (Frame: frame, Top: frame.Layers[index], Bottom: frame.Layers[index - 1])).ToArray();
        // Non-normal blend modes depend on the layers below this pair, so a two-layer merge
        // cannot in general preserve them. Keep the operation explicit and lossless.
        if (pairs.Any(pair => pair.Top.Locked || pair.Bottom.Locked || pair.Top.Channel != pair.Bottom.Channel
            || pair.Top.BlendMode != ImageBlendMode.Normal || pair.Bottom.BlendMode != ImageBlendMode.Normal)) return false;
        var old = pairs.Select(pair => pair.Bottom.Clone()).ToArray();
        var composites = pairs.Select(pair =>
        {
            var scratch = new ImageWorkspace(_workspace.Width, _workspace.Height);
            var frame = new ImageFrameBuffer(); frame.Layers.Add(pair.Bottom.Clone()); frame.Layers.Add(pair.Top.Clone()); scratch.Frames.Add(frame);
            return scratch.CompositeCurrentFrame(pair.Top.Channel);
        }).ToArray();
        EditStructure("Merge layer down", () =>
        {
            for (int i = 0; i < pairs.Length; i++)
            {
                pairs[i].Bottom.Pixels = (byte[])composites[i].Clone(); pairs[i].Bottom.Opacity = 1; pairs[i].Bottom.Visible = true;
                pairs[i].Frame.Layers.Remove(pairs[i].Top);
            }
            _workspace.SelectedLayerIndex = index - 1;
        }, () =>
        {
            for (int i = 0; i < pairs.Length; i++)
            {
                pairs[i].Bottom.Pixels = (byte[])old[i].Pixels.Clone(); pairs[i].Bottom.Opacity = old[i].Opacity; pairs[i].Bottom.Visible = old[i].Visible;
                pairs[i].Frame.Layers.Insert(index, pairs[i].Top);
            }
            _workspace.SelectedLayerIndex = index;
        }, old.Sum(layer => layer.Pixels.LongLength) + composites.Sum(pixels => pixels.LongLength));
        return true;
    }
}

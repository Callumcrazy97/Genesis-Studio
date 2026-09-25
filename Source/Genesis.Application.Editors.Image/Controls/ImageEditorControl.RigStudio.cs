using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    public void SavePixelRig(ImagePixelRig rig)
    {
        var rigs = _session.Document.PixelRigs;
        var previous = rigs.FirstOrDefault(r => r.Id == rig.Id);
        var saved = PixelRigRasterizer.Copy(rig);
        int index = previous == null ? rigs.Count : rigs.IndexOf(previous);
        EditStructure("Save rig " + saved.Name, () => { if (previous != null) rigs[index] = saved; else rigs.Insert(index,saved); },
            () => { if (previous != null) rigs[index] = previous; else rigs.Remove(saved); }, saved.BindPixels.LongLength);
    }

    public void DeletePixelRig(string id)
    {
        var rigs = _session.Document.PixelRigs; var rig = rigs.FirstOrDefault(r => r.Id == id);
        if (rig == null) return; int index = rigs.IndexOf(rig);
        EditStructure("Delete rig " + rig.Name, () => rigs.Remove(rig), () => rigs.Insert(index,rig), rig.BindPixels.LongLength);
    }

    private void OpenRigStudio(int tab)
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive || _workspace.CurrentLayer == null || _workspace.CurrentFrame == null) return;
        CommitFloatingSelection(); StopPlayback();
        ImageFrameTargetControl? target=null;
        using var dialog = new PixelRigStudioDialog(_session.Document.PixelRigs, _workspace.CurrentLayer.Pixels,
            _workspace.Width,_workspace.Height,_workspace.CurrentLayer.Id.ToString("N"),_workspace.CurrentFrame.Id.ToString("N"),
            SavePixelRig,DeletePixelRig,pixels=>InFrameScope(target!.FrameIndices,()=>ApplyPixelRigPose(pixels)),GeneratePoseAnimationAsync,tab);
        target=AttachFrameTargets(dialog,"Apply pose uses these existing frames. Generate frames uses the pose assignments on the Animate page.");
        dialog.ShowDialog(this);
    }

    public void ApplyPixelRigPose(byte[] pixels)
    {
        if (_workspace.CurrentLayer == null || _workspace.CurrentLayer.Locked)
            throw new InvalidOperationException("Select an unlocked layer before applying a rig pose.");
        if (pixels.Length != _workspace.CurrentLayer.Pixels.Length)
            throw new InvalidOperationException("The rig canvas size differs from this image. Bind a new rig for this canvas.");
        ApplyOperation("Apply rig pose", _ => (byte[])pixels.Clone());
    }

    public async Task GeneratePoseAnimationAsync(ImagePixelRig rig, ImagePoseAnimation animation, CancellationToken cancellationToken = default)
    {
        var snapshot = PixelRigRasterizer.Copy(rig); var settings = PixelRigRasterizer.Copy(animation);
        if (snapshot.Width != _workspace.Width || snapshot.Height != _workspace.Height || settings.Keys.Count == 0)
            throw new ArgumentException("The rig needs pose assignments and must match the current canvas size.");
        int count = settings.Keys.Max(key => key.Frame);
        var source = _workspace.Frames.FirstOrDefault(f => f.Id.ToString("N") == snapshot.SourceFrameId) ?? _workspace.CurrentFrame!;
        int layerIndex = source.Layers.FindIndex(l => l.Id.ToString("N") == snapshot.LayerId);
        if (layerIndex < 0) throw new ArgumentException("The rig's source layer is missing. Select the original layer and use Rig pixels to bind it again.");
        if (count is < 1 or > 600) throw new ArgumentException("Animation frame numbers must be between 1 and 600.");
        long bytesPerFrame = (long)_workspace.Width*_workspace.Height*4*source.Layers.Count;
        const long animationBudget = 536_870_912;
        if (bytesPerFrame*count > animationBudget)
            throw new ArgumentException($"Generating {count} frames at {_workspace.Width} × {_workspace.Height} with {source.Layers.Count} layers needs {bytesPerFrame*count/1048576} MB. " +
                $"The animation limit is 512 MB ({animationBudget/Math.Max(1,bytesPerFrame)} frames at this size). Reduce the last frame number or resize the image, then generate again.");
        var sourceSnapshot = source.Clone();
        var generated = await Task.Run(() =>
        {
            var renderer = new PixelRigRasterizer(snapshot,cancellationToken); List<ImageFrameBuffer> frames = [];
            for (int frame = 1; frame <= count; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = sourceSnapshot.Clone(); buffer.Name = settings.Name + " " + frame;
                buffer.DurationMilliseconds = Math.Max(1,(int)Math.Round(1000d/Math.Clamp(settings.FramesPerSecond,1,120)));
                buffer.Layers[layerIndex].Pixels = renderer.Render(PixelRigRasterizer.Interpolate(snapshot,settings,frame),cancellationToken,PixelRigRasterizer.InterpolateJoints(snapshot,settings,frame));
                frames.Add(buffer);
            }
            return frames;
        },cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var oldFrames = _workspace.Frames.ToArray(); var oldTags = _session.Document.Tags.ToArray();
        var oldRig = _session.Document.PixelRigs.FirstOrDefault(r => r.Id == snapshot.Id);
        var updatedRig = PixelRigRasterizer.Copy(snapshot);
        var updatedAnimation = settings;
        updatedRig.Animations.RemoveAll(a => a.Id == settings.Id);
        updatedRig.Animations.Add(updatedAnimation);
        var oldIds = (oldRig?.Animations.FirstOrDefault(a => a.Id == settings.Id)?.GeneratedFrameIds ?? settings.GeneratedFrameIds).ToHashSet(StringComparer.Ordinal);
        string tagId = "pose-" + settings.Id;
        bool UsesGeneratedFrames(ImageAnimationTag candidate)
        {
            int first = Array.FindIndex(oldFrames,f => f.Id.ToString("N") == candidate.StartFrameId);
            int last = Array.FindIndex(oldFrames,f => f.Id.ToString("N") == candidate.EndFrameId);
            return first >= 0 && last >= 0 && oldFrames.Skip(Math.Min(first,last)).Take(Math.Abs(last-first)+1).Any(f => oldIds.Contains(f.Id.ToString("N")));
        }
        if (oldTags.Any(tag => tag.Id != tagId && UsesGeneratedFrames(tag)))
            throw new InvalidOperationException("Generated frames are used by another animation tag. Remove that tag before regenerating this animation.");
        var nextFrames = oldFrames.Where(f => !oldIds.Contains(f.Id.ToString("N"))).ToList(); int start = nextFrames.Count; nextFrames.AddRange(generated);
        updatedAnimation.GeneratedFrameIds = generated.Select(f => f.Id.ToString("N")).ToList();
        var tag = new ImageAnimationTag { Id = tagId, Name = settings.Name, StartFrameId = generated[0].Id.ToString("N"), EndFrameId = generated[^1].Id.ToString("N"), Loop = settings.Loop };
        var nextTags = oldTags.Where(t => t.Id != tagId).Append(tag).ToArray();
        int previousSelection = _workspace.SelectedFrameIndex;
        EditStructure("Generate animation " + settings.Name, () =>
        {
            _workspace.Frames.Clear(); _workspace.Frames.AddRange(nextFrames); _workspace.SelectedFrameIndex = start;
            _session.Document.Tags.Clear(); _session.Document.Tags.AddRange(nextTags);
            _session.Document.PixelRigs.RemoveAll(r => r.Id == updatedRig.Id); _session.Document.PixelRigs.Add(updatedRig);
        }, () =>
        {
            _workspace.Frames.Clear(); _workspace.Frames.AddRange(oldFrames); _workspace.SelectedFrameIndex = previousSelection;
            _session.Document.Tags.Clear(); _session.Document.Tags.AddRange(oldTags);
            _session.Document.PixelRigs.RemoveAll(r => r.Id == updatedRig.Id); if (oldRig != null) _session.Document.PixelRigs.Add(oldRig);
        }, (long)generated.Count*_workspace.Width*_workspace.Height*4*source.Layers.Count);
        animation.GeneratedFrameIds = updatedAnimation.GeneratedFrameIds.ToList();
        _clip.SelectedIndex = _session.Document.Tags.FindIndex(t => t.Id == tagId)+1;
    }
}

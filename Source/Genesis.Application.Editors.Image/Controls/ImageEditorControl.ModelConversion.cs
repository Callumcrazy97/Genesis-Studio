using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    /// <summary>
    /// Shows the Convert to 3D Model dialog and returns the created Model's path, or null when
    /// cancelled. Studio supplies it from the editor suite, which owns the shared 3D viewport.
    /// </summary>
    public static Func<IWin32Window?, PixelModelSource, string?>? ModelConversionDialog { get; set; }

    /// <summary>Every frame composited as the canvas shows it, with durations and the image origin.</summary>
    public PixelModelSource CreateModelConversionSource()
    {
        CommitFloatingSelection();
        PixelModelFrame[] frames = _workspace.Frames
            .Select((frame, index) => new PixelModelFrame(frame.Name, _workspace.CompositeCurrentFrameFor(index).Pixels, frame.DurationMilliseconds))
            .ToArray();
        ImageOrigin origin = _session.Document.Origin;
        Vector2 normalized = origin.Space == ImageCoordinateSpace.Pixels
            ? new Vector2((float)(origin.X / Math.Max(1, _workspace.Width)), (float)(origin.Y / Math.Max(1, _workspace.Height)))
            : new Vector2((float)origin.X, (float)origin.Y);
        return new PixelModelSource
        {
            Width = _workspace.Width,
            Height = _workspace.Height,
            Frames = frames,
            CurrentFrameIndex = Math.Clamp(_workspace.SelectedFrameIndex, 0, Math.Max(0, frames.Length - 1)),
            ImagePath = _session.DocumentPath,
            Origin = new Vector2(float.IsFinite(normalized.X) ? normalized.X : .5f, float.IsFinite(normalized.Y) ? normalized.Y : .5f),
            SuggestedName = (_session.DocumentPath is { } path ? ResourceDisplayName.Format(path) : "Pixel") + " 3D",
            Clips = TagClips(),
        };
    }

    /// <summary>Each animation tag as a run of frame indices in play order (reverse and ping-pong included).</summary>
    private PixelModelClip[] TagClips()
    {
        List<PixelModelClip> clips = [];
        foreach (ImageAnimationTag tag in _session.Document.Tags)
        {
            int start = _workspace.Frames.FindIndex(frame => frame.Id.ToString("N") == tag.StartFrameId);
            int end = _workspace.Frames.FindIndex(frame => frame.Id.ToString("N") == tag.EndFrameId);
            if (start < 0 || end < 0 || string.IsNullOrWhiteSpace(tag.Name)) continue;
            if (end < start) (start, end) = (end, start);
            int[] forward = Enumerable.Range(start, end - start + 1).ToArray();
            int[] order = tag.Direction switch
            {
                ImagePlaybackDirection.Reverse => Enumerable.Reverse(forward).ToArray(),
                ImagePlaybackDirection.PingPong => forward.Concat(Enumerable.Reverse(forward).Skip(1).SkipLast(1)).ToArray(),
                _ => forward,
            };
            clips.Add(new PixelModelClip(tag.Name.Trim(), order, tag.Loop));
        }
        return clips.ToArray();
    }

    /// <summary>Opens the conversion dialog, then offers to open the new Model.</summary>
    public void ConvertToModel()
    {
        StopPlayback();
        const string title = "Convert to 3D Model";
        if (_session.DocumentPath is null)
        {
            ThemeMessageBox.Show(this, "Save this Image in a Genesis project first. The Model is created beside it.", title);
            return;
        }
        if (ModelConversionDialog is not { } show)
        {
            ThemeMessageBox.Show(this, "3D conversion needs the Studio's 3D preview, which is not available here.", title);
            return;
        }

        string? created;
        try { created = show(FindForm(), CreateModelConversionSource()); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            ThemeMessageBox.Show(this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (created is null) return;
        if (ThemeMessageBox.Show(this, "Created the Model '" + ResourceDisplayName.Format(created) + "'. Open it in the Model Editor now?",
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            OpenLinkedResourceRequested?.Invoke(this, created);
    }
}

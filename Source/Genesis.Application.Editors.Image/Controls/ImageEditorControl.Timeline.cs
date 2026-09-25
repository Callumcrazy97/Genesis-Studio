using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private readonly List<int> _visibleTimelineFrames = [];
    private int _rangeStart = -1, _rangeEnd = -1;
    private void RefreshTimelineFrames()
    {
        bool syncing = _syncing; _syncing = true;
        _visibleTimelineFrames.Clear();
        _visibleTimelineFrames.AddRange(ImageAnimationPlayback.GetPlayableFrameIndices(_session.Document,_clip.SelectedIndex,_workspace.Frames.Count));
        _timeline.Items.Clear();
        foreach (int i in _visibleTimelineFrames) _timeline.Items.Add($"{i+1} · {_workspace.Frames[i].Name}");
        _timeline.SelectedIndex = _visibleTimelineFrames.IndexOf(_workspace.SelectedFrameIndex);
        _syncing = syncing;
    }
    public IReadOnlyList<int> VisibleTimelineFrames => _visibleTimelineFrames;
    public void SelectTimelineRange(int first, int last)
    {
        _rangeStart = Math.Clamp(Math.Min(first,last),0,_workspace.Frames.Count-1);
        _rangeEnd = Math.Clamp(Math.Max(first,last),0,_workspace.Frames.Count-1); _timeline.Invalidate();
    }
}

using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>Shared playback toggle; editable tags store the value through session history.</summary>
public sealed class ImagePlaybackLoopButton : CheckBox
{
    private readonly ImageDocumentSession _session;
    private readonly Func<int> _clipIndex;
    private readonly bool _allowAllFrames;
    private readonly Dictionary<string,bool> _previewOverrides = [];
    private bool _refreshing, _allFramesLoop = true;

    public ImagePlaybackLoopButton(ImageDocumentSession session, Func<int> clipIndex, bool allowAllFrames)
    {
        _session = session; _clipIndex = clipIndex; _allowAllFrames = allowAllFrames;
        Name = "ImagePlaybackLoop"; Text = "Loop"; AccessibleName = "Loop animation playback";
        Appearance = Appearance.Button; FlatStyle = FlatStyle.Flat; TextAlign = ContentAlignment.MiddleCenter;
        Size = new Size(58,28); Margin = new Padding(0,0,8,0);
        CheckedChanged += (_,_) => ChangeLoop(); _session.Changed += SessionChanged;
        RefreshState();
    }

    private ImageAnimationTag? TagForClip => _clipIndex() > 0 && _clipIndex() <= _session.Document.Tags.Count
        ? _session.Document.Tags[_clipIndex()-1] : null;
    private void SessionChanged(object? sender, EventArgs args) => RefreshState();
    public void RefreshState()
    {
        var tag = TagForClip;
        _refreshing = true;
        Enabled = tag != null || _allowAllFrames;
        Checked = tag == null ? _allowAllFrames && _allFramesLoop : _previewOverrides.GetValueOrDefault(tag.Id,tag.Loop);
        BackColor = Checked ? ImageEditorChrome.Hover : ImageEditorChrome.Raised;
        ForeColor = ImageEditorChrome.Text; FlatAppearance.BorderColor = Checked ? ImageEditorChrome.Accent : ImageEditorChrome.Border;
        _refreshing = false;
    }
    private void ChangeLoop()
    {
        if (_refreshing) return;
        var tag = TagForClip; bool value = Checked;
        if (tag == null) _allFramesLoop = value;
        else if (!_session.CanEdit) _previewOverrides[tag.Id] = value;
        else if (tag.Loop != value)
        {
            bool before = tag.Loop;
            _session.Execute(new StructuralImageCommand("Change animation looping",_ => tag.Loop = value,_ => tag.Loop = before));
        }
        RefreshState();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _session.Changed -= SessionChanged;
        base.Dispose(disposing);
    }
}

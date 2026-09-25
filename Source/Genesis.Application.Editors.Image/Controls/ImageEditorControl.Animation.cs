using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private ImagePlaybackLoopButton _playbackLoop = null!;
    public void SetAnimationTag(string name, int start, int end, ImagePlaybackDirection direction, bool loop, int replaceIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(name) || start < 0 || end < start || end >= _workspace.Frames.Count)
            throw new ArgumentException("A tag needs a name and a valid ordered frame range.");
        var tags = _session.Document.Tags;
        ImageAnimationTag? old = replaceIndex >= 0 && replaceIndex < tags.Count ? tags[replaceIndex] : null;
        var tag = new ImageAnimationTag
        {
            Id = old?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            StartFrameId = _workspace.Frames[start].Id.ToString("N"),
            EndFrameId = _workspace.Frames[end].Id.ToString("N"),
            Direction = direction,
            Loop = loop
        };
        EditStructure(old == null ? "Create animation tag" : "Edit animation tag",
            () => { if (old == null) tags.Add(tag); else tags[replaceIndex] = tag; },
            () => { if (old == null) tags.Remove(tag); else tags[replaceIndex] = old; });
    }

    private void PromptAnimationTag(bool edit)
    {
        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive) return;
        int index = edit ? _clip.SelectedIndex - 1 : -1;
        if (edit && (index < 0 || index >= _session.Document.Tags.Count)) return;
        var tag = edit ? _session.Document.Tags[index] : null;
        int start = tag == null ? Math.Max(0,Math.Min(_rangeStart,_rangeEnd)) : _workspace.Frames.FindIndex(frame => frame.Id.ToString("N") == tag.StartFrameId);
        int end = tag == null ? (_rangeEnd >= 0 ? Math.Max(_rangeStart,_rangeEnd) : _workspace.Frames.Count - 1) : _workspace.Frames.FindIndex(frame => frame.Id.ToString("N") == tag.EndFrameId);
        using DpiAwareForm dialog = new()
        {
            Text = edit ? "Edit animation tag" : "New animation tag",
            ClientSize = new Size(365, 245),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var name = new TextBox { Text = tag?.Name ?? "Animation", Location = new Point(130, 15), Width = 214 };
        var first = Number(1, _workspace.Frames.Count, Math.Max(0, start) + 1); first.SetBounds(130, 52, 90, 26);
        var last = Number(1, _workspace.Frames.Count, Math.Max(0, end) + 1); last.SetBounds(130, 88, 90, 26);
        var direction = new ImageThemedComboBox { Location = new Point(130, 124), Width = 214 };
        direction.Items.AddRange(Enum.GetNames<ImagePlaybackDirection>()); direction.SelectedIndex = (int)(tag?.Direction ?? ImagePlaybackDirection.Forward);
        var loop = new CheckBox { Text = "Loop", Checked = tag?.Loop ?? true, AutoSize = true, Location = new Point(130, 161) };
        var ok = SectionActionButton("Save tag", 248, 200); ok.DialogResult = DialogResult.OK;
        var cancel = SectionActionButton("Cancel", 145, 200); cancel.DialogResult = DialogResult.Cancel;
        first.ValueChanged += (_, _) => { if (last.Value < first.Value) last.Value = first.Value; };
        last.ValueChanged += (_, _) => { if (first.Value > last.Value) first.Value = last.Value; };
        name.TextChanged += (_, _) => ok.Enabled = !string.IsNullOrWhiteSpace(name.Text);
        dialog.Controls.AddRange([SectionLabel("Name",15,19),SectionLabel("First frame",15,56),SectionLabel("Last frame",15,92),SectionLabel("Playback",15,128),
            name,first,last,direction,loop,ok,cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        // One range UI for tags as well as pixel edits; keep old fields out of the workflow.
        first.Visible=last.Visible=false;
        foreach(var label in dialog.Controls.OfType<Label>().Where(l=>l.Text is "First frame" or "Last frame")) label.Visible=false;
        direction.Top=52; loop.Top=89; ok.Top=cancel.Top=128;
        dialog.Controls.OfType<Label>().Single(l=>l.Text=="Playback").Top=56;
        dialog.ClientSize=new Size(365,174);
        var target=AttachFrameTargets(dialog,"The selected contiguous range becomes the animation tag.");
        target.SetSelection(new ImageFrameSelection(ImageFrameScope.Range,Math.Max(0,start)+1,Math.Max(Math.Max(0,start),end)+1));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetAnimationTag(name.Text,target.FrameIndices.First(),target.FrameIndices.Last(),(ImagePlaybackDirection)direction.SelectedIndex,loop.Checked,index);
    }

    private void DeleteAnimationTag()
    {
        int index = _clip.SelectedIndex - 1;
        if (index < 0 || index >= _session.Document.Tags.Count) return;
        var tags = _session.Document.Tags; var tag = tags[index];
        EditStructure("Delete animation tag", () => tags.Remove(tag), () => tags.Insert(index, tag));
    }

    /// <summary>Export a grid sheet and JSON timing/range metadata without changing the document.</summary>
    public void ExportSpriteSheet(string path, int columns)
    {
        if (_workspace.Frames.Count == 0) return;
        columns = Math.Clamp(columns, 1, _workspace.Frames.Count);
        int rows = (_workspace.Frames.Count + columns - 1) / columns;
        int width = checked(columns * _workspace.Width), height = checked(rows * _workspace.Height);
        if ((long)width * height > 67_108_864) throw new InvalidOperationException("The sheet exceeds 64 million pixels. Export a PNG sequence instead.");
        byte[] sheet = new byte[checked(width * height * 4)];
        for (int i = 0; i < _workspace.Frames.Count; i++)
            BlitFrame(sheet, width, height, _workspace.CompositeCurrentFrameFor(i).Pixels, _workspace.Width, _workspace.Height,
                i % columns * _workspace.Width, i / columns * _workspace.Height);
        ImageWorkspaceStorage.WritePng(path, width, height, sheet);
        var data = new
        {
            image = Path.GetFileName(path),
            width,
            height,
            frames = _workspace.Frames.Select((frame, i) => new
            {
                id = frame.Id.ToString("N"),
                name = frame.Name,
                x = i % columns * _workspace.Width,
                y = i / columns * _workspace.Height,
                width = _workspace.Width,
                height = _workspace.Height,
                duration = frame.DurationMilliseconds
            }),
            tags = _session.Document.Tags.Select(tag => new
            {
                name = tag.Name,
                from = _workspace.Frames.FindIndex(frame => frame.Id.ToString("N") == tag.StartFrameId),
                to = _workspace.Frames.FindIndex(frame => frame.Id.ToString("N") == tag.EndFrameId),
                direction = tag.Direction.ToString(),
                loop = tag.Loop
            }),
        };
        File.WriteAllText(Path.ChangeExtension(path, "json"), System.Text.Json.JsonSerializer.Serialize(data,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

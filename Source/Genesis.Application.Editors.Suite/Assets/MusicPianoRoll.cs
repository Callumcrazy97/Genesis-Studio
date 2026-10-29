using System.Drawing.Drawing2D;
using Genesis.Application.Core.Audio.Music;
using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// The Music mode's piano roll: a keyboard down the left, a ruler of bars along the top (the loop
/// range and play-head on it), the selected track's notes on a key grid, the other tracks' notes faint
/// behind them, and a velocity lane underneath. Edits go through <see cref="MusicEditorControl"/>, so
/// each drag is one undo step.
/// </summary>
internal sealed class MusicPianoRoll : Control
{
    private enum DragMode { None, Move, Resize, Velocity, Marquee, Ruler, Lane }

    private readonly MusicEditorControl _editor;
    private readonly HScrollBar _horizontal = new() { Dock = DockStyle.Bottom, Name = "MusicRollHorizontal" };
    private readonly VScrollBar _vertical = new() { Dock = DockStyle.Right, Name = "MusicRollVertical" };
    private float _pixelsPerBeat = 64;
    private int _scrollX, _scrollY;

    private DragMode _drag;
    private Point _down;
    private bool _moved;
    private long _downTick;
    private int _downPitch;
    private MusicNote? _anchor;
    private readonly Dictionary<MusicNote, (long Start, long Length, int Pitch, int Velocity)> _originals = [];
    private string _before = string.Empty, _gestureLabel = string.Empty;
    private Rectangle _marquee;
    private long _rulerStart, _rulerEnd;

    public MusicPianoRoll(MusicEditorControl editor)
    {
        _editor = editor;
        DoubleBuffered = true;
        TabStop = true;
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Controls.Add(_horizontal);
        Controls.Add(_vertical);
        _horizontal.Scroll += (_, _) => { _scrollX = _horizontal.Value; Invalidate(); };
        _vertical.Scroll += (_, _) => { _scrollY = _vertical.Value; Invalidate(); };
        // The notes are brought into view once the roll has its real size (it has none while the editor is made).
        Resize += (_, _) => { if (!_placed && GridBounds.Height > 40) ScrollToNotes(); else RefreshScroll(); };
    }

    private bool _placed;

    private MusicDocument Document => _editor.Document;

    private float UiScale => Math.Max(1f, DeviceDpi / 96f);
    private int KeyboardWidth => (int)(52 * UiScale);
    private int RulerHeight => (int)(26 * UiScale);
    private int LaneHeight => (int)(72 * UiScale);
    private int RowHeight => Math.Max(6, (int)(12 * UiScale));

    private Rectangle GridBounds => new(KeyboardWidth, RulerHeight,
        Math.Max(1, ClientSize.Width - KeyboardWidth - _vertical.Width),
        Math.Max(1, ClientSize.Height - RulerHeight - LaneHeight - _horizontal.Height));

    private Rectangle LaneBounds { get { Rectangle grid = GridBounds; return new(grid.Left, grid.Bottom, grid.Width, LaneHeight); } }

    private Rectangle RulerBounds { get { Rectangle grid = GridBounds; return new(grid.Left, 0, grid.Width, RulerHeight); } }

    /// <summary>Pixels a quarter note takes (zoom).</summary>
    public float PixelsPerBeat => _pixelsPerBeat;

    public float TickToX(long tick) => GridBounds.Left + tick * _pixelsPerBeat / Document.TicksPerBeat - _scrollX;

    public long XToTick(int x) => Math.Max(0, (long)Math.Floor((x - GridBounds.Left + _scrollX) * (double)Document.TicksPerBeat / _pixelsPerBeat));

    public int PitchToY(int pitch) => GridBounds.Top + (127 - pitch) * RowHeight - _scrollY;

    public int YToPitch(int y) => Math.Clamp(127 - (int)Math.Floor((y - GridBounds.Top + _scrollY) / (double)RowHeight), 0, 127);

    /// <summary>Where a note is drawn.</summary>
    public RectangleF NoteBounds(MusicNote note) =>
        new(TickToX(note.Start), PitchToY(note.Pitch) + 1, Math.Max(3f, note.Length * _pixelsPerBeat / Document.TicksPerBeat), RowHeight - 1);

    // ── Scrolling and zoom ──────────────────────────────────────────────────

    public void RefreshScroll()
    {
        Rectangle grid = GridBounds;
        long ticks = Math.Max(Math.Max(Document.EndTick, Document.LoopEnd) + Document.TicksPerBar * 2, Document.TicksPerBar * 16);
        int content = (int)Math.Min(int.MaxValue / 2, ticks * _pixelsPerBeat / Document.TicksPerBeat);
        _horizontal.Minimum = 0;
        _horizontal.LargeChange = Math.Max(1, grid.Width);
        _horizontal.SmallChange = Math.Max(1, (int)(_pixelsPerBeat / 2));
        _horizontal.Maximum = Math.Max(0, content);
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, content - grid.Width + 1));
        _horizontal.Value = Math.Clamp(_scrollX, _horizontal.Minimum, _horizontal.Maximum);
        int height = 128 * RowHeight;
        _vertical.Minimum = 0;
        _vertical.LargeChange = Math.Max(1, grid.Height);
        _vertical.SmallChange = RowHeight;
        _vertical.Maximum = height;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, height - grid.Height + 1));
        _vertical.Value = Math.Clamp(_scrollY, _vertical.Minimum, _vertical.Maximum);
    }

    /// <summary>Brings the selected track's notes (or middle C) into view from the start.</summary>
    public void ScrollToNotes()
    {
        List<MusicNote> notes = _editor.SelectedTrack.Notes;
        if (notes.Count == 0) notes = Document.Tracks.SelectMany(track => track.Notes).ToList();
        double pitch = notes.Count == 0 ? 60 : notes.Average(note => note.Pitch);
        _scrollY = (int)((127 - pitch) * RowHeight - GridBounds.Height / 2.0);
        _scrollX = 0;
        _placed = GridBounds.Height > 40;
        RefreshScroll();
        Invalidate();
    }

    public void Zoom(float factor)
    {
        Rectangle grid = GridBounds;
        double centre = (grid.Width / 2.0 + _scrollX) / _pixelsPerBeat;
        _pixelsPerBeat = Math.Clamp(_pixelsPerBeat * factor, 4f, 1024f);
        _scrollX = (int)Math.Max(0, centre * _pixelsPerBeat - grid.Width / 2.0);
        RefreshScroll();
        Invalidate();
    }

    /// <summary>Keeps the play-head in view while the music plays.</summary>
    public void FollowPlayhead()
    {
        Rectangle grid = GridBounds;
        float x = TickToX(_editor.PlayheadTick);
        if (x < grid.Left || x > grid.Right - 24)
        {
            _scrollX = (int)Math.Max(0, _scrollX + x - grid.Left - 32);
            RefreshScroll();
        }
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(EditorChrome.Canvas);
        Rectangle grid = GridBounds, lane = LaneBounds, ruler = RulerBounds;
        MusicDocument document = Document;
        int topPitch = YToPitch(grid.Top), bottomPitch = YToPitch(grid.Bottom);

        // Rows: the black keys' rows darker, a line above each C.
        Color whiteRow = UiTokens.Blend(EditorChrome.Surface, EditorChrome.Canvas, 0.55f);
        using (SolidBrush white = new(whiteRow))
        using (Pen octave = new(EditorChrome.Border))
        {
            g.SetClip(grid);
            for (int pitch = bottomPitch; pitch <= topPitch; pitch++)
            {
                int y = PitchToY(pitch);
                if (!IsBlackKey(pitch)) g.FillRectangle(white, grid.Left, y, grid.Width, RowHeight);
                if (pitch % 12 == 0) g.DrawLine(octave, grid.Left, y + RowHeight, grid.Right, y + RowHeight);
            }
            g.ResetClip();
        }

        // Lines: the snap's faintly, beats, then bars strongest.
        long firstTick = XToTick(grid.Left), lastTick = XToTick(grid.Right) + 1;
        Color barColour = UiTokens.Blend(EditorChrome.Border, EditorChrome.Text, 0.2f);
        DrawLines(g, grid.Top, lane.Bottom, firstTick, lastTick, _editor.SnapTicks, UiTokens.Blend(EditorChrome.Border, EditorChrome.Canvas, 0.6f));
        DrawLines(g, grid.Top, lane.Bottom, firstTick, lastTick, document.TicksPerSignatureBeat, EditorChrome.Border);
        DrawLines(g, 0, lane.Bottom, firstTick, lastTick, document.TicksPerBar, barColour);

        // The loop range, faint over the grid when Loop is on.
        if (document.LoopEnd > document.LoopStart && _editor.LoopEnabled)
        {
            using SolidBrush band = new(Color.FromArgb(28, EditorChrome.Success));
            float left = TickToX(document.LoopStart), right = TickToX(document.LoopEnd);
            g.SetClip(grid);
            g.FillRectangle(band, left, grid.Top, right - left, grid.Height);
            g.ResetClip();
        }

        // Notes: other tracks faint, then the selected track's, coloured by velocity.
        g.SetClip(grid);
        g.SmoothingMode = SmoothingMode.None;
        MusicTrack selected = _editor.SelectedTrack;
        using (SolidBrush ghost = new(Color.FromArgb(60, EditorChrome.Muted)))
            foreach (MusicTrack track in document.Tracks)
            {
                if (track == selected) continue;
                foreach (MusicNote note in track.Notes) if (IsInView(note, firstTick, lastTick, topPitch, bottomPitch)) g.FillRectangle(ghost, NoteBounds(note));
            }
        using (Pen edge = new(UiTokens.Blend(EditorChrome.Accent, EditorChrome.Canvas, 0.4f)))
        using (Pen chosen = new(EditorChrome.Warning, 2f))
            foreach (MusicNote note in selected.Notes)
            {
                if (!IsInView(note, firstTick, lastTick, topPitch, bottomPitch)) continue;
                RectangleF bounds = NoteBounds(note);
                bool isSelected = _editor.IsSelected(note);
                Color fill = UiTokens.Blend(EditorChrome.Accent, EditorChrome.Canvas, 0.25f + 0.75f * note.Velocity / 127f);
                using SolidBrush brush = new(isSelected ? UiTokens.Blend(EditorChrome.Warning, fill, 0.35f) : fill);
                g.FillRectangle(brush, bounds);
                g.DrawRectangle(isSelected ? chosen : edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            }
        if (_drag == DragMode.Marquee && _marquee.Width > 0)
        {
            using Pen box = new(EditorChrome.Text) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(box, _marquee);
        }
        if (selected.Notes.Count == 0)
        {
            using SolidBrush hint = new(EditorChrome.Muted);
            using StringFormat centre = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString($"{selected.Name} has no notes yet: click the grid to add one.", EditorChrome.BaseFont, hint, grid, centre);
        }
        g.ResetClip();

        DrawKeyboard(g, grid, topPitch, bottomPitch);
        DrawRuler(g, ruler, firstTick, lastTick);
        DrawLane(g, lane, selected, firstTick, lastTick);

        // The play-head, through ruler, grid and lane.
        float playhead = TickToX(_editor.PlayheadTick);
        if (playhead >= grid.Left && playhead <= grid.Right)
        {
            using Pen line = new(EditorChrome.Error, 2f);
            g.DrawLine(line, playhead, 0, playhead, lane.Bottom);
        }
    }

    private static bool IsInView(MusicNote note, long firstTick, long lastTick, int topPitch, int bottomPitch) =>
        note.End >= firstTick && note.Start <= lastTick && note.Pitch >= bottomPitch && note.Pitch <= topPitch;

    private void DrawLines(Graphics g, int top, int bottom, long firstTick, long lastTick, long step, Color colour)
    {
        if (step <= 0 || step * _pixelsPerBeat / Document.TicksPerBeat < 6) return;
        using Pen pen = new(colour);
        Rectangle grid = GridBounds;
        for (long tick = firstTick / step * step; tick <= lastTick; tick += step)
        {
            float x = TickToX(tick);
            if (x >= grid.Left && x <= grid.Right) g.DrawLine(pen, x, top, x, bottom);
        }
    }

    private static bool IsBlackKey(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;

    private void DrawKeyboard(Graphics g, Rectangle grid, int topPitch, int bottomPitch)
    {
        Rectangle keys = new(0, grid.Top, KeyboardWidth, grid.Height);
        g.SetClip(keys);
        using SolidBrush white = new(Color.FromArgb(226, 229, 236)), black = new(Color.FromArgb(38, 40, 48)), label = new(Color.FromArgb(70, 74, 86));
        using Pen line = new(Color.FromArgb(150, 154, 166));
        for (int pitch = bottomPitch; pitch <= topPitch; pitch++)
        {
            int y = PitchToY(pitch);
            bool blackKey = IsBlackKey(pitch);
            g.FillRectangle(blackKey ? black : white, 0, y, blackKey ? KeyboardWidth * 0.62f : KeyboardWidth, RowHeight);
            if (blackKey) g.FillRectangle(white, KeyboardWidth * 0.62f, y, KeyboardWidth * 0.38f, RowHeight);
            g.DrawLine(line, 0, y + RowHeight, KeyboardWidth, y + RowHeight);
            if (pitch % 12 == 0 && RowHeight >= 9)
                g.DrawString(MusicInstruments.NoteName(pitch), EditorChrome.SmallFont, label, KeyboardWidth - 30 * UiScale, y - 2);
        }
        g.ResetClip();
    }

    private void DrawRuler(Graphics g, Rectangle ruler, long firstTick, long lastTick)
    {
        using (SolidBrush surface = new(EditorChrome.Surface)) g.FillRectangle(surface, 0, 0, ClientSize.Width, ruler.Height);
        MusicDocument document = Document;
        g.SetClip(ruler);
        long loopStart = _drag == DragMode.Ruler && _moved ? Math.Min(_rulerStart, _rulerEnd) : document.LoopStart;
        long loopEnd = _drag == DragMode.Ruler && _moved ? Math.Max(_rulerStart, _rulerEnd) : document.LoopEnd;
        if (loopEnd > loopStart)
        {
            using SolidBrush band = new(Color.FromArgb(_editor.LoopEnabled || _drag == DragMode.Ruler ? 150 : 60, EditorChrome.Success));
            float left = TickToX(loopStart), right = TickToX(loopEnd);
            g.FillRectangle(band, left, ruler.Bottom - 8 * UiScale, right - left, 6 * UiScale);
        }
        using SolidBrush text = new(EditorChrome.Muted);
        long bar = document.TicksPerBar;
        float barWidth = bar * _pixelsPerBeat / document.TicksPerBeat;
        int every = barWidth >= 40 ? 1 : barWidth >= 12 ? 4 : 16;
        for (long tick = firstTick / bar * bar; tick <= lastTick; tick += bar)
        {
            long number = tick / bar + 1;
            if ((number - 1) % every == 0) g.DrawString(number.ToString(System.Globalization.CultureInfo.InvariantCulture), EditorChrome.SmallFont, text, TickToX(tick) + 3, 3);
        }
        g.ResetClip();
        using Pen border = new(EditorChrome.Border);
        g.DrawLine(border, 0, ruler.Bottom - 1, ClientSize.Width, ruler.Bottom - 1);
    }

    private void DrawLane(Graphics g, Rectangle lane, MusicTrack track, long firstTick, long lastTick)
    {
        using (SolidBrush surface = new(EditorChrome.Surface)) g.FillRectangle(surface, 0, lane.Top, ClientSize.Width, lane.Height);
        using (SolidBrush text = new(EditorChrome.Muted)) g.DrawString("Velocity", EditorChrome.SmallFont, text, 4, lane.Top + 4);
        using (Pen border = new(EditorChrome.Border)) g.DrawLine(border, 0, lane.Top, ClientSize.Width, lane.Top);
        g.SetClip(lane);
        using Pen bar = new(EditorChrome.Accent, Math.Max(2f, 3 * UiScale)), chosen = new(EditorChrome.Warning, Math.Max(2f, 3 * UiScale));
        float usable = lane.Height - 8;
        foreach (MusicNote note in track.Notes)
        {
            if (note.End < firstTick || note.Start > lastTick) continue;
            float x = TickToX(note.Start) + 1;
            float top = lane.Bottom - 4 - usable * note.Velocity / 127f;
            g.DrawLine(_editor.IsSelected(note) ? chosen : bar, x, lane.Bottom - 4, x, top);
            g.FillEllipse(_editor.IsSelected(note) ? Brushes.Gold : Brushes.CornflowerBlue, x - 3, top - 3, 6, 6);
        }
        g.ResetClip();
    }

    // ── Mouse ───────────────────────────────────────────────────────────────

    private MusicNote? NoteAt(Point point)
    {
        List<MusicNote> notes = _editor.SelectedTrack.Notes;
        for (int index = notes.Count - 1; index >= 0; index--)
        {
            RectangleF bounds = NoteBounds(notes[index]);
            bounds.Inflate(0, 1);
            if (bounds.Contains(point)) return notes[index];
        }
        return null;
    }

    private bool NearRightEdge(MusicNote note, int x)
    {
        RectangleF bounds = NoteBounds(note);
        return x >= bounds.Right - Math.Clamp(bounds.Width / 3, 3, 8);
    }

    private MusicNote? NoteAtLane(int x)
    {
        MusicNote? best = null;
        float distance = 7 * UiScale;
        foreach (MusicNote note in _editor.SelectedTrack.Notes)
        {
            float away = Math.Abs(TickToX(note.Start) + 1 - x);
            if (away < distance || (away <= distance && _editor.IsSelected(note))) { best = note; distance = away; }
        }
        return best;
    }

    private void Begin(string label, MusicNote anchor, DragMode mode, MouseEventArgs e)
    {
        _before = _editor.Snapshot();
        _gestureLabel = label;
        _anchor = anchor;
        _drag = mode;
        _down = e.Location;
        _moved = false;
        _downTick = XToTick(e.X);
        _downPitch = YToPitch(e.Y);
        _originals.Clear();
        IEnumerable<MusicNote> notes = _editor.IsSelected(anchor) ? _editor.SelectedNotes : [anchor];
        foreach (MusicNote note in notes) _originals[note] = (note.Start, note.Length, note.Pitch, note.Velocity);
        Capture = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Rectangle grid = GridBounds, lane = LaneBounds, ruler = RulerBounds;
        bool control = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0, alt = (ModifierKeys & Keys.Alt) != 0;
        if (e.X < KeyboardWidth && e.Y >= grid.Top && e.Y < grid.Bottom)
        {
            _editor.PreviewNote(YToPitch(e.Y));
            return;
        }
        if (ruler.Contains(e.Location) && e.Button == MouseButtons.Left)
        {
            _drag = DragMode.Ruler;
            _down = e.Location;
            _moved = false;
            _rulerStart = _rulerEnd = _editor.SnapRound(XToTick(e.X));
            Capture = true;
            return;
        }
        if (lane.Contains(e.Location) && e.Button == MouseButtons.Left)
        {
            if (NoteAtLane(e.X) is { } target)
            {
                Begin("Note velocity", target, DragMode.Lane, e);
                ApplyLane(e.Y);
            }
            return;
        }
        if (!grid.Contains(e.Location)) return;
        MusicNote? note = NoteAt(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (note is not null) _editor.DeleteNote(note);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (note is not null)
        {
            if (control) { _editor.ToggleSelected(note); return; }
            if (!_editor.IsSelected(note)) _editor.SelectNotes([note]);
            DragMode mode = alt ? DragMode.Velocity : NearRightEdge(note, e.X) ? DragMode.Resize : DragMode.Move;
            Begin(mode switch { DragMode.Velocity => "Note velocity", DragMode.Resize => "Resize notes", _ => "Move notes" }, note, mode, e);
            return;
        }
        if (control || shift)
        {
            _drag = DragMode.Marquee;
            _down = e.Location;
            _marquee = Rectangle.Empty;
            if (!control) _editor.ClearSelection();
            Capture = true;
            return;
        }
        // An empty place: a new note there, as long as the last one; dragging sets its length.
        long length = _editor.NewNoteLength > 0 ? _editor.NewNoteLength : Math.Max(_editor.SnapTicks, Math.Max(1, Document.TicksPerBeat / 4));
        string before = _editor.Snapshot();
        MusicNote added = new() { Pitch = YToPitch(e.Y), Start = _editor.SnapFloor(XToTick(e.X)), Length = length, Velocity = _editor.NewNoteVelocity };
        _editor.SelectedTrack.Notes.Add(added);
        _editor.SelectNotes([added]);
        Begin("Add note", added, DragMode.Resize, e);
        _before = before;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None)
        {
            Cursor = GridBounds.Contains(e.Location) && NoteAt(e.Location) is { } hover
                ? NearRightEdge(hover, e.X) ? Cursors.SizeWE : Cursors.SizeAll
                : Cursors.Default;
            return;
        }
        if (!_moved && Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) < 3) return;
        _moved = true;
        switch (_drag)
        {
            case DragMode.Move when _anchor is not null && _originals.TryGetValue(_anchor, out var anchor):
                {
                    long raw = XToTick(e.X) - _downTick;
                    long start = _editor.Snap == MusicSnap.Off ? anchor.Start + raw : _editor.SnapRound(anchor.Start + raw);
                    long ticks = Math.Max(start - anchor.Start, -_originals.Values.Min(value => value.Start));
                    int semitones = Math.Clamp(YToPitch(e.Y) - _downPitch, -_originals.Values.Min(value => value.Pitch), 127 - _originals.Values.Max(value => value.Pitch));
                    foreach ((MusicNote note, var original) in _originals) { note.Start = original.Start + ticks; note.Pitch = original.Pitch + semitones; }
                    break;
                }
            case DragMode.Resize when _anchor is not null && _originals.TryGetValue(_anchor, out var anchor):
                {
                    long pointer = XToTick(e.X);
                    long end = _editor.Snap == MusicSnap.Off ? pointer : _editor.SnapRound(pointer);
                    long minimum = _editor.Snap == MusicSnap.Off ? 1 : _editor.SnapTicks;
                    long delta = Math.Max(minimum, end - anchor.Start) - anchor.Length;
                    foreach ((MusicNote note, var original) in _originals) note.Length = Math.Max(1, original.Length + delta);
                    break;
                }
            case DragMode.Velocity:
                {
                    int change = (_down.Y - e.Y) / 2;
                    foreach ((MusicNote note, var original) in _originals) note.Velocity = Math.Clamp(original.Velocity + change, 1, 127);
                    break;
                }
            case DragMode.Lane:
                ApplyLane(e.Y);
                break;
            case DragMode.Marquee:
                {
                    _marquee = Rectangle.FromLTRB(Math.Min(_down.X, e.X), Math.Min(_down.Y, e.Y), Math.Max(_down.X, e.X), Math.Max(_down.Y, e.Y));
                    _editor.SelectNotes(_editor.SelectedTrack.Notes.Where(note => _marquee.IntersectsWith(Rectangle.Round(NoteBounds(note)))));
                    break;
                }
            case DragMode.Ruler:
                _rulerEnd = _editor.SnapRound(XToTick(e.X));
                break;
        }
        Invalidate();
    }

    private void ApplyLane(int y)
    {
        Rectangle lane = LaneBounds;
        int velocity = Math.Clamp((int)Math.Round((lane.Bottom - 4 - y) / (double)Math.Max(1, lane.Height - 8) * 127), 1, 127);
        foreach (MusicNote note in _originals.Keys) note.Velocity = velocity;
        _editor.NewNoteVelocity = velocity;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        DragMode mode = _drag;
        _drag = DragMode.None;
        Capture = false;
        switch (mode)
        {
            case DragMode.Move or DragMode.Resize or DragMode.Velocity or DragMode.Lane:
                if ((mode == DragMode.Resize || _gestureLabel == "Add note") && _anchor is not null) _editor.NewNoteLength = _anchor.Length;
                _editor.FinishGesture(_before, _gestureLabel);
                break;
            case DragMode.Ruler:
                if (_moved && _rulerEnd != _rulerStart) _editor.SetLoopRange(_rulerStart, _rulerEnd);
                else
                {
                    _editor.PlayheadTick = _rulerStart;
                    if (_editor.IsPlaying && !_editor.LoopEnabled) _editor.Play();
                }
                break;
        }
        _anchor = null;
        _originals.Clear();
        _marquee = Rectangle.Empty;
        Invalidate();
    }

    // A drag cut short (the window lost the mouse) still ends as one undo step.
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_drag != DragMode.None && !Capture) OnMouseUp(new MouseEventArgs(MouseButtons.Left, 0, _down.X, _down.Y, 0));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0) { Zoom(e.Delta > 0 ? 1.25f : 0.8f); return; }
        if ((ModifierKeys & Keys.Shift) != 0) _scrollX = Math.Max(0, _scrollX - e.Delta / 2);
        else _scrollY = Math.Max(0, _scrollY - e.Delta / 120 * RowHeight * 3);
        RefreshScroll();
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);
}

using System.Globalization;
using Genesis.Application.Core.Audio;
using Genesis.Application.Core.Audio.Music;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>How far notes snap when they are placed, moved and resized.</summary>
public enum MusicSnap
{
    Bar,
    Beat,
    Eighth,
    Sixteenth,
    Off,
}

/// <summary>
/// The Audio editor's Music mode, for a <c>.music.json</c> piece: tracks (each with an instrument,
/// mute, solo and volume), a piano roll to place, move and resize notes on a key grid with snapping,
/// a velocity per note, tempo and time signature, Play with a loop range and a play-head, MIDI import
/// and export, and Render to Audio, which writes the piece into an Audio resource a game plays with
/// PlaySound. Every edit can be undone.
/// </summary>
/// <remarks>
/// The piano roll: click an empty place for a note (drag to set its length); drag a note to move it,
/// its right edge to resize it, Alt+drag to change its velocity; Ctrl+click adds a note to the
/// selection and Ctrl+drag on empty space selects with a box; right-click deletes. The velocity lane
/// under the grid sets velocities by dragging. Drag on the ruler for the loop range; click it to move
/// the play-head. Delete, Ctrl+C, Ctrl+X, Ctrl+V (at the play-head), Ctrl+A, the arrow keys (Shift: an
/// octave) and Space (play and stop) work while the roll has the focus.
/// </remarks>
public sealed partial class MusicEditorControl : EditorSurfaceControl, IRunnableResource
{
    private MusicDocument _document;
    private int _trackIndex;
    private readonly HashSet<MusicNote> _selection = [];
    private static List<MusicNote> _clipboard = [];
    private readonly MusicPianoRoll _roll;
    private readonly FlowLayoutPanel _side;
    private readonly FlowLayoutPanel _trackRows;
    private readonly Label _statusLabel;
    private readonly TextBox _trackName;
    private readonly ThemedComboBox _instrument;
    private readonly NumericUpDown _attack, _release, _filter, _velocity;
    private readonly NumericUpDown _tempo;
    private readonly ToolStripComboBox _signature, _snapChoice;
    private readonly ToolStripButton _loopButton;
    private readonly ToolStripLabel _timeLabel;
    private EditorCommandBar? _commandBar;
    private bool _syncing;
    private MusicSnap _snap = MusicSnap.Sixteenth;

    private static readonly string[] Signatures = ["2/4", "3/4", "4/4", "5/4", "6/8", "7/8", "9/8", "12/8"];

    public MusicEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        Name = "MusicEditor";
        _document = LoadDocument(resourcePath);
        if (_document.Tracks.Count == 0) _document.Tracks.Add(new MusicTrack { Name = "Piano" });

        _roll = new MusicPianoRoll(this) { Name = "MusicPianoRoll", Dock = DockStyle.Fill };
        _statusLabel = EditorChrome.MakeStatusBar();
        _statusLabel.Name = "MusicStatus";

        // The transport and grid settings sit above the roll.
        ToolStrip transport = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text, Font = EditorChrome.BaseFont, Name = "MusicTransport" };
        transport.Items.Add(EditorChrome.ToolButton("Play", "Play from the play-head, or the loop range when Loop is on (Space)", Play));
        transport.Items.Add(EditorChrome.ToolButton("Stop", "Stop playing", Stop));
        _loopButton = EditorChrome.ToolButton("Loop", "Repeat the loop range (drag on the ruler to set it)", () => SetLoop(_loopButton!.Checked), toggle: true);
        _loopButton.Name = "MusicLoop";
        transport.Items.Add(_loopButton);
        transport.Items.Add(new ToolStripSeparator());
        transport.Items.Add(new ToolStripLabel("Tempo") { ForeColor = EditorChrome.Muted });
        _tempo = new NumericUpDown { Minimum = (decimal)MusicDocument.MinimumTempo, Maximum = (decimal)MusicDocument.MaximumTempo, DecimalPlaces = 1, Increment = 1, Width = 70, Name = "MusicTempo" };
        EditorChrome.StyleField(_tempo);
        transport.Items.Add(new ToolStripControlHost(_tempo) { ToolTipText = "Beats (quarter notes) a minute" });
        transport.Items.Add(new ToolStripLabel("Time") { ForeColor = EditorChrome.Muted });
        _signature = new ToolStripComboBox { AutoSize = false, DropDownStyle = ComboBoxStyle.DropDownList, Width = 64, Name = "MusicTimeSignature", ToolTipText = "Time signature" };
        _signature.Items.AddRange(Signatures);
        transport.Items.Add(_signature);
        transport.Items.Add(new ToolStripLabel("Snap") { ForeColor = EditorChrome.Muted });
        _snapChoice = new ToolStripComboBox { AutoSize = false, DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Name = "MusicSnap", ToolTipText = "Notes snap to this when placed, moved and resized" };
        _snapChoice.Items.AddRange(["Bar", "Beat", "1/8", "1/16", "Off"]);
        transport.Items.Add(_snapChoice);
        transport.Items.Add(new ToolStripSeparator());
        transport.Items.Add(EditorChrome.ToolButton("−", "Zoom out (Ctrl+wheel)", () => _roll.Zoom(0.5f)));
        transport.Items.Add(EditorChrome.ToolButton("+", "Zoom in (Ctrl+wheel)", () => _roll.Zoom(2f)));
        _timeLabel = new ToolStripLabel { ForeColor = EditorChrome.Muted, Name = "MusicTime" };
        transport.Items.Add(_timeLabel);

        _trackName = new TextBox { Name = "MusicTrackName" };
        EditorChrome.StyleField(_trackName);
        _instrument = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "MusicInstrument" };
        _instrument.Items.AddRange([.. MusicInstruments.Choices()]);
        EditorChrome.StyleField(_instrument);
        _attack = MakeNumeric(0m, 10m, 0.01m, 3, "MusicAttack");
        _release = MakeNumeric(0m, 10m, 0.01m, 3, "MusicRelease");
        _filter = MakeNumeric(0m, 20000m, 100m, 0, "MusicFilter");
        _velocity = MakeNumeric(1m, 127m, 1m, 0, "MusicVelocity");

        _trackRows = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Name = "MusicTracks",
            BackColor = EditorChrome.Surface, Margin = new Padding(0, 0, 0, 6) };
        _side = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = EditorChrome.Surface, Padding = new Padding(10, 8, 8, 8), Name = "MusicSide" };
        _side.Controls.Add(SideText("Make music: tracks of notes, each played by an instrument", true));
        _side.Controls.Add(SideText("Click the grid for a note, drag it to move, drag its right edge to resize. Play to hear it; Render to Audio makes the Audio resource a game plays."));
        _side.Controls.Add(EditorChrome.DividerLabel("Tracks"));
        _side.Controls.Add(_trackRows);
        Button addTrack = SideButton("Add track", "MusicAddTrack", () => AddTrack("Piano"));
        Button removeTrack = SideButton("Remove track", "MusicRemoveTrack", () => RemoveTrack(_trackIndex));
        _side.Controls.Add(addTrack);
        _side.Controls.Add(removeTrack);
        _side.Controls.Add(EditorChrome.DividerLabel("Selected track"));
        _side.Controls.Add(SideRow("Name", _trackName));
        _side.Controls.Add(SideRow("Instrument", _instrument));
        _side.Controls.Add(SideRow("Attack (s)", _attack));
        _side.Controls.Add(SideRow("Release (s)", _release));
        _side.Controls.Add(SideRow("Filter (Hz)", _filter));
        _side.Controls.Add(SideButton("Instrument's own sound", "MusicResetSound", ResetTrackSound));
        _side.Controls.Add(SideButton("Hear the instrument", "MusicHearInstrument", () => PreviewNote(60)));
        _side.Controls.Add(EditorChrome.DividerLabel("Notes"));
        _side.Controls.Add(SideRow("Velocity", _velocity));
        _side.Controls.Add(SideText("Velocity is how hard a note is played (1 to 127): drag in the lane under the grid, Alt+drag a note, or type it here for the selected notes. Ctrl+drag on empty space selects with a box; right-click deletes."));

        Panel side = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        side.Name = "MusicSidePanel";
        side.Controls.Add(_side);

        ToolStrip toolbar = BuildMusicToolbar();
        Panel workspace = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        workspace.Controls.Add(_roll);
        workspace.Controls.Add(transport);
        Controls.Add(workspace);
        Controls.Add(side);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);

        _tempo.ValueChanged += (_, _) => { if (!_syncing) SetTempo((double)_tempo.Value); };
        _signature.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _signature.SelectedItem is not string text) return;
            string[] parts = text.Split('/');
            SetTimeSignature(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        };
        _snapChoice.SelectedIndexChanged += (_, _) => { if (!_syncing) SetSnap((MusicSnap)Math.Max(0, _snapChoice.SelectedIndex)); };
        _trackName.Leave += (_, _) => RenameTrack(_trackName.Text);
        _trackName.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { RenameTrack(_trackName.Text); e.SuppressKeyPress = true; } };
        _instrument.SelectedIndexChanged += (_, _) => { if (!_syncing && _instrument.SelectedItem is string name) SetInstrument(name); };
        _attack.ValueChanged += (_, _) => { if (!_syncing) SetTrackSound(attack: (double)_attack.Value); };
        _release.ValueChanged += (_, _) => { if (!_syncing) SetTrackSound(release: (double)_release.Value); };
        _filter.ValueChanged += (_, _) => { if (!_syncing) SetTrackSound(filter: (double)_filter.Value); };
        _velocity.ValueChanged += (_, _) => { if (!_syncing) SetSelectedVelocity((int)_velocity.Value); };
        _side.SizeChanged += (_, _) => LayoutSide();

        InitializeTransport();
        RefreshAll();
        _roll.ScrollToNotes();
        if (LoadWarning is not null) _statusLabel.Text = "The music file could not be read; a new piece is shown and Save is refused: " + LoadWarning;
    }

    private MusicDocument LoadDocument(string path)
    {
        try { return File.Exists(path) ? MusicDocumentSerializer.Load(path) : MusicDocument.CreateDefault(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LoadWarning = exception.Message;
            return MusicDocument.CreateDefault();
        }
    }

    private ToolStrip BuildMusicToolbar()
    {
        EditorCommandBar bar = _commandBar = EditorChrome.MakeToolbar();
        bar.Name = "MusicToolbar";
        bar.Items.Add(EditorChrome.ToolButton("Import MIDI…", "Open a MIDI file (.mid) as this piece: its tracks, notes and tempo", ImportMidiWithDialog));
        bar.Items.Add(EditorChrome.ToolButton("Export MIDI…", "Save this piece as a MIDI file for other music programs", ExportMidiWithDialog));
        bar.Items.Add(EditorChrome.ToolButton("Render to Audio", "Make (or update) the Audio resource a game plays with PlaySound", () => RenderToAudioReporting()));
        ToolStripDropDownButton options = new("Options") { ForeColor = EditorChrome.Text };
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Undo", "Undo the last edit (Ctrl+Z)", Undo));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Redo", "Redo the last edit (Ctrl+Y)", Redo));
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Select all notes", "Select every note of the track (Ctrl+A)", SelectAll));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Open the Audio resource", "Open the Audio resource Render to Audio writes", OpenRenderedAudio));
        bar.Items.Add(options);
        bar.Items.Add(EditorChrome.ToolButton("Save", "Save this music (Ctrl+S)", Save));
        return bar;
    }

    public EditorCommandBar CommandBar => _commandBar!;

    // ── What tests and the roll read ────────────────────────────────────────

    /// <summary>The piece as it is being edited.</summary>
    public MusicDocument Document => _document;

    public MusicTrack SelectedTrack => _document.Tracks[Math.Clamp(_trackIndex, 0, _document.Tracks.Count - 1)];

    public int SelectedTrackIndex
    {
        get => _trackIndex;
        set
        {
            int index = Math.Clamp(value, 0, _document.Tracks.Count - 1);
            if (index == _trackIndex) return;
            _trackIndex = index;
            _selection.Clear();
            RefreshAll();
        }
    }

    public IReadOnlyCollection<MusicNote> SelectedNotes => _selection;

    public MusicSnap Snap => _snap;

    public string StatusText => _statusLabel.Text;

    internal MusicPianoRoll Roll => _roll;

    internal bool IsSelected(MusicNote note) => _selection.Contains(note);

    /// <summary>Ticks a note snaps to, 1 when snapping is off.</summary>
    public long SnapTicks => _snap switch
    {
        MusicSnap.Bar => _document.TicksPerBar,
        MusicSnap.Beat => _document.TicksPerSignatureBeat,
        MusicSnap.Eighth => Math.Max(1, _document.TicksPerBeat / 2),
        MusicSnap.Sixteenth => Math.Max(1, _document.TicksPerBeat / 4),
        _ => 1,
    };

    internal long SnapRound(long tick) => Math.Max(0, (long)Math.Round(tick / (double)SnapTicks) * SnapTicks);

    internal long SnapFloor(long tick) => Math.Max(0, (long)Math.Floor(tick / (double)SnapTicks) * SnapTicks);

    /// <summary>The length a new note gets: the last one placed or resized, or a beat at first.</summary>
    internal long NewNoteLength { get; set; }

    internal int NewNoteVelocity { get; set; } = 100;

    // ── Changes, each one undoable ──────────────────────────────────────────

    internal string Snapshot() => MusicDocumentSerializer.Serialize(_document);

    /// <summary>Makes a change to the piece; what it was before goes on the undo stack.</summary>
    private void Commit(string label, Action change)
    {
        string before = Snapshot();
        change();
        _document.Normalize();
        FinishGesture(before, label);
    }

    /// <summary>Ends an edit made in steps (a drag in the roll): one undo step from <paramref name="before"/> to now.</summary>
    internal void FinishGesture(string before, string label)
    {
        _document.Normalize();
        string after = Snapshot();
        if (before != after) PushEdit(label, () => Restore(after), () => Restore(before), maximumEntries: 100);
        RefreshAll();
    }

    private void Restore(string snapshot)
    {
        _document = MusicDocumentSerializer.Deserialize(snapshot);
        if (_document.Tracks.Count == 0) _document.Tracks.Add(new MusicTrack { Name = "Piano" });
        _trackIndex = Math.Clamp(_trackIndex, 0, _document.Tracks.Count - 1);
        _selection.Clear();
        RefreshAll();
    }

    public override void Save()
    {
        if (LoadWarning is not null) throw new InvalidDataException("The unreadable music file was kept as it was: " + LoadWarning);
        _document.Normalize();
        WriteResourceText(MusicDocumentSerializer.Serialize(_document));
        AcceptSave();
    }

    // ── Tracks ──────────────────────────────────────────────────────────────

    /// <summary>A new track at the end, playing <paramref name="instrument"/>; it becomes the selected track.</summary>
    public MusicTrack AddTrack(string instrument, string? name = null)
    {
        string chosen = MusicInstruments.Exists(instrument) ? instrument.Trim() : "Piano";
        string baseName = string.IsNullOrWhiteSpace(name) ? chosen : name.Trim();
        string unique = baseName;
        for (int index = 2; _document.Tracks.Any(track => string.Equals(track.Name, unique, StringComparison.OrdinalIgnoreCase)); index++) unique = baseName + " " + index;
        MusicTrack added = new() { Name = unique, Instrument = chosen, Volume = 0.8f };
        Commit("Add track", () => _document.Tracks.Add(added));
        _trackIndex = _document.Tracks.IndexOf(added);
        _selection.Clear();
        RefreshAll();
        return added;
    }

    public void RemoveTrack(int index)
    {
        if (_document.Tracks.Count <= 1 || index < 0 || index >= _document.Tracks.Count)
        {
            _statusLabel.Text = "A piece keeps at least one track.";
            return;
        }
        Commit("Remove track", () => _document.Tracks.RemoveAt(index));
        _trackIndex = Math.Clamp(_trackIndex, 0, _document.Tracks.Count - 1);
        _selection.Clear();
        RefreshAll();
    }

    public void RenameTrack(string name)
    {
        if (_syncing || string.IsNullOrWhiteSpace(name) || name.Trim() == SelectedTrack.Name) return;
        MusicTrack track = SelectedTrack;
        Commit("Rename track", () => track.Name = name.Trim());
    }

    public void SetInstrument(string instrument)
    {
        if (!MusicInstruments.Exists(instrument)) throw new ArgumentException($"'{instrument}' is not an instrument; choose one of {string.Join(", ", MusicInstruments.BuiltIn)}.");
        MusicTrack track = SelectedTrack;
        Commit("Change instrument", () =>
        {
            track.Instrument = instrument.Trim();
            track.Program = -1;
            if (track.Name.Length == 0 || MusicInstruments.Choices().Contains(track.Name, StringComparer.OrdinalIgnoreCase)) track.Name = UniqueTrackName(instrument.Trim(), track);
        });
    }

    private string UniqueTrackName(string name, MusicTrack except)
    {
        string unique = name;
        for (int index = 2; _document.Tracks.Any(track => track != except && string.Equals(track.Name, unique, StringComparison.OrdinalIgnoreCase)); index++) unique = name + " " + index;
        return unique;
    }

    public void SetTrackVolume(int index, float volume)
    {
        MusicTrack track = _document.Tracks[index];
        Commit("Track volume", () => track.Volume = Math.Clamp(volume, 0f, 1f));
    }

    public void SetMute(int index, bool mute)
    {
        MusicTrack track = _document.Tracks[index];
        Commit(mute ? "Mute track" : "Unmute track", () => track.Mute = mute);
    }

    public void SetSolo(int index, bool solo)
    {
        MusicTrack track = _document.Tracks[index];
        Commit(solo ? "Solo track" : "Unsolo track", () => track.Solo = solo);
    }

    /// <summary>Sets the selected track's attack and release (seconds) and filter (Hz; 0 opens it); a null leaves that one as it is.</summary>
    public void SetTrackSound(double? attack = null, double? release = null, double? filter = null)
    {
        MusicTrack track = SelectedTrack;
        Commit("Instrument sound", () =>
        {
            if (attack is double a) track.Attack = a;
            if (release is double r) track.Release = r;
            if (filter is double f) track.Filter = f <= 0 ? 20000 : f;
        });
    }

    /// <summary>The selected track goes back to its instrument's own attack, release and filter.</summary>
    public void ResetTrackSound()
    {
        MusicTrack track = SelectedTrack;
        Commit("Instrument's own sound", () => { track.Attack = null; track.Release = null; track.Filter = null; });
    }

    // ── Notes ───────────────────────────────────────────────────────────────

    /// <summary>A note on the selected track; it becomes the selection.</summary>
    public MusicNote AddNote(int pitch, long start, long length, int velocity = 100)
    {
        MusicNote note = new() { Pitch = Math.Clamp(pitch, 0, 127), Start = Math.Max(0, start), Length = Math.Max(1, length), Velocity = Math.Clamp(velocity, 1, 127) };
        MusicTrack track = SelectedTrack;
        Commit("Add note", () => track.Notes.Add(note));
        _selection.Clear();
        _selection.Add(note);
        RefreshSelection();
        return note;
    }

    public void SelectNotes(IEnumerable<MusicNote> notes)
    {
        _selection.Clear();
        foreach (MusicNote note in notes)
            if (SelectedTrack.Notes.Contains(note)) _selection.Add(note);
        RefreshSelection();
    }

    internal void ToggleSelected(MusicNote note)
    {
        if (!_selection.Remove(note)) _selection.Add(note);
        RefreshSelection();
    }

    public void SelectAll() => SelectNotes(SelectedTrack.Notes);

    public void ClearSelection() => SelectNotes([]);

    /// <summary>Moves the selected notes by <paramref name="ticks"/> and <paramref name="semitones"/>, kept on the grid's edges.</summary>
    public void MoveSelected(long ticks, int semitones)
    {
        if (_selection.Count == 0) return;
        long earliest = _selection.Min(note => note.Start);
        int lowest = _selection.Min(note => note.Pitch), highest = _selection.Max(note => note.Pitch);
        ticks = Math.Max(ticks, -earliest);
        semitones = Math.Clamp(semitones, -lowest, 127 - highest);
        if (ticks == 0 && semitones == 0) return;
        Commit("Move notes", () =>
        {
            foreach (MusicNote note in _selection) { note.Start += ticks; note.Pitch += semitones; }
        });
    }

    /// <summary>Lengthens (or with a negative number shortens) the selected notes, never below a tick.</summary>
    public void ResizeSelected(long ticks)
    {
        if (_selection.Count == 0 || ticks == 0) return;
        Commit("Resize notes", () => { foreach (MusicNote note in _selection) note.Length = Math.Max(1, note.Length + ticks); });
    }

    public void SetSelectedVelocity(int velocity)
    {
        if (_selection.Count == 0) return;
        int value = Math.Clamp(velocity, 1, 127);
        Commit("Note velocity", () => { foreach (MusicNote note in _selection) note.Velocity = value; });
        NewNoteVelocity = value;
    }

    public void DeleteSelected()
    {
        if (_selection.Count == 0) return;
        MusicTrack track = SelectedTrack;
        MusicNote[] doomed = [.. _selection];
        Commit(doomed.Length == 1 ? "Delete note" : "Delete notes", () => track.Notes.RemoveAll(note => doomed.Contains(note)));
        _selection.Clear();
        RefreshSelection();
    }

    internal void DeleteNote(MusicNote note)
    {
        _selection.Clear();
        _selection.Add(note);
        DeleteSelected();
    }

    /// <summary>Copies the selected notes (the earliest at 0) for <see cref="Paste"/>.</summary>
    public void CopySelected()
    {
        if (_selection.Count == 0) return;
        long earliest = _selection.Min(note => note.Start);
        _clipboard = _selection.OrderBy(note => note.Start).Select(note => { MusicNote copy = note.Copy(); copy.Start -= earliest; return copy; }).ToList();
        _statusLabel.Text = $"Copied {_clipboard.Count} note{(_clipboard.Count == 1 ? string.Empty : "s")}.";
    }

    public void CutSelected()
    {
        CopySelected();
        DeleteSelected();
    }

    /// <summary>Pastes copied notes onto the selected track from <paramref name="at"/> (ticks); they become the selection.</summary>
    public IReadOnlyList<MusicNote> Paste(long at)
    {
        if (_clipboard.Count == 0) return [];
        long start = Math.Max(0, at);
        List<MusicNote> pasted = _clipboard.Select(note => { MusicNote copy = note.Copy(); copy.Start += start; return copy; }).ToList();
        MusicTrack track = SelectedTrack;
        Commit("Paste notes", () => track.Notes.AddRange(pasted));
        _selection.Clear();
        foreach (MusicNote note in pasted) _selection.Add(note);
        RefreshSelection();
        return pasted;
    }

    // ── The piece ───────────────────────────────────────────────────────────

    public void SetTempo(double bpm)
    {
        double value = Math.Clamp(bpm, MusicDocument.MinimumTempo, MusicDocument.MaximumTempo);
        if (Math.Abs(value - _document.Tempo) < 1e-9) return;
        Commit("Tempo", () => _document.Tempo = value);
    }

    public void SetTimeSignature(int beatsPerBar, int beatUnit)
    {
        if (beatsPerBar == _document.BeatsPerBar && beatUnit == _document.BeatUnit) return;
        Commit("Time signature", () => { _document.BeatsPerBar = beatsPerBar; _document.BeatUnit = beatUnit; });
    }

    public void SetSnap(MusicSnap snap)
    {
        _snap = snap;
        NewNoteLength = 0;
        SyncFields();
        _roll.Invalidate();
    }

    /// <summary>Sets the range Loop repeats (ticks) and turns Loop on; an empty range turns it off.</summary>
    public void SetLoopRange(long start, long end)
    {
        long from = Math.Max(0, Math.Min(start, end)), to = Math.Max(start, end);
        Commit("Loop range", () => { _document.LoopStart = from; _document.LoopEnd = to > from ? to : 0; });
        LoopEnabled = _document.LoopEnd > _document.LoopStart;
        SyncFields();
    }

    /// <summary>Replaces the piece with a MIDI file's tracks, notes, tempo and time signature (one undo step).</summary>
    public MusicDocument ImportMidi(string path)
    {
        MusicDocument imported = MidiFile.Read(path);
        Stop();
        if (imported.Tracks.Count == 0) throw new InvalidDataException("The MIDI file has no notes to import.");
        imported.Audio = _document.Audio;
        string before = Snapshot();
        _document = imported;
        _trackIndex = 0;
        _selection.Clear();
        FinishGesture(before, "Import MIDI");
        _roll.ScrollToNotes();
        _statusLabel.Text = $"Imported {Path.GetFileName(path)}: {imported.Tracks.Count} track{(imported.Tracks.Count == 1 ? string.Empty : "s")}, "
            + $"{imported.Tracks.Sum(track => track.Notes.Count)} notes, {imported.Tempo:0.#} beats a minute.";
        return imported;
    }

    public void ExportMidi(string path)
    {
        MidiFile.Write(_document, path);
        _statusLabel.Text = "Exported " + Path.GetFileName(path) + " (MIDI format 1).";
    }

    private void ImportMidiWithDialog()
    {
        using OpenFileDialog dialog = new() { Title = "Import MIDI", Filter = "MIDI files (*.mid;*.midi)|*.mid;*.midi|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { ImportMidi(dialog.FileName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { MessageBox.Show(FindForm(), exception.Message, "Import MIDI", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ExportMidiWithDialog()
    {
        using SaveFileDialog dialog = new() { Title = "Export MIDI", Filter = "MIDI files (*.mid)|*.mid", DefaultExt = "mid",
            FileName = SynthSounds.FileStem(ResourceDisplayName.Format(ResourcePath)) + ".mid" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { ExportMidi(dialog.FileName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { MessageBox.Show(FindForm(), exception.Message, "Export MIDI", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>
    /// Renders the whole piece into its Audio resource (made the first time, in the Audio folder) and
    /// saves the piece, so a game's PlaySound of that name plays this music.
    /// </summary>
    public MusicRenderOutcome RenderToAudio()
    {
        Stop();
        if (LoadWarning is not null) throw new InvalidDataException("The unreadable music file was kept as it was: " + LoadWarning);
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        MusicRenderOutcome outcome = MusicRenderer.RenderToAudio(resources, ResourcePath, _document, ProjectAssetWriteRegistry.MarkLocalWrite);
        Save();
        _statusLabel.Text = $"Rendered {outcome.Seconds:0.#} s into the Audio resource '{outcome.AudioName}'. "
            + $"A game plays it with PlaySound({System.Text.Json.JsonSerializer.Serialize(outcome.AudioName)}, 1, 1, true).";
        return outcome;
    }

    private void RenderToAudioReporting()
    {
        try { RenderToAudio(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                           or InvalidOperationException or ArgumentException)
        { _statusLabel.Text = "Could not render: " + exception.Message; }
    }

    private void OpenRenderedAudio()
    {
        string path = string.IsNullOrWhiteSpace(_document.Audio) ? string.Empty
            : ResourceNames.Resolve(ProjectRoot, _document.Audio, ResourceType.Audio);
        if (path.Length == 0) { _statusLabel.Text = "Render to Audio first: it makes the Audio resource."; return; }
        RequestOpenLinkedResource(path);
    }

    // ── Showing the document ────────────────────────────────────────────────

    private void RefreshAll()
    {
        _trackIndex = Math.Clamp(_trackIndex, 0, Math.Max(0, _document.Tracks.Count - 1));
        _selection.RemoveWhere(note => !SelectedTrack.Notes.Contains(note));
        RebuildTrackRows();
        SyncFields();
        _roll.RefreshScroll();
        _roll.Invalidate();
    }

    private void RefreshSelection()
    {
        SyncFields();
        _roll.Invalidate();
    }

    private void SyncFields()
    {
        _syncing = true;
        try
        {
            _tempo.Value = Math.Clamp((decimal)_document.Tempo, _tempo.Minimum, _tempo.Maximum);
            string signature = _document.BeatsPerBar + "/" + _document.BeatUnit;
            if (!_signature.Items.Contains(signature)) _signature.Items.Add(signature);
            _signature.SelectedItem = signature;
            _snapChoice.SelectedIndex = (int)_snap;
            _loopButton.Checked = LoopEnabled;
            MusicTrack track = SelectedTrack;
            if (!_trackName.Focused) _trackName.Text = track.Name;
            if (!_instrument.Items.Contains(track.Instrument)) _instrument.Items.Add(track.Instrument);
            _instrument.SelectedItem = track.Instrument;
            SynthLayer? own = OwnLayer(track.Instrument);
            _attack.Value = Math.Clamp((decimal)(track.Attack ?? own?.Attack ?? 0), _attack.Minimum, _attack.Maximum);
            _release.Value = Math.Clamp((decimal)(track.Release ?? own?.Release ?? 0), _release.Minimum, _release.Maximum);
            _filter.Value = Math.Clamp((decimal)(track.Filter ?? own?.LowPass ?? 0), _filter.Minimum, _filter.Maximum);
            _velocity.Enabled = _selection.Count > 0;
            _velocity.Value = _selection.Count > 0 ? _selection.First().Velocity : NewNoteVelocity;
            UpdateTimeLabel();
            if (!IsPlaying)
                _statusLabel.Text = $"{_document.Tracks.Count} track{(_document.Tracks.Count == 1 ? string.Empty : "s")} · "
                    + $"{_document.Tracks.Sum(t => t.Notes.Count)} notes · {_document.SecondsAt(_document.EndTick):0.0} s"
                    + (_selection.Count > 0 ? $" · {_selection.Count} selected" : string.Empty);
        }
        finally { _syncing = false; }
    }

    // The values the instrument is made with, shown when the track keeps its instrument's own sound.
    private static SynthLayer? OwnLayer(string instrument)
    {
        string name = MusicInstruments.Kind(instrument) == MusicInstrumentKind.Drums ? "Snare Drum" : instrument;
        return SoundSynth.Presets.TryGetValue(name, out var make) ? make().FirstOrDefault() : null;
    }

    private sealed record TrackRowControls(Panel Row, Label Name, Label Instrument, CheckBox Mute, CheckBox Solo, TrackBar Volume);

    private readonly List<TrackRowControls> _rows = [];

    // Rows are made when the number of tracks changes and refreshed in place otherwise: a mute box
    // that removed itself while reporting its own click would be used after it was disposed.
    private void RebuildTrackRows()
    {
        if (_rows.Count != _document.Tracks.Count)
        {
            _trackRows.SuspendLayout();
            try
            {
                foreach (TrackRowControls row in _rows) row.Row.Dispose();
                _rows.Clear();
                for (int index = 0; index < _document.Tracks.Count; index++)
                {
                    TrackRowControls row = TrackRow(index);
                    _rows.Add(row);
                    _trackRows.Controls.Add(row.Row);
                }
            }
            finally { _trackRows.ResumeLayout(); }
        }
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            for (int index = 0; index < _rows.Count; index++)
            {
                MusicTrack track = _document.Tracks[index];
                TrackRowControls row = _rows[index];
                bool selected = index == _trackIndex;
                row.Row.BackColor = row.Volume.BackColor = selected ? EditorChrome.AccentSoft : EditorChrome.Raised;
                row.Name.Text = track.Name;
                row.Name.Font = selected ? EditorChrome.HeadingFont : EditorChrome.BaseFont;
                row.Instrument.Text = track.Instrument;
                row.Mute.Checked = track.Mute;
                row.Solo.Checked = track.Solo;
                row.Volume.Value = Math.Clamp((int)Math.Round(track.Volume * 100), 0, 100);
            }
        }
        finally { _syncing = syncing; }
        LayoutSide();
    }

    private TrackRowControls TrackRow(int index)
    {
        Panel row = new() { Height = 58, Name = "MusicTrackRow" + index, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(6, 4, 6, 2), Cursor = Cursors.Hand };
        Label name = new() { AutoSize = false, Height = 20, Dock = DockStyle.Top, ForeColor = EditorChrome.Text, UseMnemonic = false, AutoEllipsis = true };
        Label instrument = new() { AutoSize = false, Height = 16, Dock = DockStyle.Top, ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont, UseMnemonic = false, AutoEllipsis = true };
        FlowLayoutPanel controls = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        CheckBox mute = new() { Text = "M", AutoSize = true, ForeColor = EditorChrome.Text, Name = "MusicMute" + index, Margin = new Padding(0, 0, 4, 0) };
        CheckBox solo = new() { Text = "S", AutoSize = true, ForeColor = EditorChrome.Text, Name = "MusicSolo" + index, Margin = new Padding(0, 0, 4, 0) };
        TrackBar volume = new() { AutoSize = false, Height = 20, Width = 120, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Name = "MusicVolume" + index };
        mute.CheckedChanged += (_, _) => { if (!_syncing) SetMute(index, mute.Checked); };
        solo.CheckedChanged += (_, _) => { if (!_syncing) SetSolo(index, solo.Checked); };
        volume.MouseUp += (_, _) => SetTrackVolume(index, volume.Value / 100f);
        volume.KeyUp += (_, _) => SetTrackVolume(index, volume.Value / 100f);
        controls.Controls.AddRange([mute, solo, volume]);
        row.Controls.Add(controls);
        row.Controls.Add(instrument);
        row.Controls.Add(name);
        void Choose(object? sender, EventArgs e) => SelectedTrackIndex = index;
        row.Click += Choose; name.Click += Choose; instrument.Click += Choose; controls.Click += Choose;
        return new TrackRowControls(row, name, instrument, mute, solo, volume);
    }

    private void LayoutSide()
    {
        int width = Math.Max(160, _side.ClientSize.Width - _side.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (Control control in _side.Controls)
        {
            control.Width = width;
            if (control is Label { AutoSize: false } label && label.Tag is string)
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
        }
        foreach (Control row in _trackRows.Controls)
        {
            row.Width = width;
            foreach (TrackBar bar in row.Controls.OfType<FlowLayoutPanel>().SelectMany(flow => flow.Controls.OfType<TrackBar>()))
                bar.Width = Math.Max(60, width - 110);
        }
    }

    private static Label SideText(string text, bool heading = false) => new()
    {
        Text = text, AutoSize = false, Tag = heading ? "heading" : "text", UseMnemonic = false,
        ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted, Font = heading ? EditorChrome.HeadingFont : EditorChrome.SmallFont,
        Margin = new Padding(0, 0, 0, 8),
    };

    private static Button SideButton(string text, string name, Action click)
    {
        Button button = new() { Text = text, Name = name, Height = 30, Margin = new Padding(0, 0, 0, 4) };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => click();
        return button;
    }

    private static Panel SideRow(string caption, Control field)
    {
        TableLayoutPanel row = new() { Height = 32, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont }, 0, 0);
        field.Dock = DockStyle.Fill;
        row.Controls.Add(field, 1, 0);
        return row;
    }

    private static NumericUpDown MakeNumeric(decimal minimum, decimal maximum, decimal step, int decimals, string name)
    {
        NumericUpDown input = new() { Minimum = minimum, Maximum = maximum, Increment = step, DecimalPlaces = decimals, Name = name };
        EditorChrome.StyleField(input);
        return input;
    }

    // ── Keys ────────────────────────────────────────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool typing = ActiveControl is TextBoxBase or NumericUpDown or ComboBox
            || (ActiveControl is ContainerControl container && container.ActiveControl is TextBoxBase or NumericUpDown);
        if (!typing)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            }
        }
        if (_roll.Focused)
        {
            long step = SnapTicks;
            switch (keyData)
            {
                case Keys.Delete or Keys.Back: DeleteSelected(); return true;
                case Keys.Control | Keys.C: CopySelected(); return true;
                case Keys.Control | Keys.X: CutSelected(); return true;
                case Keys.Control | Keys.V: Paste(SnapFloor(PlayheadTick)); return true;
                case Keys.Control | Keys.A: SelectAll(); return true;
                case Keys.Up: MoveSelected(0, 1); return true;
                case Keys.Down: MoveSelected(0, -1); return true;
                case Keys.Shift | Keys.Up: MoveSelected(0, 12); return true;
                case Keys.Shift | Keys.Down: MoveSelected(0, -12); return true;
                case Keys.Left: MoveSelected(-step, 0); return true;
                case Keys.Right: MoveSelected(step, 0); return true;
                case Keys.Space: if (IsPlaying) Stop(); else Play(); return true;
                case Keys.Escape: ClearSelection(); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        RefreshAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeTransport();
        base.Dispose(disposing);
    }
}

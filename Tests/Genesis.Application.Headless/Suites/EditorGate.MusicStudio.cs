using System.Diagnostics;
using System.Windows.Forms;
using Genesis.Application.Core.Audio;
using Genesis.Application.Core.Audio.Music;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.UI;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Audio;
using Genesis.Runtime;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    // ── Music: the Audio editor's MIDI studio ───────────────────────────────────────────────────────

    private static (int Pitch, long Start, long Length, int Velocity)[] NotesOf(MusicTrack track) =>
        [.. track.Notes.Select(note => (note.Pitch, note.Start, note.Length, note.Velocity)).OrderBy(note => note.Start).ThenBy(note => note.Pitch)];

    private static string Describe(IEnumerable<(int Pitch, long Start, long Length, int Velocity)> notes) =>
        string.Join(" ", notes.Take(8).Select(note => $"{note.Pitch}@{note.Start}+{note.Length}v{note.Velocity}"));

    // A piece with what a round trip must keep: one key played again as it ends, overlapping notes,
    // the lowest and highest keys and velocities, a tempo change, 3/4 time and three instruments, one
    // of them drums.
    private static MusicDocument RoundTripPiece()
    {
        MusicDocument piece = new() { TicksPerBeat = 480, Tempo = 96.5, BeatsPerBar = 3, BeatUnit = 4,
            TempoChanges = [new MusicTempoChange { Tick = 1920, Bpm = 140 }] };
        MusicTrack melody = new() { Name = "Melody", Instrument = "Lead" };
        melody.Notes.AddRange([
            new MusicNote { Pitch = 72, Start = 0, Length = 480, Velocity = 127 },
            new MusicNote { Pitch = 72, Start = 480, Length = 240, Velocity = 1 },
            new MusicNote { Pitch = 74, Start = 600, Length = 960, Velocity = 64 },
            new MusicNote { Pitch = 76, Start = 720, Length = 120, Velocity = 90 },
            new MusicNote { Pitch = 76, Start = 1560, Length = 1, Velocity = 100 },
            new MusicNote { Pitch = 0, Start = 3000, Length = 7, Velocity = 50 },
            new MusicNote { Pitch = 127, Start = 3000, Length = 4800, Velocity = 51 }]);
        MusicTrack bass = new() { Name = "Bass line", Instrument = "Bass", Program = 33, Volume = 0.5f };
        for (int index = 0; index < 8; index++) bass.Notes.Add(new MusicNote { Pitch = 36 + index % 3 * 5, Start = index * 480L, Length = 400, Velocity = 80 + index });
        MusicTrack drums = new() { Name = "Kit", Instrument = MusicInstruments.DrumKit };
        for (int index = 0; index < 6; index++) drums.Notes.Add(new MusicNote { Pitch = index % 2 == 0 ? 36 : 38, Start = index * 480L, Length = 120, Velocity = 110 });
        piece.Tracks.AddRange([melody, bass, drums]);
        piece.Normalize();
        return piece;
    }

    // A format 0 file as another program writes it: one track holding two channels, running status,
    // note-offs written as note-ons of velocity 0, a tempo, a track name and a program change.
    private static byte[] OtherProgramFormatZero()
    {
        List<byte> body =
        [
            0x00, 0xFF, 0x03, 0x05, (byte)'S', (byte)'o', (byte)'n', (byte)'g', (byte)'1',
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, // 500,000 microseconds a beat: 120 beats a minute
            0x00, 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08,
            0x00, 0xC0, 0x18, // channel 1: program 24, a nylon guitar
            0x00, 0x90, 0x3C, 0x50, // C4 on
            0x60, 0x3C, 0x00, // 96 ticks later C4 off (running status, velocity 0)
            0x00, 0x40, 0x46, // E4 on (running status)
            0x00, 0x99, 0x24, 0x64, // channel 10: kick on
            0x30, 0x24, 0x00, // kick off
            0x30, 0x90, 0x40, 0x00, // E4 off
            0x00, 0xFF, 0x2F, 0x00,
        ];
        List<byte> file = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 0, 48, .. "MTrk"u8.ToArray(),
            (byte)(body.Count >> 24), (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count, .. body];
        return [.. file];
    }

    private static void MusicStudioWorkflow(HeadlessContext ctx)
    {
        string parent = Path.Combine(ctx.Workspace, "MusicStudio");
        Directory.CreateDirectory(parent);
        Genesis.Application.Core.Projects.ProjectSession project =
            new Genesis.Application.Core.Projects.ProjectService().CreateProject(parent, "Music Studio", "Blank");
        ResourceService resources = new(project);

        HeadlessHarness.RunCase(ctx.Report, "Editor.Music.Midi.WriterAndReaderRoundTripNotesTimesVelocitiesAndTempo", () =>
        {
            MusicDocument piece = RoundTripPiece();
            byte[] bytes = MidiFile.Write(piece);
            HeadlessHarness.Step("the file is a format 1 Standard MIDI File with a tempo track and one track per part", () =>
            {
                HeadlessHarness.Assert(bytes.Length > 14 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "MThd"
                    && bytes[9] == 1 && (bytes[10] << 8 | bytes[11]) == 4 && (bytes[12] << 8 | bytes[13]) == 480,
                    "The written file is not format 1 with four tracks at 480 ticks a beat.");
            });
            MusicDocument back = MidiFile.Read(bytes);
            HeadlessHarness.Step("reading it back gives the same notes, times, velocities, tempo and time signature", () =>
            {
                HeadlessHarness.Assert(back.TicksPerBeat == 480 && Math.Abs(back.Tempo - 96.5) < 0.01 && back.BeatsPerBar == 3 && back.BeatUnit == 4,
                    $"Timing came back as {back.TicksPerBeat} ticks, {back.Tempo} bpm, {back.BeatsPerBar}/{back.BeatUnit}.");
                HeadlessHarness.Assert(back.TempoChanges.Count == 1 && back.TempoChanges[0].Tick == 1920 && Math.Abs(back.TempoChanges[0].Bpm - 140) < 0.01,
                    "The tempo change did not come back: " + string.Join(", ", back.TempoChanges.Select(change => $"{change.Tick}:{change.Bpm}")));
                HeadlessHarness.Assert(back.Tracks.Count == 3, $"{back.Tracks.Count} tracks came back, not 3.");
                for (int index = 0; index < 3; index++)
                {
                    MusicTrack written = piece.Tracks[index], read = back.Tracks[index];
                    HeadlessHarness.Assert(read.Name == written.Name && read.Instrument == written.Instrument,
                        $"Track {index} came back as '{read.Name}' on {read.Instrument}, not '{written.Name}' on {written.Instrument}.");
                    HeadlessHarness.Assert(NotesOf(read).SequenceEqual(NotesOf(written)),
                        $"Track '{written.Name}' notes changed: wrote {Describe(NotesOf(written))}, read {Describe(NotesOf(read))}.");
                }
                HeadlessHarness.Assert(Math.Abs(back.Tracks[1].Volume - 0.5f) < 0.01f && back.Tracks[1].Program == 33 && back.Tracks[2].Channel == 9,
                    "Volume, program or the drum channel did not come back.");
                HeadlessHarness.Assert(Math.Abs(back.SecondsAt(3840) - piece.SecondsAt(3840)) < 0.001, "The tempo map plays at another speed after the round trip.");
            });
            HeadlessHarness.Step("a format 0 file from another program imports with a track per channel", () =>
            {
                MusicDocument other = MidiFile.Read(OtherProgramFormatZero());
                HeadlessHarness.Assert(other.Tracks.Count == 2, $"The format 0 file gave {other.Tracks.Count} tracks, not 2 (one per channel).");
                MusicTrack guitar = other.Tracks[0], kit = other.Tracks[1];
                HeadlessHarness.Assert(guitar.Instrument == "Pluck" && guitar.Channel == 0 && kit.Instrument == MusicInstruments.DrumKit && kit.Channel == 9,
                    $"Instruments came in as {guitar.Instrument} (channel {guitar.Channel}) and {kit.Instrument} (channel {kit.Channel}).");
                HeadlessHarness.Assert(other.TicksPerBeat == 480 && Math.Abs(other.Tempo - 120) < 0.01,
                    $"The 48-tick file was not brought to 480 ticks at 120 bpm: {other.TicksPerBeat}, {other.Tempo}.");
                // A file tick is ten here: C4 for two beats, then E4 for two and the kick for one.
                HeadlessHarness.Assert(NotesOf(guitar).SequenceEqual([(60, 0L, 960L, 80), (64, 960L, 960L, 70)]) && NotesOf(kit).SequenceEqual([(36, 960L, 480L, 100)]),
                    $"Running status or velocity-0 note-offs were misread: {Describe(NotesOf(guitar))} | {Describe(NotesOf(kit))}.");
                HeadlessHarness.Assert(guitar.Name.StartsWith("Song1", StringComparison.Ordinal), "The track name was not read: " + guitar.Name);
            });
            HeadlessHarness.Step("damaged files are refused with a reason, not a crash", () =>
            {
                foreach (byte[] damaged in new[] { "RIFF0000"u8.ToArray(), OtherProgramFormatZero()[..30], Array.Empty<byte>() })
                {
                    bool refused = false;
                    try { MidiFile.Read(damaged); }
                    catch (InvalidDataException exception) { refused = exception.Message.Length > 0; }
                    HeadlessHarness.Assert(refused, "A damaged MIDI file was not refused with InvalidDataException.");
                }
            });
            HeadlessHarness.Step("a MIDI file already on this computer reads, if there is one", () =>
            {
                string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                string? found = Directory.Exists(media) ? Directory.EnumerateFiles(media, "*.mid").FirstOrDefault() : null;
                if (found is null) return;
                MusicDocument existing = MidiFile.Read(found);
                HeadlessHarness.Assert(existing.Tracks.Count > 0 && existing.Tracks.Sum(track => track.Notes.Count) > 0, $"{found} read with no notes.");
            });
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Music.Instruments.PlayAtTheNotesKeyLoudnessAndTrackChoices", () =>
        {
            float[] One(string instrument, int pitch, int velocity = 100, double? attack = null, double? filter = null)
            {
                MusicDocument single = new() { Tracks = [new MusicTrack { Instrument = instrument, Attack = attack, Filter = filter,
                    Notes = [new MusicNote { Pitch = pitch, Start = 0, Length = 480, Velocity = velocity }] }] };
                return MusicRenderer.Render(single);
            }
            static int Crossings(float[] samples, int count)
            {
                int changes = 0;
                for (int index = 1; index < Math.Min(count, samples.Length); index++) if ((samples[index] >= 0) != (samples[index - 1] >= 0)) changes++;
                return changes;
            }
            static float Peak(IEnumerable<float> samples) => samples.Max(sample => Math.Abs(sample));
            HeadlessHarness.Step("each built-in instrument is a synthesiser preset and plays an octave up at twice the pitch", () =>
            {
                foreach (string instrument in MusicInstruments.BuiltIn.Where(name => name != MusicInstruments.DrumKit))
                {
                    HeadlessHarness.Assert(SoundSynth.Presets.ContainsKey(instrument) && SynthSounds.Info(instrument).Category == "Instruments",
                        $"{instrument} is not an Instruments preset of the synthesiser.");
                    // The filter is held open, so the sound is the same shape at both keys (it follows the key half way).
                    int window = SoundSynth.SampleRate / 4;
                    int low = Crossings(One(instrument, 57, filter: 20000), window), high = Crossings(One(instrument, 69, filter: 20000), window);
                    HeadlessHarness.Assert(low > 20 && high > low * 1.7 && high < low * 2.3,
                        $"{instrument}: A3 crossed zero {low} times in a quarter second and A4 {high}; an octave should double it.");
                }
            });
            HeadlessHarness.Step("velocity sets loudness, attack is the track's own, mute silences", () =>
            {
                float loud = Peak(One("Organ", 60, 127)), soft = Peak(One("Organ", 60, 40));
                HeadlessHarness.Assert(loud > 0.05f && Math.Abs(loud / soft - 127f / 40f) < 0.15f, $"Velocity 127 peaked at {loud:0.000} and 40 at {soft:0.000}.");
                float[] quick = One("Organ", 60), slow = One("Organ", 60, attack: 0.4);
                int early = SoundSynth.SampleRate / 20;
                HeadlessHarness.Assert(Peak(quick.Take(early)) > Peak(slow.Take(early)) * 3,
                    "A track's 0.4 s attack did not make the note's start quieter.");
                HeadlessHarness.Assert(MusicRenderer.AudibleNotes(new MusicDocument { Tracks = [new MusicTrack { Mute = true, Notes = [new MusicNote()] }] }) == 0,
                    "A muted track's note would still play.");
            });
            HeadlessHarness.Step("solo plays only the soloed track; the drum kit plays a drum for each key", () =>
            {
                MusicDocument both = RoundTripPiece();
                both.Tracks[1].Solo = true;
                MusicDocument alone = RoundTripPiece();
                alone.Tracks[0].Mute = alone.Tracks[2].Mute = true;
                HeadlessHarness.Assert(MusicRenderer.Render(both).SequenceEqual(MusicRenderer.Render(alone)), "Solo did not play the soloed track alone.");
                float[] kick = One(MusicInstruments.DrumKit, 36), snare = One(MusicInstruments.DrumKit, 38), hat = One(MusicInstruments.DrumKit, 42);
                HeadlessHarness.Assert(Peak(kick) > 0.05f && Peak(snare) > 0.05f && Peak(hat) > 0.05f && kick.Length != snare.Length && Crossings(hat, hat.Length) > Crossings(kick, kick.Length) * 4,
                    "The drum kit did not play a low kick, a snare and a bright hi-hat for keys 36, 38 and 42.");
            });
            HeadlessHarness.Step("a minute of four busy tracks renders in seconds and the same piece makes the same samples", () =>
            {
                MusicDocument busy = new() { Tempo = 120 };
                foreach (string instrument in new[] { "Piano", "Bass", "Pad", MusicInstruments.DrumKit })
                {
                    MusicTrack track = new() { Instrument = instrument };
                    for (int index = 0; index < 240; index++) track.Notes.Add(new MusicNote { Pitch = instrument == MusicInstruments.DrumKit ? 36 + index % 8 : 48 + index % 24, Start = index * 240L, Length = 240, Velocity = 90 });
                    busy.Tracks.Add(track);
                }
                Stopwatch clock = Stopwatch.StartNew();
                float[] first = MusicRenderer.Render(busy);
                clock.Stop();
                HeadlessHarness.Assert(first.Length >= SoundSynth.SampleRate * 60 && clock.Elapsed.TotalSeconds < 20,
                    $"960 notes over a minute took {clock.Elapsed.TotalSeconds:0.0} s to render ({first.Length} samples).");
                HeadlessHarness.Assert(Peak(first) <= 0.951f && MusicRenderer.Render(busy).SequenceEqual(first), "The mix clipped or a second render differed.");
            });
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Music.Studio.NewPieceTwoTracksPlayRenderSaveReopenAndPlaySound", () =>
        {
            string path = string.Empty;
            HeadlessHarness.Step("a Music resource is made in its own folder from the template, with its icon", () =>
            {
                path = resources.CreateResource(project.AssetsPath, ResourceKind.Music, "Theme Song");
                HeadlessHarness.Assert(path.EndsWith(".music.json", StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(path)) == "Music"
                    && ResourceNames.Resolve(project.RootPath, "Theme Song", ResourceType.Music) == path,
                    "The Music resource was not made in Assets/Music under its name: " + path);
                HeadlessHarness.Assert(ResourceKindVisuals.Glyph(ResourceKind.Music) == UiGlyphs.MusicNote && ResourceDefinitions.FromPath(path)?.Kind == ResourceKind.Music,
                    "The Music kind has no icon of its own or is not recognised by its extension.");
                MusicDocument template = MusicDocumentSerializer.Load(path);
                HeadlessHarness.Assert(template.Tracks.Count == 2 && template.Tracks.All(track => track.Notes.Count > 0), "The template is not a piano and drums with a bar to play.");
            });
            Form host = GateSuite.NewHost(1280, 760);
            MusicEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 30);
            MusicTrack piano = editor.Document.Tracks[0];
            MusicTrack bass = null!;
            (int Pitch, long Start, long Length, int Velocity)[] pianoNotes = [], bassNotes = [];
            try
            {
                HeadlessHarness.Step("notes are added on two tracks with snapping, and a track gets an instrument, volume and its own release", () =>
                {
                    editor.SetSnap(MusicSnap.Beat);
                    HeadlessHarness.Assert(editor.SnapTicks == 480 && editor.SnapRound(500) == 480, "Beat snapping is not a quarter note in 4/4.");
                    editor.SelectedTrackIndex = 0;
                    editor.AddNote(67, 1920, 960, 90);
                    editor.AddNote(71, 2880, 480, 70);
                    bass = editor.AddTrack("Bass");
                    HeadlessHarness.Assert(editor.SelectedTrack == bass && editor.Document.Tracks.Count == 3, "Add track did not add and select a Bass track.");
                    foreach (int bar in Enumerable.Range(0, 2)) editor.AddNote(36 + bar * 7, bar * 1920L, 1800, 100);
                    editor.SetTrackVolume(2, 0.6f);
                    editor.SetTrackSound(release: 0.3);
                    HeadlessHarness.Assert(bass.Notes.Count == 2 && Math.Abs(bass.Volume - 0.6f) < 0.001f && bass.Release == 0.3 && piano.Notes.Count == 6,
                        "The notes, volume or release were not set on the right tracks.");
                    pianoNotes = NotesOf(piano); bassNotes = NotesOf(bass);
                });
                HeadlessHarness.Step("Play renders the piece and runs the play-head; Loop plays exactly the loop range", () =>
                {
                    editor.PlayheadTick = 0;
                    ToolStripButton play = SurfaceControls(editor).OfType<ToolStrip>().Single(strip => strip.Name == "MusicTransport").Items.OfType<ToolStripButton>().Single(item => item.Text == "Play");
                    play.PerformClick();
                    // The last note (the bass's second, 3,720 ticks) ends at 3.9 s at 120 beats a minute, then fades.
                    HeadlessHarness.Assert(editor.IsPlaying && editor.LastPlayedSamples > SoundSynth.SampleRate * 3.8, $"Play rendered {editor.LastPlayedSamples} samples: {editor.StatusText}");
                    GateSuite.Pump(10, 30);
                    HeadlessHarness.Assert(editor.PlayheadTick > 0, "The play-head did not move while playing: " + editor.StatusText);
                    editor.Stop();
                    editor.SetLoopRange(1920, 3840);
                    HeadlessHarness.Assert(editor.LoopEnabled, "Setting a loop range did not turn Loop on.");
                    editor.Play();
                    int bar = (int)Math.Ceiling(2.0 * SoundSynth.SampleRate);
                    HeadlessHarness.Assert(Math.Abs(editor.LastPlayedSamples - bar) <= 1, $"The loop of one bar at 120 bpm rendered {editor.LastPlayedSamples} samples, not {bar}.");
                    editor.Stop();
                    editor.SetLoop(false);
                    HeadlessHarness.Assert(!editor.IsPlaying && !editor.LoopEnabled, "Stop or Loop off did not take.");
                    string capture = "music-studio.png";
                    editor.SelectedTrackIndex = 0;
                    editor.SelectNotes(piano.Notes.Where(note => note.Pitch == 67));
                    ctx.Report.Images.Add(ImageResult.From("Music studio (Audio editor, Music mode)", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
                });
                MusicRenderOutcome outcome = null!;
                HeadlessHarness.Step("Render to Audio makes an Audio resource with the music's WAV and saves the piece", () =>
                {
                    outcome = editor.RenderToAudio();
                    HeadlessHarness.Assert(outcome.Created && outcome.AudioName == "Theme Song Audio" && File.Exists(outcome.AudioPath)
                        && Path.GetFileName(Path.GetDirectoryName(outcome.AudioPath)) == "Audio" && File.Exists(outcome.WavPath),
                        $"Render made '{outcome.AudioName}' at {outcome.AudioPath}.");
                    AudioAssetSettings settings = AudioAssetSettings.Load(outcome.AudioPath) ?? throw new InvalidOperationException("The rendered Audio resource cannot be read.");
                    HeadlessHarness.Assert(settings.Bus == "music" && settings.Loop && ResourceNames.ResolveFile(project.RootPath, settings.Source!, ResourceType.Audio) == outcome.WavPath,
                        $"The Audio resource is not music that repeats, playing the render: bus {settings.Bus}, loop {settings.Loop}, source {settings.Source}.");
                    PcmAudioClip clip = PcmAudioClip.Load(outcome.WavPath);
                    HeadlessHarness.Assert(clip.SampleRate == SoundSynth.SampleRate && clip.Duration > 3.8 && clip.Samples.Max(sample => Math.Abs((int)sample)) > 3000,
                        $"The render is {clip.Duration:0.00} s, peak {clip.Samples.Max(sample => Math.Abs((int)sample))}.");
                    HeadlessHarness.Assert(!editor.IsDirty && editor.Document.Audio == outcome.AudioName, "Render did not save the piece with its Audio resource's name.");
                });
                HeadlessHarness.Step("the saved piece reopens with its tracks, instruments, notes and Audio resource", () =>
                {
                    using MusicEditorControl reopened = new(path, project.RootPath);
                    MusicDocument document = reopened.Document;
                    HeadlessHarness.Assert(reopened.LoadWarning is null && !reopened.IsDirty && document.Tracks.Count == 3
                        && document.Tracks.Select(track => track.Instrument).SequenceEqual(["Piano", MusicInstruments.DrumKit, "Bass"])
                        && document.Tracks[2].Release == 0.3 && Math.Abs(document.Tracks[2].Volume - 0.6f) < 0.001f && document.Audio == outcome.AudioName,
                        "The tracks, instruments or Audio resource did not reopen: " + string.Join(", ", document.Tracks.Select(track => track.Name + "/" + track.Instrument)));
                    HeadlessHarness.Assert(NotesOf(document.Tracks[0]).SequenceEqual(pianoNotes) && NotesOf(document.Tracks[2]).SequenceEqual(bassNotes),
                        "The notes changed through save and reopen.");
                });
                HeadlessHarness.Step("a second render updates the same Audio resource, which plays in a game through PlaySound", () =>
                {
                    double before = PcmAudioClip.Load(outcome.WavPath).Duration;
                    editor.SelectedTrackIndex = 2;
                    editor.AddNote(43, 3840 * 2, 1920, 100);
                    MusicRenderOutcome again = editor.RenderToAudio();
                    HeadlessHarness.Assert(!again.Created && again.AudioPath == outcome.AudioPath && again.WavPath == outcome.WavPath
                        && PcmAudioClip.Load(again.WavPath).Duration > before + 1,
                        "A second render did not replace the same Audio resource's WAV with the longer piece.");
                    using XAudioSystem audio = new(project.RootPath);
                    using RuntimeScene scene = new("Music gameplay") { Input = new InputState() };
                    RoomAsset room = RoomAsset.Create("Music Room", RoomDimension.TwoD);
                    ProjectGameContext game = new(project.RootPath, scene, null, null, room, null, audio);
                    var oldGame = PgslCommands.ActiveGameContext;
                    PgslCommands.ActiveGameContext = game;
                    try
                    {
                        double channel = PgslCommands.PlaySound(outcome.AudioName, 1, 1, false);
                        HeadlessHarness.Assert(channel > 0 && audio.IsPlaying(new AudioChannel((int)channel)), $"PlaySound(\"{outcome.AudioName}\") did not play: channel {channel}.");
                        PgslCommands.StopSound(channel);
                    }
                    finally { PgslCommands.ActiveGameContext = oldGame; }
                });
            }
            finally
            {
                editor.Stop();
                host.Close();
                host.Dispose();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Music.Studio.TheAudioEditorMakesMusicThatRendersIntoItsSound", () =>
        {
            string sound = resources.CreateResource(project.AssetsPath, ResourceKind.Audio, "Menu Theme");
            using AudioEditorControl audioEditor = new(sound, project.RootPath);
            string? opened = null;
            audioEditor.OpenLinkedResourceRequested += (_, path) => opened = path;
            string music = audioEditor.CreateMusicForThisSound();
            MusicDocument piece = MusicDocumentSerializer.Load(music);
            HeadlessHarness.Assert(opened == music && Path.GetFileName(Path.GetDirectoryName(music)) == "Music" && piece.Audio == "Menu Theme"
                && ResourceNames.Name(project.RootPath, music, ResourceType.Music) == "Menu Theme Music",
                $"Make music for this sound did not make and open 'Menu Theme Music' writing to the sound: {music}, audio '{piece.Audio}'.");
            MusicRenderOutcome outcome = MusicRenderer.RenderToAudio(resources, music, piece);
            AudioAssetSettings settings = AudioAssetSettings.Load(sound) ?? throw new InvalidOperationException("The sound cannot be read after the render.");
            HeadlessHarness.Assert(!outcome.Created && outcome.AudioPath == sound && settings.Bus == "sfx"
                && ResourceNames.ResolveFile(project.RootPath, settings.Source!, ResourceType.Audio) == outcome.WavPath,
                "The render did not go into the sound the music was made for, keeping the sound's own settings.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Music.Studio.NoteEditsUndoAndRedoAndMidiImportExport", () =>
        {
            string path = resources.CreateResource(project.AssetsPath, ResourceKind.Music, "Undo Tune");
            Form host = GateSuite.NewHost(1100, 700);
            MusicEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(4, 20);
            try
            {
                HeadlessHarness.Step("add, move, resize, velocity, copy, paste and delete each undo and redo in one step", () =>
                {
                    editor.SelectedTrackIndex = 0;
                    MusicTrack track = editor.SelectedTrack;
                    var start = NotesOf(track);
                    MusicNote note = editor.AddNote(60, 3840, 480, 100);
                    var added = NotesOf(track);
                    editor.MoveSelected(240, 2);
                    var moved = NotesOf(track);
                    HeadlessHarness.Assert(note.Pitch == 62 && note.Start == 4080, "Move did not shift the note two keys and an eighth.");
                    editor.ResizeSelected(480);
                    var resized = NotesOf(track);
                    editor.SetSelectedVelocity(20);
                    var soft = NotesOf(track);
                    editor.CopySelected();
                    IReadOnlyList<MusicNote> pasted = editor.Paste(5760);
                    var withPaste = NotesOf(track);
                    HeadlessHarness.Assert(pasted.Count == 1 && pasted[0].Start == 5760 && pasted[0].Velocity == 20 && pasted[0].Length == 960, "Paste did not copy the note to the play-head.");
                    editor.DeleteSelected();
                    var deleted = NotesOf(track);
                    HeadlessHarness.Assert(deleted.SequenceEqual(soft), "Delete did not remove just the pasted note.");
                    var history = new[] { start, added, moved, resized, soft, withPaste, deleted };
                    for (int step = history.Length - 2; step >= 0; step--)
                    {
                        editor.Undo();
                        HeadlessHarness.Assert(NotesOf(editor.SelectedTrack).SequenceEqual(history[step]), $"Undo to step {step} gave {Describe(NotesOf(editor.SelectedTrack))}.");
                    }
                    for (int step = 1; step < history.Length; step++)
                    {
                        editor.Redo();
                        HeadlessHarness.Assert(NotesOf(editor.SelectedTrack).SequenceEqual(history[step]), $"Redo to step {step} gave {Describe(NotesOf(editor.SelectedTrack))}.");
                    }
                });
                HeadlessHarness.Step("tempo, time signature, instrument, mute and solo are edits too", () =>
                {
                    editor.SetTempo(90);
                    editor.SetTimeSignature(6, 8);
                    editor.SetInstrument("Organ");
                    editor.SetMute(1, true);
                    HeadlessHarness.Assert(editor.Document.Tempo == 90 && editor.Document.TicksPerBar == 1440 && editor.SelectedTrack.Instrument == "Organ" && editor.Document.Tracks[1].Mute,
                        "Tempo, 6/8, instrument or mute did not apply.");
                    for (int step = 0; step < 4; step++) editor.Undo();
                    HeadlessHarness.Assert(editor.Document.Tempo == 120 && editor.Document.BeatsPerBar == 4 && editor.SelectedTrack.Instrument == "Piano" && !editor.Document.Tracks[1].Mute,
                        "Undo did not take back tempo, time signature, instrument and mute.");
                });
                HeadlessHarness.Step("Import MIDI replaces the piece as one undo step, and Export MIDI writes what is shown", () =>
                {
                    string midi = Path.Combine(ctx.Captures, "music-round-trip.mid");
                    MidiFile.Write(RoundTripPiece(), midi);
                    string before = MusicDocumentSerializer.Serialize(editor.Document);
                    editor.ImportMidi(midi);
                    HeadlessHarness.Assert(editor.Document.Tracks.Count == 3 && editor.Document.Tracks[0].Name == "Melody" && Math.Abs(editor.Document.Tempo - 96.5) < 0.01,
                        "Import MIDI did not bring in the file's tracks and tempo: " + editor.StatusText);
                    editor.Undo();
                    HeadlessHarness.Assert(MusicDocumentSerializer.Serialize(editor.Document) == before, "Undo did not take back the import.");
                    editor.Redo();
                    string exported = Path.Combine(ctx.Captures, "music-exported.mid");
                    editor.ExportMidi(exported);
                    MusicDocument written = MidiFile.Read(exported);
                    HeadlessHarness.Assert(written.Tracks.Count == 3 && written.Tracks.Zip(editor.Document.Tracks).All(pair => NotesOf(pair.First).SequenceEqual(NotesOf(pair.Second))),
                        "Export MIDI did not write the piece's notes.");
                    editor.Save();
                    HeadlessHarness.Assert(MusicDocumentSerializer.Load(path).Tracks.Count == 3, "The imported piece did not save.");
                });
                HeadlessHarness.Step("the piano roll's mouse: click places a note, edge-drag resizes, drag moves, the lane sets velocity, right-click deletes", () =>
                {
                    Control roll = SurfaceControls(editor).Single(control => control.Name == "MusicPianoRoll");
                    float X(long tick) => (float)roll.GetType().GetMethod("TickToX")!.Invoke(roll, [tick])!;
                    int Top(int pitch) => (int)roll.GetType().GetMethod("PitchToY")!.Invoke(roll, [pitch])!;
                    int Y(int pitch) => Top(pitch) + (Top(pitch - 1) - Top(pitch)) / 2;
                    void Mouse(string name, float x, int y, MouseButtons button = MouseButtons.Left) =>
                        typeof(Control).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(roll, [new MouseEventArgs(button, 1, (int)x, y, 0)]);
                    editor.SelectedTrackIndex = 0;
                    editor.SetSnap(MusicSnap.Beat);
                    MusicTrack track = editor.SelectedTrack;
                    HeadlessHarness.Assert(X(4800) < roll.ClientSize.Width - 30 && Y(62) > 30 && Y(62) < roll.ClientSize.Height - 100,
                        $"The test's notes are not in view: x {X(4800)}, y {Y(62)} in {roll.ClientSize}.");
                    int notes = track.Notes.Count;
                    Mouse("OnMouseDown", X(3840) + 5, Y(62)); Mouse("OnMouseUp", X(3840) + 5, Y(62));
                    MusicNote placed = track.Notes.Single(note => note.Pitch == 62 && note.Start == 3840);
                    HeadlessHarness.Assert(track.Notes.Count == notes + 1 && placed.Length == 480, $"A click placed {track.Notes.Count - notes} notes, {placed.Length} ticks long.");
                    Mouse("OnMouseDown", X(4320) - 2, Y(62)); Mouse("OnMouseMove", X(4800) + 3, Y(62)); Mouse("OnMouseUp", X(4800) + 3, Y(62));
                    HeadlessHarness.Assert(placed.Length == 960, $"Dragging the right edge a beat on made the note {placed.Length} ticks long.");
                    Mouse("OnMouseDown", X(3840) + 20, Y(62)); Mouse("OnMouseMove", X(4320) + 20, Y(64)); Mouse("OnMouseUp", X(4320) + 20, Y(64));
                    HeadlessHarness.Assert(placed.Start == 4320 && placed.Pitch == 64, $"Dragging moved the note to {placed.Pitch}@{placed.Start}, not 64@4320.");
                    int laneTop = roll.ClientSize.Height - SystemInformation.HorizontalScrollBarHeight - (Top(0) - Top(1)) * 6;
                    Mouse("OnMouseDown", X(4320) + 1, laneTop + 1); Mouse("OnMouseUp", X(4320) + 1, laneTop + 1);
                    HeadlessHarness.Assert(placed.Velocity == 127, $"Clicking the top of the velocity lane set velocity {placed.Velocity}.");
                    Mouse("OnMouseDown", X(4320) + 20, Y(64), MouseButtons.Right);
                    HeadlessHarness.Assert(!track.Notes.Contains(placed) && track.Notes.Count == notes, "Right-click did not delete the note.");
                    for (int step = 0; step < 5; step++) editor.Undo();
                    HeadlessHarness.Assert(editor.SelectedTrack.Notes.Count == notes && !editor.SelectedTrack.Notes.Any(note => note.Pitch is 62 or 64 && note.Start is 3840 or 4320),
                        "Five undos did not take back the place, resize, move, velocity and delete, one step each.");
                });
                HeadlessHarness.Step("an unreadable piece is kept as it was and Save refuses", () =>
                {
                    string broken = Path.Combine(Path.GetDirectoryName(path)!, "Broken.music.json");
                    File.WriteAllText(broken, "{ not music");
                    using MusicEditorControl damaged = new(broken, project.RootPath);
                    bool refused = false;
                    try { damaged.Save(); }
                    catch (InvalidDataException) { refused = true; }
                    HeadlessHarness.Assert(damaged.LoadWarning is not null && refused && File.ReadAllText(broken) == "{ not music",
                        "A damaged music file was overwritten.");
                    File.Delete(broken);
                });
            }
            finally
            {
                editor.Stop();
                host.Close();
                host.Dispose();
            }
        });
    }
}

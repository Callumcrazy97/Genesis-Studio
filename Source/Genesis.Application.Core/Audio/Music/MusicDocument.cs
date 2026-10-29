using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Application.Core.Audio.Music;

/// <summary>One note of a piece: a key, when it starts and how long it lasts (ticks), and how hard it is played.</summary>
public sealed class MusicNote
{
    /// <summary>The key as a MIDI note number: 60 is middle C (C4), 69 is A4 (440 Hz).</summary>
    public int Pitch { get; set; } = 60;
    /// <summary>When the note starts, in ticks from the start of the piece.</summary>
    public long Start { get; set; }
    /// <summary>How long it is held, in ticks (at least 1).</summary>
    public long Length { get; set; } = 480;
    /// <summary>How hard it is played, 1 to 127: its volume.</summary>
    public int Velocity { get; set; } = 100;

    public MusicNote Copy() => (MusicNote)MemberwiseClone();

    [JsonIgnore] public long End => Start + Length;
}

/// <summary>One part of a piece, played by one instrument, with its own volume, mute and solo.</summary>
public sealed class MusicTrack
{
    public string Name { get; set; } = "Track";
    /// <summary>
    /// The synthesiser preset that plays it, by name (<see cref="SoundSynth.Presets"/>): one of
    /// <see cref="MusicInstruments.BuiltIn"/>, or any other preset.
    /// </summary>
    public string Instrument { get; set; } = "Piano";
    /// <summary>0 to 1.</summary>
    public float Volume { get; set; } = 0.8f;
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    /// <summary>Seconds to full volume; null keeps the instrument's own.</summary>
    public double? Attack { get; set; }
    /// <summary>Seconds a note fades out after it is let go; null keeps the instrument's own.</summary>
    public double? Release { get; set; }
    /// <summary>Low-pass cutoff in Hz (lower is softer and darker); null keeps the instrument's own.</summary>
    public double? Filter { get; set; }
    /// <summary>The General MIDI program written on export; -1 takes it from the instrument.</summary>
    public int Program { get; set; } = -1;
    /// <summary>The MIDI channel (0 to 15) it was imported from, kept on export; -1 lets export choose (9 for drums).</summary>
    public int Channel { get; set; } = -1;
    public List<MusicNote> Notes { get; set; } = [];

    public MusicTrack Copy()
    {
        MusicTrack copy = (MusicTrack)MemberwiseClone();
        copy.Notes = Notes.Select(note => note.Copy()).ToList();
        return copy;
    }
}

/// <summary>A tempo change part way through a piece (an imported MIDI file can have several).</summary>
public sealed class MusicTempoChange
{
    public long Tick { get; set; }
    /// <summary>Quarter notes per minute from <see cref="Tick"/> on.</summary>
    public double Bpm { get; set; } = 120;
}

/// <summary>
/// A piece of music (a <c>.music.json</c> resource): tempo, time signature and tracks of notes, each
/// track with an instrument. The Audio editor's Music mode edits it; Render turns it into an Audio
/// resource's WAV, so a game plays it with PlaySound like any other sound. MIDI files import into
/// and export from it (<see cref="MidiFile"/>).
/// </summary>
public sealed class MusicDocument
{
    public const int DefaultTicksPerBeat = 480;
    public const double MinimumTempo = 20, MaximumTempo = 400;

    public int SchemaVersion { get; set; } = 1;
    /// <summary>Ticks in a quarter note (the MIDI file's division).</summary>
    public int TicksPerBeat { get; set; } = DefaultTicksPerBeat;
    /// <summary>Quarter notes per minute at the start.</summary>
    public double Tempo { get; set; } = 120;
    /// <summary>Later tempo changes, by tick (an imported file's); empty for one tempo throughout.</summary>
    public List<MusicTempoChange> TempoChanges { get; set; } = [];
    /// <summary>Time signature: beats in a bar (the 3 of 3/4).</summary>
    public int BeatsPerBar { get; set; } = 4;
    /// <summary>Time signature: the note that is one beat (the 4 of 3/4): 2, 4, 8 or 16.</summary>
    public int BeatUnit { get; set; } = 4;
    /// <summary>The range Loop repeats, ticks; an end of 0 is no range.</summary>
    public long LoopStart { get; set; }
    public long LoopEnd { get; set; }
    /// <summary>The Audio resource Render writes, by name; empty until the first render.</summary>
    public string? Audio { get; set; }
    public List<MusicTrack> Tracks { get; set; } = [];

    /// <summary>Ticks in one beat of the time signature (a quarter note in 4/4, an eighth in 6/8).</summary>
    [JsonIgnore] public long TicksPerSignatureBeat => Math.Max(1, (long)TicksPerBeat * 4 / Math.Max(1, BeatUnit));

    [JsonIgnore] public long TicksPerBar => TicksPerSignatureBeat * Math.Max(1, BeatsPerBar);

    /// <summary>Where the last note ends, ticks (0 for an empty piece).</summary>
    [JsonIgnore] public long EndTick => Tracks.SelectMany(track => track.Notes).Select(note => note.End).DefaultIfEmpty(0).Max();

    /// <summary>Quarter notes per minute at <paramref name="tick"/>.</summary>
    public double TempoAt(long tick)
    {
        double bpm = Tempo;
        foreach (MusicTempoChange change in TempoChanges)
        {
            if (change.Tick > tick) break;
            bpm = change.Bpm;
        }
        return bpm;
    }

    /// <summary>Seconds from the start of the piece to <paramref name="tick"/>, through every tempo change.</summary>
    public double SecondsAt(long tick)
    {
        double seconds = 0, bpm = Tempo;
        long at = 0;
        foreach (MusicTempoChange change in TempoChanges)
        {
            if (change.Tick >= tick) break;
            seconds += (change.Tick - at) * 60.0 / (bpm * TicksPerBeat);
            at = change.Tick; bpm = change.Bpm;
        }
        return seconds + (tick - at) * 60.0 / (bpm * TicksPerBeat);
    }

    /// <summary>The tick sounding <paramref name="seconds"/> into the piece.</summary>
    public long TickAt(double seconds)
    {
        double elapsed = 0, bpm = Tempo;
        long at = 0;
        foreach (MusicTempoChange change in TempoChanges)
        {
            double span = (change.Tick - at) * 60.0 / (bpm * TicksPerBeat);
            if (elapsed + span > seconds) break;
            elapsed += span; at = change.Tick; bpm = change.Bpm;
        }
        return at + (long)Math.Round((seconds - elapsed) * bpm * TicksPerBeat / 60.0);
    }

    /// <summary>A separate document with the same values.</summary>
    public MusicDocument Copy()
    {
        MusicDocument copy = (MusicDocument)MemberwiseClone();
        copy.TempoChanges = TempoChanges.Select(change => new MusicTempoChange { Tick = change.Tick, Bpm = change.Bpm }).ToList();
        copy.Tracks = Tracks.Select(track => track.Copy()).ToList();
        return copy;
    }

    /// <summary>Brings every value into its range and puts notes and tempo changes in time order.</summary>
    public void Normalize()
    {
        TicksPerBeat = Math.Clamp(TicksPerBeat, 1, 32767);
        Tempo = ClampTempo(Tempo);
        BeatsPerBar = Math.Clamp(BeatsPerBar, 1, 32);
        BeatUnit = BeatUnit is 1 or 2 or 4 or 8 or 16 or 32 ? BeatUnit : 4;
        TempoChanges = (TempoChanges ?? [])
            .Where(change => change is not null && change.Tick > 0)
            .Select(change => new MusicTempoChange { Tick = change.Tick, Bpm = ClampTempo(change.Bpm) })
            .GroupBy(change => change.Tick).Select(group => group.Last())
            .OrderBy(change => change.Tick).ToList();
        LoopStart = Math.Max(0, LoopStart);
        LoopEnd = LoopEnd > LoopStart ? LoopEnd : 0;
        Tracks ??= [];
        Tracks.RemoveAll(track => track is null);
        foreach (MusicTrack track in Tracks)
        {
            track.Name = string.IsNullOrWhiteSpace(track.Name) ? "Track" : track.Name.Trim();
            track.Instrument = string.IsNullOrWhiteSpace(track.Instrument) ? "Piano" : track.Instrument.Trim();
            track.Volume = float.IsFinite(track.Volume) ? Math.Clamp(track.Volume, 0f, 1f) : 0.8f;
            track.Attack = Within(track.Attack, 0, 10);
            track.Release = Within(track.Release, 0, 10);
            track.Filter = Within(track.Filter, 20, 20000);
            track.Program = track.Program is >= 0 and <= 127 ? track.Program : -1;
            track.Channel = track.Channel is >= 0 and <= 15 ? track.Channel : -1;
            track.Notes ??= [];
            track.Notes.RemoveAll(note => note is null);
            foreach (MusicNote note in track.Notes)
            {
                note.Pitch = Math.Clamp(note.Pitch, 0, 127);
                note.Start = Math.Max(0, note.Start);
                note.Length = Math.Max(1, note.Length);
                note.Velocity = Math.Clamp(note.Velocity, 1, 127);
            }
            track.Notes = track.Notes.OrderBy(note => note.Start).ThenBy(note => note.Pitch).ToList();
        }
    }

    private static double ClampTempo(double bpm) => double.IsFinite(bpm) ? Math.Clamp(bpm, MinimumTempo, MaximumTempo) : 120;

    private static double? Within(double? value, double minimum, double maximum) =>
        value is double number && double.IsFinite(number) ? Math.Clamp(number, minimum, maximum) : null;

    /// <summary>What a new piece starts with: a piano and a drum kit, each with one bar to press Play on.</summary>
    public static MusicDocument CreateDefault()
    {
        MusicDocument document = new();
        MusicTrack piano = new() { Name = "Piano", Instrument = "Piano" };
        int[] arpeggio = [60, 64, 67, 72];
        for (int index = 0; index < arpeggio.Length; index++)
            piano.Notes.Add(new MusicNote { Pitch = arpeggio[index], Start = index * 480L, Length = 480, Velocity = 96 });
        MusicTrack drums = new() { Name = "Drums", Instrument = MusicInstruments.DrumKit, Volume = 0.7f };
        for (int beat = 0; beat < 4; beat++)
        {
            drums.Notes.Add(new MusicNote { Pitch = beat % 2 == 0 ? 36 : 38, Start = beat * 480L, Length = 240, Velocity = 110 });
            drums.Notes.Add(new MusicNote { Pitch = 42, Start = beat * 480L, Length = 120, Velocity = 70 });
            drums.Notes.Add(new MusicNote { Pitch = 42, Start = beat * 480L + 240, Length = 120, Velocity = 55 });
        }
        document.Tracks.Add(piano);
        document.Tracks.Add(drums);
        document.Normalize();
        return document;
    }
}

/// <summary>Reads and writes <c>.music.json</c>: the same shape every time, values brought into range on read.</summary>
public static class MusicDocumentSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(MusicDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>The document in the text; throws <see cref="InvalidDataException"/> when it is not a piece of music.</summary>
    public static MusicDocument Deserialize(string json)
    {
        MusicDocument? document;
        try { document = JsonSerializer.Deserialize<MusicDocument>(json, Options); }
        catch (JsonException exception) { throw new InvalidDataException("The music file could not be read: " + exception.Message, exception); }
        if (document is null) throw new InvalidDataException("The music file is empty.");
        document.Normalize();
        return document;
    }

    public static MusicDocument Load(string path) => Deserialize(File.ReadAllText(path));
}

namespace Genesis.Application.Core.Audio.Music;

/// <summary>How an instrument plays a note.</summary>
public enum MusicInstrumentKind
{
    /// <summary>Held for the note's length, then released (piano, pad, organ...).</summary>
    Sustained,
    /// <summary>Plays the whole sound as it is made, at the note's pitch, whatever the note's length (Coin, Laser...).</summary>
    OneShot,
    /// <summary>Each key is a different drum (General MIDI's drum keys), played as made.</summary>
    Drums,
}

/// <summary>
/// The instruments the Music mode offers. An instrument is a synthesiser preset, by name
/// (<see cref="SoundSynth.Presets"/>), so any preset plays notes: the built-in musical ones first, then
/// the rest. A preset's first layer sets the pitch it is made at; a note plays it moved to its own key.
/// </summary>
public static class MusicInstruments
{
    public const string DrumKit = "Drum Kit";

    /// <summary>The musical presets, in the order the editor lists them (each made at a key of its own: Bass at C2, Lead at C5).</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["Piano", "Pluck", "Bass", "Lead", "Pad", "Organ", DrumKit];

    /// <summary>The single drums a drum kit plays; they are presets too.</summary>
    public static IReadOnlyList<string> DrumPieces { get; } = ["Kick Drum", "Snare Drum", "Clap", "Hi-Hat", "Open Hi-Hat", "Crash Cymbal", "Tom"];

    /// <summary>Every instrument a track can choose: the built-in ones, then every other ready-made sound.</summary>
    public static IReadOnlyList<string> Choices() =>
        BuiltIn.Concat(SynthSounds.GalleryPresets().Where(name => !BuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase))).ToArray();

    public static bool Exists(string instrument) => SoundSynth.Presets.ContainsKey(instrument.Trim());

    public static MusicInstrumentKind Kind(string instrument)
    {
        string name = instrument.Trim();
        if (string.Equals(name, DrumKit, StringComparison.OrdinalIgnoreCase)) return MusicInstrumentKind.Drums;
        if (BuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase)) return MusicInstrumentKind.Sustained;
        // A repeating sound that is all one chord (a drone, a pad, wind) is held; effects, drums and
        // sounds made of a sequence (birdsong, a heartbeat) are played through once.
        return SynthSounds.Info(name).Loops && SoundSynth.Presets.TryGetValue(name, out var make) && make().All(layer => layer.Start <= 0)
            ? MusicInstrumentKind.Sustained : MusicInstrumentKind.OneShot;
    }

    /// <summary>The drum a key plays in a drum kit (General MIDI's drum keys), and how far it is retuned (toms).</summary>
    public static (string Preset, double Pitch) DrumPiece(int key) => key switch
    {
        35 or 36 => ("Kick Drum", 1),
        37 or 38 or 40 => ("Snare Drum", 1),
        39 => ("Clap", 1),
        42 or 44 => ("Hi-Hat", 1),
        46 => ("Open Hi-Hat", 1),
        49 or 52 or 55 or 57 => ("Crash Cymbal", 1),
        51 or 53 or 59 => ("Open Hi-Hat", 0.8),
        41 or 43 or 45 or 47 or 48 or 50 => ("Tom", Math.Pow(2, (key - 45) / 12.0)),
        < 35 => ("Kick Drum", 1),
        _ => ("Hi-Hat", 1.2),
    };

    /// <summary>The built-in instrument closest to a General MIDI program (0 to 127).</summary>
    public static string FromGeneralMidi(int program) => (Math.Clamp(program, 0, 127) / 8) switch
    {
        0 => "Piano",
        1 or 3 or 13 or 14 => "Pluck",
        2 => "Organ",
        4 => "Bass",
        5 or 6 or 11 or 12 or 15 => "Pad",
        _ => "Lead",
    };

    /// <summary>The General MIDI program an instrument is written as in a MIDI file.</summary>
    public static int GeneralMidiProgram(string instrument) => instrument.Trim().ToLowerInvariant() switch
    {
        "piano" => 0,
        "pluck" => 25,
        "organ" or "organ chord" or "drone" => 16,
        "bass" => 33,
        "lead" => 80,
        "pad" => 89,
        "choir pad" => 52,
        _ => 0,
    };

    /// <summary>Hz of a MIDI note number (69 is A4, 440 Hz).</summary>
    public static double Frequency(int pitch) => 440.0 * Math.Pow(2, (pitch - 69) / 12.0);

    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>A MIDI note number as a name: 60 is C4, 61 C#4.</summary>
    public static string NoteName(int pitch) => NoteNames[((pitch % 12) + 12) % 12] + (pitch / 12 - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

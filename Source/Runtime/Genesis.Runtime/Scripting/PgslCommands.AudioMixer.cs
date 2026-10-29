#nullable enable
using System;
using Genesis.Shared.Audio;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// The mixer from a script: effects on a bus (filter, EQ, reverb, compressor; every one off until set)
// and snapshots, named sets of bus volumes and effects blended to over a time. The flat commands take
// any bus by name (BusReverb("ui", "hall", 0.3)); the music and sound-effects buses also have them as
// parts of Engine.Audio.Music and Engine.Audio.Effects (Engine.Audio.Music.Reverb.Mix = 0.3).
public static partial class PgslCommands
{
    private static AudioBusEffects BusEffectsOf(string bus) => ActiveGameContext?.Audio?.GetBusEffects(bus) ?? new AudioBusEffects();

    private static void ChangeBusEffects(string bus, Action<AudioBusEffects> change)
    {
        IAudioSystem? audio = ActiveGameContext?.Audio;
        if (audio == null || string.IsNullOrWhiteSpace(bus)) return;
        AudioBusEffects effects = audio.GetBusEffects(bus);
        change(effects);
        audio.SetBusEffects(bus, effects);
    }

    private static float Number(double value, double minimum, double maximum, double fallback) =>
        (float)(double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback);

    [PgslCommand("BusFilter", "BusFilter(bus, type, frequency, q?)",
        "A filter on every sound of a bus: type \"lowpass\" (keeps what is below frequency: muffled, underwater), \"highpass\" (above it: thin, a radio), \"bandpass\" (around it) or \"off\"; q 0.707 unless given (higher rings)", "Audio")]
    public static void BusFilter(string bus, string type, double frequency, double q = 0.707) => ChangeBusEffects(bus, effects =>
    {
        string kind = (type ?? "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
        effects.FilterEnabled = kind is "lowpass" or "highpass" or "bandpass";
        if (!effects.FilterEnabled) return;
        effects.Enabled = true;
        effects.FilterType = kind == "highpass" ? "HighPass" : kind == "bandpass" ? "BandPass" : "LowPass";
        effects.FilterFrequency = Number(frequency, 20, 20000, 1000);
        effects.FilterQ = Number(q, 0.5, 10, 0.707);
    });

    [PgslCommand("BusEq", "BusEq(bus, lowDb, midDb, highDb)",
        "A three-band EQ on a bus: dB of boost (up to 18) or cut (down to -18) around 150 Hz, 1 kHz and 6 kHz; all 0 turns it off", "Audio")]
    public static void BusEq(string bus, double lowDb, double midDb, double highDb) => ChangeBusEffects(bus, effects =>
    {
        effects.EqLow = Number(lowDb, -18, 18, 0);
        effects.EqMid = Number(midDb, -18, 18, 0);
        effects.EqHigh = Number(highDb, -18, 18, 0);
        effects.EqEnabled = effects.EqLow != 0 || effects.EqMid != 0 || effects.EqHigh != 0;
        if (effects.EqEnabled) effects.Enabled = true;
    });

    [PgslCommand("BusReverb", "BusReverb(bus, place, mix)",
        "A reverb on a bus: place \"room\", \"small room\", \"hall\", \"large hall\", \"cave\", \"arena\", \"hangar\", \"hallway\", \"bathroom\", \"forest\", \"city\", \"mountains\", \"underwater\" or \"plate\"; mix 0 to 1 how much is heard (0 or place \"off\" turns it off)", "Audio")]
    public static void BusReverb(string bus, string place, double mix) => ChangeBusEffects(bus, effects =>
    {
        string where = (place ?? "").Trim().ToLowerInvariant();
        effects.ReverbMix = Number(mix, 0, 1, 0.3);
        effects.ReverbEnabled = where != "off" && where.Length > 0 && effects.ReverbMix > 0;
        if (!effects.ReverbEnabled) return;
        effects.Enabled = true;
        effects.ReverbPlace = where;
    });

    [PgslCommand("BusCompressor", "BusCompressor(bus, thresholdDb, ratio, attack?, release?)",
        "A compressor on a bus: when its peak goes over thresholdDb (-12 is loud, -30 quiet) the bus is turned down so that ratio dB over comes out as 1 (4 is firm, 10 a limiter), down over attack seconds (0.01) and back over release (0.25); a ratio of 1 turns it off", "Audio")]
    public static void BusCompressor(string bus, double thresholdDb, double ratio, double attack = 0.01, double release = 0.25) => ChangeBusEffects(bus, effects =>
    {
        effects.CompressorThreshold = Number(thresholdDb, -60, 0, -12);
        effects.CompressorRatio = Number(ratio, 1, 20, 4);
        effects.CompressorAttack = Number(attack, 0, 5, 0.01);
        effects.CompressorRelease = Number(release, 0, 10, 0.25);
        effects.CompressorEnabled = effects.CompressorRatio > 1;
        if (effects.CompressorEnabled) effects.Enabled = true;
    });

    [PgslCommand("BusEffectsEnabled", "BusEffectsEnabled(bus, enabled)", "Turn all of a bus's effects off (each keeps its settings) or back on", "Audio")]
    public static void BusEffectsEnabled(string bus, bool enabled) => ChangeBusEffects(bus, effects => effects.Enabled = enabled);

    [PgslCommand("BusEffectsClear", "BusEffectsClear(bus)", "Take every effect off a bus: it plays as a plain bus again", "Audio")]
    public static void BusEffectsClear(string bus) => ActiveGameContext?.Audio?.SetBusEffects(bus, new AudioBusEffects());

    [PgslCommand("BusLevel", "BusLevel(bus) -> number", "How loud a bus is now after its effects, its peak from 0 (silent) to 1 (full); the first call starts measuring it", "Audio")]
    public static double BusLevel(string bus) => ActiveGameContext?.Audio?.GetBusLevel(bus) ?? 0;

    [PgslCommand("AudioSnapshotSave", "AudioSnapshotSave(name)", "Keep every bus's volume and effects as they are now as the snapshot name, to blend back to later", "Audio")]
    public static void AudioSnapshotSave(string name) => ActiveGameContext?.Audio?.SaveSnapshot(name);

    [PgslCommand("AudioSnapshotBlend", "AudioSnapshotBlend(name, seconds) -> bool",
        "Move the buses a snapshot names (the project's mixer, or one saved by AudioSnapshotSave) to its volumes and effects over seconds: AudioSnapshotBlend(\"Combat\", 1.5). False when there is no such snapshot", "Audio")]
    public static bool AudioSnapshotBlend(string name, double seconds) =>
        ActiveGameContext?.Audio?.BlendToSnapshot(name, Number(seconds, 0, 600, 0)) ?? false;

    [PgslCommand("AudioSnapshotCurrent", "AudioSnapshotCurrent() -> string", "The snapshot last blended to, or empty", "Audio")]
    public static string AudioSnapshotCurrent() => ActiveGameContext?.Audio?.CurrentSnapshot ?? "";

    [PgslCommand("AudioSnapshotProgress", "AudioSnapshotProgress() -> number", "How far the blend to the current snapshot has gone, 0 to 1 (1 when it is done or there is none)", "Audio")]
    public static double AudioSnapshotProgress() => ActiveGameContext?.Audio?.SnapshotProgress ?? 1;

    // ── Engine.Audio.Snapshot ────────────────────────────────────────────────────────────

    [PgslCommand("Blend", "Engine.Audio.Snapshot.Blend(\"Combat\", seconds)", "Move the buses the snapshot names to its volumes and effects over the seconds given; false when there is no such snapshot", "Audio", Namespace = "Engine.Audio.Snapshot", Flat = false)]
    public static bool AudioSnapshotBlendPart(string name, double seconds) => AudioSnapshotBlend(name, seconds);

    [PgslCommand("Save", "Engine.Audio.Snapshot.Save(\"Explore\")", "Keep every bus's volume and effects as they are now as a snapshot of that name", "Audio", Namespace = "Engine.Audio.Snapshot", Flat = false)]
    public static void AudioSnapshotSavePart(string name) => AudioSnapshotSave(name);

    [PgslCommand("Name", "Engine.Audio.Snapshot.Name", "The snapshot last blended to (\"\" unless one was)", "Audio", Namespace = "Engine.Audio.Snapshot", Flat = false)]
    public static string AudioSnapshotName => AudioSnapshotCurrent();

    [PgslCommand("Progress", "Engine.Audio.Snapshot.Progress", "How far the blend has gone, 0 to 1 (1 when it is done or there is none)", "Audio", Namespace = "Engine.Audio.Snapshot", Flat = false)]
    public static double AudioSnapshotProgressPart => AudioSnapshotProgress();

    [PgslCommand("Enabled", "Engine.Audio.Snapshot.Enabled = false", "True while a blend is moving (false when none is); setting false holds the buses where the blend has got to", "Audio", Namespace = "Engine.Audio.Snapshot", Flat = false)]
    public static bool AudioSnapshotEnabled
    {
        get => AudioSnapshotProgress() < 1;
        set { if (!value) ActiveGameContext?.Audio?.HoldSnapshotBlend(); }
    }

    // ── The music and sound-effects buses' effects: Engine.Audio.Music.Reverb.Mix and the rest ──

    // Turning an effect on turns its bus's effects, the bus and the audio on (the Enabled rule).
    private static void EffectOn(string bus, AudioBusEffects effects)
    {
        effects.Enabled = true;
        if (bus == "music") AudioMusicEnabled = true; else AudioEffectsEnabled = true;
        AudioApiEnabled = true;
    }

    private static bool FilterOn(string bus) => BusEffectsOf(bus) is { Enabled: true, FilterEnabled: true };
    private static void SetFilterOn(string bus, bool on) => ChangeBusEffects(bus, effects => { effects.FilterEnabled = on; if (on) EffectOn(bus, effects); });
    private static bool EqOn(string bus) => BusEffectsOf(bus) is { Enabled: true, EqEnabled: true };
    private static void SetEqOn(string bus, bool on) => ChangeBusEffects(bus, effects => { effects.EqEnabled = on; if (on) EffectOn(bus, effects); });
    private static bool ReverbOn(string bus) => BusEffectsOf(bus) is { Enabled: true, ReverbEnabled: true };
    private static void SetReverbOn(string bus, bool on) => ChangeBusEffects(bus, effects => { effects.ReverbEnabled = on; if (on) EffectOn(bus, effects); });
    private static bool CompressorOn(string bus) => BusEffectsOf(bus) is { Enabled: true, CompressorEnabled: true };
    private static void SetCompressorOn(string bus, bool on) => ChangeBusEffects(bus, effects => { effects.CompressorEnabled = on; if (on) EffectOn(bus, effects); });

    [PgslCommand("Enabled", "Engine.Audio.Music.Filter.Enabled = true", "A filter on the music (false unless set)", "Audio", Namespace = "Engine.Audio.Music.Filter", Flat = false)]
    public static bool AudioMusicFilterEnabled { get => FilterOn("music"); set => SetFilterOn("music", value); }

    [PgslCommand("Type", "Engine.Audio.Music.Filter.Type = \"LowPass\"", "\"LowPass\" (muffled), \"HighPass\" (thin) or \"BandPass\" (\"LowPass\" unless set)", "Audio", Namespace = "Engine.Audio.Music.Filter", Flat = false)]
    public static string AudioMusicFilterType { get => BusEffectsOf("music").FilterType; set => ChangeBusEffects("music", effects => effects.FilterType = value ?? "LowPass"); }

    [PgslCommand("Frequency", "Engine.Audio.Music.Filter.Frequency = 800", "The filter's frequency, Hz (1000 unless set)", "Audio", Namespace = "Engine.Audio.Music.Filter", Flat = false)]
    public static double AudioMusicFilterFrequency { get => BusEffectsOf("music").FilterFrequency; set => ChangeBusEffects("music", effects => effects.FilterFrequency = Number(value, 20, 20000, 1000)); }

    [PgslCommand("Q", "Engine.Audio.Music.Filter.Q = 0.707", "How sharp the filter is (0.707 unless set): higher rings", "Audio", Namespace = "Engine.Audio.Music.Filter", Flat = false)]
    public static double AudioMusicFilterQ { get => BusEffectsOf("music").FilterQ; set => ChangeBusEffects("music", effects => effects.FilterQ = Number(value, 0.5, 10, 0.707)); }

    [PgslCommand("Enabled", "Engine.Audio.Music.Eq.Enabled = true", "The music's three-band EQ (false unless set)", "Audio", Namespace = "Engine.Audio.Music.Eq", Flat = false)]
    public static bool AudioMusicEqEnabled { get => EqOn("music"); set => SetEqOn("music", value); }

    [PgslCommand("Low", "Engine.Audio.Music.Eq.Low = 3", "dB around 150 Hz, -18 to 18 (0 unless set)", "Audio", Namespace = "Engine.Audio.Music.Eq", Flat = false)]
    public static double AudioMusicEqLow { get => BusEffectsOf("music").EqLow; set => ChangeBusEffects("music", effects => effects.EqLow = Number(value, -18, 18, 0)); }

    [PgslCommand("Mid", "Engine.Audio.Music.Eq.Mid = -2", "dB around 1 kHz, -18 to 18 (0 unless set)", "Audio", Namespace = "Engine.Audio.Music.Eq", Flat = false)]
    public static double AudioMusicEqMid { get => BusEffectsOf("music").EqMid; set => ChangeBusEffects("music", effects => effects.EqMid = Number(value, -18, 18, 0)); }

    [PgslCommand("High", "Engine.Audio.Music.Eq.High = 2", "dB around 6 kHz, -18 to 18 (0 unless set)", "Audio", Namespace = "Engine.Audio.Music.Eq", Flat = false)]
    public static double AudioMusicEqHigh { get => BusEffectsOf("music").EqHigh; set => ChangeBusEffects("music", effects => effects.EqHigh = Number(value, -18, 18, 0)); }

    [PgslCommand("Enabled", "Engine.Audio.Music.Reverb.Enabled = true", "A reverb on the music (false unless set)", "Audio", Namespace = "Engine.Audio.Music.Reverb", Flat = false)]
    public static bool AudioMusicReverbEnabled { get => ReverbOn("music"); set => SetReverbOn("music", value); }

    [PgslCommand("Place", "Engine.Audio.Music.Reverb.Place = \"hall\"", "Where the reverb sounds like: \"room\", \"hall\", \"cave\", \"arena\"... (\"room\" unless set)", "Audio", Namespace = "Engine.Audio.Music.Reverb", Flat = false)]
    public static string AudioMusicReverbPlace { get => BusEffectsOf("music").ReverbPlace; set => ChangeBusEffects("music", effects => effects.ReverbPlace = value ?? "room"); }

    [PgslCommand("Mix", "Engine.Audio.Music.Reverb.Mix = 0.3", "How much reverb is heard, 0 to 1 (0.3 unless set)", "Audio", Namespace = "Engine.Audio.Music.Reverb", Flat = false)]
    public static double AudioMusicReverbMix { get => BusEffectsOf("music").ReverbMix; set => ChangeBusEffects("music", effects => effects.ReverbMix = Number(value, 0, 1, 0.3)); }

    [PgslCommand("Enabled", "Engine.Audio.Music.Compressor.Enabled = true", "A compressor on the music (false unless set)", "Audio", Namespace = "Engine.Audio.Music.Compressor", Flat = false)]
    public static bool AudioMusicCompressorEnabled { get => CompressorOn("music"); set => SetCompressorOn("music", value); }

    [PgslCommand("Threshold", "Engine.Audio.Music.Compressor.Threshold = -12", "dB of peak above which the music is turned down, -60 to 0 (-12 unless set)", "Audio", Namespace = "Engine.Audio.Music.Compressor", Flat = false)]
    public static double AudioMusicCompressorThreshold { get => BusEffectsOf("music").CompressorThreshold; set => ChangeBusEffects("music", effects => effects.CompressorThreshold = Number(value, -60, 0, -12)); }

    [PgslCommand("Ratio", "Engine.Audio.Music.Compressor.Ratio = 4", "How hard it turns down: 4 dB over comes out as 1 (4 unless set)", "Audio", Namespace = "Engine.Audio.Music.Compressor", Flat = false)]
    public static double AudioMusicCompressorRatio { get => BusEffectsOf("music").CompressorRatio; set => ChangeBusEffects("music", effects => effects.CompressorRatio = Number(value, 1, 20, 4)); }

    [PgslCommand("Level", "Engine.Audio.Music.Level", "How loud the music bus is now after its effects, 0 to 1", "Audio", Namespace = "Engine.Audio.Music", Flat = false)]
    public static double AudioMusicLevel => BusLevel("music");

    [PgslCommand("Enabled", "Engine.Audio.Effects.Filter.Enabled = true", "A filter on the sound effects (false unless set)", "Audio", Namespace = "Engine.Audio.Effects.Filter", Flat = false)]
    public static bool AudioEffectsFilterEnabled { get => FilterOn("sfx"); set => SetFilterOn("sfx", value); }

    [PgslCommand("Type", "Engine.Audio.Effects.Filter.Type = \"LowPass\"", "\"LowPass\" (muffled), \"HighPass\" (thin) or \"BandPass\" (\"LowPass\" unless set)", "Audio", Namespace = "Engine.Audio.Effects.Filter", Flat = false)]
    public static string AudioEffectsFilterType { get => BusEffectsOf("sfx").FilterType; set => ChangeBusEffects("sfx", effects => effects.FilterType = value ?? "LowPass"); }

    [PgslCommand("Frequency", "Engine.Audio.Effects.Filter.Frequency = 800", "The filter's frequency, Hz (1000 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Filter", Flat = false)]
    public static double AudioEffectsFilterFrequency { get => BusEffectsOf("sfx").FilterFrequency; set => ChangeBusEffects("sfx", effects => effects.FilterFrequency = Number(value, 20, 20000, 1000)); }

    [PgslCommand("Q", "Engine.Audio.Effects.Filter.Q = 0.707", "How sharp the filter is (0.707 unless set): higher rings", "Audio", Namespace = "Engine.Audio.Effects.Filter", Flat = false)]
    public static double AudioEffectsFilterQ { get => BusEffectsOf("sfx").FilterQ; set => ChangeBusEffects("sfx", effects => effects.FilterQ = Number(value, 0.5, 10, 0.707)); }

    [PgslCommand("Enabled", "Engine.Audio.Effects.Eq.Enabled = true", "The sound effects' three-band EQ (false unless set)", "Audio", Namespace = "Engine.Audio.Effects.Eq", Flat = false)]
    public static bool AudioEffectsEqEnabled { get => EqOn("sfx"); set => SetEqOn("sfx", value); }

    [PgslCommand("Low", "Engine.Audio.Effects.Eq.Low = 3", "dB around 150 Hz, -18 to 18 (0 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Eq", Flat = false)]
    public static double AudioEffectsEqLow { get => BusEffectsOf("sfx").EqLow; set => ChangeBusEffects("sfx", effects => effects.EqLow = Number(value, -18, 18, 0)); }

    [PgslCommand("Mid", "Engine.Audio.Effects.Eq.Mid = -2", "dB around 1 kHz, -18 to 18 (0 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Eq", Flat = false)]
    public static double AudioEffectsEqMid { get => BusEffectsOf("sfx").EqMid; set => ChangeBusEffects("sfx", effects => effects.EqMid = Number(value, -18, 18, 0)); }

    [PgslCommand("High", "Engine.Audio.Effects.Eq.High = 2", "dB around 6 kHz, -18 to 18 (0 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Eq", Flat = false)]
    public static double AudioEffectsEqHigh { get => BusEffectsOf("sfx").EqHigh; set => ChangeBusEffects("sfx", effects => effects.EqHigh = Number(value, -18, 18, 0)); }

    [PgslCommand("Enabled", "Engine.Audio.Effects.Reverb.Enabled = true", "A reverb on the sound effects, besides the room's acoustics (false unless set)", "Audio", Namespace = "Engine.Audio.Effects.Reverb", Flat = false)]
    public static bool AudioEffectsReverbEnabled { get => ReverbOn("sfx"); set => SetReverbOn("sfx", value); }

    [PgslCommand("Place", "Engine.Audio.Effects.Reverb.Place = \"cave\"", "Where the reverb sounds like: \"room\", \"hall\", \"cave\", \"arena\"... (\"room\" unless set)", "Audio", Namespace = "Engine.Audio.Effects.Reverb", Flat = false)]
    public static string AudioEffectsReverbPlace { get => BusEffectsOf("sfx").ReverbPlace; set => ChangeBusEffects("sfx", effects => effects.ReverbPlace = value ?? "room"); }

    [PgslCommand("Mix", "Engine.Audio.Effects.Reverb.Mix = 0.3", "How much reverb is heard, 0 to 1 (0.3 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Reverb", Flat = false)]
    public static double AudioEffectsReverbMix { get => BusEffectsOf("sfx").ReverbMix; set => ChangeBusEffects("sfx", effects => effects.ReverbMix = Number(value, 0, 1, 0.3)); }

    [PgslCommand("Enabled", "Engine.Audio.Effects.Compressor.Enabled = true", "A compressor on the sound effects (false unless set)", "Audio", Namespace = "Engine.Audio.Effects.Compressor", Flat = false)]
    public static bool AudioEffectsCompressorEnabled { get => CompressorOn("sfx"); set => SetCompressorOn("sfx", value); }

    [PgslCommand("Threshold", "Engine.Audio.Effects.Compressor.Threshold = -12", "dB of peak above which the sound effects are turned down, -60 to 0 (-12 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Compressor", Flat = false)]
    public static double AudioEffectsCompressorThreshold { get => BusEffectsOf("sfx").CompressorThreshold; set => ChangeBusEffects("sfx", effects => effects.CompressorThreshold = Number(value, -60, 0, -12)); }

    [PgslCommand("Ratio", "Engine.Audio.Effects.Compressor.Ratio = 4", "How hard it turns down: 4 dB over comes out as 1 (4 unless set)", "Audio", Namespace = "Engine.Audio.Effects.Compressor", Flat = false)]
    public static double AudioEffectsCompressorRatio { get => BusEffectsOf("sfx").CompressorRatio; set => ChangeBusEffects("sfx", effects => effects.CompressorRatio = Number(value, 1, 20, 4)); }

    [PgslCommand("Level", "Engine.Audio.Effects.Level", "How loud the sound-effects bus is now after its effects, 0 to 1", "Audio", Namespace = "Engine.Audio.Effects", Flat = false)]
    public static double AudioEffectsLevel => BusLevel("sfx");
}

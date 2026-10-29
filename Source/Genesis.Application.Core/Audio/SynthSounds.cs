namespace Genesis.Application.Core.Audio;

/// <summary>A tune added to a sound: notes as a string, each played with the voice's wave, envelope and filter.</summary>
public sealed record SynthMelody(string Notes, double BeatSeconds = 0.25, double Start = 0, SynthLayer? Voice = null);

/// <summary>
/// Everything that makes one synthesised sound. Automation's <c>audio.synth</c> and the Audio editor's
/// "Make a sound" both build one and render it through <see cref="SynthSounds"/>, so the same recipe
/// is the same sound whichever way it was asked for.
/// </summary>
public sealed class SynthRecipe
{
    /// <summary>A ready-made sound to start from (a key of <see cref="SoundSynth.Presets"/>).</summary>
    public string? Preset { get; init; }
    public IReadOnlyList<SynthLayer> Layers { get; init; } = [];
    public IReadOnlyList<SynthMelody> Melodies { get; init; } = [];
    public double EchoDelay { get; init; }
    public double EchoFeedback { get; init; }
    /// <summary>Seed for noise, so the same recipe makes the same sound.</summary>
    public int Seed { get; init; } = 1;
    /// <summary>
    /// How long the sound lasts, seconds, before any echo tail: its timing (starts, lengths and the
    /// envelope's attack, decay and release) is stretched or squeezed to fit. 0 leaves it as it is.
    /// </summary>
    public double Length { get; init; }
    /// <summary>1 leaves the pitch as it is, 2 is an octave up, 0.5 an octave down (filters follow).</summary>
    public double Pitch { get; init; } = 1;
    /// <summary>
    /// Seconds of a seamless loop: the sound is made exactly this long, with what runs on past the end
    /// faded into the start, so it repeats without a click (wind, rain, an engine). 0 makes it as it is.
    /// </summary>
    public double LoopSeconds { get; init; }
}

/// <summary>
/// A rendered sound: samples from -1 to 1 at <see cref="SoundSynth.SampleRate"/>, mono, or stereo
/// (interleaved left and right) when a layer is panned.
/// </summary>
public sealed record SynthSound(float[] Samples, int LayerCount, int Channels = 1)
{
    public double Seconds => Samples.Length / (double)(SoundSynth.SampleRate * Channels);
}

/// <summary>
/// What automation and the Audio editor share about making a sound with <see cref="SoundSynth"/>:
/// turning a recipe into samples, naming the WAV, and handing it over as a file to import or play.
/// Problems with a recipe throw <see cref="ArgumentException"/> or <see cref="FormatException"/>
/// with a message that says what to change.
/// </summary>
public static class SynthSounds
{
    // 512 until 29 October 2026; a long generated piece or a dense texture needs more.
    public const int MaximumLayers = 16384;

    // Past this the synthesiser would be asked for a pitch it cannot play (it refuses above 20 kHz).
    private const double HighestFrequency = 19000;

    private static readonly string[] CategoryOrder = ["Effects", "Footsteps", "Ambience", "Music", "Instruments", "Drums"];

    /// <summary>The presets a gallery lists, grouped Effects, Footsteps, Ambience, Music, Instruments, Drums (then any without a description).</summary>
    public static IReadOnlyList<string> GalleryPresets() =>
        SoundSynth.Presets.Keys
            .Where(name => Info(name).InGallery)
            .OrderBy(name => { int group = Array.IndexOf(CategoryOrder, Info(name).Category); return group < 0 ? CategoryOrder.Length : group; })
            .ToArray();

    /// <summary>What a preset is; a preset without a description is listed under "Other".</summary>
    public static SynthPresetInfo Info(string preset) =>
        SoundSynth.PresetInfo.TryGetValue(preset.Trim(), out SynthPresetInfo? info) ? info : new SynthPresetInfo("Other", string.Empty);

    /// <summary>How long a preset lasts as it is made, seconds, before any echo.</summary>
    public static double PresetSeconds(string preset)
    {
        if (!SoundSynth.Presets.TryGetValue(preset.Trim(), out var make)) throw new ArgumentException(NotAPreset(preset));
        return make().Max(layer => layer.Start + layer.Duration);
    }

    /// <summary>The recipe's layers: the preset's, then its own, then its melodies' notes.</summary>
    public static List<SynthLayer> BuildLayers(SynthRecipe recipe)
    {
        var layers = new List<SynthLayer>();
        if (!string.IsNullOrWhiteSpace(recipe.Preset))
        {
            if (!SoundSynth.Presets.TryGetValue(recipe.Preset.Trim(), out var preset)) throw new ArgumentException(NotAPreset(recipe.Preset));
            layers.AddRange(preset());
        }
        layers.AddRange(recipe.Layers);
        foreach (SynthMelody melody in recipe.Melodies)
        {
            if (!(melody.BeatSeconds > 0)) throw new ArgumentException("A melody's beat must be above 0 seconds.");
            layers.AddRange(SoundSynth.Melody(melody.Notes, melody.BeatSeconds, melody.Start, melody.Voice ?? new SynthLayer()));
        }
        if (layers.Count == 0) throw new ArgumentException("Give a preset, layers or melodies to make a sound from.");
        if (layers.Count > MaximumLayers) throw new ArgumentException($"At most {MaximumLayers} layers and notes.");
        return layers;
    }

    /// <summary>
    /// Renders the recipe: its layers, retuned to its pitch and length if those are set, mixed with its
    /// echo; in stereo when a layer is panned, and made a seamless loop when it has LoopSeconds.
    /// </summary>
    public static SynthSound Render(SynthRecipe recipe)
    {
        List<SynthLayer> layers = BuildLayers(recipe);
        if (!double.IsFinite(recipe.Pitch) || recipe.Pitch <= 0) throw new ArgumentException("The pitch must be above 0 (1 leaves it as it is).");
        if (!double.IsFinite(recipe.Length) || recipe.Length < 0) throw new ArgumentException("The length must be 0 (as it is) or a number of seconds.");
        if (!double.IsFinite(recipe.LoopSeconds) || recipe.LoopSeconds < 0 || recipe.LoopSeconds > SoundSynth.MaximumSeconds)
            throw new ArgumentException($"A loop is 0 (none) or up to {SoundSynth.MaximumSeconds:0} seconds.");
        double natural = layers.Max(layer => layer.Start + layer.Duration);
        double time = recipe.Length > 0 && natural > 0 ? recipe.Length / natural : 1;
        if (recipe.Pitch != 1 || time != 1) layers = layers.Select(layer => Retune(layer, recipe.Pitch, time)).ToList();
        int channels = layers.Any(layer => layer.Pan != 0) ? 2 : 1;
        float[] samples = channels == 2
            ? SoundSynth.RenderStereo(layers, recipe.EchoDelay, recipe.EchoFeedback, recipe.Seed)
            : SoundSynth.Render(layers, recipe.EchoDelay, recipe.EchoFeedback, recipe.Seed);
        if (recipe.LoopSeconds > 0) samples = SoundSynth.MakeLoop(samples, channels, recipe.LoopSeconds);
        return new SynthSound(samples, layers.Count, channels);
    }

    // A copy of the layer at another pitch and length; the layer given is left as it was.
    private static SynthLayer Retune(SynthLayer layer, double pitch, double time)
    {
        SynthLayer copy = layer.Copy();
        if (pitch != 1)
        {
            double frequency = string.IsNullOrWhiteSpace(layer.Note) ? layer.Frequency : SoundSynth.NoteFrequency(layer.Note);
            copy.Note = null;
            copy.Frequency = Math.Min(frequency * pitch, HighestFrequency);
            if (layer.FrequencyEnd > 0) copy.FrequencyEnd = Math.Min(layer.FrequencyEnd * pitch, HighestFrequency);
            if (layer.LowPass > 0) copy.LowPass = Math.Min(layer.LowPass * pitch, HighestFrequency);
            if (layer.HighPass > 0) copy.HighPass = Math.Min(layer.HighPass * pitch, HighestFrequency);
            if (layer.BandPass > 0) copy.BandPass = Math.Min(layer.BandPass * pitch, HighestFrequency);
        }
        if (time != 1)
        {
            copy.Start *= time; copy.Duration *= time;
            copy.Attack *= time; copy.Decay *= time; copy.Release *= time;
        }
        return copy;
    }

    /// <summary>A sound's name as a file name: characters a file cannot have become underscores.</summary>
    public static string FileStem(string name) =>
        string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>
    /// Writes the samples as a WAV named <paramref name="stem"/> in a scratch folder, gives <paramref name="use"/>
    /// the file (to import it, or to play it), and removes the folder afterwards. The file is gone once
    /// <paramref name="use"/> returns, so it must copy or read the sound by then.
    /// </summary>
    public static T WithWavFile<T>(float[] samples, string stem, Func<string, T> use) => WithWavFile(samples, 1, stem, use);

    /// <summary>As <see cref="WithWavFile{T}(float[], string, Func{string, T})"/> for a rendered sound, mono or stereo.</summary>
    public static T WithWavFile<T>(SynthSound sound, string stem, Func<string, T> use) => WithWavFile(sound.Samples, sound.Channels, stem, use);

    private static T WithWavFile<T>(float[] samples, int channels, string stem, Func<string, T> use)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "genesis-synth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string wav = Path.Combine(scratch, stem + ".wav");
            SoundSynth.WriteWav(wav, samples, channels);
            return use(wav);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static string NotAPreset(string preset) =>
        $"'{preset}' is not a preset. Presets: {string.Join(", ", SoundSynth.Presets.Keys)}.";
}

using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Genesis.Application.Core.Audio;

/// <summary>An oscillator shape.</summary>
public enum SynthWave
{
    Sine,
    Square,
    Saw,
    Triangle,
    Noise,
    /// <summary>A fundamental with soft octave and fifth harmonics, as an organ stop.</summary>
    Organ,
}

/// <summary>One voice of a sound: an oscillator, its pitch, its envelope and its filter.</summary>
public sealed class SynthLayer
{
    [Description("Sine, Square, Saw, Triangle, Noise or Organ (default Sine).")] public SynthWave Wave { get; set; } = SynthWave.Sine;
    [Description("A note (A4, F#3, Bb2); wins over frequency.")] public string? Note { get; set; }
    [Description("Pitch in Hz (default 440). For Noise, higher hisses and lower rumbles.")] public double Frequency { get; set; } = 440;
    [Description("The pitch it glides to by its end, Hz; 0 keeps it steady.")] public double FrequencyEnd { get; set; }
    [Description("When it starts, seconds.")] public double Start { get; set; }
    [Description("How long it lasts, seconds (default 0.5).")] public double Duration { get; set; } = 0.5;
    [Description("0 to 1 (default 0.5); the whole sound is normalised afterwards.")] public double Volume { get; set; } = 0.5;
    [Description("Envelope: seconds to full volume (default 0.01).")] public double Attack { get; set; } = 0.01;
    [Description("Envelope: seconds from full to the sustain level (default 0.05).")] public double Decay { get; set; } = 0.05;
    [Description("Envelope: the level held, 0 to 1 (default 0.7).")] public double Sustain { get; set; } = 0.7;
    [Description("Envelope: seconds fading out at the end (default 0.1).")] public double Release { get; set; } = 0.1;
    [Description("Vibrato speed, Hz.")] public double VibratoRate { get; set; }
    [Description("Vibrato width, semitones.")] public double VibratoDepth { get; set; }
    [Description("Square waves: the fraction of each cycle that is high (default 0.5).")] public double Duty { get; set; } = 0.5;
    [Description("Low-pass cutoff, Hz; 0 leaves it open. Lower is softer and darker.")] public double LowPass { get; set; }
    [Description("Copies of the voice spread by detune, for a thicker sound (1 to 8).")] public int Voices { get; set; } = 1;
    [Description("Spread of the copies, cents (default 8).")] public double Detune { get; set; } = 8;
    [Description("A slow swell in volume, Hz (wind, breathing pads).")] public double TremoloRate { get; set; }
    [Description("How deep the swell goes, 0 to 1.")] public double TremoloDepth { get; set; }
}

/// <summary>
/// A small synthesiser for sound effects and simple music, made for Studio's automation (and Nova):
/// oscillators with envelopes, pitch glides, vibrato, unison and a low-pass, mixed, given an echo,
/// normalised and written as a 16-bit mono WAV that the Audio resource imports like any other.
/// </summary>
public static class SoundSynth
{
    public const int SampleRate = 44100;
    public const double MaximumSeconds = 120;

    /// <summary>Hz of a note name: A4 is 440; C4 is middle C. Throws on a name it cannot read.</summary>
    public static double NoteFrequency(string note)
    {
        Match match = NotePattern().Match(note.Trim());
        if (!match.Success) throw new FormatException($"'{note}' is not a note; write it as C4, F#3 or Bb2.");
        int semitone = char.ToUpperInvariant(match.Groups["name"].Value[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11,
        };
        semitone += match.Groups["accidental"].Value switch { "#" => 1, "b" => -1, _ => 0 };
        int octave = int.Parse(match.Groups["octave"].Value, CultureInfo.InvariantCulture);
        int midi = (octave + 1) * 12 + semitone;
        return 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }

    private static readonly Regex NotePatternRegex = new(@"^(?<name>[A-Ga-g])(?<accidental>#|b)?(?<octave>-?\d)$", RegexOptions.Compiled);
    private static Regex NotePattern() => NotePatternRegex;

    /// <summary>
    /// Layers for a melody: notes separated by spaces, "-" a rest, "C4:2" a note two beats long,
    /// "C4+E4+G4" a chord. Each note takes the template's wave, envelope and filter.
    /// </summary>
    public static IEnumerable<SynthLayer> Melody(string notes, double beatSeconds, double start, SynthLayer template)
    {
        double at = start;
        foreach (string token in notes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':');
            double beats = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 ? b : 1;
            double length = beats * beatSeconds;
            if (parts[0] != "-")
            {
                foreach (string note in parts[0].Split('+', StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return new SynthLayer
                    {
                        Wave = template.Wave, Note = note, Start = at, Duration = length, Volume = template.Volume,
                        Attack = template.Attack, Decay = template.Decay, Sustain = template.Sustain, Release = Math.Min(template.Release, length * 0.5),
                        VibratoRate = template.VibratoRate, VibratoDepth = template.VibratoDepth, Duty = template.Duty, LowPass = template.LowPass,
                        Voices = template.Voices, Detune = template.Detune, TremoloRate = template.TremoloRate, TremoloDepth = template.TremoloDepth,
                    };
                }
            }
            at += length;
        }
    }

    /// <summary>Mixes the layers into samples from -1 to 1, with an optional echo, normalised to a peak of 0.9.</summary>
    public static float[] Render(IReadOnlyList<SynthLayer> layers, double echoDelay = 0, double echoFeedback = 0, int seed = 1)
    {
        double length = layers.Count == 0 ? 0 : layers.Max(l => l.Start + l.Duration);
        if (echoDelay > 0 && echoFeedback > 0) length += echoDelay * 4;
        if (length > MaximumSeconds) throw new ArgumentException($"The sound would last {length:0.#} s; the most is {MaximumSeconds:0} s.");
        var mix = new float[Math.Max(1, (int)Math.Ceiling(length * SampleRate))];
        var random = new Random(seed);
        foreach (SynthLayer layer in layers) RenderLayer(layer, mix, random);

        if (echoDelay > 0 && echoFeedback > 0)
        {
            int delay = Math.Max(1, (int)(echoDelay * SampleRate));
            float feedback = (float)Math.Clamp(echoFeedback, 0, 0.9);
            for (int i = delay; i < mix.Length; i++) mix[i] += mix[i - delay] * feedback;
        }

        float peak = 0;
        foreach (float sample in mix) peak = Math.Max(peak, Math.Abs(sample));
        if (peak > 1e-6f)
        {
            float gain = 0.9f / peak;
            for (int i = 0; i < mix.Length; i++) mix[i] *= gain;
        }
        return mix;
    }

    private static void RenderLayer(SynthLayer layer, float[] mix, Random random)
    {
        double baseFrequency = string.IsNullOrWhiteSpace(layer.Note) ? layer.Frequency : NoteFrequency(layer.Note);
        if (!(baseFrequency > 0) || baseFrequency > 20000) throw new ArgumentException($"A frequency of {baseFrequency:0.#} Hz cannot be played.");
        int first = (int)(Math.Max(0, layer.Start) * SampleRate);
        int count = (int)(Math.Max(0, layer.Duration) * SampleRate);
        int voices = Math.Clamp(layer.Voices, 1, 8);
        double alpha = layer.LowPass > 0 ? 1 - Math.Exp(-2 * Math.PI * layer.LowPass / SampleRate) : 1;
        double volume = Math.Clamp(layer.Volume, 0, 1) / Math.Sqrt(voices);
        for (int voice = 0; voice < voices; voice++)
        {
            double cents = voices == 1 ? 0 : layer.Detune * (voice / (double)(voices - 1) - 0.5) * 2;
            double detune = Math.Pow(2, cents / 1200);
            double phase = random.NextDouble(), filtered = 0, noise = 0;
            int noiseHold = 0;
            for (int i = 0; i < count && first + i < mix.Length; i++)
            {
                double t = i / (double)SampleRate;
                double progress = count <= 1 ? 0 : i / (double)(count - 1);
                double frequency = layer.FrequencyEnd > 0
                    ? baseFrequency * Math.Pow(layer.FrequencyEnd / baseFrequency, progress)
                    : baseFrequency;
                if (layer.VibratoRate > 0 && layer.VibratoDepth != 0)
                    frequency *= Math.Pow(2, layer.VibratoDepth * Math.Sin(2 * Math.PI * layer.VibratoRate * t) / 12);
                frequency *= detune;
                phase += frequency / SampleRate;
                phase -= Math.Floor(phase);
                double value;
                switch (layer.Wave)
                {
                    case SynthWave.Square: value = phase < Math.Clamp(layer.Duty, 0.05, 0.95) ? 1 : -1; break;
                    case SynthWave.Saw: value = 2 * phase - 1; break;
                    case SynthWave.Triangle: value = 1 - 4 * Math.Abs(phase - 0.5); break;
                    case SynthWave.Noise:
                        // Noise held for a period set by the pitch: higher pitches hiss, low ones rumble.
                        if (--noiseHold <= 0)
                        {
                            noise = random.NextDouble() * 2 - 1;
                            noiseHold = Math.Max(1, (int)(SampleRate / Math.Max(1, frequency * 2)));
                        }
                        value = noise;
                        break;
                    case SynthWave.Organ:
                        value = Math.Sin(2 * Math.PI * phase) + 0.5 * Math.Sin(4 * Math.PI * phase) + 0.25 * Math.Sin(6 * Math.PI * phase) + 0.12 * Math.Sin(8 * Math.PI * phase);
                        value /= 1.87;
                        break;
                    default: value = Math.Sin(2 * Math.PI * phase); break;
                }
                filtered += alpha * (value - filtered);
                double envelope = Envelope(layer, t, layer.Duration);
                if (layer.TremoloRate > 0) envelope *= 1 - Math.Clamp(layer.TremoloDepth, 0, 1) * 0.5 * (1 + Math.Sin(2 * Math.PI * layer.TremoloRate * t));
                mix[first + i] += (float)(filtered * envelope * volume);
            }
        }
    }

    private static double Envelope(SynthLayer layer, double t, double duration)
    {
        double attack = Math.Max(0.0005, layer.Attack), decay = Math.Max(0, layer.Decay), release = Math.Clamp(layer.Release, 0.0005, duration);
        double sustain = Math.Clamp(layer.Sustain, 0, 1);
        double level = t < attack ? t / attack
            : t < attack + decay ? 1 - (1 - sustain) * ((t - attack) / Math.Max(1e-6, decay))
            : sustain;
        double releaseStart = duration - release;
        if (t > releaseStart) level *= Math.Max(0, (duration - t) / release);
        return level;
    }

    /// <summary>Writes 16-bit mono PCM.</summary>
    public static void WriteWav(string path, float[] samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (float sample in samples) writer.Write((short)Math.Clamp((int)Math.Round(sample * 32767), short.MinValue, short.MaxValue));
    }

    /// <summary>Ready-made sounds, by name.</summary>
    public static IReadOnlyDictionary<string, Func<IReadOnlyList<SynthLayer>>> Presets { get; } =
        new Dictionary<string, Func<IReadOnlyList<SynthLayer>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Coin"] = () => [
                new() { Wave = SynthWave.Square, Note = "B5", Duration = 0.07, Sustain = 1, Release = 0.01, Volume = 0.4 },
                new() { Wave = SynthWave.Square, Note = "E6", Start = 0.07, Duration = 0.25, Decay = 0.2, Sustain = 0.2, Release = 0.05, Volume = 0.4 }],
            ["Jump"] = () => [new() { Wave = SynthWave.Square, Frequency = 280, FrequencyEnd = 620, Duration = 0.22, Duty = 0.3, Sustain = 0.6, Release = 0.08, Volume = 0.4 }],
            ["Laser"] = () => [new() { Wave = SynthWave.Saw, Frequency = 1400, FrequencyEnd = 180, Duration = 0.25, Sustain = 0.5, Release = 0.1, LowPass = 5000, Volume = 0.4 }],
            ["Explosion"] = () => [
                new() { Wave = SynthWave.Noise, Frequency = 900, FrequencyEnd = 120, Duration = 1.2, Attack = 0.005, Decay = 0.5, Sustain = 0.25, Release = 0.6, LowPass = 1800, Volume = 0.8 },
                new() { Wave = SynthWave.Sine, Frequency = 70, FrequencyEnd = 35, Duration = 0.8, Decay = 0.4, Sustain = 0.2, Release = 0.3, Volume = 0.6 }],
            ["Hit"] = () => [
                new() { Wave = SynthWave.Noise, Frequency = 2500, Duration = 0.09, Attack = 0.002, Decay = 0.05, Sustain = 0.2, Release = 0.03, Volume = 0.6 },
                new() { Wave = SynthWave.Square, Frequency = 220, FrequencyEnd = 70, Duration = 0.14, Duty = 0.4, Sustain = 0.5, Release = 0.05, Volume = 0.35 }],
            ["Power Up"] = () => [.. Melody("C5 E5 G5 C6:2", 0.07, 0, new SynthLayer { Wave = SynthWave.Square, Duty = 0.25, Sustain = 0.8, Release = 0.03, Volume = 0.35 })],
            ["Blip"] = () => [new() { Wave = SynthWave.Sine, Note = "A5", Duration = 0.08, Sustain = 1, Release = 0.03, Volume = 0.5 }],
            ["Footstep"] = () => [new() { Wave = SynthWave.Noise, Frequency = 1400, Duration = 0.09, Attack = 0.003, Decay = 0.05, Sustain = 0.15, Release = 0.03, LowPass = 900, Volume = 0.7 }],
            ["Heartbeat"] = () => [
                new() { Wave = SynthWave.Sine, Frequency = 62, FrequencyEnd = 45, Duration = 0.16, Decay = 0.1, Sustain = 0.3, Release = 0.05, Volume = 0.9 },
                new() { Wave = SynthWave.Sine, Frequency = 55, FrequencyEnd = 40, Start = 0.24, Duration = 0.18, Decay = 0.1, Sustain = 0.3, Release = 0.06, Volume = 0.7 },
                new() { Wave = SynthWave.Sine, Frequency = 30, Start = 0.42, Duration = 0.5, Volume = 0.0 }],
            ["Wind"] = () => [new() { Wave = SynthWave.Noise, Frequency = 600, Duration = 6, Attack = 1, Sustain = 1, Release = 1, LowPass = 500, TremoloRate = 0.25, TremoloDepth = 0.7, Volume = 0.6 }],
            ["Drone"] = () => [
                new() { Wave = SynthWave.Organ, Note = "C2", Duration = 8, Attack = 1.5, Sustain = 1, Release = 1.5, LowPass = 900, Volume = 0.5, Voices = 3, Detune = 6 },
                new() { Wave = SynthWave.Organ, Note = "G2", Duration = 8, Attack = 2, Sustain = 1, Release = 1.5, LowPass = 900, Volume = 0.35, Voices = 3, Detune = 6 }],
            ["Choir Pad"] = () => [.. new[] { "D3", "F3", "A3", "D4" }.Select(note => new SynthLayer
                { Wave = SynthWave.Saw, Note = note, Duration = 8, Attack = 2, Sustain = 1, Release = 2, LowPass = 1200, Volume = 0.3, Voices = 4, Detune = 14, VibratoRate = 5, VibratoDepth = 0.08 })],
            ["Organ Chord"] = () => [.. new[] { "C3", "G3", "C4", "E4" }.Select(note => new SynthLayer
                { Wave = SynthWave.Organ, Note = note, Duration = 4, Attack = 0.15, Sustain = 1, Release = 0.8, LowPass = 3000, Volume = 0.35 })],
        };
}

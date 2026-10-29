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
    /// <summary>Noise that changes every sample whatever the pitch: an even hiss (Noise holds each value for a time set by the pitch).</summary>
    WhiteNoise,
    /// <summary>Noise with as much energy in each octave: rain, a waterfall, a soft surf.</summary>
    PinkNoise,
    /// <summary>Noise weighted to the low end: a distant rumble, wind in a room.</summary>
    BrownNoise,
}

/// <summary>How the envelope moves between its levels.</summary>
public enum SynthEnvelope
{
    /// <summary>Straight lines (the synthesiser's first shape; every older sound uses it).</summary>
    Linear,
    /// <summary>Curves as real instruments fade: fast at first, then slower (decay and release fall about 60 dB).</summary>
    Exponential,
}

/// <summary>One voice of a sound: an oscillator, its pitch, its envelope and its filter.</summary>
public sealed class SynthLayer
{
    [Description("Sine, Square, Saw, Triangle, Noise, Organ, WhiteNoise, PinkNoise or BrownNoise (default Sine).")] public SynthWave Wave { get; set; } = SynthWave.Sine;
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
    [Description("Envelope shape: Linear (default) or Exponential, which fades as instruments do.")] public SynthEnvelope Envelope { get; set; }
    [Description("High-pass cutoff, Hz; 0 leaves it open. Takes away what is below it: thin, airy, a telephone.")] public double HighPass { get; set; }
    [Description("Band-pass centre, Hz; 0 leaves it open. Keeps only the band around it (width from Q).")] public double BandPass { get; set; }
    [Description("How sharp the high-pass and band-pass are (default 0.707): higher rings and narrows, 0.5 is gentle.")] public double Q { get; set; } = 0.707;
    [Description("Saw and Square without the buzz they alias into at high notes, and Square without the offset its duty gives (default false, as older sounds were made).")] public bool BandLimited { get; set; }
    [Description("Where it sits left to right, -1 (left) to 1 (right); any layer off centre makes the sound stereo (default 0).")] public double Pan { get; set; }

    /// <summary>A separate layer with the same values.</summary>
    public SynthLayer Copy() => (SynthLayer)MemberwiseClone();
}

/// <summary>What a ready-made sound is, for the Audio editor's gallery.</summary>
/// <param name="Category">Effects, Footsteps, Ambience or Music: the gallery groups by it.</param>
/// <param name="Description">One plain sentence on what it sounds like.</param>
/// <param name="Loops">The sound is made to repeat (wind, birds, a drone), so the editor ticks Repeat for it.</param>
/// <param name="InGallery">False for a second name of a sound already listed.</param>
public sealed record SynthPresetInfo(string Category, string Description, bool Loops = false, bool InGallery = true);

/// <summary>
/// A small synthesiser for sound effects and simple music, made for Studio's automation (and Nova) and
/// offered in the Audio editor's "Make a sound" (both go through <see cref="SynthSounds"/>):
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
                        Envelope = template.Envelope, HighPass = template.HighPass, BandPass = template.BandPass, Q = template.Q,
                        BandLimited = template.BandLimited, Pan = template.Pan,
                    };
                }
            }
            at += length;
        }
    }

    /// <summary>
    /// Mixes the layers into mono samples from -1 to 1, with an optional echo, normalised to a peak of
    /// 0.9. Pans are ignored here (<see cref="RenderStereo"/> places layers left and right).
    /// </summary>
    public static float[] Render(IReadOnlyList<SynthLayer> layers, double echoDelay = 0, double echoFeedback = 0, int seed = 1) =>
        Mix(layers, 1, echoDelay, echoFeedback, seed);

    /// <summary>
    /// As <see cref="Render"/>, in stereo: interleaved left and right samples, each layer placed by its
    /// <see cref="SynthLayer.Pan"/> (equal power, so a centred layer is at 0.707 in each ear before the
    /// sound is normalised).
    /// </summary>
    public static float[] RenderStereo(IReadOnlyList<SynthLayer> layers, double echoDelay = 0, double echoFeedback = 0, int seed = 1) =>
        Mix(layers, 2, echoDelay, echoFeedback, seed);

    private static float[] Mix(IReadOnlyList<SynthLayer> layers, int channels, double echoDelay, double echoFeedback, int seed)
    {
        double length = layers.Count == 0 ? 0 : layers.Max(l => l.Start + l.Duration);
        if (echoDelay > 0 && echoFeedback > 0) length += echoDelay * 4;
        if (length > MaximumSeconds) throw new ArgumentException($"The sound would last {length:0.#} s; the most is {MaximumSeconds:0} s.");
        var mix = new float[Math.Max(1, (int)Math.Ceiling(length * SampleRate)) * channels];
        var random = new Random(seed);
        foreach (SynthLayer layer in layers) RenderLayer(layer, mix, channels, random);

        if (echoDelay > 0 && echoFeedback > 0)
        {
            int delay = Math.Max(1, (int)(echoDelay * SampleRate)) * channels;
            float feedback = (float)Math.Clamp(echoFeedback, 0, 0.9);
            for (int i = delay; i < mix.Length; i++) mix[i] += mix[i - delay] * feedback;
        }

        Normalise(mix);
        return mix;
    }

    // To a peak of 0.9; silence is left as it is.
    private static void Normalise(float[] mix)
    {
        float peak = 0;
        foreach (float sample in mix) peak = Math.Max(peak, Math.Abs(sample));
        if (peak > 1e-6f)
        {
            float gain = 0.9f / peak;
            for (int i = 0; i < mix.Length; i++) mix[i] *= gain;
        }
    }

    // A layer's oscillator, filter and envelope into the mix. The original sound (linear envelope,
    // no high-pass or band-pass, raw saw and square, mono) runs exactly the arithmetic it always
    // did, so every sound made before these options sounds as it did, sample for sample.
    private static void RenderLayer(SynthLayer layer, float[] mix, int channels, Random random)
    {
        double baseFrequency = string.IsNullOrWhiteSpace(layer.Note) ? layer.Frequency : NoteFrequency(layer.Note);
        if (!(baseFrequency > 0) || baseFrequency > 20000) throw new ArgumentException($"A frequency of {baseFrequency:0.#} Hz cannot be played.");
        int frames = mix.Length / channels;
        int first = (int)(Math.Max(0, layer.Start) * SampleRate);
        int count = (int)(Math.Max(0, layer.Duration) * SampleRate);
        int voices = Math.Clamp(layer.Voices, 1, 8);
        double alpha = layer.LowPass > 0 ? 1 - Math.Exp(-2 * Math.PI * layer.LowPass / SampleRate) : 1;
        double volume = Math.Clamp(layer.Volume, 0, 1) / Math.Sqrt(voices);
        double duty = Math.Clamp(layer.Duty, 0.05, 0.95);
        Biquad? highPass = layer.HighPass > 0 ? Biquad.HighPass(layer.HighPass, layer.Q) : null;
        Biquad? bandPass = layer.BandPass > 0 ? Biquad.BandPass(layer.BandPass, layer.Q) : null;
        double pan = (Math.Clamp(layer.Pan, -1, 1) + 1) * Math.PI / 4;
        float leftGain = (float)Math.Cos(pan), rightGain = (float)Math.Sin(pan);
        for (int voice = 0; voice < voices; voice++)
        {
            double cents = voices == 1 ? 0 : layer.Detune * (voice / (double)(voices - 1) - 0.5) * 2;
            double detune = Math.Pow(2, cents / 1200);
            double phase = random.NextDouble(), filtered = 0, noise = 0;
            int noiseHold = 0;
            var colour = new NoiseColour();
            highPass?.Reset();
            bandPass?.Reset();
            for (int i = 0; i < count && first + i < frames; i++)
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
                    case SynthWave.Square when layer.BandLimited:
                        {
                            // PolyBLEP smooths each step over a sample; the duty's offset is taken away.
                            double step = Math.Min(0.5, frequency / SampleRate);
                            double fall = phase - duty;
                            fall -= Math.Floor(fall);
                            value = (phase < duty ? 1 : -1) + PolyBlep(phase, step) - PolyBlep(fall, step) - (2 * duty - 1);
                            break;
                        }
                    case SynthWave.Square: value = phase < duty ? 1 : -1; break;
                    case SynthWave.Saw when layer.BandLimited: value = 2 * phase - 1 - PolyBlep(phase, Math.Min(0.5, frequency / SampleRate)); break;
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
                    case SynthWave.WhiteNoise: value = random.NextDouble() * 2 - 1; break;
                    case SynthWave.PinkNoise: value = colour.Pink(random.NextDouble() * 2 - 1); break;
                    case SynthWave.BrownNoise: value = colour.Brown(random.NextDouble() * 2 - 1); break;
                    case SynthWave.Organ:
                        value = Math.Sin(2 * Math.PI * phase) + 0.5 * Math.Sin(4 * Math.PI * phase) + 0.25 * Math.Sin(6 * Math.PI * phase) + 0.12 * Math.Sin(8 * Math.PI * phase);
                        value /= 1.87;
                        break;
                    default: value = Math.Sin(2 * Math.PI * phase); break;
                }
                filtered += alpha * (value - filtered);
                double shaped = filtered;
                if (highPass is not null) shaped = highPass.Next(shaped);
                if (bandPass is not null) shaped = bandPass.Next(shaped);
                double envelope = layer.Envelope == SynthEnvelope.Exponential ? ExponentialEnvelope(layer, t, layer.Duration) : Envelope(layer, t, layer.Duration);
                if (layer.TremoloRate > 0) envelope *= 1 - Math.Clamp(layer.TremoloDepth, 0, 1) * 0.5 * (1 + Math.Sin(2 * Math.PI * layer.TremoloRate * t));
                if (channels == 1) mix[first + i] += (float)(shaped * envelope * volume);
                else
                {
                    float sample = (float)(shaped * envelope * volume);
                    mix[(first + i) * 2] += sample * leftGain;
                    mix[(first + i) * 2 + 1] += sample * rightGain;
                }
            }
        }
    }

    // The correction that turns a hard step of a saw or square into a band-limited one (two samples wide).
    private static double PolyBlep(double phase, double step)
    {
        if (step <= 0) return 0;
        if (phase < step)
        {
            double x = phase / step;
            return x + x - x * x - 1;
        }
        if (phase > 1 - step)
        {
            double x = (phase - 1) / step;
            return x * x + x + x + 1;
        }
        return 0;
    }

    /// <summary>A two-pole filter (the audio cookbook's): high-pass or band-pass with a Q.</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        private static (double Cos, double Alpha) Shape(double frequency, double q)
        {
            double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, SampleRate * 0.45) / SampleRate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * Math.Clamp(double.IsFinite(q) ? q : 0.707, 0.1, 40)));
        }

        public static Biquad HighPass(double frequency, double q)
        {
            (double cos, double alpha) = Shape(frequency, q);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        // Unit gain at the centre whatever the Q.
        public static Biquad BandPass(double frequency, double q)
        {
            (double cos, double alpha) = Shape(frequency, q);
            return new Biquad(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

        public double Next(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    // Pink noise by Paul Kellet's filter (within 0.05 dB of -3 dB an octave), brown by a leaky sum.
    private struct NoiseColour
    {
        private double _b0, _b1, _b2, _b3, _b4, _b5, _b6, _brown;

        public double Pink(double white)
        {
            _b0 = 0.99886 * _b0 + white * 0.0555179;
            _b1 = 0.99332 * _b1 + white * 0.0750759;
            _b2 = 0.96900 * _b2 + white * 0.1538520;
            _b3 = 0.86650 * _b3 + white * 0.3104856;
            _b4 = 0.55000 * _b4 + white * 0.5329522;
            _b5 = -0.7616 * _b5 - white * 0.0168980;
            double pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362;
            _b6 = white * 0.115926;
            return pink * 0.11;
        }

        public double Brown(double white)
        {
            _brown = (_brown + 0.02 * white) / 1.02;
            return _brown * 3.5;
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

    // How far an exponential decay or release falls over its time: e^-6.9 is a thousandth, -60 dB.
    private const double ExponentialFall = 6.9;

    // The same stages as Envelope on curves: the attack rises quickly then eases into full, the decay
    // and release fall fast then slowly, and each still arrives exactly where the linear one does.
    private static double ExponentialEnvelope(SynthLayer layer, double t, double duration)
    {
        double attack = Math.Max(0.0005, layer.Attack), decay = Math.Max(0, layer.Decay), release = Math.Clamp(layer.Release, 0.0005, duration);
        double sustain = Math.Clamp(layer.Sustain, 0, 1);
        static double Fall(double progress) => (Math.Exp(-ExponentialFall * progress) - Math.Exp(-ExponentialFall)) / (1 - Math.Exp(-ExponentialFall));
        double level = t < attack ? 1 - Fall(t / attack)
            : t < attack + decay ? sustain + (1 - sustain) * Fall((t - attack) / Math.Max(1e-6, decay))
            : sustain;
        double releaseStart = duration - release;
        if (t > releaseStart) level *= Fall(Math.Clamp((t - releaseStart) / release, 0, 1));
        return level;
    }

    /// <summary>
    /// Makes the sound loop seamlessly in exactly <paramref name="seconds"/>: what runs on past that
    /// length is faded into the start (over a quarter of a second, or a quarter of the loop if that is
    /// shorter), so the last sample leads on to the first as the sound itself would have. The sound is
    /// normalised again afterwards. Samples are interleaved for <paramref name="channels"/>.
    /// </summary>
    public static float[] MakeLoop(float[] samples, int channels, double seconds)
    {
        if (!(seconds > 0) || seconds > MaximumSeconds) throw new ArgumentException($"A loop lasts more than 0 and at most {MaximumSeconds:0} seconds.");
        int loop = Math.Max(2, (int)Math.Round(seconds * SampleRate));
        int fade = Math.Max(1, Math.Min(SampleRate / 4, loop / 4));
        int frames = samples.Length / channels;
        var result = new float[loop * channels];
        for (int frame = 0; frame < loop; frame++)
            for (int channel = 0; channel < channels; channel++)
            {
                float value = frame < frames ? samples[frame * channels + channel] : 0f;
                if (frame < fade)
                {
                    // The start fades in under what ran on past the loop's end, which fades out.
                    int tail = loop + frame;
                    float after = tail < frames ? samples[tail * channels + channel] : 0f;
                    float mixIn = (frame + 0.5f) / fade;
                    value = value * mixIn + after * (1 - mixIn);
                }
                result[frame * channels + channel] = value;
            }
        Normalise(result);
        return result;
    }

    /// <summary>Writes 16-bit mono PCM.</summary>
    public static void WriteWav(string path, float[] samples) => WriteWav(path, samples, 1);

    /// <summary>Writes 16-bit PCM of one or two channels (interleaved left and right).</summary>
    public static void WriteWav(string path, float[] samples, int channels)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels), "A sound is mono or stereo.");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2 * channels);
        writer.Write((short)(2 * channels));
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
            ["Footstep"] = FootstepOnGrass,
            ["Footstep Grass"] = FootstepOnGrass,
            ["Footstep Path"] = FootstepOnPath,
            ["Heartbeat"] = () => [
                new() { Wave = SynthWave.Sine, Frequency = 62, FrequencyEnd = 45, Duration = 0.16, Decay = 0.1, Sustain = 0.3, Release = 0.05, Volume = 0.9 },
                new() { Wave = SynthWave.Sine, Frequency = 55, FrequencyEnd = 40, Start = 0.24, Duration = 0.18, Decay = 0.1, Sustain = 0.3, Release = 0.06, Volume = 0.7 },
                new() { Wave = SynthWave.Sine, Frequency = 30, Start = 0.42, Duration = 0.5, Volume = 0.0 }],
            ["Wind"] = () => [new() { Wave = SynthWave.Noise, Frequency = 600, Duration = 6, Attack = 1, Sustain = 1, Release = 1, LowPass = 500, TremoloRate = 0.25, TremoloDepth = 0.7, Volume = 0.6 }],
            ["Birdsong"] = Birdsong,
            ["Drone"] = () => [
                new() { Wave = SynthWave.Organ, Note = "C2", Duration = 8, Attack = 1.5, Sustain = 1, Release = 1.5, LowPass = 900, Volume = 0.5, Voices = 3, Detune = 6 },
                new() { Wave = SynthWave.Organ, Note = "G2", Duration = 8, Attack = 2, Sustain = 1, Release = 1.5, LowPass = 900, Volume = 0.35, Voices = 3, Detune = 6 }],
            ["Choir Pad"] = () => [.. new[] { "D3", "F3", "A3", "D4" }.Select(note => new SynthLayer
                { Wave = SynthWave.Saw, Note = note, Duration = 8, Attack = 2, Sustain = 1, Release = 2, LowPass = 1200, Volume = 0.3, Voices = 4, Detune = 14, VibratoRate = 5, VibratoDepth = 0.08 })],
            ["Organ Chord"] = () => [.. new[] { "C3", "G3", "C4", "E4" }.Select(note => new SynthLayer
                { Wave = SynthWave.Organ, Note = note, Duration = 4, Attack = 0.15, Sustain = 1, Release = 0.8, LowPass = 3000, Volume = 0.35 })],
            // The Music mode's instruments: one note each, at the key of its first layer, which a
            // piece moves to each note's key and holds for the note's length.
            ["Piano"] = () => [
                new() { Wave = SynthWave.Triangle, Note = "C4", Duration = 1.5, Attack = 0.003, Decay = 1.2, Sustain = 0.15, Release = 0.35, LowPass = 3500, Volume = 0.5 },
                new() { Wave = SynthWave.Sine, Note = "C5", Duration = 1.5, Attack = 0.002, Decay = 0.5, Sustain = 0.05, Release = 0.3, Volume = 0.25 },
                new() { Wave = SynthWave.Square, Note = "C4", Duration = 1.5, Attack = 0.001, Decay = 0.15, Sustain = 0, Release = 0.1, Duty = 0.25, LowPass = 2500, Volume = 0.12 }],
            ["Pluck"] = () => [
                new() { Wave = SynthWave.Saw, Note = "C4", Duration = 0.8, Attack = 0.002, Decay = 0.35, Sustain = 0, Release = 0.15, LowPass = 2400, Volume = 0.5 },
                new() { Wave = SynthWave.Triangle, Note = "C4", Duration = 0.8, Attack = 0.002, Decay = 0.6, Sustain = 0.05, Release = 0.15, Volume = 0.3 }],
            ["Bass"] = () => [
                new() { Wave = SynthWave.Saw, Note = "C2", Duration = 1, Attack = 0.005, Decay = 0.25, Sustain = 0.6, Release = 0.12, LowPass = 700, Volume = 0.5 },
                new() { Wave = SynthWave.Sine, Note = "C2", Duration = 1, Attack = 0.005, Decay = 0.1, Sustain = 0.9, Release = 0.12, Volume = 0.5 }],
            ["Lead"] = () => [
                new() { Wave = SynthWave.Square, Note = "C5", Duration = 1, Attack = 0.01, Decay = 0.1, Sustain = 0.8, Release = 0.12, Duty = 0.3, LowPass = 4000, VibratoRate = 5.5, VibratoDepth = 0.15, Volume = 0.4 },
                new() { Wave = SynthWave.Saw, Note = "C5", Duration = 1, Attack = 0.01, Decay = 0.1, Sustain = 0.8, Release = 0.12, LowPass = 3000, Voices = 2, Detune = 10, Volume = 0.2 }],
            ["Pad"] = () => [
                new() { Wave = SynthWave.Saw, Note = "C4", Duration = 2.5, Attack = 0.4, Decay = 0.3, Sustain = 0.85, Release = 0.9, LowPass = 1400, Voices = 4, Detune = 14, TremoloRate = 0.3, TremoloDepth = 0.15, Volume = 0.4 },
                new() { Wave = SynthWave.Triangle, Note = "C3", Duration = 2.5, Attack = 0.5, Sustain = 1, Release = 0.9, Volume = 0.3 }],
            ["Organ"] = () => [
                new() { Wave = SynthWave.Organ, Note = "C4", Duration = 1.5, Attack = 0.02, Decay = 0.05, Sustain = 1, Release = 0.08, LowPass = 5000, Volume = 0.45 },
                new() { Wave = SynthWave.Organ, Note = "C5", Duration = 1.5, Attack = 0.02, Decay = 0.05, Sustain = 1, Release = 0.08, LowPass = 5000, Volume = 0.2 }],
            ["Drum Kit"] = DrumKitBeat,
            ["Kick Drum"] = KickDrum,
            ["Snare Drum"] = SnareDrum,
            ["Clap"] = Clap,
            ["Hi-Hat"] = () => [new() { Wave = SynthWave.Noise, Frequency = 9000, Duration = 0.06, Attack = 0.001, Decay = 0.04, Sustain = 0.1, Release = 0.02, Volume = 0.35 }],
            ["Open Hi-Hat"] = () => [new() { Wave = SynthWave.Noise, Frequency = 9000, Duration = 0.35, Attack = 0.001, Decay = 0.25, Sustain = 0.2, Release = 0.1, Volume = 0.35 }],
            ["Crash Cymbal"] = () => [new() { Wave = SynthWave.Noise, Frequency = 8000, Duration = 1.4, Attack = 0.002, Decay = 1.0, Sustain = 0.1, Release = 0.4, Volume = 0.4 }],
            ["Tom"] = () => [
                new() { Wave = SynthWave.Sine, Frequency = 140, FrequencyEnd = 100, Duration = 0.4, Attack = 0.001, Decay = 0.25, Sustain = 0.2, Release = 0.12, Volume = 0.8 },
                new() { Wave = SynthWave.Noise, Frequency = 3000, Duration = 0.03, Attack = 0.001, Release = 0.02, LowPass = 3000, Volume = 0.2 }],
        };

    /// <summary>What each preset is, by name; the Audio editor's gallery lists the ones with InGallery.</summary>
    public static IReadOnlyDictionary<string, SynthPresetInfo> PresetInfo { get; } =
        new Dictionary<string, SynthPresetInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["Coin"] = new("Effects", "A bright two-note pickup ping."),
            ["Jump"] = new("Effects", "A quick rising hop."),
            ["Laser"] = new("Effects", "A zap that falls in pitch."),
            ["Explosion"] = new("Effects", "A low rumbling boom."),
            ["Hit"] = new("Effects", "A short, sharp thud for a punch or a bump."),
            ["Power Up"] = new("Effects", "Four notes climbing to a high finish."),
            ["Blip"] = new("Effects", "A short, clean menu beep."),
            ["Footstep"] = new("Footsteps", "The same step on grass as Footstep Grass, under its first name.", InGallery: false),
            ["Footstep Grass"] = new("Footsteps", "A soft, muffled step on grass."),
            ["Footstep Path"] = new("Footsteps", "A harder step on a dirt path, with a low thud under the scuff."),
            ["Heartbeat"] = new("Ambience", "A slow double thump, for a tense moment.", Loops: true),
            ["Wind"] = new("Ambience", "Wind that swells and fades.", Loops: true),
            ["Birdsong"] = new("Ambience", "Seven short chirps: four gliding up, then three gliding down.", Loops: true),
            ["Drone"] = new("Music", "A low organ drone that builds slowly.", Loops: true),
            ["Choir Pad"] = new("Music", "A soft, slow chord of voices.", Loops: true),
            ["Organ Chord"] = new("Music", "A held organ chord."),
            ["Piano"] = new("Instruments", "A piano-like note that rings and fades; the Music mode plays it at any key."),
            ["Pluck"] = new("Instruments", "A short plucked string, like a guitar or harp."),
            ["Bass"] = new("Instruments", "A round, low bass note."),
            ["Lead"] = new("Instruments", "A bright square-wave lead with a little vibrato, for melodies."),
            ["Pad"] = new("Instruments", "A soft, slow swell of detuned strings for chords."),
            ["Organ"] = new("Instruments", "A single organ note, held while the key is."),
            ["Drum Kit"] = new("Drums", "One bar of kick, snare and hi-hat; in the Music mode each key is a drum."),
            ["Kick Drum"] = new("Drums", "A deep thump that drops in pitch."),
            ["Snare Drum"] = new("Drums", "A sharp crack of noise over a short tone."),
            ["Clap"] = new("Drums", "A hand clap: three quick bursts and a short tail."),
            ["Hi-Hat"] = new("Drums", "A closed hi-hat tick."),
            ["Open Hi-Hat"] = new("Drums", "An open hi-hat that rings a little."),
            ["Crash Cymbal"] = new("Drums", "A long crash of noise."),
            ["Tom"] = new("Drums", "A tom-tom thud; the drum kit tunes it by key."),
        };

    private static IReadOnlyList<SynthLayer> KickDrum() =>
    [
        new() { Wave = SynthWave.Sine, Frequency = 150, FrequencyEnd = 45, Duration = 0.35, Attack = 0.001, Decay = 0.25, Sustain = 0.2, Release = 0.1, Volume = 0.9 },
        new() { Wave = SynthWave.Noise, Frequency = 3000, Duration = 0.02, Attack = 0.001, Release = 0.015, LowPass = 4000, Volume = 0.25 },
    ];

    private static IReadOnlyList<SynthLayer> SnareDrum() =>
    [
        new() { Wave = SynthWave.Noise, Frequency = 6000, Duration = 0.22, Attack = 0.001, Decay = 0.12, Sustain = 0.15, Release = 0.08, LowPass = 7000, Volume = 0.6 },
        new() { Wave = SynthWave.Triangle, Frequency = 190, FrequencyEnd = 160, Duration = 0.12, Attack = 0.001, Decay = 0.08, Sustain = 0.2, Release = 0.04, Volume = 0.4 },
    ];

    private static IReadOnlyList<SynthLayer> Clap() =>
    [
        .. new[] { 0, 0.012, 0.024 }.Select(start => new SynthLayer { Wave = SynthWave.Noise, Frequency = 4000, Start = start, Duration = 0.02, Attack = 0.001, Release = 0.012, LowPass = 5000, Volume = 0.5 }),
        new() { Wave = SynthWave.Noise, Frequency = 4000, Start = 0.036, Duration = 0.15, Attack = 0.001, Decay = 0.08, Sustain = 0.2, Release = 0.06, LowPass = 3000, Volume = 0.4 },
    ];

    // The drum kit's gallery sound: one bar at 120 beats a minute of kick, snare and eighth-note hi-hats.
    private static IReadOnlyList<SynthLayer> DrumKitBeat()
    {
        var bar = new List<SynthLayer>();
        void At(IReadOnlyList<SynthLayer> drum, double start)
        {
            foreach (SynthLayer layer in drum)
            {
                SynthLayer copy = layer.Copy();
                copy.Start += start;
                bar.Add(copy);
            }
        }
        for (int beat = 0; beat < 4; beat++)
        {
            At(beat % 2 == 0 ? KickDrum() : SnareDrum(), beat * 0.5);
            At(Presets["Hi-Hat"](), beat * 0.5);
            At(Presets["Hi-Hat"](), beat * 0.5 + 0.25);
        }
        return bar;
    }

    // The step on grass the Nature Walk benchmark plays: a soft puff of low-passed noise.
    private static IReadOnlyList<SynthLayer> FootstepOnGrass() =>
        [new() { Wave = SynthWave.Noise, Frequency = 1400, Duration = 0.09, Attack = 0.003, Decay = 0.05, Sustain = 0.15, Release = 0.03, LowPass = 900, Volume = 0.7 }];

    // The step on a path in the same benchmark: a longer scuff of noise over a short low thud.
    private static IReadOnlyList<SynthLayer> FootstepOnPath() =>
    [
        new() { Wave = SynthWave.Noise, Frequency = 700, Duration = 0.14, Attack = 0.003, Decay = 0.05, Sustain = 0.3, Release = 0.06, LowPass = 1400, Volume = 0.8 },
        new() { Wave = SynthWave.Sine, Frequency = 90, FrequencyEnd = 60, Duration = 0.08, Attack = 0.002, Release = 0.05, Volume = 0.4 },
    ];

    // The bird chirps of the Nature Walk benchmark: four short sine chirps gliding up, a pause, then three gliding down.
    private static IReadOnlyList<SynthLayer> Birdsong()
    {
        var chirps = new List<SynthLayer>();
        for (int i = 0; i < 4; i++)
        {
            double pitch = 2780 + i * 180;
            chirps.Add(new() { Wave = SynthWave.Sine, Frequency = pitch, FrequencyEnd = pitch * 1.45, Start = 0.4 + i * 0.13, Duration = 0.08, Attack = 0.008, Decay = 0.03, Sustain = 0.4, Release = 0.04, Volume = 0.35 });
        }
        for (int i = 0; i < 3; i++)
        {
            double pitch = 3200 - i * 200;
            chirps.Add(new() { Wave = SynthWave.Sine, Frequency = pitch, FrequencyEnd = pitch * 0.8, Start = 2.52 + i * 0.16, Duration = 0.11, Attack = 0.01, Release = 0.05, Volume = 0.25 });
        }
        return chirps;
    }
}

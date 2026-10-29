#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Shared.Audio
{
    /// <summary>
    /// The effects one bus carries, applied in this order: filter, EQ, reverb, compressor. Every one is
    /// off unless turned on, and a bus with none on plays exactly as a bus always did.
    /// </summary>
    public sealed class AudioBusEffects
    {
        public static readonly string[] FilterTypes = { "LowPass", "HighPass", "BandPass" };
        public static readonly string[] ReverbPlaces = { "room", "small room", "hall", "large hall", "cave", "arena", "hangar", "hallway", "bathroom", "forest", "city", "mountains", "underwater", "plate" };

        /// <summary>All of this bus's effects at once: off bypasses them and keeps their settings.</summary>
        public bool Enabled { get; set; } = true;

        public bool FilterEnabled { get; set; }
        /// <summary>LowPass (keeps what is below the frequency), HighPass (above it) or BandPass (around it).</summary>
        public string FilterType { get; set; } = "LowPass";
        /// <summary>Hz (1000 unless set).</summary>
        public float FilterFrequency { get; set; } = 1000f;
        /// <summary>How sharp the filter is: 0.707 is smooth, higher rings (0.7 to 1.5 for a low-pass or high-pass).</summary>
        public float FilterQ { get; set; } = 0.707f;

        public bool EqEnabled { get; set; }
        /// <summary>dB at the low band, -18 to 18 (0 unless set).</summary>
        public float EqLow { get; set; }
        public float EqMid { get; set; }
        public float EqHigh { get; set; }
        public float EqLowFrequency { get; set; } = 150f;
        public float EqMidFrequency { get; set; } = 1000f;
        public float EqHighFrequency { get; set; } = 6000f;

        public bool ReverbEnabled { get; set; }
        /// <summary>One of <see cref="ReverbPlaces"/> ("room" unless set).</summary>
        public string ReverbPlace { get; set; } = "room";
        /// <summary>How much of the reverb is heard, 0 to 1 (0.3 unless set).</summary>
        public float ReverbMix { get; set; } = 0.3f;

        public bool CompressorEnabled { get; set; }
        /// <summary>dB of the bus's peak level above which it is turned down, -60 to 0 (-12 unless set).</summary>
        public float CompressorThreshold { get; set; } = -12f;
        /// <summary>How hard: 4 lets 4 dB over the threshold through as 1 (1 to 20).</summary>
        public float CompressorRatio { get; set; } = 4f;
        /// <summary>Seconds to turn down (0.01 unless set) and back up (0.25 unless set).</summary>
        public float CompressorAttack { get; set; } = 0.01f;
        public float CompressorRelease { get; set; } = 0.25f;

        /// <summary>True when an effect is on and the bus's effects are on.</summary>
        [JsonIgnore] public bool Active => Enabled && (FilterEnabled || EqEnabled || ReverbEnabled || CompressorEnabled);

        public AudioBusEffects Copy() => (AudioBusEffects)MemberwiseClone();

        /// <summary>Every value brought into its range; an unknown filter type or reverb place becomes the default.</summary>
        public AudioBusEffects Normalized()
        {
            AudioBusEffects result = Copy();
            result.FilterType = FilterTypes.FirstOrDefault(type => string.Equals(type, FilterType?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "LowPass";
            result.FilterFrequency = Clamp(FilterFrequency, 20f, 20000f, 1000f);
            result.FilterQ = Clamp(FilterQ, 0.5f, 10f, 0.707f);
            result.EqLow = Clamp(EqLow, -18f, 18f, 0f);
            result.EqMid = Clamp(EqMid, -18f, 18f, 0f);
            result.EqHigh = Clamp(EqHigh, -18f, 18f, 0f);
            result.EqLowFrequency = Clamp(EqLowFrequency, 20f, 20000f, 150f);
            result.EqMidFrequency = Clamp(EqMidFrequency, 20f, 20000f, 1000f);
            result.EqHighFrequency = Clamp(EqHighFrequency, 20f, 20000f, 6000f);
            result.ReverbPlace = ReverbPlaces.FirstOrDefault(place => string.Equals(place, ReverbPlace?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "room";
            result.ReverbMix = Clamp(ReverbMix, 0f, 1f, 0.3f);
            result.CompressorThreshold = Clamp(CompressorThreshold, -60f, 0f, -12f);
            result.CompressorRatio = Clamp(CompressorRatio, 1f, 20f, 4f);
            result.CompressorAttack = Clamp(CompressorAttack, 0f, 5f, 0.01f);
            result.CompressorRelease = Clamp(CompressorRelease, 0f, 10f, 0.25f);
            return result;
        }

        private static float Clamp(float value, float minimum, float maximum, float fallback) =>
            float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

        public bool SameAs(AudioBusEffects other) =>
            JsonSerializer.Serialize(this, AudioMixerSettings.JsonOptions) == JsonSerializer.Serialize(other, AudioMixerSettings.JsonOptions);

        /// <summary>
        /// Part way from <paramref name="from"/> to <paramref name="to"/> (<paramref name="t"/> 0 to 1). An
        /// effect being turned on comes in from where it has no effect (a reverb from no mix, an EQ band from
        /// 0 dB, a compressor from a ratio of 1, a low-pass from fully open); one being turned off goes out
        /// the same way and is switched off at the end. Frequencies move evenly in pitch.
        /// </summary>
        public static AudioBusEffects Blend(AudioBusEffects from, AudioBusEffects to, float t)
        {
            t = float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 1f;
            if (t >= 1f) return to.Copy();
            AudioBusEffects a = from.Normalized(), b = to.Normalized();
            bool aFilter = a.Enabled && a.FilterEnabled, bFilter = b.Enabled && b.FilterEnabled;
            bool aEq = a.Enabled && a.EqEnabled, bEq = b.Enabled && b.EqEnabled;
            bool aReverb = a.Enabled && a.ReverbEnabled, bReverb = b.Enabled && b.ReverbEnabled;
            bool aCompressor = a.Enabled && a.CompressorEnabled, bCompressor = b.Enabled && b.CompressorEnabled;
            var result = new AudioBusEffects
            {
                Enabled = true,
                FilterEnabled = aFilter || bFilter,
                FilterType = bFilter ? b.FilterType : a.FilterType,
                FilterQ = Lerp(aFilter ? a.FilterQ : b.FilterQ, bFilter ? b.FilterQ : a.FilterQ, t),
                EqEnabled = aEq || bEq,
                EqLow = Lerp(aEq ? a.EqLow : 0f, bEq ? b.EqLow : 0f, t),
                EqMid = Lerp(aEq ? a.EqMid : 0f, bEq ? b.EqMid : 0f, t),
                EqHigh = Lerp(aEq ? a.EqHigh : 0f, bEq ? b.EqHigh : 0f, t),
                EqLowFrequency = Pitch(aEq ? a.EqLowFrequency : b.EqLowFrequency, bEq ? b.EqLowFrequency : a.EqLowFrequency, t),
                EqMidFrequency = Pitch(aEq ? a.EqMidFrequency : b.EqMidFrequency, bEq ? b.EqMidFrequency : a.EqMidFrequency, t),
                EqHighFrequency = Pitch(aEq ? a.EqHighFrequency : b.EqHighFrequency, bEq ? b.EqHighFrequency : a.EqHighFrequency, t),
                ReverbEnabled = aReverb || bReverb,
                ReverbPlace = bReverb ? b.ReverbPlace : a.ReverbPlace,
                ReverbMix = Lerp(aReverb ? a.ReverbMix : 0f, bReverb ? b.ReverbMix : 0f, t),
                CompressorEnabled = aCompressor || bCompressor,
                CompressorThreshold = Lerp(aCompressor ? a.CompressorThreshold : b.CompressorThreshold, bCompressor ? b.CompressorThreshold : a.CompressorThreshold, t),
                CompressorRatio = Lerp(aCompressor ? a.CompressorRatio : 1f, bCompressor ? b.CompressorRatio : 1f, t),
                CompressorAttack = Lerp(aCompressor ? a.CompressorAttack : b.CompressorAttack, bCompressor ? b.CompressorAttack : a.CompressorAttack, t),
                CompressorRelease = Lerp(aCompressor ? a.CompressorRelease : b.CompressorRelease, bCompressor ? b.CompressorRelease : a.CompressorRelease, t),
            };
            // A filter type that changes cannot be blended: it moves from open to the new filter.
            string type = result.FilterType;
            float Open(string kind) => kind == "HighPass" ? 20f : kind == "BandPass" ? (aFilter ? a.FilterFrequency : b.FilterFrequency) : 20000f;
            float start = aFilter && a.FilterType == type ? a.FilterFrequency : Open(type);
            float end = bFilter && b.FilterType == type ? b.FilterFrequency : Open(type);
            result.FilterFrequency = Pitch(start, end, t);
            return result;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float Pitch(float a, float b, float t) => a > 0f && b > 0f ? MathF.Exp(MathF.Log(a) + (MathF.Log(b) - MathF.Log(a)) * t) : Lerp(a, b, t);
    }

    /// <summary>One bus in a mix: its volume and its effects.</summary>
    public sealed class AudioBusMix
    {
        /// <summary>0 to 1 (1 unless set).</summary>
        public float Volume { get; set; } = 1f;
        public AudioBusEffects Effects { get; set; } = new();

        public AudioBusMix Copy() => new() { Volume = Volume, Effects = Effects.Copy() };
    }

    /// <summary>
    /// A project's mixer (<c>AudioMixer.json</c> beside the project file, so it travels with an exported
    /// game): the volume and effects each bus starts with, and named snapshots, each a set of bus
    /// volumes and effects a game blends to over a time (<c>Engine.Audio.Snapshot.Blend("Combat", 2)</c>).
    /// </summary>
    public sealed class AudioMixerSettings
    {
        public const string FileName = "AudioMixer.json";

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        public int SchemaVersion { get; set; } = 1;
        /// <summary>Each bus by name ("music", "sfx", or a project's own): how it starts.</summary>
        public Dictionary<string, AudioBusMix> Buses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Each snapshot by name: the buses it sets (a bus it does not name is left as it is).</summary>
        public Dictionary<string, Dictionary<string, AudioBusMix>> Snapshots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static string PathFor(string projectPath) => Path.Combine(projectPath, FileName);

        /// <summary>The project's mixer, or an empty one when it has none or it cannot be read.</summary>
        public static AudioMixerSettings Load(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return new AudioMixerSettings();
            string path = PathFor(projectPath);
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new AudioMixerSettings(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new AudioMixerSettings(); }
        }

        public static AudioMixerSettings Parse(string json)
        {
            AudioMixerSettings settings = JsonSerializer.Deserialize<AudioMixerSettings>(json, JsonOptions) ?? new AudioMixerSettings();
            // Names are kept whatever their case, as buses are.
            settings.Buses = new Dictionary<string, AudioBusMix>(settings.Buses ?? new(), StringComparer.OrdinalIgnoreCase);
            settings.Snapshots = (settings.Snapshots ?? new()).ToDictionary(
                pair => pair.Key, pair => new Dictionary<string, AudioBusMix>(pair.Value ?? new(), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            return settings;
        }

        public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

        public void Save(string projectPath) => File.WriteAllText(PathFor(projectPath), Serialize(), new System.Text.UTF8Encoding(false));

        public AudioMixerSettings Copy() => Parse(Serialize());
    }
}

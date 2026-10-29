using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Genesis.Shared.Audio;
using Vortice.XAudio2;

namespace Genesis.Audio
{
    /// <summary>
    /// The mixer's buses: effects each bus carries (filter, EQ, reverb, compressor; off by default) and
    /// snapshots, named sets of bus volumes and effects a game blends between over a time.
    /// </summary>
    public sealed partial class XAudioSystem
    {
        private readonly Dictionary<string, AudioBusEffects> _busEffects = new(StringComparer.OrdinalIgnoreCase);
        // Buses asked how loud they are (GetBusLevel): their meters stay on.
        private readonly HashSet<string> _meteredBuses = new(StringComparer.OrdinalIgnoreCase);
        // Each compressing bus's gain now, in dB (0 or below).
        private readonly Dictionary<string, float> _compressorDb = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, AudioBusMix>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
        private long _lastMixerUpdate;

        private sealed class SnapshotBlend
        {
            public string Name = "";
            public Dictionary<string, AudioBusMix> From = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, AudioBusMix> To = new(StringComparer.OrdinalIgnoreCase);
            public float Seconds;
            public long Started;
        }

        private SnapshotBlend? _blend;
        private string _currentSnapshot = "";

        /// <summary>
        /// Takes a project's mixer: each bus it names starts at its volume and effects, and its snapshots
        /// are known by name. Buses it does not name are left as they are.
        /// </summary>
        public void ApplyMixer(AudioMixerSettings mixer)
        {
            ArgumentNullException.ThrowIfNull(mixer);
            foreach (KeyValuePair<string, AudioBusMix> bus in mixer.Buses)
            {
                if (bus.Value is null) continue;
                SetBusVolume(bus.Key, bus.Value.Volume);
                SetBusEffects(bus.Key, bus.Value.Effects ?? new AudioBusEffects());
            }
            foreach (KeyValuePair<string, Dictionary<string, AudioBusMix>> snapshot in mixer.Snapshots)
                if (snapshot.Value is not null) DefineSnapshot(snapshot.Key, snapshot.Value);
        }

        public void SetBusEffects(string bus, AudioBusEffects effects)
        {
            ArgumentNullException.ThrowIfNull(effects);
            string name = BusName(bus);
            if (name == "master") return; // the device's own volume: its sounds' buses carry the effects
            AudioBusEffects normalized = effects.Normalized();
            _busEffects[name] = normalized;
            ApplyBus(name);
        }

        public AudioBusEffects GetBusEffects(string bus) =>
            _busEffects.TryGetValue(BusName(bus), out AudioBusEffects? effects) ? effects.Copy() : new AudioBusEffects();

        public float GetBusLevel(string bus)
        {
            string name = BusName(bus);
            if (name == "master") return 0f;
            if (_meteredBuses.Add(name)) ApplyBus(name);
            AudioEngine.BusVoice? voice = _engine.Bus(name, IsMusic(name), create: false);
            return voice is null ? 0f : _engine.ReadPeak(voice);
        }

        // Makes the bus's submix when it needs one (an effect on, or a meter), sets its effects, and
        // moves the bus's playing sounds into it.
        private void ApplyBus(string name)
        {
            AudioBusEffects effects = _busEffects.TryGetValue(name, out AudioBusEffects? found) ? found : new AudioBusEffects();
            bool metered = _meteredBuses.Contains(name);
            bool existed = _engine.Bus(name, IsMusic(name), create: false) is not null;
            AudioEngine.BusVoice? voice = _engine.Bus(name, IsMusic(name), create: effects.Active || metered);
            if (voice is null) return;
            _engine.ApplyBusEffects(voice, effects, metered);
            if (!(effects.Enabled && effects.CompressorEnabled))
            {
                _compressorDb.Remove(name);
                _engine.SetBusGain(voice, 1f);
            }
            if (!existed)
                foreach (ChannelState state in _channels.Values)
                    if (state.Bus == name) Route(state);
        }

        // Sends a playing sound to where its bus now plays: the bus's own submix, or the world's or music's.
        private void Route(ChannelState state)
        {
            if (state.Voice is null) return;
            if (_engine.OutputForBus(state.Bus, IsMusic(state.Bus)) is not { } output) return;
            try
            {
                state.Voice.SetOutputVoices(new[] { new VoiceSendDescriptor { OutputVoice = output } });
                // The new send starts at the device's own routing: pan it again.
                state.PanLeft = state.PanRight = 1f;
            }
            catch (SharpGen.Runtime.SharpGenException) { }
        }

        public void SaveSnapshot(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            _snapshots[name.Trim()] = CurrentMix(_buses.Keys.Concat(_busEffects.Keys));
        }

        public void DefineSnapshot(string name, IReadOnlyDictionary<string, AudioBusMix> buses)
        {
            if (string.IsNullOrWhiteSpace(name) || buses is null) return;
            var mix = new Dictionary<string, AudioBusMix>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, AudioBusMix> bus in buses)
                if (bus.Value is not null && BusName(bus.Key) != "master")
                    mix[BusName(bus.Key)] = new AudioBusMix { Volume = Math.Clamp(float.IsFinite(bus.Value.Volume) ? bus.Value.Volume : 1f, 0f, 2f), Effects = (bus.Value.Effects ?? new()).Normalized() };
            _snapshots[name.Trim()] = mix;
        }

        public bool BlendToSnapshot(string name, float seconds)
        {
            if (string.IsNullOrWhiteSpace(name) || !_snapshots.TryGetValue(name.Trim(), out Dictionary<string, AudioBusMix>? target)) return false;
            _currentSnapshot = _snapshots.Keys.First(key => string.Equals(key, name.Trim(), StringComparison.OrdinalIgnoreCase));
            _blend = new SnapshotBlend
            {
                Name = _currentSnapshot,
                From = CurrentMix(target.Keys),
                To = target.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.OrdinalIgnoreCase),
                Seconds = float.IsFinite(seconds) ? Math.Max(0f, seconds) : 0f,
                Started = Stopwatch.GetTimestamp(),
            };
            AdvanceBlend();
            return true;
        }

        public string CurrentSnapshot => _currentSnapshot;

        public float SnapshotProgress => _blend is null ? 1f : BlendProgress(_blend);

        public IReadOnlyList<string> SnapshotNames => _snapshots.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Stops a blend where it has got to (the buses stay as they are now).</summary>
        public void HoldSnapshotBlend() => _blend = null;

        private Dictionary<string, AudioBusMix> CurrentMix(IEnumerable<string> buses)
        {
            var mix = new Dictionary<string, AudioBusMix>(StringComparer.OrdinalIgnoreCase);
            foreach (string bus in buses)
            {
                string name = BusName(bus);
                if (name == "master" || mix.ContainsKey(name)) continue;
                mix[name] = new AudioBusMix { Volume = GetBusVolume(name), Effects = GetBusEffects(name) };
            }
            return mix;
        }

        private static float BlendProgress(SnapshotBlend blend) =>
            blend.Seconds <= 0f ? 1f : Math.Clamp((float)(Stopwatch.GetElapsedTime(blend.Started).TotalSeconds / blend.Seconds), 0f, 1f);

        private void AdvanceBlend()
        {
            if (_blend is not { } blend) return;
            float t = BlendProgress(blend);
            foreach (KeyValuePair<string, AudioBusMix> target in blend.To)
            {
                AudioBusMix from = blend.From.TryGetValue(target.Key, out AudioBusMix? start) ? start : new AudioBusMix();
                SetBusVolume(target.Key, from.Volume + (target.Value.Volume - from.Volume) * t);
                SetBusEffects(target.Key, AudioBusEffects.Blend(from.Effects, target.Value.Effects, t));
            }
            if (t >= 1f) _blend = null;
        }

        // Once a frame: a snapshot blend moves on, and each compressing bus is turned down by how far its
        // peak is over the threshold, going down over the attack time and back over the release.
        private void UpdateMixer()
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = _lastMixerUpdate == 0 ? 0f : (float)Stopwatch.GetElapsedTime(_lastMixerUpdate, now).TotalSeconds;
            _lastMixerUpdate = now;
            AdvanceBlend();
            foreach (KeyValuePair<string, AudioBusEffects> pair in _busEffects)
            {
                AudioBusEffects effects = pair.Value;
                if (!(effects.Enabled && effects.CompressorEnabled)) continue;
                AudioEngine.BusVoice? voice = _engine.Bus(pair.Key, IsMusic(pair.Key), create: false);
                if (voice is null) continue;
                float peak = _engine.ReadPeak(voice);
                float level = 20f * MathF.Log10(Math.Max(peak, 1e-5f));
                float over = level - effects.CompressorThreshold;
                float target = over > 0f ? -over * (1f - 1f / Math.Max(1f, effects.CompressorRatio)) : 0f;
                float current = _compressorDb.TryGetValue(pair.Key, out float held) ? held : 0f;
                float time = target < current ? effects.CompressorAttack : effects.CompressorRelease;
                float step = time <= 0f ? 1f : 1f - MathF.Exp(-seconds / time);
                current += (target - current) * step;
                _compressorDb[pair.Key] = current;
                _engine.SetBusGain(voice, MathF.Pow(10f, current / 20f));
            }
        }

        /// <summary>How far a compressing bus is turned down now, dB (0 when it is not).</summary>
        public float GetBusCompression(string bus) => _compressorDb.TryGetValue(BusName(bus), out float db) ? db : 0f;
    }
}

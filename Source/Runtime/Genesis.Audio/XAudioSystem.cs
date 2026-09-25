using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Genesis.Shared.Audio;
using Vortice.XAudio2;

namespace Genesis.Audio
{
    /// <summary>
    /// XAudio2-backed <see cref="IAudioSystem"/> with real loop/stop, listener attenuation,
    /// optional .audio.json meta, and simple master/SFX/music buses.
    /// </summary>
    public sealed class XAudioSystem : IAudioSystem, IDisposable
    {
        private readonly AudioEngine _engine;
        private readonly string _projectPath;
        private readonly Dictionary<string, int> _pathToId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, SoundEntry> _sounds = new();
        private readonly Dictionary<int, ChannelState> _channels = new();
        private int _nextSoundId = 1;
        private int _nextChannelId = 1;

        private Vector3 _listenerPos;
        private Vector3 _listenerForward = -Vector3.UnitZ;
        private float _busMaster = 1f;
        private float _busSfx = 1f;
        private float _busMusic = 1f;

        private sealed class SoundEntry
        {
            public SoundEffect Effect = null!;
            public float Gain = 1f;
            public float Pitch = 1f;
            public bool Loop;
            public bool Spatial;
            public string Bus = "sfx";
            /// <summary>Authored falloff shape; also the source of Gain/Loop/Spatial/Bus above.</summary>
            public AudioAssetSettings Settings = new();
        }

        private sealed class ChannelState
        {
            public int Id;
            public int SoundId;
            public bool Loop;
            public bool Spatial;
            public Vector3 Position;
            public float BaseVolume = 1f;
            public string Bus = "sfx";
            public AudioAssetSettings Settings = new();
            public IXAudio2SourceVoice? Voice;
        }

        public XAudioSystem(string projectPath)
        {
            _projectPath = projectPath ?? string.Empty;
            _engine = new AudioEngine();
        }

        public float MasterVolume
        {
            get => _busMaster;
            set
            {
                _busMaster = Math.Clamp(value, 0f, 2f);
                if (_engine != null)
                    _engine.MasterVolume = _busMaster;
            }
        }

        public void SetBusVolume(string bus, float volume)
        {
            float v = Math.Clamp(volume, 0f, 2f);
            switch ((bus ?? "").Trim().ToLowerInvariant())
            {
                case "music": _busMusic = v; break;
                case "master": MasterVolume = v; break;
                default: _busSfx = v; break;
            }
            RefreshChannelVolumes();
        }

        public int LoadSound(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath)) return 0;
            if (_pathToId.TryGetValue(projectRelativePath, out int cached)) return cached;

            string abs = ResolvePath(projectRelativePath);

            // A .audio.json resource may be handed to us directly (that is what the Audio
            // Editor saves, and what PGSL PlaySound receives from an asset field); resolve
            // it to the WAV it points at so both spellings of "play this sound" work.
            AudioAssetSettings? authored = null;
            if (abs.EndsWith(".audio.json", StringComparison.OrdinalIgnoreCase))
            {
                authored = AudioAssetSettings.Load(abs);
                if (authored?.Source is not { Length: > 0 } source) return 0;
                abs = ResolvePath(source);
            }

            var effect = SoundEffect.FromWavFile(abs);
            if (effect == null) return 0;

            var entry = new SoundEntry { Effect = effect };
            entry.Bus = effect.DurationInSeconds > 10f ? "music" : "sfx";
            ApplyAudioMeta(abs, entry, authored);

            int id = _nextSoundId++;
            _sounds[id] = entry;
            _pathToId[projectRelativePath] = id;
            return id;
        }

        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            if (!_sounds.TryGetValue(soundId, out var entry)) return AudioChannel.Invalid;

            bool wantLoop = loop || entry.Loop;
            float gain = volume * entry.Gain * BusGain(entry.Bus);
            var voice = entry.Effect.PlayVoice(_engine, gain, pitch * entry.Pitch, wantLoop);
            if (voice == null) return AudioChannel.Invalid;

            int channelId = _nextChannelId++;
            _channels[channelId] = new ChannelState
            {
                Id = channelId,
                SoundId = soundId,
                Loop = wantLoop,
                Spatial = entry.Spatial,
                BaseVolume = volume * entry.Gain,
                Bus = entry.Bus,
                Settings = entry.Settings,
                Voice = voice,
                Position = _listenerPos,
            };
            ApplySpatial(channelId);
            return new AudioChannel(channelId);
        }

        public AudioChannel PlayAt(int soundId, Vector3 worldPos, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            var ch = Play(soundId, volume, pitch, loop);
            if (!ch.IsValid) return ch;
            if (_channels.TryGetValue(ch.Id, out var state))
            {
                state.Spatial = true;
                state.Position = worldPos;
                _channels[ch.Id] = state;
                ApplySpatial(ch.Id);
            }
            return ch;
        }

        public void Stop(AudioChannel channel)
        {
            if (!channel.IsValid) return;
            if (_channels.TryGetValue(channel.Id, out var state))
            {
                try { state.Voice?.Stop(); state.Voice?.FlushSourceBuffers(); } catch { }
                _channels.Remove(channel.Id);
            }
        }

        public void StopAll()
        {
            foreach (var kv in _channels)
            {
                try { kv.Value.Voice?.Stop(); kv.Value.Voice?.FlushSourceBuffers(); } catch { }
            }
            _channels.Clear();
        }

        public bool IsPlaying(AudioChannel channel)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out var state))
                return false;
            try
            {
                if (state.Voice == null) return false;
                return state.Voice.State.BuffersQueued > 0 || state.Loop;
            }
            catch
            {
                return false;
            }
        }

        public void SetChannelVolume(AudioChannel channel, float volume)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            state.BaseVolume = Math.Clamp(volume, 0f, 2f);
            ApplySpatial(channel.Id);
        }

        public void SetChannelPosition(AudioChannel channel, Vector3 position)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            state.Spatial = true;
            state.Position = position;
            ApplySpatial(channel.Id);
        }

        public void SetListener(Vector3 position, Vector3 forward)
        {
            _listenerPos = position;
            if (forward.LengthSquared() > 1e-6f)
                _listenerForward = Vector3.Normalize(forward);
            foreach (var id in _channels.Keys)
                ApplySpatial(id);
        }

        public void Update()
        {
            _engine?.Update();
            if (_channels.Count == 0) return;

            var dead = new List<int>();
            foreach (var kv in _channels)
            {
                try
                {
                    if (kv.Value.Voice == null) { dead.Add(kv.Key); continue; }
                    if (!kv.Value.Loop && kv.Value.Voice.State.BuffersQueued == 0)
                        dead.Add(kv.Key);
                }
                catch
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (int id in dead)
                _channels.Remove(id);
        }

        public void Dispose() => _engine?.Dispose();

        private float BusGain(string bus) => (bus ?? "").ToLowerInvariant() switch
        {
            "music" => _busMusic * _busMaster,
            "master" => _busMaster,
            _ => _busSfx * _busMaster,
        };

        private void RefreshChannelVolumes()
        {
            foreach (var kv in _channels)
                ApplySpatial(kv.Key);
        }

        private void ApplySpatial(int channelId)
        {
            if (!_channels.TryGetValue(channelId, out var state) || state.Voice == null) return;
            float vol = state.BaseVolume * BusGain(state.Bus);
            if (state.Spatial)
            {
                // Authored MinDistance / MaxDistance / Falloff, shared with the editor's
                // audition so the designer hears the curve they are dialling in.
                float dist = Vector3.Distance(_listenerPos, state.Position);
                vol *= state.Settings.AttenuationAt(dist);
            }

            try
            {
                state.Voice.SetVolume(Math.Clamp(vol, 0f, 2f));
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // The voice was destroyed underneath us (device change / recycle); Update()
                // reaps it on the next frame, so a missed volume set is not worth surfacing.
            }
        }

        private void ApplyAudioMeta(string wavPath, SoundEntry entry, AudioAssetSettings? authored)
        {
            // Either the caller already loaded a .audio.json resource, or we look for a
            // sidecar next to the WAV (Jump.wav → Jump.audio.json, or Jump.wav.audio.json).
            AudioAssetSettings? settings = authored;
            if (settings == null)
            {
                string meta = Path.ChangeExtension(wavPath, ".audio.json");
                if (!File.Exists(meta)) meta = wavPath + ".audio.json";
                settings = AudioAssetSettings.Load(meta);
            }

            if (settings == null) return;

            entry.Settings = settings;
            entry.Gain = settings.Volume;
            entry.Pitch = settings.Pitch <= 0f ? 1f : settings.Pitch;
            entry.Loop = settings.Loop;
            entry.Spatial = settings.Spatial;
            if (!string.IsNullOrWhiteSpace(settings.Bus)) entry.Bus = settings.Bus;
        }

        private string ResolvePath(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(_projectPath)) return projectRelativePath;
            return ResourceNames.ResolveFile(_projectPath, projectRelativePath, ResourceType.Audio);
        }
    }
}

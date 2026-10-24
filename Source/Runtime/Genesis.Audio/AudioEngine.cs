using System;
using System.Collections.Generic;
using Vortice.XAudio2;

namespace Genesis.Audio
{
    public sealed class AudioEngine : IDisposable
    {
        private readonly IXAudio2              _xaudio;
        private readonly IXAudio2MasteringVoice _master;
        // Each voice with the sound it is playing. The voice reads the sound's own samples, so the
        // sound is kept from the collector until the voice is gone.
        private readonly List<(IXAudio2SourceVoice Voice, SoundEffect? Sound)> _active = new();
        private bool _disposed;

        public IXAudio2 XAudio => _xaudio;

        /// <summary>Channels of the device being played to: 2 for stereo, 6 for 5.1.</summary>
        public int OutputChannels
        {
            get
            {
                try { return _disposed ? 0 : (int)_master.VoiceDetails.InputChannels; }
                catch (SharpGen.Runtime.SharpGenException) { return 0; }
            }
        }

        private float _masterVolume = 1f;
        public float MasterVolume
        {
            get => _masterVolume;
            set { _masterVolume = value; if (!_disposed) _master.SetVolume(value, 0); }
        }

        public AudioEngine()
        {
            _xaudio = XAudio2.XAudio2Create();
            _master  = _xaudio.CreateMasteringVoice();
        }

        // ── Acoustics ──────────────────────────────────────────────────────────────────────────
        // Sounds play through one of two stereo submixes, made on first use: the world's (sound
        // effects, through a reverb for the room's acoustics) and the music's (through a low-pass
        // filter that muffles it). A device that cannot make them plays straight to the speakers.

        private IXAudio2SubmixVoice? _world, _music;
        private bool _routingFailed;
        private string _acoustics = "outdoors";
        private float _acousticsAmount = .5f, _musicMuffle;

        /// <summary>The acoustics sound effects play in: "outdoors" (none), "room", "hall" or "cave".</summary>
        public string Acoustics
        {
            get => _acoustics;
            set { _acoustics = Preset(value) == null && !IsOutdoors(value) ? _acoustics : value.Trim().ToLowerInvariant(); ApplyAcoustics(); }
        }

        /// <summary>How much of the acoustics is heard, 0 to 1 (0.5 unless set).</summary>
        public float AcousticsAmount
        {
            get => _acousticsAmount;
            set { _acousticsAmount = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : _acousticsAmount; ApplyAcoustics(); }
        }

        /// <summary>How muffled the music is, 0 (clear) to 1 (heard through a wall).</summary>
        public float MusicMuffle
        {
            get => _musicMuffle;
            set { _musicMuffle = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : _musicMuffle; ApplyMuffle(); }
        }

        /// <summary>The submix a new voice plays into, music or the world's; null to play to the speakers.</summary>
        internal IXAudio2Voice? OutputFor(bool music)
        {
            if (_disposed || _routingFailed) return null;
            if (_world == null)
            {
                try
                {
                    uint rate = _master.VoiceDetails.InputSampleRate;
                    using (SharpGen.Runtime.ComObject reverb = Vortice.XAudio2.Fx.Fx.XAudio2CreateReverb())
                    {
                        var chain = new[] { new EffectDescriptor(reverb, 2) { InitialState = false } };
                        _world = _xaudio.CreateSubmixVoice(2, rate, SubmixVoiceFlags.None, 0, chain);
                    }
                    _music = _xaudio.CreateSubmixVoice(2, rate, SubmixVoiceFlags.UseFilter, 0, null);
                    ApplyAcoustics();
                    ApplyMuffle();
                }
                catch (Exception)
                {
                    _routingFailed = true;
                    return null;
                }
            }
            return music ? _music : _world;
        }

        private static bool IsOutdoors(string? name) => string.Equals(name?.Trim(), "outdoors", StringComparison.OrdinalIgnoreCase);

        private static Vortice.XAudio2.Fx.ReverbI3DL2Parameters? Preset(string? name) => name?.Trim().ToLowerInvariant() switch
        {
            "room" => Vortice.XAudio2.Fx.Presets.Room,
            "hall" => Vortice.XAudio2.Fx.Presets.ConcertHall,
            "cave" => Vortice.XAudio2.Fx.Presets.Cave,
            _ => null,
        };

        private void ApplyAcoustics()
        {
            if (_disposed || _world == null) return;
            try
            {
                if (Preset(_acoustics) is not { } preset || _acousticsAmount <= 0f)
                {
                    _world.DisableEffect(0);
                    return;
                }
                // The preset's own wet/dry is all reverb: here the dry sound stays, with up to 60% reverb.
                preset.WetDryMix = 60f * _acousticsAmount;
                _world.SetEffectParameters(0, preset, false, 0);
                _world.EnableEffect(0);
            }
            catch (SharpGen.Runtime.SharpGenException) { }
        }

        private void ApplyMuffle()
        {
            if (_disposed || _music == null) return;
            try
            {
                // From 20 kHz (clear) down to 350 Hz (through a wall), even in pitch between.
                float rate = Math.Max(8000f, _master.VoiceDetails.InputSampleRate);
                float cutoff = 20000f * MathF.Pow(350f / 20000f, _musicMuffle);
                float frequency = Math.Min(1f, 2f * MathF.Sin(MathF.PI * Math.Min(cutoff, rate / 2f) / rate));
                _music.SetFilterParameters(new FilterParameters { Type = FilterType.LowPassFilter, Frequency = frequency, OneOverQ = 1f }, 0);
            }
            catch (SharpGen.Runtime.SharpGenException) { }
        }

        // Call once per frame to recycle finished source voices.
        public void Update()
        {
            if (_disposed) return;
            lock (_active)
            {
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    if (_active[i].Voice.State.BuffersQueued == 0)
                    {
                        _active[i].Voice.DestroyVoice();
                        _active[i].Voice.Dispose();
                        _active.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Voices that are still playing or waiting to be recycled.</summary>
        public int ActiveVoices
        {
            get { lock (_active) return _active.Count; }
        }

        internal void Track(IXAudio2SourceVoice voice, SoundEffect? sound = null)
        {
            if (_disposed) { try { voice.DestroyVoice(); voice.Dispose(); } catch { } return; }
            lock (_active)
            {
                _active.Add((voice, sound));
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Safety net: if an AudioEngine is leaked without Dispose() (e.g. a demo
        // scene torn down without OnExit), the finalizer still stops the native
        // engine so its audio thread can't fire IXAudio2EngineCallback into a
        // managed shadow that the GC has already collected.
        ~AudioEngine()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            // Halt the audio processing thread FIRST. XAudio2 raises engine
            // callbacks (OnProcessingPassStart) from this thread; if it keeps
            // running after the managed callback shadow is collected we get the
            // "Shadow Vortice.XAudio2.IXAudio2EngineCallback is dead" crash.
            // Stopping the engine before releasing anything closes that window.
            try { _xaudio?.StopEngine(); } catch { }

            lock (_active)
            {
                foreach (var v in _active) { try { v.Voice.DestroyVoice(); v.Voice.Dispose(); } catch { } }
                _active.Clear();
            }

            // The submixes after the voices playing into them, before the speakers they play to.
            foreach (IXAudio2SubmixVoice? submix in new[] { _world, _music })
                try { submix?.DestroyVoice(); submix?.Dispose(); } catch { }
            _world = _music = null;

            try { _master?.Dispose(); } catch { }
            try { _xaudio?.Dispose(); } catch { }
        }
    }
}

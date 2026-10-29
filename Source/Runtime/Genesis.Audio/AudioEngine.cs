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
                    // Stage 1: a bus's own submix (stage 0, see Bus) plays into these.
                    using (SharpGen.Runtime.ComObject reverb = Vortice.XAudio2.Fx.Fx.XAudio2CreateReverb())
                    {
                        var chain = new[] { new EffectDescriptor(reverb, 2) { InitialState = false } };
                        _world = _xaudio.CreateSubmixVoice(2, rate, SubmixVoiceFlags.None, 1, chain);
                    }
                    _music = _xaudio.CreateSubmixVoice(2, rate, SubmixVoiceFlags.UseFilter, 1, null);
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

        // ── Bus effects ────────────────────────────────────────────────────────────────────────
        // A bus that carries effects (or is being measured) gets its own stereo submix, made on first
        // use, between its sounds and the world's or the music's submix: its filter, then an EQ (the
        // system's FXEQ), a reverb and a level meter, then its volume, which the compressor turns down.
        // A bus without one plays straight into the world's or music's submix as it always did.

        internal sealed class BusVoice
        {
            public IXAudio2SubmixVoice Voice = null!;
            public int EqIndex = -1, ReverbIndex = -1, MeterIndex = -1;
            public bool MeterOn;
        }

        private readonly Dictionary<string, BusVoice> _busVoices = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The bus's own submix, made if <paramref name="create"/>; null without one (or a device that cannot make it).</summary>
        internal BusVoice? Bus(string bus, bool music, bool create)
        {
            if (_busVoices.TryGetValue(bus, out BusVoice? found)) return found;
            if (!create || _disposed || OutputFor(music) is not { } output) return null;
            var chain = new List<EffectDescriptor>();
            var made = new List<SharpGen.Runtime.IUnknown>();
            var busVoice = new BusVoice();
            try
            {
                try
                {
                    SharpGen.Runtime.IUnknown eq = Vortice.XAPO.XAPO.CreateFX(Vortice.XAPO.XAPO.CLSID_FXEQ);
                    made.Add(eq);
                    busVoice.EqIndex = chain.Count;
                    chain.Add(new EffectDescriptor(eq, 2) { InitialState = false });
                }
                catch (Exception) { busVoice.EqIndex = -1; } // no FXEQ on this system: the EQ is left out
                SharpGen.Runtime.ComObject reverb = Vortice.XAudio2.Fx.Fx.XAudio2CreateReverb();
                made.Add(reverb);
                busVoice.ReverbIndex = chain.Count;
                chain.Add(new EffectDescriptor(reverb, 2) { InitialState = false });
                SharpGen.Runtime.ComObject meter = Vortice.XAudio2.Fx.Fx.XAudio2CreateVolumeMeter();
                made.Add(meter);
                busVoice.MeterIndex = chain.Count;
                chain.Add(new EffectDescriptor(meter, 2) { InitialState = false });
                uint rate = _master.VoiceDetails.InputSampleRate;
                busVoice.Voice = _xaudio.CreateSubmixVoice(2, rate, SubmixVoiceFlags.UseFilter, 0, chain.ToArray());
                busVoice.Voice.SetOutputVoices(new[] { new VoiceSendDescriptor { OutputVoice = output } });
            }
            catch (Exception)
            {
                try { busVoice.Voice?.DestroyVoice(); busVoice.Voice?.Dispose(); } catch { }
                return null;
            }
            finally
            {
                // The voice holds its own references to its effects.
                foreach (SharpGen.Runtime.IUnknown effect in made)
                    try { (effect as IDisposable)?.Dispose(); } catch { }
            }
            _busVoices[bus] = busVoice;
            return busVoice;
        }

        /// <summary>Where a sound on <paramref name="bus"/> plays: the bus's own submix when it has one, else the world's or music's.</summary>
        internal IXAudio2Voice? OutputForBus(string bus, bool music) =>
            _busVoices.TryGetValue(bus, out BusVoice? found) ? found.Voice : OutputFor(music);

        /// <summary>Sets a bus's filter, EQ and reverb (the compressor works through <see cref="SetBusGain"/>); the meter runs while <paramref name="meter"/>.</summary>
        internal void ApplyBusEffects(BusVoice bus, Genesis.Shared.Audio.AudioBusEffects effects, bool meter)
        {
            if (_disposed) return;
            Genesis.Shared.Audio.AudioBusEffects e = effects.Normalized();
            try
            {
                float rate = Math.Max(8000f, _master.VoiceDetails.InputSampleRate);
                FilterParameters filter = e.Enabled && e.FilterEnabled
                    ? new FilterParameters
                    {
                        Type = e.FilterType switch { "HighPass" => FilterType.HighPassFilter, "BandPass" => FilterType.BandPassFilter, _ => FilterType.LowPassFilter },
                        Frequency = Math.Min(1f, 2f * MathF.Sin(MathF.PI * Math.Min(e.FilterFrequency, rate / 2f) / rate)),
                        OneOverQ = Math.Clamp(1f / e.FilterQ, 0.05f, 1.5f),
                    }
                    // The filter's own resting state: a low-pass fully open, which XAudio2 passes through.
                    : new FilterParameters { Type = FilterType.LowPassFilter, Frequency = 1f, OneOverQ = 1f };
                bus.Voice.SetFilterParameters(filter, 0);

                if (bus.EqIndex >= 0)
                {
                    if (e.Enabled && e.EqEnabled)
                    {
                        static float Gain(float decibels) => Math.Clamp(MathF.Pow(10f, decibels / 20f), 0.126f, 7.94f);
                        var eq = new Vortice.XAPO.EqualizerParameters
                        {
                            FrequencyCenter0 = e.EqLowFrequency, Gain0 = Gain(e.EqLow), Bandwidth0 = 2f,
                            FrequencyCenter1 = e.EqMidFrequency, Gain1 = Gain(e.EqMid), Bandwidth1 = 1.5f,
                            FrequencyCenter2 = e.EqHighFrequency, Gain2 = Gain(e.EqHigh), Bandwidth2 = 2f,
                            FrequencyCenter3 = 10000f, Gain3 = 1f, Bandwidth3 = 1f,
                        };
                        bus.Voice.SetEffectParameters((uint)bus.EqIndex, eq, 0);
                        bus.Voice.EnableEffect((uint)bus.EqIndex);
                    }
                    else bus.Voice.DisableEffect((uint)bus.EqIndex);
                }

                if (e.Enabled && e.ReverbEnabled && e.ReverbMix > 0f)
                {
                    Vortice.XAudio2.Fx.ReverbI3DL2Parameters preset = ReverbPreset(e.ReverbPlace);
                    preset.WetDryMix = 100f * e.ReverbMix;
                    bus.Voice.SetEffectParameters((uint)bus.ReverbIndex, preset, false, 0);
                    bus.Voice.EnableEffect((uint)bus.ReverbIndex);
                }
                else bus.Voice.DisableEffect((uint)bus.ReverbIndex);

                bus.MeterOn = meter || (e.Enabled && e.CompressorEnabled);
                if (bus.MeterOn) bus.Voice.EnableEffect((uint)bus.MeterIndex);
                else bus.Voice.DisableEffect((uint)bus.MeterIndex);
            }
            catch (SharpGen.Runtime.SharpGenException) { }
        }

        private static Vortice.XAudio2.Fx.ReverbI3DL2Parameters ReverbPreset(string place) => place switch
        {
            "small room" => Vortice.XAudio2.Fx.Presets.SmallRoom,
            "hall" => Vortice.XAudio2.Fx.Presets.ConcertHall,
            "large hall" => Vortice.XAudio2.Fx.Presets.LargeHall,
            "cave" => Vortice.XAudio2.Fx.Presets.Cave,
            "arena" => Vortice.XAudio2.Fx.Presets.Arena,
            "hangar" => Vortice.XAudio2.Fx.Presets.Hangar,
            "hallway" => Vortice.XAudio2.Fx.Presets.Hallway,
            "bathroom" => Vortice.XAudio2.Fx.Presets.BathRoom,
            "forest" => Vortice.XAudio2.Fx.Presets.Forest,
            "city" => Vortice.XAudio2.Fx.Presets.City,
            "mountains" => Vortice.XAudio2.Fx.Presets.Mountains,
            "underwater" => Vortice.XAudio2.Fx.Presets.UnderWater,
            "plate" => Vortice.XAudio2.Fx.Presets.Plate,
            _ => Vortice.XAudio2.Fx.Presets.Room,
        };

        /// <summary>The bus's volume after its effects (the compressor's gain), 0 to 1.</summary>
        internal void SetBusGain(BusVoice bus, float gain)
        {
            if (_disposed) return;
            try { bus.Voice.SetVolume(Math.Clamp(gain, 0f, 4f), 0); }
            catch (SharpGen.Runtime.SharpGenException) { }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private unsafe struct MeterLevels
        {
            public float* Peak;
            public float* Rms;
            public uint Channels;
        }

        /// <summary>The bus's peak level over the mixer's last pass (before its volume), or 0 while its meter is off.</summary>
        internal unsafe float ReadPeak(BusVoice bus)
        {
            if (_disposed || !bus.MeterOn) return 0f;
            float* peak = stackalloc float[2];
            float* rms = stackalloc float[2];
            var levels = new MeterLevels { Peak = peak, Rms = rms, Channels = 2 };
            try
            {
                bus.Voice.GetEffectParameters((uint)bus.MeterIndex, new Span<byte>(&levels, sizeof(MeterLevels)));
                return Math.Max(peak[0], peak[1]);
            }
            catch (SharpGen.Runtime.SharpGenException) { return 0f; }
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

            // The submixes after the voices playing into them, before the speakers they play to: the
            // buses' first, as they play into the world's and the music's.
            foreach (BusVoice bus in _busVoices.Values)
                try { bus.Voice.DestroyVoice(); bus.Voice.Dispose(); } catch { }
            _busVoices.Clear();
            foreach (IXAudio2SubmixVoice? submix in new[] { _world, _music })
                try { submix?.DestroyVoice(); submix?.Dispose(); } catch { }
            _world = _music = null;

            try { _master?.Dispose(); } catch { }
            try { _xaudio?.Dispose(); } catch { }
        }
    }
}

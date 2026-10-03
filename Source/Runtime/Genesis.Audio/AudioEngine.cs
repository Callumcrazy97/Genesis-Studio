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

            try { _master?.Dispose(); } catch { }
            try { _xaudio?.Dispose(); } catch { }
        }
    }
}

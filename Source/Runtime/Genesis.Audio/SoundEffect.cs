using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace Genesis.Audio
{
    public sealed class SoundEffect
    {
        private readonly WaveFormat _format;
        // The samples live where the collector never moves them, so the sound card can be handed
        // their address. Every play of the sound reads this one copy.
        private readonly byte[]     _data;
        private readonly IntPtr     _samples;

        public float DurationInSeconds
        {
            get
            {
                if (_format == null || _data == null) return 0f;
                float bytesPerSec = _format.Channels * (_format.BitsPerSample / 8) * _format.SampleRate;
                if (bytesPerSec <= 0f) return 0f;
                return _data.Length / bytesPerSec;
            }
        }

        /// <summary>1 for mono, 2 for stereo.</summary>
        public int Channels => _format?.Channels ?? 0;

        /// <summary>The memory the decoded samples take.</summary>
        public long SampleBytes => _data?.LongLength ?? 0;

        private SoundEffect(WaveFormat fmt, byte[] data)
        {
            _format = fmt;
            _data = data;
            _samples = data.Length == 0 ? IntPtr.Zero : Marshal.UnsafeAddrOfPinnedArrayElement(data, 0);
        }

        /// <summary>An array for a sound's samples that stays where it is for as long as it lives.</summary>
        private static byte[] SampleArray(int bytes) => GC.AllocateUninitializedArray<byte>(bytes, pinned: true);

        /// <summary>
        /// What a voice is given to play: this sound's own samples, not a copy. A copy made for
        /// each play was never given back, so a game lost a sound's whole size every time it
        /// played it. The sound must outlive the voice; <see cref="AudioEngine"/> sees to that.
        /// </summary>
        public AudioBuffer CreatePlaybackBuffer(bool loop)
        {
            var buffer = new AudioBuffer(_samples, (uint)_data.Length, loop ? BufferFlags.None : BufferFlags.EndOfStream);
            if (loop)
                buffer.LoopCount = 255; // XAUDIO2_LOOP_INFINITE
            return buffer;
        }

        // ── Playback ──────────────────────────────────────────────────────────────

        public void Play(AudioEngine engine, float volume = 1f, float pitch = 1f)
            => PlayVoice(engine, volume, pitch, loop: false);

        /// <summary>Start a source voice; returns the tracked voice for Stop/loop control.</summary>
        public IXAudio2SourceVoice? PlayVoice(AudioEngine engine, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            if (engine == null || _data.Length == 0) return null;
            try
            {
                var voice = engine.XAudio.CreateSourceVoice(_format, false);
                voice.SetVolume(volume);
                if (MathF.Abs(pitch - 1f) > 0.001f)
                    voice.SetFrequencyRatio(pitch, 0);
                voice.SubmitSourceBuffer(CreatePlaybackBuffer(loop));
                voice.Start();
                engine.Track(voice, this);
                return voice;
            }
            catch
            {
                return null;
            }
        }

        // ── Factory: procedural tone ──────────────────────────────────────────────

        public static SoundEffect Tone(float frequency, float seconds,
            int sampleRate = 44100, float amplitude = 0.4f)
        {
            int    samples    = (int)(sampleRate * seconds);
            var    pcm        = new short[samples];
            int    attackLen  = Math.Min((int)(sampleRate * 0.01f), samples / 4);
            int    releaseLen = Math.Min((int)(sampleRate * 0.03f), samples / 4);
            double phaseStep  = 2.0 * Math.PI * frequency / sampleRate;
            double phase      = 0;

            for (int i = 0; i < samples; i++)
            {
                double env = 1.0;
                if (i < attackLen)       env = (double)i / attackLen;
                else if (i >= samples - releaseLen)
                    env = (double)(samples - i) / releaseLen;

                pcm[i] = (short)(Math.Sin(phase) * amplitude * env * short.MaxValue);
                phase += phaseStep;
            }

            var data = SampleArray(pcm.Length * 2);
            Buffer.BlockCopy(pcm, 0, data, 0, data.Length);

            var fmt = new WaveFormat(sampleRate, 16, 1);
            return new SoundEffect(fmt, data);
        }

        // ── Factory: raw synthesized PCM (mono 16-bit) ─────────────────────────────

        public static SoundEffect FromPcm(short[] samples, int sampleRate = 44100)
        {
            var data = SampleArray(samples.Length * 2);
            Buffer.BlockCopy(samples, 0, data, 0, data.Length);
            return new SoundEffect(new WaveFormat(sampleRate, 16, 1), data);
        }

        // ── Factory: WAV file ─────────────────────────────────────────────────────

        public static SoundEffect? FromWavFile(string path, Genesis.Shared.Audio.AudioAssetSettings? settings = null)
        {
            try
            {
                var clip = Genesis.Shared.Audio.PcmAudioClip.Load(path);
                if (settings is not null) clip = clip.ApplyRegion(settings);
                byte[] data = SampleArray(clip.Samples.Length * 2);
                Buffer.BlockCopy(clip.Samples, 0, data, 0, data.Length);
                return new SoundEffect(new WaveFormat(clip.SampleRate, 16, clip.Channels), data);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                return null;
            }
        }
    }
}
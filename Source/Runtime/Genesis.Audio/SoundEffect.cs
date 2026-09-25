using System;
using System.IO;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace Genesis.Audio
{
    public sealed class SoundEffect
    {
        private readonly WaveFormat _format;
        private readonly byte[]     _data;

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

        private SoundEffect(WaveFormat fmt, byte[] data) { _format = fmt; _data = data; }

        // ── Playback ──────────────────────────────────────────────────────────────

        public void Play(AudioEngine engine, float volume = 1f, float pitch = 1f)
            => PlayVoice(engine, volume, pitch, loop: false);

        /// <summary>Start a source voice; returns the tracked voice for Stop/loop control.</summary>
        public IXAudio2SourceVoice? PlayVoice(AudioEngine engine, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            if (engine == null) return null;
            try
            {
                var voice = engine.XAudio.CreateSourceVoice(_format, false);
                voice.SetVolume(volume);
                if (MathF.Abs(pitch - 1f) > 0.001f)
                    voice.SetFrequencyRatio(pitch, 0);
                var flags = loop ? BufferFlags.None : BufferFlags.EndOfStream;
                var buffer = new AudioBuffer(_data, flags);
                if (loop)
                    buffer.LoopCount = 255; // XAUDIO2_LOOP_INFINITE
                voice.SubmitSourceBuffer(buffer);
                voice.Start();
                engine.Track(voice);
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

            var data = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, data, 0, data.Length);

            var fmt = new WaveFormat(sampleRate, 16, 1);
            return new SoundEffect(fmt, data);
        }

        // ── Factory: raw synthesized PCM (mono 16-bit) ─────────────────────────────

        public static SoundEffect FromPcm(short[] samples, int sampleRate = 44100)
        {
            var data = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, data, 0, data.Length);
            return new SoundEffect(new WaveFormat(sampleRate, 16, 1), data);
        }

        // ── Factory: WAV file ─────────────────────────────────────────────────────

        public static SoundEffect? FromWavFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);

                // RIFF header
                if (new string(reader.ReadChars(4)) != "RIFF") return null;
                reader.ReadInt32(); // file size
                if (new string(reader.ReadChars(4)) != "WAVE") return null;

                WaveFormat? fmt   = null;
                byte[]?     data  = null;

                while (stream.Position < stream.Length - 8)
                {
                    string chunk = new string(reader.ReadChars(4));
                    int    size  = reader.ReadInt32();
                    long   next  = stream.Position + size;

                    if (chunk == "fmt ")
                    {
                        short audioFmt = reader.ReadInt16();
                        short channels = reader.ReadInt16();
                        int   rate     = reader.ReadInt32();
                        reader.ReadInt32(); // byte rate
                        reader.ReadInt16(); // block align
                        short bits = reader.ReadInt16();
                        fmt = new WaveFormat(rate, bits, channels);
                    }
                    else if (chunk == "data")
                    {
                        data = reader.ReadBytes(size);
                    }

                    stream.Position = next;
                }

                if (fmt == null || data == null) return null;
                return new SoundEffect(fmt, data);
            }
            catch { return null; }
        }
    }
}

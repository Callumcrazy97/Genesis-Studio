#nullable enable
using System;
using System.IO;

namespace Genesis.Shared.Audio;

/// <summary>The decoded, interleaved PCM shared by the editor's waveform and game playback.</summary>
public sealed record PcmAudioClip(short[] Samples, int SampleRate, int Channels)
{
    public double Duration => Samples.Length / (double)(SampleRate * Channels);

    public PcmAudioClip ApplyRegion(AudioAssetSettings settings)
    {
        if (!float.IsFinite(settings.TrimStart) || !float.IsFinite(settings.TrimEnd)
            || !float.IsFinite(settings.FadeIn) || !float.IsFinite(settings.FadeOut)
            || settings.TrimStart < 0 || settings.TrimEnd < 0 || settings.FadeIn < 0 || settings.FadeOut < 0)
            throw new InvalidDataException("Audio region values must be finite and non-negative.");
        int frames = Samples.Length / Channels;
        int start = (int)Math.Min(frames, Math.Round(settings.TrimStart * SampleRate));
        int end = settings.TrimEnd == 0 ? frames : (int)Math.Min(frames, Math.Round(settings.TrimEnd * SampleRate));
        if (end <= start) throw new InvalidDataException("The audio region must contain at least one frame.");
        short[] output = Samples.AsSpan(start * Channels, (end - start) * Channels).ToArray();
        int count = end - start;
        double fadeIn = settings.FadeIn * SampleRate;
        double fadeOut = settings.FadeOut * SampleRate;
        for (int frame = 0; frame < count; frame++)
        {
            double gain = (fadeIn > 0 ? Math.Min(1, frame / fadeIn) : 1)
                * (fadeOut > 0 ? Math.Min(1, (count - 1 - frame) / fadeOut) : 1);
            for (int channel = 0; channel < Channels; channel++)
                output[frame * Channels + channel] = (short)Math.Round(output[frame * Channels + channel] * gain);
        }
        return new(output, SampleRate, Channels);
    }

    /// <summary>
    /// Reads a WAV or an Ogg Vorbis file, whichever it is: the file's first bytes decide, not its
    /// name. Everything that plays or shows a sound goes through here.
    /// </summary>
    public static PcmAudioClip Load(string path)
    {
        byte[] magic = new byte[4];
        using (FileStream probe = File.OpenRead(path))
            if (probe.Read(magic, 0, 4) < 4) throw new InvalidDataException("The audio file is empty.");
        return magic[0] == (byte)'O' && magic[1] == (byte)'g' && magic[2] == (byte)'g' && magic[3] == (byte)'S'
            ? LoadOgg(path)
            : LoadWave(path);
    }

    /// <summary>Decodes an Ogg Vorbis file to 16-bit interleaved PCM.</summary>
    public static PcmAudioClip LoadOgg(string path)
    {
        using var vorbis = new NVorbis.VorbisReader(path);
        int channels = vorbis.Channels;
        int rate = vorbis.SampleRate;
        if (channels < 1 || channels > 8 || rate < 1000 || rate > 384000)
            throw new InvalidDataException($"Unsupported Ogg Vorbis audio: {channels} channels at {rate} Hz.");
        var samples = new System.Collections.Generic.List<short>(
            (int)Math.Min(int.MaxValue / 2, Math.Max(0, vorbis.TotalSamples * channels)));
        float[] buffer = new float[4096 * channels];
        int read;
        while ((read = vorbis.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++)
                samples.Add((short)Math.Round(Math.Clamp(buffer[i], -1f, 1f) * short.MaxValue));
        if (samples.Count == 0) throw new InvalidDataException("The Ogg Vorbis file contains no audio.");
        return new PcmAudioClip(samples.ToArray(), rate, channels);
    }

    public static PcmAudioClip LoadWave(string path)
    {
        using BinaryReader reader = new(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Expected RIFF audio.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Expected WAVE audio.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string chunk = new(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            long next = reader.BaseStream.Position + size;
            if (next > reader.BaseStream.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (chunk == "fmt " && size >= 16)
            {
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (format == 0xfffe && size >= 40)
                {
                    reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32();
                    format = reader.ReadUInt16(); // PCM or IEEE float subtype GUID
                }
            }
            else if (chunk == "data") data = reader.ReadBytes(checked((int)size));
            reader.BaseStream.Position = Math.Min(reader.BaseStream.Length, next + (size & 1));
        }
        if (channels is < 1 or > 8 || rate is < 1 or > 384000 || data is null
            || (format != 1 && format != 3) || bits is not (8 or 16 or 24 or 32) || (format == 3 && bits != 32))
            throw new InvalidDataException("Supported WAV formats are PCM 8/16/24/32-bit and IEEE float 32-bit.");
        int stride = bits / 8;
        if (data.Length == 0 || data.Length % (stride * channels) != 0) throw new InvalidDataException("Incomplete audio frame.");
        short[] samples = new short[data.Length / stride];
        for (int index = 0; index < samples.Length; index++)
        {
            int offset = index * stride;
            double value = format == 3 ? BitConverter.ToSingle(data, offset) * 32767d : bits switch
            {
                8 => (data[offset] - 128) * 256d,
                16 => BitConverter.ToInt16(data, offset),
                24 => ((data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16) << 8 >> 8) / 256d,
                _ => BitConverter.ToInt32(data, offset) / 65536d,
            };
            samples[index] = double.IsFinite(value) ? (short)Math.Clamp(value, short.MinValue, short.MaxValue) : (short)0;
        }
        return new(samples, rate, channels);
    }
}

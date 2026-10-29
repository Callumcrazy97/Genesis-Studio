using System.Text;

namespace Genesis.Application.Core.Audio.Music;

/// <summary>
/// Reads and writes Standard MIDI Files (formats 0 and 1) as <see cref="MusicDocument"/>s: notes with
/// their times and velocities, tempo (and tempo changes), the time signature, track names, programs
/// (as instruments) and channel volume. Written by Genesis, with no library.
/// </summary>
/// <remarks>
/// Reading splits each file track by channel, so a format 0 file (one track, many channels) comes in
/// as one track per channel; channel 10 is the drum kit. Note-offs close the earliest open note of the
/// same key and channel; notes still open at the end of their track end there. Lyrics, controllers
/// other than volume, pitch bend and system-exclusive data are skipped. Writing makes a format 1 file:
/// a tempo track, then one track per music track.
/// </remarks>
public static class MidiFile
{
    private const int DrumChannel = 9;

    // ── Reading ──────────────────────────────────────────────────────────────

    public static MusicDocument Read(string path) => Read(File.ReadAllBytes(path));

    /// <summary>The piece in a MIDI file's bytes; throws <see cref="InvalidDataException"/> with a plain reason when it cannot.</summary>
    public static MusicDocument Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var reader = new Reader(bytes, 0, bytes.Length);
        if (bytes.Length < 14 || reader.Tag() != "MThd") throw new InvalidDataException("This is not a MIDI file (it does not start with MThd).");
        int headerLength = (int)reader.UInt32();
        if (headerLength < 6 || headerLength > bytes.Length - 8) throw new InvalidDataException("The MIDI file's header is damaged.");
        int format = reader.UInt16();
        reader.UInt16(); // the track count: the chunks themselves are counted instead
        int division = reader.UInt16();
        reader.Position = 8 + headerLength;
        if (format == 2) throw new InvalidDataException("MIDI format 2 (separate patterns) is not supported; save the file as format 0 or 1.");
        if (format > 2) throw new InvalidDataException($"MIDI format {format} is not a standard MIDI file format.");

        // Ticks are kept as the file has them, except SMPTE time and very coarse files, which are
        // brought to 480 ticks a beat (at 120 beats a minute for SMPTE, whose times are seconds).
        bool smpte = (division & 0x8000) != 0;
        int fileTicks = smpte ? 0 : division;
        if (!smpte && fileTicks == 0) throw new InvalidDataException("The MIDI file gives 0 ticks a beat.");
        double scale = 1;
        int ticksPerBeat = fileTicks;
        if (smpte)
        {
            int framesPerSecond = -(sbyte)(division >> 8);
            int ticksPerFrame = division & 0xFF;
            if (framesPerSecond <= 0 || ticksPerFrame <= 0) throw new InvalidDataException("The MIDI file's SMPTE timing is damaged.");
            ticksPerBeat = MusicDocument.DefaultTicksPerBeat;
            scale = ticksPerBeat * 2.0 / (framesPerSecond * ticksPerFrame);
        }
        else if (fileTicks < 96)
        {
            ticksPerBeat = MusicDocument.DefaultTicksPerBeat;
            scale = ticksPerBeat / (double)fileTicks;
        }
        long Scaled(long tick) => scale == 1 ? tick : (long)Math.Round(tick * scale);

        var tempos = new List<(long Tick, double Bpm)>();
        (int Beats, int Unit)? signature = null;
        var parts = new List<Part>();
        int trackIndex = 0;
        while (reader.Position + 8 <= bytes.Length)
        {
            string tag = reader.Tag();
            long length = reader.UInt32();
            if (length > bytes.Length - reader.Position) throw new InvalidDataException($"A '{tag}' chunk runs past the end of the MIDI file.");
            int start = reader.Position;
            if (tag == "MTrk")
            {
                ReadTrack(new Reader(bytes, start, start + (int)length), trackIndex++, parts, tempos, ref signature);
            }
            reader.Position = start + (int)length;
        }
        if (trackIndex == 0) throw new InvalidDataException("The MIDI file has no tracks.");

        var document = new MusicDocument { TicksPerBeat = ticksPerBeat, Tracks = [] };
        if (signature is { } sig) { document.BeatsPerBar = sig.Beats; document.BeatUnit = sig.Unit; }
        if (!smpte)
        {
            var ordered = tempos.OrderBy(change => change.Tick).ToList();
            if (ordered.Count > 0 && ordered[0].Tick == 0) { document.Tempo = ordered[0].Bpm; ordered.RemoveAt(0); }
            else if (ordered.Count > 0 && !parts.Any(part => part.Notes.Count > 0 && part.Notes.Min(note => note.Start) < ordered[0].Tick))
            {
                // A first tempo given a little after the start, before any note: it is the piece's tempo.
                document.Tempo = ordered[0].Bpm; ordered.RemoveAt(0);
            }
            double previous = document.Tempo;
            foreach ((long tick, double bpm) in ordered)
            {
                if (Math.Abs(bpm - previous) < 1e-9) continue;
                document.TempoChanges.Add(new MusicTempoChange { Tick = Scaled(tick), Bpm = bpm });
                previous = bpm;
            }
        }

        var namesSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Part part in parts.Where(part => part.Notes.Count > 0).OrderBy(part => part.Track).ThenBy(part => part.Channel))
        {
            bool drums = part.Channel == DrumChannel;
            int sameTrackParts = parts.Count(other => other.Track == part.Track && other.Notes.Count > 0);
            string name = part.TrackName ?? part.InstrumentName ?? (drums ? "Drums" : FromProgramName(part.Program));
            if (sameTrackParts > 1 && part.TrackName is not null) name += $" (channel {part.Channel + 1})";
            int seen = namesSeen.TryGetValue(name, out int count) ? count + 1 : 1;
            namesSeen[name] = seen;
            if (seen > 1) name += " " + seen;
            var track = new MusicTrack
            {
                Name = name,
                Instrument = drums ? MusicInstruments.DrumKit : MusicInstruments.FromGeneralMidi(part.Program ?? 0),
                Program = drums ? -1 : part.Program ?? -1,
                Channel = part.Channel,
                Volume = (float)Math.Round((part.Volume ?? 100) / 127.0, 3),
                Notes = part.Notes.Select(note => new MusicNote
                {
                    Pitch = note.Pitch,
                    Start = Scaled(note.Start),
                    Length = Math.Max(1, Scaled(note.Start + note.Length) - Scaled(note.Start)),
                    Velocity = note.Velocity,
                }).ToList(),
            };
            document.Tracks.Add(track);
        }
        document.Normalize();
        return document;
    }

    private static string FromProgramName(int? program) => program is int value ? MusicInstruments.FromGeneralMidi(value) : "Piano";

    private sealed class Part(int track, int channel)
    {
        public int Track { get; } = track;
        public int Channel { get; } = channel;
        public string? TrackName;
        public string? InstrumentName;
        public int? Program;
        public int? Volume;
        public readonly List<MusicNote> Notes = [];
        public readonly Dictionary<int, Queue<(long Tick, int Velocity)>> Open = [];
    }

    private static void ReadTrack(Reader reader, int trackIndex, List<Part> parts, List<(long Tick, double Bpm)> tempos, ref (int Beats, int Unit)? signature)
    {
        var channels = new Dictionary<int, Part>();
        string? trackName = null, instrumentName = null;
        Part PartFor(int channel)
        {
            if (!channels.TryGetValue(channel, out Part? part))
            {
                part = new Part(trackIndex, channel);
                channels[channel] = part;
                parts.Add(part);
            }
            return part;
        }

        long tick = 0;
        int running = 0;
        while (reader.Position < reader.End)
        {
            tick += reader.VariableLength();
            if (reader.Position >= reader.End) break;
            int status = reader.Byte();
            if (status < 0x80)
            {
                if (running == 0) throw new InvalidDataException("A MIDI track uses running status before any event.");
                reader.Position--; // the byte is the first data byte
                status = running;
            }
            if (status == 0xFF)
            {
                running = 0;
                int type = reader.Byte();
                int length = (int)reader.VariableLength();
                byte[] data = reader.Bytes(length);
                switch (type)
                {
                    case 0x51 when length >= 3:
                        int microseconds = (data[0] << 16) | (data[1] << 8) | data[2];
                        if (microseconds > 0) tempos.Add((tick, 60_000_000.0 / microseconds));
                        break;
                    case 0x58 when length >= 2:
                        if (signature is null && data[0] > 0 && data[1] <= 5) signature = (data[0], 1 << data[1]);
                        break;
                    case 0x03:
                        trackName ??= Text(data);
                        break;
                    case 0x04:
                        instrumentName ??= Text(data);
                        break;
                    case 0x2F:
                        reader.Position = reader.End;
                        break;
                }
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                running = 0;
                reader.Bytes((int)reader.VariableLength());
                continue;
            }
            if (status >= 0xF0) throw new InvalidDataException($"A MIDI track holds a system message (0x{status:X2}) that a file cannot.");
            running = status;
            int channel = status & 0x0F;
            switch (status & 0xF0)
            {
                case 0x80:
                    {
                        int key = reader.Byte(); reader.Byte();
                        CloseNote(PartFor(channel), key, tick);
                        break;
                    }
                case 0x90:
                    {
                        int key = reader.Byte(), velocity = reader.Byte();
                        Part part = PartFor(channel);
                        if (velocity == 0) CloseNote(part, key, tick);
                        else
                        {
                            if (!part.Open.TryGetValue(key, out var open)) part.Open[key] = open = new Queue<(long, int)>();
                            open.Enqueue((tick, velocity));
                        }
                        break;
                    }
                case 0xA0: reader.Byte(); reader.Byte(); break;
                case 0xB0:
                    {
                        int controller = reader.Byte(), value = reader.Byte();
                        if (controller == 7) { Part part = PartFor(channel); part.Volume ??= value; }
                        break;
                    }
                case 0xC0:
                    {
                        int program = reader.Byte();
                        Part part = PartFor(channel); part.Program ??= program;
                        break;
                    }
                case 0xD0: reader.Byte(); break;
                case 0xE0: reader.Byte(); reader.Byte(); break;
            }
        }
        foreach (Part part in channels.Values)
        {
            part.TrackName = trackName;
            part.InstrumentName = instrumentName;
            foreach ((int key, Queue<(long Tick, int Velocity)> open) in part.Open)
                while (open.Count > 0)
                {
                    (long at, int velocity) = open.Dequeue();
                    part.Notes.Add(new MusicNote { Pitch = key, Start = at, Length = Math.Max(1, tick - at), Velocity = velocity });
                }
            part.Open.Clear();
        }
    }

    private static void CloseNote(Part part, int key, long tick)
    {
        if (!part.Open.TryGetValue(key, out var open) || open.Count == 0) return;
        (long at, int velocity) = open.Dequeue();
        part.Notes.Add(new MusicNote { Pitch = key & 0x7F, Start = at, Length = Math.Max(1, tick - at), Velocity = Math.Clamp(velocity, 1, 127) });
    }

    // Names in MIDI files are usually ASCII or Latin-1; an invalid one is replaced, never refused.
    private static string? Text(byte[] data)
    {
        string text = Encoding.Latin1.GetString(data).Trim('\0', ' ', '\t', '\r', '\n');
        return text.Length == 0 ? null : text;
    }

    private sealed class Reader(byte[] bytes, int position, int end)
    {
        public int Position = position;
        public int End { get; } = end;

        private void Need(int count)
        {
            if (Position + count > End) throw new InvalidDataException("The MIDI file ends in the middle of an event.");
        }

        public int Byte() { Need(1); return bytes[Position++]; }

        public byte[] Bytes(int count)
        {
            if (count < 0) throw new InvalidDataException("The MIDI file gives an event a negative length.");
            Need(count);
            byte[] data = bytes.AsSpan(Position, count).ToArray();
            Position += count;
            return data;
        }

        public int UInt16() { Need(2); int value = (bytes[Position] << 8) | bytes[Position + 1]; Position += 2; return value; }

        public uint UInt32()
        {
            Need(4);
            uint value = (uint)((bytes[Position] << 24) | (bytes[Position + 1] << 16) | (bytes[Position + 2] << 8) | bytes[Position + 3]);
            Position += 4;
            return value;
        }

        public string Tag() { Need(4); string tag = Encoding.ASCII.GetString(bytes, Position, 4); Position += 4; return tag; }

        public long VariableLength()
        {
            long value = 0;
            for (int count = 0; count < 4; count++)
            {
                int next = Byte();
                value = (value << 7) | (uint)(next & 0x7F);
                if ((next & 0x80) == 0) return value;
            }
            throw new InvalidDataException("The MIDI file has a time or length longer than four bytes.");
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public static void Write(MusicDocument document, string path) => File.WriteAllBytes(path, Write(document));

    /// <summary>The piece as a format 1 Standard MIDI File.</summary>
    public static byte[] Write(MusicDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MusicDocument piece = document.Copy();
        piece.Normalize();
        using var stream = new MemoryStream();
        stream.Write("MThd"u8);
        WriteUInt32(stream, 6);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, piece.Tracks.Count + 1);
        WriteUInt16(stream, piece.TicksPerBeat);

        // The tempo track: time signature and every tempo.
        var conductor = new List<(long Tick, int Order, byte[] Data)>
        {
            (0, 0, Meta(0x58, [(byte)piece.BeatsPerBar, (byte)Math.Log2(piece.BeatUnit), 24, 8])),
            (0, 1, Meta(0x51, Tempo(piece.Tempo))),
        };
        foreach (MusicTempoChange change in piece.TempoChanges) conductor.Add((change.Tick, 1, Meta(0x51, Tempo(change.Bpm))));
        WriteTrack(stream, conductor);

        int nextChannel = 0;
        foreach (MusicTrack track in piece.Tracks)
        {
            bool drums = MusicInstruments.Kind(track.Instrument) == MusicInstrumentKind.Drums;
            int channel;
            if (drums) channel = DrumChannel;
            else if (track.Channel >= 0 && track.Channel != DrumChannel) channel = track.Channel;
            else
            {
                channel = nextChannel;
                nextChannel = (nextChannel + 1) % 16;
                if (nextChannel == DrumChannel) nextChannel++;
            }
            int program = track.Program >= 0 ? track.Program : MusicInstruments.GeneralMidiProgram(track.Instrument);
            var events = new List<(long Tick, int Order, byte[] Data)>
            {
                (0, 0, Meta(0x03, Encoding.Latin1.GetBytes(track.Name))),
                (0, 1, [(byte)(0xB0 | channel), 7, (byte)Math.Clamp((int)Math.Round(track.Volume * 127), 0, 127)]),
            };
            if (!drums) events.Add((0, 2, [(byte)(0xC0 | channel), (byte)program]));
            foreach (MusicNote note in track.Notes)
            {
                // At one tick a note ends before the next one starts, so the same key played again is heard.
                events.Add((note.Start, 4, [(byte)(0x90 | channel), (byte)note.Pitch, (byte)note.Velocity]));
                events.Add((note.End, 3, [(byte)(0x80 | channel), (byte)note.Pitch, 64]));
            }
            WriteTrack(stream, events);
        }
        return stream.ToArray();
    }

    private static byte[] Tempo(double bpm)
    {
        int microseconds = Math.Clamp((int)Math.Round(60_000_000.0 / bpm), 1, 0xFFFFFF);
        return [(byte)(microseconds >> 16), (byte)(microseconds >> 8), (byte)microseconds];
    }

    private static byte[] Meta(int type, byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0xFF);
        buffer.WriteByte((byte)type);
        WriteVariableLength(buffer, data.Length);
        buffer.Write(data);
        return buffer.ToArray();
    }

    private static void WriteTrack(Stream stream, List<(long Tick, int Order, byte[] Data)> events)
    {
        using var body = new MemoryStream();
        long previous = 0;
        foreach ((long tick, _, byte[] data) in events.OrderBy(item => item.Tick).ThenBy(item => item.Order))
        {
            WriteVariableLength(body, tick - previous);
            body.Write(data);
            previous = tick;
        }
        WriteVariableLength(body, 0);
        body.Write([0xFF, 0x2F, 0x00]);
        stream.Write("MTrk"u8);
        WriteUInt32(stream, (uint)body.Length);
        body.Position = 0;
        body.CopyTo(stream);
    }

    private static void WriteVariableLength(Stream stream, long value)
    {
        if (value < 0 || value > 0x0FFFFFFF) throw new InvalidDataException("A time in the piece is too far apart to write in a MIDI file.");
        Span<byte> buffer = stackalloc byte[4];
        int count = 0;
        buffer[count++] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0) buffer[count++] = (byte)((value & 0x7F) | 0x80);
        for (int index = count - 1; index >= 0; index--) stream.WriteByte(buffer[index]);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}

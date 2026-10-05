using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Genesis.Runtime.Project;

namespace Genesis.Runtime.Input
{
    /// <summary>
    /// Everything a game can read from the keyboard, mouse and controller in one frame, with the
    /// frame's time step and the window's size: one frame of an input recording.
    /// </summary>
    public struct InputFrame
    {
        public float Delta;
        public int WindowWidth;
        public int WindowHeight;
        public UInt128 KeysDown;
        public UInt128 KeysPressed;
        public UInt128 KeysReleased;
        public byte MouseDown;
        public byte MousePressed;
        public byte MouseReleased;
        public float MouseX;
        public float MouseY;
        public float Wheel;
        public float LookX;
        public float LookY;
        public bool GamepadConnected;
        public ushort PadDown;
        public ushort PadPressed;
        public ushort PadReleased;
        /// <summary>The menu shortcuts the window sets for a controller (Start, Back, Confirm...), as bits.</summary>
        public ushort PadEdges;
        public Vector2 LeftStick;
        public Vector2 RightStick;
        public float LeftTrigger;
        public float RightTrigger;
        public Vector2 LeftStickRaw;
        public Vector2 RightStickRaw;
        public float LeftTriggerRaw;
        public float RightTriggerRaw;
        /// <summary>Characters typed since the previous frame; null when none were.</summary>
        public string Typed;
    }

    public enum InputReplayMode
    {
        Off,
        Recording,
        Replaying,
    }

    /// <summary>
    /// Records the input a game reads, frame by frame, and plays it back in place of the real
    /// devices, so a later run sees the same keys, mouse, controller, time step and random numbers
    /// on the same frames. Started by scripts (<c>InputRecordStart</c>, <c>InputReplayStart</c>)
    /// or by the Player's <c>--record</c> and <c>--replay</c> arguments.
    /// </summary>
    /// <remarks>
    /// The host calls <see cref="BeginFrame"/> at the start of each simulation frame, before any
    /// script runs, and only while <see cref="IsActive"/>: when nothing is recorded or replayed the
    /// cost is that one check. A frame that starts a recording or a replay is not part of it; the
    /// first recorded or replayed frame is the next one. Pressing Escape during a replay stops it
    /// and hands control back to the real devices; that press is not passed to the game.
    /// </remarks>
    public static class InputReplay
    {
        public const int FormatVersion = 1;
        public const string FileExtension = ".ginput";
        public const string ReplayEnvironmentVariable = "GENESIS_INPUT_REPLAY";
        public const string RecordEnvironmentVariable = "GENESIS_INPUT_RECORD";
        private static readonly byte[] Magic = "GENINPUT"u8.ToArray();
        private const int FlushEveryFrames = 120;

        private static BinaryWriter _writer;
        private static InputFrame[] _frames = Array.Empty<InputFrame>();
        private static bool _starting;
        private static bool _endAfterThisFrame;

        /// <summary>True while recording or replaying. The only thing the host checks each frame otherwise.</summary>
        public static bool IsActive { get; private set; }

        public static InputReplayMode Mode { get; private set; }

        /// <summary>Frames recorded or replayed so far: 1 in the first frame of a recording or replay.</summary>
        public static int Frame { get; private set; }

        /// <summary>Frames in the replay being played; 0 when none is.</summary>
        public static int FrameCount => _frames.Length;

        /// <summary>True from the frame the last recorded frame is replayed until the next start.</summary>
        public static bool Finished { get; private set; }

        /// <summary>The seed the script random numbers were given when the recording started.</summary>
        public static int Seed { get; private set; }

        /// <summary>The file being (or last) recorded or replayed.</summary>
        public static string FilePath { get; private set; } = string.Empty;

        /// <summary>Why the last start failed; empty when it did not.</summary>
        public static string LastError { get; private set; } = string.Empty;

        /// <summary>Raised in the frame after a replay's last, when the real devices are back.</summary>
        public static event Action ReplayEnded;

        /// <summary>
        /// The file for <paramref name="name"/>: a full path is used as it is; a plain name is
        /// <c>name.ginput</c> in the project's debug folder under <c>Replays</c>.
        /// </summary>
        public static string ResolvePath(string projectPath, string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;
            if (Path.IsPathRooted(trimmed))
                return Path.HasExtension(trimmed) ? trimmed : trimmed + FileExtension;
            if (string.IsNullOrWhiteSpace(projectPath)) return string.Empty;
            char[] chars = trimmed.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string file = new string(chars);
            if (file.Length > 120) file = file.Substring(0, 120);
            if (!file.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)) file += FileExtension;
            return Path.Combine(ProjectPaths.DebugRoot(projectPath), "Replays", file);
        }

        /// <summary>Starts recording into <paramref name="path"/>, replacing it. Stops a recording or replay already running.</summary>
        public static bool StartRecording(string path, int? seed = null)
        {
            StopAll();
            LastError = string.Empty;
            if (string.IsNullOrWhiteSpace(path)) { LastError = "No file to record into."; return false; }
            int chosenSeed = seed ?? (Environment.TickCount & int.MaxValue);
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                _writer = new BinaryWriter(stream);
                _writer.Write(Magic);
                _writer.Write(FormatVersion);
                _writer.Write(chosenSeed);
            }
            catch (Exception error)
            {
                _writer?.Dispose();
                _writer = null;
                LastError = error.Message;
                return false;
            }

            Begin(InputReplayMode.Recording, path, chosenSeed);
            return true;
        }

        /// <summary>Stops the recording and saves it. Returns the frames recorded; 0 when none was running.</summary>
        public static int StopRecording()
        {
            if (Mode != InputReplayMode.Recording) return 0;
            int frames = Frame;
            try { _writer?.Flush(); } catch { }
            _writer?.Dispose();
            _writer = null;
            End();
            return frames;
        }

        /// <summary>Starts playing <paramref name="path"/> in place of the real devices. Stops a recording or replay already running.</summary>
        public static bool StartReplay(string path)
        {
            StopAll();
            LastError = string.Empty;
            if (!TryRead(path, out int seed, out InputFrame[] frames, out string error))
            {
                LastError = error;
                return false;
            }

            _frames = frames;
            Begin(InputReplayMode.Replaying, path, seed);
            return true;
        }

        /// <summary>Stops a replay before its end; the real devices are read again from the next frame.</summary>
        public static void StopReplay()
        {
            if (Mode != InputReplayMode.Replaying) return;
            _releasePending = true;
            End();
        }

        /// <summary>Stops whatever is running, saving a recording. For a new game and for shutdown.</summary>
        public static void StopAll()
        {
            if (Mode == InputReplayMode.Recording) StopRecording();
            else if (Mode == InputReplayMode.Replaying) StopReplay();
        }

        /// <summary>Stops everything and forgets the last run (a new game).</summary>
        public static void Reset()
        {
            StopAll();
            _releasePending = false;
            IsActive = false;
            _frames = Array.Empty<InputFrame>();
            Frame = 0;
            Finished = false;
            FilePath = string.Empty;
            LastError = string.Empty;
            Genesis.Runtime.Scripting.PgslCommands.SeedRandom(null);
        }

        private static bool _releasePending;

        /// <summary>
        /// Called by the host at the start of each simulation frame while <see cref="IsActive"/> (or
        /// while a stopped replay still has to let go of what it held). Records this frame's input,
        /// or replaces it with the recorded frame and sets <paramref name="delta"/> and the window
        /// size to the recorded ones. <paramref name="started"/> is true in the first frame of a
        /// recording or replay, when the host should restart its fixed-step clock so both runs
        /// step the same.
        /// </summary>
        public static void BeginFrame(InputState input, ref float delta, ref int windowWidth, ref int windowHeight, out bool started)
        {
            started = false;
            if (input == null) return;
            if (_releasePending)
            {
                // A replay that stopped: whatever it held is let go, and the devices are read again.
                _releasePending = false;
                input.ReleaseAll();
                if (!IsActive) return;
            }

            if (Mode == InputReplayMode.Recording)
            {
                started = _starting;
                _starting = false;
                InputFrame frame = default;
                input.CaptureFrame(ref frame);
                frame.Delta = delta;
                frame.WindowWidth = windowWidth;
                frame.WindowHeight = windowHeight;
                try
                {
                    Write(_writer, in frame);
                    Frame++;
                    if (Frame % FlushEveryFrames == 0) _writer.Flush();
                }
                catch (Exception error)
                {
                    LastError = error.Message;
                    StopRecording();
                }
                return;
            }

            if (Mode != InputReplayMode.Replaying) return;
            if (_endAfterThisFrame || input.WasPressed(Key.Escape))
            {
                // The last frame has been played, or the person at the machine took over.
                _endAfterThisFrame = false;
                input.ReleaseAll();
                End();
                ReplayEnded?.Invoke();
                return;
            }

            started = _starting;
            _starting = false;
            ref readonly InputFrame recorded = ref _frames[Frame];
            input.ApplyFrame(in recorded);
            delta = recorded.Delta;
            if (recorded.WindowWidth > 0 && recorded.WindowHeight > 0)
            {
                windowWidth = recorded.WindowWidth;
                windowHeight = recorded.WindowHeight;
            }
            Frame++;
            if (Frame >= _frames.Length)
            {
                Finished = true;
                _endAfterThisFrame = true;
            }
        }

        private static void Begin(InputReplayMode mode, string path, int seed)
        {
            Mode = mode;
            IsActive = true;
            FilePath = path;
            Seed = seed;
            Frame = 0;
            Finished = false;
            _starting = true;
            _endAfterThisFrame = mode == InputReplayMode.Replaying && _frames.Length == 0;
            if (_endAfterThisFrame) Finished = true;
            // Random numbers drawn from here on are the same in the recording and every replay.
            Genesis.Runtime.Scripting.PgslCommands.SeedRandom(seed);
        }

        private static void End()
        {
            if (Mode == InputReplayMode.Replaying) _frames = Array.Empty<InputFrame>();
            Mode = InputReplayMode.Off;
            _starting = false;
            _endAfterThisFrame = false;
            IsActive = _releasePending;
        }

        private static void Write(BinaryWriter writer, in InputFrame frame)
        {
            writer.Write(frame.Delta);
            writer.Write(frame.WindowWidth);
            writer.Write(frame.WindowHeight);
            WriteBits(writer, frame.KeysDown);
            WriteBits(writer, frame.KeysPressed);
            WriteBits(writer, frame.KeysReleased);
            writer.Write(frame.MouseDown);
            writer.Write(frame.MousePressed);
            writer.Write(frame.MouseReleased);
            writer.Write(frame.MouseX);
            writer.Write(frame.MouseY);
            writer.Write(frame.Wheel);
            writer.Write(frame.LookX);
            writer.Write(frame.LookY);
            writer.Write(frame.GamepadConnected);
            writer.Write(frame.PadDown);
            writer.Write(frame.PadPressed);
            writer.Write(frame.PadReleased);
            writer.Write(frame.PadEdges);
            writer.Write(frame.LeftStick.X); writer.Write(frame.LeftStick.Y);
            writer.Write(frame.RightStick.X); writer.Write(frame.RightStick.Y);
            writer.Write(frame.LeftTrigger);
            writer.Write(frame.RightTrigger);
            writer.Write(frame.LeftStickRaw.X); writer.Write(frame.LeftStickRaw.Y);
            writer.Write(frame.RightStickRaw.X); writer.Write(frame.RightStickRaw.Y);
            writer.Write(frame.LeftTriggerRaw);
            writer.Write(frame.RightTriggerRaw);
            writer.Write(frame.Typed ?? string.Empty);
        }

        private static InputFrame Read(BinaryReader reader)
        {
            InputFrame frame = default;
            frame.Delta = reader.ReadSingle();
            frame.WindowWidth = reader.ReadInt32();
            frame.WindowHeight = reader.ReadInt32();
            frame.KeysDown = ReadBits(reader);
            frame.KeysPressed = ReadBits(reader);
            frame.KeysReleased = ReadBits(reader);
            frame.MouseDown = reader.ReadByte();
            frame.MousePressed = reader.ReadByte();
            frame.MouseReleased = reader.ReadByte();
            frame.MouseX = reader.ReadSingle();
            frame.MouseY = reader.ReadSingle();
            frame.Wheel = reader.ReadSingle();
            frame.LookX = reader.ReadSingle();
            frame.LookY = reader.ReadSingle();
            frame.GamepadConnected = reader.ReadBoolean();
            frame.PadDown = reader.ReadUInt16();
            frame.PadPressed = reader.ReadUInt16();
            frame.PadReleased = reader.ReadUInt16();
            frame.PadEdges = reader.ReadUInt16();
            frame.LeftStick = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            frame.RightStick = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            frame.LeftTrigger = reader.ReadSingle();
            frame.RightTrigger = reader.ReadSingle();
            frame.LeftStickRaw = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            frame.RightStickRaw = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            frame.LeftTriggerRaw = reader.ReadSingle();
            frame.RightTriggerRaw = reader.ReadSingle();
            string typed = reader.ReadString();
            frame.Typed = typed.Length > 0 ? typed : null;
            return frame;
        }

        private static void WriteBits(BinaryWriter writer, UInt128 bits)
        {
            writer.Write((ulong)bits);
            writer.Write((ulong)(bits >> 64));
        }

        private static UInt128 ReadBits(BinaryReader reader)
        {
            ulong low = reader.ReadUInt64();
            ulong high = reader.ReadUInt64();
            return new UInt128(high, low);
        }

        /// <summary>Reads a recording. A recording cut short (the game closed mid-frame) keeps its whole frames.</summary>
        public static bool TryRead(string path, out int seed, out InputFrame[] frames, out string error)
        {
            seed = 0;
            frames = Array.Empty<InputFrame>();
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "No recording at " + (path ?? string.Empty) + ".";
                return false;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(stream);
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (!magic.AsSpan().SequenceEqual(Magic))
                {
                    error = path + " is not an input recording.";
                    return false;
                }

                int version = reader.ReadInt32();
                if (version != FormatVersion)
                {
                    error = $"{path} is input recording version {version}; this Player reads version {FormatVersion}.";
                    return false;
                }

                seed = reader.ReadInt32();
                var read = new List<InputFrame>();
                while (stream.Position < stream.Length)
                {
                    try { read.Add(Read(reader)); }
                    catch (EndOfStreamException) { break; }
                }
                frames = read.ToArray();
                return true;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                return false;
            }
        }
    }
}

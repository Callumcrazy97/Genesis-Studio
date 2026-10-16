#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Recording;

/// <summary>
/// Gameplay recording for games and Debug mode: record to an MP4 or a PNG sequence, and keep a
/// rolling replay buffer of the last few seconds that a game can save or play back in game (a
/// killcam). Nothing runs until a game or the Debug key asks: no thread, no memory, no files.
/// </summary>
/// <remarks>
/// Frames are read back after the frame is finished, at most at the rate asked for, then scaled
/// and JPEG-compressed on a worker thread so the game thread only pays for the readback. A busy
/// worker drops frames rather than slowing the game. The finished file is made on a worker too.
/// </remarks>
public static class GameRecorder
{
    private sealed record Frame(double Time, byte[] Jpeg, int Width, int Height);
    private sealed record Job(byte[] Bgra, int Width, int Height, double Time, float Scale, Target Into);
    private enum Target { Buffer, Recording }

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Channel<Job>? _jobs;

    // Replay buffer.
    private static bool _bufferOn;
    private static double _bufferSeconds = 10;
    private static double _bufferFps = 30;
    private static float _bufferScale = 0.5f;
    private static double _lastBufferCapture = double.NegativeInfinity;
    private static readonly LinkedList<Frame> Buffer = new();

    // Recording to a file.
    private static bool _recording;
    private static bool _recordPng;
    private static string _recordName = string.Empty;
    private static string _recordFolder = string.Empty;
    private static double _recordFps = 30;
    private static double _lastRecordCapture = double.NegativeInfinity;
    private static readonly List<(double Time, string File)> RecordFrames = [];
    private static int _recordIndex;

    // Playback.
    private static List<Frame>? _playback;
    private static double _playStarted;
    private static double _playSpeed = 1;
    private static int _shownFrame = -1;
    private static int _texture;

    public static bool IsRecording { get { lock (Gate) return _recording; } }
    public static bool IsBufferOn { get { lock (Gate) return _bufferOn; } }
    public static bool IsPlaying => PlaybackProgress() < 1;

    /// <summary>The project the files go under (set by the player); exported games fall back to the user's folder.</summary>
    public static string ProjectPath { get; set; } = string.Empty;

    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>Called by the host after each finished frame, before it is shown.</summary>
    public static void AfterFrame(IRenderController renderer)
    {
        if (renderer is null) return;
        bool buffer, record;
        double now = Now;
        lock (Gate)
        {
            // The replay itself is not recorded into the buffer it is playing from.
            buffer = _bufferOn && _playback is null && now - _lastBufferCapture >= 1.0 / _bufferFps - 0.002;
            record = _recording && now - _lastRecordCapture >= 1.0 / _recordFps - 0.002;
            if (!buffer && !record) return;
            if (buffer) _lastBufferCapture = now;
            if (record) _lastRecordCapture = now;
        }
        byte[]? bgra;
        int width, height;
        try
        {
            if (!renderer.TryReadSubmittedFramePixels(out width, out height, out bgra) || bgra is null) return;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            return;
        }
        EnsureWorker();
        if (record) _jobs!.Writer.TryWrite(new Job(bgra, width, height, now, 1f, Target.Recording));
        if (buffer) _jobs!.Writer.TryWrite(new Job(bgra, width, height, now, _bufferScale, Target.Buffer));
    }

    // ── Recording ──────────────────────────────────────────────────────────────────────────

    /// <summary>Starts recording to <c>Recordings/&lt;name&gt;.mp4</c> (or a folder of PNGs when <paramref name="png"/>).</summary>
    public static bool StartRecording(string name, bool png, double fps = 30)
    {
        lock (Gate)
        {
            if (_recording) return false;
            string folder = RecordingsFolder();
            _recordName = SafeName(string.IsNullOrWhiteSpace(name) ? "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : name);
            _recordPng = png;
            _recordFolder = Path.Combine(folder, png ? _recordName : "." + _recordName + "-frames");
            Directory.CreateDirectory(_recordFolder);
            _recordFps = Math.Clamp(fps, 5, 60);
            _recordIndex = 0;
            _lastRecordCapture = double.NegativeInfinity;
            RecordFrames.Clear();
            _recording = true;
        }
        RenderLog.Line("Recording started: " + _recordName);
        return true;
    }

    /// <summary>Stops recording and returns where the result will be (the MP4 is finished on a worker).</summary>
    public static string StopRecording(bool wait = false)
    {
        string name, folder;
        bool png;
        double fps;
        List<(double Time, string File)> frames;
        lock (Gate)
        {
            if (!_recording) return string.Empty;
            _recording = false;
            name = _recordName;
            folder = _recordFolder;
            png = _recordPng;
            fps = _recordFps;
        }
        // Let frames already handed to the worker land before the list is read.
        Task drained = Drain();
        if (png)
        {
            if (wait) drained.Wait(TimeSpan.FromSeconds(10));
            RenderLog.Line("Recording saved: " + folder);
            return folder;
        }
        string output = UniquePath(Path.Combine(RecordingsFolder(), name + ".mp4"));
        Task encode = drained.ContinueWith(_ =>
        {
            lock (Gate) frames = [.. RecordFrames];
            try
            {
                EncodeFiles(frames, output, fps);
                Directory.Delete(folder, recursive: true);
                RenderLog.Line("Recording saved: " + output);
            }
            catch (Exception error) when (error is InvalidOperationException or COMException or IOException
                or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                // No H.264 encoder (Windows N) or a failed write: the frames stay, as pictures.
                string kept = Path.Combine(RecordingsFolder(), name + " (frames)");
                try { Directory.Move(folder, UniquePath(kept)); } catch (IOException) { }
                RenderLog.Line("Recording could not be made into a video (" + error.Message + "); its frames are in " + kept);
            }
        }, TaskScheduler.Default);
        if (wait) encode.Wait(TimeSpan.FromMinutes(2));
        return output;
    }

    // ── Replay buffer ──────────────────────────────────────────────────────────────────────

    public static void EnableBuffer(double seconds, double fps, double scale)
    {
        lock (Gate)
        {
            _bufferSeconds = Math.Clamp(seconds, 1, 60);
            _bufferFps = Math.Clamp(fps, 5, 60);
            _bufferScale = (float)Math.Clamp(scale, 0.1, 1);
            _bufferOn = true;
        }
    }

    public static void DisableBuffer()
    {
        lock (Gate)
        {
            _bufferOn = false;
            Buffer.Clear();
        }
    }

    /// <summary>How many seconds the buffer holds right now.</summary>
    public static double BufferedSeconds()
    {
        lock (Gate) return Buffer.Count < 2 ? 0 : Buffer.Last!.Value.Time - Buffer.First!.Value.Time;
    }

    /// <summary>Saves the last <paramref name="seconds"/> of the buffer to an MP4 on a worker; returns its path, or "".</summary>
    public static string SaveBuffer(string name, double seconds)
    {
        List<Frame> frames = Snapshot(seconds);
        if (frames.Count < 2) return string.Empty;
        string output = UniquePath(Path.Combine(RecordingsFolder(),
            SafeName(string.IsNullOrWhiteSpace(name) ? "replay_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : name) + ".mp4"));
        double fps;
        lock (Gate) fps = _bufferFps;
        Task.Run(() =>
        {
            try
            {
                EncodeFrames(frames, output, fps);
                RenderLog.Line("Replay saved: " + output);
            }
            catch (Exception error) when (error is InvalidOperationException or COMException or IOException
                or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                RenderLog.Line("Replay could not be saved as a video: " + error.Message);
            }
        });
        return output;
    }

    // ── Playback ───────────────────────────────────────────────────────────────────────────

    /// <summary>Plays the last <paramref name="secondsBack"/> of the buffer at <paramref name="speed"/> (0.5 is slow motion).</summary>
    public static bool Play(double secondsBack, double speed)
    {
        List<Frame> frames = Snapshot(secondsBack);
        if (frames.Count < 2) return false;
        lock (Gate)
        {
            _playback = frames;
            _playStarted = Now;
            _playSpeed = Math.Clamp(speed, 0.05, 8);
            _shownFrame = -1;
        }
        return true;
    }

    public static void StopPlayback()
    {
        lock (Gate) _playback = null;
    }

    /// <summary>0 at the start of the replay, 1 when it has finished (or none is playing).</summary>
    public static double PlaybackProgress()
    {
        lock (Gate)
        {
            if (_playback is null) return 1;
            double length = _playback[^1].Time - _playback[0].Time;
            double progress = length <= 0 ? 1 : (Now - _playStarted) * _playSpeed / length;
            if (progress >= 1) _playback = null;
            return Math.Clamp(progress, 0, 1);
        }
    }

    /// <summary>
    /// The script texture holding the replay frame for now, decoded only when the frame changes;
    /// 0 when nothing is playing. Drawn by ReplayDraw through the normal texture path.
    /// </summary>
    public static int CurrentTexture()
    {
        Frame frame;
        lock (Gate)
        {
            if (_playback is null) return 0;
            double at = _playback[0].Time + (Now - _playStarted) * _playSpeed;
            int index = _playback.FindLastIndex(candidate => candidate.Time <= at);
            if (index < 0) index = 0;
            if (at > _playback[^1].Time) { _playback = null; return 0; }
            if (index == _shownFrame && _texture != 0 && Rendering.ScriptTextures.Exists(_texture)) return _texture;
            _shownFrame = index;
            frame = _playback[index];
        }
        if (_texture == 0 || !Rendering.ScriptTextures.Exists(_texture)
            || Rendering.ScriptTextures.Width(_texture) != frame.Width || Rendering.ScriptTextures.Height(_texture) != frame.Height)
        {
            if (_texture != 0) Rendering.ScriptTextures.Destroy(_texture);
            _texture = Rendering.ScriptTextures.Create(frame.Width, frame.Height);
        }
        byte[]? pixels = Rendering.ScriptTextures.BeginWrite(_texture, 0, 0, frame.Width, frame.Height, out int width, out int height);
        if (pixels is null) return 0;
        byte[] bgra = DecodeJpeg(frame.Jpeg, out int w, out int h);
        int count = Math.Min(width * height, w * h);
        for (int i = 0; i < count; i++)
        {
            int at = i * 4;
            pixels[at] = bgra[at + 2];
            pixels[at + 1] = bgra[at + 1];
            pixels[at + 2] = bgra[at];
            pixels[at + 3] = 255;
        }
        return _texture;
    }

    /// <summary>Stops everything; a recording in progress is finished first (the game is closing).</summary>
    public static void Shutdown()
    {
        if (IsRecording) StopRecording(wait: true);
        DisableBuffer();
        StopPlayback();
    }

    // ── Worker ─────────────────────────────────────────────────────────────────────────────

    private static void EnsureWorker()
    {
        lock (Gate)
        {
            if (_jobs is not null) return;
            // A few frames of slack: when the worker falls behind, new frames are dropped, never waited for.
            _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
            _ = Task.Run(WorkAsync);
        }
    }

    private static int _pending;

    private static async Task WorkAsync()
    {
        await foreach (Job job in _jobs!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Increment(ref _pending);
            try { Process(job); }
            catch (Exception error) when (error is ExternalException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                RenderLog.Line("Recording frame skipped: " + error.Message);
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    /// <summary>Completes when the frames handed over so far have been processed (or after two seconds).</summary>
    private static Task Drain() => Task.Run(async () =>
    {
        Stopwatch waited = Stopwatch.StartNew();
        while ((_jobs?.Reader.Count > 0 || Volatile.Read(ref _pending) > 0) && waited.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20).ConfigureAwait(false);
    });

    private static void Process(Job job)
    {
        if (job.Into == Target.Recording)
        {
            string folder, file;
            bool png;
            lock (Gate)
            {
                folder = _recordFolder;
                png = _recordPng;
                file = Path.Combine(folder, $"frame_{++_recordIndex:D6}" + (png ? ".png" : ".jpg"));
            }
            using Bitmap frame = FromBgra(job.Bgra, job.Width, job.Height);
            if (png) frame.Save(file, ImageFormat.Png);
            else SaveJpeg(frame, file);
            lock (Gate) RecordFrames.Add((job.Time, file));
            return;
        }

        int width = Math.Max(2, (int)(job.Width * job.Scale)), height = Math.Max(2, (int)(job.Height * job.Scale));
        using Bitmap source = FromBgra(job.Bgra, job.Width, job.Height);
        using Bitmap scaled = new(width, height, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, 0, 0, width, height);
        }
        using MemoryStream jpeg = new();
        SaveJpeg(scaled, jpeg);
        lock (Gate)
        {
            if (!_bufferOn) return;
            Buffer.AddLast(new Frame(job.Time, jpeg.ToArray(), width, height));
            while (Buffer.Count > 1 && Buffer.Last!.Value.Time - Buffer.First!.Value.Time > _bufferSeconds) Buffer.RemoveFirst();
        }
    }

    private static List<Frame> Snapshot(double seconds)
    {
        lock (Gate)
        {
            if (Buffer.Count == 0) return [];
            double from = Buffer.Last!.Value.Time - Math.Max(0.5, seconds);
            return [.. Buffer.Where(frame => frame.Time >= from)];
        }
    }

    // ── Encoding ───────────────────────────────────────────────────────────────────────────

    private static void EncodeFrames(List<Frame> frames, string output, double fps)
    {
        using Mp4Writer writer = new(output, frames[0].Width, frames[0].Height, (int)Math.Round(fps));
        WriteTimed(writer, frames.Select(frame => (frame.Time, (Func<(byte[], int, int)>)(() => (DecodeJpeg(frame.Jpeg, out int w, out int h), w, h)))).ToList(), fps);
        writer.Finish();
    }

    private static void EncodeFiles(List<(double Time, string File)> frames, string output, double fps)
    {
        if (frames.Count == 0) throw new InvalidOperationException("No frames were recorded.");
        DecodeFile(frames[0].File, out int width, out int height);
        using Mp4Writer writer = new(output, width, height, (int)Math.Round(fps));
        WriteTimed(writer, frames.Select(frame => (frame.Time, (Func<(byte[], int, int)>)(() => (DecodeFile(frame.File, out int w, out int h), w, h)))).ToList(), fps);
        writer.Finish();
    }

    /// <summary>Writes frames at the video's rate, holding each until the next one's time, so the video keeps real time.</summary>
    private static void WriteTimed(Mp4Writer writer, List<(double Time, Func<(byte[] Bgra, int Width, int Height)> Load)> frames, double fps)
    {
        double start = frames[0].Time, step = 1.0 / fps;
        int written = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            double end = i + 1 < frames.Count ? frames[i + 1].Time - start : frames[i].Time - start + step;
            int until = Math.Max(written + 1, (int)Math.Round(end / step));
            (byte[] bgra, int width, int height) = frames[i].Load();
            writer.WriteFrame(bgra, width, height, until - written);
            written = until;
        }
    }

    private static Bitmap FromBgra(byte[] bgra, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppRgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static byte[] DecodeJpeg(byte[] jpeg, out int width, out int height)
    {
        using MemoryStream stream = new(jpeg);
        using Bitmap bitmap = new(stream);
        return ToBgra(bitmap, out width, out height);
    }

    private static byte[] DecodeFile(string file, out int width, out int height)
    {
        using Bitmap bitmap = new(file);
        return ToBgra(bitmap, out width, out height);
    }

    private static byte[] ToBgra(Bitmap bitmap, out int width, out int height)
    {
        width = bitmap.Width;
        height = bitmap.Height;
        byte[] bgra = new byte[width * height * 4];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bgra;
    }

    private static readonly Lazy<(ImageCodecInfo Codec, EncoderParameters Quality)> Jpeg = new(() =>
    {
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
        EncoderParameters quality = new(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        return (codec, quality);
    });

    private static void SaveJpeg(Bitmap bitmap, string file) => bitmap.Save(file, Jpeg.Value.Codec, Jpeg.Value.Quality);

    private static void SaveJpeg(Bitmap bitmap, Stream stream) => bitmap.Save(stream, Jpeg.Value.Codec, Jpeg.Value.Quality);

    /// <summary>Writes a two-second test video of moving colour bars, to check this PC's video encoder.</summary>
    public static void WriteTestVideo(string path)
    {
        const int width = 320, height = 180, fps = 30;
        using Mp4Writer writer = new(path, width, height, fps);
        byte[] bgra = new byte[width * height * 4];
        for (int frame = 0; frame < fps * 2; frame++)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    bgra[at] = (byte)((x + frame * 4) & 255);
                    bgra[at + 1] = (byte)(y * 255 / height);
                    bgra[at + 2] = (byte)(255 - ((x + frame * 4) & 255));
                    bgra[at + 3] = 255;
                }
            writer.WriteFrame(bgra, width, height);
        }
        writer.Finish();
    }

    // ── Files ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>&lt;project&gt;/Recordings</c>, or for a game installed where it cannot write,
    /// <c>%LocalAppData%/Genesis Games/&lt;game folder&gt;/Recordings</c>.
    /// </summary>
    public static string RecordingsFolder()
    {
        string root = !string.IsNullOrWhiteSpace(ProjectPath) ? ProjectPath
            : !string.IsNullOrWhiteSpace(Scripting.PgslCommands.ProjectPath) ? Scripting.PgslCommands.ProjectPath
            : AppContext.BaseDirectory;
        string folder = Path.Combine(root, "Recordings");
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return folder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string game = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar)).Name;
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genesis Games", game, "Recordings");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string SafeName(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return name.Trim();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}

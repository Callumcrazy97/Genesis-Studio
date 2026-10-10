using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Reads a game's scripts and small resource documents through on two low-priority threads
    /// while it starts, so the game's own thread finds them ready when it first needs them.
    /// </summary>
    /// <remarks>
    /// The first open of a newly installed file waits while an antivirus scans it: about 4 ms a
    /// file on the PC this was measured on, and side by side on several threads in a fraction of
    /// that. A fresh install of a game with 140 scripts spent about 1.5 s of its first room's Create
    /// events opening them, and each sound's document cost its first play 4 ms. Later launches find
    /// the files scanned and cached, and this costs a fraction of a second of a background thread.
    /// GENESIS_READ_AHEAD=0 turns it off; a game run from Studio (live reload) does not use it.
    /// </remarks>
    public static class ProjectFileReadAhead
    {
        /// <summary>Largest file read ahead.</summary>
        public const long MaximumFileBytes = 256 * 1024;

        // Scripts first (the first room's Create events compile them), then what objects, sounds,
        // particles and models are made from.
        private static readonly string[] Kinds =
        {
            ".pgsl", ".object.json", ".audio.json", ".particle.json", ".model.json", ".shader.json",
            ".ui.json", ".physics.json", ".pathing.json", ".terrainentity.json",
        };

        private static int _running;
        private static string _summary;
        private static Action<string> _report;
        private static readonly object Gate = new();

        /// <summary>True while files are still being read.</summary>
        public static bool Running => Volatile.Read(ref _running) != 0;

        /// <summary>Starts reading the game's small files ahead on background threads.</summary>
        public static void Start(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || Environment.GetEnvironmentVariable("GENESIS_READ_AHEAD") == "0") return;
            string assets = Path.Combine(projectPath, "Assets");
            if (!Directory.Exists(assets) || Interlocked.Exchange(ref _running, 1) != 0) return;
            var thread = new Thread(() => Run(assets))
            {
                IsBackground = true,
                Name = "Genesis game file read-ahead",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        /// <summary>Where to write one line saying what was read, when it is done (at once if it is).</summary>
        public static void ReportTo(Action<string> report)
        {
            string summary;
            lock (Gate)
            {
                _report = report;
                summary = _summary;
            }

            if (summary != null) report?.Invoke(summary);
        }

        private static void Run(string assets)
        {
            Stopwatch clock = Stopwatch.StartNew();
            long bytes = 0;
            int files = 0;
            try
            {
                var buckets = new List<string>[Kinds.Length];
                for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<string>();
                foreach (FileInfo file in new DirectoryInfo(assets).EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
                }))
                {
                    if (file.Length > MaximumFileBytes) continue;
                    for (int kind = 0; kind < Kinds.Length; kind++)
                    {
                        if (!file.Name.EndsWith(Kinds[kind], StringComparison.OrdinalIgnoreCase)) continue;
                        buckets[kind].Add(file.FullName);
                        break;
                    }
                }

                var order = new List<string>();
                foreach (List<string> bucket in buckets) order.AddRange(bucket);
                int next = -1;
                void Read()
                {
                    byte[] buffer = new byte[64 * 1024];
                    for (int index = Interlocked.Increment(ref next); index < order.Count; index = Interlocked.Increment(ref next))
                    {
                        try
                        {
                            using var stream = new FileStream(order[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                            int read;
                            long total = 0;
                            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read;
                            Interlocked.Add(ref bytes, total);
                            Interlocked.Increment(ref files);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }

                var helper = new Thread(Read) { IsBackground = true, Name = "Genesis game file read-ahead 2", Priority = ThreadPriority.BelowNormal };
                helper.Start();
                Read();
                helper.Join();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Nothing read ahead: each file is read when it is needed, as before.
            }
            finally
            {
                string summary = $"game files read ahead: {files} scripts and documents ({bytes / (1024.0 * 1024.0):F1} MB) on two background threads in {clock.ElapsedMilliseconds} ms";
                Action<string> report;
                lock (Gate)
                {
                    _summary = summary;
                    report = _report;
                    _running = 0;
                }

                report?.Invoke(summary);
            }
        }
    }
}

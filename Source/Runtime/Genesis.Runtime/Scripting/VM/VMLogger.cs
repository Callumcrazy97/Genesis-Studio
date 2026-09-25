#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Genesis.Runtime.Scripting;

namespace Genesis.Runtime.Scripting.VM
{
    public static class VMLogger
    {
        public static List<string> Logs { get; } = new List<string>();
        private static readonly List<string> _pipelineLogs = new List<string>(256);
        private const int MaxPipelineLogs = 400;
        private const int MaxUiLogs = 1000;

        public static bool EnableShadow { get; set; } = true;

        /// <summary>Legacy alias for Runtime Debug1D (requires DebuggingEnabled).</summary>
        public static bool Verbose
        {
            get => ScriptingDebugSettings.DebuggingEnabled && ScriptingDebugSettings.DebugRuntime1DPgslVm;
            set
            {
                if (value) { ScriptingDebugSettings.DebuggingEnabled = true; ScriptingDebugSettings.DebugRuntime1DPgslVm = true; }
                else { ScriptingDebugSettings.DebugRuntime1DPgslVm = false; ScriptingDebugSettings.DebuggingEnabled = false; }
            }
        }

        public static event Action OnLog;

        private static readonly ConcurrentDictionary<string, ErrorEntry> _errors = new();

        public static bool ShouldLog1D => DebugLog.ShouldLog1D;
        public static bool ShouldLog1DShadow => DebugLog.ShouldLog1DShadow;
        public static bool ShouldLog2D => DebugLog.ShouldLog2D;
        public static bool ShouldLog3DDraw => DebugLog.ShouldLog3DDraw;
        public static bool ShouldLog3DPipeline => DebugLog.ShouldLog3DPipeline;

        public static void LogDiag(string msg) => LogDiag3D(msg);

        public static void LogDiag1D(string msg)
        {
            if (!ShouldLog1D) return;
            Log(DebugLog.FormatPrefix("1D") + " " + msg);
        }

        public static void LogDiag2D(string msg)
        {
            if (!ShouldLog2D) return;
            Log(DebugLog.FormatPrefix("2D") + " " + msg);
        }

        public static void LogDiag3D(string msg)
        {
            if (!ShouldLog3DDraw) return;
            Log(DebugLog.FormatPrefix("3D") + " " + msg);
        }

        /// <summary>3D GPU / instancing pipeline trace (separate ring buffer for debug reports).</summary>
        public static void LogPipeline(string msg)
        {
            if (!ShouldLog3DPipeline) return;

            string entry = $"[{DateTime.Now:HH:mm:ss}] {DebugLog.FormatPrefix("3DPipe")} {msg}";
            lock (_pipelineLogs)
            {
                _pipelineLogs.Add(entry);
                if (_pipelineLogs.Count > MaxPipelineLogs)
                    _pipelineLogs.RemoveAt(0);
            }

            if (ScriptingDebugSettings.DebuggingEnabled)
            {
                AllLogsFileLogger.WriteLine(entry);
            }

            lock (Logs)
            {
                Logs.Add(entry);
                if (Logs.Count > MaxUiLogs) Logs.RemoveAt(0);
            }
            Console.WriteLine(entry);
            OnLog?.Invoke();
        }

        public static IReadOnlyList<string> GetPipelineLogTail(int maxLines = 80)
        {
            lock (_pipelineLogs)
            {
                if (_pipelineLogs.Count <= maxLines)
                    return _pipelineLogs.ToArray();
                return _pipelineLogs.Skip(_pipelineLogs.Count - maxLines).ToArray();
            }
        }

        public static void LogWarn(string msg) => LogWarn3D(msg);

        public static void LogWarn1D(string msg)
        {
            if (!ShouldLog1D) return;
            Log(DebugLog.FormatPrefix("1D") + " [Warn] " + msg);
        }

        public static void LogWarn2D(string msg)
        {
            if (!ShouldLog2D) return;
            Log(DebugLog.FormatPrefix("2D") + " [Warn] " + msg);
        }

        public static void LogWarn3D(string msg)
        {
            if (!ShouldLog3DDraw && !ShouldLog3DPipeline) return;
            Log(DebugLog.FormatPrefix("3D") + " [Warn] " + msg);
        }

        public static void Log(string msg)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (ScriptingDebugSettings.DebuggingEnabled)
            {
                AllLogsFileLogger.WriteLine(entry);
            }

            lock (Logs)
            {
                Logs.Add(entry);
                if (Logs.Count > MaxUiLogs) Logs.RemoveAt(0);
            }
            Console.WriteLine(entry);
            OnLog?.Invoke();
        }

        public static void LogError(string source, string sourceType, string eventType, string lineCode, string message, int lineNumber)
        {
            if (!ShouldLog1D) return;

            var key = $"{source}|{sourceType}|{eventType}|{lineNumber}|{message}";

            var entry = _errors.GetOrAdd(key, _ => new ErrorEntry
            {
                Source = source,
                SourceType = sourceType,
                EventType = eventType,
                LineNumber = lineNumber,
                LineCode = lineCode,
                Message = message,
                FirstSeen = DateTime.Now
            });

            int count;
            lock (entry)
            {
                entry.Count++;
                entry.LastSeen = DateTime.Now;
                count = entry.Count;
            }

            string errStr = $"[VM Shadow Error] {sourceType} {source} / {eventType} (line {lineNumber}): {message}";
            Log($"{DebugLog.FormatPrefix("1D")} {errStr}");
        }

        public static void FlushSummary()
        {
            if (!ShouldLog1D || _errors.IsEmpty) return;

            var lines = new List<string>();
            var now = DateTime.Now;

            foreach (var kvp in _errors.OrderBy(e => e.Value.FirstSeen))
            {
                var e = kvp.Value;
                lines.Add($"  {e.SourceType} {e.Source} / {e.EventType} (line {e.LineNumber}):");
                lines.Add($"    {e.LineCode}");
                lines.Add($"      {e.Message} (x{e.Count}, last: {e.LastSeen:HH:mm:ss})");
            }

            var block = $"[{now:HH:mm:ss}] {DebugLog.FormatPrefix("1D")} === VM Shadow Errors ({_errors.Count} unique) ===\n" +
                        string.Join("\n", lines) + "\n" +
                        "  =========================";

            Log(block);
        }

        public static void Clear()
        {
            AllLogsFileLogger.Flush();
            ClearInMemoryOnly();
            OnLog?.Invoke();
        }

        /// <summary>Clear UI buffers without flushing the pending file line (used when starting a new file session).</summary>
        public static void ClearInMemoryOnly()
        {
            lock (Logs)
                Logs.Clear();
            _errors.Clear();
            lock (_pipelineLogs)
                _pipelineLogs.Clear();
        }

        internal static void AppendCommittedLine(string line)
        {
            lock (Logs)
            {
                Logs.Add(line);
                if (Logs.Count > MaxUiLogs) Logs.RemoveAt(0);
            }
            try { Console.WriteLine(line); } catch { }
            OnLog?.Invoke();
        }

        internal static void UpdateLastCommittedLine(string line)
        {
            lock (Logs)
            {
                if (Logs.Count > 0)
                    Logs[Logs.Count - 1] = line;
                else
                    Logs.Add(line);
            }
            OnLog?.Invoke();
        }

        private class ErrorEntry
        {
            public string Source { get; set; }
            public string SourceType { get; set; }
            public string EventType { get; set; }
            public int LineNumber { get; set; }
            public string LineCode { get; set; }
            public string Message { get; set; }
            public int Count { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
        }
    }
}

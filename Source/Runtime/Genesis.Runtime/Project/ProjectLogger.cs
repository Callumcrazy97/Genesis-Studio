using System;
using System.IO;
using System.Text;
using Genesis.Runtime.Scripting;

namespace Genesis.Runtime.Project
{
    /// <summary>Writes gameplay diagnostics into the project's Ember/Debug/Logs folder.</summary>
    public sealed class ProjectLogger : IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _path;
        private bool _disposed;

        public ProjectLogger(string projectPath, string logFileName = "project_player.log")
        {
            ProjectPaths.EnsureDebugDirs(projectPath);
            _path = Path.Combine(ProjectPaths.LogsDir(projectPath), logFileName);
            try
            {
                File.WriteAllText(_path, $"=== Genesis project player {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
            catch { _path = null; }
        }

        public string LogPath => _path;

        public void Line(string message)
        {
            if (_disposed || string.IsNullOrEmpty(_path)) return;
            try
            {
                lock (_lock)
                    File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { /* logging must never throw */ }
        }

        /// <summary>Persist a structured script failure and its first stack trace.</summary>
        public void WriteScriptDiagnostic(ScriptDiagnostic diagnostic)
        {
            if (diagnostic == null) return;
            Line(diagnostic.ToLogLine());
            if (diagnostic.RepeatCount != 1 || string.IsNullOrWhiteSpace(diagnostic.StackTrace)) return;

            string stack = diagnostic.StackTrace
                .Replace("\r\n", " | ", StringComparison.Ordinal)
                .Replace("\n", " | ", StringComparison.Ordinal)
                .Replace("\r", " | ", StringComparison.Ordinal);
            if (stack.Length > 6000) stack = stack[..6000] + "...";
            Line("[SCRIPT STACK] " + stack);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}

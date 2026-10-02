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
            string header = $"=== Genesis project player {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
            _path = Path.Combine(ProjectPaths.LogsDir(projectPath), logFileName);
            try
            {
                File.WriteAllText(_path, header);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The folders were there already but may not be written to: keep the log in the
                // player's own folder instead of running without one.
                _path = null;
                if (ProjectPaths.UsesUserDebugFolder(projectPath)) return;
                ProjectPaths.UseUserDebugFolder(projectPath);
                string elsewhere = Path.Combine(ProjectPaths.LogsDir(projectPath), logFileName);
                try
                {
                    File.WriteAllText(elsewhere, header);
                    _path = elsewhere;
                }
                catch (Exception again) when (again is UnauthorizedAccessException or IOException) { }
            }
            catch { _path = null; }
        }

        public string LogPath => _path;

        public void Line(string message)
        {
            if (_disposed || string.IsNullOrEmpty(_path)) return;
            string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";
            lock (_lock)
            {
                // Something reading the log at this instant (Studio's console, a test, a text editor)
                // holds it without write sharing, and the append fails. The line used to be dropped;
                // a reader is done within milliseconds, so wait for it briefly instead.
                // A program that keeps the log locked must not stall every later line: after one
                // wait that got nowhere, lines are tried once each until an append succeeds again.
                int attempts = _heldByAnotherProgram ? 1 : 40;
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    try
                    {
                        File.AppendAllText(_path, line, Encoding.UTF8);
                        _heldByAnotherProgram = false;
                        return;
                    }
                    catch (IOException) { if (attempt + 1 < attempts) System.Threading.Thread.Sleep(5); }
                    catch { return; /* logging must never throw */ }
                }

                _heldByAnotherProgram = true;
            }
        }

        private bool _heldByAnotherProgram;

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

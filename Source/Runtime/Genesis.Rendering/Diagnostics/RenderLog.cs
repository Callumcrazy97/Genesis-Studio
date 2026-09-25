using System;
using System.IO;
using System.Text;

namespace Genesis.Rendering.Diagnostics
{
    /// <summary>
    /// Minimal always-on runtime renderer logger. Uses per-user writable storage instead of
    /// modifying the installed Engine or Game folders. Each process has its own file.
    /// </summary>
    public static class RenderLog
    {
        private static readonly object _lock = new object();
        private static string _path;
        private static bool _init;

        public static string Path => _path;

        public static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) local = System.IO.Path.GetTempPath();
                string dir = System.IO.Path.Combine(local, "GenesisRuntime", "Logs");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "render-" + Environment.ProcessId + ".log");
                File.WriteAllText(_path, $"=== Genesis render log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }
            catch
            {
                try
                {
                    _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "genesis-render-" + Environment.ProcessId + ".log");
                    File.WriteAllText(_path, $"=== Genesis render log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
                }
                catch { _path = null; }
            }
        }

        public static void Line(string message)
        {
            if (!_init) Init();
            if (_path == null) return;
            try
            {
                lock (_lock)
                    File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {message}\n", Encoding.UTF8);
            }
            catch { /* logging must never throw */ }
        }
    }
}

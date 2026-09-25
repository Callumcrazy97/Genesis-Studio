using System;
using System.Collections.Generic;
using System.IO;

namespace Genesis.Runtime
{
    /// <summary>
    /// Genesis Studio paths. The editor lives in <see cref="StudioDir"/>; the game player is
    /// <c>GenesisStudio/Player/GenesisEngine.exe</c> (same binary shipped in exports).
    /// </summary>
    public static class RuntimePaths
    {
        public const string RuntimeExeName = "GenesisEngine.exe";
        public const string PlayerSubfolder = "Player";
        public const string RuntimeSubfolder = PlayerSubfolder;

        public static string StudioDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>Folder containing <see cref="RuntimeExeName"/> (Player publish output).</summary>
        public static string ResolveRuntimeDir()
        {
            string bundled = Path.Combine(StudioDir, PlayerSubfolder);
            if (File.Exists(Path.Combine(bundled, RuntimeExeName)))
                return bundled;

            foreach (string candidate in EnumerateDevCandidates())
            {
                if (File.Exists(Path.Combine(candidate, RuntimeExeName)))
                    return candidate;
            }

            return null;
        }

        public static string ResolveRuntimeExe()
        {
            string dir = ResolveRuntimeDir();
            return dir == null ? null : Path.Combine(dir, RuntimeExeName);
        }

        /// <summary>Where F5 / export write <c>GameScripts.dll</c> (next to the player exe).</summary>
        public static string ResolveScriptsDir() => ResolveRuntimeDir() ?? StudioDir;

        public static string ResolveGameScriptsDll()
            => Path.Combine(ResolveScriptsDir(), "GameScripts.dll");

        public static IEnumerable<string> GetDevCandidateDirs() => EnumerateDevCandidates();

        private static IEnumerable<string> EnumerateDevCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string start in new[] { StudioDir, Directory.GetCurrentDirectory() })
            {
                string search = start;
                while (!string.IsNullOrEmpty(search))
                {
                    foreach (string suffix in new[]
                    {
                        Path.Combine("GenesisStudio", PlayerSubfolder),
                        Path.Combine("Runtime", "bin", "x64", "Release", "net10.0-windows"),
                        Path.Combine("Runtime", "bin", "x64", "Debug", "net10.0-windows"),
                        Path.Combine("JustTheEngine", "GenesisStudio", PlayerSubfolder),
                        // In-tree Genesis.Player build outputs (Studio running from source).
                        Path.Combine("Source", "Genesis.Player", "bin", "Release", "net10.0-windows"),
                        Path.Combine("Source", "Genesis.Player", "bin", "Debug", "net10.0-windows"),
                        Path.Combine("Source", "Genesis.Player", "bin", "Release", "net10.0-windows", "win-x64"),
                        Path.Combine("Source", "Genesis.Player", "bin", "Debug", "net10.0-windows", "win-x64"),
                    })
                    {
                        string candidate = Path.Combine(search, suffix);
                        string full;
                        try { full = Path.GetFullPath(candidate); }
                        catch { continue; }
                        if (seen.Add(full))
                            yield return full;
                    }

                    string parent = Path.GetDirectoryName(search);
                    if (parent == search) break;
                    search = parent;
                }
            }
        }
    }
}

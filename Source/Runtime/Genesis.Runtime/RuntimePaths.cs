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

        /// <summary>The Player's own folder, where an exported game keeps <c>GameScripts.dll</c>.</summary>
        public static string ResolveScriptsDir() => ResolveRuntimeDir() ?? StudioDir;

        public static string ResolveGameScriptsDll()
            => Path.Combine(ResolveScriptsDir(), "GameScripts.dll");

        /// <summary>Names the compiled scripts the Player should load instead of the ones beside it.</summary>
        public const string GameScriptsEnvironmentVariable = "GENESIS_GAME_SCRIPTS";

        /// <summary>
        /// Where Run writes a project's compiled C# scripts: inside the project, which its author
        /// can always write to. Studio's own Player folder cannot be used: installed under Program
        /// Files it is read-only, and it is shared by every project.
        /// </summary>
        public static string ProjectScriptsDir(string projectPath)
            => Path.Combine(projectPath, ".genesis", "Run");

        public static string ProjectScriptsDll(string projectPath)
            => Path.Combine(ProjectScriptsDir(projectPath), "GameScripts.dll");

        /// <summary>The compiled scripts this Player loads: the ones Studio named, else those beside it.</summary>
        public static string PlayerGameScriptsDll()
        {
            string named = Environment.GetEnvironmentVariable(GameScriptsEnvironmentVariable);
            return string.IsNullOrWhiteSpace(named)
                ? Path.Combine(AppContext.BaseDirectory, "GameScripts.dll")
                : named;
        }

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

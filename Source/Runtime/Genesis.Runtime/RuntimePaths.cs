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
        public static string ResolveRuntimeDir() => FindRuntimeDir(StudioDir, null, confineToWorktree: false);

        /// <summary>
        /// The Player a run starts (Studio's Run, <c>game.run</c>, <c>game.benchmark</c>). It is
        /// <see cref="ResolveRuntimeDir"/>, except when Studio runs from a git worktree: then only a Player
        /// inside that worktree counts, because the search up the folders would otherwise reach the main
        /// checkout and quietly play its older build. Null when there is none (see
        /// <see cref="MissingPlayerMessage()"/>).
        /// </summary>
        public static string ResolveRunDir() => FindRuntimeDir(StudioDir, null, confineToWorktree: true);

        internal static string FindRuntimeDir(string studioDir, string currentDirectory, bool confineToWorktree)
        {
            string bundled = Path.Combine(studioDir, PlayerSubfolder);
            if (File.Exists(Path.Combine(bundled, RuntimeExeName)))
                return bundled;

            string worktree = confineToWorktree ? FindWorktreeRoot(studioDir) : null;
            foreach (string candidate in EnumerateDevCandidates(studioDir, currentDirectory ?? Directory.GetCurrentDirectory(), worktree))
            {
                if (File.Exists(Path.Combine(candidate, RuntimeExeName)))
                    return candidate;
            }

            return null;
        }

        /// <summary>Why <see cref="ResolveRunDir"/> found no Player, and what to do about it.</summary>
        public static string MissingPlayerMessage() => MissingPlayerMessage(StudioDir, null);

        internal static string MissingPlayerMessage(string studioDir, string currentDirectory)
        {
            string worktree = FindWorktreeRoot(studioDir);
            if (worktree == null)
                return "Could not find GenesisEngine.exe. Build Genesis Studio first (Build.bat).";

            // The Player the search would have found past the worktree: another checkout's build of other source.
            string other = FindRuntimeDir(studioDir, currentDirectory, confineToWorktree: false);
            return $"Could not find {RuntimeExeName} in the git worktree {worktree}, and a worktree does not run another checkout's Player "
                + (other == null ? "(no other Player was found either). " : $"(it would have run {Path.Combine(other, RuntimeExeName)}, built from different source). ")
                + $"Build this worktree's own Player: dotnet build \"{Path.Combine(worktree, "Source", "Genesis.Player", "Genesis.Player.csproj")}\" -c Release "
                + $"(or agent-compile.ps1 -Repo \"{worktree}\" -Project Source\\Genesis.Player\\Genesis.Player.csproj), or run Build.bat --quick there.";
        }

        /// <summary>
        /// The root of the git worktree that <paramref name="path"/> is inside: the nearest folder above it
        /// whose <c>.git</c> is a file, which is what <c>git worktree add</c> makes (a main checkout has a
        /// <c>.git</c> folder). Null in a main checkout, in an installed or exported game, or when the path is
        /// not usable.
        /// </summary>
        internal static string FindWorktreeRoot(string path)
        {
            string search = Normalize(path);
            while (!string.IsNullOrEmpty(search))
            {
                string git = Path.Combine(search, ".git");
                if (Directory.Exists(git))
                    return null;
                if (File.Exists(git))
                    return search;
                search = Path.GetDirectoryName(search);
            }

            return null;
        }

        /// <summary>The full path without a trailing separator (a drive's root keeps its own), or null when it cannot be made.</summary>
        private static string Normalize(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }

        private static bool IsWithin(string path, string root)
        {
            string full = Normalize(path);
            return full != null
                && (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
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

        public static IEnumerable<string> GetDevCandidateDirs() => EnumerateDevCandidates(StudioDir, Directory.GetCurrentDirectory(), null);

        private static readonly string[] DevSuffixes =
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
        };

        /// <summary>A worktree also has the Player its own Build.bat published, after the build outputs above.</summary>
        private static readonly string[] WorktreeSuffixes = [.. DevSuffixes, Path.Combine("Genesis Application", PlayerSubfolder)];

        /// <summary>
        /// The folders a Player may be in when none is bundled, nearest first. With a <paramref name="worktreeRoot"/>
        /// only that git worktree is searched (see <see cref="ResolveRunDir"/>); without one, every folder up to the drive's root.
        /// </summary>
        private static IEnumerable<string> EnumerateDevCandidates(string studioDir, string currentDirectory, string worktreeRoot)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] suffixes = worktreeRoot == null ? DevSuffixes : WorktreeSuffixes;

            foreach (string start in new[] { studioDir, currentDirectory })
            {
                // A worktree's Player is its own: the current folder, if it is in another checkout, is not searched.
                if (worktreeRoot != null && !IsWithin(start, worktreeRoot))
                    continue;

                string search = start;
                while (!string.IsNullOrEmpty(search))
                {
                    foreach (string suffix in suffixes)
                    {
                        string candidate = Path.Combine(search, suffix);
                        string full;
                        try { full = Path.GetFullPath(candidate); }
                        catch { continue; }
                        if (seen.Add(full))
                            yield return full;
                    }

                    // The worktree's own folder is the last one looked in: above it is another checkout.
                    if (worktreeRoot != null && string.Equals(Normalize(search), worktreeRoot, StringComparison.OrdinalIgnoreCase))
                        break;

                    string parent = Path.GetDirectoryName(search);
                    if (parent == search) break;
                    search = parent;
                }
            }
        }
    }
}

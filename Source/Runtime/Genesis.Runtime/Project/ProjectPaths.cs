using System;
using System.IO;

namespace Genesis.Runtime.Project
{
    /// <summary>Project-relative debug artifact folders used by automated verification.</summary>
    public static class ProjectPaths
    {
        public static string DebugRoot(string projectPath)
            => Path.Combine(projectPath, "Debug");

        public static string ImagesDir(string projectPath)
            => Path.Combine(DebugRoot(projectPath), "Images");

        public static string LogsDir(string projectPath)
            => Path.Combine(DebugRoot(projectPath), "Logs");

        public static void EnsureDebugDirs(string projectPath)
        {
            Directory.CreateDirectory(ImagesDir(projectPath));
            Directory.CreateDirectory(LogsDir(projectPath));
            // Legacy path used by older projects / headless copies.
            Directory.CreateDirectory(Path.Combine(projectPath, "Ember", "Debug", "Images"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Ember", "Debug", "Logs"));
        }

        /// <summary>Reads <c>Game/settings.ini</c> StartRoom= from an exported build.</summary>
        public static string ReadStartRoom(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            string ini = Path.Combine(projectPath, "Game", "settings.ini");
            if (!File.Exists(ini)) return null;

            foreach (string line in File.ReadAllLines(ini))
            {
                if (line.StartsWith("StartRoom=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("StartRoom=".Length).Trim();
            }

            return null;
        }
    }
}

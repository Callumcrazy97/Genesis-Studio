using System;
using System.IO;
using System.Linq;
using System.Text.Json;

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

        /// <summary>Reads the legacy export override, then the authored project start room.</summary>
        public static string ReadStartRoom(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            string ini = Path.Combine(projectPath, "Game", "settings.ini");
            foreach (string line in File.Exists(ini) ? File.ReadAllLines(ini) : Array.Empty<string>())
            {
                if (line.StartsWith("StartRoom=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("StartRoom=".Length).Trim();
            }
            string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
            if (projects.Length > 1) throw new InvalidDataException("Multiple project manifests; specify --room explicitly.");
            if (projects.Length == 1)
            {
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                if (manifest.RootElement.TryGetProperty("startRoom", out JsonElement startRoom)
                    && startRoom.ValueKind == JsonValueKind.String)
                    return startRoom.GetString();
            }
            return null;
        }
    }
}

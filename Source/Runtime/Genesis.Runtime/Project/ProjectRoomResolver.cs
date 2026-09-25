using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Genesis.Runtime.Project
{
    /// <summary>Resolves project folders and room files for the editor and runtime player.</summary>
    public static class ProjectRoomResolver
    {
        /// <summary>
        /// Normalise a path to the project root folder (the directory containing Scripts/ or a *.proj file).
        /// </summary>
        public static string ResolveProjectRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            path = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(path) && path.EndsWith(".proj", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(path);

            if (File.Exists(path) && path.EndsWith(".genesisproj", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(path);

            if (Directory.Exists(path))
            {
                if (Directory.Exists(Path.Combine(path, "Scripts"))
                    || Directory.GetFiles(path, "*.proj").Length > 0
                    || Directory.GetFiles(path, "*.genesisproj").Length > 0
                    || Directory.Exists(Path.Combine(path, "Rooms"))
                    || Directory.Exists(Path.Combine(path, "Assets")))
                    return path;
            }

            return Directory.Exists(path) ? path : null;
        }

        /// <summary>
        /// For exported / standalone builds: the folder next to <c>GenesisEngine.exe</c> that
        /// contains <c>Rooms/</c> (and usually <c>GameScripts.dll</c>).
        /// </summary>
        public static string ResolveStandaloneProjectRoot(string playerBaseDir)
        {
            if (string.IsNullOrWhiteSpace(playerBaseDir)) return null;
            playerBaseDir = Path.GetFullPath(playerBaseDir);
            if (Directory.Exists(Path.Combine(playerBaseDir, "Rooms"))
                || Directory.Exists(Path.Combine(playerBaseDir, "Assets"))
                || Directory.EnumerateFiles(playerBaseDir, "*.genesisproj").Any()
                || Directory.EnumerateFiles(playerBaseDir, "*.proj").Any())
                return playerBaseDir;
            return null;
        }

        public static string ResolveRoomName(string projectPath, string preferredRoomName)
        {
            if (!string.IsNullOrWhiteSpace(preferredRoomName))
            {
                if (ResolveRoomFile(projectPath, preferredRoomName) != null)
                    return ResourceNames.Name(projectPath, preferredRoomName, ResourceType.Room);
            }

            return FindFirstRoomName(projectPath);
        }

        public static string ResolveRoomFile(string projectPath, string roomName)
        {
            string path = ResourceNames.Resolve(projectPath, roomName, ResourceType.Room);
            return path.Length == 0 ? null : path;
        }

        public static string FindFirstRoomName(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return null;
            return ResourceNames.For(projectPath).Entries.FirstOrDefault(e => e.Type == ResourceType.Room)?.Name;
        }

        /// <summary>Room folders in priority order: legacy <c>Rooms/</c>, then the Studio <c>Assets/</c> tree.</summary>
        private static IEnumerable<string> RoomSearchRoots(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) yield break;

            string legacy = Path.Combine(projectPath, "Rooms");
            if (Directory.Exists(legacy)) yield return legacy;

            string assets = Path.Combine(projectPath, "Assets");
            if (Directory.Exists(assets)) yield return assets;
        }

        private static string RoomNameOf(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".room", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);
            return name;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Genesis.Runtime.Project
{
    /// <summary>Project-relative debug artifact folders used by automated verification.</summary>
    public static class ProjectPaths
    {
        // Games whose own folder may not be written to, and where their logs and pictures go instead.
        private static readonly ConcurrentDictionary<string, string> UserDebugRoots = new(StringComparer.OrdinalIgnoreCase);

        public static string DebugRoot(string projectPath)
            => UserDebugRoots.TryGetValue(Path.GetFullPath(projectPath), out string elsewhere)
                ? elsewhere
                : Path.Combine(projectPath, "Debug");

        public static string ImagesDir(string projectPath)
            => Path.Combine(DebugRoot(projectPath), "Images");

        public static string LogsDir(string projectPath)
            => Path.Combine(DebugRoot(projectPath), "Logs");

        /// <summary>True once the game's logs and pictures have been moved to the player's own folder.</summary>
        public static bool UsesUserDebugFolder(string projectPath)
            => UserDebugRoots.ContainsKey(Path.GetFullPath(projectPath));

        public static void EnsureDebugDirs(string projectPath)
        {
            if (UsesUserDebugFolder(projectPath))
            {
                CreateUserDebugDirs(projectPath);
                return;
            }

            try
            {
                Directory.CreateDirectory(ImagesDir(projectPath));
                Directory.CreateDirectory(LogsDir(projectPath));
                // Legacy path used by older projects / headless copies.
                Directory.CreateDirectory(Path.Combine(projectPath, "Ember", "Debug", "Images"));
                Directory.CreateDirectory(Path.Combine(projectPath, "Ember", "Debug", "Logs"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                UseUserDebugFolder(projectPath);
            }
        }

        /// <summary>
        /// Sends the game's logs and pictures to the player's own folder, beside its saves. A game
        /// installed under Program Files, on a read-only share or on a disc may not write beside
        /// itself, and that must not stop it starting.
        /// </summary>
        public static void UseUserDebugFolder(string projectPath)
        {
            UserDebugRoots[Path.GetFullPath(projectPath)] =
                Path.Combine(ProjectNumberSave.GetWritableDirectory(projectPath), "Debug");
            CreateUserDebugDirs(projectPath);
        }

        private static void CreateUserDebugDirs(string projectPath)
        {
            try
            {
                Directory.CreateDirectory(ImagesDir(projectPath));
                Directory.CreateDirectory(LogsDir(projectPath));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Nowhere to write at all. The game still runs; it keeps no log.
            }
        }

        /// <summary>
        /// The project's <c>projectId</c> and <c>name</c> from its project file; empty strings when
        /// there is no single project file or it cannot be read. Copies and exports of one project
        /// share both, so they identify one game across machines.
        /// </summary>
        public static (string Id, string Name) ReadProjectIdentity(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return ("", "");
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return ("", "");
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                if (manifest.RootElement.ValueKind != JsonValueKind.Object) return ("", "");
                string Text(string camel, string pascal) =>
                    (manifest.RootElement.TryGetProperty(camel, out JsonElement value) || manifest.RootElement.TryGetProperty(pascal, out value))
                    && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
                return (Text("projectId", "ProjectId"), Text("name", "Name"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return ("", "");
            }
        }

        /// <summary>Reads the legacy export override, then the authored project start room.</summary>
        /// <summary>The project's own audio buses (its <c>audioBuses</c>), lower case; empty when it names none.</summary>
        public static System.Collections.Generic.IReadOnlyList<string> ReadAudioBuses(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return [];
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return [];
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                if (!manifest.RootElement.TryGetProperty("audioBuses", out JsonElement buses) || buses.ValueKind != JsonValueKind.Array)
                    return [];
                return buses.EnumerateArray()
                    .Where(bus => bus.ValueKind == JsonValueKind.String)
                    .Select(bus => bus.GetString()!.Trim().ToLowerInvariant())
                    .Where(bus => bus.Length > 0)
                    .Distinct()
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }

        /// <summary>
        /// The project's <c>convertRightHandedModels</c> setting: whether imported glTF models are
        /// mirrored along Z so they are not drawn as their mirror image. Off unless the project says so.
        /// </summary>
        public static bool ReadConvertRightHandedModels(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return false;
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return false;
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                return manifest.RootElement.TryGetProperty("convertRightHandedModels", out JsonElement value)
                    && value.ValueKind == JsonValueKind.True;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// The project's <c>rendering.blendGuiInLinearLight</c> setting: whether each GUI draw event
        /// starts blending in linear light (DrawSetBlendLinear). Off unless the project says so.
        /// </summary>
        public static bool ReadBlendGuiInLinearLight(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return false;
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return false;
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                return manifest.RootElement.TryGetProperty("rendering", out JsonElement rendering)
                    && rendering.ValueKind == JsonValueKind.Object
                    && rendering.TryGetProperty("blendGuiInLinearLight", out JsonElement value)
                    && value.ValueKind == JsonValueKind.True;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// The project's <c>rendering.antiAliasing</c> setting ("Off", "FXAA", "SMAA" or "TAA"); null when the
        /// project names none or names one this engine does not know.
        /// </summary>
        public static Genesis.Shared.Interfaces.AntiAliasingMode? ReadAntiAliasing(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return null;
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return null;
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                if (manifest.RootElement.TryGetProperty("rendering", out JsonElement rendering)
                    && rendering.ValueKind == JsonValueKind.Object
                    && rendering.TryGetProperty("antiAliasing", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && Genesis.Shared.Interfaces.AntiAliasingModes.TryParse(value.GetString(), out Genesis.Shared.Interfaces.AntiAliasingMode mode))
                    return mode;
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Applies the project's own anti-aliasing (<see cref="ReadAntiAliasing"/>) when the game
        /// starts, unless <c>GENESIS_ANTI_ALIASING</c> already chose one for this run.
        /// </summary>
        public static void ApplyProjectAntiAliasing(string projectPath)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    Genesis.Shared.Interfaces.MeshLightingDefaults.AntiAliasingEnvironmentVariable)))
                return;
            if (ReadAntiAliasing(projectPath) is Genesis.Shared.Interfaces.AntiAliasingMode mode)
                Genesis.Shared.Interfaces.MeshLightingDefaults.AntiAliasing = mode;
        }

        /// <summary>
        /// The project's <c>rendering.backend</c> setting as written ("Vulkan", "Direct3D12"...); null
        /// when the project names none.
        /// </summary>
        public static string ReadRenderBackend(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return null;
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return null;
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                if (manifest.RootElement.TryGetProperty("rendering", out JsonElement rendering)
                    && rendering.ValueKind == JsonValueKind.Object
                    && rendering.TryGetProperty("backend", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString().Trim();
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Starts the game on the renderer its project names (<see cref="ReadRenderBackend"/>), unless
        /// <c>GENESIS_RENDER_BACKEND</c> already chose one for this run, as Studio's Run does. An exported
        /// game has no Studio to say, so this is how it gets the renderer its export or its project named.
        /// </summary>
        /// <remarks>
        /// The name is a request, not an explicit run choice: a renderer this machine cannot use falls
        /// back to Direct3D 11 (<see cref="Genesis.Rendering.Core.RenderBackendSelection.EffectiveBackend"/>),
        /// and a name this engine does not know leaves the Player's own default, Direct3D 11.
        /// </remarks>
        public static void ApplyProjectRenderBackend(string projectPath)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    Genesis.Rendering.Core.RenderBackendSelection.EnvironmentVariable)))
                return;
            string named = ReadRenderBackend(projectPath);
            if (named == null) return;
            try
            {
                Genesis.Rendering.Core.RenderBackendSelection.Configure(
                    Genesis.Rendering.Core.RenderBackendCatalog.ParseExplicitValue(named));
            }
            catch (ArgumentException)
            {
                // A removed or misspelt renderer: the default stands.
            }
        }

        /// The project's <c>runtime.preloadAudioMegabytes</c> setting: how many megabytes of sound
        /// samples a game decodes ahead of time while it loads. 0 (off) unless the project says so.
        /// </summary>
        public static int ReadPreloadAudioMegabytes(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return 0;
            try
            {
                string[] projects = Directory.EnumerateFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly).ToArray();
                if (projects.Length != 1) return 0;
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projects[0]));
                return manifest.RootElement.TryGetProperty("runtime", out JsonElement runtime)
                    && runtime.ValueKind == JsonValueKind.Object
                    && runtime.TryGetProperty("preloadAudioMegabytes", out JsonElement value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out int megabytes)
                    ? Math.Clamp(megabytes, 0, 16384)
                    : 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return 0;
            }
        }

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

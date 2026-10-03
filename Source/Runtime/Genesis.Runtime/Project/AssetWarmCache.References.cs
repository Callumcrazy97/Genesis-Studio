using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Project
{
    // What the first room needs: the files reachable from its room file, through the Objects,
    // Images, Models, Scripts and sounds it names and the ones those name in turn. Everything else
    // is prepared when a room asks for it (a room change prepares its room behind its cover).
    public sealed partial class AssetWarmCache
    {
        /// <summary>Set to 1 to prepare every game asset before the first room, as before.</summary>
        public const string PreloadAllEnvironmentVariable = "GENESIS_PRELOAD_ALL";

        private const int MaxReferenceDepth = 12;
        private const long MaxScannedFileBytes = 16L * 1024 * 1024;
        private static readonly Regex QuotedString = new("\"([^\"\\r\\n]{1,260})\"", RegexOptions.Compiled);

        /// <summary>What the preparation list covers, for the log.</summary>
        public string Scope { get; private set; } = string.Empty;

        private bool TryCollectFirstRoom(string projectPath, string startRoomName)
        {
            if (string.Equals(Environment.GetEnvironmentVariable(PreloadAllEnvironmentVariable), "1", StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(startRoomName)) return false;
            string roomFile = ProjectRoomResolver.ResolveRoomFile(projectPath, startRoomName);
            if (string.IsNullOrEmpty(roomFile) || !File.Exists(roomFile)) return false;

            string root = Path.GetFullPath(projectPath);
            var visitedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolvedReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((Path.GetFullPath(roomFile), 0));

            while (pending.Count > 0)
            {
                (string document, int depth) = pending.Dequeue();
                if (!visitedDocuments.Add(document) || depth > MaxReferenceDepth) continue;
                if (IsWarmCandidate(document)) assets.Add(document);

                void Follow(string file)
                {
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
                    string full = Path.GetFullPath(file);
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                    if (IsDocument(full)) pending.Enqueue((full, depth + 1));
                    else if (IsWarmCandidate(full)) assets.Add(full);
                }

                if (document.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase))
                    Follow(StudioModelResourceLoader.CanonicalPath(document));
                if (document.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase)
                    && ObjectEventStore.FolderFor(document) is string events && Directory.Exists(events))
                {
                    foreach (string script in Directory.EnumerateFiles(events, "*.pgsl")) Follow(script);
                }

                string directory = Path.GetDirectoryName(document) ?? root;
                foreach (string reference in ReferencesIn(document))
                {
                    // A name or a project path, then a path beside the document (an Image's frames).
                    string key = directory + "|" + reference;
                    if (!resolvedReferences.Add(key)) continue;
                    if (resolvedReferences.Add("|" + reference)) Follow(ResourceCatalog.ResolveFile(root, reference));
                    try { Follow(Path.Combine(directory, reference.Replace('/', Path.DirectorySeparatorChar))); }
                    catch (ArgumentException) { }
                }
            }

            foreach (string asset in assets)
            {
                _criticalPaths.Add(asset);
                _bytesTotal = checked(_bytesTotal + new FileInfo(asset).Length);
            }
            _criticalPaths.Sort(StringComparer.OrdinalIgnoreCase);
            Scope = $"referenced by the first room '{startRoomName}'";
            return true;
        }

        private static bool IsDocument(string path) =>
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase);

        /// <summary>The strings a descriptor or script mentions: JSON string values, or quoted text in code.</summary>
        private static IEnumerable<string> ReferencesIn(string document)
        {
            string text;
            try
            {
                if (new FileInfo(document).Length > MaxScannedFileBytes) yield break;
                text = File.ReadAllText(document);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var values = new List<string>();
                try
                {
                    using JsonDocument json = JsonDocument.Parse(text, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    });
                    CollectStrings(json.RootElement, values);
                }
                catch (JsonException)
                {
                    yield break;
                }
                foreach (string value in values)
                    if (IsPlausibleReference(value) && seen.Add(value)) yield return value;
                yield break;
            }

            foreach (Match match in QuotedString.Matches(text))
            {
                string value = match.Groups[1].Value;
                if (IsPlausibleReference(value) && seen.Add(value)) yield return value;
            }
        }

        private static void CollectStrings(JsonElement element, List<string> values)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    values.Add(element.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject()) CollectStrings(property.Value, values);
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray()) CollectStrings(item, values);
                    break;
            }
        }

        private static bool IsPlausibleReference(string value) =>
            value.Length is >= 2 and <= 260
            && value.IndexOfAny(new[] { '\r', '\n', '\0', '<', '>', '|', '*', '?', '"' }) < 0
            && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }
}

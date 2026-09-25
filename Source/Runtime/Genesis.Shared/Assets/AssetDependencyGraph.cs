using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Genesis.Shared.Assets
{
    /// <summary>
    /// Project-wide resource reference graph. Paths are canonical absolute paths internally;
    /// callers may use absolute, project-relative, Assets-relative, file-name, or asset-stem keys.
    /// </summary>
    public sealed class AssetDependencyGraph
    {
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".pgsl", ".pathing", ".shader", ".hlsl", ".txt", ".md", ".xml", ".yaml", ".yml",
        };

        private readonly Dictionary<string, HashSet<string>> _aliases =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _dependencies =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _dependents =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _resources = new(StringComparer.OrdinalIgnoreCase);

        public AssetDependencyGraph(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("A project root is required.", nameof(projectRoot));
            ProjectRoot = Path.GetFullPath(projectRoot);
            AssetsRoot = Path.Combine(ProjectRoot, "Assets");
            Refresh();
        }

        public string ProjectRoot { get; }
        public string AssetsRoot { get; }
        public int ResourceCount => _resources.Count;
        public int ReferenceCount => _dependencies.Values.Sum(set => set.Count);
        public IReadOnlyCollection<string> Resources => _resources.ToArray();

        /// <summary>Rebuild after create/delete/rename so both new and removed links are handled.</summary>
        public void Refresh()
        {
            ResourceCatalog.Invalidate(ProjectRoot);
            _aliases.Clear();
            _dependencies.Clear();
            _dependents.Clear();
            _resources.Clear();
            if (!Directory.Exists(AssetsRoot)) return;

            foreach (string path in Directory.EnumerateFiles(AssetsRoot, "*", SearchOption.AllDirectories))
            {
                string fullPath = Path.GetFullPath(path);
                _resources.Add(fullPath);
                AddAliases(fullPath);
            }

            // Authoritative public names override incidental payload stems/legacy filename aliases.
            ResourceCatalog catalog = ResourceCatalog.For(ProjectRoot);
            if (catalog.Conflicts.Any()) throw new InvalidDataException("Resource names must be unique across all resource types.");
            foreach (NamedResource entry in catalog.Entries)
                _aliases[NormalizeKey(entry.Name)] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.FullPath };

            foreach (string resource in _resources)
            {
                HashSet<string> direct = new(StringComparer.OrdinalIgnoreCase);
                foreach (string literal in ReadReferenceLiterals(resource))
                {
                    foreach (string resolved in ResolveAll(literal))
                    {
                        if (!string.Equals(resource, resolved, StringComparison.OrdinalIgnoreCase))
                            direct.Add(resolved);
                    }
                }

                AddSidecarDependency(resource, direct);
                _dependencies[resource] = direct;
                foreach (string dependency in direct)
                {
                    if (!_dependents.TryGetValue(dependency, out HashSet<string> reverse))
                    {
                        reverse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _dependents[dependency] = reverse;
                    }
                    reverse.Add(resource);
                }
            }
        }

        public IReadOnlyCollection<string> GetDependencies(string resourcePath, bool transitive = false)
        {
            HashSet<string> roots = ResolvePathOrAlias(resourcePath);
            if (!transitive)
            {
                HashSet<string> direct = new(StringComparer.OrdinalIgnoreCase);
                foreach (string root in roots)
                    if (_dependencies.TryGetValue(root, out HashSet<string> values)) direct.UnionWith(values);
                return direct.ToArray();
            }
            return Traverse(roots, _dependencies).ToArray();
        }

        /// <summary>Changed resources plus every resource that directly or transitively consumes them.</summary>
        public IReadOnlyCollection<string> GetAffectedPaths(IEnumerable<string> changedPaths)
        {
            if (changedPaths == null) return Array.Empty<string>();
            HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
            foreach (string changed in changedPaths)
            {
                if (string.IsNullOrWhiteSpace(changed)) continue;
                HashSet<string> resolved = ResolvePathOrAlias(changed);
                if (resolved.Count > 0) roots.UnionWith(resolved);
                else
                {
                    try { roots.Add(Path.GetFullPath(changed)); }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { }
                }
            }
            return Traverse(roots, _dependents).ToArray();
        }

        public bool DependsOn(string resourcePath, string candidateDependency, bool transitive = true)
        {
            HashSet<string> candidates = ResolvePathOrAlias(candidateDependency);
            if (candidates.Count == 0) return false;
            return GetDependencies(resourcePath, transitive).Any(candidates.Contains);
        }

        private HashSet<string> Traverse(
            IEnumerable<string> roots,
            Dictionary<string, HashSet<string>> edges)
        {
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new();
            foreach (string root in roots)
                if (visited.Add(root)) pending.Enqueue(root);
            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                if (!edges.TryGetValue(current, out HashSet<string> next)) continue;
                foreach (string path in next)
                    if (visited.Add(path)) pending.Enqueue(path);
            }
            return visited;
        }

        private HashSet<string> ResolvePathOrAlias(string value)
        {
            HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value)) return result;
            foreach (string path in ResolveAll(value)) result.Add(path);
            if (Path.IsPathRooted(value))
            {
                try
                {
                    string full = Path.GetFullPath(value);
                    if (_resources.Contains(full)) result.Add(full);
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { }
            }
            return result;
        }

        private IEnumerable<string> ResolveAll(string literal)
        {
            // JSON strings also include scripts and base64 pixel buffers. Never feed arbitrary
            // payloads into the filesystem (PathTooLongException used to escape the watcher).
            if (string.IsNullOrWhiteSpace(literal) || literal.Length > 32700
                || literal.IndexOfAny(Path.GetInvalidPathChars()) >= 0) yield break;
            string key = NormalizeKey(literal);
            if (string.IsNullOrWhiteSpace(key)) yield break;

            HashSet<string> matches = new(StringComparer.OrdinalIgnoreCase);
            if (_aliases.TryGetValue(key, out HashSet<string> aliased)) matches.UnionWith(aliased);

            try
            {
                string candidate = Path.IsPathRooted(literal)
                    ? Path.GetFullPath(literal)
                    : Path.GetFullPath(Path.Combine(ProjectRoot, literal.Replace('/', Path.DirectorySeparatorChar)));
                if (_resources.Contains(candidate)) matches.Add(candidate);
                candidate = Path.GetFullPath(Path.Combine(AssetsRoot, literal.Replace('/', Path.DirectorySeparatorChar)));
                if (_resources.Contains(candidate)) matches.Add(candidate);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { }

            foreach (string match in matches) yield return match;
        }

        private void AddAliases(string fullPath)
        {
            AddAlias(fullPath, fullPath);
            AddAlias(Path.GetRelativePath(ProjectRoot, fullPath), fullPath);
            AddAlias(Path.GetRelativePath(AssetsRoot, fullPath), fullPath);
            AddAlias(Path.GetFileName(fullPath), fullPath);
            AddAlias(AssetStem(Path.GetFileName(fullPath)), fullPath);
        }

        private void AddAlias(string alias, string fullPath)
        {
            string key = NormalizeKey(alias);
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!_aliases.TryGetValue(key, out HashSet<string> paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _aliases[key] = paths;
            }
            paths.Add(fullPath);
        }

        private static void AddSidecarDependency(string resource, HashSet<string> direct)
        {
            AddFile(resource + ".meta", direct);
            string fileName = Path.GetFileName(resource);
            string parent = Path.GetDirectoryName(resource) ?? string.Empty;
            if (fileName.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            {
                string stem = fileName[..^".image.json".Length];
                AddSidecarFiles(Path.Combine(parent, stem + ".spritedata"), direct);
            }
            else if (fileName.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase))
            {
                string stem = fileName[..^".model.json".Length];
                AddSidecarFiles(Path.Combine(parent, stem + ".modeldata"), direct);
                AddFile(Path.Combine(parent, stem + ".gmodel"), direct);
            }
            else if (fileName.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase))
            {
                string stem = fileName[..^".object.json".Length];
                AddSidecarFiles(Path.Combine(parent, stem), direct);
            }
            else if (fileName.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
            {
                AddFile(resource + ".gterrain", direct);
                AddFile(resource + ".nature.json", direct);
            }

            static void AddSidecarFiles(string directory, HashSet<string> dependencies)
            {
                if (!Directory.Exists(directory)) return;
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    dependencies.Add(Path.GetFullPath(path));
            }

            static void AddFile(string path, HashSet<string> dependencies)
            {
                if (File.Exists(path)) dependencies.Add(Path.GetFullPath(path));
            }
        }

        private static IEnumerable<string> ReadReferenceLiterals(string path)
        {
            string extension = Path.GetExtension(path);
            if (!TextExtensions.Contains(extension)) yield break;
            FileInfo info = new(path);
            if (!info.Exists || info.Length > 4 * 1024 * 1024) yield break;

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                JToken root;
                try { root = JToken.Parse(text); }
                catch { yield break; }
                foreach (JValue value in EnumerateValues(root))
                    if (value.Type == JTokenType.String && value.Value is string literal)
                        yield return literal;
                yield break;
            }

            foreach (string token in ExtractQuotedStrings(text)) yield return token;
        }

        private static IEnumerable<string> ExtractQuotedStrings(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char quote = text[i];
                if (quote != '\'' && quote != '"') continue;
                int start = ++i;
                bool escaped = false;
                while (i < text.Length)
                {
                    char current = text[i];
                    if (!escaped && current == quote) break;
                    escaped = !escaped && current == '\\';
                    if (current != '\\') escaped = false;
                    i++;
                }
                if (i > start) yield return text.Substring(start, i - start);
            }
        }

        private static IEnumerable<JValue> EnumerateValues(JToken token)
        {
            if (token is JProperty property && property.Name.Equals("bindPixels", StringComparison.OrdinalIgnoreCase))
                yield break;
            if (token is JValue value)
            {
                yield return value;
                yield break;
            }
            foreach (JToken child in token.Children())
                foreach (JValue descendant in EnumerateValues(child))
                    yield return descendant;
        }

        private static string NormalizeKey(string value) =>
            (value ?? string.Empty).Trim().Trim('"', '\'').Replace('\\', '/').TrimStart('.', '/');

        private static string AssetStem(string fileName)
        {
            string[] suffixes =
            {
                ".image.json", ".object.json", ".room.json", ".model.json", ".particle.json",
                ".shader.json", ".material.json", ".physics.json", ".audio.json", ".terrain.json", ".pathing", ".ui.json",
            };
            foreach (string suffix in suffixes)
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return fileName[..^suffix.Length];
            return Path.GetFileNameWithoutExtension(fileName);
        }
    }

    public sealed class ProjectAssetChangeSet : EventArgs
    {
        public ProjectAssetChangeSet(
            string projectRoot,
            IEnumerable<string> changedPaths,
            IEnumerable<string> affectedPaths,
            long generation,
            IEnumerable<string> locallyWrittenPaths = null)
        {
            ProjectRoot = Path.GetFullPath(projectRoot);
            ChangedPaths = Canonical(changedPaths);
            AffectedPaths = Canonical(affectedPaths);
            LocallyWrittenPaths = Canonical(locallyWrittenPaths);
            Generation = generation;
        }

        public string ProjectRoot { get; }
        public IReadOnlyCollection<string> ChangedPaths { get; }
        public IReadOnlyCollection<string> AffectedPaths { get; }
        public long Generation { get; }
        public IReadOnlyCollection<string> LocallyWrittenPaths { get; }

        public bool Changed(string path) => Contains(ChangedPaths, path);
        public bool Affects(string path) => Contains(AffectedPaths, path);
        public bool IsLocalWrite(string path) => Contains(LocallyWrittenPaths, path);

        private static IReadOnlyCollection<string> Canonical(IEnumerable<string> paths) =>
            (paths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static bool Contains(IEnumerable<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full = Path.GetFullPath(path);
            return paths.Contains(full, StringComparer.OrdinalIgnoreCase);
        }
    }
}

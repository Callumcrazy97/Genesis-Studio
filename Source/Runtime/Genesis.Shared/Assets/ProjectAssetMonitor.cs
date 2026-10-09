using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Genesis.Shared.Assets
{
    /// <summary>
    /// Debounced project Assets watcher backed by <see cref="AssetDependencyGraph"/>. One event
    /// reports the physical writes and the complete transitive set that must be rebound.
    /// </summary>
    public sealed class ProjectAssetMonitor : IDisposable
    {
        private readonly object _gate = new();
        private readonly object _publicationGate = new();
        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounce;
        private readonly SynchronizationContext _context;
        private bool _disposed;
        private long _generation;

        public ProjectAssetMonitor(
            string projectRoot,
            int debounceMilliseconds = 180,
            SynchronizationContext context = null)
            : this(projectRoot, debounceMilliseconds, context, readReferences: true)
        {
        }

        /// <param name="readReferences">
        /// False watches the files without reading what refers to what: each change then lists
        /// only the files that changed as affected, and <see cref="Graph"/> is read the first time
        /// it is asked for. For a watcher that reloads everything on any change (a game run from
        /// Studio), to which the graph was only a cost: nine seconds before the first frame in a
        /// project of ten thousand resources.
        /// </param>
        public ProjectAssetMonitor(
            string projectRoot,
            int debounceMilliseconds,
            SynchronizationContext context,
            bool readReferences)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("A project root is required.", nameof(projectRoot));
            ProjectRoot = Path.GetFullPath(projectRoot);
            AssetsRoot = Path.Combine(ProjectRoot, "Assets");
            string root = ProjectRoot;
            _readsReferences = readReferences;
            _graph = new Lazy<AssetDependencyGraph>(() => new AssetDependencyGraph(root), LazyThreadSafetyMode.ExecutionAndPublication);
            if (readReferences) _ = _graph.Value;
            _context = context;
            _debounce = new Timer(_ => PublishPending(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(AssetsRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = Directory.Exists(AssetsRoot),
            };
            DebounceMilliseconds = Math.Clamp(debounceMilliseconds, 30, 5000);
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnWatcherError;
        }

        private readonly Lazy<AssetDependencyGraph> _graph;
        private readonly bool _readsReferences;

        /// <summary>The project's reference graph (read now if this monitor was made without it).</summary>
        public AssetDependencyGraph Graph => _graph.Value;

        /// <summary>Whether changes are followed through the reference graph to what uses them.</summary>
        public bool ReadsReferences => _readsReferences;

        public string ProjectRoot { get; }
        public string AssetsRoot { get; }
        public int DebounceMilliseconds { get; }
        public long Generation => Interlocked.Read(ref _generation);
        public event EventHandler<ProjectAssetChangeSet> Changed;
        public event EventHandler<Exception> Error;

        /// <summary>Queues an explicit Studio save through the same coalescing path as external edits.</summary>
        public void Notify(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || _disposed) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.IOException) { return; }
            if (!IsUnderAssets(full)) return;
            lock (_gate)
            {
                if (_disposed) return;
                _pending.Add(full);
                _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
            }
        }

        /// <summary>Synchronous publication hook for deterministic acceptance tests.</summary>
        public ProjectAssetChangeSet FlushPending()
        {
            ProjectAssetChangeSet changes = BuildChangeSet();
            if (changes != null) Dispatch(changes);
            return changes;
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => Notify(e.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Notify(e.OldFullPath);
            Notify(e.FullPath);
        }

        private void PublishPending()
        {
            try
            {
                ProjectAssetChangeSet changes = BuildChangeSet();
                if (changes != null) Dispatch(changes);
            }
            catch (Exception error) { ReportError(error); }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e) => ReportError(e.GetException());

        private void ReportError(Exception error)
        {
            if (_disposed) return;
            void Report()
            {
                if (_disposed) return;
                try { Error?.Invoke(this, error); }
                catch (Exception reportingError) { System.Diagnostics.Trace.TraceError("Asset monitor diagnostics: " + reportingError); }
            }
            try { if (_context == null) Report(); else _context.Post(_ => Report(), null); }
            catch (Exception reportingError) { System.Diagnostics.Trace.TraceError("Asset monitor diagnostics: " + reportingError); }
        }

        private ProjectAssetChangeSet BuildChangeSet()
        {
            lock (_publicationGate) return BuildChangeSetCore();
        }

        private ProjectAssetChangeSet BuildChangeSetCore()
        {
            string[] changed;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0) return null;
                changed = new string[_pending.Count];
                _pending.CopyTo(changed);
                _pending.Clear();
                _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            }

            HashSet<string> affected = new(changed, StringComparer.OrdinalIgnoreCase);
            if (_readsReferences || _graph.IsValueCreated)
            {
                try
                {
                    AssetDependencyGraph graph = Graph;
                    affected.UnionWith(graph.GetAffectedPaths(changed));
                    graph.RefreshChanged(changed);
                    affected.UnionWith(graph.GetAffectedPaths(changed));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                    or Newtonsoft.Json.JsonException or ArgumentException)
                {
                    // A resource whose identity cannot be read yet (a .meta still being written, two
                    // resources briefly sharing a name in a rename): the change is still a change. It
                    // used to be dropped here, so the files saved with it were never reloaded.
                    ReportError(error);
                }
            }
            long generation = Interlocked.Increment(ref _generation);
            // Any observed change makes frame-path caches re-validate on next use, so external edits
            // appear immediately rather than after their bounded fallback poll.
            RuntimeAssetPolicy.Invalidate();
            List<string> localWrites = new();
            foreach (string path in changed)
                if (ProjectAssetWriteRegistry.IsRecentLocalWrite(path)) localWrites.Add(path);
            return new ProjectAssetChangeSet(Graph.ProjectRoot, changed, affected, generation, localWrites);
        }

        private void Dispatch(ProjectAssetChangeSet changes)
        {
            void Publish()
            {
                if (_disposed) return;
                try { Changed?.Invoke(this, changes); }
                catch (Exception error) { ReportError(error); }
            }
            if (_context == null) Publish(); else _context.Post(_ => Publish(), null);
        }

        private bool IsUnderAssets(string path)
        {
            string root = Graph.AssetsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pending.Clear();
            }
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _debounce.Dispose();
        }
    }

    /// <summary>Process-local origin tags let Studio keep the editor that performed a save intact.</summary>
    public static class ProjectAssetWriteRegistry
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, DateTime> Writes = new(StringComparer.OrdinalIgnoreCase);

        public static void MarkLocalWrite(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (Gate)
            {
                Writes[Path.GetFullPath(path)] = DateTime.UtcNow;
                PurgeExpired();
            }
        }

        public static bool IsRecentLocalWrite(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (Gate)
            {
                PurgeExpired();
                return Writes.TryGetValue(Path.GetFullPath(path), out DateTime written)
                    && DateTime.UtcNow - written < TimeSpan.FromSeconds(4);
            }
        }

        private static void PurgeExpired()
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(4);
            List<string> expired = new();
            foreach (KeyValuePair<string, DateTime> pair in Writes)
                if (pair.Value < cutoff) expired.Add(pair.Key);
            foreach (string path in expired) Writes.Remove(path);
        }
    }
}

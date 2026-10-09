using System;
using System.IO;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Keeps an F5 player process alive while Studio or an external tool changes Assets. Detection
    /// happens off-thread; cache invalidation and the room rebind are requested on the game thread.
    /// </summary>
    public sealed class ProjectAssetLiveReloadSubsystem : ISceneSubsystem
    {
        private readonly string _projectPath;
        private readonly IRenderController _renderer;
        private readonly ProjectRoomSwitcher _roomSwitcher;
        private readonly ProjectLogger _logger;
        private readonly ProjectAssetMonitor _monitor;
        private readonly object _gate = new();
        private ProjectAssetChangeSet _pending;

        public ProjectAssetLiveReloadSubsystem(
            string projectPath,
            IRenderController renderer,
            ProjectRoomSwitcher roomSwitcher,
            ProjectLogger logger,
            int debounceMilliseconds = 180)
        {
            _projectPath = projectPath;
            _renderer = renderer;
            _roomSwitcher = roomSwitcher ?? throw new ArgumentNullException(nameof(roomSwitcher));
            _logger = logger;
            // Any change reloads the room and every cache, so what refers to what is not needed
            // here: reading it took nine seconds before the first frame in a project of ten
            // thousand resources (and seventeen more on a worker, competing with the game).
            _monitor = new ProjectAssetMonitor(projectPath, debounceMilliseconds, context: null, readReferences: false);
            _monitor.Changed += OnChanged;
            _monitor.Error += OnMonitorError;
            _logger?.Line($"AssetLiveReload watching {_monitor.AssetsRoot}");
        }

        private void OnMonitorError(object sender, Exception error) =>
            _logger?.Line($"AssetLiveReload could not read a change completely: {error.Message}");

        public long AppliedGeneration { get; private set; }
        public AssetDependencyGraph Graph => _monitor.Graph;

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void Update(RuntimeScene scene, GameTime time)
        {
            ProjectAssetChangeSet changes;
            lock (_gate)
            {
                changes = _pending;
                _pending = null;
            }
            if (changes == null) return;

            if (!ChangedJsonIsStable(changes, out string validationError)
                || !ChangedScriptsCompile(changes, out validationError))
            {
                // The game goes on as it was; the save that completes the file is a change of its own.
                _logger?.Line(
                    $"AssetLiveReload rejected generation={changes.Generation}: {validationError}");
                return;
            }

            System.Collections.Generic.IReadOnlyList<string> kept =
                RuntimeAssetInvalidation.InvalidateRunningGame(_projectPath, _renderer, out string scriptFailure);
            _roomSwitcher.RequestLiveReload(changes);
            AppliedGeneration = changes.Generation;
            _logger?.Line(
                $"AssetLiveReload detected generation={changes.Generation} changed={changes.ChangedPaths.Count} affected={changes.AffectedPaths.Count}");
            if (scriptFailure != null)
                _logger?.Line($"AssetLiveReload kept the Scripts it had: they could not be read again ({scriptFailure})");
            else if (kept.Count > 0)
                _logger?.Line($"AssetLiveReload kept the earlier version of {kept.Count} Script(s) that do not compile: "
                    + string.Join(", ", System.Linq.Enumerable.Take(kept, 8)) + (kept.Count > 8 ? ", ..." : string.Empty));
        }

        public void SubmitMeshes(
            RuntimeScene scene,
            MeshDrawCall[] buffer,
            ref int count,
            IRenderController renderer) { }

        private void OnChanged(object sender, ProjectAssetChangeSet changes)
        {
            // Merge rather than replace: two saves landing before the game thread applies the first
            // (or while play is paused) used to drop the earlier change set entirely.
            lock (_gate)
            {
                _pending = _pending == null
                    ? changes
                    : new ProjectAssetChangeSet(
                        changes.ProjectRoot,
                        System.Linq.Enumerable.Concat(_pending.ChangedPaths, changes.ChangedPaths),
                        System.Linq.Enumerable.Concat(_pending.AffectedPaths, changes.AffectedPaths),
                        Math.Max(_pending.Generation, changes.Generation),
                        System.Linq.Enumerable.Concat(_pending.LocallyWrittenPaths, changes.LocallyWrittenPaths));
            }
        }

        private static bool ChangedJsonIsStable(
            ProjectAssetChangeSet changes,
            out string validationError)
        {
            foreach (string path in changes.ChangedPaths)
            {
                // A resource's .meta is JSON too, and names it: half written, the project's resource
                // names cannot be read, and nothing that looks a resource up by name would work.
                if (!(path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    || !File.Exists(path))
                {
                    continue;
                }

                try
                {
                    JToken.Parse(File.ReadAllText(path));
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or Newtonsoft.Json.JsonException)
                {
                    validationError = $"'{Path.GetFileName(path)}' is not a stable JSON save "
                        + $"({exception.Message})";
                    return false;
                }
            }

            validationError = null;
            return true;
        }

        /// <summary>
        /// Every changed PGSL file (a library Script or an Object's event) can be read and
        /// compiles. A file a tool is still writing is usually cut off part way, and a room rebuilt
        /// from it would leave its Object without that event until the next save; the game keeps
        /// the code it is running instead. Checked before anything is invalidated, so the Script
        /// names a call may use are those of the running game.
        /// </summary>
        private static bool ChangedScriptsCompile(ProjectAssetChangeSet changes, out string validationError)
        {
            foreach (string path in changes.ChangedPaths)
            {
                if (!path.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    continue;

                string source;
                try
                {
                    source = File.ReadAllText(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    validationError = $"'{Path.GetFileName(path)}' could not be read, as while it is still being written ({exception.Message})";
                    return false;
                }

                // An empty event is an event removed, and an empty Script is reported when it is loaded.
                if (string.IsNullOrWhiteSpace(source)) continue;
                try
                {
                    Genesis.Runtime.Scripting.ScriptAssetCompiler.Compile(Path.GetFileNameWithoutExtension(path), source);
                }
                catch (Exception exception)
                {
                    int line = exception.Data["Line"] is int number ? number : 0;
                    validationError = $"'{Path.GetFileName(path)}' does not compile ({(line > 0 ? $"line {line}: " : string.Empty)}{exception.Message}); "
                        + "the game keeps the code it is running";
                    return false;
                }
            }

            validationError = null;
            return true;
        }

        public void Dispose()
        {
            _monitor.Changed -= OnChanged;
            _monitor.Error -= OnMonitorError;
            _monitor.Dispose();
        }
    }
}

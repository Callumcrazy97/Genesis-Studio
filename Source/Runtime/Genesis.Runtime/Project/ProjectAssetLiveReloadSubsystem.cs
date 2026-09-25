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
            _monitor = new ProjectAssetMonitor(projectPath, debounceMilliseconds);
            _monitor.Changed += OnChanged;
            _logger?.Line($"AssetLiveReload watching resources={_monitor.Graph.ResourceCount} references={_monitor.Graph.ReferenceCount}");
        }

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

            if (!ChangedJsonIsStable(changes, out string validationError))
            {
                _logger?.Line(
                    $"AssetLiveReload rejected generation={changes.Generation}: {validationError}");
                return;
            }

            RuntimeAssetInvalidation.Invalidate(_projectPath, _renderer);
            _roomSwitcher.RequestLiveReload(changes);
            AppliedGeneration = changes.Generation;
            _logger?.Line(
                $"AssetLiveReload detected generation={changes.Generation} changed={changes.ChangedPaths.Count} affected={changes.AffectedPaths.Count}");
        }

        public void SubmitMeshes(
            RuntimeScene scene,
            MeshDrawCall[] buffer,
            ref int count,
            IRenderController renderer) { }

        private void OnChanged(object sender, ProjectAssetChangeSet changes)
        {
            lock (_gate) _pending = changes;
        }

        private static bool ChangedJsonIsStable(
            ProjectAssetChangeSet changes,
            out string validationError)
        {
            foreach (string path in changes.ChangedPaths)
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
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

        public void Dispose()
        {
            _monitor.Changed -= OnChanged;
            _monitor.Dispose();
        }
    }
}

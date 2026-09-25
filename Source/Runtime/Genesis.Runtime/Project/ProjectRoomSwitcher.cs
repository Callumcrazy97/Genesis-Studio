using System;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.Platform;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Assets;
using Genesis.World.Terrain;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Honours <see cref="ProjectGameContext.ChangeRoom"/> by unloading the live scene and
    /// loading a different room file without restarting the player process.
    /// </summary>
    public sealed class ProjectRoomSwitcher : ISceneSubsystem
    {
        private readonly string _projectPath;
        private readonly ProjectGameContext _context;
        private readonly ScriptHostSystem _scriptHost;
        private readonly SilkGameWindow _window;
        private readonly IRenderController _renderer;
        private readonly ProjectLogger _logger;
        private string _currentRoomName;
        private long _reloadGeneration;
        private int _reloadChangedCount;
        private int _terrainSurfaceReload;

        public ProjectRoomSwitcher(
            string projectPath,
            ProjectGameContext context,
            ScriptHostSystem scriptHost,
            SilkGameWindow window,
            IRenderController renderer,
            ProjectLogger logger,
            string currentRoomName = null)
        {
            _projectPath = projectPath;
            _context = context;
            _scriptHost = scriptHost;
            _window = window;
            _renderer = renderer;
            _logger = logger;
            _currentRoomName = currentRoomName ?? context?.Room?.Name;
        }

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void Update(RuntimeScene scene, GameTime time)
        {
            if (_context.TryConsumePendingRoom(out string roomName))
            {
                SwitchTo(scene, roomName, liveReload: false, generation: 0, changedCount: 0);
                return;
            }

            long generation = System.Threading.Interlocked.Exchange(ref _reloadGeneration, 0);
            if (generation == 0) return;
            int changedCount = System.Threading.Interlocked.Exchange(ref _reloadChangedCount, 0);
            int terrainOnly = System.Threading.Interlocked.Exchange(ref _terrainSurfaceReload, 0);
            if (terrainOnly != 0 && TryReloadTerrainSurfaces(scene, generation, changedCount))
                return;
            SwitchTo(scene, _currentRoomName, liveReload: true, generation, changedCount);
        }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

        public void Dispose() { }

        public void RequestLiveReload(ProjectAssetChangeSet changes)
        {
            if (changes == null) return;
            System.Threading.Interlocked.Exchange(
                ref _terrainSurfaceReload,
                TerrainColliderMesh.IsExclusiveTerrainSurfaceChange(changes) ? 1 : 0);
            System.Threading.Interlocked.Exchange(ref _reloadChangedCount, changes.ChangedPaths.Count);
            System.Threading.Interlocked.Exchange(ref _reloadGeneration, changes.Generation);
        }

        private bool TryReloadTerrainSurfaces(RuntimeScene scene, long generation, int changedCount)
        {
            RoomTerrainSubsystem terrain = null;
            for (int i = 0; i < scene.Subsystems.Count; i++)
            {
                if (scene.Subsystems[i] is RoomTerrainSubsystem candidate)
                {
                    terrain = candidate;
                    break;
                }
            }

            if (terrain == null || !terrain.ReloadAuthoredSurfaces(scene))
                return false;

            _logger?.Line(
                $"AssetLiveReload applied generation={generation} changed={changedCount} "
                + $"terrainColliderRebuild room={_currentRoomName}");
            return true;
        }

        private void SwitchTo(
            RuntimeScene scene,
            string roomName,
            bool liveReload,
            long generation,
            int changedCount)
        {
            string roomFile = ProjectRoomResolver.ResolveRoomFile(_projectPath, roomName);
            if (roomFile == null)
            {
                _logger?.Line($"ChangeRoom: room not found '{roomName}'");
                return;
            }

            // Parse before unloading the working scene. A half-written external save therefore
            // leaves the current game running and will be retried on the following file event.
            RoomAsset room;
            try
            {
                room = RoomAssetLoader.Parse(roomFile);
            }
            catch (Exception exception)
            {
                _logger?.Line($"AssetLiveReload rejected generation={generation}: {exception.Message}");
                return;
            }

            _scriptHost.EndRoom(endGame: false);
            scene.UnloadRoomContent(_scriptHost, KeepSubsystem, preservePersistent: !liveReload);

            _context.SetRoom(room);
            RoomBuildResult build = new RoomSceneBuilder(_projectPath, _scriptHost).Build(scene, room);
            _scriptHost.BeginRoom(beginGame: false);

            if (RoomTerrainSubsystem.ShouldRegister(room))
                scene.AddSubsystem(new RoomTerrainSubsystem(_projectPath, room, _context));
            if (RoomEnvironmentAudioSubsystem.ShouldRegister(room.Environment))
                scene.AddSubsystem(new RoomEnvironmentAudioSubsystem(room.Environment, _context));
            scene.AddSubsystem(new ObjectCompositionSubsystem(_projectPath, _context.Audio));
            scene.AddSubsystem(new RoomRenderSubsystem(_projectPath, room));
            scene.AddSubsystem(new ObjectDrawSubsystem(_projectPath));

            if (room.Dimension == RoomDimension.ThreeD && room.Settings.VoxelWorld)
                SceneDefaults.ApplyVoxelWorld(scene);

            ProjectRoomPresentation.Apply(_window, _renderer, room);
            _currentRoomName = roomName;
            _logger?.Line(liveReload
                ? $"AssetLiveReload applied generation={generation} changed={changedCount} room={roomName} entities={build.SpawnedEntities.Count}"
                : $"ChangeRoom -> {roomName} entities={build.SpawnedEntities.Count}");
        }

        private static bool KeepSubsystem(ISceneSubsystem sub) =>
            sub is ProjectRoomSwitcher or ProjectAssetLiveReloadSubsystem or ScriptHostSubsystem
                or AutoshotSubsystem;
    }

    internal static class ProjectRoomPresentation
    {
        /// <summary>
        /// The authored frame rate last pushed to the window, so a room is honoured without a
        /// runtime override being stamped on at every room change.
        /// </summary>
        private static int _appliedAuthoredFps = -1;
        private static bool? _appliedAuthoredVsync;

        /// <summary>Forgets authored display defaults, so a new play session starts clean.</summary>
        public static void ResetAppliedFrameRate()
        {
            _appliedAuthoredFps = -1;
            _appliedAuthoredVsync = null;
        }

        public static void Apply(SilkGameWindow window, IRenderController renderer, RoomAsset room)
        {
            if (window == null || room == null) return;

            // A room's frame rate is part of how it plays — a menu at 30 and gameplay at 60 is an
            // ordinary thing to author — so it has to reach the window, or the setting is a lie.
            //
            // It is applied only when the *authored* value changes, which is what preserves the
            // original reason this was disconnected: scripts own VSync/TargetFps at run time
            // (GameSession.Settings via IGameContext), and re-pushing the same room value on every
            // ChangeRoom used to stamp on a player's own choice. Tracking the authored value means a
            // script override survives inside a room and between rooms that agree, while a room
            // that genuinely asks for a different speed still gets it.
            int authored = room.Settings?.TargetFps ?? 0;
            if (authored != _appliedAuthoredFps)
            {
                _appliedAuthoredFps = authored;
                window.TargetFps = authored;
            }

            // VSync is also authored room presentation. Previously the JSON value was displayed by
            // the debugger but never reached DXGI: SilkGameWindow starts false and the renderer was
            // initialised from that default before the room loaded. Track the authored value just
            // like FPS so a script's live override survives room changes that request the same
            // setting, while a genuinely different room setting is applied to both owners.
            bool authoredVsync = room.Settings?.VSync ?? true;
            if (_appliedAuthoredVsync != authoredVsync)
            {
                _appliedAuthoredVsync = authoredVsync;
                window.VSync = authoredVsync;
                renderer?.SetVSync(authoredVsync);
            }

            bool capture = room.Settings.CaptureMouse ?? (room.Dimension == RoomDimension.ThreeD);
            if (capture)
                window.SetMouseCaptured(true);
            else
                window.SetCursorMode(CursorMode.Normal);
        }
    }
}

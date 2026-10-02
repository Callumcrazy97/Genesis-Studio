using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <remarks>
    /// With a frame budget, a change that is more than a moment's work is spread over frames. The
    /// scene is held meanwhile (see <see cref="RuntimeScene.RoomChange"/>): its host goes on
    /// drawing frames, with a cover and a loading screen where the room would be, the window
    /// stays responsive and music keeps playing. The terrain is read on a worker thread, objects
    /// are placed a few milliseconds at a time, and the finished room is then drawn behind the
    /// cover until its ground is solid and what its first view shows has been loaded. Without a
    /// budget the whole change happens in one step.
    /// </remarks>
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
        private IEnumerator<(float Progress, bool Wait)> _change;
        private RoomChangeProgress _progress;
        private bool _spread;
        private int _framesSpread;
        private double _longestPieceMilliseconds;

        /// <summary>
        /// How long, in milliseconds, a room change may work in one frame before it lets the frame
        /// be drawn and carries on in the next. 0, which is what a host without a frame loop gets,
        /// does the whole change in one step. A script's own choice
        /// (<see cref="RoomChangeScreen.FrameBudgetMilliseconds"/>) outranks this.
        /// </summary>
        public double FrameBudgetMilliseconds { get; set; }

        /// <summary>The least number of frames a finished room is drawn behind the cover before it is shown.</summary>
        public int WarmUpFrames { get; set; } = 3;

        /// <summary>The longest a finished room waits behind the cover before it is shown regardless, in seconds.</summary>
        public double WarmUpTimeoutSeconds { get; set; } = 12;

        /// <summary>True while a room change is under way across frames.</summary>
        public bool Changing => _change != null;

        private double Budget => Math.Max(0, RoomChangeScreen.FrameBudgetMilliseconds ?? FrameBudgetMilliseconds);

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
            if (_change != null)
            {
                // A held scene advances the change itself. This is for a host that steps
                // subsystems by hand and does not hold: the change still moves on each frame.
                Advance(scene);
                return;
            }

            if (_context.TryConsumePendingRoom(out string roomName))
            {
                Begin(scene, roomName, liveReload: false, generation: 0, changedCount: 0);
                return;
            }

            long generation = System.Threading.Interlocked.Exchange(ref _reloadGeneration, 0);
            if (generation == 0) return;
            int changedCount = System.Threading.Interlocked.Exchange(ref _reloadChangedCount, 0);
            int terrainOnly = System.Threading.Interlocked.Exchange(ref _terrainSurfaceReload, 0);
            if (terrainOnly != 0 && TryReloadTerrainSurfaces(scene, generation, changedCount))
                return;
            Begin(scene, _currentRoomName, liveReload: true, generation, changedCount);
        }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

        public void Dispose()
        {
            _change?.Dispose();
            _change = null;
        }

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

        /// <summary>
        /// Prepares the room a game starts in the way a room that is changed to is prepared:
        /// drawn behind the cover, without being updated, until its ground is solid and its first
        /// view is loaded. Without it a game's first frames, after its start-up screen, are the
        /// long ones. Does nothing without a frame budget, or for a 2D room.
        /// </summary>
        /// <returns>True when the scene is now held behind the cover.</returns>
        public bool PrepareFirstRoom(RuntimeScene scene, RoomAsset room)
        {
            if (scene == null || room == null || _change != null || Budget <= 0 || room.Dimension != RoomDimension.ThreeD) return false;
            _spread = true;
            _framesSpread = 0;
            _longestPieceMilliseconds = 0;
            _progress = new RoomChangeProgress { RoomName = room.Name ?? _currentRoomName, Advance = Advance, RoomBuilt = true, Progress = 0.7f };
            _change = WarmUpSteps(scene, _progress.RoomName, Stopwatch.GetTimestamp(), firstRoom: true).GetEnumerator();
            scene.RoomChange = _progress;
            return true;
        }

        /// <summary>Starts a room change and does as much of it as this frame allows.</summary>
        private void Begin(RuntimeScene scene, string roomName, bool liveReload, long generation, int changedCount)
        {
            // An asset saved in Studio rebuilds the room in one step: a loading screen on every save would be a nuisance.
            _spread = Budget > 0 && !liveReload;
            _framesSpread = 0;
            _longestPieceMilliseconds = 0;
            _progress = new RoomChangeProgress { RoomName = roomName, Advance = Advance };
            _change = ChangeSteps(scene, roomName, liveReload, generation, changedCount, _spread).GetEnumerator();
            Advance(scene);
        }

        /// <summary>
        /// Carries the change on until it is finished, or this frame's share of it is spent, or
        /// the next piece is waiting for a worker thread or for a frame to be drawn.
        /// </summary>
        private void Advance(RuntimeScene scene)
        {
            if (_change == null) return;
            long started = Stopwatch.GetTimestamp();
            _framesSpread++;
            try
            {
                while (true)
                {
                    long piece = Stopwatch.GetTimestamp();
                    if (!_change.MoveNext())
                    {
                        Finish(scene);
                        return;
                    }

                    _longestPieceMilliseconds = Math.Max(_longestPieceMilliseconds, Stopwatch.GetElapsedTime(piece).TotalMilliseconds);
                    (float progress, bool wait) = _change.Current;
                    _progress.Progress = Math.Clamp(Math.Max(_progress.Progress, progress), 0f, 1f);
                    if (!_spread) continue;
                    // A 2D room that turns out to be quick is finished in the frame it was asked for.
                    double allowed = scene.RoomChange == null && _twoDimensional
                        ? Math.Max(Budget, RoomChangeScreen.GraceMilliseconds)
                        : Budget;
                    if (wait || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= allowed) break;
                }
            }
            catch
            {
                // A room that could not be built must not leave the game held behind a cover.
                Finish(scene);
                throw;
            }

            // The frame is given up with the change unfinished: from here the scene is held.
            scene.RoomChange ??= _progress;
        }

        private void Finish(RuntimeScene scene)
        {
            _change?.Dispose();
            _change = null;
            if (!ReferenceEquals(scene.RoomChange, _progress)) return;
            scene.RoomChange = null;
            _progress.Progress = 1f;
            _progress.RevealSeconds = _progress.RevealRemaining = Math.Max(0f, RoomChangeScreen.FadeSeconds);
            scene.RoomReveal = _progress.RevealSeconds > 0f ? _progress : null;
        }

        private bool _twoDimensional;

        private RoomAsset ParseForChange(string roomFile, long generation)
        {
            // Parse before unloading the working scene. A half-written external save therefore
            // leaves the current game running and will be retried on the following file event.
            try
            {
                return RoomAssetLoader.Parse(roomFile);
            }
            catch (Exception exception)
            {
                _logger?.Line($"AssetLiveReload rejected generation={generation}: {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// The room change as a sequence of pieces. Each value is how far along the change is and
        /// whether the next piece should wait for a frame to be drawn first.
        /// </summary>
        private IEnumerable<(float Progress, bool Wait)> ChangeSteps(
            RuntimeScene scene,
            string roomName,
            bool liveReload,
            long generation,
            int changedCount,
            bool spread)
        {
            string roomFile = ProjectRoomResolver.ResolveRoomFile(_projectPath, roomName);
            if (roomFile == null)
            {
                _logger?.Line($"ChangeRoom: room not found '{roomName}'");
                yield break;
            }

            RoomAsset room = ParseForChange(roomFile, generation);
            if (room == null) yield break;
            _twoDimensional = room.Dimension == RoomDimension.TwoD;

            long changeStarted = Stopwatch.GetTimestamp();
            int collectionsBefore = GC.CollectionCount(2);
            TimeSpan pausedBefore = GC.GetTotalPauseDuration();
            _scriptHost.EndRoom(endGame: false);
            scene.UnloadRoomContent(_scriptHost, KeepSubsystem, preservePersistent: !liveReload);
            double unloadMilliseconds = Stopwatch.GetElapsedTime(changeStarted).TotalMilliseconds;
            yield return (0.03f, false);

            var build = new RoomBuildResult();
            foreach ((float done, bool wait) in ProjectRoomLoader.BuildSteps(_projectPath, scene, room,
                _scriptHost, _context, beginGame: false, build, spread))
                yield return (0.03f + 0.67f * done, wait);

            long finishStarted = Stopwatch.GetTimestamp();
            if (RoomEnvironmentAudioSubsystem.ShouldRegister(room.Environment))
                scene.AddSubsystem(new RoomEnvironmentAudioSubsystem(room.Environment, _context));
            scene.AddSubsystem(new ObjectCompositionSubsystem(_projectPath, _context.Audio, room.Dimension == RoomDimension.TwoD));
            scene.AddSubsystem(new RoomRenderSubsystem(_projectPath, room));
            scene.AddSubsystem(new ObjectDrawSubsystem(_projectPath));

            if (room.Dimension == RoomDimension.ThreeD && room.Settings.VoxelWorld)
                SceneDefaults.ApplyVoxelWorld(scene);

            ProjectRoomPresentation.Apply(_window, _renderer, room);
            _currentRoomName = roomName;
            _logger?.Line(liveReload
                ? $"AssetLiveReload applied generation={generation} changed={changedCount} room={roomName} entities={build.SpawnedEntities.Count}"
                : $"ChangeRoom -> {roomName} entities={build.SpawnedEntities.Count}");
            // Where a room change's time goes, so a slow one can be attributed without a profiler.
            string slowest = build.SlowestSpawns.Count == 0 ? "none"
                : string.Join(", ", System.Linq.Enumerable.Select(build.SlowestSpawns, spawn => $"{spawn.Name} {spawn.Milliseconds:F0} ms"));
            // The build's time is the work itself; when it was spread, the frames drawn in between are not part of it.
            double buildMilliseconds = build.TerrainMilliseconds + build.SpawnMilliseconds
                + build.CreateEventsMilliseconds + build.RoomStartMilliseconds;
            _logger?.Line($"Room change timing: unload {unloadMilliseconds:F0} ms, build {buildMilliseconds:F0} ms "
                + $"(terrain {build.TerrainMilliseconds:F0}{(spread ? " on a worker" : string.Empty)}, placing objects {build.SpawnMilliseconds:F0}, "
                + $"Create events {build.CreateEventsMilliseconds:F0}, room-start events {build.RoomStartMilliseconds:F0}), finish "
                + $"{Stopwatch.GetElapsedTime(finishStarted).TotalMilliseconds:F0} ms; slowest objects: {slowest}; "
                + $"garbage collector paused {(GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds:F0} ms "
                + $"in {GC.CollectionCount(2) - collectionsBefore} full collections");

            // A change that was quick enough to finish in the frame it was asked for is done: no
            // cover was raised and none is needed. A 3D room is always prepared behind the cover,
            // because what holds a 3D room up is what its first frames load, not how it is built.
            if (!spread || (scene.RoomChange == null && room.Dimension != RoomDimension.ThreeD)) yield break;

            _progress.RoomBuilt = true;
            foreach ((float Progress, bool Wait) step in WarmUpSteps(scene, roomName, changeStarted, firstRoom: false))
                yield return step;
        }

        /// <summary>
        /// The room is whole. It is drawn behind the cover, without being updated, until its
        /// ground is solid and what its first view shows has been read and sent to the graphics
        /// card: frames that would otherwise be the room's first, and its longest.
        /// </summary>
        private IEnumerable<(float Progress, bool Wait)> WarmUpSteps(RuntimeScene scene, string roomName, long changeStarted, bool firstRoom)
        {
            long warmUpStarted = Stopwatch.GetTimestamp(), lastFrame = warmUpStarted;
            Genesis.Shared.Assets.LoadClockSnapshot loaded = Genesis.Shared.Assets.LoadClock.Capture();
            double quickest = double.MaxValue, slowest1 = 0;
            int frames = 0, settled = 0;
            while (true)
            {
                bool ready = Genesis.Runtime.Modeling.RuntimeModelStore.PrefetchesPending == 0
                    && (_renderer?.BackgroundTexturesPending ?? 0) == 0;
                long mark = Stopwatch.GetTimestamp();
                for (int i = 0; i < scene.Subsystems.Count; i++)
                {
                    if (scene.Subsystems[i] is not IRoomWarmUpSubsystem warming) continue;
                    if (!warming.WarmUp(scene)) ready = false;
                    mark = scene.WorkTimes.Add(Genesis.Runtime.Diagnostics.SceneWorkTimes.NameOf(warming, "warm-up"), mark);
                }

                // A frame that loaded something, or took far longer than the quickest frame so
                // far, shows the room is still settling.
                long now = Stopwatch.GetTimestamp();
                Genesis.Shared.Assets.LoadClockSnapshot loadedNow = Genesis.Shared.Assets.LoadClock.Capture();
                if (frames > 0)
                {
                    double frame = Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds;
                    quickest = Math.Min(quickest, frame);
                    slowest1 = Math.Max(slowest1, frame);
                    bool quiet = loadedNow.MillisecondsSince(loaded) < 1.0 && frame <= Math.Max(40.0, quickest * 1.6);
                    settled = ready && quiet ? settled + 1 : 0;
                }

                lastFrame = now;
                loaded = loadedNow;
                double seconds = Stopwatch.GetElapsedTime(warmUpStarted).TotalSeconds;
                bool shownLongEnough = Stopwatch.GetElapsedTime(changeStarted).TotalSeconds >= RoomChangeScreen.MinimumSeconds;
                if (shownLongEnough && ((frames >= WarmUpFrames && settled >= 2) || seconds >= WarmUpTimeoutSeconds)) break;
                frames++;
                yield return (0.7f + 0.3f * frames / (frames + 8f), true);
            }

            _logger?.Line((firstRoom ? "First room: " : "Room change: ")
                + $"{roomName} was prepared behind the cover for {frames} frames "
                + $"({Stopwatch.GetElapsedTime(warmUpStarted).TotalMilliseconds:F0} ms, longest frame {slowest1:F0} ms)"
                + (firstRoom
                    ? "; "
                    : $" and shown {Stopwatch.GetElapsedTime(changeStarted).TotalMilliseconds:F0} ms after the change began, over {_framesSpread} frames; ")
                + $"the longest single piece {(firstRoom ? "of that" : "of the change")} took {_longestPieceMilliseconds:F0} ms"
                + (Stopwatch.GetElapsedTime(warmUpStarted).TotalSeconds >= WarmUpTimeoutSeconds ? "; it was shown before it had settled, at the time limit" : string.Empty));
        }

        private static bool KeepSubsystem(ISceneSubsystem sub) =>
            sub is ProjectRoomSwitcher or ProjectAssetLiveReloadSubsystem or ScriptHostSubsystem
                or AutoshotSubsystem or MushroomMeadowRuntimeAcceptance;
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

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Genesis.Audio;
using Genesis.Net;
using Genesis.Rendering.Diagnostics;
using Genesis.Runtime.Platform;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Audio;
using Genesis.Shared.Commands;
using Genesis.Shared.Net;
using Genesis.Shared.Interfaces;
using Genesis.Streaming;
using Genesis.World;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Project-only GenesisEngine entry: instantiates a room, runs validated PGSL on the shared VM,
    /// and optionally loads compatibility EntityBehavior types from GameScripts.dll. Used by the
    /// editor F5 path and headless verification.
    /// </summary>
    public static class ProjectPlayerApp
    {
        private static int _exitCode;
        private static int _captureAttempts;
        private const int MaxCaptureAttempts = 12;
        private static bool _capturePending;
        private static bool _captureWatchdogScheduled;
        private static bool _closeAfterPresent;
        private static string _captureLabel;
        private static string _captureProject;
        private static ProjectLogger _captureLogger;
        private static GenesisRuntimeHost _captureHost;
        private static GenesisRuntimeHost _activeHost;
        private static SilkGameWindow _activeWindow;
        private static XAudioSystem _activeAudio;   // ticked each frame to recycle voices
        private static LiteNetGameNetwork _activeNet; // ticked each frame to pump packets
        private static volatile bool _stopRequested;
        private static volatile bool _pauseRequested;
        public static string LastError { get; private set; }
        public static Genesis.Runtime.Startup.IRuntimeStartupGate StartupGate { get; set; }
        public static void SetPaused(bool paused) => _pauseRequested = paused;

        /// <summary>Closes the active Silk play window (editor F5 stop / re-run).</summary>
        public static void RequestStop()
        {
            _stopRequested = true;
        }

        public static int Run(string[] args)
        {
            LastError = null;
            _exitCode = 0;
            try
            {
                if (TryRunScriptEntryPoint(args, out int customExit))
                    return customExit;

                ParseArgs(args, out string roomArg, out float autoshotSeconds, out string perfLabel, out bool debugMode);
                PgslProfiler.Reset();
                PgslProfiler.Enabled = debugMode;

                string projectPath = Environment.GetEnvironmentVariable("GENESIS_PROJECT_PATH");
                if (string.IsNullOrWhiteSpace(projectPath))
                    projectPath = ProjectRoomResolver.ResolveStandaloneProjectRoot(AppContext.BaseDirectory);
                projectPath = ProjectRoomResolver.ResolveProjectRoot(projectPath);
                if (string.IsNullOrEmpty(projectPath))
                {
                    Console.Error.WriteLine(
                        "GENESIS_PROJECT_PATH is not set and no Rooms/ folder was found next to the player.");
                    return 2;
                }

                GameLaunchSettings launchSettings = LoadPackagedLaunchSettings();
                if (!string.IsNullOrWhiteSpace(launchSettings.Icon))
                {
                    string iconPath = Path.Combine(AppContext.BaseDirectory, launchSettings.Icon);
                    if (File.Exists(iconPath)) Environment.SetEnvironmentVariable("GENESIS_GAME_ICON", iconPath);
                }

                string roomName = ProjectRoomResolver.ResolveRoomName(
                    projectPath,
                    roomArg
                        ?? Environment.GetEnvironmentVariable("GENESIS_START_ROOM")
                        ?? ProjectPaths.ReadStartRoom(projectPath));

                if (string.IsNullOrEmpty(roomName))
                {
                    Console.Error.WriteLine("No room to play. Pass --room or add a room to the project.");
                    return 2;
                }

                string roomFile = ProjectRoomResolver.ResolveRoomFile(projectPath, roomName);
                if (roomFile == null)
                {
                    Console.Error.WriteLine($"Room file not found: {roomName}");
                    return 2;
                }

                if (autoshotSeconds <= 0f
                    && float.TryParse(Environment.GetEnvironmentVariable("GENESIS_AUTOSHOT"), out float envShot)
                    && envShot > 0f)
                {
                    autoshotSeconds = envShot;
                }

                if (string.IsNullOrEmpty(perfLabel))
                    perfLabel = Environment.GetEnvironmentVariable("GENESIS_PERF_LABEL");

                RoomAsset room = RoomAssetLoader.Parse(roomFile);
                using var logger = new ProjectLogger(projectPath);
                logger.Line($"project={projectPath}");
                logger.Line($"room={roomName} file={roomFile}");
                logger.Line($"autoshot={autoshotSeconds} label={perfLabel ?? "(none)"}");

                ProjectPaths.EnsureDebugDirs(projectPath);
                RenderLog.Init();

                System.Drawing.Size display = RoomDisplayLayout.WindowSize(room);
                int width = display.Width;
                int height = display.Height;
                string title = !string.IsNullOrWhiteSpace(launchSettings.Title)
                    ? launchSettings.Title
                    : string.IsNullOrEmpty(room.Name) ? roomName : room.Name;

                ScriptHostSystem scriptHost = null;
                ProjectGameContext gameContext = null;
                GenesisRuntimeHost host = null;
                ProjectBootSplash bootSplash = new ProjectBootSplash(projectPath, logger, roomName);

                host = new GenesisRuntimeHost(title, width, height, (scene, renderer) =>
                {
                    var window = host.Window as SilkGameWindow;
                    if (window is not null && Enum.TryParse(launchSettings.WindowMode, true, out WindowMode startupMode))
                        window.Mode = startupMode;
                    _activeHost = host;
                    _activeWindow = window;
                    scene.Input = window?.Input ?? scene.Input;

                    if (room.Dimension == RoomDimension.ThreeD && room.Settings.VoxelWorld)
                        SceneDefaults.ApplyVoxelWorld(scene);

                    scriptHost = new ScriptHostSystem();
                    scriptHost.DiagnosticReported += logger.WriteScriptDiagnostic;
                    string dllPath = Path.Combine(AppContext.BaseDirectory, "GameScripts.dll");
                    if (File.Exists(dllPath))
                    {
                        byte[] dllBytes = File.ReadAllBytes(dllPath);
                        scriptHost.LoadAssembly(Assembly.Load(dllBytes));
                        logger.Line($"loaded GameScripts.dll ({dllBytes.Length} bytes)");
                    }
                    else
                    {
                        logger.Line("GameScripts.dll not found — PGSL objects will run on the validated VM backend.");
                    }

                    gameContext = new ProjectGameContext(projectPath, scene, renderer, window, room, logger);
                    scriptHost.SetContext(gameContext);

                    try
                    {
                        Genesis.Runtime.Textures.RuntimeTextureAtlas.Build(projectPath, renderer);
                        logger.Line(
                            $"texture groups stitched: sheets={Genesis.Runtime.Textures.RuntimeTextureAtlas.AtlasSheetCount} "
                            + $"sprites={Genesis.Runtime.Textures.RuntimeTextureAtlas.MappedSpriteCount} "
                            + $"occupancy={Genesis.Runtime.Textures.RuntimeTextureAtlas.AverageOccupancyPercent:0.0}%");
                    }
                    catch (Exception ex)
                    {
                        logger.Line($"texture group stitch failed ({ex.Message}); unique textures remain");
                        Genesis.Runtime.Textures.RuntimeTextureAtlas.Clear();
                    }

                    // ── Audio (Phase 2.1) ────────────────────────────────────────────
                    // Instantiate the XAudio2-backed audio service and expose it both
                    // through IGameContext.Audio (for C# behaviours) and the Engine.Audio.*
                    // commands (for PGSL + the unified command surface). Previously the
                    // Genesis.Audio library compiled but was never instantiated, so the
                    // runtime was silent.
                    XAudioSystem audioSystem = null;
                    try
                    {
                        audioSystem = new XAudioSystem(projectPath);
                        gameContext.SetAudio(audioSystem);
                        Engine.SetAudioSystem(
                            loadSound: name => audioSystem.LoadSound(name),
                            playSound: (sid, vol, pitch, loop) => audioSystem.Play(sid, vol, pitch, loop).Id);
                        Engine.SetAudioStop(ch => audioSystem.Stop(new AudioChannel(ch)));
                        Engine.SetAudioMasterVolume(v => audioSystem.MasterVolume = v);
                        logger.Line("audio system initialised (XAudio2)");
                    }
                    catch (Exception ex)
                    {
                        // Audio is non-fatal — degrade silently to the NullAudioSystem already on the context.
                        logger.Line($"audio init failed ({ex.Message}); running silent");
                    }
                    _activeAudio = audioSystem;

                    // ── Networking (Phase 2.2) ───────────────────────────────────────
                    // Instantiate the LiteNetLib-backed network service and expose it
                    // through Engine.Net.*. Gameplay calls Engine.NetHost/NetConnect to
                    // start a session; Update() pumps packets on the main thread.
                    _activeNet = new LiteNetGameNetwork();
                    Engine.SetNetwork(_activeNet);
                    logger.Line("network system initialised (LiteNetLib)");

                    var builder = new RoomSceneBuilder(projectPath, scriptHost);
                    RoomAsset loaded = RoomAssetLoader.Parse(roomFile);
                    RoomBuildResult build = builder.Build(scene, loaded);
                    gameContext.SetRoom(build.Asset);
                    scriptHost.BeginRoom(beginGame: true);

                    if (RoomTerrainSubsystem.ShouldRegister(loaded))
                        scene.AddSubsystem(new RoomTerrainSubsystem(projectPath, loaded, gameContext));
                    if (RoomEnvironmentAudioSubsystem.ShouldRegister(loaded.Environment))
                        scene.AddSubsystem(new RoomEnvironmentAudioSubsystem(loaded.Environment, gameContext));

                    scene.AddSubsystem(new ObjectCompositionSubsystem(projectPath, gameContext.Audio));
                    scene.AddSubsystem(new RoomRenderSubsystem(projectPath, loaded));
                    scene.AddSubsystem(new ObjectDrawSubsystem(projectPath));
                    scene.AddSubsystem(new ScriptHostSubsystem(scriptHost, () => bootSplash.IsComplete));
                    ProjectRoomSwitcher roomSwitcher = scene.AddSubsystem(new ProjectRoomSwitcher(
                        projectPath, gameContext, scriptHost, window, renderer, logger, roomName));
                    scene.AddSubsystem(new ProjectAssetLiveReloadSubsystem(
                        projectPath, renderer, roomSwitcher, logger));
                    host.ScriptHost = scriptHost;
                    bootSplash.Initialize(renderer);
                    host.BootSplash = bootSplash;

                    ApplyRoomPresentation(window, renderer, loaded);
                    logger.Line($"room loaded entities={build.SpawnedEntities.Count} dimension={loaded.Dimension}");
                    Console.WriteLine("GENESIS_PLAYER_STATE running");
                });

                host.StartupGate = StartupGate;
                if (autoshotSeconds > 0f)
                {
                    Environment.SetEnvironmentVariable("GENESIS_AUTOSHOT",
                        autoshotSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    string shotLabel = string.IsNullOrWhiteSpace(perfLabel) ? "autoshot" : perfLabel;
                    _captureProject = projectPath;
                    _captureLabel = shotLabel;
                    _captureLogger = logger;
                    _captureHost = host;
                    _capturePending = false;
                    _captureAttempts = 0;
                    _captureWatchdogScheduled = false;
                    _closeAfterPresent = false;
                    _exitCode = 0;

                    host.EndFrame += OnAutoshotEndFrame;
                    host.AfterPresent += OnAutoshotAfterPresent;
                    host.SceneBuilt += h =>
                    {
                        h.Scene.AddSubsystem(new AutoshotSubsystem(
                            autoshotSeconds,
                            ScheduleAutoshotCapture,
                            countElapsed: () => h.BootSplash == null || h.BootSplash.IsComplete));
                    };
                }

                // "Allow ESC to close the game" is a property of the game, so it arrives from the
                // project's own settings (via F5) rather than from whoever's machine is running it.
                // Absent means allowed: a game you cannot quit is the worse default.
                string escapeSetting = Environment.GetEnvironmentVariable(
                    Genesis.Rendering.Core.EngineRenderingDefaults.AllowEscapeEnvironmentVariable);
                bool allowEscapeToClose = string.IsNullOrWhiteSpace(escapeSetting) || escapeSetting != "0";
                logger.Line($"allowEscapeToClose={allowEscapeToClose}");

                using (host)
                {
                    using LiveProfilerTelemetry telemetry = debugMode
                        ? new LiveProfilerTelemetry(projectPath)
                        : null;
                    host.ScreenshotProjectPath = projectPath;
                    host.IsDebugMode = debugMode;
                    if (debugMode && host.Debugger != null)
                    {
                        host.Debugger.IsVisible = true;
                    }

                    host.DebugRoomName = roomName;
                    // Tick the audio system + network every variable frame.
                    host.FrameUpdate += dt =>
                    {
                        if (host.IsPlayPaused != _pauseRequested)
                        {
                            host.IsPlayPaused = _pauseRequested;
                            Console.WriteLine("GENESIS_PLAYER_STATE " + (host.IsPlayPaused ? "paused" : "running"));
                        }
                        if (_stopRequested) _activeWindow?.Close();
                        try { _activeAudio?.Update(); } catch { }
                        try { _activeNet?.Update(); } catch { }
                        telemetry?.Sample(dt, host.Renderer, host.DebugRoomName);

                        // Read through the scene, not the window. Both expose an InputState, but the
                        // scene's is the one the frame loop actually fills and clears — it is what
                        // the host's own screenshot checks use. Polling the window's copy saw a
                        // permanently empty pressed-set, so Escape was never observed.
                        if (allowEscapeToClose
                            && host.Scene?.Input?.WasPressed(Genesis.Runtime.Input.Key.Escape) == true)
                        {
                            logger.Line("Escape pressed — closing the game.");
                            _activeWindow?.Close();
                        }
                    };
                    host.Run();
                    if (host.StartupFailure != null) throw new InvalidOperationException("The game runtime failed.", host.StartupFailure);
                }

                return _exitCode;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                Console.Error.WriteLine("ProjectPlayerApp fatal: " + ex);
                return 3;
            }
            finally
            {
                try { _activeAudio?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
                try { _activeNet?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
                _activeAudio = null; _activeNet = null; _activeHost = null; _activeWindow = null;
                _stopRequested = false; _pauseRequested = false;
                PgslProfiler.Enabled = false;
            }
        }

        private sealed class GameLaunchSettings
        {
            public string Title { get; set; } = string.Empty;
            public string WindowMode { get; set; } = "Windowed";
            public string Icon { get; set; } = string.Empty;
        }

        private static GameLaunchSettings LoadPackagedLaunchSettings()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "GenesisGame.json");
            if (!File.Exists(path)) return new GameLaunchSettings();
            try
            {
                return JsonSerializer.Deserialize<GameLaunchSettings>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new GameLaunchSettings();
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return new GameLaunchSettings();
            }
        }

        private static void ScheduleAutoshotCapture()
        {
            _capturePending = true;
            if (_captureWatchdogScheduled) return;
            _captureWatchdogScheduled = true;
            System.Threading.Tasks.Task.Delay(15000).ContinueWith(_ =>
            {
                if (!_capturePending) return;
                _captureLogger?.Line("autoshot watchdog: capture hook never completed");
                Environment.Exit(4);
            });
        }

        private static void OnAutoshotEndFrame(Genesis.Shared.Interfaces.IRenderController renderer)
        {
            if (!_capturePending) return;
            _captureLogger?.Line("OnAutoshotEndFrame entered!");

            try
            {
                WritePerfSnapshot(renderer, _captureLabel, _captureLogger);
                string path = ProjectScreenshot.Capture(renderer, _captureProject, _captureLabel, frameAlreadySubmitted: true);
                if (path != null)
                {
                    _capturePending = false;
                    _captureAttempts = 0;
                    _captureLogger?.Line($"screenshot saved: {path}");
                    _exitCode = 0;
                }
                else
                {
                    _captureAttempts++;
                    _captureLogger?.Line(
                        $"screenshot capture failed (attempt {_captureAttempts}/{MaxCaptureAttempts})");
                    if (_captureAttempts >= MaxCaptureAttempts)
                    {
                        _capturePending = false;
                        _exitCode = 4;
                    }
                    else
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _captureAttempts++;
                _captureLogger?.Line("autoshot capture error: " + ex.Message);
                if (_captureAttempts >= MaxCaptureAttempts)
                {
                    _capturePending = false;
                    _exitCode = 4;
                }
                else
                {
                    return;
                }
            }

            // Close after Present — Closing disposes the renderer, and the host still
            // presents on the way out of this frame.
            _closeAfterPresent = true;
        }

        private static void OnAutoshotAfterPresent()
        {
            if (!_closeAfterPresent) return;
            _closeAfterPresent = false;
            try { (_captureHost?.Window as SilkGameWindow)?.Close(); } catch { }
            int exitCode = _exitCode;
            System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => Environment.Exit(exitCode));
        }

        private static void WritePerfSnapshot(IRenderController renderer, string label, ProjectLogger logger)
        {
            if (string.IsNullOrWhiteSpace(label))
                label = "autoshot";

            RenderStats stats = renderer.GetStats();
            double fps = (_captureHost?.Window as SilkGameWindow)?.CurrentFps ?? 0d;

            int cellsLoaded = 0, cellsVisible = 0, cellsDrawn = 0, meshRegion = 0, invalidSolid = 0, emptyCpu = 0;
            int terrainColliders = 0, waterVolumes = 0, authoredFoliage = 0;
            int particleEmitters = 0, activeParticles = 0, activeAudio = 0, pointLights = 0;
            int animators = 0, advancedAnimators = 0;
            RuntimeScene scene = _captureHost?.Scene;
            if (scene != null)
            {
                terrainColliders = scene.Physics?.ExternalStaticCount ?? 0;
                waterVolumes = scene.WaterVolumes.Count;
                scene.World.Query<Genesis.Runtime.ECS.Components.ModelAnimatorComponent>(
                    (Genesis.Shared.ECS.Entity entity, ref Genesis.Runtime.ECS.Components.ModelAnimatorComponent animator) =>
                    {
                        animators++;
                        if (animator.TimeSeconds > 0f) advancedAnimators++;
                    });
                for (int i = 0; i < scene.Subsystems.Count; i++)
                {
                    if (scene.Subsystems[i] is RoomTerrainSubsystem terrain)
                        authoredFoliage += terrain.AuthoredFoliageInstanceCounts.Sum();
                    else if (scene.Subsystems[i] is ObjectCompositionSubsystem composition)
                    {
                        particleEmitters += composition.ParticleEmitterCount;
                        activeParticles += composition.ActiveParticleCount;
                        activeAudio += composition.ActiveAudioCount;
                        pointLights += composition.PointLightCount;
                    }
                }
                var providers = scene.Streaming.Providers;
                for (int i = 0; i < providers.Count; i++)
                {
                    if (providers[i] is VoxelWorldRenderer vr)
                    {
                        cellsLoaded = vr.ChunksLoaded;
                        cellsVisible = vr.ChunksVisible;
                        cellsDrawn = vr.ChunksDrawn;
                        meshRegion = vr.MeshRegionSize;
                        invalidSolid = vr.InvalidSolidMeshCount;
                        emptyCpu = vr.EmptySolidCpuCount;
                        break;
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("label=" + label);
            sb.AppendLine("timestamp=" + DateTime.UtcNow.ToString("o"));
            sb.AppendLine($"fps={fps:F1}");
            sb.AppendLine($"drawCalls3D={stats.DrawCalls3D}");
            sb.AppendLine($"batches={stats.Batches}");
            sb.AppendLine($"instancesDrawn={stats.InstancesDrawn}");
            sb.AppendLine($"instancesCulled={stats.InstancesCulled}");
            sb.AppendLine($"foliageInstances={stats.FoliageInstances}");
            sb.AppendLine($"foliageBatches={stats.FoliageBatches}");
            sb.AppendLine($"foliageUploadBytes={stats.FoliageUploadBytes}");
            sb.AppendLine($"meshDrawsSubmitted={stats.ItemsSubmitted}");
            sb.AppendLine($"triangles3D={stats.Triangles3D}");
            sb.AppendLine($"gpuMs={stats.GpuMs:F2}");
            sb.AppendLine($"textureSwitches={stats.TextureSwitches}");
            sb.AppendLine($"cellsLoaded={cellsLoaded}");
            sb.AppendLine($"cellsVisible={cellsVisible}");
            sb.AppendLine($"cellsDrawn={cellsDrawn}");
            sb.AppendLine($"invalidSolidMeshes={invalidSolid}");
            sb.AppendLine($"emptySolidCpu={emptyCpu}");
            sb.AppendLine($"meshRegionSize={meshRegion}");
            sb.AppendLine($"terrainColliders={terrainColliders}");
            sb.AppendLine($"waterVolumes={waterVolumes}");
            sb.AppendLine($"authoredFoliage={authoredFoliage}");
            sb.AppendLine($"particleEmitters={particleEmitters}");
            sb.AppendLine($"activeParticles={activeParticles}");
            sb.AppendLine($"activeAudio={activeAudio}");
            sb.AppendLine($"pointLights={pointLights}");
            sb.AppendLine($"animators={animators}");
            sb.AppendLine($"advancedAnimators={advancedAnimators}");
            sb.AppendLine("renderDistance=" + (Environment.GetEnvironmentVariable("GENESIS_MC_RD") ?? "default"));

            logger?.Line(
                $"runtime composition terrainColliders={terrainColliders} waterVolumes={waterVolumes} "
                + $"authoredFoliage={authoredFoliage} visibleFoliage={stats.FoliageInstances} "
                + $"particleEmitters={particleEmitters} activeParticles={activeParticles} "
                + $"activeAudio={activeAudio} pointLights={pointLights} "
                + $"animators={animators} advancedAnimators={advancedAnimators}");

            string logDir = ResolvePerfLogDir();
            string logPath = Path.Combine(logDir, label + ".log");
            File.WriteAllText(logPath, sb.ToString());
            logger?.Line("perf snapshot: " + logPath);
        }

        private static string ResolvePerfLogDir()
        {
            string env = Environment.GetEnvironmentVariable("GENESIS_PERF_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                Directory.CreateDirectory(env);
                return env;
            }

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Ember", "Debug", "Logs");
                if (Directory.Exists(Path.Combine(dir, "Ember")))
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }

            return AppContext.BaseDirectory;
        }

        private static void ApplyRoomPresentation(SilkGameWindow window, Genesis.Shared.Interfaces.IRenderController renderer, RoomAsset room)
            => ProjectRoomPresentation.Apply(window, renderer, room);

        private static void ParseArgs(string[] args, out string room, out float autoshotSeconds, out string perfLabel, out bool debug)
        {
            room = null;
            autoshotSeconds = 0f;
            perfLabel = null;
            debug = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--room" || a == "-room") && i + 1 < args.Length)
                    room = args[++i];
                else if (a == "--debug" || a == "-debug")
                    debug = true;
                else if (a == "--autoshot" && i + 1 < args.Length
                         && float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out float shot))
                    autoshotSeconds = shot;
                else if (a == "--perf-label" && i + 1 < args.Length)
                    perfLabel = args[++i];
            }
        }

        /// <summary>
        /// If the project's compiled scripts define their own <c>Program.Main</c>, hand off entirely.
        /// </summary>
        private static bool TryRunScriptEntryPoint(string[] args, out int exitCode)
        {
            exitCode = 0;
            string dllPath = Path.Combine(AppContext.BaseDirectory, "GameScripts.dll");
            if (!File.Exists(dllPath))
                return false;

            Assembly asm;
            try { asm = Assembly.Load(File.ReadAllBytes(dllPath)); }
            catch { return false; }

            foreach (Type type in asm.GetTypes())
            {
                if (!string.Equals(type.Name, "Program", StringComparison.OrdinalIgnoreCase))
                    continue;

                MethodInfo main = type.GetMethod("Main",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (main == null)
                    continue;

                try
                {
                    object result;
                    var parms = main.GetParameters();
                    if (parms.Length == 0)
                        result = main.Invoke(null, null);
                    else if (parms.Length == 1 && parms[0].ParameterType == typeof(string[]))
                        result = main.Invoke(null, new object[] { args });
                    else
                        continue;

                    exitCode = result is int code ? code : 0;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("GameScripts Program.Main failed: " + ex);
                    exitCode = 1;
                    return true;
                }
            }

            return false;
        }
    }
}

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
        private static Genesis.Runtime.Net.NetworkReplication _replication;
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

        /// <summary>
        /// Set to a label to save a picture of each room change's loading screen, and of the first
        /// room's, as <c>&lt;label&gt;-&lt;room&gt;.png</c> beside the game's screenshots.
        /// </summary>
        public const string RoomChangeShotEnvironmentVariable = "GENESIS_ROOM_CHANGE_SHOT";

        /// <summary>Set to 0 to read every model texture on the game's thread in the frame that first draws it.</summary>
        public const string BackgroundTexturesEnvironmentVariable = "GENESIS_BACKGROUND_TEXTURES";

        /// <summary>Studio play passes this so the player watches and hot-reloads project assets.</summary>
        public const string LiveReloadArgument = "--live-reload";

        public static int Run(string[] args)
        {
            // GENESIS_LOAD_PROFILE=1: where start-up and each room's loading time goes, written to the log.
            Genesis.Shared.Diagnostics.LoadProfile.UseCurrentThreadAsGameThread();
            Genesis.Shared.Diagnostics.LoadProfile.Mark("player started");
            Genesis.Shared.Diagnostics.LoadProfile.Span startProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("start-up before the window");
            LastError = null;
            _exitCode = 0;
            // A game plays its project; it never cooks a model into it (see StudioModelResourceLoader).
            Genesis.Runtime.Modeling.StudioModelResourceLoader.WriteReimportsToProject = false;
            ScriptScreenshots.Reset();
            Genesis.Runtime.Rendering.ScriptMeshes.Reset();
            Genesis.Runtime.Rendering.ModelLayers.Reset();
            Genesis.Runtime.Input.InputReplay.Reset();
            Genesis.Runtime.Input.InputReplay.ReplayEnded -= OnInputReplayEnded;
            Genesis.Runtime.Input.InputReplay.ReplayEnded += OnInputReplayEnded;
            _quitWhenReplayEnds = false;
            try
            {
                bool customEntry;
                int customExit;
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("look for a Main in the game's scripts"))
                    customEntry = TryRunScriptEntryPoint(args, out customExit);
                if (customEntry)
                    return customExit;

                ParseArgs(args, out string roomArg, out float autoshotSeconds, out string perfLabel, out bool debugMode);
                // Only Studio play (and tooling that launches through ProjectRunLauncher) watches and
                // re-validates assets. An exported game loads each asset once: no FileSystemWatcher and
                // no per-frame or periodic file checks on its draw paths.
                bool liveReload = Array.Exists(args, value =>
                    string.Equals(value, LiveReloadArgument, StringComparison.OrdinalIgnoreCase));
                if (!liveReload) Genesis.Shared.Assets.RuntimeAssetPolicy.DisablePolling();
                int benchmarkIndex = Array.IndexOf(args, "--benchmark-output");
                string benchmarkOutput = benchmarkIndex >= 0 && benchmarkIndex + 1 < args.Length
                    && !args[benchmarkIndex + 1].StartsWith("--", StringComparison.Ordinal)
                    ? Path.GetFullPath(args[benchmarkIndex + 1]) : null;
                if (benchmarkIndex >= 0 && benchmarkOutput == null)
                    throw new ArgumentException("--benchmark-output requires an evidence directory.");
                double benchmarkSeconds = BenchmarkNumber(args, "--benchmark-seconds", 600);
                double benchmarkWarmup = BenchmarkNumber(args, "--benchmark-warmup-seconds", 5);
                int acceptanceIndex = Array.IndexOf(args, "--acceptance-meadow");
                string acceptanceOutput = acceptanceIndex >= 0 && acceptanceIndex + 1 < args.Length
                    ? Path.GetFullPath(args[acceptanceIndex + 1]) : null;
                if (acceptanceIndex >= 0 && acceptanceOutput == null)
                    throw new ArgumentException("--acceptance-meadow requires an evidence directory.");
                int verdantIndex = Array.IndexOf(args, "--acceptance-verdant");
                string verdantOutput = verdantIndex >= 0 && verdantIndex + 1 < args.Length
                    ? Path.GetFullPath(args[verdantIndex + 1]) : null;
                if (verdantIndex >= 0 && verdantOutput == null)
                    throw new ArgumentException("--acceptance-verdant requires an evidence directory.");
                int pathingIndex = Array.IndexOf(args, "--acceptance-pathing");
                string pathingOutput = pathingIndex >= 0 && pathingIndex + 1 < args.Length
                    ? Path.GetFullPath(args[pathingIndex + 1]) : null;
                if (pathingIndex >= 0 && pathingOutput == null)
                    throw new ArgumentException("--acceptance-pathing requires an evidence directory.");
                int physicsIndex = Array.IndexOf(args, "--acceptance-physics-model");
                string physicsOutput = physicsIndex >= 0 && physicsIndex + 1 < args.Length
                    ? Path.GetFullPath(args[physicsIndex + 1]) : null;
                if (physicsIndex >= 0 && physicsOutput == null)
                    throw new ArgumentException("--acceptance-physics-model requires an evidence directory.");
                if (new[] { acceptanceOutput, verdantOutput, pathingOutput, physicsOutput }.Count(value => value != null) > 1)
                    throw new ArgumentException("Choose one acceptance driver per run.");
                if (benchmarkOutput != null && (autoshotSeconds > 0
                    || new[] { acceptanceOutput, verdantOutput, pathingOutput, physicsOutput }.Any(value => value != null)))
                    throw new ArgumentException("Run a benchmark separately from autoshot and acceptance drivers.");
                PgslProfiler.Reset();
                // A new game: no globals from a previous run, the real-time clock at zero, and
                // GameEnd() closes this game's window.
                Genesis.Runtime.Scripting.PgslCommands.ResetSession();
                Genesis.Runtime.Scripting.PgslCommands.GameQuitHandler = RequestStop;
                PgslProfiler.Enabled = debugMode || benchmarkOutput != null;

                // The built-in shaders are read (from the precompiled folder shipped with the
                // engine) or compiled on worker threads while the project and room are read and
                // the window is made; the renderer then finds them ready. After an engine update
                // (nothing cached yet) compiling them one by one used to hold a blank window for
                // seconds. Every GPU backend: DXC compiles in its own processes, side by side.
                Genesis.Shared.Diagnostics.LoadProfile.Span shaderStartProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("start compiling the built-in shaders on workers");
                try
                {
                    Genesis.Rendering.Core.RenderBackendOption backend = Genesis.Rendering.Core.RenderControllerFactory.ResolveBackend();
                    if (Environment.GetEnvironmentVariable("GENESIS_SHADER_WARMUP") != "0"
                        && backend != Genesis.Rendering.Core.RenderBackendOption.Software)
                        Genesis.Rendering.Primitives.ShaderCompiler.WarmBuiltInInBackground(
                            Genesis.Rendering.Core.RenderBackendCatalog.Describe(backend).ShaderBinaryFormat);
                }
                catch (Exception warmError) when (warmError is ArgumentException or InvalidOperationException)
                {
                    // An unknown backend name is reported by the renderer itself when it starts.
                }
                shaderStartProfile.Dispose();

                Genesis.Shared.Diagnostics.LoadProfile.Span findProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("find the project and its start room");
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

                // Rooms and scripts name resources without their kind: say which resources clash
                // before anything trips over the name.
                System.Collections.Generic.IReadOnlyList<Genesis.Shared.Assets.NamedResource[]> duplicateNames =
                    Genesis.Shared.Assets.ResourceCatalog.For(projectPath).DuplicateNames;
                if (duplicateNames.Count > 0)
                {
                    LastError = string.Join(Environment.NewLine + Environment.NewLine, duplicateNames
                        .Select(named => Genesis.Shared.Assets.ResourceCatalog.DescribeDuplicate(named, projectPath)));
                    Console.Error.WriteLine(LastError);
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
                findProfile.Dispose();

                if (autoshotSeconds <= 0f
                    && float.TryParse(Environment.GetEnvironmentVariable("GENESIS_AUTOSHOT"), out float envShot)
                    && envShot > 0f)
                {
                    autoshotSeconds = envShot;
                }
                if (benchmarkOutput != null && autoshotSeconds > 0)
                    throw new ArgumentException("Run a benchmark separately from GENESIS_AUTOSHOT.");

                if (string.IsNullOrEmpty(perfLabel))
                    perfLabel = Environment.GetEnvironmentVariable("GENESIS_PERF_LABEL");

                RoomAsset room;
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("read the room file"))
                    room = RoomAssetLoader.Parse(roomFile);
                using var logger = new ProjectLogger(projectPath);
                Genesis.Shared.Diagnostics.LoadProfile.Mark("log open");
                logger.Line($"project={projectPath}");
                logger.Line($"room={roomName} file={roomFile}");
                logger.Line($"autoshot={autoshotSeconds} label={perfLabel ?? "(none)"}");

                // Start reading the first room's models now. Workers read them while the window is
                // made and the loading screen prepares everything else, so the room does not read
                // them one after another when it is built and first drawn.
                try
                {
                    int named;
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("queue the first room's models to read ahead"))
                        named = new RoomSceneBuilder(projectPath).PrefetchModels(room, ProjectRoomLoader.PreloadKeepMilliseconds);
                    if (named > 0) logger.Line($"reading {named} models ahead for {roomName}");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                    or Newtonsoft.Json.JsonException or ArgumentException)
                {
                    logger.Line($"reading models ahead failed: {ex.Message}");
                }

                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("debug folders, render log and input replay"))
                {
                    ProjectPaths.EnsureDebugDirs(projectPath);
                    RenderLog.Init();
                    StartInputReplayFromLaunch(args, projectPath, logger);
                }

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

                Genesis.Shared.Diagnostics.LoadProfile.Span windowProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("make the game window object");
                host = new GenesisRuntimeHost(title, width, height, (scene, renderer) =>
                {
                    var window = host.Window as SilkGameWindow;
                    _activeHost = host;
                    _activeWindow = window;
                    // A post effect a script turns on is compiled on a worker; the frames meanwhile
                    // are drawn without it, instead of one frame waiting seconds for the compiler.
                    renderer.CompilePostEffectsInBackground = true;
                    scene.Input = window?.Input ?? scene.Input;
                    // The window is open and the graphics card ready: say so at once. Everything
                    // below (textures, sound, the first room) used to happen behind a blank window.
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("show the loading screen"))
                    {
                        if (host.ShowStartupProgress("Starting " + title, 0.02f))
                            logger.Line("Loading screen shown");
                    }
                    Genesis.Shared.Diagnostics.LoadProfile.Mark("loading screen shown");
                    logger.Line($"engine shaders so far: {Genesis.Rendering.Primitives.ShaderCompiler.PrecompiledReads} precompiled, "
                        + $"{Genesis.Rendering.Primitives.ShaderCompiler.CompilesPerformed} compiled here");

                    if (room.Dimension == RoomDimension.ThreeD && room.Settings.VoxelWorld)
                        SceneDefaults.ApplyVoxelWorld(scene);

                    Genesis.Shared.Diagnostics.LoadProfile.Span scriptsProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("load the game's scripts");
                    scriptHost = new ScriptHostSystem();
                    scriptHost.DiagnosticReported += logger.WriteScriptDiagnostic;
                    string dllPath = RuntimePaths.PlayerGameScriptsDll();
                    if (File.Exists(dllPath))
                    {
                        // The assembly the entry-point check loaded, when it read this same file; a
                        // second copy of the scripts in the process costs time and memory and nothing else.
                        byte[] dllBytes = _scriptsBytes != null && string.Equals(_scriptsPath, dllPath, StringComparison.OrdinalIgnoreCase)
                            ? _scriptsBytes : File.ReadAllBytes(dllPath);
                        Assembly scripts = ReferenceEquals(dllBytes, _scriptsBytes) && _scriptsAssembly != null
                            ? _scriptsAssembly : Assembly.Load(dllBytes);
                        scriptHost.LoadAssembly(scripts);
                        logger.Line($"loaded GameScripts.dll ({dllBytes.Length} bytes)");
                    }
                    else
                    {
                        logger.Line("GameScripts.dll not found — PGSL objects will run on the validated VM backend.");
                    }
                    _scriptsBytes = null;
                    _scriptsAssembly = null;
                    _scriptsPath = null;
                    scriptsProfile.Dispose();

                    gameContext = new ProjectGameContext(projectPath, scene, renderer, window, room, logger);
                    scriptHost.SetContext(gameContext);
                    // The static commands (save slots, ModelInstance, the Net and Room commands)
                    // answer to these. A PGSL event sets them for itself; a game written in C#
                    // runs no PGSL event, so the Player sets them for the whole run.
                    Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = gameContext;
                    Genesis.Runtime.Scripting.PgslCommands.ProjectPath = projectPath;

                    host.ShowStartupProgress("Preparing textures", 0.05f);
                    try
                    {
                        using (Genesis.Shared.Diagnostics.LoadProfile.Begin("stitch texture groups"))
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
                    Genesis.Shared.Diagnostics.LoadProfile.Span servicesProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("start sound and networking");
                    try
                    {
                        audioSystem = new XAudioSystem(projectPath);
                        // A window opened by a test or a tool makes no noise unless asked to.
                        if (Environment.GetEnvironmentVariable("GENESIS_UNATTENDED_WINDOW") == "1"
                            && Environment.GetEnvironmentVariable("GENESIS_UNATTENDED_AUDIO") != "1")
                        {
                            audioSystem.Muted = true;
                            logger.Line("audio muted: unattended window (set GENESIS_UNATTENDED_AUDIO=1 to hear it)");
                        }
                        gameContext.SetAudio(audioSystem);
                        XAudioSystem describedAudio = audioSystem;
                        Genesis.Shared.Diagnostics.DebugResourceCatalog.Register("sounds", describedAudio, () =>
                            describedAudio.DescribeSounds().Select(sound => new Genesis.Shared.Diagnostics.DebugResourceRow(
                                "Sound", Path.GetFileName(sound.Name), 1, sound.Bytes,
                                $"{sound.Seconds:0.0} s{(sound.Playing > 0 ? $" · {sound.Playing} playing" : string.Empty)} · {sound.Name}")));
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
                    // The script commands (NetHost, NetConnect, NetSendText ...) talk to the same network.
                    Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = _activeNet;
                    logger.Line("network system initialised (LiteNetLib)");
                    // Shared objects between host and players. Copies are made without a script
                    // host, so they show the Object and run none of its scripts.
                    _replication = new Genesis.Runtime.Net.NetworkReplication();
                    var copyBuilder = new Genesis.Runtime.Scene.RoomSceneBuilder(projectPath, null);
                    _replication.CreateCopy = copyBuilder.SpawnCopy;
                    _replication.Attach(_activeNet);
                    Genesis.Runtime.Scripting.PgslCommands.ActiveReplication = _replication;
                    servicesProfile.Dispose();

                    host.ShowStartupProgress("Building " + (string.IsNullOrEmpty(room.Name) ? roomName : room.Name), 0.1f);
                    RoomAsset loaded;
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("read the room file again"))
                        loaded = RoomAssetLoader.Parse(roomFile);
                    RoomBuildResult build;
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("build the first room"))
                        build = ProjectRoomLoader.Build(projectPath, scene, loaded, scriptHost, gameContext, beginGame: true);
                    Genesis.Shared.Diagnostics.LoadProfile.Mark("first room built");
                    Genesis.Shared.Diagnostics.LoadProfile.Span finishProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("add the room's subsystems and the start-up screen");
                    host.ShowStartupProgress("Preparing what the first room shows", 0.15f);
                    if (RoomEnvironmentAudioSubsystem.ShouldRegister(loaded.Environment))
                        scene.AddSubsystem(new RoomEnvironmentAudioSubsystem(loaded.Environment, gameContext));

                    scene.AddSubsystem(new ObjectCompositionSubsystem(projectPath, gameContext.Audio, loaded.Dimension == RoomDimension.TwoD));
                    scene.AddSubsystem(new RoomRenderSubsystem(projectPath, loaded));
                    scene.AddSubsystem(new ObjectDrawSubsystem(projectPath));
                    if (acceptanceOutput != null)
                    {
                        var acceptance = scene.AddSubsystem(new MushroomMeadowRuntimeAcceptance(
                            acceptanceOutput, projectPath, gameContext, scriptHost,
                            () => bootSplash.IsComplete, result => { _exitCode = result; RequestStop(); }));
                        host.EndFrame += acceptance.CaptureFrame;
                    }
                    if (verdantOutput != null)
                    {
                        var acceptance = scene.AddSubsystem(new VerdantHollowRuntimeAcceptance(
                            verdantOutput, gameContext, scriptHost,
                            () => bootSplash.IsComplete, result => { _exitCode = result; RequestStop(); }));
                        host.EndFrame += acceptance.CaptureFrame;
                    }
                    if (pathingOutput != null)
                    {
                        var acceptance = scene.AddSubsystem(new PathingRuntimeAcceptance(
                            pathingOutput, gameContext, scriptHost,
                            () => bootSplash.IsComplete, result => { _exitCode = result; RequestStop(); }));
                        host.EndFrame += acceptance.CaptureFrame;
                    }
                    if (physicsOutput != null)
                    {
                        var acceptance = scene.AddSubsystem(new PhysicsModelRuntimeAcceptance(
                            physicsOutput, scriptHost, () => bootSplash.IsComplete, result => { _exitCode = result; RequestStop(); }));
                        host.EndFrame += acceptance.CaptureFrame;
                    }
                    scene.AddSubsystem(new ScriptHostSubsystem(scriptHost, () => bootSplash.IsComplete));
                    ProjectRoomSwitcher roomSwitcher = scene.AddSubsystem(new ProjectRoomSwitcher(
                        projectPath, gameContext, scriptHost, window, renderer, logger, roomName));
                    // Textures a model needs are read and decoded on worker threads; the model is
                    // drawn when they arrive instead of holding a frame up for them.
                    Genesis.Runtime.Modeling.RuntimeModelRenderSystem.BackgroundTextures =
                        Environment.GetEnvironmentVariable(BackgroundTexturesEnvironmentVariable) != "0";
                    // A room change that is more than a moment's work is spread over frames behind
                    // a loading screen, a few milliseconds of it in each, unless the game or the
                    // person running it says otherwise.
                    RoomChangeScreen.Reset();
                    roomSwitcher.FrameBudgetMilliseconds = RoomChangeScreen.DefaultFrameBudgetMilliseconds;
                    if (double.TryParse(Environment.GetEnvironmentVariable(RoomChangeScreen.BudgetEnvironmentVariable),
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double roomBudget)
                        && double.IsFinite(roomBudget) && roomBudget >= 0)
                        roomSwitcher.FrameBudgetMilliseconds = roomBudget;
                    logger.Line(roomSwitcher.FrameBudgetMilliseconds > 0
                        ? $"room changes are spread over frames, {roomSwitcher.FrameBudgetMilliseconds:0.#} ms in each"
                        : "room changes happen in one step");
                    // The room the game starts in is prepared the same way, once the start-up screen is done.
                    roomSwitcher.PrepareFirstRoom(scene, loaded);
                    if (liveReload)
                        scene.AddSubsystem(new ProjectAssetLiveReloadSubsystem(
                            projectPath, renderer, roomSwitcher, logger));
                    else
                        logger.Line("AssetLiveReload disabled: assets load once (launched without " + LiveReloadArgument + ")");
                    host.ScriptHost = scriptHost;
                    bootSplash.Initialize(renderer);
                    host.BootSplash = bootSplash;

                    ApplyRoomPresentation(window, renderer, loaded);
                    finishProfile.Dispose();
                    logger.Line($"room loaded entities={build.SpawnedEntities.Count} dimension={loaded.Dimension}");
                    // The same breakdown a room change logs, for the first room.
                    logger.Line($"Room load timing: terrain {build.TerrainMilliseconds:F0} ms, placing objects {build.SpawnMilliseconds:F0} ms, "
                        + $"Create events {build.CreateEventsMilliseconds:F0} ms, room-start events {build.RoomStartMilliseconds:F0} ms; slowest objects: "
                        + (build.SlowestSpawns.Count == 0 ? "none" : string.Join(", ",
                            System.Linq.Enumerable.Select(build.SlowestSpawns, spawn => $"{spawn.Name} {spawn.Milliseconds:F0} ms"))));
                    Console.WriteLine("GENESIS_PLAYER_STATE running");
                });
                windowProfile.Dispose();

                // Apply the launch mode before OnLoad creates the swap chain, so its actual
                // pixel dimensions agree with the requested startup presentation.
                if (host.Window is SilkGameWindow startupWindow
                    && Enum.TryParse(launchSettings.WindowMode, true, out WindowMode startupMode))
                    startupWindow.Mode = startupMode;
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
                bool allowEscapeToClose = string.IsNullOrWhiteSpace(escapeSetting)
                    ? launchSettings.AllowEscapeToClose : escapeSetting != "0";
                logger.Line($"allowEscapeToClose={allowEscapeToClose}");
                startProfile.Dispose();

                using (host)
                {
                    using RuntimeBenchmarkRecorder benchmark = benchmarkOutput != null
                        ? new RuntimeBenchmarkRecorder(benchmarkOutput, benchmarkSeconds, benchmarkWarmup) : null;
                    if (benchmark != null)
                    {
                        host.EndFrame += benchmark.Capture;
                        host.AfterPresent += () =>
                        {
                            benchmark.AfterPresent(host);
                            if (benchmark.Complete) RequestStop();
                        };
                    }
                    using LiveProfilerTelemetry telemetry = debugMode
                        ? new LiveProfilerTelemetry(projectPath)
                        : null;
                    host.ScreenshotProjectPath = projectPath;
                    host.IsDebugMode = debugMode;
                    // The debug screen records into the project's debug folder; a debug run starts
                    // recording at once unless Studio's preference (GENESIS_DEBUG_RECORD=0) says not to.
                    host.DebugProfilesDirectory = Path.Combine(ProjectPaths.DebugRoot(projectPath), "Profiles");
                    host.DebugProjectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath));
                    host.DebugRecordOnStart = debugMode && DebugCategories.RecordOnStartRequested;
                    // --profile (or GENESIS_PROFILE=1) records the debug screen's profile from the
                    // start to the end of the run, frame phases included, without debug mode: no
                    // debug screen drawn, no inspector, no live telemetry.
                    bool profile = ProfileRequested(args) && !debugMode;
                    host.DebugEngineCategory = DebugCategories.EngineRequested || profile;
                    host.SceneBuilt += built =>
                    {
                        if (built.Debugger == null) return;
                        built.Debugger.RecordingStarted += folder => logger.Line("profile recording: " + folder);
                        built.Debugger.RecordingSaved += folder => logger.Line("profile saved: " + folder);
                        if (profile && !built.Debugger.StartRecording())
                            logger.Line("profile recording could not start (no debug folder to write to)");
                    };
                    if (debugMode && host.Debugger != null)
                    {
                        host.Debugger.IsVisible = true;
                    }

                    host.DebugRoomName = roomName;
                    host.BeforeRenderSubmit += () => gameContext?.SubmitUpdateLights();
                    // A picture of each room change's loading screen, for whoever asks for one: the
                    // third frame drawn behind the cover, saved beside the game's other screenshots.
                    string coverShot = Environment.GetEnvironmentVariable(RoomChangeShotEnvironmentVariable);
                    if (!string.IsNullOrWhiteSpace(coverShot))
                    {
                        RoomChangeProgress photographed = null;
                        int coveredFrames = 0;
                        host.RoomChangeCovered += (coverRenderer, change) =>
                        {
                            if (!ReferenceEquals(change, photographed))
                            {
                                photographed = change;
                                coveredFrames = 0;
                            }

                            if (++coveredFrames != 3) return;
                            try
                            {
                                string saved = ProjectScreenshot.Capture(coverRenderer, projectPath,
                                    $"{coverShot.Trim()}-{change.RoomName}", frameAlreadySubmitted: true);
                                logger.Line(saved != null
                                    ? $"room change cover saved: {saved}"
                                    : $"room change cover for {change.RoomName} could not be read back");
                            }
                            catch (Exception coverError)
                            {
                                logger.Line("room change cover capture failed: " + coverError.Message);
                            }
                        };
                    }
                    // Only this thread's loading holds a frame up, so only this thread's is counted.
                    Genesis.Shared.Assets.LoadClock.UseCurrentThread();
                    var slowFrames = new SlowFrameLog(logger.Line);
                    AllocationRateLog allocations = AllocationRateLog.Requested(debugMode) ? new AllocationRateLog(logger.Line) : null;
                    allocations?.SampleTypes();
                    host.AfterPresent += () =>
                    {
                        Genesis.Runtime.Diagnostics.SceneWorkTimes parts = host.Scene?.WorkTimes;
                        allocations?.FrameEnded((host.BootSplash == null || host.BootSplash.IsComplete)
                            && host.Scene != null && host.Scene.RoomChange == null);
                        slowFrames.FrameEnded(
                            gameContext?.Room?.Name ?? roomName,
                            counted: host.BootSplash == null || host.BootSplash.IsComplete,
                            host.LastSimulationMilliseconds, host.LastCollectMilliseconds,
                            host.LastDrawMilliseconds, host.LastPresentMilliseconds,
                            parts?.Describe() ?? string.Empty, host.LastOverlayMilliseconds);
                        parts?.Clear();
                    };
                    host.FixedStepStarting += () => gameContext?.BeginFixedStep();
                    host.VariableUpdateStarting += () => gameContext?.BeginVariableUpdate();
                    // Tick the audio system + network every variable frame.
                    host.FrameUpdate += dt =>
                    {
                        if (host.IsPlayPaused != _pauseRequested)
                        {
                            host.IsPlayPaused = _pauseRequested;
                            Console.WriteLine("GENESIS_PLAYER_STATE " + (host.IsPlayPaused ? "paused" : "running"));
                        }
                        if (_stopRequested) _activeWindow?.Close();
                        try
                        {
                            // Positioned sounds are heard from the 3D camera unless a script placed the listener.
                            if (_activeAudio != null && host.Scene != null && gameContext?.Room?.Dimension == RoomDimension.ThreeD
                                && !Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual)
                            {
                                var ear = host.Scene.Camera3D;
                                _activeAudio.SetListener(ear.Position, ear.Forward, ear.Right);
                            }
                        }
                        catch { }
                        try { _activeAudio?.Update(); } catch { }
                        try { _activeNet?.Update(); } catch { }
                        try
                        {
                            // A room that is half made is no place to create shared objects.
                            if (_replication != null && host.Scene?.World != null && host.Scene.RoomChange == null)
                            {
                                // Distance is measured from the 3D camera; a 2D room shares everything.
                                _replication.InterestEnabled = gameContext?.Room?.Dimension != RoomDimension.TwoD;
                                _replication.Update(host.Scene.World, host.Scene.Camera3D.Position, (float)dt);
                            }
                        }
                        catch (Exception replicationError)
                        {
                            logger.Line("network replication error: " + replicationError.Message);
                        }
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
                    if (benchmark != null && (!benchmark.Complete || benchmark.Error.Length > 0))
                        throw new InvalidOperationException("Benchmark did not complete cleanly: " + benchmark.Error);
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
                if (_activeAudio != null) Genesis.Shared.Diagnostics.DebugResourceCatalog.Unregister("sounds", _activeAudio);
                try { _activeAudio?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
                try { _activeNet?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
                Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = null;
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = null;
                Genesis.Runtime.Scripting.PgslCommands.GameQuitHandler = null;
                Genesis.Runtime.Modeling.RuntimeModelRenderSystem.BackgroundTextures = false;
                _activeAudio = null; _activeNet = null; _activeHost = null; _activeWindow = null;
                _stopRequested = false; _pauseRequested = false;
                PgslProfiler.Enabled = false;
            }
        }

        /// <summary>Records a profile for the whole run without debug mode (see <see cref="ProfileEnvironmentVariable"/>).</summary>
        public const string ProfileArgument = "--profile";

        /// <summary>Set to 1 for the same as <see cref="ProfileArgument"/>.</summary>
        public const string ProfileEnvironmentVariable = "GENESIS_PROFILE";

        private static bool ProfileRequested(string[] args)
        {
            if (Array.Exists(args, value => string.Equals(value, ProfileArgument, StringComparison.OrdinalIgnoreCase))) return true;
            string setting = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(setting) && setting.Trim() != "0"
                && !string.Equals(setting.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        private static double BenchmarkNumber(string[] args, string option, double fallback)
        {
            int index = Array.IndexOf(args, option);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || !double.TryParse(args[index + 1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)) throw new ArgumentException(option + " requires a finite number.");
            return value;
        }

        private sealed class GameLaunchSettings
        {
            public string Title { get; set; } = string.Empty;
            public string WindowMode { get; set; } = "Windowed";
            public string Icon { get; set; } = string.Empty;
            public bool AllowEscapeToClose { get; set; } = true;
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
            sb.AppendLine($"instancesDropped={stats.InstancesDropped}");
            if (scene != null)
            {
                foreach (ISceneSubsystem subsystem in scene.Subsystems)
                {
                    if (subsystem is Genesis.Runtime.Scene.RoomSceneryStreamer scenery)
                        sb.AppendLine($"scenery=loaded {scenery.Loaded} of {scenery.Total}");
                    if (subsystem is not RoomTerrainSubsystem terrain) continue;
                    (int streamedTerrains, int loadedTerrains) = terrain.StreamedTerrainCounts;
                    if (streamedTerrains > 0) sb.AppendLine($"terrainStreaming=loaded {loadedTerrains} of {streamedTerrains}");
                    sb.AppendLine($"scatterColliders={terrain.ScatterColliderCount}");
                    foreach (Genesis.World.Terrain.TerrainLodStatistics lod in terrain.TerrainLodStatistics)
                        sb.AppendLine($"terrainLod=nodes {lod.NodesDrawn} triangles {lod.TrianglesDrawn} resident {lod.MeshesResident} "
                            + $"pending {lod.BuildsPending} levels {lod.FinestLevelDrawn}-{lod.CoarsestLevelDrawn}");
                    foreach (TerrainScatterStatistics scatter in terrain.ScatterStatistics)
                        if (scatter.Layers > 0)
                            sb.AppendLine($"scatter=layers {scatter.Layers} cells {scatter.CellsWithCopies} copies {scatter.CopiesPlaced} "
                                + $"near {scatter.NearCopies} shadowed {scatter.ShadowCopies} dropped {scatter.DroppedCopies} "
                                + $"mergedDraws {scatter.MergedDrawCalls} mergedTriangles {scatter.MergedTriangles} "
                                + $"mergedMB {scatter.MergedBytes / 1048576.0:F1} pending {scatter.PendingWork}");
                }
            }

            sb.AppendLine($"modelCache=read {Genesis.Runtime.Modeling.ModelBinaryCache.Hits} written {Genesis.Runtime.Modeling.ModelBinaryCache.Writes}");
            sb.AppendLine($"modelReadAhead=started {Genesis.Runtime.Modeling.RuntimeModelStore.PrefetchesStarted} used {Genesis.Runtime.Modeling.RuntimeModelStore.PrefetchesUsed}");
            if (_replication != null)
                sb.AppendLine($"replication=shared {_replication.SharedCount} copies {_replication.CopyCount} owned {_replication.OwnedCount}");
            int[] modelLevels = Genesis.Runtime.Modeling.ModelLodView.LevelCounts;
            sb.AppendLine($"modelLod=full {modelLevels[0]} level1 {modelLevels[1]} level2 {modelLevels[2]} level3 {modelLevels[3]} "
                + $"tooSmall {Genesis.Runtime.Modeling.ModelLodView.CulledSmall}");
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

        /// <summary>Plays this input recording from the first frame of play, then closes the game: <c>--replay name-or-file</c>.</summary>
        public const string ReplayArgument = "--replay";

        /// <summary>Records the input from the first frame of play until the game closes: <c>--record name-or-file</c>.</summary>
        public const string RecordArgument = "--record";

        private static bool _quitWhenReplayEnds;

        private static void OnInputReplayEnded(bool ranToEnd)
        {
            // Escape hands the game to the person at the machine instead of closing it.
            if (_quitWhenReplayEnds && ranToEnd) RequestStop();
            _quitWhenReplayEnds = false;
        }

        /// <summary>
        /// An acceptance run plays a recording without any script asking for it: <c>--replay</c> (or
        /// GENESIS_INPUT_REPLAY) plays it from the first frame of play and closes the game when it
        /// ends; <c>--record</c> (or GENESIS_INPUT_RECORD) records until the game closes. A plain
        /// name is a file in the project's debug Replays folder; a path is used as it is.
        /// </summary>
        private static void StartInputReplayFromLaunch(string[] args, string projectPath, ProjectLogger logger)
        {
            string replay = ArgumentValue(args, ReplayArgument)
                ?? Environment.GetEnvironmentVariable(Genesis.Runtime.Input.InputReplay.ReplayEnvironmentVariable);
            string record = ArgumentValue(args, RecordArgument)
                ?? Environment.GetEnvironmentVariable(Genesis.Runtime.Input.InputReplay.RecordEnvironmentVariable);
            bool replaying = !string.IsNullOrWhiteSpace(replay);
            if (replaying && !string.IsNullOrWhiteSpace(record))
                throw new ArgumentException("Choose --replay or --record, not both.");
            string name = replaying ? replay : record;
            if (string.IsNullOrWhiteSpace(name)) return;

            string path = File.Exists(name) || name.IndexOfAny(new[] { '\\', '/' }) >= 0
                ? Path.GetFullPath(name)
                : Genesis.Runtime.Input.InputReplay.ResolvePath(projectPath, name);
            if (replaying)
            {
                if (!Genesis.Runtime.Input.InputReplay.StartReplay(path))
                    throw new ArgumentException("--replay: " + Genesis.Runtime.Input.InputReplay.LastError);
                _quitWhenReplayEnds = true;
                logger.Line($"replaying input from {path} ({Genesis.Runtime.Input.InputReplay.FrameCount} frames)");
            }
            else
            {
                if (!Genesis.Runtime.Input.InputReplay.StartRecording(path))
                    throw new ArgumentException("--record: " + Genesis.Runtime.Input.InputReplay.LastError);
                logger.Line($"recording input to {path}");
            }
        }

        private static string ArgumentValue(string[] args, string name)
        {
            int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(name + " needs a recording name or file.");
            return args[index + 1];
        }

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
        // The game's scripts as the entry-point check loaded them, for the game's own start to reuse.
        private static byte[] _scriptsBytes;
        private static Assembly _scriptsAssembly;
        private static string _scriptsPath;

        private static bool TryRunScriptEntryPoint(string[] args, out int exitCode)
        {
            exitCode = 0;
            string dllPath = RuntimePaths.PlayerGameScriptsDll();
            _scriptsBytes = null;
            _scriptsAssembly = null;
            _scriptsPath = null;
            if (!File.Exists(dllPath))
                return false;

            Assembly asm;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(dllPath);
                asm = Assembly.Load(bytes);
            }
            catch { return false; }
            // Kept for the game's own start, which loads the same scripts (see Run).
            _scriptsBytes = bytes;
            _scriptsAssembly = asm;
            _scriptsPath = dllPath;

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

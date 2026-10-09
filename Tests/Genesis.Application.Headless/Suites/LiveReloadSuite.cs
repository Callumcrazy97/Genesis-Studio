using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A game running from Studio while its files change on disk (live reload): the game's Scripts
/// must resolve before, during and after the reload, whether the room is rebuilt or the rebuild is
/// turned down, and a file a tool is still writing must not stop the game.
/// </summary>
internal static class LiveReloadSuite
{
    private const string StepSource = """
        steps += 1;
        LiveTick();
        GlobalSet("value", LiveValue());
        GlobalSet("steps", steps);
        """;

    private static string Library(int value) => "function LiveValue() { return " + value + "; }\n";

    /// <summary>A small game stepped a frame at a time: its scripts, the room switcher and the live-reload watcher.</summary>
    private sealed class LiveGame : IDisposable
    {
        private readonly IGameContext _previousGame = PgslCommands.ActiveGameContext;
        private readonly string _previousProject = PgslCommands.ProjectPath;
        public readonly RuntimeScene Scene = new("Live reload") { Input = new Genesis.Runtime.Input.InputState() };
        public readonly ScriptHostSystem Host = new();
        public readonly ProjectGameContext Context;
        public readonly ProjectRoomSwitcher Switcher;
        public readonly ProjectAssetLiveReloadSubsystem LiveReload;
        public readonly ProjectLogger Logger;
        public readonly List<string> Errors = new();

        public LiveGame(ProjectSession project)
        {
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            PgslCommands.ResetSession();
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);
            ScriptAssetRegistry.ClearCache();
            PgslCommands.ProjectPath = project.RootPath;
            Logger = new ProjectLogger(project.RootPath);
            Host.DiagnosticReported += diagnostic => Errors.Add(diagnostic.ToDisplayString());
            string startRoom = project.Manifest.StartRoom;
            RoomAsset room = RoomAssetLoader.Parse(ProjectRoomResolver.ResolveRoomFile(project.RootPath, startRoom));
            Context = new ProjectGameContext(project.RootPath, Scene, null, null, room, Logger);
            Host.SetContext(Context);
            PgslCommands.ActiveGameContext = Context;
            ProjectRoomLoader.Build(project.RootPath, Scene, room, Host, Context, beginGame: true);
            Scene.AddSubsystem(new ScriptHostSubsystem(Host));
            Switcher = Scene.AddSubsystem(new ProjectRoomSwitcher(project.RootPath, Context, Host, null, null, Logger, startRoom));
            LiveReload = Scene.AddSubsystem(new ProjectAssetLiveReloadSubsystem(project.RootPath, null, Switcher, Logger, debounceMilliseconds: 120));
        }

        public void Frame()
        {
            Scene.UpdateFixed(1f / 60f);
            Scene.UpdateVariable(1f / 60f);
            Scene.World.FlushDeferred();
        }

        public string Log()
        {
            string? path = Logger.LogPath;
            if (path == null || !File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>Steps frames until the condition holds, a little time apart so the watcher sees the files.</summary>
        public bool FramesUntil(Func<bool> condition, double seconds = 20)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                Frame();
                if (condition()) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        public void Dispose()
        {
            Host.Shutdown();
            Scene.Dispose();
            Logger.Dispose();
            PgslCommands.ActiveGameContext = _previousGame;
            PgslCommands.ProjectPath = _previousProject;
            ScriptAssetRegistry.ClearCache();
        }
    }

    private sealed class LiveProject
    {
        public required ProjectSession Session;
        public required string LibraryFile;
        public required string StepFile;
        public required string RoomFile;
    }

    private static LiveProject Build(HeadlessContext ctx)
    {
        string parent = Path.Combine(ctx.Workspace, "LiveReload");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Live" + Guid.NewGuid().ToString("N")[..6], "Blank");
        ResourceService resources = new(project);
        string scripts = Path.Combine(project.AssetsPath, "Scripts");
        Directory.CreateDirectory(scripts);
        string library = Path.Combine(scripts, "LiveLibrary.pgsl");
        File.WriteAllText(library, Library(1));
        // A Script called by its name, as a game calls its menus and its input.
        File.WriteAllText(Path.Combine(scripts, "LiveTick.pgsl"), "GlobalSet(\"ticks\", GlobalGet(\"ticks\") + 1);\n");

        string objects = Path.Combine(project.AssetsPath, "Objects");
        Directory.CreateDirectory(objects);
        string objectFile = resources.CreateResource(objects, ResourceKind.GameObject, "Game");
        File.WriteAllText(objectFile, new JsonObject
        {
            ["schemaVersion"] = 2, ["dimension"] = "TwoD",
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent", ["props"] = new JsonObject { ["ScriptClass"] = "Game" },
            }),
            ["events"] = new JsonArray("Create", "Step"),
        }.ToJsonString());
        string events = Path.Combine(objects, "Game");
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, "Create.pgsl"), "steps = 0;\n");
        string step = Path.Combine(events, "Step.pgsl");
        File.WriteAllText(step, StepSource);

        string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
        RoomAsset start = RoomAssetLoader.Parse(roomFile);
        start.Settings.CaptureMouse = false;
        start.Nodes.Add(new RoomNode
        {
            Kind = RoomNodeKind.GameObject, Name = "Game", LayerId = start.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, objectFile).Replace('\\', '/') },
        });
        RoomAssetLoader.Save(start, roomFile);
        Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);
        return new LiveProject { Session = project, LibraryFile = library, StepFile = step, RoomFile = roomFile };
    }

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "LiveReload");

        HeadlessHarness.RunCase(ctx.Report, "Engine.LiveReload.ScriptsResolveThroughARoomRebuild", () =>
        {
            LiveProject project = Build(ctx);
            using var game = new LiveGame(project.Session);
            for (int i = 0; i < 3; i++) game.Frame();
            HeadlessHarness.Assert(PgslCommands.GlobalGet("value") == 1 && PgslCommands.GlobalGet("steps") == 3 && PgslCommands.GlobalGet("ticks") == 3,
                $"The game did not run its library before any change: value {PgslCommands.GlobalGet("value")}, steps {PgslCommands.GlobalGet("steps")}, "
                + $"errors: {string.Join("; ", game.Errors)}");

            // A tool rewrites a library Script and an Object's event while the game runs.
            File.WriteAllText(project.LibraryFile, Library(2));
            File.AppendAllText(project.StepFile, "// edited while the game ran\n");
            bool applied = game.FramesUntil(() => game.LiveReload.AppliedGeneration > 0 && !game.Switcher.Changing
                && game.Log().Contains("AssetLiveReload applied", StringComparison.Ordinal));
            HeadlessHarness.Assert(applied, "The change was never applied. Log:\r\n" + Tail(game.Log()));
            double ticks = PgslCommands.GlobalGet("ticks");
            for (int i = 0; i < 3; i++) game.Frame();
            HeadlessHarness.Assert(game.Errors.Count == 0,
                "Scripts failed during or after the reload: " + string.Join("; ", game.Errors) + "\r\nLog:\r\n" + Tail(game.Log()));
            HeadlessHarness.Assert(PgslCommands.GlobalGet("value") == 2 && PgslCommands.GlobalGet("ticks") == ticks + 3,
                $"After the reload the game does not run the new library ({PgslCommands.GlobalGet("value")}) or its Scripts ({PgslCommands.GlobalGet("ticks")} ticks, {ticks} before).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.LiveReload.AHalfWrittenFileLeavesTheGameRunning", () =>
        {
            LiveProject project = Build(ctx);
            using var game = new LiveGame(project.Session);
            for (int i = 0; i < 3; i++) game.Frame();

            // An event cut off part way, as a tool leaves it while still writing.
            File.WriteAllText(project.StepFile, "steps += 1;\nif (steps > 1) {\n    LiveTick(");
            bool rejected = game.FramesUntil(() => game.Log().Contains("AssetLiveReload rejected", StringComparison.Ordinal));
            HeadlessHarness.Assert(rejected, "The cut-off event was not turned down. Log:\r\n" + Tail(game.Log()));
            double steps = PgslCommands.GlobalGet("steps");
            for (int i = 0; i < 3; i++) game.Frame();
            HeadlessHarness.Assert(game.Errors.Count == 0 && game.LiveReload.AppliedGeneration == 0 && PgslCommands.GlobalGet("steps") == steps + 3,
                $"A cut-off event stopped the game: steps {PgslCommands.GlobalGet("steps")} after {steps}, errors: {string.Join("; ", game.Errors)}");

            // A library cut off part way: turned down too; the game goes on with the one it has.
            File.WriteAllText(project.LibraryFile, "function LiveValue() { return ");
            int rejections = Count(game.Log(), "AssetLiveReload rejected");
            HeadlessHarness.Assert(game.FramesUntil(() => Count(game.Log(), "AssetLiveReload rejected") > rejections),
                "The cut-off library was not turned down. Log:\r\n" + Tail(game.Log()));
            for (int i = 0; i < 3; i++) game.Frame();
            HeadlessHarness.Assert(game.Errors.Count == 0 && PgslCommands.GlobalGet("value") == 1,
                $"A cut-off library stopped the game: value {PgslCommands.GlobalGet("value")}, errors: {string.Join("; ", game.Errors)}");

            // The tool finishes both files: the change applies.
            File.WriteAllText(project.LibraryFile, Library(3));
            File.WriteAllText(project.StepFile, StepSource);
            bool applied = game.FramesUntil(() => game.LiveReload.AppliedGeneration > 0 && !game.Switcher.Changing && PgslCommands.GlobalGet("value") == 3);
            HeadlessHarness.Assert(applied && game.Errors.Count == 0,
                $"The finished files did not apply cleanly (value {PgslCommands.GlobalGet("value")}): {string.Join("; ", game.Errors)}\r\nLog:\r\n{Tail(game.Log())}");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.LiveReload.ScriptsResolveWhenTheRebuildIsTurnedDown", () =>
        {
            LiveProject project = Build(ctx);
            using var game = new LiveGame(project.Session);
            for (int i = 0; i < 3; i++) game.Frame();
            string room = File.ReadAllText(project.RoomFile);

            // The room is mid-rewrite (whole JSON, not yet a room) as a library Script changes: the
            // room cannot be rebuilt, and the game must go on running with Scripts that resolve.
            // They used to be dropped here and never read again: every call was an unknown command.
            File.WriteAllText(project.RoomFile, "{ \"note\": \"being written\" }");
            File.WriteAllText(project.LibraryFile, Library(2));
            bool turnedDown = game.FramesUntil(() => game.LiveReload.AppliedGeneration > 0 && !game.Switcher.Changing
                && game.Log().Contains("AssetLiveReload rejected", StringComparison.Ordinal));
            HeadlessHarness.Assert(turnedDown, "The rebuild from a room that is not a room yet was not turned down. Log:\r\n" + Tail(game.Log()));
            double steps = PgslCommands.GlobalGet("steps"), ticks = PgslCommands.GlobalGet("ticks");
            for (int i = 0; i < 5; i++) game.Frame();
            HeadlessHarness.Assert(game.Errors.Count == 0,
                "Scripts stopped resolving when the rebuild was turned down: " + string.Join("; ", game.Errors));
            HeadlessHarness.Assert(PgslCommands.GlobalGet("steps") == steps + 5 && PgslCommands.GlobalGet("ticks") == ticks + 5
                && PgslCommands.GlobalGet("value") is 1 or 2,
                $"The game did not keep running as it was: steps {PgslCommands.GlobalGet("steps")} after {steps}, ticks {PgslCommands.GlobalGet("ticks")} after {ticks}.");

            // The room is written in full: the rebuild goes ahead with the new library.
            File.WriteAllText(project.RoomFile, room);
            bool applied = game.FramesUntil(() => !game.Switcher.Changing && game.Log().Contains("AssetLiveReload applied", StringComparison.Ordinal)
                && PgslCommands.GlobalGet("value") == 2);
            HeadlessHarness.Assert(applied && game.Errors.Count == 0,
                $"The finished room did not rebuild cleanly: {string.Join("; ", game.Errors)}\r\nLog:\r\n{Tail(game.Log())}");
        });
    }

    private static int Count(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Tail(string log) => log.Length > 2500 ? log[^2500..] : log;
}

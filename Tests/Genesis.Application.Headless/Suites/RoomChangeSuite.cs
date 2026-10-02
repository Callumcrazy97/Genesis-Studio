using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Runtime;
using Genesis.Physics;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;
using Genesis.World.Terrain;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless;

/// <summary>Writes down what the engine asks of an object placed in a room, so a check can say what ran and when.</summary>
public sealed class RoomChangeProbe : EntityBehavior
{
    public static int Steps, Created, RoomStarts, LoadingScreensAsked;
    /// <summary>How many objects existed when each Create event ran.</summary>
    public static readonly List<int> PlacedAtCreate = new();

    public static void Reset()
    {
        Steps = Created = RoomStarts = LoadingScreensAsked = 0;
        PlacedAtCreate.Clear();
    }

    public override void OnCreate()
    {
        Created++;
        int placed = 0;
        World.Query<TransformComponent>((Genesis.Shared.ECS.Entity _, ref TransformComponent _) => placed++);
        PlacedAtCreate.Add(placed);
    }

    public override void OnRoomStart() => RoomStarts++;
    public override void OnUpdate(float dt) => Steps++;

    public override bool OnDrawLoadingScreen(IHudCanvas hud, float progress)
    {
        LoadingScreensAsked++;
        return false;
    }
}

/// <summary>An object that outlives room changes and may draw the game's own loading screen.</summary>
public sealed class RoomChangeKeeper : EntityBehavior
{
    public static bool DrawsLoadingScreen;
    public static int Steps, Drawn;
    public static float LastProgress = -1f;

    public static void Reset()
    {
        DrawsLoadingScreen = false;
        Steps = Drawn = 0;
        LastProgress = -1f;
    }

    public override void OnUpdate(float dt) => Steps++;

    public override bool OnDrawLoadingScreen(IHudCanvas hud, float progress)
    {
        if (!DrawsLoadingScreen) return false;
        Drawn++;
        LastProgress = progress;
        hud.Rect(0, 0, 10, 10, Vector4.One);
        return true;
    }
}

/// <summary>
/// Room changes that do not hold the game still: the change spread over frames behind a cover,
/// the loading screen, and the collision that is now built on worker threads.
/// </summary>
internal static class RoomChangeSuite
{
    private sealed class RecordingHud : IHudCanvas
    {
        public readonly List<(float X, float Y, float W, float H, Vector4 Color)> Rects = new();
        public readonly List<string> Texts = new();
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Texts.Add(text);
        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) => Texts.Add(text);
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => Rects.Add((x, y, w, h, color));
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) { }
    }

    /// <summary>Counts the sounds it is asked to read and to play.</summary>
    private sealed class SoundCounter : Genesis.Shared.Audio.IAudioSystem
    {
        public readonly List<string> Loaded = new();
        public int Played;
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string path) { Loaded.Add(path); return Loaded.Count; }
        public Genesis.Shared.Audio.AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Played++;
            return new Genesis.Shared.Audio.AudioChannel(Played);
        }

        public void Stop(Genesis.Shared.Audio.AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(Genesis.Shared.Audio.AudioChannel channel) => true;
        public void SetChannelVolume(Genesis.Shared.Audio.AudioChannel channel, float volume) { }
        public void SetChannelPosition(Genesis.Shared.Audio.AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }

    private sealed class DrawList : IMeshDrawList
    {
        public readonly List<MeshDrawCall> Calls = new();
        public int Count => Calls.Count;
        public void Clear() => Calls.Clear();
        public void Add(in MeshDrawCall call) => Calls.Add(call);
        public int CopyTo(MeshDrawCall[] buffer, int startIndex)
        {
            Calls.CopyTo(buffer, startIndex);
            return startIndex + Calls.Count;
        }
    }

    /// <summary>A software renderer on a hidden window: real texture loading, no graphics card needed.</summary>
    private sealed class SoftwareRenderer : IDisposable
    {
        public readonly Form Host = UnattendedWindowing.NewHost(320, 240);
        public readonly IRenderController Renderer = RenderControllerFactory.Create(RenderBackendOption.Software);

        public SoftwareRenderer()
        {
            UnattendedWindowing.ShowWithoutFocus(Host);
            Renderer.Initialize(Host.Handle, 320, 240);
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Host.Dispose();
        }
    }

    private static void WritePicture(string file, Color colour, int size = 256)
    {
        using Bitmap pixels = new(size, size);
        using (Graphics graphics = Graphics.FromImage(pixels)) graphics.Clear(colour);
        pixels.Save(file, ImageFormat.Png);
    }

    /// <summary>Saves a one-square model whose material uses an Image of the same project, and returns the Image's name.</summary>
    private static string TexturedModel(ProjectSession project, string modelName, Color colour)
    {
        var resources = new ResourceService(project);
        string descriptor = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, modelName + " Skin");
        // A new image resource has no pixels until the editor saves; give it a frame beside the descriptor.
        string stem = Path.GetFileName(descriptor)[..^".image.json".Length];
        WritePicture(Path.Combine(Path.GetDirectoryName(descriptor)!, stem + ".png"), colour);
        ResourceNames.Invalidate(project.RootPath);
        string image = ResourceNames.Name(project.RootPath, descriptor, ResourceType.Image);

        var asset = new GModelAsset { Name = modelName };
        asset.Materials.Add(new GModelMaterial { Name = "Skin", BaseColor = Vector4.One, AlbedoTexture = image });
        MeshVertex Corner(float x, float z) => new()
        {
            Position = new Vector3(x, 0f, z), Normal = Vector3.UnitY, Color = Vector4.One, UV = new Vector2(x + 0.5f, z + 0.5f),
        };
        asset.Meshes.Add(new GModelMesh
        {
            Name = "Square", Vertices = [Corner(-0.5f, -0.5f), Corner(0.5f, -0.5f), Corner(0.5f, 0.5f), Corner(-0.5f, 0.5f)],
            Indices = [0, 1, 2, 0, 2, 3],
        });
        asset.RecalculateBounds();
        RuntimeModelStore.SaveByName(project.RootPath, modelName, asset);
        return image;
    }

    /// <summary>A small game: a scene, its scripts and the subsystem that changes rooms, stepped a frame at a time.</summary>
    private sealed class Game : IDisposable
    {
        private readonly IGameContext _previousGame = PgslCommands.ActiveGameContext;
        public readonly RuntimeScene Scene = new("Room change") { Input = new Genesis.Runtime.Input.InputState() };
        public readonly ScriptHostSystem Host = new();
        public readonly ProjectGameContext Context;
        public readonly ProjectRoomSwitcher Switcher;

        public Game(ProjectSession project, string startRoom, double frameBudgetMilliseconds)
        {
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            RoomChangeScreen.Reset();
            RoomChangeProbe.Reset();
            RoomChangeKeeper.Reset();
            RoomAsset room = RoomAssetLoader.Parse(ProjectRoomResolver.ResolveRoomFile(project.RootPath, startRoom));
            Context = new ProjectGameContext(project.RootPath, Scene, null, null, room, null);
            Host.LoadAssembly(typeof(RoomChangeProbe).Assembly);
            Host.SetContext(Context);
            PgslCommands.ActiveGameContext = Context;
            ProjectRoomLoader.Build(project.RootPath, Scene, room, Host, Context, beginGame: true);
            Scene.AddSubsystem(new ScriptHostSubsystem(Host));
            Switcher = Scene.AddSubsystem(new ProjectRoomSwitcher(project.RootPath, Context, Host, null, null, null, startRoom));
            Switcher.FrameBudgetMilliseconds = frameBudgetMilliseconds;
        }

        public void Frame()
        {
            Scene.UpdateFixed(1f / 60f);
            Scene.UpdateVariable(1f / 60f);
            Scene.World.FlushDeferred();
        }

        public int Objects()
        {
            int count = 0;
            Scene.World.Query<TransformComponent>((Genesis.Shared.ECS.Entity _, ref TransformComponent _) => count++);
            return count;
        }

        public void Dispose()
        {
            Host.Shutdown();
            Scene.Dispose();
            PgslCommands.ActiveGameContext = _previousGame;
            RoomChangeScreen.Reset();
        }
    }

    /// <summary>A project with a keeper that outlives rooms and two rooms of scripted objects.</summary>
    private static ProjectSession Project(HeadlessContext context, RoomDimension dimension, int hallObjects, int cellarObjects)
    {
        string parent = Path.Combine(context.Workspace, "RoomChangeProjects");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Rooms" + Guid.NewGuid().ToString("N")[..6], "Blank");
        var resources = new ResourceService(project);
        string kind = dimension == RoomDimension.ThreeD ? "ThreeD" : "TwoD";
        string objects = ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject);
        File.WriteAllText(resources.CreateResource(objects, ResourceKind.GameObject, "Probe"),
            "{\"schemaVersion\":2,\"dimension\":\"" + kind + "\",\"model\":\"\",\"components\":[{\"type\":\"ScriptComponent\",\"props\":{\"ScriptClass\":\"RoomChangeProbe\"}}],\"events\":[]}");
        File.WriteAllText(resources.CreateResource(objects, ResourceKind.GameObject, "Keeper"),
            "{\"schemaVersion\":2,\"dimension\":\"" + kind + "\",\"model\":\"\",\"persistent\":true,\"components\":[{\"type\":\"ScriptComponent\",\"props\":{\"ScriptClass\":\"RoomChangeKeeper\"}}],\"events\":[]}");

        string rooms = ResourceFolderPolicy.RootFor(project, ResourceKind.Room);
        void Room(string name, int count, bool keeper)
        {
            RoomAsset room = RoomAsset.Create(name, dimension);
            void Place(string prefab, int index) => room.Nodes.Add(new RoomNode
            {
                Name = prefab + " " + index, Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                EnabledIn2D = dimension == RoomDimension.TwoD, EnabledIn3D = dimension == RoomDimension.ThreeD, Order = room.Nodes.Count,
                Transform = new RoomTransform { X = index * 4f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
                GameObject = new RoomGameObjectData { Prefab = prefab },
            });
            if (keeper) Place("Keeper", 0);
            for (int i = 0; i < count; i++) Place("Probe", i + 1);
            RoomAssetLoader.Save(room, resources.CreateResource(rooms, ResourceKind.Room, name));
        }

        Room("Hall", hallObjects, keeper: true);
        Room("Cellar", cellarObjects, keeper: false);
        ResourceNames.Invalidate(project.RootPath);
        return project;
    }

    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.Change.IsSpreadOverFramesAndNothingRunsMeanwhile", () =>
        {
            ProjectSession project = Project(context, RoomDimension.ThreeD, hallObjects: 12, cellarObjects: 40);
            // A budget this small gives up the frame after every piece, so the change takes as many frames as it has pieces.
            using var game = new Game(project, "Hall", frameBudgetMilliseconds: 0.0001);
            for (int i = 0; i < 3; i++) game.Frame();
            HeadlessHarness.Assert(RoomChangeProbe.Created == 12 && RoomChangeProbe.Steps == 36 && RoomChangeKeeper.Steps == 3 && game.Objects() == 13,
                $"The first room did not start as expected: {RoomChangeProbe.Created} created, {RoomChangeProbe.Steps} steps, {game.Objects()} objects.");
            HeadlessHarness.Assert(game.Scene.RoomChange == null && !game.Context.IsChangingRoom && game.Context.RoomLoadProgress == 1f,
                "A game that has asked for no room change reports one under way.");

            RoomChangeProbe.Reset();
            RoomChangeKeeper.Reset();
            game.Context.ChangeRoom("Cellar");
            game.Frame();
            RoomChangeProgress change = game.Scene.RoomChange
                ?? throw new InvalidOperationException("The room change finished, or was not held, in the frame it was asked for.");
            HeadlessHarness.Assert(!change.RoomBuilt && change.RoomName == "Cellar" && game.Switcher.Changing && game.Context.IsChangingRoom,
                "The held room change does not say which room it is for, or claims the room is already built.");
            HeadlessHarness.Assert(RoomChangeProbe.Created == 0 && game.Objects() <= 2,
                $"The old room's objects were not removed before the frame was given up ({game.Objects()} remain).");
            // The old room's scripts had their step in that frame, before the change began. Nothing steps from here.
            RoomChangeProbe.Reset();
            RoomChangeKeeper.Reset();

            int frames = 0, framesBuilt = 0, createdInOneFrame = 0, startsBeforeBuilt = -1;
            float last = change.Progress;
            while (game.Scene.RoomChange != null && frames < 5000)
            {
                int created = RoomChangeProbe.Created;
                bool wasBuilt = change.RoomBuilt;
                game.Frame();
                frames++;
                HeadlessHarness.Assert(change.Progress >= last, $"Progress went backwards, from {last} to {change.Progress}.");
                HeadlessHarness.Assert(game.Context.RoomLoadProgress == (game.Scene.RoomChange == null ? 1f : change.Progress),
                    "RoomLoadProgress does not follow the room change.");
                last = change.Progress;
                createdInOneFrame = Math.Max(createdInOneFrame, RoomChangeProbe.Created - created);
                if (change.RoomBuilt)
                {
                    if (!wasBuilt) startsBeforeBuilt = RoomChangeProbe.RoomStarts;
                    framesBuilt++;
                }

                HeadlessHarness.Assert(RoomChangeProbe.Steps == 0 && RoomChangeKeeper.Steps == 0,
                    $"Scripts were stepped while the room was being changed (frame {frames}).");
            }

            HeadlessHarness.Assert(game.Scene.RoomChange == null && !game.Switcher.Changing, "The room change never finished.");
            HeadlessHarness.Assert(frames >= 40, $"A change of 40 objects with every piece in its own frame took only {frames} frames.");
            // Create events run together, after every object is in place: each saw all 40 and the keeper.
            HeadlessHarness.Assert(RoomChangeProbe.Created == 40 && createdInOneFrame == 40 && RoomChangeProbe.PlacedAtCreate.All(placed => placed == 41),
                $"Create events did not run together once the room was whole: {RoomChangeProbe.Created} ran, {createdInOneFrame} in one frame, "
                + $"seeing {RoomChangeProbe.PlacedAtCreate.DefaultIfEmpty().Min()} to {RoomChangeProbe.PlacedAtCreate.DefaultIfEmpty().Max()} objects.");
            HeadlessHarness.Assert(startsBeforeBuilt == 40, $"Room-start events had run for {startsBeforeBuilt} objects when the room was first drawn behind its cover; all 40 should have.");
            HeadlessHarness.Assert(framesBuilt >= game.Switcher.WarmUpFrames,
                $"The finished room was drawn behind its cover for {framesBuilt} frames; at least {game.Switcher.WarmUpFrames} are asked for.");
            HeadlessHarness.Assert(game.Context.Room?.Name == "Cellar" && game.Objects() == 41 && change.Progress == 1f,
                $"The game is in '{game.Context.Room?.Name}' with {game.Objects()} objects after the change.");
            HeadlessHarness.Assert(ReferenceEquals(game.Scene.RoomReveal, change) && change.RevealSeconds > 0f && change.RevealRemaining == change.RevealSeconds,
                "The cover was not left to fade from the new room.");

            game.Frame();
            HeadlessHarness.Assert(RoomChangeProbe.Steps == 40 && RoomChangeKeeper.Steps == 1, "The new room did not start running once the change finished.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.Change.WithoutABudgetIsOneStepAndAQuickTwoDRoomHasNoCover", () =>
        {
            ProjectSession project = Project(context, RoomDimension.ThreeD, hallObjects: 4, cellarObjects: 6);
            using (var game = new Game(project, "Hall", frameBudgetMilliseconds: 0))
            {
                game.Frame();
                game.Context.ChangeRoom("Cellar");
                game.Frame();
                HeadlessHarness.Assert(game.Context.Room?.Name == "Cellar" && game.Scene.RoomChange == null && game.Scene.RoomReveal == null
                    && game.Objects() == 7 && RoomChangeProbe.Created == 10,
                    "Without a frame budget a room change should finish in the step it is asked for, with no cover.");

                // A script may ask for the same thing in a game that spreads its changes.
                game.Switcher.FrameBudgetMilliseconds = 0.0001;
                PgslCommands.RoomChangeBudget(0);
                game.Context.ChangeRoom("Hall");
                game.Frame();
                HeadlessHarness.Assert(game.Context.Room?.Name == "Hall" && game.Scene.RoomChange == null && game.Scene.RoomReveal == null,
                    "RoomChangeBudget(0) should make room changes happen in one step.");
            }

            // A 2D room that is ready within the grace time simply appears; one that is not is covered.
            ProjectSession flat = Project(context, RoomDimension.TwoD, hallObjects: 4, cellarObjects: 6);
            using (var game = new Game(flat, "Hall", frameBudgetMilliseconds: 0.0001))
            {
                RoomChangeScreen.GraceMilliseconds = 600_000;
                game.Frame();
                game.Context.ChangeRoom("Cellar");
                game.Frame();
                HeadlessHarness.Assert(game.Context.Room?.Name == "Cellar" && game.Scene.RoomChange == null && game.Scene.RoomReveal == null,
                    "A 2D room that was ready within the grace time was put behind a cover.");

                RoomChangeScreen.GraceMilliseconds = 0;
                game.Context.ChangeRoom("Hall");
                game.Frame();
                HeadlessHarness.Assert(game.Scene.RoomChange != null, "A 2D room that took longer than the grace time was not covered.");
                for (int i = 0; i < 2000 && game.Scene.RoomChange != null; i++) game.Frame();
                HeadlessHarness.Assert(game.Scene.RoomChange == null && game.Context.Room?.Name == "Hall", "The covered 2D room change never finished.");
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.Change.TheFirstRoomIsPreparedBehindTheCoverToo", () =>
        {
            ProjectSession project = Project(context, RoomDimension.ThreeD, hallObjects: 5, cellarObjects: 2);
            using (var game = new Game(project, "Hall", frameBudgetMilliseconds: 8))
            {
                RoomAsset room = game.Context.Room ?? throw new InvalidOperationException("The game has no room.");
                HeadlessHarness.Assert(game.Switcher.PrepareFirstRoom(game.Scene, room), "A 3D game with a frame budget did not prepare its first room.");
                RoomChangeProgress first = game.Scene.RoomChange
                    ?? throw new InvalidOperationException("Preparing the first room did not hold the scene.");
                HeadlessHarness.Assert(first.RoomBuilt && first.RoomName == "Hall" && game.Context.IsChangingRoom,
                    "The first room should be held as a room that is whole and waiting to be shown.");
                int frames = 0;
                while (game.Scene.RoomChange != null && frames < 2000)
                {
                    game.Frame();
                    frames++;
                    HeadlessHarness.Assert(RoomChangeProbe.Steps == 0 && RoomChangeKeeper.Steps == 0, "Scripts were stepped while the first room waited behind its cover.");
                }

                HeadlessHarness.Assert(game.Scene.RoomChange == null && frames >= game.Switcher.WarmUpFrames && ReferenceEquals(game.Scene.RoomReveal, first),
                    $"The first room was shown after {frames} frames; it should wait at least {game.Switcher.WarmUpFrames} and then fade in.");
                game.Frame();
                HeadlessHarness.Assert(RoomChangeProbe.Steps == 5 && RoomChangeKeeper.Steps == 1 && RoomChangeProbe.Created == 5,
                    "The first room did not start running once it was shown, or its objects were created twice.");
                HeadlessHarness.Assert(!game.Switcher.PrepareFirstRoom(game.Scene, room) || game.Scene.RoomChange != null,
                    "Preparing a room again must either hold the scene or refuse.");
                for (int i = 0; i < 2000 && game.Scene.RoomChange != null; i++) game.Frame();
            }

            // Not for a game that changes room in one step, and not for a 2D room.
            using (var game = new Game(project, "Hall", frameBudgetMilliseconds: 0))
                HeadlessHarness.Assert(!game.Switcher.PrepareFirstRoom(game.Scene, game.Context.Room!) && game.Scene.RoomChange == null,
                    "A game with no frame budget was put behind a cover at its start.");
            ProjectSession flat = Project(context, RoomDimension.TwoD, hallObjects: 2, cellarObjects: 2);
            using (var game = new Game(flat, "Hall", frameBudgetMilliseconds: 8))
                HeadlessHarness.Assert(!game.Switcher.PrepareFirstRoom(game.Scene, game.Context.Room!) && game.Scene.RoomChange == null,
                    "A 2D game was put behind a cover at its start.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.Change.LoadingScreenIsTheGamesOwnOrTheEngines", () =>
        {
            RoomChangeScreen.Reset();
            try
            {
                // The engine's own: a line of words and a bar whose filled part follows the progress.
                var hud = new RecordingHud();
                RoomChangeScreen.Draw(hud, new RoomChangeProgress("Cellar", 0.25f));
                HeadlessHarness.Assert(hud.Texts.SequenceEqual(["Loading"]) && hud.Rects.Count == 2
                    && MathF.Abs(hud.Rects[1].W - hud.Rects[0].W * 0.25f) < 0.01f && hud.Rects[1].X == hud.Rects[0].X
                    && hud.Rects[0].X > 0 && hud.Rects[0].X + hud.Rects[0].W < hud.Width && hud.Rects[0].Y > hud.Height * 0.5f,
                    "The engine's loading screen is not a caption and a bar a quarter full.");

                PgslCommands.RoomChangeText("");
                PgslCommands.RoomChangeColors(0.1, 0.2, 0.3, 1, 0.5, 0);
                hud = new RecordingHud();
                RoomChangeScreen.Draw(hud, new RoomChangeProgress("Cellar", 1f));
                HeadlessHarness.Assert(hud.Texts.Count == 0 && hud.Rects[1].W == hud.Rects[0].W && hud.Rects[1].Color == new Vector4(1f, 0.5f, 0f, 1f)
                    && RoomChangeScreen.Background == new Vector4(0.1f, 0.2f, 0.3f, 1f),
                    "The loading screen's text and colours did not follow the script's settings.");

                PgslCommands.RoomChangeProgressBar(false);
                hud = new RecordingHud();
                RoomChangeScreen.Draw(hud, new RoomChangeProgress("Cellar", 0.5f));
                HeadlessHarness.Assert(hud.Rects.Count == 0 && hud.Texts.Count == 0, "The engine drew its bar after being told not to.");

                PgslCommands.RoomChangeFade(0.75);
                PgslCommands.RoomChangeMinimumTime(2);
                HeadlessHarness.Assert(RoomChangeScreen.FadeSeconds == 0.75f && RoomChangeScreen.MinimumSeconds == 2f, "The fade and minimum times were not set.");

                // A painter draws the game's own screen with no Object to ask, and the engine then leaves its bar out.
                PgslCommands.RoomChangeProgressBar(true);
                float painted = -1f;
                RoomChangeScreen.Painter = (canvas, progress) =>
                {
                    painted = progress;
                    canvas.Rect(1, 2, 3, 4, Vector4.One);
                    return true;
                };
                hud = new RecordingHud();
                RoomChangeScreen.DrawLoadingScreen(hud, new RoomChangeProgress("Cellar", 0.35f));
                HeadlessHarness.Assert(painted == 0.35f && hud.Rects.Count == 1 && hud.Rects[0].W == 3f,
                    $"A painter that drew the loading screen should be all that is drawn; {hud.Rects.Count} rectangles were, with progress {painted}.");
                RoomChangeScreen.Painter = (_, _) => false;
                hud = new RecordingHud();
                RoomChangeScreen.DrawLoadingScreen(hud, new RoomChangeProgress("Cellar", 0.35f));
                HeadlessHarness.Assert(hud.Rects.Count == 2, "A painter that drew nothing should leave the engine's bar in place.");
                RoomChangeScreen.Reset();
                HeadlessHarness.Assert(RoomChangeScreen.Painter == null, "Resetting the loading screen left a painter behind.");
            }
            finally
            {
                RoomChangeScreen.Reset();
            }

            // The game's own: a behaviour that says it has drawn the screen. Objects whose Create has not run are not asked.
            RoomChangeProbe.Reset();
            RoomChangeKeeper.Reset();
            using var world = new EcsWorld();
            var host = new ScriptHostSystem();
            host.LoadAssembly(typeof(RoomChangeProbe).Assembly);
            host.Attach(world, world.CreateEntity(), nameof(RoomChangeKeeper));
            var screen = new RecordingHud();
            HeadlessHarness.Assert(!host.DispatchDrawLoadingScreen(screen, 0.4f) && screen.Rects.Count == 0,
                "A game that draws no loading screen was reported as drawing one.");
            RoomChangeKeeper.DrawsLoadingScreen = true;
            HeadlessHarness.Assert(host.DispatchDrawLoadingScreen(screen, 0.4f) && RoomChangeKeeper.LastProgress == 0.4f && screen.Rects.Count == 1,
                "The game's own loading screen was not drawn, or was not given the progress.");

            host.DeferCreateEvents = true;
            host.Attach(world, world.CreateEntity(), nameof(RoomChangeProbe));
            host.DispatchDrawLoadingScreen(screen, 0.5f);
            HeadlessHarness.Assert(RoomChangeProbe.LoadingScreensAsked == 0, "An object whose Create event has not run was asked to draw the loading screen.");
            host.DeferCreateEvents = false;
            host.FlushDeferredCreates();
            host.DispatchDrawLoadingScreen(screen, 0.6f);
            HeadlessHarness.Assert(RoomChangeProbe.LoadingScreensAsked == 1 && RoomChangeKeeper.LastProgress == 0.6f, "A created object was not asked to draw the loading screen.");
            host.Shutdown();
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.Change.CoverHidesTheRoomOnAllBackends", () =>
        {
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            RoomChangeScreen.Reset();
            try
            {
                foreach ((RenderBackendOption backend, string name) in new[]
                {
                    (RenderBackendOption.SilkNetDx11, "dx11"), (RenderBackendOption.Direct3D12, "dx12"),
                    (RenderBackendOption.Vulkan, "vulkan"), (RenderBackendOption.OpenGL, "opengl"),
                })
                {
                    RenderBackendSelection.Configure(backend);
                    using RuntimeViewportHarness harness = new();
                    string full = Path.Combine(context.Captures, $"room-change-{name}-cover.png");
                    harness.CaptureRoomChangeScreen(full, progress: 0.5f, strength: 1f);
                    using (Bitmap image = new(full))
                    {
                        // The room behind is a red wall: at full strength none of it may show.
                        for (int y = 8; y < image.Height; y += image.Height / 12)
                        for (int x = 8; x < image.Width; x += image.Width / 16)
                        {
                            Color pixel = image.GetPixel(x, y);
                            HeadlessHarness.Assert(pixel.R - Math.Max(pixel.G, pixel.B) < 40,
                                $"{backend}: the room shows through the cover at {x},{y} ({pixel.R},{pixel.G},{pixel.B}).");
                        }

                        Color corner = image.GetPixel(12, 12);
                        HeadlessHarness.Assert(corner.R < 48 && corner.G < 48 && corner.B < 48, $"{backend}: the cover is not dark ({corner.R},{corner.G},{corner.B}).");

                        // The bar: half full, so bright left of the middle and dim right of it.
                        float barWidth = MathF.Min(420f, image.Width * 0.5f), barHeight = MathF.Max(3f, image.Height / 240f);
                        int barY = (int)MathF.Round(image.Height * 0.84f + barHeight * 0.5f);
                        Color filled = image.GetPixel((int)(image.Width * 0.5f - barWidth * 0.25f), barY);
                        Color empty = image.GetPixel((int)(image.Width * 0.5f + barWidth * 0.25f), barY);
                        HeadlessHarness.Assert(filled.G > 150 && empty.G < 110 && empty.G > corner.G,
                            $"{backend}: the progress bar is not half full (filled part {filled.G}, empty part {empty.G}, cover {corner.G}).");
                    }

                    string part = Path.Combine(context.Captures, $"room-change-{name}-fading.png");
                    harness.CaptureRoomChangeScreen(part, progress: 1f, strength: 0.5f);
                    using Bitmap fading = new(part);
                    Color middle = fading.GetPixel(fading.Width / 2, fading.Height / 2);
                    HeadlessHarness.Assert(middle.R > 60 && middle.R < 215 && middle.G < 48,
                        $"{backend}: a half-faded cover should let about half the room through ({middle.R},{middle.G},{middle.B}).");
                    float fadedBarHeight = MathF.Max(3f, fading.Height / 240f);
                    Color bar = fading.GetPixel(fading.Width / 2, (int)MathF.Round(fading.Height * 0.84f + fadedBarHeight * 0.5f));
                    HeadlessHarness.Assert(bar.G < 48, $"{backend}: the loading bar is still drawn while the cover fades ({bar.R},{bar.G},{bar.B}).");
                }
            }
            finally
            {
                RenderBackendSelection.Configure(previous);
                RoomChangeScreen.Reset();
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Physics.Terrain.CollisionIsBuiltOnWorkersAndHandedOver", () =>
        {
            using PhysicsWorld physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            using EcsWorld world = new();

            // A mesh prepared on another thread is solid the moment it is registered.
            Vector3[] vertices = [new(-50, 3, -50), new(50, 3, -50), new(50, 3, 50), new(-50, 3, 50)];
            // Both ways round, so the floor is solid whichever side the physics engine counts as its face.
            int[] indices = [0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3];
            PhysicsWorld.PreparedStaticMesh prepared = Task.Run(() => PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, Vector3.One)).GetAwaiter().GetResult();
            HeadlessHarness.Assert(prepared.TriangleCount == 4, $"The prepared mesh has {prepared.TriangleCount} triangles.");
            int id = physics.RegisterStaticTriangleMesh(prepared, new Vector3(0, 10, 0), Quaternion.Identity, "Floor");
            HeadlessHarness.Assert(physics.RaycastDown(world, new Vector3(5, 200, 5), 400, out PhysicsRaycastHit hit) && MathF.Abs(hit.Point.Y - 13f) < 0.05f,
                $"A prepared mesh was not solid where it was placed (hit {hit.Point.Y:F2}).");
            bool threw = false;
            try { physics.RegisterStaticTriangleMesh(prepared, Vector3.Zero, Quaternion.Identity, "Twice"); }
            catch (ObjectDisposedException) { threw = true; }
            HeadlessHarness.Assert(threw, "A prepared mesh was registered twice.");
            HeadlessHarness.Assert(physics.UnregisterStaticSurface(id) && physics.ExternalStaticCount == 0
                && !physics.RaycastDown(world, new Vector3(5, 200, 5), 400, out _), "The prepared mesh was not removed.");
            PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, Vector3.One).Dispose();

            // Tiles: beneath a focus a small patch is solid at once; the tile replaces it when a worker has made it.
            TerrainAsset terrain = RuntimeViewportHarness.BuildIslandTerrain(1025, 2f);
            using TerrainColliderTiles tiles = new(terrain, Vector3.One, Vector3.Zero, Quaternion.Identity, "Terrain:patch");
            Vector3 stand = new(terrain.OriginX + 700f, 0f, terrain.OriginZ + 900f);
            TerrainColliderFocus[] focus = [new TerrainColliderFocus(stand, 120f)];
            tiles.Update(physics, focus);
            HeadlessHarness.Assert(physics.RaycastDown(world, new Vector3(stand.X, 500f, stand.Z), 1000f, out hit)
                && MathF.Abs(hit.Point.Y - terrain.SampleHeight(stand.X, stand.Z)) < 0.3f,
                "There is no ground beneath the focus after one update.");
            Vector3 beside = stand + new Vector3(60f, 0f, 0f);
            HeadlessHarness.Assert(!tiles.Settled, "The tiles claim to be settled while their meshes are still being made.");
            tiles.RegisterImmediately(physics, focus);
            HeadlessHarness.Assert(tiles.Settled && tiles.ResidentTiles >= 1 && physics.ExternalStaticCount == tiles.ResidentTiles,
                $"Once the tiles had arrived {physics.ExternalStaticCount} surfaces were registered for {tiles.ResidentTiles} tiles: a patch was left behind.");
            HeadlessHarness.Assert(physics.RaycastDown(world, new Vector3(stand.X, 500f, stand.Z), 1000f, out hit)
                && MathF.Abs(hit.Point.Y - terrain.SampleHeight(stand.X, stand.Z)) < 0.3f
                && physics.RaycastDown(world, new Vector3(beside.X, 500f, beside.Z), 1000f, out hit)
                && MathF.Abs(hit.Point.Y - terrain.SampleHeight(beside.X, beside.Z)) < 0.3f,
                "The ground is wrong or missing once the tiles replaced the patch.");
            tiles.Clear(physics);
            HeadlessHarness.Assert(physics.ExternalStaticCount == 0, "Clearing the tiles left collision registered.");

            // Behind a loading screen nothing is made on this thread: the ground arrives from the workers.
            using TerrainColliderTiles waiting = new(terrain, Vector3.One, Vector3.Zero, Quaternion.Identity, "Terrain:waiting");
            waiting.Update(physics, focus, groundBeneathAtOnce: false);
            HeadlessHarness.Assert(physics.ExternalStaticCount == 0 && !waiting.Settled,
                "Asked not to make ground at once, the tiles made some in the first update.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!waiting.Settled && watch.Elapsed.TotalSeconds < 20)
            {
                waiting.Update(physics, focus, groundBeneathAtOnce: false);
                Thread.Sleep(2);
            }

            HeadlessHarness.Assert(waiting.Settled && physics.ExternalStaticCount == waiting.ResidentTiles && waiting.ResidentTiles >= 1
                && physics.RaycastDown(world, new Vector3(stand.X, 500f, stand.Z), 1000f, out hit)
                && MathF.Abs(hit.Point.Y - terrain.SampleHeight(stand.X, stand.Z)) < 0.3f,
                "The tiles never settled, or left no ground, when they were only waited for.");
            waiting.Clear(physics);
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.Change.EmittersAndArrivingSoundsAreMadeReadyBehindTheCover", () =>
        {
            using var scene = new RuntimeScene("Chimneys");
            var sounds = new SoundCounter();
            using var composition = new ObjectCompositionSubsystem(context.Workspace, sounds);
            Genesis.Shared.ECS.Entity Place(float x)
            {
                Genesis.Shared.ECS.Entity entity = scene.World.CreateEntity();
                scene.World.Set(entity, new TransformComponent { X = x, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });
                return entity;
            }

            Genesis.Shared.ECS.Entity smoke = Place(4f), bell = Place(8f), silent = Place(12f);
            scene.World.Set(smoke, new ParticleComponent { Asset = "builtin://Explosion", ParticleTypeId = -1, RateScale = 1f, FollowEntity = true, Emitting = true });
            scene.World.Set(bell, new AudioComponent { Asset = "Bell", AutoPlay = true, Volume = 1f, Pitch = 1f });
            scene.World.Set(silent, new AudioComponent { Asset = "Horn", AutoPlay = false, Volume = 1f, Pitch = 1f });

            HeadlessHarness.Assert(composition.ParticleEmitterCount == 0, "An emitter existed before anything asked for it.");
            HeadlessHarness.Assert(!composition.WarmUp(scene) && composition.ParticleEmitterCount == 1,
                "Preparing a room should set its emitter up and ask for one more frame behind the cover.");
            HeadlessHarness.Assert(composition.TryGetParticleFlow(smoke, out _, out Vector3 origin) && origin.X == 4f,
                $"The prepared emitter should stand where its Object is; it is at {origin.X}.");
            HeadlessHarness.Assert(sounds.Loaded.SequenceEqual(["Bell"]) && sounds.Played == 0,
                $"A sound an Object plays on arrival should be read, and not played, behind the cover ({sounds.Loaded.Count} read, {sounds.Played} played).");
            HeadlessHarness.Assert(composition.WarmUp(scene) && composition.WarmUp(scene) && composition.ParticleEmitterCount == 1 && sounds.Loaded.Count == 1,
                "Preparing a room that is already prepared should do nothing and say it is ready.");

            // The room starts running: the emitter is kept, and only now is the sound played.
            scene.GameTime.Advance(1f / 60f);
            composition.Update(scene, scene.GameTime);
            HeadlessHarness.Assert(composition.ParticleEmitterCount == 1 && sounds.Played == 1, "The first update made the emitter again, or did not play the arriving sound.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rendering.Textures.AreReadInTheBackgroundAndHandedOver", () =>
        {
            string folder = Path.Combine(context.Workspace, "BackgroundTextures" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "wall.png"), broken = Path.Combine(folder, "broken.png");
            WritePicture(file, Color.SteelBlue);
            File.WriteAllText(broken, "this is not a picture");
            using var software = new SoftwareRenderer();
            IRenderController renderer = software.Renderer;

            TextureHandle handle = renderer.LoadTextureInBackground(file, TextureColorSpace.Srgb, out bool pending);
            HeadlessHarness.Assert(!handle.IsValid && pending && renderer.BackgroundTexturesPending == 1,
                "A texture that has not been read was not reported as on its way.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (pending && watch.Elapsed.TotalSeconds < 20)
            {
                Thread.Sleep(2);
                handle = renderer.LoadTextureInBackground(file, TextureColorSpace.Srgb, out pending);
            }

            HeadlessHarness.Assert(handle.IsValid && !pending && renderer.IsTextureLive(handle) && renderer.BackgroundTexturesPending == 0,
                "The texture never arrived from the worker that was reading it.");
            // Asked for again, either way, it is the texture already loaded.
            HeadlessHarness.Assert(renderer.LoadTextureInBackground(file, TextureColorSpace.Srgb, out pending).Id == handle.Id && !pending
                && renderer.LoadTexture(file, TextureColorSpace.Srgb).Id == handle.Id,
                "A texture read in the background was loaded a second time when asked for again.");
            // The same picture for a different use is a different texture, and is read in its turn.
            HeadlessHarness.Assert(!renderer.LoadTextureInBackground(file, TextureColorSpace.Linear, out pending).IsValid && pending,
                "The same file as data rather than colour should be read separately.");

            HeadlessHarness.Assert(!renderer.LoadTextureInBackground(Path.Combine(folder, "missing.png"), TextureColorSpace.Srgb, out pending).IsValid && !pending,
                "A file that does not exist was reported as on its way.");
            // A file that is not a picture: invalid, and not handed to a worker again on every frame.
            watch.Restart();
            do
            {
                Thread.Sleep(2);
                handle = renderer.LoadTextureInBackground(broken, TextureColorSpace.Srgb, out pending);
            }
            while (pending && watch.Elapsed.TotalSeconds < 20);
            HeadlessHarness.Assert(!handle.IsValid && !pending, "A file that is not a picture never stopped being on its way.");
            for (int i = 0; i < 5; i++)
            {
                handle = renderer.LoadTextureInBackground(broken, TextureColorSpace.Srgb, out pending);
                HeadlessHarness.Assert(!handle.IsValid && !pending, "A file that could not be read was handed to a worker again straight away.");
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Models.Textures.AMeshWaitsForItsTexturesInsteadOfHoldingTheFrame", () =>
        {
            string parent = Path.Combine(context.Workspace, "RoomChangeProjects");
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Skins" + Guid.NewGuid().ToString("N")[..6], "Blank");
            TexturedModel(project, "Crate", Color.Peru);
            TexturedModel(project, "Barrel", Color.SaddleBrown);
            using var software = new SoftwareRenderer();
            IRenderController renderer = software.Renderer;
            var draw = new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true };
            var model = new ModelRendererComponent { CastShadows = true, ReceiveShadows = true, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f };
            bool before = RuntimeModelRenderSystem.BackgroundTextures;
            try
            {
                var list = new DrawList();
                void Frame(RuntimeModelRenderSystem system, string name)
                {
                    list.Clear();
                    renderer.BeginFrame();
                    system.BeginFrame();
                    system.Enqueue(list, project.RootPath, name, string.Empty, Matrix4x4.Identity, draw, model, default, renderer);
                    system.EndFrame();
                    renderer.EndFrame();
                }

                // In a game: the first frame draws nothing of the model and starts its texture; it appears, textured, when that arrives.
                RuntimeModelRenderSystem.BackgroundTextures = true;
                var streaming = new RuntimeModelRenderSystem();
                Frame(streaming, "Crate");
                HeadlessHarness.Assert(list.Count == 0 && renderer.BackgroundTexturesPending == 1,
                    $"The first frame drew {list.Count} meshes with {renderer.BackgroundTexturesPending} textures on their way; the mesh should wait for its one texture.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (list.Count == 0 && watch.Elapsed.TotalSeconds < 20)
                {
                    Thread.Sleep(2);
                    Frame(streaming, "Crate");
                }

                HeadlessHarness.Assert(list.Count == 1 && list.Calls[0].Texture.IsValid && renderer.BackgroundTexturesPending == 0,
                    "The mesh never appeared with its texture.");
                TextureHandle arrived = list.Calls[0].Texture;
                Frame(streaming, "Crate");
                HeadlessHarness.Assert(list.Count == 1 && list.Calls[0].Texture.Id == arrived.Id, "A mesh whose texture had arrived stopped being drawn, or changed texture.");

                // In an editor or a capture: the texture is read in the frame that asks, and the mesh is drawn with it.
                RuntimeModelRenderSystem.BackgroundTextures = false;
                Frame(new RuntimeModelRenderSystem(), "Barrel");
                HeadlessHarness.Assert(list.Count == 1 && list.Calls[0].Texture.IsValid, "With background reading off, the mesh was not drawn with its texture in its first frame.");
            }
            finally
            {
                RuntimeModelRenderSystem.BackgroundTextures = before;
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rendering.Text.ALineInANewSizeIsSentAsOneStrip", () =>
        {
            const string line = "Alderford: the gate is shut until morning";

            // On each graphics backend the line reaches the screen, first time and after.
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                foreach ((RenderBackendOption backend, string name) in new[]
                {
                    (RenderBackendOption.SilkNetDx11, "dx11"), (RenderBackendOption.Direct3D12, "dx12"),
                    (RenderBackendOption.Vulkan, "vulkan"), (RenderBackendOption.OpenGL, "opengl"),
                })
                {
                    RenderBackendSelection.Configure(backend);
                    using RuntimeViewportHarness harness = new();
                    harness.Capture3D(Path.Combine(context.Captures, $"text-{name}-before.png"));
                    IRenderController renderer = harness.Renderer;
                    double Compose(float size)
                    {
                        long started = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool composed = renderer.ComposeOverlay(canvas =>
                            canvas.DrawText(line, new Vector2(12f, 12f), size, Vector4.One, fontFamily: "Georgia"));
                        HeadlessHarness.Assert(composed, $"{backend}: the overlay refused a line of text.");
                        return System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }

                    double warm = Compose(18f);
                    double first = Compose(64f), again = Compose(64f);
                    Console.WriteLine($"[Text] {name}: a line in a new size took {first:F1} ms to compose the first time and {again:F1} ms the next ({warm:F1} ms for the overlay's first use).");
                }
            }
            finally
            {
                RenderBackendSelection.Configure(previous);
            }

            // The atlas: a line of text in a size it has not seen is many new glyphs and few uploads.
            using var atlas = new Genesis.Shared.Overlay.GlyphAtlas();
            var quads = new List<Genesis.Shared.Overlay.GlyphQuad>();
            atlas.LayoutRun(line, "Georgia", 22f, bold: false, 0f, 0f, quads);
            long glyphs = atlas.RasterCount;
            IReadOnlyList<Genesis.Shared.Overlay.GlyphUpload> uploads = atlas.DrainUploads();
            HeadlessHarness.Assert(glyphs >= 15 && uploads.Count >= 1 && uploads.Count <= 2,
                $"A line of {glyphs} new glyphs should be one upload, or two where it runs on to the next shelf; it was {uploads.Count}.");
            foreach (Genesis.Shared.Overlay.GlyphUpload upload in uploads)
            {
                HeadlessHarness.Assert(upload.Pixels.Length == upload.Width * upload.Height * 4 && upload.X >= 0 && upload.Y >= 0
                    && upload.X + upload.Width <= Genesis.Shared.Overlay.GlyphAtlas.AtlasSize
                    && upload.Y + upload.Height <= Genesis.Shared.Overlay.GlyphAtlas.AtlasSize,
                    "A strip of glyphs does not describe a rectangle of the atlas.");
                for (int i = 0; i + 3 < upload.Pixels.Length; i += 4)
                    HeadlessHarness.Assert(upload.Pixels[i] == 255 && upload.Pixels[i + 1] == 255 && upload.Pixels[i + 2] == 255,
                        "A texel of a glyph strip is not white; text would be tinted twice.");
            }

            // Every glyph the line draws lies inside what was uploaded, and has ink there.
            foreach (Genesis.Shared.Overlay.GlyphQuad quad in quads)
            {
                int x0 = (int)MathF.Round(quad.U0 * Genesis.Shared.Overlay.GlyphAtlas.AtlasSize), y0 = (int)MathF.Round(quad.V0 * Genesis.Shared.Overlay.GlyphAtlas.AtlasSize);
                int x1 = (int)MathF.Round(quad.U1 * Genesis.Shared.Overlay.GlyphAtlas.AtlasSize), y1 = (int)MathF.Round(quad.V1 * Genesis.Shared.Overlay.GlyphAtlas.AtlasSize);
                Genesis.Shared.Overlay.GlyphUpload home = uploads.FirstOrDefault(upload =>
                    x0 >= upload.X && y0 >= upload.Y && x1 <= upload.X + upload.Width && y1 <= upload.Y + upload.Height);
                byte[] strip = home.Pixels
                    ?? throw new InvalidOperationException($"A glyph at {x0},{y0} of the atlas was drawn but not uploaded.");
                int ink = 0;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (strip[(((y - home.Y) * home.Width) + (x - home.X)) * 4 + 3] > 96) ink++;
                HeadlessHarness.Assert(ink > 0, $"The glyph at {x0},{y0} of the atlas was uploaded with nothing in it.");
            }

            atlas.LayoutRun(line, "Georgia", 22f, bold: false, 0f, 0f, quads);
            HeadlessHarness.Assert(atlas.RasterCount == glyphs && atlas.DrainUploads().Count == 0, "The same line again made new glyphs or uploads.");

        });

        HeadlessHarness.RunCase(context.Report, "Engine.Diagnostics.SlowFrames.SayWhatTheFrameWasSpentOn", () =>
        {
            var lines = new List<string>();
            var log = new SlowFrameLog(lines.Add, thresholdMilliseconds: 30);
            log.FrameEnded("Hall", counted: true, 1, 1, 1, 1);
            log.FrameEnded("Hall", counted: true, 1, 1, 1, 1);
            HeadlessHarness.Assert(lines.Count == 0, "A quick frame was written down as slow.");

            LoadClock.UseEveryThread();
            LoadClockSnapshot before = LoadClock.Capture();
            using (LoadClock.Measure(LoadWork.Texture)) Thread.Sleep(45);
            HeadlessHarness.Assert(LoadClock.Capture().Describe(before).StartsWith("1 texture ", StringComparison.Ordinal)
                && LoadClock.Capture().MillisecondsSince(before) >= 40, $"The load clock reported '{LoadClock.Capture().Describe(before)}'.");
            var parts = new SceneWorkTimes();
            long mark = System.Diagnostics.Stopwatch.GetTimestamp();
            Thread.Sleep(12);
            parts.Add("terrain update", mark);
            parts.Add("too quick to mention", System.Diagnostics.Stopwatch.GetTimestamp());
            HeadlessHarness.Assert(parts.Describe().StartsWith("terrain update ", StringComparison.Ordinal) && !parts.Describe().Contains("too quick"),
                $"The longest parts were described as '{parts.Describe()}'.");
            // The parts given account for more than the frame, however long a busy machine made it.
            log.FrameEnded("Hall", counted: true, 9046, 2, 3, 4, parts.Describe());
            HeadlessHarness.Assert(lines.Count == 1 && lines[0].Contains("in Hall (frame 3") && lines[0].Contains("update 9046 ms")
                && lines[0].Contains("longest parts: terrain update ") && lines[0].Contains("1 texture "),
                $"The slow frame was written as: {(lines.Count == 0 ? "(nothing)" : lines[0])}");
            parts.Clear();
            HeadlessHarness.Assert(parts.Describe().Length == 0, "Cleared work times still describe something.");
            HeadlessHarness.Assert(lines[0].Contains("HUD and hooks 0 ms") && !lines[0].Contains("outside the frame"),
                $"A frame whose parts account for it should not be said to have time outside it: {lines[0]}");

            // A frame far longer than its own work says so, and counts the HUD and the hooks among that work.
            Thread.Sleep(70);
            log.FrameEnded("Hall", counted: true, 2, 2, 2, 2, string.Empty, overlayMilliseconds: 9);
            HeadlessHarness.Assert(lines.Count == 2 && lines[1].Contains("HUD and hooks 9 ms") && lines[1].Contains("ms was outside the frame's own work"),
                $"A frame of 70 ms with 17 ms of work was written as: {lines[^1]}");

            // A frame that is expected to be long (a start-up loading screen) is not written down, and a room has a limit.
            Thread.Sleep(40);
            log.FrameEnded("Hall", counted: false, 1, 1, 1, 1);
            HeadlessHarness.Assert(lines.Count == 2, "A frame of the start-up loading screen was written down as slow.");
            for (int i = 0; i < SlowFrameLog.MaximumPerRoom + 5; i++)
            {
                Thread.Sleep(32);
                log.FrameEnded("Cellar", counted: true, 1, 1, 1, 1);
            }

            HeadlessHarness.Assert(log.Written == SlowFrameLog.MaximumPerRoom && lines[^1].Contains("no more are written"),
                $"{log.Written} slow frames were written for one room; the limit is {SlowFrameLog.MaximumPerRoom}.");
        });
    }
}

using System.Globalization;
using System.Numerics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Cameras;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Camera rigs (Engine.Camera.Rigs): named virtual cameras, blends between them, dolly paths, and
/// a game that makes none keeping its camera exactly as before.
/// </summary>
internal static class CameraRigSuite
{
    private const float Degrees = MathF.PI / 180f;
    // The director moves at most a quarter of a second a frame, as the orbit camera does.
    private const float Frame = 0.25f;

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static bool Near(float a, float b, float within = 0.001f) => Math.Abs(a - b) < within;
    private static bool Near(Vector3 a, Vector3 b, float within = 0.001f) => Vector3.Distance(a, b) < within;
    private static string Pose(Camera3D camera) => string.Create(CultureInfo.InvariantCulture,
        $"at {camera.Position}, yaw {camera.Yaw / Degrees:0.###}, pitch {camera.Pitch / Degrees:0.###}, fov {camera.FieldOfView / Degrees:0.###}");

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Camera Rigs");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Camera.Rigs.BlendReachesTargetPoseInTime", () => WithScene(ctx, (scene, context, game) =>
        {
            Camera3D camera = scene.Camera3D;
            Check(PgslCommands.CameraRigCreate("a", "fixed") && PgslCommands.CameraRigCreate("b", "fixed"), "Fixed rigs were not made.");
            PgslCommands.CameraSetRigOffset("a", 0, 2, 0); PgslCommands.CameraSetRigAngles("a", 0, 0); PgslCommands.CameraSetRigFov("a", 40);
            PgslCommands.CameraSetRigOffset("b", 10, 2, 0); PgslCommands.CameraSetRigAngles("b", 90, 30); PgslCommands.CameraSetRigFov("b", 80);
            Check(PgslCommands.CameraRigCut("a"), "Cut to a rig that exists reported false.");
            CameraRigDirector.Update(scene, Frame);
            Check(Near(camera.Position, new Vector3(0, 2, 0)) && Near(camera.Yaw, 0) && Near(camera.Pitch, 0) && Near(camera.FieldOfView, 40 * Degrees),
                "The active rig's pose is not on the camera: " + Pose(camera));

            Check(PgslCommands.CameraRigBlendTo("b", 1, "linear") && PgslCommands.CameraRigBlending(), "The blend did not start.");
            CameraRigDirector.Update(scene, Frame); CameraRigDirector.Update(scene, Frame);
            // Half the time: half way in position, yaw, pitch (30 degrees down is -30 for the camera) and field of view.
            Check(Near(camera.Position, new Vector3(5, 2, 0)) && Near(camera.Yaw, 45 * Degrees) && Near(camera.Pitch, -15 * Degrees)
                && Near(camera.FieldOfView, 60 * Degrees) && Near((float)PgslCommands.CameraRigsProgress, 0.5f),
                "Half way through a one-second linear blend the camera is " + Pose(camera));
            CameraRigDirector.Update(scene, Frame); CameraRigDirector.Update(scene, Frame);
            Check(Near(camera.Position, new Vector3(10, 2, 0)) && Near(camera.Yaw, 90 * Degrees) && Near(camera.Pitch, -30 * Degrees)
                && Near(camera.FieldOfView, 80 * Degrees) && !PgslCommands.CameraRigBlending() && PgslCommands.CameraRigActive() == "b",
                "After its one second the blend has not reached the rig: " + Pose(camera));

            // Ease in-out starts slowly: a quarter of the way through it has moved 0.15625 of the way.
            PgslCommands.CameraRigBlendTo("a", 1, "easeinout");
            CameraRigDirector.Update(scene, Frame);
            Check(Near(camera.Position.X, 10 - 10 * 0.15625f), $"An ease in-out blend a quarter through is at x {camera.Position.X} (8.4375).");
            // Asking for the rig being blended to again does not start the blend over.
            PgslCommands.CameraRigBlendTo("a", 1, "easeinout");
            for (int i = 0; i < 3; i++) CameraRigDirector.Update(scene, Frame);
            Check(Near(camera.Position, new Vector3(0, 2, 0)) && !PgslCommands.CameraRigBlending(), "Asking again restarted the blend: " + Pose(camera));
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Camera.Rigs.CutIsInstant", () => WithScene(ctx, (scene, context, game) =>
        {
            Camera3D camera = scene.Camera3D;
            PgslCommands.CameraRigCreate("a", "fixed"); PgslCommands.CameraSetRigOffset("a", 0, 2, 0); PgslCommands.CameraSetRigAngles("a", 0, 0);
            PgslCommands.CameraRigCreate("b", "fixed"); PgslCommands.CameraSetRigOffset("b", 30, 4, -6); PgslCommands.CameraSetRigAngles("b", 180, -10);
            PgslCommands.CameraRigCut("a");
            CameraRigDirector.Update(scene, 0.016f);
            PgslCommands.CameraRigBlendTo("b", 2, "cut");
            CameraRigDirector.Update(scene, 0.016f);
            Check(Near(camera.Position, new Vector3(30, 4, -6)) && Near(camera.Yaw, MathF.PI) && Near(camera.Pitch, 10 * Degrees) && !PgslCommands.CameraRigBlending(),
                "A cut took more than a frame: " + Pose(camera));
            // Through the API: no blend time is a cut too.
            PgslCommands.CameraRigsBlendTime = 0;
            PgslCommands.CameraRigsActive = "a";
            CameraRigDirector.Update(scene, 0.016f);
            Check(Near(camera.Position, new Vector3(0, 2, 0)) && PgslCommands.CameraRigsActive == "a", "Active with no blend time did not cut: " + Pose(camera));
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Camera.Rigs.DollyPositionMapsToPathPoint", () => WithScene(ctx, (scene, context, game) =>
        {
            Camera3D camera = scene.Camera3D;
            PgslCommands.CameraRigCreate("track", "dolly");
            PgslCommands.CameraRigPathAdd("track", 0, 1, 0); PgslCommands.CameraRigPathAdd("track", 10, 1, 0);
            Check(PgslCommands.CameraRigPathAdd("track", 10, 1, 10) == 3 && Near((float)PgslCommands.CameraRigPathLength, 20), "The path did not keep its points.");
            PgslCommands.CameraRigCut("track");
            void At(float position, Vector3 expected, float yaw, string what)
            {
                PgslCommands.CameraSetRigPathPosition("track", position);
                CameraRigDirector.Update(scene, 0);
                Check(Near(camera.Position, expected) && Near(camera.Yaw, yaw), $"{what}: position {position} put the camera " + Pose(camera));
            }
            // A position is a share of the length: a quarter is half way along the first 10 m leg.
            At(0, new Vector3(0, 1, 0), MathF.PI / 2, "the start");
            At(0.25f, new Vector3(5, 1, 0), MathF.PI / 2, "a quarter");
            At(0.75f, new Vector3(10, 1, 5), MathF.PI, "three quarters, looking along +z");
            At(1, new Vector3(10, 1, 10), MathF.PI, "the end");

            // At a speed: 10 m a second rides a quarter second's 2.5 m of the 20 m path.
            PgslCommands.CameraSetRigPathPosition("track", 0); PgslCommands.CameraSetRigPathSpeed("track", 10);
            CameraRigDirector.Update(scene, Frame);
            Check(Near((float)PgslCommands.CameraGetRigPathPosition("track"), 0.125f) && Near(camera.Position, new Vector3(2.5f, 1, 0)),
                $"Riding at 10 m/s for 0.25 s reached {PgslCommands.CameraGetRigPathPosition("track")} " + Pose(camera));
            // The end holds an open path; a looped one closes back to the start and goes round.
            PgslCommands.CameraSetRigPathPosition("track", 0.99);
            CameraRigDirector.Update(scene, Frame);
            Check(Near((float)PgslCommands.CameraGetRigPathPosition("track"), 1), "An open path rode past its end.");
            PgslCommands.CameraSetRigPathLoop("track", true);
            float closed = 20 + MathF.Sqrt(200);
            PgslCommands.CameraSetRigPathPosition("track", 0.99);
            CameraRigDirector.Update(scene, Frame);
            Check(Near((float)PgslCommands.CameraGetRigPathPosition("track"), 0.99f + 2.5f / closed - 1, 0.0005f),
                $"A looped path did not wrap round: {PgslCommands.CameraGetRigPathPosition("track")}");

            // Looking at a target instead of along the path.
            PgslCommands.CameraSetRigPathSpeed("track", 0); PgslCommands.CameraSetRigPathLoop("track", false);
            PgslCommands.CameraSetRigTargetPoint("track", 5, 1, -10);
            At(0.5f, new Vector3(10, 1, 0), MathF.Atan2(-5, 10), "looking at a point");

            // A smooth path still starts and ends on its first and last points.
            PgslCommands.CameraSetRigPathSmooth("track", true);
            PgslCommands.CameraSetRigPathPosition("track", 0); CameraRigDirector.Update(scene, 0);
            Vector3 start = camera.Position;
            PgslCommands.CameraSetRigPathPosition("track", 1); CameraRigDirector.Update(scene, 0);
            Check(Near(start, new Vector3(0, 1, 0)) && Near(camera.Position, new Vector3(10, 1, 10)) && PgslCommands.CameraRigPathLength > 20,
                $"A smooth path does not run from its first point to its last round the corner: from {start} to {camera.Position}, {PgslCommands.CameraRigPathLength} m");

            // A Pathing resource's route, by its name.
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "CameraRigTrack"), "Camera rig track");
            string pathing = new ResourceService(project).CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Pathing), ResourceKind.Pathing, "Rig Track");
            var asset = new PathingAsset { Name = "Rig Track" };
            asset.Route.LoopMode = PathingLoopMode.Once;
            foreach (Vector3 point in new[] { new Vector3(0, 2, 0), new Vector3(4, 2, 0), new Vector3(4, 2, 4) })
                asset.Route.Waypoints.Add(new PathingWaypoint { Position = point });
            PathingAssetSerializer.Save(pathing, asset);
            ResourceCatalog.Invalidate(project.RootPath);
            string oldPath = PgslCommands.ProjectPath;
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                Check(PgslCommands.CameraSetRigPath("track", "Rig Track"), "A Pathing resource could not be named for a dolly path.");
                PgslCommands.CameraSetRigPathSmooth("track", false); PgslCommands.CameraSetRigOffset("track", 0, 1, 0);
                PgslCommands.CameraSetRigPathPosition("track", 0.5); CameraRigDirector.Update(scene, 0);
                Check(Near(camera.Position, new Vector3(4, 3, 0)) && !PgslCommands.CameraSetRigPath("track", "No Such Track"),
                    "The Pathing route's middle (plus the offset) is not where its half way is: " + Pose(camera));
            }
            finally { PgslCommands.ProjectPath = oldPath; }
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Camera.Rigs.NoRigKeepsTheOldCamera", () => WithScene(ctx, (scene, context, game) =>
        {
            Camera3D camera = scene.Camera3D;
            // The orbit camera on its own, as Runtime.PGSL.ThirdPersonCamera sets it up.
            var entity = scene.World.CreateEntity(); context.InstanceId = entity.Id;
            scene.World.Set(entity, new TransformComponent { X = 10, Y = 0, Z = 20 });
            PgslCommands.CameraSetOrbitMouseLook(false); PgslCommands.CameraSetShoulderOffset(0, 2, 0);
            PgslCommands.CameraSetOrbitPitch(0); PgslCommands.CameraSetOrbitYaw(90); PgslCommands.CameraSetOrbitDistance(5);
            float fov = camera.FieldOfView;
            // Reading the rigs' values makes nothing: the frame has no rig work at all.
            bool readsOnly = PgslCommands.CameraRigsActive == "" && PgslCommands.CameraRigsCount == 0 && PgslCommands.CameraRigsEnabled
                && !PgslCommands.CameraRigExists("x") && CameraRigDirector.For(scene) == null;
            ThirdPersonCamera.UpdateScene(scene, .016f);
            scene.UpdateVariable(.016f);
            Check(readsOnly && Near(camera.Position, new Vector3(5, 2, 20)) && Near(camera.Yaw, MathF.PI / 2) && camera.FieldOfView == fov,
                "With no rig the orbit camera did not keep the camera: " + Pose(camera));

            // A rig made but never made active changes nothing either.
            PgslCommands.CameraRigCreate("unused", "follow"); PgslCommands.CameraSetRigFov("unused", 20);
            PgslCommands.SetCameraPosition(1, 2, 3);
            scene.UpdateVariable(.016f);
            Check(Near(camera.Position, new Vector3(1, 2, 3)) && camera.FieldOfView == fov, "A rig that is not active moved the camera: " + Pose(camera));

            // A rig drives through the scene's frame, then hands back its field of view and the orbit camera.
            PgslCommands.CameraSetRigTarget("unused", entity.Id);
            PgslCommands.CameraRigCut("unused");
            scene.UpdateVariable(.016f);
            Check(Near(camera.Position, new Vector3(10, 3, 28)) && Near(camera.FieldOfView, 20 * Degrees), "The follow rig did not drive the camera: " + Pose(camera));
            PgslCommands.CameraRigsActive = "";
            scene.UpdateVariable(.016f);
            ThirdPersonCamera.UpdateScene(scene, .016f);
            scene.UpdateVariable(.016f);
            Check(Near(camera.Position, new Vector3(5, 2, 20)) && camera.FieldOfView == fov, "After the rig let go the old camera did not come back: " + Pose(camera));

            // Off keeps the rigs but leaves the camera alone.
            PgslCommands.CameraRigCut("unused");
            PgslCommands.CameraRigsEnabled = false;
            PgslCommands.SetCameraPosition(1, 2, 3);
            scene.UpdateVariable(.016f);
            Check(Near(camera.Position, new Vector3(1, 2, 3)) && PgslCommands.CameraRigsCount == 1, "Rigs turned off still drove the camera: " + Pose(camera));

            // A new room starts with none.
            scene.UnloadRoomContent(null, null);
            Check(PgslCommands.CameraRigsCount == 0 && PgslCommands.CameraRigsActive == "" && PgslCommands.CameraRigsEnabled, "A room's rigs outlived it.");
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Camera.Rigs.ScriptMakesSwitchesAndRides", () => WithScene(ctx, (scene, context, game) =>
            ScriptedRigs(scene, game)));
    }

    // A game's script: a wide shot, a follow camera on itself and a dolly track; it walks along x,
    // blends to the follow camera and cuts to the track, all from its own events, in the scene's frame.
    private const string CreateScript = """
        Engine.Camera.Rigs.Create("wide", "fixed");
        Engine.Camera.Rig.OffsetX = 0;
        Engine.Camera.Rig.OffsetY = 10;
        Engine.Camera.Rig.OffsetZ = 20;
        Engine.Camera.Rig.Yaw = 0;
        Engine.Camera.Rig.Pitch = 30;
        Engine.Camera.Rig.Fov = 50;
        Engine.Camera.Rigs.Create("chase", "follow");
        Engine.Camera.Rig.Target = id;
        Engine.Camera.Rig.OffsetX = 0;
        Engine.Camera.Rig.OffsetY = 2;
        Engine.Camera.Rig.OffsetZ = 6;
        Engine.Camera.Rig.LookOffsetY = 1;
        CameraRigCreate("track", "dolly");
        CameraRigPathAdd("track", 0, 3, 0);
        CameraRigPathAdd("track", 20, 3, 0);
        CameraSetRigTarget("track", id);
        Engine.Camera.Rig.Path.Speed = 4;
        Engine.Camera.Rigs.BlendTime = 0.5;
        Engine.Camera.Rigs.BlendCurve = "linear";
        Engine.Camera.Rigs.Active = "wide";
        made = Engine.Camera.Rigs.Count;
        selected = Engine.Camera.Rig.Name;
        kindName = Engine.Camera.Rig.Kind;
        points = Engine.Camera.Rig.Path.Count;
        curveName = Engine.Camera.Rigs.BlendCurve;
        frames = 0;
        """;

    private const string StepScript = """
        frames += 1;
        x += 1;
        if (frames == 4) { Engine.Camera.Rigs.Active = "chase"; }
        if (frames == 8) { CameraRigCut("track"); }
        activeNow = Engine.Camera.Rigs.Active;
        """;

    private static void ScriptedRigs(RuntimeScene scene, ProjectGameContext game)
    {
        Camera3D camera = scene.Camera3D;
        camera.Position = new Vector3(0, 30, 40); camera.Yaw = 0; camera.Pitch = 0; camera.FieldOfView = 60 * Degrees;
        VMEngine.Initialize();
        var host = new ScriptHostSystem();
        host.SetContext(game);
        scene.AddSubsystem(new ScriptHostSubsystem(host));
        Entity walker = scene.World.CreateEntity();
        scene.World.Set(walker, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = CreateScript, ["Step"] = StepScript })) host.Attach(scene.World, walker, "RigWalker");
        PgslBehavior behavior = host.Instances.OfType<PgslBehavior>().Single(b => b.Entity == walker);
        string Diagnostics() => string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));
        object? Var(string name) => behavior.GetVariablesSnapshot().TryGetValue(name, out object? value) ? value : null;
        double Number(string name) => Var(name) is { } value and not string ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
        string Text(string name) => Var(name) as string ?? "";

        Check(Number("made") == 3 && Text("selected") == "track" && Text("kindName") == "dolly" && Number("points") == 2 && Text("curveName") == "linear",
            $"The script's rigs: {Number("made")} made, {Text("selected")} selected ({Text("kindName")}, {Number("points")} points), curve {Text("curveName")}. {Diagnostics()}");

        float walkerX() => scene.World.GetRef<TransformComponent>(walker).X;
        // Frame 1: half way from where the camera was to the wide shot; frame 2 there.
        scene.UpdateVariable(Frame);
        Check(Near(camera.Position, new Vector3(0, 20, 30)) && Near(camera.FieldOfView, 55 * Degrees),
            "Half way into the first blend the camera is " + Pose(camera) + " " + Diagnostics());
        scene.UpdateVariable(Frame);
        Check(Near(camera.Position, new Vector3(0, 10, 20)) && Near(camera.Pitch, -30 * Degrees) && Near(camera.FieldOfView, 50 * Degrees),
            "The wide shot is not on the camera: " + Pose(camera));
        scene.UpdateVariable(Frame);
        // Frame 4: the script switches in its Step; the blend to the chase camera is half way that frame.
        scene.UpdateVariable(Frame);
        Vector3 wide = new(0, 10, 20), chase = new(walkerX(), 2, 6);
        Check(Text("activeNow") == "chase" && Near(camera.Position, Vector3.Lerp(wide, chase, 0.5f)),
            $"Half way to the chase camera (the walker at x {walkerX()}) the camera is " + Pose(camera));
        scene.UpdateVariable(Frame);
        // The follow rig sees where the walker went in this frame's Step: no frame behind.
        float pitch = MathF.Asin(-1 / MathF.Sqrt(37));
        Check(Near(walkerX(), 5) && Near(camera.Position, new Vector3(5, 2, 6)) && Near(camera.Yaw, 0) && Near(camera.Pitch, pitch) && camera.FieldOfView == 60 * Degrees,
            "The chase camera is not behind the walker looking at it: " + Pose(camera));
        scene.UpdateVariable(Frame); scene.UpdateVariable(Frame);
        // Frame 8: a cut to the track, which rides 1 m a frame and looks at the walker.
        scene.UpdateVariable(Frame);
        Vector3 toWalker = new Vector3(walkerX(), 0, 0) - new Vector3(1, 3, 0);
        Check(Text("activeNow") == "track" && Near(camera.Position, new Vector3(1, 3, 0)) && Near(camera.Yaw, MathF.Atan2(toWalker.X, -toWalker.Z))
            && Near(camera.Pitch, MathF.Asin(Vector3.Normalize(toWalker).Y)),
            "The cut to the track is not on it looking at the walker: " + Pose(camera));
        scene.UpdateVariable(Frame);
        Check(Near(camera.Position, new Vector3(2, 3, 0)) && Diagnostics().Length == 0, "The track did not ride on: " + Pose(camera) + " " + Diagnostics());
    }

    private static void WithScene(HeadlessContext ctx, Action<RuntimeScene, PgslContext, ProjectGameContext> test)
    {
        using var scene = new RuntimeScene("Camera rig test");
        var game = new ProjectGameContext(ctx.Workspace, scene, null, null, RoomAsset.Create("Rigs", RoomDimension.ThreeD), null);
        var context = new PgslContext(); var oldContext = PgslCommands.BindContext(context);
        var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = ctx.Workspace;
        try { test(scene, context, game); }
        finally { PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
    }
}

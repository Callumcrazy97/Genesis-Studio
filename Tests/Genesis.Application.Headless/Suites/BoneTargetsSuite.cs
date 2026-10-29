using System.Globalization;
using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Looks and reaches on any model instance (ModelInstance.SetBoneLookAt / SetBoneReach,
/// Engine.Animation.LookAt / Reach): a head turns toward its target within its limits, a hand
/// lands on its target, each blends in and out in its time, on top of a clip or a controller,
/// and weight 0 (or never asking) leaves the pose exactly as it was.
/// </summary>
internal static class BoneTargetsSuite
{
    private const float Degrees = MathF.PI / 180f;
    private static readonly Vector3 Place = new(10, 0, 5);
    // The hand bone's full name: a socket lookup wants it, a reach finds it without its rig's prefix.
    private const string Hand = "mixamorig:RightHand";

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static bool Near(Vector3 a, Vector3 b, float within = 2e-3f) => Vector3.Distance(a, b) < within;

    /// <summary>
    /// A figure facing +Z: a spine on a root, a head 1.7 up, and a right arm of two bones about
    /// 0.3 long each from a shoulder at (0.2, 1.5). "Sway" raises the spine a centimetre a frame.
    /// </summary>
    private static GModelAsset Figure()
    {
        Matrix4x4 root = Matrix4x4.Identity, spine = Matrix4x4.CreateTranslation(0, 1, 0), head = Matrix4x4.CreateTranslation(0, 0.7f, 0),
            upper = Matrix4x4.CreateTranslation(0.2f, 0.5f, 0), lower = Matrix4x4.CreateTranslation(0.3f, -0.05f, 0), hand = Matrix4x4.CreateTranslation(0.3f, 0.05f, 0);
        var model = new GModelAsset { Name = "Looker" };
        model.Rig = new GModelRig
        {
            Bones =
            [
                new() { Name = "Root", ParentIndex = -1, BindLocal = root },
                new() { Name = "Spine", ParentIndex = 0, BindLocal = spine },
                new() { Name = "Head", ParentIndex = 1, BindLocal = head },
                new() { Name = "RightArm", ParentIndex = 1, BindLocal = upper },
                new() { Name = "RightForeArm", ParentIndex = 3, BindLocal = lower },
                new() { Name = "mixamorig:RightHand", ParentIndex = 4, BindLocal = hand },
            ],
            InverseBindMatrices = Enumerable.Repeat(Matrix4x4.Identity, 6).ToArray(),
        };
        var sway = new GModelAnimationClip { Name = "Sway", Fps = 30f };
        for (int frame = 0; frame < 30; frame++)
            sway.Frames.Add(new() { LocalBoneTransforms = [root, Matrix4x4.CreateTranslation(0, 1 + 0.01f * frame, 0), head, upper, lower, hand] });
        model.Animations.Add(sway);
        return model;
    }

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Looks and reaches");
        string project = Path.Combine(ctx.Workspace, "LookReachProject");
        Directory.CreateDirectory(Path.Combine(project, "Models"));
        RuntimeModelStore.Save(Path.Combine(project, "Models", "Looker.gmodel"), Figure());

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.LookAt.HeadTurnsTowardItsTargetWithinItsLimits", () => WithFigure(ctx, project, (scene, world, figure, game) =>
        {
            Vector3 head = Where(world, figure, "Head");
            Check(Near(head, Place + new Vector3(0, 1.7f, 0)) && Near(Facing(world, figure), Vector3.UnitZ, 1e-4f), "The head at rest is not 1.7 up facing +Z.");
            void LookAt(Vector3 point, string what, Func<Vector3, bool> right)
            {
                Check(ModelInstance.SetBoneLookAt(world, figure, "Head", point, blendSeconds: 0f, yawLimitDegrees: 70f, pitchLimitDegrees: 40f), what + ": the look was refused.");
                ModelBoneTargets.UpdateScene(scene, 0.016f);
                Vector3 facing = Facing(world, figure);
                Check(right(facing) && Near(Where(world, figure, "Head"), head, 1e-4f), $"{what}: the head faces {facing}, at {Where(world, figure, "Head")}.");
            }
            float Yaw(Vector3 facing) => MathF.Atan2(facing.X, facing.Z) / Degrees;
            float Pitch(Vector3 facing) => MathF.Asin(Math.Clamp(facing.Y, -1f, 1f)) / Degrees;
            // Within its limits it faces the point; a point to its side turns it only to 70 degrees; one high above only 40 up.
            LookAt(head + new Vector3(2, 0, 10), "a point within the limits", facing => Vector3.Dot(facing, Vector3.Normalize(new Vector3(2, 0, 10))) > MathF.Cos(0.5f * Degrees));
            LookAt(head + new Vector3(10, 0, 0), "a point to the side", facing => MathF.Abs(Yaw(facing) - 70f) < 0.5f && MathF.Abs(Pitch(facing)) < 0.5f);
            LookAt(head + new Vector3(0, 10, 10), "a point high above", facing => MathF.Abs(Pitch(facing) - 40f) < 0.5f && MathF.Abs(Yaw(facing)) < 0.5f);
            LookAt(head + new Vector3(-6, -3, 8), "a point down to the left", facing => Vector3.Dot(facing, Vector3.Normalize(new Vector3(-6, -3, 8))) > MathF.Cos(0.5f * Degrees));
            // Another instance, followed as it moves; the spine and arm are not turned.
            Entity other = world.CreateEntity();
            world.Set(other, new TransformComponent { X = 13, Y = 0, Z = 15, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            Vector3 spine = Where(world, figure, "Spine"), hand = Where(world, figure, Hand);
            Check(ModelInstance.SetBoneLookAt(world, figure, "Head", other, height: 1.7f, blendSeconds: 0f), "Looking at an instance was refused.");
            ModelBoneTargets.UpdateScene(scene, 0.016f);
            Vector3 toOther = Vector3.Normalize(new Vector3(13, 1.7f, 15) - head);
            Check(Vector3.Dot(Facing(world, figure), toOther) > MathF.Cos(0.5f * Degrees) && Near(Where(world, figure, "Spine"), spine, 1e-5f)
                && Near(Where(world, figure, Hand), hand, 1e-5f), $"The head does not face the other instance ({Facing(world, figure)}), or turned more than the head.");
            world.GetRef<TransformComponent>(other).X = 7;
            ModelBoneTargets.UpdateScene(scene, 0.016f);
            toOther = Vector3.Normalize(new Vector3(7, 1.7f, 15) - head);
            Check(Vector3.Dot(Facing(world, figure), toOther) > MathF.Cos(0.5f * Degrees), $"The head did not follow the instance it looks at ({Facing(world, figure)}).");
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.LookAt.WeightZeroAndNeverAskingLeaveThePose", () => WithFigure(ctx, project, (scene, world, figure, game) =>
        {
            Check(ModelInstance.Play(world, figure, "Sway", loop: true, blendSeconds: 0f, startSeconds: 0.5f), "The clip did not play.");
            string[] bones = { "Root", "Spine", "Head", "RightArm", "RightForeArm", Hand };
            Matrix4x4[] Pose() => bones.Select(bone => ModelInstance.TryGetSocketWorld(world, figure, bone, out Matrix4x4 at) ? at : default).ToArray();
            Matrix4x4[] played = Pose();
            Check(ModelInstance.BoneTargets(world, figure) == null && ModelBoneTargets.Of(world, figure, create: false) == null,
                "Reading a figure's looks gave it some.");
            // Weight 0: a look and a reach that are on, and turn nothing.
            ModelInstance.SetBoneLookAt(world, figure, "Head", Place + new Vector3(10, 1.7f, 0), weight: 0f, blendSeconds: 0f);
            ModelInstance.SetBoneReach(world, figure, "RightHand", Place + new Vector3(0.5f, 1.9f, 0.3f), weight: 0f, blendSeconds: 0f);
            ModelBoneTargets.UpdateScene(scene, 0.016f);
            Check(Pose().SequenceEqual(played) && ModelInstance.BoneTargets(world, figure)?.Posing == false, "Weight 0 changed the pose.");
            // Turned on, only the head and the arm move: the clip still poses the spine.
            ModelInstance.SetBoneLookAt(world, figure, "Head", Place + new Vector3(10, 1.7f, 0), weight: 1f, blendSeconds: 0f);
            ModelBoneTargets.UpdateScene(scene, 0.016f);
            Matrix4x4[] looking = Pose();
            Check(looking[1] == played[1] && looking[0] == played[0] && looking[2] != played[2], "The look did not go on top of the clip (it moved the spine or not the head).");
            // Let go: back to the clip's pose exactly.
            ModelInstance.ClearBoneLookAt(world, figure, "Head", 0f);
            ModelInstance.ClearBoneReach(world, figure, "", 0f);
            ModelBoneTargets.UpdateScene(scene, 0.016f);
            Check(Pose().SequenceEqual(played) && ModelInstance.BoneTargets(world, figure)?.Look("Head") == null, "Letting go did not give the clip's pose back.");
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.Reach.HandLandsOnItsTarget", () => WithFigure(ctx, project, (scene, world, figure, game) =>
        {
            Vector3 shoulder = Where(world, figure, "RightArm");
            float upper = new Vector3(0.3f, -0.05f, 0).Length(), lower = new Vector3(0.3f, 0.05f, 0).Length();
            void Lands(Vector3 target, string what)
            {
                ModelBoneTargets.UpdateScene(scene, 0.016f);
                Vector3 elbow = Where(world, figure, "RightForeArm"), hand = Where(world, figure, Hand);
                Check(Near(hand, target) && MathF.Abs(Vector3.Distance(shoulder, elbow) - upper) < 1e-3f && MathF.Abs(Vector3.Distance(elbow, hand) - lower) < 1e-3f
                    && Near(Where(world, figure, "RightArm"), shoulder, 1e-4f), $"{what}: the hand is at {hand}, not {target} (elbow {elbow}).");
            }
            // By the bone's name without the rig's prefix ("RightHand" for "mixamorig:RightHand").
            Vector3 point = Place + new Vector3(0.55f, 1.75f, 0.25f);
            Check(ModelInstance.SetBoneReach(world, figure, "RightHand", point, blendSeconds: 0f), "The reach was refused.");
            Lands(point, "a point");
            // On top of a clip, and of an Animation Controller.
            ModelInstance.Play(world, figure, "Sway", loop: true, blendSeconds: 0f, startSeconds: 0.5f);
            shoulder = Where(world, figure, "RightArm");
            Check(MathF.Abs(shoulder.Y - 1.65f) < 1e-4f, $"The clip did not raise the shoulder ({shoulder}).");
            Lands(point, "a point while the clip plays");
            world.GetRef<ModelAnimatorComponent>(figure).Controller = new AnimationController();
            shoulder = Where(world, figure, "RightArm");
            Lands(point, "a point with an Animation Controller");
            world.GetRef<ModelAnimatorComponent>(figure).Controller = null;
            shoulder = Where(world, figure, "RightArm");
            // Another instance, raised by the height given, followed as it moves.
            Entity cup = world.CreateEntity();
            world.Set(cup, new TransformComponent { X = 10.45f, Y = 1.3f, Z = 5.3f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            Check(ModelInstance.SetBoneReach(world, figure, "RightHand", cup, height: 0.4f, blendSeconds: 0f), "Reaching for an instance was refused.");
            Lands(new Vector3(10.45f, 1.7f, 5.3f), "an instance");
            world.GetRef<TransformComponent>(cup).Z = 5.1f;
            Lands(new Vector3(10.45f, 1.7f, 5.1f), "an instance that moved");
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.LookAt.BlendReachesItsWeightInItsTime", () => WithFigure(ctx, project, (scene, world, figure, game) =>
        {
            const float Step = 0.25f;
            ModelInstance.SetBoneLookAt(world, figure, "Head", Place + new Vector3(10, 1.7f, 0), weight: 0.8f, blendSeconds: 0.5f);
            ModelInstance.SetBoneReach(world, figure, "RightHand", Place + new Vector3(0.55f, 1.75f, 0.25f), weight: 1f, blendSeconds: 1f);
            float Look() => ModelInstance.BoneLookAtAmount(world, figure, "Head");
            float Reach() => ModelInstance.BoneReachAmount(world, figure, "RightHand");
            ModelBoneTargets.UpdateScene(scene, Step);
            // Eased: half the time is half the way (0.4 of 0.8); the reach a quarter through its second is 0.15625 of the way.
            Check(MathF.Abs(Look() - 0.4f) < 1e-4f && MathF.Abs(Reach() - 0.15625f) < 1e-4f, $"A quarter second in: look {Look()} (0.4), reach {Reach()} (0.15625).");
            ModelBoneTargets.UpdateScene(scene, Step);
            Check(MathF.Abs(Look() - 0.8f) < 1e-5f, $"The look has not reached its weight in its time: {Look()}.");
            // A head turned 0.8 of the way to its 70 degree limit.
            float yaw = MathF.Atan2(Facing(world, figure).X, Facing(world, figure).Z) / Degrees;
            Check(MathF.Abs(yaw - 56f) < 0.5f, $"At weight 0.8 the head turned {yaw:F1} degrees, not 56.");
            ModelBoneTargets.UpdateScene(scene, Step); ModelBoneTargets.UpdateScene(scene, Step);
            Check(MathF.Abs(Reach() - 1f) < 1e-5f, $"The reach has not reached its weight in its second: {Reach()}.");
            // Out the same way, then gone.
            ModelInstance.ClearBoneLookAt(world, figure, "Head", 0.5f);
            ModelBoneTargets.UpdateScene(scene, Step);
            Check(MathF.Abs(Look() - 0.4f) < 1e-4f, $"Half way out the look is at {Look()} (0.4).");
            ModelBoneTargets.UpdateScene(scene, Step); ModelBoneTargets.UpdateScene(scene, Step);
            Check(Look() == 0f && ModelInstance.BoneTargets(world, figure)?.Look("Head") == null && Near(Facing(world, figure), Vector3.UnitZ, 1e-4f),
                "The look did not blend out and go.");
        }));

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.LookAt.ScriptLooksAndReachesThroughTheEngineApi", () => WithFigure(ctx, project, (scene, world, figure, game) =>
            Scripted(scene, world, figure, game)));
    }

    // The figure's own script: limits and blend times through the API, a look at a point by the
    // flat command, a reach by the API's At; the scene's frame works them out.
    private const string CreateScript = """
        Engine.Animation.LookAt.YawLimit = 50;
        Engine.Animation.LookAt.BlendTime = 0;
        lookMade = AnimationLookAtPoint(x + 10, y + 1.7, z);
        Engine.Animation.Reach.BlendTime = 0;
        reachMade = Engine.Animation.Reach.At(x + 0.55, y + 1.75, z + 0.25);
        lookOn = Engine.Animation.LookAt.Enabled;
        lookBone = Engine.Animation.LookAt.Bone;
        reachBone = Engine.Animation.Reach.Bone;
        yawLimit = Engine.Animation.LookAt.YawLimit;
        """;

    private static void Scripted(RuntimeScene scene, Genesis.Runtime.ECS.World world, Entity figure, ProjectGameContext game)
    {
        // With no game every command and value does nothing, and throws nothing.
        var savedGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = null;
        bool quiet = !PgslCommands.AnimationLookAtPoint(1, 2, 3) && !PgslCommands.AnimationReachToPoint("RightHand", 1, 2, 3)
            && !PgslCommands.AnimationLookAtInstance(5) && PgslCommands.AnimationLookAmount() == 0 && !PgslCommands.AnimationLookAtEnabled
            && PgslCommands.AnimationLookAtBone == "Head" && PgslCommands.AnimationReachBone == "RightHand" && PgslCommands.AnimationLookAtYawLimit == 70;
        PgslCommands.AnimationLookStop(); PgslCommands.AnimationReachStop(); PgslCommands.AnimationLookAtWeight = 0.5; PgslCommands.AnimationReachEnabled = true;
        PgslCommands.ActiveGameContext = savedGame;
        Check(quiet, "The look and reach commands did something with no game.");

        VMEngine.Initialize();
        var host = new ScriptHostSystem();
        host.SetContext(game);
        using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = CreateScript })) host.Attach(world, figure, "LookReachProbe");
        var vars = host.Instances.OfType<PgslBehavior>().Single(b => b.Entity == figure).GetVariablesSnapshot();
        string diagnostics = string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));
        bool Flag(string name) => vars.TryGetValue(name, out object? value) && (value is true || value is not (string or bool or null) && Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0);
        string Text(string name) => vars.TryGetValue(name, out object? value) ? value as string ?? "" : "";
        double Number(string name) => vars.TryGetValue(name, out object? value) && value is not string ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
        Check(Flag("lookMade") && Flag("reachMade") && Flag("lookOn") && Text("lookBone") == "Head" && Text("reachBone") == "RightHand" && Number("yawLimit") == 50 && diagnostics.Length == 0,
            $"The script's look and reach: made {Flag("lookMade")}/{Flag("reachMade")}, on {Flag("lookOn")}, bones {Text("lookBone")}/{Text("reachBone")}, limit {Number("yawLimit")}. {diagnostics}");

        scene.UpdateVariable(0.016f);
        Vector3 facing = Facing(world, figure);
        float yaw = MathF.Atan2(facing.X, facing.Z) / Degrees;
        Check(MathF.Abs(yaw - 50f) < 0.5f && Near(Where(world, figure, Hand), Place + new Vector3(0.55f, 1.75f, 0.25f)),
            $"After the scene's frame the head turned {yaw:F1} degrees (50) and the hand is at {Where(world, figure, Hand)}.");
        // The debug screen lists them for this instance, and the Inspector offers them for an Object.
        var context = host.Instances.OfType<PgslBehavior>().Single(b => b.Entity == figure).Context;
        var shown = EngineSettings.InstanceValues(world, figure, context).Select(value => value.Setting.Name).ToList();
        Check(shown.Contains("Engine.Animation.LookAt.Enabled") && shown.Contains("Engine.Animation.Reach.Weight")
            && EngineSettings.Available(EngineSettingScope.Instance).Any(setting => setting.Name == "Engine.Animation.LookAt.YawLimit")
            && PgslApi.TryGet("Engine.Animation.LookAtPoint", out _) && PgslApi.Reference().Any(row => row.Name == "Engine.Animation.Reach.Follow"),
            "The looks and reaches are missing from the debug screen, the Inspector's settings or the API's names.");
    }

    private static Vector3 Where(Genesis.Runtime.ECS.World world, Entity figure, string bone)
    {
        HeadlessHarness.Assert(ModelInstance.TryGetSocketPosition(world, figure, bone, out Vector3 at), $"The figure's {bone} could not be found.");
        return at;
    }

    // Which way the head faces in the world: the model's +Z carried through the head's pose.
    private static Vector3 Facing(Genesis.Runtime.ECS.World world, Entity figure)
    {
        HeadlessHarness.Assert(ModelInstance.TryGetSocketWorld(world, figure, "Head", out Matrix4x4 head), "The figure's head could not be found.");
        return Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, head));
    }

    private static void WithFigure(HeadlessContext ctx, string project, Action<RuntimeScene, Genesis.Runtime.ECS.World, Entity, ProjectGameContext> test)
    {
        using var scene = new RuntimeScene("Look and reach test");
        var game = new ProjectGameContext(project, scene, null, null, RoomAsset.Create("Looks", RoomDimension.ThreeD), null);
        var world = scene.World;
        Entity figure = world.CreateEntity();
        world.Set(figure, new TransformComponent { X = Place.X, Y = Place.Y, Z = Place.Z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        world.Set(figure, new ModelRendererComponent { ModelAsset = "Looker", ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        var oldContext = PgslCommands.BindContext(new PgslContext { InstanceId = figure.Id });
        var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project;
        try
        {
            test(scene, world, figure, game);
        }
        finally { PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
    }
}

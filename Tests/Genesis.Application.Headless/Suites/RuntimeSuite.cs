using Genesis.Runtime.Scripting;
using Genesis.Application.Runtime;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Runtime;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;
using Genesis.Shared.Commands;
using Genesis.Runtime.Scripting.Ast;
using Genesis.Runtime.Scripting.VM;
using Genesis.Physics;
using System.Numerics;
using EcsWorld = Genesis.Runtime.ECS.World;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

internal static class RuntimeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        EngineSystemsSuite.Run(ctx);
        HeadlessHarness.BeginMajor(ctx.Report, "Runtime");
        string logs = ctx.Logs;
        string captures = ctx.Captures;
        string workspace = ctx.Workspace;
        string outputRoot = ctx.OutputRoot;

        HeadlessHarness.RunCase(ctx.Report, "Runtime.1D.PgslVm", () =>
        {
            RuntimeOneDResult result = RuntimeHeadlessSuite.RunOneD(Path.Combine(logs, "runtime-1d.txt"));
            HeadlessHarness.Assert(result.InstructionCount > 0, "PGSL produced no instructions.");
            HeadlessHarness.Assert(result.Sum == 4d, $"1D sum expected 4, got {result.Sum}.");
            HeadlessHarness.Assert(result.Product == 12d, $"1D product expected 12, got {result.Product}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Physics.DecoupledMotorway", () =>
        {
            // R7.12: fixed-step alpha + pose blend for render; Bepu Timestep on a dedicated worker.
            Genesis.Runtime.Core.FixedTimestep clock = new() { FixedDelta = 1f / 60f };
            int steps = clock.Advance((1f / 60f) + (1f / 120f));
            HeadlessHarness.Assert(steps == 1, $"Expected one fixed step, got {steps}.");
            HeadlessHarness.Assert(
                Math.Abs(clock.InterpolationAlpha - 0.5f) < 0.02f,
                $"Interpolation alpha should be ~0.5 after a half-step leftover, got {clock.InterpolationAlpha:0.###}.");

            Transform3DComponent pose = Transform3DComponent.Default;
            pose.PreviousPosition = new Vector3(0f, 0f, 0f);
            pose.Position = new Vector3(10f, 0f, 0f);
            pose.PoseHistoryValid = 1;
            Matrix4x4 mid = pose.InterpolatedWorldMatrix(0.5f);
            HeadlessHarness.Assert(
                Math.Abs(mid.M41 - 5f) < 0.01f,
                $"Interpolated translation should be midway (5), got {mid.M41:0.###}.");

            int callerThread = Environment.CurrentManagedThreadId;
            using PhysicsMotorway motorway = new();
            int observedWorker = -1;
            motorway.Run(() => observedWorker = Environment.CurrentManagedThreadId);
            HeadlessHarness.Assert(
                observedWorker != callerThread && observedWorker == motorway.LastWorkerThreadId,
                "Physics motorway must execute Timestep work on its dedicated worker thread.");

            using RuntimeScene scene = new("Physics motorway scene");
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset
            {
                GravityStrength = 0f,
                AirDrag = 0f,
                AllowSleep = false,
            });
            Entity body = scene.CreateEntity(new Vector3(0f, 2f, 0f));
            scene.World.Set(body, RigidBodyComponent.DynamicBox(new Vector3(0.5f), mass: 1f));
            scene.UpdateFixed(1f / 60f);
            HeadlessHarness.Assert(
                scene.PhysicsMotorway.LastWorkerThreadId != 0
                && scene.PhysicsMotorway.LastWorkerThreadId != callerThread,
                "RuntimeScene.UpdateFixed must step Bepu on the physics motorway worker.");
            HeadlessHarness.Assert(
                scene.World.GetRef<Transform3DComponent>(body).PoseHistoryValid == 1,
                "SyncTransforms must record a previous pose for render interpolation.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.EngineNamespaces", () =>
        {
            bool originalLodScaling = Engine.GetLodScaling();
            bool originalFrustumCulling = Engine.GetFrustumCulling();
            try
            {
                const string source = """
                    from Engine.Rendering:
                        SetLodScaling(false);
                        SetFrustumCulling(false);
                    Engine.Rendering.SetLodScaling(true);
                    var namespaceProbe = Engine.Rendering.GetLodScaling();
                    """;

                // AST/editor path: indentation form becomes a real namespace node and strict
                // validation recognises both lexical and fully-qualified calls.
                ScriptAst ast = PgslAstBuilder.Parse(source);
                NamespaceBlockStmt namespaceBlock = ast.Body.OfType<NamespaceBlockStmt>().FirstOrDefault()
                    ?? throw new InvalidOperationException("PGSL did not parse 'from Engine.Rendering:' as a namespace block.");
                HeadlessHarness.Assert(
                    namespaceBlock.Namespace == "Engine.Rendering" && namespaceBlock.Body.Body.Count == 2,
                    "PGSL namespace block did not preserve its namespace/body.");
                HeadlessHarness.Assert(
                    namespaceBlock.Body.Body.OfType<ExprStmt>()
                        .Select(statement => statement.Expression)
                        .OfType<CallExpr>()
                        .All(call => call.Namespace == "Engine.Rendering"),
                    "Bare calls inside the namespace block did not retain lexical namespace resolution.");

                PgslCommands.WarmRegistry();
                PgslCommandInfo namespacedDraw = PgslCommandRegistry.TryGet("Engine.Rendering", "DrawModel3D")
                    ?? throw new InvalidOperationException("Engine.Rendering.DrawModel3D was not registered.");
                HeadlessHarness.Assert(
                    namespacedDraw.QualifiedName == "Engine.Rendering.DrawModel3D"
                    && PgslCommandRegistry.TryGet("DrawModel3D") != null,
                    "Namespaced PGSL command did not retain its legacy flat alias.");

                List<PgslDiagnostic> diagnostics = PgslSemanticChecker.Check(
                    ast,
                    new PgslSemanticOptions { Strict = true });
                HeadlessHarness.Assert(
                    diagnostics.All(diagnostic => diagnostic.Severity != PgslDiagnostic.Kind.Error),
                    "Strict PGSL namespace validation failed: " + string.Join(" | ", diagnostics));

                // Production VM path: prove both forms actually dispatch, not just parse.
                VMEngine.ClearCompileCache();
                CompileResult compiled = VMEngine.Compile(source)
                    ?? throw new InvalidOperationException("Namespaced PGSL failed to compile.");
                PgslVm vm = VMEngine.CreateVm(debug: false);
                vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
                Dictionary<string, object> variables = vm.GetVariables();

                HeadlessHarness.Assert(Engine.GetLodScaling(),
                    "Fully-qualified Engine.Rendering.SetLodScaling did not execute.");
                HeadlessHarness.Assert(!Engine.GetFrustumCulling(),
                    "Bare SetFrustumCulling inside from Engine.Rendering did not execute.");
                HeadlessHarness.Assert(
                    variables.TryGetValue("namespaceProbe", out object? namespaceProbe)
                    && Convert.ToBoolean(namespaceProbe),
                    "Fully-qualified Engine.Rendering getter did not return through the PGSL VM.");
            }
            finally
            {
                Engine.SetLodScaling(originalLodScaling);
                Engine.SetFrustumCulling(originalFrustumCulling);
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.ScriptDiscoveryExcludesOutputsAndCachesEmptyProjects", () =>
        {
            string root = Path.Combine(workspace, "ScriptDiscovery");
            Directory.CreateDirectory(root);
            try
            {
                foreach (string folder in new[] { "Assets/Scripts", "Build/Player", ".build/stage", "Dist", "TestResults", "bin", "obj" })
                {
                    string directory = Path.Combine(root, folder);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, folder == "Assets/Scripts" ? "SourceHelper.pgsl" : "OutputHelper.pgsl"), "return 1;");
                }
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.EnsureProjectLoaded(root);
                HeadlessHarness.Assert(ScriptAssetRegistry.GetScriptNames().SequenceEqual(new[] { "SourceHelper" }),
                    "Script discovery must load nested project assets and exclude generated output copies.");

                string empty = Path.Combine(root, "Empty");
                Directory.CreateDirectory(empty);
                ScriptAssetRegistry.LoadFromProject(empty);
                File.WriteAllText(Path.Combine(empty, "Later.pgsl"), "return 2;");
                ScriptAssetRegistry.EnsureProjectLoaded(empty);
                HeadlessHarness.Assert(ScriptAssetRegistry.GetScriptNames().Count == 0,
                    "An empty project was rescanned during per-behaviour initialization.");
                ScriptAssetRegistry.LoadFromProject(empty);
                HeadlessHarness.Assert(ScriptAssetRegistry.TryGet("Later", out _),
                    "An explicit project reload must discover newly added scripts.");

                ScriptHostSystem host = new();
                EcsWorld world = new();
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent());
                using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = "var probe = 1;" }))
                    host.Attach(world, entity, "UnwiredPreviewProbe");
                HeadlessHarness.Assert(ScriptAssetRegistry.TryGet("Later", out _),
                    "An unwired preview replaced the project registry by scanning its working directory.");
            }
            finally
            {
                ScriptAssetRegistry.ClearCache();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.AnimationStateCommands", () =>
        {
            PgslContext context = new() { ModelAsset = "Assets/Models/Hero.model.json" };
            PgslContext previous = PgslCommands.BindContext(context);
            try
            {
                PgslCommands.ModelSet("Assets/Models/Hero.model.json");
                PgslCommands.ModelAnimationPlay("Idle", loop: true, blendSeconds: 0);
                context.ModelAnimationTime = 0.75;
                PgslCommands.AnimationStatePlay("Run", loop: true, blendSeconds: 0.4);
                PgslCommands.AnimationStateSetFloat("speed", 7.5);
                PgslCommands.AnimationStateSetBool("grounded", true);
                PgslCommands.AnimationStateSetTrigger("jump");
                PgslCommands.VariableSet("score", 42);
                PgslCommands.ModelKeepPreviousTransform = true;
                PgslCommands.AnimationStateSetSpeed(1.75);

                HeadlessHarness.Assert(
                    context.ModelBindingTouched && context.ModelAnimationTouched
                    && context.ModelAnimationClip == "Run"
                    && context.ModelAnimationPreviousClip == "Idle"
                    && Math.Abs(context.ModelAnimationPreviousTime - 0.75) < 0.001
                    && Math.Abs(context.ModelAnimationBlendDuration - 0.4) < 0.001,
                    "Model PGSL playback did not preserve the outgoing clip for a real transition.");
                HeadlessHarness.Assert(
                    Math.Abs(PgslCommands.AnimationStateGetFloat("speed") - 7.5) < 0.001
                    && PgslCommands.AnimationStateGetBool("grounded")
                    && PgslCommands.AnimationStateConsumeTrigger("jump")
                    && !PgslCommands.AnimationStateConsumeTrigger("jump")
                    && Math.Abs(PgslCommands.VariableGet("score") - 42) < 0.001
                    && PgslCommands.ModelKeepPreviousTransform
                    && context.ModelTransformPolicyTouched
                    && Math.Abs(context.ModelAnimationSpeed - 1.75) < 0.001,
                    "Typed animation-state parameters or one-shot triggers did not round-trip.");

                context.ModelAsset = string.Empty;
                context.SpriteIndex = "Assets/Images/Hero.image.json";
                context.ImageIndex = 3;
                context.SpriteAnimationTag = "Walk";
                context.SpriteAnimationActive = true;
                PgslCommands.AnimationStatePlay("Run", loop: true, blendSeconds: 0.25);
                HeadlessHarness.Assert(
                    context.SpriteTransitionTouched
                    && context.SpriteTransitionPreviousImage.EndsWith("Hero.image.json", StringComparison.Ordinal)
                    && Math.Abs(context.SpriteTransitionPreviousFrame - 3) < 0.001
                    && Math.Abs(context.SpriteTransitionDuration - 0.25) < 0.001
                    && context.SpriteAnimationTag == "Run",
                    "The shared animation-state command did not create a 2D frame cross-fade.");

                GModelAsset asset = new()
                {
                    Rig = new GModelRig
                    {
                        Bones = [new GModelBone { Name = "Root", BindLocal = Matrix4x4.Identity }],
                    },
                    Animations =
                    [
                        new GModelAnimationClip
                        {
                            Name = "Idle", Fps = 1f,
                            Frames = [new GModelAnimationFrame { LocalBoneTransforms = [Matrix4x4.CreateTranslation(0, 0, 0)] }],
                        },
                        new GModelAnimationClip
                        {
                            Name = "Run", Fps = 1f,
                            Frames = [new GModelAnimationFrame { LocalBoneTransforms = [Matrix4x4.CreateTranslation(8, 0, 0)] }],
                        },
                    ],
                };
                Matrix4x4[] blended = GModelPrimitiveFactory.EvaluateAnimatedLocals(
                    asset,
                    new RuntimeModelAnimationState("Run", 0, 1, true,
                        previousClipName: "Idle", previousTimeSeconds: 0, blendFactor: 0.25f));
                HeadlessHarness.Assert(blended.Length == 1 && Math.Abs(blended[0].M41 - 2f) < 0.001f,
                    $"Skeletal animation blend expected X=2, got {blended.ElementAtOrDefault(0).M41:0.###}.");
                Matrix4x4[] preserved = GModelPrimitiveFactory.EvaluateAnimatedLocals(
                    asset,
                    new RuntimeModelAnimationState("Run", 0, 1, true,
                        previousClipName: "Idle", previousTimeSeconds: 0, blendFactor: 0.25f,
                        preserveRootTransform: true));
                HeadlessHarness.Assert(preserved.Length == 1 && Math.Abs(preserved[0].M41) < 0.001f,
                    "Transform preservation did not hold the animated skeleton root at its authored bind transform.");

                string[] required =
                [
                    "ModelSet", "ModelAnimationPlay", "AnimationStatePlay",
                    "AnimationStateSetFloat", "AnimationStateSetBool", "AnimationStateSetTrigger",
                    "AnimationStateSetSpeed", "AnimationStateHasFinished", "VariableSet",
                ];
                HeadlessHarness.Assert(required.All(name => PgslCommandRegistry.TryGet(name)?.IsImplemented == true),
                    "One or more animation-state commands are absent from the implemented PGSL catalogue.");
                HeadlessHarness.Assert(
                    PgslCommandRegistry.TryGet("Engine.Rendering.Models.KeepPreviousTransform")?.IsProperty == true,
                    "The transform-preservation property is absent from the namespaced PGSL catalogue.");

                VMEngine.Initialize();
                EcsWorld world = new();
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent
                {
                    X = 12, Y = 34, Z = 56,
                    RotationX = 10, RotationY = 20, RotationZ = 30, Rotation = 30,
                    ScaleX = 2, ScaleY = 3, ScaleZ = 4,
                });
                world.Set(entity, new ModelRendererComponent
                {
                    ModelAsset = "Assets/Models/Old.model.json",
                    ScaleX = 0.5f, ScaleY = 1f, ScaleZ = 1.5f,
                    CastShadows = true, ReceiveShadows = true,
                });
                ScriptHostSystem host = new();
                using (host.UseEventSources(new Dictionary<string, string>
                {
                    ["Create"] = "Engine.Rendering.Models.KeepPreviousTransform = true;\n"
                                 + "ModelSet(\"Assets/Models/Hero.model.json\");\n"
                                 + "ModelAnimationPlay(\"Run\", true, 0.2);",
                }))
                {
                    host.Attach(world, entity, "AnimationStateProbe");
                }
                HeadlessHarness.Assert(
                    world.Has<ModelRendererComponent>(entity)
                    && world.Has<ModelAnimatorComponent>(entity)
                    && world.Has<Draw3DComponent>(entity)
                    && world.GetRef<ModelRendererComponent>(entity).ModelAsset.EndsWith("Hero.model.json", StringComparison.Ordinal)
                    && world.GetRef<ModelRendererComponent>(entity).KeepPreviousTransform
                    && Math.Abs(world.GetRef<ModelRendererComponent>(entity).ScaleX - 0.5f) < 0.001f
                    && Math.Abs(world.GetRef<ModelRendererComponent>(entity).ScaleY - 1f) < 0.001f
                    && Math.Abs(world.GetRef<ModelRendererComponent>(entity).ScaleZ - 1.5f) < 0.001f
                    && world.GetRef<ModelAnimatorComponent>(entity).ClipName == "Run"
                    && Math.Abs(world.GetRef<TransformComponent>(entity).X - 12) < 0.001f
                    && Math.Abs(world.GetRef<TransformComponent>(entity).RotationY - 20) < 0.001f
                    && Math.Abs(world.GetRef<TransformComponent>(entity).ScaleZ - 4) < 0.001f,
                    "PGSL model commands did not preserve and synchronise the runtime ECS transform policy.");
            }
            finally
            {
                PgslCommands.BindContext(previous);
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.LightEmitterCommands", () =>
        {
            PgslCommands.WarmRegistry();
            string[] required =
            [
                "LightEmitterEnable", "LightEmitterSetRadius", "LightEmitterSetIntensity",
                "LightEmitterSetFalloff", "LightEmitterSetColor", "LightEmitterSetSecondaryColor",
                "LightEmitterSetTertiaryColor", "LightEmitterSetColorCount",
                "LightEmitterSetOffset", "LightEmitterSetAction",
            ];
            HeadlessHarness.Assert(
                required.All(name => PgslCommandRegistry.TryGet(name)?.IsImplemented == true)
                && required.All(name =>
                    PgslCommandRegistry.TryGet("Engine.Rendering.Lights", name)?.IsImplemented == true),
                "One or more Light Emitter commands are absent from the flat or namespaced PGSL catalogue.");

            PgslContext context = new();
            PgslContext previous = PgslCommands.BindContext(context);
            try
            {
                PgslCommands.LightEmitterSetRadius(14.5);
                PgslCommands.LightEmitterSetIntensity(3.25);
                PgslCommands.LightEmitterSetFalloff(4.5);
                PgslCommands.LightEmitterSetColor(1, 0.6, 0.2);
                PgslCommands.LightEmitterSetSecondaryColor(0.2, 0.5, 1);
                PgslCommands.LightEmitterSetTertiaryColor(0.8, 0.15, 0.9);
                PgslCommands.LightEmitterSetColorCount(3);
                PgslCommands.LightEmitterSetOffset(1, 2, 3);
                PgslCommands.LightEmitterSetAction("Glow", 2.5, 0.7);

                HeadlessHarness.Assert(
                    context.LightEmitterTouched
                    && Math.Abs(context.LightEmitterRadius - 14.5) < 0.001
                    && Math.Abs(context.LightEmitterIntensity - 3.25) < 0.001
                    && Math.Abs(context.LightEmitterFalloff - 4.5) < 0.001
                    && context.LightEmitterColorCount == 3
                    && context.LightEmitterOffset == new Vector3(1, 2, 3)
                    && context.LightEmitterAction == "Glow"
                    && Math.Abs(context.LightEmitterActionSpeed - 2.5) < 0.001
                    && Math.Abs(context.LightEmitterActionAmount - 0.7) < 0.001,
                    "Light Emitter PGSL commands did not update their retained object context.");

                VMEngine.Initialize();
                EcsWorld world = new();
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent());
                ScriptHostSystem host = new();
                using (host.UseEventSources(new Dictionary<string, string>
                {
                    ["Create"] = "Engine.Rendering.Lights.LightEmitterSetRadius(12);\n"
                                 + "Engine.Rendering.Lights.LightEmitterSetFalloff(3);\n"
                                 + "Engine.Rendering.Lights.LightEmitterSetColorCount(2);\n"
                                 + "Engine.Rendering.Lights.LightEmitterSetAction(\"Pulse\", 4, 0.5);",
                }))
                {
                    host.Attach(world, entity, "LightEmitterProbe");
                }
                HeadlessHarness.Assert(
                    world.Has<PointLightComponent>(entity)
                    && Math.Abs(world.GetRef<PointLightComponent>(entity).Radius - 12f) < 0.001f
                    && Math.Abs(world.GetRef<PointLightComponent>(entity).Falloff - 3f) < 0.001f
                    && world.GetRef<PointLightComponent>(entity).ColorCount == 2
                    && world.GetRef<PointLightComponent>(entity).Action == LightEmitterAction.Pulse,
                    "Namespaced PGSL did not create and synchronise a persistent Light Emitter component.");
            }
            finally
            {
                PgslCommands.BindContext(previous);
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.MathAndGameplay", () =>
        {
            PgslGameplayResult game = PgslGameplaySuite.Run();

            // Every Math command the sample game relies on must return the right value.
            HeadlessHarness.Assert(
                game.MathChecksPassed == PgslGameplaySuite.MathCheckCount,
                $"PGSL Math checks: {game.MathChecksPassed}/{PgslGameplaySuite.MathCheckCount} passed — a Math command is wrong or missing.");

            // The player circle fell, landed on the platform, and ran to its right edge.
            HeadlessHarness.Assert(
                Math.Abs(game.PlayerY - (300 - 18)) < 0.51,
                $"Player should rest on the platform top (y≈282), got {game.PlayerY:0.##}.");
            HeadlessHarness.Assert(
                game.PlayerX > 500,
                $"Player should have run right along the platform, got x={game.PlayerX:0.##}.");

            // Circle-vs-circle collection scored every pickup.
            HeadlessHarness.Assert(game.PickupsRemaining == 0, $"Expected all 3 pickups collected, {game.PickupsRemaining} remain.");
            HeadlessHarness.Assert(game.Score == 30, $"Expected score 30, got {game.Score}.");

            // The HUD string a designer would draw in the corner.
            HeadlessHarness.Assert(game.HudText == "SCORE 30", $"HUD text wrong: '{game.HudText}'.");

            // …and that HUD text now actually reaches a draw surface (NEXT-033): before
            // PgslRenderDrawSurface existed, nothing implemented IPgslDrawSurface so every PGSL
            // draw call silently vanished.
            PgslHudDrawResult hud = PgslGameplaySuite.RunHudDraw();
            HeadlessHarness.Assert(hud.TextCalls == 1, $"Expected 1 HUD text call, got {hud.TextCalls} — PGSL drawing is not reaching a surface.");
            HeadlessHarness.Assert(hud.FirstText == "SCORE 30", $"HUD drew '{hud.FirstText}' instead of 'SCORE 30'.");
            HeadlessHarness.Assert(
                hud.FirstX == 16f && hud.FirstY == 12f,
                $"HUD text should land at the top-left corner (16,12), got ({hud.FirstX},{hud.FirstY}).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.OneD", () =>
        {
            // 1D: variables and maths only, with NO renderer and NO game attached. Game logic has to
            // run in that state or nothing else in the engine can be tested in isolation.
            PgslOneDResult result = PgslDimensionSuite.RunOneD();

            HeadlessHarness.Assert(
                result.ChecksPassed == result.ChecksAttempted,
                $"1D PGSL checks: {result.ChecksPassed}/{result.ChecksAttempted} passed — a command returned the wrong value.");
            HeadlessHarness.Assert(result.Sum == 55, $"for-loop sum expected 55, got {result.Sum}.");
            HeadlessHarness.Assert(result.Product == 120, $"for-loop product expected 120, got {result.Product}.");
            HeadlessHarness.Assert(result.Hypotenuse == 5, $"Sqrt/Power expected 5, got {result.Hypotenuse}.");
            HeadlessHarness.Assert(result.ArraySum == 42, $"Named array sum expected 42, got {result.ArraySum}.");
            HeadlessHarness.Assert(result.ListSum == 21, $"ds_list sum expected 21, got {result.ListSum}.");
            HeadlessHarness.Assert(result.GridSum == 39, $"ds_grid region sum expected 39, got {result.GridSum}.");

            // Dynamic string building — the gap that forced the 2D acceptance gate to draw a constant.
            HeadlessHarness.Assert(
                result.Label == "SCORE 42",
                $"String building produced '{result.Label}', expected 'SCORE 42' — dynamic HUD text is broken.");
            HeadlessHarness.Assert(
                result.AlarmRemaining == 1 && result.AlarmFiredOnTime,
                $"Alarm should have 1 frame left after 4 ticks of 5 and then fire; remaining={result.AlarmRemaining}, fired={result.AlarmFiredOnTime}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.TwoD", () =>
        {
            // 2D: the same VM drives shapes that MOVE, plus a HUD. Every draw call is recorded, so a
            // failure names the missing call instead of reporting "the picture looks different".
            PgslTwoDResult result = PgslDimensionSuite.RunTwoD(frames: 120);

            HeadlessHarness.Assert(result.ShapeCalls > 0, "PGSL 2D drawing produced no shapes at all.");
            HeadlessHarness.Assert(
                result.CircleCalls >= result.Frames * 4,
                $"Expected at least 4 circles per frame ({result.Frames * 4}), got {result.CircleCalls}.");
            HeadlessHarness.Assert(
                result.RectangleCalls >= result.Frames,
                $"Expected at least one rectangle per frame, got {result.RectangleCalls} over {result.Frames}.");

            // The shapes must VISIBLY move. A script that runs but produces a static picture is the
            // failure mode the walk-cycle bug (NEXT-036) taught us to assert numerically.
            HeadlessHarness.Assert(
                result.OrbitSamples == result.Frames,
                $"Expected {result.Frames} motion samples, got {result.OrbitSamples}.");
            HeadlessHarness.Assert(
                result.OrbitSpread > 100,
                $"The orbiting shape only moved {result.OrbitSpread:0.#}px vertically — it is effectively static. "
                + "(PGSL Sin/Cos take DEGREES; radian-scale multipliers give a ~18px sweep.)");
            HeadlessHarness.Assert(
                Math.Abs(result.FinalPlayerX - 80) > 50,
                $"The player never moved from its spawn x, ending at {result.FinalPlayerX:0.#}.");

            // HUD: two lines, and the score must be the one the SCRIPT accumulated.
            HeadlessHarness.Assert(
                result.HudTextCalls == 2,
                $"Expected 2 HUD text calls, got {result.HudTextCalls}.");
            HeadlessHarness.Assert(
                result.HudText.StartsWith("SCORE ", StringComparison.Ordinal) && result.HudText != "SCORE 0",
                $"HUD drew '{result.HudText}' — it must show the score the script accumulated, not a placeholder.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.ThreeD", () =>
        {
            // 3D: script queues real geometry and uses the 3D maths family.
            PgslThreeDResult result = PgslDimensionSuite.RunThreeD();

            HeadlessHarness.Assert(result.CubeCalls > 10, $"PGSL 3D drawing queued only {result.CubeCalls} primitives.");
            HeadlessHarness.Assert(
                Math.Abs(result.FloorWidth - 40) < 0.01,
                $"DrawFloor3D produced width {result.FloorWidth}, expected 40.");

            // Conventions, asserted rather than assumed: 3-4-5 distance, yaw 0 looks down +Z.
            HeadlessHarness.Assert(
                Math.Abs(result.DistanceCheck - 5) < 0.001,
                $"DistanceBetweenPoints3D(0,0,0 -> 3,4,0) gave {result.DistanceCheck}, expected 5.");
            HeadlessHarness.Assert(
                Math.Abs(result.YawCheck) < 0.001,
                $"A point straight down +Z should be yaw 0, got {result.YawCheck} — the yaw convention has drifted.");
            HeadlessHarness.Assert(
                Math.Abs(result.ForwardZCheck - 1) < 0.001,
                $"ForwardZ(0,0) should be 1 (yaw 0 looks down +Z), got {result.ForwardZCheck}.");

            // …and 3D commands must draw NOTHING outside a 3D pass, or they would corrupt the 2D frame.
            HeadlessHarness.Assert(
                result.RespectedInactivePass,
                "3D drawing commands queued geometry while the surface reported no active 3D pass.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.Playable3DTemplate", () =>
        {
            string parent = Path.Combine(workspace, "ThreeDTemplate");
            Directory.CreateDirectory(parent);
            ProjectService service = new();
            ProjectSession project = service.CreateProject(parent, "3D World Check", "3D");
            string root = project.RootPath;

            HeadlessHarness.Assert(
                ProjectTemplateCatalog.Find("3D")?.Available == true,
                "The implemented 3D template is still unavailable in the Project Hub.");
            foreach (string expected in new[]
                     {
                         "Assets/Objects/Player.object.json",
                         "Assets/Objects/Player/Create.pgsl",
                         "Assets/Objects/Player/Step.pgsl",
                         "Assets/Objects/Player/Draw.pgsl",
                         "Assets/Objects/Player/DrawGui.pgsl",
                         "Assets/Objects/PineTree.object.json",
                         "Assets/Objects/PineTree/Draw.pgsl",
                         "Assets/Objects/OakTree.object.json",
                         "Assets/Objects/OakTree/Draw.pgsl",
                         "Assets/Objects/Boulder.object.json",
                         "Assets/Objects/Boulder/Draw.pgsl",
                         "Assets/Objects/Beacon.object.json",
                         "Assets/Objects/Beacon/Draw.pgsl",
                         "Assets/Rooms/World.room.json",
                         "Assets/Terrain/Ground.terrain.json",
                         "Assets/Terrain/Ground.terrain.json.gterrain",
                         "Assets/Images/GroundTexture.image.json",
                         "Assets/Images/BarkTexture.image.json",
                         "Assets/Images/FoliageTexture.image.json",
                     })
            {
                HeadlessHarness.Assert(
                    File.Exists(Path.Combine(root, expected.Replace('/', Path.DirectorySeparatorChar))),
                    $"The 3D template did not create '{expected}'.");
            }

            ProjectSession reopened = service.OpenProject(root);
            HeadlessHarness.Assert(
                reopened.Manifest.StartRoom == "Assets/Rooms/World.room.json"
                && File.Exists(Path.Combine(root, reopened.Manifest.StartRoom.Replace('/', Path.DirectorySeparatorChar))),
                "The 3D template did not persist its playable room as the F5 start room.");
            HeadlessHarness.Assert(
                !service.Validate(reopened).Any(issue => issue.Severity == ProjectValidationSeverity.Error),
                "The generated 3D project failed project validation.");

            string templatePlayer = Path.Combine(root, "Build", "TemplatePlayer");
            ProjectRunLauncher.CompileOutcome compile =
                ProjectRunLauncher.CompileScripts(root, templatePlayer);
            HeadlessHarness.Assert(
                compile.Success,
                "The generated 3D template failed the File -> Run PGSL gate: "
                + (compile.ErrorMessage ?? "unknown compiler failure"));

            string roomPath = Path.Combine(root, "Assets", "Rooms", "World.room.json");
            RoomAsset room = RoomAssetLoader.Parse(roomPath);
            HeadlessHarness.Assert(
                room.Dimension == RoomDimension.ThreeD
                && room.Layers.Any(layer => layer.Name == "Environment")
                && room.Layers.Any(layer => layer.Name == "Gameplay"),
                "The template room did not round-trip as a layered 3D room.");

            RoomNode? terrainNode = room.Nodes.FirstOrDefault(n => n.Kind == RoomNodeKind.Terrain);
            HeadlessHarness.Assert(
                terrainNode is not null && terrainNode.Terrain is not null,
                "The 3D room is missing its Terrain node.");

            IReadOnlyDictionary<string, string> events = ObjectEventStore.Load(
                Path.Combine(root, "Assets", "Objects", "Player.object.json"));
            foreach (string eventId in new[] { "Create", "Step", "Draw", "DrawGui" })
                HeadlessHarness.Assert(events.ContainsKey(eventId), $"The Player is missing its {eventId} event.");

            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            ScriptAssetRegistry.LoadFromProject(root);
            PgslProfiler.Reset();
            PgslProfiler.Enabled = true;
            try
            {
                using RuntimeScene scene = new("3D template acceptance")
                {
                    Input = new InputState(),
                };
                var gameContext = new ProjectGameContext(
                    root,
                    scene,
                    renderer: null,
                    window: null,
                    room,
                    logger: null);
                var scriptHost = new ScriptHostSystem();
                scriptHost.SetContext(gameContext);

                RoomBuildResult built = new RoomSceneBuilder(root, scriptHost).Build(scene, room);
                RoomNode playerNode = room.Nodes.Single(node => node.Name == "Player 1");
                Entity player = built.EntitiesByNodeId[playerNode.Id];
                ref TransformComponent transform = ref scene.World.GetRef<TransformComponent>(player);
                float startX = transform.X;
                float startZ = transform.Z;

                // Create must frame the view with a radians-based look-at camera.
                HeadlessHarness.Assert(
                    Math.Abs(scene.Camera3D.Yaw) <= Math.PI * 2
                    && Math.Abs(scene.Camera3D.Pitch) <= Math.PI / 2,
                    "SetCameraTarget did not aim the 3D template camera using radians.");

                scene.Input.OnKeyDown(Key.D);
                scene.Input.OnKeyDown(Key.W);
                for (int frame = 0; frame < 12; frame++)
                {
                    scriptHost.Update(1f / 60f);
                    scene.Input.NextFrame();
                }
                HeadlessHarness.Assert(
                    transform.X > startX + 0.3f || transform.Z > startZ + 0.3f,
                    $"The playable 3D controller did not move on XZ: ({transform.X:0.00}, {transform.Z:0.00}).");

                scene.Input.OnKeyUp(Key.D);
                scene.Input.OnKeyUp(Key.W);
                scene.Input.NextFrame();
                float beforeJump = transform.Y;
                scene.Input.OnKeyDown(Key.Space);
                scriptHost.Update(1f / 60f);
                scene.Input.NextFrame();
                HeadlessHarness.Assert(
                    transform.Y > beforeJump + 0.1f,
                    $"The playable 3D controller did not jump: y={transform.Y:0.000}.");
                scene.Input.OnKeyUp(Key.Space);
                scene.Input.NextFrame();

                var hud = new DiagnosticHudCanvas();
                scriptHost.DispatchPgslGuiDraw(renderer: null, hud);
                HeadlessHarness.Assert(
                    hud.Texts.Any(text => text.Contains("WASD", StringComparison.Ordinal))
                    && hud.Texts.Any(text => text.Contains("3D WORLD EXPLORER", StringComparison.Ordinal)),
                    "The 3D template HUD did not explain the controls and report the title.");
                HeadlessHarness.Assert(
                    scriptHost.LastError is null,
                    $"A generated 3D template event failed at runtime: {scriptHost.LastError}");

                // Run the authored Draw events across scene entities to prove the
                // trees and landscape geometry are real runtime meshes.
                var surface = new RecordingDrawSurface { Is3DActive = true };
                foreach (var b in scriptHost.Instances)
                {
                    if (b is PgslBehavior pgsl && pgsl.HasWorldDrawScript)
                        pgsl.OnDrawWorldFrame(surface);
                }
                HeadlessHarness.Assert(
                    surface.Cubes.Count >= 10 && surface.Spheres.Count >= 5,
                    $"The 3D template drew only {surface.Cubes.Count} boxes and {surface.Spheres.Count} spheres.");

                IReadOnlyList<PgslEventProfile> profile = PgslProfiler.Snapshot();
                HeadlessHarness.Assert(
                    profile.Any(entry => entry.ObjectName == SandboxTemplate.PlayerName && entry.EventName == "Step")
                    && profile.All(entry => entry.FailedCalls == 0),
                    "The Debug profiler did not capture the playable 3D template without failures.");
            }
            finally
            {
                PgslProfiler.Enabled = false;
                PgslProfiler.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.PerformanceBudgets", () =>
        {
            PgslPerformanceReport performance = PgslPerformanceSuite.Run(samples: 3);
            File.WriteAllText(
                Path.Combine(logs, "pgsl-performance.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    performance,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var budgets = new Dictionary<string, (double Microseconds, long Bytes)>(StringComparer.Ordinal)
            {
                ["ArithmeticAndControlFlow"] = (4_000, 400_000),
                ["InstanceRegisters"] = (3_000, 300_000),
                ["NativeCommands"] = (3_000, 300_000),
                ["UserFunctions"] = (1_500, 150_000),
            };

            foreach (PgslWorkloadResult workload in performance.Workloads)
            {
                (double microseconds, long bytes) = budgets[workload.Name];
                HeadlessHarness.Assert(
                    workload.MedianMicrosecondsPerExecution < microseconds,
                    $"PGSL workload {workload.Name} took {workload.MedianMicrosecondsPerExecution:N0} us; budget {microseconds:N0} us.");
                HeadlessHarness.Assert(
                    workload.AllocatedBytesPerExecution < bytes,
                    $"PGSL workload {workload.Name} allocated {workload.AllocatedBytesPerExecution:N0} bytes; budget {bytes:N0} bytes.");
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.CommandEffects", () =>
        {
            // The command sweep below proves callability. These checks prove the four commands that
            // used to be explicitly unfinished now change real state or submit the requested asset.
            PgslRecordingDrawSurface draw = new() { Is3DActive = true };
            PgslContext drawContext = new()
            {
                DrawSurface = draw,
                DrawColor = System.Drawing.Color.FromArgb(255, 20, 40, 60),
                DrawAlpha = 0.75,
            };
            PgslContext previousDraw = PgslCommands.BindContext(drawContext);
            try
            {
                PgslCommands.DrawSphere3D(1, 2, 3, 4);
                PgslCommands.DrawModel3D("Hero.gmodel", 5, 6, 7, 2);
            }
            finally
            {
                PgslCommands.BindContext(previousDraw);
            }

            HeadlessHarness.Assert(
                draw.Spheres.Count == 1 && Math.Abs(draw.Spheres[0].Radius - 4f) < 0.001f,
                "DrawSphere3D did not submit a sphere with the requested radius.");
            HeadlessHarness.Assert(
                draw.Models.Count == 1
                && draw.Models[0].Name == "Hero.gmodel"
                && Math.Abs(draw.Models[0].Scale - 2f) < 0.001f,
                "DrawModel3D did not submit the requested model asset and scale.");

            using RuntimeScene scene = new("PGSL physics effects");
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset
            {
                GravityStrength = 0f,
                AirDrag = 0f,
                AllowSleep = false,
            });

            Entity dynamicBody = scene.CreateEntity(new Vector3(10f, 0f, 0f));
            scene.World.Set(dynamicBody, RigidBodyComponent.DynamicBox(new Vector3(0.5f), mass: 2f));
            Entity rayTarget = scene.CreateEntity(new Vector3(0f, 0f, 5f));
            scene.World.Set(rayTarget, RigidBodyComponent.StaticBox(new Vector3(0.5f)));
            scene.UpdateFixed(1f / 60f); // registers both ECS bodies with Bepu

            ProjectGameContext game = new(
                projectPath: string.Empty,
                scene,
                renderer: null,
                window: null,
                room: new RoomAsset { Dimension = RoomDimension.ThreeD },
                logger: null);
            IGameContext previousGame = PgslCommands.ActiveGameContext;
            PgslCommands.ActiveGameContext = game;
            try
            {
                PgslCommands.PhysicsApplyImpulse(dynamicBody.Id, 2, 0, 0);
                ref RigidBodyComponent registered = ref scene.World.GetRef<RigidBodyComponent>(dynamicBody);
                Vector3 velocity = scene.Physics.GetLinearVelocity(registered.RegistrationId);
                HeadlessHarness.Assert(
                    Math.Abs(velocity.X - 1f) < 0.01f,
                    $"A 2 N·s impulse on a 2 kg body should add 1 m/s, added {velocity.X:0.###}.");

                double distance = PgslCommands.PhysicsRaycast(0, 0, 0, 0, 0, 1, 20);
                HeadlessHarness.Assert(
                    distance > 4d && distance < 5d,
                    $"PhysicsRaycast should hit the box near z=5 at about 4.5 m, returned {distance:0.###}.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousGame;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.All39ObjectEvents", () =>
        {
            HeadlessHarness.Assert(
                ObjectEventCatalog.All.Count == 39,
                $"The shared object-event catalogue contains {ObjectEventCatalog.All.Count} events, expected 39.");

            Dictionary<string, string> events = new(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectEventDefinition definition in ObjectEventCatalog.All)
                events[definition.Id] = $"var hit_{definition.Id} = 1;\n";

            System.Text.StringBuilder create = new(events["Create"]);
            for (int slot = 0; slot < ObjectEventCatalog.AlarmCount; slot++)
                create.AppendLine($"SetAlarm({slot}, 1);");
            for (int slot = 0; slot < ObjectEventCatalog.UserEventCount; slot++)
                create.AppendLine($"EventUser({slot});");
            events["Create"] = create.ToString();

            EcsWorld world = new();
            Entity entity = world.CreateEntity();
            world.Set(entity, new TransformComponent
            {
                X = 100f,
                Y = 100f,
                ScaleX = 1f,
                ScaleY = 1f,
                ScaleZ = 1f,
            });
            Entity other = world.CreateEntity();

            InputState input = new();
            NullGameContext context = new()
            {
                World = world,
                Input = input,
                Room = new RoomAsset { Dimension = RoomDimension.TwoD },
            };
            ScriptHostSystem host = new();
            host.SetContext(context);

            PgslBehavior behavior;
            using (host.UseEventSources(events))
                behavior = (PgslBehavior)host.Attach(world, entity, "EveryEventObject");

            host.BeginRoom(beginGame: true);

            input.OnKeyDown(Key.A);
            input.OnMouseDown(MouseButton.Left);
            input.OnMouseDown(MouseButton.Right);
            input.OnMouseMove(100f, 100f);
            host.Update(1f / 60f);

            input.NextFrame();
            input.OnKeyUp(Key.A);
            input.OnMouseMove(500f, 500f);
            host.Update(1f / 60f);
            input.NextFrame();

            PgslRecordingDrawSurface surface = new();
            behavior.OnDrawWorldFrame(surface);
            behavior.OnDrawGuiFrame(surface);
            host.DispatchCollision(entity, other);
            host.EndRoom(endGame: true);
            host.Detach(entity);

            IReadOnlyDictionary<string, object> variables = behavior.GetVariablesSnapshot();
            string[] missing = ObjectEventCatalog.All
                .Where(definition => !variables.ContainsKey("hit_" + definition.Id))
                .Select(definition => definition.Id)
                .ToArray();
            HeadlessHarness.Assert(
                missing.Length == 0,
                "Object event handlers that never ran: " + string.Join(", ", missing));
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.ShadowingWarning", () =>
        {
            // NEXT-032: `var x = 0;` collides with the built-in instance variable and is silently
            // swallowed — the assignment looks fine and the value never changes. That cost a long
            // debugging session on a loop that ran 240 iterations with nothing moving. It is still
            // legal (so a warning, not an error), but it must no longer be invisible.
            PgslValidationReport shadowed = PgslScriptValidator.ValidateSource(
                "var x = 0;\nvar gravity = 0.5;\nvar moveSpeed = 3;\nx = x + 1;\n",
                "shadow-probe");

            HeadlessHarness.Assert(
                shadowed.Warnings.Count(w => w.Contains("shadows the built-in", StringComparison.Ordinal)) == 2,
                $"Expected 2 shadowing warnings (x, gravity), got {shadowed.Warnings.Count}: "
                + string.Join(" | ", shadowed.Warnings));
            HeadlessHarness.Assert(
                shadowed.Warnings.Any(w => w.Contains("'var x'", StringComparison.Ordinal)),
                "No warning named the shadowed variable 'x'.");
            HeadlessHarness.Assert(
                shadowed.Warnings.Any(w => w.Contains("gravityStep", StringComparison.Ordinal)),
                "The warning should suggest a concrete non-colliding name (gravityStep) so the author knows the fix.");
            HeadlessHarness.Assert(
                shadowed.Success,
                "Shadowing must stay a warning — turning it into an error would break existing scripts.");

            // Commands added since the checker was written must not be reported as unknown. The
            // validator consulted only the Engine.* pipeline, so every one of the 229 commands added
            // in this work false-warned — and a Problems pane that cries wolf is worse than none.
            PgslValidationReport commands = PgslScriptValidator.ValidateSource(
                "SetAlarm(0, 5);\nvar s = StringOf(3);\nvar l = DsListCreate();\nDrawTriangle(0,0,1,1,2,2,false);\n"
                + "var d = DistanceBetweenPoints3D(0,0,0,3,4,0);\nSpriteSet(\"Hero\");\nRoomGetWidth();\n",
                "command-probe");
            HeadlessHarness.Assert(
                !commands.Warnings.Any(w => w.Contains("Unknown command", StringComparison.Ordinal)),
                "The validator reported a real PGSL command as unknown: "
                + string.Join(" | ", commands.Warnings.Where(w => w.Contains("Unknown command", StringComparison.Ordinal))));

            // …but a genuine typo must still be caught.
            PgslValidationReport typo = PgslScriptValidator.ValidateSource("NotARealCommandAtAll(1);\n", "typo-probe");
            HeadlessHarness.Assert(
                typo.Warnings.Any(w => w.Contains("Unknown command", StringComparison.Ordinal)),
                "A misspelled command must still be reported, or the check is worthless.");

            // …and a clean script must not be nagged.
            PgslValidationReport clean = PgslScriptValidator.ValidateSource(
                "var startX = 0;\nvar gravityStep = 0.5;\nstartX = startX + 1;\n",
                "clean-probe");
            HeadlessHarness.Assert(
                !clean.Warnings.Any(w => w.Contains("shadows the built-in", StringComparison.Ordinal)),
                $"A script with no shadowing was warned anyway: {string.Join(" | ", clean.Warnings)}");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.StrictPlayValidation", () =>
        {
            PgslValidationReport unknown = PgslScriptValidator.ValidateSource(
                "NotARealCommandAtAll(1);\n", "unknown-command", strict: true);
            HeadlessHarness.Assert(
                !unknown.Success && unknown.Errors.Any(error => error.Contains("Unknown command", StringComparison.Ordinal)),
                "Strict Play validation did not reject an unknown command: " + string.Join(" | ", unknown.Errors));

            PgslValidationReport arity = PgslScriptValidator.ValidateSource(
                "SetAlarm(0);\n", "bad-arity", strict: true);
            HeadlessHarness.Assert(
                !arity.Success && arity.Errors.Any(error => error.Contains("expects 2 argument", StringComparison.Ordinal)),
                "Strict Play validation did not reject the wrong SetAlarm argument count: "
                + string.Join(" | ", arity.Errors));

            PgslValidationReport implemented = PgslScriptValidator.ValidateSource(
                "DrawSphere3D(0, 0, 0, 1);\n", "implemented-command", strict: true);
            HeadlessHarness.Assert(
                implemented.Success,
                "A now-implemented 3D command is still blocked by strict Play validation: "
                + string.Join(" | ", implemented.Errors));

            PgslValidationReport shadow = PgslScriptValidator.ValidateSource(
                "var x = 0;\n", "strict-shadow", strict: true);
            HeadlessHarness.Assert(
                !shadow.Success && shadow.Errors.Any(error => error.Contains("shadows the built-in", StringComparison.Ordinal)),
                "Strict Play validation allowed a local declaration the VM silently discards.");

            PgslValidationReport undeclared = PgslScriptValidator.ValidateSource(
                "var total = missingValue + 1;\n", "undeclared-read", strict: true);
            HeadlessHarness.Assert(
                !undeclared.Success && undeclared.Errors.Any(error => error.Contains("Undeclared variable", StringComparison.Ordinal)),
                "Strict Play validation allowed an undeclared read that resolves to zero.");

            PgslValidationReport userFunction = PgslScriptValidator.ValidateSource(
                "function Add(a, b) { return a + b; }\nvar total = Add(1, 2);\n",
                "user-function", strict: true);
            HeadlessHarness.Assert(
                userFunction.Success,
                "A valid user function was rejected by strict validation: " + string.Join(" | ", userFunction.Errors));

            PgslValidationReport scriptAsset = PgslScriptValidator.ValidateSource(
                "Helper(1, 2, 3);\n", "external-script", strict: true,
                externalFunctions: new[] { "Helper" });
            HeadlessHarness.Assert(
                scriptAsset.Success,
                "A known project script asset was rejected as an unknown command: "
                + string.Join(" | ", scriptAsset.Errors));

            string project = Path.Combine(workspace, "PgslStrictValidation");
            string objectRoot = Path.Combine(project, "Assets", "Objects");
            string objectEvents = Path.Combine(objectRoot, "Probe");
            string scripts = Path.Combine(project, "Assets", "Scripts");
            Directory.CreateDirectory(objectEvents);
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(objectRoot, "Probe.object.json"), "{}");
            File.WriteAllText(Path.Combine(objectEvents, "Step.pgsl"), "NotARealCommandAtAll(1);\n");
            File.WriteAllText(Path.Combine(scripts, "Helper.pgsl"), "var result = 1;\n");

            PgslValidationReport projectReport = PgslScriptValidator.ValidateProject(project, strict: true);
            HeadlessHarness.Assert(
                projectReport.ScriptCount == 2,
                $"Project validation scanned {projectReport.ScriptCount} files; expected Assets/Scripts and object events.");
            HeadlessHarness.Assert(
                !projectReport.Success
                && projectReport.Errors.Any(error => error.Contains("Assets", StringComparison.Ordinal)
                    && error.Contains("Step.pgsl", StringComparison.Ordinal)),
                "Project validation did not identify the bad object-event source path: "
                + string.Join(" | ", projectReport.Errors));

            ScriptCompileResult compile = CSharpScriptCompiler.CompileProjectScripts(project, "PgslNegativeGate");
            HeadlessHarness.Assert(
                !compile.Success && compile.Errors.Any(error => error.Contains("Unknown command", StringComparison.Ordinal)),
                "The F5 compiler did not stop before launch on strict PGSL semantic errors.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.RuntimeDiagnostics", () =>
        {
            string project = Path.Combine(workspace, "PgslRuntimeDiagnostics");
            Directory.CreateDirectory(project);

            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            var scriptHost = new ScriptHostSystem();
            var reported = new List<ScriptDiagnostic>();
            using var logger = new ProjectLogger(project);
            scriptHost.DiagnosticReported += diagnostic => reported.Add(diagnostic);
            scriptHost.DiagnosticReported += logger.WriteScriptDiagnostic;

            EcsWorld world = new();
            var entity = world.CreateEntity();
            var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Step"] = "NotARealCommandAtAll(1);\n",
            };
            using (scriptHost.UseEventSources(events))
                scriptHost.Attach(world, entity, "DiagnosticProbe");

            scriptHost.Update(1f / 60f);
            HeadlessHarness.Assert(scriptHost.LastDiagnostic != null, "A runtime PGSL exception produced no structured diagnostic.");
            ScriptDiagnostic diagnostic = scriptHost.LastDiagnostic
                ?? throw new InvalidOperationException("Missing runtime script diagnostic.");
            HeadlessHarness.Assert(
                diagnostic.ObjectName == "DiagnosticProbe"
                && diagnostic.EventName == "Step"
                && diagnostic.EntityId == entity.Id
                && diagnostic.Line == 1,
                "Runtime diagnostic lost object/event/entity/line context: "
                + diagnostic.ToLogLine());
            HeadlessHarness.Assert(
                !string.IsNullOrWhiteSpace(diagnostic.StackTrace),
                "Runtime diagnostic did not retain a stack trace.");

            scriptHost.Update(1f / 60f);
            HeadlessHarness.Assert(
                scriptHost.RecentDiagnostics.Count == 1 && diagnostic.RepeatCount == 2,
                "Identical per-frame failures were not deduplicated with a repetition count.");
            HeadlessHarness.Assert(
                reported.Count == 2,
                $"The diagnostic throttle should publish first occurrence and repeat 2, published {reported.Count}.");

            string log = File.ReadAllText(logger.LogPath);
            HeadlessHarness.Assert(
                log.Contains("[SCRIPT ERROR]", StringComparison.Ordinal)
                && log.Contains("object=\"DiagnosticProbe\"", StringComparison.Ordinal)
                && log.Contains("event=\"Step\"", StringComparison.Ordinal)
                && log.Contains("line=1", StringComparison.Ordinal)
                && log.Contains("[SCRIPT STACK]", StringComparison.Ordinal),
                "project_player.log did not persist the full structured script diagnostic.");

            var overlay = new DebugOverlay();
            overlay.BindScriptHost(scriptHost);
            HeadlessHarness.Assert(
                overlay.HasScriptErrors && overlay.LatestScriptError == diagnostic,
                "The debug overlay did not import a failure raised before it was constructed.");

            var hud = new DiagnosticHudCanvas();
            overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
            HeadlessHarness.Assert(
                hud.Texts.Any(text => text.Contains("SCRIPT ERROR", StringComparison.Ordinal))
                && hud.Texts.Any(text => text.Contains("project_player.log", StringComparison.Ordinal)),
                "Normal F5 rendering did not show the always-on runtime error banner.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.EventProfiler", () =>
        {
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            PgslProfiler.Reset();
            PgslProfiler.Enabled = true;
            try
            {
                var scriptHost = new ScriptHostSystem();
                EcsWorld world = new();
                var entity = world.CreateEntity();
                var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Create"] = "var profile_value = 1;\n",
                    ["Step"] = "profile_value = profile_value + 1;\n",
                };
                using (scriptHost.UseEventSources(events))
                    scriptHost.Attach(world, entity, "ProfileProbe");

                scriptHost.Update(1f / 60f);
                scriptHost.Update(1f / 60f);
                scriptHost.Update(1f / 60f);

                IReadOnlyList<PgslEventProfile> snapshot = PgslProfiler.Snapshot();
                PgslEventProfile create = snapshot.Single(profile => profile.EventName == "Create");
                PgslEventProfile step = snapshot.Single(profile => profile.EventName == "Step");
                HeadlessHarness.Assert(
                    create.ObjectName == "ProfileProbe" && create.CallCount == 1 && create.LastEntityId == entity.Id,
                    "The PGSL profiler lost Create object/entity/call context.");
                HeadlessHarness.Assert(
                    step.ObjectName == "ProfileProbe" && step.CallCount == 3 && step.TotalMicroseconds > 0,
                    "The PGSL profiler did not measure each authored Step event.");

                var overlay = new DebugOverlay();
                overlay.BindScriptHost(scriptHost);
                overlay.ShowExpandedPanels = true;
                var input = new InputState();
                input.OnKeyDown(Key.F6);
                overlay.HandleInput(input, debugMode: true);
                var hud = new DiagnosticHudCanvas();
                overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
                HeadlessHarness.Assert(
                    hud.Texts.Any(text => text.Contains("PGSL EVENTS", StringComparison.Ordinal))
                    && hud.Texts.Any(text => text.Contains("ProfileProbe.Step", StringComparison.Ordinal)),
                    "The Debug overlay did not show authored PGSL event timings.");
            }
            finally
            {
                PgslProfiler.Enabled = false;
                PgslProfiler.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Debug.F6LiveRenderHud", () =>
        {
            // R7.1: Engine/Textures tabs must show live adapter + caps, never the old Iris Xe /
            // "Player, Coin, Tiles" placeholders. Drive a real DX11 frame so AdapterName and
            // GetStats come from GpuRenderController, then assert the overlay text.
            using RuntimeViewportHarness harness = new(640, 360);
            _ = harness.Capture3D(Path.Combine(captures, "62-f6-live-hud-scene.png"));
            IRenderController renderer = harness.Renderer;
            RenderStats stats = harness.LastStats;

            HeadlessHarness.Assert(
                !string.IsNullOrWhiteSpace(renderer.AdapterName),
                "GpuRenderController did not publish AdapterName for F6.");
            HeadlessHarness.Assert(
                stats.SpriteInstanceCap == 32768 && stats.MeshInstanceCap == 32768
                && stats.LightsCap == RenderCapacityDefaults.DefaultSceneLocalLightCap,
                $"Instance/light caps are wrong (sprites={stats.SpriteInstanceCap}, "
                + $"meshes={stats.MeshInstanceCap}, lights={stats.LightsCap}; "
                + $"expected lights={RenderCapacityDefaults.DefaultSceneLocalLightCap} after R7.5).");

            var overlay = new DebugOverlay();
            overlay.ShowExpandedPanels = true;
            var input = new InputState();
            input.OnKeyDown(Key.F6);
            overlay.HandleInput(input, debugMode: true);
            HeadlessHarness.Assert(overlay.IsVisible, "F6 did not show the debug overlay.");

            var hud = new DiagnosticHudCanvas();
            overlay.Draw(hud, renderer, hud.Width, hud.Height);

            string joined = string.Join('\n', hud.Texts);
            HeadlessHarness.Assert(
                hud.Texts.Any(text => text.Contains(renderer.AdapterName, StringComparison.Ordinal)),
                $"F6 Engine tab did not show live adapter '{renderer.AdapterName}'.");
            HeadlessHarness.Assert(
                hud.Texts.Any(text => text.Contains("Draw 2D / 3D", StringComparison.Ordinal))
                && hud.Texts.Any(text => text.Contains("WorldMeshes", StringComparison.Ordinal))
                && hud.Texts.Any(text => text.Contains("Lights", StringComparison.Ordinal)
                    && text.Contains($"/ {RenderCapacityDefaults.DefaultSceneLocalLightCap}", StringComparison.Ordinal))
                && hud.Texts.Any(text => text.Contains("Sprites", StringComparison.Ordinal)
                    && text.Contains("/ 32768", StringComparison.Ordinal))
                && hud.Texts.Any(text => text.Contains("Meshes", StringComparison.Ordinal)
                    && text.Contains("/ 32768", StringComparison.Ordinal)),
                "F6 Engine tab is missing live draw/WorldMeshes/lights/instance-cap lines.\n" + joined);
            HeadlessHarness.Assert(
                !joined.Contains("Intel Iris Xe", StringComparison.Ordinal)
                && !joined.Contains("Player, Coin, Tiles", StringComparison.Ordinal)
                && !joined.Contains("GPU: 11.8%", StringComparison.Ordinal),
                "F6 still contains hardcoded placeholder telemetry.");

            // Textures tab: click the tab button (panel at right; Textures is the middle tab).
            float panelX = hud.Width - 230f - 8f;
            float texturesBtnX = panelX + 6f + 72f + 37f;
            float texturesBtnY = 40f + 6f + 10f;
            var textureInput = new InputState();
            textureInput.OnMouseMove(texturesBtnX, texturesBtnY);
            textureInput.OnMouseDown(MouseButton.Left);
            overlay.HandleInput(textureInput, debugMode: true);
            hud.Texts.Clear();
            overlay.Draw(hud, renderer, hud.Width, hud.Height);
            string textureJoined = string.Join('\n', hud.Texts);
            HeadlessHarness.Assert(
                textureJoined.Contains("Runtime atlas stitch", StringComparison.Ordinal)
                && textureJoined.Contains("Texture switches", StringComparison.Ordinal),
                "F6 Textures tab did not show live atlas stitch / switch stats.\n" + textureJoined);
            HeadlessHarness.Assert(
                !textureJoined.Contains("Player, Coin, Tiles", StringComparison.Ordinal)
                && !textureJoined.Contains("Occupancy: 4.8%", StringComparison.Ordinal),
                "F6 Textures tab still shows fake atlas occupancy.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Debug.F6SceneGizmos", () =>
        {
            // Phase 6 polish: F6 draws light/audio/camera markers and wireframe outlines
            // (DebugRuntimeF6.png). Wireframe also drives Mesh3DState via GenesisRuntimeHost.
            using RuntimeScene scene = new("F6 Gizmo Probe");
            scene.Camera3D.AspectRatio = 1280f / 720f;
            var lightEntity = scene.World.CreateEntity();
            scene.World.Set(lightEntity, new TransformComponent { X = 0f, Y = 2f, Z = 0f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            scene.World.Set(lightEntity, new PointLightComponent
            {
                Enabled = true,
                Color = new Vector3(1f, 1f, 1f),
                Intensity = 1f,
                Radius = 5f,
                Falloff = 2f,
            });
            var audioEntity = scene.World.CreateEntity();
            scene.World.Set(audioEntity, new TransformComponent { X = 1f, Y = 0.5f, Z = 1f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            scene.World.Set(audioEntity, new AudioComponent { Asset = "probe.audio.json", Volume = 1f });

            var overlay = new DebugOverlay();
            overlay.BindScene(scene, "Probe");
            overlay.ShowWireframe = true;
            overlay.ShowCameras = true;
            var input = new InputState();
            input.OnKeyDown(Key.F6);
            overlay.HandleInput(input, debugMode: true);
            HeadlessHarness.Assert(overlay.IsVisible && overlay.ShowWireframe,
                "F6 wireframe/gizmo probe did not open with wireframe enabled.");

            var hud = new DiagnosticHudCanvas();
            overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
            HeadlessHarness.Assert(
                hud.LineCount >= 4 && hud.RectCount >= 2,
                $"F6 scene gizmos did not draw light/audio markers (lines={hud.LineCount}, rects={hud.RectCount}).");
            HeadlessHarness.Assert(
                hud.Texts.Any(text => text.Contains("Genesis Engine Runtime Debug (F6)", StringComparison.Ordinal))
                && hud.Texts.Any(text => text.Contains("F6: Close Debug HUD", StringComparison.Ordinal)),
                "F6 compact card / footer chrome missing from gizmo draw.");
            HeadlessHarness.Assert(
                !overlay.ShowExpandedPanels,
                "F6SceneGizmos should exercise the compact mock layout by default.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.CommandAutoTest", () =>
        {
            // Sweeps EVERY declared command with dummy data. This is not about right answers — the
            // per-family cases cover those — it is about every command being callable with no game
            // running. One unguarded null context here is a command that would crash a real game the
            // first time a designer called it early. Same engine the Help dialog runs, so the two
            // can never disagree.
            PgslCommandTestReport report = PgslCommandAutoTester.Run(repeats: 3);

            HeadlessHarness.Assert(report.Total > 250, $"Command sweep found only {report.Total} commands.");
            HeadlessHarness.Assert(
                report.Failed == 0,
                $"{report.Failed} PGSL command(s) threw on dummy data: "
                + string.Join("; ", report.Results
                    .Where(r => r.Outcome == PgslCommandOutcome.Threw)
                    .Take(6)
                    .Select(r => $"{r.Name} ({r.Detail})")));
            HeadlessHarness.Assert(
                report.Unsupported == 0,
                $"{report.Unsupported} command(s) could not be invoked at all: "
                + string.Join("; ", report.Results
                    .Where(r => r.Outcome == PgslCommandOutcome.Unsupported)
                    .Take(6)
                    .Select(r => r.Name)));
            HeadlessHarness.Assert(
                report.NotImplemented == 0,
                "The callable PGSL catalogue still contains unfinished commands: "
                + string.Join(", ", report.Results
                    .Where(result => result.Outcome == PgslCommandOutcome.NotImplemented)
                    .Select(result => result.Name)));

            HeadlessHarness.Assert(
                PgslCommandAutoTester.Catalogue().Count == report.Total,
                "The catalogue and the auto-test disagree on how many commands exist.");
            HeadlessHarness.Assert(
                PgslCommandRegistry.GetFullCatalog().Count == report.Total
                && PgslCommandRegistry.GetFullCatalog().All(command => command.IsImplemented),
                "The editor insertion catalogue is not the same all-live command contract as the VM sweep.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.AudioCommands", () =>
        {
            // Track M: IGameContext.Audio was fully implemented but had no PGSL surface, so a
            // scripted game literally could not make a sound. These run through the VM, not by
            // calling the C# methods directly, so they also prove the commands are reachable
            // from script source.
            PgslAudioResult audio = PgslGameplaySuite.RunAudio();

            HeadlessHarness.Assert(audio.PlayCalls == 1, $"PGSL PlaySound reached the mixer {audio.PlayCalls} times, expected 1.");
            HeadlessHarness.Assert(
                audio.FirstPath == "Assets/Audio/Jump.wav",
                $"PlaySound loaded '{audio.FirstPath}' instead of the requested clip.");
            HeadlessHarness.Assert(
                Math.Abs(audio.FirstVolume - 0.75f) < 0.001f,
                $"PlaySound passed volume {audio.FirstVolume:0.###} instead of 0.75 — the argument is being dropped.");
            HeadlessHarness.Assert(
                Math.Abs(audio.FirstPitch - 1.25f) < 0.001f,
                $"PlaySound passed pitch {audio.FirstPitch:0.###} instead of 1.25.");
            HeadlessHarness.Assert(!audio.FirstLoop, "PlaySound looped a one-shot.");
            HeadlessHarness.Assert(audio.ChannelHandle > 0, "PlaySound returned no channel handle, so scripts cannot stop a sound.");
            HeadlessHarness.Assert(audio.ReportedPlaying, "IsSoundPlaying said a just-started sound was silent.");
            HeadlessHarness.Assert(audio.StopCalls == 1, $"StopSound reached the mixer {audio.StopCalls} times, expected 1.");
            HeadlessHarness.Assert(
                Math.Abs(audio.MasterVolume - 0.6f) < 0.001f,
                $"SetMasterVolume/GetMasterVolume round-tripped {audio.MasterVolume:0.###} instead of 0.6.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.2D.ViewportCapture", () =>
        {
            RuntimeImageMetrics metrics = RuntimeHeadlessSuite.Capture2D(
                Path.Combine(captures, "06-runtime-2d.png"));
            ctx.Report.Images.Add(new ImageResult(
                "Runtime 2D",
                "06-runtime-2d.png",
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance));
            HeadlessHarness.Assert(metrics.Width >= 320, "2D capture width was too small.");
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 4, "2D capture looks blank.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.3D.ViewportCapture", () =>
        {
            RuntimeImageMetrics metrics = RuntimeHeadlessSuite.Capture3D(
                Path.Combine(captures, "07-runtime-3d.png"));
            ctx.Report.Images.Add(new ImageResult(
                "Runtime 3D",
                "07-runtime-3d.png",
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance));
            HeadlessHarness.Assert(metrics.Width >= 320, "3D capture width was too small.");
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 4, "3D capture looks blank.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.ThreeDViewportCapture", () =>
        {
            RuntimeImageMetrics metrics = RuntimeHeadlessSuite.CapturePgsl3D(
                Path.Combine(captures, "07b-runtime-pgsl-3d.png"));
            ctx.Report.Images.Add(new ImageResult(
                "Runtime PGSL 3D",
                "07b-runtime-pgsl-3d.png",
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance));
            HeadlessHarness.Assert(metrics.Width >= 320, "PGSL 3D capture width was too small.");
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 4, "PGSL 3D capture looks blank.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Audio.WavAndPlayback", () =>
        {
            string audioWorkspace = Path.Combine(workspace, "RuntimeAudio");
            RuntimeAudioResult result = RuntimeHeadlessSuite.RunAudio(
                audioWorkspace,
                Path.Combine(logs, "runtime-audio.txt"));
            HeadlessHarness.Assert(result.WavBytes > 64, "Tone WAV was not written.");
            HeadlessHarness.Assert(
                result.Mode is "xaudio2" or "null-fallback",
                $"Unexpected audio mode '{result.Mode}'.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Loopback", () =>
        {
            RuntimeNetworkResult result = RuntimeHeadlessSuite.RunNetworkLoopback();
            HeadlessHarness.Assert(result.Port > 0, "Loopback host did not bind a port.");
            HeadlessHarness.Assert(result.PayloadBytes > 0, "Loopback payload was empty.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.CapacitySpine.PackAndAoi", () =>
        {
            RuntimeCapacitySpineResult result = RuntimeHeadlessSuite.RunCapacitySpineChecks();
            HeadlessHarness.Assert(result.PackBytes > 0, "Pack sample was empty.");
            HeadlessHarness.Assert(
                result.InterestCount == 1,
                $"Expected 1 interest entity, got {result.InterestCount}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.SpriteAssetLoader", () =>
        {
            RuntimeSpriteAssetResult result = RuntimeHeadlessSuite.RunSpriteAssetLoaderChecks(outputRoot);
            HeadlessHarness.Assert(result.FrameCount == 2, "Sprite loader should expose two authored frames.");
            HeadlessHarness.Assert(File.Exists(result.FirstFramePath), "First sprite frame path is missing.");
            HeadlessHarness.Assert(File.Exists(result.SecondFramePath), "Second sprite frame path is missing.");
        });
    }

    private sealed class DiagnosticHudCanvas : IHudCanvas
    {
        public List<string> Texts { get; } = new();
        public int RectCount { get; private set; }
        public int LineCount { get; private set; }
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Texts.Add(text);
        public void TextCentered(string text, float cx, float y, float w, float size, Vector4 color) => Texts.Add(text);
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => RectCount++;
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) => LineCount++;
    }
}

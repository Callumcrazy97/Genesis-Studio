using System.Drawing;
using System.Numerics;
using System.Reflection;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Images;
using Genesis.Application.Runtime;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Diagnostics;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.Project;
using Genesis.Runtime;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Climate;
using Genesis.Runtime.AI;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Input;
using Genesis.Shared.Audio;
using Genesis.Shared.Assets;
using Genesis.Shared.Commands;
using Genesis.World.Foliage;
using Genesis.World.Navigation;
using Genesis.World.Terrain;
using Genesis.World.Water;
using Genesis.Physics;
using Newtonsoft.Json.Linq;
using SharedCharacterMotor = Genesis.Shared.ECS.Components.CharacterMotorComponent;
using SharedCharacterState = Genesis.Shared.ECS.Components.CharacterMotorState;
using SharedRigidBody = Genesis.Shared.ECS.Components.RigidBodyComponent;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The runtime half of the build gate: the language the game is written in, the contracts the
/// renderer must honour, and the two templates actually running.
/// </summary>
/// <remarks>
/// Designer gameplay is PGSL and nothing else — every case here drives the same VM the shipped
/// player runs, and <see cref="GateSuite.AssertGameplayIsPgslOnly"/> refuses a project that smuggled
/// C# in. Shaders are the one exception, because HLSL is what a GPU accepts; they are covered by the
/// rendering contracts rather than exempted from scrutiny.
/// </remarks>
internal static class RuntimeGateChecks
{
    public static void TerrainWaterCharacter(HeadlessContext ctx)
    {
        HeadlessHarness.Step("one authored room supplies terrain collision, water physics, swimming, scripting and preset flow", () =>
        {
            string root = Path.Combine(ctx.Workspace, "TerrainWaterCharacterGate");
            string terrainFolder = Path.Combine(root, "Assets", "Terrain");
            string objectFolder = Path.Combine(root, "Assets", "Objects");
            Directory.CreateDirectory(terrainFolder);
            Directory.CreateDirectory(objectFolder);

            string terrainResource = Path.Combine(terrainFolder, "PhysicsCourse.terrain.json");
            File.WriteAllText(terrainResource, "{}");
            TerrainAsset terrain = new(17, 17, 1f, 0f, 0f, 0f, 8f);
            for (int z = 0; z < terrain.ResolutionZ; z++)
            for (int x = 0; x < terrain.ResolutionX; x++)
                terrain.SetHeight(x, z, 4f + x * 0.04f);
            terrain.Save(terrainResource + ".gterrain");
            TerrainNatureSerializer.Save(terrainResource, new TerrainNatureDocument
            {
                WaterBodies =
                [
                    new TerrainWaterDefinition
                    {
                        Name = "Swim Test", Center = new Vector3(11.25f, 6.5f, 11.25f),
                        SurfaceHeight = 6.5f, SizeX = 6f, SizeZ = 6f,
                        PhysicsDepth = 3f, FluidDensity = 1000f, BuoyancyStrength = 1.1f,
                        LinearDrag = 2.8f, AngularDrag = 1.2f, FlowSpeed = 0.6f, Swimmable = true,
                    },
                ],
            });

            string prefabPath = Path.Combine(objectFolder, "Player.object.json");
            File.WriteAllText(prefabPath, new JObject
            {
                ["name"] = "Player",
                ["dimension"] = "ThreeD",
                ["physics"] = "PlatformerCharacter",
                ["characterMotor"] = new JObject
                {
                    ["walkSpeed"] = 11.5f, ["sprintMultiplier"] = 1.8f,
                    ["groundAcceleration"] = 52f, ["airAcceleration"] = 17f,
                    ["jumpSpeed"] = 7.2f, ["stepHeight"] = 0.45f,
                    ["maximumSlopeDegrees"] = 51f, ["swimSpeed"] = 6.4f,
                    ["swimVerticalSpeed"] = 4.8f, ["swimAcceleration"] = 21f,
                    ["swimDrag"] = 3.1f,
                },
                ["components"] = new JArray(),
            }.ToString());

            RoomAsset room = RoomAsset.Create("Terrain Physics", RoomDimension.ThreeD);
            string layer = room.Layers[0].Id;
            room.Nodes.Add(new RoomNode
            {
                Name = "Terrain", Kind = RoomNodeKind.Terrain, LayerId = layer,
                Terrain = new RoomTerrainData { Asset = "Assets/Terrain/PhysicsCourse.terrain.json" },
            });
            RoomNode playerNode = new()
            {
                Name = "Player", Kind = RoomNodeKind.GameObject, LayerId = layer,
                Transform = new RoomTransform { Position = [11.25f, 5.6f, 11.25f], Scale = [1f, 1.2f, 1f] },
                GameObject = new RoomGameObjectData { Prefab = "Assets/Objects/Player.object.json" },
            };
            room.Nodes.Add(playerNode);
            room.Normalize();

            Genesis.Runtime.Input.InputState input = new();
            using RuntimeScene scene = new("Terrain water character gate") { Input = input };
            RoomBuildResult build = new RoomSceneBuilder(root).Build(scene, room);
            ProjectGameContext game = new(root, scene, null, null, room, null);
            scene.AddSubsystem(new RoomTerrainSubsystem(root, room, game));
            scene.UpdateFixed(1f / 60f);

            PhysicsWorld physics = scene.Physics ?? throw new InvalidOperationException("The 3D room created no physics world.");
            HeadlessHarness.Assert(physics.ExternalStaticCount == 1,
                "The authored terrain did not register one runtime collision mesh.");
            HeadlessHarness.Assert(scene.WaterVolumes.Count == 1 && scene.WaterVolumes[0].Swimmable,
                "The authored water did not register a swimmable physics volume.");
            bool terrainRay = physics.RaycastDown(scene.World, new Vector3(2.35f, 12f, 2.65f), 20f, out PhysicsRaycastHit terrainHit);
            HeadlessHarness.Assert(terrainRay && terrainHit.Normal.Y > 0.8f,
                $"A downward physics query did not resolve the authored slope (hit={terrainRay}, normal={terrainHit.Normal}, distance={terrainHit.Distance:0.###}).");

            Entity player = build.EntitiesByNodeId[playerNode.Id];
            HeadlessHarness.Assert(scene.World.Has<SharedRigidBody>(player)
                && scene.World.Has<SharedCharacterMotor>(player)
                && scene.World.GetRef<SharedRigidBody>(player).RegistrationId != 0,
                "The Object preset did not become a registered runtime character body.");
            SharedCharacterMotor motor = scene.World.GetRef<SharedCharacterMotor>(player);
            HeadlessHarness.Assert(Math.Abs(motor.WalkSpeed - 11.5f) < 0.001f
                && Math.Abs(motor.SwimAcceleration - 21f) < 0.001f
                && motor.State == SharedCharacterState.Swimming && motor.EnteredWater,
                "Authored character settings or automatic swimming state did not reach gameplay.");

            SharedRigidBody playerBody = scene.World.GetRef<SharedRigidBody>(player);
            physics.SetBodyPose(scene.World, playerBody.RegistrationId,
                new Vector3(11.25f, 6.4f, 11.25f), Quaternion.Identity);
            input.OnKeyDown(Genesis.Runtime.Input.Key.Space);
            scene.UpdateFixed(1f / 60f);
            motor = scene.World.GetRef<SharedCharacterMotor>(player);
            float jumpVelocity = physics.GetLinearVelocity(playerBody.RegistrationId).Y;
            HeadlessHarness.Assert(motor.ExitedWater && motor.State == SharedCharacterState.Falling
                && jumpVelocity > 1f,
                $"The character could not jump out of water or expose the exit transition hook (exit={motor.ExitedWater}, state={motor.State}, vy={jumpVelocity:0.###}, submerged={motor.SubmergedFraction:0.###}).");
            // Continue the acceptance course on dry terrain after proving the water-edge jump.
            physics.SetBodyPose(scene.World, playerBody.RegistrationId,
                new Vector3(6.25f, 6.4f, 6.25f), Quaternion.Identity);
            physics.SetLinearVelocity(scene.World, playerBody.RegistrationId, new Vector3(0f, jumpVelocity, 0f));
            input.NextFrame();
            input.OnKeyUp(Genesis.Runtime.Input.Key.Space);
            input.NextFrame();

            Entity dryBody = scene.CreateEntity(new Vector3(2.35f, 9f, 2.65f));
            scene.World.Set(dryBody, SharedRigidBody.DynamicBox(new Vector3(0.45f), 1f));
            for (int i = 0; i < 240; i++) scene.UpdateFixed(1f / 60f);
            float restingY = scene.World.GetRef<Genesis.Shared.ECS.Components.Transform3DComponent>(dryBody).Position.Y;
            HeadlessHarness.Assert(restingY > terrain.SampleHeight(2.35f, 2.65f) + 0.3f && restingY < 6f,
                $"A dynamic object did not settle on authored terrain (Y={restingY:0.###}).");
            HeadlessHarness.Assert(scene.World.GetRef<SharedCharacterMotor>(player).State == SharedCharacterState.Grounded,
                "The swimming character did not return to grounded slope motion after leaving water.");

            PgslCommands.ActiveGameContext = game;
            try
            {
                HeadlessHarness.Assert(PgslCommands.WaterIsSwimmable(11.25, 5.6, 11.25)
                    && PgslCommands.WaterDepthAt(11.25, 5.6, 11.25) > 0.8
                    && Math.Abs(PgslCommands.WaterSurfaceAt(11.25, 11.25) - 6.5) < 0.01,
                    "PGSL could not inspect the authored water volume.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = null;
            }
        });
    }

    /// <summary>Everything a designer's script can do, on the VM, with no window.</summary>
    public static void PgslLanguage(HeadlessContext ctx)
    {
        HeadlessHarness.Step("1D — variables, maths, arrays, lists, grids, alarms", () =>
        {
            PgslOneDResult result = PgslDimensionSuite.RunOneD();
            HeadlessHarness.Assert(
                result.ChecksPassed == result.ChecksAttempted,
                $"1D PGSL checks: {result.ChecksPassed}/{result.ChecksAttempted} passed.");
            HeadlessHarness.Assert(result.Sum == 55, $"for-loop sum expected 55, got {result.Sum}.");
            HeadlessHarness.Assert(result.ArraySum == 42, $"Array sum expected 42, got {result.ArraySum}.");
            HeadlessHarness.Assert(result.ListSum == 21, $"ds_list sum expected 21, got {result.ListSum}.");
            HeadlessHarness.Assert(result.GridSum == 39, $"ds_grid sum expected 39, got {result.GridSum}.");
            HeadlessHarness.Assert(
                result.Label == "SCORE 42",
                $"Dynamic string building produced '{result.Label}' — HUD text would be wrong.");
            HeadlessHarness.Assert(
                result.AlarmRemaining == 1 && result.AlarmFiredOnTime,
                $"Alarms drifted: remaining={result.AlarmRemaining}, firedOnTime={result.AlarmFiredOnTime}.");
        });

        HeadlessHarness.Step("2D — shapes that visibly move, and a HUD that reaches a surface", () =>
        {
            PgslTwoDResult result = PgslDimensionSuite.RunTwoD(frames: 120);
            HeadlessHarness.Assert(result.ShapeCalls > 0, "PGSL 2D drawing produced no shapes at all.");
            HeadlessHarness.Assert(
                result.CircleCalls >= result.Frames * 4,
                $"Expected ≥4 circles per frame ({result.Frames * 4}), got {result.CircleCalls}.");

            // A script that runs and paints a static picture is the failure mode NEXT-036 taught us
            // to assert numerically rather than eyeball.
            HeadlessHarness.Assert(
                result.OrbitSpread > 100,
                $"The orbiting shape moved only {result.OrbitSpread:0.#}px — it is effectively static.");
            HeadlessHarness.Assert(
                Math.Abs(result.FinalPlayerX - 80) > 50,
                $"The player never moved from its spawn x, ending at {result.FinalPlayerX:0.#}.");
            HeadlessHarness.Assert(
                result.HudText.StartsWith("SCORE ", StringComparison.Ordinal) && result.HudText != "SCORE 0",
                $"HUD drew '{result.HudText}' instead of the score the script accumulated.");
        });

        HeadlessHarness.Step("3D — geometry, conventions, and no leaking into the 2D pass", () =>
        {
            PgslThreeDResult result = PgslDimensionSuite.RunThreeD();
            HeadlessHarness.Assert(result.CubeCalls > 10, $"PGSL 3D queued only {result.CubeCalls} primitives.");
            HeadlessHarness.Assert(
                Math.Abs(result.DistanceCheck - 5) < 0.001,
                $"DistanceBetweenPoints3D(0,0,0→3,4,0) gave {result.DistanceCheck}, expected 5.");
            HeadlessHarness.Assert(
                Math.Abs(result.ForwardZCheck - 1) < 0.001,
                $"ForwardZ(0,0) should be 1 — the yaw convention has drifted ({result.ForwardZCheck}).");
            HeadlessHarness.Assert(
                result.RespectedInactivePass,
                "3D commands queued geometry with no active 3D pass; they would corrupt the 2D frame.");
        });

        HeadlessHarness.Step("gameplay — physics, collection, score, HUD", () =>
        {
            PgslGameplayResult game = PgslGameplaySuite.Run();
            HeadlessHarness.Assert(
                game.MathChecksPassed == PgslGameplaySuite.MathCheckCount,
                $"PGSL Math: {game.MathChecksPassed}/{PgslGameplaySuite.MathCheckCount} correct.");
            HeadlessHarness.Assert(
                Math.Abs(game.PlayerY - (300 - 18)) < 0.51,
                $"The player should come to rest on the platform (y≈282), got {game.PlayerY:0.##}.");
            HeadlessHarness.Assert(game.PickupsRemaining == 0, $"{game.PickupsRemaining} pickups were missed.");
            HeadlessHarness.Assert(game.Score == 30, $"Expected score 30, got {game.Score}.");

            PgslHudDrawResult hud = PgslGameplaySuite.RunHudDraw();
            HeadlessHarness.Assert(
                hud.TextCalls == 1 && hud.FirstText == "SCORE 30",
                $"PGSL drawing did not reach a surface (calls={hud.TextCalls}, text='{hud.FirstText}').");
        });

        HeadlessHarness.Step("every declared command is callable with no game running", () =>
        {
            // Not about right answers — the family steps above cover those. One unguarded null
            // context here is a command that crashes a real game the first time it is called early.
            PgslCommandTestReport report = PgslCommandAutoTester.Run(repeats: 2);
            HeadlessHarness.Assert(report.Total > 250, $"The command sweep found only {report.Total} commands.");
            HeadlessHarness.Assert(
                report.Failed == 0,
                $"{report.Failed} command(s) threw with no game attached: "
                + string.Join("; ", report.Results
                    .Where(result => result.Outcome == PgslCommandOutcome.Threw)
                    .Take(5)
                    .Select(result => $"{result.Name} ({result.Detail})")));
            HeadlessHarness.Assert(
                report.Unsupported == 0 && report.NotImplemented == 0,
                $"{report.Unsupported} command(s) are uninvokable and {report.NotImplemented} unfinished: "
                + string.Join("; ", report.Results
                    .Where(result => result.Outcome is PgslCommandOutcome.Unsupported
                        or PgslCommandOutcome.NotImplemented)
                    .Take(6)
                    .Select(result => result.Name)));
        });

        HeadlessHarness.Step("all 39 object events exist and fire", () =>
        {
            HeadlessHarness.Assert(
                ObjectEventCatalog.All.Count == 39,
                $"The object-event catalogue has {ObjectEventCatalog.All.Count} entries, expected 39.");

            Dictionary<string, string> events = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Create"] = "created = 1;\nSetAlarm(0, 1);\n",
                ["Step"] = "steps = steps + 1;\n",
                ["Alarm0"] = "alarmed = 1;\n",
                ["Draw"] = "DrawCircle(x, y, 4, true);\n",
            };

            ObjectSandboxResult result = ObjectSandbox.Run(events, frames: 4);
            HeadlessHarness.Assert(result.Ok, "Running the core event set failed: " + FirstError(result));
            HeadlessHarness.Assert(
                result.Numbers.GetValueOrDefault("created") == 1
                && result.Numbers.GetValueOrDefault("alarmed") == 1
                && result.Numbers.GetValueOrDefault("steps") == 4,
                "Create/Step/Alarm did not all fire on schedule: "
                + string.Join(", ", result.Numbers.Select(pair => $"{pair.Key}={pair.Value}")));
        });

        HeadlessHarness.Step("strict Play validation rejects what would fail at runtime", () =>
        {
            PgslValidationReport unknown = PgslScriptValidator.ValidateSource(
                "NotARealCommandAtAll(1);\n", "unknown-command", strict: true);
            HeadlessHarness.Assert(
                !unknown.Success,
                "Strict validation accepted an unknown command — F5 would launch a broken game.");

            PgslValidationReport arity = PgslScriptValidator.ValidateSource(
                "SetAlarm(0);\n", "bad-arity", strict: true);
            HeadlessHarness.Assert(!arity.Success, "Strict validation accepted the wrong argument count.");

            PgslValidationReport good = PgslScriptValidator.ValidateSource(
                "DrawSphere3D(0, 0, 0, 1);\n", "implemented", strict: true);
            HeadlessHarness.Assert(
                good.Success,
                "Strict validation rejected an implemented command: " + string.Join(" | ", good.Errors));
        });
    }

    /// <summary>CPU-only rendering contracts. A drift here is a wrong pixel three suites later.</summary>
    public static void RenderingContracts(HeadlessContext ctx)
    {
        HeadlessHarness.Step("shader constant buffers match their HLSL declarations", () =>
        {
            // Shaders are the one thing a game may author outside PGSL, so their contract with the
            // managed structs blitted into them is checked rather than trusted. Every field after a
            // mismatch is read from the wrong offset by the GPU.
            IReadOnlyList<ConstantBufferComparison> comparisons = ShaderLayoutAudit.CompareAll();
            HeadlessHarness.Assert(
                comparisons.Count > 0,
                "ShaderLayoutAudit compared nothing — the pairing table or shader sources are empty.");

            ConstantBufferComparison[] broken = comparisons.Where(c => !c.IsConsistent).ToArray();
            HeadlessHarness.Assert(
                broken.Length == 0,
                $"{broken.Length} of {comparisons.Count} constant buffers disagree with their struct:"
                + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, broken.Take(2).Select(c => c.Describe())));
        });

        HeadlessHarness.Step("shader targets compile to every native backend representation", () =>
        {
            HeadlessHarness.Assert(
                ShaderCompiler.GetProfile(GpuShaderStage.Vertex, GpuShaderBinaryFormat.Dxbc) == "vs_5_0",
                "DX11 must keep Shader Model 5 DXBC until the reference backend is retired.");
            HeadlessHarness.Assert(
                ShaderCompiler.GetProfile(GpuShaderStage.Pixel, GpuShaderBinaryFormat.Dxil) == "ps_6_0",
                "DX12 must request an explicit Shader Model 6 DXIL profile.");
            HeadlessHarness.Assert(
                ShaderCompiler.GetProfile(GpuShaderStage.Vertex, GpuShaderBinaryFormat.SpirV) == "vs_6_0",
                "Vulkan must share the Shader Model 6 HLSL source contract.");
            HeadlessHarness.Assert(
                ShaderCompiler.GetProfile(GpuShaderStage.Pixel, GpuShaderBinaryFormat.GlslUtf8) == "ps_6_0",
                "OpenGL translation must originate from the shared Shader Model 6 source contract.");

            string cache = Path.Combine(ctx.Workspace, "shader-contract-cache");
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);

            const string vertexSource =
                "float4 VS(float4 position : POSITION) : SV_Position { return position; }";
            ShaderCompileResult dxbcFirst = ShaderCompiler.CompileForBackend(
                vertexSource,
                "VS",
                GpuShaderStage.Vertex,
                GpuShaderBinaryFormat.Dxbc,
                cacheRoot: cache);
            ShaderCompileResult dxbcSecond = ShaderCompiler.CompileForBackend(
                vertexSource,
                "VS",
                GpuShaderStage.Vertex,
                GpuShaderBinaryFormat.Dxbc,
                cacheRoot: cache);

            HeadlessHarness.Assert(dxbcFirst.Blob.Length > 0, "DXBC compiler returned an empty shader.");
            HeadlessHarness.Assert(!dxbcFirst.CacheHit, "A clean DXBC cache unexpectedly reported a hit.");
            HeadlessHarness.Assert(dxbcSecond.CacheHit, "The identical second DXBC compile did not hit the disk cache.");
            HeadlessHarness.Assert(
                dxbcFirst.Blob.AsSpan().SequenceEqual(dxbcSecond.Blob),
                "A DXBC cache hit returned different bytecode from the original compile.");

            ShaderCompileResult dxilFirst = ShaderCompiler.CompileForBackend(
                vertexSource,
                "VS",
                GpuShaderStage.Vertex,
                GpuShaderBinaryFormat.Dxil,
                cacheRoot: cache);
            ShaderCompileResult dxilSecond = ShaderCompiler.CompileForBackend(
                vertexSource,
                "VS",
                GpuShaderStage.Vertex,
                GpuShaderBinaryFormat.Dxil,
                cacheRoot: cache);
            HeadlessHarness.Assert(dxilFirst.Blob.Length > 0, "DXC returned an empty DXIL shader.");
            HeadlessHarness.Assert(!dxilFirst.CacheHit, "A clean DXIL cache unexpectedly reported a hit.");
            HeadlessHarness.Assert(dxilSecond.CacheHit, "The identical second DXIL compile did not hit the disk cache.");
            HeadlessHarness.Assert(
                dxilFirst.BinaryFormat == GpuShaderBinaryFormat.Dxil,
                "DX12 compilation returned the wrong binary format tag.");

            // Compile every built-in stage, not merely a toy shader. This is the gate that found the
            // TerrainShader resource-alias problem during P5.2a.
            EngineShaderCatalog.CompileAll(
                GpuShaderBinaryFormat.Dxil,
                Path.Combine(cache, "catalog-dxil"));

            ShaderCompileResult spirvFirst = ShaderCompiler.CompileForBackend(
                vertexSource,
                "VS",
                GpuShaderStage.Vertex,
                GpuShaderBinaryFormat.SpirV,
                cacheRoot: cache);
            ShaderCompileResult spirvSecond = ShaderCompiler.CompileForBackend(
                vertexSource,
                "VS",
                GpuShaderStage.Vertex,
                GpuShaderBinaryFormat.SpirV,
                cacheRoot: cache);
            HeadlessHarness.Assert(!spirvFirst.CacheHit, "A clean SPIR-V cache unexpectedly reported a hit.");
            HeadlessHarness.Assert(spirvSecond.CacheHit, "The identical second SPIR-V compile did not hit the disk cache.");
            HeadlessHarness.Assert(
                spirvFirst.Blob.AsSpan().SequenceEqual(spirvSecond.Blob),
                "A SPIR-V cache hit returned different bytecode from the original compile.");

            // The register-shift policy outlives the Vulkan backend: the OpenGL path compiles the
            // same shifted SPIR-V and unshifts it back to GL binding points in SpirvCrossToolchain.
            HeadlessHarness.Assert(
                VulkanShaderBindingPolicy.BindingForRegister('b', 0) == 0
                && VulkanShaderBindingPolicy.BindingForRegister('t', 0) == 32
                && VulkanShaderBindingPolicy.BindingForRegister('s', 0) == 64
                && VulkanShaderBindingPolicy.BindingForRegister('u', 0) == 96,
                "The SPIR-V binding ranges changed; the OpenGL binding remap would no longer match compiled shaders.");

            EngineShaderCatalog.CompileAll(
                GpuShaderBinaryFormat.SpirV,
                Path.Combine(cache, "catalog-spirv"));

            // OpenGL consumes GLSL translated from the same SPIR-V. A nested float3 pad that DXC
            // can pack and Vulkan can consume still fails SPIRV-Cross with ErrorUnsupportedSpirv.
            EngineShaderCatalog.CompileAll(
                GpuShaderBinaryFormat.GlslUtf8,
                Path.Combine(cache, "catalog-glsl"));
        });

        HeadlessHarness.Step("all seven renderer choices have a complete, unambiguous catalog contract", () =>
        {
            var expectedFormats = new Dictionary<RenderBackendOption, GpuShaderBinaryFormat>
            {
                [RenderBackendOption.SilkNetDx11] = GpuShaderBinaryFormat.Dxbc,
                [RenderBackendOption.Direct3D12] = GpuShaderBinaryFormat.Dxil,
                [RenderBackendOption.Vulkan] = GpuShaderBinaryFormat.SpirV,
                [RenderBackendOption.OpenGL] = GpuShaderBinaryFormat.GlslUtf8,
                [RenderBackendOption.Software] = GpuShaderBinaryFormat.SpirV,
            };

            RenderBackendOption[] enumValues = Enum.GetValues<RenderBackendOption>();
            HeadlessHarness.Assert(
                RenderBackendCatalog.All.Count == expectedFormats.Count
                && enumValues.Length == expectedFormats.Count,
                $"The backend registry must contain exactly the five supported renderers; catalog="
                + $"{RenderBackendCatalog.All.Count}, enum={enumValues.Length}.");
            HeadlessHarness.Assert(
                RenderBackendCatalog.All.Select(item => item.Backend).Distinct().Count()
                    == expectedFormats.Count,
                "The backend registry contains a duplicate backend enum value.");
            HeadlessHarness.Assert(
                RenderBackendCatalog.All.Select(item => item.SettingsValue)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == expectedFormats.Count,
                "Two renderer choices share a persisted settings value.");

            foreach ((RenderBackendOption backend, GpuShaderBinaryFormat format) in expectedFormats)
            {
                RenderBackendDescriptor descriptor = RenderBackendCatalog.Describe(backend);
                HeadlessHarness.Assert(
                    descriptor.Backend == backend && descriptor.IsImplemented,
                    $"{backend} is missing from the catalog or is still marked unimplemented.");
                HeadlessHarness.Assert(
                    descriptor.ShaderBinaryFormat == format,
                    $"{backend} uses {descriptor.ShaderBinaryFormat}; expected {format}.");
                HeadlessHarness.Assert(
                    RenderBackendCatalog.ParseSettingsValue(descriptor.SettingsValue) == backend,
                    $"{backend}'s persisted value does not round-trip through the parser.");
                foreach (string alias in descriptor.Aliases)
                {
                    HeadlessHarness.Assert(
                        RenderBackendCatalog.ParseSettingsValue(alias) == backend,
                        $"Alias '{alias}' does not select {backend}.");
                }
            }
        });

        HeadlessHarness.Step("retired renderers migrate preferences but reject explicit runtime requests", () =>
        {
            HeadlessHarness.Assert((int)RenderBackendOption.Software == 6
                && (int)RenderBackendOption.OpenGL == 3, "Retained renderer IDs changed.");
            foreach (string retired in new[] { "WebGPU", "wgpu", "SDL3GPU", "sdl3", "sdl", "SDL3 GPU", "4", "5" })
            {
                HeadlessHarness.Assert(RenderBackendCatalog.ParseSettingsValue(retired) == RenderBackendOption.SilkNetDx11,
                    $"The retired preference '{retired}' did not resolve to DX11.");
                bool rejected = false;
                try { RenderBackendCatalog.ParseExplicitValue(retired); }
                catch (ArgumentException exception) { rejected = exception.Message.Contains("removed", StringComparison.Ordinal); }
                HeadlessHarness.Assert(rejected, $"The explicit renderer request '{retired}' was silently accepted.");
                string preferencesPath = Path.Combine(ctx.Workspace, "retired-renderer-preferences.json");
                File.WriteAllText(preferencesPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    rendering = new { backend = retired },
                    appearance = new { codeFontSize = 15 },
                }));
                Genesis.Application.Core.Settings.SettingsService migrated = new(preferencesPath);
                HeadlessHarness.Assert(migrated.Current.Rendering.Backend == "Direct3D11"
                    && migrated.MigrationNotice is not null && migrated.Current.Appearance.CodeFontSize == 15,
                    "Renderer migration lost preferences or omitted the diagnostic.");
                migrated.Save();
                HeadlessHarness.Assert(new Genesis.Application.Core.Settings.SettingsService(preferencesPath).Current.Rendering.Backend == "Direct3D11",
                    "The migrated renderer did not persist after saving preferences.");
            }
        });

        HeadlessHarness.Step("every built-in SPIR-V module passes the Vulkan SDK validator", () =>
        {
            // Checked by spirv-val rather than by Genesis's own code: SPIR-V feeds the shipping
            // OpenGL backend through SPIRV-Cross, and a module that is subtly malformed shows up as
            // a blank frame rather than an error. An independent implementation of the spec is the
            // only check that does not share this codebase's assumptions.
            string? validator = SpirVValidation.FindValidator();
            if (validator is null)
            {
                // Loudly, never silently: a check that quietly does nothing is worse than none.
                Console.WriteLine(
                    "      SPIR-V validation SKIPPED — spirv-val was not found. Install the Vulkan SDK "
                    + "or set VULKAN_SDK to enable it.");
                return;
            }

            string work = Path.Combine(ctx.Workspace, "spirv-validation");
            IReadOnlyList<string> failures =
                SpirVValidation.ValidateEngineShaders(validator, work, out int moduleCount);

            HeadlessHarness.Assert(
                moduleCount > 0, "The engine shader catalog compiled no SPIR-V modules to validate.");
            HeadlessHarness.Assert(
                failures.Count == 0,
                $"{failures.Count} of {moduleCount} SPIR-V modules failed validation: "
                + string.Join(" | ", failures.Take(3)));

            Console.WriteLine($"      {moduleCount} SPIR-V modules validated with {Path.GetFileName(validator)}.");
        });

        HeadlessHarness.Step("DX12 selection, descriptor plan and resource-state contract are ready", () =>
        {
            try
            {
                Dx12BackendReadiness.Validate();

                RenderBackendDescriptor dx11 = RenderBackendCatalog.Describe(RenderBackendOption.SilkNetDx11);
                RenderBackendDescriptor dx12 = RenderBackendCatalog.Describe(RenderBackendOption.Direct3D12);
                HeadlessHarness.Assert(
                    dx11.IsImplemented && dx11.ShaderBinaryFormat == GpuShaderBinaryFormat.Dxbc,
                    "DX11 must remain the implemented DXBC reference backend during Phase 6.");
                // P6.1 flipped this: DX12 was registered-but-unavailable while only its readiness
                // plan existed. It now has a device, renders through the shared renderer, and is a
                // Build.bat smoke gate.
                HeadlessHarness.Assert(
                    dx12.IsImplemented && dx12.ShaderBinaryFormat == GpuShaderBinaryFormat.Dxil,
                    "DX12 must stay a registered DXIL backend while it is unselectable.");

                // Selecting DX12 yields DX12 on capable hardware and falls back to DX11 otherwise.
                // Both are correct outcomes; the request being silently dropped is not.
                RenderBackendSelection.Configure(RenderBackendOption.Direct3D12);
                bool dx12Ready = RenderBackendSelection.IsDirect3D12Available();
                HeadlessHarness.Assert(
                    RenderBackendSelection.RequestedBackend == RenderBackendOption.Direct3D12,
                    "Requesting DX12 did not record the request.");
                HeadlessHarness.Assert(
                    dx12Ready
                        ? RenderBackendSelection.EffectiveBackend == RenderBackendOption.Direct3D12
                          && !RenderBackendSelection.IsFallbackActive
                        : RenderBackendSelection.EffectiveBackend == RenderBackendOption.SilkNetDx11
                          && RenderBackendSelection.IsFallbackActive,
                    dx12Ready
                        ? "DX12 is available here but was not selected."
                        : "DX12 is unavailable here and did not fall back to DX11.");

                EngineRenderingDefaults.ConfigureFog(true, "#123456", 40f, 160f, 0.65f);
                EngineFogDefaults fog = EngineRenderingDefaults.Fog;
                HeadlessHarness.Assert(
                    fog.Enabled
                    && EngineRenderingDefaults.ToHexColor(fog.Color) == "#123456"
                    && Math.Abs(fog.Start - 40f) < 0.001f
                    && Math.Abs(fog.End - 160f) < 0.001f
                    && Math.Abs(fog.Alpha - 0.65f) < 0.001f,
                    "Engine fog defaults were not applied through the shared rendering contract.");

                RoomFogState spriteFog = RoomFogState.CreateDepthThickness(
                    true, new System.Numerics.Vector4(1f, 0f, 0f, 0.5f), depth: 40f, thickness: 120f, alpha: 0.5f);
                HeadlessHarness.Assert(
                    Math.Abs(spriteFog.EvaluateDepth(0f)) < 0.001f
                    && Math.Abs(spriteFog.EvaluateDepth(100f) - 0.25f) < 0.001f
                    && Math.Abs(spriteFog.EvaluateDepth(160f) - 0.5f) < 0.001f,
                    "2D room fog does not map authored Z/layer depth through depth + thickness + alpha.");

                System.Numerics.Vector4 foggedBackground = spriteFog.ApplyToBackground(
                    new System.Numerics.Vector4(0f, 0f, 1f, 1f));
                HeadlessHarness.Assert(
                    foggedBackground.X > 0.49f && foggedBackground.Z > 0.49f && Math.Abs(foggedBackground.W - 1f) < 0.001f,
                    "2D background fog must blend toward fog RGB without destroying source alpha.");

                using (var roomScene = new Genesis.Runtime.RuntimeScene("Room fog defaults"))
                {
                    HeadlessHarness.Assert(
                        roomScene.Environment.FogEnabled
                        && EngineRenderingDefaults.ToHexColor(roomScene.Environment.FogColor) == "#123456"
                        && Math.Abs(roomScene.Environment.FogColor.W - 0.65f) < 0.001f
                        && Math.Abs(roomScene.Environment.FogStart - 40f) < 0.001f
                        && Math.Abs(roomScene.Environment.FogEnd - 160f) < 0.001f,
                        "A newly-created Room scene did not inherit the configured Engine Default fog values.");
                }

                IDictionary<string, string> environment = EngineRenderingDefaults.BuildEnvironment(
                    RenderBackendOption.Direct3D12,
                    fogEnabled: true,
                    fogColorHex: "#123456",
                    fogStart: 40f,
                    fogEnd: 160f,
                    fogAlpha: 0.65f);
                HeadlessHarness.Assert(
                    environment[RenderBackendSelection.EnvironmentVariable] == "Direct3D12"
                    && environment[EngineRenderingDefaults.FogEnabledEnvironmentVariable] == "1"
                    && environment[EngineRenderingDefaults.FogColorEnvironmentVariable] == "#123456"
                    && environment[EngineRenderingDefaults.FogAlphaEnvironmentVariable] == "0.65",
                    "F5 rendering preferences are not serialisable into the Player environment.");

                // Through the Studio bridge, not around it. Everything above configures the engine
                // directly, which is why a bridge that quietly dropped FogAlpha went unnoticed: the
                // setting saved, the engine kept its old alpha, and no assertion crossed the seam
                // where settings become engine state. Fog is a property of the game, so the seam
                // starts at the project manifest.
                Genesis.Application.Core.Projects.ProjectManifest manifest = new()
                {
                    ProjectId = Guid.NewGuid().ToString("N"),
                    Name = "Fog Seam",
                };
                manifest.Rendering.FogEnabled = true;
                manifest.Rendering.FogColorHex = "#204060";
                manifest.Rendering.FogStart = 12f;
                manifest.Rendering.FogEnd = 96f;
                manifest.Rendering.FogAlpha = 0.4f;
                manifest.Runtime.AllowEscapeToClose = false;

                Genesis.Application.Studio.RenderingPreferencesBridge.ApplyProject(manifest);
                EngineFogDefaults applied = EngineRenderingDefaults.Fog;
                HeadlessHarness.Assert(
                    applied.Enabled
                    && EngineRenderingDefaults.ToHexColor(applied.Color) == "#204060"
                    && Math.Abs(applied.Start - 12f) < 0.001f
                    && Math.Abs(applied.End - 96f) < 0.001f
                    && Math.Abs(applied.Alpha - 0.4f) < 0.001f,
                    $"A project's fog did not reach the engine (alpha={applied.Alpha:0.###}, expected 0.4).");

                IDictionary<string, string> fromProject =
                    Genesis.Application.Studio.RenderingPreferencesBridge.BuildPlayerEnvironment(
                        new Genesis.Application.Core.Settings.RenderingSettings
                        {
                            FaceCulling = "Front",
                            FrontFaceWinding = "CounterClockwise",
                            LightingEnabled = false,
                            ShadowsEnabled = true,
                            ShadowStrength = 0.4f,
                        }, manifest);
                HeadlessHarness.Assert(
                    fromProject[EngineRenderingDefaults.FogColorEnvironmentVariable] == "#204060"
                    && fromProject[EngineRenderingDefaults.FogAlphaEnvironmentVariable] == "0.4"
                    && fromProject[EngineRenderingDefaults.AllowEscapeEnvironmentVariable] == "0"
                    && fromProject[MeshRasterDefaults.CullingEnvironmentVariable] == "Front"
                    && fromProject[MeshRasterDefaults.WindingEnvironmentVariable] == "CounterClockwise"
                    && fromProject[MeshLightingDefaults.LightingEnvironmentVariable] == "0"
                    && fromProject[MeshLightingDefaults.ShadowsEnvironmentVariable] == "1"
                    && fromProject[MeshLightingDefaults.ShadowStrengthEnvironmentVariable] == "0.4",
                    "F5 would launch the game without the project's own settings: "
                    + string.Join(", ", fromProject.Select(pair => $"{pair.Key}={pair.Value}")));
            }
            finally
            {
                // Never leak this test's requested backend/fog into the template cases that follow.
                RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
                EngineRenderingDefaults.ConfigureFog(false, "#9EC7F0", 35f, 120f, 1f);
            }
        });

        HeadlessHarness.Step("the renderers stay backend-neutral", () =>
        {
            // Both renderers must talk to IGpuDevice only. A direct DX11 type in shared code is a
            // Vulkan or OpenGL backend that cannot exist.
            string[] forbidden = ["Silk.NET", "ID3D11", "ComPtr<", "_ctx->", "_dev->"];
            foreach (string relative in new[]
            {
                Path.Combine("Source", "Runtime", "Genesis.Rendering", "Primitives", "SpriteRenderer.cs"),
                Path.Combine("Source", "Runtime", "Genesis.Rendering", "Primitives", "ForwardRenderer.cs"),
            })
            {
                string source = File.ReadAllText(Path.Combine(RepositoryRoot(), relative));
                string[] violations = forbidden
                    .Where(token => source.Contains(token, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                HeadlessHarness.Assert(
                    violations.Length == 0,
                    $"{Path.GetFileName(relative)} crossed the backend boundary: {string.Join(", ", violations)}.");
                HeadlessHarness.Assert(
                    source.Contains("IGpuDevice", StringComparison.Ordinal),
                    $"{Path.GetFileName(relative)} no longer declares the IGpuDevice boundary.");
            }
        });

        HeadlessHarness.Step("procedural nature models are deterministic and cover every preset", () =>
        {
            ProceduralTreeOptions options = new()
            {
                Preset = ProceduralTreePreset.TwistedAncient,
                Quality = ProceduralModelQuality.Draft,
                Seed = 0xA37E_2026UL,
            };
            ProceduralMeshResult first = ProceduralModelGenerator.GenerateTree(options);
            ProceduralMeshResult second = ProceduralModelGenerator.GenerateTree(options.Clone());
            HeadlessHarness.Assert(
                first.Vertices.Length > 100 && first.Indices.Length > 300,
                $"Twisted Ancient generated only {first.Vertices.Length} vertices/{first.Indices.Length / 3} triangles.");
            HeadlessHarness.Assert(
                first.Indices.AsSpan().SequenceEqual(second.Indices)
                && first.Vertices.Select(vertex => vertex.Position)
                    .SequenceEqual(second.Vertices.Select(vertex => vertex.Position)),
                "The same tree preset and seed produced different geometry.");

            ProceduralMeshResult changed = ProceduralModelGenerator.GenerateTree(new ProceduralTreeOptions
            {
                Preset = options.Preset,
                Quality = options.Quality,
                Seed = options.Seed + 1,
            });
            HeadlessHarness.Assert(
                !first.Vertices.Select(vertex => vertex.Position)
                    .SequenceEqual(changed.Vertices.Select(vertex => vertex.Position)),
                "Changing the tree seed did not change the geometry.");

            foreach (ProceduralTreePreset preset in Enum.GetValues<ProceduralTreePreset>())
            {
                ProceduralMeshResult tree = ProceduralModelGenerator.GenerateTree(new ProceduralTreeOptions
                {
                    Preset = preset,
                    Quality = ProceduralModelQuality.Draft,
                    Seed = 42,
                });
                HeadlessHarness.Assert(
                    tree.Vertices.Length > 0 && tree.Indices.Length > 0,
                    $"Tree preset {preset} generated an empty mesh.");
                float minimumY = tree.Vertices.Min(vertex => vertex.Position.Y);
                HeadlessHarness.Assert(
                    minimumY <= 0.05f,
                    $"Tree preset {preset} floats above its origin (minimum Y {minimumY:0.###}).");
            }

            foreach (ProceduralRockPreset preset in Enum.GetValues<ProceduralRockPreset>())
            {
                ProceduralMeshResult rock = ProceduralModelGenerator.GenerateRock(new ProceduralRockOptions
                {
                    Preset = preset,
                    Quality = ProceduralModelQuality.Draft,
                    Seed = 84,
                });
                HeadlessHarness.Assert(
                    rock.Vertices.Length > 0 && rock.Indices.Length > 0,
                    $"Rock preset {preset} generated an empty mesh.");
            }
        });

        HeadlessHarness.Step("day-night weather is one authoritative runtime environment frame", () =>
        {
            EnvironmentService climate = new(new EnvironmentOptions
            {
                Seed = 90210,
                StartTimeHours = 12f,
                TimeScale = 24f,
                AutomaticWeather = false,
            });
            climate.SetWeather(WeatherKind.Rain, 0f);
            EnvironmentFrame noon = climate.Update(0f, System.Numerics.Vector3.Zero);
            HeadlessHarness.Assert(noon.NightFactor < 0.25f, $"Noon is {noon.NightFactor:P0} night.");
            for (int i = 0; i < 20; i++) climate.Update(1f, System.Numerics.Vector3.Zero);
            HeadlessHarness.Assert(
                climate.Current.GroundWetness > 0.1f && climate.Current.LocalRain > 0.5f,
                "Sustained rain did not wet the ground or reach the local frame.");
            ParticleConfig? rain = climate.CreatePrecipitationConfig();
            HeadlessHarness.Assert(
                rain is { DownwardEmit: true, FollowCameraXZ: true },
                "Rain weather did not produce the runtime precipitation emitter.");

            climate.SetWeather(WeatherKind.Snow, 0f);
            for (int i = 0; i < 20; i++) climate.Update(1f, System.Numerics.Vector3.Zero);
            HeadlessHarness.Assert(
                climate.Current.SnowAccumulation > 0.1f && climate.Current.LocalTemperatureC < 0f,
                "Snow weather did not accumulate in freezing conditions.");

            climate.SetTimeOfDay(0f);
            EnvironmentFrame midnight = climate.Update(0f, System.Numerics.Vector3.Zero);
            HeadlessHarness.Assert(midnight.NightFactor > 0.75f, $"Midnight is only {midnight.NightFactor:P0} night.");
            HeadlessHarness.Assert(
                midnight.BackgroundColor.X < noon.BackgroundColor.X,
                "Night sky did not become darker than the daytime sky.");

            SceneEnvironment scene = new();
            climate.ApplyTo(scene);
            HeadlessHarness.Assert(
                scene.SunIntensity < 0.2f
                && System.Numerics.Vector4.Distance(scene.BackgroundColor, midnight.BackgroundColor) < 0.001f
                && System.Numerics.Vector4.Distance(scene.FogColor, midnight.FogColor) < 0.001f,
                "Applying the midnight frame did not map its sky/fog and moonlight into the scene.");

            EnvironmentPersistenceState saved = climate.CaptureState();
            EnvironmentService restored = new(climate.Options);
            restored.RestoreState(saved);
            HeadlessHarness.Assert(
                restored.ActiveWeather == WeatherKind.Snow
                && Math.Abs(restored.Current.SnowAccumulation - saved.SnowAccumulation) < 0.001f,
                "Environment persistence lost weather or surface accumulation.");

            EnvironmentService automaticA = new(new EnvironmentOptions { Seed = 77, AutomaticWeather = true });
            EnvironmentService automaticB = new(new EnvironmentOptions { Seed = 77, AutomaticWeather = true });
            automaticA.Update(0.1f, System.Numerics.Vector3.Zero);
            automaticB.Update(0.1f, System.Numerics.Vector3.Zero);
            HeadlessHarness.Assert(
                automaticA.ActiveWeather == automaticB.ActiveWeather,
                "Seeded automatic weather selected different schedules for identical worlds.");
        });

        HeadlessHarness.Step("shallow-water bodies conserve volume and expose displaced render geometry", () =>
        {
            WaterBody body = WaterBody.CreateLakePreset(
                "Simulation gate", System.Numerics.Vector3.Zero, size: 16f, surfaceY: 3f);
            body.SimulationEnabled = true;
            body.SimulationResolution = 24;
            body.SimulationDepth = 2.5f;
            WaterBodySimulation simulation = new(body);
            simulation.Disturb(
                new System.Numerics.Vector3(0f, 3f, 0f),
                radiusMetres: 1.8f,
                strength: 0.7f,
                push: new System.Numerics.Vector2(0.8f, -0.25f),
                foam: 0.9f);
            int steps = simulation.Update(1f / 30f);
            HeadlessHarness.Assert(steps == 2, $"A 1/30s water update took {steps} fixed steps, expected 2.");

            (float height, float foam) = simulation.SampleSurface(0f, 0f);
            HeadlessHarness.Assert(
                Math.Abs(height) > 0.001f && foam > 0.05f,
                $"The disturbed surface sampled height={height:0.####}, foam={foam:0.####}.");
            double mean = simulation.Cells.Span.ToArray()
                .Where(cell => cell.Wet)
                .Average(cell => cell.Height);
            HeadlessHarness.Assert(
                Math.Abs(mean) < 0.0001,
                $"The conservative solver drifted to mean height {mean:0.######}.");

            Genesis.World.MeshData mesh = simulation.BuildMesh();
            HeadlessHarness.Assert(
                mesh.Vertices.Length == 24 * 24
                && mesh.Indices.Length == 23 * 23 * 6
                && mesh.Vertices.Any(vertex => Math.Abs(vertex.Position.Y - body.SurfaceY) > 0.001f),
                "Shallow-water state did not produce a displaced render mesh.");
            HeadlessHarness.Assert(
                simulation.Update(10f) <= 6,
                "A hitch made shallow-water simulation exceed its fixed catch-up cap.");
            HeadlessHarness.Assert(
                simulation.SampleSurface(100f, 100f) == (0f, 0f),
                "Sampling outside the water body returned a wave.");
        });

        HeadlessHarness.Step("natural-world authoring is deterministic, queryable, cacheable and mappable", () =>
        {
            TerrainAsset terrain = new(65, 65, 1f, 0f, 0f, -8f, 28f);
            terrain.ApplySculptBrush(18f, 20f, 14f, 7f, raise: true);
            terrain.ApplySculptBrush(47f, 42f, 11f, 5f, raise: true);
            TerrainPathSettings pathSettings = new()
            {
                Seed = 44021, PathCount = 3, Width = 3.5f, GradeStrength = 0.7f, SplatChannel = 1,
            };
            TerrainPathNetwork pathsA = TerrainPathNetwork.Generate(terrain, pathSettings);
            TerrainPathNetwork pathsB = TerrainPathNetwork.Generate(terrain, new TerrainPathSettings
            {
                Seed = pathSettings.Seed, PathCount = pathSettings.PathCount, Width = pathSettings.Width,
                GradeStrength = pathSettings.GradeStrength, SplatChannel = pathSettings.SplatChannel,
            });
            HeadlessHarness.Assert(pathsA.Paths.Count == 3 && pathsA.Paths.All(path => path.Points.Count >= 2),
                "Connected path generation did not produce three usable routes.");
            HeadlessHarness.Assert(pathsA.Paths.SelectMany(path => path.Points).SequenceEqual(pathsB.Paths.SelectMany(path => path.Points)),
                "Identical terrain path seeds produced different centre lines.");
            pathsA.ApplyTo(terrain, pathSettings.GradeStrength, pathSettings.SplatChannel);

            FoliageScatterSettings foliageSettings = new()
            {
                Seed = 8142, Preset = FoliagePreset.Meadow, MaximumInstances = 700,
                Density = 0.9f, MinimumSpacing = 1.3f, PathExclusion = 1.3f,
            };
            FoliageField foliageA = FoliageScatter.Generate(terrain, pathsA, foliageSettings);
            FoliageField foliageB = FoliageScatter.Generate(terrain, pathsA, new FoliageScatterSettings
            {
                Seed = foliageSettings.Seed, Preset = foliageSettings.Preset,
                MaximumInstances = foliageSettings.MaximumInstances, Density = foliageSettings.Density,
                MinimumSpacing = foliageSettings.MinimumSpacing, PathExclusion = foliageSettings.PathExclusion,
            });
            HeadlessHarness.Assert(foliageA.Instances.Count > 40,
                $"Ecological scatter produced only {foliageA.Instances.Count} instances.");
            HeadlessHarness.Assert(foliageA.Instances.SequenceEqual(foliageB.Instances),
                "Identical foliage settings produced different instances.");
            HeadlessHarness.Assert(foliageA.Instances.All(instance =>
            {
                TerrainPathSample sample = pathsA.Sample(instance.Position.X, instance.Position.Z);
                return !sample.IsValid || sample.Distance >= sample.Width * foliageSettings.PathExclusion;
            }), "Foliage was scattered inside an authored path exclusion zone.");

            float lakeY = terrain.SampleHeight(32f, 32f) + 1.5f;
            TerrainNatureDocument nature = new()
            {
                PathSettings = pathSettings,
                Paths = pathsA.Paths.Select(path => new TerrainPathDefinition
                {
                    Id = path.Id, Name = path.Name, Kind = path.Kind, Width = path.Width,
                    Points = new List<System.Numerics.Vector3>(path.Points),
                }).ToList(),
                FoliageSettings = foliageSettings,
                WaterBodies =
                [
                    new TerrainWaterDefinition
                    {
                        Name = "Gate Lake", Center = new System.Numerics.Vector3(32f, lakeY, 32f),
                        SurfaceHeight = lakeY, SizeX = 14f, SizeZ = 12f,
                    },
                ],
                PointsOfInterest =
                [
                    new TerrainPointOfInterest { Name = "Berry Patch", Category = "Forage Food", Position = new System.Numerics.Vector3(50f, terrain.SampleHeight(50f, 50f), 50f) },
                ],
            };
            string resource = Path.Combine(ctx.Workspace, "NatureGate.terrain.json");
            string cache = Path.Combine(ctx.Workspace, "NatureGate.gfoliage");
            nature.FoliageCacheFile = Path.GetFileName(cache);
            nature.FoliageCacheSha256 = FoliageFieldCache.Save(cache, foliageA);
            nature.FoliageInstanceCount = foliageA.Instances.Count;
            TerrainNatureSerializer.Save(resource, nature);
            TerrainNatureDocument reloadedNature = TerrainNatureSerializer.LoadOrDefault(resource);
            FoliageField reloadedFoliage = FoliageFieldCache.Load(cache, reloadedNature.FoliageCacheSha256);
            HeadlessHarness.Assert(reloadedNature.Paths.Count == nature.Paths.Count
                && reloadedFoliage.Instances.SequenceEqual(foliageA.Instances),
                "Nature sidecar/cache round-trip changed routes or foliage.");

            WorldManifest manifest = WorldManifest.Build(terrain, reloadedNature, chunkSize: 20f);
            string manifestPath = Path.Combine(ctx.Workspace, "NatureGate.world.json");
            WorldManifestSerializer.Save(manifestPath, manifest);
            WorldQuery query = new(terrain, WorldManifestSerializer.Load(manifestPath));
            HeadlessHarness.Assert(query.WaterDepthAt(32f, 32f) > 0f
                && query.PointsNear(nature.PointsOfInterest[0].Position, 5f).Count == 1,
                "World query did not resolve authored water and points of interest.");
            WorldMapImage map = WorldMapBaker.Bake(query, 128, 128);
            HeadlessHarness.Assert(map.Pixels.Length == 128 * 128 * 4 && map.Pixels.Where((_, index) => index % 4 != 3).Distinct().Count() > 12,
                "World-map bake is empty or visually flat.");
            MapDiscovery discovery = new(manifest.Bounds, 32);
            HeadlessHarness.Assert(discovery.Reveal(new System.Numerics.Vector3(32f, 0f, 32f), 10f) && discovery.ExploredFraction > 0f,
                "Map discovery did not reveal the observer's area.");
        });

        HeadlessHarness.Step("atmosphere, adaptive audio and agent decisions share the authoritative world", () =>
        {
            TerrainAsset terrain = new(49, 49, 1f, 0f, 0f, -2f, 12f);
            float lakeY = terrain.SampleHeight(36f, 36f) + 1f;
            TerrainNatureDocument nature = new()
            {
                WaterBodies = [new TerrainWaterDefinition { Center = new System.Numerics.Vector3(36f, lakeY, 36f), SurfaceHeight = lakeY, SizeX = 10f, SizeZ = 10f }],
                PointsOfInterest = [new TerrainPointOfInterest { Name = "Food", Category = "Forage Food", Position = new System.Numerics.Vector3(40f, 0f, 8f) }],
            };
            WorldQuery query = new(terrain, WorldManifest.Build(terrain, nature));
            EnvironmentService climate = new(new EnvironmentOptions { Seed = 91, StartTimeHours = 15f, AutomaticWeather = false });
            climate.SetWeather(WeatherKind.Thunderstorm, 0f);
            EnvironmentFrame environment = climate.Update(0.1f, new System.Numerics.Vector3(32f, lakeY, 32f));
            AtmosphereOptions options = new() { Preset = AtmospherePreset.Storm, CloudQuality = 3, Seed = 91 };
            AtmosphereService atmosphereA = new(options);
            AtmosphereService atmosphereB = new(options);
            AtmosphereFrame skyA = atmosphereA.Update(environment, new System.Numerics.Vector3(32f, lakeY, 32f));
            AtmosphereFrame skyB = atmosphereB.Update(environment, new System.Numerics.Vector3(32f, lakeY, 32f));
            HeadlessHarness.Assert(skyA.CloudVolumes.Count == 4 && skyA.CloudVolumes.SequenceEqual(skyB.CloudVolumes),
                "Deterministic storm cloud volumes changed between identical atmosphere services.");
            HeadlessHarness.Assert(skyA.CloudCoverage > 0.9f && skyA.SunIntensity < 0.3f,
                "Storm atmosphere did not darken the sun and close the cloud cover.");

            EnvironmentAudioMix mix = EnvironmentAudioMixer.Evaluate(environment, query, new System.Numerics.Vector3(34f, lakeY, 34f));
            HeadlessHarness.Assert(mix.Rain > 0.5f && mix.Water > 0.5f,
                $"Adaptive ambience ignored rain/water (rain={mix.Rain:0.##}, water={mix.Water:0.##}).");

            GoapAction gather = new("Gather", AgentFacts.Hungry, AgentFacts.None, AgentFacts.HasFood, AgentFacts.None, 1f);
            GoapAction wait = new("Wait", AgentFacts.None, AgentFacts.None, AgentFacts.Safe, AgentFacts.None, 4f);
            IReadOnlyList<GoapAction> plan = GoapPlanner.Plan(AgentFacts.Hungry, AgentFacts.HasFood, [wait, gather]);
            HeadlessHarness.Assert(plan.Count == 1 && plan[0].Name == "Gather", "GOAP did not choose the lowest-cost valid plan.");
            BehaviorNode<int> tree = new BehaviorSelector<int>(
                new BehaviorSequence<int>(new BehaviorCondition<int>(value => value < 0), new BehaviorAction<int>((_, _) => BehaviorStatus.Success)),
                new BehaviorAction<int>((_, _) => BehaviorStatus.Success));
            HeadlessHarness.Assert(tree.Tick(1, 0.1f) == BehaviorStatus.Success, "Behaviour-tree selector did not fall through to its valid branch.");

            using RuntimeScene scene = new("Wildlife gate");
            scene.SetWorldQuery(query);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { X = 5f, Y = terrain.SampleHeight(5f, 5f), Z = 5f, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });
            scene.World.Set(entity, new WildlifeComponent
            {
                Preset = Genesis.Runtime.ECS.Components.WildlifePreset.Grazer,
                Home = new System.Numerics.Vector3(5f, 0f, 5f), HomeRadius = 30f,
                Thirst = 0.95f, Energy = 1f, ThirstRate = 0.01f, HungerRate = 0.005f, AwarenessRadius = 80f, Seed = 44,
            });
            scene.World.Set(entity, new AgentComponent { ArrivalRadius = 1f });
            WildlifeSystem wildlifeSystem = new(scene);
            for (int i = 0; i < 30; i++) wildlifeSystem.Update(scene.World, 0.1f);
            TransformComponent moved = scene.World.GetRef<TransformComponent>(entity);
            AgentComponent agent = scene.World.GetRef<AgentComponent>(entity);
            HeadlessHarness.Assert(agent.Activity == AgentActivity.SeekWater && moved.X > 5f && moved.Z > 5f,
                "Thirsty wildlife did not choose water and move toward it through the world query.");
        });
    }

    /// <summary>
    /// The 2D template, played for real: the F5 path, the shipped player, a window, and a screenshot.
    /// </summary>
    public static void TwoDTemplate(
        HeadlessContext ctx,
        GateSuite.GateFixture fixture,
        float seconds,
        int timeoutMilliseconds)
    {
        ProjectSession project = fixture.TwoD;
        string runtimeDir = Genesis.Runtime.RuntimePaths.ResolveRuntimeDir()
            ?? throw new InvalidOperationException(
                "GenesisEngine.exe was not found. Build.bat builds the player before this gate for "
                + "exactly this reason.");
        string expectedBrandingPath = Path.Combine(
            runtimeDir,
            Genesis.Runtime.GenesisBranding.DefaultEmblemRelativePath
                .Replace('/', Path.DirectorySeparatorChar));

        HeadlessHarness.Step("the Player carries the current Genesis branding", () =>
        {
            HeadlessHarness.Assert(
                File.Exists(expectedBrandingPath),
                $"The new Genesis emblem was not staged beside GenesisEngine.exe: {expectedBrandingPath}");

            string sourceEmblem = Path.Combine(
                RepositoryRoot(),
                "Source",
                "Genesis.Application.Studio",
                "Assets",
                "Genesis_Studio_Emblem.png");
            HeadlessHarness.Assert(
                File.ReadAllBytes(expectedBrandingPath).AsSpan().SequenceEqual(File.ReadAllBytes(sourceEmblem)),
                "The Player has an emblem with the right name but not the current Genesis artwork.");

            string? resolved = Genesis.Runtime.GenesisBranding.ResolveSplashPath(runtimeDir, null);
            HeadlessHarness.Assert(
                string.Equals(
                    Path.GetFullPath(resolved ?? string.Empty),
                    Path.GetFullPath(expectedBrandingPath),
                    StringComparison.OrdinalIgnoreCase),
                $"The Player splash resolved '{resolved ?? "(nothing)"}' instead of the staged "
                + "Genesis emblem; it would still show the legacy runtime logo.");

            string explicitOverride = Path.Combine(RepositoryRoot(), "Source", "Runtime", "logo.png");
            HeadlessHarness.Assert(
                string.Equals(
                    Genesis.Runtime.GenesisBranding.ResolveSplashPath(runtimeDir, explicitOverride),
                    Path.GetFullPath(explicitOverride),
                    StringComparison.OrdinalIgnoreCase),
                "An explicit project splash no longer wins over the shipped Genesis default.");
        });

        HeadlessHarness.Step("an exported engine payload keeps the Player branding", () =>
        {
            string exportFixture = Path.Combine(ctx.Workspace, "PlayerBrandingExport");
            string studioFixture = Path.Combine(exportFixture, "Studio");
            string playerFixture = Path.Combine(exportFixture, "Player");
            string bundle = Path.Combine(exportFixture, "Bundle");
            Directory.CreateDirectory(studioFixture);
            Directory.CreateDirectory(Path.Combine(playerFixture, "Assets"));

            File.Copy(
                Path.Combine(runtimeDir, Genesis.Runtime.RuntimePaths.RuntimeExeName),
                Path.Combine(playerFixture, Genesis.Runtime.RuntimePaths.RuntimeExeName));
            foreach (string fileName in new[]
            {
                "Genesis_Studio_Emblem.png",
                "Genesis_Studio_Logo.png",
                "Genesis.ico",
            })
            {
                File.Copy(
                    Path.Combine(runtimeDir, "Assets", fileName),
                    Path.Combine(playerFixture, "Assets", fileName));
            }

            // A stale editor-side asset makes ordering observable: Player Assets must overlay it,
            // rather than export succeeding only because Studio currently ships matching artwork.
            string staleAssets = Path.Combine(studioFixture, "Assets");
            Directory.CreateDirectory(staleAssets);
            File.WriteAllText(Path.Combine(staleAssets, "Genesis_Studio_Emblem.png"), "stale editor mark");
            StudioExport.CopyEnginePayload(studioFixture, bundle, playerFixture);

            foreach (string fileName in new[]
            {
                "Genesis_Studio_Emblem.png",
                "Genesis_Studio_Logo.png",
                "Genesis.ico",
            })
            {
                string source = Path.Combine(runtimeDir, "Assets", fileName);
                string exported = Path.Combine(bundle, "Assets", fileName);
                HeadlessHarness.Assert(
                    File.Exists(exported)
                    && File.ReadAllBytes(exported).AsSpan().SequenceEqual(File.ReadAllBytes(source)),
                    $"Export did not preserve the Player-owned branding asset '{fileName}'.");
            }

            HeadlessHarness.Assert(
                File.Exists(Path.Combine(bundle, Genesis.Runtime.RuntimePaths.RuntimeExeName)),
                "Export omitted GenesisEngine.exe from the Player payload.");
            HeadlessHarness.Assert(
                !File.Exists(Path.Combine(bundle, "logo.png")),
                "Export resurrected the legacy root logo.png beside the staged Genesis artwork.");
        });

        HeadlessHarness.Step("the template is authored in PGSL only", () =>
            GateSuite.AssertGameplayIsPgslOnly(project, runtimeDir));

        HeadlessHarness.Step("one editable 2D resource graph carries frames, masks, effects, events and a live view", () =>
        {
            string idleFile = Path.Combine(project.AssetsPath, "Sprites", "Player Idle.image.json");
            string runFile = Path.Combine(project.AssetsPath, "Sprites", "Player Run.image.json");
            ImageDocument idle = ImageDocumentSerializer.LoadAtomic(idleFile).Document;
            ImageDocument run = ImageDocumentSerializer.LoadAtomic(runFile).Document;
            HeadlessHarness.Assert(
                idle.Frames.Count == 2 && run.Frames.Count == 4,
                $"The stock player does not expose its complete idle/run frames (idle={idle.Frames.Count}, run={run.Frames.Count}).");
            HeadlessHarness.Assert(
                idle.Tags.Count == 1 && idle.Tags[0].Name == "Idle"
                && run.Tags.Count == 1 && run.Tags[0].Name == "Run",
                "The stock player animation tags are missing or misnamed.");
            HeadlessHarness.Assert(
                idle.Origin.Space == ImageCoordinateSpace.Pixels && idle.Origin.X == 16 && idle.Origin.Y == 32
                && run.Origin.X == 16 && run.Origin.Y == 32,
                "The Image Editor origin is not the feet pivot used by gameplay.");
            HeadlessHarness.Assert(
                idle.CollisionShapes.Count == 1 && run.CollisionShapes.Count == 1
                && idle.CollisionShapes[0].Kind == ImageCollisionShapeKind.Rectangle,
                "The stock player images have no editable collision mask.");
            HeadlessHarness.Assert(
                SpriteAssetLoader.Load(project.RootPath, "Assets/Sprites/Player Run.image.json").Frames.Count == 4,
                "The runtime sprite loader cannot consume the Image Editor's run animation.");

            JObject player = JObject.Parse(File.ReadAllText(Path.Combine(project.AssetsPath, "Objects", "Player.object.json")));
            string[] componentTypes = (player["components"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(component => (string?)component["type"] ?? string.Empty).ToArray();
            string[] events = (player["events"] as JArray ?? new JArray()).Values<string?>()
                .Where(value => value is not null).Select(value => value!).ToArray();
            HeadlessHarness.Assert(
                componentTypes.Contains("SpriteComponent")
                && componentTypes.Contains("ParticleComponent")
                && componentTypes.Contains("ScriptComponent"),
                "The Player Object is not a real sprite + particle + script composition.");
            HeadlessHarness.Assert(
                events.Contains("Create") && events.Contains("Step") && events.Contains("KeyPressed")
                && events.Contains("Alarm0") && events.Contains("Alarm7") && events.Contains("DrawGui"),
                "The Player Object omits one of its authored event families.");

            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom)!;
            RoomAsset room = RoomAssetLoader.Parse(roomFile);
            RoomViewport view = room.Viewports[0];
            HeadlessHarness.Assert(
                view.Enabled && view.PortWidth == 1280 && view.PortHeight == 720
                && Math.Abs(view.SourceWidth - 640f) < 0.01f && Math.Abs(view.SourceHeight - 360f) < 0.01f
                && view.FollowTarget == "Player" && view.FollowSpeedX == 8f && view.FollowSpeedY == 6f,
                "The stock room does not carry its authored port, bounds, follow target and speed.");
            HeadlessHarness.Assert(
                File.Exists(Path.Combine(project.AssetsPath, "Particles", "Player Dust.particle.json"))
                && File.Exists(Path.Combine(project.AssetsPath, "Audio", "Pickup.audio.json")),
                "The Player's linked particle/audio resources are absent from the resource tree.");
        });

        HeadlessHarness.Step("the room builds into a live ECS world and its scripts move things", () =>
        {
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom)
                ?? throw new InvalidOperationException(
                    $"The start room '{project.Manifest.StartRoom}' did not resolve to a file.");

            IGameContext previousContext = PgslCommands.ActiveGameContext;
            string? previousProject = PgslCommands.ProjectPath;
            PgslCommands.ProjectPath = project.RootPath;

            try
            {
                Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);

                ScriptHostSystem scriptHost = new();
                EcsWorld world = new();
                RoomAsset asset = RoomAssetLoader.Parse(roomFile);
                InputState input = new();
                GateAudioRecorder audio = new();
                NullGameContext game = new()
                {
                    World = world,
                    Input = input,
                    Room = asset,
                    Audio = audio,
                    ProjectPath = project.RootPath,
                };
                scriptHost.SetContext(game);
                PgslCommands.ActiveGameContext = game;
                RoomBuildResult built = new RoomSceneBuilder(project.RootPath, scriptHost).Build(world, asset);

                HeadlessHarness.Assert(
                    built.SpawnedEntities.Count > 0,
                    "Building the start room spawned nothing at all.");
                HeadlessHarness.Assert(
                    built.EntitiesByNodeId.Count >= 2,
                    $"Expected the player and at least one coin, got {built.EntitiesByNodeId.Count} instances.");

                RoomNode playerNode = asset.Nodes.First(node => node.Kind == RoomNodeKind.GameObject && node.Name == "Player 1");
                Entity playerEntity = built.EntitiesByNodeId[playerNode.Id];
                PgslBehavior playerBehavior = scriptHost.Instances.OfType<PgslBehavior>()
                    .First(behavior => behavior.Entity == playerEntity);
                HeadlessHarness.Assert(playerBehavior.Context.Alarm0 == 240 && playerBehavior.Context.Alarm7 == 60,
                    "Create did not arm two independent alarm slots.");
                HeadlessHarness.Assert(
                    ObjectDrawAssetRegistry.TryGet(playerEntity, out ObjectDrawAssetEntry startingAssets)
                    && startingAssets.Image.EndsWith("Player Idle.image.json", StringComparison.OrdinalIgnoreCase),
                    "The placed Player did not begin on its authored idle Image asset.");

                float playerStartX = world.GetRef<TransformComponent>(playerEntity).X;
                input.OnKeyDown(Key.Right);
                scriptHost.Update(1f / 60f);
                input.NextFrame();
                ref SpriteComponent runningSprite = ref world.GetRef<SpriteComponent>(playerEntity);
                ref ParticleComponent runningDust = ref world.GetRef<ParticleComponent>(playerEntity);
                bool hasRunningAssets = ObjectDrawAssetRegistry.TryGet(playerEntity, out ObjectDrawAssetEntry runningAssets);
                HeadlessHarness.Assert(
                    world.GetRef<TransformComponent>(playerEntity).X > playerStartX
                    && hasRunningAssets
                    && runningAssets.Image.EndsWith("Player Run.image.json", StringComparison.OrdinalIgnoreCase)
                    && runningSprite.AnimationTagIndex == 0 && runningSprite.ImageSpeed > 0.6f
                    && runningDust.Emitting && runningDust.EmitRate > 28f,
                    "Held input did not complete the Player integration: "
                    + $"x={world.GetRef<TransformComponent>(playerEntity).X:0.##} (start {playerStartX:0.##}), "
                    + $"image='{runningAssets?.Image ?? "(none)"}', tag={runningSprite.AnimationTagIndex}, "
                    + $"speed={runningSprite.ImageSpeed:0.##}, dust={runningDust.Emitting}, rate={runningDust.EmitRate:0.##}.");
                HeadlessHarness.Assert(audio.Plays.Count > 0,
                    "The Player's KeyPressed event did not reach the audio system.");

                input.OnKeyUp(Key.Right);
                scriptHost.Update(1f / 60f);
                input.NextFrame();
                HeadlessHarness.Assert(
                    ObjectDrawAssetRegistry.TryGet(playerEntity, out ObjectDrawAssetEntry idleAssets)
                    && idleAssets.Image.EndsWith("Player Idle.image.json", StringComparison.OrdinalIgnoreCase)
                    && !world.GetRef<ParticleComponent>(playerEntity).Emitting,
                    "Releasing input did not restore the idle Image and stop the attached effect.");

                // Something authored in PGSL must actually move. Not the player specifically: with no
                // keyboard attached it holds its ground correctly, and asserting on it would be
                // asserting that a headless run presses keys. The coin's idle bob is scripted motion
                // that needs nothing but the VM, so it is the honest witness that the room is alive.
                Entity[] instances = [.. built.EntitiesByNodeId.Values];
                float[] startY = [.. instances.Select(entity => world.GetRef<TransformComponent>(entity).Y)];

                for (int frame = 0; frame < 30; frame++)
                {
                    scriptHost.Update(1f / 60f);
                }

                bool moved = instances
                    .Select((entity, index) =>
                        Math.Abs(world.GetRef<TransformComponent>(entity).Y - startY[index]))
                    .Any(delta => delta > 0.001f);
                HeadlessHarness.Assert(
                    moved,
                    "Thirty frames of PGSL moved nothing in the room — no Step event is running.");

                for (int frame = 0; frame < 30; frame++) scriptHost.Update(1f / 60f);
                IReadOnlyDictionary<string, object> variables = playerBehavior.GetVariablesSnapshot();
                HeadlessHarness.Assert(
                    variables.TryGetValue("alarmPulse", out object? pulse)
                    && Convert.ToDouble(pulse) >= 1
                    && playerBehavior.Context.Alarm7 > 0,
                    "Alarm7 did not dispatch and re-arm while Alarm0 continued independently.");

                // Alarm7 is only the stock project's visible example. Prove the complete Alarm0..11
                // storage/countdown family rather than blessing one hard-coded slot.
                Genesis.Shared.Scripting.PgslContext alarmContext = new();
                Genesis.Shared.Scripting.PgslContext previousAlarmContext = PgslCommands.BindContext(alarmContext);
                try
                {
                    for (int slot = 0; slot < PgslCommands.AlarmSlotCount; slot++) PgslCommands.SetAlarm(slot, slot + 1);
                    HeadlessHarness.Assert(PgslCommands.AlarmCountActive() == PgslCommands.AlarmSlotCount,
                        "Not all Alarm0..Alarm11 slots can be armed independently.");
                    double fired = PgslCommands.TickAlarms(PgslCommands.AlarmSlotCount);
                    for (int slot = 0; slot < PgslCommands.AlarmSlotCount; slot++)
                        HeadlessHarness.Assert(PgslCommands.AlarmFired(fired, slot) && PgslCommands.GetAlarm(slot) == -1,
                            $"Alarm{slot} did not fire and disarm through the common alarm system.");
                }
                finally { PgslCommands.BindContext(previousAlarmContext); }

                RoomViewportTracker tracker = new();
                tracker.Reset(asset);
                tracker.StartShake(asset, 0, 8f, 0.32f, 10f);
                HeadlessHarness.Assert(tracker.ShakeOffset(asset, 0, 10.05f).LengthSquared() > 0.01f
                    && tracker.ShakeOffset(asset, 0, 10.5f) == Vector2.Zero,
                    "Camera shake did not produce a decaying live offset and expire cleanly.");

                // The HUD the Player draws in DrawGui must reach a canvas (NEXT-033).
                GateHudCanvas hud = new();
                scriptHost.DispatchPgslDraw(renderer: null, hud);
                HeadlessHarness.Assert(
                    hud.Texts.Count > 0,
                    "The object's DrawGui never reached a canvas — the game would have no HUD.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousContext;
                PgslCommands.ProjectPath = previousProject;
            }
        });

        GateSuite.PlayerRun run = null!;
        HeadlessHarness.Step("F5 launches the real player and it plays", () =>
        {
            string liveAsset = Path.Combine(project.AssetsPath, "Objects", "Coin.object.json");
            string validLiveAsset = File.ReadAllText(liveAsset);
            string playerLog = Path.Combine(ProjectPaths.LogsDir(project.RootPath), "project_player.log");
            run = GateSuite.RunPlayer(
                project,
                runtimeDir,
                Math.Max(seconds,8f),
                timeoutMilliseconds,
                process =>
                {
                    // Prove this is a safe reload transaction, not just a watcher: a partial save
                    // must leave the running room intact, and the following valid save must apply
                    // without restarting the process.
                    bool rejected = false;
                    string lastReadError = string.Empty;
                    try
                    {
                        File.WriteAllText(liveAsset, "{");
                        DateTime rejectionDeadline = DateTime.UtcNow.AddSeconds(4);
                        while (!process.HasExited && DateTime.UtcNow < rejectionDeadline)
                        {
                            try
                            {
                                if (File.Exists(playerLog)
                                    && File.ReadAllText(playerLog).Contains("AssetLiveReload rejected", StringComparison.Ordinal))
                                {
                                    rejected = true;
                                    break;
                                }
                            }
                            catch (IOException error) { lastReadError = error.Message; }
                            Thread.Sleep(25);
                        }
                    }
                    finally { File.WriteAllText(liveAsset, validLiveAsset + Environment.NewLine + " "); }
                    HeadlessHarness.Assert(rejected,
                        "The current player did not reject the incomplete save before replacement. " + lastReadError);
                });
            HeadlessHarness.Assert(
                run.ExitCode == 0,
                $"GenesisEngine.exe exited with {run.ExitCode} after playing '{run.RoomName}'.");
            HeadlessHarness.Assert(
                run.FoundWindow,
                "GenesisEngine.exe ran without exposing its game window to the desktop.");
            HeadlessHarness.Assert(
                run.HasSmallWindowIcon && run.HasLargeWindowIcon,
                $"The Player window is missing its taskbar icon "
                + $"(small={run.HasSmallWindowIcon}, large={run.HasLargeWindowIcon}).");
            HeadlessHarness.Assert(
                run.SmallWindowIconWidth > 0
                && run.LargeWindowIconWidth > run.SmallWindowIconWidth,
                $"The Player reused an oversized image for both native icon roles "
                + $"(small={run.SmallWindowIconWidth}px, large={run.LargeWindowIconWidth}px). "
                + "Windows needs independently sized caption and taskbar icons.");
            HeadlessHarness.Assert(
                run.Log.Contains("room loaded entities=", StringComparison.Ordinal),
                "The player never reported loading the room. Log:\r\n" + Tail(run.Log));
            HeadlessHarness.Assert(
                run.Log.Contains("AssetLiveReload rejected", StringComparison.Ordinal)
                && run.Log.Contains("AssetLiveReload detected", StringComparison.Ordinal)
                && run.Log.Contains("AssetLiveReload applied", StringComparison.Ordinal),
                "The running player did not safely reject a partial save and then apply its valid "
                + "replacement without restarting. Log:\r\n"
                + Tail(run.Log));
            HeadlessHarness.Assert(
                run.Log.Contains(
                    "Boot splash logo: " + Path.GetFullPath(expectedBrandingPath),
                    StringComparison.OrdinalIgnoreCase),
                "The live Player did not load the staged Genesis emblem into its boot splash. "
                + "Resolver-only checks cannot catch a splash that bypasses GenesisBranding. Log:\r\n"
                + Tail(run.Log));
            HeadlessHarness.Assert(
                !run.Log.Contains("[SCRIPT ERROR]", StringComparison.Ordinal),
                "The game logged a script error while playing:\r\n" + Tail(run.Log));
        });

        HeadlessHarness.Step("the frame it drew is a real picture", () =>
        {
            HeadlessHarness.Assert(
                run.Screenshots.Length > 0,
                "The run produced no screenshot, so nothing proves it rendered.");

            string shot = run.Screenshots[0];
            using Bitmap bitmap = new(shot);
            ImageMetrics metrics = Measure(bitmap);
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 6,
                $"The played frame has only {metrics.UniqueSampledColors} distinct colours — "
                + "the game ran but drew essentially nothing.");

            string copied = Path.Combine(ctx.Captures, "gate-02-template-2d-played.png");
            File.Copy(shot, copied, overwrite: true);
            ctx.Report.Images.Add(
                ImageResult.From("Gate — 2D template played", "gate-02-template-2d-played.png", metrics));
        });
    }

    /// <summary>
    /// Canonical Studio-to-F5 3D game: one saved project owns the terrain, regional foliage, water,
    /// composed campfire, first-person PGSL player and the windowed frame used for acceptance.
    /// </summary>
    public static void ThreeDTemplate(
        HeadlessContext ctx,
        GateSuite.GateFixture fixture,
        float seconds,
        int timeoutMilliseconds)
    {
        ProjectSession project = fixture.ThreeD;
        string runtimeDir = Genesis.Runtime.RuntimePaths.ResolveRuntimeDir()
            ?? throw new InvalidOperationException("GenesisEngine.exe was not found.");

        HeadlessHarness.Step("the template is authored in PGSL only", () =>
            GateSuite.AssertGameplayIsPgslOnly(project, runtimeDir));

        RoomAsset room = null!;
        string terrainResource = Path.Combine(project.AssetsPath, "Terrain", SandboxTemplate.TerrainName + ".terrain.json");
        string campfirePrefab = Path.Combine(project.AssetsPath, "Objects", SandboxTemplate.CampfireName + ".object.json");
        string playerPrefab = Path.Combine(project.AssetsPath, "Objects", SandboxTemplate.PlayerName + ".object.json");

        HeadlessHarness.Step("one authored 3D resource graph resolves through its editors and runtime formats", () =>
        {
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom)
                ?? throw new InvalidOperationException(
                    $"The start room '{project.Manifest.StartRoom}' did not resolve.");
            room = RoomAssetLoader.Parse(roomFile);
            HeadlessHarness.Assert(
                room.Dimension == RoomDimension.ThreeD,
                $"The 3D template's start room reports {room.Dimension}.");
            RoomNode terrainNode = room.Nodes.Single(node => node.Kind == RoomNodeKind.Terrain);
            RoomNode playerNode = room.Nodes.Single(node => node.GameObject?.Prefab.EndsWith(
                "/" + SandboxTemplate.PlayerName + ".object.json", StringComparison.OrdinalIgnoreCase) == true);
            RoomNode campfireNode = room.Nodes.Single(node => node.GameObject?.Prefab.EndsWith(
                "/" + SandboxTemplate.CampfireName + ".object.json", StringComparison.OrdinalIgnoreCase) == true);
            HeadlessHarness.Assert(
                terrainNode.Terrain?.Asset.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase) == true
                && playerNode.Enabled && campfireNode.Enabled,
                "The canonical room did not contain its terrain, player and composed campfire nodes.");

            TerrainNatureDocument nature = TerrainNatureSerializer.LoadOrDefault(terrainResource);
            string foliageCache = TerrainNatureSerializer.ResolveFoliageCache(terrainResource, nature);
            FoliageField foliage = FoliageFieldCache.Load(foliageCache, nature.FoliageCacheSha256);
            HeadlessHarness.Assert(
                nature.Paths.Count == 1
                && nature.WaterBodies.Count == 1
                && nature.WaterBodies[0].Swimmable
                && nature.FoliageInstanceCount == foliage.Instances.Count
                && foliage.Instances.Count >= 2500,
                $"The terrain's natural-world graph is incomplete (paths={nature.Paths.Count}, "
                + $"water={nature.WaterBodies.Count}, foliage={foliage.Instances.Count:N0}).");
            HeadlessHarness.Assert(
                foliage.Instances.All(instance =>
                    !(MathF.Abs(instance.Position.X) < 4.35f
                      && instance.Position.Z is >= -20f and <= 40f))
                && foliage.Instances.All(instance =>
                    (instance.Position.X * instance.Position.X)
                    + ((instance.Position.Z - 6f) * (instance.Position.Z - 6f)) >= 63.5f),
                "The saved regional grass invaded the authored trail or camp clearing.");

            JObject composed = JObject.Parse(File.ReadAllText(campfirePrefab));
            string[] componentTypes = (composed["components"] as JArray)!
                .OfType<JObject>()
                .Select(component => (string?)component["type"] ?? string.Empty)
                .ToArray();
            string[] requiredComponents =
            [
                "ModelRendererComponent", "ModelAnimatorComponent", "MaterialComponent",
                "ShaderComponent", "ParticleComponent", "AudioComponent", "PointLightComponent",
                "PhysicsComponent", "ScriptComponent",
            ];
            HeadlessHarness.Assert(requiredComponents.All(componentTypes.Contains),
                "The campfire Object lost one or more authored capabilities: "
                + string.Join(", ", requiredComponents.Except(componentTypes)));

            string modelAsset = ResolveProjectPath(project, (string)composed["model"]!);
            GModelAsset model = new RuntimeModelAssetRegistry().Load(project.RootPath, (string)composed["model"]!);
            ShaderAssetDocument shader = ShaderAssetDocument.Load(ResolveProjectPath(project, (string)composed["shader"]!));
            byte[] bytecode = ShaderCompiler.Compile(shader.Source, shader.Entry, shader.Profile);
            ParticleConfig particle = ParticleAssetLoader.Load(
                project.RootPath,
                (string)ObjectProps(composed, "ParticleComponent")["Asset"]!);
            AudioAssetSettings? audio = AudioAssetSettings.Load(ResolveProjectPath(project,
                (string)ObjectProps(composed, "AudioComponent")["Asset"]!));
            HeadlessHarness.Assert(File.Exists(modelAsset) && model.HasRenderableMeshes && !model.ImportRequired,
                "The Model Editor resource used by the campfire did not reach the runtime model loader.");
            HeadlessHarness.Assert(bytecode.Length > 0 && shader.Pipeline == ShaderAssetPipeline.Mesh,
                "The campfire's authored mesh shader did not compile.");
            HeadlessHarness.Assert(particle.EmitRate > 0 && audio?.Source is { Length: > 0 }
                && File.Exists(ResolveProjectPath(project, audio.Source)),
                "The campfire particle or looping audio asset did not resolve.");

            AssertEngineCommandBinding();
        });

        HeadlessHarness.Step("the same room drives PGSL, composition, terrain collision, water and swimming", () =>
        {
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            ScriptAssetRegistry.LoadFromProject(project.RootPath);

            ScriptHostSystem scriptHost = new();
            using RuntimeScene scene = new("Canonical 3D gate") { Input = new Genesis.Runtime.Input.InputState() };
            ProjectGameContext game = new(project.RootPath, scene, null, null, room, null);
            scriptHost.SetContext(game);
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath, scriptHost).Build(scene, room);
            game.SetRoom(room);
            scriptHost.BeginRoom(beginGame: true);
            RoomTerrainSubsystem terrainSubsystem = scene.AddSubsystem(
                new RoomTerrainSubsystem(project.RootPath, room, game));
            ObjectCompositionSubsystem composition = scene.AddSubsystem(
                new ObjectCompositionSubsystem(project.RootPath, NullAudioSystem.Instance));
            scene.AddSubsystem(new ScriptHostSubsystem(scriptHost, () => true));

            PgslProfiler.Reset();
            PgslProfiler.Enabled = true;
            try
            {
                for (int frame = 0; frame < 120; frame++)
                {
                    scene.UpdateFixed(1f / 60f);
                    scene.UpdateVariable(1f / 60f);
                    scene.Input.NextFrame();
                }

                HeadlessHarness.Assert(scene.Physics?.ExternalStaticCount == 1,
                    "The template terrain did not register one runtime collision surface.");
                HeadlessHarness.Assert(scene.WaterVolumes.Count == 1 && scene.WaterVolumes[0].Swimmable,
                    "The template pond did not register one swimmable runtime volume.");
                HeadlessHarness.Assert(terrainSubsystem.AuthoredFoliageInstanceCounts.Single() >= 2500,
                    "F5 did not load the same authored regional foliage cache as Terrain Editor.");

                RoomNode campfireNode = room.Nodes.Single(node => node.GameObject?.Prefab.EndsWith(
                    "/" + SandboxTemplate.CampfireName + ".object.json", StringComparison.OrdinalIgnoreCase) == true);
                Entity campfire = built.EntitiesByNodeId[campfireNode.Id];
                HeadlessHarness.Assert(
                    scene.World.Has<ModelRendererComponent>(campfire)
                    && scene.World.Has<ModelAnimatorComponent>(campfire)
                    && scene.World.Has<ParticleComponent>(campfire)
                    && scene.World.Has<AudioComponent>(campfire)
                    && scene.World.Has<PointLightComponent>(campfire)
                    && scene.World.GetRef<ModelAnimatorComponent>(campfire).TimeSeconds > 1f
                    && composition.ParticleEmitterCount == 1
                    && composition.ActiveParticleCount > 0
                    && composition.ActiveAudioCount == 1
                    && composition.PointLightCount == 1,
                    "The played ECS world did not advance the campfire model/particle/audio/light stack.");

                PgslBehavior campfireScript = scriptHost.Instances.OfType<PgslBehavior>()
                    .Single(behavior => behavior.ScriptName == SandboxTemplate.CampfireName);
                IReadOnlyDictionary<string, object> campfireVariables = campfireScript.GetVariablesSnapshot();
                HeadlessHarness.Assert(
                    Convert.ToDouble(campfireVariables["fireAge"]) > 1.5
                    && Convert.ToDouble(campfireVariables["firePulse"]) is >= 0.69 and <= 1.01,
                    "The campfire's PGSL gameplay state did not advance with its composed effects.");
                IReadOnlyList<PgslEventProfile> profile = PgslProfiler.Snapshot();
                HeadlessHarness.Assert(
                    profile.Any(entry => entry.ObjectName == SandboxTemplate.PlayerName && entry.EventName == "Step")
                    && profile.Any(entry => entry.ObjectName == SandboxTemplate.CampfireName && entry.EventName == "Step")
                    && profile.All(entry => entry.FailedCalls == 0),
                    "The canonical first-person/campfire PGSL profile is missing or contains failures.");

                RoomNode playerNode = room.Nodes.Single(node => node.GameObject?.Prefab.EndsWith(
                    "/" + SandboxTemplate.PlayerName + ".object.json", StringComparison.OrdinalIgnoreCase) == true);
                Entity player = built.EntitiesByNodeId[playerNode.Id];
                HeadlessHarness.Assert(
                    scene.World.Has<SharedRigidBody>(player)
                    && scene.World.Has<SharedCharacterMotor>(player),
                    "The Player Object's authored character preset did not become runtime physics.");
                SharedRigidBody body = scene.World.GetRef<SharedRigidBody>(player);
                TerrainWaterDefinition water = TerrainNatureSerializer.LoadOrDefault(terrainResource).WaterBodies.Single();
                scene.Physics!.SetBodyPose(
                    scene.World,
                    body.RegistrationId,
                    new Vector3(water.Center.X, water.SurfaceHeight - 0.1f, water.Center.Z),
                    Quaternion.Identity);
                scene.UpdateFixed(1f / 60f);
                SharedCharacterMotor motor = scene.World.GetRef<SharedCharacterMotor>(player);
                HeadlessHarness.Assert(motor.State == SharedCharacterState.Swimming && motor.EnteredWater,
                    "The template Player did not enter swimming state inside its own authored pond.");

                // Buoyancy legitimately moves the body during the entry step. Put it back at the
                // shallow edge so Space exercises the motor's explicit water-exit transition.
                scene.Physics.SetBodyPose(
                    scene.World,
                    body.RegistrationId,
                    new Vector3(water.Center.X, water.SurfaceHeight - 0.05f, water.Center.Z),
                    Quaternion.Identity);
                scene.Input.OnKeyDown(Genesis.Runtime.Input.Key.Space);
                scene.UpdateFixed(1f / 60f);
                motor = scene.World.GetRef<SharedCharacterMotor>(player);
                float waterExitVelocity = scene.Physics.GetLinearVelocity(body.RegistrationId).Y;
                HeadlessHarness.Assert(motor.ExitedWater && waterExitVelocity > 1f,
                    "The template Player could not use its authored swimming physics to leave water "
                    + $"(state={motor.State}, entered={motor.EnteredWater}, exited={motor.ExitedWater}, "
                    + $"wasInWater={motor.WasInWater}, submerged={motor.SubmergedFraction:0.###}, "
                    + $"pressed={scene.Input.WasPressed(Genesis.Runtime.Input.Key.Space)}, "
                    + $"velocity={scene.Physics.GetLinearVelocity(body.RegistrationId)})." );
            }
            finally
            {
                PgslProfiler.Enabled = false;
                PgslProfiler.Reset();
            }
        });

        HeadlessHarness.Step("the template's saved regional foliage streams deterministically inside GPU budgets", () =>
        {
            TerrainNatureDocument nature = TerrainNatureSerializer.LoadOrDefault(terrainResource);
            string cachePath = TerrainNatureSerializer.ResolveFoliageCache(terrainResource, nature);
            FoliageField field = FoliageFieldCache.Load(cachePath, nature.FoliageCacheSha256);
            FoliageScatterSettings settings = nature.FoliageSettings;
            Vector3 eye = new(0f, 5f, -10f);
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 1f, 12f), Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 240f);
            FoliageStreamingPlanner planner = new(field, settings.StreamingCellSize);
            FoliageFramePlan first = planner.Build(eye, view * projection, Matrix4x4.Identity, settings, observedGpuMilliseconds: 16d);
            FoliagePerformanceSnapshot snapshot = first.Snapshot;
            string signature = string.Join("|", first.Batches.Select(batch =>
                $"{batch.Key.Species}:{batch.Key.NearLod}:{batch.Instances.Count}:{batch.Instances[0].Position}"));
            FoliageFramePlan second = planner.Build(eye, view * projection, Matrix4x4.Identity, settings, observedGpuMilliseconds: 16d);
            string repeatSignature = string.Join("|", second.Batches.Select(batch =>
                $"{batch.Key.Species}:{batch.Key.NearLod}:{batch.Instances.Count}:{batch.Instances[0].Position}"));

            HeadlessHarness.Assert(signature == repeatSignature && snapshot == second.Snapshot with
            {
                PlanningMilliseconds = snapshot.PlanningMilliseconds,
            }, "Identical camera/budget inputs produced a different foliage streaming plan.");
            HeadlessHarness.Assert(
                snapshot.TotalCells > 1
                && snapshot.SubmittedInstances > 0
                && snapshot.SubmittedInstances <= snapshot.EffectiveInstanceBudget
                && snapshot.EffectiveInstanceBudget <= settings.VisibleInstanceBudget
                && snapshot.SubmittedTriangles <= settings.TriangleBudget
                && snapshot.UploadBytes <= (long)(settings.GpuUploadBudgetMegabytes * 1024f * 1024f)
                && snapshot.Batches is > 0 and <= 14,
                $"Runtime foliage budgets were not enforced: submitted={snapshot.SubmittedInstances}, "
                + $"effective={snapshot.EffectiveInstanceBudget}, triangles={snapshot.SubmittedTriangles}, "
                + $"upload={snapshot.UploadBytes}, batches={snapshot.Batches}, adaptive={snapshot.AdaptiveScale:0.##}.");
        });

        GateSuite.PlayerRun run = null!;
        HeadlessHarness.Step("F5 launches the real 3D player and advances the integrated scene", () =>
        {
            run = GateSuite.RunPlayer(project, runtimeDir, seconds, timeoutMilliseconds);
            HeadlessHarness.Assert(
                run.ExitCode == 0 && run.FoundWindow,
                $"GenesisEngine.exe did not complete a real 3D windowed run (exit={run.ExitCode}, window={run.FoundWindow}).");
            HeadlessHarness.Assert(run.HasSmallWindowIcon && run.HasLargeWindowIcon,
                "The 3D Player window did not expose both native icon roles.");
            HeadlessHarness.Assert(
                run.Log.Contains("room loaded entities=", StringComparison.Ordinal)
                && run.Log.Contains("dimension=ThreeD", StringComparison.Ordinal)
                && !run.Log.Contains("[SCRIPT ERROR]", StringComparison.Ordinal),
                "The 3D player did not load cleanly:\r\n" + Tail(run.Log));
            HeadlessHarness.Assert(
                run.Log.Contains("runtime composition terrainColliders=1 waterVolumes=1", StringComparison.Ordinal)
                && run.Log.Contains("particleEmitters=1", StringComparison.Ordinal)
                && run.Log.Contains("pointLights=1", StringComparison.Ordinal)
                && run.Log.Contains("advancedAnimators=1", StringComparison.Ordinal),
                "The windowed F5 diagnostics did not prove the integrated terrain/water/effect stack:\r\n"
                + Tail(run.Log));
        });

        HeadlessHarness.Step("the played 3D frame is a non-blank game picture", () =>
        {
            HeadlessHarness.Assert(run.Screenshots.Length > 0,
                "The real 3D F5 run produced no screenshot.");
            using Bitmap bitmap = new(run.Screenshots[0]);
            ImageMetrics metrics = Measure(bitmap);
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 8,
                $"The played 3D frame has only {metrics.UniqueSampledColors} sampled colours.");
            string copied = Path.Combine(ctx.Captures, "gate-13-template-3d-played.png");
            bitmap.Save(copied, System.Drawing.Imaging.ImageFormat.Png);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — canonical 3D template played",
                "gate-13-template-3d-played.png",
                metrics));
        });
    }

    /// <summary>
    /// The Nature1-derived production scene shares the existing 3D acceptance workflow: it is a
    /// second, richer room inside that one workflow, not another top-level test.
    /// </summary>
    public static void VerdantHollowTemplate(
        HeadlessContext ctx,
        GateSuite.GateFixture fixture,
        float seconds,
        int timeoutMilliseconds)
    {
        ProjectSession project = fixture.NatureWalk;
        string runtimeDir = Genesis.Runtime.RuntimePaths.ResolveRuntimeDir()
            ?? throw new InvalidOperationException("GenesisEngine.exe was not found.");

        RoomAsset room = null!;
        string terrainResource = Path.Combine(
            project.AssetsPath, "Terrain", NatureWalkTemplate.TerrainName + ".terrain.json");

        HeadlessHarness.Step("Verdant Hollow is one complete, editable Nature1-derived resource graph", () =>
        {
            GateSuite.AssertGameplayIsPgslOnly(project, runtimeDir);
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom)
                ?? throw new InvalidOperationException("Verdant Hollow start room did not resolve.");
            room = RoomAssetLoader.Parse(roomFile);
            TerrainNatureDocument nature = TerrainNatureSerializer.LoadOrDefault(terrainResource);
            string cachePath = TerrainNatureSerializer.ResolveFoliageCache(terrainResource, nature);
            FoliageField foliage = FoliageFieldCache.Load(cachePath, nature.FoliageCacheSha256);

            HeadlessHarness.Assert(
                room.Dimension == RoomDimension.ThreeD
                && room.Nodes.Count(node => node.Kind == RoomNodeKind.Terrain) == 1
                && room.Layers.Count >= 6,
                "Verdant Hollow did not preserve its authored 3D room and edit layers.");
            string[] nodeNames = room.Nodes.Select(node => node.Name).ToArray();
            string[] requiredNodes =
            [
                "Nature Explorer", "South Trail Campfire", "Broken Watchtower", "Weathered Arch",
                "Willowmere Fireflies", "Grove Falling Leaves", "Forest Ambience",
            ];
            HeadlessHarness.Assert(requiredNodes.All(nodeNames.Contains),
                "Verdant Hollow is missing room placements: "
                + string.Join(", ", requiredNodes.Except(nodeNames)));

            HeadlessHarness.Assert(
                nature.Paths.Count == 4
                && nature.WaterBodies.Count == 4
                && nature.WaterBodies.All(water => water.Swimmable && water.PhysicsDepth > 5f)
                && foliage.Instances.Count >= 60000
                && foliage.Instances.Any(instance => instance.Species == FoliageSpecies.Reed)
                && nature.FoliageSettings.NearDistance >= 50f
                && nature.FoliageSettings.FarDistance >= 250f
                && nature.FoliageSettings.VisibleInstanceBudget >= 8000,
                $"Verdant Hollow natural-world data is incomplete (paths={nature.Paths.Count}, "
                + $"water={nature.WaterBodies.Count}, foliage={foliage.Instances.Count:N0}).");

            TerrainAsset authoredTerrain = TerrainAsset.Load(terrainResource + ".gterrain");
            Genesis.World.MeshData openingTrail = TerrainPathGeometry.BuildRibbon(
                nature.Paths[0], authoredTerrain.SampleHeight, authoredTerrain.CellSize * 2f);
            HeadlessHarness.Assert(
                openingTrail.Vertices.Length > nature.Paths[0].Points.Count * 5
                && openingTrail.Vertices.All(vertex =>
                    Math.Abs(vertex.Position.Y - authoredTerrain.SampleHeight(
                        vertex.Position.X, vertex.Position.Z) - 0.04f) < 0.01f),
                "Verdant Hollow's sparse trail controls did not produce a subdivided, terrain-conforming ribbon.");

            string[] requiredResources =
            [
                Path.Combine(project.AssetsPath, "Models", NatureWalkTemplate.WatchtowerName + ".model.json"),
                Path.Combine(project.AssetsPath, "Models", NatureWalkTemplate.StoneArchName + ".model.json"),
                Path.Combine(project.AssetsPath, "Models", NatureWalkTemplate.FallenLogName + ".model.json"),
                Path.Combine(project.AssetsPath, "Shaders", "FoliageWind.shader.json"),
                Path.Combine(project.AssetsPath, "Shaders", "CampfireGlow.shader.json"),
                Path.Combine(project.AssetsPath, "Particles", "TwilightFireflies.particle.json"),
                Path.Combine(project.AssetsPath, "Particles", "FallingCanopyLeaves.particle.json"),
                Path.Combine(project.AssetsPath, "Audio", NatureWalkTemplate.NatureAudioName + ".audio.json"),
                Path.Combine(project.AssetsPath, "Physics", "ForestGround.physics.json"),
                Path.Combine(project.AssetsPath, "Physics", "WeatheredStone.physics.json"),
                Path.Combine(project.AssetsPath, "Physics", "ForestWood.physics.json"),
            ];
            HeadlessHarness.Assert(requiredResources.All(File.Exists),
                "Verdant Hollow is missing authored resources: "
                + string.Join(", ", requiredResources.Where(path => !File.Exists(path)).Select(Path.GetFileName)));

            string campfirePrefab = Path.Combine(
                project.AssetsPath, "Objects", NatureWalkTemplate.CampfireName + ".object.json");
            JObject campfire = JObject.Parse(File.ReadAllText(campfirePrefab));
            string[] componentTypes = (campfire["components"] as JArray)!.OfType<JObject>()
                .Select(component => (string?)component["type"] ?? string.Empty).ToArray();
            string[] requiredComponents =
            [
                "ModelRendererComponent", "ShaderComponent", "ParticleComponent", "AudioComponent",
                "PointLightComponent", "PhysicsComponent",
            ];
            HeadlessHarness.Assert(requiredComponents.All(componentTypes.Contains),
                "The Verdant Hollow campfire is not a complete composed Object.");

            GModelAsset campfireModel = new RuntimeModelAssetRegistry().Load(
                project.RootPath, (string)campfire["model"]!);
            ShaderAssetDocument shader = ShaderAssetDocument.Load(
                ResolveProjectPath(project, (string)campfire["shader"]!));
            HeadlessHarness.Assert(campfireModel.HasRenderableMeshes && !campfireModel.ImportRequired
                && ShaderCompiler.Compile(shader.Source, shader.Entry, shader.Profile).Length > 0,
                "The authored campfire model or previewable shader did not reach the runtime format.");

            IReadOnlyList<ProjectValidationIssue> issues = new ProjectValidator().Validate(project);
            HeadlessHarness.Assert(!issues.Any(issue => issue.Severity == ProjectValidationSeverity.Error),
                "Verdant Hollow project validation failed: "
                + string.Join(" | ", issues.Where(issue => issue.Severity == ProjectValidationSeverity.Error)
                    .Select(issue => issue.Message)));
        });

        HeadlessHarness.Step("Verdant Hollow terrain, water, vegetation and composed Objects enter one live scene", () =>
        {
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            ScriptAssetRegistry.LoadFromProject(project.RootPath);
            ScriptHostSystem scriptHost = new();
            using RuntimeScene scene = new("Verdant Hollow acceptance")
            {
                Input = new Genesis.Runtime.Input.InputState(),
            };
            ProjectGameContext game = new(project.RootPath, scene, null, null, room, null);
            scriptHost.SetContext(game);
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath, scriptHost).Build(scene, room);
            game.SetRoom(room);
            scriptHost.BeginRoom(beginGame: true);
            RoomTerrainSubsystem terrain = scene.AddSubsystem(
                new RoomTerrainSubsystem(project.RootPath, room, game));
            ObjectCompositionSubsystem composition = scene.AddSubsystem(
                new ObjectCompositionSubsystem(project.RootPath, NullAudioSystem.Instance));
            scene.AddSubsystem(new ScriptHostSubsystem(scriptHost, () => true));

            for (int frame = 0; frame < 90; frame++)
            {
                scene.UpdateFixed(1f / 60f);
                scene.UpdateVariable(1f / 60f);
                scene.Input.NextFrame();
            }

            HeadlessHarness.Assert(scene.Physics?.ExternalStaticCount == 1,
                "Verdant Hollow terrain did not become one collidable runtime surface.");
            HeadlessHarness.Assert(scene.WaterVolumes.Count == 4
                && scene.WaterVolumes.All(water => water.Swimmable),
                "All four authored lakes did not become swimmable runtime volumes.");
            HeadlessHarness.Assert(terrain.AuthoredFoliageInstanceCounts.Single() >= 60000,
                "The authored dense foliage cache did not reach the runtime streaming subsystem.");
            HeadlessHarness.Assert(scene.Environment.BackgroundColor.X > 0.2f
                && scene.Environment.BackgroundColor.Y > 0.25f
                && scene.Environment.BackgroundColor.Z > 0.3f,
                $"Verdant Hollow's dynamic sky evaluated too dark: {scene.Environment.BackgroundColor}.");
            HeadlessHarness.Assert(composition.ParticleEmitterCount >= 5
                && composition.PointLightCount >= 7
                && composition.ActiveAudioCount >= 2,
                $"The live scene lost effects (particles={composition.ParticleEmitterCount}, "
                + $"lights={composition.PointLightCount}, audio={composition.ActiveAudioCount}).");

            RoomNode playerNode = room.Nodes.Single(node => node.GameObject?.Prefab.EndsWith(
                "/" + NatureWalkTemplate.PlayerName + ".object.json", StringComparison.OrdinalIgnoreCase) == true);
            Entity player = built.EntitiesByNodeId[playerNode.Id];
            HeadlessHarness.Assert(scene.World.Has<SharedRigidBody>(player)
                && scene.World.Has<SharedCharacterMotor>(player),
                "Verdant Hollow's first-person Object did not become a physics character.");
        });

        GateSuite.PlayerRun run = null!;
        HeadlessHarness.Step("F5 plays Verdant Hollow as the shipped 3D nature-walk template", () =>
        {
            run = GateSuite.RunPlayer(project, runtimeDir, seconds, timeoutMilliseconds);
            HeadlessHarness.Assert(run.ExitCode == 0 && run.FoundWindow,
                $"Verdant Hollow F5 failed (exit={run.ExitCode}, window={run.FoundWindow}).");
            HeadlessHarness.Assert(!run.Log.Contains("[SCRIPT ERROR]", StringComparison.Ordinal)
                && run.Log.Contains("terrainColliders=1 waterVolumes=4", StringComparison.Ordinal)
                && run.Log.Contains("particleEmitters=5", StringComparison.Ordinal),
                "Verdant Hollow's windowed runtime graph was incomplete:\r\n" + Tail(run.Log));
        });

        HeadlessHarness.Step("the played Verdant Hollow frame is visually non-trivial", () =>
        {
            HeadlessHarness.Assert(run.Screenshots.Length > 0,
                "The Verdant Hollow F5 run produced no visual proof.");
            using Bitmap bitmap = new(run.Screenshots[0]);
            ImageMetrics metrics = Measure(bitmap);
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 24,
                $"Verdant Hollow produced only {metrics.UniqueSampledColors} sampled colours.");

            // Regression for sparse path control points bridging rolling terrain at eye height.
            // Ignore the HUD corner and sample the unobstructed upper-middle view: a trail mesh
            // crossing the camera used to turn almost this entire region near-black.
            int darkSamples = 0;
            int upperSamples = 0;
            for (int y = bitmap.Height / 8; y < bitmap.Height / 2; y += 4)
            for (int x = bitmap.Width / 4; x < bitmap.Width * 3 / 4; x += 4)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R < 24 && pixel.G < 24 && pixel.B < 24) darkSamples++;
                upperSamples++;
            }
            float darkRatio = darkSamples / (float)Math.Max(1, upperSamples);
            HeadlessHarness.Assert(darkRatio < 0.20f,
                $"Verdant Hollow's upper view is {darkRatio:P0} near-black; a world mesh may be crossing the camera.");
            string copied = Path.Combine(ctx.Captures, "gate-13b-verdant-hollow-played.png");
            bitmap.Save(copied, System.Drawing.Imaging.ImageFormat.Png);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — Verdant Hollow 3D Nature Walk played",
                "gate-13b-verdant-hollow-played.png",
                metrics));
        });
    }

    private static JObject ObjectProps(JObject prefab, string type) =>
        ((prefab["components"] as JArray)?.OfType<JObject>()
            .First(component => string.Equals((string?)component["type"], type, StringComparison.OrdinalIgnoreCase))["props"]
            as JObject) ?? new JObject();

    /// <summary>
    /// Proves the Engine command catalogue is honest and that state-changing PGSL commands reach
    /// the exact live objects consumed on the next render frame. This deliberately shares the
    /// canonical 3D runtime workflow instead of creating one acceptance test per command.
    /// </summary>
    private static void AssertEngineCommandBinding()
    {
        EngineCommandRegistry.Build();
        IReadOnlyList<EngineCommandInfo> catalog = EngineCommandRegistry.GetCatalog();
        HeadlessHarness.Assert(
            catalog.Single(command => command.Name == "SetCameraFarPlane").IsImplemented
            && catalog.Single(command => command.Name == "Camera3DCreate").IsImplemented
            && catalog.Single(command => command.Name == "Camera2DCreate").IsImplemented
            && !catalog.Single(command => command.Name == "PhysicsUse").IsImplemented,
            "The Engine command catalogue still labels explicit NotImplementedException placeholders as live, "
            + "or still marks the CAM-1 camera registry as roadmap.");
        PgslValidationReport liveCommand = PgslScriptValidator.ValidateSource(
            "Engine.SetCameraFarPlane(1500);", "live-engine-command", strict: true);
        PgslValidationReport liveCameraRegistry = PgslScriptValidator.ValidateSource(
            "Engine.Camera3DCreate(1);", "live-camera-registry", strict: true);
        PgslValidationReport roadmapCommand = PgslScriptValidator.ValidateSource(
            "Engine.PhysicsUse(\"Wood\");", "roadmap-engine-command", strict: true);
        HeadlessHarness.Assert(
            liveCommand.Success && liveCameraRegistry.Success && !roadmapCommand.Success,
            "Strict F5 validation did not distinguish live Engine commands from catalogued roadmap entries.");

        using RuntimeScene scene = new("Engine command binding gate");
        FakeGameWindow window = new();
        RendererOptions options = new();
        IRenderController renderer = DispatchProxy.Create<IRenderController, EngineRenderProbe>();
        EngineRenderProbe renderProbe = (EngineRenderProbe)(object)renderer;
        using (EngineRuntimeCommandBinding binding = new(scene, window, options, renderer))
        {
            Engine.FpsVsync(false);
            Engine.FpsTarget(144);
            Engine.WindowMode("Borderless");
            Engine.WindowTitle("PGSL live binding");
            Engine.WindowSize(1024, 576);

            Engine.SetCameraPosition(3f, 4f, 5f);
            Engine.SetCameraTarget(3f, 4f, -5f);
            Engine.SetCameraFov(72f);
            Engine.SetCameraNearPlane(0.25f);
            Engine.SetCameraFarPlane(1800f);
            HeadlessHarness.Assert(
                scene.Camera3D.Position == new Vector3(3f, 4f, 5f)
                && Vector3.Distance(scene.Camera3D.Forward, -Vector3.UnitZ) < 0.001f
                && Math.Abs(scene.Camera3D.FieldOfView - (72f * MathF.PI / 180f)) < 0.0001f
                && Math.Abs(scene.Camera3D.NearPlane - 0.25f) < 0.0001f
                && Math.Abs(scene.Camera3D.FarPlane - 1800f) < 0.0001f,
                "Camera position/target/FOV/clip commands did not alter the live play camera.");

            // CAM-1: indexed registry drives the same play camera (id = Room viewport slot).
            Engine.Camera3DCreate(2);
            Engine.Camera3DSetPos(2, 9f, 8f, 7f);
            Engine.Camera3DSetYaw(2, 45f);
            Engine.Camera3DSetPitch(2, -12f);
            Engine.Camera3DSetFrustum(2, 70f, 1.777f, 0.2f, 900f);
            float[] frustum = Engine.Camera3DGetFrustum(2);
            HeadlessHarness.Assert(
                Math.Abs(Engine.Camera3DGetPosX(2) - 9f) < 0.0001f
                && Math.Abs(Engine.Camera3DGetYaw(2) - 45f) < 0.0001f
                && Math.Abs(Engine.Camera3DGetPitch(2) - (-12f)) < 0.0001f
                && frustum.Length == 4
                && Math.Abs(frustum[0] - 70f) < 0.0001f
                && Math.Abs(frustum[2] - 0.2f) < 0.0001f
                && Math.Abs(frustum[3] - 900f) < 0.0001f,
                "Indexed Camera3D getters did not round-trip Create/Set values.");
            HeadlessHarness.Assert(
                scene.Camera3D.Position == new Vector3(9f, 8f, 7f)
                && Math.Abs(scene.Camera3D.Yaw - (45f * MathF.PI / 180f)) < 0.0001f
                && Math.Abs(scene.Camera3D.Pitch - (-12f * MathF.PI / 180f)) < 0.0001f
                && Math.Abs(scene.Camera3D.FieldOfView - (70f * MathF.PI / 180f)) < 0.0001f
                && Math.Abs(scene.Camera3D.NearPlane - 0.2f) < 0.0001f
                && Math.Abs(scene.Camera3D.FarPlane - 900f) < 0.0001f
                && Math.Abs(scene.Camera3D.AspectRatio - 1.777f) < 0.0001f,
                "Engine.Camera3D* registry did not drive the live play camera for the active slot.");

            Engine.Camera2DCreate(1);
            Engine.Camera2DSetPos(1, 100f, 200f);
            Engine.Camera2DSetFrustum(1, -320f, 320f, -180f, 180f);
            HeadlessHarness.Assert(
                Math.Abs(Engine.Camera2DGetPosX(1) - 100f) < 0.0001f
                && Math.Abs(Engine.Camera2DGetPosY(1) - 200f) < 0.0001f
                && Engine.Camera2DGetFrustum(1) is { Length: 4 } f2
                && Math.Abs(f2[0] - (-320f)) < 0.0001f
                && Math.Abs(f2[1] - 320f) < 0.0001f,
                "Indexed Camera2D getters did not round-trip Create/Set values.");

            Engine.Lighting(false);
            Engine.SetAmbientLight(0.12f, 0.24f, 0.36f);
            Engine.SetGlobalLightColor(0.9f, 0.7f, 0.5f);
            Engine.SetGlobalLightDirection(1f, -2f, 0.5f);
            Engine.Culling("CCW", "Front");
            Engine.SetFrustumCulling(false);
            Engine.DebugView("Normals");
            Engine.DisableGPUFunction("Shadows");
            Engine.SetShadowStrength(0.38f);
            Engine.SetFog(true, 0.2f, 0.3f, 0.4f, 12f, 90f, 0.75f);

            Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = true;
            Engine.DrawRect(4f, 5f, 16f, 12f, 1f, 0f, 0f, 1f, true);
            Engine.DrawLine(0f, 1f, 8f, 9f, 0f, 1f, 0f, 1f);
            Engine.DrawText("live", 10f, 20f, 18f, 1f, 1f, 1f, 1f);
            Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = false;
            int fogVolume = Engine.FogVolumeCreate("Sphere");
            Engine.FogVolumeSetBounds(fogVolume, 3f, 2f, 1f, 6f, 5f, 4f);
            Engine.FogVolumeSetDensity(fogVolume, 0.42f);
            binding.ApplyFrameRenderState();

            HeadlessHarness.Assert(
                !window.VSync && window.TargetFps == 144 && window.Mode == WindowMode.Borderless
                && window.Title == "PGSL live binding" && window.Width == 1024 && window.Height == 576,
                "Window PGSL commands changed backing fields but did not reach the live game window.");

            Mesh3DState state = EnvironmentMapper.ToMesh3DState(
                scene.Environment,
                scene.Camera3D,
                options,
                options.Wireframe,
                binding.DebugView);
            HeadlessHarness.Assert(
                !state.LightingEnabled
                && Vector3.Distance(state.AmbientColor, new Vector3(0.12f, 0.24f, 0.36f)) < 0.001f
                && Vector3.Distance(state.SunColor, new Vector3(0.9f, 0.7f, 0.5f)) < 0.001f
                && Vector3.Distance(state.LightDirection, Vector3.Normalize(new Vector3(1f, -2f, 0.5f))) < 0.001f
                && state.CullBackFaces && state.CullFrontFaces && state.FrontCounterClockwise
                && !state.FrustumCullingEnabled && state.DebugView == RenderDebugView.Normals
                && !state.ShadowsEnabled && !Engine.GetGlobalShadows()
                && Math.Abs(state.ShadowStrength - 0.38f) < 0.001f
                && Math.Abs(Engine.GetShadowStrength() - 0.38f) < 0.001f && state.FogEnabled
                && Math.Abs(state.FogStart - 12f) < 0.001f && Math.Abs(state.FogEnd - 90f) < 0.001f,
                "Lighting/debug/culling/fog PGSL commands did not reach the next renderer state.");
            HeadlessHarness.Assert(
                renderProbe.Rectangles == 1 && renderProbe.Lines == 1 && renderProbe.Texts == 1
                && renderProbe.FogVolumes >= 1,
                "Engine draw/fog-volume commands raised state changes but did not reach the live renderer.");
            Engine.FogVolumeDestroy(fogVolume);
        }

        // Restore process-wide defaults for later gate steps. The disposed binding must not retain
        // a host through Engine's static events.
        Engine.FpsVsync(true);
        Engine.FpsTarget(-1);
        Engine.WindowMode("Windowed");
        Engine.Lighting(true);
        Engine.EnableGPUFunction("Shadows");
        Engine.SetShadowStrength(1f);
        Engine.Culling("CCW", "Back");
        Engine.SetFrustumCulling(true);
        Engine.DebugView("Shaded");
        Engine.Fog(false);
        Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = false;
    }

    private static string ResolveProjectPath(ProjectSession project, string relativePath) =>
        Path.GetFullPath(Path.Combine(
            project.RootPath,
            (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Records the HUD a game draws, so "it drew something" is an assertion not a hope.</summary>
    private sealed class GateHudCanvas : IHudCanvas
    {
        public List<(string Text, float X, float Y)> Texts { get; } = [];

        public int Width => 1280;

        public int Height => 720;

        public void Text(string text, float x, float y, float size, System.Numerics.Vector4 color) =>
            Texts.Add((text, x, y));

        public void TextCentered(
            string text, float cx, float y, float w, float size, System.Numerics.Vector4 color) =>
            Texts.Add((text, cx, y));

        public void Rect(float x, float y, float w, float h, System.Numerics.Vector4 color, bool filled = true) { }

        public void Line(
            float x1, float y1, float x2, float y2, System.Numerics.Vector4 color, float thickness = 1.5f) { }
    }

    /// <summary>Records edge-triggered PGSL audio calls without requiring a hardware mixer.</summary>
    private sealed class GateAudioRecorder : IAudioSystem
    {
        private int _nextChannel = 1;
        public List<(int Sound, float Volume, float Pitch, bool Loop)> Plays { get; } = [];
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string projectRelativePath) => 1;
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Plays.Add((soundId, volume, pitch, loop));
            return new AudioChannel(_nextChannel++);
        }
        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => channel.IsValid;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }

    private sealed class FakeGameWindow : IGameWindow
    {
        public string Title { get; set; } = string.Empty;
        public int Width { get; private set; } = 1280;
        public int Height { get; private set; } = 720;
        public int TargetFps { get; set; }
        public double CurrentFps => 0;
        public bool VSync { get; set; } = true;
        public WindowMode Mode { get; set; } = WindowMode.Windowed;
        public bool IsRunning => true;
        public IntPtr NativeHandle => IntPtr.Zero;
        public bool MouseCaptured { get; private set; }
        public event Action? Load;
        public event Action<double>? Update { add { } remove { } }
        public event Action<double>? Render { add { } remove { } }
        public event Action<int, int>? Resize;
        public event Action? Closing;
        public void Run() => Load?.Invoke();
        public void Close() => Closing?.Invoke();
        public void SetSize(int width, int height)
        {
            Width = width;
            Height = height;
            Resize?.Invoke(width, height);
        }
        public void SetMouseCaptured(bool captured) => MouseCaptured = captured;
        public void SetCursorMode(CursorMode mode) => MouseCaptured = mode == CursorMode.Locked;
        public void Dispose() { }
    }

    private class EngineRenderProbe : DispatchProxy
    {
        public int Rectangles { get; private set; }
        public int Lines { get; private set; }
        public int Texts { get; private set; }
        public int FogVolumes { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IRenderController.DrawRect): Rectangles++; break;
                case nameof(IRenderController.DrawLine): Lines++; break;
                case nameof(IRenderController.DrawText): Texts++; break;
                case nameof(IRenderController.AddFogVolume): FogVolumes++; break;
            }

            Type? returnType = targetMethod?.ReturnType;
            if (returnType == null || returnType == typeof(void)) return null;
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    /// <summary>The repository root, found by walking up to the file that defines the build.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? probe = new(AppContext.BaseDirectory);
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, "Build.bat"))) return probe.FullName;
            probe = probe.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Genesis repository above '{AppContext.BaseDirectory}'.");
    }

    private static string FirstError(ObjectSandboxResult result) =>
        result.Errors.Count == 0 ? "no error reported" : result.Errors[0].Message;

    private static string Tail(string log, int lines = 12)
    {
        string[] all = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.TakeLast(lines).Select(line => line.TrimEnd()));
    }

    private static ImageMetrics Measure(Bitmap bitmap)
    {
        HashSet<int> colors = [];
        long luminance = 0;
        int opaque = 0;
        int samples = 0;
        int stepX = Math.Max(1, bitmap.Width / 64);
        int stepY = Math.Max(1, bitmap.Height / 64);
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                colors.Add(color.ToArgb());
                samples++;
                if (color.A > 245) opaque++;
                luminance += (color.R * 299L + color.G * 587L + color.B * 114L) / 1000L;
            }
        }

        return new ImageMetrics(
            bitmap.Width,
            bitmap.Height,
            colors.Count,
            samples == 0 ? 0 : opaque / (double)samples,
            samples == 0 ? 0 : luminance / (double)samples);
    }
}

using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.Cameras;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class EngineSystemsSuite
{
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static bool Near(float a, float b) => Math.Abs(a - b) < .001f;
    private static GModelAnimationClip Clip(string name, params float[] positions) => new()
    {
        Name = name, Fps = 1, Loop = true,
        Frames = positions.Select(x => new GModelAnimationFrame
        { LocalBoneTransforms = [Matrix4x4.CreateTranslation(x, 0, 0), Matrix4x4.CreateTranslation(0, x, 0)] }).ToList(),
    };
    private static GModelAsset Asset() => new()
    {
        Rig = new GModelRig { Bones = [new GModelBone { Name = "Root", ParentIndex = -1, BindLocal = Matrix4x4.Identity },
            new GModelBone { Name = "Arm", ParentIndex = 0, BindLocal = Matrix4x4.Identity }] },
        Animations = [Clip("Idle", 0), Clip("Walk", 4), Clip("Run", 8), Clip("Travel", 0, 2, 4), Clip("Attack", 0, 10)],
    };
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Engine Systems");
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Models.FreshLookupAndInvalidation", () =>
            ModelRegistryCacheChecks.Run(ctx.Workspace, Check));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Models.SocketReusesEvaluatedAsset", () =>
            ModelSocketReuseChecks.Run(ctx.Workspace, Check));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Particles.BoundedFreshnessPreservesLiveControls", () =>
            ParticleResourceCacheChecks.Run(ctx.Workspace, Check));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.BoneRotationPreservesMotionAndIsolation", () =>
        {
            var asset = Asset();
            var first = new AnimationController();
            var second = new AnimationController();
            first.Layers[0].StateMachine.AddState("Walk", new ClipNode("Walk"));
            second.Layers[0].StateMachine.AddState("Walk", new ClipNode("Walk"));
            first.SetBoneRotation("Arm", Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f));
            Matrix4x4[] changed = first.Evaluate(asset), unchanged = second.Evaluate(asset);
            Check(changed[1].Translation == unchanged[1].Translation, "Gaze moved the animated joint origin.");
            Check(changed[0] == unchanged[0], "Gaze changed the root animation.");
            Check(changed[1] != unchanged[1], "The joint rotation was not applied.");
            Check(second.BoneRotations.Count == 0, "A shared model leaked gaze into another instance.");
            first.BoneRotations.Clear();
            Check(first.Evaluate(asset)[1] == unchanged[1], "Removing gaze did not restore the animation.");
            bool rejected = false;
            try { first.SetBoneRotation("Arm", new Quaternion(float.NaN, 0, 0, 1)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "A nonfinite rotation entered the skin palette.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Render.Water.PlanarReflection", () =>
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1, .1f, 100);
            Check(Genesis.Rendering.Primitives.PlanarReflectionMath.TryCreate(view, projection, new Vector3(0, 5, 10), 0,
                out var reflected, out var clipped, out var camera) && Near(camera.Y, -5), "Reflected camera is incorrect.");
            var above = Vector4.Transform(new Vector4(0, 2, 0, 1), reflected * clipped);
            var below = Vector4.Transform(new Vector4(0, -2, 0, 1), reflected * clipped);
            Check(above.Z > 0 && below.Z < 0, "Reflection near-plane clipping retained underwater geometry.");
            bool old = Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflections;
            var backend = Genesis.Rendering.Core.RenderBackendSelection.RequestedBackend;
            try
            {
                Genesis.Rendering.Core.RenderBackendSelection.Configure(Genesis.Rendering.Core.RenderBackendOption.SilkNetDx11);
                using var harness = new Genesis.Application.Runtime.RuntimeViewportHarness();
                Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflections = false;
                string offFile = Path.Combine(ctx.Captures, "water-reflection-off.png");
                var off = harness.CaptureWaterReflection(offFile);
                Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflections = true;
                string onFile = Path.Combine(ctx.Captures, "water-reflection-on.png");
                var on = harness.CaptureWaterReflection(onFile);
                Check(Genesis.Application.Runtime.ImageMetrics.HistogramDistance(off, on) > .005, "Enabling reflections did not change GPU output.");
                Check(CountOrangeReflection(onFile) > CountOrangeReflection(offFile) + 500,
                    "The reflected above-water object was not visible on the water surface.");
            }
            finally { Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflections = old; Genesis.Rendering.Core.RenderBackendSelection.Configure(backend); }
        });
        HeadlessHarness.RunCase(ctx.Report, "Render.Shader.SeeThroughDrawKeepsItsMeshShader", () =>
        {
            // The shader paints blue and gold stripes no tint or texture in the scene has: a
            // see-through draw that lost its shader shows none of the blue.
            static int Blue(string file)
            {
                using var bitmap = new System.Drawing.Bitmap(file);
                int count = 0;
                for (int y = 0; y < bitmap.Height; y += 2)
                    for (int x = 0; x < bitmap.Width; x += 2)
                    {
                        var c = bitmap.GetPixel(x, y);
                        if (c.B > 150 && c.R < 70 && c.G < 110) count++;
                    }
                return count;
            }
            var backend = Genesis.Rendering.Core.RenderBackendSelection.RequestedBackend;
            try
            {
                Genesis.Rendering.Core.RenderBackendSelection.Configure(Genesis.Rendering.Core.RenderBackendOption.SilkNetDx11);
                using var harness = new Genesis.Application.Runtime.RuntimeViewportHarness();
                string solidFile = Path.Combine(ctx.Captures, "shader-solid.png");
                string seeThroughFile = Path.Combine(ctx.Captures, "shader-see-through.png");
                harness.CaptureAuthoredShader(solidFile, swapVariant: true, seeThrough: false);
                harness.CaptureAuthoredShader(seeThroughFile, swapVariant: true, seeThrough: true);
                int solid = Blue(solidFile), seeThrough = Blue(seeThroughFile);
                Check(solid > 40 && seeThrough > solid / 3,
                    $"A see-through draw lost its mesh shader: {seeThrough} shader-blue samples against {solid} drawn solid.");
            }
            finally { Genesis.Rendering.Core.RenderBackendSelection.Configure(backend); }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Water.ExplicitPhysicsModes", () =>
        {
            var water = new Genesis.World.Terrain.TerrainWaterDefinition();
            Check(!water.Swimmable && water.PhysicsMode == Genesis.World.Terrain.WaterPhysicsMode.None, "New water enabled swimming implicitly.");
            var volume = Genesis.World.Terrain.TerrainColliderMesh.CreateVolume(water, Matrix4x4.Identity);
            Check(volume.Buoyancy == 0 && volume.LinearDrag == 0 && !volume.Swimmable, "Decorative water affects physics.");
            water.PhysicsMode = Genesis.World.Terrain.WaterPhysicsMode.Shallow;
            volume = Genesis.World.Terrain.TerrainColliderMesh.CreateVolume(water, Matrix4x4.Identity);
            Check(volume.Buoyancy == 0 && volume.LinearDrag > 0 && !volume.Swimmable, "Shallow water causes swimming or buoyancy.");
            var legacy = System.Text.Json.JsonSerializer.Deserialize<Genesis.World.Terrain.TerrainWaterDefinition>("{\"Swimmable\":true}")!;
            Check(legacy.PhysicsMode == Genesis.World.Terrain.WaterPhysicsMode.SwimmableVolume, "Existing explicit swimming opt-in was lost.");
            water.PhysicsMode = Genesis.World.Terrain.WaterPhysicsMode.None;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Genesis.World.Terrain.TerrainWaterDefinition>(System.Text.Json.JsonSerializer.Serialize(water))!;
            Check(saved.PhysicsMode == water.PhysicsMode, "Water mode did not survive save/load.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Theme.SharedDialog", () =>
        {
            Genesis.Application.Studio.Theme.ThemeService.SetPalette(Genesis.Application.Studio.Theme.ThemePalette.Dark);
            using var dialog = Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Create("Delete the selected asset?\n\nYou can restore it from project trash.",
                "Delete Asset", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning,
                System.Windows.Forms.MessageBoxDefaultButton.Button2);
            Check(dialog.BackColor == Genesis.Application.Studio.Theme.ThemeService.Palette.Canvas
                || dialog.BackColor == Genesis.Application.Studio.Theme.ThemeService.Palette.Surface, "Dialog ignored ThemePalette.");
            Check(dialog.AcceptButton is System.Windows.Forms.Button { DialogResult: System.Windows.Forms.DialogResult.No }
                && dialog.CancelButton == null && !dialog.ControlBox, "Dialog changed confirmation keyboard semantics.");
            dialog.ClientSize = new System.Drawing.Size(520, 220);
            VisualCapture.Capture(dialog, Path.Combine(ctx.Captures, "shared-theme-dialog.png"));
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.AnimationBlendTree", () =>
        {
            PgslContext context = new(); var prior = PgslCommands.BindContext(context);
            try
            {
                int treeId = (int)PgslCommands.AnimationBlendTreeCreate1D("Speed");
                PgslCommands.AnimationBlendTreeAddClip(treeId, "Run", 8);
                PgslCommands.AnimationBlendTreeAddClip(treeId, "Idle", 0);
                PgslCommands.AnimationBlendTreeAddClip(treeId, "Walk", 4);
                PgslCommands.AnimationStateCreate("Locomotion", treeId.ToString());
                PgslCommands.AnimationSetParameter("Speed", 6);
                var graph = context.AnimationController;
                var contributions = graph.Trees[treeId].Evaluate(graph.Parameters, 0);
                Check(contributions.Count == 2 && Near(contributions[0].Weight, .5f) && Near(contributions[1].Weight, .5f), "Incorrect 1D clip weights.");
                var asset = Asset();
                var state = new RuntimeModelAnimationState("", 0, 1, true, controller: graph);
                Check(Near(GModelPrimitiveFactory.EvaluateAnimatedLocals(asset, state)[0].Translation.X, 6), "The renderer did not evaluate the graph.");
                PgslCommands.AnimationSetParameter("Speed", -1);
                Check(Near(graph.Evaluate(asset)[0].Translation.X, 0), "Low parameter did not clamp to Idle.");
                PgslCommands.AnimationSetParameter("Speed", 30);
                Check(Near(graph.Evaluate(asset)[0].Translation.X, 8), "High parameter did not clamp to Run.");
                PgslCommands.AnimationLayerCreate(1, "Arm");
                PgslCommands.AnimationStateCreate("Upper", "Walk");
                PgslCommands.AnimationLayerSetWeight(1, .5);
                var pose = graph.Evaluate(asset);
                Check(Near(pose[0].Translation.X, 8) && Near(pose[1].Translation.Y, 6), "Bone mask or override weight leaked into lower body.");
                PgslCommands.AnimationLayerSetAdditive(1, true);
                Check(Near(graph.Evaluate(asset)[1].Translation.Y, 10), "Additive layer did not use the bind pose as reference.");
                var direct = new DirectBlend(); direct.SetWeight("Idle", 1); direct.SetWeight("Run", 3);
                Check(Near(direct.Evaluate(graph.Parameters, 0).Sum(c => c.Weight), 1), "Direct blend is not normalized.");
                var other = new AnimationController(); other.Layers[0].StateMachine.AddState("Idle", new ClipNode("Idle"));
                Check(other.Id != graph.Id && Near(other.Evaluate(asset)[0].Translation.X, 0), "Graphs share entity state.");
            }
            finally { PgslCommands.BindContext(prior); }
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Animation.TransitionsAndRootMotion", () =>
        {
            var graph = new AnimationController(); var machine = graph.Layers[0].StateMachine; var asset = Asset();
            machine.AddState("Idle", new ClipNode("Idle")); machine.AddState("Attack", new ClipNode("Attack", false));
            machine.AddTransition("*", "Attack", "Attack == true", .4f);
            machine.AddTransition("Attack", "Idle", "AnimationFinished", .2f);
            graph.Parameters["Attack"] = true; graph.Advance(.1f, 1, asset);
            Check(machine.CurrentState == "Attack", "Any-state transition was not selected.");
            graph.Parameters["Attack"] = false; graph.Advance(.2f, 1, asset);
            Check(Near(graph.Evaluate(asset)[0].Translation.X, 1), "Transition did not crossfade interpolated poses.");
            graph.Advance(.8f, 1, asset); Check(machine.CurrentState == "Idle", "A non-looping clip did not finish.");
            machine.AddState("Travel", new ClipNode("Travel")); machine.Play("Travel"); graph.RootMotionEnabled = true;
            graph.Advance(.5f, 1, asset);
            Check(Near(graph.RootMotionDelta.X, 1) && Near(graph.Evaluate(asset)[0].Translation.X, 0), "Root motion was lost or applied to the skin twice.");
            machine.Seek(2.8f); graph.Advance(.4f, 1, asset);
            Check(Near(graph.RootMotionDelta.X, .4f), "Root motion jumps at a clip loop.");
            graph.Advance(.4f, -1, asset); Check(Near(graph.RootMotionDelta.X, -.4f), "Reverse root motion is discontinuous.");
            graph.Advance(0, 1, asset); Check(graph.RootMotionDelta == Vector3.Zero, "A paused tick repeated root motion.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.TransformsAndRaycast", () => WithScene(ctx, (scene, context, game) =>
        {
            var world = scene.World; Entity entity = world.CreateEntity();
            Check(entity.Id > 0, "Live instance zero collides with the no-instance sentinel.");
            context.InstanceId = entity.Id;
            world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(entity, Transform3DComponent.Default);
            world.Set(entity, RigidBodyComponent.DynamicBox(Vector3.One));
            scene.Physics = Genesis.Physics.PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset());
            scene.Physics.RegisterEntity(world, entity, ref world.GetRef<RigidBodyComponent>(entity), ref world.GetRef<Transform3DComponent>(entity));
            scene.Physics.SetLinearVelocity(world, world.GetRef<RigidBodyComponent>(entity).RegistrationId, new Vector3(3, 0, 0));
            PgslCommands.InstanceSetScale3D(entity.Id, 2, 3, 4);
            PgslCommands.InstanceSetRotation3D(entity.Id, 0, 90, 0);
            Check(Near((float)PgslCommands.InstanceGetScaleZ(entity.Id), 4) && Near((float)PgslCommands.InstanceGetRotationYaw(entity.Id), 90), "Persistent 3D commands failed, including entity zero.");
            Check(Near(scene.Physics.GetLinearVelocity(world.GetRef<RigidBodyComponent>(entity).RegistrationId).X, 3), "Transform edit discarded velocity.");
            Check(Near((float)PgslCommands.PhysicsRaycast(10, 0, 0, -1, 0, 0, 20), 6), "Resized/rotated physics collider did not match its transform.");
            Check(Near((float)PgslCommands.PhysicsRaycastHitX(), 4) && PgslCommands.PhysicsRaycastHitInstanceId() == entity.Id
                && Near((float)PgslCommands.PhysicsRaycastHitNormalX(), 1), "Detailed raycast results are incorrect.");
            Check(PgslCommands.PhysicsRaycast(10, 20, 0, -1, 0, 0, 20) == -1 && PgslCommands.PhysicsRaycastHitInstanceId() == -1
                && PgslCommands.PhysicsRaycastHitX() == 0, "Miss left stale raycast results.");
            PgslCommands.InstanceSetScale3D(entity.Id, double.NaN, 1, 1);
            Check(PgslCommands.InstanceGetScaleZ(entity.Id) == 4, "Invalid transform input corrupted an entity.");
            VMEngine.Initialize(); var host = new ScriptHostSystem(); host.SetContext(game);
            List<PgslRuntimeNote> transformNotes = [];
            using var collectTransformNotes = PgslRuntimeDiagnostics.Collect(transformNotes);
            using (host.UseEventSources(new Dictionary<string, string>
            {
                ["Create"] = "caller = id; alias = instance_id; same = self; InstanceSetScale3D(id, 5, 6, 7); InstanceSetRotation3D(id, 10, 20, 30);",
            })) host.Attach(world, entity, "TransformProbe");
            var transformScript = (PgslBehavior)host.Instances.Single();
            Check(new[] { "caller", "alias", "same" }.All(name =>
                Convert.ToDouble(transformScript.GetVariablesSnapshot()[name]) == entity.Id)
                && transformNotes.All(note => note.Kind != PgslNoteKind.UnresolvedRead && note.Kind != PgslNoteKind.ReadFailed),
                "Instance identity aliases returned a sentinel or an unresolved read.");
            Check(Near(world.GetRef<TransformComponent>(entity).ScaleX, 5) && Near(world.GetRef<TransformComponent>(entity).ScaleZ, 7)
                && Near(world.GetRef<TransformComponent>(entity).RotationZ, 30),
                "PGSL synchronization overwrote the 3D transform: "
                + $"scale=({world.GetRef<TransformComponent>(entity).ScaleX}, "
                + $"{world.GetRef<TransformComponent>(entity).ScaleY}, {world.GetRef<TransformComponent>(entity).ScaleZ}), "
                + $"roll={world.GetRef<TransformComponent>(entity).RotationZ}, "
                + $"diagnostics={string.Join("; ", host.RecentDiagnostics.Select(diagnostic => diagnostic.Message))}.");
            Entity animated = world.CreateEntity(); world.Set(animated, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string>
            {
                ["Create"] = "InstanceSetScale3D(self, 8, 9, 10); InstanceSetRotation3D(instance_id, 0, 45, 0); var tree = AnimationBlendTreeCreate1D(\"Speed\"); AnimationBlendTreeAddClip(tree, \"Idle\", 0); AnimationBlendTreeAddClip(tree, \"Run\", 8); AnimationStateCreate(\"Move\", tree); AnimationSetParameter(\"Speed\", 4);",
            })) host.Attach(world, animated, "GraphProbe");
            Check(Near(world.GetRef<TransformComponent>(animated).ScaleZ, 10)
                && Near(world.GetRef<TransformComponent>(animated).RotationY, 45)
                && Near(world.GetRef<TransformComponent>(entity).ScaleX, 5)
                && Near(world.GetRef<TransformComponent>(entity).RotationZ, 30),
                "A second script changed another object's transform or could not resolve itself.");
            Check(host.RecentDiagnostics.Count == 0 && world.Has<ModelAnimatorComponent>(animated)
                && Near(world.GetRef<ModelAnimatorComponent>(animated).Controller.Evaluate(Asset())[0].Translation.X, 4), "PGSL graph creation did not reach the ECS animator.");
        }));
        HeadlessHarness.RunCase(ctx.Report, "Render.Textures.CutOutMipsKeepTheirCoverage", () =>
        {
            // Leaves: three texels in ten solid, the rest clear, scattered. Averaged alone, an 8 x 8
            // level would have almost nothing left above the cut-off.
            const int size = 64;
            byte[] leaves = new byte[size * size * 4];
            var random = new Random(7);
            for (int i = 0; i < size * size; i++)
            {
                leaves[i * 4 + 1] = 160;
                leaves[i * 4 + 3] = random.NextDouble() < 0.3 ? (byte)255 : (byte)0;
            }
            float full = Genesis.Rendering.Textures.TextureMipBuilder.Coverage(leaves, 0.35f);
            byte[] chain = Genesis.Rendering.Textures.TextureMipBuilder.BuildChain(leaves, size, size, srgb: true, out int levels);
            int offset = 0, width = size;
            for (int level = 1; level <= 3; level++) { offset += width * width * 4; width /= 2; }
            float small = Genesis.Rendering.Textures.TextureMipBuilder.Coverage(chain.AsSpan(offset, width * width * 4), 0.35f);
            Check(levels == 7 && Math.Abs(small - full) < 0.12f,
                $"An 8 x 8 level of a cut-out keeps {small:P0} of its texels against {full:P0} at full size.");

            // A soft fade is not a cut-out: its smaller sizes stay plain averages.
            byte[] fade = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++) fade[i * 4 + 3] = (byte)(i % size * 4);
            byte[] fadeChain = Genesis.Rendering.Textures.TextureMipBuilder.BuildChain(fade, size, size, srgb: true, out _);
            int expected = (fade[3] + fade[7] + fade[size * 4 + 3] + fade[size * 4 + 7] + 2) >> 2;
            Check(!Genesis.Rendering.Textures.TextureMipBuilder.IsCutOut(fade) && fadeChain[size * size * 4 + 3] == expected,
                "A soft alpha fade was treated as a cut-out.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Physics.ModelInstancesShareOneCollisionMesh", () => WithScene(ctx, (scene, context, game) =>
        {
            // Three crates of one model (a two-metre cube), one of them twice the size, and one mirrored.
            var world = scene.World;
            GModelAsset crate = GModelPrimitiveFactory.CreateCube("Crate", 2f);
            scene.Physics = Genesis.Physics.PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset());
            Entity Make(Vector3 position, Vector3 scale)
            {
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                Transform3DComponent transform = Transform3DComponent.Default;
                transform.Position = position;
                world.Set(entity, transform);
                RigidBodyComponent body = RigidBodyComponent.StaticBox(Vector3.One);
                body.Shape = CollisionShape.Mesh;
                world.Set(entity, body);
                ModelColliderBinding.AttachGeometry(world, entity, crate, scale);
                scene.Physics.RegisterEntity(world, entity, ref world.GetRef<RigidBodyComponent>(entity), ref world.GetRef<Transform3DComponent>(entity));
                return entity;
            }
            Entity first = Make(new Vector3(0, 0, 0), Vector3.One);
            Entity large = Make(new Vector3(10, 0, 0), new Vector3(2f));
            Entity mirrored = Make(new Vector3(20, 0, 0), new Vector3(-1, 1, 1));
            var counts = scene.Physics.SharedMeshCounts();
            Check(counts == (1, 2), $"Two plain crates should share one collision mesh, not {counts}.");
            Check(ReferenceEquals(world.GetRef<MeshColliderComponent>(first).Vertices, world.GetRef<MeshColliderComponent>(large).Vertices),
                "Instances of one model carry their own copies of its triangles.");
            // Each still collides at its own size: a ray from above meets the top of each crate.
            Check(Near((float)PgslCommands.PhysicsRaycast(0, 10, 0, 0, -1, 0, 20), 9f), "The first crate's top is not at y = 1.");
            Check(Near((float)PgslCommands.PhysicsRaycast(10, 10, 0, 0, -1, 0, 20), 8f), "The doubled crate's top is not at y = 2.");
            Check(Near((float)PgslCommands.PhysicsRaycast(20, 10, 0, 0, -1, 0, 20), 9f), "The mirrored crate's top is not at y = 1.");
            Check(Near((float)PgslCommands.PhysicsRaycast(-10, 0, 0, 1, 0, 0, 20), 9f), "The first crate's side is not at x = -1.");
            scene.Physics.UnregisterEntity(world, first, ref world.GetRef<RigidBodyComponent>(first));
            Check(scene.Physics.SharedMeshCounts() == (1, 1) && Near((float)PgslCommands.PhysicsRaycast(10, 10, 0, 0, -1, 0, 20), 8f),
                "Removing one crate freed the mesh the other still uses.");
            scene.Physics.UnregisterEntity(world, large, ref world.GetRef<RigidBodyComponent>(large));
            Check(scene.Physics.SharedMeshCounts() == (0, 0), "The shared mesh outlived its last crate.");
            _ = mirrored;
        }));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Physics.KinematicCharacterWalksStepsWallsAndCeilings", () => WithScene(ctx, (scene, context, game) =>
        {
            var world = scene.World;
            scene.Physics = Genesis.Physics.PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset());
            void Box(Vector3 centre, Vector3 half)
            {
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                Transform3DComponent transform = Transform3DComponent.Default;
                transform.Position = centre;
                world.Set(entity, transform);
                world.Set(entity, RigidBodyComponent.StaticBox(half));
                scene.Physics.RegisterEntity(world, entity, ref world.GetRef<RigidBodyComponent>(entity), ref world.GetRef<Transform3DComponent>(entity));
            }
            Box(new Vector3(0, -0.5f, 0), new Vector3(50, 0.5f, 50));     // floor, top at y = 0
            Box(new Vector3(3, 0.1f, 0), new Vector3(1, 0.1f, 1));       // a 20 cm step from x = 2 to 4
            Box(new Vector3(-3, 1, 0), new Vector3(0.5f, 1, 2));         // a wall whose face is at x = -2.5
            Box(new Vector3(0, 1.3f, 6), new Vector3(2, 0.1f, 2));       // a ceiling 1.2 m up around z = 6

            var character = new Genesis.Physics.KinematicCharacter(0.3f, 1.8f, 0.3f, 45f) { Position = new Vector3(0, 0.5f, 0) };
            var flags = character.Move(scene.Physics, world, new Vector3(0, -1, 0));
            Check(flags.HasFlag(Genesis.Physics.CharacterMoveFlags.Grounded) && Math.Abs(character.Position.Y) < 0.03f,
                $"The character did not land on the floor ({character.Position.Y:F3}, {flags}).");

            bool stepped = false;
            float highest = 0f;
            var trace = new System.Text.StringBuilder();
            for (int i = 0; i < 26; i++)
            {
                flags = character.Move(scene.Physics, world, new Vector3(0.2f, -0.05f, 0));
                trace.Append($" ({character.Position.X:F2},{character.Position.Y:F2},{(int)flags})");
                stepped |= flags.HasFlag(Genesis.Physics.CharacterMoveFlags.Stepped);
                if (character.Position.X > 2.4f && character.Position.X < 3.6f) highest = MathF.Max(highest, character.Position.Y);
            }
            Check(stepped && Math.Abs(highest - 0.2f) < 0.04f, $"The character did not climb the 20 cm step (stepped {stepped}, at {highest:F3} m):{trace}");
            Check(character.Position.X > 4.5f && Math.Abs(character.Position.Y) < 0.04f && character.Grounded,
                $"Walking off the step did not bring the character down to the floor ({character.Position}).");

            character.Position = new Vector3(0, 0.01f, 0);
            for (int i = 0; i < 20; i++) flags = character.Move(scene.Physics, world, new Vector3(-0.2f, -0.05f, 0));
            Check(Math.Abs(character.Position.X - -2.2f) < 0.05f && flags.HasFlag(Genesis.Physics.CharacterMoveFlags.Wall),
                $"The character did not stop at the wall's face ({character.Position.X:F3}, {flags}).");
            flags = character.Move(scene.Physics, world, new Vector3(-0.2f, 0, 0.3f));
            Check(character.Position.Z > 0.25f && Math.Abs(character.Position.X - -2.2f) < 0.05f, "Pushing into the wall at an angle did not slide along it.");

            character.Position = new Vector3(0, 0.01f, 3);
            for (int i = 0; i < 15; i++) character.Move(scene.Physics, world, new Vector3(0, -0.05f, 0.2f));
            Check(character.Position.Z < 4.1f, $"A standing character walked under a ceiling 1.2 m up (z = {character.Position.Z:F2}).");
            Check(character.SetHeight(scene.Physics, world, 1.0f), "The character could not crouch.");
            for (int i = 0; i < 15; i++) character.Move(scene.Physics, world, new Vector3(0, -0.05f, 0.2f));
            Check(character.Position.Z > 5.5f && !character.Fits(scene.Physics, world, 1.8f)
                && !character.SetHeight(scene.Physics, world, 1.8f) && character.Height == 1.0f,
                $"A crouched character should pass under the ceiling and not stand up there (z = {character.Position.Z:F2}).");
        }));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.SphereAndCapsuleQueries", () => WithScene(ctx, (scene, context, game) =>
        {
            // A two-metre box centred on the origin: its top is at y = 1 and its sides at x = +-1.
            var world = scene.World; Entity box = world.CreateEntity();
            world.Set(box, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(box, Transform3DComponent.Default);
            world.Set(box, RigidBodyComponent.DynamicBox(Vector3.One));
            scene.Physics = Genesis.Physics.PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset());
            scene.Physics.RegisterEntity(world, box, ref world.GetRef<RigidBodyComponent>(box), ref world.GetRef<Transform3DComponent>(box));
            context.InstanceId = 0;
            Check(Near((float)PgslCommands.PhysicsSphereRest(0, 0, 0.25, 5), 1.25f),
                $"A sphere lowered onto the box's top rested at {PgslCommands.PhysicsSphereRest(0, 0, 0.25, 5)} (1.25).");
            // Just past the edge a ray straight down misses; the sphere rests on the edge.
            double edge = PgslCommands.PhysicsSphereRest(1.1, 0, 0.25, 5);
            Check(PgslCommands.PhysicsRaycast(1.1, 5, 0, 0, -1, 0, 10) == -1 && Math.Abs(edge - (1 + Math.Sqrt(0.25 * 0.25 - 0.1 * 0.1))) < 0.01,
                $"A sphere over the box's edge rested at {edge}, not on the edge.");
            Check(PgslCommands.PhysicsSphereRest(5, 0, 0.25, 5) == PgslCommands.NothingBelow && PgslCommands.PhysicsRaycastHitInstanceId() == -1,
                "A sphere with nothing below it found somewhere to rest.");
            double side = PgslCommands.PhysicsSphereCast(5, 0, 0, 0.5, -1, 0, 0, 10);
            Check(Near((float)side, 3.5f) && Near((float)PgslCommands.PhysicsRaycastHitNormalX(), 1)
                && PgslCommands.PhysicsRaycastHitInstanceId() == box.Id && Near((float)PgslCommands.PhysicsRaycastHitX(), 1),
                $"A sphere moving at the box's side touched after {side} (3.5).");
            Check(Near((float)PgslCommands.PhysicsCapsuleCast(0, 5, 0, 0.3, 1.8, 0, -1, 0, 10), 3.1f),
                "An upright capsule did not stop with its foot on the box.");
            Check(PgslCommands.PhysicsOverlapCapsule(0, 1.4, 0, 0.3, 1) && !PgslCommands.PhysicsOverlapCapsule(0, 3, 0, 0.3, 1),
                "A capsule overlapping the box was not found, or one in the air was.");
            context.InstanceId = box.Id;
            Check(PgslCommands.PhysicsSphereCast(5, 0, 0, 0.5, -1, 0, 0, 10) == -1 && !PgslCommands.PhysicsOverlapCapsule(0, 0, 0, 0.3, 1),
                "A query hit the collider of the instance asking.");
            context.InstanceId = 0;
        }));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.ThirdPersonCamera", () => WithScene(ctx, (scene, context, game) =>
        {
            var entity = scene.World.CreateEntity(); context.InstanceId = entity.Id;
            scene.World.Set(entity, new TransformComponent { X = 10, Y = 0, Z = 20 });
            PgslCommands.CameraSetOrbitMouseLook(false); PgslCommands.CameraSetShoulderOffset(0, 2, 0);
            PgslCommands.CameraSetOrbitPitch(0); PgslCommands.CameraSetOrbitYaw(90); PgslCommands.CameraSetOrbitDistance(5);
            ThirdPersonCamera.UpdateScene(scene, .016f);
            Check(Vector3.Distance(scene.Camera3D.Position, new(5, 2, 20)) < .001f && Near(scene.Camera3D.Yaw, MathF.PI / 2), "Orbit angles or follow position are wrong.");
            var rig = scene.World.GetRef<ThirdPersonCameraComponent>(entity).Rig;
            rig.Update(new(10, 0, 20), Vector2.Zero, .016f, (_, _, _) => 2);
            Check(Near(rig.CurrentDistance, 1.75f), "Camera remained behind an occluder.");
            rig.Update(new(10, 0, 20), Vector2.Zero, .016f, (_, _, _) => -1);
            Check(rig.CurrentDistance > 1.75f && rig.CurrentDistance < 5, "Camera recovery was not smoothed.");
            rig.Update(new(10, 0, 20), Vector2.Zero, .016f, (_, _, _) => .1f);
            Check(rig.CurrentDistance == 0, "Minimum boom distance pushed the camera through a close wall.");
        }));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.NavMesh", () => WithScene(ctx, (scene, context, game) =>
        {
            PgslCommands.NavMeshBakeGrid(0, 0, 12, 12, 1);
            double path = PgslCommands.NavMeshPathFind(.5, 0, .5, 10.5, 0, 10.5);
            Check(path > 0 && PgslCommands.NavMeshPathGetWaypointCount(path) == 2, "An open navigation grid did not produce a straight route.");
            Check(Near((float)PgslCommands.NavMeshPathGetWaypointX(path, 1), 10.5f), "Destination was not preserved.");
            PgslCommands.NavMeshPathFree(path); Check(PgslCommands.NavMeshPathGetWaypointCount(path) == 0, "Freed path is still visible.");
            var entity = scene.World.CreateEntity(); context.InstanceId = entity.Id; context.X = context.Z = .5;
            scene.World.Set(entity, new TransformComponent { X = .5f, Z = .5f });
            PgslCommands.NavMeshAgentSetSpeed(20); PgslCommands.NavMeshAgentSetDestination(10.5, 0, 10.5);
            var agent = scene.World.GetRef<NavMeshAgentComponent>(entity).Agent;
            var point = agent.Update(new(.5f, 0, .5f), 1);
            Check(agent.HasArrived && agent.DistanceRemaining == 0 && Vector3.Distance(point, new(10.5f, 0, 10.5f)) < .001, "Agent overshot or did not arrive.");
            PgslCommands.NavMeshAgentSetDestination(-10, 0, -10);
            Check(!agent.HasArrived && agent.DistanceRemaining == -1, "An unreachable path reported success.");
            game.SetRoom(RoomAsset.Create("Different", RoomDimension.ThreeD));
            Check(PgslCommands.NavMeshPathFind(.5, 0, .5, 2.5, 0, 2.5) == -1, "Navigation leaked between rooms.");
        }));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Navigation.ObstaclesAndPersistence", () =>
        {
            var data = NavMeshBuilder.Build(0, 0, 12, 12, 1, (_, _) => 0,
                [new NavigationObstacle(new(5, 0, 0), new(6, 3, 8))], agentRadius: 0);
            var query = new NavMeshQuery(data); Vector3 a = new(1.5f, 0, 1.5f), b = new(10.5f, 0, 1.5f);
            var path = query.FindPath(a, b);
            Check(path.Count > 2 && !query.CanTravel(a, b), "Path cut through a carved wall.");
            for (int i = 1; i < path.Count; i++) Check(query.CanTravel(path[i - 1], path[i]), "Smoothed segment leaves walkable cells.");
            string file = Path.Combine(ctx.Workspace, "probe.navmesh"); data.Save(file);
            Check(new NavMeshQuery(NavMeshData.Load(file)).FindPath(a, b).Count == path.Count, "Navigation persistence changed connectivity.");
            var steep = NavMeshBuilder.Build(0, 0, 4, 4, 1, (x, _) => x * 3);
            Check(steep.Walkable.All(w => !w), "Unwalkable slopes were admitted.");
            var diagonal = NavMeshBuilder.Build(0, 0, 2, 2, 1, (_, _) => 0);
            diagonal.Walkable[1] = false; diagonal.Walkable[2] = false;
            Check(new NavMeshQuery(diagonal).FindPath(new(.5f, 0, .5f), new(1.5f, 0, 1.5f)).Count == 0, "Path crossed a blocked diagonal corner.");
            var avoiding = new NavMeshAgent { Speed = 2 };
            avoiding.SetPath([new Vector3(4, 0, 0)]);
            Vector3 steered = avoiding.Update(Vector3.Zero, .5f,
                (_, candidate) => candidate.X <= 0 || MathF.Abs(candidate.Z) > .2f);
            Check(MathF.Abs(steered.Z) > .2f && steered.X > 0, "Agent did not steer around a dynamic obstacle.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Components.CameraAndNavigationAuthoring", () =>
        {
            using var world = new Genesis.Runtime.ECS.World();
            var prefab = JObject.Parse("""{"components":[{"type":"ThirdPersonCameraComponent","props":{"Distance":9,"MouseLook":false}},{"type":"NavMeshAgentComponent","props":{"Speed":2}}]}""");
            Entity entity = PrefabSpawner.Spawn(world, prefab);
            Check(world.Has<ThirdPersonCameraComponent>(entity) && world.Has<NavMeshAgentComponent>(entity), "Authored components did not spawn.");
            Check(Near(world.GetRef<ThirdPersonCameraComponent>(entity).Rig.Distance, 9)
                && Near(world.GetRef<NavMeshAgentComponent>(entity).Agent.Speed, 2), "Component properties were not restored.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SnapToFloor", () =>
        {
            string root = Path.Combine(ctx.Workspace, "snap-floor");
            string rooms = Path.Combine(root, "Assets", "Rooms");
            Directory.CreateDirectory(rooms);
            RoomAsset room = RoomAsset.Create("Floor", RoomDimension.ThreeD);
            RoomNode platform = new()
            {
                Name = "Platform", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new(), Transform = new() { Y = 1, ScaleY = 2 },
            };
            RoomNode item = new()
            {
                Name = "Item", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new(), Transform = new() { Y = 5 },
            };
            room.Nodes.Add(platform); room.Nodes.Add(item);
            string file = Path.Combine(rooms, "Floor.room.json");
            RoomAssetLoader.Save(room, file);
            using var editor = new Genesis.Application.Editors.Suite.Rooms.RoomEditorControl(file, root);
            RoomNode loadedItem = editor.Room.Nodes.Single(node => node.Name == "Item");
            editor.Select(loadedItem);
            Check(editor.SnapSelectionToFloor() && Near(loadedItem.Transform.Y, 3), "Snap to floor missed the object surface.");
            editor.Undo();
            Check(Near(loadedItem.Transform.Y, 5), "Snap to floor did not participate in undo history.");
        });
    }
    private static int CountOrangeReflection(string file)
    {
        using var image = new System.Drawing.Bitmap(file);
        int count = 0;
        for (int y = (int)(image.Height * .62f); y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            System.Drawing.Color pixel = image.GetPixel(x, y);
            if (pixel.R > 70 && pixel.R > pixel.G * 1.45f && pixel.R > pixel.B * 1.2f) count++;
        }
        return count;
    }
    private static void WithScene(HeadlessContext ctx, Action<RuntimeScene, PgslContext, ProjectGameContext> test)
    {
        using var scene = new RuntimeScene("Engine system test");
        var game = new ProjectGameContext(ctx.Workspace, scene, null, null, RoomAsset.Create("Probe", RoomDimension.ThreeD), null);
        var context = new PgslContext(); var oldContext = PgslCommands.BindContext(context);
        var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = ctx.Workspace;
        try { test(scene, context, game); }
        finally { PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
    }
}





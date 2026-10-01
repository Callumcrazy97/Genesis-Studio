using System.Diagnostics;
using System.Numerics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Runtime;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless;

/// <summary>
/// What a large world is filled with: models that load quickly, scattered copies that are solid,
/// and scenery that exists only near the camera.
/// </summary>
internal static class LargeWorldContentSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Models.Cache.LoadsTheSameModelFasterAndNeverStale", () =>
        {
            string root = Path.Combine(context.Workspace, "ModelCacheProject");
            string models = Path.Combine(root, "Assets", "Models");
            Directory.CreateDirectory(models);
            string file = Path.Combine(models, "Statue.gmodel");
            string cache = ModelBinaryCache.PathFor(file)
                ?? throw new InvalidOperationException("A model under Assets has nowhere to keep its cache.");
            HeadlessHarness.Assert(cache.StartsWith(Path.Combine(root, ".genesis", "Cache", "Models"), StringComparison.OrdinalIgnoreCase),
                $"The cache belongs in the project's .genesis/Cache/Models folder, not {cache}.");
            HeadlessHarness.Assert(ModelBinaryCache.PathFor(Path.Combine(context.Workspace, "Loose.gmodel")) == null,
                "A model outside any project must not be given a cache location.");
            if (File.Exists(cache)) File.Delete(cache);

            GModelAsset authored = Statue(1f);
            RuntimeModelStore.Save(file, authored);
            HeadlessHarness.Assert(new FileInfo(file).Length > ModelBinaryCache.MinimumSourceBytes,
                "The test model is too small to be cached.");

            int hits = ModelBinaryCache.Hits;
            var watch = Stopwatch.StartNew();
            GModelAsset parsed = RuntimeModelStore.Load(file);
            double parseMs = watch.Elapsed.TotalMilliseconds;
            HeadlessHarness.Assert(ModelBinaryCache.Hits == hits, "The first load claimed a cache that did not exist yet.");
            WaitFor(() => File.Exists(cache), "The cache was not written after the first load.");

            watch.Restart();
            GModelAsset cached = RuntimeModelStore.Load(file);
            double cacheMs = watch.Elapsed.TotalMilliseconds;
            HeadlessHarness.Assert(ModelBinaryCache.Hits == hits + 1, "The second load did not use the cache.");
            AssertSame(parsed, cached);
            HeadlessHarness.Assert(new FileInfo(cache).Length < new FileInfo(file).Length * 0.6,
                $"The cache ({new FileInfo(cache).Length:N0} bytes) is not much smaller than the model ({new FileInfo(file).Length:N0}).");
            HeadlessHarness.Assert(cacheMs < parseMs,
                $"Loading from the cache took {cacheMs:F0} ms against {parseMs:F0} ms for the JSON.");

            // Editing the model must be seen at once: the cache is for the file as it was.
            Thread.Sleep(20);
            RuntimeModelStore.Save(file, Statue(1.5f));
            int before = ModelBinaryCache.Hits, writes = ModelBinaryCache.Writes;
            GModelAsset edited = RuntimeModelStore.Load(file);
            HeadlessHarness.Assert(ModelBinaryCache.Hits == before, "A stale cache was used after the model changed.");
            HeadlessHarness.Assert(MathF.Abs(edited.Meshes[0].Vertices[10].Position.Length() - 1.5f) < 1e-3f,
                "The edited model was not what loaded.");

            // A damaged cache is ignored, not fatal.
            WaitFor(() => File.Exists(cache) && ModelBinaryCache.Writes > writes, "The cache was not rewritten for the edited model.");
            GModelAsset recached = RuntimeModelStore.Load(file);
            HeadlessHarness.Assert(ModelBinaryCache.Hits == before + 1
                && MathF.Abs(recached.Meshes[0].Vertices[10].Position.Length() - 1.5f) < 1e-3f,
                "The rewritten cache was not used, or does not hold the edited model.");
            // Damage that leaves the text well-formed is the dangerous kind: it would read back
            // as some other model. Here it swallows the mesh list into the model's name.
            byte[] bytes = File.ReadAllBytes(cache);
            Array.Fill(bytes, (byte)'!', 128, Math.Min(4096, bytes.Length - 128));
            File.WriteAllBytes(cache, bytes);
            int damagedHits = ModelBinaryCache.Hits;
            GModelAsset recovered = RuntimeModelStore.Load(file);
            HeadlessHarness.Assert(ModelBinaryCache.Hits == damagedHits, "A damaged cache was accepted.");
            HeadlessHarness.Assert(recovered.Meshes.Count == 1 && recovered.Meshes[0].Vertices.Length == edited.Meshes[0].Vertices.Length
                && !recovered.ImportRequired,
                "A damaged cache broke the load instead of falling back to the model file: "
                + $"{recovered.Meshes.Count} meshes, {(recovered.Meshes.Count > 0 ? recovered.Meshes[0].Vertices.Length : 0)} vertices, "
                + $"import required {recovered.ImportRequired} ('{recovered.ImportMessage}').");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Scatter.CopiesAreSolidOnlyNearWhatCanTouchThem", () =>
        {
            TerrainAsset terrain = RuntimeViewportHarness.BuildIslandTerrain(513, 4f);
            var layer = new TerrainScatterLayer
            {
                Model = "Tree", DensityPerHectare = 200, MinimumSpacing = 4, MinimumHeight = 2, MaximumHeight = 400,
                MaximumSlopeDegrees = 30, Seed = 3, CollisionRadius = 0.3f, CollisionHeight = 5f, Sink = 0f,
            };
            layer.Normalize();
            var ghost = new TerrainScatterLayer { Model = "Grass", DensityPerHectare = 200, MinimumHeight = 2, Seed = 4 };
            ghost.Normalize();

            using PhysicsWorld physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            using var world = new Genesis.Runtime.ECS.World();
            HeadlessHarness.Assert(!new TerrainScatterColliders(terrain, new[] { ghost }, Matrix4x4.Identity, "none").HasLayers,
                "A layer with no collision size was given colliders.");
            var colliders = new TerrainScatterColliders(terrain, new[] { layer, ghost }, Matrix4x4.Identity, "Scatter:test")
            {
                RegistrationsPerUpdate = 10000,
            };

            // Stand beside a real tree.
            TerrainScatterInstance tree = default;
            (int cellsX, int cellsZ) = TerrainScatterPlacement.CellCount(terrain);
            for (int cz = 0; cz < cellsZ && tree.Scale == 0f; cz++)
            for (int cx = 0; cx < cellsX && tree.Scale == 0f; cx++)
            {
                TerrainScatterInstance[] found = TerrainScatterPlacement.Generate(terrain, layer, cx, cz);
                if (found.Length > 20) tree = found[found.Length / 2];
            }

            HeadlessHarness.Assert(tree.Scale > 0f, "The test island has no trees.");
            Vector3 here = tree.Position + new Vector3(1.5f, 1f, 0f);
            TerrainColliderFocus[] focus = [new TerrainColliderFocus(here, 14f)];
            colliders.Update(physics, focus);
            int near = colliders.ResidentColliders;
            HeadlessHarness.Assert(near >= 1 && near < 80 && physics.ExternalStaticCount == near,
                $"{near} trees are solid within 14 m ({physics.ExternalStaticCount} registered); expected a handful.");

            // The trunk stops a ray; the same ray a tree-width to the side passes.
            Vector3 trunk = tree.Position + new Vector3(0f, 2f * tree.Scale, 0f);
            HeadlessHarness.Assert(physics.RaycastDown(world, trunk + new Vector3(0f, 50f, 0f), 60f, out PhysicsRaycastHit top)
                && MathF.Abs(top.Point.Y - (tree.Position.Y + 5f * tree.Scale)) < 0.2f,
                $"A ray dropped on the tree did not land on its {5f * tree.Scale:F1} m collider (hit at {top.Point.Y - tree.Position.Y:F2} m).");

            // Walk away: the colliders go with you.
            Vector3 away = here + new Vector3(600f, 0f, 0f);
            focus[0] = new TerrainColliderFocus(away, 14f);
            colliders.Update(physics, focus);
            HeadlessHarness.Assert(physics.ExternalStaticCount == colliders.ResidentColliders,
                "Colliders left behind were not removed from the physics world.");
            HeadlessHarness.Assert(!physics.RaycastDown(world, trunk + new Vector3(0f, 50f, 0f), 60f, out _),
                "The tree is still solid 600 m behind the only thing that could touch it.");

            colliders.Clear(physics);
            HeadlessHarness.Assert(physics.ExternalStaticCount == 0 && colliders.ResidentColliders == 0, "Clearing left colliders behind.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Scenery.PlainObjectsLoadNearTheCameraAndUnloadBehindIt", () =>
        {
            JObject Prefab(string json) => JObject.Parse(json);
            const string model = """{"type":"ModelRendererComponent","props":{"ModelAsset":"House Model"}}""";
            const string script = """{"type":"ScriptComponent","props":{"ScriptClass":"Test Gate"}}""";
            HeadlessHarness.Assert(RoomSceneBuilder.IsScenery(Prefab($$"""{"components":[{{model}}],"events":[]}""")),
                "An Object that only shows a model is scenery.");
            HeadlessHarness.Assert(!RoomSceneBuilder.IsScenery(Prefab("{\"components\":[" + model + "," + script + "]}")),
                "A scripted Object must never be streamed: it has state.");
            HeadlessHarness.Assert(!RoomSceneBuilder.IsScenery(Prefab($$"""{"components":[{{model}}],"events":["Step"]}""")),
                "An Object with events must never be streamed.");
            HeadlessHarness.Assert(!RoomSceneBuilder.IsScenery(Prefab($$"""{"components":[{{model}}],"physics":"Crate"}""")),
                "An Object with a physics preset must never be streamed.");
            HeadlessHarness.Assert(!RoomSceneBuilder.IsScenery(Prefab($$"""{"components":[{{model}}],"persistent":true}""")),
                "A persistent Object must never be streamed.");
            HeadlessHarness.Assert(!RoomSceneBuilder.IsScenery(Prefab("""{"components":[{"type":"TransformComponent","props":{}}]}""")),
                "An Object with nothing to show is not scenery.");

            // A real project: forty houses in a line 100 m apart, and one scripted gate far away.
            string parent = Path.Combine(context.Workspace, "SceneryProjects");
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Scenery" + Guid.NewGuid().ToString("N")[..6], "Blank");
            var resources = new ResourceService(project);
            string objects = ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject);
            File.WriteAllText(resources.CreateResource(objects, ResourceKind.GameObject, "Test House"),
                $$"""{"schemaVersion":2,"dimension":"ThreeD","model":"","components":[{{model}}],"events":[]}""");
            File.WriteAllText(resources.CreateResource(objects, ResourceKind.GameObject, "Test Gate"),
                "{\"schemaVersion\":2,\"dimension\":\"ThreeD\",\"model\":\"\",\"components\":[" + model + "," + script + "],\"events\":[]}");
            ResourceNames.Invalidate(project.RootPath);

            RoomAsset room = RoomAsset.Create("Street", RoomDimension.ThreeD);
            for (int i = 0; i < 40; i++)
                room.Nodes.Add(new RoomNode
                {
                    Name = "House " + i, Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id, EnabledIn2D = false,
                    Transform = new RoomTransform { X = i * 100f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
                    GameObject = new RoomGameObjectData { Prefab = "Test House" },
                });
            room.Nodes.Add(new RoomNode
            {
                Name = "Gate", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id, EnabledIn2D = false,
                Transform = new RoomTransform { X = 3900f, ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
                GameObject = new RoomGameObjectData { Prefab = "Test Gate" },
            });

            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            using var scene = new RuntimeScene("Scenery");
            var streamer = new RoomSceneryStreamer(350f);
            var builder = new RoomSceneBuilder(project.RootPath, new ScriptHostSystem()) { Scenery = streamer };
            RoomBuildResult built = builder.Build(scene, room);
            HeadlessHarness.Assert(streamer.Total == 40 && built.SpawnedEntities.Count == 1,
                $"Expected 40 houses left to the streamer and the gate created with the room; got {streamer.Total} and {built.SpawnedEntities.Count}.");
            streamer.Attach(builder, room);

            var time = new GameTime();
            time.Advance(1f / 60f);
            scene.Camera3D.Position = new Vector3(0f, 2f, 0f);
            streamer.Update(scene, time);
            scene.World.FlushDeferred();
            // Houses at 0, 100, 200 and 300 m are within 350 m.
            HeadlessHarness.Assert(streamer.Loaded == 4, $"{streamer.Loaded} houses loaded around the start; expected 4.");

            // Walk the street. Houses appear ahead and disappear behind; the count stays small.
            int most = 0;
            for (float x = 0f; x <= 3900f; x += 25f)
            {
                scene.Camera3D.Position = new Vector3(x, 2f, 0f);
                for (int frame = 0; frame < 3; frame++) { streamer.Update(scene, time); scene.World.FlushDeferred(); }
                most = Math.Max(most, streamer.Loaded);
            }

            for (int frame = 0; frame < 20; frame++) { streamer.Update(scene, time); scene.World.FlushDeferred(); }
            HeadlessHarness.Assert(most <= 12, $"Up to {most} houses were loaded at once on a street where about 8 are in range.");
            // At the far end: houses from 3550 m to 3900 m are in range, and nothing from the start remains.
            HeadlessHarness.Assert(streamer.Loaded is >= 4 and <= 9, $"{streamer.Loaded} houses remain loaded at the end of the street.");
            int alive = 0;
            scene.World.Query<Genesis.Runtime.ECS.Components.TransformComponent, Genesis.Runtime.ECS.Components.ModelRendererComponent>(
                (Genesis.Shared.ECS.Entity _, ref Genesis.Runtime.ECS.Components.TransformComponent transform,
                    ref Genesis.Runtime.ECS.Components.ModelRendererComponent _) =>
                {
                    alive++;
                    HeadlessHarness.Assert(transform.X > 3300f, $"An object at {transform.X:F0} m is still alive 600 m behind the camera.");
                });
            HeadlessHarness.Assert(alive == streamer.Loaded + 1, $"{alive} model objects exist; expected the {streamer.Loaded} loaded houses and the gate.");
        });

        RunStreaming(context);
        RunReadAheadAndLights(context);
    }

    private static void RunReadAheadAndLights(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Models.ReadAhead.WorkersReadWhatTheLoadThenTakes", () =>
        {
            string models = Path.Combine(context.Workspace, "ModelReadAheadProject", "Assets", "Models");
            Directory.CreateDirectory(models);
            string[] files = Enumerable.Range(0, 6).Select(i => Path.Combine(models, $"Statue {i}.gmodel")).ToArray();
            for (int i = 0; i < files.Length; i++) RuntimeModelStore.Save(files[i], Statue(1f + i));

            bool enabled = RuntimeModelStore.PrefetchEnabled;
            try
            {
                RuntimeModelStore.PrefetchEnabled = false;
                int started = RuntimeModelStore.PrefetchesStarted, used = RuntimeModelStore.PrefetchesUsed;
                RuntimeModelStore.Prefetch(files[0]);
                HeadlessHarness.Assert(RuntimeModelStore.PrefetchesStarted == started, "Reading ahead ran while switched off.");

                RuntimeModelStore.PrefetchEnabled = true;
                foreach (string file in files) RuntimeModelStore.Prefetch(file);
                RuntimeModelStore.Prefetch(files[0]);
                RuntimeModelStore.Prefetch(Path.Combine(models, "Missing.gmodel"));
                HeadlessHarness.Assert(RuntimeModelStore.PrefetchesStarted == started + files.Length,
                    $"Six files asked for (one of them twice, and one that does not exist) started {RuntimeModelStore.PrefetchesStarted - started} reads.");

                // Give the workers a moment to begin; a file none has reached is read by the load itself.
                Thread.Sleep(60);
                for (int i = 0; i < files.Length; i++)
                {
                    GModelAsset loaded = RuntimeModelStore.Load(files[i]);
                    HeadlessHarness.Assert(!loaded.ImportRequired && loaded.Meshes.Count == 1
                        && MathF.Abs(loaded.Meshes[0].Vertices[10].Position.Length() - (1f + i)) < 1e-3f,
                        $"Model {i} did not load as saved after being read ahead.");
                }

                int answered = RuntimeModelStore.PrefetchesUsed - used;
                HeadlessHarness.Assert(answered >= 2 && answered <= files.Length,
                    $"{answered} of {files.Length} loads were answered by a worker's read; expected at least two.");

                // A script can wait for the reading to finish before it changes room.
                RuntimeModelStore.Prefetch(files[3]);
                RuntimeModelStore.Prefetch(files[4]);
                WaitFor(() => RuntimeModelStore.PrefetchesPending == 0, "Models read ahead never finished being read.");
                int before = RuntimeModelStore.PrefetchesUsed;
                RuntimeModelStore.Load(files[3]);
                RuntimeModelStore.Load(files[4]);
                HeadlessHarness.Assert(RuntimeModelStore.PrefetchesUsed == before + 2, "Models that had finished being read were read again by the load.");

                // A result is handed out once: two loads must not share one model object.
                RuntimeModelStore.Prefetch(files[1]);
                GModelAsset first = RuntimeModelStore.Load(files[1]), second = RuntimeModelStore.Load(files[1]);
                HeadlessHarness.Assert(!ReferenceEquals(first, second), "Two loads were given the same model object.");

                // A model saved after it was read ahead must load as saved, not as read.
                RuntimeModelStore.Prefetch(files[2]);
                Thread.Sleep(400);
                RuntimeModelStore.Save(files[2], Statue(9f));
                GModelAsset current = RuntimeModelStore.Load(files[2]);
                HeadlessHarness.Assert(MathF.Abs(current.Meshes[0].Vertices[10].Position.Length() - 9f) < 1e-3f,
                    "A load returned the model as it was before it was saved again.");
            }
            finally
            {
                RuntimeModelStore.PrefetchEnabled = enabled;
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Models.AutoLod.AnimatedMeshesSimplifyAndKeepTheirBoneWeights", () =>
        {
            // A limb: a tube of 48 rings bound to two bones, bending in the middle.
            const int segments = 40, rings = 48;
            var vertices = new List<SkinnedMeshVertex>();
            var indices = new List<ushort>();
            for (int ring = 0; ring <= rings; ring++)
            for (int segment = 0; segment <= segments; segment++)
            {
                float turn = MathF.Tau * segment / segments, along = ring / (float)rings;
                Vector3 normal = new(MathF.Cos(turn), 0f, MathF.Sin(turn));
                float upper = Math.Clamp(1.5f - along * 2f, 0f, 1f);
                vertices.Add(new SkinnedMeshVertex
                {
                    Position = normal * 0.12f + new Vector3(0f, along * 1.6f, 0f), Normal = normal, Color = Vector4.One,
                    UV = new Vector2(segment / (float)segments, along),
                    JointIndices = new Vector4(0f, 1f, 0f, 0f), JointWeights = new Vector4(upper, 1f - upper, 0f, 0f),
                });
            }

            for (int ring = 0; ring < rings; ring++)
            for (int segment = 0; segment < segments; segment++)
            {
                int a = ring * (segments + 1) + segment, b = a + 1, c = a + segments + 1, d = c + 1;
                indices.AddRange(new[] { (ushort)a, (ushort)b, (ushort)c, (ushort)b, (ushort)d, (ushort)c });
            }

            SkinnedMeshVertex[] source = vertices.ToArray();
            var levels = ModelGpuCache.BuildSkinnedAutoLodMeshes(source, indices.ToArray());
            HeadlessHarness.Assert(levels.Count >= 2, $"An animated mesh of {indices.Count / 3} triangles was given {levels.Count} simpler versions.");
            int previous = indices.Count / 3;
            foreach ((int level, SkinnedMeshVertex[] kept, ushort[] keptIndices) in levels)
            {
                int triangles = keptIndices.Length / 3;
                HeadlessHarness.Assert(triangles < previous, $"Level {level + 1} has {triangles} triangles; the level before has {previous}.");
                previous = triangles;
                // Each vertex kept is one of the originals, so it moves with the bones exactly as it did.
                foreach (SkinnedMeshVertex vertex in kept)
                    HeadlessHarness.Assert(Array.Exists(source, original => original.Position == vertex.Position
                        && original.JointWeights == vertex.JointWeights && original.JointIndices == vertex.JointIndices && original.UV == vertex.UV),
                        $"Level {level + 1} holds a vertex that is not one of the mesh's own; its bone weights cannot be trusted.");
                // The limb keeps its length: simplifying must not eat the ends.
                float low = kept.Min(vertex => vertex.Position.Y), high = kept.Max(vertex => vertex.Position.Y);
                HeadlessHarness.Assert(low < 0.05f && high > 1.55f, $"Level {level + 1} spans {low:F2} to {high:F2} m of a 1.6 m limb.");
            }

            bool enabled = ModelGpuCache.AutoLodSkinned;
            HeadlessHarness.Assert(enabled, "Animated meshes should be simplified unless a game turns it off.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rendering.Camera.ShakeMovesTheViewNotTheCamera", () =>
        {
            var camera = new Genesis.Shared.Rendering.Camera { Position = new Vector3(3f, 2f, -5f), Yaw = 0.4f, Pitch = -0.1f };
            Matrix4x4 still = camera.ViewMatrix;
            camera.Shake(0.5f, 1f, 4f);
            float most = 0f, mostRoll = 0f;
            for (int frame = 0; frame < 30; frame++)
            {
                camera.AdvanceShake(1f / 60f);
                most = MathF.Max(most, camera.ViewOffset.Length());
                mostRoll = MathF.Max(mostRoll, MathF.Abs(camera.ViewRoll));
                HeadlessHarness.Assert(MathF.Abs(Vector3.Dot(camera.ViewOffset, camera.Forward)) < 1e-4f,
                    "The shake pushed the view along its own direction; it should move across it.");
            }

            HeadlessHarness.Assert(camera.IsShaking && most > 0.05f && most <= 0.5f * 1.001f,
                $"A 0.5 m shake moved the view by at most {most:F3} m.");
            HeadlessHarness.Assert(mostRoll > 0.005f && mostRoll <= 4f * MathF.PI / 180f * 1.001f,
                $"A 4 degree shake tilted the view by at most {mostRoll * 180f / MathF.PI:F2} degrees.");
            HeadlessHarness.Assert(camera.Position == new Vector3(3f, 2f, -5f) && camera.ViewMatrix != still,
                "A shake must change what is seen and leave the camera where the game put it.");

            // It dies away, and the view is then exactly what it was.
            for (int frame = 0; frame < 60; frame++) camera.AdvanceShake(1f / 60f);
            HeadlessHarness.Assert(!camera.IsShaking && camera.ViewOffset == Vector3.Zero && camera.ViewRoll == 0f && camera.ViewMatrix == still,
                "The view did not return to rest when the shake ended.");

            // Nonsense is ignored rather than shaking forever.
            camera.Shake(float.NaN, 1f);
            camera.Shake(1f, 0f);
            HeadlessHarness.Assert(!camera.IsShaking, "A shake with no length or no size was started.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Time.GameSpeed.IsSetByScriptsAndKeptInRange", () =>
        {
            float before = GameSpeed.Scale;
            try
            {
                PgslCommands.GameSetSpeed(0.25);
                HeadlessHarness.Assert(PgslCommands.GameGetSpeed() == 0.25 && GameSpeed.Scale == 0.25f, "Slow motion was not set.");
                PgslCommands.GameSetSpeed(0);
                HeadlessHarness.Assert(GameSpeed.Scale == 0f, "A hit-stop (speed 0) was not accepted.");
                PgslCommands.GameSetSpeed(-3);
                HeadlessHarness.Assert(GameSpeed.Scale == 0f, "Game time was allowed to run backwards.");
                PgslCommands.GameSetSpeed(100);
                HeadlessHarness.Assert(GameSpeed.Scale == GameSpeed.Maximum, $"Game speed 100 was accepted as {GameSpeed.Scale}.");
                PgslCommands.GameSetSpeed(double.NaN);
                HeadlessHarness.Assert(GameSpeed.Scale == 1f, "A speed that is not a number should mean normal speed.");
            }
            finally
            {
                GameSpeed.Scale = before;
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rooms.GotoWhenLoaded.ChangesOnceNothingIsStillBeingRead", () =>
        {
            using var scene = new RuntimeScene("When loaded");
            var game = new Genesis.Runtime.Project.ProjectGameContext(context.Workspace, scene, null, null,
                RoomAsset.Create("Hall", RoomDimension.ThreeD), null);
            HeadlessHarness.Assert(game.RoomLoadProgress == 1f && !game.TryConsumePendingRoom(out _),
                "A game that asked for no room reports one loading or pending.");

            game.ChangeRoomWhenLoaded("Cellar");
            HeadlessHarness.Assert(game.RoomBeingLoaded == "Cellar", "The room being read was not recorded.");
            WaitFor(() => RuntimeModelStore.PrefetchesPending == 0, "Models being read ahead never finished.");
            HeadlessHarness.Assert(game.TryConsumePendingRoom(out string room) && room == "Cellar",
                "The room change did not happen once nothing was still being read.");
            HeadlessHarness.Assert(!game.TryConsumePendingRoom(out _) && game.RoomBeingLoaded.Length == 0 && game.RoomLoadProgress == 1f,
                "The room change was offered twice.");

            // An ordinary RoomGoto outranks a room that is still being read.
            game.ChangeRoomWhenLoaded("Cellar");
            game.ChangeRoom("Attic");
            HeadlessHarness.Assert(game.TryConsumePendingRoom(out room) && room == "Attic" && !game.TryConsumePendingRoom(out _),
                $"RoomGoto during a background load should win; the game changed to '{room}'.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rendering.Lights.AddedDuringAnUpdateReachTheNextFrame", () =>
        {
            using var scene = new RuntimeScene("Update lights");
            RoomAsset room = RoomAsset.Create("Hall", RoomDimension.ThreeD);
            var game = new Genesis.Runtime.Project.ProjectGameContext(context.Workspace, scene, null, null, room, null);
            bool submitting = Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit;
            var oldGame = PgslCommands.ActiveGameContext;
            // A script's commands run with a context bound; outside a Draw event it has no surface.
            Genesis.Shared.Scripting.PgslContext oldContext = PgslCommands.BindContext(new Genesis.Shared.Scripting.PgslContext());
            try
            {
                Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = false;
                Vector3 white = Vector3.One;

                // Two fixed steps in one frame: the torch is drawn once, not twice.
                game.BeginFixedStep();
                game.AddPointLight(new Vector3(0f, 2f, 0f), white, 6f);
                game.BeginFixedStep();
                game.AddPointLight(new Vector3(0f, 2f, 0f), white, 6f);
                HeadlessHarness.Assert(game.PendingUpdateLights == 1, $"Two steps left {game.PendingUpdateLights} copies of one light.");

                game.BeginVariableUpdate();
                game.AddPointLight(new Vector3(4f, 2f, 0f), white, 6f);
                HeadlessHarness.Assert(game.SubmitUpdateLights() == 2, "The frame did not receive the step's light and the update's light.");
                // A second camera drawn in the same frame, or a frame drawn while paused, is lit the same.
                HeadlessHarness.Assert(game.SubmitUpdateLights() == 2, "Only the first view drawn after an update was lit.");

                // A frame with no step in it still draws the last step's light; the update's is gone.
                game.BeginVariableUpdate();
                HeadlessHarness.Assert(game.SubmitUpdateLights() == 1, "A frame between two steps lost the step's light and would flicker.");

                // The object stops adding its light: the next step removes it.
                game.BeginFixedStep();
                game.BeginVariableUpdate();
                HeadlessHarness.Assert(game.SubmitUpdateLights() == 0, "A light nobody added any more was still drawn.");

                // The script command, called where there is no frame to draw into.
                PgslCommands.ActiveGameContext = game;
                game.BeginFixedStep();
                PgslCommands.DrawPointLight3D(1, 2, 3, 5, 1.5, 255, 180, 90);
                PgslCommands.DrawPointLightFalloff3D(1, 2, 3, 5, 1.5, 3, 255, 180, 90);
                HeadlessHarness.Assert(game.PendingUpdateLights == 2, "DrawPointLight3D from a Step event was ignored.");

                // Inside a frame a light goes straight to the renderer and is not kept.
                game.BeginFixedStep();
                Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = true;
                game.AddPointLight(Vector3.Zero, white, 3f);
                HeadlessHarness.Assert(game.PendingUpdateLights == 0, "A light added while drawing was also kept for the next frame.");

                // A new room starts with none of the last room's lights.
                Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = false;
                game.AddPointLight(Vector3.Zero, white, 3f);
                game.SetRoom(RoomAsset.Create("Cellar", RoomDimension.ThreeD));
                HeadlessHarness.Assert(game.PendingUpdateLights == 0, "A light from the last room followed the player into the next.");

                // A 2D room draws no 3D lights, so the command keeps none.
                game.SetRoom(RoomAsset.Create("Menu", RoomDimension.TwoD));
                PgslCommands.DrawPointLight3D(1, 2, 3, 5, 1.5, 255, 180, 90);
                HeadlessHarness.Assert(game.PendingUpdateLights == 0, "A 2D room kept a 3D light it can never draw.");
            }
            finally
            {
                Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = submitting;
                PgslCommands.ActiveGameContext = oldGame;
                PgslCommands.BindContext(oldContext);
            }
        });
    }

    private static void RunStreaming(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.World.Terrain.DistantTerrainsLoadAsTheCameraReachesThem", () =>
        {
            string parent = Path.Combine(context.Workspace, "TerrainStreamProjects");
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Lands" + Guid.NewGuid().ToString("N")[..6], "Blank");
            var resources = new ResourceService(project);
            string terrains = ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain);
            void MakeTerrain(string name, float height)
            {
                string resource = resources.CreateResource(terrains, ResourceKind.Terrain, name);
                var terrain = new TerrainAsset(65, 65, 4f, -128f, -128f, -10f, 200f);
                for (int z = 0; z < 65; z++)
                for (int x = 0; x < 65; x++)
                    terrain.SetHeight(x, z, height);
                terrain.Save(resource + ".gterrain");
            }

            MakeTerrain("West Land", 20f);
            MakeTerrain("East Land", 60f);
            ResourceNames.Invalidate(project.RootPath);

            RoomAsset room = RoomAsset.Create("Two Lands", RoomDimension.ThreeD);
            foreach ((string name, float x) in new[] { ("West Land", 0f), ("East Land", 5000f) })
                room.Nodes.Add(new RoomNode
                {
                    Name = name, Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id, EnabledIn2D = false,
                    Transform = new RoomTransform { X = x, ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
                    Terrain = new RoomTerrainData { Asset = name },
                });

            // Without a terrain distance every terrain loads with the room, as before.
            using (var everything = new Genesis.Runtime.Project.RoomTerrainSubsystem(project.RootPath, room, null!))
                HeadlessHarness.Assert(everything.StreamedTerrainCounts.Total == 0
                    && MathF.Abs(everything.SampleHeight(0f, 0f) - 20f) < 0.5f && MathF.Abs(everything.SampleHeight(5000f, 0f) - 60f) < 0.5f,
                    "A room with no terrain distance must load all its terrains at once.");

            room.Environment.TerrainDistance = 800f;
            using var scene = new RuntimeScene("Two lands");
            using var streamed = new Genesis.Runtime.Project.RoomTerrainSubsystem(project.RootPath, room, null!);
            HeadlessHarness.Assert(streamed.StreamedTerrainCounts == (2, 0), $"Both terrains should wait for the camera; got {streamed.StreamedTerrainCounts}.");
            var time = new GameTime();
            time.Advance(1f / 60f);
            scene.Camera3D.Position = new Vector3(0f, 50f, 0f);
            streamed.Update(scene, time);
            HeadlessHarness.Assert(streamed.StreamedTerrainCounts == (2, 1) && MathF.Abs(streamed.SampleHeight(0f, 0f) - 20f) < 0.5f,
                $"The terrain under the camera must be there on the first update; got {streamed.StreamedTerrainCounts}.");

            // Travel east. The far terrain is read in the background and joins; the first is let go.
            scene.Camera3D.Position = new Vector3(5000f, 90f, 0f);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 15 && MathF.Abs(streamed.SampleHeight(5000f, 0f) - 60f) > 0.5f)
            {
                streamed.Update(scene, time);
                Thread.Sleep(5);
            }

            HeadlessHarness.Assert(MathF.Abs(streamed.SampleHeight(5000f, 0f) - 60f) < 0.5f, "The eastern terrain never loaded after the camera reached it.");
            streamed.Update(scene, time);
            HeadlessHarness.Assert(streamed.StreamedTerrainCounts == (2, 1),
                $"Five kilometres on, only the eastern terrain should be loaded; got {streamed.StreamedTerrainCounts}.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Rendering.Camera.LongViewsMoveTheNearPlaneOut", () =>
        {
            var camera = new Genesis.Shared.Rendering.Camera { NearPlane = 0.1f, FarPlane = 2000f, AspectRatio = 16f / 9f };
            HeadlessHarness.Assert(camera.EffectiveNearPlane == 0.1f, "An ordinary view must keep its authored near plane.");
            float Near(Matrix4x4 projection) => MathF.Abs(projection.M43 / projection.M33);
            HeadlessHarness.Assert(MathF.Abs(Near(camera.ProjectionMatrix) - 0.1f) < 1e-3f, "The projection does not use the authored near plane.");
            camera.FarPlane = 16000f;
            HeadlessHarness.Assert(MathF.Abs(camera.EffectiveNearPlane - 16000f / 30000f) < 1e-4f
                && MathF.Abs(Near(camera.ProjectionMatrix) - camera.EffectiveNearPlane) < 1e-3f,
                $"A 16 km view should move the near plane to {16000f / 30000f:F2} m; it uses {camera.EffectiveNearPlane:F2} m.");
            camera.NearPlane = 2f;
            HeadlessHarness.Assert(camera.EffectiveNearPlane == 2f, "An authored near plane beyond the automatic one must be kept.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Terrain.Scatter.DialogEditsLayersWithoutTouchingTheOriginals", () =>
        {
            // A layer as a recipe makes it: its pattern number is the recipe's seed, a large number.
            var existing = new TerrainScatterLayer
            {
                Name = "Pines", Model = "Pine", DensityPerHectare = 85, PaintLayerMask = 1, CollisionRadius = 0.3f, CollisionHeight = 6f,
                Seed = 20261001,
            };
            using var dialog = new Genesis.Application.Editors.Suite.Terrain.TerrainScatterDialog(
                new[] { existing }, new[] { "Pine", "Oak" }, new[] { "Grass", "Rock", "Sand", "Snow" });
            HeadlessHarness.Assert(dialog.Layers.Count == 1 && dialog.Layers[0].Model == "Pine" && dialog.Layers[0].CollisionHeight == 6f,
                "The dialog did not open with the terrain's layers.");

            // Editing one field must leave the rest of the layer as it was: above all its pattern,
            // or renaming a forest would move every tree in it.
            dialog.Controls.Find("TerrainScatterLayerName", true)[0].Text = "Tall pines";
            TerrainScatterLayer renamed = dialog.Layers[0];
            HeadlessHarness.Assert(renamed.Name == "Tall pines" && renamed.Seed == 20261001 && renamed.PaintLayerMask == 1
                && renamed.DensityPerHectare == 85f && renamed.CollisionRadius == 0.3f,
                $"Renaming a layer changed it: pattern {renamed.Seed}, paint {renamed.PaintLayerMask}, density {renamed.DensityPerHectare}.");
            dialog.AddLayer();
            IReadOnlyList<TerrainScatterLayer> edited = dialog.Layers;
            HeadlessHarness.Assert(edited.Count == 2 && edited[1].Model == "Oak" && edited[1].Id != edited[0].Id && edited[1].Seed != edited[0].Seed,
                $"Adding a layer should offer the first model in the list with its own pattern; got '{edited[^1].Model}'.");
            HeadlessHarness.Assert(existing.Name == "Pines" && !ReferenceEquals(edited[0], existing), "The dialog edits the terrain's own layer objects; Cancel could not undo that.");
            var names = new List<string>();
            void Collect(System.Windows.Forms.Control parent)
            {
                foreach (System.Windows.Forms.Control child in parent.Controls)
                {
                    if (child is System.Windows.Forms.Label label && label.Text.Length > 0) names.Add(label.Text);
                    Collect(child);
                }
            }

            Collect(dialog);
            HeadlessHarness.Assert(names.Any(text => text.StartsWith("Copies per hectare", StringComparison.Ordinal))
                && names.Any(text => text.StartsWith("Solid trunk", StringComparison.Ordinal)),
                "The dialog is missing its density or collision fields.");
        });
    }

    /// <summary>A sphere with enough vertices and animation frames to be worth caching.</summary>
    private static GModelAsset Statue(float radius)
    {
        const int segments = 160, rings = 90;
        var vertices = new List<MeshVertex>();
        var indices = new List<ushort>();
        for (int ring = 0; ring <= rings; ring++)
        for (int segment = 0; segment <= segments; segment++)
        {
            float polar = MathF.PI * ring / rings, turn = MathF.Tau * segment / segments;
            Vector3 normal = new(MathF.Sin(polar) * MathF.Cos(turn), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(turn));
            vertices.Add(new MeshVertex
            {
                Position = normal * radius, Normal = normal, Color = new Vector4(0.8f, 0.7f, 0.6f, 1f),
                UV = new Vector2(segment / (float)segments, ring / (float)rings),
            });
        }

        for (int ring = 0; ring < rings; ring++)
        for (int segment = 0; segment < segments; segment++)
        {
            int a = ring * (segments + 1) + segment, b = a + 1, c = a + segments + 1, d = c + 1;
            indices.AddRange(new[] { (ushort)a, (ushort)b, (ushort)c, (ushort)b, (ushort)d, (ushort)c });
        }

        var asset = new GModelAsset { Name = "Statue" };
        asset.Materials.Add(new GModelMaterial { Name = "Stone", BaseColor = new Vector4(0.5f, 0.5f, 0.55f, 1f) });
        asset.Meshes.Add(new GModelMesh { Name = "Body", Vertices = vertices.ToArray(), Indices = indices.ToArray() });
        var clip = new GModelAnimationClip { Name = "Sway", Fps = 30f };
        for (int frame = 0; frame < 60; frame++)
        {
            var matrices = new Matrix4x4[24];
            for (int bone = 0; bone < matrices.Length; bone++)
                matrices[bone] = Matrix4x4.CreateRotationY(frame * 0.01f + bone) * Matrix4x4.CreateTranslation(bone, frame * 0.1f, 0f);
            clip.Frames.Add(new GModelAnimationFrame { LocalBoneTransforms = matrices });
        }

        asset.Animations.Add(clip);
        asset.RecalculateBounds();
        return asset;
    }

    private static void AssertSame(GModelAsset expected, GModelAsset actual)
    {
        HeadlessHarness.Assert(actual.Name == expected.Name && actual.Meshes.Count == expected.Meshes.Count
            && actual.Materials.Count == expected.Materials.Count && actual.Materials[0].Name == expected.Materials[0].Name
            && actual.Materials[0].BaseColor == expected.Materials[0].BaseColor,
            "The cached model's name, meshes or materials differ from the parsed model's.");
        GModelMesh a = expected.Meshes[0], b = actual.Meshes[0];
        HeadlessHarness.Assert(a.Vertices.Length == b.Vertices.Length && a.Indices.AsSpan().SequenceEqual(b.Indices),
            "The cached mesh has different vertices or indices.");
        for (int i = 0; i < a.Vertices.Length; i += 97)
            HeadlessHarness.Assert(a.Vertices[i].Position == b.Vertices[i].Position && a.Vertices[i].Normal == b.Vertices[i].Normal
                && a.Vertices[i].Color == b.Vertices[i].Color && a.Vertices[i].UV == b.Vertices[i].UV,
                $"Vertex {i} differs between the cached and the parsed model.");
        HeadlessHarness.Assert(expected.Bounds.Min == actual.Bounds.Min && expected.Bounds.Max == actual.Bounds.Max,
            "The cached model's bounds differ.");
        GModelAnimationClip clipA = expected.Animations[0], clipB = actual.Animations[0];
        HeadlessHarness.Assert(clipA.Name == clipB.Name && clipA.Fps == clipB.Fps && clipA.Frames.Count == clipB.Frames.Count,
            "The cached animation differs.");
        for (int frame = 0; frame < clipA.Frames.Count; frame += 7)
            HeadlessHarness.Assert(clipA.Frames[frame].LocalBoneTransforms.AsSpan().SequenceEqual(clipB.Frames[frame].LocalBoneTransforms),
                $"Animation frame {frame} differs between the cached and the parsed model.");
    }

    private static void WaitFor(Func<bool> condition, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed.TotalSeconds < 15) Thread.Sleep(20);
        HeadlessHarness.Assert(condition(), failure);
    }
}

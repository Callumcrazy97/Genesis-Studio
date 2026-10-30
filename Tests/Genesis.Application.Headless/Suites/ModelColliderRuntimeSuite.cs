using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Rendering.Core;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class ModelColliderRuntimeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.Colliders.SavedRoundShapesUseScaledDimensionsAndPivot", () => RoundShapes(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.Colliders.SavedHullAndMeshUseRealMirroredRampGeometry", () => Geometry(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.Transform.SavedYawMatchesPhysicsWithoutRoll", () => Orientation(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Object.Rendering.SavedControllersStayInvisibleAndProceduralDrawsRemain", () => Controllers(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.WarmUp.ObjectModelsReadBeforeTheFirstInstance", () => WarmUp(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.Colliders.StaticPhysicsKeepsAPlacedModelDrawnAndSolid", () => StaticPhysics(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.Colliders.StaticPhysicsThatCannotMakeABodyIsReported", () => StaticPhysicsReports(ctx));
    }

    /// <summary>
    /// A placed Model whose Object has Physics "Static" (what an automation caller sets on the Physics
    /// component) stays where it is, keeps producing its draw calls, and a body dropped on it comes to
    /// rest on it. "Static" was not a name the engine knew: it fell through to a dynamic box that dropped
    /// away from under the drawn Model, so the Model vanished with no message.
    /// </summary>
    private static void StaticPhysics(HeadlessContext ctx)
    {
        string suffix = Guid.NewGuid().ToString("N")[..6];
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "StaticPhysics" + suffix), "Static physics " + suffix);
        ResourceService resources = new(project);
        string root = project.RootPath;
        // A deck with no collider of its own (its triangles are its collider), and one whose own collider
        // is a Dynamic box (Static outranks it, the box stays the shape).
        string plain = "Pier deck " + suffix, boxed = "Boxed deck " + suffix;
        GModelAsset deck = GModelPrimitiveFactory.CreateCube(plain, 2);
        AddModel(root, plain, deck);
        GModelAsset withBox = GModelPrimitiveFactory.CreateCube(boxed, 2);
        withBox.Colliders.Add(GModelProductionTools.FitCollider(withBox, GModelColliderShape.Box));
        withBox.Colliders[0].Motion = GModelColliderMotion.Dynamic;
        AddModel(root, boxed, withBox);
        string crate = AddObject(root, resources, "Crate " + suffix, null, "RigidBody");
        RoomAsset room = RoomAsset.Create("Pier", RoomDimension.ThreeD);
        void Place(string id, string prefab, float x, float y, Vector3 scale) => room.Nodes.Add(new RoomNode
        {
            Id = id, Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = prefab },
            Transform = new RoomTransform { X = x, Y = y, ScaleX = scale.X, ScaleY = scale.Y, ScaleZ = scale.Z },
        });
        Vector3 flat = new(6, .5f, 6);
        Place("plain", AddObject(root, resources, "Plain pier " + suffix, plain, "Static"), 0, 0, flat);
        Place("boxed", AddObject(root, resources, "Boxed pier " + suffix, boxed, "static"), 40, 0, flat);
        Place("plainCrate", crate, 0, 4, Vector3.One);
        Place("boxedCrate", crate, 40, 4, Vector3.One);
        // The decks' top face: half the 2 m cube, scaled, above where each is placed.
        float top = (deck.Bounds.Max.Y - (deck.Pivot?.Position.Y ?? 0)) * flat.Y;

        try
        {
            using RuntimeScene scene = new();
            RoomBuildResult built = new RoomSceneBuilder(root).Build(scene, room);
            scene.Physics.EnableThreadDispatcher = false;
            Genesis.Shared.ECS.Entity plainDeck = built.EntitiesByNodeId["plain"], boxedDeck = built.EntitiesByNodeId["boxed"];
            RigidBodyComponent plainBody = scene.World.GetRef<RigidBodyComponent>(plainDeck), boxedBody = scene.World.GetRef<RigidBodyComponent>(boxedDeck);
            Assert(plainBody.Motion == PhysicsMotionType.Static && plainBody.Shape == CollisionShape.Mesh && !plainBody.UseGravity
                && scene.World.Has<MeshColliderComponent>(plainDeck),
                $"Physics \"Static\" on a Model with no collider of its own did not give it a fixed mesh body ({plainBody.Motion}, {plainBody.Shape}).");
            Assert(boxedBody.Motion == PhysicsMotionType.Static && boxedBody.Shape == CollisionShape.Box && !boxedBody.UseGravity
                && !scene.World.Has<MeshColliderComponent>(boxedDeck),
                $"Physics \"Static\" did not fix the Model's own Dynamic box collider ({boxedBody.Motion}, {boxedBody.Shape}).");

            // The Models are still drawn, where they were placed, before and after the physics has run.
            using Form host = UnattendedWindowing.NewHost(320, 240); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software); renderer.Initialize(host.Handle, 320, 240);
            MeshDrawCall[] draws = new MeshDrawCall[32];
            Vector3[] DrawnAt()
            {
                int count = 0;
                ObjectDrawPass.SubmitMeshes3D(scene.World, root, draws, ref count, renderer, new(20, 8, 40), -Vector3.UnitZ);
                return draws.Take(count).Select(call => new Vector3(call.World.M41, call.World.M42, call.World.M43)).OrderBy(point => point.X).ToArray();
            }
            Vector3[] before = DrawnAt();
            Assert(before.Length == 2, $"The two placed Models should each make a draw call before the physics runs, not {before.Length}.");
            for (int step = 0; step < 180; step++) scene.UpdateFixed(1f / 60);
            Vector3[] after = DrawnAt();
            Assert(after.Length == 2 && after.Zip(before).All(pair => Vector3.Distance(pair.First, pair.Second) < .001f),
                "A Static Model stopped drawing or moved after the physics ran: " + string.Join(" -> ", before) + " / " + string.Join(" -> ", after));
            foreach (Genesis.Shared.ECS.Entity entity in new[] { plainDeck, boxedDeck })
                Assert(Math.Abs(scene.World.GetRef<Transform3DComponent>(entity).Position.Y) < .0001f, "A Static Model fell.");

            // A body dropped on each comes to rest on its top face and stays there.
            foreach ((string id, float x) in new[] { ("plainCrate", 0f), ("boxedCrate", 40f) })
            {
                float resting = scene.World.GetRef<Transform3DComponent>(built.EntitiesByNodeId[id]).Position.Y;
                Assert(Math.Abs(resting - (top + .5f)) < .06f,
                    $"The crate dropped on the Static {id[..5]} deck did not come to rest on it: y {resting}, the deck's top is {top}.");
                for (int step = 0; step < 30; step++) scene.UpdateFixed(1f / 60);
                Assert(Math.Abs(scene.World.GetRef<Transform3DComponent>(built.EntitiesByNodeId[id]).Position.Y - resting) < .01f,
                    "The crate on the Static deck did not stay at rest.");
                Assert(scene.Physics.Raycast(scene.World, new Vector3(x + 3, 20, 3), -Vector3.UnitY, 50, out var hit)
                    && hit.Entity == (id == "plainCrate" ? plainDeck : boxedDeck) && Math.Abs(hit.Point.Y - top) < .02f,
                    "The Static Model's collider does not match its top face.");
            }
        }
        finally { ObjectDrawAssetRegistry.Clear(); }
    }

    /// <summary>
    /// A Static body its Model cannot make (every part left out of the collider, a Model whose file is
    /// missing) and a Physics preset that is not one are each reported, once, with no collider made.
    /// </summary>
    private static void StaticPhysicsReports(HeadlessContext ctx)
    {
        string suffix = Guid.NewGuid().ToString("N")[..6];
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "StaticReports" + suffix), "Static reports " + suffix);
        ResourceService resources = new(project);
        string root = project.RootPath;
        string glass = "Glass pier " + suffix, missing = "Vanished pier " + suffix, preset = "Hovering" + suffix;
        GModelAsset glassModel = GModelPrimitiveFactory.CreateCube(glass, 2);
        foreach (GModelMesh mesh in glassModel.Meshes) mesh.Name = "NOCOL_" + mesh.Name;
        AddModel(root, glass, glassModel);
        RoomAsset room = RoomAsset.Create("Reports", RoomDimension.ThreeD);
        void Place(string id, string prefab, float x) => room.Nodes.Add(new RoomNode
        {
            Id = id, Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = prefab },
            Transform = new RoomTransform { X = x, ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
        });
        Place("glass", AddObject(root, resources, "Glass object " + suffix, glass, "Static"), 0);
        Place("glass2", room.Nodes[0].GameObject.Prefab, 10);
        Place("missing", AddObject(root, resources, "Missing object " + suffix, missing, "Static"), 20);
        Place("unknown", AddObject(root, resources, "Unknown object " + suffix, null, preset), 30);

        try
        {
            using RuntimeScene scene = new();
            RoomBuildResult built = new RoomSceneBuilder(root).Build(scene, room);
            foreach (string id in new[] { "glass", "glass2", "missing" })
            {
                var entity = built.EntitiesByNodeId[id];
                Assert(!scene.World.Has<RigidBodyComponent>(entity) && !scene.World.Has<MeshColliderComponent>(entity),
                    $"The Static Object '{id}' got a body although its Model cannot make one.");
            }
            string[] warnings = ModelColliderBinding.BodyWarnings.ToArray();
            int Said(string text) => warnings.Count(warning => warning.Contains(text, StringComparison.Ordinal));
            Assert(Said($"'{glass}'") == 1 && warnings.Single(warning => warning.Contains($"'{glass}'", StringComparison.Ordinal)).Contains("NOCOL_", StringComparison.Ordinal),
                $"Two Static Objects whose Model has no part that collides should be reported once, naming the Model: {string.Join(" | ", warnings)}");
            Assert(Said($"'{missing}'") == 1, $"A Static Object whose Model file is missing should be reported once: {string.Join(" | ", warnings)}");
            Assert(Said($"'{preset}'") == 1, $"A Physics preset that is not one should be reported once: {string.Join(" | ", warnings)}");
        }
        finally { ObjectDrawAssetRegistry.Clear(); }
    }

    private static void AddModel(string root, string name, GModelAsset asset)
    {
        string file = Path.Combine(root, "Assets", "Models", name + ".model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");
        StudioModelResourceLoader.SaveCanonical(file, asset);
        ResourceCatalog.Invalidate(root);
    }

    /// <summary>A 3D Object made the way the Object editor and automation make one: a Model component (when it has a Model) and a Physics component with the preset.</summary>
    private static string AddObject(string root, ResourceService resources, string name, string? model, string preset)
    {
        JObject document = new() { ["schemaVersion"] = 2, ["dimension"] = "ThreeD", ["components"] = new JArray() };
        var composition = new Genesis.Application.Core.Objects.ObjectCompositionModel(document);
        if (model != null) composition.SetProperty("ModelRendererComponent", "ModelAsset", model);
        composition.SetProperty("PhysicsComponent", "Preset", preset);
        string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, name);
        File.WriteAllText(path, document.ToString());
        return ResourceNames.Name(root, path, ResourceType.Object);
    }

    /// <summary>
    /// The Player reads the models of the project's Objects on a worker while the game starts, so
    /// the first instance a script creates (which fits its collider from the model) reads nothing.
    /// </summary>
    private static void WarmUp(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "WarmUp");
        GModelAsset asset = GModelPrimitiveFactory.CreateCube("Warmed object", 2);
        asset.Colliders.Add(GModelProductionTools.FitCollider(asset, GModelColliderShape.Box));
        fixture.Save(asset);
        ResourceCatalog.Invalidate(fixture.Root);
        RuntimeModelAssetRegistry.Shared.Invalidate(fixture.Root, "Collider model");
        Assert(ProjectModelWarmup.ObjectModels(fixture.Root).Contains("Collider model"),
            "The Object's model is not among the models the warm-up reads.");

        Assert(ProjectModelWarmup.WaitUntilDone(TimeSpan.FromSeconds(60)), "An earlier model warm-up is still running.");
        ProjectModelWarmup.ResetForTests();
        var lines = new List<string>();
        ProjectModelWarmup.ReportTo(line => { lock (lines) lines.Add(line); });
        // The Player says so once its first room is on screen; the warm-up waits for it.
        ProjectModelWarmup.FirstRoomShown();
        ProjectModelWarmup.Start(fixture.Root);
        Assert(ProjectModelWarmup.WaitUntilDone(TimeSpan.FromSeconds(60)), "The model warm-up did not finish within a minute.");
        lock (lines)
        {
            File.WriteAllLines(Path.Combine(ctx.Logs, "model-warmup.txt"), lines);
            Assert(ProjectModelWarmup.Read >= 1 && lines.Count == 1 && lines[0].StartsWith("object models: ", StringComparison.Ordinal),
                $"The warm-up read {ProjectModelWarmup.Read} models: " + string.Join(" | ", lines));
        }

        // Building the room (which fits the Object's collider from its model) reads no model file.
        LoadClock.UseEveryThread();
        int reads = LoadClock.Count(LoadWork.ModelRead);
        using RuntimeScene scene = new();
        var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).SpawnedEntities.Single();
        Assert(LoadClock.Count(LoadWork.ModelRead) == reads, "Placing the Object read its model although the warm-up had read it.");
        Assert(scene.World.Has<RigidBodyComponent>(entity), "The warmed model's collider was not fitted to the Object.");
        ProjectModelWarmup.ResetForTests();
    }

    private static void Controllers(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Controllers");
        GModelAsset asset = GModelPrimitiveFactory.CreateCube("Visible object", 2);
        asset.Colliders.Add(GModelProductionTools.FitCollider(asset, GModelColliderShape.Box)); fixture.Save(asset);
        string controller = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.GameObject, "Wind controller");
        File.WriteAllText(controller, new JObject { ["dimension"] = "ThreeD", ["sprite"] = "", ["model"] = "", ["components"] = new JArray() }.ToString());
        fixture.Room.Nodes.Add(new RoomNode { Id = "controller", Kind = RoomNodeKind.GameObject, LayerId = fixture.Room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = ResourceNames.Name(fixture.Root, controller, ResourceType.Object) } });
        using RuntimeScene scene = new(); var built = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room);
        var helper = built.EntitiesByNodeId["controller"];
        using Form host = UnattendedWindowing.NewHost(320, 240); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software); renderer.Initialize(host.Handle, 320, 240);
        MeshDrawCall[] draws = new MeshDrawCall[16]; int count = 0;
        ObjectDrawPass.SubmitMeshes3D(scene.World, fixture.Root, draws, ref count, renderer, new(0, 0, 10), -Vector3.UnitZ);
        Assert(count == 1 && draws[0].Mesh.IsValid, "A saved nonvisual controller added placeholder geometry or hid the authored model.");
        ModelRenderQueue queue = new();
        ObjectDrawPass.EnqueueEntity3D(scene.World, helper, fixture.Root, renderer, new(0, 0, 10), -Vector3.UnitZ, queue);
        Assert(queue.Count == 0, "Explicit DrawSelf rendered a cube for the saved controller.");
        ref var drawing = ref scene.World.GetRef<Genesis.Runtime.ECS.Components.Draw3DComponent>(helper);
        drawing.Procedural = true;
        ProceduralMeshDrawRegistry.Set(helper, [draws[0]]);
        try
        {
            count = 0;
            ObjectDrawPass.SubmitMeshes3D(scene.World, fixture.Root, draws, ref count, renderer, new(0, 0, 10), -Vector3.UnitZ);
            Assert(count == 2, "The invisible controller could not submit its explicitly generated mesh.");
            ObjectDrawPass.EnqueueEntity3D(scene.World, helper, fixture.Root, renderer, new(0, 0, 10), -Vector3.UnitZ, queue);
            Assert(queue.Count == 1, "Explicit DrawSelf lost the controller's procedural geometry.");
        }
        finally { ProceduralMeshDrawRegistry.Remove(helper); }
    }

    private static void Orientation(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Orientation");
        GModelAsset asset = GModelPrimitiveFactory.CreateCube("Orientation collider", 2);
        asset.Colliders.Add(GModelProductionTools.FitCollider(asset, GModelColliderShape.Box));
        fixture.Save(asset);
        foreach (Vector3 angles in new[] { new Vector3(0, 70, 0), new Vector3(24, 70, 0), new Vector3(24, 70, -16) })
        {
            fixture.Room.Nodes[0].Transform.RotationX = angles.X;
            fixture.Room.Nodes[0].Transform.RotationY = angles.Y;
            fixture.Room.Nodes[0].Transform.RotationZ = angles.Z;
            using RuntimeScene scene = new();
            var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).SpawnedEntities.Single();
            scene.Physics.GravityStrength = 0; scene.UpdateFixed(1f / 60);
            var transform = scene.World.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
            var model = scene.World.GetRef<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity);
            var physics = scene.World.GetRef<Transform3DComponent>(entity);
            Matrix4x4 rendered = RuntimeModelRenderSystem.TransformMatrix(transform, model);
            foreach (Vector3 axis in new[] { Vector3.UnitY, Vector3.UnitZ })
                Assert(Vector3.Distance(Vector3.Normalize(Vector3.TransformNormal(axis, rendered)),
                    Vector3.Transform(axis, physics.Rotation)) < .0001f,
                    "The saved model's rendered orientation disagreed with its native collider: " + angles + "/" + axis);
            if (angles.X == 0 && angles.Z == 0)
                Assert(Vector3.Distance(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rendered)), Vector3.UnitY) < .0001f,
                    "A yaw-only saved model leaned sideways.");
        }
        var legacy = new Genesis.Runtime.ECS.Components.TransformComponent { Rotation = 90, ScaleX = 1, ScaleY = 1, ScaleZ = 1 };
        var defaultModel = new Genesis.Runtime.ECS.Components.ModelRendererComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 };
        Assert(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, RuntimeModelRenderSystem.TransformMatrix(legacy, defaultModel)), Vector3.UnitY) < .0001f,
            "Angle-only legacy model transforms lost their authored roll.");
    }

    private static void RoundShapes(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Round");
        foreach (GModelColliderShape shape in new[] { GModelColliderShape.Sphere, GModelColliderShape.Capsule, GModelColliderShape.Cylinder })
        {
            GModelAsset asset = GModelPrimitiveFactory.CreateCube("Round collider", 2);
            asset.Pivot.Position = new(.25f, -.2f, .1f);
            asset.Colliders.Add(GModelProductionTools.FitCollider(asset, shape)); fixture.Save(asset);
            using RuntimeScene scene = new(); var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).SpawnedEntities.Single();
            scene.UpdateFixed(1f / 60);
            var transform = scene.World.GetRef<Transform3DComponent>(entity);
            var body = scene.World.GetRef<RigidBodyComponent>(entity);
            float radius = shape == GModelColliderShape.Sphere ? 6 : 4.5f;
            Assert(Math.Abs(body.Size.X - radius) < .001f && (shape == GModelColliderShape.Sphere || Math.Abs(body.Size.Y - 6) < .001f),
                "The saved round collider used only X scale or treated its cap as a capsule: " + shape);
            Vector3 center = transform.Position + Vector3.Transform(body.LocalOffset, transform.Rotation);
            float x = shape == GModelColliderShape.Sphere ? 5.5f : 4.4f;
            float top = shape == GModelColliderShape.Cylinder ? 6 : (shape == GModelColliderShape.Capsule ? 6 - radius : 0) + MathF.Sqrt(radius * radius - x * x);
            Vector3 expected = center + Vector3.Transform(new Vector3(x, top, 0), transform.Rotation);
            Assert(scene.Physics.Raycast(scene.World, expected + Vector3.UnitY * 30, -Vector3.UnitY, 50, out var hit)
                && hit.Entity == entity && Vector3.Distance(hit.Point, expected) < .015f,
                "Native raycast did not match the saved " + shape + " shape and pivot.");
            Assert(Vector3.Distance(scene.Physics.GetBodyPosition(body.RegistrationId), transform.Position) < .001f,
                "The collider's center offset moved the authored model origin.");
        }
    }

    private static void Geometry(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Geometry");
        foreach (GModelColliderShape shape in new[] { GModelColliderShape.ConvexHull, GModelColliderShape.Mesh })
        foreach (GModelColliderMotion motion in new[] { GModelColliderMotion.Static, GModelColliderMotion.Dynamic })
        {
            Vector3[] positions = [new(0, 0, -1), new(2, 0, -1), new(2, 2, -1), new(0, 0, 1), new(2, 0, 1), new(2, 2, 1)];
            GModelAsset asset = new() { Name = "Ramp collider", Pivot = new() { Position = new(.25f, .1f, .2f) } };
            asset.Meshes.Add(new GModelMesh { Vertices = positions.Select(position => new MeshVertex { Position = position, Normal = Vector3.UnitY }).ToArray(),
                Indices = [0, 2, 1, 3, 4, 5, 0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4, 0, 3, 5, 0, 5, 2] });
            asset.RecalculateBounds(); var collider = GModelProductionTools.FitCollider(asset, shape); collider.Motion = motion; collider.Mass = 10; asset.Colliders.Add(collider);
            fixture.Save(asset);
            using RuntimeScene scene = new();
            var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).SpawnedEntities.Single();
            scene.Physics.GravityStrength = 0; scene.UpdateFixed(1f / 60);
            var transform = scene.World.GetRef<Transform3DComponent>(entity);
            var body = scene.World.GetRef<RigidBodyComponent>(entity);
            Vector3 scale = new(-3, 6, 4.5f);
            Vector3 expected = transform.Position + Vector3.Transform((new Vector3(.5f, .5f, 0) - asset.Pivot.Position) * scale, transform.Rotation);
            Assert(scene.World.Has<MeshColliderComponent>(entity) && scene.Physics.Raycast(scene.World, expected + Vector3.UnitY * 30,
                -Vector3.UnitY, 50, out var hit) && hit.Entity == entity && Vector3.Distance(hit.Point, expected) < .02f,
                "The saved mirrored " + shape + " collider used bounding-box geometry or reversed its exterior winding: " + motion);
            Assert(Vector3.Distance(scene.Physics.GetBodyPosition(body.RegistrationId), transform.Position) < .002f,
                "Geometry inertia centering displaced the authored model origin: " + shape + "/" + motion);
            scene.World.DestroyEntity(entity); scene.World.FlushDeferred(); scene.UpdateFixed(1f / 60);
            Assert(!scene.Physics.Raycast(scene.World, expected + Vector3.UnitY * 30, -Vector3.UnitY, 50, out _), "Destroyed geometry retained a ghost collider.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _model;
        public Fixture(HeadlessContext ctx, string suffix)
        {
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ModelCollider" + suffix), "Saved model colliders");
            Root = project.RootPath; ResourceService resources = Resources = new(project);
            _model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Collider model");
            string obj = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Collider object");
            JObject definition = new() { ["dimension"] = "ThreeD", ["solid"] = true, ["components"] = new JArray(new JObject
            { ["type"] = "ModelRendererComponent", ["props"] = new JObject { ["ModelAsset"] = "Collider model", ["ScaleX"] = 1.5, ["ScaleY"] = 1.5, ["ScaleZ"] = 1.5 } }) };
            File.WriteAllText(obj, definition.ToString());
            Room = RoomAsset.Create("Colliders", RoomDimension.ThreeD);
            Room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.GameObject, LayerId = Room.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = ResourceNames.Name(Root, obj, ResourceType.Object) },
                Transform = new RoomTransform { X = 10, Y = 5, Z = 2, RotationY = 37, ScaleX = -2, ScaleY = 4, ScaleZ = 3 } });
        }
        public string Root { get; }
        public ResourceService Resources { get; }
        public RoomAsset Room { get; }
        public void Save(GModelAsset asset)
        {
            using ModelEditorControl editor = new(_model, Root); editor.ApplyAnimationWorkspace(asset, ""); editor.Save();
            using ModelViewerControl reopened = new(_model, Root);
            Assert(reopened.PreviewAsset.Colliders.Count == 1, "The Model Editor did not preserve its saved collider metadata.");
        }
        public void Dispose() => ObjectDrawAssetRegistry.Clear();
    }
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}

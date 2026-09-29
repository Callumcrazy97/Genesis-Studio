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

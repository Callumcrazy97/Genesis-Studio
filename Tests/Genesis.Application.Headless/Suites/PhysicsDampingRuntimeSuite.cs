using System.Numerics;
using System.Reflection;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Application.Headless.Suites;

internal static class PhysicsDampingRuntimeSuite
{
    private static readonly MethodInfo BodyReferenceMethod = typeof(PhysicsWorld).GetMethod("TryGetDynamicBodyReference", BindingFlags.Instance | BindingFlags.NonPublic)!;
    public static void Run(HeadlessContext ctx)
    {
        foreach (PhysicsDimension dimension in Enum.GetValues<PhysicsDimension>())
            HeadlessHarness.RunCase(ctx.Report, "Runtime.Physics.Damping.SavedObjects." + dimension, () =>
            {
                var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "Damping" + dimension), "Damping " + dimension);
                ResourceService resources = new(project);
                bool planar = dimension == PhysicsDimension.TwoD;
                string visual = resources.CreateResource(resources.AssetsRoot, planar ? ResourceKind.Image : ResourceKind.Model, "Body visual");
                if (!planar)
                {
                    using ModelEditorControl editor = new(visual, project.RootPath);
                    editor.ApplyAnimationWorkspace(GModelPrimitiveFactory.CreateCube("Body", 1), ""); editor.Save();
                }
                string damped = MakeObject("Damped", PhysicsBodyKind.Dynamic, 2, 3);
                string free = MakeObject("Free", PhysicsBodyKind.Dynamic, 0, 0);
                string kinematic = MakeObject("Kinematic", PhysicsBodyKind.Kinematic, 2, 3);
                foreach (int frames in new[] { 60, 120 })
                {
                    RoomAsset room = RoomAsset.Create("Damping Room", planar ? RoomDimension.TwoD : RoomDimension.ThreeD);
                    foreach (string objectPath in new[] { damped, free, kinematic })
                        room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                            GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) },
                            Transform = new() { X = room.Nodes.Count * 1000 } });
                    using RuntimeScene scene = new();
                    Entity[] entities = new RoomSceneBuilder(project.RootPath).Build(scene, room).SpawnedEntities.ToArray();
                    scene.Physics.GravityStrength = 0; scene.Physics.AirDrag = 0; scene.Physics.MaxVelocity = 0;
                    scene.Physics.AllowSleep = false; scene.Physics.EnableThreadDispatcher = false;
                    scene.UpdateFixed(1f / frames);
                    foreach (Entity entity in entities) SetVelocity(entity);
                    RigidBodyComponent saved = scene.World.GetRef<RigidBodyComponent>(entities[0]);
                    Check(saved.LinearDamping == 2 && saved.AngularDamping == 3, "The saved Physics resource lost its body damping.");
                    for (int frame = 0; frame < frames; frame++) scene.UpdateFixed(1f / frames);
                    Check(MathF.Abs(Velocity(entities[0]).Linear.X - 10 * MathF.Exp(-2)) < .005f
                        && MathF.Abs(Velocity(entities[0]).Angular.Z - 4 * MathF.Exp(-3)) < .005f,
                        "Saved damping did not decay actual linear and angular motion consistently at " + frames + " Hz.");
                    foreach (Entity entity in entities.Skip(1))
                        Check(MathF.Abs(Velocity(entity).Linear.X - 10) < .005f && MathF.Abs(Velocity(entity).Angular.Z - 4) < .005f,
                            "A body inherited another body's damping or Kinematic motion was damped.");
                    ref RigidBodyComponent live = ref scene.World.GetRef<RigidBodyComponent>(entities[0]);
                    live.LinearDamping = 0; live.AngularDamping = 0; SetVelocity(entities[0]);
                    for (int frame = 0; frame < frames / 4; frame++) scene.UpdateFixed(1f / frames);
                    Check(MathF.Abs(Velocity(entities[0]).Linear.X - 10) < .005f && MathF.Abs(Velocity(entities[0]).Angular.Z - 4) < .005f,
                        "Live body damping changes retained stale registration values.");
                    live.LinearDamping = float.NaN; live.AngularDamping = -5; SetVelocity(entities[0]); scene.UpdateFixed(1f / frames);
                    Check(MathF.Abs(Velocity(entities[0]).Linear.X - 10) < .005f && MathF.Abs(Velocity(entities[0]).Angular.Z - 4) < .005f,
                        "Invalid damping poisoned actual simulation velocity.");
                    BepuPhysics.BodyVelocity Velocity(Entity entity)
                    {
                        return Reference(entity).Velocity;
                    }
                    void SetVelocity(Entity entity)
                    {
                        var body = Reference(entity); body.Awake = true;
                        body.Velocity.Linear = new(10, 0, 0); body.Velocity.Angular = new(0, 0, 4);
                    }
                    BepuPhysics.BodyReference Reference(Entity entity)
                    {
                        // Inspect the real registered solver body without expanding the public engine API for a fixture.
                        object?[] arguments = [scene.World.GetRef<RigidBodyComponent>(entity).RegistrationId, null];
                        Check((bool)BodyReferenceMethod.Invoke(scene.Physics, arguments)!, "A body was not registered.");
                        return (BepuPhysics.BodyReference)arguments[1]!;
                    }
                }
                string MakeObject(string name, PhysicsBodyKind kind, float linear, float angular)
                {
                    string physics = resources.CreateResource(resources.AssetsRoot, ResourceKind.Physics, name + " Physics");
                    PhysicsSceneConfig asset = PhysicsScenePresets.Default(); asset.Dimension = dimension; asset.BodyType = kind;
                    asset.Shape = PhysicsBodyShape.Box; asset.GravityScale = 0; asset.LockRotation = false;
                    asset.LinearDamping = linear; asset.AngularDamping = angular;
                    using PhysicsEditorControl editor = new(physics, project.RootPath);
                    Check(editor.SetDefinition(PhysicsCodeCodec.Serialize(asset)), "Physics rejected damping declarations.");
                    Check(planar ? editor.ChooseSpriteImage(ResourceNames.Name(project.RootPath, visual))
                        : editor.ChooseModel(ResourceNames.Name(project.RootPath, visual)), "The linked visual was rejected.");
                    string objectPath = editor.CreatePhysicsObject(name + " Object");
                    PhysicsSceneConfig reopened = PhysicsSceneConfig.LoadFromFile(physics);
                    Check(reopened.LinearDamping == linear && reopened.AngularDamping == angular, "Damping did not save/reopen.");
                    return objectPath;
                }
            });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Physics.Damping.SandboxMatchesSavedBodyDecay", () =>
        {
            foreach (PhysicsDimension dimension in Enum.GetValues<PhysicsDimension>())
                foreach (int frames in new[] { 60, 120 })
                {
                    using SandboxPhysicsWorld sandbox = new(false, false)
                    { Dimension = dimension, Gravity = Vector3.Zero, MaxVelocity = 0, LinearDamping = 2, AngularDamping = 3 };
                    PhysicsBody body = sandbox.AddDynamicBox(Vector3.Zero, new(.5f), 1); sandbox.SetLinearVelocity(body, new(10, 0, 0));
                    for (int frame = 0; frame < frames; frame++) sandbox.Step(1f / frames);
                    Check(MathF.Abs(sandbox.GetLinearVelocity(body).X - 10 * MathF.Exp(-2)) < .005f,
                        "The sandbox disagreed with game damping at " + frames + " Hz in " + dimension);
                }
        });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}

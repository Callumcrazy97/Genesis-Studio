using System.Numerics;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Shared.ECS.Components;

namespace Genesis.Application.Headless.Suites;

internal static class CharacterMotorRuntimeSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Runtime.Physics.Character.SavedTerrainWaterSwimmingAndLanding",
            () => RuntimeGateChecks.TerrainWaterCharacter(context));
        HeadlessHarness.RunCase(context.Report, "Runtime.Physics.Character.BufferedTapWaitsForSurface", () =>
        {
            using RuntimeScene scene = new() { Physics = PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset()),
                Input = new Genesis.Runtime.Input.InputState() };
            scene.Physics.RegisterStaticBox(new Vector3(0, -.5f, 0), new Vector3(10, .5f, 10), "Landing floor");
            var actor = scene.CreateEntity(new Vector3(0, .78f, 0));
            scene.World.Set(actor, RigidBodyComponent.Character(.5f, .6f));
            scene.World.Set(actor, CharacterMotorComponent.Default);
            scene.World.Set(actor, new PhysicsPlayerTag());
            scene.Input.OnKeyDown(Genesis.Runtime.Input.Key.Space);
            scene.UpdateFixed(1f / 60);
            HeadlessHarness.Assert(scene.World.GetRef<CharacterMotorComponent>(actor).State == CharacterMotorState.Falling,
                "The buffered jump fired before a never-grounded character reached support.");
            scene.Input.NextFrame(); scene.Input.OnKeyUp(Genesis.Runtime.Input.Key.Space); scene.Input.NextFrame();
            float highest = .78f;
            for (int tick = 0; tick < 24; tick++)
            {
                scene.UpdateFixed(1f / 60);
                highest = Math.Max(highest, scene.World.GetRef<Transform3DComponent>(actor).Position.Y);
            }
            HeadlessHarness.Assert(highest > 1.3f, "A short input tap just before landing was discarded.");
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.Physics.Character.GroundProbeHandlesSlopePenetrationAndOffset", () =>
        {
            using RuntimeScene scene = new() { Physics = PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset()) };
            scene.Physics.RegisterStaticTriangleMesh(
                [new(-10, -5, -10), new(10, -5, -10), new(-10, 5, 10), new(10, 5, 10)],
                [0, 1, 2, 1, 3, 2], Vector3.One, Vector3.Zero, Quaternion.Identity, "Walkable slope");
            foreach (Vector3 offset in new[] { Vector3.Zero, new Vector3(0, 1.2f, 0) })
            {
                var actor = scene.CreateEntity(new Vector3(0, .5f, 0) - offset);
                var authored = RigidBodyComponent.Character(.5f, .6f);
                authored.LocalOffset = offset;
                scene.World.Set(actor, authored);
                ref var body = ref scene.World.GetRef<RigidBodyComponent>(actor);
                ref var transform = ref scene.World.GetRef<Transform3DComponent>(actor);
                scene.Physics.RegisterEntity(scene.World, actor, ref body, ref transform);
                HeadlessHarness.Assert(scene.Physics.TryGetGroundContact(scene.World, body.RegistrationId, .2f, out var hit)
                    && hit.Normal.Y > .85f && Math.Abs(hit.Point.Y) < .002f,
                    "The probe began below one-sided terrain or ignored the collider's local offset: " + offset);
                scene.World.DestroyEntity(actor); scene.World.FlushDeferred(); scene.UpdateFixed(1f / 60);
            }
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.Physics.Character.MotorDescendsAndJumpsOncePerInput", () =>
        {
            using RuntimeScene scene = new() { Physics = PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset()),
                Input = new Genesis.Runtime.Input.InputState() };
            scene.Physics.RegisterStaticTriangleMesh(
                [new(-10, 10, -20), new(10, 10, -20), new(-10, -10, 20), new(10, -10, 20)],
                [0, 1, 2, 1, 3, 2], Vector3.One, Vector3.Zero, Quaternion.Identity, "Descending trail");
            var actor = scene.CreateEntity(new Vector3(0, .75f, 0));
            scene.World.Set(actor, RigidBodyComponent.Character(.5f, .6f));
            scene.World.Set(actor, CharacterMotorComponent.Default);
            scene.World.Set(actor, new PhysicsPlayerTag());
            scene.Camera3D.Yaw = MathF.PI; scene.Camera3D.Pitch = 0;
            for (int tick = 0; tick < 60; tick++) scene.UpdateFixed(1f / 60);
            scene.Input.OnKeyDown(Genesis.Runtime.Input.Key.Up);
            float maxGap = 0;
            for (int tick = 0; tick < 120; tick++)
            {
                scene.UpdateFixed(1f / 60);
                Vector3 position = scene.World.GetRef<Transform3DComponent>(actor).Position;
                maxGap = Math.Max(maxGap, position.Y + position.Z * .5f);
                scene.Input.NextFrame();
            }
            Vector3 end = scene.World.GetRef<Transform3DComponent>(actor).Position;
            HeadlessHarness.Assert(end.Z > 8 && maxGap < .9f
                && scene.World.GetRef<CharacterMotorComponent>(actor).State == CharacterMotorState.Grounded,
                $"The motor floated off a walkable descending trail (end={end}, max centre gap={maxGap}).");
            scene.Input.OnKeyUp(Genesis.Runtime.Input.Key.Up); scene.Input.NextFrame();
            scene.Input.OnKeyDown(Genesis.Runtime.Input.Key.Down);
            for (int tick = 0; tick < 120; tick++)
            {
                scene.UpdateFixed(1f / 60);
                Vector3 position = scene.World.GetRef<Transform3DComponent>(actor).Position;
                maxGap = Math.Max(maxGap, position.Y + position.Z * .5f);
                scene.Input.NextFrame();
            }
            Vector3 returned = scene.World.GetRef<Transform3DComponent>(actor).Position;
            HeadlessHarness.Assert(returned.Z < end.Z - 8 && maxGap < .9f
                && scene.World.GetRef<CharacterMotorComponent>(actor).State == CharacterMotorState.Grounded,
                $"Uphill traversal hovered above its look-ahead point (end={returned}, max centre gap={maxGap}).");
            scene.Input.OnKeyUp(Genesis.Runtime.Input.Key.Down); scene.Input.NextFrame();
            for (int tick = 0; tick < 30; tick++) scene.UpdateFixed(1f / 60);
            scene.Input.OnKeyDown(Genesis.Runtime.Input.Key.Space);
            scene.UpdateFixed(1f / 60);
            int registration = scene.World.GetRef<RigidBodyComponent>(actor).RegistrationId;
            float firstVelocity = scene.Physics.GetLinearVelocity(registration).Y;
            scene.UpdateFixed(1f / 60); // The same render frame may perform several physics ticks.
            float secondVelocity = scene.Physics.GetLinearVelocity(registration).Y;
            HeadlessHarness.Assert(firstVelocity > 5 && secondVelocity < firstVelocity - .1f
                && scene.World.GetRef<CharacterMotorComponent>(actor).State == CharacterMotorState.Falling,
                "One pressed input repeated its jump impulse across fixed ticks or reported a rising character as grounded.");
            scene.Input.OnKeyUp(Genesis.Runtime.Input.Key.Space); scene.Input.NextFrame();
            for (int tick = 0; tick < 180; tick++) scene.UpdateFixed(1f / 60);
            Vector3 landed = scene.World.GetRef<Transform3DComponent>(actor).Position;
            HeadlessHarness.Assert(scene.World.GetRef<CharacterMotorComponent>(actor).State == CharacterMotorState.Grounded
                && landed.Y + landed.Z * .5f < .78f,
                "The character reported a landing while suspended above the slope: " + landed);
        });
    }
}

#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Engine.Physics as a whole: the world's settings and the current instance's body as properties,
/// read and set like variables (<c>Engine.Physics.Mass = 4;</c>, <c>Engine.Physics.VelocityY += 3;</c>).
/// A body's property is the current instance's: its own in its events, each target's inside
/// <c>with</c>, so <c>with (Crate) { Engine.Physics.Bounciness = 0.8; }</c> makes every crate bouncy.
/// An instance with no body reads 0 (false, "") and ignores what is set, as GameMaker's do. A 2D
/// body's values are in pixels and degrees (clockwise, as image_angle); a 3D body's in metres.
/// </summary>
public static partial class PgslCommands
{
    private const string PhysicsArea = "Engine.Physics";

    // ── The world ──────────────────────────────────────────────────────────────────────────

    [PgslCommand("SleepThreshold", "Engine.Physics.SleepThreshold = speed", "How slowly a body must move to be put to sleep (it wakes when touched)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsWorldSleepThreshold
    {
        get => PhysicsWorld?.SleepThreshold ?? 0;
        set => PhysicsSetSleepThreshold(value);
    }

    [PgslCommand("RecoveryVelocity", "Engine.Physics.RecoveryVelocity = speed", "How hard overlapping bodies are pushed apart", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsWorldRecoveryVelocity
    {
        get => PhysicsWorld?.RecoveryVelocity ?? 0;
        set => PhysicsSetRecoveryVelocity(value);
    }

    [PgslCommand("GravityX", "Engine.Physics.GravityX = acceleration", "Gravity's pull along X (metres, or pixels in a 2D room, a second each second)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsWorldGravityX { get => PhysicsGravity().X; set => SetGravityAxis(0, value); }

    [PgslCommand("GravityY", "Engine.Physics.GravityY = acceleration", "Gravity's pull along Y (down is negative in 3D, positive in a 2D room)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsWorldGravityY { get => PhysicsGravity().Y; set => SetGravityAxis(1, value); }

    [PgslCommand("GravityZ", "Engine.Physics.GravityZ = acceleration", "Gravity's pull along Z", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsWorldGravityZ { get => PhysicsGravity().Z; set => SetGravityAxis(2, value); }

    private static void SetGravityAxis(int axis, double value)
    {
        Genesis.Physics.PhysicsWorld? physics = PhysicsWorld;
        if (physics is null || !double.IsFinite(value)) return;
        Vector3 pull = PhysicsGravity();
        if (axis == 0) pull.X = (float)value; else if (axis == 1) pull.Y = (float)value; else pull.Z = (float)value;
        if (ActiveGameContext?.Scene?.IsSpritePhysicsScene == true) pull = SpritePhysicsBinding.ToPhysics(pull);
        float length = pull.Length();
        physics.GravityStrength = length / 9.81f;
        if (length > 1e-6f) physics.GravityDirection = pull / length;
    }

    // ── The current instance's body ──────────────────────────────────────────────────────────

    [PgslCommand("HasBody", "Engine.Physics.HasBody", "Whether this instance has a physics body", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyExists => ThisBody(out _, out EcsWorld? world, out Entity entity)
        && world!.GetRef<RigidBodyComponent>(entity) is { } body && (body.Collision || body.RegistrationId != 0);

    [PgslCommand("BodyType", "Engine.Physics.BodyType = \"dynamic\"", "How the body takes part: \"dynamic\" (moved by forces), \"kinematic\" (moved only by the game) or \"static\" (never moves); \"\" for no body", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static string PhysicsBodyType
    {
        get => ThisBody(out _, out EcsWorld? world, out Entity entity)
            ? world!.GetRef<RigidBodyComponent>(entity).Motion.ToString().ToLowerInvariant() : "";
        set
        {
            if (!Enum.TryParse(value?.Trim(), true, out PhysicsMotionType motion) || !Enum.IsDefined(motion)) return;
            RebuildBody((ref RigidBodyComponent body) =>
            {
                body.Motion = motion;
                if (motion == PhysicsMotionType.Dynamic && body.GravityScale != 0) body.Flags |= RigidBodyFlags.UseGravity;
            });
        }
    }

    [PgslCommand("Shape", "Engine.Physics.Shape = \"sphere\"", "The body's collider: \"box\", \"sphere\", \"capsule\" or \"cylinder\" (a model's \"mesh\" can be read, not chosen)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static string PhysicsBodyShape
    {
        get => ThisBody(out _, out EcsWorld? world, out Entity entity)
            ? world!.GetRef<RigidBodyComponent>(entity).Shape.ToString().ToLowerInvariant() : "";
        set
        {
            if (!Enum.TryParse(value?.Trim(), true, out CollisionShape shape)
                || shape is not (CollisionShape.Box or CollisionShape.Sphere or CollisionShape.Capsule or CollisionShape.Cylinder)) return;
            RebuildBody((ref RigidBodyComponent body) =>
            {
                Vector3 half = body.HalfExtents;
                body.Shape = shape;
                // The new shape fills the old one's box.
                body.Size = shape switch
                {
                    CollisionShape.Sphere => new Vector3(MathF.Max(half.X, MathF.Max(half.Y, half.Z))),
                    CollisionShape.Capsule or CollisionShape.Cylinder => new Vector3(MathF.Max(half.X, half.Z), half.Y, 0),
                    _ => half,
                };
            });
        }
    }

    [PgslCommand("Mass", "Engine.Physics.Mass = kilograms", "The body's mass: a heavier body is pushed less by the same force and knocks lighter ones aside", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyMass
    {
        get => BodyValue(body => body.Mass);
        set
        {
            if (!double.IsFinite(value) || value <= 0) return;
            float mass = MathF.Max(0.001f, (float)value);
            if (!ChangeBody((ref RigidBodyComponent body) => body.Mass = mass, out Genesis.Physics.PhysicsWorld? physics, out int registration)) return;
            physics!.SetMass(registration, mass);
        }
    }

    [PgslCommand("Weight", "Engine.Physics.Weight = weight", "How little air drag slows the body (heavier falls through air faster); 0 uses the world's default", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyWeight
    {
        get => BodyValue(body => body.Weight);
        set { if (double.IsFinite(value)) ChangeBody((ref RigidBodyComponent body) => body.Weight = MathF.Max(0, (float)value), out _, out _); }
    }

    [PgslCommand("Friction", "Engine.Physics.Friction = grip", "Grip against what it touches (0.01 ice to 4)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyFriction
    {
        get => BodyValue(body => body.Friction);
        set { if (double.IsFinite(value)) ChangeBody((ref RigidBodyComponent body) => body.Friction = Math.Clamp((float)value, 0.01f, 4f), out _, out _); }
    }

    [PgslCommand("Bounciness", "Engine.Physics.Bounciness = amount", "How much of a hit it bounces back (0 none to 1)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyBounciness
    {
        get => BodyValue(body => body.Restitution);
        set { if (double.IsFinite(value)) ChangeBody((ref RigidBodyComponent body) => body.Restitution = Math.Clamp((float)value, 0f, 1f), out _, out _); }
    }

    [PgslCommand("GravityScale", "Engine.Physics.GravityScale = scale", "How strongly gravity pulls this body (0 floats, 1 as the world, 2 twice as hard, negative rises)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyGravityScale
    {
        get => BodyValue(body => body.GravityScale);
        set
        {
            if (!double.IsFinite(value)) return;
            ChangeBody((ref RigidBodyComponent body) =>
            {
                body.GravityScale = (float)value;
                if (value != 0) body.Flags |= RigidBodyFlags.UseGravity;
            }, out _, out _);
        }
    }

    [PgslCommand("UseGravity", "Engine.Physics.UseGravity = true", "Whether gravity pulls this body at all", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyUseGravity
    {
        get => BodyFlag(RigidBodyFlags.UseGravity);
        set => SetBodyFlag(RigidBodyFlags.UseGravity, value);
    }

    [PgslCommand("LinearDamping", "Engine.Physics.LinearDamping = amount", "How quickly the body's movement dies away by itself (0 none)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyLinearDamping
    {
        get => BodyValue(body => body.LinearDamping);
        set { if (double.IsFinite(value)) ChangeBody((ref RigidBodyComponent body) => body.LinearDamping = Math.Clamp((float)value, 0f, 100f), out _, out _); }
    }

    [PgslCommand("AngularDamping", "Engine.Physics.AngularDamping = amount", "How quickly the body's spin dies away by itself (0 none)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyAngularDamping
    {
        get => BodyValue(body => body.AngularDamping);
        set { if (double.IsFinite(value)) ChangeBody((ref RigidBodyComponent body) => body.AngularDamping = Math.Clamp((float)value, 0f, 100f), out _, out _); }
    }

    [PgslCommand("LockRotation", "Engine.Physics.LockRotation = true", "Whether the body is kept upright (it moves but never turns)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyLockRotation
    {
        get => BodyFlag(RigidBodyFlags.LockRotation);
        set
        {
            SetBodyFlag(RigidBodyFlags.LockRotation, value);
            if (value && ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity))
                physics!.SetAngularVelocity(world!.GetRef<RigidBodyComponent>(entity).RegistrationId, Vector3.Zero);
        }
    }

    [PgslCommand("Sensor", "Engine.Physics.Sensor = true", "Whether the body only notices what passes through it (a trigger) instead of blocking it", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodySensor
    {
        get => BodyFlag(RigidBodyFlags.Sensor);
        set => SetBodyFlag(RigidBodyFlags.Sensor, value);
    }

    [PgslCommand("Enabled", "Engine.Physics.Enabled = false", "Whether the body takes part in the simulation (false leaves it where it is, touching nothing)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyEnabled
    {
        get => BodyFlag(RigidBodyFlags.Collision);
        set => SetBodyFlag(RigidBodyFlags.Collision, value);
    }

    [PgslCommand("Layer", "Engine.Physics.Layer = layer", "The body's collision layer (0-31)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyLayer
    {
        get => BodyValue(body => body.CollisionLayer);
        set { if (double.IsFinite(value)) ChangeBody((ref RigidBodyComponent body) => body.CollisionLayer = (byte)Math.Clamp((int)value, 0, 31), out _, out _); }
    }

    [PgslCommand("Mask", "Engine.Physics.Mask = layers", "Which layers the body collides with: bit n for layer n (1 + 4 = layers 0 and 2)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyMask
    {
        get => BodyValue(body => body.CollisionMask);
        set { if (double.IsFinite(value) && value >= 0) ChangeBody((ref RigidBodyComponent body) => body.CollisionMask = (uint)Math.Min(value, uint.MaxValue), out _, out _); }
    }

    [PgslCommand("VelocityX", "Engine.Physics.VelocityX = speed", "The body's speed along X (pixels or metres a second)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyVelocityX { get => PhysicsVelocity((int)SelfId()).X; set => SetVelocityAxis(0, value); }

    [PgslCommand("VelocityY", "Engine.Physics.VelocityY = speed", "The body's speed along Y", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyVelocityY { get => PhysicsVelocity((int)SelfId()).Y; set => SetVelocityAxis(1, value); }

    [PgslCommand("VelocityZ", "Engine.Physics.VelocityZ = speed", "The body's speed along Z (always 0 for a 2D body)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodyVelocityZ { get => PhysicsVelocity((int)SelfId()).Z; set => SetVelocityAxis(2, value); }

    [PgslCommand("Speed", "Engine.Physics.Speed", "How fast the body moves, in any direction", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodySpeed => PhysicsVelocity((int)SelfId()).Length();

    [PgslCommand("SpinX", "Engine.Physics.SpinX = degrees", "How fast the body turns about X, degrees a second", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodySpinX { get => Spin().X; set => SetSpinAxis(0, value); }

    [PgslCommand("SpinY", "Engine.Physics.SpinY = degrees", "How fast the body turns about Y, degrees a second", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodySpinY { get => Spin().Y; set => SetSpinAxis(1, value); }

    [PgslCommand("SpinZ", "Engine.Physics.SpinZ = degrees", "How fast the body turns about Z, degrees a second (a 2D body's spin, clockwise as image_angle)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static double PhysicsBodySpinZ { get => Spin().Z; set => SetSpinAxis(2, value); }

    [PgslCommand("Awake", "Engine.Physics.Awake = true", "Whether the body is being simulated: false once it has come to rest (setting false puts it to sleep until something touches it)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyAwake
    {
        get => ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity)
            && physics!.IsAwake(world!.GetRef<RigidBodyComponent>(entity).RegistrationId);
        set
        {
            if (ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity))
                physics!.SetAwake(world!.GetRef<RigidBodyComponent>(entity).RegistrationId, value);
        }
    }

    [PgslCommand("Grounded", "Engine.Physics.Grounded", "Whether the body stands on something", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyGrounded => ThisBody(out _, out _, out Entity entity) && PhysicsIsGrounded(entity.Id);

    // ── Commands on the current instance ─────────────────────────────────────────────────────

    [PgslCommand("Use", "Engine.Physics.Use(physics) -> bool",
        "Give this instance the body a Physics resource describes (made in the Physics editor), in place of any it has; false when there is no such resource",
        "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyUse(string physics)
    {
        ProjectGameContext? game = ActiveGameContext as ProjectGameContext;
        EcsWorld? world = ActiveGameContext?.World;
        RuntimeScene? scene = ActiveGameContext?.Scene;
        PgslContext? context = GetContext();
        string project = ProjectPath ?? "";
        if (world is null || scene is null || context is null || string.IsNullOrWhiteSpace(physics)) return false;
        Entity entity = world.GetEntity(context.InstanceId);
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<TransformComponent>(entity)) return false;
        string path = ResourceCatalog.Resolve(project, physics, ResourceType.Physics);
        if (string.IsNullOrWhiteSpace(path)) return false;
        Genesis.Physics.PhysicsSceneConfig asset = Genesis.Physics.PhysicsSceneConfig.LoadFromFile(path);
        PhysicsBodyRemove();
        var document = new JObject { ["physics"] = physics };
        TransformComponent transform = world.GetRef<TransformComponent>(entity);
        bool attached;
        if (asset.Dimension == Genesis.Physics.PhysicsDimension.TwoD)
        {
            if (game?.Room is { } room) SpritePhysicsBinding.EnsureScene(scene, room, project);
            attached = SpritePhysicsBinding.Attach(world, entity, project, document, transform);
        }
        else
        {
            scene.Physics ??= Genesis.Physics.PhysicsWorld.Create(new PhysicsWorldAsset());
            attached = ModelPhysicsBinding.Attach(world, entity, project, document, transform) && world.Has<RigidBodyComponent>(entity);
            if (attached) SynchronizeTransform(world, entity, Vector3.One);
        }
        return attached && ThisBody(out _, out _, out _);
    }

    [PgslCommand("Remove", "Engine.Physics.Remove() -> bool", "Take this instance's body out of the simulation (false when it had none)", "Physics", Namespace = PhysicsArea, Flat = false)]
    public static bool PhysicsBodyRemove()
    {
        EcsWorld? world = ActiveGameContext?.World;
        PgslContext? context = GetContext();
        if (world is null || context is null) return false;
        Entity entity = world.GetEntity(context.InstanceId);
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<RigidBodyComponent>(entity)) return false;
        // Components stay for the life of an entity: a body taken out is one that collides with
        // nothing and is in no world (Use or Enabled = true brings one back).
        ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(entity);
        if (body.RegistrationId == 0 && !body.Collision) return false;
        PhysicsWorld?.UnregisterEntity(world, entity, ref body);
        body.Flags &= ~RigidBodyFlags.Collision;
        return true;
    }

    // Given x, y and z only, these act on the current instance's body (each command's attribute names both forms).
    public static void PhysicsApplyForce(double x, double y, double z) => PhysicsApplyForce(SelfId(), x, y, z);
    public static void PhysicsApplyImpulse(double x, double y, double z) => PhysicsApplyImpulse(SelfId(), x, y, z);
    public static void PhysicsSetVelocity(double x, double y, double z) => PhysicsSetVelocity(SelfId(), x, y, z);

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private delegate void BodyChange(ref RigidBodyComponent body);

    private static double SelfId() => GetContext()?.InstanceId ?? 0;

    // The current instance's body, registered with the world if it was not yet.
    private static bool ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity)
    {
        physics = PhysicsWorld;
        world = ActiveGameContext?.World;
        entity = Entity.Null;
        PgslContext? context = GetContext();
        if (physics is null || world is null || context is null) return false;
        entity = world.GetEntity(context.InstanceId);
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<RigidBodyComponent>(entity)) return false;
        ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(entity);
        if (body.RegistrationId == 0 && body.Collision && world.Has<Transform3DComponent>(entity)
            && (!world.Has<EntityLifecycleComponent>(entity) || world.GetRef<EntityLifecycleComponent>(entity).Enabled))
            physics.RegisterEntity(world, entity, ref body, ref world.GetRef<Transform3DComponent>(entity));
        return true;
    }

    private static double BodyValue(Func<RigidBodyComponent, double> read) =>
        ThisBody(out _, out EcsWorld? world, out Entity entity) ? read(world!.GetRef<RigidBodyComponent>(entity)) : 0;

    private static bool BodyFlag(RigidBodyFlags flag) =>
        ThisBody(out _, out EcsWorld? world, out Entity entity) && (world!.GetRef<RigidBodyComponent>(entity).Flags & flag) != 0;

    private static void SetBodyFlag(RigidBodyFlags flag, bool on) =>
        ChangeBody((ref RigidBodyComponent body) => body.Flags = on ? body.Flags | flag : body.Flags & ~flag, out _, out _);

    // A setting the world reads from the component each step: changing the component is enough.
    private static bool ChangeBody(BodyChange change, out Genesis.Physics.PhysicsWorld? physics, out int registration)
    {
        registration = 0;
        if (!ThisBody(out physics, out EcsWorld? world, out Entity entity)) return false;
        ref RigidBodyComponent body = ref world!.GetRef<RigidBodyComponent>(entity);
        change(ref body);
        // A body switched on again (or never registered) joins the world now.
        if (body.RegistrationId == 0 && body.Collision && world.Has<Transform3DComponent>(entity))
            physics!.RegisterEntity(world, entity, ref body, ref world.GetRef<Transform3DComponent>(entity));
        registration = body.RegistrationId;
        return true;
    }

    // A setting the solver holds (type, shape): the body is made again where it is, moving as it was.
    // Joints on it are removed with the old body, as when it is destroyed.
    private static void RebuildBody(BodyChange change)
    {
        if (!ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity)) return;
        ref RigidBodyComponent body = ref world!.GetRef<RigidBodyComponent>(entity);
        Vector3 linear = physics!.GetLinearVelocity(body.RegistrationId), angular = physics.GetAngularVelocity(body.RegistrationId);
        physics.UnregisterEntity(world, entity, ref body);
        change(ref body);
        if (!body.Collision || !world.Has<Transform3DComponent>(entity)) return;
        physics.RegisterEntity(world, entity, ref body, ref world.GetRef<Transform3DComponent>(entity));
        physics.SetLinearVelocity(world, body.RegistrationId, linear);
        physics.SetAngularVelocity(body.RegistrationId, angular);
    }

    private static void SetVelocityAxis(int axis, double value)
    {
        if (!double.IsFinite(value)) return;
        double self = SelfId();
        Vector3 velocity = PhysicsVelocity((int)self);
        if (axis == 0) velocity.X = (float)value; else if (axis == 1) velocity.Y = (float)value; else velocity.Z = (float)value;
        PhysicsSetVelocity(self, velocity.X, velocity.Y, velocity.Z);
    }

    // Degrees a second; a 2D body turns clockwise for a positive Z, as its image_angle does.
    private static Vector3 Spin()
    {
        if (!ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity)) return Vector3.Zero;
        Vector3 spin = physics!.GetAngularVelocity(world!.GetRef<RigidBodyComponent>(entity).RegistrationId) * (180f / MathF.PI);
        if (world.Has<SpritePhysicsBindingComponent>(entity)) spin.Z = -spin.Z;
        return spin;
    }

    private static void SetSpinAxis(int axis, double value)
    {
        if (!double.IsFinite(value) || !ThisBody(out Genesis.Physics.PhysicsWorld? physics, out EcsWorld? world, out Entity entity)) return;
        Vector3 spin = Spin();
        if (axis == 0) spin.X = (float)value; else if (axis == 1) spin.Y = (float)value; else spin.Z = (float)value;
        if (world!.Has<SpritePhysicsBindingComponent>(entity)) spin.Z = -spin.Z;
        physics!.SetAngularVelocity(world.GetRef<RigidBodyComponent>(entity).RegistrationId, spin * (MathF.PI / 180f));
    }
}

using System.Numerics;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Shape queries for a mover written in script (a kinematic character): the calling instance's own
// collider is never hit. The hit is read back with the PhysicsRaycastHit* commands; its point is
// where the shape touches, and the shape's centre is then at the start plus the direction times
// the distance. In 3D rooms (metres).
public static partial class PgslCommands
{
    /// <summary>A resting query that finds nothing below returns this height.</summary>
    public const double NothingBelow = -1000000;

    [PgslCommand("PhysicsSphereCast", "PhysicsSphereCast(x, y, z, radius, dx, dy, dz, maxDistance) -> number",
        "Distance a sphere moves before it touches something (0 when it starts touching), -1 for none; the calling instance is never hit",
        "Physics")]
    public static double PhysicsSphereCast(double x, double y, double z, double radius,
        double dx, double dy, double dz, double maxDistance) =>
        ShapeQuery((physics, world, ignore) => physics.SphereCast(world, new Vector3((float)x, (float)y, (float)z), (float)radius,
            new Vector3((float)dx, (float)dy, (float)dz), (float)maxDistance, out var hit, ignore) ? hit : null,
            Finite3(x, y, z) && Finite3(dx, dy, dz) && double.IsFinite(radius) && double.IsFinite(maxDistance));

    [PgslCommand("PhysicsCapsuleCast", "PhysicsCapsuleCast(x, y, z, radius, height, dx, dy, dz, maxDistance) -> number",
        "Distance an upright capsule (centred on x, y, z, height end to end) moves before it touches something, -1 for none",
        "Physics")]
    public static double PhysicsCapsuleCast(double x, double y, double z, double radius, double height,
        double dx, double dy, double dz, double maxDistance) =>
        ShapeQuery((physics, world, ignore) => physics.CapsuleCast(world, new Vector3((float)x, (float)y, (float)z), (float)radius,
            (float)height, new Vector3((float)dx, (float)dy, (float)dz), (float)maxDistance, out var hit, ignore) ? hit : null,
            Finite3(x, y, z) && Finite3(dx, dy, dz) && double.IsFinite(radius) && double.IsFinite(height) && double.IsFinite(maxDistance));

    [PgslCommand("PhysicsSphereRest", "PhysicsSphereRest(x, z, radius, maxCentreY) -> number",
        "Height of a sphere's centre where it comes to rest when lowered from maxCentreY (on a face, an edge or a corner); -1000000 when nothing is below",
        "Physics")]
    public static double PhysicsSphereRest(double x, double z, double radius, double maxCentreY)
    {
        double drop = PhysicsSphereCast(x, maxCentreY, z, radius, 0, -1, 0, 4096);
        return drop < 0 ? NothingBelow : maxCentreY - drop;
    }

    [PgslCommand("PhysicsOverlapCapsule", "PhysicsOverlapCapsule(x, y, z, radius, height) -> bool",
        "Whether an upright capsule here overlaps or touches anything but the calling instance: does a crouched character fit standing up?",
        "Physics")]
    public static bool PhysicsOverlapCapsule(double x, double y, double z, double radius, double height) =>
        ShapeQuery((physics, world, ignore) => physics.CapsuleOverlaps(world, new Vector3((float)x, (float)y, (float)z), (float)radius,
            (float)height, out var hit, ignore) ? hit : null,
            Finite3(x, y, z) && double.IsFinite(radius) && double.IsFinite(height)) >= 0;

    private static double ShapeQuery(
        System.Func<Genesis.Physics.PhysicsWorld, Genesis.Runtime.ECS.World, Genesis.Shared.ECS.Entity, Genesis.Physics.PhysicsRaycastHit?> query,
        bool valid)
    {
        PgslContext context = GetContext();
        if (context != null) context.LastPhysicsRaycast = null;
        Genesis.Physics.PhysicsWorld physics = PhysicsWorld;
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        if (!valid || physics is null || world is null || ActiveGameContext?.Scene?.IsSpritePhysicsScene == true) return -1d;
        Genesis.Shared.ECS.Entity ignore = Genesis.Shared.ECS.Entity.Null;
        if (context != null && context.InstanceId > 0)
        {
            Genesis.Shared.ECS.Entity self = world.GetEntity(context.InstanceId);
            if (world.IsAlive(self)) ignore = self;
        }
        Genesis.Physics.PhysicsRaycastHit? hit = query(physics, world, ignore);
        if (hit is not { } found) return -1d;
        if (context != null) context.LastPhysicsRaycast = found;
        return found.Distance;
    }
}

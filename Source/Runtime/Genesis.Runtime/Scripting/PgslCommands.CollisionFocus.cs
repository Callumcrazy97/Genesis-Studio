using Genesis.Runtime.Scene;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Colliders that a large world makes only near what can touch them (streamed scenery, a large
// terrain's tiles, solid scatter) follow the instances named here as well.
public static partial class PgslCommands
{
    [PgslCommand("PhysicsAddCollisionFocus", "PhysicsAddCollisionFocus(id, radius) -> bool",
        "Keep the colliders a large world makes only near moving things (streamed scenery, terrain tiles, solid scatter) within radius metres of this instance, however far from the camera; false when it cannot",
        "Physics")]
    public static bool PhysicsAddCollisionFocus(double instanceId, double radius)
    {
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(instanceId) || instanceId < 0 || instanceId > int.MaxValue
            || !double.IsFinite(radius) || radius <= 0) return false;
        return CollisionFoci.Add(world, world.GetEntity((int)instanceId), (float)radius);
    }

    [PgslCommand("PhysicsRemoveCollisionFocus", "PhysicsRemoveCollisionFocus(id) -> bool",
        "Stop keeping colliders around this instance; false when it was not a collision focus", "Physics")]
    public static bool PhysicsRemoveCollisionFocus(double instanceId)
    {
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(instanceId) || instanceId < 0 || instanceId > int.MaxValue) return false;
        Genesis.Shared.ECS.Entity entity = world.GetEntity((int)instanceId);
        return !entity.IsNull && CollisionFoci.Remove(world, entity);
    }
}

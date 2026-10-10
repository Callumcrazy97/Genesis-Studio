using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Physics;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A kinematic character for a script-driven player (KinematicCharacter): one per instance, its
// position the feet. The script moves it and copies its position to the instance itself.
public static partial class PgslCommands
{
    private static readonly ConditionalWeakTable<Genesis.Runtime.ECS.World, Dictionary<int, KinematicCharacter>> Characters = new();

    [PgslCommand("CharacterCreate", "CharacterCreate(radius, height, stepHeight, maxSlopeDegrees)",
        "Give this instance a kinematic character capsule at its own position (feet): it slides, climbs steps and keeps to the ground",
        "Physics")]
    public static void CharacterCreate(double radius, double height, double stepHeight, double maxSlopeDegrees)
    {
        var ctx = GetContext();
        var world = ActiveGameContext?.World;
        if (ctx == null || world == null || ctx.InstanceId <= 0) return;
        var character = new KinematicCharacter((float)radius, (float)height, (float)stepHeight, (float)maxSlopeDegrees)
        {
            Position = new Vector3((float)ctx.X, (float)ctx.Y, (float)ctx.Z),
        };
        Characters.GetOrCreateValue(world)[ctx.InstanceId] = character;
    }

    [PgslCommand("CharacterMove", "CharacterMove(dx, dy, dz) -> number",
        "Move this instance's character as far as the world allows; flags: 1 grounded, 2 ceiling, 4 wall, 8 climbed a step",
        "Physics")]
    public static double CharacterMove(double dx, double dy, double dz)
    {
        if (!Finite3(dx, dy, dz) || !TryCharacter(out KinematicCharacter character, out var world, out var self)) return 0;
        return (double)character.Move(PhysicsWorld, world, new Vector3((float)dx, (float)dy, (float)dz), self);
    }

    [PgslCommand("CharacterSetPosition", "CharacterSetPosition(x, y, z)", "Put this instance's character's feet here (a teleport)", "Physics")]
    public static void CharacterSetPosition(double x, double y, double z)
    {
        if (Finite3(x, y, z) && TryCharacter(out KinematicCharacter character, out _, out _))
            character.Position = new Vector3((float)x, (float)y, (float)z);
    }

    [PgslCommand("CharacterX", "CharacterX() -> number", "This instance's character's feet, X", "Physics")]
    public static double CharacterX() => TryCharacter(out KinematicCharacter c, out _, out _) ? c.Position.X : 0;

    [PgslCommand("CharacterY", "CharacterY() -> number", "This instance's character's feet, Y", "Physics")]
    public static double CharacterY() => TryCharacter(out KinematicCharacter c, out _, out _) ? c.Position.Y : 0;

    [PgslCommand("CharacterZ", "CharacterZ() -> number", "This instance's character's feet, Z", "Physics")]
    public static double CharacterZ() => TryCharacter(out KinematicCharacter c, out _, out _) ? c.Position.Z : 0;

    [PgslCommand("CharacterGrounded", "CharacterGrounded() -> bool", "Whether this instance's character stands on walkable ground", "Physics")]
    public static bool CharacterGrounded() => TryCharacter(out KinematicCharacter c, out _, out _) && c.Grounded;

    [PgslCommand("CharacterGroundNormalY", "CharacterGroundNormalY() -> number",
        "How upright the ground under the character is (1 flat); 1 in the air", "Physics")]
    public static double CharacterGroundNormalY() => TryCharacter(out KinematicCharacter c, out _, out _) ? c.GroundNormal.Y : 1;

    [PgslCommand("CharacterSetHeight", "CharacterSetHeight(height) -> bool",
        "Crouch or stand: change the character's height; false and unchanged when standing up would not fit", "Physics")]
    public static bool CharacterSetHeight(double height) =>
        double.IsFinite(height) && TryCharacter(out KinematicCharacter c, out var world, out var self)
        && c.SetHeight(PhysicsWorld, world, (float)height, self);

    [PgslCommand("CharacterFits", "CharacterFits(height) -> bool", "Whether the character would fit this tall where it stands", "Physics")]
    public static bool CharacterFits(double height) =>
        double.IsFinite(height) && TryCharacter(out KinematicCharacter c, out var world, out var self)
        && c.Fits(PhysicsWorld, world, (float)height, self);

    [PgslCommand("CharacterDestroy", "CharacterDestroy()", "Remove this instance's character", "Physics")]
    public static void CharacterDestroy()
    {
        var ctx = GetContext();
        var world = ActiveGameContext?.World;
        if (ctx != null && world != null && Characters.TryGetValue(world, out var characters)) characters.Remove(ctx.InstanceId);
    }

    /// <summary>
    /// Adds the feet of each character whose instance still exists, up to <paramref name="limit"/>
    /// entries: colliders made only near moving things (streamed scenery) are kept around them.
    /// </summary>
    internal static void CollectCharacterFeet(Genesis.Runtime.ECS.World world, List<Genesis.Runtime.Scene.CollisionFocus> into, float radius, int limit)
    {
        if (world == null || into == null || !Characters.TryGetValue(world, out var characters)) return;
        foreach (KeyValuePair<int, KinematicCharacter> pair in characters)
        {
            if (into.Count >= limit) return;
            if (world.GetEntity(pair.Key).IsNull) continue;
            Vector3 feet = pair.Value.Position;
            if (float.IsFinite(feet.X) && float.IsFinite(feet.Y) && float.IsFinite(feet.Z))
                into.Add(new Genesis.Runtime.Scene.CollisionFocus(feet, radius));
        }
    }

    /// <summary>The feet of an instance's character, when it has one.</summary>
    internal static bool TryCharacterFeet(Genesis.Runtime.ECS.World world, Genesis.Shared.ECS.Entity entity, out Vector3 feet)
    {
        feet = default;
        if (world == null || !world.IsAlive(entity) || !Characters.TryGetValue(world, out var characters)
            || !characters.TryGetValue(entity.Id, out KinematicCharacter character))
            return false;
        feet = character.Position;
        return true;
    }

    private static bool TryCharacter(out KinematicCharacter character, out Genesis.Runtime.ECS.World world, out Genesis.Shared.ECS.Entity self)
    {
        character = null;
        self = Genesis.Shared.ECS.Entity.Null;
        world = ActiveGameContext?.World;
        var ctx = GetContext();
        if (ctx == null || world == null || PhysicsWorld == null || !Characters.TryGetValue(world, out var characters)
            || !characters.TryGetValue(ctx.InstanceId, out character))
            return false;
        Genesis.Shared.ECS.Entity entity = world.GetEntity(ctx.InstanceId);
        if (world.IsAlive(entity)) self = entity;
        return true;
    }
}

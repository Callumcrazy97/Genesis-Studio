using Genesis.Shared.Scripting;
namespace Genesis.Runtime.Scripting;
public static partial class PgslCommands
{
    [PgslCommand("PhysicsRaycastHitX", "PhysicsRaycastHitX() -> number", "Last hit world X; zero after a miss", "Physics")]
    public static double PhysicsRaycastHitX() => GetContext()?.LastPhysicsRaycast?.Point.X ?? 0;
    [PgslCommand("PhysicsRaycastHitY", "PhysicsRaycastHitY() -> number", "Last hit world Y; zero after a miss", "Physics")]
    public static double PhysicsRaycastHitY() => GetContext()?.LastPhysicsRaycast?.Point.Y ?? 0;
    [PgslCommand("PhysicsRaycastHitZ", "PhysicsRaycastHitZ() -> number", "Last hit world Z; zero after a miss", "Physics")]
    public static double PhysicsRaycastHitZ() => GetContext()?.LastPhysicsRaycast?.Point.Z ?? 0;
    [PgslCommand("PhysicsRaycastHitNormalX", "PhysicsRaycastHitNormalX() -> number", "Last hit normal X; zero after a miss", "Physics")]
    public static double PhysicsRaycastHitNormalX() => GetContext()?.LastPhysicsRaycast?.Normal.X ?? 0;
    [PgslCommand("PhysicsRaycastHitNormalY", "PhysicsRaycastHitNormalY() -> number", "Last hit normal Y; zero after a miss", "Physics")]
    public static double PhysicsRaycastHitNormalY() => GetContext()?.LastPhysicsRaycast?.Normal.Y ?? 0;
    [PgslCommand("PhysicsRaycastHitNormalZ", "PhysicsRaycastHitNormalZ() -> number", "Last hit normal Z; zero after a miss", "Physics")]
    public static double PhysicsRaycastHitNormalZ() => GetContext()?.LastPhysicsRaycast?.Normal.Z ?? 0;
    [PgslCommand("PhysicsRaycastHitInstanceId", "PhysicsRaycastHitInstanceId() -> number", "Last hit entity ID; -1 for static geometry or a miss", "Physics")]
    public static double PhysicsRaycastHitInstanceId() => GetContext()?.LastPhysicsRaycast is { } hit && !hit.IsStatic && !hit.Entity.IsNull ? hit.Entity.Id : -1;
}

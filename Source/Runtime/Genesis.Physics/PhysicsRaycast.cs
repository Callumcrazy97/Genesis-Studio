using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Physics;

/// <summary>Result of a physics raycast against registered rigid-body colliders.</summary>
public readonly struct PhysicsRaycastHit
{
    public Entity Entity { get; init; }
    public Vector3 Point { get; init; }
    public Vector3 Normal { get; init; }
    public float Distance { get; init; }
    public bool IsStatic { get; init; }
    public bool Hit => !Entity.IsNull;
}

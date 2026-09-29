using System.Numerics;

namespace Genesis.Shared.ECS.Components;

/// <summary>Cooked model geometry in entity-local units, including the authored model pivot and scale.</summary>
public struct MeshColliderComponent : IComponent
{
    public Vector3[] Vertices;
    public int[] Indices;
    public Vector3 Scale;
}

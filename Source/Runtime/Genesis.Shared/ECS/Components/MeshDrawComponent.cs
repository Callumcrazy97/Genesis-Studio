using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Shared.ECS.Components
{
    public struct MeshDrawComponent : IComponent
    {
        public MeshHandle    Mesh;
        public Vector4       Tint;
        public MeshDrawFlags Flags;
        public float         Emissive;
    }
}

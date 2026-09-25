using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    /// <summary>Marks an entity for 2D sprite rendering via the object draw pass.</summary>
    public struct Draw2DComponent : IComponent
    {
        public bool Visible;
        public float Depth;
        public byte BlendMode; // 0 = Alpha, 1 = Additive
    }
}

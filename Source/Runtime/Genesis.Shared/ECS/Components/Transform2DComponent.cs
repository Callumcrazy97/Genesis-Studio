using Genesis.Shared.ECS;

namespace Genesis.Shared.ECS.Components
{
    public struct Transform2DComponent : IComponent
    {
        public float X;
        public float Y;
        public int   Depth;
    }
}

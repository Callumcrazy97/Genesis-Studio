using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct TransformComponent : IComponent
    {
        public float X, Y, Z;
        public float ScaleX, ScaleY, ScaleZ;
        public float Rotation; // legacy 2D Z rotation, degrees clockwise
        public float RotationX, RotationY, RotationZ;
    }
}

using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct PhysicsComponent : IComponent
    {
        public float HSpeed;            // pixels per step, horizontal
        public float VSpeed;            // pixels per step, vertical
        public float Gravity;           // added to VSpeed each step
        public float GravityDirection;  // degrees; 270 = downward
        public float Friction;          // speed reduction per step
        public bool  Solid;             // participates in solid collision checks
    }
}

using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct ScriptComponent : IComponent
    {
        // Index into the object-type registry (holds compiled PGSL bytecode per event).
        // -1 = no script assigned.
        public int ObjectTypeId;
    }
}

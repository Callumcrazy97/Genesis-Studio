namespace Genesis.Shared.ECS.Components
{
    public struct EntityLifecycleComponent : IComponent
    {
        public bool NeedsStart;
        public bool Enabled;
    }
}

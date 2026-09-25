namespace Genesis.Shared.ECS
{
    public interface IEcsSystem
    {
        // Lower priority runs first. Render systems use priority >= 300.
        int  Priority { get; }
        void Update(IEcsWorld world, float dt);
    }
}

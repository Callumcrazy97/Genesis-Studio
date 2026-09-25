namespace Genesis.Shared.ECS
{
    public interface IFixedEcsSystem
    {
        // Lower priority runs first. Pre-physics: 90–99. Post-physics: 100–109.
        int  FixedPriority { get; }
        void FixedUpdate(IEcsWorld world, float fixedDelta);
    }
}

namespace Genesis.Shared.ECS
{
    public interface IEcsWorld
    {
        // ── Entity lifecycle ─────────────────────────────────────────────────────
        Entity CreateEntity();
        void   DestroyEntity(Entity entity);  // queued — safe to call mid-system
        bool   IsAlive(Entity entity);

        // ── Component access ─────────────────────────────────────────────────────
        bool  Has<T>(Entity entity)              where T : struct, IComponent;
        void  Set<T>(Entity entity, in T value)  where T : struct, IComponent;
        ref T GetRef<T>(Entity entity)           where T : struct, IComponent;

        // ── Iteration ────────────────────────────────────────────────────────────
        void Query<T1>(ComponentAction<T1> action)
            where T1 : struct, IComponent;

        void Query<T1, T2>(ComponentAction<T1, T2> action)
            where T1 : struct, IComponent
            where T2 : struct, IComponent;

        void Query<T1, T2, T3>(ComponentAction<T1, T2, T3> action)
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent;
    }
}

namespace Genesis.Shared.ECS
{
    // Custom delegate types for Query<T> — Action<> does not support ref parameters.
    public delegate void ComponentAction<T1>(Entity e, ref T1 c1)
        where T1 : struct;

    public delegate void ComponentAction<T1, T2>(Entity e, ref T1 c1, ref T2 c2)
        where T1 : struct where T2 : struct;

    public delegate void ComponentAction<T1, T2, T3>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3)
        where T1 : struct where T2 : struct where T3 : struct;
}

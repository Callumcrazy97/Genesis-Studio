using System.Collections.Generic;
using Genesis.Shared.Interfaces;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.Rendering
{
    /// <summary>Per-entity procedural mesh batches submitted by scripts for Draw3D to draw.</summary>
    public static class ProceduralMeshDrawRegistry
    {
        private static readonly Dictionary<int, MeshDrawCall[]> Frames = new();

        public static void Set(Entity entity, MeshDrawCall[] draws)
        {
            if (draws == null || draws.Length == 0)
                Frames.Remove(entity.Id);
            else
                Frames[entity.Id] = draws;
        }

        public static bool TryGet(Entity entity, out MeshDrawCall[] draws)
            => Frames.TryGetValue(entity.Id, out draws) && draws != null && draws.Length > 0;

        public static void Remove(Entity entity) => Frames.Remove(entity.Id);

        public static void Clear() => Frames.Clear();
    }
}

using System.Collections.Generic;
using Genesis.Shared.Interfaces;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.Rendering
{
    /// <summary>Per-entity procedural mesh batches submitted by scripts for Draw3D to draw.</summary>
    public static class ProceduralMeshDrawRegistry
    {
        // Version-stamped: a recycled entity id must not draw the destroyed entity's meshes.
        private static readonly Dictionary<int, (int Version, MeshDrawCall[] Draws)> Frames = new();

        public static void Set(Entity entity, MeshDrawCall[] draws)
        {
            if (draws == null || draws.Length == 0)
                Frames.Remove(entity.Id);
            else
                Frames[entity.Id] = (entity.Version, draws);
        }

        public static bool TryGet(Entity entity, out MeshDrawCall[] draws)
        {
            if (Frames.TryGetValue(entity.Id, out var stored) && stored.Version == entity.Version
                && stored.Draws is { Length: > 0 })
            {
                draws = stored.Draws;
                return true;
            }
            draws = null;
            return false;
        }

        public static void Remove(Entity entity) => Frames.Remove(entity.Id);

        public static void Clear() => Frames.Clear();
    }
}

using System.Numerics;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    // An instance's outline (InstanceSetOutline) reaches every 3D draw the instance makes: its
    // model's meshes, a script's procedural meshes and its image cube.
    public static partial class ObjectDrawPass
    {
        /// <summary>What an instance's draws are marked with, from its <see cref="ObjectDrawAssetEntry"/>.</summary>
        internal readonly record struct OutlineMark(Vector4 ColorWidth, int Id, bool ThroughWalls);

        internal static bool OutlineFor(ObjectDrawAssetEntry assets, Entity entity, out OutlineMark mark)
        {
            mark = default;
            if (assets?.Outline is not Vector4 outline || entity.Id <= 0) return false;
            mark = new OutlineMark(outline, entity.Id, assets.OutlineThroughWalls);
            return true;
        }

        private static void Mark(ref MeshDrawCall call, in OutlineMark mark)
        {
            call.Outline = mark.ColorWidth;
            call.OutlineId = mark.Id;
            call.OutlineThroughWalls = mark.ThroughWalls;
        }

        /// <summary>Marks the instance's draws in <paramref name="buffer"/>[start, end).</summary>
        private static void MarkOutline(ObjectDrawAssetEntry assets, Entity entity, MeshDrawCall[] buffer, int start, int end)
        {
            if (!OutlineFor(assets, entity, out OutlineMark mark)) return;
            for (int index = start; index < end; index++) Mark(ref buffer[index], mark);
        }

        /// <summary>Marks every draw added through it.</summary>
        private sealed class OutlineDrawList(IMeshDrawList target, OutlineMark mark) : IMeshDrawList
        {
            public int Count => target.Count;
            public void Clear() => target.Clear();
            public int CopyTo(MeshDrawCall[] buffer, int startIndex) => target.CopyTo(buffer, startIndex);
            public void Add(in MeshDrawCall call)
            {
                MeshDrawCall marked = call;
                Mark(ref marked, mark);
                target.Add(marked);
            }
        }

        /// <summary>The list draws go to: <paramref name="target"/>, marking them when the instance has an outline.</summary>
        private static IMeshDrawList Outlined(IMeshDrawList target, ObjectDrawAssetEntry assets, Entity entity) =>
            OutlineFor(assets, entity, out OutlineMark mark) ? new OutlineDrawList(target, mark) : target;
    }
}

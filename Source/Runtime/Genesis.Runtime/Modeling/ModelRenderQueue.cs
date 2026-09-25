using System;
using System.Collections.Generic;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// Mesh list adapter. Prefer renting via <see cref="Rent"/> so hot paths never allocate.
    /// Prefer writing into <see cref="FrameRenderQueue"/> / <see cref="IMeshDrawList"/> directly when available.
    /// </summary>
    public sealed class ModelRenderQueue : IMeshDrawList
    {
        [ThreadStatic] private static ModelRenderQueue t_rented;

        private readonly List<MeshDrawCall> _draws = new(1024);

        public int Count => _draws.Count;

        public void Clear() => _draws.Clear();

        public void Add(in MeshDrawCall call) => _draws.Add(call);

        /// <summary>Thread-local reusable queue — Clear'd on rent.</summary>
        public static ModelRenderQueue Rent()
        {
            t_rented ??= new ModelRenderQueue();
            t_rented.Clear();
            return t_rented;
        }

        public int CopyTo(MeshDrawCall[] buffer, int startIndex)
        {
            if (buffer == null) return startIndex;
            int count = startIndex;
            for (int i = 0; i < _draws.Count && count < buffer.Length; i++)
                buffer[count++] = _draws[i];
            return count;
        }

        public void Draw(IRenderController renderer)
        {
            if (renderer == null || _draws.Count == 0) return;
            renderer.DrawMeshBatch(CollectionsMarshalAsSpan(_draws));
        }

        /// <summary>Adjust every queued model draw before submission (shader/tint/preview state).</summary>
        public void Transform(Func<MeshDrawCall, MeshDrawCall> transform)
        {
            if (transform == null) return;
            for (int i = 0; i < _draws.Count; i++)
                _draws[i] = transform(_draws[i]);
        }

        private static ReadOnlySpan<MeshDrawCall> CollectionsMarshalAsSpan(List<MeshDrawCall> list)
        {
#if NET5_0_OR_GREATER
            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
#else
            return list.ToArray();
#endif
        }
    }
}

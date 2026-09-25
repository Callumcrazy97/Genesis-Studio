using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Collects per-frame render commands so runtime systems can share one explicit
    /// submission path before the backend-specific flush.
    /// </summary>
    public sealed class FrameRenderQueue : IRenderCommandSink, IMeshDrawList
    {
        private readonly List<SpriteDrawCall> _sprites = new(256);
        private readonly List<MeshDrawCall> _meshes = new(256);

        public int SpriteCount => _sprites.Count;
        public int MeshCount => _meshes.Count;
        public int Count => _meshes.Count;
        public bool IsEmpty => _sprites.Count == 0 && _meshes.Count == 0;

        public void Reset()
        {
            _sprites.Clear();
            _meshes.Clear();
        }

        public void Clear() => Reset();

        public void DrawSprite(in SpriteDrawCall call) => _sprites.Add(call);

        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls)
        {
            for (int i = 0; i < calls.Length; i++)
                _sprites.Add(calls[i]);
        }

        public void DrawMesh(in MeshDrawCall call) => _meshes.Add(call);

        public void Add(in MeshDrawCall call) => _meshes.Add(call);

        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls)
        {
            for (int i = 0; i < calls.Length; i++)
                _meshes.Add(calls[i]);
        }

        public int CopyTo(MeshDrawCall[] buffer, int startIndex)
        {
            if (buffer == null) return startIndex;
            int count = startIndex;
            for (int i = 0; i < _meshes.Count && count < buffer.Length; i++)
                buffer[count++] = _meshes[i];
            return count;
        }

        public void Flush(IRenderController renderer, bool includeMeshes = true, bool includeSprites = true)
        {
            if (renderer == null)
                return;

            if (includeMeshes && _meshes.Count > 0)
                renderer.DrawMeshBatch(CollectionsMarshal.AsSpan(_meshes));

            if (includeSprites && _sprites.Count > 0)
                renderer.DrawSpriteBatch(CollectionsMarshal.AsSpan(_sprites));
        }
    }
}

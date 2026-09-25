using System;
using CommunityToolkit.HighPerformance.Buffers;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS
{
    // Sparse-set component storage.
    // Read: O(1).  Write: O(1) amortised.  Remove: O(1) swap-erase.
    // AllComponents() returns a contiguous Span<T> safe for cache-friendly iteration.
    internal interface IComponentStoreInternal
    {
        void Remove(int entityId);
    }

    internal sealed class ComponentStore<T> : IComponentStoreInternal, IDisposable
        where T : struct, IComponent
    {
        private const int InitialCapacity = 64;

        private int[]            _sparse;       // entityId → dense index; -1 = absent
        private MemoryOwner<T>   _denseOwner;   // component values, packed
        private MemoryOwner<int> _entityMapOwner; // dense index → entityId
        private int              _count;

        internal int Count => _count;

        internal ComponentStore()
        {
            _sparse         = new int[InitialCapacity];
            _denseOwner     = MemoryOwner<T>.Allocate(InitialCapacity);
            _entityMapOwner = MemoryOwner<int>.Allocate(InitialCapacity);
            _sparse.AsSpan().Fill(-1);
        }

        internal bool Has(int entityId)
            => entityId < _sparse.Length && _sparse[entityId] >= 0;

        internal ref T GetRef(int entityId)
            => ref _denseOwner.Span[_sparse[entityId]];

        internal void Set(int entityId, in T value)
        {
            if (entityId >= _sparse.Length)
                GrowSparse(entityId + 1);

            if (_sparse[entityId] >= 0)
            {
                _denseOwner.Span[_sparse[entityId]] = value;
                return;
            }

            if (_count == _denseOwner.Length)
                GrowDense();

            _sparse[entityId]              = _count;
            _denseOwner.Span[_count]       = value;
            _entityMapOwner.Span[_count]   = entityId;
            _count++;
        }

        internal void Remove(int entityId)
        {
            if (!Has(entityId)) return;

            int denseIdx   = _sparse[entityId];
            int lastIdx    = _count - 1;
            int lastEntity = _entityMapOwner.Span[lastIdx];

            // Swap-erase: copy last element into the vacated slot
            _denseOwner.Span[denseIdx]     = _denseOwner.Span[lastIdx];
            _entityMapOwner.Span[denseIdx] = lastEntity;
            _sparse[lastEntity]            = denseIdx;
            _sparse[entityId]              = -1;
            _count--;
        }

        void IComponentStoreInternal.Remove(int entityId) => Remove(entityId);

        internal Span<T>   AllComponents() => _denseOwner.Span[.._count];
        internal Span<int> AllEntities()   => _entityMapOwner.Span[.._count];

        private void GrowSparse(int minSize)
        {
            int oldSize = _sparse.Length;
            int newSize = Math.Max(minSize, oldSize * 2);
            Array.Resize(ref _sparse, newSize);
            // Fill newly added slots with -1 (Array.Resize leaves them as default 0)
            _sparse.AsSpan(oldSize).Fill(-1);
        }

        private void GrowDense()
        {
            int newCap = _denseOwner.Length * 2;

            var newDense  = MemoryOwner<T>.Allocate(newCap);
            var newEntMap = MemoryOwner<int>.Allocate(newCap);

            _denseOwner.Span[.._count].CopyTo(newDense.Span);
            _entityMapOwner.Span[.._count].CopyTo(newEntMap.Span);

            _denseOwner.Dispose();
            _entityMapOwner.Dispose();

            _denseOwner     = newDense;
            _entityMapOwner = newEntMap;
        }

        public void Dispose()
        {
            _denseOwner.Dispose();
            _entityMapOwner.Dispose();
        }
    }
}

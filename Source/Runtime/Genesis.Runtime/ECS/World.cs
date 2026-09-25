using System;
using System.Collections.Generic;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS
{
    public sealed class World : IEcsWorld, IDisposable
    {
        private const int DefaultCapacity = 256;

        // Entity identity pool
        private int[]  _versions;   // version per slot — incremented on destroy
        private bool[] _alive;      // true while entity occupies this slot
        private int[]  _freeList;   // recycled slot ids
        private int    _freeCount;
        private int    _nextId = 1; // Zero is the PGSL no-instance sentinel; never allocate it.

        private readonly Dictionary<Type, object> _stores;

        // Exposed so SystemScheduler can flush it between Update and Render passes
        internal readonly DeferredCommandBuffer Deferred;

        public void FlushDeferred()
        {
            Deferred.Flush(this);
        }

        /// <summary>Destroy every live entity (used when swapping rooms in the same scene).</summary>
        public void DestroyAllEntities(Func<Entity, bool> keepEntity = null)
        {
            Deferred.Flush(this);
            int limit = _nextId;
            for (int id = 0; id < limit; id++)
            {
                if (!_alive[id]) continue;
                if (keepEntity?.Invoke(new Entity(id, _versions[id])) == true) continue;
                DestroyEntityImmediate(new Entity(id, _versions[id]));
            }
            Deferred.Flush(this);
        }

        public World(int initialCapacity = DefaultCapacity)
        {
            if (initialCapacity < 1) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            initialCapacity = Math.Max(2, initialCapacity);
            _versions = new int[initialCapacity];
            _alive    = new bool[initialCapacity];
            _freeList = new int[initialCapacity];
            _stores   = new Dictionary<Type, object>(16);
            Deferred  = new DeferredCommandBuffer();
        }

        // ── IEcsWorld ────────────────────────────────────────────────────────────

        public Entity CreateEntity()
        {
            int id;
            if (_freeCount > 0)
                id = _freeList[--_freeCount];
            else
            {
                id = _nextId++;
                if (id >= _versions.Length)
                    GrowEntityArrays();
            }
            _alive[id] = true;
            return new Entity(id, _versions[id]);
        }

        // Safe to call mid-system — queues to DeferredCommandBuffer
        public void DestroyEntity(Entity entity)
        {
            Deferred.EnqueueDestroy(entity);
        }

        public bool IsAlive(Entity entity)
        {
            return entity.Id >= 0
                && entity.Id < _alive.Length
                && _alive[entity.Id]
                && _versions[entity.Id] == entity.Version;
        }

        public Entity GetEntity(int id)
        {
            if (id >= 0 && id < _versions.Length && _alive[id])
                return new Entity(id, _versions[id]);
            return Entity.Null;
        }

        public int LivingEntityCount
        {
            get
            {
                int count = 0;
                int limit = _nextId;
                for (int i = 0; i < limit; i++)
                    if (_alive[i]) count++;
                return count;
            }
        }

        public bool Has<T>(Entity entity) where T : struct, IComponent
        {
            return GetStore<T>() is { } s && s.Has(entity.Id);
        }

        public void Set<T>(Entity entity, in T value) where T : struct, IComponent
        {
            GetOrCreateStore<T>().Set(entity.Id, value);
        }

        public ref T GetRef<T>(Entity entity) where T : struct, IComponent
        {
            return ref GetOrCreateStore<T>().GetRef(entity.Id);
        }

        // --- Delegate-free Enumerables ---

        public QueryEnumerable1<T1> Query<T1>()
            where T1 : struct, IComponent
        {
            return new QueryEnumerable1<T1>(_versions, _alive, GetStore<T1>());
        }

        public QueryEnumerable2<T1, T2> Query<T1, T2>()
            where T1 : struct, IComponent
            where T2 : struct, IComponent
        {
            return new QueryEnumerable2<T1, T2>(_versions, _alive, GetStore<T1>(), GetStore<T2>());
        }

        public QueryEnumerable3<T1, T2, T3> Query<T1, T2, T3>()
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent
        {
            return new QueryEnumerable3<T1, T2, T3>(_versions, _alive, GetStore<T1>(), GetStore<T2>(), GetStore<T3>());
        }

        // --- Delegate-based Queries ---

        public void Query<T1>(ComponentAction<T1> action)
            where T1 : struct, IComponent
        {
            var s1 = GetStore<T1>();
            if (s1 == null) return;
            Span<int> ids = s1.AllEntities();
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (!IsAliveById(id)) continue;
                action(new Entity(id, _versions[id]), ref s1.GetRef(id));
            }
        }

        public void Query<T1, T2>(ComponentAction<T1, T2> action)
            where T1 : struct, IComponent
            where T2 : struct, IComponent
        {
            var s1 = GetStore<T1>();
            var s2 = GetStore<T2>();
            if (s1 == null || s2 == null) return;
            Span<int> ids = s1.AllEntities();
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (!IsAliveById(id) || !s2.Has(id)) continue;
                action(new Entity(id, _versions[id]), ref s1.GetRef(id), ref s2.GetRef(id));
            }
        }

        public void Query<T1, T2, T3>(ComponentAction<T1, T2, T3> action)
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent
        {
            var s1 = GetStore<T1>();
            var s2 = GetStore<T2>();
            var s3 = GetStore<T3>();
            if (s1 == null || s2 == null || s3 == null) return;
            Span<int> ids = s1.AllEntities();
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (!IsAliveById(id) || !s2.Has(id) || !s3.Has(id)) continue;
                action(new Entity(id, _versions[id]), ref s1.GetRef(id), ref s2.GetRef(id), ref s3.GetRef(id));
            }
        }

        // ── Internal ─────────────────────────────────────────────────────────────

        // Called only by DeferredCommandBuffer.Flush — never call from systems
        internal void DestroyEntityImmediate(Entity entity)
        {
            if (!IsAlive(entity)) return;
            int id = entity.Id;
            Genesis.Runtime.Imaging.SpriteRigRuntime.Clear(this, entity);

            _alive[id] = false;
            _versions[id]++;

            foreach (var store in _stores.Values)
                ((IComponentStoreInternal)store).Remove(id);

            if (_freeCount == _freeList.Length)
                Array.Resize(ref _freeList, _freeList.Length * 2);
            _freeList[_freeCount++] = id;
        }

        private bool IsAliveById(int id)
            => id >= 0 && id < _alive.Length && _alive[id];

        private ComponentStore<T> GetStore<T>() where T : struct, IComponent
        {
            _stores.TryGetValue(typeof(T), out var s);
            return s as ComponentStore<T>;
        }

        private ComponentStore<T> GetOrCreateStore<T>() where T : struct, IComponent
        {
            if (!_stores.TryGetValue(typeof(T), out var s))
            {
                s = new ComponentStore<T>();
                _stores[typeof(T)] = s;
            }
            return (ComponentStore<T>)s;
        }

        private void GrowEntityArrays()
        {
            int newSize = _versions.Length * 2;
            Array.Resize(ref _versions, newSize);
            Array.Resize(ref _alive,    newSize);
        }

        public void Dispose()
        {
            Query<Genesis.Runtime.ECS.Components.PixelRigSpriteComponent>((Entity entity, ref Genesis.Runtime.ECS.Components.PixelRigSpriteComponent component) =>
            { component.Binding?.Dispose(); component.Binding = null; });
            foreach (var store in _stores.Values)
                if (store is IDisposable d) d.Dispose();
            _stores.Clear();
        }
    }
}

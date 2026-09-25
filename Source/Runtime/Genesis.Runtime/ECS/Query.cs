using System;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS
{
    // --- 1 Component Query ---
    
    public readonly ref struct QueryItem1<T1>
        where T1 : struct, IComponent
    {
        public readonly Entity Entity;
        public readonly ref T1 C1;

        public QueryItem1(Entity entity, ref T1 c1)
        {
            Entity = entity;
            C1 = ref c1;
        }
    }

    public ref struct QueryEnumerator1<T1>
        where T1 : struct, IComponent
    {
        private readonly int[] _versions;
        private readonly bool[] _alive;
        private readonly ComponentStore<T1> _s1;
        private readonly Span<int> _ids;
        private int _index;

        internal QueryEnumerator1(int[] versions, bool[] alive, ComponentStore<T1> s1)
        {
            _versions = versions;
            _alive = alive;
            _s1 = s1;
            _ids = s1 != null ? s1.AllEntities() : Span<int>.Empty;
            _index = -1;
        }

        public bool MoveNext()
        {
            while (++_index < _ids.Length)
            {
                int id = _ids[_index];
                if (id >= 0 && id < _alive.Length && _alive[id])
                {
                    return true;
                }
            }
            return false;
        }

        public QueryItem1<T1> Current
        {
            get
            {
                int id = _ids[_index];
                return new QueryItem1<T1>(new Entity(id, _versions[id]), ref _s1.GetRef(id));
            }
        }
    }

    public readonly ref struct QueryEnumerable1<T1>
        where T1 : struct, IComponent
    {
        private readonly int[] _versions;
        private readonly bool[] _alive;
        private readonly ComponentStore<T1> _s1;

        internal QueryEnumerable1(int[] versions, bool[] alive, ComponentStore<T1> s1)
        {
            _versions = versions;
            _alive = alive;
            _s1 = s1;
        }

        public QueryEnumerator1<T1> GetEnumerator() => new QueryEnumerator1<T1>(_versions, _alive, _s1);
    }

    // --- 2 Component Query ---

    public readonly ref struct QueryItem2<T1, T2>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        public readonly Entity Entity;
        public readonly ref T1 C1;
        public readonly ref T2 C2;

        public QueryItem2(Entity entity, ref T1 c1, ref T2 c2)
        {
            Entity = entity;
            C1 = ref c1;
            C2 = ref c2;
        }
    }

    public ref struct QueryEnumerator2<T1, T2>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        private readonly int[] _versions;
        private readonly bool[] _alive;
        private readonly ComponentStore<T1> _s1;
        private readonly ComponentStore<T2> _s2;
        private readonly Span<int> _ids;
        private int _index;

        internal QueryEnumerator2(int[] versions, bool[] alive, ComponentStore<T1> s1, ComponentStore<T2> s2)
        {
            _versions = versions;
            _alive = alive;
            _s1 = s1;
            _s2 = s2;
            _ids = s1 != null ? s1.AllEntities() : Span<int>.Empty;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_s2 == null) return false;
            while (++_index < _ids.Length)
            {
                int id = _ids[_index];
                if (id >= 0 && id < _alive.Length && _alive[id] && _s2.Has(id))
                {
                    return true;
                }
            }
            return false;
        }

        public QueryItem2<T1, T2> Current
        {
            get
            {
                int id = _ids[_index];
                return new QueryItem2<T1, T2>(new Entity(id, _versions[id]), ref _s1.GetRef(id), ref _s2.GetRef(id));
            }
        }
    }

    public readonly ref struct QueryEnumerable2<T1, T2>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        private readonly int[] _versions;
        private readonly bool[] _alive;
        private readonly ComponentStore<T1> _s1;
        private readonly ComponentStore<T2> _s2;

        internal QueryEnumerable2(int[] versions, bool[] alive, ComponentStore<T1> s1, ComponentStore<T2> s2)
        {
            _versions = versions;
            _alive = alive;
            _s1 = s1;
            _s2 = s2;
        }

        public QueryEnumerator2<T1, T2> GetEnumerator() => new QueryEnumerator2<T1, T2>(_versions, _alive, _s1, _s2);
    }

    // --- 3 Component Query ---

    public readonly ref struct QueryItem3<T1, T2, T3>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        public readonly Entity Entity;
        public readonly ref T1 C1;
        public readonly ref T2 C2;
        public readonly ref T3 C3;

        public QueryItem3(Entity entity, ref T1 c1, ref T2 c2, ref T3 c3)
        {
            Entity = entity;
            C1 = ref c1;
            C2 = ref c2;
            C3 = ref c3;
        }
    }

    public ref struct QueryEnumerator3<T1, T2, T3>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        private readonly int[] _versions;
        private readonly bool[] _alive;
        private readonly ComponentStore<T1> _s1;
        private readonly ComponentStore<T2> _s2;
        private readonly ComponentStore<T3> _s3;
        private readonly Span<int> _ids;
        private int _index;

        internal QueryEnumerator3(int[] versions, bool[] alive, ComponentStore<T1> s1, ComponentStore<T2> s2, ComponentStore<T3> s3)
        {
            _versions = versions;
            _alive = alive;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;
            _ids = s1 != null ? s1.AllEntities() : Span<int>.Empty;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_s2 == null || _s3 == null) return false;
            while (++_index < _ids.Length)
            {
                int id = _ids[_index];
                if (id >= 0 && id < _alive.Length && _alive[id] && _s2.Has(id) && _s3.Has(id))
                {
                    return true;
                }
            }
            return false;
        }

        public QueryItem3<T1, T2, T3> Current
        {
            get
            {
                int id = _ids[_index];
                return new QueryItem3<T1, T2, T3>(new Entity(id, _versions[id]), ref _s1.GetRef(id), ref _s2.GetRef(id), ref _s3.GetRef(id));
            }
        }
    }

    public readonly ref struct QueryEnumerable3<T1, T2, T3>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        private readonly int[] _versions;
        private readonly bool[] _alive;
        private readonly ComponentStore<T1> _s1;
        private readonly ComponentStore<T2> _s2;
        private readonly ComponentStore<T3> _s3;

        internal QueryEnumerable3(int[] versions, bool[] alive, ComponentStore<T1> s1, ComponentStore<T2> s2, ComponentStore<T3> s3)
        {
            _versions = versions;
            _alive = alive;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;
        }

        public QueryEnumerator3<T1, T2, T3> GetEnumerator() => new QueryEnumerator3<T1, T2, T3>(_versions, _alive, _s1, _s2, _s3);
    }
}


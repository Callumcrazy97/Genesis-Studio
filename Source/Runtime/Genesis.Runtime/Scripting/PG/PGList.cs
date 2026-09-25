using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting.PG
{
    /// <summary>Array-backed list for PGSL collections (replaces boxed List&lt;object&gt; for hot paths).</summary>
    public sealed class PGList
    {
        private readonly List<double> _numbers = new();
        private readonly List<object> _objects = new();
        private bool _useObjects;

        public int Count => _useObjects ? _objects.Count : _numbers.Count;

        public static PGList Create() => new PGList();

        public void Clear()
        {
            _numbers.Clear();
            _objects.Clear();
            _useObjects = false;
        }

        public void Add(object value)
        {
            if (value is double d)
            {
                if (!_useObjects && _objects.Count == 0)
                    _numbers.Add(d);
                else
                {
                    PromoteToObjects();
                    _objects.Add(d);
                }
            }
            else if (value is int i)
            {
                Add((double)i);
            }
            else if (value is float f)
            {
                Add((double)f);
            }
            else
            {
                PromoteToObjects();
                _objects.Add(value);
            }
        }

        public object Get(int index)
        {
            if (index < 0) return 0;
            if (_useObjects)
                return index < _objects.Count ? _objects[index] : 0;
            return index < _numbers.Count ? _numbers[index] : 0;
        }

        public void Set(int index, object value)
        {
            if (index < 0) return;
            if (_useObjects)
            {
                while (_objects.Count <= index)
                    _objects.Add(0);
                _objects[index] = Coerce(value);
            }
            else if (value is double d || value is int || value is float)
            {
                while (_numbers.Count <= index)
                    _numbers.Add(0);
                _numbers[index] = Convert.ToDouble(value);
            }
            else
            {
                PromoteToObjects();
                while (_objects.Count <= index)
                    _objects.Add(0);
                _objects[index] = value;
            }
        }

        public void RemoveAt(int index)
        {
            if (index < 0) return;
            if (_useObjects)
            {
                if (index < _objects.Count)
                    _objects.RemoveAt(index);
            }
            else if (index < _numbers.Count)
                _numbers.RemoveAt(index);
        }

        private void PromoteToObjects()
        {
            if (_useObjects) return;
            _useObjects = true;
            foreach (var n in _numbers)
                _objects.Add(n);
            _numbers.Clear();
        }

        private static object Coerce(object value)
        {
            if (value is int i) return (double)i;
            if (value is float f) return (double)f;
            return value ?? 0;
        }
    }
}

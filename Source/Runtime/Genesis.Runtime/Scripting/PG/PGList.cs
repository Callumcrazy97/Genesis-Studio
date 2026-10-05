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

        /// <summary>Puts a value at an index, moving the later ones up; past the end it is added.</summary>
        public void Insert(int index, object value)
        {
            index = Math.Clamp(index, 0, Count);
            if (!_useObjects && value is double or int or float)
            {
                _numbers.Insert(index, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            PromoteToObjects();
            _objects.Insert(index, Coerce(value));
        }

        /// <summary>The first index holding an equal value (numbers by value, text exactly), or -1.</summary>
        public int IndexOf(object value)
        {
            for (int index = 0; index < Count; index++)
                if (SameValue(Get(index), value)) return index;
            return -1;
        }

        /// <summary>Numbers before text; numbers by value and text in ordinal order.</summary>
        public void Sort(bool ascending)
        {
            if (!_useObjects) _numbers.Sort();
            else _objects.Sort(CompareValues);
            if (ascending) return;
            if (_useObjects) _objects.Reverse();
            else _numbers.Reverse();
        }

        /// <summary>Swaps entries into a random order, drawing indices from <paramref name="next"/> (0 to n-1).</summary>
        public void Shuffle(Func<int, int> next)
        {
            for (int index = Count - 1; index > 0; index--)
            {
                int swap = next(index + 1);
                object held = Get(index);
                Set(index, Get(swap));
                Set(swap, held);
            }
        }

        public PGList Copy()
        {
            var copy = new PGList();
            for (int index = 0; index < Count; index++) copy.Add(Get(index));
            return copy;
        }

        /// <summary>The list as text, such as [1, 2.5, "a"]; nested lists are written inside.</summary>
        public override string ToString()
        {
            var text = new System.Text.StringBuilder("[");
            for (int index = 0; index < Count; index++)
            {
                if (index > 0) text.Append(", ");
                object value = Get(index);
                text.Append(value switch
                {
                    string s => "\"" + s + "\"",
                    bool flag => flag ? "true" : "false",
                    IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                    _ => value?.ToString() ?? "",
                });
            }
            return text.Append(']').ToString();
        }

        private static bool SameValue(object left, object right)
        {
            if (left is string || right is string) return string.Equals(left as string, right as string, StringComparison.Ordinal);
            if (IsNumber(left) && IsNumber(right))
                return Convert.ToDouble(left, System.Globalization.CultureInfo.InvariantCulture)
                    == Convert.ToDouble(right, System.Globalization.CultureInfo.InvariantCulture);
            return Equals(left, right);
        }

        private static int CompareValues(object left, object right)
        {
            bool leftNumber = IsNumber(left), rightNumber = IsNumber(right);
            if (leftNumber && rightNumber)
                return Convert.ToDouble(left, System.Globalization.CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDouble(right, System.Globalization.CultureInfo.InvariantCulture));
            if (leftNumber != rightNumber) return leftNumber ? -1 : 1;
            return string.CompareOrdinal(left?.ToString(), right?.ToString());
        }

        private static bool IsNumber(object value) => value is double or int or float or bool;

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

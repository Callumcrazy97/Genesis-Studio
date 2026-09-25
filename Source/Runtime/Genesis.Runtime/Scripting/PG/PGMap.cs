using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting.PG
{
    /// <summary>String-keyed map for PGSL (faster than Dictionary in Variables for typed access).</summary>
    public sealed class PGMap
    {
        private readonly Dictionary<string, object> _data = new(StringComparer.OrdinalIgnoreCase);

        public static PGMap Create() => new PGMap();

        public int Count => _data.Count;

        public bool ContainsKey(string key) => _data.ContainsKey(key);

        public object Get(string key) => _data.TryGetValue(key, out var v) ? v : 0;

        public void Set(string key, object value) => _data[key ?? ""] = value ?? 0;

        public void Remove(string key) => _data.Remove(key ?? "");
    }
}

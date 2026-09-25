using System.Collections.Generic;

namespace Genesis.Runtime.Scripting.PG
{
    /// <summary>Handle table for DsList* commands when <see cref="GameSettings.Settings.UsePgCollections"/> is true.</summary>
    public static class PGCollectionRegistry
    {
        private static readonly object _lock = new();
        private static int _nextId = 1;
        private static readonly Dictionary<int, PGList> _lists = new();

        public static int CreateList()
        {
            lock (_lock)
            {
                int id = _nextId++;
                _lists[id] = PGList.Create();
                return id;
            }
        }

        public static PGList GetList(int id)
        {
            lock (_lock)
            {
                return _lists.TryGetValue(id, out var list) ? list : null;
            }
        }

        public static bool DestroyList(int id)
        {
            lock (_lock)
            {
                return _lists.Remove(id);
            }
        }

        public static void ClearAll()
        {
            lock (_lock)
            {
                _lists.Clear();
                _nextId = 1;
            }
        }
    }
}

using System.Collections.Generic;

namespace Genesis.Runtime.Scripting
{
    internal static class DataStructures
    {
        private static int nextId = 1;
        private static Dictionary<int, List<object>> lists = new();
        private static Dictionary<int, Dictionary<string, object>> maps = new();
        private static Dictionary<int, object[,]> grids = new();

        public static int CreateList() { int id = nextId++; lists[id] = new List<object>(); return id; }
        public static void ListAdd(int id, object value) { if (lists.TryGetValue(id, out var l)) l.Add(value); }
        public static void ListClear(int id) { if (lists.TryGetValue(id, out var l)) l.Clear(); }
        public static int ListSize(int id) { return lists.TryGetValue(id, out var l) ? l.Count : 0; }
        public static void ListDelete(int id, int index) { if (lists.TryGetValue(id, out var l) && index >= 0 && index < l.Count) l.RemoveAt(index); }
        public static object ListFind(int id, int index) { return lists.TryGetValue(id, out var l) && index >= 0 && index < l.Count ? l[index] : null; }

        public static int CreateMap() { int id = nextId++; maps[id] = new Dictionary<string, object>(); return id; }
        public static void MapAdd(int id, string key, object value) { if (maps.TryGetValue(id, out var m)) m[key] = value; }
        public static bool MapExists(int id, string key) { return maps.TryGetValue(id, out var m) && m.ContainsKey(key); }
        public static object MapFind(int id, string key) { return maps.TryGetValue(id, out var m) && m.TryGetValue(key, out var v) ? v : null; }

        public static int CreateGrid(int w, int h) { int id = nextId++; grids[id] = new object[w, h]; return id; }
        public static void GridSet(int id, int x, int y, object value) { if (grids.TryGetValue(id, out var g) && x >= 0 && x < g.GetLength(0) && y >= 0 && y < g.GetLength(1)) g[x, y] = value; }
        public static object GridGet(int id, int x, int y) { return grids.TryGetValue(id, out var g) && x >= 0 && x < g.GetLength(0) && y >= 0 && y < g.GetLength(1) ? g[x, y] : null; }
        public static void GridDestroy(int id) { grids.Remove(id); }
    }
}

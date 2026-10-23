using System.Collections.Generic;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// The storage of large DsLists destroyed, kept for DsListReserve on an empty list: a game that
/// makes a list for each chunk's faces, reserves room in it and destroys it afterwards made a new
/// large-object-heap array every time (about 280 MB a run in GenesisCraft). Worker jobs make and
/// destroy lists too, so it is locked.
/// </summary>
internal static class ListStorage
{
    /// <summary>Smaller lists are left to the collector: they are cheap to make.</summary>
    public const int MinimumKept = 1024;
    /// <summary>The most entries kept in all (about 32 MB of references).</summary>
    public const long MaxKeptEntries = 4_000_000;

    private static readonly List<List<object>> Kept = new();
    private static long _keptEntries;

    public static long KeptEntries { get { lock (Kept) return _keptEntries; } }

    public static void Return(List<object> list)
    {
        if (list == null || list.Capacity < MinimumKept) return;
        list.Clear();
        lock (Kept)
        {
            if (_keptEntries + list.Capacity > MaxKeptEntries) return;
            Kept.Add(list);
            _keptEntries += list.Capacity;
        }
    }

    /// <summary>The smallest kept list with room for this many entries, emptied; null when none.</summary>
    public static List<object> Take(int wanted)
    {
        lock (Kept)
        {
            int best = -1;
            for (int i = 0; i < Kept.Count; i++)
                if (Kept[i].Capacity >= wanted && (best < 0 || Kept[i].Capacity < Kept[best].Capacity)) best = i;
            if (best < 0) return null;
            List<object> list = Kept[best];
            Kept[best] = Kept[^1];
            Kept.RemoveAt(Kept.Count - 1);
            _keptEntries -= list.Capacity;
            return list;
        }
    }

    /// <summary>Forgets what is kept (a new game).</summary>
    public static void Clear()
    {
        lock (Kept) { Kept.Clear(); _keptEntries = 0; }
    }
}

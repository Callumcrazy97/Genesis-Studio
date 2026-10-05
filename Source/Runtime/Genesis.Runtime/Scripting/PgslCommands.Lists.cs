using System;
using Genesis.Runtime.Scripting.PG;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Lists that are values: `var a = [1, 2, 3];` makes one (the parser calls PgListCreate), `a[i]` reads
// and `a[i] = v` writes it. A list is passed by reference and freed when nothing holds it. Like the
// other collection commands these ignore a value that is not a list; indexing past the end is an error.
public static partial class PgslCommands
{
    [PgslCommand("PgListCreate", "PgListCreate(values...) -> list",
        "A new list holding the values given, in order; what an array literal such as [1, 2, 3] makes", "Lists")]
    public static PGList PgListCreate(params object[] values)
    {
        var list = new PGList();
        if (values != null)
            foreach (object value in values) list.Add(value);
        return list;
    }

    [PgslCommand("PgListSize", "PgListSize(list) -> number", "How many entries a list holds", "Lists")]
    public static double PgListSize(object list) => list is PGList pgList ? pgList.Count : 0;

    [PgslCommand("PgListGet", "PgListGet(list, index) -> value", "The entry at an index (from 0); an error past the end", "Lists")]
    public static object PgListGet(object list, double index) => list is PGList pgList ? ReadListEntry(pgList, index) : 0.0;

    [PgslCommand("PgListSet", "PgListSet(list, index, value)", "Set the entry at an index; past the end the list grows, new entries 0", "Lists")]
    public static void PgListSet(object list, double index, object value)
    {
        if (list is PGList pgList) WriteListEntry(pgList, index, value);
    }

    [PgslCommand("PgListAdd", "PgListAdd(list, value)", "Add a value to the end", "Lists")]
    public static void PgListAdd(object list, object value) => (list as PGList)?.Add(value);

    [PgslCommand("PgListInsert", "PgListInsert(list, index, value)", "Put a value at an index, moving the later entries up", "Lists")]
    public static void PgListInsert(object list, double index, object value) =>
        (list as PGList)?.Insert(double.IsFinite(index) ? (int)index : 0, value);

    [PgslCommand("PgListRemove", "PgListRemove(list, index)", "Remove the entry at an index, moving the later entries down", "Lists")]
    public static void PgListRemove(object list, double index)
    {
        if (list is PGList pgList && double.IsFinite(index)) pgList.RemoveAt((int)index);
    }

    [PgslCommand("PgListClear", "PgListClear(list)", "Remove every entry", "Lists")]
    public static void PgListClear(object list) => (list as PGList)?.Clear();

    [PgslCommand("PgListPop", "PgListPop(list) -> value", "Remove and return the last entry (0 when empty)", "Lists")]
    public static object PgListPop(object list)
    {
        if (list is not PGList pgList || pgList.Count == 0) return 0.0;
        object value = pgList.Get(pgList.Count - 1);
        pgList.RemoveAt(pgList.Count - 1);
        return value;
    }

    [PgslCommand("PgListDequeue", "PgListDequeue(list) -> value", "Remove and return the first entry (0 when empty)", "Lists")]
    public static object PgListDequeue(object list)
    {
        if (list is not PGList pgList || pgList.Count == 0) return 0.0;
        object value = pgList.Get(0);
        pgList.RemoveAt(0);
        return value;
    }

    [PgslCommand("PgListFind", "PgListFind(list, value) -> number", "The first index holding the value, or -1", "Lists")]
    public static double PgListFind(object list, object value) => list is PGList pgList ? pgList.IndexOf(value) : -1;

    [PgslCommand("PgListSort", "PgListSort(list, ascending)", "Sort in place: numbers by value before text", "Lists")]
    public static void PgListSort(object list, bool ascending) => (list as PGList)?.Sort(ascending);

    [PgslCommand("PgListShuffle", "PgListShuffle(list)", "Put the entries in a random order (RandomSeed repeats it)", "Lists")]
    public static void PgListShuffle(object list) => (list as PGList)?.Shuffle(NextRandomInt);

    [PgslCommand("PgListCopy", "PgListCopy(list) -> list", "A new list with the same entries (nested lists are shared)", "Lists")]
    public static PGList PgListCopy(object list) => list is PGList pgList ? pgList.Copy() : new PGList();

    [PgslCommand("IsList", "IsList(value) -> bool", "Whether a value is a list", "Lists")]
    public static bool IsList(object value) => value is PGList;

    /// <summary>An entry of a list, with an error that says why when the index is outside it.</summary>
    internal static object ReadListEntry(PGList list, double index)
    {
        if (!double.IsFinite(index) || index < 0 || index >= list.Count)
            throw new InvalidOperationException(list.Count == 0
                ? $"List index {FormatIndex(index)} is outside the list: it is empty."
                : $"List index {FormatIndex(index)} is outside the list: it has {list.Count} entries, 0 to {list.Count - 1}.");
        return list.Get((int)index);
    }

    internal static void WriteListEntry(PGList list, double index, object value)
    {
        if (!double.IsFinite(index) || index < 0)
            throw new InvalidOperationException($"List index {FormatIndex(index)} cannot be written: indices start at 0.");
        if (index > 1_000_000)
            throw new InvalidOperationException($"List index {FormatIndex(index)} is too large (the most is 1000000).");
        list.Set((int)index, value);
    }

    private static string FormatIndex(double index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

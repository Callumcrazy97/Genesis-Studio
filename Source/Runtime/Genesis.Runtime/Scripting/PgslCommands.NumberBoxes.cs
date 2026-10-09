namespace Genesis.Runtime.Scripting;

// Numbers kept in lists, stacks, queues, maps and named arrays are stored as objects, so storing one
// used to allocate a box every time (System.Double was among the most allocated types of a voxel
// game). Whole numbers are what games store most (block kinds, flags, handles, coordinates), so those
// from -1 024 to 65 535 are boxed once and reused: storing one allocates nothing. A boxed number is
// never changed in place, so entries sharing one cannot tell; -0 and every other number get a box of
// their own as before.
public static partial class PgslCommands
{
    private const int BoxedNumbersBelowZero = 1024;
    private static readonly object[] BoxedNumbers = new object[BoxedNumbersBelowZero + 65536];

    /// <summary>The number as an object for a collection: a shared box for a small whole number, a new one otherwise.</summary>
    internal static object BoxNumber(double value)
    {
        int whole = (int)value;
        uint slot = (uint)(whole + BoxedNumbersBelowZero);
        if (ScriptingDebugSettings.VmSharedNumberBoxes && whole == value && slot < (uint)BoxedNumbers.Length && (whole != 0 || !double.IsNegative(value)))
            return BoxedNumbers[slot] ??= value;
        return value;
    }
}

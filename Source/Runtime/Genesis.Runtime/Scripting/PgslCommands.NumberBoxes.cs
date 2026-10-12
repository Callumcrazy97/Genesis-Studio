using System;

namespace Genesis.Runtime.Scripting;

// Numbers kept in lists, stacks, queues, maps and named arrays are stored as objects, so storing one
// used to allocate a box every time (System.Double was among the most allocated types of a voxel
// game). Whole numbers are what games store most (block kinds, flags, handles, coordinates), so those
// from -1 024 to 65 535 are boxed once and reused: storing one allocates nothing. Other numbers a game
// stores tend to repeat too: a mesh builder adds the same texture coordinates, light levels and
// half-block corners for face after face. The last box made for each of 32 768 slots (picked by the
// number's bits) is kept, and storing that same number again reuses it. A boxed number is never
// changed in place, so entries sharing one cannot tell; -0 and every other number get a box as before.
public static partial class PgslCommands
{
    private const int BoxedNumbersBelowZero = 1024;
    private static readonly object[] BoxedNumbers = new object[BoxedNumbersBelowZero + 65536];
    private const int RecentBoxBits = 15;
    private static readonly object[] RecentBoxes = new object[1 << RecentBoxBits];

    /// <summary>The number as an object for a collection: a shared box for a small whole number or one stored lately, a new one otherwise.</summary>
    internal static object BoxNumber(double value)
    {
        if (!ScriptingDebugSettings.VmSharedNumberBoxes) return value;
        int whole = (int)value;
        uint slot = (uint)(whole + BoxedNumbersBelowZero);
        if (whole == value && slot < (uint)BoxedNumbers.Length && (whole != 0 || !double.IsNegative(value)))
            return BoxedNumbers[slot] ??= value;

        // The same bits (not just an equal value), so -0 and 0, and each NaN, keep boxes of their own.
        long bits = BitConverter.DoubleToInt64Bits(value);
        int index = (int)(unchecked((ulong)bits * 0x9E3779B97F4A7C15UL) >> (64 - RecentBoxBits));
        object recent = RecentBoxes[index];
        if (recent is double kept && BitConverter.DoubleToInt64Bits(kept) == bits)
        {
            if (PgslAllocationLedger.Enabled) PgslAllocationLedger.CountBox(made: false);
            return recent;
        }
        object box = value;
        RecentBoxes[index] = box;
        if (PgslAllocationLedger.Enabled) PgslAllocationLedger.CountBox(made: true);
        return box;
    }
}

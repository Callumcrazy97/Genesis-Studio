#nullable disable
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// One box for each small whole number and for true and false, shared by every value that goes
/// where an object is wanted (a DsList entry, a map value, a command's any-type argument). A boxed
/// number cannot change, so sharing is safe; a voxel game's lists of block kinds and coordinates
/// boxed a new double for every entry it added (DsListAdd averaged 14 bytes a call).
/// </summary>
internal static class BoxedNumbers
{
    public const int Lowest = -1024, Highest = 65535;

    public static readonly object True = true, False = false;

    private static readonly object[] Doubles = new object[Highest - Lowest + 1];
    private static readonly object[] Integers = new object[Highest - Lowest + 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Of(double value)
    {
        int whole = (int)value;
        // Whole numbers in range, not -0 (its box must stay -0).
        if (whole != value || whole < Lowest || whole > Highest || (whole == 0 && double.IsNegative(value))) return value;
        // Made on first use; two threads making the same one at once is harmless.
        return Doubles[whole - Lowest] ??= value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Of(int value) =>
        value < Lowest || value > Highest ? value : Integers[value - Lowest] ??= value;
}

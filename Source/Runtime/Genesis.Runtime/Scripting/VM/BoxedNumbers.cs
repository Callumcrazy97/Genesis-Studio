#nullable disable
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// One box for true and false and for each small whole integer, shared by every value that goes
/// where an object is wanted (a DsList entry, a map value, a command's any-type argument); doubles
/// use <see cref="PgslCommands.BoxNumber"/>'s boxes. A box cannot change, so sharing is safe.
/// </summary>
internal static class BoxedNumbers
{
    public const int Lowest = -1024, Highest = 65535;

    public static readonly object True = true, False = false;

    private static readonly object[] Integers = new object[Highest - Lowest + 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Of(int value) =>
        value < Lowest || value > Highest ? value : Integers[value - Lowest] ??= value;
}

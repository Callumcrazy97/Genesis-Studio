using System.Globalization;
using System.Threading;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// A number given where a command takes text (a map key, text to draw) as the text it has always
/// become. Whole numbers from -1 048 576 to 1 048 575 come from a table filled as each is first
/// used, so a game that keys its maps by number allocates nothing per call; any other number is
/// formatted each time. Worker jobs share the table.
/// </summary>
internal static class PgslNumberText
{
    private const int PageBits = 12;
    private const int PageSize = 1 << PageBits;
    private const int Range = 1 << 20;
    private static readonly string[][] Pages = new string[(2 * Range) >> PageBits][];

    public static string Of(double number)
    {
        if (number >= -Range && number < Range)
        {
            int whole = (int)number;
            // -0 is text of its own ("-0"), as it always was.
            if (whole == number && (whole != 0 || !double.IsNegative(number)))
            {
                int slot = whole + Range;
                ref string[] pageRef = ref Pages[slot >> PageBits];
                string[] page = Volatile.Read(ref pageRef);
                if (page == null)
                {
                    Interlocked.CompareExchange(ref pageRef, new string[PageSize], null);
                    page = Volatile.Read(ref pageRef);
                }
                return page[slot & (PageSize - 1)] ??= Format(number);
            }
        }
        return Format(number);
    }

    // What a number given for text has always become: the boxed number's ToString (ConvertArg).
    private static string Format(double number) => number.ToString(CultureInfo.CurrentCulture);
}

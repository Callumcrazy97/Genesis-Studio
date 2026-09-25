using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// String handling. Until this existed a script could not build "SCORE " + points, so the 2D
// acceptance gate had to draw a constant label — dynamic HUD text was literally unauthorable.
//
// Two conventions, both deliberate:
//   • Indices are 1-BASED, matching the legacy catalogue and GameMaker. Off-by-one confusion is
//     worse than the inconsistency with the 0-based ds_* families, which follow their own heritage.
//   • Every number<->string conversion pins InvariantCulture, so a game does not silently render
//     "3,5" instead of "3.5" for a player with a European locale.
public static partial class PgslCommands
{
    #region Strings

    [PgslCommand("StringOf", "StringOf(value) -> string", "Number as text; whole numbers lose the trailing .0", "Strings")]
    public static string StringOf(double value)
    {
        // A score of 3 must render "SCORE 3", not "SCORE 3.0" — the whole point of this command.
        if (Math.Abs(value % 1d) < 1e-9 && Math.Abs(value) < 1e15)
        {
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("0.############", CultureInfo.InvariantCulture);
    }

    [PgslCommand("RealOf", "RealOf(text) -> number", "Parse text as a number; 0 when it is not one", "Strings")]
    public static double RealOf(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0d;

    [PgslCommand("StringFormatNumber", "StringFormatNumber(value, decimals) -> string", "Number as text with fixed decimals", "Strings")]
    public static string StringFormatNumber(double value, double decimals) =>
        value.ToString("F" + (int)Math.Clamp(decimals, 0, 15), CultureInfo.InvariantCulture);

    [PgslCommand("StringLength", "StringLength(text) -> number", "Character count", "Strings")]
    public static double StringLength(string text) => text?.Length ?? 0;

    [PgslCommand("StringUpper", "StringUpper(text) -> string", "Upper case", "Strings")]
    public static string StringUpper(string text) => text?.ToUpperInvariant() ?? string.Empty;

    [PgslCommand("StringLower", "StringLower(text) -> string", "Lower case", "Strings")]
    public static string StringLower(string text) => text?.ToLowerInvariant() ?? string.Empty;

    [PgslCommand("StringTrim", "StringTrim(text) -> string", "Remove leading and trailing whitespace", "Strings")]
    public static string StringTrim(string text) => text?.Trim() ?? string.Empty;

    [PgslCommand("StringCopy", "StringCopy(text, index, count) -> string", "Substring; index is 1-based", "Strings")]
    public static string StringCopy(string text, double index, double count)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int start = (int)Math.Clamp(index - 1, 0, text.Length);
        int take = (int)Math.Clamp(count, 0, text.Length - start);
        return text.Substring(start, take);
    }

    [PgslCommand("StringCharAt", "StringCharAt(text, index) -> string", "One character; index is 1-based", "Strings")]
    public static string StringCharAt(string text, double index)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int at = (int)index - 1;
        return at >= 0 && at < text.Length ? text[at].ToString() : string.Empty;
    }

    [PgslCommand("StringOrdAt", "StringOrdAt(text, index) -> number", "Character code at a 1-based index", "Strings")]
    public static double StringOrdAt(string text, double index)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int at = (int)index - 1;
        return at >= 0 && at < text.Length ? text[at] : 0;
    }

    [PgslCommand("StringChr", "StringChr(code) -> string", "Character for a character code", "Strings")]
    public static string StringChr(double code)
    {
        int value = (int)code;
        return value is >= 0 and <= char.MaxValue ? ((char)value).ToString() : string.Empty;
    }

    [PgslCommand("StringPos", "StringPos(needle, haystack) -> number", "1-based position of the first match, 0 if absent", "Strings")]
    public static double StringPos(string needle, string haystack)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        return haystack.IndexOf(needle, StringComparison.Ordinal) + 1;
    }

    [PgslCommand("StringLastPos", "StringLastPos(needle, haystack) -> number", "1-based position of the last match, 0 if absent", "Strings")]
    public static double StringLastPos(string needle, string haystack)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        return haystack.LastIndexOf(needle, StringComparison.Ordinal) + 1;
    }

    [PgslCommand("StringContains", "StringContains(haystack, needle) -> bool", "True when the text contains the needle", "Strings")]
    public static bool StringContains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle)
        && haystack.Contains(needle, StringComparison.Ordinal);

    [PgslCommand("StringStartsWith", "StringStartsWith(text, prefix) -> bool", "True when the text starts with the prefix", "Strings")]
    public static bool StringStartsWith(string text, string prefix) =>
        !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(prefix)
        && text.StartsWith(prefix, StringComparison.Ordinal);

    [PgslCommand("StringEndsWith", "StringEndsWith(text, suffix) -> bool", "True when the text ends with the suffix", "Strings")]
    public static bool StringEndsWith(string text, string suffix) =>
        !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(suffix)
        && text.EndsWith(suffix, StringComparison.Ordinal);

    [PgslCommand("StringDelete", "StringDelete(text, index, count) -> string", "Remove characters from a 1-based index", "Strings")]
    public static string StringDelete(string text, double index, double count)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int start = (int)Math.Clamp(index - 1, 0, text.Length);
        int take = (int)Math.Clamp(count, 0, text.Length - start);
        return text.Remove(start, take);
    }

    [PgslCommand("StringInsert", "StringInsert(insert, text, index) -> string", "Insert at a 1-based index", "Strings")]
    public static string StringInsert(string insert, string text, double index)
    {
        text ??= string.Empty;
        insert ??= string.Empty;
        int at = (int)Math.Clamp(index - 1, 0, text.Length);
        return text.Insert(at, insert);
    }

    [PgslCommand("StringReplace", "StringReplace(text, find, replace) -> string", "Replace the first occurrence", "Strings")]
    public static string StringReplace(string text, string find, string replace)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find)) return text ?? string.Empty;
        int at = text.IndexOf(find, StringComparison.Ordinal);
        return at < 0 ? text : text.Remove(at, find.Length).Insert(at, replace ?? string.Empty);
    }

    [PgslCommand("StringReplaceAll", "StringReplaceAll(text, find, replace) -> string", "Replace every occurrence", "Strings")]
    public static string StringReplaceAll(string text, string find, string replace)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find)) return text ?? string.Empty;
        return text.Replace(find, replace ?? string.Empty, StringComparison.Ordinal);
    }

    [PgslCommand("StringCount", "StringCount(needle, haystack) -> number", "How many times the needle occurs", "Strings")]
    public static double StringCount(string needle, string haystack)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [PgslCommand("StringRepeat", "StringRepeat(text, times) -> string", "Concatenate the text with itself", "Strings")]
    public static string StringRepeat(string text, double times)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Hard cap: a script asking for 1e9 repeats of a long string would exhaust memory, and a
        // scripting mistake must not be able to take the game down.
        int count = (int)Math.Clamp(times, 0, 100_000);
        long total = (long)count * text.Length;
        if (total > 1_000_000) count = (int)(1_000_000 / Math.Max(1, text.Length));

        StringBuilder builder = new(Math.Max(0, count * text.Length));
        for (int index = 0; index < count; index++) builder.Append(text);
        return builder.ToString();
    }

    [PgslCommand("StringSplitPart", "StringSplitPart(text, delimiter, index) -> string", "One 1-based field of delimited text", "Strings")]
    public static string StringSplitPart(string text, string delimiter, double index)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (string.IsNullOrEmpty(delimiter)) return (int)index == 1 ? text : string.Empty;

        string[] parts = text.Split(delimiter);
        int at = (int)index - 1;
        return at >= 0 && at < parts.Length ? parts[at] : string.Empty;
    }

    [PgslCommand("StringSplitCount", "StringSplitCount(text, delimiter) -> number", "How many delimited fields the text has", "Strings")]
    public static double StringSplitCount(string text, string delimiter)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (string.IsNullOrEmpty(delimiter)) return 1;
        return text.Split(delimiter).Length;
    }

    [PgslCommand("StringDigits", "StringDigits(text) -> string", "Only the digit characters", "Strings")]
    public static string StringDigits(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.Where(char.IsDigit).ToArray());

    [PgslCommand("StringLetters", "StringLetters(text) -> string", "Only the letter characters", "Strings")]
    public static string StringLetters(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.Where(char.IsLetter).ToArray());

    [PgslCommand("StringLettersDigits", "StringLettersDigits(text) -> string", "Only letters and digits", "Strings")]
    public static string StringLettersDigits(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.Where(char.IsLetterOrDigit).ToArray());

    [PgslCommand("StringReverse", "StringReverse(text) -> string", "Characters in reverse order", "Strings")]
    public static string StringReverse(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        char[] characters = text.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }

    [PgslCommand("StringPadLeft", "StringPadLeft(text, width, padChar) -> string", "Right-align within a width", "Strings")]
    public static string StringPadLeft(string text, double width, string padChar)
    {
        text ??= string.Empty;
        char pad = string.IsNullOrEmpty(padChar) ? ' ' : padChar[0];
        return text.PadLeft((int)Math.Clamp(width, 0, 4096), pad);
    }

    [PgslCommand("StringPadRight", "StringPadRight(text, width, padChar) -> string", "Left-align within a width", "Strings")]
    public static string StringPadRight(string text, double width, string padChar)
    {
        text ??= string.Empty;
        char pad = string.IsNullOrEmpty(padChar) ? ' ' : padChar[0];
        return text.PadRight((int)Math.Clamp(width, 0, 4096), pad);
    }

    [PgslCommand("StringJoin2", "StringJoin2(a, b) -> string", "Concatenate two strings", "Strings")]
    public static string StringJoin2(string a, string b) => (a ?? string.Empty) + (b ?? string.Empty);

    [PgslCommand("StringJoin3", "StringJoin3(a, b, c) -> string", "Concatenate three strings", "Strings")]
    public static string StringJoin3(string a, string b, string c) =>
        (a ?? string.Empty) + (b ?? string.Empty) + (c ?? string.Empty);

    [PgslCommand("StringEquals", "StringEquals(a, b) -> bool", "Exact comparison", "Strings")]
    public static bool StringEquals(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    [PgslCommand("StringEqualsIgnoreCase", "StringEqualsIgnoreCase(a, b) -> bool", "Case-insensitive comparison", "Strings")]
    public static bool StringEqualsIgnoreCase(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [PgslCommand("StringIsEmpty", "StringIsEmpty(text) -> bool", "True for empty or whitespace-only text", "Strings")]
    public static bool StringIsEmpty(string text) => string.IsNullOrWhiteSpace(text);

    #endregion
}

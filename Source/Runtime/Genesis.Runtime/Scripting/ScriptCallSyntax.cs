using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Normalizes script resource calls: <c>Script1;</c> → <c>Script1();</c>, optional <c>scr_Script1</c> alias.
/// </summary>
public static class ScriptCallSyntax
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "while", "for", "return", "true", "false", "var", "with",
        "ScrExecute", "Print", "ShowMessage", "and", "or", "not"
    };

    private static readonly Regex ScrCallWithoutArguments = new(@"\bscr_([a-zA-Z_][a-zA-Z0-9_]*)\s*\(\s*\)", RegexOptions.Compiled);
    private static readonly Regex ScrCall = new(@"\bscr_([a-zA-Z_][a-zA-Z0-9_]*)\s*\(", RegexOptions.Compiled);
    // `if condition: Name` alone on a line, and `Name;`, for any identifier: one pass each over the
    // code, the identifier then looked up among the Script names. (A pattern per Script name meant
    // two regexes per name for every stretch of code between strings and comments, and with a few
    // hundred Scripts it overflowed the regex cache, so each was parsed again every time: about
    // ten seconds of a 253-Script game's start.)
    // `Name;` (the regex \b([A-Za-z_][A-Za-z0-9_]*)\s*; ) is found by AppendBareCalls instead.
    private static readonly Regex IfColonCall = new(@"^(\s*)if\s+(.+?):\s*([A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    // The same two patterns for a Script name that is not an identifier ("Pw Game"), made once per name.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Regex IfColon, Regex Bare)> OtherNamePatterns = new(StringComparer.Ordinal);

    public static string Apply(string source, IEnumerable<string> scriptNames)
    {
        if (string.IsNullOrEmpty(source) || scriptNames == null)
            return source;

        // Names that are plain identifiers (every name code can call) go through the two shared
        // patterns; any other name keeps a pattern of its own, longest first, as before.
        var callable = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> otherNames = null;
        foreach (string name in scriptNames)
        {
            if (string.IsNullOrWhiteSpace(name) || Reserved.Contains(name) || !seen.Add(name)) continue;
            if (IsIdentifier(name)) callable.Add(name);
            else (otherNames ??= new List<string>()).Add(name);
        }

        if (callable.Count == 0 && otherNames == null)
            return source;
        otherNames = otherNames?.OrderByDescending(n => n.Length).ToList();
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> callableSpans = callable.GetAlternateLookup<ReadOnlySpan<char>>();

        var segments = SplitPreservingLiterals(source);
        var sb = new StringBuilder(source.Length + 64);

        foreach (var seg in segments)
        {
            if (!seg.IsCode)
            {
                sb.Append(source, seg.Start, seg.Length);
                continue;
            }

            // Most code has no scr_ call, no `if condition: Name` and no other name: its bare calls are
            // then the only change, found from each `;` instead of a regex trying every identifier
            // (GenesisCraft's 44 MB of generated data Scripts: over a second of its start).
            ReadOnlySpan<char> span = source.AsSpan(seg.Start, seg.Length);
            if (!span.Contains("scr_", StringComparison.Ordinal) && !MayCallAfterColon(span, callableSpans)
                && !ContainsAny(span, otherNames))
            {
                AppendBareCalls(sb, span, callableSpans);
                continue;
            }

            string code = source.Substring(seg.Start, seg.Length);
            code = ScrCallWithoutArguments.Replace(code, "$1()");
            code = ScrCall.Replace(code, "$1(");

            if (callable.Count > 0)
            {
                code = IfColonCall.Replace(code, m => callable.Contains(m.Groups[3].Value)
                    ? $"{m.Groups[1].Value}if ({m.Groups[2].Value}) {{ {m.Groups[3].Value}(); }}"
                    : m.Value);
                code = AppendBareCalls(new StringBuilder(code.Length + 16), code, callableSpans).ToString();
            }

            if (otherNames != null)
            {
                foreach (string name in otherNames)
                {
                    // Both patterns contain the name verbatim, so code without it cannot match.
                    // Kinfield has ~280 such names ("Pw Game"): two fresh Regex parses per name per
                    // code segment were most of its script load (400 MB of RegexNode).
                    if (!code.Contains(name, StringComparison.Ordinal)) continue;
                    (Regex ifColon, Regex bare) = OtherNamePatterns.GetOrAdd(name, static key =>
                    {
                        string esc = Regex.Escape(key);
                        return (new Regex($@"^(\s*)if\s+(.+?):\s*{esc}\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant),
                            new Regex($@"\b{esc}\s*;", RegexOptions.CultureInvariant));
                    });
                    code = ifColon.Replace(code, m => $"{m.Groups[1].Value}if ({m.Groups[2].Value}) {{ {name}(); }}");
                    code = bare.Replace(code, $"{name}();");
                }
            }

            sb.Append(code);
        }

        return sb.ToString();
    }

    private static bool IsIdentifier(string name)
    {
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        for (int i = 1; i < name.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_')) return false;
        return true;
    }

    private static bool IsAsciiWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>A character the regex \b counts as part of a word.</summary>
    internal static bool IsRegexWordChar(char c)
    {
        if (c < 128) return IsAsciiWordChar(c);
        if (char.IsLetterOrDigit(c) || c == '‍' || c == '‌') return true;
        System.Globalization.UnicodeCategory category = char.GetUnicodeCategory(c);
        return category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.ConnectorPunctuation;
    }

    private static bool ContainsAny(ReadOnlySpan<char> code, List<string> names)
    {
        if (names == null) return false;
        foreach (string name in names)
            if (code.Contains(name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Writes <paramref name="code"/> with each `Name;` for a Script name made `Name();`, as the
    /// regex \b([A-Za-z_][A-Za-z0-9_]*)\s*; replaced them: the identifier just before a `;` (only
    /// white space between) that starts with a letter or `_` and does not follow a word character.
    /// </summary>
    private static StringBuilder AppendBareCalls(StringBuilder sb, ReadOnlySpan<char> code, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> callable)
    {
        int written = 0;
        if (callable.Set.Count > 0)
        {
            for (int semicolon = code.IndexOf(';'); semicolon >= 0; semicolon = NextSemicolon(code, semicolon))
            {
                int end = semicolon;
                while (end > 0 && char.IsWhiteSpace(code[end - 1])) end--;
                int begin = end;
                while (begin > 0 && IsAsciiWordChar(code[begin - 1])) begin--;
                if (begin == end || char.IsAsciiDigit(code[begin]) || (begin > 0 && IsRegexWordChar(code[begin - 1]))) continue;
                if (!callable.Contains(code[begin..end])) continue;
                sb.Append(code[written..end]).Append("();");
                written = semicolon + 1;
            }
        }
        return sb.Append(code[written..]);
    }

    private static int NextSemicolon(ReadOnlySpan<char> code, int after)
    {
        int next = code[(after + 1)..].IndexOf(';');
        return next < 0 ? -1 : after + 1 + next;
    }

    /// <summary>
    /// False when <see cref="IfColonCall"/> cannot change <paramref name="code"/>: no `:` is followed,
    /// past white space, by a Script name and then only white space to the end of a line.
    /// </summary>
    private static bool MayCallAfterColon(ReadOnlySpan<char> code, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> callable)
    {
        if (callable.Set.Count == 0) return false;
        for (int colon = code.IndexOf(':'); colon >= 0;)
        {
            int begin = colon + 1;
            while (begin < code.Length && char.IsWhiteSpace(code[begin])) begin++;
            int end = begin;
            while (end < code.Length && IsAsciiWordChar(code[end])) end++;
            if (end > begin && !char.IsAsciiDigit(code[begin]) && callable.Contains(code[begin..end]))
            {
                int after = end;
                while (after < code.Length && code[after] != '\n' && char.IsWhiteSpace(code[after])) after++;
                if (after == code.Length || code[after] == '\n') return true;
            }
            int next = code[(colon + 1)..].IndexOf(':');
            colon = next < 0 ? -1 : colon + 1 + next;
        }
        return false;
    }

    private readonly struct Segment
    {
        public Segment(int start, int length, bool isCode) { Start = start; Length = length; IsCode = isCode; }
        public int Start { get; }
        public int Length { get; }
        public bool IsCode { get; }
    }

    private static List<Segment> SplitPreservingLiterals(string source)
    {
        var list = new List<Segment>();
        int i = 0;
        while (i < source.Length)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                int start = i;
                while (i < source.Length && source[i] != '\n') i++;
                list.Add(new Segment(start, i - start, false));
                continue;
            }

            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                if (i + 1 < source.Length) i += 2;
                list.Add(new Segment(start, i - start, false));
                continue;
            }

            if (source[i] == '"')
            {
                int start = i;
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    if (source[i] == '"') { i++; break; }
                    i++;
                }
                list.Add(new Segment(start, i - start, false));
                continue;
            }

            int codeStart = i;
            while (i < source.Length)
            {
                if (source[i] == '"') break;
                if (i + 1 < source.Length && source[i] == '/' && (source[i + 1] == '/' || source[i + 1] == '*')) break;
                i++;
            }
            if (i > codeStart)
                list.Add(new Segment(codeStart, i - codeStart, true));
        }

        return list;
    }
}

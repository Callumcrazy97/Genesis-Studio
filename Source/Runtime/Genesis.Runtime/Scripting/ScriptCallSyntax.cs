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
    private static readonly Regex IfColonCall = new(@"^(\s*)if\s+(.+?):\s*([A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BareCall = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*;", RegexOptions.Compiled);

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

        var segments = SplitPreservingLiterals(source);
        var sb = new StringBuilder(source.Length + 64);

        foreach (var seg in segments)
        {
            if (!seg.IsCode)
            {
                sb.Append(seg.Text);
                continue;
            }

            string code = seg.Text;
            code = ScrCallWithoutArguments.Replace(code, "$1()");
            code = ScrCall.Replace(code, "$1(");

            if (callable.Count > 0)
            {
                code = IfColonCall.Replace(code, m => callable.Contains(m.Groups[3].Value)
                    ? $"{m.Groups[1].Value}if ({m.Groups[2].Value}) {{ {m.Groups[3].Value}(); }}"
                    : m.Value);
                code = BareCall.Replace(code, m => callable.Contains(m.Groups[1].Value) ? m.Groups[1].Value + "();" : m.Value);
            }

            if (otherNames != null)
            {
                foreach (string name in otherNames)
                {
                    string esc = Regex.Escape(name);
                    code = Regex.Replace(code,
                        $@"^(\s*)if\s+(.+?):\s*{esc}\s*$",
                        m => $"{m.Groups[1].Value}if ({m.Groups[2].Value}) {{ {name}(); }}",
                        RegexOptions.Multiline);
                    code = Regex.Replace(code, $@"\b{esc}\s*;", $"{name}();");
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

    private readonly struct Segment
    {
        public Segment(string text, bool isCode) { Text = text; IsCode = isCode; }
        public string Text { get; }
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
                list.Add(new Segment(source.Substring(start, i - start), false));
                continue;
            }

            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                if (i + 1 < source.Length) i += 2;
                list.Add(new Segment(source.Substring(start, i - start), false));
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
                list.Add(new Segment(source.Substring(start, i - start), false));
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
                list.Add(new Segment(source.Substring(codeStart, i - codeStart), true));
        }

        return list;
    }
}

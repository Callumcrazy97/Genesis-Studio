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

    public static string Apply(string source, IEnumerable<string> scriptNames)
    {
        if (string.IsNullOrEmpty(source) || scriptNames == null)
            return source;

        var names = scriptNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && !Reserved.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();

        if (names.Count == 0)
            return source;

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
            code = Regex.Replace(code, @"\bscr_([a-zA-Z_][a-zA-Z0-9_]*)\s*\(\s*\)", "$1()");
            code = Regex.Replace(code, @"\bscr_([a-zA-Z_][a-zA-Z0-9_]*)\s*\(", "$1(");

            foreach (string name in names)
            {
                string esc = Regex.Escape(name);
                code = Regex.Replace(code,
                    $@"^(\s*)if\s+(.+?):\s*{esc}\s*$",
                    m => $"{m.Groups[1].Value}if ({m.Groups[2].Value}) {{ {name}(); }}",
                    RegexOptions.Multiline);
                code = Regex.Replace(code, $@"\b{esc}\s*;", $"{name}();");
            }

            sb.Append(code);
        }

        return sb.ToString();
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

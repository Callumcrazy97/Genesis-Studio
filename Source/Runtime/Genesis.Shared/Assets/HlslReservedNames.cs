using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Shared.Assets;

/// <summary>
/// Finds HLSL keywords and reserved words that shader code declares as names: a variable,
/// parameter or field called <c>line</c>, <c>point</c> or <c>sample</c>. The compiler refuses
/// those with a bare "unexpected token" error that does not say why. Studio's project check
/// warns about them before a game runs, and the Player names them beside a compile error.
/// </summary>
public static class HlslReservedNames
{
    // From Microsoft's HLSL "Keywords" and "Reserved Words" lists: the ones that read like
    // ordinary names. Words only used as statements (return, discard, break, default...) are left
    // out, as nobody declares them and they would match "else discard;". Case matters: HLSL
    // reserves "linear" but not "Linear".
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "line", "lineadj", "point", "triangle", "triangleadj", "linear", "centroid", "sample",
        "nointerpolation", "noperspective", "precise", "shared", "groupshared", "uniform", "volatile",
        "extern", "export", "static", "inline", "in", "out", "inout", "vector", "matrix", "texture",
        "sampler", "string", "half", "dword", "pass", "technique", "compile", "snorm", "unorm",
        "packoffset", "register", "interface", "class", "namespace", "typedef", "row_major",
        "column_major", "asm", "auto", "char", "short", "long", "signed", "unsigned", "new", "delete",
        "this", "template", "typename", "operator", "private", "protected", "public", "friend",
        "virtual", "mutable", "explicit", "union", "using", "enum", "sizeof", "goto", "throw", "try",
        "catch", "const_cast", "static_cast", "dynamic_cast", "reinterpret_cast",
    };

    // A type (with an optional template argument) then a name, then what follows a declared name:
    // = ; , ) [ or : (a semantic). "float line = 1;", "float3 point : TEXCOORD1;", "(float2 sample)";
    // or a further name in a list, "float a, line;". Only the type is consumed, so a name can
    // start the next match. Modifiers ("point VSOut v[1]", "sample float4 c") are followed by a type.
    private static readonly Regex Declaration = new(
        @"(?:(?<![\w.])[A-Za-z_]\w*(?:\s*<[^<>;{}()]*>)?\s+|,\s*)(?=(?<name>[A-Za-z_]\w*)\s*[=;,)\[:])",
        RegexOptions.CultureInvariant);

    /// <summary>A reserved word declared as a name, and the 1-based line it is on.</summary>
    public readonly record struct Use(string Word, int Line);

    /// <summary>True when the word is one HLSL reserves (case-sensitive).</summary>
    public static bool IsReserved(string word) => !string.IsNullOrEmpty(word) && Words.Contains(word);

    /// <summary>Each reserved word the HLSL source declares as a name, once per line, in order.</summary>
    public static IReadOnlyList<Use> Find(string source)
    {
        var uses = new List<Use>();
        if (string.IsNullOrEmpty(source)) return uses;
        string code = WithoutCommentsStringsAndDirectives(source);
        var seen = new HashSet<(string, int)>();
        foreach (Match match in Declaration.Matches(code))
        {
            string name = match.Groups["name"].Value;
            if (!Words.Contains(name)) continue;
            int line = 1;
            for (int i = 0; i < match.Groups["name"].Index; i++)
                if (code[i] == '\n') line++;
            if (seen.Add((name, line))) uses.Add(new Use(name, line));
        }
        return uses;
    }

    /// <summary>
    /// One sentence naming the reserved words, or empty when there are none. Lines are left out
    /// for source the engine has added to (a compiled variant), whose lines no longer match.
    /// </summary>
    public static string Describe(IReadOnlyList<Use> uses, bool withLines = true)
    {
        if (uses == null || uses.Count == 0) return string.Empty;
        var parts = new List<string>();
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Use use in uses)
        {
            if (!withLines && !words.Add(use.Word)) continue;
            if (parts.Count == 4) { parts.Add("..."); break; }
            parts.Add(withLines ? $"'{use.Word}' (line {use.Line})" : $"'{use.Word}'");
        }
        return "HLSL reserves " + string.Join(", ", parts) + ", so it cannot be a name; rename it (for example '"
            + uses[0].Word + "Value').";
    }

    /// <summary>
    /// The source with comments, string literals and preprocessor lines blanked out, keeping every
    /// line break so line numbers still match.
    /// </summary>
    private static string WithoutCommentsStringsAndDirectives(string source)
    {
        var text = new StringBuilder(source.Length);
        bool lineStart = true;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/' || lineStart && c == '#')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    // A directive continued with a backslash goes on to the next line.
                    if (source[i] == '\\' && i + 1 < source.Length && source[i + 1] == '\n' && c == '#') { text.Append('\n'); i++; }
                    else text.Append(' ');
                    i++;
                }
                if (i < source.Length) text.Append('\n');
                lineStart = true;
                continue;
            }
            if (c == '/' && next == '*')
            {
                text.Append("  ");
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    text.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < source.Length) { text.Append("  "); i++; }
                lineStart = false;
                continue;
            }
            if (c == '"')
            {
                text.Append(' ');
                for (i++; i < source.Length && source[i] != '"' && source[i] != '\n'; i++)
                {
                    if (source[i] == '\\' && i + 1 < source.Length && source[i + 1] != '\n') { text.Append(' '); i++; }
                    text.Append(' ');
                }
                if (i < source.Length) text.Append(source[i] == '\n' ? '\n' : ' ');
                lineStart = i < source.Length && source[i] == '\n';
                continue;
            }
            text.Append(c);
            if (c == '\n') lineStart = true;
            else if (!char.IsWhiteSpace(c)) lineStart = false;
        }
        return text.ToString();
    }
}

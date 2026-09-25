using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Runtime.Scripting;

/// <summary>Protects source literals while the PGSL shorthand syntax is normalized.</summary>
internal sealed class PgslSourcePreprocessor
{
    private readonly Dictionary<string, string> _literals = new(StringComparer.Ordinal);
    private readonly string _prefix;

    public string Text { get; }

    public PgslSourcePreprocessor(string source)
    {
        _prefix = "__GENESIS_PGSL_LITERAL_";
        while (source.Contains(_prefix, StringComparison.Ordinal)) _prefix += "_";
        StringBuilder result = new(source.Length);
        int index = 0;
        while (index < source.Length)
        {
            char current = source[index];
            if (current is '\"' or '\'')
            {
                int start = index++;
                while (index < source.Length)
                {
                    char character = source[index++];
                    if (character == '\\' && index < source.Length) index++;
                    else if (character == current) break;
                }
                string token = "\"" + _prefix + _literals.Count.ToString(CultureInfo.InvariantCulture) + "\"";
                _literals.Add(token, source[start..index]);
                result.Append(token);
            }
            else if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] is not '\r' and not '\n')
                {
                    result.Append(' ');
                    index++;
                }
            }
            else if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Keep malformed input visible to the parser instead of silently deleting it.
                    result.Append(source.AsSpan(index));
                    break;
                }
                end += 2;
                while (index < end)
                {
                    char character = source[index++];
                    result.Append(character is '\r' or '\n' ? character : ' ');
                }
            }
            else result.Append(source[index++]);
        }
        Text = result.ToString();
    }

    public string RestoreLiterals(string normalized) => _literals.Count == 0
        ? normalized
        : Regex.Replace(normalized, "\"" + Regex.Escape(_prefix) + "[0-9]+\"",
            match => _literals.TryGetValue(match.Value, out string literal) ? literal : match.Value,
            RegexOptions.CultureInvariant);
}

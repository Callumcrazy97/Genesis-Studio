using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>
/// Discovers PGSL file-scope variables for Unity-style Inspector authoring. PGSL has no
/// public/private field modifier, so file-scope <c>var</c> declarations are the inspectable
/// contract; declarations inside events/functions remain implementation details.
/// </summary>
public static partial class PgslInspectableVariables
{
    public sealed record Variable(string Name, object Value, string Literal);

    public static IReadOnlyList<Variable> Reflect(string source)
    {
        List<Variable> variables = [];
        foreach ((Match Match, int Depth) declaration in Declarations(source))
        {
            if (declaration.Depth != 0) continue;
            Match match = declaration.Match;
            string literal = match.Groups["literal"].Value.Trim();
            if (TryParseLiteral(literal, out object? value) && value is not null)
            {
                variables.Add(new Variable(match.Groups["name"].Value, value, literal));
            }
        }

        return variables;
    }

    public static bool TrySetValue(
        string source,
        string variableName,
        object? value,
        out string updated)
    {
        updated = source;
        foreach ((Match Match, int Depth) declaration in Declarations(source))
        {
            Match match = declaration.Match;
            if (declaration.Depth != 0
                || !string.Equals(match.Groups["name"].Value, variableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string literal = FormatLiteral(value);
            Group group = match.Groups["literal"];
            updated = source[..group.Index] + literal + source[(group.Index + group.Length)..];
            return true;
        }

        return false;
    }

    private static IEnumerable<(Match Match, int Depth)> Declarations(string source)
    {
        source ??= string.Empty;
        int depth = 0;
        int scan = 0;
        foreach (Match match in DeclarationPattern().Matches(source))
        {
            depth = ScanDepth(source, scan, match.Index, depth);
            yield return (match, depth);
            scan = match.Index + match.Length;
        }
    }

    private static int ScanDepth(string source, int start, int end, int depth)
    {
        bool lineComment = false;
        bool blockComment = false;
        bool quoted = false;
        char quote = '\0';
        for (int index = start; index < end; index++)
        {
            char current = source[index];
            char next = index + 1 < end ? source[index + 1] : '\0';
            if (lineComment)
            {
                if (current is '\r' or '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (current == '*' && next == '/')
                {
                    blockComment = false;
                    index++;
                }
                continue;
            }
            if (quoted)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }
                if (current == quote) quoted = false;
                continue;
            }
            if (current == '/' && next == '/')
            {
                lineComment = true;
                index++;
            }
            else if (current == '/' && next == '*')
            {
                blockComment = true;
                index++;
            }
            else if (current is '\'' or '"')
            {
                quoted = true;
                quote = current;
            }
            else if (current == '{') depth++;
            else if (current == '}') depth = Math.Max(0, depth - 1);
        }

        return depth;
    }

    private static bool TryParseLiteral(string literal, out object? value)
    {
        if (bool.TryParse(literal, out bool flag))
        {
            value = flag;
            return true;
        }
        if (double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double number))
        {
            value = number;
            return true;
        }
        if (literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"')
        {
            value = Unescape(literal[1..^1]);
            return true;
        }

        value = null;
        return false;
    }

    private static string FormatLiteral(object? value) => value switch
    {
        bool flag => flag ? "true" : "false",
        string text => $"\"{Escape(text)}\"",
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
        _ => throw new ArgumentException("PGSL Inspector variables support numbers, booleans and strings.",
            nameof(value)),
    };

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        StringBuilder result = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                result.Append(current);
                continue;
            }

            char escaped = value[++index];
            result.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => escaped,
            });
        }
        return result.ToString();
    }

    [GeneratedRegex(
        """(?m)^[\t ]*var[\t ]+(?<name>[A-Za-z_]\w*)[\t ]*=[\t ]*(?<literal>true|false|[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|"(?:\\.|[^"\\])*")[\t ]*;""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();
}

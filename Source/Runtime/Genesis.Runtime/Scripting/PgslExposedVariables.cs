using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Finds the literal, file-scope variables that form PGSL's Inspector contract and applies
/// instance values to those declaration literals in an in-memory event source. The authored
/// source on disk is never changed.
/// </summary>
public static class PgslExposedVariables
{
    public sealed record Variable(string Name, object Value, string Literal, int LiteralStart, int LiteralLength);

    public static IReadOnlyList<Variable> Reflect(string source)
    {
        source ??= string.Empty;
        List<Variable> variables = [];
        List<SourceToken> tokens = Tokenize(source);
        for (int index = 0; index < tokens.Count; index++)
        {
            SourceToken keyword = tokens[index];
            if (keyword.Depth != 0 || keyword.Kind != SourceTokenKind.Identifier
                || !keyword.Text.Equals("var", StringComparison.OrdinalIgnoreCase)
                || !IsStatementStart(tokens, index)
                || index + 4 >= tokens.Count) continue;

            SourceToken name = tokens[index + 1];
            SourceToken equals = tokens[index + 2];
            if (name.Depth != 0 || name.Kind != SourceTokenKind.Identifier
                || equals.Depth != 0 || equals.Kind != SourceTokenKind.Equals) continue;

            int literalIndex = index + 3;
            SourceToken literal = tokens[literalIndex];
            int literalStart = literal.Start;
            int literalEnd = literal.Start + literal.Length;
            string literalText = literal.Text;
            if (literal.Kind is SourceTokenKind.Plus or SourceTokenKind.Minus)
            {
                if (++literalIndex >= tokens.Count || tokens[literalIndex].Kind != SourceTokenKind.Number
                    || tokens[literalIndex].Depth != 0) continue;
                SourceToken number = tokens[literalIndex];
                literalEnd = number.Start + number.Length;
                literalText = literal.Text + number.Text;
            }
            if (literalIndex + 1 >= tokens.Count || tokens[literalIndex + 1].Depth != 0
                || tokens[literalIndex + 1].Kind != SourceTokenKind.Semicolon
                || !IsLiteralToken(literal, literalText)
                || !TryParseLiteral(literalText, out object value)) continue;

            variables.Add(new Variable(
                name.Text,
                value,
                literalText,
                literalStart,
                literalEnd - literalStart));
        }
        return variables;
    }

    /// <summary>
    /// Returns an instance-specialized copy of one event source. Only the parsed literal span of a
    /// matching top-level declaration is replaced; expressions, nested locals and all surrounding
    /// source bytes remain intact.
    /// </summary>
    public static string ApplyOverrides(string source, IReadOnlyDictionary<string, string> overrides)
    {
        if (string.IsNullOrEmpty(source) || overrides is null || overrides.Count == 0) return source;

        List<(int Start, int Length, string Value)> replacements = [];
        foreach (Variable variable in Reflect(source))
        {
            if (!overrides.TryGetValue(variable.Name, out string serialized)
                || !TryFormatLiteral(variable.Literal, serialized, out string replacement)) continue;
            replacements.Add((variable.LiteralStart, variable.LiteralLength, replacement));
        }
        if (replacements.Count == 0) return source;

        StringBuilder result = new(source);
        foreach ((int start, int length, string value) in replacements.OrderByDescending(item => item.Start))
        {
            result.Remove(start, length);
            result.Insert(start, value);
        }
        return result.ToString();
    }

    private static bool IsLiteralToken(SourceToken token, string literal) => token.Kind switch
    {
        SourceTokenKind.Number or SourceTokenKind.String => true,
        SourceTokenKind.Plus or SourceTokenKind.Minus => literal.Length > 1,
        SourceTokenKind.Identifier => literal.Equals("true", StringComparison.OrdinalIgnoreCase)
                                      || literal.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsStatementStart(IReadOnlyList<SourceToken> tokens, int index)
    {
        if (index == 0) return true;
        SourceToken previous = tokens[index - 1];
        return previous.Depth == 0
               && (previous.Kind == SourceTokenKind.Semicolon || previous.Text == "}");
    }

    /// <summary>
    /// A continuous source scan supplies exact token spans while carrying comment, string and brace
    /// state across the whole event. This deliberately does not depend on line starts, so adjacent
    /// declarations are discoverable without mistaking text inside comments or strings for code.
    /// </summary>
    private static List<SourceToken> Tokenize(string source)
    {
        List<SourceToken> tokens = [];
        int depth = 0;
        int index = 0;
        while (index < source.Length)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }
            if (current == '/' && next == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
                    {
                        index += 2;
                        break;
                    }
                    index++;
                }
                continue;
            }
            if (current is '"' or '\'')
            {
                int start = index;
                char quote = current;
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        index += 2;
                        continue;
                    }
                    char value = source[index++];
                    if (value == quote) break;
                }
                SourceTokenKind kind = quote == '"' ? SourceTokenKind.String : SourceTokenKind.Other;
                tokens.Add(new SourceToken(kind, source[start..index], start, index - start, depth));
                continue;
            }
            if (current is '{' or '(' or '[')
            {
                tokens.Add(new SourceToken(SourceTokenKind.Other, current.ToString(), index++, 1, depth));
                depth++;
                continue;
            }
            if (current is '}' or ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
                tokens.Add(new SourceToken(SourceTokenKind.Other, current.ToString(), index++, 1, depth));
                continue;
            }
            if (char.IsLetter(current) || current == '_')
            {
                int start = index++;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++;
                tokens.Add(new SourceToken(SourceTokenKind.Identifier, source[start..index], start, index - start, depth));
                continue;
            }
            if (char.IsDigit(current) || (current == '.' && char.IsDigit(next)))
            {
                int start = index;
                bool dot = false;
                if (source[index] == '.') { dot = true; index++; }
                while (index < source.Length && char.IsDigit(source[index])) index++;
                if (!dot && index < source.Length && source[index] == '.')
                {
                    dot = true;
                    index++;
                    while (index < source.Length && char.IsDigit(source[index])) index++;
                }
                if (index < source.Length && source[index] is 'e' or 'E')
                {
                    int exponent = index++;
                    if (index < source.Length && source[index] is '+' or '-') index++;
                    int digits = index;
                    while (index < source.Length && char.IsDigit(source[index])) index++;
                    if (digits == index) index = exponent;
                }
                tokens.Add(new SourceToken(SourceTokenKind.Number, source[start..index], start, index - start, depth));
                continue;
            }

            SourceTokenKind punctuation = current switch
            {
                '=' => SourceTokenKind.Equals,
                ';' => SourceTokenKind.Semicolon,
                '+' => SourceTokenKind.Plus,
                '-' => SourceTokenKind.Minus,
                _ => SourceTokenKind.Other,
            };
            tokens.Add(new SourceToken(punctuation, current.ToString(), index++, 1, depth));
        }
        return tokens;
    }

    private enum SourceTokenKind { Identifier, Number, String, Equals, Semicolon, Plus, Minus, Other }

    private readonly record struct SourceToken(
        SourceTokenKind Kind,
        string Text,
        int Start,
        int Length,
        int Depth);

    private static bool TryParseLiteral(string literal, out object value)
    {
        if (bool.TryParse(literal, out bool flag))
        {
            value = flag;
            return true;
        }
        if (double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            value = number;
            return true;
        }
        if (literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"')
        {
            value = Unescape(literal[1..^1]);
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryFormatLiteral(string original, string serialized, out string replacement)
    {
        if (original.Length >= 2 && original[0] == '"' && original[^1] == '"')
        {
            replacement = "\"" + Escape(serialized ?? string.Empty) + "\"";
            return true;
        }
        if (bool.TryParse(original, out _))
        {
            if (bool.TryParse(serialized, out bool flag))
            {
                replacement = flag ? "true" : "false";
                return true;
            }
            replacement = original;
            return false;
        }
        if (double.TryParse(original, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            && double.TryParse(serialized, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && double.IsFinite(number))
        {
            replacement = number.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }
        replacement = original;
        return false;
    }

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
}

using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>The kind of editor assistance requested at the current caret position.</summary>
public enum CodeIntelligenceRequestKind
{
    Completion,
    SignatureHelp,
}

/// <summary>One item in the code-completion popup.</summary>
public sealed record CodeCompletionItem(
    string DisplayText,
    string InsertText,
    string Kind,
    string Description = "")
{
    public override string ToString() => string.IsNullOrWhiteSpace(Kind)
        ? DisplayText
        : $"{DisplayText}    {Kind}";
}

/// <summary>A strongly typed request emitted by <see cref="CodeEditor"/>.</summary>
public sealed class CodeIntelligenceRequestEventArgs : EventArgs
{
    private CodeIntelligenceRequestEventArgs(
        CodeIntelligenceRequestKind kind,
        string prefix,
        int replacementStart,
        int replacementLength,
        string commandName,
        int activeParameterIndex)
    {
        Kind = kind;
        Prefix = prefix;
        ReplacementStart = replacementStart;
        ReplacementLength = replacementLength;
        CommandName = commandName;
        ActiveParameterIndex = activeParameterIndex;
    }

    public CodeIntelligenceRequestKind Kind { get; }

    public string Prefix { get; }

    public int ReplacementStart { get; }

    public int ReplacementLength { get; }

    public string CommandName { get; }

    public int ActiveParameterIndex { get; }

    public static CodeIntelligenceRequestEventArgs Completion(
        string prefix,
        int replacementStart,
        int replacementLength) =>
        new(
            CodeIntelligenceRequestKind.Completion,
            prefix,
            replacementStart,
            replacementLength,
            string.Empty,
            -1);

    public static CodeIntelligenceRequestEventArgs Signature(string commandName, int activeParameterIndex) =>
        new(
            CodeIntelligenceRequestKind.SignatureHelp,
            string.Empty,
            -1,
            0,
            commandName,
            activeParameterIndex);
}

/// <summary>A named symbol discovered in the current source file.</summary>
public sealed record CodeSymbol(string Name, string Kind);

/// <summary>
/// Small lexical analyser used by the editor UI. It deliberately understands only the contexts
/// needed for completion and signature help, but it does understand nested delimiters, escaped
/// strings, and both PGSL comment forms. That keeps assistance stable while code is half-written.
/// </summary>
public static class CodeContextAnalyzer
{
    private static readonly Regex DeclarationPattern = new(
        @"\b(?<kind>var|function|event|struct)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryGetCompletion(
        string source,
        int caret,
        out string prefix,
        out int replacementStart,
        out int replacementLength)
    {
        source ??= string.Empty;
        caret = Math.Clamp(caret, 0, source.Length);
        int start = caret;
        while (start > 0 && IsCompletionCharacter(source[start - 1]))
        {
            start--;
        }

        prefix = source[start..caret];
        replacementStart = start;
        replacementLength = caret - start;
        return prefix.Length > 0 && (char.IsLetter(prefix[0]) || prefix[0] == '_');
    }

    public static bool TryGetActiveCall(
        string source,
        int caret,
        out string commandName,
        out int activeParameterIndex)
    {
        source ??= string.Empty;
        caret = Math.Clamp(caret, 0, source.Length);
        List<DelimiterFrame> stack = [];
        LexicalState state = LexicalState.Code;

        for (int index = 0; index < caret; index++)
        {
            char current = source[index];
            char next = index + 1 < caret ? source[index + 1] : '\0';

            switch (state)
            {
                case LexicalState.LineComment:
                    if (current is '\r' or '\n')
                    {
                        state = LexicalState.Code;
                    }
                    continue;

                case LexicalState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = LexicalState.Code;
                        index++;
                    }
                    continue;

                case LexicalState.DoubleQuotedString:
                    if (current == '\\')
                    {
                        index++;
                    }
                    else if (current == '"')
                    {
                        state = LexicalState.Code;
                    }
                    continue;

                case LexicalState.SingleQuotedString:
                    if (current == '\\')
                    {
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = LexicalState.Code;
                    }
                    continue;
            }

            if (current == '/' && next == '/')
            {
                state = LexicalState.LineComment;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                state = LexicalState.BlockComment;
                index++;
                continue;
            }

            if (current == '"')
            {
                state = LexicalState.DoubleQuotedString;
                continue;
            }

            if (current == '\'')
            {
                state = LexicalState.SingleQuotedString;
                continue;
            }

            if (current is '(' or '[' or '{')
            {
                string name = current == '(' ? ReadIdentifierBefore(source, index) : string.Empty;
                stack.Add(new DelimiterFrame(current, name, 0));
                continue;
            }

            if (current is ')' or ']' or '}')
            {
                char expected = current switch
                {
                    ')' => '(',
                    ']' => '[',
                    _ => '{',
                };
                int matching = stack.FindLastIndex(frame => frame.OpenDelimiter == expected);
                if (matching >= 0)
                {
                    stack.RemoveRange(matching, stack.Count - matching);
                }
                continue;
            }

            if (current == ',' && stack.Count > 0)
            {
                int top = stack.Count - 1;
                DelimiterFrame frame = stack[top];
                if (frame.OpenDelimiter == '(' && frame.CommandName.Length > 0)
                {
                    stack[top] = frame with { ActiveParameterIndex = frame.ActiveParameterIndex + 1 };
                }
            }
        }

        for (int index = stack.Count - 1; index >= 0; index--)
        {
            DelimiterFrame frame = stack[index];
            if (frame.OpenDelimiter == '(' && frame.CommandName.Length > 0)
            {
                commandName = frame.CommandName;
                activeParameterIndex = frame.ActiveParameterIndex;
                return true;
            }
        }

        commandName = string.Empty;
        activeParameterIndex = -1;
        return false;
    }

    public static IReadOnlyList<CodeSymbol> DiscoverSymbols(string source)
    {
        string searchable = MaskCommentsAndStrings(source ?? string.Empty);
        Dictionary<string, CodeSymbol> symbols = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DeclarationPattern.Matches(searchable))
        {
            string name = match.Groups["name"].Value;
            string declaration = match.Groups["kind"].Value;
            string kind = declaration switch
            {
                "var" => "Variable",
                "function" => "Function",
                "event" => "Event",
                "struct" => "Struct",
                _ => "Symbol",
            };
            symbols.TryAdd(name, new CodeSymbol(name, kind));
        }

        return symbols.Values.OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool TryGetParameterSpan(
        string signature,
        int parameterIndex,
        out int start,
        out int length)
    {
        start = 0;
        length = 0;
        if (string.IsNullOrWhiteSpace(signature) || parameterIndex < 0)
        {
            return false;
        }

        int open = signature.IndexOf('(');
        if (open < 0)
        {
            return false;
        }

        int argumentStart = open + 1;
        int currentParameter = 0;
        int depth = 0;
        for (int index = open + 1; index < signature.Length; index++)
        {
            char current = signature[index];
            if (current is '(' or '[' or '<')
            {
                depth++;
            }
            else if (current is ')' or ']' or '>')
            {
                if (current == ')' && depth == 0)
                {
                    return FinishParameter(signature, parameterIndex, currentParameter, argumentStart, index, out start, out length);
                }
                depth = Math.Max(0, depth - 1);
            }
            else if (current == ',' && depth == 0)
            {
                if (currentParameter == parameterIndex)
                {
                    return FinishParameter(signature, parameterIndex, currentParameter, argumentStart, index, out start, out length);
                }
                currentParameter++;
                argumentStart = index + 1;
            }
        }

        return FinishParameter(
            signature,
            parameterIndex,
            currentParameter,
            argumentStart,
            signature.Length,
            out start,
            out length);
    }

    private static bool FinishParameter(
        string signature,
        int requestedParameter,
        int currentParameter,
        int rawStart,
        int rawEnd,
        out int start,
        out int length)
    {
        start = rawStart;
        while (start < rawEnd && char.IsWhiteSpace(signature[start]))
        {
            start++;
        }

        int end = rawEnd;
        while (end > start && char.IsWhiteSpace(signature[end - 1]))
        {
            end--;
        }

        length = end - start;
        return currentParameter == requestedParameter && length > 0;
    }

    private static bool IsCompletionCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '.';

    private static string ReadIdentifierBefore(string source, int openParenthesis)
    {
        int end = openParenthesis;
        while (end > 0 && char.IsWhiteSpace(source[end - 1]))
        {
            end--;
        }

        int start = end;
        while (start > 0 && IsCompletionCharacter(source[start - 1]))
        {
            start--;
        }

        return start < end ? source[start..end] : string.Empty;
    }

    private static string MaskCommentsAndStrings(string source)
    {
        StringBuilder masked = new(source.Length);
        LexicalState state = LexicalState.Code;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (state == LexicalState.Code)
            {
                if (current == '/' && next == '/')
                {
                    state = LexicalState.LineComment;
                    masked.Append("  ");
                    index++;
                }
                else if (current == '/' && next == '*')
                {
                    state = LexicalState.BlockComment;
                    masked.Append("  ");
                    index++;
                }
                else if (current == '"')
                {
                    state = LexicalState.DoubleQuotedString;
                    masked.Append(' ');
                }
                else if (current == '\'')
                {
                    state = LexicalState.SingleQuotedString;
                    masked.Append(' ');
                }
                else
                {
                    masked.Append(current);
                }
                continue;
            }

            masked.Append(current is '\r' or '\n' ? current : ' ');
            if (state == LexicalState.LineComment && current is '\r' or '\n')
            {
                state = LexicalState.Code;
            }
            else if (state == LexicalState.BlockComment && current == '*' && next == '/')
            {
                masked.Append(' ');
                index++;
                state = LexicalState.Code;
            }
            else if (state is LexicalState.DoubleQuotedString or LexicalState.SingleQuotedString)
            {
                char quote = state == LexicalState.DoubleQuotedString ? '"' : '\'';
                if (current == '\\' && index + 1 < source.Length)
                {
                    masked.Append(' ');
                    index++;
                }
                else if (current == quote)
                {
                    state = LexicalState.Code;
                }
            }
        }

        return masked.ToString();
    }

    private sealed record DelimiterFrame(char OpenDelimiter, string CommandName, int ActiveParameterIndex);

    private enum LexicalState
    {
        Code,
        LineComment,
        BlockComment,
        DoubleQuotedString,
        SingleQuotedString,
    }
}

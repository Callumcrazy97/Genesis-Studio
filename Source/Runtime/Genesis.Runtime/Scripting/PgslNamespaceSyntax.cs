#nullable disable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Normalises the indentation form of PGSL namespace blocks into the brace form understood by both
/// parser front-ends. The transform preserves the original line count so editor diagnostics continue
/// to point at the authored source line.
/// </summary>
internal static class PgslNamespaceSyntax
{
    private static readonly Regex NamespaceHeader = new(
        @"^(?<indent>[ \t]*)from\s+(?<namespace>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string NormalizeIndentedBlocks(string source)
    {
        if (string.IsNullOrEmpty(source) || source.IndexOf("from", StringComparison.OrdinalIgnoreCase) < 0)
            return source;

        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string normalised = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalised.Split('\n');
        var openBlocks = new Stack<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            string original = lines[i];
            if (string.IsNullOrWhiteSpace(original)) continue;

            int indent = IndentWidth(original);
            int closeCount = 0;
            while (openBlocks.Count > 0 && indent <= openBlocks.Peek())
            {
                openBlocks.Pop();
                closeCount++;
            }

            Match match = NamespaceHeader.Match(original);
            string leading = original[..Math.Min(indent, original.Length)];
            string closers = closeCount == 0 ? string.Empty : new string('}', closeCount) + " ";
            if (match.Success)
            {
                lines[i] = leading + closers + "from " + match.Groups["namespace"].Value + " {";
                openBlocks.Push(indent);
            }
            else if (closeCount > 0)
            {
                lines[i] = leading + closers + original[indent..];
            }
        }

        if (openBlocks.Count > 0)
        {
            int last = lines.Length - 1;
            while (last > 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
            lines[last] += " " + new string('}', openBlocks.Count);
        }

        return string.Join(newline, lines);
    }

    private static int IndentWidth(string line)
    {
        int width = 0;
        while (width < line.Length && (line[width] == ' ' || line[width] == '\t')) width++;
        return width;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Splits a large constant array in translated GLSL into pieces a driver's GLSL compiler
    /// accepts, reading it through a small function instead.
    /// </summary>
    /// <remarks>
    /// <para>An HLSL <c>static const</c> table (a game's block table of 2,048 <c>int3</c>, say)
    /// becomes one GLSL <c>const ivec3 _N[2048] = ivec3[](...)</c>. Direct3D and Vulkan take it;
    /// NVIDIA's GLSL compiler refuses it outright ("C1058: too much data in initialization"), so the
    /// shader never compiled on OpenGL. Every array over the size below is declared as consecutive
    /// pieces of at most <see cref="MaxPieceElements"/> elements and <see cref="MaxPieceComponents"/>
    /// scalars (the largest table seen accepted: 128 <c>float3</c>), and each <c>_N[i]</c> becomes
    /// <c>_N_at(int(i))</c>, which picks the piece with one switch. The values and the result of
    /// every read are unchanged.</para>
    ///
    /// <para>Only a global <c>const</c> array read by index is split. One used whole (passed to a
    /// function, copied, <c>.length()</c>) is left exactly as SPIRV-Cross wrote it.</para>
    /// </remarks>
    internal static class GlslConstantArrays
    {
        internal const int MaxPieceElements = 128;
        internal const int MaxPieceComponents = 384;

        // SPIRV-Cross writes each global constant on one line: const T _N[count] = T[](e0, e1, ...);
        private static readonly Regex Declaration = new(
            @"^const (?<type>[A-Za-z_]\w*) (?<name>[A-Za-z_]\w*)\[(?<count>\d+)\] = \k<type>\[\]\((?<body>.*)\);[ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex VectorType = new(@"^[ibud]?vec(?<n>[234])$", RegexOptions.CultureInvariant);
        private static readonly Regex MatrixType = new(@"^d?mat(?<c>[234])(?:x(?<r>[234]))?$", RegexOptions.CultureInvariant);
        private static readonly Regex NumberLiteral = new(
            @"(?<![\w.])[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?[uUfF]?", RegexOptions.CultureInvariant);

        /// <summary>Returns <paramref name="glsl"/> with its large constant arrays split, or unchanged.</summary>
        public static string Split(string glsl)
        {
            if (string.IsNullOrEmpty(glsl) || !glsl.Contains("const ", StringComparison.Ordinal))
                return glsl;

            var split = new HashSet<string>(StringComparer.Ordinal);
            var output = new StringBuilder(glsl.Length + 1024);
            int copied = 0;
            foreach (Match match in Declaration.Matches(glsl))
            {
                string type = match.Groups["type"].Value;
                string name = match.Groups["name"].Value;
                if (!int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                    || count < 2)
                {
                    continue;
                }

                List<string> elements = SplitTopLevel(match.Groups["body"].Value);
                if (elements == null || elements.Count != count) continue;
                int components = ComponentsOf(type, elements);
                if (count <= MaxPieceElements && (long)count * components <= MaxPieceComponents) continue;
                if (!OnlyIndexed(glsl, name, match.Index + match.Length)) continue;

                int piece = MaxPieceElements;
                while (piece > 1 && (long)piece * components > MaxPieceComponents) piece /= 2;

                output.Append(glsl, copied, match.Index - copied);
                AppendPieces(output, type, name, elements, piece);
                copied = match.Index + match.Length;
                split.Add(name);
            }

            if (split.Count == 0) return glsl;
            output.Append(glsl, copied, glsl.Length - copied);
            return RewriteReads(output.ToString(), split) ?? glsl;
        }

        private static void AppendPieces(StringBuilder output, string type, string name, List<string> elements, int piece)
        {
            int pieces = (elements.Count + piece - 1) / piece;
            for (int p = 0; p < pieces; p++)
            {
                int start = p * piece;
                int length = Math.Min(piece, elements.Count - start);
                output.Append("const ").Append(type).Append(' ').Append(name).Append("_c").Append(p)
                    .Append('[').Append(length).Append("] = ").Append(type).Append("[](")
                    .AppendJoin(", ", elements.GetRange(start, length)).Append(");\n");
            }

            int shift = 0;
            while ((1 << shift) < piece) shift++;
            int mask = piece - 1;
            int lastLength = elements.Count - (pieces - 1) * piece;
            output.Append(type).Append(' ').Append(name).Append("_at(int i)\n{\n    switch (i >> ").Append(shift).Append(")\n    {\n");
            for (int p = 0; p < pieces; p++)
            {
                output.Append("        case ").Append(p).Append(": return ").Append(name).Append("_c").Append(p).Append('[');
                if (p == pieces - 1 && lastLength < piece)
                    output.Append("min(i & ").Append(mask).Append(", ").Append(lastLength - 1).Append(')');
                else
                    output.Append("i & ").Append(mask);
                output.Append("];\n");
            }
            output.Append("    }\n    return ").Append(name).Append("_c0[0];\n}");
        }

        /// <summary>Scalars in one element: from the type, else the most literals any element holds.</summary>
        private static int ComponentsOf(string type, List<string> elements)
        {
            switch (type)
            {
                case "float": case "int": case "uint": case "bool": case "double":
                    return 1;
            }

            Match vector = VectorType.Match(type);
            if (vector.Success) return vector.Groups["n"].Value[0] - '0';
            Match matrix = MatrixType.Match(type);
            if (matrix.Success)
            {
                int columns = matrix.Groups["c"].Value[0] - '0';
                int rows = matrix.Groups["r"].Success ? matrix.Groups["r"].Value[0] - '0' : columns;
                return columns * rows;
            }

            int most = 1;
            foreach (string element in elements)
                most = Math.Max(most, NumberLiteral.Matches(element).Count);
            return most;
        }

        /// <summary>Splits a constructor's arguments at the commas outside any parentheses.</summary>
        private static List<string> SplitTopLevel(string body)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']')
                {
                    if (--depth < 0) return null;
                }
                else if (c == ',' && depth == 0)
                {
                    parts.Add(body.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            if (depth != 0) return null;
            parts.Add(body.Substring(start).Trim());
            return parts;
        }

        /// <summary>True when every later use of <paramref name="name"/> is an index read.</summary>
        private static bool OnlyIndexed(string glsl, string name, int from)
        {
            for (int at = glsl.IndexOf(name, from, StringComparison.Ordinal); at >= 0;
                 at = glsl.IndexOf(name, at + name.Length, StringComparison.Ordinal))
            {
                int end = at + name.Length;
                bool word = (at == 0 || !IsIdentifierChar(glsl[at - 1])) && (end >= glsl.Length || !IsIdentifierChar(glsl[end]));
                if (!word) continue;
                int next = end;
                while (next < glsl.Length && glsl[next] == ' ') next++;
                if (next >= glsl.Length || glsl[next] != '[') return false;
            }

            return true;
        }

        /// <summary>Turns each <c>name[expr]</c> into <c>name_at(int(expr))</c>, inner reads first.</summary>
        private static string RewriteReads(string text, HashSet<string> names)
        {
            var output = new StringBuilder(text.Length + 256);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if ((char.IsAsciiLetter(c) || c == '_') && (i == 0 || !IsIdentifierChar(text[i - 1])))
                {
                    int end = i;
                    while (end < text.Length && IsIdentifierChar(text[end])) end++;
                    string word = text.Substring(i, end - i);
                    int open = end;
                    while (open < text.Length && text[open] == ' ') open++;
                    if (names.Contains(word) && open < text.Length && text[open] == '[')
                    {
                        int close = MatchingBracket(text, open);
                        if (close < 0) return null;
                        string index = RewriteReads(text.Substring(open + 1, close - open - 1), names);
                        if (index == null) return null;
                        output.Append(word).Append("_at(int(").Append(index).Append("))");
                        i = close + 1;
                        continue;
                    }

                    output.Append(word);
                    i = end;
                    continue;
                }

                output.Append(c);
                i++;
            }

            return output.ToString();
        }

        private static int MatchingBracket(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']' && --depth == 0) return i;
            }

            return -1;
        }

        private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }
}

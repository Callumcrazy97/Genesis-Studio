using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The rewrite of bare Script calls (<c>Name;</c> to <c>Name();</c>, <c>if c: Name</c>) gives exactly
/// what the per-name version it replaced gave, and stays fast with hundreds of Scripts. Part of
/// pgsl-scripts.
/// </summary>
internal static class ScriptCallSyntaxChecks
{
    public static void Run(HeadlessContext ctx)
    {
        string[] names =
        [
            "Foo", "FooBar", "foo", "Bar", "_hidden", "A1", "Time Bonus", "my-script", "Café", "if", "Print", "scr_Old", "Spawn",
        ];

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ScriptCallSyntax.SameAsThePerNameRewrite", () =>
        {
            string[] cases =
            [
                "Foo;", "Foo ;", "Foo\n;", "x.Foo;", "FooBar;", "Foo1;", "_Foo;", "2Foo;", "éFoo;", "Fooé;", "foo;", "Bar ;Foo;",
                "if a: Foo", "   if a == 1: FooBar   ", "if a: b: Foo", "if a: NotScript", "\n\n  if x: Bar\n\n\nif y: Foo\n",
                "if a: Foo;", "if (a) Foo;", "var Foo;", "s = \"Foo;\"; Foo;", "// Foo;\nBar;", "/* Foo; */ Spawn;",
                "scr_Foo(); scr_Bar(1, 2); scr_Old;", "Time Bonus;", "if x: Time Bonus", "my-script;", "Café;", "if;", "Print;",
                "_hidden;\nA1 ;\n\tif k:A1", "if a: Foo\r\nBar;\r\n", "\"unterminated Foo;", "Foo;/* open", "x = \"a\\\"Foo;\"; Foo;",
            ];
            int checkedCount = 0;
            foreach (string source in cases)
            {
                Same(source, names);
                checkedCount++;
            }

            // Random code made of the pieces the rewrite looks at.
            string[] pieces =
            [
                "Foo", "FooBar", "foo", "Bar", "Spawn", "_hidden", "A1", "Other", "x", "if", "while", "Time", "Bonus", "Café", "my",
                "-", "script", ";", " ;", ":", " : ", "(", ")", "()", ".", " ", "  ", "\t", "\n", "\r\n", "\n\n", "\"Foo;\"", "\"",
                "// Foo;\n", "/* Bar; */", "scr_", "scr_Foo(", "1", "==", "=", "{", "}",
            ];
            var random = new Random(9102026);
            for (int i = 0; i < 3000; i++)
            {
                var text = new StringBuilder();
                int length = random.Next(1, 40);
                for (int p = 0; p < length; p++) text.Append(pieces[random.Next(pieces.Length)]);
                Same(text.ToString(), names);
                checkedCount++;
            }
            Console.WriteLine($"ScriptCallSyntax: {checkedCount} sources rewritten the same as before.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.ScriptCallSyntax.FastWithManyScripts", () =>
        {
            // A game with 253 Scripts: every Script's source is rewritten against every name.
            string[] many = Enumerable.Range(0, 253).Select(i => "Mc" + (char)('A' + i % 26) + "Part" + i).ToArray();
            var source = new StringBuilder();
            for (int line = 0; line < 400; line++)
            {
                source.Append("    if (DsListGet(l, ").Append(line).Append(") == 1) { DrawText(10, 20, \"row ").Append(line).Append("\"); } // note\n");
                if (line % 40 == 0) source.Append("    ").Append(many[line % many.Length]).Append(";\n");
            }
            string code = source.ToString();
            Same(code, many);

            double Time(Func<string> rewrite, int runs)
            {
                rewrite();
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < runs; i++) rewrite();
                return clock.Elapsed.TotalMilliseconds / runs;
            }

            double before = Time(() => Reference(code, many), 3);
            double after = Time(() => ScriptCallSyntax.Apply(code, many), 20);
            string line2 = $"Rewriting a 400-line script against 253 Script names: {before:F1} ms per script before, {after:F2} ms now.";
            Console.WriteLine(line2);
            HeadlessHarness.Assert(after < 20 && after * 5 < before, line2);
        });
    }

    private static void Same(string source, IEnumerable<string> names)
    {
        string expected = Reference(source, names);
        string actual = ScriptCallSyntax.Apply(source, names);
        HeadlessHarness.Assert(expected == actual,
            $"The Script call rewrite changed: '{Escape(source)}' gave '{Escape(actual)}', before '{Escape(expected)}'.");
    }

    private static string Escape(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static readonly HashSet<string> ReferenceReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "while", "for", "return", "true", "false", "var", "with",
        "ScrExecute", "Print", "ShowMessage", "and", "or", "not"
    };

    /// <summary>The rewrite as it was before 9 Oct 2026 (a pattern per name), kept to compare with.</summary>
    internal static string Reference(string source, IEnumerable<string> scriptNames)
    {
        if (string.IsNullOrEmpty(source) || scriptNames == null)
            return source;

        var names = scriptNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && !ReferenceReserved.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();

        if (names.Count == 0)
            return source;

        var sb = new StringBuilder(source.Length + 64);
        foreach ((string text, bool isCode) in ReferenceSplit(source))
        {
            if (!isCode)
            {
                sb.Append(text);
                continue;
            }

            string code = text;
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

    private static List<(string Text, bool IsCode)> ReferenceSplit(string source)
    {
        var list = new List<(string, bool)>();
        int i = 0;
        while (i < source.Length)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                int start = i;
                while (i < source.Length && source[i] != '\n') i++;
                list.Add((source.Substring(start, i - start), false));
                continue;
            }

            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                if (i + 1 < source.Length) i += 2;
                list.Add((source.Substring(start, i - start), false));
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
                list.Add((source.Substring(start, i - start), false));
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
                list.Add((source.Substring(codeStart, i - codeStart), true));
        }

        return list;
    }
}

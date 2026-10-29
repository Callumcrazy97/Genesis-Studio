using System.Globalization;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// PGSL run twice, once by the interpreter alone and once with the compiled tier on, and the two
/// compared variable by variable: a script function called often enough to be compiled must give
/// exactly what the interpreter gives (values, text, instance variables, errors). Each script calls
/// its functions well past the point where the tier compiles them (40 calls), so most of the calls in
/// a run are compiled ones. Covers the language (operators, loops with <c>i++</c>, recursion,
/// <c>break</c> and <c>continue</c>), the commands (maths, typed grids, lists with text, strings counted
/// from 1, <c>Choose</c> with a seed), the engine API in a function, the instance's variables, short
/// circuits, division, and the instruction and recursion limits; and a large Script read back from the
/// compile cache on disk, run both ways.
/// </summary>
internal static class PgslTierSuite
{
    /// <summary>One script: the functions it defines that the compiled tier is expected to compile.</summary>
    private sealed record Corpus(string Name, string Code, string[] Compiled, string[] ErrorsContain);

    // Names of the functions (and the variables worth reading) are unique to each script: the tier
    // keeps the reason a function stays interpreted by its name.
    private static readonly Corpus[] Scripts =
    [
        new("Operators", """
            function TaMix(a, b) {
                var r = (a + b * 2 - a / 4) % 7;
                if (a > b && b != 0 || a == 3) { r += 1; } else { r -= 1; }
                var s = a < b ? a : b;
                r = r + s * 3 - (a <= 2 ? 1 : 0) + (a == b ? 5 : 0);
                r *= 2; r /= 4; r -= 0.5;
                return r;
            }
            function TaBits(a, b) { return (a & 6) + (b | 1) + (a ^ b) + (a << 2) + (b >> 1) + ~a; }
            function TaNeg(a) { return -a + 3 - (-a) * 2; }
            o_mix = 0; o_bits = 0; o_neg = 0;
            for (var k = 0; k < 120; k++) { o_mix += TaMix(k % 9, k % 4); o_bits += TaBits(k % 13, k % 5); o_neg += TaNeg(k); }
            o_mixNegative = TaMix(-7, 3);
            o_mixZero = TaMix(5, 0);
            """, ["TaMix", "TaBits", "TaNeg"], []),

        new("Loops", """
            function TlCount(n) {
                var t = 0; var u = 0; var w = 0;
                for (var i = 0; i < n; i++) { t += i; }
                for (var j = n; j > 0; j--) { u += 1; }
                for (var h = 1; h < 100; h *= 3) { t += h; }
                for (var k2 = 0; k2 < n; k2 += 3) { if (k2 % 2 == 0) { continue; } w += k2; if (w > 500) { break; } }
                var m = 0; while (m < n) { m++; }
                var rp = 0; repeat (5) { rp += 2; }
                var nest = 0;
                for (var a = 0; a < 3; a++) { for (var b = 0; b < 4; b++) { if (b == 2) { break; } nest++; } }
                return t * 1000000 + u * 10000 + w * 100 + m + rp + nest;
            }
            o_loops = 0;
            for (var q = 0; q < 100; q++) { o_loops += TlCount(q % 17); }
            o_big = TlCount(40);
            """, ["TlCount"], []),

        new("Recursion", """
            function TrFact(n) { if (n <= 1) { return 1; } return n * TrFact(n - 1); }
            function TrFib(n) { if (n < 2) { return n; } return TrFib(n - 1) + TrFib(n - 2); }
            function TrEven(n) { if (n == 0) { return 1; } return TrOdd(n - 1); }
            function TrOdd(n) { if (n == 0) { return 0; } return TrEven(n - 1); }
            function TrSum(n) { var s = 0; for (var i = 1; i <= n; i++) { s += TrFact(i % 8 + 1); } return s; }
            o_fact = 0;
            for (var q = 0; q < 100; q++) { o_fact += TrFact(q % 10); }
            o_fib = TrFib(15);
            o_even = TrEven(40); o_odd = TrOdd(41);
            o_sum = TrSum(50);
            """, ["TrFact", "TrFib", "TrEven", "TrOdd"], []),

        new("MathCommands", """
            function TmMath(x) {
                return Floor(x * 1.7) + Ceil(x / 3) + Round(x * 0.5) + Abs(x - 9) + Sign(x - 4) + Min(x, 5) + Max(x, 5) + Clamp(x, 2, 8)
                    + Sqrt(x) + Power(x, 2) + Lerp(0, x, 0.25) + Approach(x, 20, 3) + Sin(x * 15) + Cos(x * 7) + PointDistance(0, 0, x, 3);
            }
            o_math = 0;
            for (var q = 0; q < 100; q++) { o_math += TmMath(q % 23); }
            o_half = TmMath(2.5);
            o_negative = TmMath(-3);
            """, ["TmMath"], []),

        new("TypedGrids", """
            function TgFill(g, n) {
                for (var i = 0; i < n; i++) { DsGridSet(g, i % 8, Floor(i / 8) % 4, i * 37 - 100); }
                return DsGridGetSum(g, 0, 0, 7, 3);
            }
            function TgAdd(g, v) { DsGridAdd(g, 2, 1, v); DsGridMultiply(g, 3, 1, 1.5); return DsGridGet(g, 2, 1) + DsGridGet(g, 3, 1); }
            g8 = DsGridCreate(8, 4, "u8"); g16 = DsGridCreate(8, 4, "u16"); g32 = DsGridCreate(8, 4, "i32"); gf = DsGridCreate(8, 4, "f32"); gd = DsGridCreate(8, 4);
            o_u8 = 0; o_u16 = 0; o_i32 = 0; o_f32 = 0; o_f64 = 0; o_add = 0;
            for (var q = 0; q < 60; q++) {
                o_u8 = TgFill(g8, 32); o_u16 = TgFill(g16, 32); o_i32 = TgFill(g32, 32); o_f32 = TgFill(gf, 32); o_f64 = TgFill(gd, 32);
                o_add += TgAdd(g8, q) + TgAdd(g16, q) + TgAdd(gf, q * 0.1);
            }
            o_copy = DsGridCopyRegion(g16, 0, 0, gd, 1, 1, 6, 2);
            o_copyBack = DsGridCopyRegion(gf, 0, 0, g8, 0, 0, 7, 3);
            o_sum16 = DsGridGetSum(g16, 0, 0, 7, 3); o_sumF32 = DsGridGetSum(gf, 0, 0, 7, 3);
            o_kinds = DsGridKind(g8) + "," + DsGridKind(g16) + "," + DsGridKind(g32) + "," + DsGridKind(gf) + "," + DsGridKind(gd);
            """, ["TgFill", "TgAdd"], []),

        new("ListsWithText", """
            function TlsBuild(n) {
                var l = DsListCreate();
                for (var i = 0; i < n; i++) { DsListAdd(l, i * 3 % 7); }
                DsListInsert(l, 1, 100);
                DsListInsert(l, 2, "mid");
                DsListInsert(l, 0, 5);
                var total = DsListSum(l);
                var s = DsListGetString(l, 3) + DsListSize(l);
                DsListDestroy(l);
                return total + StringLength(s);
            }
            o_lists = 0;
            for (var q = 0; q < 100; q++) { o_lists += TlsBuild(q % 9); }
            nums = DsListCreate(); o_added = DsListAddNumbers(nums, "1, 2.5; 3  4"); o_numsSum = DsListSum(nums);
            """, ["TlsBuild"], []),

        new("StringsFromOne", """
            function TsText(n) {
                var s = "ab" + n;
                var t = StringUpper(s) + StringCopy("hello world", 1 + n % 5, 3) + StringSplitPart("a,b,c", ",", 1 + n % 3) + StringOf(n * 0.5);
                if (StringPos("o", "hello world") == 5) { t = t + "!"; }
                var u = StringInsert("XY", "abcdef", 1 + n % 4) + StringDelete("abcdef", 1 + n % 3, 2) + StringCharAt("hello", 1 + n % 5);
                return t + "|" + u + "|" + StringLength(s);
            }
            o_text = ""; o_len = 0;
            for (var q = 0; q < 100; q++) { o_text = TsText(q); o_len += StringLength(o_text); }
            """, ["TsText"], []),

        new("RandomAndChooseWithASeed", """
            function TrnPick(n) { return Choose(n, n + 1, n + 2, 10 * n) + RandomRange(1, 3) + Random(4); }
            RandomSeed(42);
            o_pick = 0;
            for (var q = 0; q < 100; q++) { o_pick += TrnPick(q % 5); }
            o_after = Random(1000);
            """, ["TrnPick"], []),

        new("EngineApiInAFunction", """
            function TeStep(k) { Engine.Image.Frame = k; Engine.Instance.X = k * 2; Engine.Image.Alpha = 0.5; return Engine.Image.Frame + Engine.Math.Max(k, 1) + Engine.Instance.X; }
            o_api = 0;
            for (var q = 0; q < 100; q++) { o_api += TeStep(q); }
            o_frame = Engine.Image.Frame; o_x = x; o_alpha = image_alpha;
            """, [], []),

        new("InstanceVariables", """
            function TvBump(d) { self.hp = self.hp + d; x += d; return hp; }
            hp = 10; x = 0;
            var evt = 5;
            for (var q = 0; q < 100; q++) { TvBump(1); }
            o_hp = hp; o_x = x; o_evt = evt;
            """, ["TvBump"], []),

        new("ShortCircuits", """
            function TcBump() { GlobalSet("tcHits", GlobalGet("tcHits") + 1); return 1; }
            function TcLogic(a, b) {
                var r = 0;
                if (a > b && TcBump()) { r += 1; }
                if (a > b || TcBump()) { r += 2; }
                r += (a < b ? TcBump() : 0);
                return r;
            }
            GlobalSet("tcHits", 0);
            o_logic = 0;
            for (var q = 0; q < 100; q++) { o_logic += TcLogic(q % 7, q % 5); }
            o_hits = GlobalGet("tcHits");
            """, ["TcBump", "TcLogic"], []),

        new("DivisionAndModulo", """
            function TdDiv(a, b) { return a / b; }
            function TdMod(a, b) { return a % b; }
            o_div = 0; o_mod = 0;
            for (var q = 0; q < 100; q++) { o_div += TdDiv(q, 7); o_mod += TdMod(q - 50, 7); }
            o_inf = TdDiv(1, 0); o_nan = TdDiv(0, 0); o_negInf = TdDiv(-1, 0);
            o_modNegative = TdMod(-7, 3); o_modZero = TdMod(5, 0);
            """, ["TdDiv", "TdMod"], []),

        new("TextKeysInAMap", """
            function TkCount(m, w) {
                var key = "w" + (w % 5);
                if (DsMapExists(m, key)) { DsMapSet(m, key, DsMapGet(m, key) + 1); } else { DsMapSet(m, key, 1); }
                return DsMapSize(m);
            }
            mp = DsMapCreate(); o_size = 0;
            for (var q = 0; q < 100; q++) { o_size = TkCount(mp, q); }
            o_k0 = DsMapGet(mp, "w0"); o_k4 = DsMapGet(mp, "w4");
            """, ["TkCount"], []),

        new("TheInstructionLimit", """
            function TiSpin(n) { var k = 0; while (k < n) { k++; } return k; }
            for (var q = 0; q < 60; q++) { TiSpin(10); }
            o_before = 1;
            o_spun = TiSpin(300000);
            o_after = 1;
            """, ["TiSpin"], ["Maximum instruction limit"]),

        new("TheRecursionLimit", """
            function TdDeep(n) { if (n <= 0) { return 0; } return 1 + TdDeep(n - 1); }
            for (var q = 0; q < 60; q++) { TdDeep(5); }
            o_ok = TdDeep(150);
            o_deep = TdDeep(5000);
            o_after = 1;
            """, ["TdDeep"], ["recursion", "too deeply"]),
    ];

    private sealed record Outcome(Dictionary<string, double> Numbers, Dictionary<string, string> Strings, string Errors, long Compiled);

    // One script as an event body in a VM of its own, as ObjectSandbox runs it but without its
    // diagnostics collector: the compiled tier does not run while per-command notes are collected, and
    // the Player runs without them.
    private static Outcome Execute(string code, bool compiledTier)
    {
        bool was = PgslCompiledTier.Enabled;
        PgslCompiledTier.Enabled = compiledTier;
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();
        PgslContext context = new()
        {
            InstanceId = 1, RoomWidth = 1280, RoomHeight = 720, DrawSurface = new PgslRecordingDrawSurface(),
            DrawColor = System.Drawing.Color.White, DrawAlpha = 1.0,
        };
        PgslContext? previousCommands = PgslCommands.BindContext(context);
        PgslContext? previousBridge = VMEngine.Bridge.GetContext();
        VMEngine.Bridge.SetContext(context);
        try
        {
            long before = PgslCompiledTier.CompiledCount;
            PgslVm vm = VMEngine.CreateVm();
            context.ActiveVm = vm;
            string errors = "";
            try
            {
                CompileResult compiled = VMEngine.Compile(code) ?? throw new InvalidOperationException("The script did not compile.");
                vm.LoadUserFunctions(compiled.UserFunctions);
                vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
            }
            catch (Exception exception) { errors = exception.Message; }
            var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, object value) in vm.GetVariables())
            {
                if (name.StartsWith("__", StringComparison.Ordinal)) continue;
                switch (value)
                {
                    case double number: numbers[name] = number; break;
                    case int integer: numbers[name] = integer; break;
                    case bool flag: numbers[name] = flag ? 1 : 0; break;
                    case string text: strings[name] = text; break;
                }
            }
            return new Outcome(numbers, strings, errors, PgslCompiledTier.CompiledCount - before);
        }
        finally
        {
            PgslCompiledTier.Enabled = was;
            VMEngine.Bridge.SetContext(previousBridge);
            PgslCommands.BindContext(previousCommands);
        }
    }

    /// <summary>
    /// The result variables (o_...) the two runs disagree on, as text; empty when they agree. Handles
    /// (a grid's number) and loop counters are not results and are left out.
    /// </summary>
    private static string Differences(Outcome interpreted, Outcome compiled)
    {
        var differences = new List<string>();
        foreach (string name in interpreted.Numbers.Keys.Union(compiled.Numbers.Keys).Where(name => name.StartsWith("o_", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
        {
            bool inA = interpreted.Numbers.TryGetValue(name, out double a), inB = compiled.Numbers.TryGetValue(name, out double b);
            if (inA && inB && a.Equals(b)) continue;
            differences.Add($"{name}: interpreted {(inA ? a.ToString("R", CultureInfo.InvariantCulture) : "(unset)")}, compiled {(inB ? b.ToString("R", CultureInfo.InvariantCulture) : "(unset)")}");
        }
        foreach (string name in interpreted.Strings.Keys.Union(compiled.Strings.Keys).Where(name => name.StartsWith("o_", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
        {
            bool inA = interpreted.Strings.TryGetValue(name, out string? a), inB = compiled.Strings.TryGetValue(name, out string? b);
            if (inA && inB && string.Equals(a, b, StringComparison.Ordinal)) continue;
            differences.Add($"{name}: interpreted \"{a}\", compiled \"{b}\"");
        }
        return string.Join("; ", differences.Take(6)) + (differences.Count > 6 ? $" (and {differences.Count - 6} more)" : "");
    }

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PgslTier");
        var page = new StringBuilder("# PGSL: interpreter against the compiled tier\n\n| Script | Functions compiled | Why the others stay interpreted | Same result |\n|---|---:|---|---|\n");
        var notes = new List<string>();

        foreach (Corpus script in Scripts)
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Tier.SameResultInterpretedAndCompiled." + script.Name, () =>
            {
                Outcome interpreted = Execute(script.Code, compiledTier: false);
                Outcome compiled = Execute(script.Code, compiledTier: true);
                HeadlessHarness.Assert(interpreted.Compiled == 0, $"With the tier off {interpreted.Compiled} functions were compiled.");

                string[] functions = System.Text.RegularExpressions.Regex.Matches(script.Code, @"function\s+(\w+)\s*\(")
                    .Select(match => match.Groups[1].Value).ToArray();
                string reasons = string.Join(", ", functions.Select(name =>
                    name + (PgslCompiledTier.ReasonFor(name) is { Length: > 0 } why ? " (" + why + ")" : PgslCompiledTier.ReasonFor(name) is null ? " (not hot)" : " (compiled)")));
                string difference = Differences(interpreted, compiled);
                page.AppendLine($"| {script.Name} | {compiled.Compiled} | {reasons} | {(difference.Length == 0 ? "yes" : "NO: " + difference)} |");

                if (script.ErrorsContain.Length == 0)
                {
                    HeadlessHarness.Assert(interpreted.Errors.Length == 0 && compiled.Errors.Length == 0,
                        $"The {script.Name} script reported errors: interpreted '{interpreted.Errors}', compiled '{compiled.Errors}'.");
                }
                else
                {
                    // The same fault in both tiers; the wording may differ (the recursion limit has two messages).
                    bool Named(string errors) => script.ErrorsContain.Any(word => errors.Contains(word, StringComparison.OrdinalIgnoreCase));
                    HeadlessHarness.Assert(Named(interpreted.Errors) && Named(compiled.Errors),
                        $"Expected a {string.Join("/", script.ErrorsContain)} error in both: interpreted '{interpreted.Errors}', compiled '{compiled.Errors}'.");
                    if (!string.Equals(interpreted.Errors, compiled.Errors, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"Tier note: {script.Name} reports it as '{interpreted.Errors}' interpreted and '{compiled.Errors}' compiled.");
                        notes.Add($"- {script.Name}: interpreted `{interpreted.Errors}`; compiled `{compiled.Errors}`");
                    }
                }

                HeadlessHarness.Assert(difference.Length == 0, $"The {script.Name} script gives other results compiled: {difference}");
                foreach (string name in script.Compiled)
                {
                    string? why = PgslCompiledTier.ReasonFor(name);
                    HeadlessHarness.Assert(why is { Length: 0 }, $"{name} should be compiled by the tier after 40 calls ({(why is null ? "never looked at" : why)}).");
                }
                if (script.Compiled.Length > 0)
                    HeadlessHarness.Assert(compiled.Compiled > 0, $"The tier compiled no function of the {script.Name} script.");
            });
        }

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Tier.ALargeScriptReadBackFromTheCacheRunsTheSameBothWays", () => CachedScript(ctx));

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Tier.TheTierCanBeTurnedOffAndOnAgain", () =>
        {
            const string code = "function TtOnce(n) { var s = 0; for (var i = 0; i < n; i++) { s += i; } return s; }\nfor (var q = 0; q < 60; q++) { TtOnce(5); }\no_value = TtOnce(10);";
            bool was = PgslCompiledTier.Enabled;
            Outcome off = Execute(code, false), on = Execute(code, true), offAgain = Execute(code, false);
            HeadlessHarness.Assert(PgslCompiledTier.Enabled == was, "Running a script left the tier switched differently.");
            HeadlessHarness.Assert(off.Compiled == 0 && on.Compiled > 0 && offAgain.Compiled == 0, $"Functions compiled with the tier off, on, off: {off.Compiled}, {on.Compiled}, {offAgain.Compiled}.");
            HeadlessHarness.Assert(off.Numbers["o_value"] == 45 && on.Numbers["o_value"] == 45 && offAgain.Numbers["o_value"] == 45, "The sum came out differently.");
        });

        if (notes.Count > 0) page.AppendLine().AppendLine("## The same fault, worded differently by the two tiers").AppendLine().AppendLine(string.Join("\n", notes));
        string folder = Path.Combine(ctx.Captures);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pgsl-tier.md"), page.ToString());
    }

    // A Script of 64 KB or more is kept compiled on disk (ScriptCompileCache): read back, its hot
    // functions run the same compiled as interpreted, and the same as the freshly compiled Script did.
    private static void CachedScript(HeadlessContext ctx)
    {
        string parent = Path.Combine(ctx.Workspace, "PgslTier");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Tier" + Guid.NewGuid().ToString("N")[..6], "Blank");
        string scripts = Path.Combine(project.AssetsPath, "Scripts");
        Directory.CreateDirectory(scripts);
        var text = new StringBuilder("// A large Script for the compile cache, with functions the tier compiles.\n");
        text.Append("function TcMix(a, b) { var r = (a + b * 2) % 7; if (a > b) { r += 1; } else { r -= 1; } return r * 3 + Floor(a / 2); }\n");
        text.Append("function TcLoop(n) { var t = 0; for (var i = 0; i < n; i++) { t += TcMix(i, 3); } return t; }\n");
        text.Append("function TcText(n) { return StringUpper(\"cache\") + n; }\n");
        for (int f = 0; text.Length < 3 * ScriptCompileCache.SmallestCached; f++)
        {
            text.Append("function TcFill").Append(f).Append("() {\n");
            for (int line = 0; line < 40; line++)
                text.Append("    DsListAdd(tcList, ").Append((line % 7 / 4.0).ToString(CultureInfo.InvariantCulture)).Append("); q = TcMix(").Append(line).Append(", 2) > 3 || false;\n");
            text.Append("}\n");
        }
        File.WriteAllText(Path.Combine(scripts, "TierLarge.pgsl"), text.ToString());
        ResourceNames.Invalidate(project.RootPath);

        const string code = "o_cached = 0; for (var q = 0; q < 100; q++) { o_cached += TcMix(q % 9, q % 4) + TcLoop(q % 11); } o_text = TcText(7);";
        string previous = PgslCommands.ProjectPath;
        try
        {
            PgslCommands.ProjectPath = project.RootPath;
            int writes = ScriptCompileCache.Writes, hits = ScriptCompileCache.Hits;
            ScriptAssetRegistry.ClearCache();
            ScriptAssetRegistry.LoadFromProject(project.RootPath);
            ScriptCompileCache.LastBackgroundWrite?.Join(30000);
            HeadlessHarness.Assert(ScriptCompileCache.Writes > writes, "The large Script was not written to the compile cache.");
            Outcome freshInterpreted = Execute(code, false), freshCompiled = Execute(code, true);

            ScriptAssetRegistry.ClearCache();
            ScriptAssetRegistry.LoadFromProject(project.RootPath);
            HeadlessHarness.Assert(ScriptCompileCache.Hits > hits, "The second load did not read the large Script from the cache.");
            Outcome cachedInterpreted = Execute(code, false), cachedCompiled = Execute(code, true);

            foreach (Outcome outcome in new[] { freshInterpreted, freshCompiled, cachedInterpreted, cachedCompiled })
                HeadlessHarness.Assert(outcome.Errors.Length == 0, "A run of the large Script reported: " + outcome.Errors);
            HeadlessHarness.Assert(Differences(freshInterpreted, freshCompiled).Length == 0, "Fresh Script, interpreted against compiled: " + Differences(freshInterpreted, freshCompiled));
            HeadlessHarness.Assert(Differences(freshInterpreted, cachedInterpreted).Length == 0, "Fresh against read back from the cache, interpreted: " + Differences(freshInterpreted, cachedInterpreted));
            HeadlessHarness.Assert(Differences(cachedInterpreted, cachedCompiled).Length == 0, "Read back from the cache, interpreted against compiled: " + Differences(cachedInterpreted, cachedCompiled));
            HeadlessHarness.Assert(cachedCompiled.Compiled > 0 && freshCompiled.Compiled > 0,
                $"The tier compiled {freshCompiled.Compiled} functions of the fresh Script and {cachedCompiled.Compiled} of the one read from the cache.");
        }
        finally
        {
            PgslCommands.ProjectPath = previous;
            ScriptAssetRegistry.ClearCache();
            File.Delete(Path.Combine(scripts, "TierLarge.pgsl"));
            ResourceNames.Invalidate(project.RootPath);
        }
    }
}

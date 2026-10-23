using System.Globalization;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Number lists (unboxed, ranged), a mesh built in a job and handed over finished from pooled arrays,
/// a job that waits for another and reads what it made, and the caches a worker keeps between jobs.
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    private const string MeshLibrary = """
        // A chunk's top faces, built straight into a mesh lent to the job.
        function MjBuildTops(m, n) {
            for (var i = 0; i < n; i = i + 1) {
                MeshAddQuadColors(m, i, 1, 0, i + 1, 1, 0, i + 1, 1, 1, i, 1, 1, 0, 1, 0, 0, 0, 1, 1,
                    200, 200, 200, 1, 190, 190, 190, 1, 180, 180, 180, 1, 170, 170, 170, 1, i % 2);
            }
            MeshAddCube(m, 0, 5, 0, 1, 63, 255, 255, 255, 0, 0, 1, 1);
            return MeshVertexCount(m);
        }
        // Heights into a number list (the first job), then their sum read by the job waiting for it.
        function MjHeights(heights, n) {
            NumListClear(heights);
            for (var i = 0; i < n; i = i + 1) { NumListAdd(heights, i * 2); }
            return NumListSize(heights);
        }
        function MjSumHeights(heights) { return NumListSum(heights, 0, NumListSize(heights)); }
        function TjFib(n) { if (n < 2) { return n; } return TjFib(n - 1) + TjFib(n - 2); }
        // Functions that read the instance's numbers (compiled, read live), one whose callee would see
        // its own variable (interpreted), one given text (interpreted that call), one moving x.
        function GtScale(v) { return v * gtFactor + gtOffset; }
        function GtSum(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + GtScale(i); } return s; }
        function GtShadow(v) { var gtFactor = 100; return GtScale(v); }
        function GtUse(v) { return gtWord + v; }
        function GtMoveX(dx) { x = x + dx; return x; }
        function GtJob(flag) { if (flag > 0) { return gtNeverSetInJob; } return 7; }
        function GtJobLoop(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + GtJob(0); } return s; }
        function GtNothing(v) { if (v > 0) { return; } return v * 2; }
        // Text only as a command's argument: a variable set by name, a map read by a number key, a list
        // given text with nothing used back (compiled, the commands called the interpreter's way); a
        // variable command naming the function's own variable, and text kept in a variable (interpreted).
        function GtCount(n) { for (var i = 0; i < n; i = i + 1) { VariableSet("gtCounter", gtCounter + 1); } return gtCounter; }
        function GtSeen(m, k) { if (DsMapExists(m, k)) { return 1; } DsMapSet(m, k, 1); DsListAddString(gtNames, "seen"); return 0; }
        function GtOwnName(n) { var gtLocal = n; VariableSet("gtLocal", 5); return gtLocal; }
        function GtOwnRead(n) { var gtLocal2 = n; return VariableGet("gtLocal2"); }
        function GtWord(n) { var w = "a"; return n; }
        // Text worked with (compiled with values): keys made from numbers, parts of a text, a value
        // that is text or a number, text compared with a number, a numbers-only function called, a
        // function building text by calling itself, and sprite_index.
        function GtKey(x, y) { return StringOf(x) + "," + StringOf(y); }
        function GtParts(s) { var n = 0; for (var i = 1; i <= 3; i = i + 1) { n = n + RealOf(StringSplitPart(s, ",", i)); } return n; }
        function GtMix(a) { var t = "x"; if (a > 2) { t = t + a; } else { t = a; } return t; }
        function GtEq(a) { return (a == "5") + (a != "5") * 10 + ("ab" + a == "ab5") * 100; }
        function GtCallsNumbers(n) { var s = ""; for (var i = 0; i < n; i = i + 1) { s = s + GtScale(i) + ";"; } return s; }
        function GtSelf(n) { if (n <= 0) { return ""; } return GtSelf(n - 1) + "a"; }
        function GtSprite(n) { return sprite_index + n; }
        // Functions calling each other (compiled together), a pair where one cannot compile (the other,
        // made while it was, is withdrawn), and a variable of its own read before it is set.
        function GtEven(n) { if (n <= 0) { return "even"; } return GtOdd(n - 1); }
        function GtOdd(n) { if (n <= 0) { return "odd"; } return GtEven(n - 1); }
        function GtRevA(n) { if (n <= 0) { return "a"; } var r = GtRevB(n - 1); var q = [n]; return r + q[0]; }
        // A callee reading its caller's variable (the caller gives it its frame), and a function calling
        // interpreted ones (indexing a list keeps them interpreted), one of them reading the caller's variable.
        function GtScopeInner() { return gtScopeV * 2; }
        function GtScopeOuter(n) { var gtScopeV = n + 1; return GtScopeInner(); }
        function GtInterp(n) { var q = [n]; return q[0] + 1; }
        function GtInterpRead() { var q = [0]; return q[0] + gtT2; }
        function GtCallsInterp(n) { var gtT2 = n * 3; return GtInterp(n) + GtInterpRead(); }
        function GtRevB(n) { return GtRevA(n) + "b"; }
        function GtUnset(flag) { if (flag > 0) { gtU = 5; } return gtU; }
        function GtTextLoop(n) { var s = 0; var k = ""; for (var i = 0; i < n; i = i + 1) { k = StringOf(i % 100) + ","; s = s + StringLength(k) + i % 7 + gtOffset; } return s; }
        // A table a worker fills once and reads on later jobs.
        function MjCached(n) {
            var table = JobCacheNumList("squares", n);
            if (NumListGet(table, n - 1) == 0) { for (var i = 0; i < n; i = i + 1) { NumListSet(table, i, i * i); } JobCacheSet("fills", JobCacheGet("fills") + 1); }
            return NumListGet(table, n - 1);
        }
        """;

    private static void RunMeshesAndLists(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        ScriptAssetRegistry.Register("MeshJobLibrary", MeshLibrary);

        // Numbers kept as numbers, worked on a range at a time.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.NumberListsWorkOnRangesWithoutBoxing", () =>
        {
            using Bench bench = new();
            string result = bench.Text("""
                a = NumListCreate(); b = NumListCreate();
                for (var i = 0; i < 10; i = i + 1) { NumListAdd(a, 10 - i); }
                NumListFill(b, 0, 4, 7);
                NumListCopy(b, 2, a, 0, 3);
                NumListAddRange(b, a, 8, 5);
                NumListScale(a, 0, 3, 2, 1);
                NumListSort(a, 0, 10, true);
                s = NumListSum(b, 0, 100); mn = NumListMin(a, 0, 10); mx = NumListMax(a, 0, 10);
                l = DsListCreate(); NumListToList(b, l);
                r = StringFormatNumber(NumListSize(b), 0) + " " + StringFormatNumber(s, 0) + " " + StringFormatNumber(mn, 0) + " " + StringFormatNumber(mx, 0)
                    + " " + StringFormatNumber(NumListGet(b, 2), 0) + " " + StringFormatNumber(DsListSize(l), 0) + " " + StringFormatNumber(NumListIndexOf(a, 21), 0);
                """);
            // b: four 7s, a's first three (10 9 8) copied in at 2, then a's last two (2 1) added: 7 7 10 9 8 2 1.
            const string expected = "7 44 1 21 10 7 9";
            row("Number lists", "fill, copy, add a range, scale, sort, sum, min, max, to a list", result == expected ? "PASS" : "FAIL", result);
            HeadlessHarness.Assert(result == expected, $"Number list results were '{result}', expected '{expected}'.");
        });

        // A job builds a mesh lent to it and JobTake hands the finished arrays over: the same vertices
        // as building it on the game's thread, and a rebuild reuses pooled arrays.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AMeshBuiltInAJobIsHandedOverFinished", () =>
        {
            using Bench bench = new();
            double direct = bench.Number("md = MeshCreate(); r = MjBuildTops(md, 40);");
            ScriptMeshes.TryGetVertices((int)bench.Number("r = md;"), out MeshVertex[] directVertices, out ushort[] directIndices);
            long reusedBefore = MeshArrayPool.Reused;
            double total = 0;
            for (int round = 0; round < 3; round++)
            {
                double job = bench.Number("""
                    if (round == 0) { mj = MeshCreate(); }
                    jm = JobScriptCreate("MjBuildTops"); JobScriptMesh(jm, mj);
                    r = JobScriptStart(jm, mj, 40) ? jm : -1;
                    """.Replace("round == 0", round == 0 ? "1 == 1" : "1 == 0", StringComparison.Ordinal));
                HeadlessHarness.Assert(job > 0, "The mesh job did not start: " + PgslCommands.JobLastError());
                string status = bench.Wait(job);
                HeadlessHarness.Assert(status == "succeeded", $"The mesh job ended {status}: {PgslCommands.JobError(job)}");
                total = PgslCommands.JobResultNumber(job);
                HeadlessHarness.Assert(PgslCommands.JobTake(job), "JobTake refused the mesh job: " + PgslCommands.JobLastError());
                PgslCommands.JobRelease(job);
            }
            int mesh = (int)bench.Number("r = mj;");
            ScriptMeshes.TryGetVertices(mesh, out MeshVertex[] jobVertices, out ushort[] jobIndices);
            bool same = total == direct && jobVertices.AsSpan().SequenceEqual(directVertices) && jobIndices.AsSpan().SequenceEqual(directIndices);
            long reused = MeshArrayPool.Reused - reusedBefore;
            bool notUploaded = !ScriptMeshes.IsUploaded(mesh);
            bench.Run("MeshDestroy(md); MeshDestroy(mj); r = 0;");
            row("Jobs: meshes", "a mesh built in a job (JobScriptMesh) equals the game-thread build; rebuilt three times", same && reused > 0 ? "PASS" : "FAIL",
                $"{jobVertices.Length} vertices, {jobIndices.Length / 3} triangles; pooled arrays reused {reused} times; waits for upload: {notUploaded}");
            HeadlessHarness.Assert(same, $"The job's mesh differs: {jobVertices.Length} vertices / {jobIndices.Length} indices against {directVertices.Length} / {directIndices.Length}, result {total} against {direct}.");
            HeadlessHarness.Assert(reused > 0, "Rebuilding a mesh in jobs never reused a pooled array.");
            HeadlessHarness.Assert(notUploaded, "A handed-over mesh read as uploaded before it was drawn.");

            // A mesh not lent to the job is an error, not the game's mesh written from a worker.
            double stray = bench.Number("""ms = MeshCreate(); sj = JobRunScript("MjBuildTops", ms, 2); r = sj;""");
            string strayStatus = bench.Wait(stray);
            HeadlessHarness.Assert(strayStatus == "failed" && PgslCommands.JobError(stray).Contains("was not given to this job", StringComparison.Ordinal),
                $"A job writing a mesh not lent to it ended {strayStatus}: {PgslCommands.JobError(stray)}");
            PgslCommands.JobRelease(stray);
            bench.Run("MeshDestroy(ms); r = 0;");
        });

        // A job that waits for another starts only when it has finished, and reads what it made.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AJobWaitsForAnotherAndReadsWhatItMade", () =>
        {
            using Bench bench = new();
            double second = bench.Number("""
                hs = NumListCreate();
                first = JobScriptCreate("MjHeights"); JobScriptNumList(first, hs, true);
                sum = JobScriptCreate("MjSumHeights"); JobScriptNumList(sum, hs, false); JobScriptAfter(sum, first);
                JobScriptStart(sum, hs);
                waitingState = JobStatus(sum);
                JobScriptStart(first, hs, 50);
                r = sum;
                """);
            string waiting = bench.Text("r = waitingState;");
            string status = bench.Wait(second);
            double result = PgslCommands.JobResultNumber(second);
            double first = bench.Number("r = first;");
            bench.Wait(first);
            // 0 + 2 + ... + 98: the first job's heights, which the game's list never held.
            bool pass = waiting == "waiting" && status == "succeeded" && result == 2450 && PgslCommands.NumListSize(bench.Number("r = hs;")) == 0;
            row("Jobs: dependencies", "JobScriptAfter: waits, then reads the first job's list", pass ? "PASS" : "FAIL",
                $"state before the first started: {waiting}; ended {status}; sum {result}");
            HeadlessHarness.Assert(pass, $"Waiting job: '{waiting}' before, {status} after, sum {result} (expected 2450), game's list size {PgslCommands.NumListSize(bench.Number("r = hs;"))}.");
            PgslCommands.JobRelease(second); PgslCommands.JobRelease(first);
        });

        // A function called often whose work is numbers runs compiled: the same result, quicker, and
        // the instruction limit still stops a loop that runs too long.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.HotNumberFunctionsRunCompiled", () =>
        {
            using Bench bench = new();
            double Time(string source, out double result)
            {
                double best = double.MaxValue; result = 0;
                for (int round = 0; round < 5; round++)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    result = bench.Number(source);
                    best = Math.Min(best, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                return best;
            }
            const string Loop = "r = SnLoopScript(3000);";
            bool before = Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled;
            try
            {
                Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled = false;
                double interpreted = Time(Loop, out double slowResult);
                Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled = true;
                long compiledBefore = Genesis.Runtime.Scripting.VM.PgslCompiledTier.CompiledCount;
                for (int i = 0; i < 50; i++) bench.Run("r = SnLoopScript(10); r = WjSpin(10); r = WjText(1, 2);");
                bench.Run("ScriptProfileReset(); ScriptProfileEnable(true); r = 0;");
                double compiled = Time(Loop, out double fastResult);
                string report = bench.Text("r = ScriptProfileReport(5); ScriptProfileEnable(false);");
                double fib = bench.Number("r = TjFib(20);");
                string text = bench.Text("r = WjText(3, 4);");
                string limit;
                try { bench.Run("r = WjSpin(300000);"); limit = "ran"; }
                catch (Exception error) { limit = error.Message; }
                long made = Genesis.Runtime.Scripting.VM.PgslCompiledTier.CompiledCount - compiledBefore;
                double speedup = interpreted / Math.Max(0.001, compiled);
                bool same = fastResult == slowResult;
                bool pass = same && speedup >= 2 && fib == 6765 && text == "3-4" && limit.Contains("Maximum instruction limit", StringComparison.Ordinal)
                    && report.Contains("SnLoopScript | 5 |", StringComparison.Ordinal) && report.Contains("| 5" + Environment.NewLine, StringComparison.Ordinal);
                row("Compiled functions", "SnLoopScript(3000): script value noise, interpreted against compiled", pass ? "PASS" : "FAIL",
                    $"{interpreted:F2} ms -> {compiled:F2} ms ({speedup:F1}x), same result {same}; {made} functions compiled; TjFib(20) {fib}; limit: {(limit.Length > 40 ? limit[..40] : limit)}");
                Console.WriteLine("Script profile:\n" + report);
                HeadlessHarness.Assert(same, $"Compiled result {fastResult} differs from interpreted {slowResult}.");
                HeadlessHarness.Assert(speedup >= 2, $"Compiled took {compiled:F2} ms against {interpreted:F2} ms interpreted ({speedup:F1}x).");
                HeadlessHarness.Assert(fib == 6765 && text == "3-4", $"TjFib(20) gave {fib}, WjText gave '{text}'.");
                HeadlessHarness.Assert(limit.Contains("Maximum instruction limit", StringComparison.Ordinal), "A compiled loop ran past the instruction limit: " + limit);
                // A function called from a compiled function runs inside it: its time is its caller's.
            }
            finally { Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled = before; }
        });

        // Functions reading the instance's numbers compile and read them live; a callee that would see
        // its caller's own variable, a name holding text, x and a job's unset name all keep the
        // interpreter's answers.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.FunctionsReadingInstanceNumbersRunCompiled", () =>
        {
            using Bench bench = new();
            bench.Run("ScriptInstructionLimit(50000000); gtFactor = 2; gtOffset = 1; gtWord = \"ab\"; x = 10; r = 0;");
            for (int i = 0; i < 60; i++) bench.Run("r = GtSum(3) + GtShadow(1) + GtMoveX(0);");
            bench.Run("r = GtUse(1);");
            for (int i = 0; i < 60; i++) bench.Run("r = GtUse(1);");
            double sum = bench.Number("r = GtSum(100);");
            double live = bench.Number("gtFactor = 3; r = GtSum(10);");
            double shadow = bench.Number("r = GtShadow(5);");
            string word = bench.Text("r = GtUse(5);");
            double moved = bench.Number("x = 10; for (var i = 0; i < 50; i = i + 1) { GtMoveX(1); } r = x;");
            string sumWhy = Genesis.Runtime.Scripting.VM.PgslCompiledTier.ReasonFor("GtSum") ?? "never looked at";
            string shadowWhy = Genesis.Runtime.Scripting.VM.PgslCompiledTier.ReasonFor("GtShadow") ?? "never looked at";
            for (int i = 0; i < 60; i++) bench.Run("r = GtNothing(-1); r = GtNothing(1);");
            string nothing = bench.Text("r = GtNothing(1);") + "|" + bench.Text("r = GtNothing(-3);");
            string nothingWhy = Genesis.Runtime.Scripting.VM.PgslCompiledTier.ReasonFor("GtNothing") ?? "never looked at";
            HeadlessHarness.Assert(nothing == "|-6" && nothingWhy.Length == 0, $"A function with `return;` should compile and return nothing there (got \"{nothing}\", '{nothingWhy}').");
            double job = bench.Number("""r = JobRunScript("GtJobLoop", 60);""");
            string jobStatus = bench.Wait(job);
            double jobResult = PgslCommands.JobResultNumber(job);
            PgslCommands.JobRelease(job);
            // GtShadow's callee reads its own variable: compiled with values, it gives the callee its frame.
            bool pass = sum == 10000 && live == 145 && shadow == 501 && word == "ab5" && moved == 60 && sumWhy.Length == 0
                && shadowWhy.Length == 0 && jobStatus == "succeeded" && jobResult == 420;
            row("Compiled functions", "reading the instance's numbers (GtSum), a caller's own variable, text, x, a job's unset name", pass ? "PASS" : "FAIL",
                $"sum {sum}, live {live}, shadow {shadow}, text {word}, x {moved}, GtSum '{sumWhy}', GtShadow '{shadowWhy}', job {jobStatus} {jobResult}");
            PgslCommands.ScriptInstructionLimit(0);
            HeadlessHarness.Assert(pass, $"Compiled functions reading outside names: sum {sum} (10000), live {live} (145), shadow {shadow} (501), text '{word}' (ab5), x {moved} (60), GtSum '{sumWhy}' (compiled), GtShadow '{shadowWhy}', job {jobStatus} {jobResult} (420).");
        });

        // A function whose only text is a command's argument runs compiled, with what the interpreter
        // gives: GenesisCraft's generation functions were all interpreted for a VariableSet("name", n).
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.FunctionsGivingTextToCommandsRunCompiled", () =>
        {
            string Results(bool compiled)
            {
                bool was = Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled;
                Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled = compiled;
                try
                {
                    using Bench bench = new();
                    bench.Run("gtCounter = 0; gtNames = DsListCreate(); gtMap = DsMapCreate();");
                    for (int i = 0; i < 60; i++) bench.Run("r = GtCount(1) + GtSeen(gtMap, 5) + GtOwnName(1) + GtWord(1);");
                    double counted = bench.Number("r = GtCount(10);");
                    double first = bench.Number("r = GtSeen(gtMap, 777);"), again = bench.Number("r = GtSeen(gtMap, 777);");
                    double names = bench.Number("r = DsListSize(gtNames);");
                    double own = bench.Number("r = GtOwnName(3);"), word = bench.Number("r = GtWord(4);");
                    for (int i = 0; i < 60; i++)
                        bench.Run("r = GtKey(1, 2) + GtParts(\"1,2,3\") + GtMix(1) + GtEq(5) + GtCallsNumbers(2) + GtSelf(2) + GtSprite(1);");
                    string texts = string.Join(" | ",
                        bench.Text("r = GtKey(12, -3.5);"), bench.Text("r = GtParts(\"1.5,x,4\");"), bench.Text("r = GtMix(7);"), bench.Text("r = GtMix(1);"),
                        bench.Text("r = GtEq(5);"), bench.Text("r = GtEq(\"5\");"), bench.Text("r = GtEq(6);"), bench.Text("r = GtCallsNumbers(4);"),
                        bench.Text("r = GtSelf(5);"), bench.Text("sprite_index = \"Hero\"; r = GtSprite(2);"));
                    for (int i = 0; i < 60; i++) bench.Run("r = GtRevA(1) + GtEven(3) + GtUnset(1) + GtOwnRead(1);");
                    texts += " | own read " + bench.Text("r = GtOwnRead(9);");
                    for (int i = 0; i < 60; i++) bench.Run("r = GtScopeOuter(1) + GtCallsInterp(1);");
                    texts += " | scope " + bench.Text("r = GtScopeOuter(4);") + " | interpreted " + bench.Text("r = GtCallsInterp(5);");
                    for (int i = 0; i < 60; i++) bench.Run("r = GtRevB(1);");
                    texts += " | " + string.Join(" | ", bench.Text("r = GtEven(7);"), bench.Text("r = GtEven(4);"), bench.Text("r = GtRevA(3);"),
                        bench.Text("r = GtRevB(2);"), bench.Text("gtU = 77; r = GtUnset(0);"), bench.Text("r = GtUnset(1);"));
                    // How long a loop working with text takes this way (printed, not checked).
                    bench.Run("gtOffset = 1; ScriptInstructionLimit(50000000);");
                    for (int i = 0; i < 60; i++) bench.Run("r = GtTextLoop(10);");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    double loop = bench.Number("r = GtTextLoop(200000);");
                    Console.WriteLine($"GtTextLoop(200000) {(compiled ? "compiled" : "interpreted")}: {clock.Elapsed.TotalMilliseconds:F1} ms ({loop}).");
                    PgslCommands.ScriptInstructionLimit(0);
                    return $"counted {counted}, seen {first}/{again}, names {names}, own {own}, word {word}; {texts}";
                }
                finally { Genesis.Runtime.Scripting.VM.PgslCompiledTier.Enabled = was; }
            }
            string interpreted = Results(compiled: false);
            string compiled = Results(compiled: true);
            string Why(string name) => Genesis.Runtime.Scripting.VM.PgslCompiledTier.ReasonFor(name) ?? "never looked at";
            // A variable set by name never reaches a function's own (GtOwnName); one read by name, and a
            // callee reading the caller's, see the caller's variables in the frame it gives them (GtOwnRead,
            // GtScopeOuter); interpreted functions are called the interpreter's way (GtCallsInterp).
            string count = Why("GtCount"), seen = Why("GtSeen"), ownWhy = Why("GtOwnRead");
            string[] withValues = ["GtWord", "GtKey", "GtParts", "GtMix", "GtEq", "GtCallsNumbers", "GtSelf", "GtSprite", "GtEven", "GtOdd", "GtUnset",
                "GtOwnName", "GtOwnRead", "GtScopeOuter", "GtScopeInner", "GtCallsInterp"];
            string valueWhy = string.Join(", ", withValues.Select(name => name + " '" + Why(name) + "'"));
            string revA = Why("GtRevA"), revB = Why("GtRevB"), interp = Why("GtInterp");
            valueWhy += $", GtRevA '{revA}', GtRevB '{revB}', GtInterp '{interp}'";
            bool pass = compiled == interpreted && count.Length == 0 && seen.Length == 0 && withValues.All(name => Why(name).Length == 0)
                && revA.Length > 0 && revB.Contains("calls it back", StringComparison.Ordinal) && interp.Length > 0;
            row("Compiled functions", "text as a command's argument, and text worked with (compiled with values)", pass ? "PASS" : "FAIL",
                $"{compiled}; GtCount '{count}', GtSeen '{seen}', GtOwnRead '{ownWhy}', {valueWhy}");
            HeadlessHarness.Assert(pass, $"Functions with text: compiled '{compiled}', interpreted '{interpreted}'; GtCount '{count}' (compiled), "
                + $"GtSeen '{seen}' (compiled), {valueWhy} (each compiled but GtRevA, GtRevB withdrawn, GtInterp).");
        });

        // What a worker keeps between jobs: filled once on each worker, read again afterwards.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.WorkersKeepTheirCachesBetweenJobs", () =>
        {
            using Bench bench = new();
            PgslCommands.JobCacheReset();
            double[] results = new double[24];
            for (int i = 0; i < results.Length; i++)
            {
                double job = bench.Number("""r = JobRunScript("MjCached", 300);""");
                HeadlessHarness.Assert(job > 0, "A cache job did not start: " + PgslCommands.JobLastError());
                HeadlessHarness.Assert(bench.Wait(job) == "succeeded", "A cache job failed: " + PgslCommands.JobError(job));
                results[i] = PgslCommands.JobResultNumber(job);
                PgslCommands.JobRelease(job);
            }
            bool right = results.All(value => value == 299 * 299);
            row("Jobs: worker caches", "a table filled once per worker and read by later jobs", right ? "PASS" : "FAIL",
                string.Join(",", results.Distinct().Select(v => v.ToString(CultureInfo.InvariantCulture))));
            HeadlessHarness.Assert(right, "A cached table gave a wrong value: " + string.Join(",", results));
        });
    }
}

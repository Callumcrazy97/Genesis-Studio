using System.Globalization;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// What a command call costs in garbage, by the shape of its arguments and result: a script calling
/// commands every frame should allocate nothing for the call itself (a command whose work makes
/// something, such as text, still does).
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    private static void RunBridge(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.CommandCallsAllocateNothing", () =>
        {
            using Bench bench = new();
            bench.Run("""
                bl = DsListCreate(); DsListAdd(bl, 4); bg = DsGridCreate(4, 4); bn = NumListCreate(); NumListAdd(bn, 2);
                bm = DsMapCreate(); DsMapSet(bm, "k", 3); bt = "some text"; bx = 0.5; bmesh = MeshCreate(); ScriptInstructionLimit(50000000); r = 0;
                """);
            const int Calls = 4000;
            (string Name, string Call)[] shapes =
            [
                ("number in, number out (Floor)", "bx = Floor(bx + 0.25);"),
                ("two numbers (DsListGet)", "bx = DsListGet(bl, 0);"),
                ("three numbers (DsGridGet)", "bx = DsGridGet(bg, 1, 1);"),
                ("text in (StringLength)", "bx = StringLength(bt);"),
                ("map key (DsMapGet)", "bx = DsMapGet(bm, \"k\");"),
                ("true/false out (NumListSet)", "ok = NumListSet(bn, 0, bx);"),
                ("true/false in (DrawSetBlendLinear)", "DrawSetBlendLinear(false);"),
                ("none (ViewGetCount)", "bx = ViewGetCount();"),
                ("engine command (Engine.Camera3DSetPos)", "Engine.Camera3DSetPos(7, bx, 1, 2);"),
                ("four numbers, then two (NumListAdd3, NumListResize)", "NumListAdd3(bn, 1, 2, 3); NumListResize(bn, 1);"),
                ("37 numbers (MeshAddQuadColors)", "MeshAddQuadColors(bmesh, 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 1, 255, 255, 255, 1, 255, 255, 255, 1, 255, 255, 255, 1, 255, 255, 255, 1, 0); MeshClear(bmesh);"),
            ];
            string Loop(string body) => $"for (var i = 0; i < {Calls}; i = i + 1) {{ {body} }} r = bx;";
            double Bytes(string body)
            {
                bench.Run(Loop(body));
                long best = long.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    bench.Run(Loop(body));
                    best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
                }
                return best;
            }
            double baseline = Bytes("bx = bx + 0;");
            var failures = new List<string>();
            foreach ((string name, string call) in shapes)
            {
                double perCall = (Bytes(call) - baseline) / Calls;
                bool none = perCall < 0.5;
                row("Command calls", name, none ? "PASS" : "FAIL", perCall.ToString("F1", CultureInfo.InvariantCulture) + " bytes a call");
                if (!none) failures.Add($"{name}: {perCall:F1} bytes a call");
            }
            // A whole number or true/false given where any value goes (a list entry, a map value) is a shared box.
            foreach ((string name, string call) in new[] {
                ("a whole number as any value (DsListSet)", "DsListSet(bl, 0, Floor(bx) + 7);"),
                ("true/false as any value (DsMapSet)", "DsMapSet(bm, \"k\", bx > 2);") })
            {
                double perCall = (Bytes(call) - baseline) / Calls;
                row("Command calls", name, perCall < 0.5 ? "PASS" : "FAIL", perCall.ToString("F1", CultureInfo.InvariantCulture) + " bytes a call");
                HeadlessHarness.Assert(perCall < 0.5, $"{name}: {perCall:F1} bytes a call.");
            }
            // A reserved list made and destroyed over and over (a chunk's faces) reuses its storage.
            bench.Run("ScriptInstructionLimit(50000000); r = 0;");
            const int Lists = 200;
            string listLoop = $"for (var i = 0; i < {Lists}; i = i + 1) {{ fl = DsListCreate(); DsListReserve(fl, 6000); DsListAdd(fl, i); DsListDestroy(fl); }} r = 0;";
            bench.Run(listLoop);
            long listBefore = GC.GetAllocatedBytesForCurrentThread();
            bench.Run(listLoop);
            double perList = (GC.GetAllocatedBytesForCurrentThread() - listBefore) / (double)Lists;
            row("Command calls", "a 6000-entry reserved list made and destroyed", perList < 1024 ? "PASS" : "FAIL", perList.ToString("F0", CultureInfo.InvariantCulture) + " bytes a list");
            HeadlessHarness.Assert(perList < 1024, $"A reserved list made and destroyed allocated {perList:F0} bytes (its storage was not reused).");
            // A call of a hot numbers-only script function runs compiled, and allocates nothing either.
            double compiled = (Bytes("bx = TjFib(3) + bx * 0;") - baseline) / Calls;
            row("Command calls", "a hot script function (compiled)", compiled < 0.5 ? "PASS" : "FAIL", compiled.ToString("F1", CultureInfo.InvariantCulture) + " bytes a call");
            HeadlessHarness.Assert(compiled < 0.5, $"A compiled script function call allocated {compiled:F1} bytes.");
            PgslCommands.ScriptInstructionLimit(0);
        });

        // What a worker job costs in garbage from JobScriptCreate to JobRelease: its VM is the worker
        // bridge's own, reused, so what is left is the job's bookkeeping.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AJobLeavesLittleGarbage", () =>
        {
            using Bench bench = new();
            double Cycle(int jobs)
            {
                long before = GC.GetTotalAllocatedBytes(true);
                for (int k = 0; k < jobs; k++)
                {
                    double job = bench.Number("""jg = JobScriptCreate("TjFib"); JobScriptStart(jg, 5); r = jg;""");
                    bench.Wait(job);
                    PgslCommands.JobTake(job);
                    PgslCommands.JobRelease(job);
                }
                return (GC.GetTotalAllocatedBytes(true) - before) / (double)jobs;
            }
            Cycle(50);
            double perJob = Math.Min(Cycle(200), Cycle(200));
            row("Jobs: garbage", "a job created, started, taken and released (TjFib(5))", perJob < 8192 ? "PASS" : "FAIL",
                (perJob / 1024).ToString("F2", CultureInfo.InvariantCulture) + " KB a job");
            HeadlessHarness.Assert(perJob < 8192, $"A job allocated {perJob / 1024:F2} KB from create to release.");
        });
    }
}

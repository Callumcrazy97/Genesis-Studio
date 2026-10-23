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
            PgslCommands.ScriptInstructionLimit(0);
        });
    }
}

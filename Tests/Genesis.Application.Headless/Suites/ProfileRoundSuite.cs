using System.Runtime.ExceptionServices;
using System.Text.Json;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.PG;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The behaviour kept by the 17 October 2026 allocation work (Documentation/Verification/2026-10-17-GameProfiles.md):
/// script-call syntax for names with spaces, list reads without boxing, and unset names read without exceptions.
/// </summary>
internal static class ProfileRoundSuite
{
    public static void Run(HeadlessContext context)
    {
        VMEngine.Initialize();
        PgslContext? previousBridge = VMEngine.Bridge.GetContext(), previousCommands = PgslCommands.GetContext();
        string? previousProject = PgslCommands.ProjectPath;
        try
        {
            PgslCommands.ProjectPath = null;
            VMEngine.Bridge.SetContext(new());

            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.ScriptCalls.SpacedNamesStillCallAndAbsentNamesAreSkipped", () =>
            {
                string called = ScriptCallSyntax.Apply("x = 1;\nPw Game;\n", ["Pw Game", "Other Thing", "Plain"]);
                HeadlessHarness.Assert(called.Contains("Pw Game();", StringComparison.Ordinal), "A Script name with a space was not called: " + called);
                string colon = ScriptCallSyntax.Apply("if ready: Pw Game\n", ["Pw Game"]);
                HeadlessHarness.Assert(colon.Contains("if (ready) { Pw Game(); }", StringComparison.Ordinal), "The colon form lost its call: " + colon);
                string untouched = ScriptCallSyntax.Apply("y = 2;\n", ["Pw Game", "Other Thing"]);
                HeadlessHarness.Assert(untouched == "y = 2;\n", "Code without the names changed: " + untouched);
            });

            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Lists.NumberEntriesReadUnboxedAndIndexesBehaveAsBefore", () =>
            {
                PGList numbers = PGList.Create();
                numbers.Add(1.5);
                numbers.Add(2d);
                HeadlessHarness.Assert(numbers.TryGetNumber(1, out double second) && second == 2 && !numbers.TryGetNumber(2, out _)
                    && !numbers.TryGetNumber(-1, out _), "A list of numbers did not hand back its entries.");
                PGList mixed = PGList.Create();
                mixed.Add(4d);
                mixed.Add("text");
                HeadlessHarness.Assert(!mixed.TryGetNumber(0, out _) && Equals(mixed.Get(0), 4d), "A mixed list took the number path.");

                var vm = VMEngine.CreateVm();
                CompiledScriptAsset source = ScriptAssetCompiler.Compile("ListReads", """
                    var l = [1.5, 2, 3];
                    var a = l[1];
                    l[2] = 9;
                    var b = l[2];
                    var m = ["x", 4];
                    var c = m[1];
                    var d = m[0];
                    """);
                vm.LoadUserFunctions(source.CompileResult.UserFunctions);
                vm.Execute(source.CompileResult.Instructions, source.CompileResult.Constants, clearVariables: false);
                var variables = vm.GetVariables();
                HeadlessHarness.Assert(Convert.ToDouble(variables["a"]) == 2 && Convert.ToDouble(variables["b"]) == 9
                    && Convert.ToDouble(variables["c"]) == 4 && (string)variables["d"] == "x",
                    "List reads or writes changed: " + JsonSerializer.Serialize(variables.Where(pair => pair.Key.Length == 1).ToDictionary()));
            });

            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Values.AnUnsetNameReadsZeroWithoutAnException", () =>
            {
                int thrown = 0;
                EventHandler<FirstChanceExceptionEventArgs> count = (_, e) =>
                {
                    if (e.Exception is InvalidOperationException) Interlocked.Increment(ref thrown);
                };
                var vm = VMEngine.CreateVm();
                CompiledScriptAsset source = ScriptAssetCompiler.Compile("UnsetRead", "var r = neverSetAnywhereInThisCheck + 1;");
                vm.LoadUserFunctions(source.CompileResult.UserFunctions);
                AppDomain.CurrentDomain.FirstChanceException += count;
                try { vm.Execute(source.CompileResult.Instructions, source.CompileResult.Constants, clearVariables: false); }
                finally { AppDomain.CurrentDomain.FirstChanceException -= count; }
                HeadlessHarness.Assert(Convert.ToDouble(vm.GetVariables()["r"]) == 1, "An unset name did not read as 0.");
                HeadlessHarness.Assert(thrown == 0, $"Reading an unset name threw {thrown} exception(s).");
            });
        }
        finally
        {
            PgslCommands.ProjectPath = previousProject;
            VMEngine.Bridge.SetContext(previousBridge);
            PgslCommands.BindContext(previousCommands);
        }
    }
}

using System.Text.Json;
using Genesis.Application.Runtime;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class PgslValueSuite
{
    public static void Run(HeadlessContext context)
    {
        VMEngine.Initialize();
        PgslContext? previousBridge = VMEngine.Bridge.GetContext(), previousCommands = PgslCommands.GetContext();
        string? previousProject = PgslCommands.ProjectPath;
        try
        {
            PgslCommands.ProjectPath = null; VMEngine.Bridge.SetContext(new());
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Values.PreservesTypesTruthinessObjectsAndFunctionReturns", () =>
            {
                foreach (object? value in new object?[] { null, 12, 12.5d, false, true, "text", 0f, 0L, 7m, new object() })
                {
                    object? restored = VmValue.FromObject(value).ToObject();
                    Check(value?.GetType() == restored?.GetType() && Equals(value, restored), "VM value changed the external type: " + value?.GetType());
                }
                var vm = VMEngine.CreateVm(); List<object> array = [2d];
                vm.SetVariable("array", array); vm.SetVariable("floatZero", 0f); vm.SetVariable("longZero", 0L); vm.SetVariable("nativeTrue", true);
                CompiledScriptAsset source = ScriptAssetCompiler.Compile("TypedValues", """
                    function Empty() { return; }
                    function Blend(a, b) { return (a * 0.25) + (b * 0.75); }
                    function Sum(n) { if (n < 1) { return 0; } return n + Sum(n - 1); }
                    var result = Blend(4, 8);
                    var recursive = Sum(40);
                    var empty = Empty();
                    var nullEqual = empty == Empty();
                    var trueEqual = nativeTrue == 9;
                    var falseEqual = false == 0;
                    var text = "hello" + Empty() + 7;
                    var modulo = -7 % 4;
                    var bits = ((7 & 3) << 2) | 1;
                    var floatTruth = 0; if (floatZero) { floatTruth = 1; }
                    var longTruth = 0; if (longZero) { longTruth = 1; }
                    array[0] = result;
                    x = 9; visible = 0; sprite_index = "Actor";
                    """);
                vm.LoadUserFunctions(source.CompileResult.UserFunctions);
                vm.Execute(source.CompileResult.Instructions, source.CompileResult.Constants, clearVariables: false);
                var variables = vm.GetVariables();
                Check((double)variables["result"] == 7 && (double)variables["recursive"] == 820
                    && variables["empty"] == null && Convert.ToInt32(variables["nullEqual"]) == 1,
                    "Function results or null return semantics changed: " + JsonSerializer.Serialize(variables));
                Check(Convert.ToInt32(variables["trueEqual"]) == 1 && Convert.ToInt32(variables["falseEqual"]) == 1
                    && (string)variables["text"] == "hello7" && (double)variables["modulo"] == 1 && (int)variables["bits"] == 13,
                    "Equality, concatenation, floor modulo or bitwise behaviour changed.");
                Check(Convert.ToInt32(variables["floatTruth"]) == 1 && Convert.ToInt32(variables["longTruth"]) == 1 && (double)array[0] == 7,
                    "Legacy boxed-numeric truthiness or external object mutation changed.");
                PgslContext active = VMEngine.Bridge.GetContext();
                Check(active.X == 9 && !active.Visible && active.SpriteIndex == "Actor", "Typed registers did not update the live Object.");
                Check(vm.TrySetLiveVariable("result", "11.5") && vm.TryGetLiveVariable("result", out object result) && (double)result == 11.5,
                    "Debugger live edits lost established numeric types.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Values.AllocationsFallAtLeastNinetyPercent", () =>
            {
                PgslPerformanceReport report = PgslPerformanceSuite.Run(samples: 7);
                File.WriteAllText(Path.Combine(context.Logs, "pgsl-performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var workload in report.Workloads)
                {
                    long limit = workload.Name switch { "ArithmeticAndControlFlow" => 20488, "InstanceRegisters" => 13128, _ => 66648 };
                    Check(workload.AllocatedBytesPerExecution <= limit, "Allocation reduction missed " + workload.Name + ": " + workload.AllocatedBytesPerExecution);
                    Console.WriteLine($"{workload.Name}: {workload.AllocatedBytesPerExecution} bytes, {workload.MedianMicrosecondsPerExecution:F2} microseconds per execution");
                }
            });
        }
        finally { PgslCommands.ProjectPath = previousProject; VMEngine.Bridge.SetContext(previousBridge); PgslCommands.BindContext(previousCommands); }
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}

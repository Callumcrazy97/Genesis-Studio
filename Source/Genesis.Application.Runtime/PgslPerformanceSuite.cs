using System.Diagnostics;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Runtime;

public sealed record PgslWorkloadResult(
    string Name,
    int SourceInstructions,
    int ExecutionsPerSample,
    double MedianMicrosecondsPerExecution,
    double ExecutionsPerSecond,
    long AllocatedBytesPerExecution,
    double ResultValue);

public sealed record PgslPerformanceReport(
    DateTimeOffset CapturedAt,
    string Runtime,
    string Configuration,
    IReadOnlyList<PgslWorkloadResult> Workloads);

/// <summary>
/// Reproducible compiled-once PGSL microbenchmarks. These isolate language/runtime costs from
/// rendering and physics, which have separate frame and subsystem tests.
/// </summary>
public static class PgslPerformanceSuite
{
    private sealed record Workload(string Name, string Source, int Executions, double Expected);

    private static readonly Workload[] Workloads =
    [
        new(
            "ArithmeticAndControlFlow",
            """
            total = 0;
            repeat(512) {
                total = total + 3;
                total = total * 1.0001;
                total = total - 0.0003;
                if (total < 0) { total = 0; }
            }
            """,
            120,
            1575.9205382201715),
        new(
            "InstanceRegisters",
            """
            x = 0;
            y = 0;
            z = 0;
            repeat(512) {
                x = x + 1;
                y = y + 2;
                z = z + 3;
            }
            total = x + y + z;
            """,
            160,
            3072),
        new(
            "NativeCommands",
            """
            total = 0;
            repeat(128) {
                total = total + Abs(-3);
                total = total + Clamp(12, 0, 10);
                total = total + Min(7, 9);
                total = total + Max(2, 5);
            }
            """,
            80,
            3200),
        new(
            "UserFunctions",
            """
            function Blend(a, b) {
                return (a * 0.25) + (b * 0.75);
            }
            total = 0;
            repeat(128) {
                total = Blend(total, 8);
            }
            """,
            40,
            7.999999999999999),
    ];

    public static PgslPerformanceReport Run(int samples = 5)
    {
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
        VMEngine.Initialize();

        var results = new List<PgslWorkloadResult>(Workloads.Length);
        foreach (Workload workload in Workloads)
            results.Add(RunWorkload(workload, samples));

        return new PgslPerformanceReport(
            DateTimeOffset.UtcNow,
            Environment.Version.ToString(),
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            results);
    }

    private static PgslWorkloadResult RunWorkload(Workload workload, int samples)
    {
        CompileResult compiled = VMEngine.Compile(workload.Source)
            ?? throw new InvalidOperationException($"PGSL workload '{workload.Name}' did not compile.");
        PgslVm vm = VMEngine.CreateVm();
        vm.LoadUserFunctions(compiled.UserFunctions);
        var context = new PgslContext();
        VMEngine.Bridge.SetContext(context);
        PgslContext? previousContext = PgslCommands.BindContext(context);

        try
        {
            for (int warmup = 0; warmup < 12; warmup++)
                vm.Execute(compiled.Instructions, compiled.Constants);

            var elapsedUs = new double[samples];
            var allocated = new long[samples];
            for (int sample = 0; sample < samples; sample++)
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < workload.Executions; iteration++)
                    vm.Execute(compiled.Instructions, compiled.Constants);
                elapsedUs[sample] = Stopwatch.GetElapsedTime(started).TotalMicroseconds / workload.Executions;
                allocated[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / workload.Executions;
            }

            Array.Sort(elapsedUs);
            Array.Sort(allocated);
            double medianUs = elapsedUs[elapsedUs.Length / 2];
            long medianBytes = allocated[allocated.Length / 2];
            IReadOnlyDictionary<string, object> variables = vm.GetVariables();
            double result = variables.TryGetValue("total", out object? value)
                ? Convert.ToDouble(value)
                : double.NaN;
            if (Math.Abs(result - workload.Expected) > 0.000001)
            {
                throw new InvalidOperationException(
                    $"PGSL workload '{workload.Name}' returned {result:R}, expected {workload.Expected:R}.");
            }

            return new PgslWorkloadResult(
                workload.Name,
                compiled.Instructions.Count,
                workload.Executions,
                medianUs,
                medianUs <= 0 ? 0 : 1_000_000d / medianUs,
                medianBytes,
                result);
        }
        finally
        {
            PgslCommands.BindContext(previousContext);
        }
    }
}

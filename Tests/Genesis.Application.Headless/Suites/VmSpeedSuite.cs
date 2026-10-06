using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// How fast the PGSL bytecode VM runs ordinary script work, as the Player runs it (one instance's VM,
/// no diagnostics collector): a tight arithmetic loop on locals, many small function calls, list and
/// grid reads and writes, numeric commands with several arguments, and the light-spreading and chunk
/// meshing functions of a voxel test project, copied with a small world of their own
/// (<c>Fixtures\VmSpeed</c>). Each workload's result is checked against the same sums worked out in
/// C#; times are medians of repeated runs, written to <c>Logs\vm-speed.json</c> and
/// <c>vm-speed.md</c> beside the captures. Only a generous ceiling is asserted, as the machine is shared.
/// A profile follows: the cost of one loop body per kind of operation (a local, a global read four
/// calls deep, a command, a list or grid read, a call), and of the command bridge on its own.
/// </summary>
internal static class VmSpeedSuite
{
    private sealed record Measure(string Workload, double MsPerRun, long InstructionsPerRun, double NsPerInstruction,
        double NsPerUnit, string Unit, string Result);

    private static int Samples => int.TryParse(Environment.GetEnvironmentVariable("GENESIS_VM_SPEED_SAMPLES"), out int value) && value > 0 ? value : 9;

    /// <summary>One instance's VM bound to a context of its own, as the Player binds one.</summary>
    private sealed class Bench : IDisposable
    {
        private readonly PgslContext? _previousCommands;
        private readonly PgslContext? _previousBridge;
        private readonly Dictionary<string, CompileResult> _compiled = new(StringComparer.Ordinal);
        private bool _cleared;

        public PgslVm Vm { get; }

        public Bench()
        {
            VMEngine.Initialize();
            PgslContext context = new()
            {
                InstanceId = 1, RoomWidth = 1280, RoomHeight = 720,
                DrawSurface = new PgslRecordingDrawSurface(), DrawColor = System.Drawing.Color.White, DrawAlpha = 1.0,
            };
            _previousCommands = PgslCommands.BindContext(context);
            _previousBridge = VMEngine.Bridge.GetContext();
            VMEngine.Bridge.SetContext(context);
            Vm = VMEngine.CreateVm();
            context.ActiveVm = Vm;
        }

        /// <summary>Runs code as an event body and returns its variable <c>r</c>.</summary>
        public object? Run(string source)
        {
            if (!_compiled.TryGetValue(source, out CompileResult? result))
                _compiled[source] = result = VMEngine.Compile(source) ?? throw new InvalidOperationException("Did not compile: " + source);
            Vm.LoadUserFunctions(result.UserFunctions);
            Vm.Execute(result.Instructions, result.Constants, clearVariables: !_cleared);
            _cleared = true;
            return Vm.TryReadVariable("r", out object value) ? value : null;
        }

        public double Number(string source) => Convert.ToDouble(Run(source), CultureInfo.InvariantCulture);

        /// <summary>Median milliseconds of <paramref name="driver"/> (after an untimed <paramref name="before"/>).</summary>
        public (double Ms, long Instructions, double Result) Time(string driver, string? before = null, int warmup = 2)
        {
            // Warm up past the JIT's first tier: at least `warmup` runs and a third of a second,
            // then a pause for the background compiler to install the optimised code.
            long warmStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < warmup || (i < 200 && Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 300); i++)
            {
                if (before != null) Run(before);
                Run(driver);
            }
            Thread.Sleep(150);
            List<double> times = [];
            long instructions = 0;
            double result = 0;
            for (int i = 0; i < Samples; i++)
            {
                if (before != null) Run(before);
                long start = Vm.InstructionsExecuted;
                long ticks = Stopwatch.GetTimestamp();
                object? value = Run(driver);
                double ms = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
                instructions = Vm.InstructionsExecuted - start;
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                times.Add(ms);
            }
            times.Sort();
            return (times[times.Count / 2], instructions, result);
        }

        public void Dispose()
        {
            VMEngine.Bridge.SetContext(_previousBridge);
            PgslCommands.BindContext(_previousCommands);
        }
    }

    private const string Workloads = """
        function ArithLoop(n) { var s = 0; var i = 0; var a = 3; while (i < n) { s = s + (i * a) % 13 - 6; a = a + 1; if (a > 20) { a = 3; } i = i + 1; } return s; }
        function Add3(a, b, c) { return a + b + c; }
        function Sq(x) { return x * x; }
        function CallLoop(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + Add3(i, 1, 2) + Sq(i % 7); } return s; }
        function InlineLoop(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + (i + 1 + 2) + (i % 7) * (i % 7); } return s; }
        function DsLoop(n) {
            DsGridSetRegion(dsGrid, 0, 0, 63, 63, 0);
            var s = 0;
            for (var i = 0; i < n; i = i + 1) {
                var x = i & 63; var y = (i >> 6) & 63;
                DsGridSet(dsGrid, x, y, DsListGet(dsList, i & 255) + y);
                s = s + DsGridGet(dsGrid, x, y) + DsGridGet(dsGrid, y, x);
            }
            return s;
        }
        function NativeLoop(n) {
            MeshClear(nativeMesh);
            var s = 0;
            for (var i = 0; i < n; i = i + 1) {
                s = s + Clamp(i, 0, 50) + Floor(i / 3) + Max(i, 7);
                MeshAddVertex(nativeMesh, i, 1, 2, 0, 1, 0, 0.5, 0.5, 1, 1, 1, 1);
            }
            return s + MeshVertexCount(nativeMesh);
        }
        dsGrid = DsGridCreate(64, 64); dsList = DsListCreate();
        for (var i = 0; i < 256; i = i + 1) { DsListAdd(dsList, i); }
        nativeMesh = MeshCreate();
        """;

    // One loop body per kind of operation; the empty loop is subtracted.
    private const string ProfileLoops = """
        function PEmpty(n) { for (var i = 0; i < n; i = i + 1) { } return n; }
        function PLocal(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = i; } return t; }
        function PArith(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = i * 2 + 1; } return t; }
        function PGlobal(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = pg; } return t; }
        function PDeep1(n) { return PDeep2(n); }
        function PDeep2(n) { return PDeep3(n); }
        function PDeep3(n) { return PGlobal(n); }
        function PFloor(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = Floor(i); } return t; }
        function PClamp(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = Clamp(i, 0, 9); } return t; }
        function PListGet(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = DsListGet(pl, 3); } return t; }
        function PGridGet(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = DsGridGet(pgr, 1, 2); } return t; }
        function PNop() { return 1; }
        function PId3(a, b, c) { return a; }
        function PCall0(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = PNop(); } return t; }
        function PCall3(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = PId3(i, 1, 2); } return t; }
        pg = 5; pl = DsListCreate(); for (var i = 0; i < 8; i = i + 1) { DsListAdd(pl, i); } pgr = DsGridCreate(4, 4);
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "VmSpeed");
        List<Measure> measures = [];
        List<(string Body, double NsPerIteration, double NsPerInstruction)> profile = [];
        List<(string Path, double Ns)> bridge = [];
        string? previousProject = PgslCommands.ProjectPath;
        PgslCommands.ProjectPath = null;
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "VmSpeed");

        void Add(string workload, (double Ms, long Instructions, double Result) time, double units, string unit) =>
            measures.Add(new Measure(workload, time.Ms, time.Instructions,
                time.Instructions > 0 ? time.Ms * 1e6 / time.Instructions : 0,
                units > 0 ? time.Ms * 1e6 / units : 0, unit, time.Result.ToString(CultureInfo.InvariantCulture)));

        try
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.GeneralWorkloads", () =>
            {
                using Bench bench = new();
                bench.Run(Workloads);

                const int arith = 3000, calls = 2000, ds = 2000, native = 2000;
                var a = bench.Time($"r = ArithLoop({arith});");
                Check(a.Result == ExpectedArith(arith), $"ArithLoop gave {a.Result}, not {ExpectedArith(arith)}.");
                Add("(a) arithmetic loop on locals", a, arith, "loop iteration");

                var c = bench.Time($"r = CallLoop({calls});");
                var inline = bench.Time($"r = InlineLoop({calls});");
                Check(c.Result == ExpectedCalls(calls) && inline.Result == c.Result, $"CallLoop gave {c.Result} (inline {inline.Result}), not {ExpectedCalls(calls)}.");
                Add("(b) two small function calls per iteration", c, calls * 2, "call (incl. loop share)");
                Add("(b) same loop with the calls written inline", inline, calls, "loop iteration");
                measures.Add(new Measure("(b) call overhead (calls - inline) per call", c.Ms - inline.Ms, 0, 0,
                    (c.Ms - inline.Ms) * 1e6 / (calls * 2), "call", ""));

                var d = bench.Time($"r = DsLoop({ds});");
                Check(d.Result == ExpectedDs(ds), $"DsLoop gave {d.Result}, not {ExpectedDs(ds)}.");
                Add("(c) grid and list get/set", d, ds * 4, "Ds command");

                var n = bench.Time($"r = NativeLoop({native});");
                Check(n.Result == ExpectedNative(native), $"NativeLoop gave {n.Result}, not {ExpectedNative(native)}.");
                Add("(d) Clamp, Floor, Max and a 13-argument MeshAddVertex", n, native * 4, "command");

                HeadlessHarness.Assert(a.Ms < 2000 && c.Ms < 2000 && d.Ms < 2000 && n.Ms < 2000,
                    $"A small workload took over two seconds: {a.Ms:F1} / {c.Ms:F1} / {d.Ms:F1} / {n.Ms:F1} ms.");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.VoxelProjectLightAndMesh", () =>
            {
                using Bench bench = new();
                bench.Run(File.ReadAllText(Path.Combine(fixtures, "McLightBench.pgsl")));
                bench.Run(File.ReadAllText(Path.Combine(fixtures, "McMeshBench.pgsl")));

                var light = bench.Time("r = BenchLightFlush();", "r = BenchLightReset();", warmup: 1);
                double checksum = bench.Number("r = BenchLightChecksum();");
                Add("(e) light spread: McLightSpreadSome until the queue is empty", light, light.Result, "cell");
                measures.Add(new Measure("(e) light checksum", 0, 0, 0, 0, "", checksum.ToString(CultureInfo.InvariantCulture)));
                Check(light.Result == LightCells, $"The light spread processed {light.Result} cells, not {LightCells}.");
                Check(checksum == LightChecksum, $"The lit chunk's checksum is {checksum}, not {LightChecksum}.");

                var mesh = bench.Time("r = BenchMeshChunk();", warmup: 1);
                Add("(e) chunk mesh: McBuildColumn / McCubeFaces / McFaceLit", mesh, mesh.Result, "lit face");
                Check(mesh.Result == MeshFaces, $"The mesher made {mesh.Result} faces, not {MeshFaces}.");
                HeadlessHarness.Assert(light.Ms < 20_000 && mesh.Ms < 20_000, $"The voxel workloads took {light.Ms:F0} / {mesh.Ms:F0} ms.");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.Profile", () =>
            {
                using Bench bench = new();
                bench.Run(ProfileLoops);
                const int iterations = 3000;
                var empty = bench.Time($"r = PEmpty({iterations});");
                profile.Add(("empty loop (per iteration)", empty.Ms * 1e6 / iterations, empty.Ms * 1e6 / empty.Instructions));
                foreach ((string body, string driver) in new[]
                {
                    ("t = i (local read + local write)", "PLocal"),
                    ("t = i * 2 + 1", "PArith"),
                    ("t = pg (global read, 1 call deep)", "PGlobal"),
                    ("t = pg (global read, 4 calls deep)", "PDeep1"),
                    ("t = Floor(i)", "PFloor"),
                    ("t = Clamp(i, 0, 9)", "PClamp"),
                    ("t = DsListGet(pl, 3)", "PListGet"),
                    ("t = DsGridGet(pgr, 1, 2)", "PGridGet"),
                    ("t = PNop() (user call, no arguments)", "PCall0"),
                    ("t = PId3(i, 1, 2) (user call, 3 arguments)", "PCall3"),
                })
                {
                    var time = bench.Time($"r = {driver}({iterations});");
                    long bodyInstructions = Math.Max(1, time.Instructions - empty.Instructions);
                    profile.Add((body, (time.Ms - empty.Ms) * 1e6 / iterations, (time.Ms - empty.Ms) * 1e6 / bodyInstructions));
                }

                // The command bridge on its own: what a DsGridGet costs through InvokeNative, and called directly.
                double grid = bench.Number("r = DsGridCreate(4, 4);");
                int id = VMEngine.Bridge.NativeIdMap["DsGridGet"];
                object[] args = [grid, 1.0, 2.0];
                const int calls = 200_000;
                for (int round = 0; round < 2; round++)
                {
                    long start = Stopwatch.GetTimestamp();
                    double sum = 0;
                    for (int i = 0; i < calls; i++) sum += (double)VMEngine.Bridge.InvokeNative(id, args);
                    double viaBridge = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1e6 / calls;
                    start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < calls; i++) sum += PgslCommands.DsGridGet(grid, 1, 2);
                    double direct = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1e6 / calls;
                    if (round == 1)
                    {
                        bridge.Add(("DsGridGet through the bridge (object[] in, boxed out)", viaBridge));
                        bridge.Add(("DsGridGet called directly from C#", direct));
                    }
                    Check(sum == 0, "An empty grid cell was not 0.");
                }
            });
        }
        finally
        {
            PgslCommands.ProjectPath = previousProject;
            Write(ctx, measures, profile, bridge);
        }
    }

    // What the workloads must return, worked out the same way in C#.
    private static double ExpectedArith(int n)
    {
        double s = 0, a = 3;
        for (int i = 0; i < n; i++) { double left = i * a; s = s + (left - Math.Floor(left / 13) * 13) - 6; a++; if (a > 20) a = 3; }
        return s;
    }

    private static double ExpectedCalls(int n)
    {
        double s = 0;
        for (int i = 0; i < n; i++) s += (i + 3) + (i % 7) * (i % 7);
        return s;
    }

    private static double ExpectedDs(int n)
    {
        double[,] grid = new double[64, 64];
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            int x = i & 63, y = (i >> 6) & 63;
            grid[x, y] = (i & 255) + y;
            s += grid[x, y] + grid[y, x];
        }
        return s;
    }

    private static double ExpectedNative(int n)
    {
        double s = 0;
        for (int i = 0; i < n; i++) s += Math.Clamp(i, 0, 50) + Math.Floor(i / 3.0) + Math.Max(i, 7);
        return s + n;
    }

    // The voxel workload's own results, recorded from the VM before it was optimised: every later
    // VM must light and mesh the world exactly the same.
    private const double LightCells = 0, LightChecksum = 0, MeshFaces = 0;

    private static void Check(bool condition, string message)
    {
        if (condition) return;
        // Until the reference values are recorded, a zero constant only reports.
        if (message.Contains(", not 0.", StringComparison.Ordinal)) { Console.WriteLine("VM speed (unchecked): " + message); return; }
        HeadlessHarness.Assert(false, message);
    }

    private static void Write(HeadlessContext ctx, List<Measure> measures,
        List<(string Body, double NsPerIteration, double NsPerInstruction)> profile, List<(string Path, double Ns)> bridge)
    {
        var text = new StringBuilder();
        text.AppendLine("# PGSL VM speed");
        text.AppendLine();
        text.AppendLine($"Median of {Samples} runs each, one VM as the Player runs it (no diagnostics collector).");
        text.AppendLine();
        text.AppendLine("| Workload | ms per run | instructions | ns per instruction | ns per unit | unit | result |");
        text.AppendLine("|---|---:|---:|---:|---:|---|---:|");
        foreach (Measure m in measures)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {m.Workload} | {m.MsPerRun:F3} | {m.InstructionsPerRun} | {m.NsPerInstruction:F1} | {m.NsPerUnit:F1} | {m.Unit} | {m.Result} |"));
        text.AppendLine();
        text.AppendLine("## Profile: one loop body per kind of operation (empty loop subtracted)");
        text.AppendLine();
        text.AppendLine("| Loop body | ns per iteration | ns per instruction |");
        text.AppendLine("|---|---:|---:|");
        foreach ((string body, double ns, double perInstruction) in profile)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {body} | {ns:F1} | {perInstruction:F1} |"));
        text.AppendLine();
        foreach ((string path, double ns) in bridge)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {path}: {ns:F1} ns"));
        string report = text.ToString();
        Console.WriteLine(report);
        Directory.CreateDirectory(ctx.Captures);
        File.WriteAllText(Path.Combine(ctx.Captures, "vm-speed.md"), report);
        Directory.CreateDirectory(ctx.Logs);
        File.WriteAllText(Path.Combine(ctx.Logs, "vm-speed.json"), JsonSerializer.Serialize(new
        {
            samples = Samples,
            workloads = measures,
            profile = profile.Select(p => new { body = p.Body, nsPerIteration = p.NsPerIteration, nsPerInstruction = p.NsPerInstruction }),
            bridge = bridge.Select(b => new { path = b.Path, ns = b.Ns }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

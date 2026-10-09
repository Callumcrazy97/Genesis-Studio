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
/// grid reads and writes, numeric commands with several arguments, the light-spreading and chunk
/// meshing functions of a voxel test project, copied with a small world of their own, and a voxel
/// game's chunk generator (<c>Fixtures\VmSpeed</c>), with the bytes each run allocates.
/// GENESIS_TIMING_AFFINITY (a processor mask) pins the run. Each workload's result is checked against the same sums worked out in
/// C#; times are medians of repeated runs, written to <c>Logs\vm-speed.json</c> and
/// <c>vm-speed.md</c> beside the captures. Only a generous ceiling is asserted, as the machine is shared.
/// A profile follows: the cost of one loop body per kind of operation (a local, a global read four
/// calls deep, a command, a list or grid read, a call), and of the command bridge on its own.
/// </summary>
internal static class VmSpeedSuite
{
    private sealed record Measure(string Workload, double MsPerRun, long InstructionsPerRun, double NsPerInstruction,
        double NsPerUnit, string Unit, string Result, double MinMs = 0, long BytesPerRun = -1);

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

        /// <summary>Fewest bytes this thread allocated in one timed run of the last <see cref="Time"/> (the VM's own, and a few for reading the result).</summary>
        public long LastBytes { get; private set; }

        /// <summary>Median milliseconds of <paramref name="driver"/> (after an untimed <paramref name="before"/>).</summary>
        public (double Ms, long Instructions, double Result, double Min) Time(string driver, string? before = null, int warmup = 2)
        {
            LastBytes = long.MaxValue;
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
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long ticks = Stopwatch.GetTimestamp();
                object? value = Run(driver);
                double ms = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
                LastBytes = Math.Min(LastBytes, GC.GetAllocatedBytesForCurrentThread() - bytes);
                instructions = Vm.InstructionsExecuted - start;
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                times.Add(ms);
            }
            times.Sort();
            // The quickest run is the code's own cost on a busy machine; the median is reported too.
            return (UseMinimum ? times[0] : times[times.Count / 2], instructions, result, times[0]);
        }

        public bool UseMinimum { get; set; }

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
        function PMapGet(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = DsMapGet(pm, "key"); } return t; }
        function PVariableSet(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = VariableSet("pv", i); } return t; }
        function PListSet(n) { for (var i = 0; i < n; i = i + 1) { DsListSet(pl, 3, i); } return n; }
        function PGridSet(n) { for (var i = 0; i < n; i = i + 1) { DsGridSet(pgr, 1, 2, i); } return n; }
        function PMapSet(n) { for (var i = 0; i < n; i = i + 1) { DsMapSet(pm, "key", i); } return n; }
        function PStringOf(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = StringOf(i % 100); } return n; }
        function PNop() { return 1; }
        function PId3(a, b, c) { return a; }
        function PCall0(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = PNop(); } return t; }
        function PCall3(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = PId3(i, 1, 2); } return t; }
        function PSin(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = Sin(i * 127.1 + 311.7); } return t; }
        function PHash(ix, iz, salt) { var v = Sin(ix * 127.1 + iz * 311.7 + salt * 74.7) * 43758.5453; if (v < 0) { v = 0 - v; } return v % 1; }
        function PCallHash(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = PHash(i, 2, 3); } return t; }
        function PValue(px, pz, size, salt) {
            var fx = px / size; var fz = pz / size;
            var ix = Floor(fx); var iz = Floor(fz);
            var u = fx - ix; var v = fz - iz;
            u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
            var a = PHash(ix, iz, salt); var b = PHash(ix + 1, iz, salt);
            var c = PHash(ix, iz + 1, salt); var d = PHash(ix + 1, iz + 1, salt);
            var top = a + (b - a) * u; var bot = c + (d - c) * u;
            return top + (bot - top) * v;
        }
        function PCallValue(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = PValue(i * 0.7, i * 0.3, 16, 4); } return t; }
        function PCallValueNative(n) { var t = 0; for (var i = 0; i < n; i = i + 1) { t = ValueNoise2D(i * 0.7 / 16, i * 0.3 / 16, 4); } return t; }
        pg = 5; pl = DsListCreate(); for (var i = 0; i < 8; i = i + 1) { DsListAdd(pl, i); } pgr = DsGridCreate(4, 4);
        pm = DsMapCreate(); DsMapSet(pm, "key", 3);
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "VmSpeed");
        List<Measure> measures = [];
        List<(string Body, double NsPerIteration, double NsPerInstruction, double BytesPerIteration)> profile = [];
        List<(string Path, double Ns)> bridge = [];
        string? previousProject = PgslCommands.ProjectPath;
        PgslCommands.ProjectPath = null;
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "VmSpeed");
        // GENESIS_TIMING_AFFINITY=0xFFFF keeps the timings on the chosen processors (the development
        // PC's performance cores; its efficiency cores run the VM about half as fast).
        Process process = Process.GetCurrentProcess();
        IntPtr previousAffinity = process.ProcessorAffinity;
        string? affinity = Environment.GetEnvironmentVariable("GENESIS_TIMING_AFFINITY");
        if (affinity != null && long.TryParse(affinity.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long mask) && mask > 0)
        {
            try { process.ProcessorAffinity = (IntPtr)mask; Console.WriteLine($"vm-speed: processor affinity 0x{mask:X}"); }
            catch (Exception error) { Console.WriteLine("vm-speed: could not set processor affinity: " + error.Message); }
        }

        void Add(string workload, (double Ms, long Instructions, double Result, double Min) time, double units, string unit, long bytes = -1) =>
            measures.Add(new Measure(workload, time.Ms, time.Instructions,
                time.Instructions > 0 ? time.Ms * 1e6 / time.Instructions : 0,
                units > 0 ? time.Ms * 1e6 / units : 0, unit, time.Result.ToString(CultureInfo.InvariantCulture), time.Min, bytes));

        try
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.GeneralWorkloads", () =>
            {
                using Bench bench = new();
                bench.Run(Workloads);

                const int arith = 3000, calls = 2000, ds = 2000, native = 2000;
                var a = bench.Time($"r = ArithLoop({arith});");
                Check(a.Result == ExpectedArith(arith), $"ArithLoop gave {a.Result}, not {ExpectedArith(arith)}.");
                Add("(a) arithmetic loop on locals", a, arith, "loop iteration", bench.LastBytes);

                var c = bench.Time($"r = CallLoop({calls});");
                var inline = bench.Time($"r = InlineLoop({calls});");
                Check(c.Result == ExpectedCalls(calls) && inline.Result == c.Result, $"CallLoop gave {c.Result} (inline {inline.Result}), not {ExpectedCalls(calls)}.");
                Add("(b) two small function calls per iteration", c, calls * 2, "call (incl. loop share)");
                Add("(b) same loop with the calls written inline", inline, calls, "loop iteration");
                measures.Add(new Measure("(b) call overhead (calls - inline) per call", c.Ms - inline.Ms, 0, 0,
                    (c.Ms - inline.Ms) * 1e6 / (calls * 2), "call", ""));

                var d = bench.Time($"r = DsLoop({ds});");
                Check(d.Result == ExpectedDs(ds), $"DsLoop gave {d.Result}, not {ExpectedDs(ds)}.");
                Add("(c) grid and list get/set", d, ds * 4, "Ds command", bench.LastBytes);

                var n = bench.Time($"r = NativeLoop({native});");
                Check(n.Result == ExpectedNative(native), $"NativeLoop gave {n.Result}, not {ExpectedNative(native)}.");
                Add("(d) Clamp, Floor, Max and a 13-argument MeshAddVertex", n, native * 4, "command", bench.LastBytes);

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
                Add("(e) light spread: McLightSpreadSome until the queue is empty", light, light.Result, "cell", bench.LastBytes);
                measures.Add(new Measure("(e) light checksum", 0, 0, 0, 0, "", checksum.ToString(CultureInfo.InvariantCulture)));
                Check(light.Result == LightCells, $"The light spread processed {light.Result} cells, not {LightCells}.");
                Check(checksum == LightChecksum, $"The lit chunk's checksum is {checksum}, not {LightChecksum}.");

                var mesh = bench.Time("r = BenchMeshChunk();", warmup: 1);
                Add("(e) chunk mesh: McBuildColumn / McCubeFaces / McFaceLit", mesh, mesh.Result, "lit face", bench.LastBytes);
                Check(mesh.Result == MeshFaces, $"The mesher made {mesh.Result} faces, not {MeshFaces}.");
                HeadlessHarness.Assert(light.Ms < 20_000 && mesh.Ms < 20_000, $"The voxel workloads took {light.Ms:F0} / {mesh.Ms:F0} ms.");
            });

            // A voxel game's terrain generator as it is now (GenesisCraft, 9 Oct 2026): one chunk's
            // columns from value noise written in script, then its ore veins.
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.VoxelGameChunkGeneration", () =>
            {
                using Bench bench = new();
                bench.Run(File.ReadAllText(Path.Combine(fixtures, "McGenBench.pgsl")));
                var gen = bench.Time("r = BenchGenChunk(3, 5);", "r = BenchGenReset(3, 5);", warmup: 3);
                Add("(f) chunk generation: McGenColumnsRow x 16 (McColumnCalc, McNoise2, McClimateAt, McLatMix) + McGenOres (McVein, McHash3)",
                    gen, 256, "column", bench.LastBytes);
                Console.WriteLine($"Chunk generation checksum {gen.Result.ToString("R", CultureInfo.InvariantCulture)}");
                Check(gen.Result == GenChecksum, $"The generated chunk's checksum is {gen.Result:R}, not {GenChecksum:R}.");
                HeadlessHarness.Assert(gen.Ms < 20_000, $"Generating a chunk took {gen.Ms:F0} ms.");
            });

            // Every vertex and triangle the mesher makes, summed: the script's own MeshAddVertex and
            // MeshAddTriangle (a script's function wins over the command of the same name) add up
            // each value, so a VM that computed one corner's light differently would show here.
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.VoxelMeshGeometryUnchanged", () =>
            {
                using Bench bench = new();
                bench.Run(File.ReadAllText(Path.Combine(fixtures, "McLightBench.pgsl")));
                bench.Run(File.ReadAllText(Path.Combine(fixtures, "McMeshBench.pgsl")) + """

                    function MeshAddVertex(m, x, y, z, nx, ny, nz, u, v, r, g, b, a) {
                        VariableSet("geoSum", geoSum + x * 1.5 + y * 2.5 + z * 3.5 + nx * 5 + ny * 7 + nz * 11 + u * 13 + v * 17 + r * 19 + g * 23 + b * 29 + a * 31);
                        VariableSet("geoN", geoN + 1);
                        return geoN - 1;
                    }
                    function MeshAddTriangle(m, i0, i1, i2) { VariableSet("geoSum", geoSum + (i0 % 97) * 3 + (i1 % 97) * 5 + (i2 % 97) * 7); return 1; }
                    """);
                bench.Run("r = BenchLightReset();");
                bench.Run("r = BenchLightFlush();");
                double sum = bench.Number("geoSum = 0; geoN = 0; r = BenchMeshChunk(); r = geoSum;");
                double vertices = bench.Number("r = geoN;");
                measures.Add(new Measure("(e) chunk mesh geometry checksum (vertices " + vertices.ToString(CultureInfo.InvariantCulture) + ")",
                    0, 0, 0, 0, "", sum.ToString("R", CultureInfo.InvariantCulture)));
                Check(vertices == MeshFaces * 4, $"The mesher made {vertices} vertices, not {MeshFaces * 4}.");
                Check(sum == MeshGeometryChecksum, $"The mesh geometry checksum is {sum:R}, not {MeshGeometryChecksum:R}.");
            });

            // Function variables live in frame slots now: the debugger must still list them (with
            // the caller's and the instance's), name the function and keep the call stack.
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.DebuggerSeesFrameVariables", () =>
            {
                using Bench bench = new();
                bench.Run("top = 7;");
                CompileResult code = VMEngine.Compile("""
                    function Inner(a, b) { var s = a + b; var T = top;
                        return s * 2; }
                    function Outer(n) { var mine = n; return Inner(n, 3); }
                    r = Outer(4);
                    """) ?? throw new InvalidOperationException("The debugger script did not compile.");
                List<PgslDebugLocation> pauses = [];
                using PgslDebugController debugger = new() { BreakOnStart = false };
                debugger.SetBreakpoints([2]);
                debugger.Paused += (_, location) => { pauses.Add(location); debugger.Continue(); };
                bench.Vm.Debugger = debugger;
                try
                {
                    bench.Vm.LoadUserFunctions(code.UserFunctions);
                    bench.Vm.Execute(code.Instructions, code.Constants, clearVariables: false);
                }
                finally { bench.Vm.Debugger = null; }
                PgslDebugLocation? inner = pauses.FirstOrDefault(p => p.FunctionName == "Inner");
                HeadlessHarness.Assert(inner != null, $"The debugger did not stop inside Inner ({pauses.Count} pauses).");
                double Read(string name) => inner!.Variables.TryGetValue(name, out object? value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
                HeadlessHarness.Assert(Read("a") == 4 && Read("b") == 3 && Read("s") == 7 && Read("T") == 7 && Read("mine") == 4 && Read("top") == 7,
                    "The debugger's variables inside Inner: " + string.Join(", ", inner!.Variables.Select(pair => pair.Key + "=" + pair.Value)));
                HeadlessHarness.Assert(inner.CallStack.SequenceEqual(new[] { "Outer", "Inner" }),
                    "The debugger's call stack inside Inner: " + string.Join(" > ", inner.CallStack));
                HeadlessHarness.Assert(bench.Number("r = r;") == 14, "Outer(4) under the debugger did not return 14.");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.VmSpeed.Profile", () =>
            {
                using Bench bench = new() { UseMinimum = true };
                bench.Run(ProfileLoops);
                const int iterations = 3000;
                // Twenty calls per timed run, so a run is long enough to time on a busy machine.
                const int repeats = 20;
                static string Repeat(string driver) => $"for (var k = 0; k < {repeats}; k = k + 1) {{ r = {driver}({iterations}); }}";
                var empty = bench.Time(Repeat("PEmpty"));
                long emptyBytes = bench.LastBytes;
                profile.Add(("empty loop (per iteration)", empty.Ms * 1e6 / (iterations * repeats), empty.Ms * 1e6 / empty.Instructions, 0));
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
                    ("t = DsMapGet(pm, \"key\") (a command taking text)", "PMapGet"),
                    ("t = VariableSet(\"pv\", i) (text and a number, no result)", "PVariableSet"),
                    ("DsListSet(pl, 3, i)", "PListSet"),
                    ("DsGridSet(pgr, 1, 2, i)", "PGridSet"),
                    ("DsMapSet(pm, \"key\", i)", "PMapSet"),
                    ("t = StringOf(i % 100)", "PStringOf"),
                    ("t = PNop() (user call, no arguments)", "PCall0"),
                    ("t = PId3(i, 1, 2) (user call, 3 arguments)", "PCall3"),
                    ("t = Sin(i * 127.1 + 311.7)", "PSin"),
                    ("t = PHash(i, 2, 3) (a lattice hash written in script: Sin, multiplies, a branch, %)", "PCallHash"),
                    ("t = PValue(i * 0.7, i * 0.3, 16, 4) (value noise written in script: four PHash calls)", "PCallValue"),
                    ("t = ValueNoise2D(i * 0.7 / 16, i * 0.3 / 16, 4) (the command)", "PCallValueNative"),
                })
                {
                    var time = bench.Time(Repeat(driver));
                    long bodyInstructions = Math.Max(1, time.Instructions - empty.Instructions);
                    profile.Add((body, (time.Ms - empty.Ms) * 1e6 / (iterations * repeats), (time.Ms - empty.Ms) * 1e6 / bodyInstructions,
                        Math.Max(0, bench.LastBytes - emptyBytes) / (double)(iterations * repeats)));
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
            try { process.ProcessorAffinity = previousAffinity; } catch (Exception) { }
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
    private const double LightCells = 10920, LightChecksum = 188552302, MeshFaces = 1740, MeshGeometryChecksum = 123593722.76293945;
    // The generated chunk's checksum, recorded from the VM of 9 Oct 2026 before its allocation and call changes.
    private const double GenChecksum = 91240438;

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private static void Write(HeadlessContext ctx, List<Measure> measures,
        List<(string Body, double NsPerIteration, double NsPerInstruction, double BytesPerIteration)> profile, List<(string Path, double Ns)> bridge)
    {
        var text = new StringBuilder();
        text.AppendLine("# PGSL VM speed");
        text.AppendLine();
        text.AppendLine($"Median of {Samples} runs each, one VM as the Player runs it (no diagnostics collector).");
        text.AppendLine();
        text.AppendLine("| Workload | ms per run (median) | fastest run | instructions | ns per instruction | ns per unit | unit | bytes allocated per run (fewest) | result |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|---:|");
        foreach (Measure m in measures)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {m.Workload} | {m.MsPerRun:F3} | {m.MinMs:F3} | {m.InstructionsPerRun} | {m.NsPerInstruction:F1} | {m.NsPerUnit:F1} | {m.Unit} | {(m.BytesPerRun >= 0 ? m.BytesPerRun.ToString(CultureInfo.InvariantCulture) : "")} | {m.Result} |"));
        text.AppendLine();
        text.AppendLine("## Profile: one loop body per kind of operation (fastest of the runs, empty loop subtracted)");
        text.AppendLine();
        text.AppendLine("| Loop body | ns per iteration | ns per instruction | bytes allocated per iteration |");
        text.AppendLine("|---|---:|---:|---:|");
        foreach ((string body, double ns, double perInstruction, double bytes) in profile)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {body} | {ns:F1} | {perInstruction:F1} | {bytes:F1} |"));
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
            profile = profile.Select(p => new { body = p.Body, nsPerIteration = p.NsPerIteration, nsPerInstruction = p.NsPerInstruction, bytesPerIteration = p.BytesPerIteration }),
            bridge = bridge.Select(b => new { path = b.Path, ns = b.Ns }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

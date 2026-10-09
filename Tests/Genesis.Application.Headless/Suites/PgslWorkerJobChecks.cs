using System.Diagnostics;
using System.Globalization;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Script functions run as worker jobs (JobScriptCreate / JobRunScript): a job fills the same grids
/// as calling the function directly, refuses what reaches the game, runs many at once without
/// touching each other, can be cancelled and released, and keeps to its instruction budget. Part of
/// pgsl-logic; timings are reported in its page, not asserted beyond a generous ceiling.
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    // A small chunk of a block world, as a game's generator would fill it: a height per column from
    // noise and a helper call, then a block kind per cell. 8 x 8 columns, 32 high (2 048 cells).
    private const string Library = """
        function WjHeight(bx, bz, seed) {
            return Floor(14 + 10 * FractalNoise2D(bx / 24, bz / 24, seed, 4, 2, 0.5));
        }
        function WjKind(level, h) {
            if (level < h - 3) { return 1; }
            if (level < h) { return 2; }
            if (level == h) { return 3; }
            return 0;
        }
        function WjFillChunk(blocks, heights, kinds, cx, cz, seed) {
            var total = 0;
            for (var cz2 = 0; cz2 < 8; cz2 = cz2 + 1) {
                for (var cx2 = 0; cx2 < 8; cx2 = cx2 + 1) {
                    var h = WjHeight(cx * 8 + cx2, cz * 8 + cz2, seed);
                    DsGridSet(heights, cx2, cz2, h);
                    var column = cx2 + cz2 * 8;
                    for (var level = 0; level < 32; level = level + 1) {
                        var kind = WjKind(level, h);
                        DsGridSet(blocks, column, level, kind);
                        total = total + DsListGet(kinds, kind);
                    }
                }
            }
            return total;
        }
        function WjDraws() { var m = MeshCreate(); return m; }
        function WjRandom() { return Random(5); }
        function WjSeed() { RandomSeed(4); return 1; }
        function WjReadsUnset() { return notGivenToTheJob + 1; }
        function WjSpin(n) { var k = 0; while (k < n) { k = k + 1; } return k; }
        function WjText(a, b) { return a + "-" + b; }
        // A top face for each column, worked out into a list (in a job) or added straight to a mesh.
        function WjQuad(faces, qx, qy, qz, shade, flip) {
            DsListAdd(faces, qx); DsListAdd(faces, qy + 1); DsListAdd(faces, qz);
            DsListAdd(faces, qx + 1); DsListAdd(faces, qy + 1); DsListAdd(faces, qz);
            DsListAdd(faces, qx + 1); DsListAdd(faces, qy + 1); DsListAdd(faces, qz + 1);
            DsListAdd(faces, qx); DsListAdd(faces, qy + 1); DsListAdd(faces, qz + 1);
            DsListAdd(faces, 0); DsListAdd(faces, 1); DsListAdd(faces, 0);
            DsListAdd(faces, 0.25); DsListAdd(faces, 0); DsListAdd(faces, 0.5); DsListAdd(faces, 0.25);
            for (var k = 0; k < 4; k = k + 1) { DsListAdd(faces, shade); DsListAdd(faces, shade - k * 10); DsListAdd(faces, 90); DsListAdd(faces, 1); }
            DsListAdd(faces, flip);
            return 1;
        }
        function WjFaceList(heights, faces) {
            DsListClear(faces);
            for (var cz2 = 0; cz2 < 8; cz2 = cz2 + 1) {
                for (var cx2 = 0; cx2 < 8; cx2 = cx2 + 1) {
                    var h = DsGridGet(heights, cx2, cz2);
                    WjQuad(faces, cx2, h, cz2, 150 + h * 3, (cx2 + cz2) % 2);
                }
            }
            return DsListSize(faces) / 36;
        }
        function WjFacesDirect(heights, m) {
            for (var cz2 = 0; cz2 < 8; cz2 = cz2 + 1) {
                for (var cx2 = 0; cx2 < 8; cx2 = cx2 + 1) {
                    var h = DsGridGet(heights, cx2, cz2); var shade = 150 + h * 3;
                    MeshAddQuadColors(m, cx2, h + 1, cz2, cx2 + 1, h + 1, cz2, cx2 + 1, h + 1, cz2 + 1, cx2, h + 1, cz2 + 1, 0, 1, 0, 0.25, 0, 0.5, 0.25,
                        shade, shade, 90, 1, shade, shade - 10, 90, 1, shade, shade - 20, 90, 1, shade, shade - 30, 90, 1, (cx2 + cz2) % 2);
                }
            }
            return MeshVertexCount(m);
        }
        // Reads two instance variables, as a game's own functions do.
        function WjUsesGlobals(n) { return n * wjScale + DsGridGet(wjGrid, 1, 0) + StringLength(wjName); }
        // Value noise written in script, the way a game did it before the noise commands.
        function SnHash(ix, iz, salt) {
            var v = Sin(ix * 127.1 + iz * 311.7 + salt * 74.7) * 43758.5453;
            if (v < 0) { v = 0 - v; }
            return v % 1;
        }
        function SnNoise(px, pz, size, salt) {
            var fx = px / size; var fz = pz / size;
            var ix = Floor(fx); var iz = Floor(fz);
            var u = fx - ix; var v = fz - iz;
            u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
            var a = SnHash(ix, iz, salt); var b = SnHash(ix + 1, iz, salt);
            var c = SnHash(ix, iz + 1, salt); var d = SnHash(ix + 1, iz + 1, salt);
            var top = a + (b - a) * u; var bot = c + (d - c) * u;
            return top + (bot - top) * v;
        }
        function SnLoopScript(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + SnNoise(i * 0.7, i * 0.3, 16, 4); } return s; }
        function SnLoopValue(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + ValueNoise2D(i * 0.7 / 16, i * 0.3 / 16, 4); } return s; }
        function SnLoopFractal(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + FractalNoise2D(i * 0.7 / 16, i * 0.3 / 16, 4, 4, 2, 0.5); } return s; }
        function SnLoopEmpty(n) { var s = 0; for (var i = 0; i < n; i = i + 1) { s = s + (i * 0.7 / 16 + i * 0.3 / 16 + 4); } return s; }
        """;

    private sealed class Bench : IDisposable
    {
        private readonly PgslContext? _previousCommands;
        private readonly PgslContext? _previousBridge;
        private readonly Dictionary<string, CompileResult> _compiled = new(StringComparer.Ordinal);
        private bool _cleared;
        public PgslContext Context { get; }
        public PgslVm Vm { get; }

        public Bench()
        {
            VMEngine.Initialize();
            Context = new PgslContext { InstanceId = 1, RoomWidth = 1280, RoomHeight = 720, DrawSurface = new PgslRecordingDrawSurface() };
            _previousCommands = PgslCommands.BindContext(Context);
            _previousBridge = VMEngine.Bridge.GetContext();
            VMEngine.Bridge.SetContext(Context);
            Vm = VMEngine.CreateVm();
            Context.ActiveVm = Vm;
        }

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

        /// <summary>
        /// Runs the code for most of half a second, then waits: this process JITs new code all the
        /// time, which postpones the runtime's optimised recompile of the VM, so a timing taken
        /// straight away measures its first, unoptimised tier (three times slower) instead.
        /// </summary>
        public void WarmUp(params string[] sources)
        {
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 400)
                foreach (string source in sources) Run(source);
            Thread.Sleep(200);
        }
        public string Text(string source) => Convert.ToString(Run(source), CultureInfo.InvariantCulture) ?? "";

        /// <summary>Waits (without running the job's work here) until a job has finished; its final status.</summary>
        public string Wait(double job, double seconds = 20)
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                string status = PgslCommands.JobStatus(job);
                if (status is not ("queued" or "running")) return status;
                TimeSpan waited = Stopwatch.GetElapsedTime(start);
                if (waited.TotalSeconds > seconds) return status + " (timed out)";
                // Sleep(1) lasts about 15 ms on Windows: yield at first, so short waits are timed truly.
                if (waited.TotalMilliseconds < 200) Thread.Yield();
                else Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            PgslCommands.ReleaseJobs(Context);
            VMEngine.Bridge.SetContext(_previousBridge);
            PgslCommands.BindContext(_previousCommands);
        }
    }

    private const string Setup = """
        kinds = DsListCreate(); DsListAdd(kinds, 0); DsListAdd(kinds, 10); DsListAdd(kinds, 100); DsListAdd(kinds, 1000);
        r = kinds;
        """;

    // A chunk filled directly by this VM (the game's thread): its grids' cells and the function's result.
    private static (double[] Blocks, double[] Heights, double Total) Direct(Bench bench, int cx, int cz, double seed)
    {
        double total = bench.Number($"bd = DsGridCreate(64, 32); hd = DsGridCreate(8, 8); r = WjFillChunk(bd, hd, kinds, {cx}, {cz}, {seed.ToString(CultureInfo.InvariantCulture)});");
        double blocks = bench.Number("r = bd;"), heights = bench.Number("r = hd;");
        (double[] b, double[] h) = (Cells(blocks), Cells(heights));
        bench.Run("DsGridDestroy(bd); DsGridDestroy(hd); r = 0;");
        return (b, h, total);
    }

    private static double[] Cells(double grid)
    {
        int width = (int)PgslCommands.DsGridWidth(grid), height = (int)PgslCommands.DsGridHeight(grid);
        double[] cells = new double[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) cells[(y * width) + x] = PgslCommands.DsGridGet(grid, x, y);
        return cells;
    }

    /// <summary>Starts a chunk job into two new grids; returns the job and the grids.</summary>
    private static (double Job, double Blocks, double Heights) StartChunk(Bench bench, int cx, int cz, double seed)
    {
        double job = bench.Number($"""
            jb = DsGridCreate(64, 32); jh = DsGridCreate(8, 8);
            jj = JobScriptCreate("WjFillChunk");
            JobScriptGrid(jj, jb, true); JobScriptGrid(jj, jh, true); JobScriptList(jj, kinds, false);
            started = JobScriptStart(jj, jb, jh, kinds, {cx}, {cz}, {seed.ToString(CultureInfo.InvariantCulture)});
            r = started ? jj : -1;
            """);
        HeadlessHarness.Assert(job > 0, "A chunk job did not start: " + PgslCommands.JobLastError());
        return (job, bench.Number("r = jb;"), bench.Number("r = jh;"));
    }

    public static void Run(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        string? previousProject = PgslCommands.ProjectPath;
        PgslCommands.ProjectPath = null;
        ScriptAssetRegistry.ClearCache();
        ScriptAssetRegistry.Register("WorkerJobLibrary", Library);
        try
        {
            // What a noise sample costs a script: written in script (four hashes with a sine each)
            // against the commands, each called from a script loop (the loop itself subtracted).
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.NoiseInScriptAgainstCommands", () =>
            {
                using Bench bench = new();
                const int n = 2000;
                double Best(string loop)
                {
                    double best = double.MaxValue;
                    for (int round = 0; round < 9; round++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        bench.Run($"r = {loop}({n});");
                        best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                    return best * 1e6 / n;
                }
                bench.WarmUp("r = SnLoopEmpty(200);", "r = SnLoopScript(200);", "r = SnLoopValue(200);", "r = SnLoopFractal(200);");
                double empty = Best("SnLoopEmpty");
                double script = Best("SnLoopScript") - empty, value = Best("SnLoopValue") - empty, fractal = Best("SnLoopFractal") - empty;
                string F(double ns) => ns.ToString("F0", CultureInfo.InvariantCulture) + " ns";
                row("Noise speed", "value noise written in script (SnNoise: 4 hashes with Sin), a call from script", "behaviour", F(script));
                row("Noise speed", "ValueNoise2D called from script", "behaviour", F(value));
                row("Noise speed", "FractalNoise2D with 4 octaves called from script", "behaviour", F(fractal));
                Console.WriteLine($"Noise from script: script value noise {F(script)}, ValueNoise2D {F(value)}, FractalNoise2D x4 {F(fractal)} a sample");
                HeadlessHarness.Assert(value < script, $"ValueNoise2D ({F(value)}) was not quicker than noise written in script ({F(script)}).");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.SameGridsAsCallingTheFunction", () =>
            {
                using Bench bench = new();
                bench.Run(Setup);
                (double[] blocks, double[] heights, double total) = Direct(bench, 3, -2, 77);
                (double job, double jobBlocks, double jobHeights) = StartChunk(bench, 3, -2, 77);
                string status = bench.Wait(job);
                HeadlessHarness.Assert(status == "succeeded", $"The chunk job ended {status}: {PgslCommands.JobError(job)}");
                // Until it is taken, the game's grids are untouched: the job filled copies of its own.
                HeadlessHarness.Assert(Cells(jobBlocks).All(cell => cell == 0), "The job wrote into the game's grid before JobTake.");
                HeadlessHarness.Assert(PgslCommands.JobTake(job), "JobTake refused a finished job: " + PgslCommands.JobLastError());
                HeadlessHarness.Assert(!PgslCommands.JobTake(job), "JobTake took the same job's data twice.");
                double jobTotal = PgslCommands.JobResultNumber(job);
                HeadlessHarness.Assert(Cells(jobBlocks).SequenceEqual(blocks) && Cells(jobHeights).SequenceEqual(heights) && jobTotal == total,
                    $"The job's chunk differs from the direct call's (result {jobTotal} against {total}).");
                HeadlessHarness.Assert(PgslCommands.JobRelease(job), "The finished job could not be released.");
                row("Jobs", "a chunk job fills the same grids as calling the function", "PASS", $"result {total}, {blocks.Count(cell => cell != 0)} solid cells");

                // Instance variables the function reads, given to the job by name.
                double direct = bench.Number("""wjScale = 3; wjGrid = DsGridCreate(2, 1); DsGridSet(wjGrid, 1, 0, 40); wjName = "abcd"; r = WjUsesGlobals(5);""");
                double named = bench.Number("""
                    vj = JobScriptCreate("WjUsesGlobals");
                    JobScriptGrid(vj, wjGrid, false);
                    JobScriptVariable(vj, "wjScale", wjScale); JobScriptVariable(vj, "wjGrid", wjGrid); JobScriptVariableText(vj, "wjName", wjName);
                    refusedBuiltIn = JobScriptVariable(vj, "x", 1) == 0 && JobScriptVariable(vj, "2bad", 1) == 0;
                    JobScriptStart(vj, 5);
                    r = vj;
                    """);
                HeadlessHarness.Assert(bench.Wait(named) == "succeeded" && PgslCommands.JobResultNumber(named) == direct && direct == 59,
                    $"A job given wjScale, wjGrid and wjName returned {PgslCommands.JobResultNumber(named)} ({PgslCommands.JobError(named)}), the direct call {direct}.");
                HeadlessHarness.Assert(bench.Number("r = refusedBuiltIn;") == 1, "JobScriptVariable accepted a built-in or a bad name.");
                PgslCommands.JobRelease(named);
                row("Jobs", "instance variables given by name (JobScriptVariable)", "PASS", $"result {direct} as directly");

                double text = bench.Number("""tj = JobRunScript("WjText", "a", 2); r = tj;""");
                HeadlessHarness.Assert(text > 0 && bench.Wait(text) == "succeeded" && PgslCommands.JobResultString(text) == "a-2",
                    $"JobRunScript with text gave '{PgslCommands.JobResultString(text)}' ({PgslCommands.JobStatus(text)}: {PgslCommands.JobError(text)}).");
                PgslCommands.JobRelease(text);
            });

            // A job works out a mesh's faces into a list; the game's thread adds them in one call, and
            // the mesh is the same as one built a face at a time.
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.MeshFacesFromAJob", () =>
            {
                using Bench bench = new();
                double job = bench.Number("""
                    hh = DsGridCreate(8, 8); NoiseFillGrid(hh, 0, 0, 0.3, 5, 2, 2, 0.5, 6, 10); DsGridFloorRegion(hh, 0, 0, 7, 7);
                    fl = DsListCreate();
                    fj = JobScriptCreate("WjFaceList"); JobScriptGrid(fj, hh, false); JobScriptList(fj, fl, true);
                    JobScriptStart(fj, hh, fl);
                    r = fj;
                    """);
                HeadlessHarness.Assert(bench.Wait(job) == "succeeded" && PgslCommands.JobTake(job),
                    $"The faces job ended {PgslCommands.JobStatus(job)}: {PgslCommands.JobError(job)}");
                PgslCommands.JobRelease(job);
                double quads = bench.Number("m1 = MeshCreate(); r = MeshAddQuadsFromList(m1, fl);");
                bench.Run("m2 = MeshCreate(); r = WjFacesDirect(hh, m2);");
                int listed = (int)bench.Number("r = m1;"), direct = (int)bench.Number("r = m2;");
                Genesis.Runtime.Rendering.ScriptMeshes.TryGetVertices(listed, out var listVertices, out ushort[] listIndices);
                Genesis.Runtime.Rendering.ScriptMeshes.TryGetVertices(direct, out var directVertices, out ushort[] directIndices);
                bool same = listVertices.Length == directVertices.Length && listIndices.SequenceEqual(directIndices)
                    && listVertices.Zip(directVertices).All(pair => pair.First.Position == pair.Second.Position && pair.First.Normal == pair.Second.Normal
                        && pair.First.UV == pair.Second.UV && pair.First.Color == pair.Second.Color);
                row("Jobs", "a mesh's faces worked out in a job, added with MeshAddQuadsFromList", same && quads == 64 ? "PASS" : "FAIL",
                    $"{quads} quads, {listVertices.Length} vertices, the same as added one at a time: {same}");
                HeadlessHarness.Assert(quads == 64 && same, $"MeshAddQuadsFromList added {quads} quads; the same mesh as one face at a time: {same}.");

                // What the game's thread spends a face: one list command against a script call each.
                const string fromList = "MeshClear(m1); r = MeshAddQuadsFromList(m1, fl);", oneByOne = "MeshClear(m2); r = WjFacesDirect(hh, m2);";
                bench.WarmUp(fromList, oneByOne);
                double Fastest(string source)
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < 30; i++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        bench.Run(source);
                        best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                    return best * 1e6 / 64;
                }
                double listNs = Fastest(fromList), callNs = Fastest(oneByOne);
                row("Job speed", "game-thread time per face: MeshAddQuadsFromList / MeshAddQuadColors called from script", "behaviour",
                    $"{listNs.ToString("F0", CultureInfo.InvariantCulture)} ns / {callNs.ToString("F0", CultureInfo.InvariantCulture)} ns");
                Console.WriteLine($"Mesh faces: from a list {listNs:F0} ns a face, one script call each {callNs:F0} ns a face");

                // Vertices and triangles from lists, a vertex that is not a number skipped with its triangle.
                double vertices = bench.Number("""
                    vl = DsListCreate(); tl = DsListCreate();
                    for (k = 0; k < 4; k = k + 1) {
                        DsListAdd(vl, k % 2); DsListAdd(vl, 0); DsListAdd(vl, Floor(k / 2)); DsListAdd(vl, 0); DsListAdd(vl, 1); DsListAdd(vl, 0);
                        DsListAdd(vl, 0); DsListAdd(vl, 0); DsListAdd(vl, 255); DsListAdd(vl, 255); DsListAdd(vl, 255); DsListAdd(vl, 1);
                    }
                    DsListAdd(vl, 0 / 0); for (k = 0; k < 11; k = k + 1) { DsListAdd(vl, 0); }
                    DsListAdd(tl, 0); DsListAdd(tl, 1); DsListAdd(tl, 3); DsListAdd(tl, 0); DsListAdd(tl, 3); DsListAdd(tl, 2); DsListAdd(tl, 0); DsListAdd(tl, 4); DsListAdd(tl, 1);
                    m3 = MeshCreate(); MeshAddVertex(m3, 9, 9, 9, 0, 1, 0, 0, 0, 1, 1, 1, 1);
                    r = MeshAddVerticesFromList(m3, vl, tl);
                    """);
                double triangles = bench.Number("r = MeshTriangleCount(m3);"), total = bench.Number("r = MeshVertexCount(m3);");
                row("Jobs", "MeshAddVerticesFromList", vertices == 4 && triangles == 2 && total == 5 ? "PASS" : "FAIL",
                    $"{vertices} vertices added after one already there, {triangles} triangles (the one using the bad vertex skipped)");
                HeadlessHarness.Assert(vertices == 4 && triangles == 2 && total == 5,
                    $"MeshAddVerticesFromList added {vertices} vertices and {triangles} triangles ({total} in the mesh).");
                bench.Run("MeshDestroy(m1); MeshDestroy(m2); MeshDestroy(m3); r = 0;");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.WhatAJobCannotDo", () =>
            {
                using Bench bench = new();
                foreach ((string function, string expected) in new[]
                {
                    ("WjDraws", "MeshCreate is not available in a worker job"),
                    ("WjRandom", "Random is not available in a worker job"),
                    ("WjSeed", "RandomSeed is not available in a worker job"),
                    ("WjReadsUnset", "'notGivenToTheJob' has no value in this job"),
                })
                {
                    double job = bench.Number($"""r = JobRunScript("{function}");""");
                    HeadlessHarness.Assert(job > 0, $"{function} did not start: {PgslCommands.JobLastError()}");
                    string status = bench.Wait(job), error = PgslCommands.JobError(job);
                    row("Jobs", function, status == "failed" && error.Contains(expected, StringComparison.Ordinal) ? "PASS" : "FAIL", error);
                    HeadlessHarness.Assert(status == "failed" && error.Contains(expected, StringComparison.Ordinal),
                        $"{function} ended {status} with '{error}', not an error saying '{expected}'.");
                    PgslCommands.JobRelease(job);
                }
                HeadlessHarness.Assert(bench.Number("""r = JobRunScript("NoSuchFunction");""") == 0
                    && PgslCommands.JobLastError().Contains("No function 'NoSuchFunction'", StringComparison.Ordinal),
                    "A job of a missing function was not refused: " + PgslCommands.JobLastError());
                HeadlessHarness.Assert(bench.Number("""r = JobRunScript("WjSpin");""") == 0
                    && PgslCommands.JobLastError().Contains("takes 1 arguments", StringComparison.Ordinal),
                    "A job given too few arguments was not refused: " + PgslCommands.JobLastError());
            });

            RunSharedData(ctx, row);

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.ManyAtOnceStayApart", () =>
            {
                using Bench bench = new();
                bench.Run(Setup);
                // Twelve jobs in flight, two of each seed: every job's grids must match the direct
                // call's for its seed, so no job saw another's data.
                double[] seeds = [5, 6, 7, 8, 9, 10];
                Dictionary<double, (double[] Blocks, double[] Heights, double Total)> expected = seeds.ToDictionary(seed => seed, seed => Direct(bench, 1, 4, seed));
                List<(double Seed, double Job, double Blocks, double Heights)> jobs = [];
                foreach (double seed in seeds.Concat(seeds))
                {
                    (double job, double blocks, double heights) = StartChunk(bench, 1, 4, seed);
                    jobs.Add((seed, job, blocks, heights));
                }
                int matched = 0;
                foreach ((double seed, double job, double blocks, double heights) in jobs)
                {
                    string status = bench.Wait(job);
                    HeadlessHarness.Assert(status == "succeeded", $"Job for seed {seed} ended {status}: {PgslCommands.JobError(job)}");
                    PgslCommands.JobTake(job);
                    if (Cells(blocks).SequenceEqual(expected[seed].Blocks) && Cells(heights).SequenceEqual(expected[seed].Heights)
                        && PgslCommands.JobResultNumber(job) == expected[seed].Total) matched++;
                    PgslCommands.JobRelease(job);
                }
                row("Jobs", $"{jobs.Count} jobs at once on {NativeJobPool.ScriptWorkerCount} workers", matched == jobs.Count ? "PASS" : "FAIL", $"{matched} matched the direct call");
                HeadlessHarness.Assert(matched == jobs.Count, $"Only {matched} of {jobs.Count} concurrent jobs matched the direct call for their seed.");
            });

            // As many jobs as the game may hold (16 for each of two Objects), more than there are
            // workers: the rest wait their turn, no more threads start than the limit, all finish.
            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.MoreJobsThanWorkersWaitTheirTurn", () =>
            {
                using Bench bench = new();
                PgslContext other = new() { InstanceId = 2 };
                PgslContext[] owners = [bench.Context, other];
                int reservedBefore = NativeJobPool.ReservedJobs;
                List<(PgslContext Owner, double Job)> Start(Func<double> start)
                {
                    List<(PgslContext Owner, double Job)> started = [];
                    foreach (PgslContext owner in owners)
                    {
                        PgslCommands.BindContext(owner);
                        for (int i = 0; i < NativeJobPool.MaximumContextJobs; i++)
                        {
                            double job = start();
                            HeadlessHarness.Assert(job > 0, "A job did not start: " + PgslCommands.JobLastError());
                            started.Add((owner, job));
                        }
                    }
                    return started;
                }
                string Status((PgslContext Owner, double Job) entry) { PgslCommands.BindContext(entry.Owner); return PgslCommands.JobStatus(entry.Job); }
                try
                {
                    // Waiting, made certain: every job spins until it is cancelled (far longer than
                    // the count takes, however busy the machine), so no worker frees up meanwhile and
                    // every job beyond the workers is still queued when counted.
                    var spinning = Start(() =>
                    {
                        double job = PgslCommands.JobScriptCreate("WjSpin");
                        PgslCommands.JobScriptBudget(job, 2_000_000_000);
                        return PgslCommands.JobScriptStart(job, 1_000_000_000.0) ? job : 0;
                    });
                    int waiting = spinning.Count(entry => Status(entry) == "queued");
                    int threadsWhileFull = NativeJobPool.ScriptThreads;
                    // A job still waiting for a thread is cancelled at once (with every worker busy, it stays waiting).
                    (PgslContext lastOwner, double last) = spinning[^1];
                    bool lastWaiting = Status(spinning[^1]) == "queued";
                    PgslCommands.BindContext(lastOwner);
                    bool cancelledAtOnce = !lastWaiting || (PgslCommands.JobCancel(last) && PgslCommands.JobStatus(last) == "cancelled");
                    int cancelled = 0;
                    foreach ((PgslContext owner, double job) in spinning)
                    {
                        PgslCommands.BindContext(owner);
                        PgslCommands.JobCancel(job);
                        if (bench.Wait(job, 30) == "cancelled") cancelled++;
                        PgslCommands.JobRelease(job);
                    }
                    long freed = Stopwatch.GetTimestamp();
                    while (NativeJobPool.ReservedJobs > reservedBefore && Stopwatch.GetElapsedTime(freed).TotalSeconds < 10) Thread.Sleep(1);

                    // All finish: 32 ordinary jobs, more than the workers, each with its own result.
                    var finishing = Start(() => PgslCommands.JobRunScript("WjSpin", 3_000_000.0));
                    int succeeded = 0;
                    foreach ((PgslContext owner, double job) in finishing)
                    {
                        PgslCommands.BindContext(owner);
                        if (bench.Wait(job, 60) == "succeeded" && PgslCommands.JobResultNumber(job) == 3_000_000) succeeded++;
                        PgslCommands.JobRelease(job);
                    }
                    int expectedWaiting = Math.Max(0, spinning.Count - NativeJobPool.ScriptWorkerCount);
                    bool pass = waiting >= expectedWaiting && cancelledAtOnce && cancelled == spinning.Count && succeeded == finishing.Count
                        && threadsWhileFull <= NativeJobPool.ScriptWorkerCount && NativeJobPool.ScriptThreads <= NativeJobPool.ScriptWorkerCount;
                    row("Jobs", $"{spinning.Count} jobs on {NativeJobPool.ScriptWorkerCount} workers", pass ? "PASS" : "FAIL",
                        $"{waiting} of {spinning.Count} long jobs waiting (at least {expectedWaiting} expected), {cancelled} cancelled, a waiting job cancelled at once: {cancelledAtOnce && lastWaiting}; "
                        + $"{succeeded} of {finishing.Count} short jobs finished with their result; {NativeJobPool.ScriptThreads} threads started");
                    HeadlessHarness.Assert(waiting >= expectedWaiting, $"Only {waiting} of {spinning.Count} jobs waited with {NativeJobPool.ScriptWorkerCount} workers busy.");
                    HeadlessHarness.Assert(cancelledAtOnce, "A job waiting for a thread was not cancelled at once.");
                    HeadlessHarness.Assert(cancelled == spinning.Count, $"Only {cancelled} of {spinning.Count} spinning jobs ended cancelled.");
                    HeadlessHarness.Assert(succeeded == finishing.Count, $"Only {succeeded} of {finishing.Count} jobs ended as expected.");
                    HeadlessHarness.Assert(threadsWhileFull <= NativeJobPool.ScriptWorkerCount && NativeJobPool.ScriptThreads <= NativeJobPool.ScriptWorkerCount,
                        $"{NativeJobPool.ScriptThreads} worker threads started; the limit is {NativeJobPool.ScriptWorkerCount}.");
                }
                finally
                {
                    PgslCommands.ReleaseJobs(other);
                    PgslCommands.BindContext(bench.Context);
                }
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.CancelReleaseAndBudget", () =>
            {
                using Bench bench = new();
                int reservedBefore = NativeJobPool.ReservedJobs;
                // A long job is cancelled while it runs.
                double spin = bench.Number("""sj = JobScriptCreate("WjSpin"); JobScriptBudget(sj, 2000000000); JobScriptStart(sj, 1000000000); r = sj;""");
                long deadline = Stopwatch.GetTimestamp();
                while (PgslCommands.JobStatus(spin) == "queued" && Stopwatch.GetElapsedTime(deadline).TotalSeconds < 5) Thread.Sleep(1);
                HeadlessHarness.Assert(PgslCommands.JobCancel(spin), "A running job could not be cancelled: " + PgslCommands.JobLastError());
                long cancelled = Stopwatch.GetTimestamp();
                string status = bench.Wait(spin, 5);
                double stopMs = Stopwatch.GetElapsedTime(cancelled).TotalMilliseconds;
                row("Jobs", "a running job cancelled", status == "cancelled" ? "PASS" : "FAIL", $"stopped {stopMs.ToString("F1", CultureInfo.InvariantCulture)} ms after JobCancel");
                HeadlessHarness.Assert(status == "cancelled", $"The cancelled job ended {status}: {PgslCommands.JobError(spin)}");
                PgslCommands.JobRelease(spin);

                // A job over its budget fails and says so.
                double over = bench.Number("""oj = JobScriptCreate("WjSpin"); JobScriptBudget(oj, 100000); JobScriptStart(oj, 10000000); r = oj;""");
                status = bench.Wait(over);
                HeadlessHarness.Assert(status == "failed" && PgslCommands.JobError(over).Contains("budget of 100000", StringComparison.Ordinal),
                    $"A job over its budget ended {status}: {PgslCommands.JobError(over)}");
                PgslCommands.JobRelease(over);

                // A job may run far more than one call's 100 000 instructions when its budget allows.
                double long1 = bench.Number("""lj = JobRunScript("WjSpin", 400000); r = lj;""");
                HeadlessHarness.Assert(bench.Wait(long1) == "succeeded" && PgslCommands.JobResultNumber(long1) == 400000,
                    $"A job of 2 million instructions ended {PgslCommands.JobStatus(long1)}: {PgslCommands.JobError(long1)}");
                PgslCommands.JobRelease(long1);

                // Released while running, and a prepared job released or cancelled: all capacity comes back.
                double running = bench.Number("""rj = JobRunScript("WjSpin", 1000000000); r = rj;""");
                double prepared = bench.Number("""pj = JobScriptCreate("WjSpin"); r = pj;""");
                double preparedCancelled = bench.Number("""pc = JobScriptCreate("WjSpin"); JobCancel(pc); r = pc;""");
                HeadlessHarness.Assert(PgslCommands.JobStatus(prepared) == "prepared" && PgslCommands.JobStatus(preparedCancelled) == "cancelled",
                    $"Prepared jobs read {PgslCommands.JobStatus(prepared)} / {PgslCommands.JobStatus(preparedCancelled)}.");
                HeadlessHarness.Assert(!PgslCommands.JobScriptStart(preparedCancelled, 5), "A cancelled prepared job started.");
                HeadlessHarness.Assert(PgslCommands.JobRelease(running) && PgslCommands.JobRelease(prepared) && PgslCommands.JobRelease(preparedCancelled),
                    "Jobs could not be released.");
                HeadlessHarness.Assert(PgslCommands.JobStatus(running) == "invalid", "A released job's handle still answers.");
                long wait = Stopwatch.GetTimestamp();
                while (NativeJobPool.ReservedJobs > reservedBefore && Stopwatch.GetElapsedTime(wait).TotalSeconds < 5) Thread.Sleep(1);
                HeadlessHarness.Assert(NativeJobPool.ReservedJobs == reservedBefore,
                    $"{NativeJobPool.ReservedJobs - reservedBefore} job places were not given back after release.");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.Throughput", () =>
            {
                using Bench bench = new();
                bench.Run(Setup);
                bench.WarmUp("bw = DsGridCreate(64, 32); hw = DsGridCreate(8, 8); r = WjFillChunk(bw, hw, kinds, 0, 0, 1); DsGridDestroy(bw); DsGridDestroy(hw);");
                // The function called on the game's thread into the same two grids, fastest of twenty.
                bench.Run("bt = DsGridCreate(64, 32); ht = DsGridCreate(8, 8); r = 0;");
                double directMs = double.MaxValue;
                for (int i = 0; i < 20; i++)
                {
                    long call = Stopwatch.GetTimestamp();
                    bench.Run("r = WjFillChunk(bt, ht, kinds, 5, 1, 3);");
                    directMs = Math.Min(directMs, Stopwatch.GetElapsedTime(call).TotalMilliseconds);
                }

                // As many jobs in flight as an Object may hold (16), refilled as they finish.
                const int inFlightLimit = 14;
                double workerMs = 0;
                (double WallMs, double GameThreadMs) Round(int total, int limit = inFlightLimit)
                {
                    int started = 0, finished = 0;
                    double gameThreadMs = 0;
                    workerMs = 0;
                    List<double> inFlight = [];
                    long roundStart = Stopwatch.GetTimestamp();
                    while (finished < total)
                    {
                        while (started < total && inFlight.Count < limit)
                        {
                            long launch = Stopwatch.GetTimestamp();
                            (double job, double blocks, double heights) = StartChunk(bench, started, 1, 3);
                            PgslCommands.DsGridDestroy(blocks);
                            PgslCommands.DsGridDestroy(heights);
                            inFlight.Add(job);
                            gameThreadMs += Stopwatch.GetElapsedTime(launch).TotalMilliseconds;
                            started++;
                        }
                        for (int i = inFlight.Count - 1; i >= 0; i--)
                        {
                            string status = PgslCommands.JobStatus(inFlight[i]);
                            if (status is "queued" or "running") continue;
                            HeadlessHarness.Assert(status == "succeeded", $"A throughput job ended {status}: {PgslCommands.JobError(inFlight[i])}");
                            workerMs += PgslCommands.ScriptJobWorkerMilliseconds(inFlight[i]);
                            long take = Stopwatch.GetTimestamp();
                            PgslCommands.JobTake(inFlight[i]);
                            PgslCommands.JobRelease(inFlight[i]);
                            gameThreadMs += Stopwatch.GetElapsedTime(take).TotalMilliseconds;
                            inFlight.RemoveAt(i);
                            finished++;
                        }
                        Thread.Yield();
                    }
                    return (Stopwatch.GetElapsedTime(roundStart).TotalMilliseconds, gameThreadMs);
                }

                // The first jobs on each worker set up its command table; the second round is the steady state.
                (double coldMs, _) = Round(32);
                // One at a time: what a job costs a worker with the machine otherwise idle.
                Round(16, 1);
                double aloneMs = workerMs / 16;
                const int measured = 128;
                (double wallMs, double mainThreadMs) = Round(measured);
                double perSecond = measured / (wallMs / 1000);
                string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
                row("Job speed", "chunk (2 048 cells) filled directly on the game's thread", "behaviour", F(directMs) + " ms");
                row("Job speed", $"first 32 chunk jobs (workers setting up), up to {inFlightLimit} in flight", "behaviour", F(coldMs) + " ms in all");
                row("Job speed", $"{measured} chunk jobs, up to {inFlightLimit} in flight, {NativeJobPool.ScriptWorkerCount} workers", "behaviour",
                    $"{F(wallMs)} ms in all, {F(perSecond)} chunks a second ({F(1000 / directMs)} a second on one thread)");
                row("Job speed", "game-thread time per job (create, grids, start, take, release)", "behaviour", F(mainThreadMs / measured) + " ms");
                row("Job speed", $"worker time per job (its own VM, copies and result), {inFlightLimit} at once / one at a time", "behaviour",
                    $"{F(workerMs / measured)} ms / {F(aloneMs)} ms");
                Console.WriteLine($"Script jobs: direct {F(directMs)} ms a chunk; first 32 jobs {F(coldMs)} ms; {measured} jobs in {F(wallMs)} ms = {F(perSecond)}/s on {NativeJobPool.ScriptWorkerCount} workers; game thread {F(mainThreadMs / measured)} ms a job; worker {F(workerMs / measured)} ms a job ({F(aloneMs)} ms alone)");
                HeadlessHarness.Assert(wallMs < 60_000, $"{measured} chunk jobs took {F(wallMs)} ms.");
            });
        }
        finally
        {
            ScriptAssetRegistry.ClearCache();
            PgslCommands.ProjectPath = previousProject;
        }
    }
}

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
internal static class PgslWorkerJobChecks
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
        function WjReadsUnset() { return notGivenToTheJob + 1; }
        function WjSpin(n) { var k = 0; while (k < n) { k = k + 1; } return k; }
        function WjText(a, b) { return a + "-" + b; }
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

                double text = bench.Number("""tj = JobRunScript("WjText", "a", 2); r = tj;""");
                HeadlessHarness.Assert(text > 0 && bench.Wait(text) == "succeeded" && PgslCommands.JobResultString(text) == "a-2",
                    $"JobRunScript with text gave '{PgslCommands.JobResultString(text)}' ({PgslCommands.JobStatus(text)}: {PgslCommands.JobError(text)}).");
                PgslCommands.JobRelease(text);
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.WhatAJobCannotDo", () =>
            {
                using Bench bench = new();
                foreach ((string function, string expected) in new[]
                {
                    ("WjDraws", "MeshCreate is not available in a worker job"),
                    ("WjRandom", "Random is not available in a worker job"),
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
                Direct(bench, 0, 0, 1);
                Direct(bench, 0, 0, 1);
                const int directRuns = 10;
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < directRuns; i++) Direct(bench, i, 0, 1);
                double directMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds / directRuns;

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

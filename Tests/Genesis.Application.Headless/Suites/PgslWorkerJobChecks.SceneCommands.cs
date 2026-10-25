using System.Diagnostics;
using System.Globalization;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Scene changes queued in jobs (each updating its share of the instances side by side) and made by
/// the game in order at JobTake, the game's own queue made together, and the per-part frame budget report.
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    private const string SceneLibrary = """
        // A job's share of the instances: each gets a new position (and a value) queued, made at JobTake.
        function ScUpdate(idList, startAt, many, gap) {
            for (var i = startAt; i < startAt + many; i = i + 1) {
                var who = NumListGet(idList, i);
                SceneQueuePosition(who, i * gap, 1, 0 - i);
                SceneQueueVariable(who, "hp", i + 100);
            }
            return SceneQueueCount();
        }
        function ScApply() { return SceneQueueApply(); }
        // Reads a cell outside its rectangle, writes one inside.
        function ScGridPeek(grd) { var seen = DsGridGet(grd, 7, 0); DsGridSet(grd, 0, 0, 5); return seen; }
        // Fills a range of a list and a number list and a row of a grid, and writes outside them too.
        function ScRanges(lst, nums, grd, startAt, many, mark) {
            for (var i = startAt; i < startAt + many; i = i + 1) { DsListSet(lst, i, mark); NumListSet(nums, i, mark); }
            DsListSet(lst, 9, 99); NumListSet(nums, 9, 99);
            for (var gx = 0; gx < 8; gx = gx + 1) { DsGridSet(grd, gx, 0, mark); }
            return 1;
        }
        """;

    private static void RunSceneCommands(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        ScriptAssetRegistry.Register("SceneJobLibrary", SceneLibrary);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.SceneChangesQueuedInJobsLandAtTake", () =>
        {
            using Bench bench = new();
            IGameContext? previous = PgslCommands.ActiveGameContext;
            EcsWorld world = new();
            PgslCommands.ActiveGameContext = new NullGameContext { World = world };
            try
            {
                var ids = new List<int>();
                for (int i = 0; i < 8; i++)
                {
                    Entity entity = world.CreateEntity();
                    world.Set(entity, new TransformComponent { X = -1, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                    ids.Add(entity.Id);
                }
                string list = string.Join(" ", ids.Select(id => $"NumListAdd(ids, {id.ToString(CultureInfo.InvariantCulture)});"));
                bench.Run($$"""
                    ids = NumListCreate(); {{list}}
                    ja = JobScriptCreate("ScUpdate"); JobScriptNumList(ja, ids, false);
                    jb = JobScriptCreate("ScUpdate"); JobScriptNumList(jb, ids, false);
                    okA = JobScriptStart(ja, ids, 0, 4, 10); okB = JobScriptStart(jb, ids, 4, 4, 10);
                    applyJob = JobRunScript("ScApply");
                    r = 0;
                    """);
                double ja = bench.Number("r = ja;"), jb = bench.Number("r = jb;"), applyJob = bench.Number("r = applyJob;");
                string statusA = bench.Wait(ja), statusB = bench.Wait(jb), applyStatus = bench.Wait(applyJob);
                HeadlessHarness.Assert(statusA == "succeeded" && statusB == "succeeded",
                    $"The update jobs ended {statusA} / {statusB}: {PgslCommands.JobError(ja)} {PgslCommands.JobError(jb)}");
                HeadlessHarness.Assert(PgslCommands.JobResultNumber(ja) == 8 && PgslCommands.JobResultNumber(jb) == 8,
                    $"Each job should have queued 8 changes (got {PgslCommands.JobResultNumber(ja)}, {PgslCommands.JobResultNumber(jb)}).");
                HeadlessHarness.Assert(applyStatus == "failed" && PgslCommands.JobError(applyJob).Contains("not available in a worker job", StringComparison.Ordinal),
                    $"SceneQueueApply in a job should be refused (ended {applyStatus}: {PgslCommands.JobError(applyJob)}).");
                float X(int index) => world.GetRef<TransformComponent>(world.GetEntity(ids[index])).X;
                HeadlessHarness.Assert(X(0) == -1 && X(7) == -1, "Nothing should move before the game takes a job.");

                // Taken in the other order: each job's share lands when it is taken.
                HeadlessHarness.Assert(PgslCommands.JobTake(jb), "JobTake refused: " + PgslCommands.JobLastError());
                bool secondHalfFirst = X(0) == -1 && X(5) == 50;
                HeadlessHarness.Assert(PgslCommands.JobTake(ja), "JobTake refused: " + PgslCommands.JobLastError());
                TransformComponent third = world.GetRef<TransformComponent>(world.GetEntity(ids[3]));
                HeadlessHarness.Assert(secondHalfFirst && X(0) == 0 && X(7) == 70 && third.Y == 1 && third.Z == -3,
                    $"Each job's moves should land at its JobTake (x0 {X(0)}, x7 {X(7)}, third at {third.X}, {third.Y}, {third.Z}).");
                PgslCommands.JobRelease(ja); PgslCommands.JobRelease(jb); PgslCommands.JobRelease(applyJob);

                // The game's own queue waits for the end of the Step events (here, SceneQueueApply).
                string queued = bench.Text($$"""
                    SceneQueuePosition({{ids[1].ToString(CultureInfo.InvariantCulture)}}, 5, 6, 7); SceneQueueDestroy(-1);
                    waiting = SceneQueueCount(); before = InstanceGetX({{ids[1].ToString(CultureInfo.InvariantCulture)}});
                    made = SceneQueueApply();
                    r = StringFormatNumber(waiting, 0) + " " + StringFormatNumber(before, 0) + " " + StringFormatNumber(made, 0) + " " + StringFormatNumber(InstanceGetX({{ids[1].ToString(CultureInfo.InvariantCulture)}}), 0) + " " + StringFormatNumber(SceneQueueCount(), 0);
                    """);
                HeadlessHarness.Assert(queued == "1 10 1 5 0", $"The game's queue should hold a move until applied (got \"{queued}\").");
                row("Jobs: scene commands", "two jobs queue moves for 4 instances each; made at JobTake", "PASS", "8 changes a job; order kept; SceneQueueApply refused in jobs");
            }
            finally
            {
                PgslCommands.ClearSceneCommands();
                PgslCommands.ActiveGameContext = previous;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.ListRangesAndSeveralRectanglesComeBack", () =>
        {
            using Bench bench = new();
            string result = bench.Text("""
                rl = DsListCreate(); rn = NumListCreate(); rg = DsGridCreate(8, 4);
                for (var i = 0; i < 10; i = i + 1) { DsListAdd(rl, 0); NumListAdd(rn, 0); }
                ja = JobScriptCreate("ScRanges");
                JobScriptListRegion(ja, rl, 0, 3, true); JobScriptNumListRegion(ja, rn, 0, 3, true);
                JobScriptGridRegion(ja, rg, 0, 0, 1, 0, true); JobScriptGridRegion(ja, rg, 6, 0, 7, 0, true);
                jb = JobScriptCreate("ScRanges");
                JobScriptListRegion(jb, rl, 5, 3, true); JobScriptNumListRegion(jb, rn, 5, 3, true); JobScriptGrid(jb, rg, false);
                JobScriptStart(ja, rl, rn, rg, 0, 3, 1); JobScriptStart(jb, rl, rn, rg, 5, 3, 2);
                r = StringFormatNumber(ja, 0) + " " + StringFormatNumber(jb, 0);
                """);
            string[] jobs = result.Split(' ');
            double ja = double.Parse(jobs[0], CultureInfo.InvariantCulture), jb = double.Parse(jobs[1], CultureInfo.InvariantCulture);
            string statusA = bench.Wait(ja), statusB = bench.Wait(jb);
            HeadlessHarness.Assert(statusA == "succeeded" && statusB == "succeeded", $"The range jobs ended {statusA} / {statusB}: {PgslCommands.JobError(ja)} {PgslCommands.JobError(jb)}");
            HeadlessHarness.Assert(PgslCommands.JobTake(jb) && PgslCommands.JobTake(ja), "JobTake refused: " + PgslCommands.JobLastError());
            string after = bench.Text("""
                var s = "";
                for (var i = 0; i < 10; i = i + 1) { s = s + StringFormatNumber(DsListGet(rl, i), 0); }
                s = s + " ";
                for (var i = 0; i < 10; i = i + 1) { s = s + StringFormatNumber(NumListGet(rn, i), 0); }
                s = s + " ";
                for (var gx = 0; gx < 8; gx = gx + 1) { s = s + StringFormatNumber(DsGridGet(rg, gx, 0), 0); }
                r = s;
                """);
            PgslCommands.JobRelease(ja); PgslCommands.JobRelease(jb);
            HeadlessHarness.Assert(after == "1110022200 1110022200 11000011",
                $"Only each job's ranges and rectangles should come back (got \"{after}\", wanted \"1110022200 1110022200 11000011\").");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AnUrgentJobGoesBeforeOnesWaiting", () =>
        {
            using Bench bench = new();
            int workers = NativeJobPool.ScriptWorkerCount;
            // Every worker held, so both jobs queue; then let go. (Not disposed: a worker may still be
            // leaving its wait, and one left waiting would hold a worker for good, hence the finally.)
            var release = new ManualResetEventSlim(false);
            var holding = new CountdownEvent(workers);
            bool held;
            double normal, urgent;
            try
            {
                NativeJobPool.HoldWorkers(release, holding);
                held = holding.Wait(TimeSpan.FromSeconds(20));
                normal = bench.Number("""r = JobRunScript("WjSpin", 10);""");
                urgent = bench.Number("""ju = JobScriptCreate("WjSpin"); JobScriptUrgent(ju, true); JobScriptStart(ju, 10); r = ju;""");
            }
            finally { release.Set(); }
            bool inOrder = held;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds < 20)
            {
                // The normal job first: once it has left the queue the urgent one must have left before it.
                // Workers let go together can take both at once, and the one with the urgent job may be
                // paused before it marks it running, so it gets a moment to show it has left too.
                string normalState = PgslCommands.JobStatus(normal);
                string urgentState = PgslCommands.JobStatus(urgent);
                if (normalState != "queued" && urgentState == "queued")
                {
                    long seen = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (PgslCommands.JobStatus(urgent) == "queued" && System.Diagnostics.Stopwatch.GetElapsedTime(seen).TotalMilliseconds < 250)
                        Thread.Yield();
                    if (PgslCommands.JobStatus(urgent) == "queued") inOrder = false;
                    break;
                }
                if (normalState != "queued") break;
                Thread.Yield();
            }
            string urgentEnd = bench.Wait(urgent), normalEnd = bench.Wait(normal);
            PgslCommands.JobRelease(normal); PgslCommands.JobRelease(urgent);
            row("Jobs: urgent", "an urgent job is taken before normal ones waiting", inOrder ? "PASS" : "FAIL", $"{workers} workers kept busy; urgent {urgentEnd}, normal {normalEnd}");
            HeadlessHarness.Assert(inOrder && urgentEnd == "succeeded" && normalEnd == "succeeded",
                $"The urgent job should leave the queue before the normal one started earlier (in order {inOrder}; every one of the {workers} workers held {held}; {urgentEnd}, {normalEnd}).");
        });

        // A grid lent in part is copied on the worker as the job starts; a game write before then makes
        // the copy first, so the job still reads the grid as it was when it started.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.ALentGridIsCopiedOffTheFrameAsItWas", () =>
        {
            using Bench bench = new();
            var release = new ManualResetEventSlim(false);
            var holding = new CountdownEvent(NativeJobPool.ScriptWorkerCount);
            double job;
            bool held;
            try
            {
                NativeJobPool.HoldWorkers(release, holding);
                held = holding.Wait(TimeSpan.FromSeconds(20));
                job = bench.Number("""
                    pg = DsGridCreate(8, 2, "u16"); DsGridSet(pg, 7, 0, 3);
                    pj = JobScriptCreate("ScGridPeek"); JobScriptGridRegion(pj, pg, 0, 0, 3, 0, true); JobScriptStart(pj, pg);
                    DsGridSet(pg, 7, 0, 9); DsGridSet(pg, 1, 0, 4);
                    r = pj;
                    """);
            }
            finally { release.Set(); }
            string status = bench.Wait(job);
            double seen = PgslCommands.JobResultNumber(job);
            bool taken = PgslCommands.JobTake(job);
            string cells = bench.Text("r = StringFormatNumber(DsGridGet(pg, 0, 0), 0) + StringFormatNumber(DsGridGet(pg, 1, 0), 0) + StringFormatNumber(DsGridGet(pg, 7, 0), 0);");
            PgslCommands.JobRelease(job);
            // The job saw 3 (not the game's later 9); its rectangle came back (0,0 = 5, and 1,0 is the job's copy's 0,
            // as the rectangle replaces the game's cells), the cell outside kept the game's 9.
            HeadlessHarness.Assert(held && status == "succeeded" && seen == 3 && taken && cells == "509",
                $"The job should read the grid as at its start and give back only its rectangle (held {held}, {status}, saw {seen}, cells '{cells}').");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Runtime.FrameBudget.ReportsEachPartAgainstItsBudget", () =>
        {
            bool wasEnabled = FrameBudget.Enabled;
            double frameBudget = FrameBudget.FrameMilliseconds;
            try
            {
                FrameBudget.Reset();
                FrameBudget.Enabled = true;
                FrameBudget.FrameMilliseconds = 1000;
                FrameBudget.SetBudget("physics step", 3);
                var parts = new SceneWorkTimes();
                long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);
                FrameBudget.FrameEnded(parts); // the first frame only starts the clock
                for (int frame = 0; frame < 10; frame++)
                {
                    long now = Stopwatch.GetTimestamp();
                    parts.Add("physics step", now - Ms(frame < 4 ? 5 : 1));
                    parts.Add("Terrain update", now - Ms(2));
                    FrameBudget.FrameEnded(parts);
                    parts.Clear();
                }
                string report = FrameBudget.Report(10);
                // Real clock readings: a busy machine adds a little to each, so the averages are checked to 0.3 ms.
                double physics = FrameBudget.AverageMilliseconds("physics step");
                double terrain = FrameBudget.AverageMilliseconds("Terrain update");
                string[] lines = report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string physicsLine = lines.FirstOrDefault(line => line.StartsWith("physics step |", StringComparison.Ordinal)) ?? "";
                HeadlessHarness.Assert(FrameBudget.Frames == 10 && lines[0].StartsWith("frames 10 |", StringComparison.Ordinal)
                    && Math.Abs(physics - 2.6) < 0.3 && Math.Abs(terrain - 2) < 0.3
                    && lines[2].StartsWith("physics step |", StringComparison.Ordinal) && physicsLine.EndsWith("| 3.00 | 4", StringComparison.Ordinal),
                    $"The report should count 10 frames, physics 2.6 ms on average and over its 3 ms budget 4 times:\n{report}");
            }
            finally
            {
                FrameBudget.Reset();
                FrameBudget.SetBudget("physics step", 0);
                FrameBudget.FrameMilliseconds = frameBudget;
                FrameBudget.Enabled = wasEnabled;
            }
        });
    }
}

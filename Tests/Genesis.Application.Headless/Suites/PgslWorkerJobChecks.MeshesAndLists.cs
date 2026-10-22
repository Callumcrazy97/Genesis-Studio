using System.Globalization;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Number lists (unboxed, ranged), a mesh built in a job and handed over finished from pooled arrays,
/// a job that waits for another and reads what it made, and the caches a worker keeps between jobs.
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    private const string MeshLibrary = """
        // A chunk's top faces, built straight into a mesh lent to the job.
        function MjBuildTops(m, n) {
            for (var i = 0; i < n; i = i + 1) {
                MeshAddQuadColors(m, i, 1, 0, i + 1, 1, 0, i + 1, 1, 1, i, 1, 1, 0, 1, 0, 0, 0, 1, 1,
                    200, 200, 200, 1, 190, 190, 190, 1, 180, 180, 180, 1, 170, 170, 170, 1, i % 2);
            }
            MeshAddCube(m, 0, 5, 0, 1, 63, 255, 255, 255, 0, 0, 1, 1);
            return MeshVertexCount(m);
        }
        // Heights into a number list (the first job), then their sum read by the job waiting for it.
        function MjHeights(heights, n) {
            NumListClear(heights);
            for (var i = 0; i < n; i = i + 1) { NumListAdd(heights, i * 2); }
            return NumListSize(heights);
        }
        function MjSumHeights(heights) { return NumListSum(heights, 0, NumListSize(heights)); }
        // A table a worker fills once and reads on later jobs.
        function MjCached(n) {
            var table = JobCacheNumList("squares", n);
            if (NumListGet(table, n - 1) == 0) { for (var i = 0; i < n; i = i + 1) { NumListSet(table, i, i * i); } JobCacheSet("fills", JobCacheGet("fills") + 1); }
            return NumListGet(table, n - 1);
        }
        """;

    private static void RunMeshesAndLists(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        ScriptAssetRegistry.Register("MeshJobLibrary", MeshLibrary);

        // Numbers kept as numbers, worked on a range at a time.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.NumberListsWorkOnRangesWithoutBoxing", () =>
        {
            using Bench bench = new();
            string result = bench.Text("""
                a = NumListCreate(); b = NumListCreate();
                for (var i = 0; i < 10; i = i + 1) { NumListAdd(a, 10 - i); }
                NumListFill(b, 0, 4, 7);
                NumListCopy(b, 2, a, 0, 3);
                NumListAddRange(b, a, 8, 5);
                NumListScale(a, 0, 3, 2, 1);
                NumListSort(a, 0, 10, true);
                s = NumListSum(b, 0, 100); mn = NumListMin(a, 0, 10); mx = NumListMax(a, 0, 10);
                l = DsListCreate(); NumListToList(b, l);
                r = StringFormatNumber(NumListSize(b), 0) + " " + StringFormatNumber(s, 0) + " " + StringFormatNumber(mn, 0) + " " + StringFormatNumber(mx, 0)
                    + " " + StringFormatNumber(NumListGet(b, 2), 0) + " " + StringFormatNumber(DsListSize(l), 0) + " " + StringFormatNumber(NumListIndexOf(a, 21), 0);
                """);
            // b: four 7s, a's first three (10 9 8) copied in at 2, then a's last two (2 1) added: 7 7 10 9 8 2 1.
            const string expected = "7 44 1 21 10 7 9";
            row("Number lists", "fill, copy, add a range, scale, sort, sum, min, max, to a list", result == expected ? "PASS" : "FAIL", result);
            HeadlessHarness.Assert(result == expected, $"Number list results were '{result}', expected '{expected}'.");
        });

        // A job builds a mesh lent to it and JobTake hands the finished arrays over: the same vertices
        // as building it on the game's thread, and a rebuild reuses pooled arrays.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AMeshBuiltInAJobIsHandedOverFinished", () =>
        {
            using Bench bench = new();
            double direct = bench.Number("md = MeshCreate(); r = MjBuildTops(md, 40);");
            ScriptMeshes.TryGetVertices((int)bench.Number("r = md;"), out MeshVertex[] directVertices, out ushort[] directIndices);
            long reusedBefore = MeshArrayPool.Reused;
            double total = 0;
            for (int round = 0; round < 3; round++)
            {
                double job = bench.Number("""
                    if (round == 0) { mj = MeshCreate(); }
                    jm = JobScriptCreate("MjBuildTops"); JobScriptMesh(jm, mj);
                    r = JobScriptStart(jm, mj, 40) ? jm : -1;
                    """.Replace("round == 0", round == 0 ? "1 == 1" : "1 == 0", StringComparison.Ordinal));
                HeadlessHarness.Assert(job > 0, "The mesh job did not start: " + PgslCommands.JobLastError());
                string status = bench.Wait(job);
                HeadlessHarness.Assert(status == "succeeded", $"The mesh job ended {status}: {PgslCommands.JobError(job)}");
                total = PgslCommands.JobResultNumber(job);
                HeadlessHarness.Assert(PgslCommands.JobTake(job), "JobTake refused the mesh job: " + PgslCommands.JobLastError());
                PgslCommands.JobRelease(job);
            }
            int mesh = (int)bench.Number("r = mj;");
            ScriptMeshes.TryGetVertices(mesh, out MeshVertex[] jobVertices, out ushort[] jobIndices);
            bool same = total == direct && jobVertices.AsSpan().SequenceEqual(directVertices) && jobIndices.AsSpan().SequenceEqual(directIndices);
            long reused = MeshArrayPool.Reused - reusedBefore;
            bool notUploaded = !ScriptMeshes.IsUploaded(mesh);
            bench.Run("MeshDestroy(md); MeshDestroy(mj); r = 0;");
            row("Jobs: meshes", "a mesh built in a job (JobScriptMesh) equals the game-thread build; rebuilt three times", same && reused > 0 ? "PASS" : "FAIL",
                $"{jobVertices.Length} vertices, {jobIndices.Length / 3} triangles; pooled arrays reused {reused} times; waits for upload: {notUploaded}");
            HeadlessHarness.Assert(same, $"The job's mesh differs: {jobVertices.Length} vertices / {jobIndices.Length} indices against {directVertices.Length} / {directIndices.Length}, result {total} against {direct}.");
            HeadlessHarness.Assert(reused > 0, "Rebuilding a mesh in jobs never reused a pooled array.");
            HeadlessHarness.Assert(notUploaded, "A handed-over mesh read as uploaded before it was drawn.");

            // A mesh not lent to the job is an error, not the game's mesh written from a worker.
            double stray = bench.Number("""ms = MeshCreate(); sj = JobRunScript("MjBuildTops", ms, 2); r = sj;""");
            string strayStatus = bench.Wait(stray);
            HeadlessHarness.Assert(strayStatus == "failed" && PgslCommands.JobError(stray).Contains("was not given to this job", StringComparison.Ordinal),
                $"A job writing a mesh not lent to it ended {strayStatus}: {PgslCommands.JobError(stray)}");
            PgslCommands.JobRelease(stray);
            bench.Run("MeshDestroy(ms); r = 0;");
        });

        // A job that waits for another starts only when it has finished, and reads what it made.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AJobWaitsForAnotherAndReadsWhatItMade", () =>
        {
            using Bench bench = new();
            double second = bench.Number("""
                hs = NumListCreate();
                first = JobScriptCreate("MjHeights"); JobScriptNumList(first, hs, true);
                sum = JobScriptCreate("MjSumHeights"); JobScriptNumList(sum, hs, false); JobScriptAfter(sum, first);
                JobScriptStart(sum, hs);
                waitingState = JobStatus(sum);
                JobScriptStart(first, hs, 50);
                r = sum;
                """);
            string waiting = bench.Text("r = waitingState;");
            string status = bench.Wait(second);
            double result = PgslCommands.JobResultNumber(second);
            double first = bench.Number("r = first;");
            bench.Wait(first);
            // 0 + 2 + ... + 98: the first job's heights, which the game's list never held.
            bool pass = waiting == "waiting" && status == "succeeded" && result == 2450 && PgslCommands.NumListSize(bench.Number("r = hs;")) == 0;
            row("Jobs: dependencies", "JobScriptAfter: waits, then reads the first job's list", pass ? "PASS" : "FAIL",
                $"state before the first started: {waiting}; ended {status}; sum {result}");
            HeadlessHarness.Assert(pass, $"Waiting job: '{waiting}' before, {status} after, sum {result} (expected 2450), game's list size {PgslCommands.NumListSize(bench.Number("r = hs;"))}.");
            PgslCommands.JobRelease(second); PgslCommands.JobRelease(first);
        });

        // What a worker keeps between jobs: filled once on each worker, read again afterwards.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.WorkersKeepTheirCachesBetweenJobs", () =>
        {
            using Bench bench = new();
            PgslCommands.JobCacheReset();
            double[] results = new double[24];
            for (int i = 0; i < results.Length; i++)
            {
                double job = bench.Number("""r = JobRunScript("MjCached", 300);""");
                HeadlessHarness.Assert(job > 0, "A cache job did not start: " + PgslCommands.JobLastError());
                HeadlessHarness.Assert(bench.Wait(job) == "succeeded", "A cache job failed: " + PgslCommands.JobError(job));
                results[i] = PgslCommands.JobResultNumber(job);
                PgslCommands.JobRelease(job);
            }
            bool right = results.All(value => value == 299 * 299);
            row("Jobs: worker caches", "a table filled once per worker and read by later jobs", right ? "PASS" : "FAIL",
                string.Join(",", results.Distinct().Select(v => v.ToString(CultureInfo.InvariantCulture))));
            HeadlessHarness.Assert(right, "A cached table gave a wrong value: " + string.Join(",", results));
        });
    }
}

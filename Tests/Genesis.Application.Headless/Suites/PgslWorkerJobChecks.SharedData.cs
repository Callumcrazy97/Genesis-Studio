using System.Diagnostics;
using System.Globalization;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Worker jobs reading the game's own data: a job given every structure and instance variable of
/// its Object (JobScriptShareAll) or single ones (JobScriptGrid / JobScriptList / JobScriptMap) sees
/// them as they were when it started, shared rather than copied. A chunk mesher written the way a
/// voxel game writes it (blocks in strip grids found by handle arithmetic, light grids, flag and tint
/// tables, a list of lists, a facing map, globals) gives the same faces in a job as called directly;
/// the game's changes during a job never reach it; a job's own changes never reach the game except
/// through JobTake; and a structure is copied only when one side changes it while a job holds it.
/// </summary>
internal static partial class PgslWorkerJobChecks
{
    private const string SharedLibrary = """
        // A block world kept as a voxel game keeps it: 64 x 64 columns sjH high in four grids of
        // 16-block strips (block (x, y, z) in grid sjBase + ((z >> 4) & 3), column (x & 63) + ((z & 15) << 6),
        // row y), light in four more (sjLight + strip), flag and tint tables, each kind's six tiles in a
        // list of lists, a facing map keyed "x,y,z", and globals.
        function SjGet(bx, by, bz) {
            if (by < 0) { return 1; }
            if (by >= sjH || bx < 0 || bx >= 64 || bz < 0 || bz >= 64) { return 0; }
            return DsGridGet(sjBase + ((bz >> 4) & 3), (bx & 63) + ((bz & 15) << 6), by);
        }
        function SjLightAt(bx, by, bz) {
            if (by < 0 || by >= sjH || bx < 0 || bx >= 64 || bz < 0 || bz >= 64) { return 15; }
            return DsGridGet(sjLight + ((bz >> 4) & 3), (bx & 63) + ((bz & 15) << 6), by);
        }
        function SjBuildWorld(seed) {
            for (var bz = 0; bz < 64; bz = bz + 1) {
                for (var bx = 0; bx < 64; bx = bx + 1) {
                    var h = Floor(12 + 10 * FractalNoise2D(bx / 20, bz / 20, seed, 3, 2, 0.5));
                    var g = (bz >> 4) & 3; var c = (bx & 63) + ((bz & 15) << 6);
                    for (var y = 0; y < sjH; y = y + 1) {
                        var k = 0;
                        if (y < h - 3) { k = 1; } else if (y < h) { k = 2; } else if (y == h) { k = 3; } else if (y <= sjSea) { k = 4; }
                        if (k == 3 && (bx * 7 + bz * 13) % 17 == 0) { k = 5; DsMapSet(sjFacing, StringOf(bx) + "," + StringOf(y) + "," + StringOf(bz), (bx + bz) % 4); }
                        DsGridSet(sjBase + g, c, y, k);
                        var light = 15; if (y < h) { light = Max(0, 15 - (h - y) * 3); }
                        DsGridSet(sjLight + g, c, y, light);
                    }
                }
            }
            return 1;
        }
        // One face for MeshAddQuadsFromList: 36 numbers (four corners, normal, uv rectangle, four colours, flip).
        function SjQuad(faces, bx, by, bz, dir, tile, tint, light) {
            var nx = 0; var ny = 0; var nz = 0;
            if (dir == 0) { nx = 1; } else if (dir == 1) { nx = -1; } else if (dir == 2) { ny = 1; } else if (dir == 3) { ny = -1; } else if (dir == 4) { nz = 1; } else { nz = -1; }
            var cx = bx + 0.5 + nx * 0.5; var cy = by + 0.5 + ny * 0.5; var cz = bz + 0.5 + nz * 0.5;
            var ax = 0; var az = 0; var uy = 0; var uz = 0;
            if (nx != 0) { az = 0.5; uy = 0.5; } else if (ny != 0) { ax = 0.5; uz = 0.5; } else { ax = 0.5; uy = 0.5; }
            DsListAdd(faces, cx - ax); DsListAdd(faces, cy - uy); DsListAdd(faces, cz - az - uz);
            DsListAdd(faces, cx + ax); DsListAdd(faces, cy - uy); DsListAdd(faces, cz + az - uz);
            DsListAdd(faces, cx + ax); DsListAdd(faces, cy + uy); DsListAdd(faces, cz + az + uz);
            DsListAdd(faces, cx - ax); DsListAdd(faces, cy + uy); DsListAdd(faces, cz - az + uz);
            DsListAdd(faces, nx); DsListAdd(faces, ny); DsListAdd(faces, nz);
            var u0 = (tile % 16) / 16; var v0 = Floor(tile / 16) / sjAtlasRows;
            DsListAdd(faces, u0); DsListAdd(faces, v0); DsListAdd(faces, u0 + 1 / 16); DsListAdd(faces, v0 + 1 / sjAtlasRows);
            for (var k = 0; k < 4; k = k + 1) { DsListAdd(faces, tint * 16); DsListAdd(faces, 255 - k * 8); DsListAdd(faces, light * 17); DsListAdd(faces, 1); }
            DsListAdd(faces, (bx + bz) % 2);
            return 1;
        }
        // The visible faces of chunk (cx, cz), 16 x 16 columns, into a list: what a game's mesher works out.
        function SjMeshChunk(cx, cz, faces) {
            DsListClear(faces);
            var n = 0;
            for (var lz = 0; lz < 16; lz = lz + 1) {
                for (var lx = 0; lx < 16; lx = lx + 1) {
                    var bx = cx * 16 + lx; var bz = cz * 16 + lz;
                    for (var y = 0; y < sjH; y = y + 1) {
                        var k = SjGet(bx, y, bz);
                        if (k > 0) {
                            var tiles = DsListGet(sjTiles, k);
                            var turn = 0;
                            if ((DsListGet(sjFlags, k) & 16) != 0) {
                                var key = StringOf(bx) + "," + StringOf(y) + "," + StringOf(bz);
                                if (DsMapExists(sjFacing, key)) { turn = DsMapGet(sjFacing, key); }
                            }
                            for (var dir = 0; dir < 6; dir = dir + 1) {
                                var ox = bx; var oy = y; var oz = bz;
                                if (dir == 0) { ox = bx + 1; } else if (dir == 1) { ox = bx - 1; } else if (dir == 2) { oy = y + 1; } else if (dir == 3) { oy = y - 1; } else if (dir == 4) { oz = bz + 1; } else { oz = bz - 1; }
                                var o = SjGet(ox, oy, oz);
                                if (o != k && (DsListGet(sjFlags, o) & 2) == 0) {
                                    n = n + SjQuad(faces, bx, y, bz, dir, DsListGet(tiles, (dir + turn) % 6), DsListGet(sjTint, k), SjLightAt(ox, oy, oz));
                                }
                            }
                        }
                    }
                }
            }
            return n * 1000 + StringLength(sjAtlasName);
        }
        // Everything the world holds, summed; the same each round unless something changed it meanwhile.
        function SjChecksumRound() {
            var s = 0;
            for (var g = 0; g < 4; g = g + 1) { s = s + DsGridGetSum(sjBase + g, 0, 0, 1023, sjH - 1) * (g + 1) + DsGridGetSum(sjLight + g, 0, 0, 1023, sjH - 1); }
            var n = DsListSize(sjLog);
            for (var i = 0; i < n; i = i + 1) { s = s + DsListGet(sjLog, i) * (i % 7 + 1); }
            return s + n * 1000 + DsMapGet(sjFacing, "0,0,0") * 5 + DsMapSize(sjFacing) * 3 + sjSea * 11;
        }
        // The first round's checksum, or -1 when a later round differed (a change by the game reached the job).
        function SjChecksumRounds(rounds) {
            var first = SjChecksumRound();
            for (var r = 1; r < rounds; r = r + 1) { if (SjChecksumRound() != first) { return -1; } }
            return first;
        }
        // Changes a job makes to the game's structures without copyBack: its own from then on.
        function SjScribble() {
            DsGridSet(sjBase, 5, 5, 99); DsListAdd(sjLog, 12345); DsMapSet(sjFacing, "scribble", 1); DsGridClear(sjLight, 7);
            return DsGridGet(sjBase, 5, 5) + DsListGet(sjLog, DsListSize(sjLog) - 1) + DsMapGet(sjFacing, "scribble") + DsGridGet(sjLight, 0, 0);
        }
        // Compact grids (DsGridCreate's kind) in a job: read as given, changed as the job's own.
        function SjCompactWork(blocks, light) {
            var s = DsGridGetSum(blocks, 0, 0, DsGridWidth(blocks) - 1, DsGridHeight(blocks) - 1) + DsGridGetSum(light, 0, 0, DsGridWidth(light) - 1, DsGridHeight(light) - 1);
            DsGridSet(blocks, 0, 0, 70000); DsGridSet(light, 0, 0, 3);
            var kinds = 0; if (DsGridKind(blocks) == "u16") { kinds = kinds + 1; } if (DsGridKind(light) == "u8") { kinds = kinds + 2; }
            return s * 10 + kinds;
        }
        // A* over a 32 x 32 grid of walls (sjWalls, 1 = wall) with the open set in a priority queue
        // the job was given (pq, emptied first): the length of the shortest path from (0, 0) to
        // (31, 31), -1 when there is none. Leaves a marker entry in the queue for copyBack.
        function SjAStar(pq) {
            DsPriorityClear(pq);
            var best = DsGridCreate(32, 32); DsGridClear(best, 999999);
            DsGridSet(best, 0, 0, 0); DsPriorityAdd(pq, 0, 62);
            var found = -1;
            while (DsPriorityEmpty(pq) == 0 && found < 0) {
                var node = DsPriorityDeleteMin(pq);
                var nx = node % 32; var ny = Floor(node / 32); var g = DsGridGet(best, nx, ny);
                if (nx == 31 && ny == 31) { found = g; }
                for (var d = 0; d < 4; d = d + 1) {
                    var mx = nx; var my = ny;
                    if (d == 0) { mx = nx + 1; } else if (d == 1) { mx = nx - 1; } else if (d == 2) { my = ny + 1; } else { my = ny - 1; }
                    if (mx >= 0 && my >= 0 && mx < 32 && my < 32 && DsGridGet(sjWalls, mx, my) == 0 && g + 1 < DsGridGet(best, mx, my)) {
                        DsGridSet(best, mx, my, g + 1);
                        DsPriorityAdd(pq, mx + my * 32, g + 1 + (31 - mx) + (31 - my));
                    }
                }
            }
            DsPriorityClear(pq); DsPriorityAddString(pq, "done", -1);
            return found;
        }
        function SjMapEdit(m) { DsMapSet(m, "a", 5); DsMapDelete(m, "b"); return DsMapSize(m); }
        function SjLeaveAlone(l) { return DsListSize(l); }
        function SjNothing() { return 1; }
        function SjSpinLong(n) { var k = 0; while (k < n) { k = k + 1; } return k; }
        """;

    // The world's tables and globals, then the world itself (on the game's thread, in one call: the
    // per-call limit is raised as a game generating its world in one go would raise it).
    private const string SharedWorld = """
        ScriptInstructionLimit(50000000);
        sjH = 32; sjSea = 9; sjAtlasRows = 8; sjAtlasName = "Blocks";
        sjBase = DsGridCreate(1024, sjH); DsGridCreate(1024, sjH); DsGridCreate(1024, sjH); DsGridCreate(1024, sjH);
        sjLight = DsGridCreate(1024, sjH); DsGridCreate(1024, sjH); DsGridCreate(1024, sjH); DsGridCreate(1024, sjH);
        sjFlags = DsListCreate(); DsListAdd(sjFlags, 0); DsListAdd(sjFlags, 3); DsListAdd(sjFlags, 3); DsListAdd(sjFlags, 3); DsListAdd(sjFlags, 4); DsListAdd(sjFlags, 19);
        sjTint = DsListCreate(); DsListAdd(sjTint, 0); DsListAdd(sjTint, 0); DsListAdd(sjTint, 0); DsListAdd(sjTint, 1); DsListAdd(sjTint, 2); DsListAdd(sjTint, 0);
        sjTiles = DsListCreate();
        for (k = 0; k < 6; k = k + 1) { t = DsListCreate(); for (d = 0; d < 6; d = d + 1) { DsListAdd(t, k * 6 + d); } DsListAdd(sjTiles, t); }
        sjFacing = DsMapCreate(); DsMapSet(sjFacing, "0,0,0", 2);
        sjLog = DsListCreate(); for (k = 0; k < 400; k = k + 1) { DsListAdd(sjLog, k % 13); }
        r = SjBuildWorld(31);
        """;

    private static double[] ListEntries(double list)
    {
        double[] entries = new double[(int)PgslCommands.DsListSize(list)];
        for (int i = 0; i < entries.Length; i++) entries[i] = PgslCommands.DsListGet(list, i);
        return entries;
    }

    private static void RunSharedData(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        ScriptAssetRegistry.Register("SharedJobLibrary", SharedLibrary);
        try { RunSharedDataCases(ctx, row); }
        finally { PgslCommands.ScriptInstructionLimit(0); }
    }

    private static void RunSharedDataCases(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        // A call may run 100 000 instructions unless the game raises the limit; a lower number restores it.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.ScriptInstructionLimitRaisesThePerCallCap", () =>
        {
            using Bench bench = new();
            const string Long = "r = SjSpinLong(60000);";
            string Attempt()
            {
                try { bench.Run(Long); return "ran"; }
                catch (Exception error) { return error.Message; }
            }
            string before = Attempt();
            double raised = bench.Number("r = ScriptInstructionLimit(5000000);");
            string after = Attempt();
            double spun = bench.Number("r = r;");
            double restored = bench.Number("r = ScriptInstructionLimit(10);");
            string again = Attempt();
            bool pass = before.Contains("Maximum instruction limit (100000)", StringComparison.Ordinal) && before.Contains("ScriptInstructionLimit", StringComparison.Ordinal)
                && raised == 5_000_000 && after == "ran" && spun == 60000 && restored == 100_000 && again.Contains("(100000)", StringComparison.Ordinal);
            row("Limits", "ScriptInstructionLimit raises the per-call limit; a lower number restores 100 000", pass ? "PASS" : "FAIL",
                $"before: {before}; raised to {raised}: {after} ({spun}); restored to {restored}: {again}");
            HeadlessHarness.Assert(pass, $"Before: {before} / raised {raised}: {after} {spun} / restored {restored}: {again}");
        });

        // The worked example of the documentation: a chunk's faces worked out in a job from the
        // game's own grids, lists, map and globals, added on the game's thread in one call.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.ShareAllMeshesAChunkAsCallingTheFunction", () =>
        {
            using Bench bench = new();
            bench.Run(SharedWorld);
            double direct = bench.Number("facesDirect = DsListCreate(); r = SjMeshChunk(1, 2, facesDirect);");
            double[] directFaces = ListEntries(bench.Number("r = facesDirect;"));
            double job = bench.Number("""
                facesJob = DsListCreate();
                mj = JobScriptCreate("SjMeshChunk");
                JobScriptShareAll(mj); JobScriptList(mj, facesJob, true);
                r = JobScriptStart(mj, 1, 2, facesJob) ? mj : -1;
                """);
            HeadlessHarness.Assert(job > 0, "The mesh job did not start: " + PgslCommands.JobLastError());
            string status = bench.Wait(job);
            HeadlessHarness.Assert(status == "succeeded", $"The mesh job ended {status}: {PgslCommands.JobError(job)}");
            HeadlessHarness.Assert(PgslCommands.JobTake(job), "JobTake refused the mesh job: " + PgslCommands.JobLastError());
            double result = PgslCommands.JobResultNumber(job);
            PgslCommands.JobRelease(job);
            double[] jobFaces = ListEntries(bench.Number("r = facesJob;"));
            bool same = result == direct && jobFaces.SequenceEqual(directFaces) && directFaces.Length > 0;
            double quadsDirect = bench.Number("mDirect = MeshCreate(); r = MeshAddQuadsFromList(mDirect, facesDirect);");
            double quadsJob = bench.Number("mJob = MeshCreate(); r = MeshAddQuadsFromList(mJob, facesJob);");
            bench.Run("MeshDestroy(mDirect); MeshDestroy(mJob); r = 0;");
            row("Jobs: shared data", "a chunk mesher reading the game's grids, lists of lists, map and globals (JobScriptShareAll)", same && quadsJob == quadsDirect ? "PASS" : "FAIL",
                $"{directFaces.Length / 36} faces, result {result} (directly {direct}), {quadsJob} quads added from the job's list");
            HeadlessHarness.Assert(same, $"The job's faces differ from the direct call's: {jobFaces.Length} numbers against {directFaces.Length}, result {result} against {direct}.");
            HeadlessHarness.Assert(quadsJob == quadsDirect && quadsJob == directFaces.Length / 36, $"MeshAddQuadsFromList added {quadsJob} quads from the job's list and {quadsDirect} from the direct one.");

            // Without JobScriptShareAll the same function cannot find the game's globals: an error, not a wrong mesh.
            double bare = bench.Number("""bj = JobScriptCreate("SjMeshChunk"); JobScriptList(bj, facesJob, true); JobScriptStart(bj, 1, 2, facesJob); r = bj;""");
            string bareStatus = bench.Wait(bare);
            HeadlessHarness.Assert(bareStatus == "failed" && PgslCommands.JobError(bare).Contains("has no value in this job", StringComparison.Ordinal),
                $"A job not given the globals ended {bareStatus}: {PgslCommands.JobError(bare)}");
            PgslCommands.JobRelease(bare);
        });

        // The game changes everything a job reads while the job reads it: each job still sees the
        // data exactly as it was when it started (a torn read would give -1 or another checksum).
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.GameChangesNeverReachARunningJob", () =>
        {
            using Bench bench = new();
            bench.Run(SharedWorld);
            const string Start = """
                cj = JobScriptCreate("SjChecksumRounds"); JobScriptShareAll(cj);
                r = JobScriptStart(cj, 60) ? cj : -1;
                """;
            double Checksum() => bench.Number("r = SjChecksumRounds(1);");
            List<(double Job, double Expected)> jobs = [];
            double first = Checksum();
            jobs.Add((bench.Number(Start), first));
            int writes = 0, writesWhileRunning = 0;
            long started = Stopwatch.GetTimestamp();
            bool Running() => jobs.Any(entry => PgslCommands.JobStatus(entry.Job) is "queued" or "running");
            while (Running() || jobs.Count < 4)
            {
                bool running = Running();
                bench.Vm.SetVariable("w", (double)writes);
                bench.Run("""
                    DsGridSet(sjBase + (w % 4), (w * 37) % 1024, w % sjH, w % 6); DsGridSetRegion(sjLight + (w % 4), 0, w % sjH, 63, w % sjH, w % 16);
                    DsListSet(sjLog, w % 400, w); DsListAdd(sjLog, w % 11); DsMapSet(sjFacing, "0,0,0", w); DsMapSet(sjFacing, "w" + StringOf(w), 1);
                    sjSea = 9 + (w % 5); r = 0;
                    """);
                writes++;
                if (running) writesWhileRunning++;
                // More jobs started part-way, each expected to see the data as it is at its own start.
                if (writes % 25 == 0 && jobs.Count < 4)
                {
                    double expected = Checksum();
                    jobs.Add((bench.Number(Start), expected));
                }
                HeadlessHarness.Assert(Stopwatch.GetElapsedTime(started).TotalSeconds < 60, "The checksum jobs did not finish within a minute.");
            }
            int matched = 0;
            List<string> results = [];
            foreach ((double job, double expected) in jobs)
            {
                string status = bench.Wait(job);
                double result = PgslCommands.JobResultNumber(job);
                results.Add($"{status} {result} (expected {expected})");
                if (status == "succeeded" && result == expected) matched++;
                PgslCommands.JobRelease(job);
            }
            bool distinct = jobs.Select(entry => entry.Expected).Distinct().Count() == jobs.Count;
            row("Jobs: shared data", $"{jobs.Count} jobs reading grids, lists, a map and globals while the game wrote them {writes} times",
                matched == jobs.Count && distinct && writesWhileRunning > 0 ? "PASS" : "FAIL",
                $"{matched} saw exactly the data of their start; {writesWhileRunning} writes made while a job ran");
            HeadlessHarness.Assert(writesWhileRunning > 0, "No write happened while a job ran: the check proved nothing.");
            HeadlessHarness.Assert(distinct, "Two jobs were started on the same data: the writes did not change it.");
            HeadlessHarness.Assert(matched == jobs.Count, "A job saw a change the game made after it started: " + string.Join("; ", results));
            // The game's own writes are all there afterwards.
            int last = writes - 1;
            double lastLog = bench.Number("r = DsListGet(sjLog, DsListSize(sjLog) - 1);"), size = bench.Number("r = DsListSize(sjLog);");
            double facing = bench.Number("""r = DsMapGet(sjFacing, "0,0,0");"""), sea = bench.Number("r = sjSea;");
            HeadlessHarness.Assert(size == 400 + writes && lastLog == last % 11 && facing == last && sea == 9 + (last % 5),
                $"The game's own writes were not all kept: log size {size} (expected {400 + writes}), last entry {lastLog}, facing {facing}, sea {sea}.");
        });

        // A job's changes to what it was given stay its own; copyBack brings back only what it changed.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.AJobsChangesStayItsOwnUntilTaken", () =>
        {
            using Bench bench = new();
            bench.Run(SharedWorld);
            double before = bench.Number("r = SjChecksumRounds(1);");
            double job = bench.Number("""sc = JobScriptCreate("SjScribble"); JobScriptShareAll(sc); JobScriptStart(sc); r = sc;""");
            string status = bench.Wait(job);
            double seen = PgslCommands.JobResultNumber(job);
            PgslCommands.JobTake(job);
            PgslCommands.JobRelease(job);
            double after = bench.Number("r = SjChecksumRounds(1);");
            bool scribbled = bench.Number("""r = DsMapExists(sjFacing, "scribble");""") != 0;
            HeadlessHarness.Assert(status == "succeeded" && seen == 99 + 12345 + 1 + 7, $"The job ended {status} and saw {seen} of its own changes: {PgslCommands.JobError(job)}");
            HeadlessHarness.Assert(after == before && !scribbled, $"A job's changes reached the game without copyBack (checksum {after}, before {before}).");

            // A map given with copyBack comes back changed; a list given with copyBack but left alone
            // keeps what the game did to it meanwhile.
            double mapJob = bench.Number("""
                em = DsMapCreate(); DsMapSet(em, "b", 2); DsMapSet(em, "c", 3);
                el = DsListCreate(); DsListAdd(el, 1);
                mk = JobScriptCreate("SjMapEdit"); JobScriptMap(mk, em, true); JobScriptStart(mk, em);
                lk = JobScriptCreate("SjLeaveAlone"); JobScriptList(lk, el, true); JobScriptStart(lk, el);
                DsListAdd(el, 2);
                r = mk;
                """);
            double listJob = bench.Number("r = lk;");
            HeadlessHarness.Assert(bench.Wait(mapJob) == "succeeded" && bench.Wait(listJob) == "succeeded", $"The map or list job failed: {PgslCommands.JobError(mapJob)} {PgslCommands.JobError(listJob)}");
            double mapBefore = bench.Number("""r = DsMapExists(em, "b") * 10 + DsMapSize(em);""");
            PgslCommands.JobTake(mapJob); PgslCommands.JobTake(listJob);
            double mapAfter = bench.Number("""r = DsMapGet(em, "a") * 100 + DsMapExists(em, "b") * 10 + DsMapSize(em);""");
            double listAfter = bench.Number("r = DsListSize(el) * 10 + DsListGet(el, 1);");
            PgslCommands.JobRelease(mapJob); PgslCommands.JobRelease(listJob);
            row("Jobs: shared data", "a job's changes without copyBack stay its own; copyBack brings back a map it changed, not a list it left alone",
                mapBefore == 12 && mapAfter == 502 && listAfter == 22 ? "PASS" : "FAIL", $"map before JobTake {mapBefore}, after {mapAfter}; list {listAfter}");
            HeadlessHarness.Assert(mapBefore == 12 && mapAfter == 502, $"The map read {mapBefore} before JobTake and {mapAfter} after (expected 12 and 502).");
            HeadlessHarness.Assert(listAfter == 22, $"A copyBack list the job left alone lost the game's change: {listAfter} (expected 22).");
        });

        // Compact grids are shared with a job as number grids are: the job reads them as they were
        // at its start, the game's change while it runs makes the game's own copy (of the same kind),
        // and copyBack brings the job's changed grid back, still compact.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.CompactGridsAreSharedAndComeBack", () =>
        {
            using Bench bench = new();
            double job = bench.Number("""
                cb = DsGridCreate(64, 256, "u16"); cl = DsGridCreate(64, 256, "u8");
                DsGridSetRegion(cb, 0, 0, 63, 9, 3071); DsGridClear(cl, 15);
                cj = JobScriptCreate("SjCompactWork"); JobScriptGrid(cj, cb, true); JobScriptGrid(cj, cl, false); JobScriptBudget(cj, 50000000);
                r = JobScriptStart(cj, cb, cl) ? cj : -1;
                """);
            HeadlessHarness.Assert(job > 0, "The compact grid job did not start: " + PgslCommands.JobLastError());
            bool heldWhenChanged = PgslCommands.SharedHolders("grid", bench.Number("r = cl;")) > 0;
            bench.Run("DsGridSet(cl, 1, 1, 300); r = 0;");
            string status = bench.Wait(job);
            double result = PgslCommands.JobResultNumber(job);
            HeadlessHarness.Assert(status == "succeeded", $"The compact grid job ended {status}: {PgslCommands.JobError(job)}");
            HeadlessHarness.Assert(PgslCommands.JobTake(job), "JobTake refused the compact grid job: " + PgslCommands.JobLastError());
            PgslCommands.JobRelease(job);
            const double Expected = ((64 * 10 * 3071.0) + (64 * 256 * 15.0)) * 10 + 3;
            double blocks = bench.Number("r = DsGridGet(cb, 0, 0) * 10 + ((DsGridKind(cb) == \"u16\") ? 1 : 0);");
            double light = bench.Number("r = DsGridGet(cl, 0, 0) * 1000 + DsGridGet(cl, 1, 1) * 10 + ((DsGridKind(cl) == \"u8\") ? 1 : 0);");
            bool pass = result == Expected && blocks == 655351 && light == 17551;
            row("Jobs: shared data", "u16 and u8 grids given to a job: read as at its start, the game's change to one stays the game's, copyBack brings the job's u16 grid back",
                pass ? "PASS" : "FAIL", $"job result {result} (expected {Expected}); blocks after JobTake {blocks}, light {light}"
                + (heldWhenChanged ? "; the game changed the light grid while the job held it" : "; the job had let go of the light grid before the game changed it"));
            HeadlessHarness.Assert(result == Expected, $"The job read {result} from the compact grids, not {Expected}.");
            HeadlessHarness.Assert(blocks == 655351, $"After JobTake the u16 grid reads {blocks} (cell * 10 + is u16), not 655351.");
            HeadlessHarness.Assert(light == 17551, $"The u8 grid (no copyBack) reads {light} (cell(0,0) * 1000 + cell(1,1) * 10 + is u8), not 17551.");
        });

        // A* with its open set in a priority queue, run in a job: the queue is shared like a list
        // (the game's own entries and its change meanwhile stay the game's), and copyBack brings the
        // job's queue back.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.APriorityQueueSearchRunsInAJob", () =>
        {
            using Bench bench = new();
            double job = bench.Number("""
                walls = DsGridCreate(32, 32, "u8");
                DsGridSetRegion(walls, 10, 0, 10, 30, 1); DsGridSetRegion(walls, 20, 1, 20, 31, 1);
                open = DsPriorityCreate(); DsPriorityAdd(open, 5, 5); DsPriorityAdd(open, 6, 1);
                aj = JobScriptCreate("SjAStar"); JobScriptGrid(aj, walls, false); JobScriptPriority(aj, open, true);
                JobScriptVariable(aj, "sjWalls", walls);
                r = JobScriptStart(aj, open) ? aj : -1;
                """);
            HeadlessHarness.Assert(job > 0, "The A* job did not start: " + PgslCommands.JobLastError());
            double held = bench.Number("r = DsPrioritySize(open) * 100 + DsPriorityFindMin(open); DsPriorityAdd(open, 7, 0);");
            double afterChange = bench.Number("r = DsPriorityFindMin(open) * 100 + DsPrioritySize(open);");
            string status = bench.Wait(job);
            double length = PgslCommands.JobResultNumber(job);
            HeadlessHarness.Assert(status == "succeeded", $"The A* job ended {status}: {PgslCommands.JobError(job)}");
            HeadlessHarness.Assert(PgslCommands.JobTake(job), "JobTake refused the A* job: " + PgslCommands.JobLastError());
            PgslCommands.JobRelease(job);
            string back = bench.Text("r = DsPriorityFindMinString(open) + \",\" + StringOf(DsPrioritySize(open));");
            bool pass = length == 124 && held == 206 && afterChange == 703 && back == "done,1";
            row("Jobs: shared data", "A* in a job with its open set in a priority queue (JobScriptPriority, copyBack)", pass ? "PASS" : "FAIL",
                $"path length {length} (expected 124); the game's queue while held {held} (206), after its own change {afterChange} (703); after JobTake \"{back}\" (\"done,1\")");
            HeadlessHarness.Assert(length == 124, $"A* in the job found a path of {length}, not 124.");
            HeadlessHarness.Assert(held == 206 && afterChange == 703, $"The game's own queue changed under a job: {held} (206) / {afterChange} (703).");
            HeadlessHarness.Assert(back == "done,1", $"copyBack did not bring the job's queue back: \"{back}\".");
        });

        // Copies only when one side changes a structure a job holds: the game's first change of a held
        // grid or list copies it once, later changes and changes after the job has ended copy nothing.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.CopiedOnlyWhenChangedWhileHeld", () =>
        {
            using Bench bench = new();
            bench.Run(SharedWorld);
            double grid = bench.Number("r = sjBase;"), other = bench.Number("r = sjBase + 1;"), log = bench.Number("r = sjLog;");
            long copies = PgslCommands.SharedCopies;
            double job = bench.Number("""hj = JobScriptCreate("SjChecksumRounds"); JobScriptShareAll(hj); JobScriptStart(hj, 400); r = hj;""");
            int heldWhileRunning = PgslCommands.SharedHolders("grid", other);
            bench.Run("DsGridSet(sjBase, 1, 1, 9); DsGridSet(sjBase, 2, 2, 9); DsListAdd(sjLog, 1); DsListAdd(sjLog, 2); r = DsGridGet(sjBase + 1, 0, 0);");
            long copiedWhileHeld = PgslCommands.SharedCopies - copies;
            bool stillRunning = PgslCommands.JobStatus(job) is "queued" or "running";
            string status = bench.Wait(job);
            int heldAfter = PgslCommands.SharedHolders("grid", other);
            long beforeLate = PgslCommands.SharedCopies;
            bench.Run("DsGridSet(sjBase + 1, 3, 3, 9); DsGridSet(sjBase + 2, 3, 3, 9); r = 0;");
            long copiedAfter = PgslCommands.SharedCopies - beforeLate;
            PgslCommands.JobRelease(job);

            // A job cancelled before it ran lets go of its data too.
            double queued = bench.Number("""qj = JobScriptCreate("SjNothing"); JobScriptShareAll(qj); JobScriptStart(qj); JobCancel(qj); r = qj;""");
            bench.Wait(queued);
            long settle = Stopwatch.GetTimestamp();
            while (PgslCommands.SharedHolders("list", log) > 0 && Stopwatch.GetElapsedTime(settle).TotalSeconds < 5) Thread.Sleep(1);
            int heldAfterCancel = PgslCommands.SharedHolders("list", log);
            PgslCommands.JobRelease(queued);

            bool pass = status == "succeeded" && heldWhileRunning == 1 && heldAfter == 0 && copiedAfter == 0 && heldAfterCancel == 0
                && (!stillRunning || copiedWhileHeld == 2);
            row("Jobs: shared data", "copied only when changed while a job holds it", pass ? "PASS" : "FAIL",
                $"held by {heldWhileRunning} job while it ran, {heldAfter} after; the game's two grid and two list changes during the job made {copiedWhileHeld} copies"
                + (stillRunning ? "" : " (the job had ended already)") + $", changes after it {copiedAfter}; a cancelled job's holds {heldAfterCancel}");
            HeadlessHarness.Assert(status == "succeeded" && PgslCommands.JobResultNumber(job) != -1, $"The held job ended {status}: {PgslCommands.JobError(job)}");
            HeadlessHarness.Assert(heldWhileRunning == 1 && heldAfter == 0, $"A grid was held by {heldWhileRunning} jobs while one ran and {heldAfter} after it ended.");
            HeadlessHarness.Assert(!stillRunning || copiedWhileHeld == 2, $"Changing one grid and one list while a job held them made {copiedWhileHeld} copies, not 2.");
            HeadlessHarness.Assert(copiedAfter == 0, $"Changing grids after the job ended made {copiedAfter} copies.");
            HeadlessHarness.Assert(heldAfterCancel == 0, $"A job cancelled before it ran still holds its data ({heldAfterCancel}).");
            _ = grid;
        });

        // What giving a job the data of a game the size of a voxel world costs the game's thread.
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.Jobs.ShareAllCostAtAVoxelGamesSize", () =>
        {
            using Bench bench = new();
            // As GenesisCraft holds its world: 32 grids of 4096 x 96 (blocks and light), 12 of 256 x 256,
            // about 2 500 lists (block tables of 2 048, small ones, 1 024 empty), 65 maps, 2 500 globals.
            bench.Run("""
                for (k = 0; k < 32; k = k + 1) { DsGridCreate(4096, 96); }
                for (k = 0; k < 12; k = k + 1) { DsGridCreate(256, 256); }
                for (k = 0; k < 30; k = k + 1) { l = DsListCreate(); DsListFill(l, 2048, k); }
                for (k = 0; k < 1400; k = k + 1) { l = DsListCreate(); DsListFill(l, 6, k); }
                for (k = 0; k < 1024; k = k + 1) { DsListCreate(); }
                for (k = 0; k < 65; k = k + 1) { m = DsMapCreate(); for (e = 0; e < 40; e = e + 1) { DsMapSet(m, "k" + StringOf(e), e); } }
                r = 0;
                """);
            var globals = new System.Text.StringBuilder();
            for (int i = 0; i < 2500; i++) globals.Append("mc_value_").Append(i).Append(" = ").Append(i).Append("; ");
            bench.Run(globals.Append("r = 0;").ToString());
            const string StartShared = """sa = JobScriptCreate("SjNothing"); JobScriptShareAll(sa); JobScriptStart(sa); r = sa;""";
            const string StartSixGrids = """
                sg = JobScriptCreate("SjNothing");
                JobScriptGrid(sg, 1, false); JobScriptGrid(sg, 2, false); JobScriptGrid(sg, 3, false);
                JobScriptGrid(sg, 17, false); JobScriptGrid(sg, 18, false); JobScriptGrid(sg, 19, false);
                JobScriptStart(sg); r = sg;
                """;
            long startBytes = long.MaxValue;
            (double GameMs, double WorkerMs) Measure(string start)
            {
                List<double> game = [], worker = [];
                startBytes = long.MaxValue;
                for (int i = 0; i < 15; i++)
                {
                    long t = Stopwatch.GetTimestamp();
                    long bytes = GC.GetAllocatedBytesForCurrentThread();
                    double job = bench.Number(start);
                    startBytes = Math.Min(startBytes, GC.GetAllocatedBytesForCurrentThread() - bytes);
                    game.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    HeadlessHarness.Assert(bench.Wait(job) == "succeeded", "A sharing job failed: " + PgslCommands.JobError(job));
                    worker.Add(PgslCommands.ScriptJobWorkerMilliseconds(job));
                    PgslCommands.JobRelease(job);
                }
                game.Sort(); worker.Sort();
                return (game[game.Count / 2], worker[worker.Count / 2]);
            }
            Measure(StartShared);
            (double sharedGame, double sharedWorker) = Measure(StartShared);
            long sharedBytes = startBytes;
            (double gridsGame, double gridsWorker) = Measure(StartSixGrids);

            // What the game's first change of a big grid a running job holds costs: the copy.
            double holder = bench.Number("""lh = JobScriptCreate("SjSpinLong"); JobScriptShareAll(lh); JobScriptBudget(lh, 2000000000); JobScriptStart(lh, 50000000); r = lh;""");
            long copiesBefore = PgslCommands.SharedCopies;
            List<double> firstWrites = [], laterWrites = [];
            for (int grid = 1; grid <= 9; grid++)
            {
                long t = Stopwatch.GetTimestamp();
                PgslCommands.DsGridSet(grid, 1, 1, 7);
                firstWrites.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                t = Stopwatch.GetTimestamp();
                PgslCommands.DsGridSet(grid, 2, 2, 7);
                laterWrites.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            bool heldThroughout = PgslCommands.JobStatus(holder) is "queued" or "running";
            long copiesMade = PgslCommands.SharedCopies - copiesBefore;
            PgslCommands.JobCancel(holder);
            bench.Wait(holder);
            PgslCommands.JobRelease(holder);
            firstWrites.Sort(); laterWrites.Sort();
            double copyMs = firstWrites[firstWrites.Count / 2], laterMs = laterWrites[laterWrites.Count / 2];

            string F(double ms) => ms.ToString("F3", CultureInfo.InvariantCulture) + " ms";
            row("Job speed", "JobScriptShareAll at a voxel game's size (44 grids, 2 500 lists, 65 maps, 2 500 globals): game thread / worker set-up (medians)", "behaviour",
                $"{F(sharedGame)} / {F(sharedWorker)}; the game thread allocates {sharedBytes / 1024.0:F0} KB a start (fewest)");
            row("Job speed", "six 4096 x 96 grids given with JobScriptGrid: game thread / worker set-up (medians)", "behaviour", $"{F(gridsGame)} / {F(gridsWorker)}");
            row("Job speed", "the game's first DsGridSet of a 4096 x 96 grid a running job holds (the copy) / its next one (medians)", "behaviour",
                $"{F(copyMs)} / {F(laterMs)}; {copiesMade} copies for 9 grids" + (heldThroughout ? "" : " (the job ended part-way)"));
            Console.WriteLine($"Sharing: JobScriptShareAll at a voxel game's size {F(sharedGame)} game thread, {F(sharedWorker)} worker; six big grids {F(gridsGame)} / {F(gridsWorker)}; copy of a held 4096 x 96 grid {F(copyMs)}, next write {F(laterMs)}");
            HeadlessHarness.Assert(!heldThroughout || copiesMade == 9, $"Nine first writes to held grids made {copiesMade} copies.");
            HeadlessHarness.Assert(sharedGame < 50 && gridsGame < 20, $"Sharing took {F(sharedGame)} (all) and {F(gridsGame)} (six grids) of the game's thread.");
        });
    }
}

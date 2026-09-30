using System.Numerics;
using Genesis.Streaming;
using Genesis.World.Navigation;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

internal static class WorldStreamingSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Runtime.WorldStreaming.WorkerShutdownCancelsAndDropsStaleCompletions", () =>
        {
            using var jobs = new StreamingJobQueue(1);
            using var started = new ManualResetEventSlim(); using var cancelled = new ManualResetEventSlim();
            bool applied = false; jobs.PostMainThread(() => applied = true);
            AsyncLocal<string> ambient = new(); ambient.Value = "Object script context"; string? inherited = "not run";
            Check(jobs.TryRunBackground(token =>
            {
                inherited = ambient.Value; started.Set();
                while (!token.IsCancellationRequested) Thread.Sleep(1);
                jobs.PostMainThread(() => applied = true); cancelled.Set();
            }), "Native worker was not accepted.");
            Check(started.Wait(5000) && !jobs.TryRunBackground(() => { }), "Worker concurrency limit failed.");
            jobs.Dispose();
            Check(cancelled.Wait(5000) && SpinWait.SpinUntil(() => jobs.ActiveJobs == 0, 5000), "Disposed streaming work did not stop cleanly.");
            jobs.ProcessMainThread(100);
            Check(inherited == null && !applied && jobs.PendingMainThreadCallbacks == 0 && !jobs.TryRunBackground(() => { }),
                "Shutdown applied stale callbacks, accepted work or retained ambient script state.");
            jobs.Dispose();
            using var legacy = new StreamingJobQueue(1);
            using var legacyStarted = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            Check(legacy.TryRunBackground(() => { legacyStarted.Set(); release.Wait(5000); }), "Legacy native work was rejected.");
            Check(legacyStarted.Wait(5000), "Legacy native worker did not start.");
            legacy.Dispose(); release.Set();
            Check(SpinWait.SpinUntil(() => legacy.ActiveJobs == 0, 5000), "Legacy work could not finish after queue disposal.");
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.WorldStreaming.MainThreadCompletionsRespectCountAndTimeBudgets", () =>
        {
            using var jobs = new StreamingJobQueue(1); int applied = 0;
            jobs.PostMainThread(() => { Thread.Sleep(20); applied++; });
            jobs.PostMainThread(() => applied++); jobs.PostMainThread(() => applied++);
            jobs.ProcessMainThread(100, TimeSpan.FromMilliseconds(1));
            Check(applied == 1 && jobs.PendingMainThreadCallbacks == 2, "Elapsed-time budget did not defer remaining completions.");
            jobs.ProcessMainThread(1, TimeSpan.FromMilliseconds(100));
            Check(applied == 2 && jobs.PendingMainThreadCallbacks == 1, "Count budget was ignored.");
            jobs.ProcessMainThread(0); Check(applied == 2, "A zero callback budget applied work.");
            jobs.ProcessMainThread(1); Check(applied == 3, "Deferred completion was lost.");
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.WorldStreaming.SpatialLookupClipsEightKilometreCellsAndPersists", () =>
        {
            TerrainAsset terrain = new(801, 801, 10, -4000, -4000, -1, 1);
            WorldManifest manifest = WorldManifest.Build(terrain, new(), chunkSize: 256);
            Check(manifest.Chunks.Count == 1024 && manifest.Chunks[^1].Bounds.Size == new Vector2(64),
                "The 8 km extent did not clip its 256 m boundary cells.");
            string path = Path.Combine(context.Workspace, "Streaming.world.json");
            WorldManifestSerializer.Save(path, manifest); manifest = WorldManifestSerializer.Load(path);
            var provider = new WorldManifestStreamingProvider(manifest);
            using var jobs = new StreamingJobQueue(1);
            var settings = new StreamingSettings { LoadDistanceScale = 1, MaxGenerationStartsPerFrame = 100, MaxUnloadsPerFrame = 100 };
            StreamingContext frame = Frame(new(-4000, 0, -4000), settings, jobs);
            provider.Tick(frame);
            Check(provider.Resident.ToHashSet().SetEquals(Wanted(manifest, frame.FocusPosition, 256)), "Indexed lookup missed the corner's exact-radius neighbour.");
            Check(provider.SpatialCandidatesLastRefresh <= 16 && provider.SpatialCandidatesLastRefresh < manifest.Chunks.Count / 10,
                "Local lookup still scanned the full world manifest.");
            int refreshes = provider.QueueRefreshes;
            for (int i = 0; i < 10; i++) provider.Tick(frame);
            Check(provider.QueueRefreshes == refreshes, "A stationary focus rebuilt its queue each frame.");
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) provider.Tick(frame);
            Check(GC.GetAllocatedBytesForCurrentThread() - allocated == 0, "Stationary manifest ticks allocate collections.");
            provider.RequestStreamingRefresh(); provider.Tick(frame);
            Check(provider.QueueRefreshes == refreshes + 1, "An explicit refresh did not rebuild the load queue.");
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.WorldStreaming.TraversalHonoursBudgetsHysteresisAndUniqueLifecycle", () =>
        {
            WorldManifest manifest = WorldManifest.Build(new TerrainAsset(801, 801, 10, -4000, -4000, -1, 1), new(), 256);
            var provider = new WorldManifestStreamingProvider(manifest); HashSet<WorldChunkCoordinate> active = [];
            provider.ChunkEntered += cell => Check(active.Add(cell.Coordinate), "Duplicate cell-enter notification.");
            provider.ChunkRetired += cell => Check(active.Remove(cell.Coordinate), "Retired a cell that was not resident.");
            using var jobs = new StreamingJobQueue(1);
            var settings = new StreamingSettings { LoadDistanceScale = 1, UnloadDistanceScale = 1, MaxGenerationStartsPerFrame = 3, MaxUnloadsPerFrame = 2 };
            foreach (Vector3 focus in new Vector3[] { new(-4000, 0, -4000), new(0), new(256, 0, 256), new(4000, 0, 4000), new(12000, 0, 12000), new(-4000, 0, -4000) })
            {
                StreamingContext frame = Frame(focus, settings, jobs);
                HashSet<WorldChunkCoordinate> wanted = Wanted(manifest, focus, 256);
                for (int tick = 0; tick < 40; tick++)
                {
                    provider.Tick(frame);
                    Check(provider.Stats.LoadsStartedLastFrame <= 3 && provider.Stats.UnloadsLastFrame <= 2, "Streaming exceeded its per-frame budget.");
                    Check(provider.Stats.CellsPending == wanted.Except(provider.Resident).Count(), "Pending count is wrong while old cells await retirement.");
                    Check(active.SetEquals(provider.Resident), "Lifecycle notifications and residency disagree.");
                }
                Check(wanted.IsSubsetOf(active), "Traversal never loaded its destination cells.");
                Check(active.All(coordinate => Squared(focus, manifest.Chunks.First(cell => cell.Coordinate == coordinate).Bounds) <= 512 * 512),
                    "Traversal retained distant metadata cells beyond the hysteresis radius.");
            }
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.WorldStreaming.SparseNonuniformLegacyRecordsAndInvalidBounds", () =>
        {
            WorldManifest manifest = new()
            {
                Bounds = new(new(-512), new(512)), ChunkSize = 256,
                Chunks =
                [
                    new() { Coordinate = new(999, -7), Bounds = new(new(-512, -256), new(0, 256)) },
                    new() { Coordinate = new(-10, 41), Bounds = new(new(0, -512), new(512, 512)) },
                    new() { Coordinate = new(4, 3), Bounds = new(new(-64), new(64)) },
                ],
            };
            var provider = new WorldManifestStreamingProvider(manifest);
            using var jobs = new StreamingJobQueue(1);
            var settings = new StreamingSettings { LoadDistanceScale = 1, MaxGenerationStartsPerFrame = 100, MaxUnloadsPerFrame = 100 };
            foreach (Vector3 focus in new Vector3[] { new(-768, 0, 0), new(0), new(256.001f, 0, -512), new(4096, 0, 4096) })
            {
                provider.Tick(Frame(focus, settings, jobs));
                Check(provider.Stats.CellsVisible == Wanted(manifest, focus, 256).Count && provider.SpatialCandidatesLastRefresh <= 3,
                    "Nonuniform legacy bounds were omitted or counted more than once.");
            }
            manifest.ChunkSize = float.NaN;
            Reject(() => new WorldManifestStreamingProvider(manifest));
            manifest.ChunkSize = 256; manifest.Chunks[0].Bounds = new(new(-1024), new(0));
            Reject(() => WorldManifestSerializer.Validate(manifest));
        });
    }

    private static StreamingContext Frame(Vector3 focus, StreamingSettings settings, StreamingJobQueue jobs) => new()
    {
        FocusPosition = focus, ViewProjection = Matrix4x4.Identity, StreamingViewProjection = Matrix4x4.Identity,
        CameraFarPlane = 256, CameraYaw = 0, CameraPitch = 0, Settings = settings, Jobs = jobs,
    };
    private static HashSet<WorldChunkCoordinate> Wanted(WorldManifest manifest, Vector3 focus, float radius) =>
        manifest.Chunks.Where(cell => Squared(focus, cell.Bounds) <= radius * radius).Select(cell => cell.Coordinate).ToHashSet();
    private static float Squared(Vector3 focus, WorldBounds bounds)
    {
        float x = MathF.Max(bounds.Minimum.X - focus.X, MathF.Max(0, focus.X - bounds.Maximum.X));
        float z = MathF.Max(bounds.Minimum.Y - focus.Z, MathF.Max(0, focus.Z - bounds.Maximum.Y));
        return x * x + z * z;
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid manifest was accepted.");
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}

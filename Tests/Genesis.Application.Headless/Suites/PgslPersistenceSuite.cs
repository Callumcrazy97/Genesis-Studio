using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class PgslPersistenceSuite
{
    public static void Run(HeadlessContext context, bool native = true)
    {
        HeadlessHarness.BeginMajor(context.Report, "PGSL.Persistence");
        string? previousProject = PgslCommands.ProjectPath;
        PgslContext? previousContext = PgslCommands.BindContext(new());
        try
        {
            string root = Path.Combine(context.Workspace, "Persistence", "Original"); Directory.CreateDirectory(root);
            string id = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(root, "Probe.genesisproj"), new JsonObject { ["projectId"] = id }.ToJsonString());
            PgslCommands.ProjectPath = root;
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Files.UnicodeAtomicReplacementAndFailureDiagnostics", () =>
            {
                const string text = "meadow / 繁體 / café / 🐲\nsecond line";
                string relative = "engine-check/文本.txt";
                Check(PgslCommands.FileWriteText(relative, text) && PgslCommands.FileExists(relative), "Relative text write failed: " + PgslCommands.FileLastError());
                Check(PgslCommands.FileReadText(relative) == text && PgslCommands.FileLastError() == "", "UTF-8 did not round trip.");
                Check(PgslCommands.FileWriteText(relative, "replaced") && PgslCommands.FileReadText(relative) == "replaced", "Atomic replacement failed.");
                Check(!PgslCommands.FileExists("missing.txt") && PgslCommands.FileLastError() == "", "Missing files should be a clean false.");
                Check(PgslCommands.FileReadText("missing.txt") == "" && PgslCommands.FileLastError().Length > 0, "Missing reads have no diagnostic.");
                Check(!PgslCommands.FileWriteText("../../escape.txt", "bad") && PgslCommands.FileLastError().Length > 0, "Relative path escaped its game directory.");
                Check(!PgslCommands.FileWriteText(relative, new string('x', ProjectTextFiles.MaximumTextBytes + 1))
                    && PgslCommands.FileReadText(relative) == "replaced", "Oversized write damaged the previous file.");
                string absolute = Path.Combine(context.OutputRoot, "absolute-unicode.txt");
                Check(PgslCommands.FileWriteText(absolute, text) && File.ReadAllText(absolute) == text, "Explicit absolute file paths failed.");
                using CancellationTokenSource cancellation = new(); cancellation.Cancel();
                try { ProjectTextFiles.Write(absolute, "cancelled", cancellation.Token); throw new InvalidOperationException("Cancelled write succeeded."); }
                catch (OperationCanceledException) { }
                Check(File.ReadAllText(absolute) == text, "Cancelled write changed existing data.");
                using (FileStream locked = new(absolute, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(!PgslCommands.FileWriteText(absolute, "locked") && PgslCommands.FileLastError().Length > 0, "Locked destination was reported as saved.");
                Check(File.ReadAllText(absolute) == text && !Directory.EnumerateFiles(context.OutputRoot, ".genesis-*.tmp").Any(), "Failed write damaged data or leaked a pending file.");
                string invalid = Path.Combine(context.OutputRoot, "invalid-utf8.txt"); File.WriteAllBytes(invalid, [0xff, 0xfe]);
                Check(PgslCommands.FileReadText(invalid) == "" && PgslCommands.FileLastError().Length > 0, "Invalid UTF-8 was silently replaced.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Files.IdentitySurvivesProjectRelocation", () =>
            {
                string moved = Path.Combine(context.Workspace, "Persistence", "Moved"); Directory.CreateDirectory(moved);
                File.Copy(Path.Combine(root, "Probe.genesisproj"), Path.Combine(moved, "Probe.genesisproj"), true);
                Check(ProjectNumberSave.GetWritableDirectory(root) == ProjectNumberSave.GetWritableDirectory(moved), "Moved project lost writable identity.");
                PgslCommands.ProjectPath = moved;
                Check(PgslCommands.FileReadText("engine-check/文本.txt") == "replaced", "Moved project lost its custom text save.");
                PgslCommands.ProjectPath = root;
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Save.CheckedSetterPreservesLegacyFormatAndLimits", () =>
            {
                ProjectNumberSave save = new(Path.Combine(context.OutputRoot, "numeric-save.json"));
                Check(!save.TrySet("", 1) && save.LastError.Length > 0 && !save.TrySet("nan", double.NaN), "Invalid save data was accepted.");
                for (int index = 0; index < 256; index++) Check(save.TrySet("key" + index, index), "Valid numeric key failed.");
                Check(!save.TrySet("overflow", 1) && save.LastError.Contains("256") && save.TrySet("key0", 17), "Numeric capacity/update semantics changed.");
                Check(save.Flush() && save.LastError == "", "Numeric flush failed.");
                ProjectNumberSave reopened = new(Path.Combine(context.OutputRoot, "numeric-save.json"));
                Check(reopened.Get("key0", -1) == 17 && reopened.Get("key255", -1) == 255, "Version-1 numeric values changed.");
                Check(PgslCommands.SaveTrySetNumber("probe", 42) && PgslCommands.SaveLastError() == "", "Checked PGSL setter is not callable.");
                Check(!PgslCommands.SaveTrySetNumber("probe", double.PositiveInfinity) && PgslCommands.SaveLastError().Length > 0, "PGSL save diagnostics hide nonfinite values.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Json.TypedNestedDataAndOverlappingHandleFamilies", () =>
            {
                PgslCommands.BindContext(new());
                double map = PgslCommands.DsMapCreate(), list = PgslCommands.DsListCreate();
                Check(map == list, "Fixture must exercise overlapping map/list handles.");
                PgslCommands.DsMapSet(map, "plainHandle", list);
                PgslCommands.DsMapSetString(map, "text", "繁體 🐲 café");
                PgslCommands.DsMapSetBoolean(map, "yes", true); PgslCommands.DsMapSetNull(map, "nothing");
                PgslCommands.DsListAddString(list, "line\nquoted \"text\""); PgslCommands.DsListAddBoolean(list, false); PgslCommands.DsListAddNull(list);
                Check(PgslCommands.DsMapSetCollection(map, "nested", list, "list"), "Typed list reference failed.");
                string encoded = PgslCommands.JsonEncode(map, "map");
                using JsonDocument document = JsonDocument.Parse(encoded);
                Check(document.RootElement.GetProperty("plainHandle").ValueKind == JsonValueKind.Number
                    && document.RootElement.GetProperty("nested").ValueKind == JsonValueKind.Array, "Numeric handle was guessed as a collection.");
                double restored = PgslCommands.JsonDecode(encoded, "map"), child = PgslCommands.DsMapGet(restored, "nested");
                Check(restored > 0 && PgslCommands.DsMapGetString(restored, "text") == "繁體 🐲 café"
                    && PgslCommands.DsMapValueKind(restored, "yes") == "boolean" && PgslCommands.DsMapValueKind(restored, "nothing") == "null"
                    && PgslCommands.DsMapValueKind(restored, "nested") == "list" && PgslCommands.DsListValueKind(child, 1) == "boolean"
                    && PgslCommands.DsListValueKind(child, 2) == "null", "Decode lost nested value types.");
                PgslCommands.DsMapDelete(restored, "nested");
                Check(PgslCommands.DsMapSetCollection(restored, "manual", list, "list") && PgslCommands.JsonFree(restored, "map"), "Owned tree release failed.");
                Check(PgslCommands.DsListValueKind(child, 0) == "missing" && PgslCommands.DsListSize(list) == 3, "JsonFree leaked a removed child or freed a manual collection.");
                Check(!PgslCommands.JsonFree(restored, "map") && PgslCommands.JsonLastError().Length > 0, "Double release has no diagnostic.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Json.RejectsCyclesCorruptionAndRollsBackFailedDecodes", () =>
            {
                PgslCommands.BindContext(new());
                double map = PgslCommands.DsMapCreate(); PgslCommands.DsMapSetCollection(map, "cycle", map, "map");
                Check(PgslCommands.JsonEncode(map, "map") == "" && PgslCommands.JsonLastError().Contains("cycle"), "Cycle was serialized.");
                int before = Collections();
                foreach (string text in new[] { "{bad", "{\"a\":{},\"a\":[]}", "{\"value\":1e999}", "[]", "{\"deep\":" + new string('[', 65) + "0" + new string(']', 65) + "}" })
                    Check(PgslCommands.JsonDecode(text, "map") == 0 && PgslCommands.JsonLastError().Length > 0 && Collections() == before,
                        "Bad input allocated an orphaned collection: " + text);
                Check(PgslCommands.JsonEncode(map, "guess") == "", "Unknown root kind accepted.");
                double restored = PgslCommands.JsonDecode("{\"ok\":true}", "map");
                Check(restored > 0 && PgslCommands.JsonLastError() == "" && PgslCommands.JsonFree(restored, "map"), "Successful JSON did not clear errors.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Jobs.AsyncTextResultsAreTypedIsolatedAndReleased", () =>
            {
                PgslContext owner = new(); PgslCommands.BindContext(owner);
                double write = PgslCommands.FileWriteTextAsync("engine-check/async.txt", "繁體 🐲 café");
                Check(write > 0 && PgslCommands.JobResultKind(write) == "boolean", "Async write was not queued.");
                Wait(() => PgslCommands.JobStatus(write) is "succeeded" or "failed" or "cancelled");
                Check(PgslCommands.JobStatus(write) == "succeeded" && PgslCommands.JobResultBool(write), "Async write failed: " + PgslCommands.JobError(write));
                Check(PgslCommands.JobResultString(write) == "" && PgslCommands.JobLastError().Length > 0, "Wrong result type did not report an error.");
                Check(PgslCommands.JobRelease(write) && PgslCommands.JobStatus(write) == "invalid" && !PgslCommands.JobRelease(write), "Released handle remained usable.");
                double read = PgslCommands.FileReadTextAsync("engine-check/async.txt");
                PgslCommands.BindContext(new()); Check(PgslCommands.JobStatus(read) == "invalid", "A different Object read the job.");
                PgslCommands.BindContext(owner); Wait(() => PgslCommands.JobStatus(read) is "succeeded" or "failed");
                Check(PgslCommands.JobResultString(read) == "繁體 🐲 café" && PgslCommands.JobLastError() == "", "Async UTF-8 result changed.");
                PgslCommands.JobRelease(read);
                double failed = PgslCommands.FileReadTextAsync("missing-async.txt"); Wait(() => PgslCommands.JobStatus(failed) == "failed");
                Check(PgslCommands.JobError(failed).Length > 0, "Async I/O failure has no diagnostic."); PgslCommands.JobRelease(failed);
                Check(PgslCommands.FileReadTextAsync("../escape.txt") == 0 && PgslCommands.FileLastError().Length > 0, "Async relative path escaped.");
                PgslCommands.ReleaseJobs(owner);
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Jobs.BoundsWorkersRetainedCapacityAndCancellation", () =>
            {
                Wait(() => NativeJobPool.ReservedJobs == 0);
                NativeJobPool first = new(), second = new(), extra = new();
                using ManualResetEventSlim release = new(); using CountdownEvent started = new(4);
                int active = 0, peak = 0, entered = 0;
                NativeJobPool.Result Block(CancellationToken token)
                {
                    int running = Interlocked.Increment(ref active);
                    int previous;
                    do { previous = Volatile.Read(ref peak); if (previous >= running) break; }
                    while (Interlocked.CompareExchange(ref peak, running, previous) != previous);
                    if (Interlocked.Increment(ref entered) <= 4) started.Signal();
                    try { release.Wait(token); return new("number", 42d); }
                    finally { Interlocked.Decrement(ref active); }
                }
                try
                {
                    List<int> ids = [];
                    for (int index = 0; index < NativeJobPool.MaximumContextJobs; index++) ids.Add(first.Start("number", Block));
                    Check(started.Wait(10000), "Native worker pool did not start.");
                    ExpectCapacity(() => first.Start("number", Block));
                    for (int index = 0; index < NativeJobPool.MaximumContextJobs; index++) second.Start("number", Block);
                    ExpectCapacity(() => extra.Start("number", Block));
                    int queued = ids.First(id => first.Read(id).State == "queued");
                    Check(first.Cancel(queued), "Queued work could not be cancelled.");
                    Wait(() => first.Read(queued).State == "cancelled"); Check(first.Release(queued), "Cancelled result retained capacity.");
                    int runningId = ids.First(id => first.Read(id).State == "running");
                    Check(first.Release(runningId), "Running work could not be released.");
                    Wait(() => NativeJobPool.ReservedJobs <= 30);
                    release.Set(); Wait(() => Volatile.Read(ref active) == 0 && first.Read(ids[0]).State is "succeeded" or "invalid" or "cancelled");
                    Check(peak <= 4, "Native jobs exceeded four workers.");
                }
                finally { release.Set(); first.ReleaseAll(); second.ReleaseAll(); extra.ReleaseAll(); }
                Wait(() => NativeJobPool.ReservedJobs == 0);
                static void ExpectCapacity(Func<int> start)
                {
                    try { _ = start(); throw new InvalidDataException("Unbounded jobs were admitted."); }
                    catch (InvalidOperationException error) { Check(error.Message.Contains("maximum") || error.Message.Contains("capacity"), "Wrong capacity failure."); }
                }
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Diagnostics.CommandSweepsReleaseJobsAndPreserveCallerSaves", () =>
            {
                PgslContext caller = new(); PgslCommands.BindContext(caller);
                string? callerProject = PgslCommands.ProjectPath;
                Check(PgslCommands.FileWriteText("PgslAutoTest", "caller file") && PgslCommands.SaveTrySetNumber("caller", 123)
                    && PgslCommands.SaveFlush(), "Caller persistence setup failed.");
                string numeric = Path.Combine(ProjectNumberSave.GetWritableDirectory(root), "campaign.json");
                byte[] previousNumeric = File.ReadAllBytes(numeric);
                PgslCommandTestReport files = PgslCommandAutoTester.Run("Files", repeats: 3);
                PgslCommandTestReport saves = PgslCommandAutoTester.Run("Save Data", repeats: 1);
                Check(files.Total >= 6 && saves.Total >= 7 && files.Failed == 0 && saves.Failed == 0,
                    "Persistence command sweep failed: " + files.Total + " file / " + saves.Total + " save commands; "
                    + string.Join("; ", files.Results.Concat(saves.Results).Where(result => result.Outcome == PgslCommandOutcome.Threw)));
                Wait(() => NativeJobPool.ReservedJobs == 0);
                Check(ReferenceEquals(PgslCommands.GetContext(), caller) && PgslCommands.ProjectPath == callerProject
                    && PgslCommands.FileReadText("PgslAutoTest") == "caller file" && PgslCommands.SaveGetNumber("caller", 0) == 123
                    && File.ReadAllBytes(numeric).SequenceEqual(previousNumeric), "Command checks changed caller context or saves.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Persistence.SavedSourceExecutesThroughNativeBridge", () =>
            {
                string file = Path.Combine(context.Workspace, "Persistence", "Create.pgsl"); File.WriteAllText(file, Probe);
                VMEngine.Initialize(); PgslContext? previous = VMEngine.Bridge.GetContext();
                try
                {
                    VMEngine.Bridge.SetContext(new());
                    CompiledScriptAsset compiled = ScriptAssetCompiler.Compile("PersistenceProbe", File.ReadAllText(file));
                    var vm = VMEngine.CreateVm(); vm.Execute(compiled.CompileResult.Instructions, compiled.CompileResult.Constants);
                    Check(File.Exists(ProjectTextFiles.ResolvePath(root, "engine-check/custom.json")), "Saved source did not execute native persistence.");
                }
                finally { VMEngine.Bridge.SetContext(previous); }
            });
        }
        finally { PgslCommands.ProjectPath = previousProject; PgslCommands.BindContext(previousContext); }
        if (native) NativeObjectEvents(context);
    }

    private static int Collections() => PgslCommands.GetContext().Variables.Keys.Count(key => key.StartsWith("__ds_map_") || key.StartsWith("__ds_list_"));

    private static void NativeObjectEvents(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Persistence.ExportedObjectEventSurvivesProcessRestart", () =>
        {
            ProjectSession project = new ProjectService().CreateProject(Path.Combine(context.Workspace, "PersistenceNative"), "Engine persistence probe");
            ResourceService resources = new(project);
            string folder = Path.Combine(project.AssetsPath, "Objects"); Directory.CreateDirectory(folder);
            string file = resources.CreateResource(folder, ResourceKind.GameObject, "Persistence probe");
            File.WriteAllText(file, new JsonObject { ["schemaVersion"] = 2, ["dimension"] = "TwoD", ["components"] = new JsonArray(
                new JsonObject { ["type"] = "ScriptComponent", ["props"] = new JsonObject { ["ScriptClass"] = "Persistence probe" } }),
                ["events"] = new JsonArray("Create", "Step") }.ToJsonString());
            string scripts = Path.Combine(folder, "Persistence probe"); Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "Create.pgsl"), Probe);
            File.AppendAllText(Path.Combine(scripts, "Create.pgsl"), "\nprobeJob = FileReadTextAsync(\"engine-check/custom.json\"); probePhase = 0;\n");
            File.WriteAllText(Path.Combine(scripts, "Step.pgsl"), AsyncProbe);
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
            RoomAsset room = RoomAssetLoader.Parse(roomFile); room.Settings.TargetFps = 30; room.Settings.CaptureMouse = false;
            room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.GameObject, Name = "Generic persistence probe", LayerId = room.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, file).Replace('\\', '/') } });
            RoomAssetLoader.Save(room, roomFile);
            string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No matching Player.");
            Check(File.ReadAllBytes(typeof(RuntimePaths).Assembly.Location).SequenceEqual(File.ReadAllBytes(Path.Combine(runtime, "Genesis.Runtime.dll"))), "Use Build.bat --quick --test pgsl-persistence to select matching Player.");
            GameExportResult exported = GameExportService.Export(new(project, Path.Combine(context.OutputRoot, "PersistencePlayer"), GameExportFormat.Folder));
            Check(exported.Success, "Probe export failed: " + exported.ErrorMessage);
            string custom = ProjectTextFiles.ResolvePath(project.RootPath, "engine-check/custom.json");
            Check(!File.Exists(custom), "New native probe identity reused old state.");
            for (int run = 1; run <= 2; run++)
            {
                ProcessStartInfo start = new(Path.Combine(exported.OutputPath, exported.ExecutableName))
                { WorkingDirectory = exported.OutputPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--autoshot"); start.ArgumentList.Add("4");
                start.Environment["GENESIS_RENDER_BACKEND"] = "DX11"; start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
                foreach (string name in new[] { "GENESIS_PROJECT_PATH", "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(name);
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("Player did not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Persistence Player timed out."); }
                string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(context.Logs, "persistence-player-" + run + ".log"), log);
                Check(process.ExitCode == 0 && log.Contains("GENESIS_PERSISTENCE_OK") && log.Contains("GENESIS_ASYNC_PERSISTENCE_OK")
                    && !log.Contains("SCRIPT ERROR"), "Saved Object event failed: " + log);
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(custom));
                Check(saved.RootElement.GetProperty("runs").GetInt32() == run && saved.RootElement.GetProperty("text").GetString() == "繁體 🐲 café",
                    "Export identity or fresh-process structured save changed.");
                Check(File.ReadAllText(ProjectTextFiles.ResolvePath(project.RootPath, "engine-check/async.json")) == File.ReadAllText(custom), "Async saved Object events changed structured data.");
            }
        });
    }

    private const string Probe = """
        var root = 0;
        var restored = FileExists("engine-check/custom.json");
        if (restored) { root = JsonDecode(FileReadText("engine-check/custom.json"), "map"); }
        else {
            root = DsMapCreate();
            DsMapSetString(root, "text", "繁體 🐲 café");
            DsMapSetBoolean(root, "flag", true);
            var list = DsListCreate(); DsListAddNull(list); DsListAddString(list, "nested");
            DsMapSetCollection(root, "items", list, "list");
        }
        var previousRuns = DsMapGet(root, "runs");
        var numeric = SaveGetNumber("probeRuns", 0);
        var ok = root > 0 && previousRuns == numeric;
        ok = ok && DsMapGetString(root, "text") == "繁體 🐲 café";
        ok = ok && DsMapValueKind(root, "flag") == "boolean";
        var child = DsMapGet(root, "items");
        ok = ok && DsMapValueKind(root, "items") == "list" && DsListValueKind(child, 0) == "null";
        DsMapSet(root, "runs", previousRuns + 1);
        var encoded = JsonEncode(root, "map");
        ok = ok && JsonLastError() == "";
        ok = ok && FileWriteText("engine-check/custom.json", encoded);
        ok = ok && SaveTrySetNumber("probeRuns", previousRuns + 1) && SaveFlush();
        if (restored) { JsonFree(root, "map"); }
        else { DsListDestroy(child); DsMapDestroy(root); }
        if (ok) { Print("GENESIS_PERSISTENCE_OK"); }
        else { Print("GENESIS_PERSISTENCE_FAILED " + FileLastError() + " / " + JsonLastError() + " / " + SaveLastError()); }
        """;

    private const string AsyncProbe = """
        if (probeJob > 0 && JobStatus(probeJob) == "succeeded") {
            if (probePhase == 0) {
                var text = JobResultString(probeJob);
                JobRelease(probeJob);
                probeJob = FileWriteTextAsync("engine-check/async.json", text);
                probePhase = 1;
            }
            else {
                if (JobResultBool(probeJob)) { Print("GENESIS_ASYNC_PERSISTENCE_OK"); }
                JobRelease(probeJob); probeJob = 0;
            }
        }
        """;

    private static void Wait(Func<bool> condition) => Check(SpinWait.SpinUntil(condition, 10000), "Native job did not reach the expected state.");

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}

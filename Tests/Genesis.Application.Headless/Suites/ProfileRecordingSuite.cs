using System.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The debug screen's profile, recorded by the real Player without debug mode (GENESIS_PROFILE=1,
/// or --profile): from the first frame of play to the game's end, with frame phases, PGSL time
/// per event and allocation, and none of debug mode's screen.
/// </summary>
internal static class ProfileRecordingSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.AProfileIsRecordedWithoutDebugMode", () =>
        {
            string runtime = NewestPlayer() ?? throw new InvalidOperationException("No Player found.");
            string parent = Path.Combine(ctx.Workspace, "ProfileRecording");
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Profiled Game", "Blank");
            ResourceService resources = new(project);
            string objects = Path.Combine(project.AssetsPath, "Objects");
            Directory.CreateDirectory(objects);
            string objectFile = resources.CreateResource(objects, ResourceKind.GameObject, "Worker");
            File.WriteAllText(objectFile, "{\"schemaVersion\":2,\"dimension\":\"ThreeD\",\"components\":[{\"type\":\"ScriptComponent\",\"props\":{\"ScriptClass\":\"Worker\"}}],\"events\":[\"Create\",\"Step\"]}");
            Directory.CreateDirectory(Path.Combine(objects, "Worker"));
            File.WriteAllText(Path.Combine(objects, "Worker", "Create.pgsl"), "t0 = TimeMs(); total = 0;");
            // Some script work every frame, then the game ends by itself after three seconds.
            File.WriteAllText(Path.Combine(objects, "Worker", "Step.pgsl"), """
                for (var i = 0; i < 200; i = i + 1) { total = total + Sqrt(i); }
                if (TimeMs() > t0 + 3000) { GameQuit(); }
                """);
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
            RoomAsset start = RoomAsset.Create("Start", RoomDimension.ThreeD);
            start.Settings.CaptureMouse = false;
            start.Nodes.Add(new RoomNode
            {
                Kind = RoomNodeKind.GameObject, Name = "Worker", LayerId = start.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, objectFile).Replace('\\', '/') },
            });
            RoomAssetLoader.Save(start, roomFile);

            ProcessStartInfo info = new(Path.Combine(runtime, RuntimePaths.RuntimeExeName))
            {
                WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            info.Environment["GENESIS_PROJECT_PATH"] = project.RootPath;
            info.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
            info.Environment["GENESIS_RENDER_BACKEND"] = "DX11";
            info.Environment[ProjectPlayerApp.ProfileEnvironmentVariable] = "1";
            foreach (string name in new[] { "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED", DebugCategories.EngineEnvironmentVariable })
                info.Environment.Remove(name);
            string output;
            using (Process process = Process.Start(info) ?? throw new InvalidOperationException("The Player did not start."))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            }
            string playerLog = Path.Combine(ProjectPaths.LogsDir(project.RootPath), "project_player.log");
            string log = (File.Exists(playerLog) ? File.ReadAllText(playerLog) : string.Empty) + Environment.NewLine + output;
            File.WriteAllText(Path.Combine(ctx.Logs, "profile-recording-player.log"), log);

            HeadlessHarness.Assert(log.Contains("profile saved: ", StringComparison.Ordinal) && output.Contains("GENESIS_PROFILE_SAVED", StringComparison.Ordinal),
                "The run did not save a profile. Log:\r\n" + Tail(log));
            string profiles = Path.Combine(ProjectPaths.DebugRoot(project.RootPath), "Profiles");
            string folder = Directory.Exists(profiles) ? Directory.GetDirectories(profiles).OrderBy(path => path).LastOrDefault() ?? "" : "";
            HeadlessHarness.Assert(folder.Length > 0
                && File.Exists(Path.Combine(folder, DebugProfileRecording.CsvFileName))
                && File.Exists(Path.Combine(folder, DebugProfileRecording.SummaryFileName))
                && File.Exists(Path.Combine(folder, DebugProfileRecording.ReportFileName)),
                $"The profile folder is missing or incomplete ({folder}).");
            string[] rows = File.ReadAllLines(Path.Combine(folder, DebugProfileRecording.CsvFileName));
            string[] header = rows[0].Split(',');
            HeadlessHarness.Assert(header.Contains("pgsl_ms") && header.Contains("alloc_kb") && header.Contains("sim_ms") && header.Contains("present_ms"),
                "The profile has no frame phases, PGSL time or allocation: " + rows[0]);
            HeadlessHarness.Assert(rows.Length > 30, $"The profile holds {rows.Length - 1} frames for three seconds of play.");
            int pgsl = Array.IndexOf(header, "pgsl_ms");
            HeadlessHarness.Assert(rows.Skip(1).Any(row => double.TryParse(row.Split(',')[pgsl], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double milliseconds) && milliseconds > 0),
                "No frame of the profile spent any time in PGSL.");
            string report = File.ReadAllText(Path.Combine(folder, DebugProfileRecording.ReportFileName));
            HeadlessHarness.Assert(report.Contains("Worker", StringComparison.Ordinal),
                "The report does not name the Object whose Step ran every frame.");
        });
    }

    // The newest Player within reach (as PgslProjectSuite finds it).
    private static string? NewestPlayer()
    {
        List<string> candidates = [];
        if (RuntimePaths.ResolveRuntimeDir() is { } resolved) candidates.Add(resolved);
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string published = Path.Combine(directory.FullName, "Genesis Application", "Player");
            if (File.Exists(Path.Combine(published, RuntimePaths.RuntimeExeName))) { candidates.Add(published); break; }
        }
        return candidates
            .Where(path => File.Exists(Path.Combine(path, "Genesis.Runtime.dll")))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "Genesis.Runtime.dll")))
            .FirstOrDefault();
    }

    private static string Tail(string log) => log.Length > 2500 ? log[^2500..] : log;
}

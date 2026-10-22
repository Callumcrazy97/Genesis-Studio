using System.Text.RegularExpressions;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The DX12 pipeline library: a game run twice on Direct3D 12 with a fresh shader cache compiles
/// its pipeline states the first time and loads them from the library the second time.
/// </summary>
internal static class Dx12PipelineLibrarySuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.Dx12.PipelinesAreLoadedFromTheLibraryOnTheNextRun", () =>
        {
            string runtime = NewestPlayer() ?? throw new InvalidOperationException("No Player found.");
            string parent = Path.Combine(ctx.Workspace, "Dx12Pipelines");
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Pipeline Game", "Blank");
            ResourceService resources = new(project);
            string objects = Path.Combine(project.AssetsPath, "Objects");
            Directory.CreateDirectory(objects);
            string objectFile = resources.CreateResource(objects, ResourceKind.GameObject, "Quitter");
            File.WriteAllText(objectFile, "{\"schemaVersion\":2,\"dimension\":\"ThreeD\",\"components\":[{\"type\":\"ScriptComponent\",\"props\":{\"ScriptClass\":\"Quitter\"}}],\"events\":[\"Create\",\"Step\"]}");
            Directory.CreateDirectory(Path.Combine(objects, "Quitter"));
            File.WriteAllText(Path.Combine(objects, "Quitter", "Create.pgsl"), "t0 = TimeMs(); done = 0;");
            // Two seconds of a lit 3D room with the engine's sky, then quit.
            File.WriteAllText(Path.Combine(objects, "Quitter", "Step.pgsl"),
                "SetCameraPosition(0, 2, 0); SetCameraTarget(0, 2, 10);\nif (done == 0 && TimeMs() > t0 + 2000) { GameQuit(); done = 1; }");
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
            RoomAsset start = RoomAsset.Create("Start", RoomDimension.ThreeD);
            start.Settings.CaptureMouse = false;
            start.Environment.DynamicSky = true;
            start.Environment.TimeOfDayHours = 12f;
            start.Environment.TimeScale = 0f;
            start.Nodes.Add(new RoomNode
            {
                Kind = RoomNodeKind.GameObject, Name = "Quitter", LayerId = start.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, objectFile).Replace('\\', '/') },
            });
            RoomAssetLoader.Save(start, roomFile);
            ResourceCatalog.Invalidate(project.RootPath);

            string cache = Path.Combine(parent, "ShaderCache");
            string first = RunPlayer(runtime, project.RootPath, cache);
            string second = RunPlayer(runtime, project.RootPath, cache);
            File.WriteAllText(Path.Combine(ctx.Logs, "dx12-pipelines-first.log"), first);
            File.WriteAllText(Path.Combine(ctx.Logs, "dx12-pipelines-second.log"), second);
            (int loaded1, int compiled1) = Counts(first);
            (int loaded2, int compiled2) = Counts(second);
            string files = Directory.Exists(Path.Combine(cache, "DX12Pipelines"))
                ? string.Join(", ", Directory.GetFiles(Path.Combine(cache, "DX12Pipelines")).Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length / 1024 + " KB"))
                : "(no folder)";
            Console.WriteLine($"[DX12 pipelines] first run {loaded1} loaded, {compiled1} compiled; second run {loaded2} loaded, {compiled2} compiled; {files}");
            HeadlessHarness.Assert(compiled1 > 0 && loaded1 == 0, $"The first run should compile its pipelines: {loaded1} loaded, {compiled1} compiled. Log tail:\r\n{Tail(first)}");
            HeadlessHarness.Assert(loaded2 >= compiled1 / 2 && compiled2 < compiled1,
                $"The second run should load what the first compiled: {loaded2} loaded, {compiled2} compiled (first run compiled {compiled1}); {files}. Log tail:\r\n{Tail(second)}");
        });
    }

    private static (int Loaded, int Compiled) Counts(string log)
    {
        Match match = Regex.Matches(log, @"\[DX12\] Pipelines this run: (\d+) loaded from the library, (\d+) compiled").LastOrDefault() ?? Match.Empty;
        return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : (-1, -1);
    }

    private static string Tail(string text) => text.Length <= 3000 ? text : text[^3000..];

    private static string RunPlayer(string runtime, string projectPath, string shaderCache)
    {
        System.Diagnostics.ProcessStartInfo start = new(Path.Combine(runtime, RuntimePaths.RuntimeExeName))
        {
            WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["GENESIS_PROJECT_PATH"] = projectPath;
        start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
        start.Environment["GENESIS_RENDER_BACKEND"] = "Direct3D12";
        start.Environment["GENESIS_SHADER_CACHE"] = shaderCache;
        foreach (string name in new[] { "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED", "GENESIS_DX12_PIPELINE_LIBRARY" })
            start.Environment.Remove(name);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The Player did not start.");
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(90_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(10_000); }
        Thread.Sleep(200);
        string renderLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GenesisRuntime", "Logs", "render-" + process.Id + ".log");
        string text;
        lock (output) text = output.ToString();
        return (File.Exists(renderLog) ? File.ReadAllText(renderLog) : "(no render log at " + renderLog + ")") + Environment.NewLine + text;
    }

    // The newest Player within reach (as PostEffectsSuite finds it).
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
}

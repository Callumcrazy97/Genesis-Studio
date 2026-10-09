using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Export of a game written only in PGSL: no C# is compiled and no <c>GameScripts.dll</c> ships,
/// while the PGSL, the project file (with its chosen backend) and the game settings do, and the
/// exported Player runs the game on its VM. A game with C# scripts is still compiled as before.
/// </summary>
internal static class PgslExportSuite
{
    private const string Marker = "GENESIS_PGSL_EXPORT_OK";
    private const string CreateEvent = "Print(\"" + Marker + "\");\n";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Export.PgslOnly");
        ProjectSession? project = null;
        GameExportResult? export = null;
        string pgslFile = string.Empty;

        HeadlessHarness.RunCase(ctx.Report, "Export.PgslOnly.SkipsCSharpCompile", () =>
        {
            project = BuildProject(ctx, "PgslOnly", out pgslFile);
            HeadlessHarness.Assert(!ProjectRunLauncher.HasCSharpScripts(project.RootPath),
                "A project written only in PGSL was reported as having C# scripts.");
            List<string> steps = [];
            export = GameExportService.Export(
                new(project, Path.Combine(ctx.OutputRoot, "PgslOnlyGame"), GameExportFormat.Folder, PrecompileShaders: false),
                new InlineProgress(steps.Add));
            File.WriteAllLines(Path.Combine(ctx.Logs, "pgsl-export-steps.txt"), steps);
            HeadlessHarness.Assert(export.Success, "The PGSL-only export failed: " + export.ErrorMessage);
            HeadlessHarness.Assert(!export.CompiledCSharpScripts, "The export compiled C# for a game written only in PGSL.");
            HeadlessHarness.Assert(!steps.Contains("Compiling game scripts…") && steps.Contains("Checking PGSL scripts…"),
                "The export did not take the PGSL-only path: " + string.Join(" | ", steps));
            HeadlessHarness.Assert(!File.Exists(Path.Combine(export.OutputPath, "GameScripts.dll")),
                "A PGSL-only export shipped GameScripts.dll.");

            string relative = Path.GetRelativePath(project.RootPath, pgslFile);
            string exportedPgsl = Path.Combine(export.OutputPath, relative);
            HeadlessHarness.Assert(File.Exists(exportedPgsl) && File.ReadAllText(exportedPgsl) == File.ReadAllText(pgslFile),
                "The export did not keep the PGSL source: " + relative);

            string projectFile = Path.Combine(export.OutputPath, Path.GetRelativePath(project.RootPath, project.ProjectFile));
            HeadlessHarness.Assert(File.Exists(projectFile) && File.ReadAllText(projectFile) == File.ReadAllText(project.ProjectFile),
                "The exported project file differs from the project's own, so its chosen backend may not travel.");

            string settings = Path.Combine(export.OutputPath, "GenesisGame.json");
            HeadlessHarness.Assert(File.Exists(settings), "The export wrote no GenesisGame.json.");
            using JsonDocument game = JsonDocument.Parse(File.ReadAllText(settings));
            HeadlessHarness.Assert(game.RootElement.GetProperty("title").GetString() == project.Manifest.Name,
                "GenesisGame.json does not name the game.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Export.PgslOnly.PlayerRunsWithoutGameScripts", () =>
        {
            HeadlessHarness.Assert(export is { Success: true }, "The PGSL-only export did not succeed.");
            string executable = Path.Combine(export!.OutputPath, export.ExecutableName);
            if (!File.Exists(executable)) throw new CheckNotRunException("The export has no Player executable to start.");
            ProcessStartInfo start = new(executable)
            {
                WorkingDirectory = export.OutputPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--autoshot");
            start.ArgumentList.Add("3");
            start.Environment["GENESIS_RENDER_BACKEND"] = "DX11";
            start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
            foreach (string name in new[] { "GENESIS_PROJECT_PATH", "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED", RuntimePaths.GameScriptsEnvironmentVariable })
                start.Environment.Remove(name);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The exported Player did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            string log = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(ctx.Logs, "pgsl-export-player.log"), log);
            HeadlessHarness.Assert(process.ExitCode == 0 && log.Contains(Marker, StringComparison.Ordinal),
                "The exported PGSL-only game did not run its Create event: " + Tail(log));
            HeadlessHarness.Assert(!log.Contains("SCRIPT ERROR", StringComparison.OrdinalIgnoreCase),
                "A script reported an error: " + Tail(log));
            HeadlessHarness.Assert(!log.Contains("loaded GameScripts.dll", StringComparison.Ordinal),
                "The exported Player loaded compiled scripts for a game written only in PGSL.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Export.CSharpScripts.StillCompileGameScripts", () =>
        {
            ProjectSession scripted = BuildProject(ctx, "WithCSharp", out _);
            string scripts = Path.Combine(scripted.AssetsPath, "Scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "ExportProbe.cs"),
                "namespace ExportProbeGame { public static class ExportProbe { public static int Answer() => 42; } }\n");
            // The project's list of resources was made before this file existed.
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(scripted.RootPath);
            HeadlessHarness.Assert(ProjectRunLauncher.HasCSharpScripts(scripted.RootPath),
                "A project with Assets/Scripts/ExportProbe.cs was not reported as having C# scripts.");
            List<string> steps = [];
            GameExportResult compiled = GameExportService.Export(
                new(scripted, Path.Combine(ctx.OutputRoot, "CSharpGame"), GameExportFormat.Folder, PrecompileShaders: false),
                new InlineProgress(steps.Add));
            HeadlessHarness.Assert(compiled.Success, "The export with C# scripts failed: " + compiled.ErrorMessage);
            HeadlessHarness.Assert(compiled.CompiledCSharpScripts && steps.Contains("Compiling game scripts…"),
                "The export did not compile the project's C# scripts: " + string.Join(" | ", steps));
            string dll = Path.Combine(compiled.OutputPath, "GameScripts.dll");
            HeadlessHarness.Assert(File.Exists(dll) && new FileInfo(dll).Length > 1024,
                "The export with C# scripts did not ship GameScripts.dll.");
            HeadlessHarness.Assert(!File.Exists(Path.Combine(compiled.OutputPath, "Assets", "Scripts", "ExportProbe.cs")),
                "The export shipped C# source.");
        });

        RunShaderCases(ctx);
    }

    /// <summary>
    /// An exported game carries its own shaders compiled for every backend, under the keys the
    /// game computes where it is installed, so its first start compiles none of them.
    /// </summary>
    private static void RunShaderCases(HeadlessContext ctx)
    {
        GameExportResult? shaded = null;
        HeadlessHarness.RunCase(ctx.Report, "Export.Shaders.ProjectShadersCookedForEveryBackend", () =>
        {
            ProjectSession project = BuildProject(ctx, "Shaded", out _);
            ProjectShaderFixtures.Write(project.RootPath);
            Stopwatch timer = Stopwatch.StartNew();
            shaded = GameExportService.Export(new(project, Path.Combine(ctx.OutputRoot, "ShadedGame"), GameExportFormat.Folder));
            timer.Stop();
            HeadlessHarness.Assert(shaded.Success, "The export with project shaders failed: " + shaded.ErrorMessage);
            HeadlessHarness.Assert(shaded.ShaderFailures is { Count: 0 },
                "Project shader programs did not compile at export: " + string.Join(" | ", shaded.ShaderFailures ?? []));

            // Listed and keyed as the exported game itself lists and keys them, from where it is.
            string game = shaded.OutputPath;
            string cache = Path.Combine(game, ".genesis-shaders");
            IReadOnlyList<Genesis.Runtime.Rendering.ProjectShaderProgram> programs =
                Genesis.Runtime.Rendering.ProjectShaderPrograms.Enumerate(game, everyVariant: true);
            HeadlessHarness.Assert(programs.Count == ProjectShaderFixtures.EveryVariantPrograms,
                $"The exported game lists {programs.Count} shader programs, expected {ProjectShaderFixtures.EveryVariantPrograms}.");
            var missing = new List<string>();
            foreach (Genesis.Runtime.Rendering.ProjectShaderProgram program in programs)
            {
                foreach (Genesis.Rendering.Abstractions.GpuShaderBinaryFormat format in Genesis.Rendering.Primitives.PrecompiledShaders.AllFormats)
                {
                    string key = Genesis.Rendering.Primitives.ShaderCompiler.CacheKey(program.Source, program.Entry, program.Stage, format,
                        program.ShaderPath, Genesis.Rendering.Primitives.ShaderCompiler.BuildDefaultIncludeSearchPaths(program.ShaderPath, game));
                    string file = Path.Combine(cache, Genesis.Rendering.Primitives.ShaderBinaryCache.RelativePath(key, format)
                        .Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(file) || new FileInfo(file).Length == 0)
                        missing.Add($"{program.Name} {program.Entry} variant '{program.Variant}' ({format})");
                }
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "export-shader-cook.txt"),
                new[] { $"export took {timer.Elapsed.TotalSeconds:F1} s; {shaded.ShadersCooked} shader entries; missing {missing.Count}" }.Concat(missing));
            HeadlessHarness.Assert(missing.Count == 0, $"{missing.Count} project shader programs are not in the exported game's cache: "
                + string.Join("; ", missing.Take(6)));
        });
    }

    /// <summary>A Blank project whose start room holds one Object with a PGSL Create event.</summary>
    private static ProjectSession BuildProject(HeadlessContext ctx, string name, out string pgslFile)
    {
        string parent = Path.Combine(ctx.Workspace, "PgslExport", name);
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        Directory.CreateDirectory(parent);
        ProjectService projects = new();
        ProjectSession project = projects.CreateProject(parent, "PGSL Export " + name, "Blank");
        // A named backend, so the exported project file shows the choice travelled with it.
        project.Manifest.Rendering.Backend = "DX11";
        projects.Save(project);

        ResourceService resources = new(project);
        string objects = Path.Combine(project.AssetsPath, "Objects");
        Directory.CreateDirectory(objects);
        string objectFile = resources.CreateResource(objects, ResourceKind.GameObject, "Export probe");
        File.WriteAllText(objectFile, new JsonObject
        {
            ["schemaVersion"] = 2, ["dimension"] = "TwoD",
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent", ["props"] = new JsonObject { ["ScriptClass"] = "Export probe" },
            }),
            ["events"] = new JsonArray("Create"),
        }.ToJsonString());
        string events = Path.Combine(objects, "Export probe");
        Directory.CreateDirectory(events);
        pgslFile = Path.Combine(events, "Create.pgsl");
        File.WriteAllText(pgslFile, CreateEvent);

        string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
        RoomAsset room = RoomAssetLoader.Parse(roomFile);
        room.Settings.TargetFps = 30;
        room.Settings.CaptureMouse = false;
        room.Nodes.Add(new RoomNode
        {
            Kind = RoomNodeKind.GameObject, Name = "Export probe", LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, objectFile).Replace('\\', '/') },
        });
        RoomAssetLoader.Save(room, roomFile);
        Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);
        return project;
    }

    private static string Tail(string log) => log.Length > 1500 ? log[^1500..] : log;

    /// <summary>Records progress on the exporting thread, in order.</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

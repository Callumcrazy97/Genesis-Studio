using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime;
using Genesis.Runtime.Project;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// How long Genesis keeps its user waiting, measured on the 3D Nature Walk template: opening the
/// project in Studio, opening each editor (twice: the first open of a session pays for the graphics
/// device, the second is what every later open costs), the work F5 does before the game starts, and
/// the Player's start (first loading frame, first game frame) and its idle allocation rate. Every
/// number is written to <c>Logs\speed-timings.json</c>; the assertions are ceilings that only catch
/// a gross regression, so the suite can run on a busy machine.
/// </summary>
internal static class SpeedSuite
{
    private static readonly List<(string Name, double Value, string Unit)> Results = [];

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Speed");
        Results.Clear();
        string parent = Path.Combine(ctx.Workspace, "Speed");
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        StudioServices? services = null;

        HeadlessHarness.RunCase(ctx.Report, "Speed.ProjectOpen.NatureWalk", () =>
        {
            ProjectSession created = new ProjectService().CreateProject(parent, "Nature", "3DNatureWalk");
            services = new StudioServices(new SettingsService(Path.Combine(parent, "settings.json")),
                new ProjectService(), new ProjectValidator(), new StudioLog());
            services.Settings.Current.Editing.AutoSave = false;
            services.Settings.Current.General.CheckForExternalChanges = false;

            // A cold open, as after starting Studio: nothing of this project is cached in the process.
            ResourceNames.Invalidate(created.RootPath);
            Stopwatch clock = Stopwatch.StartNew();
            project = services.Projects.OpenProject(created.ProjectFile);
            Record("Project open: read and upgrade the project", clock.Elapsed.TotalMilliseconds);

            clock.Restart();
            using StudioShellForm shell = new(services, project, persistLayout: false);
            Record("Project open: build the Studio window", clock.Elapsed.TotalMilliseconds);

            clock.Restart();
            GateSuite.ShowHost(shell);
            System.Windows.Forms.Application.DoEvents();
            Record("Project open: show the Studio window", clock.Elapsed.TotalMilliseconds);
            double shown = Total("Project open:");
            Record("Project open: total until the window is usable", shown);

            // What still runs after the window is shown (previews, the dependency graph): pumped
            // until the browser has nothing left to do.
            clock.Restart();
            for (int i = 0; i < 400 && shell.AssetBrowser.PreviewBusy; i++) GateSuite.Pump(1, 5);
            Record("Project open: background work after showing", clock.Elapsed.TotalMilliseconds);
            HeadlessHarness.Assert(shell.AssetBrowser.ResourceTreeSnapshot is not null, "The resource tree was not built.");
            HeadlessHarness.Assert(shown < 60_000, $"Opening the project took {shown:F0} ms.");
        });

        // GENESIS_SPEED_ONLY=Terrain,Room limits a local investigation to some of the editors.
        string[] only = (Environment.GetEnvironmentVariable("GENESIS_SPEED_ONLY") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string editor in new[] { "ModelViewer", "ModelEditor", "Room", "Terrain", "Shader", "Particle" })
        {
            if (only.Length > 0 && !only.Contains(editor, StringComparer.OrdinalIgnoreCase)) continue;
            HeadlessHarness.RunCase(ctx.Report, "Speed.EditorOpen." + editor, () =>
            {
                HeadlessHarness.Assert(project != null, "The project did not open.");
                double first = OpenEditor(project!, editor, "first");
                double again = OpenEditor(project!, editor, "again");
                HeadlessHarness.Assert(first < 60_000 && again < 60_000, $"{editor} took {first:F0} ms, then {again:F0} ms, to open.");
            });
        }

        HeadlessHarness.RunCase(ctx.Report, "Speed.Run.WorkBeforeTheGameStarts", () =>
        {
            HeadlessHarness.Assert(project != null, "The project did not open.");
            Stopwatch clock = Stopwatch.StartNew();
            ProjectRunLauncher.CompileOutcome compile = ProjectRunLauncher.CompileScripts(project!.RootPath);
            Record("Run (F5): check and compile scripts", clock.Elapsed.TotalMilliseconds);
            HeadlessHarness.Assert(compile.Success, "The project's scripts did not compile: " + compile.ErrorMessage);
        });

        if (only.Length == 0 || only.Contains("Player", StringComparer.OrdinalIgnoreCase))
        {
            HeadlessHarness.RunCase(ctx.Report, "Speed.Player.StartAndIdleAllocations", () =>
            {
                HeadlessHarness.Assert(project != null, "The project did not open.");
                RunPlayer(ctx, project!);
            });
        }

        WriteResults(ctx);
    }

    private static double OpenEditor(ProjectSession project, string editor, string pass)
    {
        string root = project.RootPath;
        string assets = project.AssetsPath;
        string path = editor switch
        {
            "ModelViewer" or "ModelEditor" => Path.Combine(assets, "Models", "AncientOak.model.json"),
            "Room" => Path.Combine(assets, "Rooms", "VerdantHollow.room.json"),
            "Terrain" => Path.Combine(assets, "Terrain", "VerdantHollowTerrain.terrain.json"),
            "Shader" => Path.Combine(assets, "Shaders", "FoliageWind.shader.json"),
            _ => Path.Combine(assets, "Particles", "CampfireSparks.particle.json"),
        };
        HeadlessHarness.Assert(File.Exists(path), "The template has no " + path);

        Stopwatch clock = Stopwatch.StartNew();
        using Control control = editor switch
        {
            "ModelViewer" => new ModelViewerControl(path, root),
            "ModelEditor" => new ModelEditorControl(path, root),
            "Room" => new RoomEditorControl(path, root),
            "Terrain" => new TerrainEditorControl(path, root),
            "Shader" => new ShaderEditorControl(path, root),
            _ => new ParticleEditorControl(path, root),
        };
        double constructed = clock.Elapsed.TotalMilliseconds;

        using var host = UnattendedWindowing.NewHost(1400, 900);
        clock.Restart();
        control.Dock = DockStyle.Fill;
        host.Controls.Add(control);
        ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host);
        System.Windows.Forms.Application.DoEvents();
        double shown = clock.Elapsed.TotalMilliseconds;

        EditorViewport3D viewport = control switch
        {
            ModelViewerControl model => model.Viewport,
            RoomEditorControl room => room.Viewport,
            TerrainEditorControl terrain => terrain.Viewport,
            ShaderEditorControl shader => shader.Viewport,
            ParticleEditorControl particle => particle.Viewport,
            _ => throw new InvalidOperationException(),
        };
        clock.Restart();
        using (viewport.CaptureFrame(0)) { }
        double frame = clock.Elapsed.TotalMilliseconds;

        string label = $"Editor open ({pass}): {editor}";
        Record(label + " - create", constructed);
        Record(label + " - show and start the viewport", shown);
        Record(label + " - first frame", frame);
        Record(label + " - total", constructed + shown + frame);
        return constructed + shown + frame;
    }

    private static void RunPlayer(HeadlessContext ctx, ProjectSession project)
    {
        string? runtime = NewestPlayer();
        if (runtime == null) throw new CheckNotRunException("No built Player was found (build Source\\Genesis.Player first).");
        File.WriteAllText(Path.Combine(ctx.Logs, "speed-player-path.txt"), runtime);
        string log = Path.Combine(project.RootPath, "Debug", "Logs", "project_player.log");
        if (File.Exists(log)) File.Delete(log);

        ProcessStartInfo start = new(Path.Combine(runtime, RuntimePaths.RuntimeExeName))
        {
            WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["GENESIS_PROJECT_PATH"] = project.RootPath;
        start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
        start.Environment["GENESIS_RENDER_BACKEND"] = "DX11";
        start.Environment[AllocationRateLog.EnvironmentVariable] = "1";
        foreach (string name in new[] { "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(name);

        DateTime started = DateTime.Now;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The Player did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Stopwatch clock = Stopwatch.StartNew();
        string text = "";
        while (clock.Elapsed.TotalSeconds < 90 && !process.HasExited)
        {
            Thread.Sleep(250);
            text = ReadShared(log);
            if (CountOf(text, "Managed allocations:") >= 3) break;
        }
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        text = ReadShared(log);
        File.WriteAllText(Path.Combine(ctx.Logs, "speed-player.log"), text);

        double? At(string marker)
        {
            foreach (string line in text.Split('\n'))
            {
                if (!line.Contains(marker, StringComparison.Ordinal) || line.Length < 12) continue;
                if (!TimeSpan.TryParseExact(line[..12], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan time)) continue;
                double ms = (started.Date + time - started).TotalMilliseconds;
                return ms < -60_000 ? ms + TimeSpan.FromDays(1).TotalMilliseconds : ms;
            }
            return null;
        }

        void Mark(string label, string marker)
        {
            if (At(marker) is double ms) Record(label, ms);
        }

        Mark("Player start: process running and log open", "project=");
        Mark("Player start: first loading frame shown", "Loading screen shown");
        Mark("Player start: graphics device ready", "texture groups stitched");
        Mark("Player start: first room built", "room loaded entities=");
        Mark("Player start: start-up screen finished", "Runtime boot ready");
        Mark("Player start: first room ready to play", "First room:");
        HeadlessHarness.Assert(At("Runtime boot ready") != null, "The Player never finished starting (see speed-player.log).");

        List<double> perFrame = [];
        foreach (string line in text.Split('\n'))
        {
            int at = line.IndexOf(" KB a frame", StringComparison.Ordinal);
            if (at < 0) continue;
            int open = line.LastIndexOf('(', at);
            if (open >= 0 && double.TryParse(line[(open + 1)..at], NumberStyles.Float, CultureInfo.InvariantCulture, out double kb))
                perFrame.Add(kb);
        }
        HeadlessHarness.Assert(perFrame.Count > 0, "The Player wrote no allocation rate (see speed-player.log).");
        double steady = perFrame.Min();
        Record("Player idle: managed allocation per frame", steady, "KB");
        HeadlessHarness.Assert(steady < 1024, $"An idle game allocates {steady:F0} KB a frame.");
    }

    private static string ReadShared(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The most recently built Player: the one beside the sources, or a published one.</summary>
    private static string? NewestPlayer()
    {
        List<string> candidates = [];
        string? asked = Environment.GetEnvironmentVariable("GENESIS_SPEED_PLAYER");
        if (!string.IsNullOrWhiteSpace(asked)) candidates.Add(asked);
        if (RuntimePaths.ResolveRuntimeDir() is { } resolved) candidates.Add(resolved);
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string built = Path.Combine(directory.FullName, "Source", "Genesis.Player", "bin", "Release", "net10.0-windows");
            if (File.Exists(Path.Combine(built, RuntimePaths.RuntimeExeName))) { candidates.Add(built); break; }
        }
        return candidates
            .Where(path => File.Exists(Path.Combine(path, RuntimePaths.RuntimeExeName)) && File.Exists(Path.Combine(path, "Genesis.Runtime.dll")))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "Genesis.Runtime.dll")))
            .FirstOrDefault();
    }

    private static void Record(string name, double value, string unit = "ms")
    {
        Results.Add((name, value, unit));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  TIMING {name}: {value:F0} {unit}"));
    }

    private static double Total(string prefix) =>
        Results.Where(result => result.Name.StartsWith(prefix, StringComparison.Ordinal) && result.Unit == "ms").Sum(result => result.Value);

    private static void WriteResults(HeadlessContext ctx)
    {
        var rows = Results.Select(result => new { result.Name, Value = Math.Round(result.Value, 1), result.Unit });
        File.WriteAllText(Path.Combine(ctx.Logs, "speed-timings.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}

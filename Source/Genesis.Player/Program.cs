using Genesis.Runtime.Project;

namespace Genesis.Player;

/// <summary>
/// The Ember game player (GenesisEngine.exe). Studio F5/F6 launches this beside a project;
/// exported games ship the same binary next to their content. All behaviour lives in
/// <see cref="ProjectPlayerApp"/> inside the integrated runtime.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // GenesisEngine.exe --relay [port]: a relay for games played over the internet, no game.
        int relayAt = Array.FindIndex(args, argument => string.Equals(argument, "--relay", StringComparison.OrdinalIgnoreCase));
        if (relayAt >= 0)
            return RunRelay(relayAt + 1 < args.Length && int.TryParse(args[relayAt + 1], out int relayPort) ? relayPort : 7777);

        // The start-up trace's first line comes before any engine assembly is loaded, so a start that
        // stops before it never reached this program's code at all (the runtime's own start).
        string? trace = Environment.GetEnvironmentVariable("GENESIS_STARTUP_TRACE");
        if (!string.IsNullOrEmpty(trace))
        {
            try { File.AppendAllText(trace, "Main entered" + Environment.NewLine); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Genesis.Shared.Diagnostics.LoadProfile.Trace("engine diagnostics loaded");
        // .NET's own mode (Interactive, with background collections): full collections already run
        // beside the game rather than stopping it. SustainedLowLatency was the default until 23 Oct
        // 2026; measured on a game's garbage (30 to 90 MB/s over a 400 MB heap with a turning-over
        // cache) it saved no pause time and gave more frames over 8 ms (25-30 against 18-23 in 40 s),
        // and GenesisCraft saw longer pauses with it. The pauses that remain are short collections,
        // which only allocating less shortens. GENESIS_GC_LATENCY=sustained puts it back, to compare.
        System.Runtime.GCSettings.LatencyMode = string.Equals(Environment.GetEnvironmentVariable("GENESIS_GC_LATENCY"), "sustained", StringComparison.OrdinalIgnoreCase)
            ? System.Runtime.GCLatencyMode.SustainedLowLatency
            : System.Runtime.GCLatencyMode.Interactive;
        // Packaging step: compile the engine's built-in shaders into the folder shipped beside
        // Studio and the Player. Opens no window and plays nothing.
        if (args.Length > 0 && string.Equals(args[0], Genesis.Rendering.Primitives.PrecompiledShaders.CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            return Genesis.Rendering.Primitives.PrecompiledShaders.RunCommandLine(args);

        string packagedShaderCache = Path.Combine(AppContext.BaseDirectory, ".genesis-shaders");
        if (Directory.Exists(packagedShaderCache)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GENESIS_SHADER_CACHE")))
        {
            Environment.SetEnvironmentVariable("GENESIS_SHADER_CACHE", packagedShaderCache);
        }

        bool debug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        bool supervised = int.TryParse(Environment.GetEnvironmentVariable("GENESIS_EDITOR_PID"), out int editorPid)
            && long.TryParse(Environment.GetEnvironmentVariable("GENESIS_EDITOR_STARTED"), out _);
        long.TryParse(Environment.GetEnvironmentVariable("GENESIS_EDITOR_STARTED"), out long editorStarted);
        // An unattended run (tests, measurements) reports in its log and exit code; a dialog would
        // stop it and appear on the desktop of whoever is using the PC.
        bool quiet = supervised || Environment.GetEnvironmentVariable("GENESIS_UNATTENDED_WINDOW") == "1"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GENESIS_AUTOSHOT"));
        using var parentWatch = new System.Threading.Timer(_ =>
        {
            if (!supervised) return;
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(editorPid);
                if (!parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == editorStarted) return;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            // The original editor has gone (including a crash). Never leave its player orphaned.
            Environment.Exit(0);
        }, null, 1000, 1000);
        if (supervised)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is string command)
                    {
                        if (command == "pause") ProjectPlayerApp.SetPaused(true);
                        else if (command == "resume") ProjectPlayerApp.SetPaused(false);
                        else if (command == "stop") ProjectPlayerApp.RequestStop();
                    }
                    ProjectPlayerApp.RequestStop();
                }
                catch (IOException) { ProjectPlayerApp.RequestStop(); }
            });
        }

        // A GUI-subsystem crash otherwise vanishes into Event Viewer with no context. Writing the
        // exception beside the player means the failure is visible where the game was launched.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashReport(e.ExceptionObject);

        if (debug)
        {
            // Launching with the debugger implies wanting the graphics validation layers too;
            // without them a Vulkan fault reports nothing at all.
            Environment.SetEnvironmentVariable("GENESIS_VULKAN_DEBUG", "1");
        }

        try
        {
            int code = ProjectPlayerApp.Run(args);
            if (code != 0)
            {
                string error = ProjectPlayerApp.LastError ?? $"The game could not start (exit code {code}). Check the project and its start room.";
                WriteCrashReport(error);
                if (!quiet)
                    MessageBox.Show(error + ReportNote(), "Genesis Player — game error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return code;
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed: an exception escaping here is what the user sees as a
            // silent hang, and the stack is the only thing that identifies the cause.
            WriteCrashReport(ex);
            if (!quiet) MessageBox.Show(ex.Message + ReportNote(), "Genesis Player — game error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
    }

    private static string ReportNote() =>
        _lastCrashReport == null ? "" : $"{Environment.NewLine}{Environment.NewLine}A report was saved to {_lastCrashReport}";

    /// <summary>Where the last crash report was written, for the error dialog to name; null before one is.</summary>
    private static string? _lastCrashReport;

    private static void WriteCrashReport(object? error)
    {
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] GenesisEngine crashed{Environment.NewLine}{CrashContext()}{error}";
        Console.Error.WriteLine(text);

        // Beside the game first, where its author looks. A game installed under Program Files may
        // not write there, and the report is the only record of the failure, so it then goes to
        // the player's own folder.
        foreach (string folder in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genesis", "CrashReports"),
                 })
        {
            try
            {
                Directory.CreateDirectory(folder);
                string report = Path.Combine(folder, "GenesisEngine.crash.log");
                File.AppendAllText(report, text + Environment.NewLine + Environment.NewLine);
                _lastCrashReport = report;
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // The report is best-effort; a locked or read-only directory must not mask the
                // original failure with a second one.
            }
        }
    }

    // What a report needs to be acted on: which game and version, which engine build and renderer,
    // the room and the machine, and what the game logged last.
    private static string CrashContext()
    {
        var context = new System.Text.StringBuilder();
        try
        {
            string project = Genesis.Runtime.Scripting.PgslCommands.ProjectPath ?? "";
            (string name, string version) = Genesis.Runtime.Project.ProjectPaths.ReadGameInfo(project);
            string engine = typeof(Genesis.Runtime.Project.ProjectPlayerApp).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                ?? typeof(Genesis.Runtime.Project.ProjectPlayerApp).Assembly.GetName().Version?.ToString() ?? "unknown";
            context.AppendLine($"Game: {(name.Length > 0 ? name : "unknown")} {version}");
            context.AppendLine($"Engine: {engine}, built {File.GetLastWriteTime(typeof(Genesis.Runtime.Project.ProjectPlayerApp).Assembly.Location):yyyy-MM-dd HH:mm}");
            context.AppendLine($"Renderer: {Environment.GetEnvironmentVariable("GENESIS_RENDER_BACKEND") ?? "default"}");
            context.AppendLine($"Room: {Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext?.Room?.Name ?? "none"}");
            context.AppendLine($"System: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}, {Environment.ProcessorCount} threads");
            string[] tail = Genesis.Runtime.Project.ProjectLogger.Tail(40);
            if (tail.Length > 0)
            {
                context.AppendLine("Last lines of the game's log:");
                foreach (string line in tail) context.AppendLine("  " + line);
            }
        }
        catch (Exception exception)
        {
            // A report that cannot gather its context still reports the failure.
            context.AppendLine("(context unavailable: " + exception.Message + ")");
        }
        return context.ToString();
    }

    /// <summary>Runs a relay (GenesisRelayServer) on a UDP port until the process is stopped.</summary>
    private static int RunRelay(int port)
    {
        using var relay = new Genesis.Net.GenesisRelayServer();
        if (port < 1 || port > 65535 || !relay.Start(port))
        {
            Console.Error.WriteLine($"Genesis relay: UDP port {port} could not be opened.");
            return 1;
        }
        Console.WriteLine($"Genesis relay on UDP port {port}: games host with NetHostOnline and join with NetJoinOnline. Ctrl+C stops it.");
        bool running = true;
        Console.CancelKeyPress += (_, stop) => { stop.Cancel = true; running = false; };
        while (running)
        {
            relay.Update();
            System.Threading.Thread.Sleep(5);
        }
        return 0;
    }
}

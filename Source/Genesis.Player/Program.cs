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
        // A game, not a batch job: full collections run in the background rather than stopping every
        // thread (GenesisCraft streaming chunks: a blocking gen 2 froze one frame for 561 ms). Only a
        // collection Windows forces under real memory pressure still blocks.
        // GENESIS_GC_LATENCY=interactive keeps .NET's default mode, for comparing the two.
        System.Runtime.GCSettings.LatencyMode = string.Equals(Environment.GetEnvironmentVariable("GENESIS_GC_LATENCY"), "interactive", StringComparison.OrdinalIgnoreCase)
            ? System.Runtime.GCLatencyMode.Interactive
            : System.Runtime.GCLatencyMode.SustainedLowLatency;
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
                    MessageBox.Show(error, "Genesis Player — game error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return code;
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed: an exception escaping here is what the user sees as a
            // silent hang, and the stack is the only thing that identifies the cause.
            WriteCrashReport(ex);
            if (!quiet) MessageBox.Show(ex.Message, "Genesis Player — game error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
    }

    private static void WriteCrashReport(object? error)
    {
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] GenesisEngine crashed{Environment.NewLine}{error}";
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
                File.AppendAllText(Path.Combine(folder, "GenesisEngine.crash.log"), text + Environment.NewLine + Environment.NewLine);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // The report is best-effort; a locked or read-only directory must not mask the
                // original failure with a second one.
            }
        }
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

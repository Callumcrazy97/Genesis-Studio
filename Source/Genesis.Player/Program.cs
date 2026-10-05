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
}

using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio.Forms;

namespace Genesis.Application.Studio;

internal sealed class StudioApplicationContext : ApplicationContext
{
    private readonly StudioServices _services;
    private readonly string[] _arguments;
    private bool _transitioning;

    public StudioApplicationContext(StudioServices services, string[] arguments)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _arguments = arguments ?? [];
        Start();
    }

    private void Start()
    {
        string? projectArgument = FindProjectArgument();
        if (!string.IsNullOrWhiteSpace(projectArgument))
        {
            try
            {
                ProjectSession project = _services.Projects.OpenProject(projectArgument);
                _services.Settings.AddRecentProject(project.Manifest.Name, project.ProjectFile);
                ShowStudio(project, null);
                return;
            }
            catch (Exception exception)
            {
                _services.Log.Error("Startup", "The requested project could not be opened.", exception);
            }
        }

        bool skipSplash = _arguments.Any(
            argument => string.Equals(argument, "--no-splash", StringComparison.OrdinalIgnoreCase));
        if (_services.Settings.Current.General.ShowSplashScreen && !skipSplash)
        {
            ShowSplash();
        }
        else if (TryOpenLastProject(null))
        {
            return;
        }
        else
        {
            ShowProjectHub(null);
        }
    }

    private bool TryOpenLastProject(Form? previous)
    {
        if (!_services.Settings.Current.General.ReopenLastProject)
        {
            return false;
        }

        RecentProject? recent = _services.Settings.Current.RecentProjects
            .FirstOrDefault(entry => File.Exists(entry.ProjectFile));
        if (recent is null)
        {
            return false;
        }

        try
        {
            ProjectSession project = _services.Projects.OpenProject(recent.ProjectFile);
            _services.Settings.AddRecentProject(project.Manifest.Name, project.ProjectFile);
            ShowStudio(project, previous);
            return true;
        }
        catch (Exception exception)
        {
            _services.Log.Warning(
                "Startup",
                $"Could not reopen '{recent.Name}': {exception.Message}");
            return false;
        }
    }

    private void ShowSplash()
    {
        SplashForm splash = new();
        MainForm = splash;
        splash.Completed += (_, _) =>
        {
            if (TryOpenLastProject(splash))
            {
                return;
            }

            ShowProjectHub(splash);
        };
        splash.FormClosed += (_, _) =>
        {
            if (!_transitioning && ReferenceEquals(MainForm, splash))
            {
                ExitThread();
            }
        };
        splash.Show();
        splash.RunStartup(BuildStartupSequence());
    }

    /// <summary>
    /// The work that genuinely happens between launching and being able to use Genesis.
    /// </summary>
    /// <remarks>
    /// Every step here is real and is done once, up front, rather than paid for later by whoever
    /// happens to open the first script or press F5 — which is what made those actions feel slow
    /// while the splash sat there animating a bar that measured nothing. The PGSL command registry
    /// in particular reflects several hundred commands into a dispatch table; doing it here means
    /// the first Object Editor opens against a warm engine.
    /// </remarks>
    private StartupSequence BuildStartupSequence()
    {
        StartupSequence sequence = new(_services.Log);

        sequence.Add("Applying your theme…", () =>
            Theme.ThemeService.ApplySettings(_services.Settings.Current));

        sequence.Add("Preparing the rendering backend…", () =>
            RenderingPreferencesBridge.Apply(_services.Settings.Current.Rendering));

        sequence.Add("Building the PGSL command surface…", () =>
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize());

        sequence.Add("Locating the Ember player…", () =>
        {
            string? runtimeDirectory = Genesis.Runtime.RuntimePaths.ResolveRuntimeDir();
            _services.Log.Information(
                "Startup",
                runtimeDirectory is null
                    ? "GenesisEngine.exe was not found; F5 will report it rather than fail silently."
                    : $"Ember player: {runtimeDirectory}");
        });

        sequence.Add("Checking your recent projects…", () =>
            _services.Settings.RemoveMissingRecentProjects());

        return sequence;
    }

    private void ShowProjectHub(Form? previous)
    {
        TransitionFrom(previous);
        ProjectHubForm hub = new(_services);
        MainForm = hub;
        hub.ProjectOpened += (_, args) => ShowStudio(args.Session, hub);
        hub.FormClosed += (_, _) =>
        {
            if (!_transitioning && ReferenceEquals(MainForm, hub))
            {
                ExitThread();
            }
        };
        hub.Show();
        CompleteTransition(previous);
    }

    private void ShowStudio(ProjectSession project, Form? previous)
    {
        // Build the next window before hiding the hub. A resource error must leave a
        // visible, usable window instead of an invisible message loop in Task Manager.
        StudioShellForm? studio = null;
        try
        {
            studio = new(_services, project);
            TransitionFrom(previous);
            MainForm = studio;
            studio.CloseProjectRequested += (_, _) => ShowProjectHub(studio);
            studio.SwitchProjectRequested += (_, args) => ShowStudio(args.Session, studio);
            studio.FormClosed += (_, _) =>
            {
                if (!_transitioning && ReferenceEquals(MainForm, studio)) ExitThread();
            };
            studio.Show();
            CompleteTransition(previous);
        }
        catch
        {
            studio?.Dispose();
            _transitioning = false;
            if (previous != null && !previous.IsDisposed) { MainForm = previous; previous.Show(); }
            throw;
        }
    }

    private void TransitionFrom(Form? previous)
    {
        _transitioning = true;
        previous?.Hide();
    }

    private void CompleteTransition(Form? previous)
    {
        if (previous is not null && !previous.IsDisposed)
        {
            previous.Close();
            previous.Dispose();
        }

        _transitioning = false;
    }

    private string? FindProjectArgument()
    {
        for (int index = 0; index < _arguments.Length; index++)
        {
            if (string.Equals(_arguments[index], "--project", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < _arguments.Length)
            {
                return _arguments[index + 1];
            }

            if (_arguments[index].EndsWith(
                    ProjectService.ProjectExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _arguments[index];
            }
        }

        return null;
    }
}

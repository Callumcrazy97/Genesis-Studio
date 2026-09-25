using Genesis.Application.Core;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;

namespace Genesis.Application.Studio;

public sealed class StudioServices
{
    public StudioServices(
        SettingsService settings,
        ProjectService projects,
        ProjectValidator validator,
        StudioLog log)
    {
        Settings = settings;
        Projects = projects;
        Validator = validator;
        Log = log;
    }

    public SettingsService Settings { get; }

    public ProjectService Projects { get; }

    public ProjectValidator Validator { get; }

    public StudioLog Log { get; }

    public static StudioServices CreateDefault()
    {
        ApplicationPaths.EnsureUserDirectories();
        StudioLog log = new(ApplicationPaths.SessionLogFile);
        log.Information("Startup", "Genesis Application services initialised.");
        SettingsService settings = new();
        if (settings.MigrationNotice is { } notice)
            log.Information("Preferences migration", notice);
        return new StudioServices(
            settings,
            new ProjectService(),
            new ProjectValidator(),
            log);
    }
}

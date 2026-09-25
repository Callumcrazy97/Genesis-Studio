namespace Genesis.Application.Core;

public static class ApplicationPaths
{
    private const string OverrideVariable = "GENESIS_APPLICATION_HOME";

    public static string UserDataDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return Path.GetFullPath(overridden);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Genesis",
                "Genesis Application");
        }
    }

    public static string SettingsFile => Path.Combine(UserDataDirectory, "preferences.json");

    public static string LayoutFile => Path.Combine(UserDataDirectory, "workspace-layout.xml");

    public static string SessionLogFile => Path.Combine(UserDataDirectory, "Logs", "studio.log");

    /// <summary>Where a person drops their own theme images.</summary>
    /// <remarks>
    /// Separate from the Themes folder beside the executable, which holds the ones that ship. An
    /// upgrade replaces the installed folder wholesale, so anything personal kept there would be
    /// lost; this one is never written to by the installer.
    /// </remarks>
    public static string UserThemesDirectory => Path.Combine(UserDataDirectory, "Themes");

    /// <summary>Theme images that shipped with the application.</summary>
    public static string InstalledThemesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Themes");

    public static void EnsureUserDirectories()
    {
        Directory.CreateDirectory(UserDataDirectory);
        Directory.CreateDirectory(Path.Combine(UserDataDirectory, "Logs"));
        Directory.CreateDirectory(UserThemesDirectory);
    }
}

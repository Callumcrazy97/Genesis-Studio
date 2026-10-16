namespace Genesis.Application.Core.Backup;

// The client code below came from GitHub Manager (Cal's own app), merged into Genesis so the
// backup needs no separate program. These two small classes stand in for that app's logger and
// paths so the moved files stay as they were.

internal static class Logger
{
    public static void Info(string message) => System.Diagnostics.Trace.WriteLine("GitHub backup: " + message);
    public static void Warn(string message) => System.Diagnostics.Trace.WriteLine("GitHub backup warning: " + message);
    public static void Debug(string message) => System.Diagnostics.Trace.WriteLine("GitHub backup: " + message);
}

internal static class AppPaths
{
    /// <summary>The access token, encrypted to this Windows user; never inside a project.</summary>
    public static string CredentialFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genesis Studio", "github-token.bin");
}

/// <summary>Counts bytes as they cross the wire, for upload and download progress.</summary>
public interface IByteMeter
{
    void AddWireBytes(long count);
}

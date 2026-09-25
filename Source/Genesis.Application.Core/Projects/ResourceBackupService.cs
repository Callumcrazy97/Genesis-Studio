using System.Globalization;

namespace Genesis.Application.Core.Projects;

/// <summary>
/// Keeps a dated copy of a resource each time it is overwritten.
/// </summary>
/// <remarks>
/// "Create backups" and "Backup retention (days)" were preferences that saved, reloaded, and were
/// read by nothing: `.genesis/Backups` was created for every project and stayed empty forever. An
/// editor overwrote a file in place, and the previous contents were gone.
///
/// A backup is written *before* the overwrite, from the bytes still on disk, so it is the last
/// known-good version rather than a copy of whatever is about to be written. Failure to back up
/// never blocks the save — losing an edit because a backup could not be written would be a worse
/// outcome than the missing backup — but it is reported rather than swallowed.
/// </remarks>
public static class ResourceBackupService
{
    /// <summary>How the service behaves. Set from Preferences through <c>SettingsService</c>.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Backups older than this are pruned as new ones are written. 0 disables pruning.</summary>
    public static int RetentionDays { get; set; } = 14;

    /// <summary>Raised when a backup could not be written, so the shell can log it.</summary>
    public static event Action<string>? Failed;

    /// <summary>
    /// Copies the current contents of <paramref name="resourcePath"/> into the project's backup
    /// folder. Does nothing when disabled, when the file is new, or when the path is outside a
    /// Genesis project.
    /// </summary>
    public static void BackupBeforeOverwrite(string resourcePath)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(resourcePath) || !File.Exists(resourcePath))
        {
            return;
        }

        try
        {
            string? projectRoot = FindProjectRoot(resourcePath);
            if (projectRoot is null) return;

            string backupRoot = Path.Combine(projectRoot, ".genesis", "Backups");
            string relative = Path.GetRelativePath(projectRoot, resourcePath);
            string stamped =
                $"{Path.GetFileNameWithoutExtension(relative)}." +
                $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}" +
                Path.GetExtension(relative);

            string destinationDirectory = Path.Combine(
                backupRoot,
                Path.GetDirectoryName(relative) ?? string.Empty);
            Directory.CreateDirectory(destinationDirectory);
            File.Copy(resourcePath, Path.Combine(destinationDirectory, stamped), overwrite: true);

            Prune(backupRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Failed?.Invoke($"Could not back up '{Path.GetFileName(resourcePath)}': {exception.Message}");
        }
    }

    /// <summary>Deletes backups older than <see cref="RetentionDays"/>.</summary>
    private static void Prune(string backupRoot)
    {
        if (RetentionDays <= 0 || !Directory.Exists(backupRoot)) return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (string file in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A locked backup is not worth failing a save over; the next prune will get it.
            }
        }
    }

    /// <summary>Walks up for the folder holding the project's <c>.genesis</c> directory.</summary>
    private static string? FindProjectRoot(string resourcePath)
    {
        DirectoryInfo? probe = new FileInfo(resourcePath).Directory;
        while (probe is not null)
        {
            if (Directory.Exists(Path.Combine(probe.FullName, ".genesis"))) return probe.FullName;
            probe = probe.Parent;
        }

        return null;
    }
}

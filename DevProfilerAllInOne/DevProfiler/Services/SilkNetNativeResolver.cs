using System.IO;

namespace DevProfiler.Services;

/// <summary>
/// Finds a native GLFW companion from the target's own build tree when a
/// single-file Silk.NET publish cannot expose its bundled native library to
/// Silk.NET's custom loader.
/// </summary>
public static class SilkNetNativeResolver
{
    public static string? FindGlfwDirectory(string targetPath, string workingDirectory)
    {
        string targetDirectory = Path.GetDirectoryName(targetPath) ?? workingDirectory;
        if (File.Exists(Path.Combine(targetDirectory, "glfw3.dll")))
            return null;

        var current = new DirectoryInfo(workingDirectory);
        for (int depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            string binDirectory = Path.Combine(current.FullName, "bin");
            if (!Directory.Exists(binDirectory))
                continue;

            string? match = FindBestGlfw(binDirectory, targetPath);
            if (match is not null)
                return Path.GetDirectoryName(match);
        }

        return null;
    }

    private static string? FindBestGlfw(string binDirectory, string targetPath)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            return Directory.EnumerateFiles(binDirectory, "glfw3.dll", options)
                .Where(path => File.Exists(
                    Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "Silk.NET.Windowing.Glfw.dll")))
                .OrderByDescending(path => Score(path, targetPath))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int Score(string candidate, string targetPath)
    {
        int score = 0;
        if (candidate.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            score += 4;
        if (candidate.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
            targetPath.Contains("x64", StringComparison.OrdinalIgnoreCase))
            score += 2;
        if (candidate.Contains("win-arm64", StringComparison.OrdinalIgnoreCase) &&
            targetPath.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            score += 2;
        return score;
    }
}

using System;
using System.IO;
using System.Threading;
using Genesis.Runtime.Project;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private static readonly AsyncLocal<string> PersistenceProjectOverride = new();
    internal static string PersistenceProjectPath => PersistenceProjectOverride.Value ?? ProjectPath;
    internal static string BindPersistenceProject(string path)
    {
        string previous = PersistenceProjectOverride.Value; PersistenceProjectOverride.Value = path; return previous;
    }
    private static readonly AsyncLocal<string> FileErrorFallback = new();
    private static void SetFileError(string error)
    {
        if (Store != null) Store["__file_error"] = error;
        else FileErrorFallback.Value = error;
    }

    [PgslCommand("FileLastError", "FileLastError() -> string", "Last text-file error for this Object; empty after a successful file operation", "Files")]
    public static string FileLastError() => Store?.TryGetValue("__file_error", out object value) == true
        ? value as string ?? "" : FileErrorFallback.Value ?? "";

    [PgslCommand("FileExists", "FileExists(path) -> bool", "Check a game-relative or explicit absolute path; directories are not files", "Files")]
    public static bool FileExists(string path)
    {
        try
        {
            string resolved = ProjectTextFiles.ResolvePath(PersistenceProjectPath, path);
            // GetAttributes distinguishes missing files from permission/path failures hidden by File.Exists.
            bool exists = (File.GetAttributes(resolved) & FileAttributes.Directory) == 0;
            SetFileError(""); return exists;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { SetFileError(""); return false; }
        catch (Exception error) when (ProjectTextFiles.IsFileError(error))
        { SetFileError(error.Message); return false; }
    }

    [PgslCommand("FileReadText", "FileReadText(path) -> string", "Read strict UTF-8, up to 4 MiB; check FileLastError to distinguish errors from empty files", "Files")]
    public static string FileReadText(string path)
    {
        try { string text = ProjectTextFiles.Read(ProjectTextFiles.ResolvePath(PersistenceProjectPath, path)); SetFileError(""); return text; }
        catch (Exception error) when (ProjectTextFiles.IsFileError(error)) { SetFileError(error.Message); return ""; }
    }

    [PgslCommand("FileWriteText", "FileWriteText(path, text) -> bool", "Atomically replace a UTF-8 file, up to 4 MiB, creating parent folders; relative to stable game saves", "Files")]
    public static bool FileWriteText(string path, string text)
    {
        try { ProjectTextFiles.Write(ProjectTextFiles.ResolvePath(PersistenceProjectPath, path), text); SetFileError(""); return true; }
        catch (Exception error) when (ProjectTextFiles.IsFileError(error)) { SetFileError(error.Message); return false; }
    }
}

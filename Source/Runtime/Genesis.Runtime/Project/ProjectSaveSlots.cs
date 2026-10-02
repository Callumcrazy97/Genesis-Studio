#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Genesis.Runtime.Project;

/// <summary>
/// Named save slots for a game: one piece of text per slot (JSON, or anything else), kept under
/// the player's profile in the game's own folder and replaced whole on each write.
/// </summary>
/// <remarks>
/// The numeric save (<see cref="ProjectNumberSave"/>) is one set of numbers for the whole game. A
/// game with several saves, or a save too rich for 256 numbers, uses slots: "Quick", "Slot 1",
/// "Autosave". A slot is written to a new file and moved into place, so a crash or a power cut
/// during a save leaves the previous save intact.
/// </remarks>
public static class ProjectSaveSlots
{
    public const int MaximumNameLength = 48;
    private const string Folder = "Slots";
    private const string Extension = ".save";

    /// <summary>A slot name is 1 to 48 letters, digits, spaces, hyphens or underscores.</summary>
    public static bool ValidName(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot) || slot.Length > MaximumNameLength || slot != slot.Trim()) return false;
        foreach (char c in slot)
            if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_') return false;
        return true;
    }

    private static string Directory(string project) => Path.Combine(ProjectNumberSave.GetWritableDirectory(project), Folder);

    private static string PathOf(string project, string slot) => Path.Combine(Directory(project), slot + Extension);

    /// <summary>Writes a slot, replacing what it held. Text is limited to 4 MiB.</summary>
    public static bool Write(string project, string slot, string text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(project)) { error = "No active project for save slots."; return false; }
        if (!ValidName(slot)) { error = "A slot name is 1 to 48 letters, digits, spaces, hyphens or underscores."; return false; }
        try
        {
            ProjectTextFiles.Write(PathOf(project, slot), text ?? "");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Reads a slot. False when it does not exist or cannot be read.</summary>
    public static bool TryRead(string project, string slot, out string text)
    {
        text = "";
        if (string.IsNullOrWhiteSpace(project) || !ValidName(slot)) return false;
        string path = PathOf(project, slot);
        if (!File.Exists(path)) return false;
        try
        {
            text = ProjectTextFiles.Read(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool Exists(string project, string slot) =>
        !string.IsNullOrWhiteSpace(project) && ValidName(slot) && File.Exists(PathOf(project, slot));

    /// <summary>Removes a slot. True when it is gone, including when it was never there.</summary>
    public static bool Delete(string project, string slot)
    {
        if (string.IsNullOrWhiteSpace(project) || !ValidName(slot)) return false;
        try
        {
            File.Delete(PathOf(project, slot));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The slots that exist, most recently written first.</summary>
    public static IReadOnlyList<string> List(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) return Array.Empty<string>();
        try
        {
            string directory = Directory(project);
            if (!System.IO.Directory.Exists(directory)) return Array.Empty<string>();
            return new DirectoryInfo(directory).EnumerateFiles("*" + Extension)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => Path.GetFileNameWithoutExtension(file.Name))
                .Where(ValidName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>When a slot was last written, in UTC; <see cref="DateTime.MinValue"/> when it does not exist.</summary>
    public static DateTime WrittenUtc(string project, string slot) =>
        Exists(project, slot) ? File.GetLastWriteTimeUtc(PathOf(project, slot)) : DateTime.MinValue;
}

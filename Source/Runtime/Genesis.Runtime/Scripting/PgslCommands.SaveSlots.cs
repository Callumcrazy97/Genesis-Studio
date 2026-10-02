using System.Collections.Generic;
using Genesis.Runtime.Project;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Named save slots. These are ordinary static methods as well as script commands: a C# behaviour
/// calls <c>PgslCommands.SaveSlotWrite("Quick", json)</c>.
/// </summary>
public static partial class PgslCommands
{
    private static string _saveSlotError = "";

    [PgslCommand("SaveSlotWrite", "SaveSlotWrite(slot, text) -> bool",
        "Write a named save slot (JSON or any text up to 4 MiB), replacing what it held. Kept under the player's profile", "Save Data")]
    public static bool SaveSlotWrite(string slot, string text)
    {
        bool written = ProjectSaveSlots.Write(PersistenceProjectPath, slot, text, out string error);
        _saveSlotError = error;
        return written;
    }

    [PgslCommand("SaveSlotRead", "SaveSlotRead(slot) -> string", "Read a named save slot; empty when it does not exist", "Save Data")]
    public static string SaveSlotRead(string slot) =>
        ProjectSaveSlots.TryRead(PersistenceProjectPath, slot, out string text) ? text : "";

    [PgslCommand("SaveSlotExists", "SaveSlotExists(slot) -> bool", "True when a named save slot exists", "Save Data")]
    public static bool SaveSlotExists(string slot) => ProjectSaveSlots.Exists(PersistenceProjectPath, slot);

    [PgslCommand("SaveSlotDelete", "SaveSlotDelete(slot) -> bool", "Remove a named save slot", "Save Data")]
    public static bool SaveSlotDelete(string slot) => ProjectSaveSlots.Delete(PersistenceProjectPath, slot);

    [PgslCommand("SaveSlotCount", "SaveSlotCount() -> number", "How many save slots exist", "Save Data")]
    public static double SaveSlotCount() => ProjectSaveSlots.List(PersistenceProjectPath).Count;

    [PgslCommand("SaveSlotName", "SaveSlotName(index) -> string",
        "The name of a save slot, most recently written first, counting from 0; empty when there is none", "Save Data")]
    public static string SaveSlotName(double index)
    {
        IReadOnlyList<string> slots = ProjectSaveSlots.List(PersistenceProjectPath);
        int at = (int)index;
        return at >= 0 && at < slots.Count ? slots[at] : "";
    }

    [PgslCommand("SaveSlotLastError", "SaveSlotLastError() -> string", "Why the last SaveSlotWrite failed; empty after one that worked", "Save Data")]
    public static string SaveSlotLastError() => _saveSlotError;
}

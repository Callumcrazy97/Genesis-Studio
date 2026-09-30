using Genesis.Runtime.Project;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private static ProjectNumberSave NumberSave => ProjectNumberSave.ForProject(PersistenceProjectPath);
    [PgslCommand("SaveGetNumber", "SaveGetNumber(key, fallback) -> number", "Read a project-isolated numeric save value", "Save Data")]
    public static double SaveGetNumber(string key, double fallback) => NumberSave?.Get(key, fallback) ?? fallback;
    [PgslCommand("SaveSetNumber", "SaveSetNumber(key, value)", "Stage a numeric save value; call SaveFlush at a checkpoint", "Save Data")]
    public static void SaveSetNumber(string key, double value) => NumberSave?.Set(key, value);
    [PgslCommand("SaveTrySetNumber", "SaveTrySetNumber(key, value) -> bool", "Stage a finite number; report invalid keys, values and the 256-key limit through SaveLastError", "Save Data")]
    public static bool SaveTrySetNumber(string key, double value) => NumberSave?.TrySet(key, value) ?? false;
    [PgslCommand("SaveLastError", "SaveLastError() -> string", "Last numeric save error; empty after a successful checked setter or flush", "Save Data")]
    public static string SaveLastError() => NumberSave?.LastError ?? "No active project for numeric saves.";
    [PgslCommand("SaveClear", "SaveClear()", "Clear staged numeric save values; persisted only by SaveFlush", "Save Data")]
    public static void SaveClear() => NumberSave?.Clear();
    [PgslCommand("SaveFlush", "SaveFlush() -> bool", "Atomically persist numeric values under LocalAppData/Genesis/GameSaves", "Save Data")]
    public static bool SaveFlush() => NumberSave?.Flush() ?? false;
    [PgslCommand("SaveExists", "SaveExists() -> bool", "Whether this project has numeric save values", "Save Data")]
    public static bool SaveExists() => NumberSave?.Exists ?? false;
}

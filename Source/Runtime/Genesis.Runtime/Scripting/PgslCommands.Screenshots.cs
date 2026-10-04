using Genesis.Runtime.Project;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("ScreenshotSave", "ScreenshotSave(name) -> bool",
        "Save a picture of this frame once it is drawn, as name.png in the project's debug images folder; one run can take many",
        "Debug")]
    public static bool ScreenshotSave(string name) => ScriptScreenshots.Request(name);

    [PgslCommand("ScreenshotPending", "ScreenshotPending() -> number", "Pictures asked for that are not taken yet", "Debug")]
    public static double ScreenshotPending() => ScriptScreenshots.PendingCount;

    [PgslCommand("ScreenshotLastPath", "ScreenshotLastPath() -> string",
        "The file the last picture was saved to; empty before the first or when it could not be saved", "Debug")]
    public static string ScreenshotLastPath() => ScriptScreenshots.LastPath;
}

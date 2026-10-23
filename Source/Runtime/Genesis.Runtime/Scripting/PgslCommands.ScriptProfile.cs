using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Where a game's scripts spend their time, function by function (PgslScriptProfile).
public static partial class PgslCommands
{
    [PgslCommand("ScriptProfileEnable", "ScriptProfileEnable(enabled)",
        "Start or stop timing every script function call (calls, time including what it calls, how many ran compiled); GENESIS_SCRIPT_PROFILE=1 starts it with the game and writes Debug/Logs/script_profile.txt at the end", "General")]
    public static void ScriptProfileEnable(bool enabled) => PgslScriptProfile.Enabled = enabled;

    [PgslCommand("ScriptProfileReport", "ScriptProfileReport(count) -> string",
        "The script functions that took longest, most first, one line each: name | calls | total ms | average us | compiled calls", "General")]
    public static string ScriptProfileReport(double count) => PgslScriptProfile.Report(double.IsFinite(count) ? (int)System.Math.Clamp(count, 1, 1000) : 20);

    [PgslCommand("ScriptProfileReset", "ScriptProfileReset()", "Forget the script profile's counts", "General")]
    public static void ScriptProfileReset() => PgslScriptProfile.Reset();
}

using Genesis.Runtime.Debugger;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("DebugKeysSetEnabled", "DebugKeysSetEnabled(enabled)",
        "Turn the debug screen's keys (F6, F7, F8, P pauses, N steps) off while the game has the keyboard, as a chat line does, and on again. "
        + "They act only in Studio's Debug and Run, or with GENESIS_DEBUG_KEYS=1; this cannot turn them on in an exported game",
        "Debug")]
    public static void DebugKeysSetEnabled(bool enabled) => DebugKeys.GameAllows = enabled;

    [PgslCommand("DebugKeysEnabled", "DebugKeysEnabled() -> bool",
        "True when the debug screen's keys act: the game was started to be worked on and has not turned them off", "Debug")]
    public static bool DebugKeysEnabled() => DebugKeys.Active;
}

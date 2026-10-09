using System;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// How much one event body or function call may run before the VM stops it as a hang. 100 000
// instructions unless a game asks for more: a world generator or a map rebuild that would otherwise
// be cut into many calls just to stay under it can raise it once in its Create event.
public static partial class PgslCommands
{
    [PgslCommand("ScriptInstructionLimit", "ScriptInstructionLimit(instructions) -> number",
        "Instructions each event body and function call of this game may run before it is stopped as a hang (100 000 unless raised, up to 1 000 000 000; a lower number restores 100 000); returns the limit now in force. Lasts until the game ends; worker jobs keep their own budget", "Game")]
    public static double ScriptInstructionLimit(double instructions)
    {
        PgslVm.CallInstructionLimit = double.IsNaN(instructions)
            ? PgslVm.DefaultCallInstructionLimit
            : (int)Math.Clamp(Math.Floor(instructions), 0, PgslVm.MaximumCallInstructionLimit);
        return PgslVm.CallInstructionLimit;
    }
}

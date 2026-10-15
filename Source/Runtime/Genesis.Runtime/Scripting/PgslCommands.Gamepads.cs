using System;
using Genesis.Runtime.Input;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Up to four controllers, for games two people play on one machine. Controller 0 is the one the
// unnumbered Gamepad* commands read, so GamepadCheckAt(0, "A") is GamepadCheck("A").
public static partial class PgslCommands
{
    #region Controllers by number

    /// <summary>The input state and a controller number from 0 to 3, or false.</summary>
    private static bool TryPad(double index, out InputState input, out int pad)
    {
        input = ActiveGameContext?.Input;
        pad = 0;
        if (input == null || !double.IsFinite(index) || index < 0 || index >= InputState.MaxGamepads) return false;
        pad = (int)index;
        return true;
    }

    private static double PadAxis(InputState.GamepadAxes axes, string axis, bool raw)
    {
        if (string.IsNullOrWhiteSpace(axis)) return 0;
        System.Numerics.Vector2 left = raw ? axes.LeftStickRaw : axes.LeftStick;
        System.Numerics.Vector2 right = raw ? axes.RightStickRaw : axes.RightStick;
        return axis.Trim().ToLowerInvariant() switch
        {
            "leftx" => left.X,
            // Away from the player is positive on both sticks, as GamepadAxis reads them.
            "lefty" => left.Y,
            "rightx" => right.X,
            "righty" => -right.Y,
            "lefttrigger" or "lt" => raw ? axes.LeftTriggerRaw : axes.LeftTrigger,
            "righttrigger" or "rt" => raw ? axes.RightTriggerRaw : axes.RightTrigger,
            _ => 0,
        };
    }

    [PgslCommand("GamepadCount", "GamepadCount() -> number", "How many controllers are plugged in; up to four are read", "Input")]
    public static double GamepadCount() => ActiveGameContext?.Input?.GamepadCount ?? 0;

    [PgslCommand("GamepadConnectedAt", "GamepadConnectedAt(index) -> bool",
        "True while controller 0 to 3 is plugged in. Controller 0 is the one GamepadConnected reads; the next ones plugged in are 1, 2 and 3", "Input")]
    public static bool GamepadConnectedAt(double index) => TryPad(index, out InputState input, out int pad) && input.IsGamepadConnected(pad);

    [PgslCommand("GamepadCheckAt", "GamepadCheckAt(index, button) -> bool",
        "True while a button on controller 0 to 3 is held; the button names are GamepadCheck's", "Input")]
    public static bool GamepadCheckAt(double index, string button) =>
        TryPad(index, out InputState input, out int pad) && ParseGamepadButton(button, out GamepadButton parsed) && input.IsDown(pad, parsed);

    [PgslCommand("GamepadPressedAt", "GamepadPressedAt(index, button) -> bool", "True in the frame a button on controller 0 to 3 goes down", "Input")]
    public static bool GamepadPressedAt(double index, string button) =>
        TryPad(index, out InputState input, out int pad) && ParseGamepadButton(button, out GamepadButton parsed) && input.WasPressed(pad, parsed);

    [PgslCommand("GamepadReleasedAt", "GamepadReleasedAt(index, button) -> bool", "True in the frame a button on controller 0 to 3 comes up", "Input")]
    public static bool GamepadReleasedAt(double index, string button) =>
        TryPad(index, out InputState input, out int pad) && ParseGamepadButton(button, out GamepadButton parsed) && input.WasReleased(pad, parsed);

    [PgslCommand("GamepadAxisAt", "GamepadAxisAt(index, axis) -> number",
        "A stick or trigger of controller 0 to 3, with the names, directions and dead zones of GamepadAxis", "Input")]
    public static double GamepadAxisAt(double index, string axis) =>
        TryPad(index, out InputState input, out int pad) ? PadAxis(input.GetGamepadAxes(pad), axis, raw: false) : 0;

    [PgslCommand("GamepadAxisRawAt", "GamepadAxisRawAt(index, axis) -> number",
        "A stick or trigger of controller 0 to 3 as it reports it, with no dead zone", "Input")]
    public static double GamepadAxisRawAt(double index, string axis) =>
        TryPad(index, out InputState input, out int pad) ? PadAxis(input.GetGamepadAxes(pad), axis, raw: true) : 0;

    [PgslCommand("GamepadVibrateAt", "GamepadVibrateAt(index, low, high, seconds)",
        "Run the motors of controller 0 to 3: the heavy one and the light one, each 0 to 1, for a time", "Input")]
    public static void GamepadVibrateAt(double index, double low, double high, double seconds)
    {
        if (TryPad(index, out InputState input, out int pad)) input.Rumble(pad, (float)low, (float)high, (float)seconds);
    }

    #endregion
}

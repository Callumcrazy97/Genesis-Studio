#nullable disable
using System;
using Genesis.Runtime.Input;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Named input actions a player can rebind (Documentation/Design/UnityGapAudit.md, input): a script
// asks InputActionCheck("Jump") instead of KeyCheck("Space"), and a settings screen changes what
// "Jump" means with InputActionListen and keeps it with InputActionsSave.
public static partial class PgslCommands
{
    private static InputActions Actions => ActiveGameContext?.Input?.Actions;

    /// <summary>
    /// "W", "Space", "Key:W" for keys; "Mouse:Left" (or "Mouse Left"); "Pad:LeftStickLeft" (or "Pad LeftStick Left") and the other stick directions and "Pad:LeftTrigger"; "Pad:A", "Pad A" or
    /// "Gamepad A" for controller buttons, by the names GamepadCheck takes.
    /// </summary>
    internal static bool TryParseBinding(string text, out InputBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (InputBinding.TryParseSaved(trimmed, out binding)) return true;
        // "Pad2 A", "Gamepad3 LB", "Pad2 LeftStick Left": controller 2 to 4 by number.
        var numbered = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(?:Pad|Gamepad)([1-4])[ :_]+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (numbered.Success)
        {
            int pad = numbered.Groups[1].Value[0] - '1';
            string part = numbered.Groups[2].Value.Trim();
            if (ParseGamepadButton(part, out GamepadButton numberedButton)) { binding = InputBinding.ForGamepad(numberedButton, pad); return true; }
            if (Enum.TryParse(part.Replace(" ", ""), true, out GamepadDirection numberedDirection) && Enum.IsDefined(numberedDirection)) { binding = InputBinding.ForDirection(numberedDirection, pad); return true; }
            return false;
        }
        foreach (string prefix in new[] { "Mouse", "Gamepad", "Pad" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || trimmed.Length == prefix.Length) continue;
            string rest = trimmed[prefix.Length..].TrimStart(' ', ':', '_');
            if (prefix == "Mouse")
            {
                if (Enum.TryParse(rest, true, out MouseButton mouse) && Enum.IsDefined(mouse)) { binding = InputBinding.ForMouse(mouse); return true; }
                continue;
            }
            if (ParseGamepadButton(rest, out GamepadButton pad)) { binding = InputBinding.ForGamepad(pad); return true; }
            if (Enum.TryParse(rest.Replace(" ", ""), true, out GamepadDirection direction) && Enum.IsDefined(direction)) { binding = InputBinding.ForDirection(direction); return true; }
        }
        // "Ctrl+S", "Shift+Tab": a key with the modifiers it needs held.
        if (trimmed.Contains('+') && trimmed.Length > 1 && InputBinding.TryParseCombination(trimmed, out Key combined, out int modifiers, ParseKey))
        { binding = InputBinding.ForKey(combined, modifiers); return true; }
        Key key = ParseKey(trimmed);
        if (key == Key.Unknown) return false;
        binding = InputBinding.ForKey(key);
        return true;
    }

    [PgslCommand("InputActionBind", "InputActionBind(action, binding) -> bool",
        "Bind an action to a key (\"Space\"), mouse button (\"Mouse:Left\"), controller button (\"Pad:A\") or stick direction (\"Pad:LeftStickLeft\", \"Pad:RightStickUp\", \"Pad:LeftTrigger\"); an action can have several. False when the binding is not understood", "Input")]
    public static bool InputActionBind(string action, string binding)
    {
        if (Actions is not { } actions || string.IsNullOrWhiteSpace(action) || !TryParseBinding(binding, out InputBinding parsed)) return false;
        actions.Bind(action.Trim(), parsed);
        return true;
    }

    [PgslCommand("InputActionClear", "InputActionClear(action)", "Remove every binding of an action", "Input")]
    public static void InputActionClear(string action) => Actions?.Clear(action);

    [PgslCommand("InputActionCheck", "InputActionCheck(action) -> bool", "True while anything bound to the action is held", "Input")]
    public static bool InputActionCheck(string action) => Actions?.IsDown(ActiveGameContext.Input, action) ?? false;

    [PgslCommand("InputActionPressed", "InputActionPressed(action) -> bool", "True in the frame anything bound to the action goes down", "Input")]
    public static bool InputActionPressed(string action) => Actions?.WasPressed(ActiveGameContext.Input, action) ?? false;

    [PgslCommand("InputActionReleased", "InputActionReleased(action) -> bool", "True in the frame anything bound to the action comes up", "Input")]
    public static bool InputActionReleased(string action) => Actions?.WasReleased(ActiveGameContext.Input, action) ?? false;

    [PgslCommand("InputActionValue", "InputActionValue(action) -> number",
        "How strongly an action is held, 0 to 1: 1 for a key or button, how far a stick is pushed or a trigger pulled for those", "Input")]
    public static double InputActionValue(string action) => Actions?.Value(ActiveGameContext.Input, action) ?? 0;

    [PgslCommand("InputActionAxis", "InputActionAxis(negative, positive) -> number",
        "From -1 to 1 between two actions, for moving: InputActionAxis(\"Left\", \"Right\"); smooth with a stick, -1, 0 or 1 with keys", "Input")]
    public static double InputActionAxis(string negative, string positive) =>
        Actions?.Axis(ActiveGameContext.Input, negative, positive) ?? 0;

    [PgslCommand("InputActionListen", "InputActionListen(action, replace?)",
        "Rebind: the next key, mouse button or controller button pressed binds the action (replacing its bindings unless replace is false). Escape gives up. Poll InputActionListening each frame", "Input")]
    public static void InputActionListen(string action, bool replace = true) => Actions?.Listen(action, replace);

    [PgslCommand("InputActionListening", "InputActionListening() -> bool",
        "True while InputActionListen still waits for a press; call each frame while a rebinding prompt shows", "Input")]
    public static bool InputActionListening()
    {
        if (Actions is not { } actions || actions.Listening == null) return false;
        actions.TakeListenedPress(ActiveGameContext.Input);
        return actions.Listening != null;
    }

    [PgslCommand("InputActionBindingCount", "InputActionBindingCount(action) -> number", "How many bindings the action has", "Input")]
    public static double InputActionBindingCount(string action) => Actions?.BindingCount(action) ?? 0;

    [PgslCommand("InputActionBindingName", "InputActionBindingName(action, index) -> string",
        "A binding's name for a settings screen (\"W\", \"Mouse Left\", \"Pad A\"); empty when there is none", "Input")]
    public static string InputActionBindingName(string action, double index) =>
        Actions?.BindingAt(action, (int)index)?.DisplayName ?? "";

    [PgslCommand("InputActionsSave", "InputActionsSave(path?) -> bool",
        "Keep every action's bindings in the game's saves (default \"input-bindings.txt\"); see FileLastError when false", "Input")]
    public static bool InputActionsSave(string path = "input-bindings.txt") =>
        Actions is { } actions && FileWriteText(string.IsNullOrWhiteSpace(path) ? "input-bindings.txt" : path, actions.Save());

    [PgslCommand("InputActionsLoad", "InputActionsLoad(path?) -> number",
        "Read bindings kept by InputActionsSave: actions it names are rebound, others keep theirs. Returns how many it read (0 when there is no file yet)", "Input")]
    public static double InputActionsLoad(string path = "input-bindings.txt")
    {
        if (Actions is not { } actions) return 0;
        string file = string.IsNullOrWhiteSpace(path) ? "input-bindings.txt" : path;
        if (!FileExists(file)) return 0;
        return actions.Load(FileReadText(file));
    }
}

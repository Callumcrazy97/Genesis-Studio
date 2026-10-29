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
            if (Enum.TryParse(part.Replace(" ", ""), true, out GamepadAxis numberedAxis) && Enum.IsDefined(numberedAxis)) { binding = InputBinding.ForAxis(numberedAxis, pad); return true; }
            if (!part.Contains('+')) return false;
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
            if (Enum.TryParse(rest.Replace(" ", ""), true, out GamepadAxis axis) && Enum.IsDefined(axis)) { binding = InputBinding.ForAxis(axis); return true; }
        }
        // "Ctrl+S", "Shift+Tab": a key with the modifiers it needs held.
        if (trimmed.Contains('+') && trimmed.Length > 1 && InputBinding.TryParseCombination(trimmed, out Key combined, out int modifiers, ParseKey))
        { binding = InputBinding.ForKey(combined, modifiers); return true; }
        // "LB+A", "Pad:LB+Pad:A", "Q+E": a chord of two to four held together.
        if (trimmed.Contains('+') && trimmed.Length > 1) return TryParseChord(trimmed, out binding);
        Key key = ParseKey(trimmed);
        if (key == Key.Unknown) return false;
        binding = InputBinding.ForKey(key);
        return true;
    }

    /// <summary>
    /// "LB+A", "Pad2:LB+A", "Shift+LB", "Q+E": each part a binding. A bare part is a key when it is
    /// one, else a controller button; after a controller part, a bare part is on that controller.
    /// </summary>
    private static bool TryParseChord(string text, out InputBinding binding)
    {
        binding = default;
        string[] texts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (texts.Length < 2 || texts.Length > 4) return false;
        var parts = new InputBinding[texts.Length];
        string controller = null;
        for (int i = 0; i < texts.Length; i++)
        {
            string part = texts[i];
            bool bare = !part.Contains(':') && !part.Contains(' ');
            if (bare && controller != null && TryParseBinding(controller + ":" + part, out parts[i])) continue;
            if (!TryParseBinding(part, out parts[i]) && !(bare && TryParseBinding("Pad:" + part, out parts[i]))) return false;
            if (parts[i].Kind == InputBindingKind.Chord) return false;
            controller = parts[i].IsController ? (parts[i].Pad == 0 ? "Pad" : "Pad" + (parts[i].Pad + 1)) : null;
        }
        binding = InputBinding.ForChord(parts);
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
        "How strongly an action is held: 1 for a key, button or chord; 0 to 1 for a stick direction or trigger; -1 to 1 for a whole stick axis (\"Pad:LeftStickX\"); sticks and triggers through the action's dead zone and sensitivity", "Input")]
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

    // ── Analogue values, one controller's own bindings, and putting the game's back ──

    [PgslCommand("InputActionSetDeadZone", "InputActionSetDeadZone(action, amount)",
        "How far a stick or trigger bound to the action must move before it counts, 0 to 0.95 (0 unless set); the rest of its travel reads 0 to 1 again. Keys and buttons are not changed", "Input")]
    public static void InputActionSetDeadZone(string action, double amount) => Actions?.SetDeadZone(action, (float)amount);

    [PgslCommand("InputActionDeadZone", "InputActionDeadZone(action) -> number", "The action's dead zone for sticks and triggers (0 unless set)", "Input")]
    public static double InputActionDeadZone(string action) => Actions?.DeadZone(action) ?? 0;

    [PgslCommand("InputActionSetSensitivity", "InputActionSetSensitivity(action, amount)",
        "How strongly a stick or trigger bound to the action counts, 0.01 to 10 (1 unless set): 2 reaches full value half way; values stay within -1 to 1", "Input")]
    public static void InputActionSetSensitivity(string action, double amount) => Actions?.SetSensitivity(action, (float)amount);

    [PgslCommand("InputActionSensitivity", "InputActionSensitivity(action) -> number", "The action's sensitivity for sticks and triggers (1 unless set)", "Input")]
    public static double InputActionSensitivity(string action) => Actions?.Sensitivity(action) ?? 1;

    // Controllers are numbered 1 to 4 for scripts ("Pad2"), 0 to 3 inside.
    private static int Controller(double number) => double.IsFinite(number) && number >= 1 && number <= InputState.MaxGamepads ? (int)number - 1 : -1;

    [PgslCommand("InputActionBindPad", "InputActionBindPad(controller, action, binding) -> bool",
        "Give one controller (1 to 4) a binding of its own for an action (\"Pad:B\", \"Pad:LeftStickX\", \"LB+A\"): its own bindings replace the shared ones on that controller. False when it is not a controller binding", "Input")]
    public static bool InputActionBindPad(double controller, string action, string binding)
    {
        int pad = Controller(controller);
        if (Actions is not { } actions || pad < 0 || string.IsNullOrWhiteSpace(action) || !TryParseBinding(binding, out InputBinding parsed) || !parsed.IsController) return false;
        actions.BindForPad(pad, action.Trim(), parsed);
        return true;
    }

    [PgslCommand("InputActionClearPad", "InputActionClearPad(controller, action)", "Unbind an action on one controller (1 to 4): nothing on it triggers the action until it is bound again", "Input")]
    public static void InputActionClearPad(double controller, string action) => Actions?.ClearForPad(Controller(controller), action);

    [PgslCommand("InputActionResetPad", "InputActionResetPad(controller)", "Drop a controller's (1 to 4) own bindings: it uses the shared ones again", "Input")]
    public static void InputActionResetPad(double controller) => Actions?.ResetPad(Controller(controller));

    [PgslCommand("InputActionCheckPad", "InputActionCheckPad(controller, action) -> bool",
        "True while one controller (1 to 4) holds the action, by its own bindings or the shared controller bindings read on it: one player each in local multiplayer", "Input")]
    public static bool InputActionCheckPad(double controller, string action) => Actions?.IsDown(ActiveGameContext.Input, action, Controller(controller)) ?? false;

    [PgslCommand("InputActionPressedPad", "InputActionPressedPad(controller, action) -> bool", "True in the frame one controller (1 to 4) presses the action", "Input")]
    public static bool InputActionPressedPad(double controller, string action) => Actions?.WasPressed(ActiveGameContext.Input, action, Controller(controller)) ?? false;

    [PgslCommand("InputActionReleasedPad", "InputActionReleasedPad(controller, action) -> bool", "True in the frame one controller (1 to 4) lets the action go", "Input")]
    public static bool InputActionReleasedPad(double controller, string action) => Actions?.WasReleased(ActiveGameContext.Input, action, Controller(controller)) ?? false;

    [PgslCommand("InputActionValuePad", "InputActionValuePad(controller, action) -> number", "InputActionValue on one controller (1 to 4)", "Input")]
    public static double InputActionValuePad(double controller, string action) => Actions?.Value(ActiveGameContext.Input, action, Controller(controller)) ?? 0;

    [PgslCommand("InputActionListenPad", "InputActionListenPad(controller, action, replace?)",
        "Rebind an action for one controller (1 to 4): the next button or stick push on that controller becomes its own binding. Escape gives up; poll InputActionListening", "Input")]
    public static void InputActionListenPad(double controller, string action, bool replace = true)
    {
        int pad = Controller(controller);
        if (pad >= 0) Actions?.Listen(action, replace, pad);
    }

    [PgslCommand("InputActionBindingCountPad", "InputActionBindingCountPad(controller, action) -> number", "How many controller bindings the action has on one controller (1 to 4): its own, or the shared ones", "Input")]
    public static double InputActionBindingCountPad(double controller, string action) => Actions?.BindingCount(action, Controller(controller)) ?? 0;

    [PgslCommand("InputActionBindingNamePad", "InputActionBindingNamePad(controller, action, index) -> string", "A controller binding's name on one controller (1 to 4); empty when there is none", "Input")]
    public static string InputActionBindingNamePad(double controller, string action, double index) =>
        Actions?.BindingAt(action, (int)index, Controller(controller))?.DisplayName ?? "";

    [PgslCommand("InputActionsReset", "InputActionsReset()",
        "Put back the bindings the game made before the player changed them, and drop every controller's own (the rebinding screen's Reset)", "Input")]
    public static void InputActionsReset() => Actions?.ResetToDefaults();

    // ── Engine.Input.Actions (the standard names of the new values) ──

    [PgslCommand("Enabled", "Engine.Input.Actions.Enabled = false", "Every action reads as not held while false; the rebinding screen turns it off while open (true unless set)", "Input", Namespace = "Engine.Input.Actions", Flat = false)]
    public static bool InputActionsApiEnabled
    {
        get => Actions?.Enabled ?? true;
        set { if (Actions is { } actions) actions.Enabled = value; }
    }

    [PgslCommand("SetDeadZone", "Engine.Input.Actions.SetDeadZone(action, amount)", "The action's dead zone for sticks and triggers, 0 to 0.95 (0 unless set)", "Input", Namespace = "Engine.Input.Actions", Flat = false)]
    public static void InputActionsApiSetDeadZone(string action, double amount) => InputActionSetDeadZone(action, amount);

    [PgslCommand("DeadZone", "Engine.Input.Actions.DeadZone(action) -> number", "The action's dead zone for sticks and triggers", "Input", Namespace = "Engine.Input.Actions", Flat = false)]
    public static double InputActionsApiDeadZone(string action) => InputActionDeadZone(action);

    [PgslCommand("SetSensitivity", "Engine.Input.Actions.SetSensitivity(action, amount)", "The action's sensitivity for sticks and triggers, 0.01 to 10 (1 unless set)", "Input", Namespace = "Engine.Input.Actions", Flat = false)]
    public static void InputActionsApiSetSensitivity(string action, double amount) => InputActionSetSensitivity(action, amount);

    [PgslCommand("Sensitivity", "Engine.Input.Actions.Sensitivity(action) -> number", "The action's sensitivity for sticks and triggers", "Input", Namespace = "Engine.Input.Actions", Flat = false)]
    public static double InputActionsApiSensitivity(string action) => InputActionSensitivity(action);

    [PgslCommand("Reset", "Engine.Input.Actions.Reset()", "Put back the game's own bindings and drop every controller's own", "Input", Namespace = "Engine.Input.Actions", Flat = false)]
    public static void InputActionsApiReset() => InputActionsReset();
}

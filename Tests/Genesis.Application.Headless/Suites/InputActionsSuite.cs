using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>Input actions as values, per controller, as chords, and the ready-made rebinding screen (UnityParityPlan.md, Input).</summary>
internal static class InputActionsSuite
{
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-4;
    private static InputBinding? Parsed(string text) => PgslCommands.TryParseBinding(text, out InputBinding binding) ? binding : null;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Runtime.Input.Actions");
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Input.Actions.AxisBindingsReadValuesThroughDeadZoneAndSensitivity", () =>
        {
            var input = new InputState();
            input.Actions.Bind("Steer", InputBinding.ForAxis(GamepadAxis.LeftStickX));
            input.Actions.Bind("Steer", InputBinding.ForKey(Key.D));
            input.Actions.Bind("Throttle", InputBinding.ForAxis(GamepadAxis.RightTrigger));
            input.Actions.Bind("Look", InputBinding.ForAxis(GamepadAxis.RightStickY));
            input.Actions.Bind("Left", InputBinding.ForDirection(GamepadDirection.LeftStickLeft));
            input.LeftStick = new Vector2(-0.6f, 0f);
            input.RightTrigger = 0.3f;
            input.RightStick = new Vector2(0f, -0.7f);
            Check(Near(input.Actions.Value(input, "Steer"), -0.6) && input.Actions.DeadZone("Steer") == 0 && input.Actions.Sensitivity("Steer") == 1,
                "A stick axis did not read as a value from -1 to 1 with no dead zone or sensitivity of its own.");
            Check(Near(input.Actions.Value(input, "Throttle"), 0.3) && Near(input.Actions.Value(input, "Look"), 0.7),
                "A trigger did not read 0 to 1, or the right stick's up was not positive.");
            Check(Near(input.Actions.Value(input, "Left"), 0.6) && input.Actions.IsDown(input, "Steer") && !input.Actions.IsDown(input, "Throttle"),
                "A stick direction read differently from before, or an axis pushed past half way did not count as held.");
            input.Actions.SetDeadZone("Steer", 0.2f);
            Check(Near(input.Actions.Value(input, "Steer"), -0.5), $"The dead zone did not spread the rest of the travel over 0 to 1: {input.Actions.Value(input, "Steer")}.");
            input.Actions.SetSensitivity("Steer", 2f);
            Check(Near(input.Actions.Value(input, "Steer"), -1), "Sensitivity did not scale the value, or let it past -1.");
            input.LeftStick = new Vector2(-0.1f, 0f);
            Check(input.Actions.Value(input, "Steer") == 0f, "A push inside the dead zone counted.");
            input.OnKeyDown(Key.D);
            Check(input.Actions.Value(input, "Steer") == 1f, "A key bound to an analogue action did not read 1.");
            Check(Parsed("Pad:LeftStickX") == InputBinding.ForAxis(GamepadAxis.LeftStickX) && Parsed("Axis2:RightTrigger") == InputBinding.ForAxis(GamepadAxis.RightTrigger, 1)
                && Parsed("Pad3 RightStickY") == InputBinding.ForAxis(GamepadAxis.RightStickY, 2) && Parsed("Pad:LeftTrigger") == InputBinding.ForGamepad(GamepadButton.LeftTrigger)
                && InputBinding.ForAxis(GamepadAxis.LeftStickX).DisplayName == "Pad LeftStickX",
                "An axis binding name was read or shown wrongly.");
            var kept = new InputState();
            Check(kept.Actions.Load(input.Actions.Save()) >= 4 && kept.Actions.BindingAt("Steer", 0) == InputBinding.ForAxis(GamepadAxis.LeftStickX),
                $"An axis binding did not save and load: {input.Actions.Save().Replace('\n', ' ')}");

            // The same through PGSL and the Engine API.
            IGameContext previous = PgslCommands.ActiveGameContext;
            try
            {
                PgslCommands.ActiveGameContext = new NullGameContext { Input = input };
                input.OnKeyUp(Key.D); input.LeftStick = new Vector2(0.6f, 0f);
                PgslCommands.InputActionSetSensitivity("Steer", 1);
                Check(Near(PgslCommands.InputActionValue("Steer"), 0.5) && Near(PgslCommands.InputActionsApiDeadZone("Steer"), 0.2),
                    "InputActionValue did not give the shaped value, or Engine.Input.Actions.DeadZone was not the action's.");
                PgslCommands.InputActionsApiEnabled = false;
                Check(PgslCommands.InputActionValue("Steer") == 0 && !PgslCommands.InputActionCheck("Steer"), "Engine.Input.Actions.Enabled = false still read actions.");
                PgslCommands.InputActionsApiEnabled = true;
            }
            finally { PgslCommands.ActiveGameContext = previous; }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Input.Actions.EachControllerHasItsOwnBindingsOrTheShared", () =>
        {
            var input = new InputState();
            input.Actions.Bind("Jump", InputBinding.ForKey(Key.Space));
            input.Actions.Bind("Jump", InputBinding.ForGamepad(GamepadButton.A));
            input.SetGamepadButton(1, GamepadButton.A, true);
            Check(input.Actions.IsDown(input, "Jump", 1) && !input.Actions.IsDown(input, "Jump", 0) && input.Actions.WasPressed(input, "Jump", 1),
                "A controller without its own bindings did not read the shared ones on itself alone.");
            Check(!input.Actions.IsDown(input, "Jump"), "The shared reading changed: controller 2's A pressed an action bound to controller 1's A.");
            input.OnKeyDown(Key.Space);
            Check(input.Actions.IsDown(input, "Jump") && !input.Actions.IsDown(input, "Jump", 2), "The keyboard counted as a controller, or stopped counting for everyone.");
            input.OnKeyUp(Key.Space);
            input.Actions.BindForPad(1, "Jump", InputBinding.ForGamepad(GamepadButton.B, 1));
            Check(!input.Actions.IsDown(input, "Jump", 1) && input.Actions.HasOwnBindings(1, "Jump") && !input.Actions.HasOwnBindings(0, "Jump")
                && input.Actions.BindingAt("Jump", 0, 1) == InputBinding.ForGamepad(GamepadButton.B),
                "A controller's own binding did not replace the shared ones on it (kept as controller 1's).");
            input.SetGamepadButton(1, GamepadButton.B, true);
            input.SetGamepadButton(0, GamepadButton.A, true);
            Check(input.Actions.IsDown(input, "Jump", 1) && input.Actions.IsDown(input, "Jump", 0), "Controller 2's own B, or controller 1's shared A, did not press Jump.");
            string saved = input.Actions.Save();
            Check(saved.Contains("[Pad 2]\nJump=Pad:B\n", StringComparison.Ordinal), $"The saved bindings do not keep controller 2's own: {saved.Replace('\n', ' ')}");
            var loaded = new InputState();
            loaded.Actions.Bind("Jump", InputBinding.ForKey(Key.Space));
            Check(loaded.Actions.Load(saved) == 2 && loaded.Actions.HasOwnBindings(1, "Jump") && !loaded.Actions.HasOwnBindings(0, "Jump")
                && loaded.Actions.BindingAt("Jump", 1) == InputBinding.ForGamepad(GamepadButton.A),
                "Loading lost a controller's own bindings or the shared ones.");
            input.Actions.ResetPad(1);
            Check(!input.Actions.HasOwnBindings(1, "Jump") && input.Actions.IsDown(input, "Jump", 1), "Resetting a controller did not give it the shared bindings again.");
            input.SetGamepadButton(1, GamepadButton.A, false);
            Check(!input.Actions.IsDown(input, "Jump", 1), "A controller's dropped own binding (B, still held) still pressed the action.");
            input.Actions.Listen("Jump", true, 2);
            input.OnKeyDown(Key.W);
            Check(!input.Actions.TakeListenedPress(input), "A key bound an action listening for one controller.");
            input.SetGamepadButton(2, GamepadButton.Y, true);
            Check(input.Actions.TakeListenedPress(input) && input.Actions.BindingAt("Jump", 0, 2) == InputBinding.ForGamepad(GamepadButton.Y)
                && input.Actions.BindingCount("Jump") == 2, "Listening for controller 3 did not give it its own binding, or changed the shared ones.");
            input.Actions.ResetToDefaults();
            Check(!input.Actions.HasOwnBindings(2, "Jump") && input.Actions.BindingCount("Jump") == 2, "Reset to defaults kept a controller's own bindings.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Input.Actions.ChordsFireOncePerCompletionAndSingleKeysStillWork", () =>
        {
            var input = new InputState();
            InputBinding chord = InputBinding.ForChord(InputBinding.ForGamepad(GamepadButton.LeftBumper), InputBinding.ForGamepad(GamepadButton.A));
            input.Actions.Bind("Fire", InputBinding.ForGamepad(GamepadButton.A));
            input.Actions.Bind("Special", chord);
            input.SetGamepadButton(GamepadButton.A, true);
            Check(input.Actions.WasPressed(input, "Fire") && !input.Actions.IsDown(input, "Special"), "A alone pressed the LB+A chord, or stopped pressing Fire.");
            input.NextFrame(); input.SetGamepadButton(GamepadButton.A, false); input.NextFrame();
            input.SetGamepadButton(GamepadButton.LeftBumper, true);
            Check(!input.Actions.IsDown(input, "Special"), "LB alone pressed the chord.");
            input.NextFrame(); input.SetGamepadButton(GamepadButton.A, true);
            Check(input.Actions.WasPressed(input, "Special") && input.Actions.IsDown(input, "Special") && input.Actions.WasPressed(input, "Fire"),
                "Completing the chord did not press it, or the single A binding stopped working meanwhile.");
            input.NextFrame();
            Check(input.Actions.IsDown(input, "Special") && !input.Actions.WasPressed(input, "Special"), "Holding the chord pressed it again.");
            input.SetGamepadButton(GamepadButton.A, false);
            Check(input.Actions.WasReleased(input, "Special") && !input.Actions.IsDown(input, "Special"), "Letting a part go did not release the chord.");
            input.NextFrame(); input.SetGamepadButton(GamepadButton.A, true);
            Check(input.Actions.WasPressed(input, "Special"), "Completing the chord a second time did not press it again.");
            input.NextFrame(); input.SetGamepadButton(GamepadButton.A, false); input.SetGamepadButton(GamepadButton.LeftBumper, false); input.NextFrame();
            input.SetGamepadButton(GamepadButton.A, true); input.NextFrame(); input.SetGamepadButton(GamepadButton.LeftBumper, true);
            Check(input.Actions.WasPressed(input, "Special"), "The chord pressed only when its parts came in one order.");

            var keys = new InputState();
            keys.Actions.Bind("Dash", Parsed("Q+E") ?? default);
            keys.OnKeyDown(Key.Q);
            Check(!keys.Actions.IsDown(keys, "Dash"), "Q alone pressed Q+E.");
            keys.NextFrame(); keys.OnKeyDown(Key.E);
            Check(keys.Actions.WasPressed(keys, "Dash"), "Q then E did not press Q+E.");
            Check(Parsed("Q+E") == InputBinding.ForChord(InputBinding.ForKey(Key.Q), InputBinding.ForKey(Key.E)) && Parsed("LB+A") == chord
                && Parsed("Pad:LB+Pad:A") == chord
                && Parsed("Pad2:LB+A") == InputBinding.ForChord(InputBinding.ForGamepad(GamepadButton.LeftBumper, 1), InputBinding.ForGamepad(GamepadButton.A, 1))
                && Parsed("Ctrl+S") == InputBinding.ForKey(Key.S, InputBinding.Ctrl) && chord.DisplayName == "Pad LeftBumper + Pad A",
                "A chord was read or shown wrongly, or Ctrl+S stopped being a key with a modifier.");
            var kept = new InputState();
            Check(kept.Actions.Load(input.Actions.Save()) == 2 && kept.Actions.BindingAt("Special", 0) == chord
                && input.Actions.Save().Contains("Special=Chord:Pad:LeftBumper&Pad:A", StringComparison.Ordinal),
                $"A chord did not save and load: {input.Actions.Save().Replace('\n', ' ')}");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Input.Rebinding.ScreenOpensRebindsResetsAndCloses", () =>
        {
            var input = new InputState();
            input.Actions.Bind("Jump", InputBinding.ForKey(Key.Space));
            input.Actions.Bind("Jump", InputBinding.ForGamepad(GamepadButton.A));
            input.Actions.Bind("Fire", InputBinding.ForMouse(MouseButton.Left));
            IGameContext previous = PgslCommands.ActiveGameContext;
            double speed = PgslCommands.GameGetSpeed();
            var surface = new PgslRecordingDrawSurface();
            void Frame() { surface.Texts.Clear(); PgslCommands.DrawRebindingScreen(surface); input.NextFrame(); }
            string Focused()
            {
                PgslContext before = PgslCommands.BindContext(PgslCommands.RebindingScreenContext);
                try { return PgslCommands.UiGetFocused(PgslCommands.RebindingScreenUi); }
                finally { PgslCommands.BindContext(before); }
            }
            void Focus(string id)
            {
                PgslContext before = PgslCommands.BindContext(PgslCommands.RebindingScreenContext);
                try { PgslCommands.UiSetFocus(PgslCommands.RebindingScreenUi, id); }
                finally { PgslCommands.BindContext(before); }
            }
            try
            {
                PgslCommands.ActiveGameContext = new NullGameContext { Input = input };
                PgslCommands.GameSetSpeed(1);
                Check(PgslCommands.InputRebindingOpen() && PgslCommands.InputRebindingIsOpen() && PgslCommands.InputRebindingShowing
                    && !input.Actions.Enabled && PgslCommands.GameGetSpeed() == 0,
                    "One call did not open the screen, pause the game and keep the game's actions from reading the screen's presses.");
                Frame();
                Check(surface.Texts.Any(text => text.Text == "Controls") && surface.Texts.Any(text => text.Text == "Jump") && surface.Texts.Any(text => text.Text == "Fire")
                    && surface.Texts.Any(text => text.Text == "Space, Pad A") && surface.Texts.Any(text => text.Text == "Mouse Left") && Focused() == "Bind0",
                    $"The screen did not list every action with its bindings: {string.Join(" | ", surface.Texts.Select(text => text.Text))}");
                input.OnKeyDown(Key.Enter); Frame();
                Check(input.Actions.Listening == "Jump", "Enter on the first action did not start listening for its new key.");
                input.OnKeyUp(Key.Enter); input.OnKeyDown(Key.W); Frame();
                Check(input.Actions.Listening == null && input.Actions.BindingCount("Jump") == 1 && input.Actions.BindingAt("Jump", 0) == InputBinding.ForKey(Key.W)
                    && PgslCommands.InputRebindingIsOpen() && Focused() == "Bind0", "Pressing W did not rebind Jump, or it also moved or closed the screen.");
                input.OnKeyUp(Key.W);
                input.SetGamepadButton(GamepadButton.DPadDown, true); Frame();
                Check(Focused() == "Bind1", $"The controller's d-pad did not move to the next action (focus {Focused()}).");
                input.SetGamepadButton(GamepadButton.DPadDown, false); input.SetGamepadButton(GamepadButton.A, true); Frame();
                Check(input.Actions.Listening == "Fire", "The controller's A did not start listening.");
                input.SetGamepadButton(GamepadButton.A, false); input.SetGamepadButton(GamepadButton.X, true); Frame();
                Check(input.Actions.BindingAt("Fire", 0) == InputBinding.ForGamepad(GamepadButton.X) && input.Actions.BindingCount("Fire") == 1,
                    "Pressing X did not rebind Fire to the controller's X.");
                input.SetGamepadButton(GamepadButton.X, false);
                Focus("Owner"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Focus("Bind0"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Check(input.Actions.Listening == "Jump" && input.Actions.ListeningPad == 0, "For: controller 1 did not listen for that controller's own binding.");
                input.SetGamepadButton(GamepadButton.B, true); Frame();
                Check(input.Actions.HasOwnBindings(0, "Jump") && input.Actions.BindingAt("Jump", 0, 0) == InputBinding.ForGamepad(GamepadButton.B)
                    && PgslCommands.InputRebindingIsOpen(), "B did not become controller 1's own Jump, or it closed the screen while listening.");
                input.SetGamepadButton(GamepadButton.B, false);
                Focus("Owner"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Focus("Owner"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Focus("Owner"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Focus("Owner"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Focus("Reset"); input.OnKeyDown(Key.Enter); Frame(); input.OnKeyUp(Key.Enter);
                Check(input.Actions.BindingCount("Jump") == 2 && input.Actions.BindingAt("Jump", 0) == InputBinding.ForKey(Key.Space)
                    && input.Actions.BindingAt("Fire", 0) == InputBinding.ForMouse(MouseButton.Left) && !input.Actions.HasOwnBindings(0, "Jump"),
                    "Reset to defaults did not put the game's bindings back for everyone.");
                input.OnKeyDown(Key.Escape); Frame(); input.OnKeyUp(Key.Escape);
                Check(!PgslCommands.InputRebindingIsOpen() && input.Actions.Enabled && PgslCommands.GameGetSpeed() == 1,
                    "Escape did not close the screen back to the running game.");
                input.OnKeyDown(Key.Space);
                Check(input.Actions.IsDown(input, "Jump"), "The game's actions did not read again after the screen closed.");
                PgslCommands.InputRebindingEnabled = false;
                Check(!PgslCommands.InputRebindingOpen() && !PgslCommands.InputRebindingIsOpen(), "The screen opened with Engine.Input.Rebinding.Enabled off.");
            }
            finally
            {
                PgslCommands.InputRebindingEnabled = true;
                PgslCommands.InputRebindingClose();
                PgslCommands.ActiveGameContext = previous;
                PgslCommands.GameSetSpeed(speed);
            }
        });
    }
}

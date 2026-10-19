using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Input
{
    public sealed class InputState
    {
        private readonly HashSet<Key> _down = new HashSet<Key>();
        private readonly HashSet<Key> _pressed = new HashSet<Key>();
        private readonly HashSet<Key> _released = new HashSet<Key>();
        private readonly bool[] _mouseDown = new bool[3];
        private readonly bool[] _mousePressed = new bool[3];
        private readonly bool[] _mouseReleased = new bool[3];

        public Vector2 MousePosition { get; private set; }
        public float   WheelDelta { get; private set; }
        public Vector2 LookDelta { get; set; }

        // ── Gamepad (native; populated by the platform window) ──
        public bool    GamepadConnected { get; set; }
        public Vector2 LeftStick  { get; set; }   // -1..1, analog movement
        public Vector2 RightStick { get; set; }   // -1..1, analog look
        public float   LeftTrigger { get; set; }
        public float   RightTrigger { get; set; }

        /// <summary>The sticks and triggers as the controller reports them, before any dead zone.</summary>
        public Vector2 LeftStickRaw { get; set; }
        public Vector2 RightStickRaw { get; set; }
        public float   LeftTriggerRaw { get; set; }
        public float   RightTriggerRaw { get; set; }

        /// <summary>
        /// How far a stick or trigger must move (0 to 1) before it reads as moved. The sticks'
        /// default is 0.18; the triggers have none.
        /// </summary>
        public float LeftStickDeadZone { get; set; } = DefaultStickDeadZone;
        public float RightStickDeadZone { get; set; } = DefaultStickDeadZone;
        public float TriggerDeadZone { get; set; }
        public const float DefaultStickDeadZone = 0.18f;

        private readonly System.Text.StringBuilder _typed = new();
        private const int TypedCapacity = 256;

        /// <summary>A character typed on the keyboard, as the keyboard layout and shift make it.</summary>
        public void OnChar(char character)
        {
            if (char.IsControl(character)) return;
            if (_typed.Length >= TypedCapacity) _typed.Remove(0, 1);
            _typed.Append(character);
        }

        private bool _textTakenThisFrame, _textTakenLastFrame;

        /// <summary>
        /// True while something reads typed text: <see cref="TakeTypedText"/> was called this frame
        /// or the last. A game reading a chat line or a name box does so every frame, so its
        /// letters are text then, not shortcuts (the debug screen's P and N stand aside).
        /// </summary>
        public bool IsTakingText => _textTakenThisFrame || _textTakenLastFrame;

        /// <summary>Everything typed since the last call, in order, and forgets it.</summary>
        public string TakeTypedText()
        {
            _textTakenThisFrame = true;
            if (_typed.Length == 0) return string.Empty;
            string text = _typed.ToString();
            _typed.Clear();
            return text;
        }
        public bool    LeftStickPressed { get; set; }
        /// <summary>Start/Menu button pressed this frame (maps to pause).</summary>
        public bool    StartPressed { get; set; }
        /// <summary>B button pressed this frame (maps to back/cancel/quit).</summary>
        public bool    BackPressed { get; set; }
        /// <summary>A button pressed this frame (maps to confirm in menus).</summary>
        public bool    ConfirmPressed { get; set; }
        public bool    XPressed { get; set; }
        public bool    YPressed { get; set; }
        public bool    DPadUpPressed { get; set; }
        public bool    DPadDownPressed { get; set; }
        public bool    DPadLeftPressed { get; set; }
        public bool    DPadRightPressed { get; set; }
        public bool    LeftBumperPressed { get; set; }
        public bool    RightBumperPressed { get; set; }
        public bool    RecipeBookPressed { get; set; }

        private const int GamepadButtonCount = 16;
        private readonly bool[] _padDown = new bool[GamepadButtonCount];
        private readonly bool[] _padPressed = new bool[GamepadButtonCount];
        private readonly bool[] _padReleased = new bool[GamepadButtonCount];

        /// <summary>
        /// True (the default) makes a controller act as keys and mouse buttons as well: A is
        /// Space, the stick clicks are Control and Shift, the triggers are the mouse buttons and
        /// the right stick turns a captured view. A game that reads the controller itself turns
        /// this off so one press is not two actions.
        /// </summary>
        public bool GamepadEmulatesKeyboard { get; set; } = true;

        /// <summary>Strength of the controller's two motors (0 to 1) and how long they still run.</summary>
        public float RumbleLow { get; private set; }
        public float RumbleHigh { get; private set; }
        public float RumbleSeconds { get; set; }

        public bool IsDown(GamepadButton button) => _padDown[(int)button];
        public bool WasPressed(GamepadButton button) => _padPressed[(int)button];
        public bool WasReleased(GamepadButton button) => _padReleased[(int)button];

        /// <summary>The platform window reports each button every frame; edges are worked out here.</summary>
        public void SetGamepadButton(GamepadButton button, bool down)
        {
            int index = (int)button;
            if (_padDown[index] == down) return;
            _padDown[index] = down;
            if (down) _padPressed[index] = true;
            else _padReleased[index] = true;
        }

        /// <summary>The controller has gone: everything held on it is let go.</summary>
        public void ReleaseGamepad()
        {
            for (int i = 0; i < GamepadButtonCount; i++)
                if (_padDown[i]) { _padDown[i] = false; _padReleased[i] = true; }
            LeftStick = Vector2.Zero;
            RightStick = Vector2.Zero;
            LeftTrigger = 0f;
            RightTrigger = 0f;
            LeftStickRaw = Vector2.Zero;
            RightStickRaw = Vector2.Zero;
            LeftTriggerRaw = 0f;
            RightTriggerRaw = 0f;
        }

        /// <summary>Runs the controller's motors: the heavy low one and the light high one, each 0 to 1.</summary>
        public void Rumble(float low, float high, float seconds)
        {
            RumbleLow = Math.Clamp(float.IsFinite(low) ? low : 0f, 0f, 1f);
            RumbleHigh = Math.Clamp(float.IsFinite(high) ? high : 0f, 0f, 1f);
            RumbleSeconds = Math.Clamp(float.IsFinite(seconds) ? seconds : 0f, 0f, 10f);
        }

        // ── More than one controller ──
        // Controller 0 is the one the properties above describe, exactly as before. Controllers 1
        // to 3 are kept here; they never press keys, and the dead zones above apply to all four.

        /// <summary>How many controllers are read at once.</summary>
        public const int MaxGamepads = 4;

        /// <summary>A controller's sticks and triggers, after the dead zones and as the controller reports them.</summary>
        public readonly record struct GamepadAxes(
            Vector2 LeftStick, Vector2 RightStick, float LeftTrigger, float RightTrigger,
            Vector2 LeftStickRaw, Vector2 RightStickRaw, float LeftTriggerRaw, float RightTriggerRaw);

        private sealed class OtherGamepad
        {
            public bool Connected;
            public GamepadAxes Axes;
            public readonly bool[] Down = new bool[GamepadButtonCount];
            public readonly bool[] Pressed = new bool[GamepadButtonCount];
            public readonly bool[] Released = new bool[GamepadButtonCount];
            public float RumbleLow, RumbleHigh, RumbleSeconds;
        }

        private readonly OtherGamepad[] _otherPads = [new(), new(), new()];

        /// <summary>Controller 1 to 3's state, or null for controller 0 and numbers out of range.</summary>
        private OtherGamepad Other(int pad) => pad >= 1 && pad < MaxGamepads ? _otherPads[pad - 1] : null;

        private static bool ValidButton(GamepadButton button) => (uint)button < GamepadButtonCount;

        /// <summary>True while that controller (0 to 3) is plugged in.</summary>
        public bool IsGamepadConnected(int pad) => pad == 0 ? GamepadConnected : Other(pad)?.Connected ?? false;

        /// <summary>The platform window says whether controller 1 to 3 is there; 0 is <see cref="GamepadConnected"/>.</summary>
        public void SetGamepadConnected(int pad, bool connected)
        {
            if (pad == 0) { GamepadConnected = connected; return; }
            OtherGamepad other = Other(pad);
            if (other != null) other.Connected = connected;
        }

        /// <summary>How many controllers are plugged in.</summary>
        public int GamepadCount
        {
            get
            {
                int count = GamepadConnected ? 1 : 0;
                foreach (OtherGamepad other in _otherPads) if (other.Connected) count++;
                return count;
            }
        }

        public bool IsDown(int pad, GamepadButton button) =>
            ValidButton(button) && (pad == 0 ? _padDown[(int)button] : Other(pad)?.Down[(int)button] ?? false);

        public bool WasPressed(int pad, GamepadButton button) =>
            ValidButton(button) && (pad == 0 ? _padPressed[(int)button] : Other(pad)?.Pressed[(int)button] ?? false);

        public bool WasReleased(int pad, GamepadButton button) =>
            ValidButton(button) && (pad == 0 ? _padReleased[(int)button] : Other(pad)?.Released[(int)button] ?? false);

        /// <summary>The platform window reports each button of each controller every frame; edges are worked out here.</summary>
        public void SetGamepadButton(int pad, GamepadButton button, bool down)
        {
            if (pad == 0) { SetGamepadButton(button, down); return; }
            OtherGamepad other = Other(pad);
            if (other == null || !ValidButton(button)) return;
            int index = (int)button;
            if (other.Down[index] == down) return;
            other.Down[index] = down;
            if (down) other.Pressed[index] = true;
            else other.Released[index] = true;
        }

        public GamepadAxes GetGamepadAxes(int pad) => pad == 0
            ? new GamepadAxes(LeftStick, RightStick, LeftTrigger, RightTrigger, LeftStickRaw, RightStickRaw, LeftTriggerRaw, RightTriggerRaw)
            : Other(pad)?.Axes ?? default;

        public void SetGamepadAxes(int pad, in GamepadAxes axes)
        {
            if (pad == 0)
            {
                LeftStick = axes.LeftStick; RightStick = axes.RightStick;
                LeftTrigger = axes.LeftTrigger; RightTrigger = axes.RightTrigger;
                LeftStickRaw = axes.LeftStickRaw; RightStickRaw = axes.RightStickRaw;
                LeftTriggerRaw = axes.LeftTriggerRaw; RightTriggerRaw = axes.RightTriggerRaw;
                return;
            }

            OtherGamepad other = Other(pad);
            if (other != null) other.Axes = axes;
        }

        /// <summary>That controller has gone: everything held on it is let go.</summary>
        public void ReleaseGamepad(int pad)
        {
            if (pad == 0) { ReleaseGamepad(); return; }
            OtherGamepad other = Other(pad);
            if (other == null) return;
            for (int i = 0; i < GamepadButtonCount; i++)
                if (other.Down[i]) { other.Down[i] = false; other.Released[i] = true; }
            other.Axes = default;
        }

        /// <summary>Runs one controller's motors, as <see cref="Rumble(float, float, float)"/> does for controller 0.</summary>
        public void Rumble(int pad, float low, float high, float seconds)
        {
            if (pad == 0) { Rumble(low, high, seconds); return; }
            OtherGamepad other = Other(pad);
            if (other == null) return;
            other.RumbleLow = Math.Clamp(float.IsFinite(low) ? low : 0f, 0f, 1f);
            other.RumbleHigh = Math.Clamp(float.IsFinite(high) ? high : 0f, 0f, 1f);
            other.RumbleSeconds = Math.Clamp(float.IsFinite(seconds) ? seconds : 0f, 0f, 10f);
        }

        /// <summary>A controller's motor strengths and how long they still run.</summary>
        public (float Low, float High, float Seconds) GetRumble(int pad)
        {
            if (pad == 0) return (RumbleLow, RumbleHigh, RumbleSeconds);
            OtherGamepad other = Other(pad);
            return other == null ? (0f, 0f, 0f) : (other.RumbleLow, other.RumbleHigh, other.RumbleSeconds);
        }

        /// <summary>Counts a controller's vibration down by the frame's time.</summary>
        public void AdvanceRumble(int pad, float seconds)
        {
            if (pad == 0) { if (RumbleSeconds > 0f) RumbleSeconds = MathF.Max(0f, RumbleSeconds - seconds); return; }
            OtherGamepad other = Other(pad);
            if (other != null && other.RumbleSeconds > 0f) other.RumbleSeconds = MathF.Max(0f, other.RumbleSeconds - seconds);
        }

        public bool IsDown(Key key) => _down.Contains(key);
        public bool WasPressed(Key key) => _pressed.Contains(key);
        public bool WasReleased(Key key) => _released.Contains(key);
        public bool AnyKeyDown => _down.Count > 0;
        public bool AnyKeyPressed => _pressed.Count > 0;
        /// <summary>The keys that went down this frame (for rebinding: "press a key").</summary>
        public IReadOnlyCollection<Key> PressedKeys => _pressed;

        /// <summary>The game's named actions and what they are bound to (<see cref="InputActions"/>).</summary>
        public InputActions Actions { get; } = new();
        public bool AnyKeyReleased => _released.Count > 0;

        public bool IsDown(MouseButton b) => _mouseDown[(int)b];
        public bool WasPressed(MouseButton b) => _mousePressed[(int)b];
        public bool WasReleased(MouseButton b) => _mouseReleased[(int)b];

        public void OnKeyDown(Key key)
        {
            if (key == Key.Unknown) return;
            if (_down.Add(key))
                _pressed.Add(key);
        }

        public void OnKeyUp(Key key)
        {
            if (key == Key.Unknown) return;
            if (_down.Remove(key))
                _released.Add(key);
        }

        public void OnMouseDown(MouseButton b)
        {
            if (!_mouseDown[(int)b])
                _mousePressed[(int)b] = true;
            _mouseDown[(int)b] = true;
        }

        public void OnMouseUp(MouseButton b)
        {
            if (_mouseDown[(int)b])
                _mouseReleased[(int)b] = true;
            _mouseDown[(int)b] = false;
        }

        public void OnMouseMove(float x, float y) => MousePosition = new Vector2(x, y);
        public void OnWheel(float detents) => WheelDelta += detents;

        /// <summary>Hides the keyboard from the game for this frame (a debug text box has it).</summary>
        public void SuppressKeyboard()
        {
            _down.Clear();
            _pressed.Clear();
            _released.Clear();
        }

        public void ClearHeld()
        {
            _down.Clear();
            Array.Clear(_mouseDown);
            Array.Clear(_padDown);
            foreach (OtherGamepad other in _otherPads) Array.Clear(other.Down);
        }

        // ── Recording and replay (InputReplay) ──

        /// <summary>Copies everything a game can read this frame into <paramref name="frame"/>.</summary>
        public void CaptureFrame(ref InputFrame frame)
        {
            frame.KeysDown = KeyBits(_down);
            frame.KeysPressed = KeyBits(_pressed);
            frame.KeysReleased = KeyBits(_released);
            frame.MouseDown = MouseBits(_mouseDown);
            frame.MousePressed = MouseBits(_mousePressed);
            frame.MouseReleased = MouseBits(_mouseReleased);
            frame.MouseX = MousePosition.X;
            frame.MouseY = MousePosition.Y;
            frame.Wheel = WheelDelta;
            frame.LookX = LookDelta.X;
            frame.LookY = LookDelta.Y;
            frame.GamepadConnected = GamepadConnected;
            frame.PadDown = PadBits(_padDown);
            frame.PadPressed = PadBits(_padPressed);
            frame.PadReleased = PadBits(_padReleased);
            frame.PadEdges = (ushort)((StartPressed ? 1 : 0) | (BackPressed ? 2 : 0) | (ConfirmPressed ? 4 : 0)
                | (XPressed ? 8 : 0) | (YPressed ? 16 : 0) | (DPadUpPressed ? 32 : 0) | (DPadDownPressed ? 64 : 0)
                | (DPadLeftPressed ? 128 : 0) | (DPadRightPressed ? 256 : 0) | (LeftBumperPressed ? 512 : 0)
                | (RightBumperPressed ? 1024 : 0) | (RecipeBookPressed ? 2048 : 0) | (LeftStickPressed ? 4096 : 0));
            frame.LeftStick = LeftStick;
            frame.RightStick = RightStick;
            frame.LeftTrigger = LeftTrigger;
            frame.RightTrigger = RightTrigger;
            frame.LeftStickRaw = LeftStickRaw;
            frame.RightStickRaw = RightStickRaw;
            frame.LeftTriggerRaw = LeftTriggerRaw;
            frame.RightTriggerRaw = RightTriggerRaw;
            frame.Typed = _typed.Length > 0 ? _typed.ToString() : null;
        }

        /// <summary>
        /// Replaces everything a game can read this frame with <paramref name="frame"/>: whatever the
        /// keyboard, mouse and controller did since the last frame is overwritten.
        /// </summary>
        public void ApplyFrame(in InputFrame frame)
        {
            SetKeys(_down, frame.KeysDown);
            SetKeys(_pressed, frame.KeysPressed);
            SetKeys(_released, frame.KeysReleased);
            SetMouse(_mouseDown, frame.MouseDown);
            SetMouse(_mousePressed, frame.MousePressed);
            SetMouse(_mouseReleased, frame.MouseReleased);
            MousePosition = new Vector2(frame.MouseX, frame.MouseY);
            WheelDelta = frame.Wheel;
            LookDelta = new Vector2(frame.LookX, frame.LookY);
            GamepadConnected = frame.GamepadConnected;
            SetPad(_padDown, frame.PadDown);
            SetPad(_padPressed, frame.PadPressed);
            SetPad(_padReleased, frame.PadReleased);
            int edges = frame.PadEdges;
            StartPressed = (edges & 1) != 0;
            BackPressed = (edges & 2) != 0;
            ConfirmPressed = (edges & 4) != 0;
            XPressed = (edges & 8) != 0;
            YPressed = (edges & 16) != 0;
            DPadUpPressed = (edges & 32) != 0;
            DPadDownPressed = (edges & 64) != 0;
            DPadLeftPressed = (edges & 128) != 0;
            DPadRightPressed = (edges & 256) != 0;
            LeftBumperPressed = (edges & 512) != 0;
            RightBumperPressed = (edges & 1024) != 0;
            RecipeBookPressed = (edges & 2048) != 0;
            LeftStickPressed = (edges & 4096) != 0;
            LeftStick = frame.LeftStick;
            RightStick = frame.RightStick;
            LeftTrigger = frame.LeftTrigger;
            RightTrigger = frame.RightTrigger;
            LeftStickRaw = frame.LeftStickRaw;
            RightStickRaw = frame.RightStickRaw;
            LeftTriggerRaw = frame.LeftTriggerRaw;
            RightTriggerRaw = frame.RightTriggerRaw;
            _typed.Clear();
            if (!string.IsNullOrEmpty(frame.Typed)) _typed.Append(frame.Typed);
        }

        /// <summary>Lets go of every key, button, stick and trigger, as when a replay hands back to the real devices.</summary>
        public void ReleaseAll()
        {
            // The pointer stays where it was: the real mouse reports a new place when it moves.
            Vector2 mouse = MousePosition;
            ApplyFrame(default);
            MousePosition = mouse;
        }

        private static UInt128 KeyBits(HashSet<Key> keys)
        {
            UInt128 bits = UInt128.Zero;
            foreach (Key key in keys)
                if ((int)key > 0 && (int)key < 128) bits |= UInt128.One << (int)key;
            return bits;
        }

        private static void SetKeys(HashSet<Key> keys, UInt128 bits)
        {
            keys.Clear();
            for (int i = 1; i < 128 && bits != UInt128.Zero; i++)
                if ((bits & (UInt128.One << i)) != UInt128.Zero) keys.Add((Key)i);
        }

        private static byte MouseBits(bool[] buttons) =>
            (byte)((buttons[0] ? 1 : 0) | (buttons[1] ? 2 : 0) | (buttons[2] ? 4 : 0));

        private static void SetMouse(bool[] buttons, byte bits)
        {
            for (int i = 0; i < buttons.Length; i++) buttons[i] = (bits & (1 << i)) != 0;
        }

        private static ushort PadBits(bool[] buttons)
        {
            int bits = 0;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i]) bits |= 1 << i;
            return (ushort)bits;
        }

        private static void SetPad(bool[] buttons, ushort bits)
        {
            for (int i = 0; i < buttons.Length; i++) buttons[i] = (bits & (1 << i)) != 0;
        }

        public void NextFrame()
        {
            _textTakenLastFrame = _textTakenThisFrame;
            _textTakenThisFrame = false;
            _pressed.Clear();
            _released.Clear();
            Array.Clear(_mousePressed);
            Array.Clear(_mouseReleased);
            Array.Clear(_padPressed);
            Array.Clear(_padReleased);
            foreach (OtherGamepad other in _otherPads)
            {
                Array.Clear(other.Pressed);
                Array.Clear(other.Released);
            }
            WheelDelta = 0;
            LookDelta = Vector2.Zero;
            StartPressed = false;
            BackPressed = false;
            ConfirmPressed = false;
            XPressed = false;
            YPressed = false;
            DPadUpPressed = false;
            DPadDownPressed = false;
            DPadLeftPressed = false;
            DPadRightPressed = false;
            LeftBumperPressed = false;
            RightBumperPressed = false;
            RecipeBookPressed = false;
        }
    }
}

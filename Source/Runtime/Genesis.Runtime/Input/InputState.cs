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
        }

        /// <summary>Runs the controller's motors: the heavy low one and the light high one, each 0 to 1.</summary>
        public void Rumble(float low, float high, float seconds)
        {
            RumbleLow = Math.Clamp(float.IsFinite(low) ? low : 0f, 0f, 1f);
            RumbleHigh = Math.Clamp(float.IsFinite(high) ? high : 0f, 0f, 1f);
            RumbleSeconds = Math.Clamp(float.IsFinite(seconds) ? seconds : 0f, 0f, 10f);
        }

        public bool IsDown(Key key) => _down.Contains(key);
        public bool WasPressed(Key key) => _pressed.Contains(key);
        public bool WasReleased(Key key) => _released.Contains(key);
        public bool AnyKeyDown => _down.Count > 0;
        public bool AnyKeyPressed => _pressed.Count > 0;
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

        public void ClearHeld()
        {
            _down.Clear();
            Array.Clear(_mouseDown);
            Array.Clear(_padDown);
        }

        public void NextFrame()
        {
            _pressed.Clear();
            _released.Clear();
            Array.Clear(_mousePressed);
            Array.Clear(_mouseReleased);
            Array.Clear(_padPressed);
            Array.Clear(_padReleased);
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

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
        }

        public void NextFrame()
        {
            _pressed.Clear();
            _released.Clear();
            Array.Clear(_mousePressed);
            Array.Clear(_mouseReleased);
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

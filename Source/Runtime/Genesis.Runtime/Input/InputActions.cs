using System;
using System.Collections.Generic;
using System.Text;

namespace Genesis.Runtime.Input
{
    /// <summary>What an input action is bound to: a key, a mouse button or a controller button.</summary>
    public enum InputBindingKind { Key, Mouse, Gamepad, Stick }

    /// <summary>One thing that triggers an action.</summary>
    public readonly record struct InputBinding(InputBindingKind Kind, int Code)
    {
        /// <summary>Modifier keys a key binding needs held (Ctrl+S): add these together.</summary>
        public const int Ctrl = 1, Shift = 2, Alt = 4;

        public static InputBinding ForKey(Key key, int modifiers = 0) => new(InputBindingKind.Key, (int)key | ((modifiers & 7) << 16));

        /// <summary>The modifiers a key binding needs held (<see cref="Ctrl"/>, <see cref="Shift"/>, <see cref="Alt"/>).</summary>
        public int Modifiers => Kind == InputBindingKind.Key ? (Code >> 16) & 7 : 0;
        private string KeyName => ((Modifiers & Ctrl) != 0 ? "Ctrl+" : "") + ((Modifiers & Shift) != 0 ? "Shift+" : "") + ((Modifiers & Alt) != 0 ? "Alt+" : "") + (Key)(Code & 0xFFFF);
        public static InputBinding ForMouse(MouseButton button) => new(InputBindingKind.Mouse, (int)button);
        // A controller's number (0 to 3) is kept above the button or direction, so controller 0's
        // bindings are the numbers they always were.
        public static InputBinding ForGamepad(GamepadButton button, int pad = 0) => new(InputBindingKind.Gamepad, (int)button | (Math.Clamp(pad, 0, 3) << 8));
        public static InputBinding ForDirection(GamepadDirection direction, int pad = 0) => new(InputBindingKind.Stick, (int)direction | (Math.Clamp(pad, 0, 3) << 8));

        /// <summary>Which controller a controller binding is for, 0 to 3.</summary>
        public int Pad => Kind is InputBindingKind.Gamepad or InputBindingKind.Stick ? (Code >> 8) & 3 : 0;
        private int Part => Code & 0xFF;
        private string PadName => Pad == 0 ? "Pad" : "Pad" + (Pad + 1);

        /// <summary>How far a stick or trigger must go to count as held.</summary>
        public const float StickThreshold = 0.5f;

        /// <summary>The name a settings screen shows: "W", "Mouse Left", "Pad A".</summary>
        public string DisplayName => Kind switch
        {
            InputBindingKind.Mouse => "Mouse " + (MouseButton)Code,
            InputBindingKind.Gamepad => PadName + " " + (GamepadButton)Part,
            InputBindingKind.Stick => PadName + " " + (GamepadDirection)Part,
            _ => KeyName,
        };

        /// <summary>The form saved in a bindings file ("Key:W", "Mouse:Left", "Pad:A").</summary>
        public string SavedName => Kind switch
        {
            InputBindingKind.Mouse => "Mouse:" + (MouseButton)Code,
            InputBindingKind.Gamepad => PadName + ":" + (GamepadButton)Part,
            InputBindingKind.Stick => (Pad == 0 ? "Stick" : "Stick" + (Pad + 1)) + ":" + (GamepadDirection)Part,
            _ => "Key:" + KeyName,
        };

        public static bool TryParseSaved(string text, out InputBinding binding)
        {
            binding = default;
            int colon = text?.IndexOf(':') ?? -1;
            if (colon <= 0) return false;
            string kind = text[..colon].Trim(), name = text[(colon + 1)..].Trim();
            // "Pad2", "Stick3": the controller's number, 1 to 4, after the kind.
            int pad = 0;
            if (kind.Length > 3 && char.IsDigit(kind[^1]) && (kind.StartsWith("Pad", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("Stick", StringComparison.OrdinalIgnoreCase)))
            {
                pad = kind[^1] - '1';
                if (pad < 0 || pad > 3) return false;
                kind = kind[..^1];
            }
            if (kind.Equals("Key", StringComparison.OrdinalIgnoreCase) && TryParseCombination(name, out Key key, out int modifiers))
                binding = ForKey(key, modifiers);
            else if (kind.Equals("Mouse", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(name, true, out MouseButton mouse) && Enum.IsDefined(mouse))
                binding = ForMouse(mouse);
            else if (kind.Equals("Pad", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(name, true, out GamepadButton padButton) && Enum.IsDefined(padButton))
                binding = ForGamepad(padButton, pad);
            else if ((kind.Equals("Stick", StringComparison.OrdinalIgnoreCase) || kind.Equals("Pad", StringComparison.OrdinalIgnoreCase))
                && Enum.TryParse(name.Replace(" ", ""), true, out GamepadDirection direction) && Enum.IsDefined(direction))
                binding = ForDirection(direction, pad);
            else return false;
            return true;
        }

        /// <summary>"S", "Ctrl+S", "Shift+Alt+Tab": the last part is the key, the others modifiers.</summary>
        public static bool TryParseCombination(string text, out Key key, out int modifiers, Func<string, Key> parseKey = null)
        {
            key = Key.Unknown; modifiers = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= Ctrl;
                else if (parts[i].Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= Shift;
                else if (parts[i].Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= Alt;
                else return false;
            }
            string last = parts[^1];
            key = parseKey != null ? parseKey(last) : Enum.TryParse(last, true, out Key parsed) && Enum.IsDefined(parsed) ? parsed : Key.Unknown;
            return key != Key.Unknown;
        }
    }

    /// <summary>
    /// Named actions ("Jump", "Fire") bound to keys, mouse buttons and controller buttons, which a
    /// player can rebind while the game runs and keep in the game's saves. A script asks for the
    /// action instead of a key, so a settings screen can change what it means.
    /// </summary>
    public sealed class InputActions
    {
        private readonly Dictionary<string, List<InputBinding>> _actions = new(StringComparer.OrdinalIgnoreCase);
        private string _listening;
        private bool _listenReplaces;

        /// <summary>The action waiting for its next key or button, or null.</summary>
        public string Listening => _listening;

        public IEnumerable<string> Names => _actions.Keys;

        /// <summary>Adds a binding to an action (made if new); a binding it already has is not added twice.</summary>
        public void Bind(string action, InputBinding binding)
        {
            if (string.IsNullOrWhiteSpace(action)) return;
            if (!_actions.TryGetValue(action, out List<InputBinding> bindings)) _actions[action] = bindings = new List<InputBinding>(2);
            if (!bindings.Contains(binding)) bindings.Add(binding);
        }

        /// <summary>Removes every binding of an action; the action stays known, bound to nothing.</summary>
        public void Clear(string action)
        {
            if (!string.IsNullOrWhiteSpace(action) && _actions.TryGetValue(action, out List<InputBinding> bindings)) bindings.Clear();
        }

        public int BindingCount(string action) =>
            !string.IsNullOrWhiteSpace(action) && _actions.TryGetValue(action, out List<InputBinding> bindings) ? bindings.Count : 0;

        public InputBinding? BindingAt(string action, int index) =>
            !string.IsNullOrWhiteSpace(action) && _actions.TryGetValue(action, out List<InputBinding> bindings)
                && index >= 0 && index < bindings.Count ? bindings[index] : null;

        public bool IsDown(InputState input, string action) => Any(input, action, 0);
        public bool WasPressed(InputState input, string action) => Any(input, action, 1);
        public bool WasReleased(InputState input, string action) => Any(input, action, 2);

        /// <summary>
        /// How strongly an action is held, from 0 to 1: 1 for a key or button down, how far a stick
        /// is pushed or a trigger pulled for those; the strongest of its bindings.
        /// </summary>
        public float Value(InputState input, string action)
        {
            if (input == null || string.IsNullOrWhiteSpace(action) || !_actions.TryGetValue(action, out List<InputBinding> bindings)) return 0f;
            float strongest = 0f;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding binding = bindings[i];
                float value = binding.Kind == InputBindingKind.Stick ? input.DirectionValue(binding.Pad, (GamepadDirection)(binding.Code & 0xFF))
                    : Hit(input, binding, 0) ? 1f : 0f;
                if (value > strongest) strongest = value;
            }
            return strongest;
        }

        /// <summary>From -1 to 1 between two actions: Axis("Left", "Right"); smooth for a stick, -1, 0 or 1 for keys.</summary>
        public float Axis(InputState input, string negative, string positive) => Value(input, positive) - Value(input, negative);

        private bool Any(InputState input, string action, int query)
        {
            if (input == null || string.IsNullOrWhiteSpace(action) || !_actions.TryGetValue(action, out List<InputBinding> bindings)) return false;
            for (int i = 0; i < bindings.Count; i++)
                if (Hit(input, bindings[i], query)) return true;
            return false;
        }

        private static bool Hit(InputState input, InputBinding binding, int query)
        {
            if (binding.Kind == InputBindingKind.Stick)
            {
                var direction = (GamepadDirection)(binding.Code & 0xFF);
                float now = input.DirectionValue(binding.Pad, direction);
                float before = input.PreviousDirectionValue(binding.Pad, direction);
                return query == 0 ? now >= InputBinding.StickThreshold
                    : query == 1 ? now >= InputBinding.StickThreshold && before < InputBinding.StickThreshold
                    : now < InputBinding.StickThreshold && before >= InputBinding.StickThreshold;
            }
            return binding.Kind switch
            {
                InputBindingKind.Mouse => query == 0 ? input.IsDown((MouseButton)binding.Code)
                    : query == 1 ? input.WasPressed((MouseButton)binding.Code) : input.WasReleased((MouseButton)binding.Code),
                InputBindingKind.Gamepad => query == 0 ? input.IsDown(binding.Pad, (GamepadButton)(binding.Code & 0xFF))
                    : query == 1 ? input.WasPressed(binding.Pad, (GamepadButton)(binding.Code & 0xFF)) : input.WasReleased(binding.Pad, (GamepadButton)(binding.Code & 0xFF)),
                _ => ModifiersHeld(input, binding.Modifiers) && (query == 0 ? input.IsDown((Key)(binding.Code & 0xFFFF))
                    : query == 1 ? input.WasPressed((Key)(binding.Code & 0xFFFF)) : input.WasReleased((Key)(binding.Code & 0xFFFF))),
            };
        }

        private static bool ModifiersHeld(InputState input, int modifiers) =>
            ((modifiers & InputBinding.Ctrl) == 0 || input.IsDown(Key.Control))
            && ((modifiers & InputBinding.Shift) == 0 || input.IsDown(Key.Shift))
            && ((modifiers & InputBinding.Alt) == 0 || input.IsDown(Key.Alt));

        private static bool IsModifier(Key key) => key is Key.Control or Key.Shift or Key.Alt;

        // A modifier pressed while listening: bound alone when it is let go with no other key pressed
        // meanwhile, or held for the key pressed with it (Ctrl then S binds Ctrl+S).
        private Key _heldModifier = Key.Unknown;
        private bool _modifierUsed;

        /// <summary>
        /// Waits for the next key, mouse button or controller button pressed, which then binds the
        /// action (replacing its bindings, or added to them). Escape gives up.
        /// </summary>
        public void Listen(string action, bool replace = true)
        {
            _listening = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
            _listenReplaces = replace;
            _heldModifier = Key.Unknown;
        }

        public void StopListening() => _listening = null;

        /// <summary>
        /// While listening, takes this frame's first press: true when it bound the action or gave
        /// up (Escape), false while still waiting or when not listening.
        /// </summary>
        public bool TakeListenedPress(InputState input)
        {
            if (_listening == null || input == null) return false;
            InputBinding? pressed = null;
            if (input.WasPressed(Key.Escape)) { _listening = null; return true; }
            foreach (Key key in input.PressedKeys)
            {
                if (IsModifier(key)) { if (_heldModifier == Key.Unknown) { _heldModifier = key; _modifierUsed = false; } continue; }
                int modifiers = (input.IsDown(Key.Control) ? InputBinding.Ctrl : 0) | (input.IsDown(Key.Shift) ? InputBinding.Shift : 0) | (input.IsDown(Key.Alt) ? InputBinding.Alt : 0);
                pressed = InputBinding.ForKey(key, modifiers);
                _modifierUsed = true;
                break;
            }
            if (pressed == null && _heldModifier != Key.Unknown && input.WasReleased(_heldModifier) && !_modifierUsed)
                pressed = InputBinding.ForKey(_heldModifier);
            if (pressed == null)
                foreach (MouseButton button in Enum.GetValues<MouseButton>())
                    if (input.WasPressed(button)) { pressed = InputBinding.ForMouse(button); break; }
            for (int pad = 0; pad < InputState.MaxGamepads && pressed == null; pad++)
            {
                foreach (GamepadButton button in Enum.GetValues<GamepadButton>())
                    if (input.WasPressed(pad, button)) { pressed = InputBinding.ForGamepad(button, pad); break; }
                if (pressed == null)
                    foreach (GamepadDirection direction in Enum.GetValues<GamepadDirection>())
                    {
                        InputBinding stick = InputBinding.ForDirection(direction, pad);
                        if (Hit(input, stick, 1)) { pressed = stick; break; }
                    }
            }
            if (pressed == null) return false;
            _heldModifier = Key.Unknown;
            if (_listenReplaces) Clear(_listening);
            Bind(_listening, pressed.Value);
            _listening = null;
            return true;
        }

        /// <summary>One line per action: <c>Jump=Key:Space;Pad:A</c>.</summary>
        public string Save()
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, List<InputBinding>> action in _actions)
            {
                text.Append(action.Key).Append('=');
                for (int i = 0; i < action.Value.Count; i++)
                {
                    if (i > 0) text.Append(';');
                    text.Append(action.Value[i].SavedName);
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>
        /// Reads what <see cref="Save"/> wrote: each action named replaces its bindings; actions it
        /// does not name keep theirs (a game's new actions keep their defaults). Returns how many
        /// actions it read.
        /// </summary>
        public int Load(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int read = 0;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string action = line[..equals].Trim();
                if (action.Length == 0) continue;
                if (_actions.TryGetValue(action, out List<InputBinding> existing)) existing.Clear();
                else _actions[action] = new List<InputBinding>(2);
                foreach (string part in line[(equals + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (InputBinding.TryParseSaved(part, out InputBinding binding)) Bind(action, binding);
                read++;
            }
            return read;
        }
    }
}

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
        public static InputBinding ForKey(Key key) => new(InputBindingKind.Key, (int)key);
        public static InputBinding ForMouse(MouseButton button) => new(InputBindingKind.Mouse, (int)button);
        public static InputBinding ForGamepad(GamepadButton button) => new(InputBindingKind.Gamepad, (int)button);
        public static InputBinding ForDirection(GamepadDirection direction) => new(InputBindingKind.Stick, (int)direction);

        /// <summary>How far a stick or trigger must go to count as held.</summary>
        public const float StickThreshold = 0.5f;

        /// <summary>The name a settings screen shows: "W", "Mouse Left", "Pad A".</summary>
        public string DisplayName => Kind switch
        {
            InputBindingKind.Mouse => "Mouse " + (MouseButton)Code,
            InputBindingKind.Gamepad => "Pad " + (GamepadButton)Code,
            InputBindingKind.Stick => "Pad " + (GamepadDirection)Code,
            _ => ((Key)Code).ToString(),
        };

        /// <summary>The form saved in a bindings file ("Key:W", "Mouse:Left", "Pad:A").</summary>
        public string SavedName => Kind switch
        {
            InputBindingKind.Mouse => "Mouse:" + (MouseButton)Code,
            InputBindingKind.Gamepad => "Pad:" + (GamepadButton)Code,
            InputBindingKind.Stick => "Stick:" + (GamepadDirection)Code,
            _ => "Key:" + (Key)Code,
        };

        public static bool TryParseSaved(string text, out InputBinding binding)
        {
            binding = default;
            int colon = text?.IndexOf(':') ?? -1;
            if (colon <= 0) return false;
            string kind = text[..colon].Trim(), name = text[(colon + 1)..].Trim();
            if (kind.Equals("Key", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(name, true, out Key key) && Enum.IsDefined(key) && key != Key.Unknown)
                binding = ForKey(key);
            else if (kind.Equals("Mouse", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(name, true, out MouseButton mouse) && Enum.IsDefined(mouse))
                binding = ForMouse(mouse);
            else if (kind.Equals("Pad", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(name, true, out GamepadButton pad) && Enum.IsDefined(pad))
                binding = ForGamepad(pad);
            else if ((kind.Equals("Stick", StringComparison.OrdinalIgnoreCase) || kind.Equals("Pad", StringComparison.OrdinalIgnoreCase))
                && Enum.TryParse(name.Replace(" ", ""), true, out GamepadDirection direction) && Enum.IsDefined(direction))
                binding = ForDirection(direction);
            else return false;
            return true;
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
                float value = binding.Kind == InputBindingKind.Stick ? input.DirectionValue((GamepadDirection)binding.Code)
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
                float now = input.DirectionValue((GamepadDirection)binding.Code);
                float before = input.PreviousDirectionValue((GamepadDirection)binding.Code);
                return query == 0 ? now >= InputBinding.StickThreshold
                    : query == 1 ? now >= InputBinding.StickThreshold && before < InputBinding.StickThreshold
                    : now < InputBinding.StickThreshold && before >= InputBinding.StickThreshold;
            }
            return binding.Kind switch
            {
                InputBindingKind.Mouse => query == 0 ? input.IsDown((MouseButton)binding.Code)
                    : query == 1 ? input.WasPressed((MouseButton)binding.Code) : input.WasReleased((MouseButton)binding.Code),
                InputBindingKind.Gamepad => query == 0 ? input.IsDown((GamepadButton)binding.Code)
                    : query == 1 ? input.WasPressed((GamepadButton)binding.Code) : input.WasReleased((GamepadButton)binding.Code),
                _ => query == 0 ? input.IsDown((Key)binding.Code)
                    : query == 1 ? input.WasPressed((Key)binding.Code) : input.WasReleased((Key)binding.Code),
            };
        }

        /// <summary>
        /// Waits for the next key, mouse button or controller button pressed, which then binds the
        /// action (replacing its bindings, or added to them). Escape gives up.
        /// </summary>
        public void Listen(string action, bool replace = true)
        {
            _listening = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
            _listenReplaces = replace;
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
            foreach (Key key in input.PressedKeys) { pressed = InputBinding.ForKey(key); break; }
            if (pressed == null)
                foreach (MouseButton button in Enum.GetValues<MouseButton>())
                    if (input.WasPressed(button)) { pressed = InputBinding.ForMouse(button); break; }
            if (pressed == null)
                foreach (GamepadButton button in Enum.GetValues<GamepadButton>())
                    if (input.WasPressed(button)) { pressed = InputBinding.ForGamepad(button); break; }
            if (pressed == null)
                foreach (GamepadDirection direction in Enum.GetValues<GamepadDirection>())
                {
                    InputBinding stick = InputBinding.ForDirection(direction);
                    if (Hit(input, stick, 1)) { pressed = stick; break; }
                }
            if (pressed == null) return false;
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

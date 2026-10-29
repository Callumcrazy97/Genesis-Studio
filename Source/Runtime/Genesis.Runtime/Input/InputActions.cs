using System;
using System.Collections.Generic;
using System.Text;

namespace Genesis.Runtime.Input
{
    /// <summary>
    /// What an input action is bound to: a key, a mouse button, a controller button, a stick
    /// direction, a whole stick axis or trigger (read as a value), or a chord of several held together.
    /// </summary>
    public enum InputBindingKind { Key, Mouse, Gamepad, Stick, Axis, Chord }

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

        /// <summary>A whole stick axis (-1 to 1) or trigger (0 to 1), read as the action's value.</summary>
        public static InputBinding ForAxis(GamepadAxis axis, int pad = 0) => new(InputBindingKind.Axis, (int)axis | (Math.Clamp(pad, 0, 3) << 8));

        /// <summary>
        /// Two to four bindings held together (LB+A, Q+E): held while every one is, pressed once each
        /// time the last of them goes down. Its parts are keys, mouse buttons, controller buttons and
        /// stick directions or axes, not other chords. A part's own binding keeps working by itself.
        /// </summary>
        public static InputBinding ForChord(params InputBinding[] parts) => new(InputBindingKind.Chord, InputChords.Intern(parts));

        /// <summary>A chord's parts, in the order given; any other binding is its own one part.</summary>
        public IReadOnlyList<InputBinding> Parts => Kind == InputBindingKind.Chord ? InputChords.Parts(Code) : [this];

        /// <summary>Which controller a controller binding is for, 0 to 3.</summary>
        public int Pad => Kind is InputBindingKind.Gamepad or InputBindingKind.Stick or InputBindingKind.Axis ? (Code >> 8) & 3 : 0;
        private int Part => Code & 0xFF;
        private string PadName => Pad == 0 ? "Pad" : "Pad" + (Pad + 1);

        /// <summary>A controller's: a button, stick or trigger, or a chord made only of those.</summary>
        public bool IsController
        {
            get
            {
                if (Kind is InputBindingKind.Gamepad or InputBindingKind.Stick or InputBindingKind.Axis) return true;
                if (Kind != InputBindingKind.Chord) return false;
                foreach (InputBinding part in InputChords.Parts(Code)) if (!part.IsController) return false;
                return true;
            }
        }

        /// <summary>The same binding on controller 0, as a controller's own set keeps it.</summary>
        public InputBinding OnFirstPad() => Kind switch
        {
            InputBindingKind.Gamepad or InputBindingKind.Stick or InputBindingKind.Axis => new(Kind, Code & 0xFF),
            InputBindingKind.Chord => ForChord(Array.ConvertAll(InputChords.Parts(Code), part => part.OnFirstPad())),
            _ => this,
        };

        /// <summary>How far a stick or trigger must go to count as held.</summary>
        public const float StickThreshold = 0.5f;

        /// <summary>The name a settings screen shows: "W", "Mouse Left", "Pad A", "Pad LeftStickX", "Pad LeftBumper + Pad A".</summary>
        public string DisplayName => Kind switch
        {
            InputBindingKind.Mouse => "Mouse " + (MouseButton)Code,
            InputBindingKind.Gamepad => PadName + " " + (GamepadButton)Part,
            InputBindingKind.Stick => PadName + " " + (GamepadDirection)Part,
            InputBindingKind.Axis => PadName + " " + (GamepadAxis)Part,
            InputBindingKind.Chord => string.Join(" + ", Array.ConvertAll(InputChords.Parts(Code), part => part.DisplayName)),
            _ => KeyName,
        };

        /// <summary>The form saved in a bindings file ("Key:W", "Mouse:Left", "Pad:A", "Axis:LeftStickX", "Chord:Pad:LeftBumper&amp;Pad:A").</summary>
        public string SavedName => Kind switch
        {
            InputBindingKind.Mouse => "Mouse:" + (MouseButton)Code,
            InputBindingKind.Gamepad => PadName + ":" + (GamepadButton)Part,
            InputBindingKind.Stick => (Pad == 0 ? "Stick" : "Stick" + (Pad + 1)) + ":" + (GamepadDirection)Part,
            InputBindingKind.Axis => (Pad == 0 ? "Axis" : "Axis" + (Pad + 1)) + ":" + (GamepadAxis)Part,
            InputBindingKind.Chord => "Chord:" + string.Join("&", Array.ConvertAll(InputChords.Parts(Code), part => part.SavedName)),
            _ => "Key:" + KeyName,
        };

        public static bool TryParseSaved(string text, out InputBinding binding)
        {
            binding = default;
            int colon = text?.IndexOf(':') ?? -1;
            if (colon <= 0) return false;
            string kind = text[..colon].Trim(), name = text[(colon + 1)..].Trim();
            if (kind.Equals("Chord", StringComparison.OrdinalIgnoreCase))
            {
                string[] texts = name.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (texts.Length < 2 || texts.Length > InputChords.MaxParts) return false;
                var parts = new InputBinding[texts.Length];
                for (int i = 0; i < texts.Length; i++)
                    if (!TryParseSaved(texts[i], out parts[i]) || parts[i].Kind == InputBindingKind.Chord) return false;
                binding = ForChord(parts);
                return true;
            }
            // "Pad2", "Stick3", "Axis4": the controller's number, 1 to 4, after the kind.
            int pad = 0;
            if (kind.Length > 3 && char.IsDigit(kind[^1]) && (kind.StartsWith("Pad", StringComparison.OrdinalIgnoreCase)
                || kind.StartsWith("Stick", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("Axis", StringComparison.OrdinalIgnoreCase)))
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
            else if ((kind.Equals("Axis", StringComparison.OrdinalIgnoreCase) || kind.Equals("Pad", StringComparison.OrdinalIgnoreCase))
                && Enum.TryParse(name.Replace(" ", ""), true, out GamepadAxis axis) && Enum.IsDefined(axis))
                binding = ForAxis(axis, pad);
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
    /// The chords in use, by number: a binding is a kind and one number, so a chord's binding names
    /// its parts here. The same parts in the same order are always the same chord.
    /// </summary>
    internal static class InputChords
    {
        public const int MaxParts = 4;
        private static readonly object Gate = new();
        private static readonly List<InputBinding[]> Chords = [];
        private static readonly Dictionary<string, int> Numbers = new(StringComparer.Ordinal);

        public static int Intern(InputBinding[] parts)
        {
            if (parts is null || parts.Length < 2 || parts.Length > MaxParts)
                throw new ArgumentException("A chord is two to four bindings held together.", nameof(parts));
            var key = new StringBuilder();
            foreach (InputBinding part in parts)
            {
                if (part.Kind == InputBindingKind.Chord) throw new ArgumentException("A chord's parts are single bindings.", nameof(parts));
                key.Append((int)part.Kind).Append(':').Append(part.Code).Append('&');
            }
            lock (Gate)
            {
                if (Numbers.TryGetValue(key.ToString(), out int number)) return number;
                Chords.Add((InputBinding[])parts.Clone());
                number = Chords.Count - 1;
                Numbers[key.ToString()] = number;
                return number;
            }
        }

        public static InputBinding[] Parts(int number)
        {
            lock (Gate) return (uint)number < (uint)Chords.Count ? Chords[number] : [];
        }
    }

    /// <summary>
    /// Named actions ("Jump", "Fire") bound to keys, mouse buttons, controller buttons, sticks and
    /// chords, which a player can rebind while the game runs and keep in the game's saves. A script
    /// asks for the action instead of a key, so a settings screen can change what it means.
    /// </summary>
    /// <remarks>
    /// Each controller (0 to 3) can have bindings of its own for an action, for local multiplayer; a
    /// controller without its own uses the shared ones, read on that controller. A game that never
    /// asks per controller, never sets a dead zone or sensitivity and never binds an axis or chord
    /// reads every action exactly as before these existed.
    /// </remarks>
    public sealed class InputActions
    {
        private const int Controllers = InputState.MaxGamepads;
        private readonly Dictionary<string, List<InputBinding>> _actions = new(StringComparer.OrdinalIgnoreCase);
        // A controller's own bindings, by action; kept on controller 0's codes and read on that controller.
        private readonly Dictionary<string, List<InputBinding>>[] _padActions = new Dictionary<string, List<InputBinding>>[Controllers];
        private readonly Dictionary<string, (float DeadZone, float Sensitivity)> _shaping = new(StringComparer.OrdinalIgnoreCase);
        // The game's own bindings, kept the first time a player changes any, for ResetToDefaults.
        private Dictionary<string, List<InputBinding>> _defaults;
        private readonly HashSet<string> _madeAfterDefaults = new(StringComparer.OrdinalIgnoreCase);
        private string _listening;
        private bool _listenReplaces;
        private int _listenPad = -1;

        /// <summary>False makes every action read as not held (0), as while a rebinding screen is open; true unless set.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The action waiting for its next key or button, or null.</summary>
        public string Listening => _listening;

        /// <summary>The controller (0 to 3) whose own bindings the listened press goes into; -1 for the shared ones.</summary>
        public int ListeningPad => _listening == null ? -1 : _listenPad;

        public IEnumerable<string> Names => _actions.Keys;

        /// <summary>Adds a binding to an action (made if new); a binding it already has is not added twice.</summary>
        public void Bind(string action, InputBinding binding)
        {
            if (string.IsNullOrWhiteSpace(action)) return;
            Add(_actions, action, binding);
            // An action the game makes after a player changed bindings is the game's too, for Reset.
            if (_defaults != null && (!_defaults.ContainsKey(action) || _madeAfterDefaults.Contains(action)))
            {
                _madeAfterDefaults.Add(action);
                Add(_defaults, action, binding);
            }
        }

        private static void Add(Dictionary<string, List<InputBinding>> actions, string action, InputBinding binding)
        {
            if (!actions.TryGetValue(action, out List<InputBinding> bindings)) actions[action] = bindings = new List<InputBinding>(2);
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

        // ── One controller's own bindings ──

        private List<InputBinding> ListFor(string action, int pad)
        {
            if (string.IsNullOrWhiteSpace(action)) return null;
            if ((uint)pad < Controllers && _padActions[pad] is { } own && own.TryGetValue(action, out List<InputBinding> list)) return list;
            return _actions.TryGetValue(action, out List<InputBinding> shared) ? shared : null;
        }

        /// <summary>True when the controller (0 to 3) has bindings of its own for the action rather than the shared ones.</summary>
        public bool HasOwnBindings(int pad, string action) =>
            (uint)pad < Controllers && !string.IsNullOrWhiteSpace(action) && _padActions[pad]?.ContainsKey(action) == true;

        /// <summary>How many bindings the action has on that controller: its own, or the shared controller bindings.</summary>
        public int BindingCount(string action, int pad) => ControllerBindings(action, pad).Count;

        public InputBinding? BindingAt(string action, int index, int pad)
        {
            List<InputBinding> bindings = ControllerBindings(action, pad);
            return index >= 0 && index < bindings.Count ? bindings[index] : null;
        }

        // The bindings a controller reads an action with: its keys and mice are not a controller's.
        private List<InputBinding> ControllerBindings(string action, int pad)
        {
            var result = new List<InputBinding>(2);
            if (ListFor(action, pad) is { } list)
                foreach (InputBinding binding in list) if (binding.IsController) result.Add(binding);
            return result;
        }

        /// <summary>
        /// Gives a controller (0 to 3) a binding of its own for an action; its first own binding
        /// replaces the shared ones on that controller. The binding is kept as controller 0's.
        /// </summary>
        public void BindForPad(int pad, string action, InputBinding binding)
        {
            if ((uint)pad >= Controllers || string.IsNullOrWhiteSpace(action) || !binding.IsController) return;
            EnsureDefaults();
            Add(_padActions[pad] ??= new Dictionary<string, List<InputBinding>>(StringComparer.OrdinalIgnoreCase), action.Trim(), binding.OnFirstPad());
        }

        /// <summary>Unbinds an action on one controller (it then has its own, empty set: nothing on that controller triggers it).</summary>
        public void ClearForPad(int pad, string action)
        {
            if ((uint)pad >= Controllers || string.IsNullOrWhiteSpace(action)) return;
            EnsureDefaults();
            (_padActions[pad] ??= new Dictionary<string, List<InputBinding>>(StringComparer.OrdinalIgnoreCase))[action.Trim()] = [];
        }

        /// <summary>Drops a controller's own bindings: it reads the shared ones again.</summary>
        public void ResetPad(int pad)
        {
            if ((uint)pad < Controllers) _padActions[pad] = null;
        }

        /// <summary>
        /// Puts back the bindings the game made (before a player rebound, loaded or gave a controller
        /// its own) and drops every controller's own.
        /// </summary>
        public void ResetToDefaults()
        {
            StopListening();
            if (_defaults != null)
                foreach (KeyValuePair<string, List<InputBinding>> action in _defaults) _actions[action.Key] = new List<InputBinding>(action.Value);
            for (int pad = 0; pad < Controllers; pad++) _padActions[pad] = null;
        }

        private void EnsureDefaults()
        {
            if (_defaults != null) return;
            _defaults = new Dictionary<string, List<InputBinding>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<InputBinding>> action in _actions) _defaults[action.Key] = new List<InputBinding>(action.Value);
        }

        // ── Dead zone and sensitivity ──

        /// <summary>
        /// How far a stick or trigger bound to the action must move before it counts (0 to 0.95); the
        /// rest of its travel is spread over 0 to 1 again. 0 unless set. Keys and buttons are not changed.
        /// </summary>
        public void SetDeadZone(string action, float amount)
        {
            if (string.IsNullOrWhiteSpace(action) || !float.IsFinite(amount)) return;
            (float _, float sensitivity) = Shaping(action);
            _shaping[action.Trim()] = (Math.Clamp(amount, 0f, 0.95f), sensitivity);
        }

        public float DeadZone(string action) => Shaping(action).DeadZone;

        /// <summary>
        /// How strongly a stick or trigger bound to the action counts, 0.01 to 10 (1 unless set): 2
        /// reaches full value half way. Values stay within -1 to 1; keys and buttons are not changed.
        /// </summary>
        public void SetSensitivity(string action, float amount)
        {
            if (string.IsNullOrWhiteSpace(action) || !float.IsFinite(amount)) return;
            (float deadZone, float _) = Shaping(action);
            _shaping[action.Trim()] = (deadZone, Math.Clamp(amount, 0.01f, 10f));
        }

        public float Sensitivity(string action) => Shaping(action).Sensitivity;

        private (float DeadZone, float Sensitivity) Shaping(string action) =>
            !string.IsNullOrWhiteSpace(action) && _shaping.TryGetValue(action.Trim(), out var shaping) ? shaping : (0f, 1f);

        /// <summary>A stick or trigger value through a dead zone and a sensitivity, kept within -1 to 1.</summary>
        public static float Shape(float value, float deadZone, float sensitivity)
        {
            float size = MathF.Abs(value);
            if (size <= deadZone) return 0f;
            if (deadZone > 0f) size = (size - deadZone) / (1f - deadZone);
            return MathF.Sign(value) * MathF.Min(1f, size * sensitivity);
        }

        // ── Reading actions ──

        public bool IsDown(InputState input, string action) => Any(input, action, 0, -1);
        public bool WasPressed(InputState input, string action) => Any(input, action, 1, -1);
        public bool WasReleased(InputState input, string action) => Any(input, action, 2, -1);

        /// <summary>The action on one controller (0 to 3): its own bindings, or the shared ones read on it.</summary>
        public bool IsDown(InputState input, string action, int pad) => (uint)pad < Controllers && Any(input, action, 0, pad);
        public bool WasPressed(InputState input, string action, int pad) => (uint)pad < Controllers && Any(input, action, 1, pad);
        public bool WasReleased(InputState input, string action, int pad) => (uint)pad < Controllers && Any(input, action, 2, pad);

        /// <summary>
        /// How strongly an action is held: 1 for a key, button or chord down; how far a stick is pushed
        /// or a trigger pulled (0 to 1); a whole stick axis from -1 to 1. Sticks and triggers go through
        /// the action's dead zone and sensitivity. The strongest of its bindings, keeping its sign.
        /// </summary>
        public float Value(InputState input, string action) => ValueOf(input, action, -1);

        /// <summary><see cref="Value(InputState, string)"/> on one controller (0 to 3).</summary>
        public float Value(InputState input, string action, int pad) => (uint)pad < Controllers ? ValueOf(input, action, pad) : 0f;

        private float ValueOf(InputState input, string action, int pad)
        {
            if (!Enabled || input == null) return 0f;
            List<InputBinding> bindings = pad < 0 ? (string.IsNullOrWhiteSpace(action) ? null : _actions.GetValueOrDefault(action)) : ListFor(action, pad);
            if (bindings == null) return 0f;
            (float deadZone, float sensitivity) = Shaping(action);
            float strongest = 0f;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding binding = bindings[i];
                if (pad >= 0 && !binding.IsController) continue;
                float value = binding.Kind switch
                {
                    InputBindingKind.Stick => Shape(input.DirectionValue(PadOf(binding, pad), (GamepadDirection)(binding.Code & 0xFF)), deadZone, sensitivity),
                    InputBindingKind.Axis => Shape(AxisValue(input, PadOf(binding, pad), (GamepadAxis)(binding.Code & 0xFF), previous: false), deadZone, sensitivity),
                    _ => Hit(input, binding, 0, pad) ? 1f : 0f,
                };
                if (MathF.Abs(value) > MathF.Abs(strongest)) strongest = value;
            }
            return strongest;
        }

        /// <summary>From -1 to 1 between two actions: Axis("Left", "Right"); smooth for a stick, -1, 0 or 1 for keys.</summary>
        public float Axis(InputState input, string negative, string positive) => Value(input, positive) - Value(input, negative);

        /// <summary>A whole stick axis (-1 to 1, right and up positive) or a trigger (0 to 1), now or in the frame before.</summary>
        public static float AxisValue(InputState input, int pad, GamepadAxis axis, bool previous)
        {
            float Read(GamepadDirection direction) => previous ? input.PreviousDirectionValue(pad, direction) : input.DirectionValue(pad, direction);
            return axis switch
            {
                GamepadAxis.LeftStickX => Read(GamepadDirection.LeftStickRight) - Read(GamepadDirection.LeftStickLeft),
                GamepadAxis.LeftStickY => Read(GamepadDirection.LeftStickUp) - Read(GamepadDirection.LeftStickDown),
                GamepadAxis.RightStickX => Read(GamepadDirection.RightStickRight) - Read(GamepadDirection.RightStickLeft),
                GamepadAxis.RightStickY => Read(GamepadDirection.RightStickUp) - Read(GamepadDirection.RightStickDown),
                GamepadAxis.LeftTrigger => Read(GamepadDirection.LeftTrigger),
                GamepadAxis.RightTrigger => Read(GamepadDirection.RightTrigger),
                _ => 0f,
            };
        }

        private bool Any(InputState input, string action, int query, int pad)
        {
            if (!Enabled || input == null) return false;
            List<InputBinding> bindings = pad < 0 ? (string.IsNullOrWhiteSpace(action) ? null : _actions.GetValueOrDefault(action)) : ListFor(action, pad);
            if (bindings == null) return false;
            for (int i = 0; i < bindings.Count; i++)
            {
                if (pad >= 0 && !bindings[i].IsController) continue;
                if (Hit(input, bindings[i], query, pad)) return true;
            }
            return false;
        }

        // A controller binding read on a given controller (a controller's own set, or the shared set
        // asked about one controller), or on its own controller.
        private static int PadOf(InputBinding binding, int pad) => pad >= 0 ? pad : binding.Pad;

        private static bool Hit(InputState input, InputBinding binding, int query, int pad = -1)
        {
            switch (binding.Kind)
            {
                case InputBindingKind.Stick:
                {
                    var direction = (GamepadDirection)(binding.Code & 0xFF);
                    float now = input.DirectionValue(PadOf(binding, pad), direction);
                    float before = input.PreviousDirectionValue(PadOf(binding, pad), direction);
                    return Edge(now >= InputBinding.StickThreshold, before >= InputBinding.StickThreshold, query);
                }
                case InputBindingKind.Axis:
                {
                    var axis = (GamepadAxis)(binding.Code & 0xFF);
                    float now = MathF.Abs(AxisValue(input, PadOf(binding, pad), axis, previous: false));
                    float before = MathF.Abs(AxisValue(input, PadOf(binding, pad), axis, previous: true));
                    return Edge(now >= InputBinding.StickThreshold, before >= InputBinding.StickThreshold, query);
                }
                case InputBindingKind.Chord:
                {
                    // Held while every part is; pressed when the last part completes it, once.
                    bool now = true, before = true;
                    foreach (InputBinding part in InputChords.Parts(binding.Code))
                    {
                        now &= Hit(input, part, 0, pad);
                        before &= HeldBefore(input, part, pad);
                    }
                    return Edge(now, before, query);
                }
            }
            return binding.Kind switch
            {
                InputBindingKind.Mouse => query == 0 ? input.IsDown((MouseButton)binding.Code)
                    : query == 1 ? input.WasPressed((MouseButton)binding.Code) : input.WasReleased((MouseButton)binding.Code),
                InputBindingKind.Gamepad => query == 0 ? input.IsDown(PadOf(binding, pad), (GamepadButton)(binding.Code & 0xFF))
                    : query == 1 ? input.WasPressed(PadOf(binding, pad), (GamepadButton)(binding.Code & 0xFF)) : input.WasReleased(PadOf(binding, pad), (GamepadButton)(binding.Code & 0xFF)),
                _ => ModifiersHeld(input, binding.Modifiers) && (query == 0 ? input.IsDown((Key)(binding.Code & 0xFFFF))
                    : query == 1 ? input.WasPressed((Key)(binding.Code & 0xFFFF)) : input.WasReleased((Key)(binding.Code & 0xFFFF))),
            };
        }

        private static bool Edge(bool now, bool before, int query) => query == 0 ? now : query == 1 ? now && !before : before && !now;

        // Whether a chord's part was held in the frame before: down now and not pressed this frame, or let go this frame.
        private static bool HeldBefore(InputState input, InputBinding part, int pad)
        {
            switch (part.Kind)
            {
                case InputBindingKind.Stick:
                    return input.PreviousDirectionValue(PadOf(part, pad), (GamepadDirection)(part.Code & 0xFF)) >= InputBinding.StickThreshold;
                case InputBindingKind.Axis:
                    return MathF.Abs(AxisValue(input, PadOf(part, pad), (GamepadAxis)(part.Code & 0xFF), previous: true)) >= InputBinding.StickThreshold;
                case InputBindingKind.Mouse:
                {
                    var button = (MouseButton)part.Code;
                    return input.IsDown(button) && !input.WasPressed(button) || input.WasReleased(button);
                }
                case InputBindingKind.Gamepad:
                {
                    var button = (GamepadButton)(part.Code & 0xFF);
                    int at = PadOf(part, pad);
                    return input.IsDown(at, button) && !input.WasPressed(at, button) || input.WasReleased(at, button);
                }
                default:
                {
                    var key = (Key)(part.Code & 0xFFFF);
                    return ModifiersHeld(input, part.Modifiers) && (input.IsDown(key) && !input.WasPressed(key) || input.WasReleased(key));
                }
            }
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
        public void Listen(string action, bool replace = true) => Listen(action, replace, -1);

        /// <summary>
        /// As <see cref="Listen(string, bool)"/>, for one controller's own bindings (0 to 3): only that
        /// controller's buttons and sticks bind it; -1 listens for the shared bindings.
        /// </summary>
        public void Listen(string action, bool replace, int pad)
        {
            _listening = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
            _listenReplaces = replace;
            _listenPad = (uint)pad < Controllers ? pad : -1;
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
            InputBinding? controller = ControllerPress(input, _listenPad);
            if (_listenPad >= 0)
            {
                if (controller == null) return false;
                EnsureDefaults();
                Dictionary<string, List<InputBinding>> own = _padActions[_listenPad] ??= new Dictionary<string, List<InputBinding>>(StringComparer.OrdinalIgnoreCase);
                if (_listenReplaces || !own.ContainsKey(_listening)) own[_listening] = _listenReplaces ? [] : ControllerBindings(_listening, _listenPad);
                Add(own, _listening, controller.Value.OnFirstPad());
                _listening = null;
                return true;
            }
            // A controller that also presses keys (Engine.Input.Gamepad.KeyboardEmulation) presses its
            // button and a key in the same frame: the button is what the player pressed.
            if (controller != null && input.GamepadEmulatesKeyboard) pressed = controller;
            if (pressed == null)
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
            pressed ??= controller;
            if (pressed == null) return false;
            _heldModifier = Key.Unknown;
            EnsureDefaults();
            if (_listenReplaces) Clear(_listening);
            Add(_actions, _listening, pressed.Value);
            _listening = null;
            return true;
        }

        // This frame's first controller button or stick push, on one controller or on any.
        private static InputBinding? ControllerPress(InputState input, int only)
        {
            for (int pad = only >= 0 ? only : 0; pad < (only >= 0 ? only + 1 : InputState.MaxGamepads); pad++)
            {
                foreach (GamepadButton button in Enum.GetValues<GamepadButton>())
                    if (input.WasPressed(pad, button)) return InputBinding.ForGamepad(button, pad);
                foreach (GamepadDirection direction in Enum.GetValues<GamepadDirection>())
                {
                    InputBinding stick = InputBinding.ForDirection(direction, pad);
                    if (Hit(input, stick, 1)) return stick;
                }
            }
            return null;
        }

        /// <summary>
        /// One line per action: <c>Jump=Key:Space;Pad:A</c>; then, for each controller with bindings
        /// of its own, a <c>[Pad 2]</c> line and its actions' lines.
        /// </summary>
        public string Save()
        {
            var text = new StringBuilder();
            AppendActions(text, _actions);
            for (int pad = 0; pad < Controllers; pad++)
            {
                if (_padActions[pad] is not { Count: > 0 } own) continue;
                text.Append("[Pad ").Append(pad + 1).Append("]\n");
                AppendActions(text, own);
            }
            return text.ToString();
        }

        private static void AppendActions(StringBuilder text, Dictionary<string, List<InputBinding>> actions)
        {
            foreach (KeyValuePair<string, List<InputBinding>> action in actions)
            {
                text.Append(action.Key).Append('=');
                for (int i = 0; i < action.Value.Count; i++)
                {
                    if (i > 0) text.Append(';');
                    text.Append(action.Value[i].SavedName);
                }
                text.Append('\n');
            }
        }

        /// <summary>
        /// Reads what <see cref="Save"/> wrote: each action named replaces its bindings; actions it
        /// does not name keep theirs (a game's new actions keep their defaults). Lines after
        /// <c>[Pad 2]</c> are that controller's own. Returns how many actions it read.
        /// </summary>
        public int Load(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            EnsureDefaults();
            int read = 0;
            Dictionary<string, List<InputBinding>> section = _actions;
            int sectionPad = -1;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string inside = line[1..^1].Trim();
                    sectionPad = inside.StartsWith("Pad", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(inside[3..].Trim(), out int number) && number is >= 1 and <= Controllers ? number - 1 : -2;
                    section = sectionPad >= 0 ? _padActions[sectionPad] ??= new Dictionary<string, List<InputBinding>>(StringComparer.OrdinalIgnoreCase) : null;
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0 || section == null) continue;
                string action = line[..equals].Trim();
                if (action.Length == 0) continue;
                if (section.TryGetValue(action, out List<InputBinding> existing)) existing.Clear();
                else section[action] = new List<InputBinding>(2);
                foreach (string part in line[(equals + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (InputBinding.TryParseSaved(part, out InputBinding binding) && (sectionPad < 0 || binding.IsController))
                        Add(section, action, sectionPad < 0 ? binding : binding.OnFirstPad());
                read++;
            }
            return read;
        }
    }
}

#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Genesis.Runtime.Input;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// The ready-made rebinding screen (Documentation/Design/UnityParityPlan.md, Input): one call shows
// every action with what it is bound to; the player chooses one and presses its new key or button,
// puts the game's bindings back, saves them, and goes back to the game. It is a User Interface built
// here and drawn by the UI toolkit like any other (focus, keyboard and controller navigation, themes
// by UiSetTheme's named styles), over the game's own GUI, from the GUI pass.
public static partial class PgslCommands
{
    /// <summary>The User Interface name the screen is drawn under; its named styles are Backdrop, Window, Title, Text, List, Button.</summary>
    public const string RebindingScreenUi = "Engine.Input.Rebinding";

    // User Interfaces the engine makes itself, found by name before the project's resources.
    private static readonly Dictionary<string, UiAssetDocument> BuiltInUi = new(StringComparer.OrdinalIgnoreCase);

    private static PgslContext _rebinding;
    private static string[] _rebindingActions = [];
    private static int _rebindingPad = -1;
    private static bool _rebindingPaused, _rebindingActionsWereOn, _rebindingFresh, _rebindingEnabled = true;
    private static string _rebindingMessage = string.Empty;
    private const string RebindingFile = "input-bindings.txt";

    /// <summary>The open screen's own UI state (focus, clicks), as an instance's would be; null while closed. For hosts and tests.</summary>
    public static PgslContext RebindingScreenContext => _rebinding;

    [PgslCommand("InputRebindingOpen", "InputRebindingOpen(pause?, theme?) -> bool",
        "Show the ready-made rebinding screen over the game: every action and its bindings; choose one and press its new key or button, Reset puts the game's bindings back, Save keeps them (input-bindings.txt, as InputActionsSave), Back or Escape returns. Keyboard, mouse and controller all work it. Pauses the game while open unless pause is false; theme names a User Interface whose styles it takes. False when Engine.Input.Rebinding.Enabled is off", "Input")]
    public static bool InputRebindingOpen(bool pause = true, string theme = "")
    {
        if (!_rebindingEnabled || ActiveGameContext?.Input?.Actions is not { } actions) return false;
        if (_rebinding != null) return true;
        _rebinding = new PgslContext { InstanceId = -1 };
        _rebindingPad = -1;
        _rebindingMessage = string.Empty;
        BuildRebindingUi(actions);
        // The game's actions read nothing while the screen has the keys and buttons.
        _rebindingActionsWereOn = actions.Enabled;
        actions.Enabled = false;
        _rebindingPaused = pause && GameGetSpeed() != 0;
        if (_rebindingPaused) GameApiPaused = true;
        if (!string.IsNullOrWhiteSpace(theme)) _rebinding.Variables[UiOverrideKey(RebindingScreenUi, string.Empty, "theme")] = theme.Trim();
        _rebinding.Variables[UiPointerKey(RebindingScreenUi, "focus")] = _rebindingActions.Length > 0 ? "Bind0" : "Back";
        // The press that opened the screen is not also a press on it.
        _rebindingFresh = true;
        return true;
    }

    [PgslCommand("InputRebindingClose", "InputRebindingClose()", "Close the rebinding screen and go back to the game, as its Back button does (unsaved changes stay for this run)", "Input")]
    public static void InputRebindingClose()
    {
        if (_rebinding == null) return;
        if (ActiveGameContext?.Input?.Actions is { } actions)
        {
            actions.StopListening();
            actions.Enabled = _rebindingActionsWereOn;
        }
        if (_rebindingPaused) GameApiPaused = false;
        _rebinding = null;
        _rebindingPaused = false;
    }

    [PgslCommand("InputRebindingIsOpen", "InputRebindingIsOpen() -> bool", "True while the rebinding screen is open", "Input")]
    public static bool InputRebindingIsOpen() => _rebinding != null;

    [PgslCommand("Open", "Engine.Input.Rebinding.Open(pause?, theme?) -> bool", "Show the ready-made rebinding screen (InputRebindingOpen)", "Input", Namespace = "Engine.Input.Rebinding", Flat = false)]
    public static bool InputRebindingApiOpen(bool pause = true, string theme = "") => InputRebindingOpen(pause, theme);

    [PgslCommand("Close", "Engine.Input.Rebinding.Close()", "Close the rebinding screen", "Input", Namespace = "Engine.Input.Rebinding", Flat = false)]
    public static void InputRebindingApiClose() => InputRebindingClose();

    [PgslCommand("Showing", "Engine.Input.Rebinding.Showing", "True while the rebinding screen is open (read)", "Input", Namespace = "Engine.Input.Rebinding", Flat = false)]
    public static bool InputRebindingShowing => _rebinding != null;

    [PgslCommand("Enabled", "Engine.Input.Rebinding.Enabled = false", "Whether the game may open the ready-made rebinding screen; off closes it (true unless set)", "Input", Namespace = "Engine.Input.Rebinding", Flat = false)]
    public static bool InputRebindingEnabled
    {
        get => _rebindingEnabled;
        set { _rebindingEnabled = value; if (!value) InputRebindingClose(); }
    }

    /// <summary>
    /// Draws the open screen and acts on what the player did on it. The GUI pass calls it after the
    /// game's own DrawGui events, so it is on top; a host or test may call it with any surface.
    /// </summary>
    public static void DrawRebindingScreen(IPgslDrawSurface surface)
    {
        if (_rebinding == null || surface == null || ActiveGameContext?.Input is not { } input || input.Actions is not { } actions) return;
        PgslContext previous = BindContext(_rebinding);
        try
        {
            _rebinding.DrawSurface = surface;
            if (!_rebindingActions.SequenceEqual(actions.Names, StringComparer.OrdinalIgnoreCase)) BuildRebindingUi(actions);
            // While a press is awaited, that press rebinds: it does not also move, click or close.
            bool quiet = _rebindingFresh || actions.Listening != null;
            _rebindingFresh = false;
            if (actions.Listening != null && actions.TakeListenedPress(input)) _rebindingMessage = string.Empty;
            RefreshRebindingTexts(actions);
            string focus = UiFocus(_rebinding, RebindingScreenUi);
            DrawUi(RebindingScreenUi);
            if (quiet)
            {
                _rebinding.Variables[UiPointerKey(RebindingScreenUi, "focus")] = focus;
                foreach (string id in RebindingButtons()) TakeUiFlag(RebindingScreenUi, id, "clicked");
                return;
            }
            ActOnRebindingScreen(input, actions);
        }
        finally { BindContext(previous); }
    }

    private static IEnumerable<string> RebindingButtons()
    {
        for (int i = 0; i < _rebindingActions.Length; i++) yield return "Bind" + i;
        yield return "Owner"; yield return "Reset"; yield return "Save"; yield return "Back";
    }

    private static void ActOnRebindingScreen(InputState input, InputActions actions)
    {
        for (int i = 0; i < _rebindingActions.Length; i++)
            if (TakeUiFlag(RebindingScreenUi, "Bind" + i, "clicked"))
            {
                actions.Listen(_rebindingActions[i], replace: true, _rebindingPad);
                _rebindingMessage = _rebindingPad < 0 ? "Press the new key or button for " + _rebindingActions[i] + "."
                    : "Press the new button on controller " + (_rebindingPad + 1) + " for " + _rebindingActions[i] + ".";
            }
        if (TakeUiFlag(RebindingScreenUi, "Owner", "clicked"))
        {
            _rebindingPad = _rebindingPad + 1 >= InputState.MaxGamepads ? -1 : _rebindingPad + 1;
            _rebindingMessage = _rebindingPad < 0 ? "Bindings everyone uses." : "Controller " + (_rebindingPad + 1) + "'s own bindings; it uses everyone's until it has its own.";
        }
        if (TakeUiFlag(RebindingScreenUi, "Reset", "clicked"))
        {
            if (_rebindingPad < 0) actions.ResetToDefaults(); else actions.ResetPad(_rebindingPad);
            _rebindingMessage = _rebindingPad < 0 ? "The game's bindings are back." : "Controller " + (_rebindingPad + 1) + " uses everyone's bindings again.";
        }
        if (TakeUiFlag(RebindingScreenUi, "Save", "clicked"))
            _rebindingMessage = InputActionsSave(RebindingFile) ? "Saved." : "Not saved: " + FileLastError();
        bool back = TakeUiFlag(RebindingScreenUi, "Back", "clicked") || input.WasPressed(Key.Escape) || input.BackPressed;
        for (int pad = 0; pad < InputState.MaxGamepads && !back; pad++) back = input.WasPressed(pad, GamepadButton.B);
        if (back) InputRebindingClose();
        else RefreshRebindingTexts(actions);
    }

    private static void RefreshRebindingTexts(InputActions actions)
    {
        for (int i = 0; i < _rebindingActions.Length; i++)
        {
            string action = _rebindingActions[i];
            SetUiOverride(RebindingScreenUi, "Bind" + i, "text", string.Equals(actions.Listening, action, StringComparison.OrdinalIgnoreCase)
                ? "Press a key or button... (Esc gives up)" : RebindingBindingsText(actions, action));
        }
        SetUiOverride(RebindingScreenUi, "Owner", "text", _rebindingPad < 0 ? "For: everyone" : "For: controller " + (_rebindingPad + 1));
        SetUiOverride(RebindingScreenUi, "Status", "text", _rebindingMessage);
    }

    private static string RebindingBindingsText(InputActions actions, string action)
    {
        var names = new List<string>();
        if (_rebindingPad < 0)
            for (int i = 0; i < actions.BindingCount(action); i++) names.Add(actions.BindingAt(action, i)?.DisplayName);
        else
            for (int i = 0; i < actions.BindingCount(action, _rebindingPad); i++) names.Add(actions.BindingAt(action, i, _rebindingPad)?.DisplayName);
        string text = names.Count == 0 ? "(none)" : string.Join(", ", names);
        return _rebindingPad >= 0 && !actions.HasOwnBindings(_rebindingPad, action) ? text + " (everyone's)" : text;
    }

    /// <summary>The screen as a User Interface: a window over the game, a scrolling row per action, and its buttons.</summary>
    private static void BuildRebindingUi(InputActions actions)
    {
        _rebindingActions = actions.Names.ToArray();
        var document = new UiAssetDocument { DesignWidth = 1280, DesignHeight = 720, Elements = [] };
        int order = 0;
        UiElement Add(UiElement element) { element.Order = order++; document.Elements.Add(element); return element; }
        const string Clear = "#00000000";
        Add(new UiElement { Id = "Backdrop", Type = UiElementType.Panel, Anchor = UiAnchor.Stretch, X = 0, Y = 0, Width = 0, Height = 0,
            Background = "#B4000000", BorderWidth = 0, Style = "Backdrop" });
        Add(new UiElement { Id = "Window", ParentId = "Backdrop", Type = UiElementType.Panel, Anchor = UiAnchor.Center, X = 0, Y = 0, Width = 760, Height = 620,
            Background = "#F2161B22", CornerRadius = 10, Style = "Window" });
        Add(new UiElement { Id = "Title", ParentId = "Window", Type = UiElementType.Text, X = 28, Y = 16, Width = 420, Height = 42, Text = "Controls",
            FontSize = 28, Background = Clear, TextAlign = UiHorizontalAlign.Left, Style = "Title" });
        Add(new UiElement { Id = "Owner", ParentId = "Window", Type = UiElementType.Button, Anchor = UiAnchor.TopRight, X = 28, Y = 20, Width = 240, Height = 36,
            Text = "For: everyone", FontSize = 16, CornerRadius = 6, Style = "Button" });
        Add(new UiElement { Id = "Hint", ParentId = "Window", Type = UiElementType.Text, X = 28, Y = 62, Width = 704, Height = 26, FontSize = 15,
            Text = "Choose an action, then press its new key or button. Esc gives up; Back returns to the game.",
            Background = Clear, Foreground = "#FFB8C2D6", TextAlign = UiHorizontalAlign.Left, Style = "Text" });
        Add(new UiElement { Id = "List", ParentId = "Window", Type = UiElementType.ScrollList, X = 28, Y = 96, Width = 704, Height = 410,
            Layout = UiLayoutMode.Vertical, Spacing = 6, Padding = 6, Background = "#40000000", CornerRadius = 6, Style = "List" });
        if (_rebindingActions.Length == 0)
            Add(new UiElement { Id = "Empty", ParentId = "List", Type = UiElementType.Text, Anchor = UiAnchor.Stretch, Height = 44,
                Text = "This game has no actions to rebind.", Background = Clear, Style = "Text" });
        for (int i = 0; i < _rebindingActions.Length; i++)
        {
            Add(new UiElement { Id = "Row" + i, ParentId = "List", Type = UiElementType.Panel, Anchor = UiAnchor.Stretch, Width = 0, Height = 44,
                Layout = UiLayoutMode.Horizontal, Spacing = 8, Background = Clear, BorderWidth = 0 });
            Add(new UiElement { Id = "Name" + i, ParentId = "Row" + i, Type = UiElementType.Text, Width = 250, Height = 44, Text = _rebindingActions[i],
                FontSize = 18, Background = Clear, TextAlign = UiHorizontalAlign.Left, Style = "Text" });
            Add(new UiElement { Id = "Bind" + i, ParentId = "Row" + i, Type = UiElementType.Button, Width = 434, Height = 44, Text = string.Empty,
                FontSize = 16, CornerRadius = 6, Style = "Button" });
        }
        Add(new UiElement { Id = "Status", ParentId = "Window", Type = UiElementType.Text, Anchor = UiAnchor.BottomLeft, X = 28, Y = 76, Width = 704, Height = 26,
            FontSize = 15, Background = Clear, Foreground = "#FFB8C2D6", TextAlign = UiHorizontalAlign.Left, Style = "Text" });
        Add(new UiElement { Id = "Reset", ParentId = "Window", Type = UiElementType.Button, Anchor = UiAnchor.BottomLeft, X = 28, Y = 24, Width = 220, Height = 44,
            Text = "Reset to defaults", FontSize = 16, CornerRadius = 6, Style = "Button" });
        Add(new UiElement { Id = "Save", ParentId = "Window", Type = UiElementType.Button, Anchor = UiAnchor.Bottom, X = 0, Y = 24, Width = 200, Height = 44,
            Text = "Save", FontSize = 16, CornerRadius = 6, Style = "Button" });
        Add(new UiElement { Id = "Back", ParentId = "Window", Type = UiElementType.Button, Anchor = UiAnchor.BottomRight, X = 28, Y = 24, Width = 200, Height = 44,
            Text = "Back", FontSize = 16, CornerRadius = 6, Style = "Button" });
        document.Validate();
        lock (UiCacheGate) BuiltInUi[RebindingScreenUi] = document;
    }
}

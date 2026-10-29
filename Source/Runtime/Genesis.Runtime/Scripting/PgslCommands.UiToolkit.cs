using System;
using System.Collections.Generic;
using System.Drawing;
using Genesis.Runtime.Input;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// The parts of a User Interface that make it a toolkit and not only a picture: panels and scroll
// lists that lay their children out one after another, lists that scroll (wheel, controller,
// UiScrollTo) and clip what is outside them, text boxes the player types into, a focus that the
// arrow keys, a controller's d-pad and left stick move between the interactive elements (Enter,
// Space or A presses; left and right move a slider), and named styles a theme swaps at run time.
public static partial class PgslCommands
{
    // Design pixels a notch of the mouse wheel scrolls a list by.
    private const float UiWheelStep = 48f;

    #region PGSL commands

    [PgslCommand("UiGetText", "UiGetText(uiAsset, elementId) -> string",
        "The text of an element on this instance: what the player typed into a Text Input, or what UiSetText or the resource gave it", "User Interface")]
    public static string UiGetText(string uiAsset, string elementId)
    {
        PgslContext context = GetContext();
        if (context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return string.Empty;
        return GetUiLayout(document).Elements.TryGetValue(elementId ?? string.Empty, out UiElement element)
            ? TextOverride(context, uiAsset, element) : string.Empty;
    }

    [PgslCommand("UiSubmitted", "UiSubmitted(uiAsset, elementId) -> bool",
        "True once after the player presses Enter in a Text Input; reading it clears it", "User Interface")]
    public static bool UiSubmitted(string uiAsset, string elementId) => TakeUiFlag(uiAsset, elementId, "submitted");

    [PgslCommand("UiGetFocused", "UiGetFocused(uiAsset) -> string",
        "The element the keyboard and controller act on (it has its Selected look), or an empty string", "User Interface")]
    public static string UiGetFocused(string uiAsset)
    {
        PgslContext context = GetContext();
        return context is null ? string.Empty : UiFocus(context, uiAsset);
    }

    [PgslCommand("UiSetFocus", "UiSetFocus(uiAsset, elementId)",
        "Give an element the focus (an empty id takes it away): a menu opens with its first button ready for the controller, a name box ready for typing", "User Interface")]
    public static void UiSetFocus(string uiAsset, string elementId)
    {
        PgslContext context = GetContext();
        if (context is null || string.IsNullOrWhiteSpace(uiAsset)) return;
        context.Variables[UiPointerKey(uiAsset, "focus")] = elementId?.Trim() ?? string.Empty;
        if (TryLoadUi(uiAsset, out UiAssetDocument document)) ScrollIntoView(context, uiAsset, GetUiLayout(document), elementId);
    }

    [PgslCommand("UiScrollTo", "UiScrollTo(uiAsset, listId, offset)",
        "Scroll a Scroll List to an offset in design pixels from its start (kept within its content)", "User Interface")]
    public static void UiScrollTo(string uiAsset, string listId, double offset)
    {
        PgslContext context = GetContext();
        if (context is null || !TryLoadUi(uiAsset, out UiAssetDocument document) || !double.IsFinite(offset)) return;
        SetUiScroll(context, uiAsset, GetUiLayout(document), listId, (float)offset);
    }

    [PgslCommand("UiGetScroll", "UiGetScroll(uiAsset, listId) -> number", "How far a Scroll List is scrolled, in design pixels", "User Interface")]
    public static double UiGetScroll(string uiAsset, string listId)
    {
        PgslContext context = GetContext();
        return context is null ? 0 : UiScroll(context, uiAsset, listId ?? string.Empty);
    }

    [PgslCommand("UiSetTheme", "UiSetTheme(uiAsset, themeUiAsset)",
        "Draw this UI on this instance with the named styles of another User Interface resource (a light or dark theme, a team colour); an empty name goes back to its own", "User Interface")]
    public static void UiSetTheme(string uiAsset, string themeUiAsset)
    {
        PgslContext context = GetContext();
        if (context is null || string.IsNullOrWhiteSpace(uiAsset)) return;
        // The whole UI's, not an element's: SetUiOverride takes element ids only.
        context.Variables[UiOverrideKey(uiAsset, string.Empty, "theme")] = themeUiAsset?.Trim() ?? string.Empty;
    }

    #endregion

    #region Layout, scrolling and clipping

    /// <summary>
    /// Where each child of a laid-out Panel or Scroll List goes (its offset along the layout), and
    /// how long each laid-out container's content is.
    /// </summary>
    private static (Dictionary<string, float> Slots, Dictionary<string, float> Extents) UiLayoutSlots(IReadOnlyList<UiElement> ordered,
        IReadOnlyDictionary<string, UiElement> elements) => UiLayoutPlacement.Slots(ordered, elements);

    /// <summary>A laid-out child's box: at its slot along the layout, inset by the padding across it (shared with the UI Editor's canvas).</summary>
    private static RectangleF LayoutUiRect(RectangleF parent, UiElement container, UiElement element, float slot) =>
        UiLayoutPlacement.Place(parent, container, element, slot);

    /// <summary>The Scroll Lists an element is inside, nearest first.</summary>
    private static string[] UiScrollAncestors(UiElement element, IReadOnlyDictionary<string, UiElement> elements)
    {
        List<string> lists = null;
        UiElement current = element;
        for (int depth = 0; depth <= elements.Count && !string.IsNullOrWhiteSpace(current.ParentId)
            && elements.TryGetValue(current.ParentId, out UiElement parent); depth++)
        {
            if (parent.Type == UiElementType.ScrollList) (lists ??= []).Add(parent.Id);
            current = parent;
        }
        return lists?.ToArray() ?? [];
    }

    private static float UiScroll(PgslContext context, string asset, string listId) =>
        context.Variables.TryGetValue(UiOverrideKey(asset, listId, "scroll"), out object value) ? Convert.ToSingle(value) : 0f;

    private static void SetUiScroll(PgslContext context, string asset, UiLayoutCache layout, string listId, float offset)
    {
        if (string.IsNullOrWhiteSpace(listId) || !layout.Elements.TryGetValue(listId, out UiElement list)
            || list.Type != UiElementType.ScrollList || !layout.Rectangles.TryGetValue(list.Id, out RectangleF box)) return;
        float visible = list.Layout == UiLayoutMode.Horizontal ? box.Width : box.Height;
        float content = layout.ContentExtents.TryGetValue(list.Id, out float extent) ? extent : visible;
        context.Variables[UiOverrideKey(asset, list.Id, "scroll")] = (double)Math.Clamp(offset, 0f, MathF.Max(0f, content - visible));
    }

    /// <summary>An element's design box where it is now: moved by the scroll of every list it is in.</summary>
    private static RectangleF UiScrolledRect(PgslContext context, string asset, UiLayoutCache layout, UiElement element, RectangleF rect)
    {
        if (!layout.ScrollAncestors.TryGetValue(element.Id, out string[] lists)) return rect;
        foreach (string listId in lists)
        {
            float scroll = UiScroll(context, asset, listId);
            if (scroll == 0 || !layout.Elements.TryGetValue(listId, out UiElement list)) continue;
            if (list.Layout == UiLayoutMode.Horizontal) rect.X -= scroll;
            else rect.Y -= scroll;
        }
        return rect;
    }

    /// <summary>The design box an element is seen through (the lists it is in, overlapped); null when it is in none.</summary>
    private static RectangleF? UiClipRect(PgslContext context, string asset, UiLayoutCache layout, UiElement element)
    {
        if (!layout.ScrollAncestors.TryGetValue(element.Id, out string[] lists) || lists.Length == 0) return null;
        RectangleF? clip = null;
        foreach (string listId in lists)
        {
            if (!layout.Elements.TryGetValue(listId, out UiElement list) || !layout.Rectangles.TryGetValue(listId, out RectangleF box)) continue;
            RectangleF seen = UiScrolledRect(context, asset, layout, list, box);
            clip = clip is RectangleF inner ? RectangleF.Intersect(inner, seen) : seen;
        }
        return clip;
    }

    /// <summary>Scrolls the lists an element is in so it can be seen whole.</summary>
    private static void ScrollIntoView(PgslContext context, string asset, UiLayoutCache layout, string elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId) || !layout.Elements.TryGetValue(elementId, out UiElement element)
            || !layout.ScrollAncestors.TryGetValue(element.Id, out string[] lists) || !layout.Rectangles.TryGetValue(element.Id, out RectangleF rect)) return;
        foreach (string listId in lists)
        {
            if (!layout.Elements.TryGetValue(listId, out UiElement list) || !layout.Rectangles.TryGetValue(listId, out RectangleF box)) continue;
            bool across = list.Layout == UiLayoutMode.Horizontal;
            float start = across ? rect.X - box.X : rect.Y - box.Y;
            float size = across ? rect.Width : rect.Height, visible = across ? box.Width : box.Height;
            float scroll = UiScroll(context, asset, listId);
            if (start < scroll) scroll = start;
            else if (start + size > scroll + visible) scroll = start + size - visible;
            SetUiScroll(context, asset, layout, listId, scroll);
        }
    }

    /// <summary>The wheel scrolls the topmost Scroll List under the pointer.</summary>
    private static void ScrollUiWithWheel(PgslContext context, string asset, UiLayoutCache layout, float x, float y, float scaleX, float scaleY, float wheel)
    {
        if (wheel == 0) return;
        foreach (UiElement element in layout.ReverseOrdered)
        {
            if (element.Type != UiElementType.ScrollList || !IsUiElementVisible(context, asset, element, layout.Elements)) continue;
            if (!layout.Rectangles.TryGetValue(element.Id, out RectangleF design)) continue;
            if (!ScaleUiRect(UiScrolledRect(context, asset, layout, element, design), scaleX, scaleY).Contains(x, y)) continue;
            SetUiScroll(context, asset, layout, element.Id, UiScroll(context, asset, element.Id) - (wheel * UiWheelStep));
            return;
        }
    }

    #endregion

    #region Focus, the controller and typing

    private static string UiFocus(PgslContext context, string asset) =>
        context.Variables.TryGetValue(UiPointerKey(asset, "focus"), out object focus) ? focus as string ?? string.Empty : string.Empty;

    // A direction pressed this frame on the keyboard or any controller (d-pad, or the left stick passing half way).
    private static bool UiDirectionPressed(InputState input, Key key, GamepadButton pad, GamepadDirection stick)
    {
        if (input.WasPressed(key)) return true;
        for (int p = 0; p < 4; p++)
        {
            if (input.WasPressed(p, pad)) return true;
            if (input.DirectionValue(p, stick) >= 0.5f && input.PreviousDirectionValue(p, stick) < 0.5f) return true;
        }
        return false;
    }

    private static bool UiConfirmPressed(InputState input)
    {
        if (input.WasPressed(Key.Enter) || input.WasPressed(Key.Space)) return true;
        for (int p = 0; p < 4; p++) if (input.WasPressed(p, GamepadButton.A)) return true;
        return false;
    }

    /// <summary>
    /// Moves the focus with the arrows, the d-pad and the left stick (to the nearest interactive element
    /// that way); presses the focused element with Enter, Space or A; moves a focused slider with left
    /// and right; types into a focused Text Input.
    /// </summary>
    private static void UpdateUiNavigation(PgslContext context, string asset, UiLayoutCache layout, InputState input, string clicked)
    {
        string focus = UiFocus(context, asset);
        // A click (or a press of the pointer) puts the focus where it is.
        if (clicked.Length > 0) focus = clicked;
        if (focus.Length > 0 && (!layout.Elements.TryGetValue(focus, out UiElement focused)
            || !IsUiElementVisible(context, asset, focused, layout.Elements) || !IsUiElementEnabled(context, asset, focused)))
        {
            focus = string.Empty;
            focused = null;
        }
        else focused = focus.Length > 0 ? layout.Elements[focus] : null;

        // Typing goes to a focused Text Input; its letters are not shortcuts meanwhile.
        bool typing = focused?.Type == UiElementType.TextInput;
        if (typing)
        {
            string text = TextOverride(context, asset, focused);
            string typed = input.TakeTypedText();
            bool changed = typed.Length > 0;
            if (input.WasPressed(Key.Backspace) && text.Length > 0) { text = text[..^1]; changed = true; }
            text += typed;
            if (focused.MaxLength > 0 && text.Length > focused.MaxLength) text = text[..focused.MaxLength];
            if (changed)
            {
                context.Variables[UiOverrideKey(asset, focused.Id, "text")] = text;
                context.Variables[UiOverrideKey(asset, focused.Id, "changed")] = true;
            }
            if (input.WasPressed(Key.Enter)) context.Variables[UiOverrideKey(asset, focused.Id, "submitted")] = true;
        }

        // With Engine.Ui.Focus.Navigation off the keys and controllers leave the focus to the pointer and scripts.
        if (!_uiFocusNavigation)
        {
            context.Variables[UiPointerKey(asset, "focus")] = focus;
            return;
        }

        bool up = UiDirectionPressed(input, Key.Up, GamepadButton.DPadUp, GamepadDirection.LeftStickUp);
        bool down = UiDirectionPressed(input, Key.Down, GamepadButton.DPadDown, GamepadDirection.LeftStickDown);
        bool left = UiDirectionPressed(input, Key.Left, GamepadButton.DPadLeft, GamepadDirection.LeftStickLeft);
        bool right = UiDirectionPressed(input, Key.Right, GamepadButton.DPadRight, GamepadDirection.LeftStickRight);

        // Left and right move a focused slider instead of the focus.
        if (focused?.Type == UiElementType.Slider && (left || right))
        {
            float step = focused.Step > 0 ? focused.Step : (focused.Maximum - focused.Minimum) * 0.05f;
            SetUiValueFromPlayer(context, asset, focused, SliderValueAt(focused,
                ((ValueOverride(context, asset, focused) + (right ? step : -step)) - focused.Minimum) / MathF.Max(0.0001f, focused.Maximum - focused.Minimum)));
            left = right = false;
        }

        if (up || down || left || right)
        {
            string next = focused == null ? FirstFocusable(context, asset, layout) : NearestFocusable(context, asset, layout, focused,
                up ? new PointF(0, -1) : down ? new PointF(0, 1) : left ? new PointF(-1, 0) : new PointF(1, 0));
            if (next.Length > 0)
            {
                focus = next;
                ScrollIntoView(context, asset, layout, focus);
            }
        }
        else if (focused != null && !typing && UiConfirmPressed(input))
        {
            context.Variables[UiOverrideKey(asset, focused.Id, "clicked")] = true;
            if (focused.Type == UiElementType.Toggle)
                SetUiValueFromPlayer(context, asset, focused, ValueOverride(context, asset, focused) >= 0.5f ? 0f : 1f);
        }
        context.Variables[UiPointerKey(asset, "focus")] = focus;
    }

    private static bool Focusable(PgslContext context, string asset, UiLayoutCache layout, UiElement element) =>
        element.IsInteractive && IsUiElementVisible(context, asset, element, layout.Elements) && IsUiElementEnabled(context, asset, element)
        && layout.Rectangles.ContainsKey(element.Id);

    private static string FirstFocusable(PgslContext context, string asset, UiLayoutCache layout)
    {
        foreach (UiElement element in layout.Ordered)
            if (Focusable(context, asset, layout, element)) return element.Id;
        return string.Empty;
    }

    /// <summary>
    /// The interactive element nearest the focused one in a direction (where they are laid out, not
    /// where a list has scrolled them): within 60 degrees of it, the closest, preferring one straight on.
    /// </summary>
    private static string NearestFocusable(PgslContext context, string asset, UiLayoutCache layout, UiElement from, PointF direction)
    {
        RectangleF origin = layout.Rectangles[from.Id];
        PointF centre = new(origin.X + (origin.Width * 0.5f), origin.Y + (origin.Height * 0.5f));
        string best = string.Empty;
        float bestScore = float.MaxValue;
        foreach (UiElement element in layout.Ordered)
        {
            if (element == from || !Focusable(context, asset, layout, element)) continue;
            RectangleF box = layout.Rectangles[element.Id];
            float dx = box.X + (box.Width * 0.5f) - centre.X, dy = box.Y + (box.Height * 0.5f) - centre.Y;
            float along = (dx * direction.X) + (dy * direction.Y);
            if (along <= 0.5f) continue;
            float across = MathF.Abs((dx * direction.Y) - (dy * direction.X));
            if (across > along * 1.75f) continue;
            float score = along + (across * 2f);
            if (score < bestScore) { bestScore = score; best = element.Id; }
        }
        return best;
    }

    #endregion

    #region Themes

    /// <summary>The named style an element draws with: from the theme UiSetTheme chose, else from its own UI; null when it names none.</summary>
    private static UiNamedStyle UiNamed(PgslContext context, string asset, UiAssetDocument document, UiElement element)
    {
        if (string.IsNullOrWhiteSpace(element.Style)) return null;
        if (context.Variables.TryGetValue(UiOverrideKey(asset, string.Empty, "theme"), out object themeName) && themeName is string theme
            && theme.Length > 0 && TryLoadUi(theme, out UiAssetDocument themed)
            && themed.Styles is { } themeStyles && TryStyle(themeStyles, element.Style, out UiNamedStyle fromTheme))
            return fromTheme;
        return document.Styles is { } own && TryStyle(own, element.Style, out UiNamedStyle style) ? style : null;
    }

    private static bool TryStyle(Dictionary<string, UiNamedStyle> styles, string name, out UiNamedStyle style)
    {
        if (styles.TryGetValue(name, out style)) return style != null;
        foreach (KeyValuePair<string, UiNamedStyle> pair in styles)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) { style = pair.Value; return style != null; }
        return false;
    }

    #endregion
}

using System;
using System.Drawing;
using Genesis.Runtime.Input;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// What a User Interface resource needs to be a game menu: rounded, bordered and gradient boxes,
// aligned and spaced text, hover/pressed/selected/disabled looks, sliders and toggles the player
// changes with the pointer, and images that contain, cover or crop. Every default draws exactly
// what DrawUi drew before, so older UI files look the same.
public static partial class PgslCommands
{
    /// <summary>The pointer as one UI resource on one instance last saw it.</summary>
    private readonly record struct UiPointerState(string Hovered, string Pressed);

    #region PGSL commands

    [PgslCommand("UiUpdate", "UiUpdate(uiAsset)",
        "Track the pointer over a User Interface: hover, press, clicks, slider drags and toggle flips. DrawUi does this too; call it in Step to read clicks before drawing", "User Interface")]
    public static void UiUpdate(string uiAsset)
    {
        PgslContext context = GetContext();
        if (context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return;
        SizeF target = UiTargetSize(context);
        UpdateUiPointer(context, uiAsset, GetUiLayout(document),
            target.Width / Math.Max(1, document.DesignWidth), target.Height / Math.Max(1, document.DesignHeight));
    }

    [PgslCommand("UiGetValue", "UiGetValue(uiAsset, elementId) -> number",
        "The value of a slider, toggle (0 or 1) or progress bar on this instance, as the player or UiSetValue left it", "User Interface")]
    public static double UiGetValue(string uiAsset, string elementId)
    {
        PgslContext context = GetContext();
        if (context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return 0;
        return GetUiLayout(document).Elements.TryGetValue(elementId ?? string.Empty, out UiElement element)
            ? ValueOverride(context, uiAsset, element) : 0;
    }

    [PgslCommand("UiValueChanged", "UiValueChanged(uiAsset, elementId) -> bool",
        "True once after the player moves a slider or flips a toggle; reading it clears it", "User Interface")]
    public static bool UiValueChanged(string uiAsset, string elementId) => TakeUiFlag(uiAsset, elementId, "changed");

    [PgslCommand("UiClicked", "UiClicked(uiAsset, elementId) -> bool",
        "True once after the player presses and releases the pointer on a button, slider or toggle; reading it clears it", "User Interface")]
    public static bool UiClicked(string uiAsset, string elementId) => TakeUiFlag(uiAsset, elementId, "clicked");

    [PgslCommand("UiGetHovered", "UiGetHovered(uiAsset) -> string",
        "The enabled button, slider or toggle under the pointer, or an empty string", "User Interface")]
    public static string UiGetHovered(string uiAsset)
    {
        PgslContext context = GetContext();
        return context is null ? string.Empty : ReadUiPointer(context, uiAsset).Hovered;
    }

    [PgslCommand("UiSetSelected", "UiSetSelected(uiAsset, elementId, selected)",
        "Give one element its Selected look on this instance, for menus moved with keys or a controller", "User Interface")]
    public static void UiSetSelected(string uiAsset, string elementId, bool selected) =>
        SetUiOverride(uiAsset, elementId, "selected", selected);

    [PgslCommand("UiSetEnabled", "UiSetEnabled(uiAsset, elementId, enabled)",
        "Enable or disable one element on this instance: a disabled one has its Disabled look and ignores the pointer", "User Interface")]
    public static void UiSetEnabled(string uiAsset, string elementId, bool enabled) =>
        SetUiOverride(uiAsset, elementId, "enabled", enabled);

    #endregion

    #region Pointer

    private static string UiPointerKey(string asset, string property) => UiOverrideKey(asset, string.Empty, property);

    private static UiPointerState ReadUiPointer(PgslContext context, string asset) => new(
        context.Variables.TryGetValue(UiPointerKey(asset, "hovered"), out object hovered) ? hovered as string ?? string.Empty : string.Empty,
        context.Variables.TryGetValue(UiPointerKey(asset, "pressed"), out object pressed) ? pressed as string ?? string.Empty : string.Empty);

    private static bool TakeUiFlag(string asset, string id, string flag)
    {
        PgslContext context = GetContext();
        if (context is null || string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(id)) return false;
        string key = UiOverrideKey(asset, id, flag);
        if (!context.Variables.TryGetValue(key, out object value) || value is not true) return false;
        context.Variables.Remove(key);
        return true;
    }

    private static bool UiFlag(PgslContext context, string asset, UiElement element, string property, bool fallback) =>
        context.Variables.TryGetValue(UiOverrideKey(asset, element.Id, property), out object value)
            ? Convert.ToBoolean(value) : fallback;

    private static bool IsUiElementEnabled(PgslContext context, string asset, UiElement element) =>
        UiFlag(context, asset, element, "enabled", element.Enabled);

    /// <summary>
    /// Moves one UI's pointer state on by what the input shows now. The button's previous state is
    /// kept per instance, so calling this from both UiUpdate and DrawUi in one frame sees a press
    /// or release once.
    /// </summary>
    private static void UpdateUiPointer(PgslContext context, string asset, UiLayoutCache layout, float scaleX, float scaleY)
    {
        InputState input = ActiveGameContext?.Input;
        if (input is null) return;
        float x = (float)UiMouseX(), y = (float)UiMouseY();
        bool down = input.IsDown(MouseButton.Left);

        string hovered = string.Empty;
        foreach (UiElement element in layout.ReverseOrdered)
        {
            if (!element.IsInteractive || !IsUiElementVisible(context, asset, element, layout.Elements)) continue;
            if (!layout.Rectangles.TryGetValue(element.Id, out RectangleF design)) continue;
            if (!ScaleUiRect(design, scaleX, scaleY).Contains(x, y)) continue;
            if (IsUiElementEnabled(context, asset, element)) hovered = element.Id;
            break;
        }

        UiPointerState previous = ReadUiPointer(context, asset);
        bool wasDown = context.Variables.TryGetValue(UiPointerKey(asset, "down"), out object last) && last is true;
        string pressed = previous.Pressed;
        if (down && !wasDown) pressed = hovered;

        if (pressed.Length > 0 && layout.Elements.TryGetValue(pressed, out UiElement active)
            && layout.Rectangles.TryGetValue(active.Id, out RectangleF activeDesign))
        {
            if (down && active.Type == UiElementType.Slider)
            {
                RectangleF rect = ScaleUiRect(activeDesign, scaleX, scaleY);
                float knob = rect.Height * 0.5f;
                float travel = MathF.Max(1f, rect.Width - (knob * 2));
                SetUiValueFromPlayer(context, asset, active, SliderValueAt(active, (x - rect.X - knob) / travel));
            }
            if (!down && wasDown)
            {
                if (pressed == hovered)
                {
                    context.Variables[UiOverrideKey(asset, active.Id, "clicked")] = true;
                    if (active.Type == UiElementType.Toggle)
                        SetUiValueFromPlayer(context, asset, active, ValueOverride(context, asset, active) >= 0.5f ? 0f : 1f);
                }
                pressed = string.Empty;
            }
        }
        else if (!down) pressed = string.Empty;

        context.Variables[UiPointerKey(asset, "down")] = down;
        context.Variables[UiPointerKey(asset, "hovered")] = hovered;
        context.Variables[UiPointerKey(asset, "pressed")] = pressed;
    }

    /// <summary>A slider's value at a fraction of its travel, snapped to its step.</summary>
    internal static float SliderValueAt(UiElement slider, float fraction)
    {
        float minimum = slider.Minimum, maximum = MathF.Max(slider.Minimum + 0.0001f, slider.Maximum);
        float value = minimum + (Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f) * (maximum - minimum));
        if (slider.Step > 0) value = minimum + (MathF.Round((value - minimum) / slider.Step) * slider.Step);
        return Math.Clamp(value, minimum, maximum);
    }

    private static void SetUiValueFromPlayer(PgslContext context, string asset, UiElement element, float value)
    {
        if (MathF.Abs(ValueOverride(context, asset, element) - value) < 0.00001f) return;
        context.Variables[UiOverrideKey(asset, element.Id, "value")] = (double)value;
        context.Variables[UiOverrideKey(asset, element.Id, "changed")] = true;
    }

    /// <summary>The look an element is in: disabled, pressed, hover, selected, or "" for normal.</summary>
    private static string UiElementState(PgslContext context, string asset, UiElement element, UiPointerState pointer)
    {
        if (!IsUiElementEnabled(context, asset, element)) return "disabled";
        if (element.IsInteractive && pointer.Pressed.Equals(element.Id, StringComparison.OrdinalIgnoreCase)
            && (element.Type == UiElementType.Slider || pointer.Hovered.Equals(element.Id, StringComparison.OrdinalIgnoreCase))) return "pressed";
        if (element.IsInteractive && pointer.Hovered.Equals(element.Id, StringComparison.OrdinalIgnoreCase)) return "hover";
        return UiFlag(context, asset, element, "selected", false) ? "selected" : string.Empty;
    }

    private static RectangleF ScaleUiRect(RectangleF design, float scaleX, float scaleY) =>
        new(design.X * scaleX, design.Y * scaleY, design.Width * scaleX, design.Height * scaleY);

    #endregion

    #region Drawing

    private static void DrawUiElement(IPgslDrawSurface surface, PgslContext context, string asset, UiElement element,
        RectangleF rect, float scale, string state)
    {
        (Color background, Color gradientEnd, Color foreground, Color accent, Color border) = UiLook.Resolve(element, state);
        float radius = MathF.Max(0, element.CornerRadius * scale);
        string text = TextOverride(context, asset, element);

        switch (element.Type)
        {
            case UiElementType.Panel:
                FillUiBox(surface, rect, radius, background, gradientEnd, element.Fill);
                DrawUiBorder(surface, element, rect, radius, scale, border, legacyOutline: false);
                break;
            case UiElementType.Text:
                DrawUiElementText(surface, element, text, rect, scale, foreground, centredByDefault: false);
                break;
            case UiElementType.Image:
                DrawUiImage(surface, element, rect, foreground.A / 255f);
                break;
            case UiElementType.Button:
                FillUiBox(surface, rect, radius, background, gradientEnd, element.Fill);
                DrawUiBorder(surface, element, rect, radius, scale, border, legacyOutline: true);
                DrawUiElementText(surface, element, text, rect, scale, foreground, centredByDefault: true);
                break;
            case UiElementType.ProgressBar:
            {
                FillUiBox(surface, rect, radius, background, gradientEnd, element.Fill);
                float maximum = MathF.Max(0.0001f, element.Maximum);
                float amount = Math.Clamp(ValueOverride(context, asset, element) / maximum, 0f, 1f);
                if (amount > 0)
                {
                    RectangleF fill = new(rect.X, rect.Y, rect.Width * amount, rect.Height);
                    FillUiBox(surface, fill, MathF.Min(radius, fill.Width * 0.5f), accent, accent, UiFill.Solid);
                }
                DrawUiBorder(surface, element, rect, radius, scale, border, legacyOutline: true);
                break;
            }
            case UiElementType.Slider:
            {
                float knob = rect.Height * 0.5f;
                float range = MathF.Max(0.0001f, element.Maximum - element.Minimum);
                float amount = Math.Clamp((ValueOverride(context, asset, element) - element.Minimum) / range, 0f, 1f);
                float centreX = rect.X + knob + (amount * MathF.Max(0, rect.Width - (knob * 2)));
                // The track is a third of the height, centred, so the knob stands out from it.
                float trackHeight = MathF.Max(2f, rect.Height / 3f);
                RectangleF track = new(rect.X, rect.Y + ((rect.Height - trackHeight) * 0.5f), rect.Width, trackHeight);
                float trackRadius = MathF.Min(radius, trackHeight * 0.5f);
                FillUiBox(surface, track, trackRadius, background, gradientEnd, element.Fill);
                RectangleF filled = new(track.X, track.Y, MathF.Max(0, centreX - track.X), track.Height);
                if (filled.Width > 0) FillUiBox(surface, filled, MathF.Min(trackRadius, filled.Width * 0.5f), accent, accent, UiFill.Solid);
                DrawUiBorder(surface, element, track, trackRadius, scale, border, legacyOutline: false);
                FillUiCircle(surface, foreground, centreX, rect.Y + knob, knob);
                break;
            }
            case UiElementType.Toggle:
            {
                bool on = ValueOverride(context, asset, element) >= 0.5f;
                float switchWidth = MathF.Min(rect.Width, rect.Height * 1.8f);
                RectangleF track = new(rect.X, rect.Y, switchWidth, rect.Height);
                float trackRadius = element.CornerRadius > 0 ? MathF.Min(radius, rect.Height * 0.5f) : rect.Height * 0.5f;
                if (on) FillUiBox(surface, track, trackRadius, accent, accent, UiFill.Solid);
                else FillUiBox(surface, track, trackRadius, background, gradientEnd, element.Fill);
                DrawUiBorder(surface, element, track, trackRadius, scale, border, legacyOutline: false);
                float inset = MathF.Max(2f, rect.Height * 0.12f);
                float knob = MathF.Max(1f, (rect.Height * 0.5f) - inset);
                float knobX = on ? track.Right - inset - knob : track.X + inset + knob;
                FillUiCircle(surface, foreground, knobX, rect.Y + (rect.Height * 0.5f), knob);
                float gap = 10f * scale;
                RectangleF label = new(track.Right + gap, rect.Y, MathF.Max(1, rect.Width - switchWidth - gap), rect.Height);
                if (!string.IsNullOrEmpty(text) && label.Width > 1)
                    DrawUiElementText(surface, element, text, label, scale, foreground, centredByDefault: false);
                break;
            }
        }
    }

    /// <summary>
    /// A box in one colour or a gradient, square or rounded. A square solid box is one rectangle,
    /// as boxes have always been drawn; the rest are smooth-edged pixel rows (columns for a
    /// left-to-right gradient, so each column is one colour).
    /// </summary>
    private static void FillUiBox(IPgslDrawSurface surface, RectangleF rect, float radius, Color from, Color to, UiFill fill)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        bool gradient = fill != UiFill.Solid && from != to;
        if (!gradient && radius <= 0)
        {
            surface.FillRectangle(from, rect);
            return;
        }
        double r = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) * 0.5);
        if (fill == UiFill.HorizontalGradient && gradient)
        {
            // Rows and columns swap: the shape is walked one column at a time.
            RoundRectSpan columns = new(rect.Y, rect.X, rect.Height, rect.Width, r);
            FillShapeCoverage(surface, column => Mix(from, to, ((column + 0.5) - rect.X) / rect.Width),
                rect.X, rect.Right, columns.At, transposed: true);
            return;
        }
        RoundRectSpan rows = new(rect.X, rect.Y, rect.Width, rect.Height, r);
        FillShapeCoverage(surface, row => gradient ? Mix(from, to, ((row + 0.5) - rect.Y) / rect.Height) : from,
            rect.Y, rect.Bottom, rows.At, transposed: false);
    }

    private static void FillUiCircle(IPgslDrawSurface surface, Color colour, float x, float y, float radius)
    {
        if (!(radius > 0)) return;
        RoundRectSpan circle = new(x - radius, y - radius, radius * 2, radius * 2, radius);
        FillShapeCoverage(surface, _ => colour, y - radius, y + radius, circle.At, transposed: false);
    }

    /// <summary>
    /// The border: its own width when set, otherwise the type's one-pixel outline (buttons and
    /// progress bars) exactly as before.
    /// </summary>
    private static void DrawUiBorder(IPgslDrawSurface surface, UiElement element, RectangleF rect, float radius, float scale,
        Color colour, bool legacyOutline)
    {
        float width;
        if (element.BorderWidth is float authored) width = authored * scale;
        else if (legacyOutline)
        {
            if (radius <= 0) { surface.DrawRectangle(colour, rect); return; }
            width = 1f;
        }
        else return;
        if (!(width > 0) || colour.A == 0 || rect.Width <= 0 || rect.Height <= 0) return;
        double b = Math.Min(width, Math.Min(rect.Width, rect.Height) * 0.5);
        double r = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) * 0.5);
        RoundRectSpan outer = new(rect.X, rect.Y, rect.Width, rect.Height, r);
        RoundRectSpan inner = new(rect.X + b, rect.Y + b, rect.Width - (b * 2), rect.Height - (b * 2), Math.Max(0, r - b));
        FillShapeCoverage(surface, _ => colour, rect.Y, rect.Bottom, (double y, Span<double> spans) => Subtract(outer, inner, y, spans), transposed: false);
    }

    private static Color Mix(Color from, Color to, double t)
    {
        t = Math.Clamp(double.IsFinite(t) ? t : 0, 0, 1);
        return Color.FromArgb(Channel(Lerp(from.A, to.A, t)), Channel(Lerp(from.R, to.R, t)),
            Channel(Lerp(from.G, to.G, t)), Channel(Lerp(from.B, to.B, t)));
    }

    /// <summary>
    /// Text in its box. With no alignment or spacing authored it is drawn exactly as before (text
    /// on the left, buttons centred, both in the middle); otherwise it is measured and placed.
    /// </summary>
    private static void DrawUiElementText(IPgslDrawSurface surface, UiElement element, string text, RectangleF rect, float scale,
        Color colour, bool centredByDefault)
    {
        if (string.IsNullOrEmpty(text) || colour.A == 0) return;
        float size = MathF.Max(1, element.FontSize * scale);
        if (element.TextAlign == UiHorizontalAlign.Auto && element.TextVerticalAlign == UiVerticalAlign.Auto && element.LetterSpacing == 0)
        {
            surface.DrawUiText(text, element.Font, size, colour, Rectangle.Round(rect), centredByDefault);
            return;
        }
        float tracking = element.LetterSpacing * scale;
        UiHorizontalAlign horizontal = element.TextAlign != UiHorizontalAlign.Auto ? element.TextAlign
            : centredByDefault ? UiHorizontalAlign.Center : UiHorizontalAlign.Left;
        float width = surface.MeasureText(text, element.Font, size, tracking).X;
        float lineHeight = size * 1.2f;
        float x = horizontal switch
        {
            UiHorizontalAlign.Center => rect.X + ((rect.Width - width) * 0.5f),
            UiHorizontalAlign.Right => rect.Right - width,
            _ => rect.X,
        };
        float y = element.TextVerticalAlign switch
        {
            UiVerticalAlign.Top => rect.Y,
            UiVerticalAlign.Bottom => rect.Bottom - lineHeight,
            _ => rect.Y + MathF.Max(0, (rect.Height - lineHeight) * 0.5f),
        };
        surface.DrawTextRun(text, element.Font, size, colour, MathF.Round(x), MathF.Round(y), tracking);
    }

    /// <summary>An Image element: stretched (times its scale), contained, covering, or a crop.</summary>
    private static void DrawUiImage(IPgslDrawSurface surface, UiElement element, RectangleF rect, float alpha)
    {
        if (string.IsNullOrWhiteSpace(element.Image)) return;
        if (element.ImageFit == UiImageFit.Stretch)
        {
            surface.DrawSpriteRectangle(element.Image,
                new RectangleF(rect.X, rect.Y, rect.Width * element.ImageScaleX, rect.Height * element.ImageScaleY),
                0, Color.White, alpha);
            return;
        }
        if (element.ImageFit == UiImageFit.Crop)
        {
            surface.DrawSpritePart(element.Image, 0, UiImageLayout.CropSource(element), rect, Color.White, alpha);
            return;
        }
        if (!ObjectDrawPass.TryGetImageFrameSize(ProjectPath, element.Image, out int imageWidth, out int imageHeight)
            || imageWidth <= 0 || imageHeight <= 0)
        {
            surface.DrawSpriteRectangle(element.Image, rect, 0, Color.White, alpha);
            return;
        }
        UiImageLayout.Fit(element, imageWidth, imageHeight, rect, out RectangleF source, out RectangleF destination);
        if (element.ImageFit == UiImageFit.Contain) surface.DrawSpriteRectangle(element.Image, destination, 0, Color.White, alpha);
        else surface.DrawSpritePart(element.Image, 0, source, destination, Color.White, alpha);
    }

    /// <summary>
    /// <see cref="FillCoverage"/> with a colour per pixel row; transposed, the "rows" are columns,
    /// so a left-to-right gradient is one colour per column.
    /// </summary>
    private static void FillShapeCoverage(IPgslDrawSurface surface, Func<int, Color> shade, double top, double bottom,
        SpanSource source, bool transposed)
    {
        if (!double.IsFinite(top) || !double.IsFinite(bottom)) return;
        int firstRow = (int)Math.Floor(top);
        int lastRow = Math.Min((int)Math.Ceiling(bottom), firstRow + 8192);
        Span<double> sampleSpans = stackalloc double[CoverageSamples * 4];
        Span<int> sampleCounts = stackalloc int[CoverageSamples];
        Span<double> scratch = stackalloc double[4];
        Span<int> edges = stackalloc int[CoverageSamples * 4];

        for (int row = firstRow; row < lastRow; row++)
        {
            Color color = shade(row);
            if (color.A == 0) continue;
            int edgeCount = 0;
            for (int s = 0; s < CoverageSamples; s++)
            {
                int count = source(row + ((s + 0.5) / CoverageSamples), scratch);
                sampleCounts[s] = count;
                for (int i = 0; i < count * 2; i++)
                {
                    sampleSpans[(s * 4) + i] = scratch[i];
                    edges[edgeCount++] = (int)Math.Floor(scratch[i]);
                }
            }
            if (edgeCount == 0) continue;

            edges[..edgeCount].Sort();
            int previous = int.MinValue;
            for (int e = 0; e < edgeCount; e++)
            {
                int column = edges[e];
                if (column == previous) continue;
                if (previous != int.MinValue && column > previous + 1)
                    EmitShape(surface, color, previous + 1, row, column - previous - 1,
                        Coverage(sampleSpans, sampleCounts, previous + 1, column), transposed);
                EmitShape(surface, color, column, row, 1, Coverage(sampleSpans, sampleCounts, column, column + 1), transposed);
                previous = column;
            }
        }
    }

    private static void EmitShape(IPgslDrawSurface surface, Color color, int x, int y, int width, double coverage, bool transposed)
    {
        if (coverage <= 0.002 || width <= 0) return;
        Color shade = coverage >= 0.998 ? color : Color.FromArgb((int)Math.Round(color.A * Math.Min(1, coverage)), color);
        if (shade.A == 0) return;
        surface.FillRectangle(shade, transposed ? new RectangleF(y, x, 1, width) : new RectangleF(x, y, width, 1));
    }

    #endregion
}

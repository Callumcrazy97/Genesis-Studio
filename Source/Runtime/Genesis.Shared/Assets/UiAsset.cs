using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Shared.Assets;

public enum UiElementType
{
    Panel,
    Text,
    Image,
    Button,
    ProgressBar,
    /// <summary>A track the player drags between Minimum and Maximum; PGSL reads it with UiGetValue.</summary>
    Slider,
    /// <summary>An on/off switch: Value is 0 or 1 and flips when the player clicks it.</summary>
    Toggle,
}

/// <summary>How a box is filled: one colour, or Background shading into GradientEnd.</summary>
public enum UiFill
{
    Solid,
    /// <summary>Background at the top shading to GradientEnd at the bottom.</summary>
    VerticalGradient,
    /// <summary>Background at the left shading to GradientEnd at the right.</summary>
    HorizontalGradient,
}

/// <summary>Where text sits across its box; Auto keeps the element type's own placement.</summary>
public enum UiHorizontalAlign
{
    Auto,
    Left,
    Center,
    Right,
}

/// <summary>Where text sits down its box; Auto is the middle, as text has always been placed.</summary>
public enum UiVerticalAlign
{
    Auto,
    Top,
    Middle,
    Bottom,
}

/// <summary>How an Image element's picture meets its box.</summary>
public enum UiImageFit
{
    /// <summary>Stretched to the box (times the image scale), as images have always been drawn.</summary>
    Stretch,
    /// <summary>The whole picture, as large as fits, centred; the rest of the box stays empty.</summary>
    Contain,
    /// <summary>The box filled without stretching, centred; what overflows is cut off.</summary>
    Cover,
    /// <summary>Only the Crop rectangle of the picture (fractions 0 to 1), stretched to the box.</summary>
    Crop,
}

/// <summary>
/// Colours an element changes to while hovered, pressed, selected or disabled. Empty values keep
/// the element's normal colour.
/// </summary>
public sealed class UiStateStyle
{
    public string Background { get; set; } = string.Empty;
    public string GradientEnd { get; set; } = string.Empty;
    public string Foreground { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string BorderColor { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Background) && string.IsNullOrWhiteSpace(GradientEnd)
        && string.IsNullOrWhiteSpace(Foreground) && string.IsNullOrWhiteSpace(Accent) && string.IsNullOrWhiteSpace(BorderColor);
}

public enum UiAnchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
    Stretch,
}

/// <summary>An element's colours in one state, shared by the UI Editor canvas and the game.</summary>
public readonly record struct UiLook(Color Background, Color GradientEnd, Color Foreground, Color Accent, Color Border)
{
    /// <summary>
    /// The colours for a state ("hover", "pressed", "selected", "disabled", or empty for normal).
    /// A state without a look of its own changes nothing, except disabled, which fades to half.
    /// </summary>
    public static UiLook Resolve(UiElement element, string state)
    {
        Color background = ParseColor(element.Background, Color.FromArgb(204, 22, 27, 34));
        Color gradientEnd = ParseColor(element.GradientEnd, background);
        Color foreground = ParseColor(element.Foreground, Color.White);
        Color accent = ParseColor(element.Accent, Color.FromArgb(108, 140, 255));
        UiStateStyle style = string.IsNullOrEmpty(state) ? null : element.StateStyle(state);
        if (style is not null)
        {
            background = ParseColor(style.Background, background);
            gradientEnd = ParseColor(style.GradientEnd, string.IsNullOrWhiteSpace(style.Background) ? gradientEnd : background);
            foreground = ParseColor(style.Foreground, foreground);
            accent = ParseColor(style.Accent, accent);
        }
        Color border = ParseColor(element.BorderColor, element.Type == UiElementType.ProgressBar ? foreground : accent);
        if (style is not null) border = ParseColor(style.BorderColor, border);
        if (string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase) && (style is null || style.IsEmpty))
        {
            static Color Fade(Color colour) => Color.FromArgb(colour.A / 2, colour);
            return new UiLook(Fade(background), Fade(gradientEnd), Fade(foreground), Fade(accent), Fade(border));
        }
        return new UiLook(background, gradientEnd, foreground, accent, border);
    }

    /// <summary>#RRGGBB or #AARRGGBB; anything else is the fallback.</summary>
    public static Color ParseColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint argb)) return fallback;
        return hex.Length == 6 ? Color.FromArgb(255, Color.FromArgb(unchecked((int)argb))) : Color.FromArgb(unchecked((int)argb));
    }
}

/// <summary>Where an Image element's picture goes, shared by the UI Editor canvas and the game.</summary>
public static class UiImageLayout
{
    /// <summary>The crop rectangle in fractions of the picture, kept inside it.</summary>
    public static RectangleF CropSource(UiElement element)
    {
        float x = Math.Clamp(element.CropX, 0f, 1f), y = Math.Clamp(element.CropY, 0f, 1f);
        return new RectangleF(x, y, Math.Clamp(element.CropWidth, 0.0001f, MathF.Max(0.0001f, 1f - x)),
            Math.Clamp(element.CropHeight, 0.0001f, MathF.Max(0.0001f, 1f - y)));
    }

    /// <summary>
    /// The part of the picture (in fractions) and the rectangle it is drawn into. Contain: the
    /// whole picture in the largest centred rectangle of its shape. Cover: the box filled by the
    /// centred part of the picture with the box's shape. Crop: the crop rectangle into the box.
    /// Stretch: the whole picture into the box.
    /// </summary>
    public static void Fit(UiElement element, int imageWidth, int imageHeight, RectangleF box,
        out RectangleF source, out RectangleF destination)
    {
        source = element.ImageFit == UiImageFit.Crop ? CropSource(element) : new RectangleF(0, 0, 1, 1);
        destination = box;
        if (imageWidth <= 0 || imageHeight <= 0 || box.Width <= 0 || box.Height <= 0) return;
        float imageAspect = imageWidth / (float)imageHeight;
        float boxAspect = box.Width / box.Height;
        if (element.ImageFit == UiImageFit.Contain)
        {
            float width = boxAspect > imageAspect ? box.Height * imageAspect : box.Width;
            float height = boxAspect > imageAspect ? box.Height : box.Width / imageAspect;
            destination = new RectangleF(box.X + ((box.Width - width) * 0.5f), box.Y + ((box.Height - height) * 0.5f), width, height);
        }
        else if (element.ImageFit == UiImageFit.Cover)
        {
            if (boxAspect > imageAspect)
            {
                float part = imageAspect / boxAspect;
                source = new RectangleF(0, (1 - part) * 0.5f, 1, part);
            }
            else
            {
                float part = boxAspect / imageAspect;
                source = new RectangleF((1 - part) * 0.5f, 0, part, 1);
            }
        }
    }
}

public sealed class UiAssetDocument
{
    public int SchemaVersion { get; set; } = 1;
    public int DesignWidth { get; set; } = 1280;
    public int DesignHeight { get; set; } = 720;
    public List<UiElement> Elements { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static UiAssetDocument Load(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("UI resource is larger than 16 MB.");
        return Deserialize(File.ReadAllText(path));
    }

    public static UiAssetDocument Deserialize(string json)
    {
        UiAssetDocument document = JsonSerializer.Deserialize<UiAssetDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("UI resource is empty.");
        document.Validate();
        return document;
    }

    public static string Serialize(UiAssetDocument document)
    {
        document.Validate();
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Unsupported UI schema {SchemaVersion}.");
        if (DesignWidth is < 1 or > 16384 || DesignHeight is < 1 or > 16384)
            throw new InvalidDataException("UI canvas dimensions must be between 1 and 16384.");
        if (Elements is null || Elements.Count > 16384) throw new InvalidDataException("Invalid UI element collection.");
        Dictionary<string, UiElement> elements = new(StringComparer.OrdinalIgnoreCase);
        foreach (UiElement element in Elements)
        {
            if (element is null || string.IsNullOrWhiteSpace(element.Id) || !elements.TryAdd(element.Id, element))
                throw new InvalidDataException("UI elements need unique, nonempty IDs.");
            if (!Enum.IsDefined(element.Type) || !Enum.IsDefined(element.Anchor)) throw new InvalidDataException("Invalid UI type or anchor.");
            if (!float.IsFinite(element.X) || !float.IsFinite(element.Y) || !float.IsFinite(element.Width) || !float.IsFinite(element.Height)
                || !float.IsFinite(element.FontSize) || !float.IsFinite(element.Value) || !float.IsFinite(element.Maximum)
                || !float.IsFinite(element.ImageScaleX) || !float.IsFinite(element.ImageScaleY)
                || element.Width < 0 || element.Height < 0 || element.Anchor != UiAnchor.Stretch && (element.Width == 0 || element.Height == 0)
                || element.ImageScaleX <= 0 || element.ImageScaleY <= 0 || element.FontSize <= 0 || element.Maximum <= 0)
                throw new InvalidDataException($"UI element '{element.Id}' contains invalid dimensions or values.");
            if (!Enum.IsDefined(element.Fill) || !Enum.IsDefined(element.TextAlign) || !Enum.IsDefined(element.TextVerticalAlign)
                || !Enum.IsDefined(element.ImageFit)
                || !float.IsFinite(element.Minimum) || !float.IsFinite(element.Step) || !float.IsFinite(element.CornerRadius)
                || !float.IsFinite(element.LetterSpacing) || element.BorderWidth is float border && (!float.IsFinite(border) || border < 0)
                || !float.IsFinite(element.CropX) || !float.IsFinite(element.CropY) || !float.IsFinite(element.CropWidth) || !float.IsFinite(element.CropHeight)
                || element.Step < 0 || element.CornerRadius < 0 || element.CropWidth <= 0 || element.CropHeight <= 0
                || element.Type == UiElementType.Slider && element.Minimum >= element.Maximum)
                throw new InvalidDataException($"UI element '{element.Id}' contains invalid style values.");
        }
        foreach (UiElement element in Elements)
        {
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { element.Id };
            string parent = element.ParentId;
            while (!string.IsNullOrWhiteSpace(parent))
            {
                if (!visited.Add(parent)) throw new InvalidDataException($"UI element '{element.Id}' has a parent cycle.");
                if (!elements.TryGetValue(parent, out UiElement ancestor)) throw new InvalidDataException($"UI parent '{parent}' does not exist.");
                parent = ancestor.ParentId;
            }
        }
    }

    public static void Save(string path, UiAssetDocument document)
    {
        string json = Serialize(document);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class UiElement
{
    public string Id { get; set; } = "Element";
    public string ParentId { get; set; } = string.Empty;
    public UiElementType Type { get; set; } = UiElementType.Panel;
    public UiAnchor Anchor { get; set; } = UiAnchor.TopLeft;
    public float X { get; set; } = 40f;
    public float Y { get; set; } = 40f;
    public float Width { get; set; } = 240f;
    public float Height { get; set; } = 80f;
    public int Order { get; set; }
    public bool Visible { get; set; } = true;
    public string Text { get; set; } = string.Empty;
    public string Font { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 18f;
    public string Image { get; set; } = string.Empty;
    public float ImageScaleX { get; set; } = 1f;
    public float ImageScaleY { get; set; } = 1f;
    public string Background { get; set; } = "#CC161B22";
    public string Foreground { get; set; } = "#FFFFFFFF";
    public string Accent { get; set; } = "#FF6C8CFF";
    public float Value { get; set; } = 100f;
    public float Maximum { get; set; } = 100f;

    // Menu styling. Every default draws an element exactly as before these existed, so older UI
    // files load and look unchanged.

    /// <summary>Lowest value of a slider (a progress bar and toggle start at 0).</summary>
    public float Minimum { get; set; }
    /// <summary>Slider snapping: the value moves in steps of this size; 0 is continuous.</summary>
    public float Step { get; set; }
    /// <summary>Corner radius in design pixels; 0 is square.</summary>
    public float CornerRadius { get; set; }
    /// <summary>Border thickness in design pixels; unset keeps the type's own outline (buttons and bars draw one pixel).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? BorderWidth { get; set; }
    /// <summary>Border colour; empty uses the accent (a progress bar, its text colour).</summary>
    public string BorderColor { get; set; } = string.Empty;
    public UiFill Fill { get; set; } = UiFill.Solid;
    /// <summary>Second gradient colour, at the bottom or right.</summary>
    public string GradientEnd { get; set; } = string.Empty;
    public UiHorizontalAlign TextAlign { get; set; } = UiHorizontalAlign.Auto;
    public UiVerticalAlign TextVerticalAlign { get; set; } = UiVerticalAlign.Auto;
    /// <summary>Extra design pixels between letters.</summary>
    public float LetterSpacing { get; set; }
    public UiImageFit ImageFit { get; set; } = UiImageFit.Stretch;
    /// <summary>Part of the picture a Crop image shows, in fractions of its width and height.</summary>
    public float CropX { get; set; }
    public float CropY { get; set; }
    public float CropWidth { get; set; } = 1f;
    public float CropHeight { get; set; } = 1f;
    /// <summary>False greys the element out with its Disabled look and ignores the pointer.</summary>
    public bool Enabled { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiStateStyle Hover { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiStateStyle Pressed { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiStateStyle Selected { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiStateStyle Disabled { get; set; }

    /// <summary>True for the element types the pointer can hover, press and change.</summary>
    [JsonIgnore]
    public bool IsInteractive => Type is UiElementType.Button or UiElementType.Slider or UiElementType.Toggle;

    /// <summary>The style for a state name (Hover, Pressed, Selected, Disabled); null when it has none.</summary>
    public UiStateStyle StateStyle(string state) => state?.Trim().ToLowerInvariant() switch
    {
        "hover" => Hover,
        "pressed" => Pressed,
        "selected" => Selected,
        "disabled" => Disabled,
        _ => null,
    };

    /// <summary>The style for a state, created when it does not exist yet.</summary>
    public UiStateStyle EnsureStateStyle(string state)
    {
        switch (state?.Trim().ToLowerInvariant())
        {
            case "hover": return Hover ??= new UiStateStyle();
            case "pressed": return Pressed ??= new UiStateStyle();
            case "selected": return Selected ??= new UiStateStyle();
            case "disabled": return Disabled ??= new UiStateStyle();
            default: throw new ArgumentException("Unknown UI state " + state + ".", nameof(state));
        }
    }

    /// <summary>Clears a state's style; returns false for an unknown state.</summary>
    public bool ClearStateStyle(string state)
    {
        switch (state?.Trim().ToLowerInvariant())
        {
            case "hover": Hover = null; return true;
            case "pressed": Pressed = null; return true;
            case "selected": Selected = null; return true;
            case "disabled": Disabled = null; return true;
            default: return false;
        }
    }

    public static readonly string[] StateNames = ["Hover", "Pressed", "Selected", "Disabled"];
}

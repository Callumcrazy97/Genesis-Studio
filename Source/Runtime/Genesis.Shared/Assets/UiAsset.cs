using System;
using System.Collections.Generic;
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
}

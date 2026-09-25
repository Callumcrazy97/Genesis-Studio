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

    public static UiAssetDocument Load(string path) =>
        JsonSerializer.Deserialize<UiAssetDocument>(File.ReadAllText(path), JsonOptions)
        ?? new UiAssetDocument();

    public static void Save(string path, UiAssetDocument document) =>
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
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

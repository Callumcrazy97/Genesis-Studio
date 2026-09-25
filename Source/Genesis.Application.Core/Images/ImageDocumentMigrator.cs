using System.Text.Json;

namespace Genesis.Application.Core.Images;

/// <summary>
/// Upgrades early greenfield sprite schema v1 stubs to the current v2 document model.
/// This is not a GameManager or legacy-project importer.
/// </summary>
internal static class ImageDocumentMigrator
{
    public static ImageDocument MigrateFromV1(JsonElement root)
    {
        int width = 64;
        int height = 64;
        if (TryGetProperty(root, "canvas", out JsonElement canvas))
        {
            if (TryGetInt(canvas, "width", out int canvasWidth) && canvasWidth > 0)
                width = canvasWidth;
            if (TryGetInt(canvas, "height", out int canvasHeight) && canvasHeight > 0)
                height = canvasHeight;
        }

        ImageDocument document = ImageDocument.CreateDefault(width, height);
        document.SchemaVersion = ImageDocument.CurrentSchemaVersion;

        if (TryGetProperty(root, "frames", out JsonElement frames) && frames.ValueKind == JsonValueKind.Array)
        {
            document.Frames.Clear();
            foreach (JsonElement frameElement in frames.EnumerateArray())
            {
                ImageFrame frame = new()
                {
                    Id = ReadString(frameElement, "id") ?? Guid.NewGuid().ToString("N"),
                    Name = ReadString(frameElement, "name") ?? "Frame",
                    DurationMilliseconds = Math.Max(1, ReadInt(frameElement, "durationMilliseconds", 100)),
                };
                if (TryGetProperty(frameElement, "sourceRectangle", out JsonElement sourceRectangle))
                {
                    frame.SourceRectangle = new ImageRectangle
                    {
                        X = ReadInt(sourceRectangle, "x", 0),
                        Y = ReadInt(sourceRectangle, "y", 0),
                        Width = Math.Max(1, ReadInt(sourceRectangle, "width", width)),
                        Height = Math.Max(1, ReadInt(sourceRectangle, "height", height)),
                    };
                }
                else
                {
                    frame.SourceRectangle = new ImageRectangle { Width = width, Height = height };
                }

                string? source = ReadString(frameElement, "source");
                if (!string.IsNullOrWhiteSpace(source))
                    frame.Source = source;
                document.Frames.Add(frame);
            }
        }

        if (TryGetProperty(root, "layers", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
        {
            document.Layers.Clear();
            foreach (JsonElement layerElement in layers.EnumerateArray())
            {
                ImageLayer layer = new()
                {
                    Id = ReadString(layerElement, "id") ?? Guid.NewGuid().ToString("N"),
                    Name = ReadString(layerElement, "name") ?? "Layer",
                    Visible = ReadBool(layerElement, "visible", true),
                    Locked = ReadBool(layerElement, "locked", false),
                    Opacity = ReadDouble(layerElement, "opacity", 1d),
                };
                if (TryGetProperty(layerElement, "cels", out JsonElement cels) && cels.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement celElement in cels.EnumerateArray())
                    {
                        string? frameId = ReadString(celElement, "frameId");
                        if (string.IsNullOrWhiteSpace(frameId))
                            continue;
                        layer.Cels.Add(new ImageCel
                        {
                            FrameId = frameId,
                            Source = ReadString(celElement, "source"),
                        });
                    }
                }

                document.Layers.Add(layer);
            }
        }

        return document;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        TryGetInt(element, name, out int value) ? value : fallback;

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        if (!TryGetProperty(element, name, out JsonElement property))
        {
            value = 0;
            return false;
        }

        return property.TryGetInt32(out value);
    }

    private static double ReadDouble(JsonElement element, string name, double fallback) =>
        TryGetProperty(element, name, out JsonElement property) && property.TryGetDouble(out double value)
            ? value
            : fallback;
}

using System.Text.Json;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Reads the few object-document fields that other surfaces need without opening the editor.
/// </summary>
/// <remarks>
/// An object's image is a project-relative path to an <c>.image.json</c> resource, stored in its
/// <c>sprite</c> field. Anything that wants to *show* the object — the Assets tree's icon column, the
/// Object Editor's own preview — needs to get from that field to real pixels, and each of them
/// reinventing "parse the JSON, join to the root, find the frame PNG" is how those views end up
/// disagreeing about what an object looks like. One reader, used by all of them.
/// </remarks>
public static class ObjectResourceReader
{
    private const int MaxObjectDocumentBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The project-relative image path an object is bound to (<c>Assets/Images/Coin.image.json</c>),
    /// or null when the object has no image or cannot be read.
    /// </summary>
    public static string? ReadImagePath(string objectDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(objectDocumentPath) || !File.Exists(objectDocumentPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(
                objectDocumentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxObjectDocumentBytes)
            {
                // Object documents contain metadata and event references, not bulk payloads. Refusing
                // an implausibly large document keeps thumbnail resolution from blocking the UI on it.
                return null;
            }

            byte[] json = new byte[(int)stream.Length];
            stream.ReadExactly(json);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetProperty(document.RootElement, "sprite", out JsonElement sprite) ||
                sprite.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = sprite.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed or locked object document is not a reason to fail whatever asked; the
            // caller simply gets "no image", and the Object Editor reports the parse error itself.
            return null;
        }
    }

    /// <summary>
    /// Absolute path of the image *resource document* an object is bound to, or null.
    /// </summary>
    public static string? ResolveImageDocument(string objectDocumentPath, string projectRoot)
    {
        string? reference = ReadImagePath(objectDocumentPath);
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(projectRoot)) return null;
        string resolved = ResourceNames.Resolve(projectRoot, reference, ResourceType.Image);
        return resolved.Length > 0 ? resolved : null;
    }

    /// <summary>
    /// Absolute path of a raster file (PNG/JPG/…) that shows what an object looks like, or null.
    /// </summary>
    public static string? ResolveImageFile(string objectDocumentPath, string projectRoot)
    {
        string? document = ResolveImageDocument(objectDocumentPath, projectRoot);
        return document is null ? null : ResourceAssociates.FindPrimaryImage(document);
    }

    private static bool TryGetProperty(
        JsonElement parent,
        string name,
        out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            if (parent.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (JsonProperty property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

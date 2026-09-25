#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Imaging;

/// <summary>Loads the existing Image Editor pixelRigs schema; never reads assets during playback.</summary>
public static class PixelRigAssetLoader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static PixelRigSprite Load(string projectPath, string image, string rigName = "")
    {
        string descriptor = SpriteAssetLoader.ResolveDescriptorPath(projectPath, image);
        if (string.IsNullOrWhiteSpace(descriptor)) throw new FileNotFoundException($"Image resource '{image}' was not found.");
        return LoadFile(descriptor, rigName);
    }

    public static PixelRigSprite LoadFile(string descriptor, string rigName = "")
    {
        descriptor = Path.GetFullPath(descriptor);
        FileInfo file = new(descriptor);
        if (!file.Exists) throw new FileNotFoundException("Rig image resource was not found.", descriptor);
        if (file.Length > 128L * 1024 * 1024) throw new InvalidDataException("Rig image descriptors must be below 128 MiB.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptor));
        JsonElement rigs = default;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (property.Name.Equals("pixelRigs", StringComparison.OrdinalIgnoreCase)) rigs = property.Value;
        if (rigs.ValueKind != JsonValueKind.Array || rigs.GetArrayLength() == 0)
            throw new InvalidDataException("This image has no pixel rig. Bind a raster layer in the Image Editor and save it first.");
        PixelRigDefinition? selected = null;
        foreach (JsonElement item in rigs.EnumerateArray())
        {
            PixelRigDefinition? candidate = item.Deserialize<PixelRigDefinition>(Options);
            if (candidate is not null && (string.IsNullOrWhiteSpace(rigName)
                || string.Equals(candidate.Id, rigName, StringComparison.Ordinal)
                || string.Equals(candidate.Name, rigName, StringComparison.OrdinalIgnoreCase)))
            { selected = candidate; break; }
        }
        if (selected is null) throw new InvalidDataException($"Pixel rig '{rigName}' was not found in '{Path.GetFileName(descriptor)}'.");
        PixelRigData.Validate(selected);
        SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(descriptor);
        if (sprite.Canvas.Width != selected.Width || sprite.Canvas.Height != selected.Height)
            throw new InvalidDataException("The rig canvas no longer matches the image canvas; rebind the rig after resizing the image.");
        SpriteRuntimeOrigin origin = sprite.Frames.FirstOrDefault(f => f.Id == selected.SourceFrameId)?.OriginOverride ?? sprite.Origin;
        bool pixels = string.Equals(origin.Space, "pixels", StringComparison.OrdinalIgnoreCase);
        float originX = (float)(pixels ? origin.X : origin.X * selected.Width);
        float originY = (float)(pixels ? origin.Y : origin.Y * selected.Height);
        if (!float.IsFinite(originX) || !float.IsFinite(originY)) throw new InvalidDataException("Rig image origin must be finite.");
        PixelRigPlayer player = new(selected);
        PixelRigLayerStack? layers = PixelRigLayerStack.Load(document.RootElement, descriptor, player);
        return new PixelRigSprite(player, descriptor, rigName, originX, originY, layers);
    }
}

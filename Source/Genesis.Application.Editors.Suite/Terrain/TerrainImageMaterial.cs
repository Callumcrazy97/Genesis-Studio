using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Reads the same channel-tagged layers that the Image Editor authors. No raster relinking.</summary>
internal sealed record TerrainImageMaterial(int Width, int Height, byte[] Albedo, byte[] Normal, byte[] Orm)
{
    internal static readonly ImageMaterialChannel[] Required = [ImageMaterialChannel.Normal, ImageMaterialChannel.Roughness, ImageMaterialChannel.Occlusion];

    private static ImageWorkspace Open(string root, string reference, out ImageDocumentSession session)
    {
        string path = ResourceNames.Resolve(root, reference, ResourceType.Image);
        var loaded = ImageDocumentSerializer.LoadAtomic(path);
        session = new ImageDocumentSession(loaded.Document, path, ImageDocumentAccess.Editor);
        return ImageWorkspaceStorage.Load(session);
    }

    public static string Describe(string root, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "Choose an Image resource for Albedo / Normal / Roughness / AO.";
        try
        {
            var workspace = Open(root, reference, out _);
            var channels = workspace.Frames.FirstOrDefault()?.Layers.Select(layer => layer.Channel).ToHashSet() ?? [];
            var missing = Required.Where(channel => !channels.Contains(channel)).Select(channel => channel == ImageMaterialChannel.Occlusion ? "AO" : channel.ToString()).ToArray();
            return missing.Length == 0 ? "PBR ready · Albedo · Normal · Roughness · AO" : "Albedo ready · Missing: " + string.Join(", ", missing);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Text.Json.JsonException)
        { return "Cannot read material: " + exception.Message; }
    }

    public static void GenerateMissing(string root, string reference, PbrMaterialSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var workspace = Open(root, reference, out var session);
        if ((long)workspace.Width * workspace.Height * 4 * 8 * workspace.Frames.Count > 536870912)
            throw new InvalidOperationException("Material generation exceeds the 512 MB budget. Reduce the image size first.");
        var ids = workspace.Frames.SelectMany(frame => frame.Layers).GroupBy(layer => layer.Channel)
            .ToDictionary(group => group.Key, group => group.First().Id);
        for (int frameIndex = 0; frameIndex < workspace.Frames.Count; frameIndex++)
        {
            var frame = workspace.Frames[frameIndex];
            var maps = PbrMaterialGenerator.Generate(workspace.CompositeCurrentFrameFor(frameIndex).Pixels, workspace.Width, workspace.Height, settings);
            foreach (var map in maps)
            {
                // Preserve hand-authored or previously generated channels.
                if (frame.Layers.Any(layer => layer.Channel == map.Channel)) continue;
                if (!ids.TryGetValue(map.Channel, out Guid id)) ids[map.Channel] = id = Guid.NewGuid();
                frame.Layers.Add(new ImageLayerBuffer { Id = id, Name = map.Name, Channel = map.Channel, Pixels = map.Pixels });
            }
        }
        ImageWorkspaceStorage.Save(session, workspace);
    }

    public static TerrainImageMaterial Load(string root, string reference, int frameIndex = 0)
    {
        var pixels = Genesis.Runtime.Assets.ImageMaterialAssetLoader.Load(root, reference, frameIndex);
        return new(pixels.Width, pixels.Height, pixels.Albedo, pixels.Normal, pixels.Orm);
    }
}

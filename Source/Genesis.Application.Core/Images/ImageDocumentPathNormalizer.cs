namespace Genesis.Application.Core.Images;

internal static class ImageDocumentPathNormalizer
{
    public static void Normalize(ImageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Import.Source = NormalizeResourcePath(document.Import.Source);
        foreach (ImageFrame frame in document.Frames)
            frame.Source = NormalizeResourcePath(frame.Source);
        foreach (ImageLayer layer in document.Layers)
        {
            foreach (ImageCel cel in layer.Cels)
                cel.Source = NormalizeResourcePath(cel.Source);
        }
        foreach (ImageMaterialChannelDefinition channel in document.MaterialChannels)
            channel.Source = NormalizeResourcePath(channel.Source);
    }

    private static string? NormalizeResourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) || normalized.Split('/').Any(segment => segment == ".."))
            return null;

        return normalized;
    }
}

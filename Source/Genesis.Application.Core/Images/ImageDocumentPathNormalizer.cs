namespace Genesis.Application.Core.Images;

internal static class ImageDocumentPathNormalizer
{
    public static void Normalize(ImageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Import.Source = NormalizeResourcePath(document.Import.Source);
        foreach (ImageFrame frame in document.Frames)
            frame.Source = NormalizeResourcePath(frame.Source, allowSibling: true);
        foreach (ImageLayer layer in document.Layers)
        {
            foreach (ImageCel cel in layer.Cels)
                cel.Source = NormalizeResourcePath(cel.Source, allowSibling: true);
        }
        foreach (ImageMaterialChannelDefinition channel in document.MaterialChannels)
            channel.Source = NormalizeResourcePath(channel.Source);
    }

    private static string? NormalizeResourcePath(string? path, bool allowSibling = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalized = path.Replace('\\', '/');
        // Shared frame/cel pixels may live beside the document's folder. They remain
        // document-relative; imports are copied locally and keep their stricter policy.
        if (Path.IsPathRooted(path) || (!allowSibling && normalized.Split('/').Any(segment => segment == "..")))
            return null;

        return normalized;
    }
}

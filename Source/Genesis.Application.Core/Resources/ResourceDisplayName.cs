namespace Genesis.Application.Core.Resources;

/// <summary>Only the resource name is displayed. Storage paths and extensions are never reference values.</summary>
public static class ResourceDisplayName
{
    public static string Format(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return string.Empty;
        if (Path.IsPathRooted(pathOrName) && File.Exists(pathOrName))
            return ResourceNames.Name(ResourceNames.FindProjectRoot(pathOrName), pathOrName);
        return ResourceNames.FormatName(pathOrName);
    }
}

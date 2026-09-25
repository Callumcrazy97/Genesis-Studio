namespace Genesis.Runtime.Assets;

/// <summary>Private storage resolution behind the public, project-wide resource-name namespace.</summary>
public static class TexturePathResolver
{
    public static string Resolve(string projectPath, string resourceName) =>
        ResourceNames.ResolveFile(projectPath, resourceName, ResourceType.Image);

    public static void InvalidateIndex(string projectPath) => ResourceNames.Invalidate(projectPath);
}

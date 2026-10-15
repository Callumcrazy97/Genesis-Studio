using System;
using System.IO;
using Genesis.Runtime.Assets;

namespace Genesis.Runtime.Particles;

/// <summary>
/// The picture a Particle resource's <c>texturePath</c> names, as a file to load. An Image
/// resource is the usual reference: by its name (what the Particle editor's picker writes, and
/// what Rename rewrites), or by its id (the guid in its .meta, which a rename keeps). The Image's
/// first frame is used, with its cooked texture beside it when the Image is cooked. A file path
/// inside the project still works as before.
/// </summary>
public static class ParticleTextureResolver
{
    /// <summary>The file to load for <paramref name="reference"/>, or empty when nothing is found.</summary>
    public static string ResolveFile(string projectPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(projectPath)) return string.Empty;
        string value = reference.Trim();
        string resolved;
        try
        {
            resolved = TexturePathResolver.Resolve(projectPath, value);
            if (string.IsNullOrWhiteSpace(resolved) && Guid.TryParse(value, out Guid id))
                resolved = ResourceNames.For(projectPath).FindById(id, ResourceType.Image)?.FullPath ?? string.Empty;
        }
        catch (InvalidDataException)
        {
            // Two resources share the name: Studio's validation names them; nothing is drawn from it.
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(resolved)) return string.Empty;
        if (!SpriteAssetLoader.IsSpriteDescriptorPath(resolved)) return resolved;
        try
        {
            var image = SpriteAssetLoader.Load(resolved);
            return SpriteAssetLoader.ResolveFrameTexturePath(resolved, image, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or ArgumentException or System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }
}

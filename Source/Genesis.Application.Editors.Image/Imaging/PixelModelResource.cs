using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Image.Imaging;

/// <summary>Saves a converted pixel model as an ordinary Model resource the Studio lists.</summary>
public static class PixelModelResource
{
    /// <summary>
    /// Creates the Model beside its Image when that folder may hold Models, otherwise in the
    /// project's Models folder, and returns the new resource's path.
    /// </summary>
    public static string Create(string imagePath, GModelAsset asset, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(asset);
        string validName = ResourceNames.ValidateName(name);
        string root = ResourceNames.FindProjectRoot(imagePath);
        if (File.Exists(ResourceNames.Resolve(root, validName, ResourceType.Model)))
            throw new InvalidOperationException("A Model named '" + validName + "' already exists. Choose another name.");
        ResourceService resources = new(new ProjectService().OpenProject(root));
        string folder = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? resources.AssetsRoot;
        string path;
        try { path = resources.CreateResource(folder, ResourceKind.Model, validName); }
        catch (InvalidOperationException)
        {
            // Typed resource roots keep Models out of the Images folder.
            path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Model), ResourceKind.Model, validName);
        }
        asset.Name = validName;
        StudioModelResourceLoader.SaveCanonical(path, asset);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        ProjectAssetWriteRegistry.MarkLocalWrite(StudioModelResourceLoader.CanonicalPath(path));
        ResourceNames.Invalidate(root);
        return path;
    }
}

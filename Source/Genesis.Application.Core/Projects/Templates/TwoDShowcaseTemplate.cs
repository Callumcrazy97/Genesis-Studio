using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>Copies a native, fully editable resource project; no alternate engine or embedded game loop.</summary>
public static class TwoDShowcaseTemplate
{
    public const string TemplateId = "2DShowcase";
    public const string StartRoom = "Assets/Rooms/Mushroom Meadow.room.json";

    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string bundle = Path.Combine(AppContext.BaseDirectory, "Projects", "Templates", "Assets", "TwoDShowcase");
        if (!Directory.Exists(Path.Combine(bundle, "Assets")))
            throw new DirectoryNotFoundException("The Mushroom Meadow template assets are missing. Rebuild Studio with its template content.");
        foreach (string source in Directory.EnumerateFiles(Path.Combine(bundle, "Assets"), "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(session.RootPath, Path.GetRelativePath(bundle, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
        ResourceFolderPolicy.EnsureRoots(session);
        session.Manifest.StartRoom = StartRoom;
        session.Manifest.Description = "Mushroom Meadow: an editable SNES-style 2D platformer showcase.";
        session.Manifest.Runtime.AllowEscapeToClose = false;
        session.Manifest.Rendering.FogEnabled = false;
    }
}

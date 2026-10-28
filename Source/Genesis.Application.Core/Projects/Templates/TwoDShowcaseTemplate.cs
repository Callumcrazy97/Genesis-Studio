using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>Copies a native, fully editable resource project; no alternate engine or embedded game loop.</summary>
public static class TwoDShowcaseTemplate
{
    public const string TemplateId = "2DShowcase";
    /// <summary>Where the shipped game starts: the skippable intro cinematic, then the Main Menu.</summary>
    public const string StartRoom = "Assets/Rooms/Intro.room.json";
    /// <summary>Level 1 (world 1-1), the room the gameplay regressions drive directly.</summary>
    public const string LevelOneRoom = "Assets/Rooms/Mushroom Meadow.room.json";
    /// <summary>The resource name of <see cref="LevelOneRoom"/>, for RoomGoto and the Player's --room.</summary>
    public const string LevelOneRoomName = "Mushroom Meadow";

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
        session.Manifest.Description = "Mushroom Meadow: a complete, editable SNES-style 2D platformer adventure. "
            + "Pip recovers the five Glowspores across five levels, two bosses, a village hub and an overworld map.";
        // Ambience plays on its own bus (beside the built-in music and sfx) so it can be balanced.
        if (!session.Manifest.AudioBuses.Contains("ambient")) session.Manifest.AudioBuses.Add("ambient");
        // The game shows its version on the Main Menu and in the credits; the icon is the Pip portrait.
        session.Manifest.Version = "1.0.0";
        session.Manifest.ProjectIcon = "Portrait Pip";
        session.Manifest.Runtime.AllowEscapeToClose = false;
        session.Manifest.Rendering.FogEnabled = false;
    }
}

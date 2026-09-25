using System;

namespace Genesis.Shared.Assets;

/// <summary>
/// Typed enumeration of every resource kind the editor and runtime understand.
/// This is the Shared-layer canonical set; the editor's <c>ScriptAssetKind</c>
/// and the on-disk plural folder names map onto it (see <see cref="AssetKindNames"/>).
/// </summary>
public enum AssetKind
{
    Unknown = 0,
    Sprite,
    Background,
    Texture,
    Material,
    Audio,
    Script,
    Shader,
    Object,
    Model,
    Room,
    Terrain,
    Particle,
    Physics,
    Note,
    Pathing,
    UserInterface,
    TerrainEntity,
}

/// <summary>
/// Static mapping helpers between <see cref="AssetKind"/> and the two string
/// representations used on disk: the plural folder name ("Sprites") and the
/// singular label stored in <c>.meta</c> files ("Sprite"). Handles the
/// irregular plurals (Physics, Audio) that the older <c>TrimEnd('s')</c>
/// heuristic got wrong.
/// </summary>
public static class AssetKindNames
{
    public const string SpritesFolder     = "Sprites";
    public const string BackgroundsFolder = "Backgrounds";
    public const string TexturesFolder    = "Textures";
    public const string MaterialsFolder   = "Materials";
    public const string AudioFolder       = "Audio";
    public const string ScriptsFolder     = "Scripts";
    public const string ShadersFolder     = "Shaders";
    public const string ObjectsFolder     = "Objects";
    public const string ModelsFolder      = "Models";
    public const string RoomsFolder       = "Rooms";
    public const string TerrainFolder     = "Terrain";
    public const string ParticlesFolder   = "Particles";
    public const string PhysicsFolder     = "Physics";
    public const string NotesFolder       = "Notes";

    /// <summary>Canonical plural folder name for a kind (e.g. <c>Sprite → "Sprites"</c>).</summary>
    public static string FolderName(AssetKind kind) => kind switch
    {
        AssetKind.Sprite     => SpritesFolder,
        AssetKind.Background => BackgroundsFolder,
        AssetKind.Texture    => TexturesFolder,
        AssetKind.Material   => MaterialsFolder,
        AssetKind.Audio      => AudioFolder,
        AssetKind.Script     => ScriptsFolder,
        AssetKind.Shader     => ShadersFolder,
        AssetKind.Object     => ObjectsFolder,
        AssetKind.Model      => ModelsFolder,
        AssetKind.Room       => RoomsFolder,
        AssetKind.Terrain    => TerrainFolder,
        AssetKind.Particle   => ParticlesFolder,
        AssetKind.Physics    => PhysicsFolder,
        AssetKind.Note       => NotesFolder,
        AssetKind.Pathing => "Paths",
        AssetKind.UserInterface => "User Interfaces",
        AssetKind.TerrainEntity => "Terrain",
        _ => null,
    };

    /// <summary>Singular label stored in <c>.meta</c> <c>type</c> (e.g. <c>Sprite → "Sprite"</c>).</summary>
    public static string TypeLabel(AssetKind kind) => kind switch
    {
        AssetKind.Sprite     => "Sprite",
        AssetKind.Background => "Background",
        AssetKind.Texture    => "Texture",
        AssetKind.Material   => "Material",
        AssetKind.Audio      => "Audio",
        AssetKind.Script     => "Script",
        AssetKind.Shader     => "Shader",
        AssetKind.Object     => "Object",
        AssetKind.Model      => "Model",
        AssetKind.Room       => "Room",
        AssetKind.Terrain    => "Terrain",
        AssetKind.Particle   => "Particle",
        AssetKind.Physics    => "Physics",
        AssetKind.Note       => "Note",
        AssetKind.Pathing => "Pathing",
        AssetKind.UserInterface => "UserInterface",
        AssetKind.TerrainEntity => "TerrainEntity",
        _ => null,
    };

    /// <summary>Parse a plural folder name into a kind (case-insensitive). Returns <c>Unknown</c> if unrecognized.</summary>
    public static AssetKind FromFolderName(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return AssetKind.Unknown;
        return folder.ToLowerInvariant() switch
        {
            "sprites"     => AssetKind.Sprite,
            "backgrounds" => AssetKind.Background,
            "textures"    => AssetKind.Texture,
            "materials"   => AssetKind.Material,
            "audio"       => AssetKind.Audio,
            "scripts"     => AssetKind.Script,
            "shaders"     => AssetKind.Shader,
            "objects"     => AssetKind.Object,
            "models"      => AssetKind.Model,
            "rooms"       => AssetKind.Room,
            "terrain"     => AssetKind.Terrain,
            "particles"   => AssetKind.Particle,
            "physics"     => AssetKind.Physics,
            "notes"       => AssetKind.Note,
            "paths" => AssetKind.Pathing,
            "user interfaces" or "ui" => AssetKind.UserInterface,
            _ => AssetKind.Unknown,
        };
    }

    /// <summary>Parse a singular <c>.meta</c> type label into a kind (case-insensitive). Returns <c>Unknown</c> if unrecognized.</summary>
    public static AssetKind FromTypeLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return AssetKind.Unknown;
        return label.ToLowerInvariant() switch
        {
            "sprite" or "image" => AssetKind.Sprite,
            "background"=> AssetKind.Background,
            "texture"   => AssetKind.Texture,
            "material"  => AssetKind.Material,
            "audio"     => AssetKind.Audio,
            "script" or "pgslscript" => AssetKind.Script,
            "shader"    => AssetKind.Shader,
            "object" or "gameobject" => AssetKind.Object,
            "model"     => AssetKind.Model,
            "room"      => AssetKind.Room,
            "terrain"   => AssetKind.Terrain,
            "particle"  => AssetKind.Particle,
            // Tolerate the legacy "Physic" typo produced by the old TrimEnd('s') heuristic.
            "physics"   => AssetKind.Physics,
            "physic"    => AssetKind.Physics,
            "note"      => AssetKind.Note,
            "pathing" => AssetKind.Pathing,
            "userinterface" => AssetKind.UserInterface,
            "terrainentity" => AssetKind.TerrainEntity,
            _ => AssetKind.Unknown,
        };
    }
}

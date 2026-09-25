namespace Genesis.Application.Core.Projects.Templates;

/// <summary>One project template offered in the hub's Templates section.</summary>
/// <param name="Id">Value passed to <see cref="ProjectService.CreateProject"/>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Tagline">One line describing what you get.</param>
/// <param name="Glyph">Icon character for the card.</param>
/// <param name="Dimension">Whether the starter targets the 2D or 3D workflow.</param>
/// <param name="Artwork">The gallery artwork used to explain the starter at a glance.</param>
/// <param name="Contents">What the template creates, listed for the card body.</param>
/// <param name="Available">
/// False for templates that are designed but not built. The Project Hub deliberately shows only
/// available templates; roadmap concepts remain here for planning without occupying creation UI.
/// </param>
public sealed record ProjectTemplate(
    string Id,
    string Name,
    string Tagline,
    string Glyph,
    ProjectTemplateDimension Dimension,
    ProjectTemplateArtwork Artwork,
    IReadOnlyList<string> Contents,
    bool Available);

public enum ProjectTemplateDimension
{
    TwoD,
    ThreeD,
}

public enum ProjectTemplateArtwork
{
    Blank,
    Platformer,
    DungeonCrawler,
    NatureWalk,
    World,
    Voxel,
}

/// <summary>
/// The templates the app can create.
/// </summary>
/// <remarks>
/// Single source of truth for the hub's Templates gallery and for
/// <see cref="ProjectService.CreateProject"/>. Keeping them together means the gallery cannot offer
/// a template the service does not implement — the same one-source discipline applied to the audio
/// schema (NEXT-041) and the object event set.
///
/// Note that "Blank" is deliberately not in this list: a blank project is what **New Project**
/// makes, and the Templates gallery is for starting points with content in them.
/// </remarks>
public static class ProjectTemplateCatalog
{
    private static readonly ProjectTemplate[] Templates =
    [
        new(
            "LuigisMansion", "Luigi's Mansion: A Light in the Dark", "Six haunted chapters; native PGSL fan-game template", "◆",
            ProjectTemplateDimension.TwoD, ProjectTemplateArtwork.DungeonCrawler,
            ["Luigi's running start, torch aiming and flash-to-stun ghost capture",
             "Poltergust tug-of-war, treasure, candle wards and portrait finale",
             "Shadow-tested 2D lights and smooth, editable particle effects",
             "Supplied original artwork/audio, asset archive and chapter saves"],
            Available: true),

        new(
            "2DShowcase", "Mushroom Meadow", "SNES-style 2D authoring showcase", "▦",
            ProjectTemplateDimension.TwoD, ProjectTemplateArtwork.Platformer,
            ["Editable pixel sprites, tiles, masks and PGSL objects",
             "Swept tile collision, coins, enemies and question blocks",
             "Checkpoint, lives, pause, restart and level completion",
             "Original audio, responsive HUD and an authoring guide"],
            Available: true),

        new(
            "2D",
            "2D Platformer",
            "A complete, playable side-scroller",
            "▦",
            ProjectTemplateDimension.TwoD,
            ProjectTemplateArtwork.Platformer,
            [
                "Animated player, collisions and follow camera",
                "Tiles, coins, particles and input audio",
                "Editable PGSL events and all twelve alarms",
                "Ready-to-run authored 2D room",
            ],
            Available: true),

        new(
            "2DDungeonCrawler",
            "Crypts of Genesis",
            "Preview: a top-down action template in development",
            "◆",
            ProjectTemplateDimension.TwoD,
            ProjectTemplateArtwork.DungeonCrawler,
            [
                "Responsive movement, combat, enemies and boss",
                "Seeded 5–8 room dungeon map and room flow",
                "Pixel art, audio, particles, shaders and HUD",
                "Five ready-to-run editable 2D rooms",
            ],
            Available: true),

        new(
            "3DNatureWalk",
            "3D Nature Walk",
            "Verdant Hollow: a kilometre of authored woodland, lakes and trails",
            "▲",
            ProjectTemplateDimension.ThreeD,
            ProjectTemplateArtwork.NatureWalk,
            [
                "Painted terrain, four trails and four swimmable lakes",
                "72,000 authored, streamed vegetation instances",
                "Landmarks, campfire, lanterns, weather and effects",
                "First-person PGSL, collision, buoyancy and swimming",
            ],
            Available: true),

        new(
            "3D",
            "3D World",
            "A practical first-person world-building starter",
            "◈",
            ProjectTemplateDimension.ThreeD,
            ProjectTemplateArtwork.World,
            [
                "Editable terrain, forest props + room",
                "Campfire: model, shader, particles, light + audio",
                "First-person controller + terrain physics",
                "Instanced foliage + visible performance budget",
            ],
            Available: true),

        new(
            "Voxel",
            "3D Voxel World",
            "A chunked, generated world",
            "▩",
            ProjectTemplateDimension.ThreeD,
            ProjectTemplateArtwork.Voxel,
            [
                "Procedural chunk generation",
                "First-person controller",
                "Block placement and removal",
            ],
            Available: false),
    ];

    /// <summary>Every template, including those not yet built.</summary>
    public static IReadOnlyList<ProjectTemplate> All => Templates;

    /// <summary>Templates that can actually be created today.</summary>
    public static IReadOnlyList<ProjectTemplate> Available =>
        [.. Templates.Where(template => template.Available)];

    /// <summary>Find a template by id, or null.</summary>
    public static ProjectTemplate? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Templates.FirstOrDefault(template =>
                string.Equals(template.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the id names a template that is implemented.</summary>
    public static bool CanCreate(string? id) => Find(id)?.Available == true;
}

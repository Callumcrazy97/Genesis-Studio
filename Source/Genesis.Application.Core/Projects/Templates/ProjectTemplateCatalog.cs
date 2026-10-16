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
    Marble,
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
///
/// 16 Oct 2026 purge: galleries offer Mushroom Meadow (2D) and Tumble Isles (3D) only. The other
/// starters are kept unavailable rather than deleted: <see cref="ProjectService.CreateProject"/>
/// still builds them by id for the regression suites, but no gallery shows them.
/// </remarks>
public static class ProjectTemplateCatalog
{
    private static readonly ProjectTemplate[] Templates =
    [
        new(
            "2DShowcase", "Mushroom Meadow", "A complete SNES-style 2D adventure", "▦",
            ProjectTemplateDimension.TwoD, ProjectTemplateArtwork.Platformer,
            ["Five levels, two bosses, a village hub and an overworld map",
             "Intro cinematic, main menu, three save slots, options and credits",
             "Hearts, power-ups, swimming, wind, dark caves and gamepad play in PGSL",
             "Original pixel art, music and sound, and an authoring guide"],
            Available: true),

        new(
            TumbleIslesTemplate.TemplateId, "Tumble Isles", "A bright 3D marble-rolling game to learn from", "●",
            ProjectTemplateDimension.ThreeD, ProjectTemplateArtwork.Marble,
            ["Five floating-isle courses: bumpers, moving decks, boost pads, springs and ice",
             "Rolling physics, camera and HUD in one readable PGSL object",
             "Gems, checkpoints, best times and course unlocks saved",
             "Original sound and music; keyboard, mouse or gamepad"],
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
            Available: false),

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
            Available: false),

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
            Available: false),

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
            Available: false),

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

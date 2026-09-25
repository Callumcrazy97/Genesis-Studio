namespace Genesis.Application.Core.Resources;

// ════════════════════════════════════════════════════════════════════════════════
//   T1 consolidation — one kind per *thing*, not per *use*.
//
//   Removed: Sprite, TileSet, Material (all folded into Image, distinguished by
//   ImageUsage flags) and VoxelPalette. UserInterface is a portable layout resource;
//   PGSL can render and modify it during Draw GUI events.
//
//   Clean break, by decision: there is no migration path. Projects authored before
//   this change do not open.
// ════════════════════════════════════════════════════════════════════════════════

public enum ResourceKind
{
    Unknown,
    Folder,
    /// <summary>Sprites, tile sets, backgrounds and model textures — one type, usage flags.</summary>
    Image,
    Audio,
    Shader,
    PgslScript,
    GameObject,
    Room,
    Model,
    Particle,
    Physics,
    Terrain,
    TerrainEntity,
    Pathing,
    UserInterface,
    Note,
}


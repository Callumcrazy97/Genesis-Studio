using System.Drawing;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.UI;

/// <summary>Icon, colour and one-line purpose for each resource kind, shared by Home and the editors.</summary>
/// <remarks>
/// The purpose lines are written for someone who has never made a game: what the thing is for, not
/// what the file contains. The colours give each kind a stable hue so cards can be scanned by colour.
/// </remarks>
public static class ResourceKindVisuals
{
    public static string Glyph(ResourceKind kind) => kind switch
    {
        ResourceKind.Image => UiGlyphs.Picture,
        ResourceKind.Audio => UiGlyphs.Audio,
        ResourceKind.Shader => UiGlyphs.Color,
        ResourceKind.PgslScript => UiGlyphs.Code,
        ResourceKind.GameObject => UiGlyphs.Puzzle,
        ResourceKind.Room => UiGlyphs.Floor,
        ResourceKind.Model => UiGlyphs.Cube,
        ResourceKind.Particle => UiGlyphs.Sparkle,
        ResourceKind.Physics => UiGlyphs.Flask,
        ResourceKind.Terrain => UiGlyphs.Mountain,
        ResourceKind.TerrainEntity => UiGlyphs.Leaf,
        ResourceKind.Pathing => UiGlyphs.Map,
        ResourceKind.UserInterface => UiGlyphs.Monitor,
        ResourceKind.Note => UiGlyphs.Document,
        ResourceKind.Folder => UiGlyphs.Folder,
        _ => UiGlyphs.Document,
    };

    public static Color Swatch(ResourceKind kind) => kind switch
    {
        ResourceKind.Image => UiTokens.FromHex("#E0668F"),
        ResourceKind.Audio => UiTokens.FromHex("#B07CF2"),
        ResourceKind.Shader => UiTokens.FromHex("#F28C4B"),
        ResourceKind.PgslScript => UiTokens.FromHex("#5E9CF0"),
        ResourceKind.GameObject => UiTokens.FromHex("#6C8CFF"),
        ResourceKind.Room => UiTokens.FromHex("#4FB6A5"),
        ResourceKind.Model => UiTokens.FromHex("#8E86F5"),
        ResourceKind.Particle => UiTokens.FromHex("#F2B84B"),
        ResourceKind.Physics => UiTokens.FromHex("#58B8D8"),
        ResourceKind.Terrain => UiTokens.FromHex("#6DBE6A"),
        ResourceKind.TerrainEntity => UiTokens.FromHex("#8CC265"),
        ResourceKind.Pathing => UiTokens.FromHex("#D98A5C"),
        ResourceKind.UserInterface => UiTokens.FromHex("#7AA5C9"),
        ResourceKind.Note => UiTokens.FromHex("#9DA6BB"),
        _ => UiTokens.Accent,
    };

    public static string Purpose(ResourceKind kind) => kind switch
    {
        ResourceKind.Image => "Sprites, tiles, backgrounds and textures.",
        ResourceKind.Audio => "Music and sound effects.",
        ResourceKind.Shader => "A custom look for images, models or the whole screen.",
        ResourceKind.PgslScript => "Game code shared between Objects.",
        ResourceKind.GameObject => "Anything in your game that moves, reacts or can be collected.",
        ResourceKind.Room => "A level: place Objects, lights and cameras.",
        ResourceKind.Model => "3D models, rigs and animations.",
        ResourceKind.Particle => "Smoke, fire, sparks, rain and magic.",
        ResourceKind.Physics => "How things bounce, slide, float and collide.",
        ResourceKind.Terrain => "Sculpted and painted 3D landscapes.",
        ResourceKind.TerrainEntity => "Trees, rocks and plants to scatter on terrain.",
        ResourceKind.Pathing => "Routes for moving platforms, enemies and cameras.",
        ResourceKind.UserInterface => "Menus, HUDs, health bars and buttons.",
        ResourceKind.Note => "Design notes, ideas and to-do lists.",
        _ => string.Empty,
    };

    /// <summary>The group a kind belongs to on create galleries.</summary>
    public static string Category(ResourceKind kind) => kind switch
    {
        ResourceKind.Image or ResourceKind.Audio or ResourceKind.Model or ResourceKind.Shader
            or ResourceKind.Particle => "Art and sound",
        ResourceKind.Room or ResourceKind.Terrain or ResourceKind.TerrainEntity or ResourceKind.Pathing
            or ResourceKind.Physics => "Worlds",
        _ => "Logic and interface",
    };
}

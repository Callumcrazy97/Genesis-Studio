using Genesis.Application.Core.UI;
using Genesis.Physics;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class PhysicsEditorControl
{
    private StarterGallery? _physicsPresetGallery;

    /// <summary>The "What is it?" cards: materials, worlds and preview-only playgrounds.</summary>
    public StarterGallery? PhysicsPresetGallery => _physicsPresetGallery;

    /// <summary>
    /// Presets grouped by what they are for. Materials and worlds are what a game uses; the test
    /// playgrounds (planets, ragdolls, stress tests) only demonstrate the simulator in this preview,
    /// so they say so instead of looking like things you can ship.
    /// </summary>
    private StarterGallery BuildPhysicsPresetGallery()
    {
        _physicsPresetGallery = new StarterGallery("PhysicsPresetGallery")
        {
            Compact = true,
            Dock = DockStyle.Fill,
        };
        _physicsPresetGallery.SetItems(PhysicsScenePresets.Names.Select(name =>
        {
            (string category, string glyph, string swatch) = PresetStyle(name);
            bool previewOnly = category == PlaygroundCategory;
            return new StarterItem(name, name, PhysicsScenePresets.Create(name).Notes)
            {
                Category = category,
                Glyph = glyph,
                Swatch = UiTokens.FromHex(swatch),
                Badge = previewOnly ? "Preview only" : string.Empty,
                BadgeTone = StarterBadgeTone.Warning,
            };
        }));
        _physicsPresetGallery.ItemChosen += (_, item) => ApplyPreset(item.Id);
        return _physicsPresetGallery;
    }

    private const string MaterialCategory = "Materials";
    private const string WorldCategory = "Worlds and gravity";
    private const string PlaygroundCategory = "Test playgrounds";

    private static (string Category, string Glyph, string Swatch) PresetStyle(string name) => name switch
    {
        "Heavy Rock" => (MaterialCategory, UiGlyphs.Mountain, "#8C8A86"),
        "Rubber Ball" => (MaterialCategory, UiGlyphs.Refresh, "#E0668F"),
        "Slick Ice" => (MaterialCategory, UiGlyphs.Snow, "#9ED0EC"),
        "Hard Wood" => (MaterialCategory, UiGlyphs.Leaf, "#A87545"),
        "Steel" => (MaterialCategory, UiGlyphs.Shield, "#7A8699"),
        "Default" => (MaterialCategory, UiGlyphs.Cube, "#6C8CFF"),
        "Water Basin" or "Underwater" => (WorldCategory, UiGlyphs.Drop, "#4F8FD8"),
        "Zero-G" or "Moon" or "Jupiter" => (WorldCategory, UiGlyphs.Moon, "#8E86F5"),
        "Wind Tunnel" => (WorldCategory, UiGlyphs.Waves, "#58B8D8"),
        "2D Platformer" => (WorldCategory, UiGlyphs.Game, "#4FB6A5"),
        _ => (PlaygroundCategory, UiGlyphs.Flask, "#F2B84B"),
    };
}

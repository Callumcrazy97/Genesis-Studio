using System.Drawing;
using System.Drawing.Imaging;
using Genesis.Application.Core.UI;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private static readonly Dictionary<TerrainPreset, Bitmap> LandformThumbnails = [];
    private static readonly object LandformThumbnailLock = new();
    private StarterGallery? _landforms;

    /// <summary>The Shape step's landform cards (one click replaces the terrain, undoably).</summary>
    public StarterGallery? LandformGallery => _landforms;

    /// <summary>
    /// The ten landforms, shown as cards with a shaded relief of each, directly on the Create page.
    /// </summary>
    /// <remarks>
    /// They were only reachable through Create terrain… → a modal wizard. The wizard stays for
    /// resolution, seed and processes; the cards are the one-click path.
    /// </remarks>
    private StarterGallery BuildLandformGallery()
    {
        _landforms = new StarterGallery("TerrainLandforms")
        {
            Compact = true,
            FitsContent = true,
            Margin = new System.Windows.Forms.Padding(0, 0, 0, 8),
        };
        _landforms.SetItems(Enum.GetValues<TerrainPreset>().Select(preset => new StarterItem(
            preset.ToString(),
            LandformTitle(preset),
            TerrainGenerator.Describe(preset))
        {
            Thumbnail = LandformThumbnail(preset),
            Swatch = UiTokens.FromHex("#6DBE6A"),
        }));
        if (Enum.TryParse(_settings.Preset, out TerrainPreset current)) _landforms.SelectedId = current.ToString();
        _landforms.ItemChosen += (_, item) =>
        {
            if (Enum.TryParse(item.Id, out TerrainPreset preset)) ApplyLandform(preset);
        };
        return _landforms;
    }

    /// <summary>Regenerates the terrain as <paramref name="preset"/>, keeping its size and seed. Undoable.</summary>
    public void ApplyLandform(TerrainPreset preset)
    {
        TerrainGenParams parameters = new()
        {
            ResolutionX = _settings.Resolution is { Length: >= 2 } ? _settings.Resolution[0] : 129,
            ResolutionZ = _settings.Resolution is { Length: >= 2 } ? _settings.Resolution[1] : 129,
            CellSize = _settings.CellSize,
            MinHeight = _settings.MinHeight,
            MaxHeight = _settings.MaxHeight,
            Preset = preset,
            Seed = _settings.Seed,
            ErosionIterations = _settings.ErosionIterations,
            ErosionStrength = _settings.ErosionStrength,
            TerraceStrength = _settings.TerraceStrength,
            TerraceSteps = _settings.TerraceSteps,
            RiverCount = _settings.RiverCount,
            RiverDepth = _settings.RiverDepth,
        };
        ApplyGeneration(parameters);
        if (_landforms is not null) _landforms.SelectedId = preset.ToString();
        FrameTerrain();
        _terrainWorkflow?.RefreshProgress();
    }

    private static string LandformTitle(TerrainPreset preset) => preset switch
    {
        TerrainPreset.Flatlands => "Flatlands",
        TerrainPreset.RollingHills => "Rolling hills",
        TerrainPreset.Hills => "Hills",
        TerrainPreset.Mountains => "Mountains",
        TerrainPreset.ErodedMountains => "Worn mountains",
        TerrainPreset.RidgeValleys => "Ridges and valleys",
        TerrainPreset.Badlands => "Badlands",
        TerrainPreset.Volcanic => "Volcano",
        TerrainPreset.Islands => "Islands",
        TerrainPreset.Canyon => "Canyon",
        _ => preset.ToString(),
    };

    /// <summary>A small shaded relief of a landform, generated once from the real generator.</summary>
    private static Bitmap LandformThumbnail(TerrainPreset preset)
    {
        lock (LandformThumbnailLock)
        {
            if (LandformThumbnails.TryGetValue(preset, out Bitmap? cached)) return cached;
            const int columns = 48, rows = 30, scale = 2;
            // Cover the same world span as a default 129-cell terrain so shapes match what you get.
            TerrainAsset asset = TerrainGenerator.Generate(new TerrainGenParams
            {
                ResolutionX = columns,
                ResolutionZ = rows,
                CellSize = 129f / columns,
                Preset = preset,
            });
            float min = float.MaxValue, max = float.MinValue;
            for (int z = 0; z < rows; z++)
            for (int x = 0; x < columns; x++)
            {
                float h = asset.GetHeight(x, z);
                min = Math.Min(min, h);
                max = Math.Max(max, h);
            }

            float span = Math.Max(0.001f, max - min);
            Bitmap bitmap = new(columns * scale, rows * scale, PixelFormat.Format32bppArgb);
            for (int z = 0; z < rows; z++)
            for (int x = 0; x < columns; x++)
            {
                float h = asset.GetHeight(x, z);
                float t = (h - min) / span;
                // Light from the upper left: compare with the neighbour below-right.
                float slope = asset.GetHeight(Math.Min(columns - 1, x + 1), Math.Min(rows - 1, z + 1)) - h;
                float shade = Math.Clamp(1f - slope / span * 6f, 0.55f, 1.25f);
                Color colour = ReliefColour(preset, h, t);
                Color shaded = Color.FromArgb(
                    Math.Clamp((int)(colour.R * shade), 0, 255),
                    Math.Clamp((int)(colour.G * shade), 0, 255),
                    Math.Clamp((int)(colour.B * shade), 0, 255));
                for (int dy = 0; dy < scale; dy++)
                for (int dx = 0; dx < scale; dx++)
                    bitmap.SetPixel(x * scale + dx, z * scale + dy, shaded);
            }

            LandformThumbnails[preset] = bitmap;
            return bitmap;
        }
    }

    private static Color ReliefColour(TerrainPreset preset, float height, float t)
    {
        if (height < 0f && preset is TerrainPreset.Islands) return Color.FromArgb(52, 112, 168);
        if (preset is TerrainPreset.Badlands or TerrainPreset.Canyon)
            return Lerp(Color.FromArgb(176, 112, 70), Color.FromArgb(222, 170, 118), t);
        if (preset is TerrainPreset.Volcanic)
            return t > 0.85f ? Color.FromArgb(120, 60, 50) : Lerp(Color.FromArgb(70, 66, 70), Color.FromArgb(120, 112, 108), t);
        if (t < 0.55f) return Lerp(Color.FromArgb(76, 128, 62), Color.FromArgb(120, 160, 80), t / 0.55f);
        if (t < 0.85f) return Lerp(Color.FromArgb(120, 150, 86), Color.FromArgb(128, 120, 112), (t - 0.55f) / 0.3f);
        return Lerp(Color.FromArgb(150, 145, 140), Color.FromArgb(238, 240, 245), (t - 0.85f) / 0.15f);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}

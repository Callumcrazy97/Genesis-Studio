using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Genesis.World.Foliage;

/// <summary>
/// Grass grown from a rule around the camera instead of stored for the whole terrain. Square cells
/// near the camera are filled with tufts as it moves, from how much of each painted layer lies under
/// each spot, and handed back for reuse once the camera has left them behind. Off unless switched
/// on, so a terrain saved before this existed looks exactly as it did.
/// </summary>
public sealed class TerrainGrassRuleSettings
{
    /// <summary>Painted layers a rule can name a density for.</summary>
    public const int LayerCount = TerrainGrassLayerWeights.LayerCount;

    [Description("Grow grass around the camera from this rule. Off by default.")]
    public bool Enabled { get; set; }

    [Description("Distance from the camera, in metres, that grass is grown and drawn to.")]
    public float Radius { get; set; } = 100f;

    [Description("Distance between neighbouring tufts where a layer's density is 1, in metres.")]
    public float Spacing { get; set; } = 0.5f;

    [Description("Fraction of the radius that keeps full density; beyond it the grass thins out to nothing at the radius.")]
    public float FullDensityFraction { get; set; } = 0.45f;

    [Description("Tufts nearer than this, in metres, draw with the detailed blade mesh; further ones with the simple one.")]
    public float NearDistance { get; set; } = 30f;

    [Description("How far a tuft may wander from its lattice point, as a fraction of the spacing.")]
    public float Jitter { get; set; } = 0.9f;

    public float MinimumScale { get; set; } = 0.75f;
    public float MaximumScale { get; set; } = 1.2f;

    [Description("Steepest ground, in degrees, grass grows on.")]
    public float MaximumSlopeDegrees { get; set; } = 40f;

    [Description("Which tuft to grow. Meadow grass looks like the scattered grass.")]
    public FoliageSpecies Species { get; set; } = FoliageSpecies.MeadowGrass;

    public int Seed { get; set; } = 1;

    [Description("Side of the square cells grass is grown and recycled in, in metres.")]
    public float CellSize { get; set; } = 8f;

    [Description("Most cells grown in one frame, so walking never holds a frame up.")]
    public int CellsPerFrame { get; set; } = 8;

    [Description("Most tufts drawn in one frame; nearest cells are drawn first.")]
    public int MaximumDrawnTufts { get; set; } = 16000;

    /// <summary>Tufts per lattice point on each painted layer, 0 to 1, in layer order.</summary>
    [Browsable(false)]
    public float[] LayerDensities { get; set; } = { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };

    [JsonIgnore, DisplayName("Layer 1 density")] public float Layer1Density { get => Density(0); set => SetDensity(0, value); }
    [JsonIgnore, DisplayName("Layer 2 density")] public float Layer2Density { get => Density(1); set => SetDensity(1, value); }
    [JsonIgnore, DisplayName("Layer 3 density")] public float Layer3Density { get => Density(2); set => SetDensity(2, value); }
    [JsonIgnore, DisplayName("Layer 4 density")] public float Layer4Density { get => Density(3); set => SetDensity(3, value); }
    [JsonIgnore, DisplayName("Layer 5 density")] public float Layer5Density { get => Density(4); set => SetDensity(4, value); }
    [JsonIgnore, DisplayName("Layer 6 density")] public float Layer6Density { get => Density(5); set => SetDensity(5, value); }
    [JsonIgnore, DisplayName("Layer 7 density")] public float Layer7Density { get => Density(6); set => SetDensity(6, value); }
    [JsonIgnore, DisplayName("Layer 8 density")] public float Layer8Density { get => Density(7); set => SetDensity(7, value); }

    /// <summary>The density for one painted layer; 0 for a layer the rule does not name.</summary>
    public float Density(int layer) =>
        LayerDensities != null && layer >= 0 && layer < LayerDensities.Length ? LayerDensities[layer] : 0f;

    public void SetDensity(int layer, float value)
    {
        if (layer < 0 || layer >= LayerCount) return;
        if (LayerDensities == null || LayerDensities.Length < LayerCount)
        {
            float[] grown = new float[LayerCount];
            LayerDensities?.AsSpan(0, Math.Min(LayerDensities.Length, LayerCount)).CopyTo(grown);
            LayerDensities = grown;
        }

        LayerDensities[layer] = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    public void Normalize()
    {
        Radius = Clamp(Radius, 4f, 2000f, 100f);
        Spacing = Clamp(Spacing, 0.1f, 16f, 0.5f);
        FullDensityFraction = Clamp(FullDensityFraction, 0f, 1f, 0.45f);
        NearDistance = Clamp(NearDistance, 0f, 2000f, 30f);
        Jitter = Clamp(Jitter, 0f, 1f, 0.9f);
        MinimumScale = Clamp(MinimumScale, 0.05f, 16f, 0.75f);
        MaximumScale = Clamp(MaximumScale, MinimumScale, 16f, MinimumScale);
        MaximumSlopeDegrees = Clamp(MaximumSlopeDegrees, 0f, 90f, 40f);
        if (!Enum.IsDefined(Species)) Species = FoliageSpecies.MeadowGrass;
        CellSize = Clamp(CellSize, 2f, 64f, 8f);
        // A cell holds at most 128 x 128 tufts, so a tiny spacing cannot make one cell enormous.
        Spacing = MathF.Max(Spacing, CellSize / 128f);
        CellsPerFrame = Math.Clamp(CellsPerFrame, 1, 256);
        MaximumDrawnTufts = Math.Clamp(MaximumDrawnTufts, 0, 32768);

        float[] densities = new float[LayerCount];
        for (int layer = 0; layer < LayerCount; layer++)
        {
            float value = Density(layer);
            densities[layer] = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
        }

        LayerDensities = densities;
    }

    /// <summary>A copy that shares nothing with this one.</summary>
    public TerrainGrassRuleSettings Clone()
    {
        var copy = (TerrainGrassRuleSettings)MemberwiseClone();
        copy.LayerDensities = (float[])LayerDensities?.Clone() ?? new float[LayerCount];
        return copy;
    }

    /// <summary>True when the two rules grow the same grass.</summary>
    public bool SameAs(TerrainGrassRuleSettings other)
    {
        if (other == null) return false;
        if (Enabled != other.Enabled || Radius != other.Radius || Spacing != other.Spacing
            || FullDensityFraction != other.FullDensityFraction || NearDistance != other.NearDistance
            || Jitter != other.Jitter || MinimumScale != other.MinimumScale || MaximumScale != other.MaximumScale
            || MaximumSlopeDegrees != other.MaximumSlopeDegrees || Species != other.Species || Seed != other.Seed
            || CellSize != other.CellSize || CellsPerFrame != other.CellsPerFrame || MaximumDrawnTufts != other.MaximumDrawnTufts)
            return false;
        for (int layer = 0; layer < LayerCount; layer++)
            if (Density(layer) != other.Density(layer)) return false;
        return true;
    }

    /// <summary>Tufts along one side of a cell.</summary>
    [JsonIgnore, Browsable(false)]
    public int TuftsPerCellSide => Math.Max(1, (int)MathF.Floor(CellSize / MathF.Max(Spacing, 0.01f) + 0.001f));

    private static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

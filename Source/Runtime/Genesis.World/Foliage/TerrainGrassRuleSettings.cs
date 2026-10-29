using System;
using System.ComponentModel;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Genesis.World.Foliage;

/// <summary>
/// Grass grown from a rule around the camera instead of stored for the whole terrain. Square cells
/// near the camera are filled with tufts as it moves, from how much of each painted layer lies under
/// each spot, and handed back for reuse once the camera has left them behind. Off unless switched
/// on, so a terrain saved before this existed looks exactly as it did.
/// </summary>
/// <remarks>
/// A new rule's values are the built-in meadow grass (the Nature Walk's look, 28 October): switched
/// on with nothing else changed, it gives that grass. The blade colours, wind, parting and paths
/// came later; a rule saved before them keeps its tuft's own colours (see <see cref="OnDeserializing"/>).
/// </remarks>
public sealed class TerrainGrassRuleSettings : IJsonOnDeserializing, IJsonOnDeserialized
{
    /// <summary>Painted layers a rule can name a density for.</summary>
    public const int LayerCount = TerrainGrassLayerWeights.LayerCount;

    // 29 Oct (T7): darker and warmer than the first values (root 0.10, 0.22, 0.06; tip 0.27, 0.62,
    // 0.17; curve 0.8). Under the sunny sky those rendered the meadow about 1.5 times as bright as
    // the reference picture, lime rather than olive green, with no dark roots. Rules already saved
    // keep the colours they were saved with. The ground colour stays: it is what distant hills show
    // where the grass has thinned out, and they already matched.

    /// <summary>The built-in grass's colour at the root of each blade (a deep, shaded meadow green).</summary>
    public static readonly Vector3 DefaultBaseColor = new(0.08f, 0.15f, 0.05f);

    /// <summary>The built-in grass's colour at the tip of each blade (a sunlit, slightly olive meadow green).</summary>
    public static readonly Vector3 DefaultTipColor = new(0.27f, 0.46f, 0.18f);

    /// <summary>The built-in grass's colour curve: the dark root colour climbs a little way up the blade.</summary>
    public const float DefaultColorCurve = 1.25f;

    /// <summary>The colour of a painted Grass layer that suits the built-in grass: the ground between its tufts.</summary>
    public static readonly Vector3 DefaultGroundColor = new(0.22f, 0.38f, 0.11f);

    [Description("Grow grass around the camera from this rule. Off by default.")]
    public bool Enabled { get; set; }

    [Description("Distance from the camera, in metres, that grass is grown and drawn to.")]
    public float Radius { get; set; } = 90f;

    [Description("Distance between neighbouring tufts where a layer's density is 1, in metres.")]
    public float Spacing { get; set; } = 0.35f;

    [Description("Fraction of the radius that keeps full density; beyond it the grass thins out to nothing at the radius.")]
    public float FullDensityFraction { get; set; } = 0.45f;

    // Named apart from the scatter settings' fields, which share a page and are found by name.
    [Description("Tufts nearer than this, in metres, draw with the detailed blade mesh; further ones with the simple one.")]
    public float DetailDistance { get; set; } = 30f;

    [Description("How far a tuft may wander from its lattice point, as a fraction of the spacing.")]
    public float Jitter { get; set; } = 0.9f;

    public float MinimumScale { get; set; } = 0.6f;
    public float MaximumScale { get; set; } = 1f;

    [Description("Steepest ground, in degrees, grass grows on.")]
    public float SteepestSlopeDegrees { get; set; } = 40f;

    [Description("Which tuft to grow. Meadow grass looks like the scattered grass.")]
    public FoliageSpecies Species { get; set; } = FoliageSpecies.MeadowGrass;

    [Description("Picks the pattern: where each tuft stands, its size, turn and tone.")]
    public int PatternSeed { get; set; } = 1;

    // ── The grass shader: blade colour, texture and wind ───────────────────────────

    [Description("Colour each blade from the root colour to the tip colour. Off, the tuft keeps its own colours.")]
    public bool ColorEnabled
    {
        get => _colorEnabled;
        set
        {
            _colorEnabled = value;
            _colorRead = true;
        }
    }

    private bool _colorEnabled = true;

    [Description("The blades' colour at the root, red, green and blue from 0 to 1.")]
    public Vector3 BaseColor { get; set; } = DefaultBaseColor;

    [Description("The blades' colour at the tip, red, green and blue from 0 to 1.")]
    public Vector3 TipColor { get; set; } = DefaultTipColor;

    [Description("How the colour runs up a blade: 1 evenly, below 1 the tip colour reaches further down, above 1 it keeps to the tips (0.1 to 8).")]
    public float ColorCurve { get; set; } = DefaultColorCurve;

    [Description("An Image drawn on each blade, tinted by the colours above; its see-through parts cut the blade's shape. Empty for plain blades.")]
    public string Texture { get; set; } = "";

    [Description("Sway the blades in the wind (the weather's wind, when the room has weather).")]
    public bool WindEnabled { get; set; } = true;

    [Description("How far the blades sway, 0 to 4 (1 is a breeze).")]
    public float WindStrength { get; set; } = 1f;

    [Description("How fast the blades sway, 0 to 4.")]
    public float WindSpeed { get; set; } = 1f;

    [Description("Gusts rolling across the field, 0 (none) to 1 (strong), leaning the grass as they pass.")]
    public float WindGusts { get; set; }

    // ── Parting and paths ──────────────────────────────────────────────────────────

    [Description("Grass bends away from animated characters and from pushers a game sets (Engine.Terrain.Grass.Pushers), and springs back after.")]
    public bool PushersEnabled { get; set; } = true;

    [Description("How far pushed grass bends, 0 to 1 (1 lays it nearly flat).")]
    public float PushStrength { get; set; } = 0.8f;

    [Description("Seconds pushed grass takes to stand up again.")]
    public float SpringBackSeconds { get; set; } = 0.8f;

    [Description("Keep grass off the terrain's paths (the Paths step), thinning in at their edges.")]
    public bool ClearPaths { get; set; } = true;

    [Description("Metres over which grass thins in at a path's edge.")]
    public float PathEdge { get; set; } = 0.6f;

    [Description("Side of the square cells grass is grown and recycled in, in metres.")]
    public float CellSize { get; set; } = 8f;

    [Description("Most cells grown in one frame, so walking never holds a frame up.")]
    public int CellsPerFrame { get; set; } = 8;

    [Description("Most time, in milliseconds, one frame spends growing cells; at least one cell grows each frame something is wanted.")]
    public float GenerationBudgetMilliseconds { get; set; } = 1.5f;

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
        Radius = Clamp(Radius, 4f, 2000f, 90f);
        Spacing = Clamp(Spacing, 0.1f, 16f, 0.35f);
        FullDensityFraction = Clamp(FullDensityFraction, 0f, 1f, 0.45f);
        DetailDistance = Clamp(DetailDistance, 0f, 2000f, 30f);
        Jitter = Clamp(Jitter, 0f, 1f, 0.9f);
        MinimumScale = Clamp(MinimumScale, 0.05f, 16f, 0.6f);
        MaximumScale = Clamp(MaximumScale, MinimumScale, 16f, MinimumScale);
        SteepestSlopeDegrees = Clamp(SteepestSlopeDegrees, 0f, 90f, 40f);
        if (!Enum.IsDefined(Species)) Species = FoliageSpecies.MeadowGrass;
        BaseColor = ClampColor(BaseColor, DefaultBaseColor);
        TipColor = ClampColor(TipColor, DefaultTipColor);
        ColorCurve = Clamp(ColorCurve, 0.1f, 8f, DefaultColorCurve);
        Texture = Texture?.Trim() ?? "";
        WindStrength = Clamp(WindStrength, 0f, 4f, 1f);
        WindSpeed = Clamp(WindSpeed, 0f, 4f, 1f);
        WindGusts = Clamp(WindGusts, 0f, 1f, 0f);
        PushStrength = Clamp(PushStrength, 0f, 1f, 0.8f);
        SpringBackSeconds = Clamp(SpringBackSeconds, 0f, 10f, 0.8f);
        PathEdge = Clamp(PathEdge, 0f, 16f, 0.6f);
        CellSize = Clamp(CellSize, 2f, 64f, 8f);
        // A cell holds at most 128 x 128 tufts, so a tiny spacing cannot make one cell enormous.
        Spacing = MathF.Max(Spacing, CellSize / 128f);
        CellsPerFrame = Math.Clamp(CellsPerFrame, 1, 256);
        GenerationBudgetMilliseconds = Clamp(GenerationBudgetMilliseconds, 0.1f, 33f, 1.5f);
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

    /// <summary>
    /// True when the two rules grow the same grass: the same tufts in the same places. The look
    /// (<see cref="SameLookAs"/>) can differ; it changes how they are drawn, not where they grow.
    /// </summary>
    public bool SameAs(TerrainGrassRuleSettings other)
    {
        if (other == null) return false;
        if (Enabled != other.Enabled || Radius != other.Radius || Spacing != other.Spacing
            || FullDensityFraction != other.FullDensityFraction || DetailDistance != other.DetailDistance
            || Jitter != other.Jitter || MinimumScale != other.MinimumScale || MaximumScale != other.MaximumScale
            || SteepestSlopeDegrees != other.SteepestSlopeDegrees || Species != other.Species || PatternSeed != other.PatternSeed
            || CellSize != other.CellSize || CellsPerFrame != other.CellsPerFrame || MaximumDrawnTufts != other.MaximumDrawnTufts
            || GenerationBudgetMilliseconds != other.GenerationBudgetMilliseconds
            || ClearPaths != other.ClearPaths || PathEdge != other.PathEdge)
            return false;
        for (int layer = 0; layer < LayerCount; layer++)
            if (Density(layer) != other.Density(layer)) return false;
        return true;
    }

    /// <summary>True when the two rules draw their grass alike: colours, texture, wind and parting.</summary>
    public bool SameLookAs(TerrainGrassRuleSettings other) =>
        other != null && ColorEnabled == other.ColorEnabled && BaseColor == other.BaseColor && TipColor == other.TipColor
        && ColorCurve == other.ColorCurve && string.Equals(Texture ?? "", other.Texture ?? "", StringComparison.Ordinal)
        && WindEnabled == other.WindEnabled && WindStrength == other.WindStrength && WindSpeed == other.WindSpeed
        && WindGusts == other.WindGusts && PushersEnabled == other.PushersEnabled && PushStrength == other.PushStrength
        && SpringBackSeconds == other.SpringBackSeconds;

    /// <summary>
    /// True for a rule read from a document saved before the grass shader (29 October 2026): it keeps
    /// its tuft's own colours, and <c>terrain.setGrass</c> switching it on starts it as the built-in grass.
    /// </summary>
    [JsonIgnore, Browsable(false)]
    public bool SavedBeforeShader { get; private set; }

    private bool _colorRead;

    /// <summary>A rule read from a document: notes whether the document had the blade colours.</summary>
    public void OnDeserializing() => _colorRead = false;

    /// <summary>A document without the blade colours keeps its tuft's own colours, as it did.</summary>
    public void OnDeserialized()
    {
        SavedBeforeShader = !_colorRead;
        if (SavedBeforeShader) ColorEnabled = false;
    }

    /// <summary>
    /// True for a switched-off rule nobody chose: saved before the grass shader, or still holding the
    /// old stock values (radius 100, spacing 0.5, scale 0.75 to 1.2). Switching grass on starts such
    /// a rule as the built-in grass.
    /// </summary>
    [JsonIgnore, Browsable(false)]
    public bool LooksUnset => !Enabled && (SavedBeforeShader
        || (Radius == 100f && Spacing == 0.5f && MinimumScale == 0.75f && MaximumScale == 1.2f));

    /// <summary>The built-in grass, keeping this rule's layer densities (which name its own painted layers).</summary>
    public TerrainGrassRuleSettings BuiltInWithLayers()
    {
        var rule = new TerrainGrassRuleSettings { Enabled = Enabled };
        for (int layer = 0; layer < LayerCount; layer++) rule.SetDensity(layer, Density(layer));
        return rule;
    }

    /// <summary>Tufts along one side of a cell.</summary>
    [JsonIgnore, Browsable(false)]
    public int TuftsPerCellSide => Math.Max(1, (int)MathF.Floor(CellSize / MathF.Max(Spacing, 0.01f) + 0.001f));

    private static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static Vector3 ClampColor(Vector3 value, Vector3 fallback) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? Vector3.Clamp(value, Vector3.Zero, Vector3.One)
            : fallback;
}

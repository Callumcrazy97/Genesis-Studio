using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Terrain;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The Terrain editor's nature as commands: the grass grown around the camera (the grass rule) and
/// the scatter layers that fill a terrain with copies of a Model (forests, rocks, flowers), stored
/// in the terrain's <c>.nature.json</c> beside it, as the editor stores them. A game's grass belongs
/// here, not in a script that places tufts itself.
/// </summary>
public static class TerrainCommands
{
    public static IEnumerable<IAutomationCommand> All() =>
        [new TerrainNature(), new TerrainSetGrass(), new TerrainAddScatterLayer(), new TerrainUpdateScatterLayer(), new TerrainRemoveScatterLayer(),
         new TerrainAddWater(), new TerrainUpdateWater(), new TerrainRemoveWater()];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Colours are Vector3s, whose parts are fields: written as {"x": .., "y": .., "z": ..}.
        IncludeFields = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string Terrain(AutomationContext context, string path)
    {
        string full = context.Resolve(path);
        if (!File.Exists(full) || !full.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
            throw new AutomationException($"'{path}' is not a Terrain resource (a .terrain.json file).");
        return full;
    }

    private static AutomationResult Save(AutomationContext context, string terrain, TerrainNatureDocument document, string message, JsonNode? data = null)
    {
        string sidecar = TerrainNatureSerializer.SidecarPath(terrain);
        if (File.Exists(sidecar)) ResourceBackupService.BackupBeforeOverwrite(sidecar);
        TerrainNatureSerializer.Save(terrain, document);
        return AutomationResult.Success(message, data, context.Relative(sidecar));
    }

    /// <summary>Copies every input property that was given (not null) onto the target property of the same name.</summary>
    private static void Apply(object input, object target, params string[] skip)
    {
        foreach (PropertyInfo property in input.GetType().GetProperties())
        {
            if (skip.Contains(property.Name, StringComparer.Ordinal)) continue;
            object? value = property.GetValue(input);
            if (value is null) continue;
            PropertyInfo? destination = target.GetType().GetProperty(property.Name);
            if (destination is null || !destination.CanWrite) continue;
            Type type = Nullable.GetUnderlyingType(destination.PropertyType) ?? destination.PropertyType;
            destination.SetValue(target, type.IsEnum && value is string text ? Enum.Parse(type, text, true) : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public sealed class TerrainInput
    {
        [Description("The Terrain resource, project-relative (Assets/.../Name.terrain.json)."), RequiredInput] public string Terrain { get; set; } = string.Empty;
    }

    public sealed class TerrainNature : AutomationCommand<TerrainInput>
    {
        public override string Name => "terrain.nature";
        public override string Description => "A terrain's grass rule, scatter layers (forests, rocks, flowers) and water (lakes, rivers, the sea), as the Terrain editor's Decorate step shows them.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, TerrainInput input)
        {
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(Terrain(context, input.Terrain));
            return AutomationResult.Success($"{document.ScatterLayers.Count} scatter layers, {document.WaterBodies.Count} water; grass rule {(document.GrassRule.Enabled ? "on" : "off")}.", new JsonObject
            {
                ["grass"] = JsonSerializer.SerializeToNode(document.GrassRule, Json),
                ["scatterLayers"] = JsonSerializer.SerializeToNode(document.ScatterLayers, Json),
                ["water"] = new JsonArray([.. document.WaterBodies.Select(water => (JsonNode?)DescribeWater(water))]),
            });
        }
    }

    public sealed class GrassInput
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("Grow grass around the camera from the rule (default: on; false switches it off).")] public bool? Enabled { get; set; }
        [Description("Metres from the camera grass is grown and drawn to (default 90).")] public float? Radius { get; set; }
        [Description("Metres between neighbouring tufts at full density (default 0.35).")] public float? Spacing { get; set; }
        [Description("Fraction of the radius at full density before it thins out (default 0.45).")] public float? FullDensityFraction { get; set; }
        [Description("Tufts nearer than this use the detailed blade mesh (default 30 m).")] public float? DetailDistance { get; set; }
        [Description("How far a tuft wanders from its lattice point, as a fraction of the spacing (default 0.9).")] public float? Jitter { get; set; }
        [Description("Smallest tuft scale (default 0.6).")] public float? MinimumScale { get; set; }
        [Description("Largest tuft scale (default 1).")] public float? MaximumScale { get; set; }
        [Description("Steepest ground in degrees grass grows on (default 40).")] public float? SteepestSlopeDegrees { get; set; }
        [Description("Which tuft to grow (MeadowGrass looks like the scattered grass).")] public FoliageSpecies? Species { get; set; }
        [Description("Picks the pattern of tufts.")] public int? PatternSeed { get; set; }
        [Description("Grass on each painted layer, first to eighth, 0 to 1 (default 1 on the first, usually Grass, and 0 on the rest): [1, 0.3] also grows thin grass on the second.")] public float[]? LayerDensities { get; set; }
        [Description("Most tufts drawn in a frame, nearest first (default 16000, at most 32768).")] public int? MaximumDrawnTufts { get; set; }
        [Description("Colour the blades from baseColor at the root to tipColor at the tip (default true; false keeps the tuft's own colours).")] public bool? ColorEnabled { get; set; }
        [Description("The blades' colour at the root [r, g, b], 0 to 1 (default [0.10, 0.22, 0.06]).")] public float[]? BaseColor { get; set; }
        [Description("The blades' colour at the tip [r, g, b], 0 to 1 (default [0.27, 0.62, 0.17]).")] public float[]? TipColor { get; set; }
        [Description("How the colour runs up a blade: 1 evenly, below 1 the tip colour reaches further down, above 1 it keeps to the tips (default 0.8).")] public float? ColorCurve { get; set; }
        [Description("An Image resource drawn on each blade, tinted by the colours; its see-through parts cut the blade (default none; empty clears it).")] public string? Texture { get; set; }
        [Description("Sway the blades in the wind (default true).")] public bool? WindEnabled { get; set; }
        [Description("How far the blades sway, 0 to 4 (default 1).")] public float? WindStrength { get; set; }
        [Description("How fast the blades sway, 0 to 4 (default 1).")] public float? WindSpeed { get; set; }
        [Description("Gusts rolling across the field, 0 to 1 (default 0).")] public float? WindGusts { get; set; }
        [Description("Grass bends away from animated characters and a game's pushers (default true).")] public bool? PushersEnabled { get; set; }
        [Description("How far pushed grass bends, 0 to 1 (default 0.8).")] public float? PushStrength { get; set; }
        [Description("Seconds pushed grass takes to stand up again (default 0.8).")] public float? SpringBackSeconds { get; set; }
        [Description("Keep grass off the terrain's paths (default true).")] public bool? ClearPaths { get; set; }
        [Description("Metres over which grass thins in at a path's edge (default 0.6).")] public float? PathEdge { get; set; }
    }

    public sealed class TerrainSetGrass : AutomationCommand<GrassInput>
    {
        public override string Name => "terrain.setGrass";
        public override string Description => "Sets a terrain's grass rule (grass grown around the camera, as the Terrain editor's grass does) and switches it on unless enabled is false; only the fields given change. With only the terrain, it grows the built-in meadow grass.";

        protected override AutomationResult Execute(AutomationContext context, GrassInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            TerrainGrassRuleSettings rule = document.GrassRule;
            bool suitGround = false;
            if (input.Enabled != false && !rule.Enabled)
            {
                // Asking for grass grows it. A switched-off rule nobody chose (saved before the grass
                // shader, or with the old stock values) starts as the built-in grass instead.
                if (rule.LooksUnset) document.GrassRule = rule = rule.BuiltInWithLayers();
                rule.Enabled = true;
                suitGround = true;
            }
            Apply(input, rule, nameof(GrassInput.Terrain), nameof(GrassInput.BaseColor), nameof(GrassInput.TipColor));
            if (input.BaseColor != null) rule.BaseColor = Vector(input.BaseColor, "The base colour");
            if (input.TipColor != null) rule.TipColor = Vector(input.TipColor, "The tip colour");
            if (input.Texture is { Length: > 0 } texture && !ImageExists(context, texture))
                throw new AutomationException($"There is no Image resource '{texture}' for the grass texture.");
            rule.Normalize();
            // The built-in grass comes with its ground: a stock Grass layer takes the darker green
            // between its tufts, as the Terrain editor's Foliage › Grass template does.
            bool groundSuited = suitGround && TerrainGrassGround.SuitTerrainFile(terrain);
            return Save(context, terrain, document,
                $"Grass rule {(rule.Enabled ? "on" : "off")}{(groundSuited ? "; the Grass layer's colour suits it" : "")}.",
                JsonSerializer.SerializeToNode(rule, Json));
        }

        private static bool ImageExists(AutomationContext context, string texture) =>
            File.Exists(context.Resolve(texture))
            || Genesis.Shared.Assets.ResourceCatalog.Resolve(context.Root, texture, Genesis.Shared.Assets.ResourceType.Image).Length > 0;
    }

    public class ScatterLayerFields
    {
        [Description("The layer's name, shown in the Terrain editor.")] public string? Name { get; set; }
        [Description("The Model resource to place, project-relative (Assets/.../Tree.model.json).")] public string? Model { get; set; }
        [Description("Whether the layer is drawn.")] public bool? Enabled { get; set; }
        [Description("Copies per hectare (100 m x 100 m) where every condition is met (default 120).")] public float? DensityPerHectare { get; set; }
        [Description("Closest two copies may stand, in metres (default 3).")] public float? MinimumSpacing { get; set; }
        [Description("Lowest ground height copies stand on.")] public float? MinimumHeight { get; set; }
        [Description("Highest ground height copies stand on.")] public float? MaximumHeight { get; set; }
        [Description("Steepest ground in degrees (default 30).")] public float? MaximumSlopeDegrees { get; set; }
        [Description("Painted layers (bits 0-3) copies may stand on; 15 allows all four.")] public int? PaintLayerMask { get; set; }
        [Description("How much of an allowed painted layer a spot needs, 0 to 1.")] public float? MinimumPaintWeight { get; set; }
        [Description("Size of the clumps the layer gathers into, in metres; 0 spreads it evenly.")] public float? ClumpSize { get; set; }
        [Description("How strongly clumps gather, 0 to 1.")] public float? ClumpStrength { get; set; }
        [Description("Smallest scale (default 0.8).")] public float? MinimumScale { get; set; }
        [Description("Largest scale (default 1.25).")] public float? MaximumScale { get; set; }
        [Description("Metres each copy sinks into the ground.")] public float? Sink { get; set; }
        [Description("Tilt each copy to the ground instead of standing upright.")] public bool? AlignToGround { get; set; }
        [Description("Distance copies stop being drawn; 0 uses the view distance.")] public float? DrawDistance { get; set; }
        [Description("Whether copies cast shadows.")] public bool? CastShadows { get; set; }
        [Description("Half-width of each copy's collision box at scale 1 (a trunk's radius); 0 = no collision.")] public float? CollisionRadius { get; set; }
        [Description("Height of that box at scale 1.")] public float? CollisionHeight { get; set; }
        [Description("Picks the pattern of copies.")] public int? Seed { get; set; }
        [Description("Whether copies bend away from characters and grass pushers: Auto (default: small plants with no shadow and no collision), On or Off.")] public TerrainScatterBend? Bend { get; set; }
    }

    public sealed class AddLayerInput : ScatterLayerFields
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
    }

    public sealed class TerrainAddScatterLayer : AutomationCommand<AddLayerInput>
    {
        public override string Name => "terrain.addScatterLayer";
        public override string Description => "Adds a scatter layer: many copies of a Model over a terrain by rules (density, spacing, slope, height, painted layer, scale), drawn and streamed by the engine.";

        protected override AutomationResult Execute(AutomationContext context, AddLayerInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            if (string.IsNullOrWhiteSpace(input.Model)) throw new AutomationException("Say which Model resource to scatter ('model').");
            // The editor takes a model's path or its name; a path must exist.
            bool isPath = input.Model.Contains('/') || input.Model.Contains('\\') || input.Model.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (isPath && !File.Exists(context.Resolve(input.Model))) throw new AutomationException($"There is no Model resource at '{input.Model}'.");
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            var layer = new TerrainScatterLayer();
            Apply(input, layer, nameof(AddLayerInput.Terrain));
            layer.Normalize();
            document.ScatterLayers.Add(layer);
            return Save(context, terrain, document, $"Added the scatter layer '{layer.Name}' ({layer.Id}).", JsonSerializer.SerializeToNode(layer, Json));
        }
    }

    public sealed class UpdateLayerInput : ScatterLayerFields
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("The layer to change: its id, or its name."), RequiredInput] public string Layer { get; set; } = string.Empty;
    }

    private static TerrainScatterLayer Layer(TerrainNatureDocument document, string layer) =>
        document.ScatterLayers.FirstOrDefault(l => l.Id.Equals(layer, StringComparison.OrdinalIgnoreCase))
        ?? document.ScatterLayers.FirstOrDefault(l => l.Name.Equals(layer, StringComparison.OrdinalIgnoreCase))
        ?? throw new AutomationException($"The terrain has no scatter layer '{layer}'.");

    public sealed class TerrainUpdateScatterLayer : AutomationCommand<UpdateLayerInput>
    {
        public override string Name => "terrain.updateScatterLayer";
        public override string Description => "Changes a scatter layer's rules; only the fields given change.";

        protected override AutomationResult Execute(AutomationContext context, UpdateLayerInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            TerrainScatterLayer layer = Layer(document, input.Layer);
            Apply(input, layer, nameof(UpdateLayerInput.Terrain), nameof(UpdateLayerInput.Layer));
            layer.Normalize();
            return Save(context, terrain, document, $"Changed the scatter layer '{layer.Name}'.", JsonSerializer.SerializeToNode(layer, Json));
        }
    }

    public sealed class RemoveLayerInput
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("The layer to remove: its id, or its name."), RequiredInput] public string Layer { get; set; } = string.Empty;
    }

    public sealed class TerrainRemoveScatterLayer : AutomationCommand<RemoveLayerInput>
    {
        public override string Name => "terrain.removeScatterLayer";
        public override string Description => "Removes a scatter layer from a terrain (the nature file is backed up first).";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, RemoveLayerInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            TerrainScatterLayer layer = Layer(document, input.Layer);
            document.ScatterLayers.Remove(layer);
            return Save(context, terrain, document, $"Removed the scatter layer '{layer.Name}'.");
        }
    }

    public class WaterFields
    {
        [Description("The water's name, shown in the Terrain editor.")] public string? Name { get; set; }
        [Description("Water (a lake or pond: an ellipse, sizeX by sizeZ), River (along riverPoints) or Ocean (the sea to the horizon at surfaceHeight).")] public TerrainWaterKind? Kind { get; set; }
        [Description("Centre [x, y, z] of a lake, or of the sea's reach.")] public float[]? Center { get; set; }
        [Description("A lake's width across x, metres; for the sea, how far it reaches.")] public float? SizeX { get; set; }
        [Description("A lake's width across z, metres.")] public float? SizeZ { get; set; }
        [Description("Height of a lake's or the sea's surface.")] public float? SurfaceHeight { get; set; }
        [Description("A river's course: points [x, y, z, width, depth], y the surface there (so a river can fall), width and depth in metres.")] public List<float[]>? RiverPoints { get; set; }
        [Description("Cut a river's bed into the ground (default true).")] public bool? CarveRiverbed { get; set; }
        [Description("Height of the waves, metres (default 0.18).")] public float? WaveAmplitude { get; set; }
        [Description("How fast the surface flows (default 0.2).")] public float? FlowSpeed { get; set; }
        [Description("Use the colours below instead of the kind's own look.")] public bool? CustomAppearance { get; set; }
        [Description("Colour over shallow water [r, g, b], 0 to 1.")] public float[]? ShallowColor { get; set; }
        [Description("Colour the water darkens to with depth [r, g, b].")] public float[]? DeepColor { get; set; }
        [Description("How much the surface hides what is under it, 0 (glass) to 1.")] public float? Opacity { get; set; }
        [Description("Depth in metres over which the shallow colour becomes the deep one.")] public float? ClarityDepth { get; set; }
        [Description("Width of the foam along the shore, metres.")] public float? FoamWidth { get; set; }
        [Description("What the water does to things in it: None, Shallow (wading) or SwimmableVolume.")] public WaterPhysicsMode? PhysicsMode { get; set; }
        [Description("How deep the water is for floating and swimming, metres.")] public float? PhysicsDepth { get; set; }
        [Description("A mesh Shader resource to draw this water with, project-relative (the Terrain editor's water shader); empty clears it.")] public string? Shader { get; set; }
    }

    private static JsonObject DescribeWater(TerrainWaterDefinition water) => new()
    {
        ["id"] = water.Id,
        ["name"] = water.Name,
        ["kind"] = water.Kind.ToString(),
        ["center"] = new JsonArray(water.Center.X, water.Center.Y, water.Center.Z),
        ["size"] = new JsonArray(water.SizeX, water.SizeZ),
        ["surfaceHeight"] = water.SurfaceHeight,
        ["riverPoints"] = water.RiverPoints.Count,
        ["physicsMode"] = water.PhysicsMode.ToString(),
    };

    private static System.Numerics.Vector3 Vector(float[] values, string what) =>
        values is { Length: 3 } && values.All(float.IsFinite) ? new(values[0], values[1], values[2]) : throw new AutomationException($"{what} is three numbers.");

    /// <summary>The given fields onto a water body, and its shader into the terrain's settings.</summary>
    private static void ApplyWater(AutomationContext context, string terrain, WaterFields input, TerrainWaterDefinition water)
    {
        Apply(input, water, nameof(WaterFields.Center), nameof(WaterFields.RiverPoints), nameof(WaterFields.ShallowColor), nameof(WaterFields.DeepColor), nameof(WaterFields.Shader),
            nameof(AddWaterInput.Terrain), nameof(UpdateWaterInput.Water));
        if (input.Center != null) water.Center = Vector(input.Center, "The centre");
        if (input.ShallowColor != null) water.ShallowColor = Vector(input.ShallowColor, "The shallow colour");
        if (input.DeepColor != null) water.DeepColor = Vector(input.DeepColor, "The deep colour");
        if (input.RiverPoints != null)
        {
            water.RiverPoints = [.. input.RiverPoints.Select(point => point is { Length: 5 } && point.All(float.IsFinite)
                ? new Genesis.World.Water.WaterSplinePoint { Position = new(point[0], point[1], point[2]), Width = point[3], Depth = point[4] }
                : throw new AutomationException("Each river point is [x, y, z, width, depth]."))];
            if (water.Kind == TerrainWaterKind.River && water.RiverPoints.Count >= 2)
                water.Center = water.RiverPoints.Aggregate(System.Numerics.Vector3.Zero, (sum, point) => sum + point.Position) / water.RiverPoints.Count;
        }
        if (water.Kind == TerrainWaterKind.River && water.RiverPoints.Count < 2) throw new AutomationException("A river needs at least two riverPoints.");
        water.Normalize();
        if (input.Shader == null) return;
        JsonObject settings = JsonNode.Parse(File.ReadAllText(terrain)) as JsonObject ?? throw new AutomationException("The terrain's settings are not a JSON object.");
        JsonObject shaders = settings["componentShaders"] as JsonObject ?? [];
        if (string.IsNullOrWhiteSpace(input.Shader)) shaders.Remove("water:" + water.Id);
        else
        {
            if (!File.Exists(context.Resolve(input.Shader))) throw new AutomationException($"There is no Shader resource at '{input.Shader}'.");
            shaders["water:" + water.Id] = input.Shader;
        }
        settings["componentShaders"] = shaders;
        ResourceBackupService.BackupBeforeOverwrite(terrain);
        File.WriteAllText(terrain, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed class AddWaterInput : WaterFields
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
    }

    public sealed class TerrainAddWater : AutomationCommand<AddWaterInput>
    {
        public override string Name => "terrain.addWater";
        public override string Description => "Adds water to a terrain, as the Terrain editor's Add water does: a lake or pond, a river along points (it may fall), or the sea; with its look, flow, wading or swimming, and optionally its own shader.";

        protected override AutomationResult Execute(AutomationContext context, AddWaterInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            var water = new TerrainWaterDefinition { Name = input.Name ?? (input.Kind ?? TerrainWaterKind.Water).ToString() };
            ApplyWater(context, terrain, input, water);
            document.WaterBodies.Add(water);
            return Save(context, terrain, document, $"Added the water '{water.Name}' ({water.Id}).", DescribeWater(water));
        }
    }

    public sealed class UpdateWaterInput : WaterFields
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("The water to change: its id, or its name."), RequiredInput] public string Water { get; set; } = string.Empty;
    }

    private static TerrainWaterDefinition Water(TerrainNatureDocument document, string water) =>
        document.WaterBodies.FirstOrDefault(w => w.Id.Equals(water, StringComparison.OrdinalIgnoreCase))
        ?? document.WaterBodies.FirstOrDefault(w => w.Name.Equals(water, StringComparison.OrdinalIgnoreCase))
        ?? throw new AutomationException($"The terrain has no water '{water}'.");

    public sealed class TerrainUpdateWater : AutomationCommand<UpdateWaterInput>
    {
        public override string Name => "terrain.updateWater";
        public override string Description => "Changes a terrain's water; only the fields given change.";

        protected override AutomationResult Execute(AutomationContext context, UpdateWaterInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            TerrainWaterDefinition water = Water(document, input.Water);
            ApplyWater(context, terrain, input, water);
            return Save(context, terrain, document, $"Changed the water '{water.Name}'.", DescribeWater(water));
        }
    }

    public sealed class RemoveWaterInput
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("The water to remove: its id, or its name."), RequiredInput] public string Water { get; set; } = string.Empty;
    }

    public sealed class TerrainRemoveWater : AutomationCommand<RemoveWaterInput>
    {
        public override string Name => "terrain.removeWater";
        public override string Description => "Removes water from a terrain (the nature file is backed up first).";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, RemoveWaterInput input)
        {
            string terrain = Terrain(context, input.Terrain);
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(terrain);
            TerrainWaterDefinition water = Water(document, input.Water);
            document.WaterBodies.Remove(water);
            return Save(context, terrain, document, $"Removed the water '{water.Name}'.");
        }
    }
}
using System.ComponentModel;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Terrain;
using Genesis.World.Terrain;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The Terrain editor's Shape and Sculpt steps as commands: a new terrain from a landform (the same
/// generator as the editor's landform gallery), what a terrain is like, and strokes of the editor's own
/// brushes (<see cref="TerrainSculpt"/>). The heights are saved beside the terrain as
/// <c>.terrain.json.gterrain</c>, which is what the editor and the game load.
/// </summary>
public static class TerrainShapeCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new TerrainCreate(), new TerrainInfo(), new TerrainSculptStroke()];

    private static string Terrain(AutomationContext context, string path)
    {
        string full = context.Resolve(path);
        if (!File.Exists(full) || !full.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
            throw new AutomationException($"'{path}' is not a Terrain resource (a .terrain.json file).");
        return full;
    }

    private static JsonObject Settings(string full) =>
        JsonNode.Parse(File.ReadAllText(full)) as JsonObject ?? throw new AutomationException("The terrain's settings are not a JSON object.");

    private static float Number(JsonObject settings, string name, float fallback) =>
        settings[name] is JsonValue value && value.TryGetValue(out double number) ? (float)number : fallback;

    /// <summary>The generator's parameters from a terrain's settings, as the editor reads them.</summary>
    private static TerrainGenParams Params(JsonObject settings)
    {
        JsonArray? resolution = settings["resolution"] as JsonArray;
        Enum.TryParse(settings["preset"]?.GetValue<string>() ?? settings["seedProfile"]?.GetValue<string>(), out TerrainPreset preset);
        return new TerrainGenParams
        {
            ResolutionX = resolution is { Count: >= 2 } ? resolution[0]!.GetValue<int>() : 129,
            ResolutionZ = resolution is { Count: >= 2 } ? resolution[1]!.GetValue<int>() : 129,
            CellSize = Number(settings, "cellSize", 1f),
            MinHeight = Number(settings, "minHeight", -24f),
            MaxHeight = Number(settings, "maxHeight", 72f),
            Preset = preset,
            Seed = (int)Number(settings, "seed", 1337),
            ErosionIterations = (int)Number(settings, "erosionIterations", 0),
            ErosionStrength = Number(settings, "erosionStrength", 0.35f),
            TerraceStrength = Number(settings, "terraceStrength", 0),
            TerraceSteps = (int)Number(settings, "terraceSteps", 12),
            RiverCount = (int)Number(settings, "riverCount", 0),
            RiverDepth = Number(settings, "riverDepth", 8f),
        };
    }

    /// <summary>The terrain's heights: its saved .gterrain, or (never sculpted) its landform generated afresh, as the editor opens it.</summary>
    private static TerrainAsset Load(string full)
    {
        string binary = full + ".gterrain";
        return File.Exists(binary) ? TerrainAsset.Load(binary) : TerrainGenerator.Generate(Params(Settings(full)));
    }

    private static JsonObject Describe(AutomationContext context, string full, TerrainAsset terrain)
    {
        float low = float.MaxValue, high = float.MinValue;
        for (int z = 0; z < terrain.ResolutionZ; z++)
            for (int x = 0; x < terrain.ResolutionX; x++)
            {
                float height = terrain.GetHeight(x, z);
                low = Math.Min(low, height);
                high = Math.Max(high, height);
            }
        JsonObject settings = Settings(full);
        float width = (terrain.ResolutionX - 1) * terrain.CellSize, depth = (terrain.ResolutionZ - 1) * terrain.CellSize;
        return new JsonObject
        {
            ["path"] = context.Relative(full),
            ["preset"] = settings["preset"]?.GetValue<string>() ?? settings["seedProfile"]?.GetValue<string>(),
            ["seed"] = settings["seed"]?.DeepClone(),
            ["resolution"] = new JsonArray(terrain.ResolutionX, terrain.ResolutionZ),
            ["cellSize"] = terrain.CellSize,
            ["size"] = new JsonArray(width, depth),
            ["extent"] = new JsonObject
            {
                ["minX"] = terrain.OriginX, ["maxX"] = terrain.OriginX + width,
                ["minZ"] = terrain.OriginZ, ["maxZ"] = terrain.OriginZ + depth,
            },
            ["heightLimits"] = new JsonArray(terrain.MinHeight, terrain.MaxHeight),
            ["heights"] = new JsonArray(MathF.Round(low, 2), MathF.Round(high, 2)),
            ["layers"] = new JsonArray([.. (settings["layers"] as JsonArray ?? []).Select(layer => (JsonNode?)(layer?["name"]?.GetValue<string>() ?? "Layer"))]),
        };
    }

    public sealed class CreateInput
    {
        [Description("The terrain's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Terrain).")] public string? Folder { get; set; }
        [Description("The landform, as the Terrain editor's gallery offers them (default RollingHills).")] public TerrainPreset? Preset { get; set; }
        [Description("Seed: the same seed and settings make the same land (default 1337).")] public int? Seed { get; set; }
        [Description("Height samples across x (default 129; 17 to 4097).")] public int? ResolutionX { get; set; }
        [Description("Height samples across z (default 129; 17 to 4097).")] public int? ResolutionZ { get; set; }
        [Description("Metres between samples (default 1).")] public float? CellSize { get; set; }
        [Description("Lowest height the land can reach, metres (default -24).")] public float? MinHeight { get; set; }
        [Description("Highest height the land can reach, metres (default 72).")] public float? MaxHeight { get; set; }
        [Description("Erosion passes (default 0).")] public int? ErosionIterations { get; set; }
        [Description("Erosion strength, 0 to 1 (default 0.35).")] public float? ErosionStrength { get; set; }
        [Description("Terracing, 0 to 1 (default 0).")] public float? TerraceStrength { get; set; }
        [Description("Terrace steps (default 12).")] public int? TerraceSteps { get; set; }
        [Description("Rivers to carve (default 0).")] public int? RiverCount { get; set; }
        [Description("River depth, metres (default 8).")] public float? RiverDepth { get; set; }
    }

    public sealed class TerrainCreate : AutomationCommand<CreateInput>
    {
        public override string Name => "terrain.create";
        public override string Description => "Makes a Terrain from a landform (flatlands, hills, mountains, islands, canyon...), as the Terrain editor's landform gallery does, with its size, height range, erosion, terraces and rivers.";

        protected override AutomationResult Execute(AutomationContext context, CreateInput input)
        {
            var parameters = new TerrainGenParams();
            parameters.Preset = input.Preset ?? parameters.Preset;
            parameters.Seed = input.Seed ?? parameters.Seed;
            parameters.ResolutionX = input.ResolutionX ?? parameters.ResolutionX;
            parameters.ResolutionZ = input.ResolutionZ ?? parameters.ResolutionZ;
            parameters.CellSize = input.CellSize ?? parameters.CellSize;
            parameters.MinHeight = input.MinHeight ?? parameters.MinHeight;
            parameters.MaxHeight = input.MaxHeight ?? parameters.MaxHeight;
            parameters.ErosionIterations = input.ErosionIterations ?? parameters.ErosionIterations;
            parameters.ErosionStrength = input.ErosionStrength ?? parameters.ErosionStrength;
            parameters.TerraceStrength = input.TerraceStrength ?? parameters.TerraceStrength;
            parameters.TerraceSteps = input.TerraceSteps ?? parameters.TerraceSteps;
            parameters.RiverCount = input.RiverCount ?? parameters.RiverCount;
            parameters.RiverDepth = input.RiverDepth ?? parameters.RiverDepth;
            if (parameters.ResolutionX is < 17 or > 4097 || parameters.ResolutionZ is < 17 or > 4097)
                throw new AutomationException("The resolution must be 17 to 4097 samples each way.");
            if (parameters.CellSize <= 0) throw new AutomationException("The cell size must be above 0.");
            if (parameters.MaxHeight <= parameters.MinHeight) throw new AutomationException("The highest height must be above the lowest.");

            TerrainAsset terrain = TerrainGenerator.Generate(parameters);
            string full = context.Resources.CreateResource(context.Resolve(input.Folder ?? "Assets/Terrain"), ResourceKind.Terrain, input.Name);
            JsonObject settings = Settings(full);
            settings["schemaVersion"] = 2;
            settings["resolution"] = new JsonArray(parameters.ResolutionX, parameters.ResolutionZ);
            settings["cellSize"] = parameters.CellSize;
            settings["minHeight"] = parameters.MinHeight;
            settings["maxHeight"] = parameters.MaxHeight;
            settings["preset"] = parameters.Preset.ToString();
            settings["seedProfile"] = parameters.Preset.ToString();
            settings["seed"] = parameters.Seed;
            settings["erosionIterations"] = parameters.ErosionIterations;
            settings["erosionStrength"] = parameters.ErosionStrength;
            settings["terraceStrength"] = parameters.TerraceStrength;
            settings["terraceSteps"] = parameters.TerraceSteps;
            settings["riverCount"] = parameters.RiverCount;
            settings["riverDepth"] = parameters.RiverDepth;
            File.WriteAllText(full, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            terrain.Save(full + ".gterrain");
            return AutomationResult.Success($"Made {Path.GetFileName(full)} ({parameters.Preset}).", Describe(context, full, terrain), context.Relative(full), context.Relative(full + ".gterrain"));
        }
    }

    public sealed class InfoInput
    {
        [Description("The Terrain resource, project-relative (Assets/Terrain/Name.terrain.json)."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("World points [x, z] whose ground height to report.")] public List<float[]>? Points { get; set; }
    }

    public sealed class TerrainInfo : AutomationCommand<InfoInput>
    {
        public override string Name => "terrain.info";
        public override string Description => "Describes a terrain: landform, size, where it lies (its x and z extent), height limits, the heights it actually reaches, paint layers, and the ground height at any points asked about.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, InfoInput input)
        {
            string full = Terrain(context, input.Terrain);
            TerrainAsset terrain = Load(full);
            JsonObject data = Describe(context, full, terrain);
            if (input.Points is { Count: > 0 } points)
                data["pointHeights"] = new JsonArray([.. points.Select(p => (JsonNode?)MathF.Round(terrain.SampleHeight(Point(p).X, Point(p).Y), 3))]);
            return AutomationResult.Success($"{data["size"]![0]} x {data["size"]![1]} m, heights {data["heights"]![0]} to {data["heights"]![1]} m.", data);
        }
    }

    private static System.Numerics.Vector2 Point(float[] point) =>
        point is { Length: 2 } ? new(point[0], point[1]) : throw new AutomationException("Each point is [x, z].");

    public sealed class SculptInput
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("The brush: Raise, Lower, Smooth, Flatten (to the ground where the stroke starts, or flattenHeight), InFill (fills hollows to their rim) or Paint (lays a paint layer)."), RequiredInput]
        public TerrainSculptBrush Brush { get; set; }
        [Description("The stroke: world points [x, z]; dabs are laid along it every quarter of the radius, as dragging the brush does."), RequiredInput]
        public List<float[]> Points { get; set; } = [];
        [Description("Brush radius, metres (default 8).")] public float? Radius { get; set; }
        [Description("Strength per dab, 0 to 1 (default 0.5).")] public float? Strength { get; set; }
        [Description("Falloff: Smooth (default), Linear or Hard.")] public string? Falloff { get; set; }
        [Description("Paint: the layer, by name or 0-based number.")] public string? Layer { get; set; }
        [Description("Flatten: the height to level to.")] public float? FlattenHeight { get; set; }
    }

    public sealed class TerrainSculptStroke : AutomationCommand<SculptInput>
    {
        public override string Name => "terrain.sculpt";
        public override string Description => "Sculpts or paints a terrain with one stroke of the Terrain editor's own brushes (raise, lower, smooth, flatten, in-fill, paint).";

        protected override AutomationResult Execute(AutomationContext context, SculptInput input)
        {
            string full = Terrain(context, input.Terrain);
            if (input.Brush == TerrainSculptBrush.Erase)
                throw new AutomationException("Erase returns ground to how it was when the editor opened it, which a command has no notion of; use Smooth, Flatten or Lower.");
            if (input.Points.Count == 0) throw new AutomationException("The stroke needs at least one point.");
            string falloff = input.Falloff ?? "Smooth";
            if (falloff is not ("Smooth" or "Linear" or "Hard")) throw new AutomationException("The falloff is Smooth, Linear or Hard.");
            float radius = input.Radius ?? 8f;
            if (radius <= 0) throw new AutomationException("The radius must be above 0.");
            JsonArray layers = Settings(full)["layers"] as JsonArray ?? [];
            int layer = 0;
            if (input.Brush == TerrainSculptBrush.Paint)
            {
                string wanted = input.Layer ?? throw new AutomationException("Paint needs a layer (its name or number).");
                layer = int.TryParse(wanted, out int number) ? number
                    : layers.Select((node, index) => (node?["name"]?.GetValue<string>(), index)).FirstOrDefault(pair => string.Equals(pair.Item1, wanted, StringComparison.OrdinalIgnoreCase)) is { Item1: not null } found ? found.index
                    : throw new AutomationException($"The terrain has no layer '{wanted}'. Layers: {string.Join(", ", layers.Select(node => node?["name"]?.GetValue<string>()))}.");
                if (layer < 0 || layer >= Math.Min(layers.Count, TerrainAsset.MaximumPaintLayers))
                    throw new AutomationException($"Layer {layer} does not exist; the terrain has {layers.Count}.");
            }

            TerrainAsset terrain = Load(full);
            System.Numerics.Vector2[] points = [.. input.Points.Select(Point)];
            var dab = new TerrainSculptDab
            {
                Radius = radius,
                Strength = Math.Clamp(input.Strength ?? 0.5f, 0f, 1f),
                Falloff = falloff,
                Layer = layer,
                FlattenTarget = input.FlattenHeight ?? terrain.SampleHeight(points[0].X, points[0].Y),
            };
            int dabs = 0;
            float spacing = Math.Max(radius * 0.25f, terrain.CellSize * 0.5f);
            for (int i = 0; i < points.Length; i++)
            {
                if (i == 0) { TerrainSculpt.Apply(terrain, input.Brush, points[0].X, points[0].Y, dab); dabs++; continue; }
                float length = System.Numerics.Vector2.Distance(points[i - 1], points[i]);
                int steps = Math.Max(1, (int)MathF.Ceiling(length / spacing));
                for (int step = 1; step <= steps; step++)
                {
                    var at = System.Numerics.Vector2.Lerp(points[i - 1], points[i], step / (float)steps);
                    TerrainSculpt.Apply(terrain, input.Brush, at.X, at.Y, dab);
                    dabs++;
                }
            }

            string binary = full + ".gterrain";
            if (File.Exists(binary)) ResourceBackupService.BackupBeforeOverwrite(binary);
            terrain.Save(binary);
            return AutomationResult.Success($"{input.Brush}: {dabs} dabs.", Describe(context, full, terrain), context.Relative(binary));
        }
    }
}

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
    public static IEnumerable<IAutomationCommand> All() => [new TerrainCreate(), new TerrainFromModel(), new TerrainInfo(), new TerrainSculptStroke(), new TerrainSetHole()];

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
    internal static TerrainGenParams Params(JsonObject settings)
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

    public sealed class FromModelLayer
    {
        [Description("The paint layer's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("The model's materials whose ground is painted with this layer.")] public List<string>? Materials { get; set; }
        [Description("Its colour [r, g, b], 0 to 1 (default the first material's colour).")] public float[]? Color { get; set; }
    }

    public sealed class FromModelInput
    {
        [Description("The terrain's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Terrain).")] public string? Folder { get; set; }
        [Description("The Model resource whose surfaces become the ground, project-relative (Assets/Models/Name.model.json)."), RequiredInput]
        public string Model { get; set; } = string.Empty;
        [Description("The model's materials that are ground (default all of them); walls and roofs left out stay with the model.")] public List<string>? Surfaces { get; set; }
        [Description("Metres between height samples (default 1).")] public float? CellSize { get; set; }
        [Description("Where the model stands in the room [x, y, z] (default the origin), so the terrain lies under it.")] public float[]? Position { get; set; }
        [Description("Paint layers (up to 8), each with the materials painted with it; ground of a material in none of them takes the first layer. Default: one layer per ground material.")]
        public List<FromModelLayer>? Layers { get; set; }
        [Description("Also hide those surfaces on the Model (its hiddenMaterials), so the terrain stands in for them (default false).")] public bool? HideSurfaces { get; set; }
    }

    /// <summary>
    /// A Terrain made from the ground of a Model: the highest of its chosen surfaces at every sample
    /// (an imported scene's ground, a hand-made landscape), each sample painted with the layer of the
    /// material it came from. Gaps the surfaces leave take the nearest ground's height.
    /// </summary>
    public sealed class TerrainFromModel : AutomationCommand<FromModelInput>
    {
        public override string Name => "terrain.fromModel";
        public override string Description => "Makes a Terrain from a Model's ground surfaces (chosen by material): their heights, a paint layer per material, and optionally hides those surfaces on the Model so the terrain takes their place.";

        protected override AutomationResult Execute(AutomationContext context, FromModelInput input)
        {
            string modelPath = context.Resolve(input.Model);
            if (!File.Exists(modelPath) || !modelPath.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Model}' is not a Model resource (a .model.json file).");
            float cell = input.CellSize ?? 1f;
            if (!(cell > 0f)) throw new AutomationException("The cell size must be above 0.");
            System.Numerics.Vector3 offset = input.Position is { Length: 3 } place ? new(place[0], place[1], place[2])
                : input.Position == null ? System.Numerics.Vector3.Zero : throw new AutomationException("The position is [x, y, z].");
            // Its cooked model, or else its source read afresh; nothing is written (a cooked copy of a big
            // scene would add hundreds of megabytes to the project for one look at its ground).
            Genesis.Runtime.Modeling.GModelAsset model = Genesis.Runtime.Modeling.StudioModelResourceLoader.LoadReadOnly(modelPath);
            if (!model.HasRenderableMeshes) model = Genesis.Runtime.Modeling.StudioModelResourceLoader.ImportSource(modelPath);
            List<string> names = [.. model.Materials.Select(material => material?.Name ?? string.Empty)];

            int Material(string name) => names.FindIndex(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) is int found and >= 0 ? found
                : throw new AutomationException($"The model has no material '{name}'. Its materials: {string.Join(", ", names)}.");
            var ground = new HashSet<int>(input.Surfaces is { Count: > 0 } surfaces ? surfaces.Select(Material) : Enumerable.Range(0, Math.Max(1, names.Count)));
            List<FromModelLayer> layers = input.Layers is { Count: > 0 } given ? given
                : [.. ground.Order().Select(index => new FromModelLayer { Name = index < names.Count ? names[index] : "Ground", Materials = index < names.Count ? [names[index]] : [] })];
            if (layers.Count > TerrainAsset.MaximumPaintLayers)
                throw new AutomationException($"A terrain has at most {TerrainAsset.MaximumPaintLayers} paint layers; give fewer, each with several materials.");
            var layerOf = new Dictionary<int, int>();
            for (int layer = 0; layer < layers.Count; layer++)
                foreach (string name in layers[layer].Materials ?? [])
                    layerOf[Material(name)] = layer;

            // The ground's extent, then every chosen triangle laid over the grid from above.
            var triangles = new List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, int Material)>();
            foreach (Genesis.Runtime.Modeling.GModelMesh mesh in model.Meshes)
            {
                if (mesh?.Vertices is not { Length: > 0 } vertices || mesh.Lod != 0 || mesh.Indices == null) continue;
                int count = mesh.Indices.Length / 3;
                bool perTriangle = mesh.TriangleMaterialIndices?.Length == count;
                for (int triangle = 0; triangle < count; triangle++)
                {
                    int material = perTriangle ? mesh.TriangleMaterialIndices![triangle] : mesh.MaterialIndex;
                    if (!ground.Contains(material)) continue;
                    triangles.Add((vertices[mesh.Indices[triangle * 3]].Position + offset, vertices[mesh.Indices[triangle * 3 + 1]].Position + offset,
                        vertices[mesh.Indices[triangle * 3 + 2]].Position + offset, material));
                }
            }
            if (triangles.Count == 0) throw new AutomationException("The model has no triangles of those materials.");
            float minX = triangles.Min(t => MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X))), maxX = triangles.Max(t => MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)));
            float minZ = triangles.Min(t => MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z))), maxZ = triangles.Max(t => MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z)));
            float originX = MathF.Floor(minX / cell) * cell, originZ = MathF.Floor(minZ / cell) * cell;
            int resX = (int)MathF.Ceiling((maxX - originX) / cell) + 1, resZ = (int)MathF.Ceiling((maxZ - originZ) / cell) + 1;
            if (resX > 4097 || resZ > 4097)
                throw new AutomationException($"At {cell} m a sample the ground needs {resX} x {resZ} samples; the most is 4097 each way. Use a larger cell size.");
            resX = Math.Max(resX, 17); resZ = Math.Max(resZ, 17);

            var heights = new float[resX * resZ];
            var source = new int[heights.Length];
            Array.Fill(heights, float.NaN);
            foreach ((System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c, int material) in triangles)
            {
                float area = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
                if (MathF.Abs(area) < 1e-9f) continue; // seen edge-on from above: a wall, not ground
                int x0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - originX) / cell));
                int x1 = Math.Min(resX - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - originX) / cell));
                int z0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - originZ) / cell));
                int z1 = Math.Min(resZ - 1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - originZ) / cell));
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = originX + x * cell, pz = originZ + z * cell;
                        float u = ((b.X - px) * (c.Z - pz) - (c.X - px) * (b.Z - pz)) / area;
                        float v = ((c.X - px) * (a.Z - pz) - (a.X - px) * (c.Z - pz)) / area;
                        float w = 1f - u - v;
                        if (u < -1e-5f || v < -1e-5f || w < -1e-5f) continue;
                        float y = u * a.Y + v * b.Y + w * c.Y;
                        int at = z * resX + x;
                        if (float.IsNaN(heights[at]) || y > heights[at]) { heights[at] = y; source[at] = material; }
                    }
            }

            // Samples no surface covers take the height and paint of the nearest one that it does.
            var queue = new Queue<int>();
            for (int i = 0; i < heights.Length; i++) if (!float.IsNaN(heights[i])) queue.Enqueue(i);
            int covered = queue.Count;
            if (covered == 0) throw new AutomationException("No sample lies under those surfaces; use a smaller cell size.");
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(), x = i % resX, z = i / resX;
                foreach ((int nx, int nz) in new[] { (x - 1, z), (x + 1, z), (x, z - 1), (x, z + 1) })
                {
                    if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;
                    int n = nz * resX + nx;
                    if (!float.IsNaN(heights[n])) continue;
                    heights[n] = heights[i]; source[n] = source[i]; queue.Enqueue(n);
                }
            }

            float low = heights.Min(), high = heights.Max();
            var terrain = new TerrainAsset(resX, resZ, cell, originX, originZ, MathF.Floor(low) - 1f, MathF.Ceiling(high) + 1f);
            for (int z = 0; z < resZ; z++)
                for (int x = 0; x < resX; x++)
                {
                    int at = z * resX + x;
                    terrain.SetHeight(x, z, heights[at]);
                    terrain.SetLayerAt(at, layerOf.GetValueOrDefault(source[at], 0));
                }

            string full = context.Resources.CreateResource(context.Resolve(input.Folder ?? "Assets/Terrain"), ResourceKind.Terrain, input.Name);
            JsonObject settings = Settings(full);
            settings["schemaVersion"] = 2;
            settings["resolution"] = new JsonArray(resX, resZ);
            settings["cellSize"] = cell;
            settings["minHeight"] = terrain.MinHeight;
            settings["maxHeight"] = terrain.MaxHeight;
            settings["preset"] = TerrainPreset.Flatlands.ToString();
            settings["seedProfile"] = TerrainPreset.Flatlands.ToString();
            settings["sourceModel"] = context.Relative(modelPath);
            settings["layers"] = new JsonArray([.. layers.Select(layer =>
            {
                float[] colour = layer.Color is { Length: 3 } own ? own : Colour(model, layer.Materials?.Select(Material).FirstOrDefault(-1) ?? -1);
                return (JsonNode?)new JsonObject { ["name"] = layer.Name, ["color"] = new JsonArray(colour[0], colour[1], colour[2]) };
            })]);
            File.WriteAllText(full, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            terrain.Save(full + ".gterrain");

            var changed = new List<string> { context.Relative(full), context.Relative(full + ".gterrain") };
            if (input.HideSurfaces == true)
            {
                JsonObject descriptor = JsonNode.Parse(File.ReadAllText(modelPath)) as JsonObject ?? throw new AutomationException("The model's settings are not a JSON object.");
                var hidden = new List<string>((descriptor["hiddenMaterials"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? string.Empty));
                foreach (int index in ground.Order())
                    if (index < names.Count && !hidden.Contains(names[index], StringComparer.OrdinalIgnoreCase)) hidden.Add(names[index]);
                descriptor["hiddenMaterials"] = new JsonArray([.. hidden.Select(name => (JsonNode?)name)]);
                ResourceBackupService.BackupBeforeOverwrite(modelPath);
                File.WriteAllText(modelPath, descriptor.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                changed.Add(context.Relative(modelPath));
            }
            JsonObject data = Describe(context, full, terrain);
            data["coveredSamples"] = covered;
            data["samples"] = heights.Length;
            return AutomationResult.Success($"Made {Path.GetFileName(full)} from {triangles.Count} triangles: {resX} x {resZ} samples, {covered * 100 / heights.Length}% under the surfaces.",
                data, [.. changed]);
        }

        private static float[] Colour(Genesis.Runtime.Modeling.GModelAsset model, int material) =>
            material >= 0 && material < model.Materials.Count && model.Materials[material] is { } found
                ? [Math.Clamp(found.BaseColor.X, 0f, 1f), Math.Clamp(found.BaseColor.Y, 0f, 1f), Math.Clamp(found.BaseColor.Z, 0f, 1f)]
                : [0.33f, 0.52f, 0.26f];
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
        [Description("The brush: Raise, Lower, Smooth, Flatten (to the ground where the stroke starts, or flattenHeight), InFill (fills hollows to their rim), Paint (lays a paint layer), Hole (cuts the ground away) or FillHole (puts it back)."), RequiredInput]
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

    public sealed class HoleInput
    {
        [Description("The Terrain resource, project-relative."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("Where to cut: world points [x, z]; one point cuts a round hole, more cut along the line between them (a trench, a crevasse).")]
        public List<float[]>? Points { get; set; }
        [Description("Radius of the hole round each point, metres (default 2).")] public float? Radius { get; set; }
        [Description("Put the ground back there instead of cutting it (default false).")] public bool? Fill { get; set; }
        [Description("Fill every hole the terrain has first (default false); alone, it removes them all.")] public bool? Clear { get; set; }
    }

    public sealed class TerrainSetHole : AutomationCommand<HoleInput>
    {
        public override string Name => "terrain.setHole";
        public override string Description => "Cuts holes in a terrain (or fills them): the ground there is not drawn, light falls through, bodies fall through and agents do not walk there; its grass and scattered copies keep off. As the Terrain editor's Hole brush.";

        protected override AutomationResult Execute(AutomationContext context, HoleInput input)
        {
            string full = Terrain(context, input.Terrain);
            int count = input.Points?.Count ?? 0;
            if (count == 0 && input.Clear != true) throw new AutomationException("Give points to cut (or fill), or clear: true to fill every hole.");
            float radius = input.Radius ?? 2f;
            if (!(radius > 0f)) throw new AutomationException("The radius must be above 0.");
            TerrainAsset terrain = Load(full);
            int before = terrain.HoleCount;
            if (input.Clear == true) terrain.ClearHoles();
            if (count > 0)
            {
                System.Numerics.Vector2[] points = [.. input.Points!.Select(Point)];
                TerrainSculptBrush brush = input.Fill == true ? TerrainSculptBrush.FillHole : TerrainSculptBrush.Hole;
                var dab = new TerrainSculptDab { Radius = radius };
                float spacing = Math.Max(radius * 0.25f, terrain.CellSize * 0.5f);
                TerrainSculpt.Apply(terrain, brush, points[0].X, points[0].Y, dab);
                for (int i = 1; i < points.Length; i++)
                {
                    int steps = Math.Max(1, (int)MathF.Ceiling(System.Numerics.Vector2.Distance(points[i - 1], points[i]) / spacing));
                    for (int step = 1; step <= steps; step++)
                    {
                        var at = System.Numerics.Vector2.Lerp(points[i - 1], points[i], step / (float)steps);
                        TerrainSculpt.Apply(terrain, brush, at.X, at.Y, dab);
                    }
                }
            }

            string binary = full + ".gterrain";
            if (File.Exists(binary)) ResourceBackupService.BackupBeforeOverwrite(binary);
            terrain.Save(binary);
            JsonObject data = Describe(context, full, terrain);
            data["holeCells"] = terrain.HoleCount;
            return AutomationResult.Success($"The terrain has {terrain.HoleCount} cells cut away (was {before}).", data, context.Relative(binary));
        }
    }
}

using System.ComponentModel;
using System.Drawing;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Modeling;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// Models built from parts, as the Model editor's kitbash and the templates build them: primitives
/// with their own place, turn, size and colour, stored in the Model resource and baked by the
/// engine's own model loader, which is asked to read the result back before it is kept.
/// </summary>
public static class ModelCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new ModelCreate(), new ModelInfo()];

    private static readonly string[] Primitives = ["Cube", "Sphere", "Cylinder", "Cone", "Pyramid", "Torus", "Quad"];

    public sealed class PartInput
    {
        [Description("Cube, Sphere, Cylinder, Cone, Pyramid, Torus or Quad (default Cube)."), RequiredInput] public string Primitive { get; set; } = "Cube";
        [Description("The part's name.")] public string? Name { get; set; }
        [Description("Its centre [x, y, z], metres (y is up).")] public float[]? Position { get; set; }
        [Description("Its turn [x, y, z], degrees.")] public float[]? Rotation { get; set; }
        [Description("Its size [x, y, z], metres (a unit primitive is 1 across).")] public float[]? Scale { get; set; }
        [Description("Its colour: #RRGGBB or a name (default a soft blue-grey).")] public string? Color { get; set; }
    }

    public sealed class CreateInput
    {
        [Description("The model's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Models).")] public string? Folder { get; set; }
        [Description("The parts, each a primitive with its place, turn, size and colour."), RequiredInput] public List<PartInput> Parts { get; set; } = [];
        [Description("Glow, 0 upwards (default 0).")] public float? Emissive { get; set; }
        [Description("Draw both sides of every face (thin things: leaves, banners).")] public bool? DoubleSided { get; set; }
    }

    public sealed class ModelCreate : AutomationCommand<CreateInput>
    {
        public override string Name => "model.create";
        public override string Description => "Makes a Model from primitive parts (cube, sphere, cylinder, cone, pyramid, torus, quad), each placed, turned, sized and coloured, as the Model editor's kitbash does; the engine bakes it into one mesh. Place it with an Object (object.create with model).";

        protected override AutomationResult Execute(AutomationContext context, CreateInput input)
        {
            if (input.Parts.Count == 0) throw new AutomationException("A model needs at least one part.");
            if (input.Parts.Count > 1000) throw new AutomationException("At most 1000 parts.");
            var parts = new JArray();
            for (int i = 0; i < input.Parts.Count; i++)
            {
                PartInput part = input.Parts[i];
                string primitive = Primitives.FirstOrDefault(p => p.Equals(part.Primitive?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new AutomationException($"'{part.Primitive}' is not a primitive: {string.Join(", ", Primitives)}.");
                Color color = ImageCommands.ParseColor(part.Color, Color.FromArgb(158, 173, 217));
                parts.Add(new JObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(part.Name) ? $"{primitive} {i + 1}" : part.Name,
                    ["primitive"] = primitive,
                    ["position"] = Vector(part.Position, 0, "position"),
                    ["rotation"] = Vector(part.Rotation, 0, "rotation"),
                    ["scale"] = Vector(part.Scale, 1, "scale"),
                    ["color"] = new JArray(Math.Round(color.R / 255.0, 4), Math.Round(color.G / 255.0, 4), Math.Round(color.B / 255.0, 4)),
                });
            }

            string full = context.Resources.CreateResource(context.Resolve(input.Folder ?? "Assets/Models"), ResourceKind.Model, input.Name);
            JObject document = JObject.Parse(File.ReadAllText(full));
            document["parts"] = parts;
            if (input.Emissive is float emissive) document["emissive"] = Math.Max(0, emissive);
            if (input.DoubleSided is bool doubleSided) document["doubleSided"] = doubleSided;
            File.WriteAllText(full, document.ToString(Newtonsoft.Json.Formatting.Indented));

            GModelAsset asset;
            try
            {
                asset = StudioModelResourceLoader.LoadReadOnly(full);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                throw new AutomationException("The engine could not read the model: " + exception.Message);
            }
            int vertices = asset.Meshes.Sum(m => m.Vertices?.Length ?? 0);
            if (vertices == 0) throw new AutomationException("The engine baked the parts into nothing.");
            return AutomationResult.Success($"Made {Path.GetFileName(full)}: {input.Parts.Count} part(s), {vertices} vertices.", Describe(context, full, asset), context.Relative(full));
        }

        private static JArray Vector(float[]? values, float fallback, string what)
        {
            if (values is null) return new JArray(fallback, fallback, fallback);
            if (values.Length != 3 || values.Any(v => !float.IsFinite(v))) throw new AutomationException($"A part's {what} is [x, y, z].");
            return new JArray(values[0], values[1], values[2]);
        }
    }

    private static JsonObject Describe(AutomationContext context, string full, GModelAsset asset)
    {
        System.Numerics.Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (GModelMesh mesh in asset.Meshes)
            foreach (var vertex in mesh.Vertices ?? [])
            {
                min = System.Numerics.Vector3.Min(min, vertex.Position);
                max = System.Numerics.Vector3.Max(max, vertex.Position);
            }
        bool any = min.X <= max.X;
        return new JsonObject
        {
            ["path"] = context.Relative(full),
            ["name"] = ResourceNames.Name(context.Root, full),
            ["meshes"] = asset.Meshes.Count,
            ["vertices"] = asset.Meshes.Sum(m => m.Vertices?.Length ?? 0),
            ["triangles"] = asset.Meshes.Sum(m => (m.Indices?.Length ?? 0) / 3),
            ["boundsMin"] = any ? new JsonArray(Math.Round(min.X, 3), Math.Round(min.Y, 3), Math.Round(min.Z, 3)) : null,
            ["boundsMax"] = any ? new JsonArray(Math.Round(max.X, 3), Math.Round(max.Y, 3), Math.Round(max.Z, 3)) : null,
            ["materials"] = new JsonArray([.. asset.Materials.Select(m => (JsonNode?)m.Name)]),
            ["animations"] = new JsonArray([.. asset.Animations.Select(a => (JsonNode?)a.Name)]),
            ["rigged"] = asset.Rig?.IsValid == true,
        };
    }

    public sealed class InfoInput
    {
        [Description("The Model resource, project-relative (Assets/Models/Name.model.json)."), RequiredInput] public string Model { get; set; } = string.Empty;
    }

    public sealed class ModelInfo : AutomationCommand<InfoInput>
    {
        public override string Name => "model.info";
        public override string Description => "Describes a Model as the engine loads it: meshes, vertices, triangles, bounds, materials, animations and whether it is rigged.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, InfoInput input)
        {
            string full = context.Resolve(input.Model);
            if (!File.Exists(full) || !full.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Model}' is not a Model resource (a .model.json file).");
            GModelAsset asset = StudioModelResourceLoader.LoadReadOnly(full);
            JsonObject data = Describe(context, full, asset);
            return AutomationResult.Success($"{data["meshes"]} mesh(es), {data["vertices"]} vertices, {data["triangles"]} triangles.", data);
        }
    }
}

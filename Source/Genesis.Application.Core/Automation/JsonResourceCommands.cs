using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// Settings of any JSON resource (particle effects, physics materials, shaders, audio, models...)
/// set by name, and particle effects made from the engine's presets, as the Particle editor's gallery
/// does. A change is read back by the engine's own loader for that kind before it is saved, so a
/// value of the wrong kind is refused rather than written.
/// </summary>
public static class JsonResourceCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new ResourceSet(), new ParticleCreate()];

    // As the editors save JSON resources: camelCase names, enums by name, indented.
    private static readonly JsonSerializerOptions EditorJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Throws when the engine would not read <paramref name="text"/> as this kind of resource.</summary>
    private static void Validate(string full, string text)
    {
        ResourceKind? kind = ResourceDefinitions.FromPath(full)?.Kind;
        try
        {
            switch (kind)
            {
                case ResourceKind.Particle:
                    _ = JsonSerializer.Deserialize<ParticleConfig>(text, ReadJson);
                    break;
                case ResourceKind.Room:
                    string temporary = Path.Combine(Path.GetDirectoryName(full)!, "." + Guid.NewGuid().ToString("N") + ".room.json");
                    try
                    {
                        File.WriteAllText(temporary, text);
                        _ = RoomAssetLoader.Parse(temporary);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    break;
                default:
                    _ = JToken.Parse(text);
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or Newtonsoft.Json.JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new AutomationException($"The engine would not read that as a {kind?.ToString() ?? "resource"}: {exception.Message}");
        }
    }

    public sealed class SetInput
    {
        [Description("A JSON resource, project-relative (Assets/Particles/Fire.particle.json, a .physics.json, .shader.json, .audio.json, .model.json...)."), RequiredInput]
        public string Path { get; set; } = string.Empty;
        [Description("Fields to set, as named in the file (resource.read shows them); objects merge, arrays are replaced."), RequiredInput]
        public JsonObject Values { get; set; } = new();
    }

    public sealed class ResourceSet : AutomationCommand<SetInput>
    {
        public override string Name => "resource.set";
        public override string Description => "Sets fields of any JSON resource (particle effect, physics material, shader, audio, model settings...); the engine must still read it, so a value of the wrong kind is refused. The file is backed up first.";

        protected override AutomationResult Execute(AutomationContext context, SetInput input)
        {
            string full = context.Resolve(input.Path);
            if (!File.Exists(full)) throw new AutomationException($"'{input.Path}' does not exist.");
            if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Path}' is not a JSON resource; scripts are written with script.write or object.setEvent.");
            ResourceKind? kind = ResourceDefinitions.FromPath(full)?.Kind;
            // These have commands that keep what goes with them (event scripts, the heights file) in step.
            string[] guarded = kind switch
            {
                ResourceKind.GameObject => ["components", "events"],
                ResourceKind.Terrain => ["resolution", "cellSize", "minHeight", "maxHeight"],
                _ => [],
            };
            string? refused = input.Values.Select(pair => pair.Key).FirstOrDefault(key => guarded.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (refused is not null)
                throw new AutomationException(kind == ResourceKind.GameObject
                    ? $"An Object's '{refused}' are changed with object.setComponent, object.setEvent and their remove commands."
                    : $"A terrain's '{refused}' goes with its heights; make a new one with terrain.create.");

            JObject root = JObject.Parse(File.ReadAllText(full));
            root.Merge(JObject.Parse(input.Values.ToJsonString()), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            string text = root.ToString(Newtonsoft.Json.Formatting.Indented);
            Validate(full, text);
            ResourceBackupService.BackupBeforeOverwrite(full);
            File.WriteAllText(full, text);
            return AutomationResult.Success($"Set {input.Values.Count} field(s) of {System.IO.Path.GetFileName(full)}.", null, context.Relative(full));
        }
    }

    public sealed class ParticleCreateInput
    {
        [Description("The effect's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Particles).")] public string? Folder { get; set; }
        [Description("The preset to start from (see the command's description); default a plain emitter.")] public string? Preset { get; set; }
        [Description("Fields to change after the preset, as named in the file (emitRate, speed, startSize, startColor...).")] public JsonObject? Values { get; set; }
    }

    public sealed class ParticleCreate : AutomationCommand<ParticleCreateInput>
    {
        public override string Name => "particle.create";
        public override string Description => "Makes a Particle effect from one of the engine's presets, as the Particle editor's gallery does, with any settings changed. Presets: "
            + string.Join(", ", ParticlePresets.Names) + ". Attach it to an Object with object.setComponent (ParticleComponent, Asset).";

        protected override AutomationResult Execute(AutomationContext context, ParticleCreateInput input)
        {
            string? preset = input.Preset is null ? null
                : ParticlePresets.Names.FirstOrDefault(name => name.Equals(input.Preset.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new AutomationException($"'{input.Preset}' is not a preset. Presets: {string.Join(", ", ParticlePresets.Names)}.");
            string full = context.Resources.CreateResource(context.Resolve(input.Folder ?? "Assets/Particles"), ResourceKind.Particle, input.Name);
            string text = preset is null ? File.ReadAllText(full) : JsonSerializer.Serialize(ParticlePresets.Create(preset), EditorJson);
            if (input.Values is { Count: > 0 })
            {
                JObject root = JObject.Parse(text);
                root.Merge(JObject.Parse(input.Values.ToJsonString()), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                text = root.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            try
            {
                Validate(full, text);
            }
            catch (AutomationException)
            {
                // Refused: the resource just made goes again, metadata and all.
                File.Delete(full);
                if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
                throw;
            }
            File.WriteAllText(full, text);
            return AutomationResult.Success($"Made {System.IO.Path.GetFileName(full)}{(preset is null ? string.Empty : " from " + preset)}.",
                new JsonObject { ["path"] = context.Relative(full), ["name"] = ResourceNames.Name(context.Root, full) }, context.Relative(full));
        }
    }
}

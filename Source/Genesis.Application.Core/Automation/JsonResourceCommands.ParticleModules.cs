using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Core.Automation;

public static partial class JsonResourceCommands
{
    public sealed class ParticleModuleInput
    {
        [Description("The Particle resource, project-relative (Assets/Particles/Camp Fire.particle.json)."), RequiredInput]
        public string Path { get; set; } = string.Empty;
        [Description("The emitter, by name (default: the effect's first).")]
        public string? Emitter { get; set; }
        [Description("list (the default: the stack with every field), add, remove, on, off, reset or set.")]
        public string? Action { get; set; }
        [Description("The module, as list shows it: spawn.rate, spawn.burst, init.lifetime, update.gravity, update.colour, update.size, render.trail...")]
        public string? Module { get; set; }
        [Description("With set: the field's key, as list shows it (rate, gradient, curve...).")]
        public string? Field { get; set; }
        [Description("With set: the value, as JSON text: 60, true, \"Ring\", {\"r\":1,\"g\":0.5,\"b\":0,\"a\":1}, [0.8, 1.4] for a random range, "
            + "[{\"position\":0,\"color\":{\"r\":1,\"g\":1,\"b\":1,\"a\":1}}, ...] for gradient keys, [{\"time\":0,\"value\":0,\"smooth\":true}, ...] for curve points.")]
        public string? Value { get; set; }
    }

    /// <summary>The Particle editor's module stack, by command: the same view over the same runtime settings.</summary>
    public sealed class ParticleModule : AutomationCommand<ParticleModuleInput>
    {
        public override string Name => "particle.module";
        public override string Description => "The Particle editor's module stack for one emitter of a Particle effect: list its Spawn, Initialise, Update and Render modules "
            + "with their fields, add or remove a module, switch one on or off, reset it, or set a field (curves take points). It changes the effect's ordinary "
            + "settings as the editor does, so a game plays it with nothing new. The file is backed up first.";

        protected override AutomationResult Execute(AutomationContext context, ParticleModuleInput input)
        {
            string full = context.Resolve(input.Path);
            if (!File.Exists(full)) throw new AutomationException($"'{input.Path}' does not exist.");
            if (ResourceDefinitions.FromPath(full)?.Kind != ResourceKind.Particle)
                throw new AutomationException($"'{input.Path}' is not a Particle resource.");
            ParticleConfig effect;
            try
            {
                effect = JsonSerializer.Deserialize<ParticleConfig>(File.ReadAllText(full), EditorJson)
                    ?? throw new AutomationException($"'{input.Path}' is empty.");
            }
            catch (JsonException error)
            {
                throw new AutomationException($"'{input.Path}' is not a particle effect the engine reads: {error.Message}");
            }
            effect.Emitters ??= [];
            int index = 0;
            if (!string.IsNullOrWhiteSpace(input.Emitter))
            {
                index = Enumerable.Range(0, ParticleEffectEditing.Count(effect))
                    .FirstOrDefault(i => string.Equals(ParticleEffectEditing.Name(effect, i), input.Emitter.Trim(), StringComparison.OrdinalIgnoreCase), -1);
                if (index < 0)
                    throw new AutomationException($"The effect has no emitter '{input.Emitter}'. Emitters: "
                        + string.Join(", ", Enumerable.Range(0, ParticleEffectEditing.Count(effect)).Select(i => ParticleEffectEditing.Name(effect, i))) + ".");
            }
            ParticleConfig emitter = ParticleEffectEditing.Emitter(effect, index);
            ParticleModuleContext stack = new(emitter, effect, effect.Preview2D);
            string action = (input.Action ?? "list").Trim().ToLowerInvariant();
            if (action == "list")
                return AutomationResult.Success($"{ParticleEffectEditing.Name(effect, index)}: {ParticleModuleStack.Entries(stack).Count} modules.",
                    Describe(stack));

            if (string.IsNullOrWhiteSpace(input.Module)) throw new AutomationException("Name the module (list shows them).");
            string id = input.Module.Trim();
            string done;
            try
            {
                ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
                switch (action)
                {
                    case "add": ParticleModuleStack.Add(stack, module.Id); done = "Added " + module.Title; break;
                    case "remove": ParticleModuleStack.Remove(stack, module.Id); done = "Removed " + module.Title; break;
                    case "on": ParticleModuleStack.SetEnabled(stack, module.Id, true); done = "Switched on " + module.Title; break;
                    case "off": ParticleModuleStack.SetEnabled(stack, module.Id, false); done = "Switched off " + module.Title; break;
                    case "reset": ParticleModuleStack.Reset(stack, module.Id); done = "Reset " + module.Title; break;
                    case "set":
                        if (string.IsNullOrWhiteSpace(input.Field) || input.Value is null)
                            throw new AutomationException("set takes a field and a value.");
                        ParticleModuleField field = module.Field(input.Field.Trim());
                        string text = input.Value.Trim();
                        if (field.Kind == ParticleModuleFieldKind.Curve && text.StartsWith('['))
                            ParticleModuleStack.SetCurvePoints(stack, module.Id, field.Key, Parse<List<ParticleCurveKey>>(text));
                        else
                            ParticleModuleStack.SetField(stack, module.Id, field.Key, Value(field, text));
                        done = $"Set {module.Title} {field.Label}";
                        break;
                    default:
                        throw new AutomationException($"'{input.Action}' is not an action: list, add, remove, on, off, reset or set.");
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or JsonException or OverflowException)
            {
                throw new AutomationException(error.Message);
            }

            string saved = JsonSerializer.Serialize(effect, EditorJson);
            Validate(full, saved);
            ResourceBackupService.BackupBeforeOverwrite(full);
            File.WriteAllText(full, saved);
            return AutomationResult.Success($"{done} on {ParticleEffectEditing.Name(effect, index)}.", Describe(stack), context.Relative(full));
        }

        private static object Value(ParticleModuleField field, string text) => field.Kind switch
        {
            ParticleModuleFieldKind.Number => double.Parse(Unquoted(text), NumberStyles.Float, CultureInfo.InvariantCulture),
            ParticleModuleFieldKind.Toggle => bool.Parse(Unquoted(text)),
            ParticleModuleFieldKind.Colour => text.StartsWith('{') ? Parse<ParticleColor>(text) : Hex(Unquoted(text)),
            ParticleModuleFieldKind.Range => text.StartsWith('[')
                ? Parse<double[]>(text) is { Length: 2 } ends ? (object)new ParticleValueRange(ends[0], ends[1]) : throw new FormatException("A range is [lowest, highest].")
                : double.Parse(Unquoted(text), NumberStyles.Float, CultureInfo.InvariantCulture),
            ParticleModuleFieldKind.Gradient => Parse<List<ParticleGradientStop>>(text),
            _ => Unquoted(text),
        };

        private static T Parse<T>(string text) => JsonSerializer.Deserialize<T>(text, ReadJson)
            ?? throw new FormatException("The value is empty.");

        private static string Unquoted(string text) => text.StartsWith('"') ? JsonSerializer.Deserialize<string>(text) ?? string.Empty : text;

        private static ParticleColor Hex(string text)
        {
            string hex = text.TrimStart('#');
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
                throw new FormatException($"'{text}' is not a colour: give {{\"r\",\"g\",\"b\",\"a\"}} (0 to 1) or #RRGGBB.");
            if (hex.Length == 6) packed = (packed << 8) | 0xFF;
            return new ParticleColor((packed >> 24 & 0xFF) / 255f, (packed >> 16 & 0xFF) / 255f, (packed >> 8 & 0xFF) / 255f, (packed & 0xFF) / 255f);
        }

        /// <summary>The stack as list returns it: each module, on or off, with its fields' values.</summary>
        private static JsonObject Describe(ParticleModuleContext stack)
        {
            JsonArray modules = new();
            foreach (ParticleModuleEntry entry in ParticleModuleStack.Entries(stack))
            {
                JsonObject fields = new();
                foreach (ParticleModuleField field in entry.Definition.Fields.Where(field => field.Visible(stack)))
                    fields[field.Key] = FieldValue(stack, field);
                modules.Add(new JsonObject
                {
                    ["id"] = entry.Definition.Id,
                    ["group"] = ParticleModuleCatalog.GroupTitle(entry.Definition.Group),
                    ["title"] = entry.Definition.Title,
                    ["enabled"] = entry.Enabled,
                    ["alwaysOn"] = entry.Definition.Required,
                    ["fields"] = fields,
                });
            }
            JsonArray addable = new();
            foreach (ParticleModuleGroup group in Enum.GetValues<ParticleModuleGroup>())
                foreach (ParticleModuleDefinition module in ParticleModuleStack.Addable(stack, group))
                    addable.Add((JsonNode?)module.Id);
            return new JsonObject { ["modules"] = modules, ["addable"] = addable };
        }

        private static JsonNode? FieldValue(ParticleModuleContext stack, ParticleModuleField field)
        {
            object? value = field.Read(stack);
            switch (value)
            {
                case ParticleValueRange range: return new JsonArray(range.Minimum, range.Maximum);
                case ParticleColor colour: return new JsonObject { ["r"] = colour.R, ["g"] = colour.G, ["b"] = colour.B, ["a"] = colour.A };
                case List<ParticleGradientStop> stops:
                    return JsonSerializer.SerializeToNode(stops, EditorJson);
                case string mode when field.Kind == ParticleModuleFieldKind.Curve:
                    return mode == ParticleModuleCurves.Points
                        ? new JsonObject { ["mode"] = mode, ["points"] = JsonSerializer.SerializeToNode(ParticleModuleCurves.Curve(stack.Emitter, field.Channel).Keys, EditorJson) }
                        : new JsonObject { ["mode"] = mode };
                case string text: return text;
                case bool flag: return flag;
                case double number: return number;
                default: return value is null ? null : JsonSerializer.SerializeToNode(value, EditorJson);
            }
        }
    }
}

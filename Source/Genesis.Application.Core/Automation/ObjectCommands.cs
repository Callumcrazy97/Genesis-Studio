using System.ComponentModel;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Objects;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The Object editor's work as commands: making an Object with an image or a model, its components
/// (physics, light, audio, particles...) and its PGSL events, plus writing library scripts. Objects
/// are composed by the editor's own <see cref="ObjectCompositionModel"/> and their events saved by the
/// runtime's <see cref="ObjectEventStore"/>, so a command leaves an Object exactly as the editor would.
/// </summary>
public static class ObjectCommands
{
    public static IEnumerable<IAutomationCommand> All() =>
        [new ObjectCreate(), new ObjectInfo(), new ObjectSetComponent(), new ObjectRemoveComponent(),
         new ObjectSetEvent(), new ObjectAddRecipe(), new ObjectRemoveEvent(), new ScriptWrite()];

    private const string Extension = ".object.json";

    private static string ObjectFile(AutomationContext context, string path)
    {
        string full = context.Resolve(path);
        if (!File.Exists(full) || !full.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException($"'{path}' is not an Object resource (a .object.json file).");
        return full;
    }

    /// <summary>
    /// A project-relative path as the resource name the editor stores, after checking it is the kind
    /// the component takes. A value that is not a path is taken to be a resource name already.
    /// </summary>
    private static string AssetName(AutomationContext context, string value, ResourceKind? kind)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || !(trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            return trimmed;
        string full = context.Resolve(trimmed);
        if (!File.Exists(full)) throw new AutomationException($"'{value}' does not exist.");
        ResourceKind? found = ResourceDefinitions.FromPath(full)?.Kind;
        if (kind is ResourceKind wanted && found != wanted)
            throw new AutomationException($"'{value}' should be a {ResourceDefinitions.Get(wanted).DisplayName} resource, but it is {(found is ResourceKind kindFound ? "a " + ResourceDefinitions.Get(kindFound).DisplayName : "not a resource")}.");
        return ResourceNames.Name(context.Root, full);
    }

    private static string EventId(string id)
    {
        ObjectEventDefinition? definition = ObjectEventCatalog.Find(id);
        if (definition is null)
            throw new AutomationException($"'{id}' is not an event. Events: {string.Join(", ", ObjectEventCatalog.All.Select(e => e.Id))}.");
        return definition.Id;
    }

    /// <summary>
    /// Writes the Object and its events as the editor's Save does: sorted event list, root bindings
    /// mirrored, and one ScriptComponent naming the Object exactly when it has events.
    /// </summary>
    private static void Save(AutomationContext context, string full, JObject document, IReadOnlyDictionary<string, string> events)
    {
        var composition = new ObjectCompositionModel(document);
        string[] active = [.. events.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal)];
        document["schemaVersion"] = 3;
        document["events"] = new JArray(active);
        composition.SynchronizeLegacyBindings();
        foreach (JObject stale in composition.Components.OfType<JObject>().Where(IsScript).ToList()) stale.Remove();
        if (active.Length > 0)
        {
            composition.Components.Add(new JObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JObject { ["ScriptClass"] = ResourceNames.Name(context.Root, full, ResourceType.Object) },
            });
        }
        ResourceBackupService.BackupBeforeOverwrite(full);
        File.WriteAllText(full, document.ToString(Formatting.Indented));
        ObjectEventStore.Save(full, events);
    }

    private static bool IsScript(JObject component) =>
        string.Equals((string?)component["type"], "ScriptComponent", StringComparison.OrdinalIgnoreCase);

    private static JsonObject Describe(AutomationContext context, string full)
    {
        JObject document = JObject.Parse(File.ReadAllText(full));
        var composition = new ObjectCompositionModel(document);
        var components = new JsonArray();
        foreach (JObject component in composition.Components.OfType<JObject>().Where(c => !IsScript(c)))
        {
            components.Add(new JsonObject
            {
                ["type"] = (string?)component["type"],
                ["enabled"] = (bool?)component["enabled"] ?? true,
                ["props"] = JsonNode.Parse(ObjectCompositionModel.Props(component).ToString(Formatting.None)),
            });
        }
        Dictionary<string, string> events = ObjectEventStore.Load(full);
        return new JsonObject
        {
            ["path"] = context.Relative(full),
            ["name"] = ResourceNames.Name(context.Root, full, ResourceType.Object),
            ["dimension"] = (string?)document["dimension"],
            ["sprite"] = (string?)document["sprite"],
            ["model"] = (string?)document["model"],
            ["components"] = components,
            ["events"] = new JsonArray([.. events.Keys.OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)id)]),
            ["eventFolder"] = context.Relative(ObjectEventStore.FolderFor(full)),
        };
    }

    public sealed class CreateInput
    {
        [Description("The Object's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Objects).")] public string? Folder { get; set; }
        [Description("An Image resource to show (a 2D Object), project-relative.")] public string? Sprite { get; set; }
        [Description("A Model resource to show (a 3D Object), project-relative.")] public string? Model { get; set; }
        [Description("Solid: other instances collide with it (default false).")] public bool? Solid { get; set; }
        [Description("Visible (default true).")] public bool? Visible { get; set; }
        [Description("Kept when the room changes (default false).")] public bool? Persistent { get; set; }
        [Description("Event scripts to start with: event id (Create, Step, Draw, DrawGui, KeyPressed, Collision, Alarm0...) to PGSL code. With recipes, this code comes after theirs, so it can change their numbers.")]
        public Dictionary<string, string>? Events { get; set; }
        [Description("Behaviour recipes to add, as the Object editor's Behaviour cards do (ordinary PGSL appended to the events): " +
            "FirstPerson, ThirdPerson, Spin3D, Float3D for 3D; MoveArrows, Platformer, Patrol, Chase, Projectile, Wrap, Collectible, Spin, Float for 2D; Timed for both. " +
            "object.info lists the ones that suit an Object.")]
        public List<string>? Recipes { get; set; }
        [Description("A 3D Object even without a model (a first-person player, a camera rig, a trigger). A model or a 3D-only recipe makes it 3D anyway.")]
        public bool? ThreeD { get; set; }
    }

    /// <summary>The recipe with this id, or a mistake that lists the ids there are.</summary>
    private static ObjectBehaviourRecipe Recipe(string id) =>
        ObjectBehaviourRecipes.All.FirstOrDefault(recipe => recipe.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new AutomationException($"'{id}' is not a behaviour recipe. Recipes: {string.Join(", ", ObjectBehaviourRecipes.All.Select(r => r.Id))}.");

    /// <summary>Appends a recipe to events as the Object editor's card does; false when it is already there.</summary>
    private static bool AddRecipe(Dictionary<string, string> events, ObjectBehaviourRecipe recipe)
    {
        bool added = false;
        foreach ((string eventId, string block) in recipe.Events)
        {
            string existing = events.GetValueOrDefault(eventId) ?? string.Empty;
            if (existing.Contains(recipe.Marker, StringComparison.Ordinal)) continue;
            events[eventId] = ObjectBehaviourRecipes.Append(existing, block);
            added = true;
        }
        return added;
    }

    public sealed class ObjectCreate : AutomationCommand<CreateInput>
    {
        public override string Name => "object.create";
        public override string Description => "Makes an Object, as New › Object and the Object editor do: an image (2D) or a model (3D), solid/visible, and its first PGSL events.";

        protected override AutomationResult Execute(AutomationContext context, CreateInput input)
        {
            if (!string.IsNullOrWhiteSpace(input.Sprite) && !string.IsNullOrWhiteSpace(input.Model))
                throw new AutomationException("An Object shows an image or a model, not both.");
            string sprite = string.IsNullOrWhiteSpace(input.Sprite) ? string.Empty : AssetName(context, input.Sprite, ResourceKind.Image);
            string model = string.IsNullOrWhiteSpace(input.Model) ? string.Empty : AssetName(context, input.Model, ResourceKind.Model);
            ObjectBehaviourRecipe[] recipes = [.. (input.Recipes ?? []).Select(Recipe)];
            bool threeD = model.Length > 0 || input.ThreeD == true || recipes.Any(recipe => recipe.ThreeD && !recipe.TwoD);
            if (threeD && sprite.Length > 0) throw new AutomationException("A 3D Object shows a model, not an image.");
            // Recipes first, then the caller's own code after them, so that code can change a recipe's numbers.
            var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectBehaviourRecipe recipe in recipes)
            {
                if (threeD ? !recipe.ThreeD : !recipe.TwoD)
                    throw new AutomationException($"The recipe {recipe.Id} is for {(recipe.ThreeD ? "3D" : "2D")} Objects.");
                AddRecipe(events, recipe);
            }
            foreach ((string given, string code) in input.Events ?? [])
            {
                string id = EventId(given);
                events[id] = events.TryGetValue(id, out string? recipeCode) ? ObjectBehaviourRecipes.Append(recipeCode, code) : code;
            }

            string full = context.Resources.CreateResource(context.Resolve(input.Folder ?? "Assets/Objects"), ResourceKind.GameObject, input.Name);
            JObject document = JObject.Parse(File.ReadAllText(full));
            var composition = new ObjectCompositionModel(document);
            if (threeD)
            {
                document["dimension"] = "ThreeD";
                composition.Remove("SpriteComponent");
                if (model.Length > 0) composition.SetAsset("ModelRendererComponent", model);
            }
            else if (sprite.Length > 0)
            {
                composition.SetAsset("SpriteComponent", sprite);
            }
            document["visible"] = input.Visible ?? true;
            document["solid"] = input.Solid ?? false;
            if (input.Persistent == true) document["persistent"] = true;
            Save(context, full, document, events);
            return AutomationResult.Success($"Made {ResourceNames.Name(context.Root, full, ResourceType.Object)}.", Describe(context, full),
                [context.Relative(full), .. events.Keys.Select(id => context.Relative(ObjectEventStore.PathFor(full, id)))]);
        }
    }

    public sealed class ObjectInput
    {
        [Description("The Object resource, project-relative (Assets/Objects/Name.object.json)."), RequiredInput] public string Object { get; set; } = string.Empty;
    }

    public sealed class ObjectInfo : AutomationCommand<ObjectInput>
    {
        public override string Name => "object.info";
        public override string Description => "Describes an Object: what it shows, its components and their settings, and its events.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, ObjectInput input)
        {
            string full = ObjectFile(context, input.Object);
            JsonObject data = Describe(context, full);
            data["components"]!.AsArray().Add(new JsonObject { ["available"] = new JsonArray([.. ObjectCompositionModel.Definitions.Where(d => d.Removable).Select(d => (JsonNode?)d.Type)]) });
            bool threeD = string.Equals((string?)data["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase);
            data["recipes"] = new JsonArray([.. ObjectBehaviourRecipes.For(threeD).Select(recipe => (JsonNode?)new JsonObject
            {
                ["id"] = recipe.Id,
                ["title"] = recipe.Title,
                ["description"] = recipe.Description,
            })]);
            return AutomationResult.Success($"{data["name"]}: {data["components"]!.AsArray().Count - 1} components, {data["events"]!.AsArray().Count} events.", data);
        }
    }

    public sealed class SetComponentInput
    {
        [Description("The Object resource, project-relative."), RequiredInput] public string Object { get; set; } = string.Empty;
        [Description("The component: SpriteComponent, ModelRendererComponent, ModelAnimatorComponent, PhysicsComponent, PointLightComponent, AudioComponent, ParticleComponent, MaterialComponent, ShaderComponent, ThirdPersonCameraComponent, NavMeshAgentComponent, CrowdAgentComponent, ModelMorphComponent."), RequiredInput]
        public string Type { get; set; } = string.Empty;
        [Description("Settings to change, as named in object.info (Asset, Radius, Intensity, Preset, Volume...); asset paths become resource names.")]
        public JsonObject? Values { get; set; }
        [Description("Turn the component on or off.")] public bool? Enabled { get; set; }
    }

    public sealed class ObjectSetComponent : AutomationCommand<SetComponentInput>
    {
        public override string Name => "object.setComponent";
        public override string Description => "Adds a component to an Object, or changes one it has; only the settings given change.";

        protected override AutomationResult Execute(AutomationContext context, SetComponentInput input)
        {
            string full = ObjectFile(context, input.Object);
            ObjectComponentDefinition definition = Definition(input.Type);
            JObject document = JObject.Parse(File.ReadAllText(full));
            var composition = new ObjectCompositionModel(document);
            JObject component = composition.Ensure(definition.Type);
            foreach ((string key, JsonNode? value) in input.Values ?? [])
            {
                JToken token = value is null ? JValue.CreateNull() : JToken.Parse(value.ToJsonString());
                if (key.Equals(definition.AssetProperty, StringComparison.OrdinalIgnoreCase) && token.Type == JTokenType.String)
                    token = AssetName(context, (string)token!, definition.AssetKind);
                composition.SetProperty(definition.Type, key, token);
            }
            if (input.Enabled is bool enabled) composition.SetEnabled(definition.Type, enabled);
            if (definition.Type == "ModelRendererComponent") document["dimension"] = "ThreeD";
            Save(context, full, document, ObjectEventStore.Load(full));
            return AutomationResult.Success($"Set {definition.DisplayName} on {ResourceNames.Name(context.Root, full, ResourceType.Object)}.",
                JsonNode.Parse(ObjectCompositionModel.Props(component).ToString(Formatting.None)), context.Relative(full));
        }
    }

    private static ObjectComponentDefinition Definition(string type)
    {
        ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !definition.Removable)
            throw new AutomationException($"'{type}' is not a component. Components: {string.Join(", ", ObjectCompositionModel.Definitions.Where(d => d.Removable).Select(d => d.Type))}.");
        return definition;
    }

    public sealed class RemoveComponentInput
    {
        [Description("The Object resource, project-relative."), RequiredInput] public string Object { get; set; } = string.Empty;
        [Description("The component type."), RequiredInput] public string Type { get; set; } = string.Empty;
    }

    public sealed class ObjectRemoveComponent : AutomationCommand<RemoveComponentInput>
    {
        public override string Name => "object.removeComponent";
        public override string Description => "Removes a component from an Object (the Object is backed up first).";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, RemoveComponentInput input)
        {
            string full = ObjectFile(context, input.Object);
            ObjectComponentDefinition definition = Definition(input.Type);
            JObject document = JObject.Parse(File.ReadAllText(full));
            if (!new ObjectCompositionModel(document).Remove(definition.Type))
                throw new AutomationException($"The Object has no {definition.Type}.");
            Save(context, full, document, ObjectEventStore.Load(full));
            return AutomationResult.Success($"Removed {definition.DisplayName}.", null, context.Relative(full));
        }
    }

    public sealed class SetEventInput
    {
        [Description("The Object resource, project-relative."), RequiredInput] public string Object { get; set; } = string.Empty;
        [Description("The event: Create, Step, Draw, DrawGui, KeyPressed, KeyHeld, Collision, Alarm0-11, UserEvent0-7, Destroy, RoomStart..."), RequiredInput]
        public string Event { get; set; } = string.Empty;
        [Description("The event's whole PGSL code."), RequiredInput] public string Code { get; set; } = string.Empty;
    }

    public sealed class ObjectSetEvent : AutomationCommand<SetEventInput>
    {
        public override string Name => "object.setEvent";
        public override string Description => "Writes one of an Object's PGSL events (adding it if new); the script check's findings for it come back with the result.";

        protected override AutomationResult Execute(AutomationContext context, SetEventInput input)
        {
            string full = ObjectFile(context, input.Object);
            string id = EventId(input.Event);
            if (string.IsNullOrWhiteSpace(input.Code)) throw new AutomationException("The code is empty; use object.removeEvent to remove an event.");
            Dictionary<string, string> events = ObjectEventStore.Load(full);
            string file = ObjectEventStore.PathFor(full, id);
            if (File.Exists(file)) ResourceBackupService.BackupBeforeOverwrite(file);
            events[id] = input.Code;
            Save(context, full, JObject.Parse(File.ReadAllText(full)), events);
            return AutomationResult.Success($"Wrote the {id} event.", new JsonObject { ["script"] = context.Relative(file) }, context.Relative(full), context.Relative(file));
        }
    }

    public sealed class AddRecipeInput
    {
        [Description("The Object resource, project-relative."), RequiredInput] public string Object { get; set; } = string.Empty;
        [Description("The recipe's id (object.info lists those that suit the Object: FirstPerson, ThirdPerson, Platformer...)."), RequiredInput]
        public string Recipe { get; set; } = string.Empty;
    }

    public sealed class ObjectAddRecipe : AutomationCommand<AddRecipeInput>
    {
        public override string Name => "object.addRecipe";
        public override string Description => "Adds a behaviour recipe to an Object, as the Object editor's Behaviour cards do: ordinary PGSL appended to its events " +
            "(walking in first person, a third-person character, a platformer, a patrol...), never replacing code that is there, and not added twice.";

        protected override AutomationResult Execute(AutomationContext context, AddRecipeInput input)
        {
            string full = ObjectFile(context, input.Object);
            ObjectBehaviourRecipe recipe = Recipe(input.Recipe);
            JObject document = JObject.Parse(File.ReadAllText(full));
            bool threeD = string.Equals((string?)document["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase);
            if (threeD ? !recipe.ThreeD : !recipe.TwoD)
                throw new AutomationException($"The recipe {recipe.Id} is for {(recipe.ThreeD ? "3D" : "2D")} Objects, and this one is {(threeD ? "3D" : "2D")}.");
            Dictionary<string, string> events = ObjectEventStore.Load(full);
            if (!AddRecipe(events, recipe))
                return AutomationResult.Success($"{recipe.Title} is already part of the Object.", new JsonObject { ["recipe"] = recipe.Id });
            foreach (string id in recipe.Events.Keys)
            {
                string file = ObjectEventStore.PathFor(full, id);
                if (File.Exists(file)) ResourceBackupService.BackupBeforeOverwrite(file);
            }
            Save(context, full, document, events);
            return AutomationResult.Success($"Added {recipe.Title} to {string.Join(" and ", recipe.Events.Keys)}. It is ordinary code: change its numbers to tune it.",
                new JsonObject { ["recipe"] = recipe.Id, ["events"] = new JsonArray([.. recipe.Events.Keys.Select(id => (JsonNode?)id)]) },
                [context.Relative(full), .. recipe.Events.Keys.Select(id => context.Relative(ObjectEventStore.PathFor(full, id)))]);
        }
    }

    public sealed class RemoveEventInput
    {
        [Description("The Object resource, project-relative."), RequiredInput] public string Object { get; set; } = string.Empty;
        [Description("The event id."), RequiredInput] public string Event { get; set; } = string.Empty;
    }

    public sealed class ObjectRemoveEvent : AutomationCommand<RemoveEventInput>
    {
        public override string Name => "object.removeEvent";
        public override string Description => "Removes one of an Object's events and its script (both backed up first).";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, RemoveEventInput input)
        {
            string full = ObjectFile(context, input.Object);
            string id = EventId(input.Event);
            Dictionary<string, string> events = ObjectEventStore.Load(full);
            if (!events.Remove(id)) throw new AutomationException($"The Object has no {id} event.");
            string file = ObjectEventStore.PathFor(full, id);
            if (File.Exists(file)) ResourceBackupService.BackupBeforeOverwrite(file);
            Save(context, full, JObject.Parse(File.ReadAllText(full)), events);
            return AutomationResult.Success($"Removed the {id} event.", null, context.Relative(full), context.Relative(file));
        }
    }

    public sealed class ScriptWriteInput
    {
        [Description("The PGSL Script, project-relative (Assets/Scripts/Name.pgsl); made if it does not exist."), RequiredInput] public string Path { get; set; } = string.Empty;
        [Description("The script's whole code."), RequiredInput] public string Code { get; set; } = string.Empty;
    }

    public sealed class ScriptWrite : AutomationCommand<ScriptWriteInput>
    {
        public override string Name => "script.write";
        public override string Description => "Writes a library PGSL Script (functions any Object can call), making it if new; the script check's findings come back with the result.";

        protected override AutomationResult Execute(AutomationContext context, ScriptWriteInput input)
        {
            string full = context.Resolve(input.Path);
            if (!full.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase)) throw new AutomationException($"'{input.Path}' is not a .pgsl file.");
            if (ObjectEventCatalog.Exists(System.IO.Path.GetFileNameWithoutExtension(full))
                && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(full))!, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(full)) + Extension)))
                throw new AutomationException($"'{input.Path}' is an Object's event; use object.setEvent.");
            bool made = !File.Exists(full);
            if (made)
            {
                string folder = System.IO.Path.GetDirectoryName(full)!;
                if (!Directory.Exists(folder)) throw new AutomationException($"The folder '{context.Relative(folder)}' does not exist; make it with folder.create.");
                string created = context.Resources.CreateResource(folder, ResourceKind.PgslScript, System.IO.Path.GetFileNameWithoutExtension(full));
                if (!string.Equals(created, full, StringComparison.OrdinalIgnoreCase)) full = created;
            }
            else
            {
                ResourceBackupService.BackupBeforeOverwrite(full);
            }
            File.WriteAllText(full, Genesis.Shared.Assets.ResourceReferenceRewriter.Normalize(context.Root, full, input.Code));
            return AutomationResult.Success(made ? $"Made {context.Relative(full)}." : $"Wrote {context.Relative(full)}.", null, context.Relative(full));
        }
    }
}

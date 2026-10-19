using System.ComponentModel;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The Room editor's placing and setting-up as commands: the instances in a room (add, move,
/// remove, list) and its environment (sky, time, weather, fog...). Rooms are read and written by the
/// engine's own room loader, so what is saved is a valid room or nothing.
/// </summary>
public static class RoomCommands
{
    public static IEnumerable<IAutomationCommand> All() =>
        [new RoomNodes(), new RoomAddInstance(), new RoomMoveInstance(), new RoomRemoveInstance(), new RoomSetEnvironment(), new RoomAddTerrain(), new RoomMakeStart()];

    public sealed class AddTerrainInput
    {
        [Description("The Room resource, project-relative (a 3D room)."), RequiredInput] public string Room { get; set; } = string.Empty;
        [Description("The Terrain resource, project-relative (Assets/Terrain/Name.terrain.json)."), RequiredInput] public string Terrain { get; set; } = string.Empty;
        [Description("How often its paint layers' pictures repeat across it (default 6).")] public float? UvScale { get; set; }
        [Description("The instance's name (default the terrain's name).")] public string? Name { get; set; }
    }

    public sealed class RoomAddTerrain : AutomationCommand<AddTerrainInput>
    {
        public override string Name => "room.addTerrain";
        public override string Description => "Puts a Terrain in a 3D room, as dragging it in from the Room editor's palette does; its grass and scatter layers come with it.";

        protected override AutomationResult Execute(AutomationContext context, AddTerrainInput input)
        {
            string full = Room(context, input.Room);
            string terrain = context.Resolve(input.Terrain);
            if (!File.Exists(terrain) || !terrain.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Terrain}' is not a Terrain resource (a .terrain.json file).");
            RoomAsset room = RoomAssetLoader.Parse(full);
            if (room.Dimension != RoomDimension.ThreeD) throw new AutomationException("A terrain goes in a 3D room.");
            string name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileName(terrain)[..^".terrain.json".Length] : input.Name;
            var node = new RoomNode
            {
                Name = name,
                Kind = RoomNodeKind.Terrain,
                LayerId = room.Layers.FirstOrDefault()?.Id ?? string.Empty,
                Terrain = new RoomTerrainData { Asset = context.Relative(terrain), UvScale = input.UvScale ?? 6f },
            };
            room.Nodes.Add(node);
            return Save(context, full, room, $"Put {name} in the room.", Describe(node));
        }
    }

    public sealed class RoomMakeStart : AutomationCommand<RoomInput>
    {
        public override string Name => "room.makeStart";
        public override string Description => "Makes a room the one the game starts in, as the Room editor's Make starting room does.";

        protected override AutomationResult Execute(AutomationContext context, RoomInput input)
        {
            string full = Room(context, input.Room);
            context.Project.Manifest.StartRoom = context.Relative(full);
            new ProjectService().Save(context.Project);
            return AutomationResult.Success($"{Path.GetFileName(full)} is the start room.", null, context.Relative(full));
        }
    }

    private static string Room(AutomationContext context, string path)
    {
        string full = context.Resolve(path);
        if (!File.Exists(full) || !full.EndsWith(".room.json", StringComparison.OrdinalIgnoreCase))
            throw new AutomationException($"'{path}' is not a Room resource (a .room.json file).");
        return full;
    }

    private static AutomationResult Save(AutomationContext context, string full, RoomAsset room, string message, JsonNode? data = null)
    {
        ResourceBackupService.BackupBeforeOverwrite(full);
        RoomAssetLoader.Save(room, full);
        return AutomationResult.Success(message, data, context.Relative(full));
    }

    /// <summary>"Rock 1", "Rock 2"...: the first number no instance of the room has.</summary>
    private static string UnusedName(RoomAsset room, string baseName)
    {
        for (int number = 1; ; number++)
        {
            string name = $"{baseName} {number}";
            if (!room.Nodes.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    private static RoomNode Node(RoomAsset room, string node) =>
        room.Nodes.FirstOrDefault(n => n.Id.Equals(node, StringComparison.OrdinalIgnoreCase))
        ?? room.Nodes.FirstOrDefault(n => n.Name.Equals(node, StringComparison.OrdinalIgnoreCase))
        ?? throw new AutomationException($"The room has no instance '{node}'.");

    private static JsonObject Describe(RoomNode node) => new()
    {
        ["id"] = node.Id,
        ["name"] = node.Name,
        ["kind"] = node.Kind.ToString(),
        ["object"] = node.GameObject?.Prefab,
        ["position"] = new JsonArray([.. node.Transform.Position.Select(v => (JsonNode?)v)]),
        ["rotation"] = new JsonArray([.. node.Transform.Rotation.Select(v => (JsonNode?)v)]),
        ["scale"] = new JsonArray([.. node.Transform.Scale.Select(v => (JsonNode?)v)]),
    };

    public sealed class RoomInput
    {
        [Description("The Room resource, project-relative (Assets/Rooms/Name.room.json)."), RequiredInput] public string Room { get; set; } = string.Empty;
    }

    public sealed class RoomNodes : AutomationCommand<RoomInput>
    {
        public override string Name => "room.nodes";
        public override string Description => "Lists a room's instances: id, name, kind, object, position, rotation, scale.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, RoomInput input)
        {
            RoomAsset room = RoomAssetLoader.Parse(Room(context, input.Room));
            return AutomationResult.Success($"{room.Nodes.Count} instances.", new JsonArray([.. room.Nodes.Select(n => (JsonNode?)Describe(n))]));
        }
    }

    public sealed class AddInstanceInput
    {
        [Description("The Room resource, project-relative."), RequiredInput] public string Room { get; set; } = string.Empty;
        [Description("The Object resource to place, project-relative (Assets/Objects/Name.object.json)."), RequiredInput] public string Object { get; set; } = string.Empty;
        [Description("Position x.")] public float X { get; set; }
        [Description("Position y.")] public float Y { get; set; }
        [Description("Position z (3D rooms).")] public float Z { get; set; }
        [Description("Turn about the vertical axis, degrees.")] public float Yaw { get; set; }
        [Description("Tilt, degrees.")] public float Pitch { get; set; }
        [Description("Roll (the angle of a 2D instance), degrees.")] public float Roll { get; set; }
        [Description("Uniform scale (default 1).")] public float? Scale { get; set; }
        [Description("The instance's name (default the object's name and a number).")] public string? Name { get; set; }
        [Description("The layer's id or name (default the room's first layer).")] public string? Layer { get; set; }
    }

    public sealed class RoomAddInstance : AutomationCommand<AddInstanceInput>
    {
        public override string Name => "room.addInstance";
        public override string Description => "Places an instance of an Object in a room, as dragging it in from the Room editor's palette does.";

        protected override AutomationResult Execute(AutomationContext context, AddInstanceInput input)
        {
            string full = Room(context, input.Room);
            if (!File.Exists(context.Resolve(input.Object)) || !input.Object.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Object}' is not an Object resource (a .object.json file).");
            RoomAsset room = RoomAssetLoader.Parse(full);
            string objectName = Path.GetFileName(input.Object)[..^".object.json".Length];
            RoomLayer? layer = string.IsNullOrWhiteSpace(input.Layer)
                ? room.Layers.FirstOrDefault()
                : room.Layers.FirstOrDefault(l => l.Id.Equals(input.Layer, StringComparison.OrdinalIgnoreCase) || l.Name.Equals(input.Layer, StringComparison.OrdinalIgnoreCase))
                  ?? throw new AutomationException($"The room has no layer '{input.Layer}'.");
            float scale = input.Scale ?? 1f;
            var node = new RoomNode
            {
                Name = string.IsNullOrWhiteSpace(input.Name) ? UnusedName(room, objectName) : input.Name,
                Kind = RoomNodeKind.GameObject,
                LayerId = layer?.Id ?? string.Empty,
                Transform = new RoomTransform
                {
                    Position = [input.X, input.Y, input.Z],
                    Rotation = [input.Pitch, input.Yaw, input.Roll],
                    Scale = [scale, scale, scale],
                },
                GameObject = new RoomGameObjectData { Prefab = input.Object.Replace('\\', '/') },
            };
            room.Nodes.Add(node);
            return Save(context, full, room, $"Placed {node.Name}.", Describe(node));
        }
    }

    public sealed class MoveInstanceInput
    {
        [Description("The Room resource, project-relative."), RequiredInput] public string Room { get; set; } = string.Empty;
        [Description("The instance: its id or its name."), RequiredInput] public string Instance { get; set; } = string.Empty;
        public float? X { get; set; }
        public float? Y { get; set; }
        public float? Z { get; set; }
        public float? Yaw { get; set; }
        public float? Pitch { get; set; }
        public float? Roll { get; set; }
        [Description("Uniform scale.")] public float? Scale { get; set; }
    }

    public sealed class RoomMoveInstance : AutomationCommand<MoveInstanceInput>
    {
        public override string Name => "room.moveInstance";
        public override string Description => "Moves, turns or scales an instance in a room; only the values given change.";

        protected override AutomationResult Execute(AutomationContext context, MoveInstanceInput input)
        {
            string full = Room(context, input.Room);
            RoomAsset room = RoomAssetLoader.Parse(full);
            RoomNode node = Node(room, input.Instance);
            float[] p = node.Transform.Position, r = node.Transform.Rotation;
            node.Transform.Position = [input.X ?? p[0], input.Y ?? p[1], input.Z ?? p[2]];
            node.Transform.Rotation = [input.Pitch ?? r[0], input.Yaw ?? r[1], input.Roll ?? r[2]];
            if (input.Scale is float s) node.Transform.Scale = [s, s, s];
            return Save(context, full, room, $"Moved {node.Name}.", Describe(node));
        }
    }

    public sealed class RemoveInstanceInput
    {
        [Description("The Room resource, project-relative."), RequiredInput] public string Room { get; set; } = string.Empty;
        [Description("The instance: its id or its name."), RequiredInput] public string Instance { get; set; } = string.Empty;
    }

    public sealed class RoomRemoveInstance : AutomationCommand<RemoveInstanceInput>
    {
        public override string Name => "room.removeInstance";
        public override string Description => "Removes an instance from a room (the room is backed up first).";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, RemoveInstanceInput input)
        {
            string full = Room(context, input.Room);
            RoomAsset room = RoomAssetLoader.Parse(full);
            RoomNode node = Node(room, input.Instance);
            if (room.Nodes.Any(n => n.ParentId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
                throw new AutomationException($"{node.Name} has children; remove or move them first.");
            room.Nodes.Remove(node);
            return Save(context, full, room, $"Removed {node.Name}.");
        }
    }

    public sealed class EnvironmentInput
    {
        [Description("The Room resource, project-relative."), RequiredInput] public string Room { get; set; } = string.Empty;
        [Description("Environment fields to set, as named in the room file (timeOfDayHours, weather, dynamicSky, fogDensity, atmosphericHaze, visibilityKilometres, shadowDistance, ambientIntensity...)."), RequiredInput]
        public JsonObject Values { get; set; } = new();
    }

    public sealed class RoomSetEnvironment : AutomationCommand<EnvironmentInput>
    {
        public override string Name => "room.setEnvironment";
        public override string Description => "Sets a room's environment (sky, time of day, weather, fog, shadows...); only the fields given change, and the room must still load.";

        protected override AutomationResult Execute(AutomationContext context, EnvironmentInput input)
        {
            string full = Room(context, input.Room);
            JObject root = JObject.Parse(File.ReadAllText(full));
            JObject environment = root["environment"] as JObject ?? new JObject();
            environment.Merge(JObject.Parse(input.Values.ToJsonString()), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            root["environment"] = environment;
            // Through the loader, so a value of the wrong kind is refused rather than saved.
            string temporary = Path.Combine(Path.GetDirectoryName(full)!, "." + Guid.NewGuid().ToString("N") + ".room.json");
            RoomAsset room;
            try
            {
                File.WriteAllText(temporary, root.ToString());
                room = RoomAssetLoader.Parse(temporary);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            room.Name = RoomAssetLoader.Parse(full).Name;
            return Save(context, full, room, "Set the room's environment.", JsonNode.Parse(environment.ToString()));
        }
    }
}

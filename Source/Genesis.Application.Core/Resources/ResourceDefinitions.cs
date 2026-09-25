using Genesis.Application.Core.Images;

namespace Genesis.Application.Core.Resources;

public sealed record ResourceDefinition(
    ResourceKind Kind,
    string DisplayName,
    string Extension,
    string IconGlyph,
    string DefaultContent);

public static class ResourceDefinitions
{
    private static readonly IReadOnlyList<ResourceDefinition> Definitions =
    [
        new(ResourceKind.Image, "Image", ".image.json", "▧",
            ImageDocumentSerializer.Serialize(ImageDocument.CreateDefault())),
        new(ResourceKind.Audio, "Audio", ".audio.json", "♪",
            """{"schemaVersion":1,"source":null,"volume":1.0,"loop":false,"spatial":false}"""),
        new(ResourceKind.Shader, "Shader", ".shader.json", "◈",
            """{"schemaVersion":3,"pipeline":"Sprite","authoringMode":"Preset","targetType":"Image","preset":"Image Rainbow","targetComponent":"","entry":"MainPS","profile":"ps_5_0","source":null,"previewAsset":"","parameters":[]}"""),
        new(ResourceKind.PgslScript, "PGSL Script", ".pgsl", "ƒ",
            "// @function Main inputs= return=Void\nfunction Main() {\n    // Reusable library routine\n}\n"),
        // Object/Room defaults ARE the Ember runtime formats (PrefabSpawner / RoomAsset),
        // so a freshly created resource plays with F5 without migration.
        new(ResourceKind.GameObject, "Object", ".object.json", "⬡",
            """
            {
              "schemaVersion": 1,
              "dimension": "TwoD",
              "culling": "Default",
              "windingOrder": "Default",
              "sprite": "",
              "model": "",
              "components": [
                { "type": "TransformComponent", "enabled": true, "props": { "Position": "0,0,0", "Rotation": "0,0,0", "Scale": "1,1,1" } },
                { "type": "SpriteComponent", "enabled": true, "props": { "ImageSpeed": "1", "Alpha": "1", "Depth": "0" } }
              ],
              "events": {}
            }
            """),
        new(ResourceKind.Room, "Room", ".room.json", "▱",
            """
            {
              "$schema": "genesis.room",
              "version": 1,
              "name": "Room",
              "dimension": 0,
              "settings": { "width": 1280, "height": 720, "gridSize": 32.0, "snapEnabled": true },
              "environment": { "backgroundColor": [0.07, 0.09, 0.14, 1.0], "ambientIntensity": 1.0 },
              "layers": [ { "name": "Default", "order": 0, "enabled": true } ],
              "nodes": []
            }
            """),
        new(ResourceKind.Model, "Model", ".model.json", "◆",
            """{"schemaVersion":3,"source":null,"parts":[],"culling":"Default","windingOrder":"Default","materials":[],"lods":[],"rig":null,"animations":[]}"""),
        new(ResourceKind.Particle, "Particle System", ".particle.json", "✦",
            """
            {
              "maxParticles": 1000,
              "emitRate": 60.0,
              "burstCount": 0,
              "loop": true,
              "shape": "Cone",
              "spreadDegrees": 22.0,
              "emitRadius": 0.6,
              "speed": 4.0,
              "speedVariance": 0.35,
              "gravity": 1.5,
              "drag": 0.4,
              "lifetime": 1.4,
              "lifetimeVariance": 0.25,
              "startSize": 0.5,
              "endSize": 0.06,
              "emissive": 1.8,
              "blendMode": "Alpha",
              "startColor": { "r": 1.0, "g": 0.85, "b": 0.32, "a": 1.0 },
              "endColor": { "r": 0.9, "g": 0.18, "b": 0.05, "a": 0.0 }
            }
            """),
        new(ResourceKind.Physics, "Physics Material", ".physics.json", "◉",
            """{"schemaVersion":1,"friction":0.5,"restitution":0.0,"density":1.0}"""),
        new(ResourceKind.Terrain, "Terrain", ".terrain.json", "⌁",
            """
            {
              "schemaVersion": 1,
              "resolution": [129, 129],
              "cellSize": 1.0,
              "minHeight": -24.0,
              "maxHeight": 72.0,
              "seedProfile": "RollingHills",
              "culling": "Default",
              "windingOrder": "Default",
              "layers": [
                { "name": "Grass", "color": [0.33, 0.52, 0.26] },
                { "name": "Dirt",  "color": [0.44, 0.34, 0.23] },
                { "name": "Rock",  "color": [0.46, 0.46, 0.50] },
                { "name": "Snow",  "color": [0.90, 0.92, 0.96] }
              ],
              "entities": []
            }
            """),
        new(ResourceKind.TerrainEntity, "Terrain Entity", ".terrainentity.json", "❋",
            """
            {
              "schemaVersion": 1,
              "name": "New Terrain Entity",
              "type": "Foliage",
              "culling": "Default",
              "windingOrder": "Default",
              "icon": "",
              "components": []
            }
            """),
        new(ResourceKind.Pathing, "Pathing & Navigation", ".pathing", "⌁",
            """
            {
              "schemaVersion": 1,
              "name": "Pathing Route",
              "targetRoom": "",
              "targetObject": "",
              "previewAgentCount": 3,
              "route": {
                "mode": "WaypointPatrol",
                "loopMode": "Loop",
                "speed": 3.8,
                "stoppingDistance": 0.15,
                "defaultWaitSeconds": 0.0,
                "wanderRadius": 8.0,
                "followOffset": 2.0,
                "followTarget": "",
                "animationState": "",
                "speedCurve": [
                  { "time": 0.0, "multiplier": 1.0 },
                  { "time": 12.0, "multiplier": 1.0 }
                ],
                "waypoints": [
                  { "name": "WP1", "x": -4.0, "y": 0.0, "z": -3.0, "waitSeconds": 0.0, "curve": true },
                  { "name": "WP2", "x": 4.0, "y": 0.0, "z": -3.0, "waitSeconds": 0.0, "curve": true },
                  { "name": "WP3", "x": 4.0, "y": 0.0, "z": 3.0, "waitSeconds": 0.0, "curve": true },
                  { "name": "WP4", "x": -4.0, "y": 0.0, "z": 3.0, "waitSeconds": 0.0, "curve": true }
                ]
              }
            }
            """),
        new(ResourceKind.UserInterface, "User Interface", ".ui.json", "▤",
            """
            {
              "schemaVersion": 1,
              "designWidth": 1280,
              "designHeight": 720,
              "elements": []
            }
            """),
        new(ResourceKind.Note, "Note", ".md", "≡", "# New Note\n"),
    ];

    // Retain the legacy definition for decoding existing projects, but Terrain owns
    // entity authoring; it is no longer a standalone resource or New-menu entry.
    public static IReadOnlyList<ResourceDefinition> All { get; } = Definitions.Where(definition => definition.Kind != ResourceKind.TerrainEntity).ToArray();

    /// <summary>Standalone resource types offered in New menus.</summary>
    public static IReadOnlyList<ResourceDefinition> Creatable => All;

    public static ResourceDefinition Get(ResourceKind kind) =>
        Definitions.FirstOrDefault(definition => definition.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource kind.");

    public static ResourceDefinition? FromPath(string path) =>
        Definitions
            .OrderByDescending(definition => definition.Extension.Length)
            .FirstOrDefault(
                definition => path.EndsWith(
                    definition.Extension,
                    StringComparison.OrdinalIgnoreCase));
}

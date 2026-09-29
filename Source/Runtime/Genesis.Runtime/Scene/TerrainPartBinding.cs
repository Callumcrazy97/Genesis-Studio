using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Physics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

internal sealed record TerrainPartDefinition(string Name, JObject Definition,
    IReadOnlyDictionary<string, string> Events);

/// <summary>Reads editor-owned parts without a dependency on the editor assembly. Translates
/// their authored components into the same live entity pipeline used by Room Objects.</summary>
internal static class TerrainPartBinding
{
    public static TerrainPartDefinition Load(string project, TerrainPlacedEntity placed)
    {
        string path = ResourceNames.ResolveFile(project, placed.Entity, ResourceType.TerrainEntity);
        if (path.Length == 0 || !(path.EndsWith(".terrainpart.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".terrainentity.json", StringComparison.OrdinalIgnoreCase))) return null;
        JObject document = JObject.Parse(File.ReadAllText(path));
        if (Number(document, "SchemaVersion", 1) != 1)
            throw new InvalidDataException($"Unsupported terrain part version in '{placed.Entity}'.");

        string name = Text(document, "Name", Path.GetFileNameWithoutExtension(path));
        var components = new JArray();
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var step = new StringBuilder();
        var scriptFields = new JObject { ["ScriptClass"] = "Terrain part: " + name };
        var definition = new JObject
        {
            ["name"] = name, ["dimension"] = "ThreeD", ["solid"] = false,
            ["culling"] = placed.Culling == Genesis.Shared.Interfaces.FaceCullingOverride.Default
                ? Text(document, "Culling", "Default") : placed.Culling.ToString(),
            ["windingOrder"] = placed.WindingOrder == Genesis.Shared.Interfaces.FrontFaceWindingOverride.Default
                ? Text(document, "WindingOrder", "Default") : placed.WindingOrder.ToString(),
            ["components"] = components,
        };
        bool model = false;
        JObject condition = null;
        foreach (JObject component in (Value(document, "Components") as JArray ?? new JArray()).OfType<JObject>())
        {
            if (!Boolean(component, "Enabled", true)) continue;
            JObject props = Value(component, "Props") as JObject ?? new JObject();
            switch (Text(component, "Type"))
            {
                case "Model":
                    string asset = Text(props, "Model");
                    if (asset.Length == 0) break;
                    model = true;
                    float scale = Number(props, "Scale", 1);
                    Add(components, "ModelRendererComponent", new JObject
                    {
                        ["ModelAsset"] = asset, ["ScaleX"] = scale, ["ScaleY"] = scale, ["ScaleZ"] = scale,
                        ["CastShadows"] = placed.CastShadows && Boolean(props, "CastShadows", true),
                        ["ReceiveShadows"] = placed.ReceiveShadows && Boolean(props, "ReceiveShadows", true),
                    });
                    Add(components, "ModelAnimatorComponent", new JObject
                    {
                        ["ClipName"] = string.IsNullOrWhiteSpace(placed.AnimationClip) ? Text(props, "AnimationClip") : placed.AnimationClip,
                        ["ClipFps"] = placed.AnimationFps, ["Playing"] = placed.AnimationPlaying,
                        ["Loop"] = placed.AnimationLoop,
                    });
                    Add(components, "Draw3DComponent", new JObject
                    {
                        ["CastShadows"] = placed.CastShadows && Boolean(props, "CastShadows", true),
                        ["ReceiveShadows"] = placed.ReceiveShadows && Boolean(props, "ReceiveShadows", true),
                    });
                    break;
                case "Texture":
                    Add(components, "SpriteComponent", new JObject
                    {
                        ["Sprite"] = Text(props, "Texture"), ["ImageSpeed"] = 0,
                    });
                    definition["terrainTexture"] = props.DeepClone();
                    break;
                case "Shader":
                    definition["shader"] = Text(props, "Shader");
                    var parameters = new JObject();
                    foreach (JProperty property in props.Properties())
                    {
                        if (!property.Name.StartsWith("Parameter.", StringComparison.OrdinalIgnoreCase)) continue;
                        int end = property.Name.LastIndexOf('.');
                        if (end <= "Parameter.".Length || !int.TryParse(property.Name[(end + 1)..], out int index)
                            || index < 0 || index > 15) continue;
                        string parameter = property.Name["Parameter.".Length..end];
                        JArray values = parameters[parameter] as JArray ?? new JArray();
                        while (values.Count <= index) values.Add(0f);
                        values[index] = Number(props, property.Name, 0);
                        parameters[parameter] = values;
                    }
                    definition["shaderParameters"] = parameters;
                    break;
                case "ParticleEmitter":
                    Add(components, "ParticleComponent", new JObject { ["Asset"] = Text(props, "Particle"), ["Emitting"] = true });
                    break;
                case "AudioEmitter":
                    Add(components, "AudioComponent", new JObject
                    {
                        ["Asset"] = Text(props, "Audio"), ["AutoPlay"] = true, ["Loop"] = true,
                        ["Volume"] = Number(props, "Volume", 1), ["Spatial"] = Text(props, "Mode", "ThreeD") is "ThreeD" or "1",
                        ["MinDistance"] = Number(props, "MinDistance", 2), ["MaxDistance"] = Number(props, "MaxDistance", 40),
                        ["Falloff"] = Text(props, "Falloff", "Linear"),
                    });
                    break;
                case "Physics":
                    definition["terrainPhysics"] = props.DeepClone();
                    foreach (var contact in new[] { ("OnEnterScript", "CollisionEnter"), ("OnExitScript", "CollisionExit") })
                    {
                        string contactScript = ResourceNames.Resolve(project, Text(props, contact.Item1), ResourceType.Script);
                        if (contactScript.Length > 0) events[contact.Item2] = File.ReadAllText(contactScript);
                    }
                    break;
                case "Script":
                    string script = ResourceNames.Resolve(project, Text(props, "Script"), ResourceType.Script);
                    if (script.Length > 0) step.AppendLine(File.ReadAllText(script));
                    foreach (JProperty property in props.Properties())
                        if (property.Name.StartsWith("Variables.", StringComparison.Ordinal))
                            scriptFields[ScriptHostSystem.FieldPrefix + property.Name["Variables.".Length..]] = property.Value.DeepClone();
                    break;
                case "Condition": condition = props; break;
            }
        }
        string expression = condition == null ? ""
            : !string.IsNullOrWhiteSpace(placed.IfExpression) ? placed.IfExpression : Text(condition, "If");
        if (!string.IsNullOrWhiteSpace(expression))
        {
            string Branch(string source, string clip) => !string.IsNullOrWhiteSpace(source) ? source
                : string.IsNullOrWhiteSpace(clip) ? "" : $"AnimationStatePlay({Newtonsoft.Json.JsonConvert.SerializeObject(clip)}, true, 0);";
            string then = Branch(placed.ThenSource.Length > 0 ? placed.ThenSource : Text(condition, "ThenSource"),
                placed.ThenClip.Length > 0 ? placed.ThenClip : Text(condition, "ThenClip"));
            string otherwise = Branch(placed.ElseSource.Length > 0 ? placed.ElseSource : Text(condition, "ElseSource"),
                placed.ElseClip.Length > 0 ? placed.ElseClip : Text(condition, "ElseClip"));
            // Existing visual terrain actions authored AnimationPlay for either sprite or model.
            // The shared state command selects the live binding and retains clip time while unchanged.
            if (model)
            {
                then = ModelActions(then);
                otherwise = ModelActions(otherwise);
            }
            step.AppendLine($"if ({expression}) {{\n{then}\n}} else {{\n{otherwise}\n}}");
        }
        if (step.Length > 0) events["Step"] = step.ToString();
        if (events.Count > 0) Add(components, "ScriptComponent", scriptFields);
        return new TerrainPartDefinition(name, definition, events);
    }

    private static string ModelActions(string source) => Regex.Replace(source,
        @"\bAnimationPlay\s*\(\s*(""(?:\\.|[^""\\])*"")\s*,\s*(true|false)\s*\)",
        "AnimationStatePlay($1, $2, 0)", RegexOptions.CultureInvariant);

    private static void Add(JArray components, string type, JObject props) =>
        components.Add(new JObject { ["type"] = type, ["props"] = props });

    internal static JToken Value(JObject document, string key) => document?.GetValue(key, StringComparison.OrdinalIgnoreCase);
    internal static string Text(JObject document, string key, string fallback = "") => Value(document, key)?.ToString() ?? fallback;
    internal static float Number(JObject document, string key, float fallback) =>
        float.TryParse(Text(document, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && float.IsFinite(parsed) ? parsed : fallback;
    internal static bool Boolean(JObject document, string key, bool fallback) => bool.TryParse(Text(document, key), out bool parsed) ? parsed : fallback;

    public static void AttachPhysics(EcsWorld world, Entity entity, string project, JObject props, TransformComponent transform)
    {
        bool sensor = Boolean(props, "IsTrigger", false);
        if (!Boolean(props, "Solid", true) && !sensor) return;
        Vector3 scale = new(transform.ScaleX, transform.ScaleY, transform.ScaleZ);
        Vector3 size = new(.5f);
        GModelAsset asset = null;
        if (world.Has<ModelRendererComponent>(entity))
        {
            ModelRendererComponent renderer = world.GetRef<ModelRendererComponent>(entity);
            asset = new RuntimeModelAssetRegistry().Load(project, renderer.ModelAsset);
            size = Vector3.Max((asset.Bounds.Max - asset.Bounds.Min) * .5f, new Vector3(.05f));
            scale *= new Vector3(renderer.ScaleX, renderer.ScaleY, renderer.ScaleZ);
        }
        size = Vector3.Max(size * Vector3.Abs(scale), new Vector3(.01f));
        string physics = Text(props, "Physics");
        bool gravity = Boolean(props, "AffectedByGravity", false);
        RigidBodyComponent body;
        if (physics.Length > 0)
            PhysicsDeclarativeBinding.TryBuildRigidBody(physics, Text(props, "Shape"), null, project, size, out body, out _);
        else body = gravity ? RigidBodyComponent.DynamicBox(size) : RigidBodyComponent.StaticBox(size);
        body.Shape = Text(props, "Shape", "Box") switch
        {
            "Sphere" or "1" => Genesis.Shared.ECS.Components.CollisionShape.Sphere,
            "Capsule" or "2" => Genesis.Shared.ECS.Components.CollisionShape.Capsule,
            "Mesh" or "3" => Genesis.Shared.ECS.Components.CollisionShape.Mesh,
            _ => Genesis.Shared.ECS.Components.CollisionShape.Box,
        };
        body.Size = body.Shape switch
        {
            Genesis.Shared.ECS.Components.CollisionShape.Sphere => new Vector3(MathF.Max(size.X, MathF.Max(size.Y, size.Z))),
            Genesis.Shared.ECS.Components.CollisionShape.Capsule => new Vector3(MathF.Max(size.X, size.Z), size.Y, 0),
            _ => size,
        };
        if (asset != null)
        {
            if (body.Shape == Genesis.Shared.ECS.Components.CollisionShape.Mesh)
                ModelColliderBinding.AttachGeometry(world, entity, asset, scale);
            else body.LocalOffset = (asset.Bounds.Center - (asset.Pivot?.Position ?? Vector3.Zero)) * scale;
        }
        body.Flags &= ~RigidBodyFlags.PlanarTwoD;
        if (gravity && body.Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Dynamic) body.Flags |= RigidBodyFlags.UseGravity;
        else body.Flags &= ~RigidBodyFlags.UseGravity;
        if (sensor) body.Flags |= RigidBodyFlags.Sensor;
        body.Friction = Math.Clamp(Number(props, "Friction", body.Friction), 0, 1);
        body.Restitution = Math.Clamp(Number(props, "Restitution", body.Restitution), 0, 1);
        Vector3 extents = body.Size;
        float volume = body.Shape switch
        {
            Genesis.Shared.ECS.Components.CollisionShape.Sphere => 4f / 3f * MathF.PI * extents.X * extents.X * extents.X,
            Genesis.Shared.ECS.Components.CollisionShape.Capsule => MathF.PI * extents.X * extents.X * MathF.Max(0, extents.Y * 2 - extents.X * 2)
                + 4f / 3f * MathF.PI * extents.X * extents.X * extents.X,
            _ => extents.X * extents.Y * extents.Z * 8,
        };
        if (world.Has<MeshColliderComponent>(entity))
        {
            float closedVolume = ModelColliderBinding.ClosedVolume(world.GetRef<MeshColliderComponent>(entity));
            if (closedVolume > .000001f) volume = closedVolume;
        }
        body.Mass = MathF.Max(.001f, Number(props, "Density", body.Mass) * volume);
        world.Set(entity, body);
    }
}

using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public enum BlueprintValueType { Float, Int, String, Boolean, Vector3 }

internal static class BlueprintActions
{
    public static IReadOnlyList<VisualActionTemplate> Templates { get; } =
    [
        new("Set Position", "Editor.Position", "Movement", "Set this instance's position", [new("X", "100"), new("Y", "100"), new("Z", "0")], "x = {{X}};\ny = {{Y}};\nz = {{Z}};"),
        new("Set Speed", "Editor.Speed", "Movement", "Set speed and direction", [new("Speed", "1"), new("Direction", "0")], "speed = {{Speed}};\ndirection = {{Direction}};"),
        new("Move Toward", "Editor.MoveToward", "Movement", "Move toward a point, retaining a common starting position", [new("X", "0"), new("Y", "0"), new("Z", "0"), new("Distance", "0.1")], "var __oldx = x;\nvar __oldy = y;\nvar __oldz = z;\nx = MoveTowards3DX(__oldx, __oldy, __oldz, {{X}}, {{Y}}, {{Z}}, {{Distance}});\ny = MoveTowards3DY(__oldx, __oldy, __oldz, {{X}}, {{Y}}, {{Z}}, {{Distance}});\nz = MoveTowards3DZ(__oldx, __oldy, __oldz, {{X}}, {{Y}}, {{Z}}, {{Distance}});"),
        new("Create Instance", "CreateInstance", "Instances", "Spawn an Object resource", [new("obj", "", VisualActionValueKind.Asset, ResourceKind.GameObject), new("x", "0"), new("y", "0"), new("z", "0")]),
        new("Spawn Prefab", "CreateInstance", "Instances", "Spawn an Object resource", [new("obj", "", VisualActionValueKind.Asset, ResourceKind.GameObject), new("x", "0"), new("y", "0"), new("z", "0")]),
        new("Destroy Instance", "InstanceDestroy", "Instances", "Destroy this instance", [new("id", "id")]),
        new("Set Variable", "Editor.Variable", "Variables", "Assign a PGSL variable", [new("Variable", "value"), new("Value", "0")], "{{Variable}} = {{Value}};"),
        new("Compare Variable", "Editor.Compare", "Variables", "Compare values and store the result", [new("Result", "comparisonResult"), new("Left", "value"), new("Operator", ">"), new("Right", "0")], "{{Result}} = {{Left}} {{Operator}} {{Right}};"),
        new("If Condition", "Control.IfElse", "Flow Control", "An editable Then / Else package", []),
        new("Return Value", "Editor.Return", "Flow Control", "Return a value from the current callable routine", [new("Value", "0")], "return {{Value}};"),
        new("Loop", "Editor.Loop", "Flow Control", "Repeat a PGSL body", [new("Count", "3"), new("Body", "x = x + 1;")], "for (var i = 0; i < {{Count}}; i = i + 1) {\n{{Body}}\n}"),
        new("Wait / Delay", "SetAlarm", "Flow Control", "Schedule an Alarm event without blocking Step", [new("index", "0"), new("frames", "60")]),
        new("Random Range", "RandomRange", "Math", "Random floating point value", [new("min", "0"), new("max", "1")]),
        new("Spawn Particle Emitter", "SpawnParticleEmitter", "Particles", "Spawn a runtime Particle resource", [new("particle", "", VisualActionValueKind.Asset, ResourceKind.Particle), new("x", "0"), new("y", "0"), new("z", "0"), new("scale", "1")]),
        new("Apply Physics Force", "PhysicsApplyForce", "Physics / Collision", "Apply a continuous force for this frame", [new("instanceId", "id"), new("x", "0"), new("y", "1"), new("z", "0")]),
        new("Apply Physics Impulse", "PhysicsApplyImpulse", "Physics / Collision", "Apply an immediate impulse to a dynamic body", [new("instanceId", "id"), new("x", "0"), new("y", "1"), new("z", "0")]),
        new("Set Physics Velocity", "PhysicsSetVelocity", "Physics / Collision", "Set a dynamic body's linear velocity", [new("instanceId", "id"), new("x", "0"), new("y", "0"), new("z", "0")]),
        new("Physics Raycast", "PhysicsRaycast", "Physics / Collision", "Return the distance to the first physics hit", [new("x", "x"), new("y", "y"), new("z", "z"), new("dx", "0"), new("dy", "-1"), new("dz", "0"), new("maxDistance", "100")]),
        new("Create Physics Joint", "PhysicsCreateJoint", "Physics / Collision", "Join two dynamic bodies at a world point", [new("instanceA", "id"), new("instanceB", "other"), new("anchorX", "x"), new("anchorY", "y"), new("anchorZ", "z")]),
        new("Draw User Interface", "DrawUi", "User Interface", "Draw a reusable UI resource during DrawGui", [new("uiAsset", "", VisualActionValueKind.Asset, ResourceKind.UserInterface)]),
        new("Set UI Text", "UiSetText", "User Interface", "Override one UI text element", [new("uiAsset", "", VisualActionValueKind.Asset, ResourceKind.UserInterface), new("elementId", "\"Text1\""), new("text", "\"Text\"")]),
        new("Set UI Value", "UiSetValue", "User Interface", "Override one progress/value element", [new("uiAsset", "", VisualActionValueKind.Asset, ResourceKind.UserInterface), new("elementId", "\"ProgressBar1\""), new("value", "100")]),
        new("Draw World Label", "DrawText3D", "Drawing 3D", "Draw text at a projected world position", [new("x", "x"), new("y", "y"), new("z", "z"), new("text", "\"Label\""), new("size", "18")]),
        new("Play Sound", "PlaySound", "Audio", "Play an Audio resource", [new("path", "", VisualActionValueKind.Asset, ResourceKind.Audio), new("volume", "1"), new("pitch", "1"), new("loop", "false", VisualActionValueKind.Boolean)]),
        new("Stop Sound", "StopSound", "Audio", "Stop a channel", [new("channel", "0")]),
    ];

    public static BlueprintValueType TypeOf(VisualActionParameter parameter)
    {
        if (parameter.DataType is { } declared) return declared;
        if (parameter.Kind == VisualActionValueKind.Boolean || parameter.Value is "true" or "false") return BlueprintValueType.Boolean;
        if (parameter.Kind == VisualActionValueKind.Asset || parameter.Value.StartsWith('"') || parameter.Name is "Variable" or "Result" or "Operator" or "Body") return BlueprintValueType.String;
        if (parameter.Value.Contains("Vector3", StringComparison.OrdinalIgnoreCase) || parameter.Name.Contains("vector", StringComparison.OrdinalIgnoreCase)) return BlueprintValueType.Vector3;
        if (parameter.Name.ToLowerInvariant() is "id" or "index" or "count" or "frames" or "maxparticles" or "instanceid") return BlueprintValueType.Int;
        return BlueprintValueType.Float;
    }
    public static BlueprintValueType? FromClrType(Type? type) => type == typeof(bool) ? BlueprintValueType.Boolean
        : type == typeof(string) ? BlueprintValueType.String : type == typeof(System.Numerics.Vector3) ? BlueprintValueType.Vector3
        : type == typeof(int) || type == typeof(long) ? BlueprintValueType.Int
        : type == typeof(float) || type == typeof(double) ? BlueprintValueType.Float : null;

    private static readonly Dictionary<string, BlueprintValueType?> ReturnTypes = typeof(Genesis.Runtime.Scripting.PgslCommands)
        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => FromClrType(group.First().ReturnType), StringComparer.OrdinalIgnoreCase);

    public static BlueprintValueType OutputType(VisualActionBlock block) => ReturnTypes.GetValueOrDefault(block.CommandName.Split('.').Last())
        ?? (block.CommandName is "ParticleCreate" or "CreateInstance" or "SpawnParticleEmitter" ? BlueprintValueType.Int
        : block.CommandName.Contains("Bool", StringComparison.OrdinalIgnoreCase) || block.CommandName.StartsWith("Is", StringComparison.Ordinal) ? BlueprintValueType.Boolean
        : block.CommandName.Contains("Text", StringComparison.OrdinalIgnoreCase) ? BlueprintValueType.String
        : block.CommandName.Contains("Vector3", StringComparison.OrdinalIgnoreCase) ? BlueprintValueType.Vector3 : BlueprintValueType.Float);
    public static Color Colour(BlueprintValueType type) => type switch { BlueprintValueType.Int => Color.LimeGreen, BlueprintValueType.String => Color.MediumPurple,
        BlueprintValueType.Boolean => Color.IndianRed, BlueprintValueType.Vector3 => Color.Gold, _ => Color.DeepSkyBlue };
    public static string Category(string category)
    {
        if (category.Contains("Physics", StringComparison.OrdinalIgnoreCase) || category.Contains("Collision", StringComparison.OrdinalIgnoreCase)) return "Physics / Collision";
        if (category.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Math";
        if (category.Contains("Movement", StringComparison.OrdinalIgnoreCase) || category.Contains("Navigation", StringComparison.OrdinalIgnoreCase)) return "Movement";
        if (category.Contains("Flow", StringComparison.OrdinalIgnoreCase) || category == "Alarms") return "Flow Control";
        if (category.Contains("Variable", StringComparison.OrdinalIgnoreCase)) return "Variables";
        if (category.Contains("Particle", StringComparison.OrdinalIgnoreCase)) return "Particles";
        if (category.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (category.Contains("User Interface", StringComparison.OrdinalIgnoreCase)) return "User Interface";
        if (category.Contains("Instance", StringComparison.OrdinalIgnoreCase)) return "Instances";
        return category is "My Presets" or "Starter Presets" ? category : "More Actions";
    }
}

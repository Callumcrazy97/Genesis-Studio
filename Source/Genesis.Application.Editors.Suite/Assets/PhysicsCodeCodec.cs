using System.Globalization;
using System.Numerics;
using System.Text;
using Genesis.Physics;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Declarative projection of PhysicsSceneConfig used by both visual controls and code.</summary>
internal static class PhysicsCodeCodec
{
    public static string Serialize(PhysicsSceneConfig config)
    {
        Vector3 gravity = config.ResolveGravityVector();
        StringBuilder text = new();
        text.Append("physics_material \"").Append(Escape(config.Name)).AppendLine("\" {");
        Line(text, "density", config.Density);
        Line(text, "friction", config.Friction);
        Line(text, "restitution", config.Restitution);
        Line(text, "body", config.BodyType);
        Line(text, "shape", config.Shape);
        Line(text, "sensor", config.IsSensor);
        Line(text, "gravity", $"{N(gravity.X)}, {N(gravity.Y)}, {N(gravity.Z)}");
        Line(text, "gravity_scale", config.GravityScale);
        Line(text, "linear_damping", config.LinearDamping);
        Line(text, "angular_damping", config.AngularDamping);
        Line(text, "collision.layer", (char)('A' + Math.Clamp(config.CollisionLayer, 0, 6)));
        Line(text, "sleep.enabled", config.AllowSleep);
        Line(text, "sleep.threshold", config.SleepThreshold);
        Line(text, "solver.iterations", config.SolverIterations);
        Line(text, "solver.substeps", config.SubstepCount);
        Line(text, "spawn.shape", config.SpawnShape);
        Line(text, "spawn.mass", config.SpawnMass);
        Line(text, "spawn.count", config.SpawnCount);
        Line(text, "spawn.layout", config.SpawnLayout);
        text.AppendLine("}");
        return text.ToString();
    }

    public static PhysicsSceneConfig Parse(string source, PhysicsSceneConfig baseline)
    {
        PhysicsSceneConfig config = baseline.Clone();
        foreach (string raw in source.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line is "{" or "}") continue;
            if (line.StartsWith("physics_material ", StringComparison.OrdinalIgnoreCase))
            {
                int first = line.IndexOf('"');
                int last = line.LastIndexOf('"');
                if (first >= 0 && last > first) config.Name = line[(first + 1)..last].Replace("\\\"", "\"", StringComparison.Ordinal);
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim().TrimEnd(';');
            switch (key)
            {
                case "density": config.Density = Number(value); break;
                case "friction": config.Friction = Number(value); break;
                case "restitution": config.Restitution = Number(value); break;
                case "body": config.BodyType = EnumValue<PhysicsBodyKind>(value); break;
                case "shape": config.Shape = EnumValue<PhysicsBodyShape>(value); break;
                case "sensor": config.IsSensor = Boolean(value); break;
                case "gravity": SetGravity(config, Triple(value)); break;
                case "gravity_scale": config.GravityScale = Float(value); break;
                case "linear_damping": config.LinearDamping = Float(value); break;
                case "angular_damping": config.AngularDamping = Float(value); break;
                case "collision.layer": config.CollisionLayer = ParseLayer(value); break;
                case "sleep.enabled": config.AllowSleep = Boolean(value); break;
                case "sleep.threshold": config.SleepThreshold = Float(value); break;
                case "solver.iterations": config.SolverIterations = Integer(value); break;
                case "solver.substeps": config.SubstepCount = Integer(value); break;
                case "spawn.shape": config.SpawnShape = EnumValue<PhysicsBodyShape>(value); break;
                case "spawn.mass": config.SpawnMass = Float(value); break;
                case "spawn.count": config.SpawnCount = Integer(value); break;
                case "spawn.layout": config.SpawnLayout = EnumValue<PhysicsSpawnLayout>(value); break;
            }
        }
        return config;
    }

    private static void SetGravity(PhysicsSceneConfig config, Vector3 gravity)
    {
        float magnitude = gravity.Length();
        if (magnitude < 0.0001f)
        {
            config.GravityModel = PhysicsGravityModel.ZeroG;
            config.GravityStrength = 0;
            return;
        }
        config.GravityModel = PhysicsGravityModel.Directional;
        config.GravityDirection = Vector3.Normalize(gravity);
        config.GravityStrength = magnitude / 9.81f;
    }

    private static void Line(StringBuilder text, string key, object value) =>
        text.Append("    ").Append(key).Append(": ").AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture));
    private static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Escape(string value) => (value ?? "Physics").Replace("\"", "\\\"", StringComparison.Ordinal);
    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : throw new FormatException("Invalid number: " + value);
    private static float Float(string value) => (float)Number(value);
    private static int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : throw new FormatException("Invalid integer: " + value);
    private static bool Boolean(string value) => bool.TryParse(value, out bool result) ? result : throw new FormatException("Invalid Boolean: " + value);
    private static T EnumValue<T>(string value) where T : struct, Enum => Enum.TryParse(value.Trim(), true, out T result) ? result : throw new FormatException($"Invalid {typeof(T).Name}: {value}");
    private static int ParseLayer(string value)
    {
        string layer = value.Trim();
        if (layer.Length == 1 && char.ToUpperInvariant(layer[0]) is >= 'A' and <= 'G')
            return char.ToUpperInvariant(layer[0]) - 'A';
        return Math.Clamp(Integer(layer), 0, 6);
    }
    private static Vector3 Triple(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new FormatException("Expected three comma-separated values: " + value);
        return new Vector3(Float(parts[0]), Float(parts[1]), Float(parts[2]));
    }
}

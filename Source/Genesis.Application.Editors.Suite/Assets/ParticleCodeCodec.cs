using System.Globalization;
using System.Text;
using System.Text.Json;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Small declarative authoring language for one emitter. It is deliberately a projection of
/// ParticleConfig: the property inspector and code editor edit the same values, without a second
/// script runtime or an editor-only representation.
/// </summary>
internal static class ParticleCodeCodec
{
    public static string Serialize(ParticleConfig config)
    {
        ParticleColor start = config.StartColor ?? new ParticleColor();
        ParticleColor end = config.EndColor ?? new ParticleColor(1, 1, 1, 0);
        StringBuilder text = new();
        text.Append("particle ").Append(Quote(config.EmitterName)).AppendLine(" {");
        Line(text, "emission.rate", config.EmitRate);
        Line(text, "emission.maximum", config.MaxParticles);
        Line(text, "emission.burst", config.BurstCount);
        Line(text, "emission.loop", config.Loop);
        Line(text, "emission.shape", config.Shape);
        Line(text, "emission.radius", config.EmitRadius);
        Line(text, "emission.spread", config.SpreadDegrees);
        Line(text, "emission.box", $"{N(config.BoxSizeX)}, {N(config.BoxSizeY)}, {N(config.BoxSizeZ)}");
        Line(text, "emission.meshSurface", Quote(config.MeshSurfaceAsset));
        Line(text, "motion.speed", config.Speed);
        Line(text, "motion.speedVariance", config.SpeedVariance);
        Line(text, "motion.gravity", $"{N(config.GravityX)}, {N(config.Gravity)}, {N(config.GravityZ)}");
        Line(text, "motion.wind", $"{N(config.WindX)}, 0, {N(config.WindZ)}");
        Line(text, "motion.drag", config.Drag);
        Line(text, "motion.turbulence", config.TurbulenceStrength);
        Line(text, "motion.downward", config.DownwardEmit);
        Line(text, "lifetime.seconds", config.Lifetime);
        Line(text, "lifetime.variance", config.LifetimeVariance);
        Line(text, "appearance.size", $"{N(config.StartSize)}, {N(config.EndSize)}");
        Line(text, "appearance.colourStart", ColorLiteral(start));
        Line(text, "appearance.colourEnd", ColorLiteral(end));
        Line(text, "appearance.colourMiddle", config.MidColor is null ? "none" : $"{ColorLiteral(config.MidColor)} at {N(config.ColorMidpoint)}");
        Line(text, "appearance.emissive", config.Emissive);
        Line(text, "appearance.jitter", config.ColorJitter);
        Line(text, "appearance.sizeScale", $"{N(config.SizeXScale)}, {N(config.SizeYScale)}");
        Line(text, "appearance.rotation", $"{N(config.RotationSpeed)}, {N(config.RotationVariance)}");
        Line(text, "curve.size", Curve(config.UseCustomSizeCurve, config.SizeOverLifetime));
        Line(text, "curve.speed", Curve(config.UseCustomSpeedCurve, config.SpeedOverLifetime));
        Line(text, "curve.alpha", Curve(config.UseCustomAlphaCurve, config.AlphaOverLifetime));
        Line(text, "curve.velocity", Curve(config.UseCustomVelocityCurve, config.VelocityOverLifetime));
        if (config.GradientStops.Count > 0)
            Line(text, "gradient.stops", string.Join(" | ", config.GradientStops.OrderBy(stop => stop.Position).Select(stop => $"{N(stop.Position)} {ColorLiteral(stop.Color)}")));
        Line(text, "render.blend", config.BlendMode);
        Line(text, "render.alignment", config.Alignment);
        Line(text, "render.kind", config.RendererKind);
        Line(text, "render.space", config.SimulationSpace);
        Line(text, "render.trail", $"{N(config.TrailDuration)}, {N(config.TrailWidth)}");
        Line(text, "render.ribbonMaxSegment", config.RibbonMaxSegmentLength);
        Line(text, "render.velocityStretch", config.VelocityStretch);
        Line(text, "render.beam", $"{N(config.BeamEndX)}, {N(config.BeamEndY)}, {N(config.BeamEndZ)}, {N(config.BeamNoise)}");
        Line(text, "render.texture", Quote(config.TexturePath));
        Line(text, "render.mesh", Quote(config.MeshParticleAsset));
        Line(text, "render.flipbook", $"{config.UseFlipbook.ToString().ToLowerInvariant()}, {config.FlipbookColumns}, {config.FlipbookRows}, {N(config.FlipbookFps)}");
        Line(text, "collision.response", config.CollisionMode);
        Line(text, "collision.height", config.CollisionPlaneHeight);
        Line(text, "collision.bounce", config.CollisionBounce);
        Line(text, "collision.terrain", config.CollideWithTerrain);
        Line(text, "collision.geometry", config.CollideWithGeometry);
        Line(text, "bounds.mode", config.BoundsMode);
        Line(text, "bounds.center", $"{N(config.BoundsCenterX)}, {N(config.BoundsCenterY)}, {N(config.BoundsCenterZ)}");
        Line(text, "bounds.size", $"{N(config.BoundsSizeX)}, {N(config.BoundsSizeY)}, {N(config.BoundsSizeZ)}");
        text.AppendLine("}");
        return text.ToString();
    }

    public static ParticleConfig Parse(string source, ParticleConfig baseline)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(baseline);
        if (source.Length > 524288) throw new FormatException("The emitter definition is too large (512 KiB limit).");
        ParticleConfig config = baseline.CloneEmitter();
        bool opened = false, closed = false;
        int lineNumber = 0;
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in source.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            try
            {
                if (closed) throw new FormatException("Unexpected content after the closing brace.");
                if (line.StartsWith("particle ", StringComparison.OrdinalIgnoreCase))
                {
                    if (opened || !line.EndsWith('{')) throw new FormatException("Expected particle \"Emitter name\" { once at the start.");
                    config.EmitterName = JsonSerializer.Deserialize<string>(line[9..^1].Trim())
                        ?? throw new FormatException("The emitter name is missing.");
                    opened = true;
                    continue;
                }
                if (!opened) throw new FormatException("Start with particle \"Emitter name\" {.");
                if (line == "}") { closed = true; continue; }
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new FormatException("Expected property: value.");
                string key = line[..colon].Trim().ToLowerInvariant();
                if (!keys.Add(key)) throw new FormatException("Property '" + key + "' occurs more than once.");
                string value = line[(colon + 1)..].Trim().TrimEnd(';').TrimEnd();
                switch (key)
                {
                    case "emission.rate": config.EmitRate = Number(value); break;
                    case "emission.maximum": config.MaxParticles = Integer(value); break;
                    case "emission.burst": config.BurstCount = Integer(value); break;
                    case "emission.loop": config.Loop = Boolean(value); break;
                    case "emission.shape": config.Shape = EnumValue<ParticleEmitShape>(value); break;
                    case "emission.radius": config.EmitRadius = Number(value); break;
                    case "emission.spread": config.SpreadDegrees = Number(value); break;
                    case "emission.box": (config.BoxSizeX, config.BoxSizeY, config.BoxSizeZ) = Triple(value); break;
                    case "emission.meshsurface": config.MeshSurfaceAsset = Unquote(value); break;
                    case "motion.speed": config.Speed = Number(value); break;
                    case "motion.speedvariance": config.SpeedVariance = Number(value); break;
                    case "motion.gravity": (config.GravityX, config.Gravity, config.GravityZ) = Triple(value); break;
                    case "motion.wind": (config.WindX, _, config.WindZ) = Triple(value); break;
                    case "motion.drag": config.Drag = Number(value); break;
                    case "motion.turbulence": config.TurbulenceStrength = Number(value); break;
                    case "motion.downward": config.DownwardEmit = Boolean(value); break;
                    case "lifetime.seconds": config.Lifetime = Number(value); break;
                    case "lifetime.variance": config.LifetimeVariance = Number(value); break;
                    case "appearance.size": (config.StartSize, config.EndSize) = Pair(value); break;
                    case "appearance.colourstart": config.StartColor = Color(value); break;
                    case "appearance.colourend": config.EndColor = Color(value); break;
                    case "appearance.colourmiddle": ParseMiddle(value, config); break;
                    case "appearance.emissive": config.Emissive = Number(value); break;
                    case "appearance.jitter": config.ColorJitter = Number(value); break;
                    case "appearance.sizescale": (config.SizeXScale, config.SizeYScale) = Pair(value); break;
                    case "appearance.rotation": (config.RotationSpeed, config.RotationVariance) = Pair(value); break;
                    case "curve.size": config.UseCustomSizeCurve = ParseCurve(value, config.SizeOverLifetime); break;
                    case "curve.speed": config.UseCustomSpeedCurve = ParseCurve(value, config.SpeedOverLifetime); break;
                    case "curve.alpha": config.UseCustomAlphaCurve = ParseCurve(value, config.AlphaOverLifetime); break;
                    case "curve.velocity": config.UseCustomVelocityCurve = ParseCurve(value, config.VelocityOverLifetime); break;
                    case "gradient.stops": config.GradientStops = ParseStops(value); break;
                    case "render.blend": config.BlendMode = EnumValue<ParticleBlendMode>(value); break;
                    case "render.alignment": config.Alignment = EnumValue<ParticleAlignment>(value); break;
                    case "render.kind": config.RendererKind = EnumValue<ParticleRendererKind>(value); break;
                    case "render.space": config.SimulationSpace = EnumValue<ParticleSimulationSpace>(value); break;
                    case "render.trail": (config.TrailDuration, config.TrailWidth) = Pair(value); break;
                    case "render.ribbonmaxsegment": config.RibbonMaxSegmentLength = Number(value); break;
                    case "render.velocitystretch": config.VelocityStretch = Number(value); break;
                    case "render.beam": (config.BeamEndX, config.BeamEndY, config.BeamEndZ, config.BeamNoise) = Quad(value); break;
                    case "render.texture": config.TexturePath = Unquote(value); break;
                    case "render.mesh": config.MeshParticleAsset = Unquote(value); break;
                    case "render.flipbook": ParseFlipbook(value, config); break;
                    case "collision.response": config.CollisionMode = EnumValue<ParticleCollisionMode>(value); break;
                    case "collision.height": config.CollisionPlaneHeight = Number(value); break;
                    case "collision.bounce": config.CollisionBounce = Number(value); break;
                    case "collision.terrain": config.CollideWithTerrain = Boolean(value); break;
                    case "collision.geometry": config.CollideWithGeometry = Boolean(value); break;
                    case "bounds.mode": config.BoundsMode = EnumValue<ParticleBoundsMode>(value); break;
                    case "bounds.center": (config.BoundsCenterX, config.BoundsCenterY, config.BoundsCenterZ) = Triple(value); break;
                    case "bounds.size": (config.BoundsSizeX, config.BoundsSizeY, config.BoundsSizeZ) = Triple(value); break;
                    default: throw new FormatException("Unknown emitter property '" + key + "'.");
                }
            }
            catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException)
            {
                throw new FormatException($"Line {lineNumber}: {error.Message}", error);
            }
        }
        if (!opened || !closed) throw new FormatException("The emitter definition needs its header and closing brace.");
        return config;
    }

    private static void Line(StringBuilder text, string key, object value) => text.Append("    ").Append(key).Append(": ").AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture));
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Curve(bool enabled, ParticleBezierCurve curve) => $"{enabled.ToString().ToLowerInvariant()}, {N(curve.X1)}, {N(curve.Y1)}, {N(curve.X2)}, {N(curve.Y2)}";
    private static string Quote(string? value) => JsonSerializer.Serialize(value ?? string.Empty);
    private static string Unquote(string value) => JsonSerializer.Deserialize<string>(value.Trim())
        ?? throw new FormatException("Expected a quoted resource name.");
    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
        ? number : throw new FormatException("Expected a finite number: " + value);
    private static int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : throw new FormatException("Invalid integer: " + value);
    private static bool Boolean(string value) => bool.TryParse(value, out bool result) ? result : throw new FormatException("Invalid Boolean: " + value);
    private static T EnumValue<T>(string value) where T : struct, Enum => Enum.TryParse(value.Trim(), true, out T result) && Enum.IsDefined(result) ? result : throw new FormatException($"Invalid {typeof(T).Name}: {value}");

    private static (double X, double Y) Pair(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new FormatException("Expected two comma-separated values: " + value);
        return (Number(parts[0]), Number(parts[1]));
    }

    private static (double X, double Y, double Z) Triple(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new FormatException("Expected three comma-separated values: " + value);
        return (Number(parts[0]), Number(parts[1]), Number(parts[2]));
    }

    private static (double X, double Y, double Z, double W) Quad(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) throw new FormatException("Expected four comma-separated values: " + value);
        return (Number(parts[0]), Number(parts[1]), Number(parts[2]), Number(parts[3]));
    }

    private static string ColorLiteral(ParticleColor color) => $"rgba({N(color.R)}, {N(color.G)}, {N(color.B)}, {N(color.A)})";

    private static ParticleColor Color(string value)
    {
        value = value.Trim();
        if (value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')'))
        {
            string[] channels = value[5..^1].Split(',', StringSplitOptions.TrimEntries);
            if (channels.Length != 4) throw new FormatException("Expected rgba(red, green, blue, alpha).");
            double[] values = channels.Select(Number).ToArray();
            if (values.Any(channel => channel is < 0 or > 1)) throw new FormatException("RGBA channels must be between 0 and 1.");
            return new ParticleColor((float)values[0], (float)values[1], (float)values[2], (float)values[3]);
        }
        string hex = value.TrimStart('#');
        if (hex.Length is not (6 or 8)) throw new FormatException("Expected #RRGGBB or #RRGGBBAA: " + value);
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
        return new ParticleColor(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    private static void ParseMiddle(string value, ParticleConfig config)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) { config.MidColor = null; return; }
        int at = value.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        config.MidColor = Color(at < 0 ? value : value[..at]);
        if (at >= 0) config.ColorMidpoint = Number(value[(at + 4)..]);
    }

    private static void ParseFlipbook(string value, ParticleConfig config)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) throw new FormatException("Flipbook expects enabled, columns, rows, fps.");
        config.UseFlipbook = Boolean(parts[0]);
        config.FlipbookColumns = Integer(parts[1]);
        config.FlipbookRows = Integer(parts[2]);
        config.FlipbookFps = Number(parts[3]);
    }

    private static bool ParseCurve(string value, ParticleBezierCurve curve)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) throw new FormatException("Curve expects enabled, x1, y1, x2, y2.");
        curve.X1 = Number(parts[1]); curve.Y1 = Number(parts[2]); curve.X2 = Number(parts[3]); curve.Y2 = Number(parts[4]);
        return Boolean(parts[0]);
    }

    private static List<ParticleGradientStop> ParseStops(string value)
    {
        List<ParticleGradientStop> result = [];
        foreach (string item in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int space = item.IndexOf(' ');
            if (space <= 0) throw new FormatException("Gradient stop expects position and colour.");
            result.Add(new ParticleGradientStop { Position = Number(item[..space]), Color = Color(item[(space + 1)..]) });
        }
        return result;
    }
}

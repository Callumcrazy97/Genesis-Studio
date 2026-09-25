using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Runtime.Navigation;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Small declarative PGSL surface for a pathing asset. Unknown lines are reported, never discarded silently.</summary>
internal static partial class PathingCodeCodec
{
    public static string Encode(PathingAsset asset)
    {
        PathingRoute route = asset.Route;
        StringBuilder code = new();
        code.Append("pathing \"").Append(Escape(asset.Name)).AppendLine("\" {");
        code.Append("    room: \"").Append(Escape(asset.TargetRoom)).AppendLine("\"");
        code.Append("    object: \"").Append(Escape(asset.TargetObject)).AppendLine("\"");
        code.Append("    preview_agents: ").AppendLine(asset.PreviewAgentCount.ToString(CultureInfo.InvariantCulture));
        code.Append("    mode: ").AppendLine(route.Mode.ToString());
        code.Append("    loop: ").AppendLine(route.LoopMode.ToString());
        code.Append("    speed: ").AppendLine(Number(route.Speed));
        code.Append("    stopping_distance: ").AppendLine(Number(route.StoppingDistance));
        code.Append("    wait: ").AppendLine(Number(route.DefaultWaitSeconds));
        code.Append("    wander_radius: ").AppendLine(Number(route.WanderRadius));
        code.Append("    follow_offset: ").AppendLine(Number(route.FollowOffset));
        code.Append("    follow_target: \"").Append(Escape(route.FollowTarget)).AppendLine("\"");
        code.Append("    animation: \"").Append(Escape(route.AnimationState)).AppendLine("\"");
        code.AppendLine();
        foreach (PathingWaypoint point in route.Waypoints)
        {
            code.Append("    waypoint \"").Append(Escape(point.Name)).Append("\" (")
                .Append(Number(point.X)).Append(", ").Append(Number(point.Y)).Append(", ")
                .Append(Number(point.Z)).Append(") wait ").Append(Number(point.WaitSeconds))
                .Append(" curve ").AppendLine(point.Curve ? "true" : "false");
        }
        foreach (PathingSpeedKey key in route.SpeedCurve)
            code.Append("    speed_key ").Append(Number(key.Time)).Append(' ').AppendLine(Number(key.Multiplier));
        code.AppendLine("}");
        return code.ToString();
    }

    public static bool TryDecode(string code, PathingAsset basis, out PathingAsset asset, out string error)
    {
        asset = Clone(basis);
        error = string.Empty;
        Match header = Header().Match(code ?? string.Empty);
        if (!header.Success)
        {
            error = "Expected: pathing \"Name\" { ... }";
            return false;
        }

        asset.Name = Unescape(header.Groups["name"].Value);
        PathingRoute route = asset.Route = new PathingRoute { SpeedCurve = [] };
        foreach (string raw in (code ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("pathing ", StringComparison.OrdinalIgnoreCase) || line == "}") continue;
            Match point = Waypoint().Match(line);
            if (point.Success)
            {
                if (!Float(point, "x", out float x) || !Float(point, "y", out float y)
                    || !Float(point, "z", out float z) || !Float(point, "wait", out float wait))
                {
                    error = "A waypoint contains an invalid number.";
                    return false;
                }
                route.Waypoints.Add(new PathingWaypoint
                {
                    Name = Unescape(point.Groups["name"].Value), X = x, Y = y, Z = z,
                    WaitSeconds = wait, Curve = bool.Parse(point.Groups["curve"].Value),
                });
                continue;
            }
            Match speedKey = SpeedKey().Match(line);
            if (speedKey.Success)
            {
                if (!Float(speedKey, "time", out float time) || !Float(speedKey, "multiplier", out float multiplier))
                {
                    error = "A speed key contains an invalid number.";
                    return false;
                }
                route.SpeedCurve.Add(new PathingSpeedKey { Time = time, Multiplier = multiplier });
                continue;
            }
            Match property = Property().Match(line);
            if (!property.Success)
            {
                error = $"Unrecognized pathing statement: {line}";
                return false;
            }
            string key = property.Groups["key"].Value.ToLowerInvariant();
            string value = property.Groups["value"].Value.Trim();
            if (!Apply(asset, route, key, value, out error)) return false;
        }

        try { asset.Validate(); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
        {
            error = exception.Message;
            return false;
        }
        return true;
    }

    private static bool Apply(PathingAsset asset, PathingRoute route, string key, string value, out string error)
    {
        error = string.Empty;
        switch (key)
        {
            case "room": asset.TargetRoom = Quoted(value); return true;
            case "object": asset.TargetObject = Quoted(value); return true;
            case "follow_target": route.FollowTarget = Quoted(value); return true;
            case "animation": route.AnimationState = Quoted(value); return true;
            case "preview_agents" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int agents): asset.PreviewAgentCount = agents; return true;
            case "mode" when Enum.TryParse(value, true, out PathingRouteMode mode): route.Mode = mode; return true;
            case "loop" when Enum.TryParse(value, true, out PathingLoopMode loop): route.LoopMode = loop; return true;
            case "speed" when Number(value, out float speed): route.Speed = speed; return true;
            case "stopping_distance" when Number(value, out float stopping): route.StoppingDistance = stopping; return true;
            case "wait" when Number(value, out float wait): route.DefaultWaitSeconds = wait; return true;
            case "wander_radius" when Number(value, out float radius): route.WanderRadius = radius; return true;
            case "follow_offset" when Number(value, out float offset): route.FollowOffset = offset; return true;
            default: error = $"Invalid value for '{key}': {value}"; return false;
        }
    }

    private static PathingAsset Clone(PathingAsset asset) => new()
    {
        SchemaVersion = asset.SchemaVersion,
        Name = asset.Name,
        TargetRoom = asset.TargetRoom,
        TargetObject = asset.TargetObject,
        PreviewAgentCount = asset.PreviewAgentCount,
        Route = new PathingRoute
        {
            Mode = asset.Route.Mode, LoopMode = asset.Route.LoopMode, Speed = asset.Route.Speed,
            StoppingDistance = asset.Route.StoppingDistance, DefaultWaitSeconds = asset.Route.DefaultWaitSeconds,
            WanderRadius = asset.Route.WanderRadius, FollowOffset = asset.Route.FollowOffset,
            FollowTarget = asset.Route.FollowTarget, AnimationState = asset.Route.AnimationState,
            SpeedCurve = asset.Route.SpeedCurve.Select(key => new PathingSpeedKey
            {
                Time = key.Time, Multiplier = key.Multiplier,
            }).ToList(),
            Waypoints = asset.Route.Waypoints.Select(point => new PathingWaypoint
            {
                Name = point.Name, X = point.X, Y = point.Y, Z = point.Z,
                WaitSeconds = point.WaitSeconds, Curve = point.Curve,
            }).ToList(),
        },
    };

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool Number(string value, out float result) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && float.IsFinite(result);
    private static bool Float(Match match, string group, out float result) => Number(match.Groups[group].Value, out result);
    private static string Quoted(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? Unescape(value[1..^1]) : value;
    }
    private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string Unescape(string value) => value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex("^\\s*pathing\\s+\\\"(?<name>(?:[^\\\"\\\\]|\\\\.)*)\\\"\\s*\\{", RegexOptions.IgnoreCase)]
    private static partial Regex Header();
    [GeneratedRegex("^waypoint\\s+\\\"(?<name>(?:[^\\\"\\\\]|\\\\.)*)\\\"\\s*\\(\\s*(?<x>-?[0-9.]+)\\s*,\\s*(?<y>-?[0-9.]+)\\s*,\\s*(?<z>-?[0-9.]+)\\s*\\)\\s+wait\\s+(?<wait>-?[0-9.]+)\\s+curve\\s+(?<curve>true|false)$", RegexOptions.IgnoreCase)]
    private static partial Regex Waypoint();
    [GeneratedRegex("^speed_key\\s+(?<time>-?[0-9.]+)\\s+(?<multiplier>-?[0-9.]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedKey();
    [GeneratedRegex("^(?<key>[A-Za-z_][A-Za-z0-9_]*)\\s*:\\s*(?<value>.+)$")]
    private static partial Regex Property();
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Runtime.Navigation;

public enum PathingRouteMode
{
    WaypointPatrol,
    NavMeshSearch,
    WanderRadius,
    FollowLeader,
}

public enum PathingLoopMode
{
    Loop,
    PingPong,
    Once,
}

public sealed class PathingWaypoint
{
    public string Name { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float WaitSeconds { get; set; }
    public bool Curve { get; set; }

    [JsonIgnore]
    public Vector3 Position
    {
        get => new(X, Y, Z);
        set { X = value.X; Y = value.Y; Z = value.Z; }
    }
}

public sealed class PathingSpeedKey
{
    public float Time { get; set; }
    public float Multiplier { get; set; } = 1f;
}

/// <summary>
/// Reusable navigation routine. Animation is an optional asset-owned state name; the engine does
/// not assign semantic roles such as worker, guard, creature, or vehicle to it.
/// </summary>
public sealed class PathingRoute
{
    public PathingRouteMode Mode { get; set; } = PathingRouteMode.WaypointPatrol;
    public PathingLoopMode LoopMode { get; set; } = PathingLoopMode.Loop;
    public float Speed { get; set; } = 3.8f;
    public float StoppingDistance { get; set; } = .15f;
    public float DefaultWaitSeconds { get; set; }
    public float WanderRadius { get; set; } = 8f;
    public float FollowOffset { get; set; } = 2f;
    public string FollowTarget { get; set; } = string.Empty;
    public string AnimationState { get; set; } = string.Empty;
    public List<PathingWaypoint> Waypoints { get; set; } = [];
    public List<PathingSpeedKey> SpeedCurve { get; set; } =
    [
        new() { Time = 0f, Multiplier = 1f },
        new() { Time = 12f, Multiplier = 1f },
    ];

    public float SpeedAt(float time)
    {
        if (SpeedCurve.Count == 0) return Speed;
        if (SpeedCurve.Count == 1) return Speed * SpeedCurve[0].Multiplier;
        float duration = SpeedCurve[^1].Time;
        float sample = duration > 0f ? Math.Clamp(time, 0f, duration) : 0f;
        for (int index = 0; index + 1 < SpeedCurve.Count; index++)
        {
            PathingSpeedKey left = SpeedCurve[index];
            PathingSpeedKey right = SpeedCurve[index + 1];
            if (sample > right.Time) continue;
            float span = MathF.Max(.0001f, right.Time - left.Time);
            float amount = Math.Clamp((sample - left.Time) / span, 0f, 1f);
            return Speed * (left.Multiplier + (right.Multiplier - left.Multiplier) * amount);
        }
        return Speed * SpeedCurve[^1].Multiplier;
    }
}

/// <summary>
/// Authored <c>.pathing</c> resource shared by Studio, PGSL and the standalone Player.
/// Room/Object references remain project-relative and are used only as authoring defaults.
/// </summary>
public sealed class PathingAsset
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Pathing Route";
    public string TargetRoom { get; set; } = string.Empty;
    public string TargetObject { get; set; } = string.Empty;
    public int PreviewAgentCount { get; set; } = 3;
    public PathingRoute Route { get; set; } = new();

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Unsupported pathing schema {SchemaVersion}.");
        Route ??= new PathingRoute();
        Route.Waypoints ??= [];
        Route.SpeedCurve ??= [];
        Route.Speed = Finite(Route.Speed) ? Math.Clamp(Route.Speed, 0f, 10_000f) : 3.8f;
        Route.StoppingDistance = Finite(Route.StoppingDistance) ? Math.Clamp(Route.StoppingDistance, 0f, 10_000f) : .15f;
        Route.DefaultWaitSeconds = Finite(Route.DefaultWaitSeconds) ? Math.Clamp(Route.DefaultWaitSeconds, 0f, 86_400f) : 0f;
        Route.WanderRadius = Finite(Route.WanderRadius) ? Math.Clamp(Route.WanderRadius, 0f, 100_000f) : 8f;
        Route.FollowOffset = Finite(Route.FollowOffset) ? Math.Clamp(Route.FollowOffset, 0f, 100_000f) : 2f;
        PreviewAgentCount = Math.Clamp(PreviewAgentCount, 1, 128);
        if (Route.Waypoints.Count > 16_384) throw new InvalidDataException("A pathing route cannot contain more than 16,384 waypoints.");
        if (Route.SpeedCurve.Count > 1_024) throw new InvalidDataException("A pathing speed curve cannot contain more than 1,024 keys.");
        foreach (PathingSpeedKey key in Route.SpeedCurve)
        {
            if (key is null || !Finite(key.Time) || !Finite(key.Multiplier))
                throw new InvalidDataException("A pathing speed key contains a non-finite value.");
            key.Time = Math.Clamp(key.Time, 0f, 86_400f);
            key.Multiplier = Math.Clamp(key.Multiplier, 0f, 8f);
        }
        Route.SpeedCurve.Sort((left, right) => left.Time.CompareTo(right.Time));
        for (int index = 0; index < Route.Waypoints.Count; index++)
        {
            PathingWaypoint point = Route.Waypoints[index] ?? throw new InvalidDataException("A pathing waypoint is null.");
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(point.Z))
                throw new InvalidDataException($"Waypoint {index + 1} has a non-finite position.");
            point.WaitSeconds = Finite(point.WaitSeconds) ? Math.Clamp(point.WaitSeconds, 0f, 86_400f) : 0f;
            if (string.IsNullOrWhiteSpace(point.Name)) point.Name = $"WP{index + 1}";
        }
    }

    private static bool Finite(float value) => float.IsFinite(value);
}

public static class PathingAssetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static PathingAsset Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Pathing resource is larger than 16 MB.");
        PathingAsset asset = JsonSerializer.Deserialize<PathingAsset>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Pathing resource is empty.");
        asset.Validate();
        return asset;
    }

    public static string Serialize(PathingAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        asset.Validate();
        return JsonSerializer.Serialize(asset, Options);
    }

    public static void Save(string path, PathingAsset asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(asset));
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

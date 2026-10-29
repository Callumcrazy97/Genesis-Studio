#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Navigation;
using Genesis.Shared.ECS;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Cameras;

/// <summary>What a camera rig (Engine.Camera.Rigs) does: where it stands and where it looks.</summary>
public enum CameraRigKind
{
    /// <summary>Stands at its offset and looks the way its yaw and pitch say.</summary>
    Fixed,
    /// <summary>Keeps its offset from the target, in world axes, and looks at it.</summary>
    Follow,
    /// <summary>Circles the target on an arm (distance, yaw, pitch), turned by the mouse unless told not to.</summary>
    Orbit,
    /// <summary>An orbit held close over the target's shoulder.</summary>
    Shoulder,
    /// <summary>Stands at its offset and turns to keep the target in view.</summary>
    LookAt,
    /// <summary>Rides a path of points, looking at the target or along the path.</summary>
    Dolly,
}

/// <summary>How a blend from one rig to the next moves.</summary>
public enum CameraBlendCurve
{
    Cut,
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>
/// Where a camera is and which way it looks, in the engine camera's own terms: radians, yaw 0
/// looking along -z, pitch positive looking up. <see cref="Fov"/> is the vertical field of view in
/// radians, or NaN for "the camera's own".
/// </summary>
public readonly record struct CameraPose(Vector3 Position, float Yaw, float Pitch, float Fov)
{
    public static CameraPose Of(Camera camera) => new(camera.Position, camera.Yaw, camera.Pitch, float.NaN);

    /// <summary>Part way from one pose to another: yaw the short way round, a missing field of view as <paramref name="ownFov"/>.</summary>
    public static CameraPose Blend(CameraPose from, CameraPose to, float amount, float ownFov)
    {
        float t = float.IsFinite(amount) ? Math.Clamp(amount, 0f, 1f) : 1f;
        if (t >= 1f) return to;
        float turn = to.Yaw - from.Yaw;
        turn -= MathF.Tau * MathF.Round(turn / MathF.Tau);
        float fov = float.IsNaN(from.Fov) && float.IsNaN(to.Fov) ? float.NaN
            : Lerp(float.IsNaN(from.Fov) ? ownFov : from.Fov, float.IsNaN(to.Fov) ? ownFov : to.Fov, t);
        return new CameraPose(Vector3.Lerp(from.Position, to.Position, t), from.Yaw + turn * t, Lerp(from.Pitch, to.Pitch, t), fov);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>The blend curves and rig kinds by the names scripts give them.</summary>
public static class CameraRigNames
{
    /// <summary>How far a blend has moved at <paramref name="t"/> (0 to 1) of its time.</summary>
    public static float Ease(CameraBlendCurve curve, float t)
    {
        t = float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 1f;
        return curve switch
        {
            CameraBlendCurve.Cut => 1f,
            CameraBlendCurve.Linear => t,
            CameraBlendCurve.EaseIn => t * t,
            CameraBlendCurve.EaseOut => 1f - (1f - t) * (1f - t),
            _ => t * t * (3f - 2f * t),
        };
    }

    public static bool TryCurve(string? text, out CameraBlendCurve curve)
    {
        switch (Plain(text))
        {
            case "cut": case "none": curve = CameraBlendCurve.Cut; return true;
            case "linear": curve = CameraBlendCurve.Linear; return true;
            case "easein": curve = CameraBlendCurve.EaseIn; return true;
            case "easeout": curve = CameraBlendCurve.EaseOut; return true;
            case "easeinout": case "ease": case "smooth": case "": curve = CameraBlendCurve.EaseInOut; return true;
            default: curve = CameraBlendCurve.EaseInOut; return false;
        }
    }

    public static string CurveName(CameraBlendCurve curve) => curve.ToString().ToLowerInvariant();

    public static bool TryKind(string? text, out CameraRigKind kind)
    {
        switch (Plain(text))
        {
            case "fixed": case "static": kind = CameraRigKind.Fixed; return true;
            case "follow": kind = CameraRigKind.Follow; return true;
            case "orbit": kind = CameraRigKind.Orbit; return true;
            case "shoulder": kind = CameraRigKind.Shoulder; return true;
            case "lookat": kind = CameraRigKind.LookAt; return true;
            case "dolly": case "track": case "rail": kind = CameraRigKind.Dolly; return true;
            default: kind = CameraRigKind.Fixed; return false;
        }
    }

    public static string KindName(CameraRigKind kind) => kind.ToString().ToLowerInvariant();

    // "Ease In-Out" and "easeinout" are the same curve.
    private static string Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var plain = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (char.IsLetter(c)) plain.Append(char.ToLowerInvariant(c));
        return plain.ToString();
    }
}

/// <summary>
/// A dolly rig's path: the points it rides through, straight between them or smoothly curved, open
/// or closed back to the first. A position along it is a share of its length, 0 at the first point.
/// </summary>
public sealed class CameraDollyPath
{
    private const int SamplesPerSegment = 16;
    private readonly List<Vector3> _points = new();
    private Vector3[] _samples = Array.Empty<Vector3>();
    private float[] _distances = Array.Empty<float>();
    private bool _smooth, _closed, _dirty = true;

    public int Count => _points.Count;

    /// <summary>Curves smoothly through the points (Catmull-Rom) instead of running straight between them.</summary>
    public bool Smooth { get => _smooth; set { if (value != _smooth) { _smooth = value; _dirty = true; } } }

    /// <summary>Runs on from the last point back to the first.</summary>
    public bool Closed { get => _closed; set { if (value != _closed) { _closed = value; _dirty = true; } } }

    public float Length { get { Build(); return _distances.Length == 0 ? 0f : _distances[^1]; } }

    public Vector3 this[int index] => _points[index];

    public void Add(Vector3 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) || _points.Count >= 16_384) return;
        _points.Add(point);
        _dirty = true;
    }

    public void Clear()
    {
        _points.Clear();
        _dirty = true;
    }

    /// <summary>
    /// A Pathing resource's route as the path: its waypoints, curved where they are marked to curve
    /// (the same curve its agents walk), closed back to the start when it loops.
    /// </summary>
    public void UseRoute(PathingRoute route)
    {
        _points.Clear();
        _smooth = false;
        // A looping route's own way back to the start is part of the points, so closing adds nothing more.
        _closed = route.LoopMode == PathingLoopMode.Loop && route.Waypoints.Count > 2;
        if (route.Waypoints.Count > 0)
        {
            _points.Add(route.Waypoints[0].Position);
            for (int i = 0; i + 1 < route.Waypoints.Count; i++) _points.AddRange(route.SegmentPoints(i, i + 1));
            if (_closed) _points.AddRange(route.SegmentPoints(route.Waypoints.Count - 1, 0));
        }
        _dirty = true;
    }

    /// <summary>The point at <paramref name="position"/> (0 to 1) of the path's length.</summary>
    public Vector3 PointAt(float position)
    {
        Build();
        if (_samples.Length == 0) return Vector3.Zero;
        if (_samples.Length == 1 || _distances[^1] <= 0f) return _samples[0];
        int segment = Locate(position, out float along);
        return Vector3.Lerp(_samples[segment], _samples[segment + 1], along);
    }

    /// <summary>Which way the path runs at <paramref name="position"/>, of length 1; zero where it does not run anywhere.</summary>
    public Vector3 DirectionAt(float position)
    {
        Build();
        if (_samples.Length < 2 || _distances[^1] <= 0f) return Vector3.Zero;
        int segment = Locate(position, out _);
        Vector3 step = _samples[segment + 1] - _samples[segment];
        return step.LengthSquared() > 1e-12f ? Vector3.Normalize(step) : Vector3.Zero;
    }

    // The segment holding a share of the length, and how far along it that is; zero-length
    // segments (a closed route that already ends at its start) are passed over.
    private int Locate(float position, out float along)
    {
        float total = _distances[^1];
        float wanted = (float.IsFinite(position) ? Math.Clamp(position, 0f, 1f) : 0f) * total;
        int high = Array.BinarySearch(_distances, wanted);
        if (high < 0) high = ~high;
        high = Math.Clamp(high, 1, _distances.Length - 1);
        while (high < _distances.Length - 1 && _distances[high] - _distances[high - 1] <= 0f) high++;
        while (high > 1 && _distances[high] - _distances[high - 1] <= 0f) high--;
        float span = _distances[high] - _distances[high - 1];
        along = span > 0f ? Math.Clamp((wanted - _distances[high - 1]) / span, 0f, 1f) : 0f;
        return high - 1;
    }

    private void Build()
    {
        if (!_dirty) return;
        _dirty = false;
        var samples = new List<Vector3>();
        int count = _points.Count;
        if (count == 1) samples.Add(_points[0]);
        else if (count > 1)
        {
            int segments = _closed ? count : count - 1;
            samples.Add(_points[0]);
            for (int i = 0; i < segments; i++)
            {
                Vector3 p1 = _points[i], p2 = _points[(i + 1) % count];
                if (!_smooth) { samples.Add(p2); continue; }
                Vector3 p0 = Neighbour(i - 1), p3 = Neighbour(i + 2);
                for (int step = 1; step <= SamplesPerSegment; step++)
                {
                    float t = step / (float)SamplesPerSegment, t2 = t * t, t3 = t2 * t;
                    samples.Add(0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3));
                }
            }
        }
        _samples = samples.ToArray();
        _distances = new float[_samples.Length];
        for (int i = 1; i < _samples.Length; i++) _distances[i] = _distances[i - 1] + Vector3.Distance(_samples[i - 1], _samples[i]);

        Vector3 Neighbour(int index) => _closed ? _points[(index % count + count) % count] : _points[Math.Clamp(index, 0, count - 1)];
    }
}

/// <summary>
/// One named virtual camera (Engine.Camera.Rigs): a kind, a target, its offsets, field of view and
/// damping. It works out the pose it would give the camera; <see cref="CameraRigDirector"/> puts the
/// active rig's pose (or a blend of two) on the real camera each frame.
/// </summary>
public sealed class CameraRig
{
    private const float Degrees = MathF.PI / 180f;
    private Vector3 _position, _look, _pivot;
    private bool _settled, _looked;
    private float _distanceNow = -1f;
    private Vector3? _lastTarget;
    private int _targetInstance;
    private Vector3? _targetPoint;

    public CameraRig(string name, CameraRigKind kind)
    {
        Name = name;
        Kind = kind;
        switch (kind)
        {
            case CameraRigKind.Follow: Offset = new Vector3(0f, 3f, 8f); LookOffset = new Vector3(0f, 1f, 0f); break;
            case CameraRigKind.Orbit: Offset = new Vector3(0f, 1.5f, 0f); Distance = 6f; Pitch = 20f; MouseLook = true; break;
            case CameraRigKind.Shoulder: Offset = new Vector3(0.6f, 1.6f, 0f); Distance = 3f; Pitch = 10f; MouseLook = true; break;
        }
    }

    public string Name { get; }
    public CameraRigKind Kind { get; set; }

    /// <summary>The instance followed or looked at; 0 for none. Setting it forgets a target point.</summary>
    public int TargetInstance
    {
        get => _targetInstance;
        set { _targetInstance = Math.Max(0, value); _targetPoint = null; _lastTarget = null; }
    }

    /// <summary>A world point followed or looked at in place of an instance.</summary>
    public Vector3? TargetPoint
    {
        get => _targetPoint;
        set { _targetPoint = value; _targetInstance = 0; _lastTarget = null; }
    }

    /// <summary>
    /// Fixed and look-at rigs: where the camera stands. Follow: where it stands from the target.
    /// Orbit and shoulder: the arm's pivot from the target (x to the side, y up, z ahead). Dolly:
    /// added to every point of the path.
    /// </summary>
    public Vector3 Offset { get; set; }

    /// <summary>Added to the target before it is looked at (a head above the feet).</summary>
    public Vector3 LookOffset { get; set; }

    /// <summary>Vertical field of view in degrees; 0 keeps the camera's own.</summary>
    public float FovDegrees { get; set; }

    /// <summary>Seconds the rig takes to catch up with where it should be; 0 is rigid.</summary>
    public float Damping { get; set; }

    /// <summary>Degrees: the way a fixed rig faces, or round the target an orbit looks (0 along -z, 90 along +x).</summary>
    public float Yaw { get; set; }

    /// <summary>Degrees the rig looks down; negative looks up.</summary>
    public float Pitch { get; set; }

    /// <summary>The orbit or shoulder arm's length.</summary>
    public float Distance { get; set; } = 6f;

    public bool MouseLook { get; set; }
    public float Sensitivity { get; set; } = 0.15f;
    public bool Collision { get; set; } = true;
    public float Clearance { get; set; } = 0.25f;

    public CameraDollyPath Path { get; } = new();

    /// <summary>Where a dolly rig is along its path: 0 its first point, 1 its end, by distance.</summary>
    public float PathPosition { get; set; }

    /// <summary>Metres a second a dolly rig rides its path while it is in use; negative rides back.</summary>
    public float PathSpeed { get; set; }

    /// <summary>The path closes back to its first point and riding goes round and round.</summary>
    public bool PathLoop
    {
        get => Path.Closed;
        set => Path.Closed = value;
    }

    /// <summary>Forget where the rig was: its next pose is where it should be, with no lag behind it.</summary>
    public void Settle()
    {
        _settled = false;
        _looked = false;
        _distanceNow = -1f;
    }

    /// <summary>
    /// The pose this rig gives the camera now, moving its damping, its path and (when
    /// <paramref name="takesInput"/>) its mouse look on by <paramref name="dt"/> seconds.
    /// </summary>
    public CameraPose Evaluate(RuntimeScene scene, float dt, bool takesInput)
    {
        dt = float.IsFinite(dt) ? Math.Clamp(dt, 0f, 0.25f) : 0f;
        Vector3? target = Target(scene, out Entity targetEntity);
        float yaw = (float.IsFinite(Yaw) ? Yaw : 0f) * Degrees;
        float pitch = -(float.IsFinite(Pitch) ? Math.Clamp(Pitch, -89f, 89f) : 0f) * Degrees;
        float fov = FovDegrees > 0f && float.IsFinite(FovDegrees) ? Math.Clamp(FovDegrees, 1f, 179f) * Degrees : float.NaN;
        Vector3 aim = target is { } seen ? seen + LookOffset : default;

        if (Kind is CameraRigKind.Orbit or CameraRigKind.Shoulder)
        {
            Vector3 position = Orbit(scene, target, targetEntity, dt, takesInput, out yaw, out pitch);
            _settled = true;
            return new CameraPose(position, yaw, pitch, fov);
        }

        Vector3 stand = Kind switch
        {
            CameraRigKind.Follow when target is { } followed => followed + Offset,
            CameraRigKind.Dolly => Ride(dt) + Offset,
            _ => Offset,
        };
        _position = _settled ? Damp(_position, stand, dt) : stand;
        _settled = true;

        bool looks = target != null && (Kind is CameraRigKind.Follow or CameraRigKind.LookAt or CameraRigKind.Dolly);
        if (looks)
        {
            _look = _looked ? Damp(_look, aim, dt) : aim;
            _looked = true;
            Face(_look - _position, ref yaw, ref pitch);
        }
        else
        {
            _looked = false;
            if (Kind == CameraRigKind.Dolly)
            {
                Vector3 along = Path.DirectionAt(PathPosition);
                Face(PathSpeed < 0f ? -along : along, ref yaw, ref pitch);
            }
        }
        return new CameraPose(_position, yaw, pitch, fov);
    }

    // The instance's place now, the last place it was seen once it is gone, or the target point.
    private Vector3? Target(RuntimeScene scene, out Entity entity)
    {
        entity = Entity.Null;
        if (_targetInstance <= 0) return _targetPoint;
        var world = scene.World;
        Entity found = world.GetEntity(_targetInstance);
        if (!world.IsAlive(found) || !world.Has<TransformComponent>(found)) return _lastTarget;
        ref TransformComponent transform = ref world.GetRef<TransformComponent>(found);
        entity = found;
        return _lastTarget = new Vector3(transform.X, transform.Y, transform.Z);
    }

    private Vector3 Ride(float dt)
    {
        float length = Path.Length;
        float position = float.IsFinite(PathPosition) ? PathPosition : 0f;
        if (length > 0f && PathSpeed != 0f && float.IsFinite(PathSpeed)) position += PathSpeed * dt / length;
        PathPosition = PathLoop ? position - MathF.Floor(position) : Math.Clamp(position, 0f, 1f);
        return Path.Count == 0 ? Vector3.Zero : Path.PointAt(PathPosition);
    }

    private Vector3 Orbit(RuntimeScene scene, Vector3? target, Entity targetEntity, float dt, bool takesInput, out float yaw, out float pitch)
    {
        if (takesInput && MouseLook && scene.Input is { } input)
        {
            Vector2 look = input.LookDelta;
            Yaw -= look.X * Sensitivity;
            Pitch += look.Y * Sensitivity;
        }
        Yaw = float.IsFinite(Yaw) ? Yaw % 360f : 0f;
        Pitch = float.IsFinite(Pitch) ? Math.Clamp(Pitch, -89f, 89f) : 0f;
        yaw = Yaw * Degrees;
        pitch = -Pitch * Degrees;
        Vector3 forward = Conventions.DirectionFromYawPitch(yaw, pitch);
        Vector3 ahead = Conventions.DirectionFromYawPitch(yaw, 0f);
        // The same side as the orbit camera's shoulder (Engine.Camera.Shoulder.X).
        Vector3 side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, ahead));
        Vector3 centre = target ?? Vector3.Zero;
        Vector3 pivot = centre + Vector3.UnitY * Offset.Y + side * Offset.X + ahead * Offset.Z;
        _pivot = _settled ? Damp(_pivot, pivot, dt) : pivot;

        float wanted = float.IsFinite(Distance) ? Math.Clamp(Distance, 0.05f, 1000f) : 6f;
        float clear = wanted;
        if (Collision && scene.Physics is { } physics)
        {
            Entity? ignore = targetEntity.IsNull ? null : targetEntity;
            if (physics.Raycast(scene.World, _pivot, -forward, wanted, out var hit, ignore))
                clear = MathF.Max(0f, hit.Distance - Clearance);
        }
        // In at once in front of a wall, back out smoothly once it is gone (as the orbit camera does).
        if (_distanceNow < 0f || clear < _distanceNow) _distanceNow = clear;
        else _distanceNow += (clear - _distanceNow) * (1f - MathF.Exp(-10f * dt));
        return _pivot - forward * _distanceNow;
    }

    private Vector3 Damp(Vector3 now, Vector3 goal, float dt) =>
        Damping > 0f && float.IsFinite(Damping) ? now + (goal - now) * (1f - MathF.Exp(-dt / Damping)) : goal;

    // Turns the angles to look along a direction; a direction of no length leaves them.
    private static void Face(Vector3 direction, ref float yaw, ref float pitch)
    {
        if (direction.LengthSquared() < 1e-10f) return;
        Vector3 unit = Vector3.Normalize(direction);
        yaw = MathF.Atan2(unit.X, -unit.Z);
        pitch = MathF.Asin(Math.Clamp(unit.Y, -1f, 1f));
    }
}

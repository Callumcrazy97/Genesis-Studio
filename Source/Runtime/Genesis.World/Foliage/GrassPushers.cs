using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.World.Foliage;

/// <summary>One point grass bends away from this frame: where it is, how far it reaches and how hard (0 to 1).</summary>
public readonly record struct GrassPushPoint(Vector3 Position, float Radius, float Strength);

/// <summary>
/// What parts the grass this frame: each pusher where it stands (a walking character, or a point a
/// game sets), and the trail it left, fading over the spring-back time so trodden grass stands up
/// again behind it instead of snapping upright.
/// </summary>
/// <remarks>
/// Call <see cref="Begin"/> once a frame, <see cref="Add"/> for each pusher, then <see cref="End"/>;
/// drawing then bends the tufts and copies near the points with <see cref="Bend"/>. Grass bent here
/// is bent on the CPU, in the copy of its matrix that is drawn, so it works with every renderer and
/// every shader, and costs nothing where no pusher is.
/// </remarks>
public sealed class GrassPushers
{
    /// <summary>Most points kept at once; the oldest trail goes first.</summary>
    public const int MaximumPoints = 64;

    /// <summary>The steepest a blade is laid over, in degrees, at full strength.</summary>
    public const float MaximumBendDegrees = 72f;

    private readonly List<GrassPushPoint> _points = new();
    private readonly List<(Vector3 Position, float Radius, float Age)> _trail = new();
    private readonly Dictionary<long, (Vector3 Position, float Radius, Vector3 Dropped, bool Seen)> _pushers = new();
    private readonly List<long> _gone = new();
    private float _springBack = 0.8f;
    private Vector3 _min, _max;

    /// <summary>The points that bend grass this frame, pushers first, then their trails.</summary>
    public IReadOnlyList<GrassPushPoint> Points => _points;

    /// <summary>True when nothing bends grass this frame.</summary>
    public bool IsEmpty => _points.Count == 0;

    /// <summary>Ages the trails by <paramref name="deltaSeconds"/> and starts a new frame's pushers.</summary>
    public void Begin(float deltaSeconds, float springBackSeconds)
    {
        _springBack = float.IsFinite(springBackSeconds) ? Math.Clamp(springBackSeconds, 0f, 10f) : 0.8f;
        float step = float.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0f, 1f) : 0f;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            (Vector3 position, float radius, float age) = _trail[i];
            age += step;
            if (age >= _springBack) _trail.RemoveAt(i);
            else _trail[i] = (position, radius, age);
        }

        _gone.Clear();
        foreach (KeyValuePair<long, (Vector3 Position, float Radius, Vector3 Dropped, bool Seen)> pair in _pushers)
            _gone.Add(pair.Key);
        foreach (long id in _gone)
        {
            var pusher = _pushers[id];
            _pushers[id] = (pusher.Position, pusher.Radius, pusher.Dropped, false);
        }
    }

    /// <summary>A pusher this frame: <paramref name="id"/> names it across frames, so its trail follows it.</summary>
    public void Add(long id, Vector3 position, float radius)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !float.IsFinite(radius) || radius <= 0.01f) return;
        radius = MathF.Min(radius, 16f);
        bool known = _pushers.TryGetValue(id, out var pusher);
        Vector3 dropped = known ? pusher.Dropped : position;
        // A pusher on the move leaves a point behind every third of its reach.
        if (!known || Vector3.DistanceSquared(dropped, position) > radius * radius * 0.12f)
        {
            if (known) DropTrail(dropped, radius);
            dropped = position;
        }

        _pushers[id] = (position, radius, dropped, true);
    }

    /// <summary>Ends the frame: a pusher no longer given leaves its last place to stand up again.</summary>
    public void End()
    {
        _gone.Clear();
        foreach (KeyValuePair<long, (Vector3 Position, float Radius, Vector3 Dropped, bool Seen)> pair in _pushers)
            if (!pair.Value.Seen) _gone.Add(pair.Key);
        foreach (long id in _gone)
        {
            var pusher = _pushers[id];
            DropTrail(pusher.Position, pusher.Radius);
            _pushers.Remove(id);
        }

        _points.Clear();
        foreach (var pusher in _pushers.Values)
            _points.Add(new GrassPushPoint(pusher.Position, pusher.Radius, 1f));
        for (int i = _trail.Count - 1; i >= 0 && _points.Count < MaximumPoints; i--)
        {
            (Vector3 position, float radius, float age) = _trail[i];
            float left = _springBack > 0f ? 1f - age / _springBack : 0f;
            if (left > 0f) _points.Add(new GrassPushPoint(position, radius, left * left * (3f - 2f * left)));
        }

        _min = new Vector3(float.MaxValue);
        _max = new Vector3(float.MinValue);
        foreach (GrassPushPoint point in _points)
        {
            Vector3 reach = new(point.Radius, point.Radius + 2.5f, point.Radius);
            _min = Vector3.Min(_min, point.Position - reach);
            _max = Vector3.Max(_max, point.Position + reach);
        }
    }

    /// <summary>Forgets every pusher and trail (a new room).</summary>
    public void Clear()
    {
        _points.Clear();
        _trail.Clear();
        _pushers.Clear();
    }

    /// <summary>True when some point reaches into the box (world space).</summary>
    public bool Touches(Vector3 min, Vector3 max) =>
        _points.Count > 0 && min.X <= _max.X && max.X >= _min.X && min.Y <= _max.Y && max.Y >= _min.Y
        && min.Z <= _max.Z && max.Z >= _min.Z;

    /// <summary>
    /// Picks the points that reach into the box (a cell of grass, world space) for <see cref="BendNear"/>,
    /// so a cell tests only the few pushers near it however many there are. False when none does.
    /// </summary>
    public bool Select(Vector3 min, Vector3 max)
    {
        _near.Clear();
        if (!Touches(min, max)) return false;
        foreach (GrassPushPoint point in _points)
        {
            float r = point.Radius;
            if (min.X <= point.Position.X + r && max.X >= point.Position.X - r && min.Z <= point.Position.Z + r && max.Z >= point.Position.Z - r
                && min.Y <= point.Position.Y + 2.5f && max.Y >= point.Position.Y - 1.5f)
                _near.Add(point);
        }

        return _near.Count > 0;
    }

    private readonly List<GrassPushPoint> _near = new();

    /// <summary>
    /// Bends a tuft (or a plant) standing at its matrix's translation away from the points near it,
    /// <paramref name="strength"/> 0 to 1 of <see cref="MaximumBendDegrees"/>: its upright axis leans
    /// over and shortens, its root stays where it is. Returns false, leaving it alone, when no point
    /// reaches it.
    /// </summary>
    public bool Bend(ref Matrix4x4 world, float strength) => Bend(ref world, strength, _points);

    /// <summary>As <see cref="Bend(ref Matrix4x4, float)"/>, testing only the points the last <see cref="Select"/> picked.</summary>
    public bool BendNear(ref Matrix4x4 world, float strength) => Bend(ref world, strength, _near);

    private bool Bend(ref Matrix4x4 world, float strength, List<GrassPushPoint> points)
    {
        if (points.Count == 0 || !(strength > 0f)) return false;
        Vector3 root = world.Translation;
        if (root.X < _min.X || root.X > _max.X || root.Z < _min.Z || root.Z > _max.Z) return false;
        Vector2 push = Vector2.Zero;
        foreach (GrassPushPoint point in points)
        {
            float above = root.Y - point.Position.Y;
            // A pusher's position is its feet: grass far below or above it is not under it.
            if (above > 1.5f || above < -2.5f) continue;
            Vector2 away = new(root.X - point.Position.X, root.Z - point.Position.Z);
            float distanceSquared = away.LengthSquared();
            if (distanceSquared >= point.Radius * point.Radius) continue;
            float distance = MathF.Sqrt(distanceSquared);
            float near = 1f - distance / point.Radius;
            // Eased out: most of the circle lies well over, fading only towards its rim.
            float amount = near * (2f - near) * point.Strength;
            // Right under the pusher there is no "away": lean along a direction picked by the spot.
            Vector2 direction = distance > 0.001f ? away / distance
                : new Vector2(MathF.Cos(root.X * 12.9898f + root.Z * 78.233f), MathF.Sin(root.X * 12.9898f + root.Z * 78.233f));
            push += direction * amount;
        }

        float length = push.Length();
        if (length < 0.002f) return false;
        float angle = MathF.Min(length, 1f) * Math.Clamp(strength, 0f, 1f) * MaximumBendDegrees * (MathF.PI / 180f);
        Vector2 lean = push / length;
        float sine = MathF.Sin(angle), cosine = MathF.Cos(angle);
        // In world space about the root: up becomes (sin * lean, cos), the ground plane stays put.
        var bend = new Matrix4x4(
            1f, 0f, 0f, 0f,
            sine * lean.X, cosine, sine * lean.Y, 0f,
            0f, 0f, 1f, 0f,
            -root.Y * sine * lean.X, root.Y * (1f - cosine), -root.Y * sine * lean.Y, 1f);
        world *= bend;
        return true;
    }

    private void DropTrail(Vector3 position, float radius)
    {
        if (_springBack <= 0f) return;
        _trail.Add((position, radius, 0f));
        if (_trail.Count > MaximumPoints) _trail.RemoveAt(0);
    }
}

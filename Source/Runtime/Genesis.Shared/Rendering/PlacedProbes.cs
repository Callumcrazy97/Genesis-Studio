using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Genesis.Shared.Rendering;

/// <summary>What a placed probe gives what is inside it: the light around it, or what is seen from it.</summary>
public enum PlacedProbeKind
{
    /// <summary>Bounced light: surfaces inside are lit by the light arriving where the probe stands.</summary>
    Light,
    /// <summary>Reflections: smooth surfaces inside mirror what the probe sees.</summary>
    Reflection,
}

/// <summary>The volume a placed probe covers.</summary>
public enum PlacedProbeShape { Sphere, Box }

/// <summary>
/// One probe placed in a room (the Room editor's Light Probe and Reflection Probe,
/// Engine.Lighting.Probes): what is around it is captured from where it stands, and surfaces inside
/// its volume take their bounced light or reflections from it, fading out over its blend distance
/// towards the edge. Where no placed probe reaches, the automatic probe at the camera stays in use.
/// </summary>
public sealed class PlacedProbe
{
    public string Name = "";
    public PlacedProbeKind Kind;
    public PlacedProbeShape Shape;
    /// <summary>Where it is captured from and the centre of its volume, in the world.</summary>
    public Vector3 Position;
    /// <summary>A sphere's radius, in metres.</summary>
    public float Radius = PlacedProbes.DefaultRadius;
    /// <summary>A box's whole size along the world's axes, in metres.</summary>
    public Vector3 Size = PlacedProbes.DefaultSize;
    /// <summary>How far inside its edge the probe fades in, in metres (0: a hard edge).</summary>
    public float BlendDistance = PlacedProbes.DefaultBlendDistance;
    /// <summary>A reflection box mirrors its walls where they are (parallax-correct), not at infinity.</summary>
    public bool BoxProjection;
    /// <summary>Whether it takes part (Engine.Lighting.Probes.SetActive); its capture is kept while off.</summary>
    public bool Active = true;
    /// <summary>Raised by a change that needs a new capture (moved, resized).</summary>
    public int Revision;
    /// <summary>Set by the 3D view once it has captured the probe at its current revision.</summary>
    public bool Captured;

    /// <summary>A box's half size, never below a centimetre.</summary>
    public Vector3 HalfSize => Vector3.Max(Size * 0.5f, new Vector3(0.01f));
}

/// <summary>
/// The probes placed in the room being played (Engine.Lighting.Probes). The room loader sets them;
/// a game's 3D view captures each one, one a frame, and blends the nearest by position for
/// reflections and bounced light. A room without any draws exactly as before.
/// </summary>
public static class PlacedProbes
{
    /// <summary>The most a room uses; probes past these are left out (and reported).</summary>
    public const int MaxCount = 16;

    public const float DefaultRadius = 6f, DefaultBlendDistance = 1.5f;
    public static readonly Vector3 DefaultSize = new(10f, 5f, 10f);

    private static PlacedProbe[] _probes = Array.Empty<PlacedProbe>();
    private static int _layout, _generation;

    /// <summary>Placed probes blend in where they reach. On in each room unless a script turns it off.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The room's placed probes, in the room's order (at most <see cref="MaxCount"/>).</summary>
    public static IReadOnlyList<PlacedProbe> All => Volatile.Read(ref _probes);

    public static int Count => Volatile.Read(ref _probes).Length;

    /// <summary>Probes the room placed past <see cref="MaxCount"/>, which are not used.</summary>
    public static int LeftOut { get; private set; }

    /// <summary>Raised when the room's probes are replaced: a view lays them out and captures them afresh.</summary>
    public static int Layout => Volatile.Read(ref _layout);

    /// <summary>Raised by <see cref="Recapture"/>: every placed probe is captured again, one a frame.</summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>A room's start: its probes (or none), on, none captured yet.</summary>
    public static void SetRoom(IEnumerable<PlacedProbe> probes)
    {
        var list = new List<PlacedProbe>();
        int leftOut = 0;
        if (probes != null)
        {
            foreach (PlacedProbe probe in probes)
            {
                if (probe == null) continue;
                if (list.Count == MaxCount) { leftOut++; continue; }
                probe.Captured = false;
                list.Add(probe);
            }
        }
        LeftOut = leftOut;
        Enabled = true;
        ResetCaptureReport();
        Volatile.Write(ref _probes, list.ToArray());
        Interlocked.Increment(ref _layout);
    }

    /// <summary>No placed probes (a room without any, a test's end).</summary>
    public static void Clear() => SetRoom(Array.Empty<PlacedProbe>());

    /// <summary>Asks every 3D view to capture each placed probe again, one a frame, keeping the old ones in use meanwhile.</summary>
    public static void Recapture() => Interlocked.Increment(ref _generation);

    /// <summary>The placed probe of this name (any case), or null.</summary>
    public static PlacedProbe Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string wanted = name.Trim();
        foreach (PlacedProbe probe in Volatile.Read(ref _probes))
            if (string.Equals(probe.Name, wanted, StringComparison.OrdinalIgnoreCase)) return probe;
        return null;
    }

    /// <summary>
    /// A probe's share at a point, 0 to 1: whole further than its blend distance inside its edge,
    /// fading to nothing at the edge (the forward shader's PlacedProbeWeight).
    /// </summary>
    public static float Weight(PlacedProbe probe, Vector3 point)
    {
        if (probe == null) return 0f;
        float inside;
        if (probe.Shape == PlacedProbeShape.Sphere)
        {
            inside = MathF.Max(probe.Radius, 0.01f) - Vector3.Distance(point, probe.Position);
        }
        else
        {
            Vector3 room = probe.HalfSize - Vector3.Abs(point - probe.Position);
            inside = MathF.Min(room.X, MathF.Min(room.Y, room.Z));
        }
        float blend = MathF.Max(probe.BlendDistance, 0f);
        return blend > 0f ? Math.Clamp(inside / blend, 0f, 1f) : inside >= 0f ? 1f : 0f;
    }

    /// <summary>The captured, active probes with a share at this point.</summary>
    public static int BlendingAt(Vector3 point)
    {
        if (!Enabled) return 0;
        int count = 0;
        foreach (PlacedProbe probe in Volatile.Read(ref _probes))
            if (probe.Active && probe.Captured && Weight(probe, point) > 0f) count++;
        return count;
    }

    // Reported by the 3D view that captures them (Engine.Lighting.Probes, F6).

    /// <summary>The probes captured at their current place and size.</summary>
    public static int Captured
    {
        get
        {
            int count = 0;
            foreach (PlacedProbe probe in Volatile.Read(ref _probes)) if (probe.Captured) count++;
            return count;
        }
    }

    /// <summary>The probes blending at the camera in the last frame drawn.</summary>
    public static int BlendingAtCamera { get; set; }

    /// <summary>CPU milliseconds the last probe's capture took to record.</summary>
    public static double LastCaptureMs { get; set; }

    /// <summary>CPU milliseconds of every capture since the room's probes were laid out.</summary>
    public static double CaptureMsTotal { get; set; }

    /// <summary>Captures made since the room's probes were laid out.</summary>
    public static int CapturesMade { get; set; }

    /// <summary>The frames between the probes' layout and the last of them captured (0 until then).</summary>
    public static int FramesToCaptureAll { get; set; }

    private static void ResetCaptureReport()
    {
        BlendingAtCamera = 0;
        LastCaptureMs = CaptureMsTotal = 0;
        CapturesMade = FramesToCaptureAll = 0;
    }
}

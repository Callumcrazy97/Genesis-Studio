using System;
using System.Numerics;
using System.Threading;

namespace Genesis.Shared.Rendering;

/// <summary>
/// Automatic reflections and bounced light (Engine.Lighting.*). A 3D view captures what is around
/// its camera into a light probe when a room starts, when a script asks (Engine.Lighting.Recapture())
/// and when the camera has gone far from the last one; smooth surfaces then mirror it and every
/// surface is lit by the light it holds instead of the room's two flat ambient colours.
/// </summary>
public static class LightProbes
{
    public const string EnvironmentVariable = "GENESIS_LIGHT_PROBES";

    public const float DefaultRecaptureDistance = 40f, DefaultCaptureRadius = 80f;

    private static int _generation;
    private static int _quality = DefaultQuality;
    private static float _bouncedLightStrength = 1f, _reflectionStrength = 1f;
    private static float _recaptureDistance = DefaultRecaptureDistance, _captureRadius = DefaultCaptureRadius;

    /// <summary>The camera moving this far from the last capture captures again: 5 to 1000 metres, 40 unless set.</summary>
    public static float RecaptureDistance
    {
        get => _recaptureDistance;
        set => _recaptureDistance = float.IsFinite(value) ? Math.Clamp(value, 5f, 1000f) : DefaultRecaptureDistance;
    }

    /// <summary>What lies within this distance of a capture is drawn into it wherever the camera looks: 10 to 2000 metres, 80 unless set.</summary>
    public static float CaptureRadius
    {
        get => _captureRadius;
        set => _captureRadius = float.IsFinite(value) ? Math.Clamp(value, 10f, 2000f) : DefaultCaptureRadius;
    }

    /// <summary>How strongly surfaces reflect what is around them, 0 to 4; 1 (physically balanced) unless set.</summary>
    public static float ReflectionStrength
    {
        get => _reflectionStrength;
        set => _reflectionStrength = float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 1f;
    }

    /// <summary>The detail of the captured view when nothing sets it: 2 of 1 to 3.</summary>
    public const int DefaultQuality = 2;

    /// <summary>Off for a whole run with GENESIS_LIGHT_PROBES=0 (comparisons with the flat ambient look).</summary>
    public static bool Available { get; } =
        Environment.GetEnvironmentVariable(EnvironmentVariable) is not { } value || value.Trim() is not ("0" or "false");

    /// <summary>
    /// Whether this process captures light probes: a game turns it on when it starts. Editor views
    /// change their scene without the camera moving (a shader moved to another part, an Object
    /// placed), so a probe there would show what was, not what is; they keep the flat look.
    /// </summary>
    public static bool InGame
    {
        get => _inGame;
        set
        {
            _inGame = value;
            // No capture is coming any more: culling must stop keeping things for one.
            if (!value) PendingCapture = default;
        }
    }

    private static bool _inGame;

    /// <summary>Smooth and metal surfaces mirror what is around them. On unless a room or script turns it off.</summary>
    public static bool Reflections { get; set; } = true;

    /// <summary>1 to 3: the detail of the captured view (32, 64 or 128 pixels a side).</summary>
    public static int ReflectionQuality
    {
        get => Volatile.Read(ref _quality);
        set
        {
            int quality = Math.Clamp(value, 1, 3);
            if (Interlocked.Exchange(ref _quality, quality) != quality) Recapture();
        }
    }

    /// <summary>Surfaces are lit by the light around them (the sky, and the walls and ground it falls on).</summary>
    public static bool BouncedLight { get; set; } = true;

    /// <summary>0 keeps the room's flat ambient colours, 1 (unless set) lights each surface by what is around it.</summary>
    public static float BouncedLightStrength
    {
        get => _bouncedLightStrength;
        set => _bouncedLightStrength = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
    }

    /// <summary>The share of the room's own ambient light kept under bounced light unless set.</summary>
    public const float DefaultAmbientFloor = 0.35f;

    private static float _ambientFloor = DefaultAmbientFloor;

    /// <summary>
    /// The least light bounced light and reflections leave a surface, as a share of the room's own
    /// ambient light and reflection, 0 to 1 (0.35 unless set). A probe that captured a dark room or a
    /// black background used to take every shaded surface to pure black.
    /// </summary>
    public static float AmbientFloor
    {
        get => _ambientFloor;
        set => _ambientFloor = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : DefaultAmbientFloor;
    }

    /// <summary>The pixels a side of each face of the captured view.</summary>
    public static int FaceSize => 16 << ReflectionQuality;

    /// <summary>Raised to have every 3D view capture again (a room's start, Engine.Lighting.Recapture()).</summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>Asks every 3D view to capture what is around its camera again, at its next frame.</summary>
    public static void Recapture() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// A room's start: reflections and bounced light on, at the usual detail and distances, captured
    /// afresh; <paramref name="reflectionStrength"/> is the room's own environment reflection (0 for 1).
    /// </summary>
    public static void ResetForRoom(float reflectionStrength = 0f)
    {
        Reflections = true;
        BouncedLight = true;
        _bouncedLightStrength = 1f;
        _ambientFloor = DefaultAmbientFloor;
        ReflectionStrength = reflectionStrength > 0f ? reflectionStrength : 1f;
        _recaptureDistance = DefaultRecaptureDistance;
        _captureRadius = DefaultCaptureRadius;
        Volatile.Write(ref _quality, DefaultQuality);
        Recapture();
    }

    /// <summary>
    /// The capture a view makes at its next frame: xyz where, w its radius; w = 0 when none. Culling
    /// keeps what is inside it for that frame, so the capture sees behind the camera too.
    /// </summary>
    public static Vector4 PendingCapture;

    /// <summary>True when a sphere is inside the capture about to be made.</summary>
    public static bool KeepsForCapture(Vector3 centre, float radius)
    {
        Vector4 pending = PendingCapture;
        if (pending.W <= 0f) return false;
        float reach = pending.W + radius;
        return Vector3.DistanceSquared(centre, new Vector3(pending.X, pending.Y, pending.Z)) <= reach * reach;
    }
}

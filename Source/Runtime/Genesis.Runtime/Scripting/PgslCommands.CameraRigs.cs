#nullable enable
using System;
using System.IO;
using System.Numerics;
using Genesis.Runtime.Cameras;
using Genesis.Runtime.Navigation;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Camera rigs (Engine.Camera.Rigs): named virtual cameras a game makes from script (fixed, follow,
// orbit, shoulder, look-at, dolly), one of which drives the 3D camera; switching blends position,
// rotation and field of view. A room where no rig is made active keeps its camera exactly as before.
public static partial class PgslCommands
{
    // Reading never makes a room's rigs; setting something up does.
    private static CameraRigDirector? RigsIfAny() => CameraRigDirector.For(ActiveGameContext?.Scene);
    private static CameraRigDirector? RigsMade() => CameraRigDirector.Make(ActiveGameContext?.Scene);
    private static CameraRig? RigNamed(string name) => RigsIfAny()?.Find(name);
    private static CameraRig? SelectedRig() => RigsIfAny() is { } rigs ? rigs.Find(rigs.Selected) : null;

    private static bool RigBlendTo(string name, double seconds, string curve)
    {
        if (ActiveGameContext?.Scene is not { } scene || RigsMade() is not { } rigs) return false;
        CameraRigNames.TryCurve(curve, out CameraBlendCurve parsed);
        return rigs.BlendTo(name, double.IsFinite(seconds) ? (float)Math.Clamp(seconds, 0, 600) : 0f, parsed, scene.Camera3D);
    }

    private static bool RigUsePath(CameraRig? rig, string pathing)
    {
        if (rig == null) return false;
        string path = ResolvePathingAsset(pathing);
        if (string.IsNullOrWhiteSpace(path))
        {
            ActiveGameContext?.Log($"Pathing resource was not found: {pathing}");
            return false;
        }
        try { rig.Path.UseRoute(PathingAssetSerializer.Load(path).Route); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ActiveGameContext?.Log($"Pathing resource could not be loaded: {exception.Message}");
            return false;
        }
        return true;
    }

    private static Vector3? RigPoint(double x, double y, double z) =>
        Finite3(x, y, z) ? new Vector3((float)x, (float)y, (float)z) : null;

    // ── The flat commands: each names its rig ────────────────────────────────────────────

    [PgslCommand("CameraRigCreate", "CameraRigCreate(name, kind) -> bool", "Make a camera rig: \"fixed\", \"follow\", \"orbit\", \"shoulder\", \"lookat\" or \"dolly\", in place of any of that name; a fixed or look-at rig starts where the camera is. It drives the camera once made active (CameraRigBlendTo, Engine.Camera.Rigs.Active). False for an unknown kind or an empty name", "Camera")]
    public static bool CameraRigCreate(string name, string kind) =>
        CameraRigNames.TryKind(kind, out CameraRigKind parsed) && ActiveGameContext?.Scene is { } scene
        && RigsMade()?.Create(name, parsed, scene.Camera3D) != null;

    [PgslCommand("CameraRigRemove", "CameraRigRemove(name) -> bool", "Remove a camera rig; when it was the active one the camera is handed back at once. False when there was none of that name", "Camera")]
    public static bool CameraRigRemove(string name) => RigsIfAny()?.Remove(name) ?? false;

    [PgslCommand("CameraRigExists", "CameraRigExists(name) -> bool", "Whether this room has a camera rig of that name", "Camera")]
    public static bool CameraRigExists(string name) => RigNamed(name) != null;

    [PgslCommand("CameraRigBlendTo", "CameraRigBlendTo(name, seconds, curve) -> bool", "Make a camera rig the one driving the camera, blending its position, rotation and field of view from where the camera is over the seconds given; curve is \"linear\", \"easein\", \"easeout\", \"easeinout\" or \"cut\". \"\" hands the camera back at once. False when no rig has the name yet (one made with it takes over then)", "Camera")]
    public static bool CameraRigBlendTo(string name, double seconds, string curve) => RigBlendTo(name, seconds, curve);

    [PgslCommand("CameraRigCut", "CameraRigCut(name) -> bool", "Make a camera rig the one driving the camera at once, with no blend; \"\" hands the camera back", "Camera")]
    public static bool CameraRigCut(string name) => RigBlendTo(name, 0, "cut");

    [PgslCommand("CameraRigActive", "CameraRigActive() -> string", "The name of the camera rig driving the camera (\"\" when none is)", "Camera")]
    public static string CameraRigActive() => RigsIfAny()?.Active ?? "";

    [PgslCommand("CameraRigBlending", "CameraRigBlending() -> bool", "Whether the camera is blending from one rig to another", "Camera")]
    public static bool CameraRigBlending() => RigsIfAny()?.Blending ?? false;

    [PgslCommand("CameraSetRigTarget", "CameraSetRigTarget(name, instance)", "The instance a camera rig follows, circles or looks at (0 for none); once it is gone the rig keeps to where it was last", "Camera")]
    public static void CameraSetRigTarget(string name, double instance)
    {
        if (RigNamed(name) is { } rig) rig.TargetInstance = double.IsFinite(instance) && instance >= 1 && instance <= int.MaxValue ? (int)instance : 0;
    }

    [PgslCommand("CameraSetRigTargetPoint", "CameraSetRigTargetPoint(name, x, y, z)", "A world point a camera rig follows, circles or looks at, in place of an instance", "Camera")]
    public static void CameraSetRigTargetPoint(string name, double x, double y, double z)
    {
        if (RigNamed(name) is { } rig && RigPoint(x, y, z) is { } point) rig.TargetPoint = point;
    }

    [PgslCommand("CameraSetRigOffset", "CameraSetRigOffset(name, x, y, z)", "Fixed and look-at rigs: where the camera stands. Follow: where it stands from its target. Orbit and shoulder: the arm's pivot from the target (x to the side, y up, z ahead). Dolly: added to the path", "Camera")]
    public static void CameraSetRigOffset(string name, double x, double y, double z)
    {
        if (RigNamed(name) is { } rig && RigPoint(x, y, z) is { } offset) rig.Offset = offset;
    }

    [PgslCommand("CameraSetRigLookOffset", "CameraSetRigLookOffset(name, x, y, z)", "Added to a camera rig's target before it is looked at (a head above the feet)", "Camera")]
    public static void CameraSetRigLookOffset(string name, double x, double y, double z)
    {
        if (RigNamed(name) is { } rig && RigPoint(x, y, z) is { } offset) rig.LookOffset = offset;
    }

    [PgslCommand("CameraSetRigFov", "CameraSetRigFov(name, degrees)", "A camera rig's vertical field of view, 1 to 179 degrees; 0 keeps the camera's own", "Camera")]
    public static void CameraSetRigFov(string name, double degrees)
    {
        if (RigNamed(name) is { } rig) rig.FovDegrees = Finite(degrees, 0, 179, rig.FovDegrees);
    }

    [PgslCommand("CameraSetRigDamping", "CameraSetRigDamping(name, seconds)", "How long a camera rig takes to catch up with where it should be, 0 (rigid) to 10 seconds", "Camera")]
    public static void CameraSetRigDamping(string name, double seconds)
    {
        if (RigNamed(name) is { } rig) rig.Damping = Finite(seconds, 0, 10, rig.Damping);
    }

    [PgslCommand("CameraSetRigAngles", "CameraSetRigAngles(name, yaw, pitch)", "Degrees: the way a fixed rig faces, or round its target an orbit looks (yaw 0 along -z, 90 along +x), and how far it looks down (negative up)", "Camera")]
    public static void CameraSetRigAngles(string name, double yaw, double pitch)
    {
        if (RigNamed(name) is not { } rig) return;
        if (double.IsFinite(yaw)) rig.Yaw = (float)(yaw % 360);
        rig.Pitch = Finite(pitch, -89, 89, rig.Pitch);
    }

    [PgslCommand("CameraSetRigDistance", "CameraSetRigDistance(name, distance)", "How far an orbit or shoulder rig sits from its target, 0.05 to 1000", "Camera")]
    public static void CameraSetRigDistance(string name, double distance)
    {
        if (RigNamed(name) is { } rig) rig.Distance = Finite(distance, 0.05, 1000, rig.Distance);
    }

    [PgslCommand("CameraSetRigMouseLook", "CameraSetRigMouseLook(name, enabled)", "Whether moving the mouse turns an orbit or shoulder rig while it drives the camera (on for those unless set)", "Camera")]
    public static void CameraSetRigMouseLook(string name, bool enabled)
    {
        if (RigNamed(name) is { } rig) rig.MouseLook = enabled;
    }

    [PgslCommand("CameraSetRigCollision", "CameraSetRigCollision(name, enabled)", "Whether an orbit or shoulder rig moves in front of walls between it and its target (on unless set)", "Camera")]
    public static void CameraSetRigCollision(string name, bool enabled)
    {
        if (RigNamed(name) is { } rig) rig.Collision = enabled;
    }

    [PgslCommand("CameraRigPathAdd", "CameraRigPathAdd(name, x, y, z) -> number", "Add a world point to the end of a dolly rig's path; gives how many points it has", "Camera")]
    public static double CameraRigPathAdd(string name, double x, double y, double z)
    {
        if (RigNamed(name) is not { } rig) return 0;
        if (RigPoint(x, y, z) is { } point) rig.Path.Add(point);
        return rig.Path.Count;
    }

    [PgslCommand("CameraRigPathClear", "CameraRigPathClear(name)", "Take every point off a dolly rig's path", "Camera")]
    public static void CameraRigPathClear(string name) => RigNamed(name)?.Path.Clear();

    [PgslCommand("CameraSetRigPath", "CameraSetRigPath(name, pathing) -> bool", "Give a dolly rig a Pathing resource's route as its path: its waypoints, curved where they are marked to curve, closed when it loops. False when the resource cannot be read", "Camera")]
    public static bool CameraSetRigPath(string name, string pathing) => RigUsePath(RigNamed(name), pathing);

    [PgslCommand("CameraSetRigPathPosition", "CameraSetRigPathPosition(name, position)", "Where a dolly rig is along its path, 0 (its first point) to 1 (its end), by distance", "Camera")]
    public static void CameraSetRigPathPosition(string name, double position)
    {
        if (RigNamed(name) is { } rig) rig.PathPosition = Finite(position, 0, 1, rig.PathPosition);
    }

    [PgslCommand("CameraGetRigPathPosition", "CameraGetRigPathPosition(name) -> number", "Where a dolly rig is along its path, 0 to 1", "Camera")]
    public static double CameraGetRigPathPosition(string name) => RigNamed(name)?.PathPosition ?? 0;

    [PgslCommand("CameraSetRigPathSpeed", "CameraSetRigPathSpeed(name, speed)", "How fast a dolly rig rides its path while it is in use, -1000 to 1000 metres a second; negative rides back", "Camera")]
    public static void CameraSetRigPathSpeed(string name, double speed)
    {
        if (RigNamed(name) is { } rig) rig.PathSpeed = Finite(speed, -1000, 1000, rig.PathSpeed);
    }

    [PgslCommand("CameraSetRigPathLoop", "CameraSetRigPathLoop(name, loop)", "Whether a dolly rig's path closes back to its first point and riding it goes round and round", "Camera")]
    public static void CameraSetRigPathLoop(string name, bool loop)
    {
        if (RigNamed(name) is { } rig) rig.PathLoop = loop;
    }

    [PgslCommand("CameraSetRigPathSmooth", "CameraSetRigPathSmooth(name, smooth)", "Whether a dolly rig's path curves smoothly through its points instead of running straight between them", "Camera")]
    public static void CameraSetRigPathSmooth(string name, bool smooth)
    {
        if (RigNamed(name) is { } rig) rig.Path.Smooth = smooth;
    }

    // ── Engine.Camera.Rigs: the room's rigs and the one driving the camera ──────────────

    [PgslCommand("Enabled", "Engine.Camera.Rigs.Enabled = true", "Whether the active camera rig drives the 3D camera (true unless set); false hands the camera back as it is without a rig and keeps the rigs", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsEnabled { get => RigsIfAny()?.Enabled ?? true; set { if (RigsMade() is { } rigs) rigs.Enabled = value; } }

    [PgslCommand("Active", "Engine.Camera.Rigs.Active = \"intro\"", "The camera rig driving the 3D camera (\"\" unless set): setting it blends there over BlendTime along BlendCurve, \"\" hands the camera back at once; a name no rig has yet takes over when a rig of that name is made", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static string CameraRigsActive
    {
        get => RigsIfAny()?.Active ?? "";
        set { if (RigsMade() is { } rigs) RigBlendTo(value, rigs.BlendTime, CameraRigNames.CurveName(rigs.BlendCurve)); }
    }

    [PgslCommand("BlendTime", "Engine.Camera.Rigs.BlendTime = seconds", "How long setting Active takes to blend from one camera rig to the next, 0 to 60 seconds (1 unless set); 0 cuts", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static double CameraRigsBlendTime { get => RigsIfAny()?.BlendTime ?? 1; set { if (RigsMade() is { } rigs) rigs.BlendTime = Finite(value, 0, 60, rigs.BlendTime); } }

    [PgslCommand("BlendCurve", "Engine.Camera.Rigs.BlendCurve = \"easeinout\"", "How a blend set going by Active moves: \"cut\", \"linear\", \"easein\", \"easeout\" or \"easeinout\" (\"easeinout\" unless set)", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static string CameraRigsBlendCurve
    {
        get => CameraRigNames.CurveName(RigsIfAny()?.BlendCurve ?? CameraBlendCurve.EaseInOut);
        set { if (RigsMade() is { } rigs && CameraRigNames.TryCurve(value, out CameraBlendCurve curve)) rigs.BlendCurve = curve; }
    }

    [PgslCommand("Blending", "Engine.Camera.Rigs.Blending", "Whether the camera is blending from one rig to another (read)", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsBlending => RigsIfAny()?.Blending ?? false;

    [PgslCommand("Progress", "Engine.Camera.Rigs.Progress", "How far the blend into the active rig has gone, 0 to 1; 1 when none is under way (read)", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static double CameraRigsProgress => RigsIfAny()?.Progress ?? 1;

    [PgslCommand("Count", "Engine.Camera.Rigs.Count", "How many camera rigs this room has (read)", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static double CameraRigsCount => RigsIfAny()?.Count ?? 0;

    [PgslCommand("Create", "Engine.Camera.Rigs.Create(name, kind) -> bool", "Make a camera rig (\"fixed\", \"follow\", \"orbit\", \"shoulder\", \"lookat\" or \"dolly\") and choose it for Engine.Camera.Rig's values; false for an unknown kind", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsCreate(string name, string kind) => CameraRigCreate(name, kind);

    [PgslCommand("Remove", "Engine.Camera.Rigs.Remove(name) -> bool", "Remove a camera rig; when it was the active one the camera is handed back at once", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsRemove(string name) => CameraRigRemove(name);

    [PgslCommand("Clear", "Engine.Camera.Rigs.Clear()", "Remove every camera rig of this room and hand the camera back", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static void CameraRigsClear() => RigsIfAny()?.Clear();

    [PgslCommand("BlendTo", "Engine.Camera.Rigs.BlendTo(name, seconds, curve) -> bool", "Make a camera rig the active one, blending over the seconds given along the curve (\"linear\", \"easein\", \"easeout\", \"easeinout\", \"cut\")", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsBlendTo(string name, double seconds, string curve) => RigBlendTo(name, seconds, curve);

    [PgslCommand("Cut", "Engine.Camera.Rigs.Cut(name) -> bool", "Make a camera rig the active one at once, with no blend", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsCut(string name) => RigBlendTo(name, 0, "cut");

    [PgslCommand("Exists", "Engine.Camera.Rigs.Exists(name) -> bool", "Whether this room has a camera rig of that name", "Camera", Namespace = "Engine.Camera.Rigs", Flat = false)]
    public static bool CameraRigsExists(string name) => CameraRigExists(name);

    // ── Engine.Camera.Rig: the rig being set up (the last made, or the one named) ────────

    [PgslCommand("Name", "Engine.Camera.Rig.Name = \"intro\"", "The camera rig Engine.Camera.Rig's values belong to: the last one made, or the one named here", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static string CameraRigName
    {
        get => SelectedRig()?.Name ?? "";
        set { if (RigsIfAny() is { } rigs && rigs.Find(value) is { } rig) rigs.Selected = rig.Name; }
    }

    [PgslCommand("Kind", "Engine.Camera.Rig.Kind = \"follow\"", "What the rig does: \"fixed\", \"follow\", \"orbit\", \"shoulder\", \"lookat\" or \"dolly\"; changing it keeps the rig's values", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static string CameraRigKindName
    {
        get => SelectedRig() is { } rig ? CameraRigNames.KindName(rig.Kind) : "";
        set
        {
            if (SelectedRig() is not { } rig || !CameraRigNames.TryKind(value, out CameraRigKind kind)) return;
            rig.Kind = kind;
            rig.Settle();
        }
    }

    [PgslCommand("Target", "Engine.Camera.Rig.Target = id", "The instance the rig follows, circles or looks at (0 for none)", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigTarget { get => SelectedRig()?.TargetInstance ?? 0; set => CameraSetRigTarget(SelectedRig()?.Name ?? "", value); }

    [PgslCommand("SetTargetPoint", "Engine.Camera.Rig.SetTargetPoint(x, y, z)", "A world point the rig follows, circles or looks at, in place of an instance", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static void CameraRigSetTargetPoint(double x, double y, double z) => CameraSetRigTargetPoint(SelectedRig()?.Name ?? "", x, y, z);

    [PgslCommand("OffsetX", "Engine.Camera.Rig.OffsetX = x", "Across: where a fixed or look-at rig stands, a follow rig from its target, an orbit's pivot to the side, or a dolly's path moved", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigOffsetX { get => SelectedRig()?.Offset.X ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.Offset = rig.Offset with { X = (float)value }; } }

    [PgslCommand("OffsetY", "Engine.Camera.Rig.OffsetY = y", "Up: the height of a fixed or look-at rig, a follow rig above its target, an orbit's pivot, or a dolly's path moved", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigOffsetY { get => SelectedRig()?.Offset.Y ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.Offset = rig.Offset with { Y = (float)value }; } }

    [PgslCommand("OffsetZ", "Engine.Camera.Rig.OffsetZ = z", "Along z: where a fixed or look-at rig stands, a follow rig from its target, an orbit's pivot ahead, or a dolly's path moved", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigOffsetZ { get => SelectedRig()?.Offset.Z ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.Offset = rig.Offset with { Z = (float)value }; } }

    [PgslCommand("LookOffsetX", "Engine.Camera.Rig.LookOffsetX = x", "Added across to the target before the rig looks at it", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigLookOffsetX { get => SelectedRig()?.LookOffset.X ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.LookOffset = rig.LookOffset with { X = (float)value }; } }

    [PgslCommand("LookOffsetY", "Engine.Camera.Rig.LookOffsetY = y", "Added up to the target before the rig looks at it (a head above the feet)", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigLookOffsetY { get => SelectedRig()?.LookOffset.Y ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.LookOffset = rig.LookOffset with { Y = (float)value }; } }

    [PgslCommand("LookOffsetZ", "Engine.Camera.Rig.LookOffsetZ = z", "Added along z to the target before the rig looks at it", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigLookOffsetZ { get => SelectedRig()?.LookOffset.Z ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.LookOffset = rig.LookOffset with { Z = (float)value }; } }

    [PgslCommand("Fov", "Engine.Camera.Rig.Fov = degrees", "The rig's vertical field of view, 1 to 179 degrees; 0 keeps the camera's own", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigFov { get => SelectedRig()?.FovDegrees ?? 0; set => CameraSetRigFov(SelectedRig()?.Name ?? "", value); }

    [PgslCommand("Damping", "Engine.Camera.Rig.Damping = seconds", "How long the rig takes to catch up with where it should be, 0 (rigid) to 10 seconds: 0.3 is a soft follow", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigDamping { get => SelectedRig()?.Damping ?? 0; set => CameraSetRigDamping(SelectedRig()?.Name ?? "", value); }

    [PgslCommand("Yaw", "Engine.Camera.Rig.Yaw = degrees", "The way a fixed rig faces, or round its target an orbit or shoulder rig looks: 0 along -z, 90 along +x", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigYaw { get => SelectedRig()?.Yaw ?? 0; set { if (SelectedRig() is { } rig && double.IsFinite(value)) rig.Yaw = (float)(value % 360); } }

    [PgslCommand("Pitch", "Engine.Camera.Rig.Pitch = degrees", "How far the rig looks down, -89 to 89 degrees (negative looks up)", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigPitch { get => SelectedRig()?.Pitch ?? 0; set { if (SelectedRig() is { } rig) rig.Pitch = Finite(value, -89, 89, rig.Pitch); } }

    [PgslCommand("Distance", "Engine.Camera.Rig.Distance = metres", "How far an orbit or shoulder rig sits from its target, 0.05 to 1000", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static double CameraRigDistance { get => SelectedRig()?.Distance ?? 0; set => CameraSetRigDistance(SelectedRig()?.Name ?? "", value); }

    [PgslCommand("MouseLook", "Engine.Camera.Rig.MouseLook = false", "Whether moving the mouse turns an orbit or shoulder rig while it drives the camera (on for those unless set)", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static bool CameraRigMouseLook { get => SelectedRig()?.MouseLook ?? false; set { if (SelectedRig() is { } rig) rig.MouseLook = value; } }

    [PgslCommand("Collision", "Engine.Camera.Rig.Collision = false", "Whether an orbit or shoulder rig moves in front of walls between it and its target (on unless set)", "Camera", Namespace = "Engine.Camera.Rig", Flat = false)]
    public static bool CameraRigCollision { get => SelectedRig()?.Collision ?? true; set { if (SelectedRig() is { } rig) rig.Collision = value; } }

    // ── Engine.Camera.Rig.Path: a dolly rig's path ───────────────────────────────────────

    [PgslCommand("Position", "Engine.Camera.Rig.Path.Position = 0.5", "Where the dolly rig is along its path, 0 (its first point) to 1 (its end), by distance", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static double CameraRigPathPosition { get => SelectedRig()?.PathPosition ?? 0; set => CameraSetRigPathPosition(SelectedRig()?.Name ?? "", value); }

    [PgslCommand("Speed", "Engine.Camera.Rig.Path.Speed = metres", "How fast the dolly rig rides its path while it is in use, -1000 to 1000 metres a second; negative rides back", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static double CameraRigPathSpeed { get => SelectedRig()?.PathSpeed ?? 0; set => CameraSetRigPathSpeed(SelectedRig()?.Name ?? "", value); }

    [PgslCommand("Loop", "Engine.Camera.Rig.Path.Loop = true", "Whether the path closes back to its first point and riding it goes round and round", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static bool CameraRigPathLoop { get => SelectedRig()?.PathLoop ?? false; set { if (SelectedRig() is { } rig) rig.PathLoop = value; } }

    [PgslCommand("Smooth", "Engine.Camera.Rig.Path.Smooth = true", "Whether the path curves smoothly through its points instead of running straight between them", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static bool CameraRigPathSmooth { get => SelectedRig()?.Path.Smooth ?? false; set { if (SelectedRig() is { } rig) rig.Path.Smooth = value; } }

    [PgslCommand("Count", "Engine.Camera.Rig.Path.Count", "How many points the path has (read)", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static double CameraRigPathCount => SelectedRig()?.Path.Count ?? 0;

    [PgslCommand("Length", "Engine.Camera.Rig.Path.Length", "How long the path is, in metres (read)", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static double CameraRigPathLength => SelectedRig()?.Path.Length ?? 0;

    [PgslCommand("Add", "Engine.Camera.Rig.Path.Add(x, y, z) -> number", "Add a world point to the end of the path; gives how many points it has", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static double CameraRigPathAddPoint(double x, double y, double z) => CameraRigPathAdd(SelectedRig()?.Name ?? "", x, y, z);

    [PgslCommand("Clear", "Engine.Camera.Rig.Path.Clear()", "Take every point off the path", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static void CameraRigPathClearPoints() => SelectedRig()?.Path.Clear();

    [PgslCommand("Use", "Engine.Camera.Rig.Path.Use(pathing) -> bool", "Take the path from a Pathing resource's route: its waypoints, curved where they are marked to curve, closed when it loops; false when it cannot be read", "Camera", Namespace = "Engine.Camera.Rig.Path", Flat = false)]
    public static bool CameraRigPathUse(string pathing) => RigUsePath(SelectedRig(), pathing);
}

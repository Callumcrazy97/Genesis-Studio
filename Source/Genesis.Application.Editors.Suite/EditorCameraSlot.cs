using System.Numerics;

namespace Genesis.Application.Editors.Suite;

/// <summary>Which shared camera the viewport is currently treating as a first-class slot.</summary>
public enum EditorCameraSlotKind
{
    Primary,
    PinnedSecondary,
    AuthoredGame,
}

/// <summary>How a secondary camera moves after it has been created.</summary>
public enum EditorCameraMotionMode
{
    Pinned,
    FollowSelection,
    OrbitPoint,
}

/// <summary>
/// One camera the shared viewport can show: the editor orbit rig, a pinned second view, or an
/// authored/game camera override. Editors keep their own save documents; this is session state.
/// </summary>
public sealed class EditorCameraSlot
{
    public EditorCameraSlotKind Kind { get; set; } = EditorCameraSlotKind.PinnedSecondary;

    public string Label { get; set; } = "Second camera";

    public OrbitCameraRig Camera { get; } = new();

    public float FieldOfViewDegrees { get; set; } = 60f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 900f;

    public float Camera2DX { get; set; }

    public float Camera2DY { get; set; }

    public float Zoom2D { get; set; } = 1f;

    public float OrbitRadius2D { get; set; } = 96f;

    public float OrbitAngle2D { get; set; }

    public EditorCameraMotionMode Motion { get; set; } = EditorCameraMotionMode.Pinned;

    public Vector3 OrbitPoint { get; set; }

    public float OrbitSpeedDegreesPerSecond { get; set; } = 18f;

    /// <summary>When set, the inset uses these matrices instead of the orbit rig (Room game camera).</summary>
    public Func<EditorCameraOverride>? OverrideFactory { get; set; }

    public void CopyViewFrom(EditorViewport3D source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Camera.CopyFrom(source.Camera);
        FieldOfViewDegrees = source.FieldOfViewDegrees;
        NearPlane = source.NearPlane;
        FarPlane = source.FarPlane;
        Camera2DX = source.Camera2DX;
        Camera2DY = source.Camera2DY;
        Zoom2D = source.Zoom2D;
        OverrideFactory = null;
        Motion = EditorCameraMotionMode.Pinned;
    }
}

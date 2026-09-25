using System.Numerics;

namespace Genesis.Physics;

/// <summary>Per-frame input for <see cref="PhysicsInteractionSession"/> (camera + movement + grab).</summary>
public struct PhysicsInteractionInput
{
    public Vector3 CameraPosition;
    public Vector3 CameraForward;
    public Vector3 CameraRight;

    public bool MoveForward;
    public bool MoveBack;
    public bool MoveLeft;
    public bool MoveRight;
    public bool Jump;
    public bool Sprint;

    public bool GrabHeld;
    public bool GrabPressed;
    public Vector2 LookDelta;
}

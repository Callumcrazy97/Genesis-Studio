using System.Numerics;
using BepuPhysics;

namespace Genesis.Physics;

/// <summary>
/// Lightweight handle returned by <see cref="SandboxPhysicsWorld"/> body registration (dynamic or static).
/// </summary>
/// <remarks>
/// A default-initialized value — <c>default(PhysicsBody)</c>, <c>PhysicsBody.None</c>, or any unset
/// field — always has <see cref="IsValid"/> == false. Callers must use <see cref="IsValid"/> instead of
/// inspecting <see cref="Handle"/> directly: Bepu freely assigns handle/slot 0 to the first real body it
/// creates, so comparing against 0 cannot distinguish "no body" from "the first body."
/// </remarks>
public readonly record struct PhysicsBody(
    BodyHandle Handle,
    Vector3 HalfExtents,
    int SlotIndex = -1,
    bool IsStatic = false,
    bool IsValid = true)
{
    /// <summary>Sentinel representing "no body" — distinct from every real body, including handle/slot 0.</summary>
    public static readonly PhysicsBody None = default;
}

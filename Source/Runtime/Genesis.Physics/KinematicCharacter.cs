using System;
using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Physics;

/// <summary>What a <see cref="KinematicCharacter.Move"/> ran into.</summary>
[Flags]
public enum CharacterMoveFlags
{
    None = 0,
    /// <summary>Standing on walkable ground after the move.</summary>
    Grounded = 1,
    /// <summary>The head met something on the way up.</summary>
    Ceiling = 2,
    /// <summary>Blocked by something too steep to walk on.</summary>
    Wall = 4,
    /// <summary>Climbed a step on the way.</summary>
    Stepped = 8,
}

/// <summary>
/// An upright capsule moved by a script, not by forces: it slides along what it meets, climbs
/// steps up to <see cref="StepHeight"/>, keeps to the ground going down slopes and steps, and says
/// what it stands on. Its position is its feet. It does not push bodies or get pushed by them.
/// </summary>
public sealed class KinematicCharacter
{
    private const float Skin = 0.01f;

    public KinematicCharacter(float radius, float height, float stepHeight, float maxSlopeDegrees)
    {
        Radius = Math.Clamp(float.IsFinite(radius) ? radius : 0.35f, 0.05f, 10f);
        Height = Math.Max(Radius * 2f, float.IsFinite(height) ? height : 1.8f);
        StepHeight = Math.Clamp(float.IsFinite(stepHeight) ? stepHeight : 0.3f, 0f, Height * 0.5f);
        MaxSlopeDegrees = Math.Clamp(float.IsFinite(maxSlopeDegrees) ? maxSlopeDegrees : 45f, 0f, 89f);
    }

    public float Radius { get; }
    public float Height { get; private set; }
    public float StepHeight { get; set; }
    public float MaxSlopeDegrees { get; set; }

    /// <summary>The feet: the bottom of the capsule.</summary>
    public Vector3 Position { get; set; }

    public bool Grounded { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.UnitY;

    private float WalkableY => MathF.Cos(MaxSlopeDegrees * MathF.PI / 180f);

    private Vector3 Centre(Vector3 feet, float height) => feet + Vector3.UnitY * (height * 0.5f);

    /// <summary>Whether the capsule would fit standing <paramref name="height"/> tall where it is.</summary>
    public bool Fits(PhysicsWorld physics, IEcsWorld world, float height, Entity ignore = default)
    {
        height = Math.Max(Radius * 2f, height);
        // Lifted a hair so the ground under the feet does not count as an overlap.
        return !physics.CapsuleOverlaps(world, Centre(Position + Vector3.UnitY * (Skin * 2f), height) , Radius - Skin, height - Skin * 2f,
            out _, ignore.IsNull ? null : ignore);
    }

    /// <summary>Changes the capsule's height (crouch, stand); false and unchanged when it would not fit.</summary>
    public bool SetHeight(PhysicsWorld physics, IEcsWorld world, float height, Entity ignore = default)
    {
        height = Math.Max(Radius * 2f, float.IsFinite(height) ? height : Height);
        if (height > Height && !Fits(physics, world, height, ignore)) return false;
        Height = height;
        return true;
    }

    /// <summary>Moves the feet by <paramref name="delta"/> (metres) as far as the world allows.</summary>
    public CharacterMoveFlags Move(PhysicsWorld physics, IEcsWorld world, Vector3 delta, Entity ignore = default)
    {
        if (physics == null || world == null || !float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
            return Grounded ? CharacterMoveFlags.Grounded : CharacterMoveFlags.None;
        Entity? skip = ignore.IsNull ? null : ignore;
        CharacterMoveFlags flags = CharacterMoveFlags.None;
        bool wasGrounded = Grounded;
        Vector3 feet = Position;

        feet = Slide(physics, world, feet, new Vector3(delta.X, 0f, delta.Z), wasGrounded, skip, ref flags);
        feet = Slide(physics, world, feet, new Vector3(0f, delta.Y, 0f), false, skip, ref flags);

        // Ground under the feet; when it was there before and the move did not lift away from it,
        // follow it down a slope or a step rather than float off the edge.
        float probe = Skin * 3f + (wasGrounded && delta.Y <= 0f ? StepHeight : 0f);
        Grounded = false;
        if (physics.CapsuleCast(world, Centre(feet, Height), Radius, Height, -Vector3.UnitY, probe + Skin, out PhysicsRaycastHit ground, skip)
            && ground.Normal.Y >= WalkableY)
        {
            feet.Y -= MathF.Max(0f, ground.Distance - Skin);
            Grounded = true;
            GroundNormal = ground.Normal;
        }
        else
        {
            GroundNormal = Vector3.UnitY;
        }

        Position = feet;
        if (Grounded) flags |= CharacterMoveFlags.Grounded;
        return flags;
    }

    private Vector3 Slide(PhysicsWorld physics, IEcsWorld world, Vector3 feet, Vector3 move, bool allowStep, Entity? skip,
        ref CharacterMoveFlags flags)
    {
        float walkable = WalkableY;
        for (int iteration = 0; iteration < 4; iteration++)
        {
            float length = move.Length();
            if (length < 1e-5f) break;
            Vector3 direction = move / length;
            if (!physics.CapsuleCast(world, Centre(feet, Height), Radius, Height, direction, length + Skin, out PhysicsRaycastHit hit, skip))
            {
                feet += move;
                break;
            }
            float travel = MathF.Max(0f, hit.Distance - Skin);
            feet += direction * travel;
            Vector3 remaining = direction * (length - travel);
            Vector3 normal = hit.Normal;
            bool wall = MathF.Abs(normal.Y) < walkable;
            if (normal.Y < -0.5f) flags |= CharacterMoveFlags.Ceiling;

            if (wall && allowStep && StepHeight > 0f && TryStep(physics, world, ref feet, remaining, skip))
            {
                flags |= CharacterMoveFlags.Stepped;
                break;
            }
            if (wall)
            {
                flags |= CharacterMoveFlags.Wall;
                // Slide along a wall sideways only: a steep face is not climbed by sliding up it.
                if (MathF.Abs(move.Y) < 1e-6f)
                {
                    Vector3 flat = new(normal.X, 0f, normal.Z);
                    normal = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : normal;
                }
            }
            remaining -= normal * Vector3.Dot(remaining, normal);
            move = remaining;
        }
        return feet;
    }

    private bool TryStep(PhysicsWorld physics, IEcsWorld world, ref Vector3 feet, Vector3 remaining, Entity? skip)
    {
        float length = remaining.Length();
        if (length < 1e-4f) return false;
        Vector3 direction = remaining / length;
        float up = physics.CapsuleCast(world, Centre(feet, Height), Radius, Height, Vector3.UnitY, StepHeight + Skin, out PhysicsRaycastHit above, skip)
            ? MathF.Max(0f, above.Distance - Skin)
            : StepHeight;
        if (up < Skin) return false;
        Vector3 raised = feet + Vector3.UnitY * up;
        // Over the edge by at least half the radius, so the rounded foot lands on the step's top
        // and not on its corner (where it would slide back off).
        float wanted = MathF.Max(length, Radius * 0.5f);
        float forward = physics.CapsuleCast(world, Centre(raised, Height), Radius, Height, direction, wanted + Skin, out PhysicsRaycastHit ahead, skip)
            ? MathF.Max(0f, ahead.Distance - Skin)
            : wanted;
        if (forward < Radius * 0.5f) return false;
        Vector3 moved = raised + direction * forward;
        if (!physics.CapsuleCast(world, Centre(moved, Height), Radius, Height, -Vector3.UnitY, up + Skin, out PhysicsRaycastHit landing, skip)
            || landing.Normal.Y < WalkableY)
            return false;
        moved.Y -= MathF.Max(0f, landing.Distance - Skin);
        feet = moved;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Genesis.Physics.Buoyancy;

namespace Genesis.Physics;

public enum SandboxPropShape
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
}

/// <summary>
/// Central sandbox physics interaction: first-person movement, raycast highlight, grab/throw.
/// Used by Engine Sandbox demos; runtime scenes use <see cref="PhysicsWorld"/> + ECS systems instead.
/// </summary>
public sealed class PhysicsInteractionSession : IDisposable
{
    public const float DefaultReach = 8f;
    public const float DefaultHoldDistance = 3.5f;

    private readonly SandboxPhysicsWorld _world;
    private readonly PhysicsBody[] _props;
    private readonly SandboxPropShape[] _propShapes;
    private readonly List<CourseBlock> _courseBlocks = new();
    private readonly Random _rng = new(7);

    private PhysicsBody _heldBody;
    private Vector3 _lastHandAnchor;
    private Vector3 _handVelocity;
    private Vector2 _dragLookAccum;
    private float _viewBobPhase;
    private float _viewBobOffset;
    private float _stepMsAvg;

    public PhysicsBody PlayerBody { get; private set; }
    public int HighlightedPropIndex { get; private set; } = -1;
    public int PropCount => _props.Length;
    public int CourseBlockCount => _courseBlocks.Count;
    public float HorizontalSpeed { get; private set; }
    public float HorizontalAcceleration { get; private set; }
    public float VerticalSpeed { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool IsSprinting { get; private set; }
    public float LastStepMs { get; private set; }
    public float AverageStepMs => _stepMsAvg;
    public float PeakStepMs { get; private set; }
    public int DynamicBodyCount => _world.DynamicBodyCount;
    public int StaticBodyCount => _world.StaticBodyCount;
    public bool HasHeldBody => _heldBody.IsValid;
    public int HeldPropIndex => _heldBody.IsValid && TryGetPropIndex(_heldBody, out int index) ? index : -1;

    public float MoveSpeed { get; set; } = 8f;
    public float JumpSpeed { get; set; } = 5.45f;
    public float SprintMultiplier { get; set; } = 1.6f;
    public float SprintJumpBoost { get; set; } = 1.18f;
    public float SprintJumpForwardBoost { get; set; } = 1.8f;
    public float GroundAcceleration { get; set; } = 40f;
    public float GroundDeceleration { get; set; } = 30f;
    public float AirAcceleration { get; set; } = 8f;
    public float AirDeceleration { get; set; } = 1.2f;
    public float AirControlForward { get; set; } = 0.24f;
    public float AirControlSide { get; set; } = 0.10f;
    public float AirControlBack { get; set; } = 0.045f;
    public float EyeHeight { get; set; } = 1.75f;
    public float Reach { get; set; } = DefaultReach;
    public float HoldDistance { get; set; } = DefaultHoldDistance;
    public float ThrowBaseSpeed { get; set; } = 12f;
    public float ThrowDragScale { get; set; } = 0.02f;
    public float HandVelocityScale { get; set; } = 1.25f;
    public bool ViewBobEnabled { get; set; } = true;
    public float ViewBobWalkAmplitude { get; set; } = 0.03f;
    public float ViewBobRunAmplitude { get; set; } = 0.055f;
    public float ViewBobWalkFrequency { get; set; } = 1.7f;
    public float ViewBobRunFrequency { get; set; } = 2.8f;
    public float ViewBobDamping { get; set; } = 8f;

    /// <summary>Optional water integration — buoyancy is applied in <see cref="Step"/> before the physics tick.</summary>
    public SandboxWaterCallbacks? WaterCallbacks { get; set; }

    public bool IsInWater { get; private set; }
    public float SwimMoveSpeed { get; set; } = 5.5f;
    public float SwimUpSpeed { get; set; } = 4.5f;
    public float SwimAcceleration { get; set; } = 18f;

    public SandboxPhysicsWorld PhysicsWorld => _world;

    public PhysicsInteractionSession(int propCount = 24)
    {
        _world = new SandboxPhysicsWorld();
        _props = new PhysicsBody[propCount];
        _propShapes = new SandboxPropShape[propCount];
    }

    public bool IsPropActive(int index) =>
        (uint)index < (uint)_props.Length && _props[index].IsValid;

    public SandboxPropShape GetPropShape(int index) =>
        (uint)index < (uint)_propShapes.Length ? _propShapes[index] : SandboxPropShape.Box;

    public int ActivePropCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _props.Length; i++)
            {
                if (_props[i].IsValid)
                    count++;
            }

            return count;
        }
    }

    public void BuildPlayground() => BuildStressPlayground();

    /// <summary>Large flat floor with player only — no obstacle course.</summary>
    public void BuildStressPlayground()
    {
        _courseBlocks.Clear();
        ClearProps();

        AddCourseStatic(Vector3.Zero, new Vector3(900f, 0.5f, 900f));
        ResetMovementState();
        PlayerBody = _world.AddCharacterCapsule(new Vector3(0f, 1.25f, 14f), radius: 0.35f, halfHeight: 1.0f);
    }

    /// <summary>Empty terrain sandbox — player spawn only, props added via UI.</summary>
    public void BuildMinimalSandbox(Func<float, float, float> sampleGroundY)
    {
        _courseBlocks.Clear();
        ClearProps();
        sampleGroundY ??= (_, _) => 0f;

        const float spawnX = 0f;
        const float spawnZ = -12f;
        float spawnGround = sampleGroundY(spawnX, spawnZ);

        ResetMovementState();
        PlayerBody = _world.AddCharacterCapsule(
            new Vector3(spawnX, spawnGround + 1.35f, spawnZ),
            radius: 0.35f,
            halfHeight: 1.0f);
    }

    public int SpawnBulkProps(
        SandboxPropShape shape,
        int count,
        Func<float, float, float> sampleGroundY,
        Vector3 origin,
        float spreadRadius,
        float? authoredMass = null)
    {
        if (count <= 0)
            return 0;

        sampleGroundY ??= (_, _) => 0f;
        int spawned = 0;

        for (int slot = 0; slot < _props.Length && spawned < count; slot++)
        {
            if (_props[slot].IsValid)
                continue;

            float angle = (float)_rng.NextDouble() * MathF.Tau;
            float radius = (float)_rng.NextDouble() * spreadRadius;
            float x = origin.X + MathF.Cos(angle) * radius;
            float z = origin.Z + MathF.Sin(angle) * radius;
            float ground = sampleGroundY(x, z);
            float y = ground + 0.55f + (spawned % 4) * 0.45f;
            var position = new Vector3(x, y, z);
            float mass = authoredMass is > 0f ? authoredMass.Value : 1f + (float)_rng.NextDouble();

            _props[slot] = shape switch
            {
                SandboxPropShape.Sphere => _world.AddDynamicSphere(position, 0.5f, mass),
                SandboxPropShape.Capsule => _world.AddDynamicCapsule(position, 0.35f, 0.5f, mass),
                _ => _world.AddDynamicBox(position, new Vector3(0.5f, 0.5f, 0.5f), mass),
            };
            _propShapes[slot] = shape;
            spawned++;
        }

        return spawned;
    }

    public void AddStaticTriangleMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices) =>
        _world.AddStaticTriangleMesh(vertices, indices);

    public bool TrySpawnLightSource(Vector3 position, out int propIndex)
    {
        propIndex = FindFreePropSlot();
        if (propIndex < 0)
            return false;

        _props[propIndex] = _world.AddDynamicBox(position, new Vector3(0.35f, 0.35f, 0.35f), mass: 0.5f);
        _propShapes[propIndex] = SandboxPropShape.Box;
        return true;
    }

    private int FindFreePropSlot()
    {
        for (int i = 0; i < _props.Length; i++)
        {
            if (!_props[i].IsValid)
                return i;
        }

        return -1;
    }

    private void ClearProps()
    {
        Array.Clear(_props, 0, _props.Length);
        Array.Clear(_propShapes, 0, _propShapes.Length);
    }

    private bool TryGetPropIndex(PhysicsBody body, out int propIndex)
    {
        if (!body.IsValid)
        {
            propIndex = -1;
            return false;
        }

        for (int i = 0; i < _props.Length; i++)
        {
            if (_props[i].IsValid && _props[i].Handle.Value == body.Handle.Value)
            {
                propIndex = i;
                return true;
            }
        }

        propIndex = -1;
        return false;
    }

    private void ResetMovementState()
    {
        _viewBobPhase = 0f;
        _viewBobOffset = 0f;
        HorizontalSpeed = 0f;
        HorizontalAcceleration = 0f;
        VerticalSpeed = 0f;
        IsGrounded = false;
        IsSprinting = false;
        IsInWater = false;
        LastStepMs = 0f;
        _stepMsAvg = 0f;
        PeakStepMs = 0f;
    }

    public void Step(in PhysicsInteractionInput input, float dt)
    {
        dt = Math.Min(dt, 0.033f);
        long stepStart = Stopwatch.GetTimestamp();
        UpdateHighlight(input);
        UpdateGrab(input, dt);
        UpdatePlayer(input, dt);
        WaterCallbacks?.PreStep(dt);
        _world.Step(dt);
        CorrectHeldBodyGroundPenetration();

        LastStepMs = (float)Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds;
        _stepMsAvg = _stepMsAvg <= 0f ? LastStepMs : Lerp(_stepMsAvg, LastStepMs, 0.15f);
        PeakStepMs = MathF.Max(PeakStepMs * 0.995f, LastStepMs);
    }

    public void GetPropPose(int index, out Vector3 position, out Quaternion rotation)
    {
        if (index < 0 || index >= _props.Length)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            return;
        }

        _world.GetPose(_props[index], out position, out rotation);
    }

    public PhysicsBody GetPropBody(int index) => _props[index];

    public void GetCourseBlock(int index, out Vector3 position, out Vector3 halfExtents)
    {
        if ((uint)index >= (uint)_courseBlocks.Count)
        {
            position = Vector3.Zero;
            halfExtents = Vector3.One * 0.5f;
            return;
        }

        var block = _courseBlocks[index];
        position = block.Position;
        halfExtents = block.HalfExtents;
    }

    public void GetPlayerPose(out Vector3 position, out Quaternion rotation) =>
        _world.GetPose(PlayerBody, out position, out rotation);

    public void SetPlayerPosition(Vector3 position)
    {
        if (!PlayerBody.IsValid)
            return;

        _world.SetBodyPose(PlayerBody, position, Quaternion.Identity);
        _world.SetLinearVelocity(PlayerBody, Vector3.Zero);
    }

    public Vector3 GetEyePosition()
    {
        _world.GetPose(PlayerBody, out Vector3 pos, out _);
        Vector3 desiredEye = pos + new Vector3(0f, EyeHeight - PlayerBody.HalfExtents.Y + _viewBobOffset, 0f);

        // Camera push-out (Issue 2): the capsule body keeps the player off solid
        // geometry, but the eye offset above is a fixed local height and can still end
        // up inside an overhang/slope the capsule is pressed against. Raycast from the
        // capsule center to the desired eye point and clamp to the first surface hit so
        // the view never slices into the mesh, even when the capsule itself is blocked
        // correctly.
        Vector3 toEye = desiredEye - pos;
        float toEyeDist = toEye.Length();
        if (toEyeDist > 1e-4f)
        {
            Vector3 dir = toEye / toEyeDist;
            if (_world.Raycast(pos, dir, toEyeDist, out _, out float hitDistance, PlayerBody))
            {
                // Pull back slightly from the hit surface to avoid near-plane clipping.
                const float skin = 0.05f;
                float clamped = MathF.Max(0f, hitDistance - skin);
                return pos + dir * clamped;
            }
        }

        return desiredEye;
    }

    /// <summary>Pull a third-person camera out of scenery along the pivot→eye ray.</summary>
    public Vector3 ClampThirdPersonCamera(Vector3 pivot, Vector3 desiredEye, float minDistance = 0.9f, float skin = 0.4f)
    {
        Vector3 offset = desiredEye - pivot;
        float distance = offset.Length();
        if (distance < 1e-4f)
            return desiredEye;

        Vector3 dir = offset / distance;
        float allowed = distance;

        if (_world.Raycast(pivot, dir, distance, out _, out float hitDistance, PlayerBody))
            allowed = MathF.Min(allowed, MathF.Max(minDistance, hitDistance - skin));

        // Sweep a few extra samples — coarse mesh raycasts can miss steep triangle gaps.
        const int extra = 12;
        for (int i = 1; i < extra; i++)
        {
            float t = distance * i / extra;
            if (t >= allowed)
                break;
            Vector3 p = pivot + dir * t;
            if (_world.Raycast(p, dir, allowed - t, out _, out float segHit, PlayerBody))
                allowed = MathF.Min(allowed, MathF.Max(minDistance, t + segHit - skin));
        }

        return pivot + dir * MathF.Max(minDistance, allowed);
    }

    /// <summary>Lock the player capsule to camera yaw so it cannot tumble and spin the visual.</summary>
    public void AlignPlayerToCameraYaw(Vector3 flatForward)
    {
        if (flatForward.LengthSquared() < 1e-6f)
            return;

        flatForward = Vector3.Normalize(flatForward);
        float yaw = MathF.Atan2(flatForward.X, -flatForward.Z);
        _world.SetBodyYaw(PlayerBody, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
    }

    public Vector3 GetPlayerLinearVelocity() => _world.GetLinearVelocity(PlayerBody);

    public void ApplyPhysicsConfig(PhysicsConfig config)
    {
        if (config == null)
            return;

        _world.EnableThreadDispatcher = config.EnableThreadDispatcher;
        _world.AllowSleep = config.AllowSleep;
        _world.SleepThreshold = config.SleepThreshold;
        _world.MaxVelocity = MathF.Max(0f, config.MaxVelocity);
        _world.Gravity = new Vector3(0f, -9.81f * MathF.Max(0f, config.GravityStrength), 0f);
    }

    public void ApplyPhysicsSceneConfig(PhysicsSceneConfig config)
    {
        if (config == null)
            return;

        ApplyPhysicsConfig(config.ToLegacyPhysicsConfig());
        _world.Gravity = config.ResolveGravityVector();
        _world.FrictionCoefficient = (float)config.Friction;
        _world.Restitution = (float)config.Restitution;
        _world.LinearDamping = (float)config.LinearDamping;
        _world.AngularDamping = (float)config.AngularDamping;
        int layer = Math.Clamp(config.CollisionLayer, 0, 6);
        bool[][] matrix = config.CollisionLayerMatrix ?? PhysicsSceneConfig.CreateDefaultCollisionLayerMatrix();
        bool[]? row = layer < matrix.Length ? matrix[layer] : null;
        uint mask = 0;
        for (int i = 0; i < 7; i++)
            if (row is null || i >= row.Length || row[i]) mask |= 1u << i;
        _world.DefaultCollisionLayer = (byte)layer;
        _world.DefaultCollisionMask = mask;

        MoveSpeed = config.MoveSpeed;
        JumpSpeed = config.JumpSpeed;
        SprintMultiplier = config.SprintMultiplier;
        GroundAcceleration = config.GroundAcceleration;
        GroundDeceleration = config.GroundDeceleration;
        AirAcceleration = config.AirAcceleration;
        AirDeceleration = config.AirDeceleration;
        AirControlForward = config.AirControlForward;
        AirControlSide = config.AirControlSide;
        AirControlBack = config.AirControlBack;
    }

    public bool TryGetHeldBodyPose(out Vector3 position, out Quaternion rotation)
    {
        if (_heldBody.IsValid)
        {
            _world.GetPose(_heldBody, out position, out rotation);
            return true;
        }

        position = Vector3.Zero;
        rotation = Quaternion.Identity;
        return false;
    }

    public Vector3 GetPropLinearVelocity(int index)
    {
        if ((uint)index >= (uint)_props.Length)
            return Vector3.Zero;
        return _world.GetLinearVelocity(_props[index]);
    }

    public bool IsPropGrounded(int index, float extraDistance = 0.08f)
    {
        if ((uint)index >= (uint)_props.Length)
            return false;
        return _world.IsGrounded(_props[index], extraDistance, out _);
    }

    private void UpdateHighlight(in PhysicsInteractionInput input)
    {
        HighlightedPropIndex = -1;
        if (_heldBody.IsValid) return;

        if (_world.Raycast(input.CameraPosition, input.CameraForward, Reach, out PhysicsBody hit, PlayerBody)
            && !hit.IsStatic
            && TryGetPropIndex(hit, out int propIndex))
        {
            HighlightedPropIndex = propIndex;
        }
    }

    private void UpdateGrab(in PhysicsInteractionInput input, float dt)
    {
        if (input.GrabPressed)
            TryGrab(input);

        if (_heldBody.IsValid)
        {
            if (input.GrabHeld)
            {
                _dragLookAccum = _dragLookAccum * MathF.Exp(-12.0f * dt) + input.LookDelta;
            }
            else
            {
                ReleaseHeld(input);
                return;
            }

            Vector3 holdPoint = input.CameraPosition + input.CameraForward * HoldDistance;
            holdPoint.Y = MathF.Max(holdPoint.Y, GetMinHeldCenterY(holdPoint));

            // Hand velocity tracks the player's own positional movement only, not look rotation.
            // holdPoint orbits around the camera at HoldDistance whenever you look around, so deriving
            // velocity from holdPoint's delta (the old behaviour) meant simply turning your head while
            // holding an object baked in a large "throw" velocity even though nothing was thrown — this
            // is the engine bug behind objects flinging away on release. Deliberate throws are still
            // captured separately via _dragLookAccum below.
            if (dt > 0f)
                _handVelocity = (input.CameraPosition - _lastHandAnchor) / dt;
            _lastHandAnchor = input.CameraPosition;

            if (dt > 0f)
            {
                _world.GetPose(_heldBody, out Vector3 currPos, out _);
                Vector3 diff = holdPoint - currPos;
                _world.SetLinearVelocity(_heldBody, diff * 12f + _handVelocity);
            }
        }
    }

    private void TryGrab(in PhysicsInteractionInput input)
    {
        if (!_world.Raycast(input.CameraPosition, input.CameraForward, Reach, out PhysicsBody hit, PlayerBody))
            return;

        if (hit.IsStatic || !TryGetPropIndex(hit, out _))
            return;

        _heldBody = hit;
        _dragLookAccum = Vector2.Zero;
        _handVelocity = Vector3.Zero;
        _lastHandAnchor = input.CameraPosition;
        HighlightedPropIndex = -1;
    }

    private void ReleaseHeld(in PhysicsInteractionInput input)
    {
        Vector3 releaseVelocity = _handVelocity * HandVelocityScale;

        if (_dragLookAccum.LengthSquared() > 1f)
        {
            Vector3 throwDir = input.CameraForward;
            throwDir += input.CameraRight * (_dragLookAccum.X * ThrowDragScale);
            throwDir += Vector3.UnitY * (-_dragLookAccum.Y * ThrowDragScale);
            if (throwDir.LengthSquared() > 1e-4f)
                throwDir = Vector3.Normalize(throwDir);

            float dragSpeed = _dragLookAccum.Length() * ThrowDragScale;
            releaseVelocity += throwDir * (ThrowBaseSpeed + dragSpeed);
        }

        if (_world.MaxVelocity > 0f)
        {
            float cap = _world.MaxVelocity * 0.85f;
            float speed = releaseVelocity.Length();
            if (speed > cap)
                releaseVelocity = releaseVelocity / speed * cap;
        }

        _world.SetLinearVelocity(_heldBody, releaseVelocity);
        _heldBody = PhysicsBody.None;
        _dragLookAccum = Vector2.Zero;
        _handVelocity = Vector3.Zero;
        _lastHandAnchor = Vector3.Zero;
    }

    /// <summary>Maximum per-frame ground-snap correction (well above the natural range produced by GroundProbeDistance).</summary>
    private const float MaxGroundCorrectionPerFrame = 0.2f;
    private const float MainFloorTopY = 0f;
    private const float HeldGroundMargin = 0.05f;

    private float GetMinHeldCenterY(Vector3 referencePoint)
    {
        _world.TryGetGroundSurfaceY(referencePoint, _heldBody, out float surfaceY, MainFloorTopY);
        return surfaceY + _heldBody.HalfExtents.Y + HeldGroundMargin;
    }

    private void CorrectHeldBodyGroundPenetration()
    {
        if (!_heldBody.IsValid)
            return;

        _world.GetPose(_heldBody, out Vector3 pos, out _);
        float minCenterY = GetMinHeldCenterY(pos);
        if (pos.Y >= minCenterY)
            return;

        _world.AdjustPositionY(_heldBody, minCenterY - pos.Y);

        Vector3 velocity = _world.GetLinearVelocity(_heldBody);
        if (velocity.Y < 0f)
        {
            velocity.Y = 0f;
            _world.SetLinearVelocity(_heldBody, velocity);
        }
    }

    private void UpdatePlayer(in PhysicsInteractionInput input, float dt)
    {
        bool inWater = EvaluateSwimState();
        IsInWater = inWater;

        float groundDistance = 0f;
        bool grounded = !inWater && _world.IsGrounded(PlayerBody, extraDistance: 0.4f, out groundDistance);
        Vector3 velocity = _world.GetLinearVelocity(PlayerBody);

        Vector3 wish = Vector3.Zero;
        if (input.MoveForward) wish += input.CameraForward;
        if (input.MoveBack) wish -= input.CameraForward;
        if (input.MoveRight) wish += input.CameraRight;
        if (input.MoveLeft) wish -= input.CameraRight;
        wish.Y = 0f;
        if (wish != Vector3.Zero)
            wish = Vector3.Normalize(wish);

        float speed = (inWater ? SwimMoveSpeed : MoveSpeed) * (input.Sprint ? SprintMultiplier : 1f);
        var wishHorizontal = new Vector2(wish.X, wish.Z);
        var currentHorizontal = new Vector2(velocity.X, velocity.Z);
        float currentSpeed = currentHorizontal.Length();

        Vector2 nextHorizontal;
        if (inWater)
        {
            if (wishHorizontal.LengthSquared() > 1e-6f)
            {
                Vector2 wishDir = Vector2.Normalize(wishHorizontal);
                float targetSpeed = speed;
                float nextSpeed = Approach(currentSpeed, targetSpeed, SwimAcceleration * dt);
                nextHorizontal = wishDir * nextSpeed;
            }
            else
            {
                float decel = SwimAcceleration * 0.65f;
                float nextSpeed = Approach(currentSpeed, 0f, decel * dt);
                nextHorizontal = currentSpeed > 1e-6f
                    ? currentHorizontal / currentSpeed * nextSpeed
                    : Vector2.Zero;
            }

            if (input.Jump)
                velocity.Y = SwimUpSpeed;
            else if (velocity.Y > 0.05f)
                velocity.Y *= MathF.Max(0f, 1f - dt * 2.5f);
        }
        else if (grounded && wishHorizontal.LengthSquared() > 1e-6f)
        {
            Vector2 wishDir = Vector2.Normalize(wishHorizontal);
            float accel = GroundAcceleration;
            float nextSpeed = Approach(currentSpeed, speed, accel * dt);
            nextHorizontal = wishDir * nextSpeed;
        }
        else if (grounded)
        {
            float decel = GroundDeceleration;
            float nextSpeed = Approach(currentSpeed, 0f, decel * dt);
            if (currentSpeed > 1e-6f)
                nextHorizontal = currentHorizontal / currentSpeed * nextSpeed;
            else
                nextHorizontal = Vector2.Zero;
        }
        else
        {
            // Airborne movement: preserve momentum, allow only slight steering (especially weak
            // backwards and sideways). This prevents instant "cancel and reverse" mid-jump.
            nextHorizontal = currentHorizontal;
            if (wishHorizontal.LengthSquared() > 1e-6f)
            {
                Vector2 wishDir = Vector2.Normalize(wishHorizontal);

                Vector2 camForward = new Vector2(input.CameraForward.X, input.CameraForward.Z);
                camForward = camForward.LengthSquared() > 1e-6f ? Vector2.Normalize(camForward) : new Vector2(0f, -1f);
                Vector2 camRight = new Vector2(input.CameraRight.X, input.CameraRight.Z);
                camRight = camRight.LengthSquared() > 1e-6f ? Vector2.Normalize(camRight) : new Vector2(1f, 0f);

                float forwardDot = Vector2.Dot(wishDir, camForward);              // -1..1
                float sideDotAbs = MathF.Abs(Vector2.Dot(wishDir, camRight));     // 0..1

                float control = forwardDot >= 0f
                    ? Lerp(AirControlSide, AirControlForward, forwardDot)
                    : Lerp(AirControlSide, AirControlBack, -forwardDot);
                control *= Lerp(1f, 0.7f, sideDotAbs);
                if (forwardDot < -0.05f && currentSpeed > 0.5f)
                    control *= 0.35f;

                float maxDelta = MathF.Max(0f, AirAcceleration * control * dt);
                Vector2 target = wishDir * speed;
                Vector2 delta = target - nextHorizontal;
                float deltaLen = delta.Length();
                if (deltaLen > maxDelta && deltaLen > 1e-6f)
                    delta = delta / deltaLen * maxDelta;
                nextHorizontal += delta;
            }
            else if (currentSpeed > 1e-4f)
            {
                // Very light air braking only when no input.
                float brake = Math.Clamp(AirDeceleration * dt * 0.04f, 0f, 0.08f);
                nextHorizontal *= 1f - brake;
            }
        }

        bool jumped = false;
        if (grounded && !inWater)
        {
            if (input.Jump)
            {
                velocity.Y = JumpSpeed;
                jumped = true;
            }
            else if (velocity.Y < 0f)
            {
                velocity.Y = 0f;
            }
        }

        HorizontalSpeed = nextHorizontal.Length();
        HorizontalAcceleration = dt > 0f
            ? (HorizontalSpeed - currentHorizontal.Length()) / dt
            : 0f;
        VerticalSpeed = velocity.Y;
        IsGrounded = grounded && !inWater;
        IsSprinting = input.Sprint && wishHorizontal.LengthSquared() > 1e-6f;
        UpdateViewBob(HorizontalSpeed, grounded && !inWater, input.Sprint, dt);

        velocity.X = nextHorizontal.X;
        velocity.Z = nextHorizontal.Y;
        _world.SetLinearVelocity(PlayerBody, velocity);

        // Ground-snap correction: the rigid-body solver alone tends to leave a small amount of
        // resting penetration under continuous gravity loading, which accumulates over time into
        // visible sinking. Actively correct it every grounded frame instead of relying purely on
        // solver convergence (skipped on the jump frame so it doesn't fight the jump impulse).
        if (grounded && !jumped)
        {
            float deltaY = Math.Clamp(
                SandboxPhysicsWorld.GroundProbeOffset - groundDistance,
                -MaxGroundCorrectionPerFrame,
                MaxGroundCorrectionPerFrame);
            if (MathF.Abs(deltaY) > 0.0005f)
                _world.AdjustPositionY(PlayerBody, deltaY);
        }

        _world.GetPose(PlayerBody, out Vector3 playerPos, out _);
        if (playerPos.Y < -40f)
        {
            _world.SetBodyPose(PlayerBody, new Vector3(0f, 15f, 0f), Quaternion.Identity);
            _world.SetLinearVelocity(PlayerBody, Vector3.Zero);
        }
    }

    private bool EvaluateSwimState()
    {
        if (WaterCallbacks == null || !PlayerBody.IsValid)
            return false;

        _world.GetPose(PlayerBody, out Vector3 pos, out _);
        float? surfaceY = WaterCallbacks.GetWaterSurfaceY(pos.X, pos.Z);
        if (surfaceY == null)
            return false;

        float headY = pos.Y + PlayerBody.HalfExtents.Y;
        if (headY > surfaceY.Value + 0.35f)
            return false;

        float feetY = pos.Y - PlayerBody.HalfExtents.Y;
        return feetY < surfaceY.Value - 0.05f;
    }

    private void AddCourseStatic(Vector3 position, Vector3 halfExtents)
    {
        _world.AddStaticBox(position, halfExtents);
        _courseBlocks.Add(new CourseBlock(position, halfExtents));
    }

    private void UpdateViewBob(float horizontalSpeed, bool grounded, bool sprinting, float dt)
    {
        if (!ViewBobEnabled || !grounded)
        {
            _viewBobOffset = Approach(_viewBobOffset, 0f, ViewBobDamping * dt);
            return;
        }

        float sprintTopSpeed = MathF.Max(0.1f, MoveSpeed * MathF.Max(1f, SprintMultiplier));
        float speed01 = Math.Clamp(horizontalSpeed / sprintTopSpeed, 0f, 1f);
        if (horizontalSpeed < 0.08f)
        {
            _viewBobOffset = Approach(_viewBobOffset, 0f, ViewBobDamping * dt);
            return;
        }

        float runBias = sprinting ? 1f : speed01;
        float amplitude = Lerp(ViewBobWalkAmplitude, ViewBobRunAmplitude, runBias);
        float frequency = Lerp(ViewBobWalkFrequency, ViewBobRunFrequency, runBias);
        _viewBobPhase += dt * frequency * MathF.Tau;
        if (_viewBobPhase > MathF.Tau)
            _viewBobPhase -= MathF.Tau;

        float targetOffset = MathF.Sin(_viewBobPhase) * amplitude * Math.Clamp(0.35f + speed01 * 1.15f, 0f, 1.35f);
        _viewBobOffset = Lerp(_viewBobOffset, targetOffset, Math.Clamp(dt * ViewBobDamping, 0f, 1f));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Approach(float value, float target, float maxDelta)
    {
        if (value < target) return MathF.Min(value + maxDelta, target);
        return MathF.Max(value - maxDelta, target);
    }

    public void Dispose() => _world.Dispose();

    private readonly struct CourseBlock
    {
        public readonly Vector3 Position;
        public readonly Vector3 HalfExtents;

        public CourseBlock(Vector3 position, Vector3 halfExtents)
        {
            Position = position;
            HalfExtents = halfExtents;
        }
    }
}

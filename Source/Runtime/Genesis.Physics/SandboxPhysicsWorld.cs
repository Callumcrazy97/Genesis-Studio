using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;

namespace Genesis.Physics;

/// <summary>
/// Standalone Bepu world for sandbox demos and <see cref="PhysicsInteractionSession"/>.
/// The ECS-backed <see cref="PhysicsWorld"/> is used by runtime scenes.
/// </summary>
public sealed class SandboxPhysicsWorld : IDisposable
{
    private readonly Simulation _sim;
    private readonly BufferPool _pool;
    private readonly List<TrackedBody> _dynamicBodies = new();
    private readonly List<TrackedStatic> _staticBodies = new();
    private readonly Dictionary<BodyHandle, int> _dynamicIndices = new();
    private readonly Dictionary<StaticHandle, int> _staticIndices = new();
    private readonly Dictionary<BodyHandle, (byte Layer, uint Mask)> _dynamicCollisionFilter = new();
    private readonly Dictionary<StaticHandle, (byte Layer, uint Mask)> _staticCollisionFilter = new();
    private readonly ThreadDispatcher _threadDispatcher;

    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
    public float MaxVelocity { get; set; } = 45f;
    public bool AllowSleep { get; set; } = true;
    public float SleepThreshold { get; set; } = 0.25f;
    public float FrictionCoefficient { get; set; } = 0.8f;
    public float Restitution { get; set; } = 0.1f;
    public float LinearDamping { get; set; } = 0.02f;
    public float AngularDamping { get; set; } = 0.05f;
    public bool EnableThreadDispatcher { get; set; }
    public int DispatcherThreadCount => _threadDispatcher.ThreadCount;
    public byte DefaultCollisionLayer { get; set; }
    public uint DefaultCollisionMask { get; set; } = 0x7Fu;

    public int DynamicBodyCount => _dynamicBodies.Count;
    public int StaticBodyCount => _staticBodies.Count;

    public SandboxPhysicsWorld(bool enableThreadDispatcher = true, bool allowSleep = true, int? dispatcherThreadCount = null)
    {
        EnableThreadDispatcher = enableThreadDispatcher;
        AllowSleep = allowSleep;
        _pool = new BufferPool();
        _threadDispatcher = new ThreadDispatcher(Math.Max(1, dispatcherThreadCount ?? Environment.ProcessorCount), 1 << 18);
        _sim = Simulation.Create(
            _pool,
            new SandboxNarrowPhaseCallbacks(this),
            new SandboxPoseCallbacks(this),
            new SolveDescription(8, 1));
    }

    public PhysicsBody AddDynamicBox(Vector3 position, Vector3 halfExtents, float mass = 1f)
    {
        var shape = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
        var shapeIndex = _sim.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(mass);

        var handle = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            new CollidableDescription(shapeIndex, 0.1f),
            CreateActivityDescription()));

        int slot = _dynamicBodies.Count;
        _dynamicBodies.Add(new TrackedBody(handle, halfExtents, mass));
        _dynamicIndices[handle] = slot;
        _dynamicCollisionFilter[handle] = (DefaultCollisionLayer, DefaultCollisionMask);
        return new PhysicsBody(handle, halfExtents, slot);
    }

    public PhysicsBody AddDynamicSphere(Vector3 position, float radius, float mass = 1f)
    {
        var shape = new Sphere(radius);
        var shapeIndex = _sim.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(mass);
        var halfExtents = new Vector3(radius, radius, radius);

        var handle = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            new CollidableDescription(shapeIndex, 0.1f),
            CreateActivityDescription()));

        int slot = _dynamicBodies.Count;
        _dynamicBodies.Add(new TrackedBody(handle, halfExtents, mass));
        _dynamicIndices[handle] = slot;
        _dynamicCollisionFilter[handle] = (DefaultCollisionLayer, DefaultCollisionMask);
        return new PhysicsBody(handle, halfExtents, slot);
    }

    public PhysicsBody AddDynamicCapsule(Vector3 position, float radius, float halfHeight, float mass = 1f)
    {
        var shape = CreateCapsule(radius, halfHeight);
        var shapeIndex = _sim.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(mass);
        var halfExtents = new Vector3(radius, halfHeight, radius);

        var handle = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            new CollidableDescription(shapeIndex, 0.1f),
            CreateActivityDescription()));

        int slot = _dynamicBodies.Count;
        _dynamicBodies.Add(new TrackedBody(handle, halfExtents, mass));
        _dynamicIndices[handle] = slot;
        _dynamicCollisionFilter[handle] = (DefaultCollisionLayer, DefaultCollisionMask);
        return new PhysicsBody(handle, halfExtents, slot);
    }

    public PhysicsBody AddCharacterCapsule(Vector3 position, float radius = 0.35f, float halfHeight = 0.9f, float mass = 75f)
    {
        var shape = CreateCapsule(radius, halfHeight);
        var shapeIndex = _sim.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(mass);

        var handle = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            new CollidableDescription(shapeIndex, 0.1f),
            CreateActivityDescription()));

        var halfExtents = new Vector3(radius, halfHeight, radius);
        int slot = _dynamicBodies.Count;
        _dynamicBodies.Add(new TrackedBody(handle, halfExtents, mass));
        _dynamicIndices[handle] = slot;
        _dynamicCollisionFilter[handle] = (DefaultCollisionLayer, DefaultCollisionMask);
        return new PhysicsBody(handle, halfExtents, slot);
    }

    private BodyActivityDescription CreateActivityDescription() =>
        new BodyActivityDescription(
            AllowSleep ? MathF.Max(SleepThreshold, 1e-5f) : -1f,
            32);

    public PhysicsBody AddStaticBox(Vector3 position, Vector3 halfExtents)
    {
        var shape = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
        var shapeIndex = _sim.Shapes.Add(shape);
        var handle = _sim.Statics.Add(new StaticDescription(position, shapeIndex));

        int slot = _staticBodies.Count;
        _staticBodies.Add(new TrackedStatic(handle, halfExtents));
        _staticIndices[handle] = slot;
        _staticCollisionFilter[handle] = (0, 0x7Fu);
        return new PhysicsBody(default, halfExtents, slot, IsStatic: true);
    }

    /// <summary>Adds real authored collision geometry to the editor sandbox.</summary>
    public PhysicsBody AddStaticTriangleMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count < 3 || indices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must be a non-empty multiple of three.", nameof(indices));

        _pool.Take<Triangle>(indices.Count / 3, out Buffer<Triangle> triangles);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < vertices.Count; i++)
        {
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }
        for (int i = 0; i < triangles.Length; i++)
        {
            int offset = i * 3;
            triangles[i] = new Triangle(vertices[indices[offset]], vertices[indices[offset + 1]], vertices[indices[offset + 2]]);
        }
        Mesh mesh = new(triangles, Vector3.One, _pool);
        TypedIndex shapeIndex = _sim.Shapes.Add(mesh);
        StaticHandle handle = _sim.Statics.Add(new StaticDescription(Vector3.Zero, shapeIndex));
        Vector3 halfExtents = Vector3.Max((max - min) * 0.5f, new Vector3(0.01f));
        int slot = _staticBodies.Count;
        _staticBodies.Add(new TrackedStatic(handle, halfExtents));
        _staticIndices[handle] = slot;
        _staticCollisionFilter[handle] = (0, 0x7Fu);
        return new PhysicsBody(default, halfExtents, slot, IsStatic: true);
    }

    private float _timeAccumulator;
    private const float FixedTimeStep = 1f / 60f;

    internal bool ShouldCollide(CollidableReference a, CollidableReference b)
    {
        (byte Layer, uint Mask) filterA = GetCollisionFilter(a);
        (byte Layer, uint Mask) filterB = GetCollisionFilter(b);
        return (filterA.Mask & (1u << filterB.Layer)) != 0 &&
               (filterB.Mask & (1u << filterA.Layer)) != 0;
    }

    private (byte Layer, uint Mask) GetCollisionFilter(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic &&
            _dynamicCollisionFilter.TryGetValue(collidable.BodyHandle, out var dynamicFilter)) return dynamicFilter;
        if (collidable.Mobility != CollidableMobility.Dynamic &&
            _staticCollisionFilter.TryGetValue(collidable.StaticHandle, out var staticFilter)) return staticFilter;
        return (0, 0x7Fu);
    }

    public void Step(float dt)
    {
        if (dt <= 0f) return;

        // Clamp dt to avoid spiral of death
        if (dt > 0.1f) dt = 0.1f;

        _timeAccumulator += dt;
        while (_timeAccumulator >= FixedTimeStep)
        {
            if (EnableThreadDispatcher)
                _sim.Timestep(FixedTimeStep, _threadDispatcher);
            else
                _sim.Timestep(FixedTimeStep);

            _timeAccumulator -= FixedTimeStep;
        }
    }

    public void GetPose(PhysicsBody body, out Vector3 position, out Quaternion rotation)
    {
        if (body.IsStatic)
        {
            GetStaticPose(_staticBodies[body.SlotIndex].Handle, out position, out rotation);
            return;
        }

        GetDynamicPose(body.Handle, out position, out rotation);
    }

    private void GetDynamicPose(BodyHandle handle, out Vector3 position, out Quaternion rotation)
    {
        BodyReference bodyRef = _sim.Bodies.GetBodyReference(handle);
        RigidPose pose = bodyRef.Pose;
        position = pose.Position;
        var q = pose.Orientation;
        rotation = new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    private void GetStaticPose(StaticHandle handle, out Vector3 position, out Quaternion rotation)
    {
        StaticReference staticRef = _sim.Statics.GetStaticReference(handle);
        RigidPose pose = staticRef.Pose;
        position = pose.Position;
        var q = pose.Orientation;
        rotation = new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    public void SetLinearVelocity(PhysicsBody body, Vector3 velocity)
    {
        BodyReference bodyRef = _sim.Bodies.GetBodyReference(body.Handle);
        bodyRef.Awake = true;
        bodyRef.Velocity.Linear = velocity;
    }

    public Vector3 GetLinearVelocity(PhysicsBody body)
    {
        return _sim.Bodies.GetBodyReference(body.Handle).Velocity.Linear;
    }

    /// <summary>Returns a <see cref="PhysicsBody"/> by its dynamic slot index (0-based).
    /// Used by <c>WaterPhysicsIntegration</c> to enumerate all dynamic bodies.</summary>
    public PhysicsBody GetDynamicBody(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _dynamicBodies.Count) return PhysicsBody.None;
        var tracked = _dynamicBodies[slotIndex];
        return new PhysicsBody(tracked.Handle, tracked.HalfExtents, slotIndex);
    }

    /// <summary>Returns the mass of a dynamic body in kg.</summary>
    public float GetBodyMass(PhysicsBody body)
    {
        if (body.IsStatic || !body.IsValid) return 0f;
        if (body.SlotIndex < 0 || body.SlotIndex >= _dynamicBodies.Count) return 1f;
        return _dynamicBodies[body.SlotIndex].Mass;
    }

    /// <summary>Scales the angular velocity of a dynamic body by <paramref name="factor"/> (0–1 applies drag).</summary>
    public void ScaleAngularVelocity(PhysicsBody body, float factor)
    {
        if (body.IsStatic || !body.IsValid) return;
        BodyReference bodyRef = _sim.Bodies.GetBodyReference(body.Handle);
        bodyRef.Velocity.Angular *= factor;
    }


    /// <summary>Sets yaw-only facing without disturbing linear velocity (third-person avatar).</summary>
    public void SetBodyYaw(PhysicsBody body, Quaternion rotation)
    {
        if (body.IsStatic || !body.IsValid)
            return;

        BodyReference bodyRef = _sim.Bodies.GetBodyReference(body.Handle);
        bodyRef.Awake = true;
        bodyRef.Pose.Orientation = new System.Numerics.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        bodyRef.Velocity.Angular = default;
    }

    public void SetBodyPose(PhysicsBody body, Vector3 position, Quaternion rotation)
    {
        BodyReference bodyRef = _sim.Bodies.GetBodyReference(body.Handle);
        bodyRef.Awake = true;
        bodyRef.Pose.Position = position;
        bodyRef.Pose.Orientation = new System.Numerics.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        bodyRef.Velocity = default;
    }

    /// <summary>Removes a dynamic body from the simulation. Do not use on the player body.</summary>
    public void RemoveDynamicBody(PhysicsBody body)
    {
        if (!body.IsValid || body.IsStatic)
            return;

        _sim.Bodies.Remove(body.Handle);

        if (!_dynamicIndices.TryGetValue(body.Handle, out int slot))
            return;

        int last = _dynamicBodies.Count - 1;
        if (slot < last)
        {
            TrackedBody swapped = _dynamicBodies[last];
            _dynamicBodies[slot] = swapped;
            _dynamicIndices[swapped.Handle] = slot;
        }

        _dynamicBodies.RemoveAt(last);
        _dynamicIndices.Remove(body.Handle);
        _dynamicCollisionFilter.Remove(body.Handle);
    }

    /// <summary>Ground probe distance baked into <see cref="IsGrounded"/>'s downward ray origin offset.</summary>
    public const float GroundProbeOffset = 0.05f;

    public bool IsGrounded(PhysicsBody body, float extraDistance = 0.15f) => IsGrounded(body, extraDistance, out _);

    /// <summary>
    /// As <see cref="IsGrounded(PhysicsBody, float)"/>, but also reports the raycast distance to the
    /// surface beneath the body. Used by character controllers to actively correct resting penetration
    /// instead of relying solely on the rigid-body solver to converge.
    /// </summary>
    public bool IsGrounded(PhysicsBody body, float extraDistance, out float groundDistance)
    {
        GetPose(body, out Vector3 pos, out _);
        Vector3 origin = pos - new Vector3(0f, body.HalfExtents.Y - GroundProbeOffset, 0f);
        return RaycastCore(origin, -Vector3.UnitY, extraDistance, body, out _, out groundDistance);
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out PhysicsBody hitBody, PhysicsBody ignore = default) =>
        RaycastCore(origin, direction, maxDistance, ignore, out hitBody, out _);

    /// <summary>As <see cref="Raycast(Vector3,Vector3,float,out PhysicsBody,PhysicsBody)"/>, but also
    /// reports the hit distance — used by camera push-out to clamp the eye to the surface (Issue 2).</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out PhysicsBody hitBody, out float hitDistance, PhysicsBody ignore = default) =>
        RaycastCore(origin, direction, maxDistance, ignore, out hitBody, out hitDistance);

    /// <summary>
    /// Finds the highest static/dynamic surface below <paramref name="referencePoint"/> via downward raycast.
    /// Falls back to <paramref name="fallbackSurfaceY"/> when nothing is hit.
    /// </summary>
    public bool TryGetGroundSurfaceY(Vector3 referencePoint, PhysicsBody ignore, out float surfaceY, float fallbackSurfaceY = 0f)
    {
        Vector3 origin = referencePoint + Vector3.UnitY * 32f;
        const float probeDistance = 64f;
        if (RaycastCore(origin, -Vector3.UnitY, probeDistance, ignore, out _, out float hitDistance))
        {
            surfaceY = origin.Y - hitDistance;
            return true;
        }

        surfaceY = fallbackSurfaceY;
        return false;
    }

    private bool RaycastCore(Vector3 origin, Vector3 direction, float maxDistance, PhysicsBody ignore,
        out PhysicsBody hitBody, out float hitDistance)
    {
        hitBody = PhysicsBody.None;
        hitDistance = maxDistance;
        if (maxDistance <= 0f)
            return false;
        if (direction.LengthSquared() < 1e-8f)
            return false;

        direction = Vector3.Normalize(direction);

        var handler = new SandboxRayHitHandler(this, ignore);
        _sim.RayCast(origin, direction, maxDistance, ref handler, id: 0);
        if (!handler.Found)
            return false;

        hitBody = handler.HitBody;
        hitDistance = handler.HitDistance;
        return true;
    }

    /// <summary>Adjusts only the Y position of a dynamic body, leaving velocity and X/Z untouched.</summary>
    /// <remarks>Used for ground-snap correction; never call this on a body with <see cref="PhysicsBody.IsStatic"/>.</remarks>
    public void AdjustPositionY(PhysicsBody body, float deltaY)
    {
        if (body.IsStatic || MathF.Abs(deltaY) < 1e-5f) return;

        BodyReference bodyRef = _sim.Bodies.GetBodyReference(body.Handle);
        RigidPose pose = bodyRef.Pose;
        pose.Position.Y += deltaY;
        bodyRef.Pose = pose;
        bodyRef.Awake = true;
    }

    public enum PhysicsStressLayout
    {
        AtRest = 0,
        Pile = 1,
    }

    public readonly struct PhysicsStressResult
    {
        public readonly int DynamicBodyCount;
        public readonly PhysicsStressLayout Layout;
        public readonly bool DispatcherEnabled;
        public readonly bool SleepingEnabled;
        public readonly float AverageStepMs;

        public PhysicsStressResult(int dynamicBodyCount, PhysicsStressLayout layout,
            bool dispatcherEnabled, bool sleepingEnabled, float averageStepMs)
        {
            DynamicBodyCount = dynamicBodyCount;
            Layout = layout;
            DispatcherEnabled = dispatcherEnabled;
            SleepingEnabled = sleepingEnabled;
            AverageStepMs = averageStepMs;
        }

        public override string ToString() =>
            $"{Layout} bodies={DynamicBodyCount}, dispatcher={(DispatcherEnabled ? "on" : "off")}, sleep={(SleepingEnabled ? "on" : "off")} -> {AverageStepMs:F3} ms";
    }

    /// <summary>
    /// Runs the Phase 4 physics stress matrix (500 and 5k dynamic bodies, at rest and piled)
    /// and returns average step times in milliseconds for dispatcher/sleeping on/off.
    /// </summary>
    public static IReadOnlyList<PhysicsStressResult> RunStressSuite(
        int warmupSteps = 120, int measuredSteps = 240, float dt = 1f / 60f)
    {
        var results = new List<PhysicsStressResult>(16);
        int[] counts = { 500, 5000 };
        PhysicsStressLayout[] layouts = { PhysicsStressLayout.AtRest, PhysicsStressLayout.Pile };

        foreach (int count in counts)
        {
            foreach (var layout in layouts)
            {
                foreach (bool dispatcherEnabled in new[] { false, true })
                {
                    foreach (bool sleepingEnabled in new[] { false, true })
                    {
                        using var world = BuildStressScene(count, layout, dispatcherEnabled, sleepingEnabled);

                        for (int i = 0; i < warmupSteps; i++)
                            world.Step(dt);

                        var stopwatch = Stopwatch.StartNew();
                        for (int i = 0; i < measuredSteps; i++)
                            world.Step(dt);
                        stopwatch.Stop();

                        float avgMs = (float)(stopwatch.Elapsed.TotalMilliseconds / measuredSteps);
                        results.Add(new PhysicsStressResult(count, layout, dispatcherEnabled, sleepingEnabled, avgMs));
                    }
                }
            }
        }

        return results;
    }

    private static SandboxPhysicsWorld BuildStressScene(int dynamicBodyCount, PhysicsStressLayout layout,
        bool dispatcherEnabled, bool sleepingEnabled)
    {
        var world = new SandboxPhysicsWorld(
            enableThreadDispatcher: dispatcherEnabled,
            allowSleep: sleepingEnabled);

        world.AddStaticBox(new Vector3(0f, -0.5f, 0f), new Vector3(250f, 0.5f, 250f));

        const float half = 0.45f;
        if (layout == PhysicsStressLayout.AtRest)
        {
            int side = (int)MathF.Ceiling(MathF.Sqrt(dynamicBodyCount));
            float spacing = half * 2.8f;
            float y = half + 0.02f;
            for (int i = 0; i < dynamicBodyCount; i++)
            {
                int x = i % side;
                int z = i / side;
                var position = new Vector3(
                    (x - side * 0.5f) * spacing,
                    y,
                    (z - side * 0.5f) * spacing);
                world.AddDynamicBox(position, new Vector3(half, half, half), 1f);
            }
        }
        else
        {
            int width = (int)MathF.Ceiling(MathF.Pow(dynamicBodyCount, 1f / 3f));
            float spacing = half * 2.05f;
            for (int i = 0; i < dynamicBodyCount; i++)
            {
                int layer = i / (width * width);
                int idxInLayer = i % (width * width);
                int x = idxInLayer % width;
                int z = idxInLayer / width;

                float jitterX = (((i * 17) % 11) - 5) * 0.005f;
                float jitterZ = (((i * 31) % 13) - 6) * 0.005f;
                var position = new Vector3(
                    (x - width * 0.5f) * spacing + jitterX,
                    half + 0.02f + layer * spacing,
                    (z - width * 0.5f) * spacing + jitterZ);
                world.AddDynamicBox(position, new Vector3(half, half, half), 1f);
            }
        }

        return world;
    }

    private static Capsule CreateCapsule(float radius, float halfHeight)
    {
        float length = MathF.Max(0.01f, halfHeight * 2f - radius * 2f);
        return new Capsule(radius, length);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sim.Dispose();
        _threadDispatcher.Dispose();
        _dynamicIndices.Clear();
        _staticIndices.Clear();
        _dynamicCollisionFilter.Clear();
        _staticCollisionFilter.Clear();
        _pool.Clear();
    }

    /// <summary>Physics simulation used by terrain colliders and advanced sandbox features.</summary>
    public Simulation PhysicsSimulation => _sim;

    /// <summary>Shared buffer pool for mesh colliders and other Bepu allocations.</summary>
    public BufferPool PhysicsBufferPool => _pool;

    private readonly struct TrackedBody
    {
        public readonly BodyHandle Handle;
        public readonly Vector3 HalfExtents;
        public readonly float Mass;

        public TrackedBody(BodyHandle handle, Vector3 halfExtents, float mass)
        {
            Handle = handle;
            HalfExtents = halfExtents;
            Mass = mass;
        }
    }

    private readonly struct TrackedStatic
    {
        public readonly StaticHandle Handle;
        public readonly Vector3 HalfExtents;

        public TrackedStatic(StaticHandle handle, Vector3 halfExtents)
        {
            Handle = handle;
            HalfExtents = halfExtents;
        }
    }

    private struct SandboxRayHitHandler : IRayHitHandler
    {
        private readonly SandboxPhysicsWorld _world;
        private readonly PhysicsBody _ignore;

        public PhysicsBody HitBody;
        public float HitDistance;
        public bool Found;

        public SandboxRayHitHandler(SandboxPhysicsWorld world, PhysicsBody ignore)
        {
            _world = world;
            _ignore = ignore;
            HitBody = PhysicsBody.None;
            HitDistance = 0f;
            Found = false;
        }

        public bool AllowTest(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Dynamic)
            {
                if (_ignore.IsValid && !_ignore.IsStatic && collidable.BodyHandle.Value == _ignore.Handle.Value)
                    return false;
                return _world._dynamicIndices.ContainsKey(collidable.BodyHandle);
            }

            // Allow terrain mesh statics (not tracked in _staticIndices) as well as course blocks.
            if (_ignore.IsValid && _ignore.IsStatic
                && _world._staticIndices.TryGetValue(collidable.StaticHandle, out int ignoredSlot)
                && _ignore.SlotIndex == ignoredSlot)
                return false;

            return collidable.Mobility == CollidableMobility.Static;
        }

        public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            Found = true;
            HitDistance = t;
            maximumT = t;

            if (collidable.Mobility == CollidableMobility.Dynamic)
            {
                int slot = _world._dynamicIndices[collidable.BodyHandle];
                Vector3 half = _world._dynamicBodies[slot].HalfExtents;
                HitBody = new PhysicsBody(collidable.BodyHandle, half, slot);
            }
            else
            {
                if (_world._staticIndices.TryGetValue(collidable.StaticHandle, out int slot))
                {
                    Vector3 half = _world._staticBodies[slot].HalfExtents;
                    HitBody = new PhysicsBody(default, half, slot, IsStatic: true);
                }
                else
                {
                    // Terrain triangle meshes and other untracked statics.
                    HitBody = new PhysicsBody(default, new Vector3(8f, 4f, 8f), -1, IsStatic: true);
                }
            }
        }
    }
}

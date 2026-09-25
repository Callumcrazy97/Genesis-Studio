using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Genesis.Runtime.Particles;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Net;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Particles, physics and networking — the three system families a game needs beyond drawing and
// logic.
//
// Each has a real runtime behind it, reached differently:
//   • Particles — ParticleSimulation instances created and owned here, keyed by handle. There is no
//     scene-level particle registry to borrow, so script gets its own emitters.
//   • Physics   — ActiveGameContext.Scene.Physics (a Bepu-backed PhysicsWorld). Scene-wide settings
//     are real; per-body manipulation needs a body registry PGSL cannot reach yet and is marked
//     unimplemented rather than faked.
//   • Networking — ActiveNetwork, an explicit hook the host assigns. IGameNetwork is not on
//     IGameContext, so rather than widen that interface for one family this keeps the seam small.
public static partial class PgslCommands
{
    #region Particles

    private const int MaxParticleEmitters = 64;

    // Emitters are owned by script and keyed by handle. Held statically rather than in the instance
    // context because an emitter usually outlives the instance that lit it (an explosion should not
    // vanish because the exploding thing did).
    private static readonly Dictionary<int, ParticleSimulation> _emitters = [];
    private static readonly Dictionary<int, ParticleConfig> _emitterConfigs = [];
    private static int _nextEmitter = 1;

    /// <summary>Live emitters, for hosts that want to update and draw them.</summary>
    public static IReadOnlyDictionary<int, ParticleSimulation> ParticleEmitters => _emitters;

    [PgslCommand("ParticleCreate", "ParticleCreate(maxParticles, emitRate, life) -> id", "Create a particle emitter", "Particles")]
    public static double ParticleCreate(double maxParticles, double emitRate, double life)
    {
        if (_emitters.Count >= MaxParticleEmitters) return 0;

        ParticleConfig config = new()
        {
            MaxParticles = (int)Math.Clamp(maxParticles, 1, 20_000),
            EmitRate = Math.Clamp(emitRate, 0, 10_000),
        };

        ParticleSimulation simulation = new();
        simulation.LoadConfig(config);

        int handle = _nextEmitter++;
        _emitters[handle] = simulation;
        _emitterConfigs[handle] = config;
        _ = life;   // lifetime lives on the config's own life fields; see ParticleSetLife
        return handle;
    }

    [PgslCommand("ParticleDestroy", "ParticleDestroy(id)", "Release an emitter", "Particles")]
    public static void ParticleDestroy(double id)
    {
        int handle = (int)id;
        _emitters.Remove(handle);
        _emitterConfigs.Remove(handle);
    }

    [PgslCommand("ParticleDestroyAll", "ParticleDestroyAll()", "Release every emitter", "Particles")]
    public static void ParticleDestroyAll()
    {
        _emitters.Clear();
        _emitterConfigs.Clear();
    }

    [PgslCommand("ParticleBurst", "ParticleBurst(id)", "Emit a one-off burst", "Particles")]
    public static void ParticleBurst(double id)
    {
        if (_emitters.TryGetValue((int)id, out ParticleSimulation simulation)) simulation.Burst();
    }

    [PgslCommand("ParticleReset", "ParticleReset(id)", "Kill every live particle", "Particles")]
    public static void ParticleReset(double id)
    {
        if (_emitters.TryGetValue((int)id, out ParticleSimulation simulation)) simulation.Reset();
    }

    [PgslCommand("ParticleUpdate", "ParticleUpdate(id, deltaSeconds)", "Advance an emitter's simulation", "Particles")]
    public static void ParticleUpdate(double id, double deltaSeconds)
    {
        if (!_emitters.TryGetValue((int)id, out ParticleSimulation simulation)) return;

        // Clamp the step: a script passing a frame time from a stalled frame would otherwise teleport
        // every particle and look like a glitch.
        simulation.Step((float)Math.Clamp(deltaSeconds, 0, 0.25));
    }

    [PgslCommand("ParticleCount", "ParticleCount(id) -> number", "Live particles in an emitter", "Particles")]
    public static double ParticleCount(double id) =>
        _emitters.TryGetValue((int)id, out ParticleSimulation simulation) ? simulation.ActiveCount : 0;

    [PgslCommand("ParticleCapacity", "ParticleCapacity(id) -> number", "Maximum particles an emitter can hold", "Particles")]
    public static double ParticleCapacity(double id) =>
        _emitters.TryGetValue((int)id, out ParticleSimulation simulation) ? simulation.Capacity : 0;

    [PgslCommand("ParticleEmitterCount", "ParticleEmitterCount() -> number", "How many emitters exist", "Particles")]
    public static double ParticleEmitterCount() => _emitters.Count;

    [PgslCommand("ParticleAttachedSetEmitting", "ParticleAttachedSetEmitting(enabled)", "Start or stop this object's attached particle component", "Particles")]
    public static void ParticleAttachedSetEmitting(bool enabled)
    {
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        int instanceId = GetContext()?.InstanceId ?? 0;
        if (world == null || instanceId <= 0) return;
        Entity entity = world.GetEntity(instanceId);
        if (entity.IsNull || !world.Has<ParticleComponent>(entity)) return;
        ref ParticleComponent component = ref world.GetRef<ParticleComponent>(entity);
        component.Emitting = enabled;
    }

    [PgslCommand("ParticleAttachedGetEmitting", "ParticleAttachedGetEmitting() -> bool", "Whether this object's attached particles are emitting", "Particles")]
    public static bool ParticleAttachedGetEmitting()
    {
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        int instanceId = GetContext()?.InstanceId ?? 0;
        if (world == null || instanceId <= 0) return false;
        Entity entity = world.GetEntity(instanceId);
        return !entity.IsNull && world.Has<ParticleComponent>(entity)
            && world.GetRef<ParticleComponent>(entity).Emitting;
    }

    [PgslCommand("ParticleAttachedSetRate", "ParticleAttachedSetRate(perSecond)", "Set this object's attached particle rate", "Particles")]
    public static void ParticleAttachedSetRate(double perSecond)
    {
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        int instanceId = GetContext()?.InstanceId ?? 0;
        if (world == null || instanceId <= 0) return;
        Entity entity = world.GetEntity(instanceId);
        if (entity.IsNull || !world.Has<ParticleComponent>(entity)) return;
        ref ParticleComponent component = ref world.GetRef<ParticleComponent>(entity);
        component.EmitRate = (float)Math.Clamp(perSecond, 0, 10_000);
    }

    [PgslCommand("ParticleAttachedGetRate", "ParticleAttachedGetRate() -> number", "This object's attached particle rate", "Particles")]
    public static double ParticleAttachedGetRate()
    {
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        int instanceId = GetContext()?.InstanceId ?? 0;
        if (world == null || instanceId <= 0) return 0;
        Entity entity = world.GetEntity(instanceId);
        return !entity.IsNull && world.Has<ParticleComponent>(entity)
            ? world.GetRef<ParticleComponent>(entity).EmitRate
            : 0;
    }

    [PgslCommand("ParticleExists", "ParticleExists(id) -> bool", "True while an emitter is alive", "Particles")]
    public static bool ParticleExists(double id) => _emitters.ContainsKey((int)id);

    [PgslCommand("ParticleSetRate", "ParticleSetRate(id, perSecond)", "Particles emitted per second", "Particles")]
    public static void ParticleSetRate(double id, double perSecond)
    {
        if (!_emitterConfigs.TryGetValue((int)id, out ParticleConfig config)) return;
        config.EmitRate = Math.Clamp(perSecond, 0, 10_000);
        if (_emitters.TryGetValue((int)id, out ParticleSimulation simulation)) simulation.UpdateConfig(config);
    }

    [PgslCommand("ParticleGetRate", "ParticleGetRate(id) -> number", "Particles emitted per second", "Particles")]
    public static double ParticleGetRate(double id) =>
        _emitterConfigs.TryGetValue((int)id, out ParticleConfig config) ? config.EmitRate : 0;

    [PgslCommand("ParticleSetCameraPosition", "ParticleSetCameraPosition(x, y, z)", "Tell an emitter set where the camera is, for depth sorting", "Particles")]
    public static void ParticleSetCameraPosition(double x, double y, double z)
    {
        Vector3 camera = new((float)x, (float)y, (float)z);
        foreach (ParticleSimulation simulation in _emitters.Values)
        {
            simulation.UpdateCameraPosition(camera);
        }
    }

    #endregion

    #region Physics

    private static Genesis.Physics.PhysicsWorld PhysicsWorld => ActiveGameContext?.Scene?.Physics;

    [PgslCommand("PhysicsAvailable", "PhysicsAvailable() -> bool", "True when a physics world exists", "Physics")]
    public static bool PhysicsAvailable() => PhysicsWorld is not null;

    [PgslCommand("PhysicsSetGravityStrength", "PhysicsSetGravityStrength(strength)", "Scale scene gravity", "Physics")]
    public static void PhysicsSetGravityStrength(double strength)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is not null) world.GravityStrength = (float)strength;
    }

    [PgslCommand("PhysicsGetGravityStrength", "PhysicsGetGravityStrength() -> number", "Scene gravity scale", "Physics")]
    public static double PhysicsGetGravityStrength() => PhysicsWorld?.GravityStrength ?? 0;

    [PgslCommand("PhysicsSetGravityDirection", "PhysicsSetGravityDirection(x, y, z)", "Point scene gravity somewhere else", "Physics")]
    public static void PhysicsSetGravityDirection(double x, double y, double z)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is null) return;

        Vector3 direction = new((float)x, (float)y, (float)z);
        // A zero direction would make gravity meaningless and NaN the normalisation downstream.
        world.GravityDirection = direction.LengthSquared() < 1e-8f
            ? new Vector3(0f, -1f, 0f)
            : Vector3.Normalize(direction);
    }

    [PgslCommand("PhysicsGetGravityX", "PhysicsGetGravityX() -> number", "X of the gravity acceleration", "Physics")]
    public static double PhysicsGetGravityX() => PhysicsWorld?.GetGravityAcceleration().X ?? 0;

    [PgslCommand("PhysicsGetGravityY", "PhysicsGetGravityY() -> number", "Y of the gravity acceleration", "Physics")]
    public static double PhysicsGetGravityY() => PhysicsWorld?.GetGravityAcceleration().Y ?? 0;

    [PgslCommand("PhysicsGetGravityZ", "PhysicsGetGravityZ() -> number", "Z of the gravity acceleration", "Physics")]
    public static double PhysicsGetGravityZ() => PhysicsWorld?.GetGravityAcceleration().Z ?? 0;

    [PgslCommand("PhysicsSetAirDrag", "PhysicsSetAirDrag(drag)", "Global air resistance", "Physics")]
    public static void PhysicsSetAirDrag(double drag)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is not null) world.AirDrag = (float)Math.Clamp(drag, 0, 10);
    }

    [PgslCommand("PhysicsGetAirDrag", "PhysicsGetAirDrag() -> number", "Global air resistance", "Physics")]
    public static double PhysicsGetAirDrag() => PhysicsWorld?.AirDrag ?? 0;

    [PgslCommand("PhysicsSetMaxVelocity", "PhysicsSetMaxVelocity(speed)", "Velocity ceiling for every body", "Physics")]
    public static void PhysicsSetMaxVelocity(double speed)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is not null) world.MaxVelocity = (float)Math.Max(0.01, speed);
    }

    [PgslCommand("PhysicsGetMaxVelocity", "PhysicsGetMaxVelocity() -> number", "Velocity ceiling", "Physics")]
    public static double PhysicsGetMaxVelocity() => PhysicsWorld?.MaxVelocity ?? 0;

    [PgslCommand("PhysicsSetAllowSleep", "PhysicsSetAllowSleep(allow)", "Whether idle bodies may sleep", "Physics")]
    public static void PhysicsSetAllowSleep(bool allow)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is not null) world.AllowSleep = allow;
    }

    [PgslCommand("PhysicsGetAllowSleep", "PhysicsGetAllowSleep() -> bool", "Whether idle bodies may sleep", "Physics")]
    public static bool PhysicsGetAllowSleep() => PhysicsWorld?.AllowSleep ?? false;

    [PgslCommand("PhysicsSetSleepThreshold", "PhysicsSetSleepThreshold(threshold)", "Speed below which a body may sleep", "Physics")]
    public static void PhysicsSetSleepThreshold(double threshold)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is not null) world.SleepThreshold = (float)Math.Max(0, threshold);
    }

    [PgslCommand("PhysicsSetRecoveryVelocity", "PhysicsSetRecoveryVelocity(velocity)", "How hard overlaps are pushed apart", "Physics")]
    public static void PhysicsSetRecoveryVelocity(double velocity)
    {
        Genesis.Physics.PhysicsWorld world = PhysicsWorld;
        if (world is not null) world.RecoveryVelocity = (float)Math.Max(0, velocity);
    }

    [PgslCommand("PhysicsSolverIterations", "PhysicsSolverIterations() -> number", "Solver iterations per step", "Physics")]
    public static double PhysicsSolverIterations() => PhysicsWorld?.SolverIterations ?? 0;

    [PgslCommand("PhysicsApplyImpulse", "PhysicsApplyImpulse(instanceId, x, y, z)", "Push one body", "Physics")]
    public static void PhysicsApplyImpulse(double instanceId, double x, double y, double z)
    {
        Genesis.Physics.PhysicsWorld physics = PhysicsWorld;
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        if (physics is null || world is null) return;

        Entity entity = world.GetEntity((int)instanceId);
        if (entity.IsNull || !world.Has<RigidBodyComponent>(entity)) return;
        ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(entity);
        if (body.RegistrationId == 0 || body.Motion != PhysicsMotionType.Dynamic) return;

        physics.ApplyLinearImpulse(
            world,
            body.RegistrationId,
            new Vector3((float)x, (float)y, (float)z));
    }

    [PgslCommand("PhysicsApplyForce", "PhysicsApplyForce(instanceId, x, y, z)", "Apply a force for the current frame", "Physics")]
    public static void PhysicsApplyForce(double instanceId, double x, double y, double z)
    {
        if (!TryGetDynamicBody((int)instanceId, out Genesis.Physics.PhysicsWorld physics,
                out Genesis.Runtime.ECS.World world, out RigidBodyComponent body)) return;
        float delta = Math.Clamp(ActiveGameContext?.DeltaTime ?? (1f / 60f), 0f, 0.1f);
        physics.ApplyLinearImpulse(
            world,
            body.RegistrationId,
            new Vector3((float)x, (float)y, (float)z) * delta);
    }

    [PgslCommand("PhysicsSetVelocity", "PhysicsSetVelocity(instanceId, x, y, z)", "Set a dynamic body's linear velocity", "Physics")]
    public static void PhysicsSetVelocity(double instanceId, double x, double y, double z)
    {
        if (!TryGetDynamicBody((int)instanceId, out Genesis.Physics.PhysicsWorld physics,
                out Genesis.Runtime.ECS.World world, out RigidBodyComponent body)) return;
        physics.SetLinearVelocity(
            world,
            body.RegistrationId,
            new Vector3((float)x, (float)y, (float)z));
    }

    [PgslCommand("PhysicsGetVelocityX", "PhysicsGetVelocityX(instanceId) -> number", "Read linear velocity X", "Physics")]
    public static double PhysicsGetVelocityX(double instanceId) => PhysicsVelocity((int)instanceId).X;

    [PgslCommand("PhysicsGetVelocityY", "PhysicsGetVelocityY(instanceId) -> number", "Read linear velocity Y", "Physics")]
    public static double PhysicsGetVelocityY(double instanceId) => PhysicsVelocity((int)instanceId).Y;

    [PgslCommand("PhysicsGetVelocityZ", "PhysicsGetVelocityZ(instanceId) -> number", "Read linear velocity Z", "Physics")]
    public static double PhysicsGetVelocityZ(double instanceId) => PhysicsVelocity((int)instanceId).Z;

    [PgslCommand("PhysicsIsGrounded", "PhysicsIsGrounded(instanceId, probeDistance) -> bool", "Check for ground below a physics body", "Physics")]
    public static bool PhysicsIsGrounded(double instanceId, double probeDistance = 0.15)
    {
        if (!TryGetDynamicBody((int)instanceId, out Genesis.Physics.PhysicsWorld physics,
                out Genesis.Runtime.ECS.World world, out RigidBodyComponent body)) return false;
        return physics.IsGrounded(world, body.RegistrationId, (float)Math.Clamp(probeDistance, 0.001, 100));
    }

    [PgslCommand("PhysicsCreateJoint", "PhysicsCreateJoint(instanceA, instanceB, anchorX, anchorY, anchorZ) -> number", "Create a ball joint between two dynamic bodies", "Physics")]
    public static double PhysicsCreateJoint(
        double instanceA, double instanceB, double anchorX, double anchorY, double anchorZ)
    {
        if (!TryGetDynamicBody((int)instanceA, out Genesis.Physics.PhysicsWorld physics,
                out _, out RigidBodyComponent bodyA)
            || !TryGetDynamicBody((int)instanceB, out Genesis.Physics.PhysicsWorld secondPhysics,
                out _, out RigidBodyComponent bodyB)
            || !ReferenceEquals(physics, secondPhysics)) return 0;
        return physics.CreateBallJoint(
            bodyA.RegistrationId,
            bodyB.RegistrationId,
            new Vector3((float)anchorX, (float)anchorY, (float)anchorZ));
    }

    [PgslCommand("PhysicsDestroyJoint", "PhysicsDestroyJoint(jointId) -> bool", "Remove a script-created physics joint", "Physics")]
    public static bool PhysicsDestroyJoint(double jointId) =>
        PhysicsWorld?.DestroyScriptJoint((int)jointId) ?? false;

    private static Vector3 PhysicsVelocity(int instanceId)
    {
        return TryGetDynamicBody(instanceId, out Genesis.Physics.PhysicsWorld physics,
            out _, out RigidBodyComponent body)
            ? physics.GetLinearVelocity(body.RegistrationId)
            : Vector3.Zero;
    }

    private static bool TryGetDynamicBody(
        int instanceId,
        out Genesis.Physics.PhysicsWorld physics,
        out Genesis.Runtime.ECS.World world,
        out RigidBodyComponent body)
    {
        physics = PhysicsWorld;
        world = ActiveGameContext?.World;
        body = default;
        if (physics is null || world is null) return false;
        Entity entity = world.GetEntity(instanceId);
        if (entity.IsNull || !world.Has<RigidBodyComponent>(entity)) return false;
        body = world.GetRef<RigidBodyComponent>(entity);
        return body.RegistrationId != 0 && body.Motion == PhysicsMotionType.Dynamic;
    }

    [PgslCommand("PhysicsRaycast", "PhysicsRaycast(x, y, z, dx, dy, dz, maxDistance) -> number", "Distance to the first hit, -1 for none", "Physics")]
    public static double PhysicsRaycast(
        double x, double y, double z, double dx, double dy, double dz, double maxDistance)
    {
        PgslContext context = GetContext();
        if (context != null) context.LastPhysicsRaycast = null;
        Genesis.Physics.PhysicsWorld physics = PhysicsWorld;
        Genesis.Runtime.ECS.World world = ActiveGameContext?.World;
        if (physics is null || world is null || maxDistance <= 0d || !float.IsFinite((float)maxDistance)
            || !Finite3(x, y, z) || !Finite3(dx, dy, dz)) return -1d;

        bool found = physics.Raycast(
            world,
            new Vector3((float)x, (float)y, (float)z),
            new Vector3((float)dx, (float)dy, (float)dz),
            (float)maxDistance,
            out Genesis.Physics.PhysicsRaycastHit hit);
        if (found && context != null) context.LastPhysicsRaycast = hit;
        return found ? hit.Distance : -1d;
    }

    #endregion

    #region Networking

    /// <summary>
    /// The network the Net* commands talk to. The host assigns this; it is not on
    /// <see cref="IGameContext"/> because widening that interface for one family would make every
    /// implementation carry networking it does not use.
    /// </summary>
    public static IGameNetwork ActiveNetwork { get; set; }

    [PgslCommand("NetAvailable", "NetAvailable() -> bool", "True when networking is wired up", "Networking")]
    public static bool NetAvailable() => ActiveNetwork is not null;

    [PgslCommand("NetHost", "NetHost(port) -> bool", "Start hosting on a port", "Networking")]
    public static bool NetHost(double port)
    {
        IGameNetwork network = ActiveNetwork;
        if (network is null) return false;

        try
        {
            network.Host((int)Math.Clamp(port, 1, 65535));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // A port already in use is a normal runtime condition, not a script bug — report it as
            // false so the game can offer another port instead of crashing.
            return false;
        }
    }

    [PgslCommand("NetConnect", "NetConnect(ip, port) -> bool", "Join a host", "Networking")]
    public static bool NetConnect(string ip, double port)
    {
        IGameNetwork network = ActiveNetwork;
        if (network is null || string.IsNullOrWhiteSpace(ip)) return false;

        try
        {
            network.Connect(ip, (int)Math.Clamp(port, 1, 65535));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [PgslCommand("NetDisconnect", "NetDisconnect()", "Leave and stop hosting", "Networking")]
    public static void NetDisconnect() => ActiveNetwork?.Disconnect();

    [PgslCommand("NetIsHost", "NetIsHost() -> bool", "True when this process is the host", "Networking")]
    public static bool NetIsHost() => ActiveNetwork?.IsHost ?? false;

    [PgslCommand("NetIsConnected", "NetIsConnected() -> bool", "True when connected or hosting", "Networking")]
    public static bool NetIsConnected() => ActiveNetwork?.IsConnected ?? false;

    [PgslCommand("NetPeerCount", "NetPeerCount() -> number", "How many peers are connected", "Networking")]
    public static double NetPeerCount() => ActiveNetwork?.PeerCount ?? 0;

    [PgslCommand("NetPort", "NetPort() -> number", "The port in use, 0 when inactive", "Networking")]
    public static double NetPort() => ActiveNetwork?.Port ?? 0;

    [PgslCommand("NetSendText", "NetSendText(peerId, tag, text) -> bool", "Send a UTF-8 message; peer 0 broadcasts", "Networking")]
    public static bool NetSendText(double peerId, double tag, string text)
    {
        IGameNetwork network = ActiveNetwork;
        if (network is null || !network.IsConnected) return false;

        try
        {
            network.Send((int)peerId, (int)tag, Encoding.UTF8.GetBytes(text ?? string.Empty));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [PgslCommand("NetSendNumber", "NetSendNumber(peerId, tag, value) -> bool", "Send a single number", "Networking")]
    public static bool NetSendNumber(double peerId, double tag, double value)
    {
        IGameNetwork network = ActiveNetwork;
        if (network is null || !network.IsConnected) return false;

        try
        {
            network.Send((int)peerId, (int)tag, BitConverter.GetBytes(value));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    #endregion
}

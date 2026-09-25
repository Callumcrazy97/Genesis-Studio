using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Physics;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.World.Terrain;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Project;

/// <summary>
/// Editor/runtime-shared playable preview: live heightfield collider, water volumes, and a
/// drop-in capsule that uses the same Bepu registration, buoyancy and motor states as F5.
/// </summary>
public sealed class TerrainPhysicsPreview : IDisposable
{
    private readonly PhysicsWorld _physics;
    private readonly EcsWorld _world;
    private readonly List<PhysicsWaterVolume> _volumes = new();
    private int _colliderId;
    private Entity _playable;
    private bool _playableActive;
    private int _rebuildGeneration;
    private bool _disposed;

    public TerrainPhysicsPreview()
    {
        _physics = PhysicsWorld.Create(new PhysicsWorldAsset
        {
            AllowSleep = false,
            SolverIterations = 8,
        });
        _world = new EcsWorld();
    }

    public int RebuildGeneration => _rebuildGeneration;
    public int ColliderRegistrationId => _colliderId;
    public bool PlayableIsActive => _playableActive;
    public IReadOnlyList<PhysicsWaterVolume> WaterVolumes => _volumes;
    public Vector3 PlayablePosition { get; private set; }
    public float PlayableSubmergedFraction { get; private set; }
    public CharacterMotorState PlayableMotorState { get; private set; } = CharacterMotorState.Falling;
    public bool PlayableEnteredWater { get; private set; }
    public bool PlayableExitedWater { get; private set; }

    public void Rebuild(TerrainAsset terrain, IEnumerable<TerrainWaterDefinition> waterBodies)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (_colliderId != 0)
        {
            _physics.UnregisterStaticSurface(_colliderId);
            _colliderId = 0;
        }

        TerrainColliderMesh.Build(terrain, out Vector3[] vertices, out int[] indices);
        _colliderId = _physics.RegisterStaticTriangleMesh(
            vertices,
            indices,
            Vector3.One,
            Vector3.Zero,
            Quaternion.Identity,
            "TerrainEditor:preview");

        _volumes.Clear();
        foreach (TerrainWaterDefinition definition in waterBodies ?? Array.Empty<TerrainWaterDefinition>())
            if (definition.PhysicsMode != WaterPhysicsMode.None)
                _volumes.Add(TerrainColliderMesh.CreateVolume(definition, Matrix4x4.Identity));
        _rebuildGeneration++;
    }

    public bool TryRaycastDown(float worldX, float worldZ, float ceiling, out float height)
    {
        height = 0f;
        Vector3 origin = new(worldX, ceiling, worldZ);
        float distance = MathF.Max(1f, ceiling - -1000f);
        if (!_physics.RaycastDown(_world, origin, distance, out PhysicsRaycastHit hit))
            return false;
        height = hit.Point.Y;
        return true;
    }

    public void DropIntoWater(TerrainAsset terrain, IEnumerable<TerrainWaterDefinition> waterBodies, string waterId = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        Rebuild(terrain, waterBodies);
        ClearPlayable();

        TerrainWaterDefinition target = FindWater(waterBodies, waterId);
        Vector3 spawn;
        if (target != null)
        {
            spawn = new Vector3(target.Center.X, target.SurfaceHeight + 4.5f, target.Center.Z);
        }
        else
        {
            float x = terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize * 0.5f;
            float z = terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize * 0.5f;
            spawn = new Vector3(x, terrain.SampleHeight(x, z) + 6f, z);
        }

        SpawnCapsule(spawn);
    }

    public void DropAt(Vector3 spawn, TerrainAsset terrain, IEnumerable<TerrainWaterDefinition> waterBodies)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        Rebuild(terrain, waterBodies);
        ClearPlayable();
        SpawnCapsule(spawn);
    }

    public void ResetPlayable()
    {
        ClearPlayable();
        PlayablePosition = Vector3.Zero;
        PlayableSubmergedFraction = 0f;
        PlayableMotorState = CharacterMotorState.Falling;
        PlayableEnteredWater = false;
        PlayableExitedWater = false;
    }

    public void Step(float dt, int steps = 1)
    {
        if (!_playableActive || dt <= 0f || steps <= 0)
            return;
        for (int i = 0; i < steps; i++)
            StepOnce(dt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPlayable();
        if (_colliderId != 0)
        {
            _physics.UnregisterStaticSurface(_colliderId);
            _colliderId = 0;
        }
        _physics.Dispose();
        _world.Dispose();
    }

    private void StepOnce(float dt)
    {
        if (!_world.IsAlive(_playable))
            return;

        _physics.ApplyWater(_world, _volumes, dt);
        _physics.Step(_world, dt);
        _physics.SyncTransforms(_world);

        ref RigidBodyComponent body = ref _world.GetRef<RigidBodyComponent>(_playable);
        ref Transform3DComponent transform = ref _world.GetRef<Transform3DComponent>(_playable);
        CharacterMotorComponent motor = _world.Has<CharacterMotorComponent>(_playable)
            ? _world.GetRef<CharacterMotorComponent>(_playable)
            : CharacterMotorComponent.Default;

        Vector3 position = _physics.GetBodyPosition(body.RegistrationId);
        transform.Position = position;
        PlayablePosition = position;

        PhysicsWaterVolume water = FindVolume(position);
        float submerged = water != null ? water.SubmergedFraction(position, body.HalfExtents) : 0f;
        bool swimming = water != null && water.Swimmable && submerged >= 0.2f;
        bool grounded = _physics.TryGetGroundContact(_world, body.RegistrationId, motor.StepHeight + 0.12f, out PhysicsRaycastHit ground);
        float slopeCos = MathF.Cos(Math.Clamp(motor.MaximumSlopeDegrees, 0f, 89f) * MathF.PI / 180f);
        bool walkable = grounded && ground.Normal.Y >= slopeCos;
        Vector3 velocity = _physics.GetLinearVelocity(body.RegistrationId);

        if (swimming)
        {
            velocity *= MathF.Exp(-MathF.Max(0f, motor.SwimDrag) * dt * 0.25f);
            if (position.Y >= water.SurfaceY - MathF.Max(0f, motor.SurfaceOffset) && velocity.Y > 0f)
                velocity.Y = 0f;
            motor.State = CharacterMotorState.Swimming;
        }
        else if (walkable)
        {
            if (velocity.Y < 0f) velocity.Y = 0f;
            motor.State = CharacterMotorState.Grounded;
        }
        else
        {
            motor.State = CharacterMotorState.Falling;
        }

        motor.EnteredWater = swimming && !motor.WasInWater;
        motor.ExitedWater = !swimming && motor.WasInWater;
        motor.WasInWater = swimming;
        motor.SubmergedFraction = submerged;
        _world.Set(_playable, motor);
        _world.Set(_playable, transform);
        _physics.SetLinearVelocity(_world, body.RegistrationId, velocity);

        PlayableSubmergedFraction = submerged;
        PlayableMotorState = motor.State;
        PlayableEnteredWater = motor.EnteredWater || PlayableEnteredWater;
        PlayableExitedWater = motor.ExitedWater || PlayableExitedWater;
    }

    private void SpawnCapsule(Vector3 spawn)
    {
        _playable = _world.CreateEntity();
        var transform = new Transform3DComponent
        {
            Position = spawn,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        RigidBodyComponent body = RigidBodyComponent.Character();
        CharacterMotorComponent motor = CharacterMotorComponent.Default;
        _world.Set(_playable, transform);
        _world.Set(_playable, body);
        _world.Set(_playable, motor);
        _physics.RegisterEntity(_world, _playable, ref body, ref transform);
        _world.Set(_playable, body);
        _world.Set(_playable, transform);
        PlayablePosition = spawn;
        PlayableSubmergedFraction = 0f;
        PlayableMotorState = CharacterMotorState.Falling;
        PlayableEnteredWater = false;
        PlayableExitedWater = false;
        _playableActive = true;
    }

    private void ClearPlayable()
    {
        if (!_playableActive || !_world.IsAlive(_playable))
        {
            _playableActive = false;
            return;
        }

        if (_world.Has<RigidBodyComponent>(_playable))
        {
            ref RigidBodyComponent body = ref _world.GetRef<RigidBodyComponent>(_playable);
            _physics.UnregisterEntity(_world, _playable, ref body);
        }

        _world.DestroyEntity(_playable);
        _world.FlushDeferred();
        _playableActive = false;
        _playable = default;
    }

    private PhysicsWaterVolume FindVolume(Vector3 position)
    {
        PhysicsWaterVolume selected = null;
        float best = 0f;
        foreach (PhysicsWaterVolume volume in _volumes)
        {
            float fraction = volume.SubmergedFraction(position, new Vector3(0.35f, 0.9f, 0.35f));
            if (fraction > best)
            {
                best = fraction;
                selected = volume;
            }
        }

        return selected;
    }

    private static TerrainWaterDefinition FindWater(IEnumerable<TerrainWaterDefinition> waterBodies, string waterId)
    {
        TerrainWaterDefinition first = null;
        foreach (TerrainWaterDefinition definition in waterBodies ?? Array.Empty<TerrainWaterDefinition>())
        {
            first ??= definition;
            if (!string.IsNullOrWhiteSpace(waterId)
                && string.Equals(definition.Id, waterId, StringComparison.OrdinalIgnoreCase))
                return definition;
        }

        return first;
    }
}

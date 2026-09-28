#nullable enable annotations
using System;
using System.Numerics;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

/// <summary>
/// CPU particle simulation.  Each active particle is a billboard quad that faces the
/// camera, optionally spins, drifts in wind, and is jittered by turbulence.
///
/// Rendering uses a precomputed shared camera basis (p1a) — one set of normalize+cross per
/// frame instead of per-particle, removing ~200 float ops per particle.
///
/// Stepping is synchronous (<see cref="Step"/>): for the particle counts this sim handles,
/// a direct call beats the old Task.Run/Wait path, which added per-frame allocation and
/// thread-pool latency with no real GPU overlap.
/// </summary>
public sealed partial class ParticleSimulation
{
    public readonly record struct ParticleEvent(ParticleEventTrigger Trigger, Vector3 Position, Vector3 Velocity);
    public event Action<ParticleEvent>? Occurred;
    private struct ParticleState
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public float Rotation;          // current screen-space rotation in degrees
        public float RotationVelocity;  // degrees/second
        public float PreviousSpeedScale;
        public float ColorJitter;
        public uint Serial;
        public uint PreviousSerial;
        public float TrailClock;
        public bool Stuck;
    }

    private ParticleConfig _config = new();
    private ParticleState[] _particles = new ParticleState[1];
    private MeshInstanceData[] _instanceBuffer = new MeshInstanceData[1];
    private int[] _frameIndexBuffer = new int[1];
    private MeshInstanceData[] _groupScratch = new MeshInstanceData[1];
    // Grown on demand, not in EnsureCapacity: AF1.6 is off by default, so an emitter that is never
    // asked for smoke volumes never pays for this buffer.
    private (Vector3 Position, float Weight)[] _smokeSamples = [];
    private int   _count;
    private float _emitAccumulator;
    private Random _rng = new();
    private Func<Vector3, float> _collisionHeightProvider;
    private Vector3[] _meshSurfaceSamples = Array.Empty<Vector3>();

    // World-space offset applied to new spawns. Stays Vector3.Zero (no behavior change) unless
    // ParticleConfig.FollowCameraXZ is set, in which case UpdateCameraPosition keeps it pinned
    // to the camera's XZ position so weather (rain/snow) always falls around the player.
    private Vector3 _originOffset = Vector3.Zero;
    private Matrix4x4 _planarTransform = Matrix4x4.Identity;

    /// <summary>Pixel placement for the planar simulation, including authored rotation and scale.</summary>
    public void SetPlanarTransform(Matrix4x4 transform) => _planarTransform = transform;

    public int ActiveCount => _count;
    public int Capacity    => _particles.Length;

    // ── Config management ────────────────────────────────────────────────────

    /// <summary>Full (re)load: resizes buffers and restarts emission.</summary>
    public void LoadConfig(ParticleConfig config)
    {
        _config = config ?? new ParticleConfig();
        EnsureCapacity();
        Reset();
    }

    /// <summary>Live edit: applies new parameters without clearing in-flight particles.</summary>
    public void UpdateConfig(ParticleConfig config)
    {
        if (config == null) return;
        _config = config;
        EnsureCapacity();
    }

    /// <summary>Restart with a reproducible random stream (editor previews and repeatable tests).</summary>
    /// <remarks>Ordinary runtime Reset/LoadConfig calls retain their existing random behaviour.</remarks>
    public void Reset(int randomSeed)
    {
        _rng = new Random(randomSeed);
        Reset();
    }

    public void Reset()
    {
        _count = 0;
        _emitAccumulator = 0f;
        _lastBirthSerial = 0; _nextBirthSerial = 0; _planarSeconds = 0;
        if (!_config.Loop && _config.BurstCount > 0)
            Burst();
    }

    /// <summary>
    /// Called once per frame by the host scene with the active camera's world position.
    /// Configs with <see cref="ParticleConfig.FollowCameraXZ"/> set (Rain, Snow) use this to
    /// recentre new spawns under the camera every frame, by default — this is owned entirely by
    /// the particle config, not a flag the caller has to opt into per-effect.
    /// </summary>
    public void UpdateCameraPosition(Vector3 cameraPos)
    {
        if (_config.FollowCameraXZ)
            _originOffset = new Vector3(cameraPos.X, 0f, cameraPos.Z);
    }

    /// <summary>
    /// Places new particles at an authored Object's world position. Existing particles retain
    /// their trajectories, which gives moving emitters a natural trail instead of teleporting the
    /// complete simulation every frame.
    /// </summary>
    public void SetEmitterOrigin(Vector3 worldPosition)
    {
        if (!_config.FollowCameraXZ)
            _originOffset = worldPosition;
    }

    /// <summary>
    /// Supplies scene geometry height for Bounce/Die/Stick collision. Return NaN when no terrain
    /// or collider exists below the sample; the authored collision plane remains the fallback.
    /// </summary>
    public void SetCollisionHeightProvider(Func<Vector3, float> provider) => _collisionHeightProvider = provider;

    /// <summary>Supplies local-space surface points for MeshSurface emission.</summary>
    public void SetMeshSurfaceSamples(ReadOnlySpan<Vector3> positions) => _meshSurfaceSamples = positions.ToArray();

    /// <summary>Emits a one-shot batch (used by non-looping presets and the editor Burst button).</summary>
    public void Burst()
        => Burst(_config.BurstCount > 0 ? _config.BurstCount : 200);

    /// <summary>Emits an explicit one-shot count without changing the authored configuration.</summary>
    public void Burst(int requestedCount)
    {
        int n = Math.Min(Math.Max(0, requestedCount), _particles.Length - _count);
        for (int i = 0; i < n; i++)
            Emit();
    }

    /// <summary>Spawns linked particles at a source event's world position with inherited motion.</summary>
    public int BurstAt(int requestedCount, Vector3 worldPosition, Vector3 worldVelocity, float inheritedVelocity)
    {
        int count = Math.Min(Math.Max(0, requestedCount), _particles.Length - _count);
        for (int index = 0; index < count; index++)
            Emit(worldPosition, worldVelocity, inheritedVelocity);
        return count;
    }

    // ── Simulation step ──────────────────────────────────────────────────────

    /// <summary>Synchronous step — safe to call from any thread at any time.</summary>
    public void Step(float dt)
    {
        if (dt <= 0f) return;
        _planarSeconds += dt;

        Vector3 gravity = new((float)_config.GravityX, (float)_config.Gravity, (float)_config.GravityZ);
        float damping    = MathF.Max(0f, 1f - (float)_config.Drag * dt);
        float turbulence = (float)_config.TurbulenceStrength;
        float windX      = (float)_config.WindX;
        float windZ      = (float)_config.WindZ;

        for (int i = 0; i < _count; i++)
        {
            ref ParticleState p = ref _particles[i];
            Vector3 previousPosition = p.Position;
            float previousAge = p.Age;
            p.Age += dt;
            if (previousAge < p.Life && p.Age >= p.Life)
                Notify(p, ParticleEventTrigger.Death);
            bool trail = _config.IsPlanar2D && _config.RendererKind == ParticleRendererKind.Trail;
            if (p.Age >= p.Life + (trail ? (float)_config.TrailDuration : 0))
            {
                RemoveParticle(i);
                i--;
                continue;
            }
            if (p.Age >= p.Life) continue;
            if (p.Stuck && _config.CollisionMode == ParticleCollisionMode.Stick) continue;
            p.Stuck = false;

            p.Velocity += gravity * dt;

            if (turbulence > 0f)
            {
                p.Velocity.X += RandSym() * turbulence * dt;
                p.Velocity.Y += RandSym() * turbulence * 0.25f * dt;
                if (!_config.IsPlanar2D) p.Velocity.Z += RandSym() * turbulence * dt;
            }

            float lifeTime = p.Life > 0f ? Math.Clamp(p.Age / p.Life, 0f, 1f) : 1f;
            if (_config.UseCustomSpeedCurve)
            {
                float speedScale = MathF.Max(0.001f, _config.SpeedOverLifetime?.Evaluate(lifeTime) ?? 1f);
                p.Velocity *= speedScale / MathF.Max(0.001f, p.PreviousSpeedScale);
                p.PreviousSpeedScale = speedScale;
            }
            p.Velocity *= damping;
            float velocityScale = _config.UseCustomVelocityCurve
                ? MathF.Max(0f, _config.VelocityOverLifetime?.Evaluate(lifeTime) ?? 1f)
                : 1f;
            p.Position  += p.Velocity * dt * velocityScale;
            p.Position.X += windX * dt;
            p.Position.Z += windZ * dt;
            p.Rotation   += p.RotationVelocity * dt;

            if (_config.IsPlanar2D)
            {
                CollidePlanar(ref p, previousPosition, dt);
                if (trail) UpdatePlanarTrail(i, ref p, dt);
                if (p.Age >= p.Life && !trail)
                {
                    RemoveParticle(i);
                    i--;
                }
                continue;
            }

            float collisionHeight = (float)_config.CollisionPlaneHeight;
            if (_collisionHeightProvider != null && (_config.CollideWithTerrain || _config.CollideWithGeometry))
            {
                float sampled = _collisionHeightProvider(p.Position);
                if (float.IsFinite(sampled)) collisionHeight = sampled;
            }
            if (_config.CollisionMode != ParticleCollisionMode.None
                && (_config.IsPlanar2D ? p.Position.Y >= collisionHeight : p.Position.Y <= collisionHeight))
            {
                p.Position.Y = collisionHeight;
                Notify(p, _config.CollisionMode == ParticleCollisionMode.Die
                    ? ParticleEventTrigger.Collision | ParticleEventTrigger.Death : ParticleEventTrigger.Collision);
                switch (_config.CollisionMode)
                {
                    case ParticleCollisionMode.Die:
                        p.Age = p.Life;
                        break;
                    case ParticleCollisionMode.Stick:
                        p.Velocity = Vector3.Zero;
                        p.Stuck = true;
                        break;
                    case ParticleCollisionMode.Bounce:
                        p.Velocity.Y = (_config.IsPlanar2D ? -1 : 1) * MathF.Abs(p.Velocity.Y)
                            * (float)Math.Clamp(_config.CollisionBounce, 0d, 1.5d);
                        p.Velocity.X *= 0.8f;
                        p.Velocity.Z *= 0.8f;
                        break;
                }
            }
        }

        if (_config.Loop)
        {
            float rate = (float)_config.EmitRate;
            if (rate > 0f)
            {
                _emitAccumulator += rate * dt;
                while (_emitAccumulator >= 1f && _count < _particles.Length)
                {
                    _emitAccumulator -= 1f;
                    Emit();
                }
            }
        }
    }

    private void CollidePlanar(ref ParticleState particle, Vector3 previousPosition, float dt)
    {
        if (_config.CollisionMode == ParticleCollisionMode.None) return;
        bool local = _config.SimulationSpace == ParticleSimulationSpace.Local;
        Vector3 start = local ? Vector3.Transform(previousPosition, _planarTransform) : previousPosition;
        Vector3 end = local ? Vector3.Transform(particle.Position, _planarTransform) : particle.Position;
        float floor = Vector3.Transform(new Vector3(0, (float)_config.CollisionPlaneHeight, 0), _planarTransform).Y;
        float radius = MathF.Max(.001f, (float)Math.Min(_config.StartSize, Math.Max(_config.EndSize, .001)) * .25f);
        floor -= radius;
        if (end.Y <= floor) return;

        float fraction = start.Y >= floor ? 0 : Math.Clamp((floor - start.Y) / MathF.Max(1e-8f, end.Y - start.Y), 0, 1);
        Vector3 position = Vector3.Lerp(start, end, fraction);
        position.Y = floor - .0005f;
        Vector3 velocity = local ? Vector3.TransformNormal(particle.Velocity, _planarTransform) : particle.Velocity;
        switch (_config.CollisionMode)
        {
            case ParticleCollisionMode.Die:
                particle.Age = particle.Life;
                velocity = Vector3.Zero;
                break;
            case ParticleCollisionMode.Stick:
                velocity = Vector3.Zero;
                particle.Stuck = true;
                break;
            case ParticleCollisionMode.Bounce:
                velocity.Y = -MathF.Abs(velocity.Y);
                velocity *= (float)Math.Clamp(_config.CollisionBounce, 0, 1.5);
                position += velocity * dt * (1 - fraction);
                break;
        }
        if (local && Matrix4x4.Invert(_planarTransform, out Matrix4x4 inverse))
        {
            position = Vector3.Transform(position, inverse);
            velocity = Vector3.TransformNormal(velocity, inverse);
        }
        particle.Position = position;
        particle.Velocity = velocity;
        Notify(particle, _config.CollisionMode == ParticleCollisionMode.Die
            ? ParticleEventTrigger.Collision | ParticleEventTrigger.Death : ParticleEventTrigger.Collision);
    }

    // ── Render ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds one camera-facing <see cref="MeshDrawCall"/> per active particle.
    /// The camera basis is precomputed once for the frame (p1a) — no per-particle
    /// <c>CreateBillboard</c>, <c>CreateScale</c>, or <c>CreateRotationZ</c> calls.
    /// </summary>
    /// <summary>
    /// Builds camera-facing billboard instances for GPU TransBatch upload without boxing each
    /// particle as a <see cref="MeshDrawCall"/>. Callers submit via
    /// <see cref="IRenderController.DrawMeshInstances"/> (or <see cref="DrawInstances3D"/>).
    /// </summary>
    public void RenderInstances3D(
        ReadOnlySpan<MeshHandle> frames,
        TextureHandle texture,
        Vector3 cameraPos,
        Vector3 cameraForward,
        out MeshInstanceData[] instances,
        out int count,
        out MeshDrawCall template)
    {
        _ = cameraPos;
        float startSize   = (float)_config.StartSize;
        float endSize     = (float)_config.EndSize;
        float emissive    = (float)_config.Emissive;
        bool  additive    = _config.BlendMode == ParticleBlendMode.Additive;
        bool  multiply    = _config.BlendMode == ParticleBlendMode.Multiply;
        float sizeXScale  = (float)_config.SizeXScale;
        float sizeYScale  = (float)_config.SizeYScale;
        float flipFps     = frames.Length > 1 ? (float)_config.FlipbookFps : 0f;
        int   totalFrames = Math.Max(1, frames.Length);
        bool  hasRotation = _config.RotationSpeed != 0.0 || _config.RotationVariance != 0.0;

        ParticleColor sc  = _config.StartColor ?? new ParticleColor();
        ParticleColor mc  = _config.MidColor;
        ParticleColor ec  = _config.EndColor   ?? new ParticleColor();
        float midPt       = (float)_config.ColorMidpoint;

        var flags = MeshDrawFlags.Transparent | MeshDrawFlags.NoShadow | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.Emissive;
        if (additive) flags |= MeshDrawFlags.Additive;
        if (multiply) flags |= MeshDrawFlags.Multiply;

        // Camera-facing billboard basis (default).
        Vector3 billFwd   = Vector3.Normalize(cameraForward.LengthSquared() > 1e-6f ? cameraForward : Vector3.UnitZ);
        Vector3 billRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, billFwd));
        if (billRight.LengthSquared() < 1e-6f)
            billRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, billFwd));
        Vector3 billUp    = Vector3.Cross(billFwd, billRight);

        // Precipitation streaks align to fall direction but keep a camera-visible width axis.
        Vector3 streakDir = Vector3.Zero, streakWidth = Vector3.Zero;
        if (_config.DownwardEmit)
        {
            streakDir = new Vector3((float)_config.WindX * 0.12f, -1f, (float)_config.WindZ * 0.12f);
            if (streakDir.LengthSquared() < 1e-6f) streakDir = Vector3.UnitY * -1f;
            streakDir = Vector3.Normalize(streakDir);
            streakWidth = Vector3.Cross(billFwd, streakDir);
            if (streakWidth.LengthSquared() < 1e-6f)
                streakWidth = Vector3.Cross(Vector3.UnitY, streakDir);
            if (streakWidth.LengthSquared() < 1e-6f)
                streakWidth = Vector3.UnitX;
            streakWidth = Vector3.Normalize(streakWidth);
        }

        for (int i = 0; i < _count; i++)
        {
            ref ParticleState p = ref _particles[i];
            float t = p.Life > 0f ? Math.Clamp(p.Age / p.Life, 0f, 1f) : 1f;

            float sizeTime = _config.UseCustomSizeCurve
                ? _config.SizeOverLifetime?.Evaluate(t) ?? t
                : ParticleCurveMath.Evaluate(_config.SizeCurve, t);
            float size = Lerp(startSize, endSize, sizeTime);
            float sx   = size * (sizeXScale > 0f ? sizeXScale : 1f);
            float sy   = size * (sizeYScale > 0f ? sizeYScale : 1f);
            RenderColor col    = ApplyColorJitter(EvaluateGradient(t, sc, mc, ec, midPt, _config.AlphaCurve), p.ColorJitter);
            float rotRad       = p.Rotation * (MathF.PI / 180f);

            int frameIdx = flipFps > 0f ? ((int)(p.Age * flipFps)) % totalFrames : 0;
            _frameIndexBuffer[i] = frameIdx;

            // Build billboard world matrix without CreateBillboard / CreateScale / CreateRotationZ.
            Matrix4x4 world;
            if (_config.Alignment == ParticleAlignment.Mesh3D)
            {
                Matrix4x4 rotation = Matrix4x4.CreateRotationY(rotRad);
                rotation.M11 *= sx; rotation.M12 *= sx; rotation.M13 *= sx;
                rotation.M21 *= sy; rotation.M22 *= sy; rotation.M23 *= sy;
                rotation.M31 *= sx; rotation.M32 *= sx; rotation.M33 *= sx;
                rotation.Translation = p.Position;
                world = rotation;
            }
            else if (_config.Alignment == ParticleAlignment.Horizontal)
            {
                world = new Matrix4x4(
                    sx, 0f, 0f, 0f,
                    0f, 0f, sy, 0f,
                    0f, 1f, 0f, 0f,
                    p.Position.X, p.Position.Y, p.Position.Z, 1f);
            }
            else if (_config.Alignment == ParticleAlignment.Velocity && p.Velocity.LengthSquared() > 1e-6f)
            {
                Vector3 along = Vector3.Normalize(p.Velocity);
                Vector3 width = Vector3.Cross(billFwd, along);
                if (width.LengthSquared() < 1e-6f) width = billRight;
                width = Vector3.Normalize(width);
                Vector3 depth = Vector3.Normalize(Vector3.Cross(width, along));
                world = new Matrix4x4(
                    width.X * sx, width.Y * sx, width.Z * sx, 0f,
                    along.X * sy, along.Y * sy, along.Z * sy, 0f,
                    depth.X, depth.Y, depth.Z, 0f,
                    p.Position.X, p.Position.Y, p.Position.Z, 1f);
            }
            else if (_config.DownwardEmit)
            {
                Vector3 width = streakWidth * sx;
                Vector3 along = streakDir * sy;
                Vector3 depth = Vector3.Normalize(Vector3.Cross(width, along));
                world = new Matrix4x4(
                    width.X,  width.Y,  width.Z,  0f,
                    along.X,  along.Y,  along.Z,  0f,
                    depth.X,  depth.Y,  depth.Z,  0f,
                    p.Position.X, p.Position.Y, p.Position.Z, 1f);
            }
            else if (hasRotation && rotRad != 0f)
            {
                float cR = MathF.Cos(rotRad), sR = MathF.Sin(rotRad);
                Vector3 rotRight = billRight * cR - billUp * sR;
                Vector3 rotUp    = billRight * sR + billUp * cR;
                world = new Matrix4x4(
                    rotRight.X * sx, rotRight.Y * sx, rotRight.Z * sx, 0f,
                    rotUp.X    * sy, rotUp.Y    * sy, rotUp.Z    * sy, 0f,
                    billFwd.X,       billFwd.Y,       billFwd.Z,       0f,
                    p.Position.X,    p.Position.Y,    p.Position.Z,    1f);
            }
            else
            {
                world = new Matrix4x4(
                    billRight.X * sx, billRight.Y * sx, billRight.Z * sx, 0f,
                    billUp.X    * sy, billUp.Y    * sy, billUp.Z    * sy, 0f,
                    billFwd.X,        billFwd.Y,        billFwd.Z,        0f,
                    p.Position.X,     p.Position.Y,     p.Position.Z,     1f);
            }

            _instanceBuffer[i] = new MeshInstanceData(world, col);
        }

        instances = _instanceBuffer;
        count = _count;
        template = new MeshDrawCall
        {
            Mesh = frames.Length > 0 ? frames[0] : default,
            Texture = texture,
            Tint = RenderColor.White,
            Alpha = 1f,
            Emissive = emissive,
            Flags = flags,
        };
    }

    /// <summary>
    /// Fills instance data and submits one <see cref="IRenderController.DrawMeshInstances"/> call
    /// per flipbook mesh group so particles never enter the MeshDrawCall / WorldMeshes path.
    /// </summary>
    public void DrawInstances3D(
        IRenderController renderer,
        ReadOnlySpan<MeshHandle> frames,
        TextureHandle texture,
        Vector3 cameraPos,
        Vector3 cameraForward)
    {
        if (renderer == null || frames.IsEmpty || _count <= 0) return;

        RenderInstances3D(frames, texture, cameraPos, cameraForward,
            out MeshInstanceData[] instances, out int count, out MeshDrawCall template);
        if (count <= 0) return;

        if (frames.Length == 1)
        {
            template.Mesh = frames[0];
            renderer.DrawMeshInstances(template, instances.AsSpan(0, count));
            return;
        }

        bool multiFrame = false;
        for (int i = 0; i < count; i++)
        {
            if (_frameIndexBuffer[i] != 0) { multiFrame = true; break; }
        }

        if (!multiFrame)
        {
            template.Mesh = frames[0];
            renderer.DrawMeshInstances(template, instances.AsSpan(0, count));
            return;
        }

        for (int frame = 0; frame < frames.Length; frame++)
        {
            if (!frames[frame].IsValid) continue;
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                if (_frameIndexBuffer[i] != frame) continue;
                _groupScratch[n++] = instances[i];
            }
            if (n <= 0) continue;
            template.Mesh = frames[frame];
            renderer.DrawMeshInstances(template, _groupScratch.AsSpan(0, n));
        }
    }

    /// <summary>
    /// Builds screen-space sprites for 2D editor previews (Particle Editor, Object Sandbox).
    /// World XY maps to screen via the shared 2D camera center and zoom.
    /// </summary>
    public int FillSpriteDrawCalls2D(
        Span<SpriteDrawCall> buffer,
        float centerX, float centerY, float zoom,
        TextureHandle texture)
    {
        if (_count <= 0 || buffer.Length == 0)
            return 0;
        if (_config.IsPlanar2D && _config.RendererKind != ParticleRendererKind.Billboard)
            return FillPlanarSegments(buffer, centerX, centerY, zoom, texture);

        float startSize  = (float)_config.StartSize;
        float endSize    = (float)_config.EndSize;
        float sizeXScale = (float)_config.SizeXScale;
        float sizeYScale = (float)_config.SizeYScale;
        float pixelScale = _config.IsPlanar2D ? zoom : MathF.Max(4f, zoom * 12f);
        if (_config.IsPlanar2D)
        {
            sizeXScale *= new Vector2(_planarTransform.M11, _planarTransform.M12).Length();
            sizeYScale *= new Vector2(_planarTransform.M21, _planarTransform.M22).Length();
        }

        ParticleColor sc = _config.StartColor ?? new ParticleColor();
        ParticleColor mc = _config.MidColor;
        ParticleColor ec = _config.EndColor   ?? new ParticleColor();
        float midPt      = (float)_config.ColorMidpoint;

        int n = Math.Min(_count, buffer.Length);
        for (int i = 0; i < n; i++)
        {
            ref ParticleState p = ref _particles[i];
            float t = p.Life > 0f ? Math.Clamp(p.Age / p.Life, 0f, 1f) : 1f;
            float sizeTime = _config.UseCustomSizeCurve
                ? _config.SizeOverLifetime?.Evaluate(t) ?? t
                : ParticleCurveMath.Evaluate(_config.SizeCurve, t);
            float size = Lerp(startSize, endSize, sizeTime);
            float w = MathF.Max(2f, size * (sizeXScale > 0f ? sizeXScale : 1f) * pixelScale);
            float h = MathF.Max(2f, size * (sizeYScale > 0f ? sizeYScale : 1f) * pixelScale);
            RenderColor col = ApplyColorJitter(EvaluateGradient(t, sc, mc, ec, midPt, _config.AlphaCurve), p.ColorJitter);
            Vector3 position = _config.IsPlanar2D && _config.SimulationSpace == ParticleSimulationSpace.Local
                ? Vector3.Transform(p.Position, _planarTransform) : p.Position;

            float rotation = p.Rotation;
            if (_config.Alignment == ParticleAlignment.Velocity || _config.DownwardEmit)
            {
                Vector3 direction = _config.SimulationSpace == ParticleSimulationSpace.Local && _config.IsPlanar2D
                    ? Vector3.TransformNormal(p.Velocity, _planarTransform) : p.Velocity;
                if (direction.X * direction.X + direction.Y * direction.Y > 1e-8)
                    rotation += MathF.Atan2(direction.X, -direction.Y) * (180f / MathF.PI);
            }
            if (_config.Alignment == ParticleAlignment.Velocity)
                h *= 1 + p.Velocity.Length() * (float)Math.Max(0, _config.VelocityStretch);
            Vector4 uv = default;
            if (_config.UseFlipbook)
            {
                int columns = Math.Clamp(_config.FlipbookColumns, 1, 64), rows = Math.Clamp(_config.FlipbookRows, 1, 64);
                int frame = (int)(p.Age * Math.Max(0, _config.FlipbookFps)) % (columns * rows);
                int column = frame % columns, row = frame / columns;
                uv = new Vector4(column / (float)columns, row / (float)rows,
                    (column + 1) / (float)columns, (row + 1) / (float)rows);
            }

            buffer[i] = new SpriteDrawCall
            {
                Blend = _config.BlendMode switch
                {
                    ParticleBlendMode.Additive => BlendMode.Additive, ParticleBlendMode.Multiply => BlendMode.Multiply, _ => BlendMode.Alpha,
                },
                SmoothSampling = true,
                Texture  = texture,
                X        = centerX + position.X * zoom,
                Y        = centerY + position.Y * zoom,
                Width    = MathF.Max(2f, w),
                Height   = MathF.Max(2f, h),
                OriginX  = w * 0.5f,
                OriginY  = h * 0.5f,
                Rotation = rotation,
                UvRect = uv,
                Alpha    = col.A,
                Tint     = new RenderColor(col.R, col.G, col.B, 1f),
            };
        }
        return n;
    }

    // ── Smoke extinction volumes (AF1.6) ─────────────────────────────────────

    /// <summary>
    /// Derives up to <see cref="SmokeExtinctionMath.MaxVolumes"/> analytic smoke spheres from the
    /// live particles and writes them to <paramref name="destination"/>, returning the count.
    /// </summary>
    /// <remarks>
    /// Alpha-blended emitters only. Additive fire and embers are light being added to the scene,
    /// not a medium absorbing it, so letting them build extinction volumes would darken the scene
    /// exactly where the fire is meant to brighten it — the same smoke/fire split ForestLight makes
    /// by binning only its smoke particles.
    ///
    /// The per-particle weight is alpha x remaining life, so a puff contributes most while it is
    /// opaque and young and fades out of the volume as it dissipates, rather than popping when the
    /// particle finally dies.
    /// </remarks>
    public int TryGetSmokeVolumes(Span<SmokeExtinctionMath.SmokeVolume> destination)
    {
        if (destination.IsEmpty || _count <= 0 || _config.BlendMode != ParticleBlendMode.Alpha)
            return 0;

        if (_smokeSamples.Length < _count)
            _smokeSamples = new (Vector3, float)[_count];

        ParticleColor sc = _config.StartColor ?? new ParticleColor();
        ParticleColor mc = _config.MidColor;
        ParticleColor ec = _config.EndColor ?? new ParticleColor();
        float midPt = (float)_config.ColorMidpoint;

        int samples = 0;
        for (int i = 0; i < _count; i++)
        {
            ref ParticleState p = ref _particles[i];
            float t = p.Life > 0f ? Math.Clamp(p.Age / p.Life, 0f, 1f) : 1f;
            float alpha = EvaluateGradient(t, sc, mc, ec, midPt, _config.AlphaCurve).A;
            float weight = alpha * (1f - t);
            if (weight <= 0f) continue;
            _smokeSamples[samples++] = (p.Position, weight);
        }

        if (samples == 0)
            return 0;

        float particleSize = MathF.Max((float)_config.StartSize, (float)_config.EndSize);
        return SmokeExtinctionMath.BuildVolumesFromSamples(
            _smokeSamples.AsSpan(0, samples),
            destination,
            SmokeExtinctionMath.MaxVolumes,
            MathF.Max(particleSize, SmokeExtinctionMath.DefaultMinRadius));
    }

    // ── Emitter bounds (p3b) ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the approximate world-space bounding sphere of all active particles.
    /// Callers can use this for emitter-level frustum culling before calling
    /// <see cref="DrawInstances3D"/>.
    /// </summary>
    public void GetEmitterBounds(out Vector3 center, out float radius)
    {
        if (_count == 0) { center = Vector3.Zero; radius = 0f; return; }

        Vector3 mn = _particles[0].Position;
        Vector3 mx = _particles[0].Position;
        for (int i = 1; i < _count; i++)
        {
            Vector3 pos = _particles[i].Position;
            mn = Vector3.Min(mn, pos);
            mx = Vector3.Max(mx, pos);
        }
        center = (mn + mx) * 0.5f;
        radius = Vector3.Distance(mn, mx) * 0.5f
               + (float)Math.Max(_config.StartSize, _config.EndSize);
    }

    // ── Gradient evaluation ──────────────────────────────────────────────────

    private RenderColor EvaluateGradient(
        float t,
        ParticleColor sc, ParticleColor mc, ParticleColor ec,
        float midPt,
        ParticleInterpolationCurve alphaCurve)
    {
        RenderColor color = EvaluateGradientRaw(t, sc, mc, ec, midPt);
        if (_config.GradientStops is { Count: > 0 })
            color = EvaluateStops(t, color);

        float alphaTime = _config.UseCustomAlphaCurve
            ? _config.AlphaOverLifetime?.Evaluate(t) ?? t
            : ParticleCurveMath.Evaluate(alphaCurve, t);
        if (MathF.Abs(alphaTime - t) < 1e-5f) return color;
        RenderColor alpha = _config.GradientStops is { Count: > 0 }
            ? EvaluateStops(alphaTime, color)
            : EvaluateGradientRaw(alphaTime, sc, mc, ec, midPt);
        return new RenderColor(color.R, color.G, color.B, alpha.A);
    }

    private RenderColor EvaluateStops(float t, RenderColor fallback)
    {
        ParticleGradientStop before = null;
        ParticleGradientStop after = null;
        foreach (ParticleGradientStop stop in _config.GradientStops)
        {
            if (stop?.Color == null) continue;
            float position = (float)Math.Clamp(stop.Position, 0d, 1d);
            if (position <= t && (before == null || stop.Position > before.Position)) before = stop;
            if (position >= t && (after == null || stop.Position < after.Position)) after = stop;
        }
        before ??= after;
        after ??= before;
        if (before?.Color == null || after?.Color == null) return fallback;
        float a = (float)Math.Clamp(before.Position, 0d, 1d);
        float b = (float)Math.Clamp(after.Position, 0d, 1d);
        float amount = b - a <= 1e-5f ? 0f : Math.Clamp((t - a) / (b - a), 0f, 1f);
        ParticleColor ca = before.Color;
        ParticleColor cb = after.Color;
        return new RenderColor(
            Lerp(ca.R, cb.R, amount), Lerp(ca.G, cb.G, amount),
            Lerp(ca.B, cb.B, amount), Lerp(ca.A, cb.A, amount));
    }

    private static RenderColor ApplyColorJitter(RenderColor color, float jitter)
    {
        if (MathF.Abs(jitter) < 1e-5f) return color;
        float scale = Math.Clamp(1f + jitter, 0f, 2f);
        return new RenderColor(
            Math.Clamp(color.R * scale, 0f, 1f),
            Math.Clamp(color.G * scale, 0f, 1f),
            Math.Clamp(color.B * scale, 0f, 1f), color.A);
    }

    private static RenderColor EvaluateGradientRaw(
        float t,
        ParticleColor sc, ParticleColor mc, ParticleColor ec,
        float midPt)
    {
        if (mc == null || midPt <= 0f || midPt >= 1f)
            return new RenderColor(
                Lerp(sc.R, ec.R, t), Lerp(sc.G, ec.G, t),
                Lerp(sc.B, ec.B, t), Lerp(sc.A, ec.A, t));

        if (t <= midPt)
        {
            float tt = t / midPt;
            return new RenderColor(
                Lerp(sc.R, mc.R, tt), Lerp(sc.G, mc.G, tt),
                Lerp(sc.B, mc.B, tt), Lerp(sc.A, mc.A, tt));
        }
        else
        {
            float tt = (t - midPt) / (1f - midPt);
            return new RenderColor(
                Lerp(mc.R, ec.R, tt), Lerp(mc.G, ec.G, tt),
                Lerp(mc.B, ec.B, tt), Lerp(mc.A, ec.A, tt));
        }
    }

    // ── Emission ─────────────────────────────────────────────────────────────

    private void Notify(in ParticleState particle, ParticleEventTrigger trigger)
    {
        if (Occurred is null) return;
        Vector3 position = _config.IsPlanar2D && _config.SimulationSpace == ParticleSimulationSpace.Local
            ? Vector3.Transform(particle.Position, _planarTransform) : particle.Position;
        Vector3 velocity = _config.IsPlanar2D && _config.SimulationSpace == ParticleSimulationSpace.Local
            ? Vector3.TransformNormal(particle.Velocity, _planarTransform) : particle.Velocity;
        Occurred(new ParticleEvent(trigger, position, velocity));
    }

    private void Emit(Vector3? eventWorldPosition = null, Vector3? eventWorldVelocity = null, float inheritedVelocity = 0)
    {
        if (_count >= _particles.Length) return;

        float speed  = (float)_config.Speed * (1f + RandSym() * (float)_config.SpeedVariance);
        float life   = MathF.Max(0.05f,
            (float)_config.Lifetime * (1f + RandSym() * (float)_config.LifetimeVariance));
        float emitR  = MathF.Max(0.01f, (float)_config.EmitRadius);

        Vector3 pos;
        Vector3 dir;

        switch (_config.Shape)
        {
            case ParticleEmitShape.Sphere:
                pos = Vector3.Zero;
                dir = RandomOnSphere();
                break;

            case ParticleEmitShape.Disc:
            {
                float ang = _rng.NextSingle() * MathF.Tau;
                float rad = MathF.Sqrt(_rng.NextSingle()) * emitR;
                pos = _config.IsPlanar2D
                    ? new Vector3(MathF.Cos(ang) * rad, MathF.Sin(ang) * rad, 0)
                    : new Vector3(MathF.Cos(ang) * rad, 0f, MathF.Sin(ang) * rad);
                dir = ConeDirection(MathF.PI / 180f * (float)_config.SpreadDegrees * 0.5f);
                break;
            }

            case ParticleEmitShape.Ring:
            {
                float ang        = _rng.NextSingle() * MathF.Tau;
                pos = _config.IsPlanar2D
                    ? new Vector3(MathF.Cos(ang) * emitR, MathF.Sin(ang) * emitR, 0)
                    : new Vector3(MathF.Cos(ang) * emitR, 0f, MathF.Sin(ang) * emitR);
                float radialBias = (float)_config.SpreadDegrees / 180f - 1f;
                var tangent = _config.IsPlanar2D ? new Vector3(-MathF.Sin(ang), MathF.Cos(ang), 0)
                    : new Vector3(-MathF.Sin(ang), 0, MathF.Cos(ang));
                var radial = Vector3.Normalize(pos);
                dir = Vector3.Normalize(tangent + (_config.IsPlanar2D ? Vector3.Zero : Vector3.UnitY * .6f)
                    + radial * radialBias * .5f);
                break;
            }

            case ParticleEmitShape.Box:
                pos = new Vector3(
                    RandSym() * (float)_config.BoxSizeX * 0.5f,
                    RandSym() * (float)_config.BoxSizeY * 0.5f,
                    RandSym() * (float)_config.BoxSizeZ * 0.5f);
                dir = ConeDirection(MathF.PI / 180f * (float)_config.SpreadDegrees);
                break;

            case ParticleEmitShape.MeshSurface:
            {
                if (_meshSurfaceSamples.Length > 0)
                {
                    pos = _meshSurfaceSamples[_rng.Next(_meshSurfaceSamples.Length)];
                    if (_config.IsPlanar2D) pos = new Vector3(pos.X, pos.Y, 0) * Particle2DLayout.PixelsPerUnit;
                    dir = Vector3.Normalize(pos.LengthSquared() > 1e-6f ? pos : Vector3.UnitY);
                    break;
                }
                // The runtime keeps shape sampling deterministic and renderer-independent. Until a
                // mesh sampler is supplied by the host, use the six faces of the authored bounds.
                float hx = (float)_config.BoxSizeX * 0.5f;
                float hy = (float)_config.BoxSizeY * 0.5f;
                float hz = (float)_config.BoxSizeZ * 0.5f;
                int face = _rng.Next(6);
                pos = face switch
                {
                    0 => new Vector3(hx, RandSym() * hy, RandSym() * hz),
                    1 => new Vector3(-hx, RandSym() * hy, RandSym() * hz),
                    2 => new Vector3(RandSym() * hx, hy, RandSym() * hz),
                    3 => new Vector3(RandSym() * hx, -hy, RandSym() * hz),
                    4 => new Vector3(RandSym() * hx, RandSym() * hy, hz),
                    _ => new Vector3(RandSym() * hx, RandSym() * hy, -hz),
                };
                dir = Vector3.Normalize(pos.LengthSquared() > 1e-6f ? pos : Vector3.UnitY);
                break;
            }

            case ParticleEmitShape.Point:
                pos = Vector3.Zero;
                dir = ConeDirection(MathF.PI / 180f * 4f);
                break;

            default: // Cone
                pos = Vector3.Zero;
                dir = ConeDirection(MathF.PI / 180f * (float)_config.SpreadDegrees);
                break;
        }

        // Precipitation falls from the sky: flip the launch so it points downward.
        if (_config.IsPlanar2D)
        {
            pos.Y = -pos.Y;
            dir.Y = -dir.Y;
            pos.Z = 0; dir.Z = 0;
        }
        if (_config.DownwardEmit) dir.Y = (_config.IsPlanar2D ? 1 : -1) * MathF.Abs(dir.Y);

        float startRot = RandSym() * (float)_config.RotationVariance * 180f;
        float rotVel   = (float)_config.RotationSpeed * (1f + RandSym() * 0.3f);

        // Recentre the spawn point under the tracked origin (camera XZ for precipitation
        // presets, Vector3.Zero otherwise — see UpdateCameraPosition). In-flight particles are
        // unaffected; only new spawns shift, so existing motion stays exactly as it was.
        Vector3 velocity = dir * speed;
        if (_config.IsPlanar2D)
        {
            if (_config.SimulationSpace == ParticleSimulationSpace.World)
            {
                pos = Vector3.Transform(pos, _planarTransform);
                velocity = Vector3.TransformNormal(velocity, _planarTransform);
            }
        }
        else pos += _originOffset;
        if (eventWorldPosition is Vector3 eventPosition)
        {
            if (_config.IsPlanar2D && _config.SimulationSpace == ParticleSimulationSpace.Local
                && Matrix4x4.Invert(_planarTransform, out Matrix4x4 inverse))
            {
                pos += Vector3.Transform(eventPosition, inverse);
                if (eventWorldVelocity is Vector3 inherited)
                    velocity += Vector3.TransformNormal(inherited, inverse) * inheritedVelocity;
            }
            else
            {
                pos += eventPosition - new Vector3(_planarTransform.M41, _planarTransform.M42, _planarTransform.M43);
                if (eventWorldVelocity is Vector3 inherited) velocity += inherited * inheritedVelocity;
            }
        }

        _particles[_count++] = new ParticleState
        {
            Position         = pos,
            Velocity         = velocity,
            Age              = 0f,
            Life             = life,
            Rotation         = startRot,
            RotationVelocity = rotVel,
            PreviousSpeedScale = 1f,
            ColorJitter      = RandSym() * (float)Math.Clamp(_config.ColorJitter, 0d, 1d),
            Serial           = ++_nextBirthSerial,
            PreviousSerial   = _lastBirthSerial,
        };
        _lastBirthSerial = _nextBirthSerial;
        InitialisePlanarTrail(_count - 1);
        Notify(_particles[_count - 1], ParticleEventTrigger.Birth);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Vector3 ConeDirection(float halfAngle)
    {
        if (_config.IsPlanar2D)
        {
            float angle = RandSym() * Math.Clamp(halfAngle, 0f, MathF.PI);
            return new Vector3(MathF.Sin(angle), MathF.Cos(angle), 0);
        }
        float cosA     = MathF.Cos(Math.Clamp(halfAngle, 0f, MathF.PI));
        float cosTheta = Lerp(cosA, 1f, _rng.NextSingle());
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        float phi      = _rng.NextSingle() * MathF.Tau;
        return new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
    }

    private Vector3 RandomOnSphere()
    {
        if (_config.IsPlanar2D)
        {
            float angle = _rng.NextSingle() * MathF.Tau;
            return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
        }
        float z   = RandSym();
        float phi = _rng.NextSingle() * MathF.Tau;
        float r   = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(phi), z, r * MathF.Sin(phi));
    }

    private void EnsureCapacity()
    {
        int cap = Math.Max(1, _config.MaxParticles);
        if (_particles.Length == cap) { EnsurePlanarTrails(); return; }
        var resized = new ParticleState[cap];
        int keep = Math.Min(_count, cap);
        Array.Copy(_particles, resized, keep);
        _particles = resized;
        _instanceBuffer = new MeshInstanceData[cap];
        _frameIndexBuffer = new int[cap];
        _groupScratch = new MeshInstanceData[cap];
        _count = keep;
        EnsurePlanarTrails();
    }

    private float RandSym() => _rng.NextSingle() * 2f - 1f;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

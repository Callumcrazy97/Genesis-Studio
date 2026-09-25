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
public sealed class ParticleSimulation
{
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
    private Matrix4x4 _emitterRotation = Matrix4x4.Identity;

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
        {
            _originOffset = new Vector3(cameraPos.X, 0f, cameraPos.Z) + new Vector3(
                (float)_config.EmitterOffsetX,
                (float)_config.EmitterOffsetY,
                (float)_config.EmitterOffsetZ);
            _emitterRotation = Matrix4x4.CreateFromYawPitchRoll(
                (float)(_config.EmitterYaw * Math.PI / 180.0),
                (float)(_config.EmitterPitch * Math.PI / 180.0),
                (float)(_config.EmitterRoll * Math.PI / 180.0));
        }
    }

    /// <summary>
    /// Places new particles at an authored Object's world position. Existing particles retain
    /// their trajectories, which gives moving emitters a natural trail instead of teleporting the
    /// complete simulation every frame.
    /// </summary>
    public void SetEmitterOrigin(Vector3 worldPosition)
    {
        if (!_config.FollowCameraXZ)
            _originOffset = worldPosition + new Vector3(
                (float)_config.EmitterOffsetX,
                (float)_config.EmitterOffsetY,
                (float)_config.EmitterOffsetZ);
        _emitterRotation = Matrix4x4.CreateFromYawPitchRoll(
            (float)(_config.EmitterYaw * Math.PI / 180.0),
            (float)(_config.EmitterPitch * Math.PI / 180.0),
            (float)(_config.EmitterRoll * Math.PI / 180.0));
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

    // ── Simulation step ──────────────────────────────────────────────────────

    /// <summary>Synchronous step — safe to call from any thread at any time.</summary>
    public void Step(float dt)
    {
        if (dt <= 0f) return;

        Vector3 gravity = new((float)_config.GravityX, (float)_config.Gravity, (float)_config.GravityZ);
        float damping    = MathF.Max(0f, 1f - (float)_config.Drag * dt);
        float turbulence = (float)_config.TurbulenceStrength;
        float windX      = (float)_config.WindX;
        float windZ      = (float)_config.WindZ;

        for (int i = 0; i < _count; i++)
        {
            ref ParticleState p = ref _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _count--;
                if (i < _count) _particles[i] = _particles[_count];
                i--;
                continue;
            }

            p.Velocity += gravity * dt;

            if (turbulence > 0f)
            {
                p.Velocity.X += RandSym() * turbulence * dt;
                p.Velocity.Y += RandSym() * turbulence * 0.25f * dt;
                p.Velocity.Z += RandSym() * turbulence * dt;
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

            float collisionHeight = (float)_config.CollisionPlaneHeight;
            if (_collisionHeightProvider != null && (_config.CollideWithTerrain || _config.CollideWithGeometry))
            {
                float sampled = _collisionHeightProvider(p.Position);
                if (float.IsFinite(sampled)) collisionHeight = sampled;
            }
            if (_config.CollisionMode != ParticleCollisionMode.None
                && p.Position.Y <= collisionHeight)
            {
                p.Position.Y = collisionHeight;
                switch (_config.CollisionMode)
                {
                    case ParticleCollisionMode.Die:
                        p.Age = p.Life;
                        break;
                    case ParticleCollisionMode.Stick:
                        p.Velocity = Vector3.Zero;
                        break;
                    case ParticleCollisionMode.Bounce:
                        p.Velocity.Y = MathF.Abs(p.Velocity.Y) * (float)Math.Clamp(_config.CollisionBounce, 0d, 1.5d);
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

        float startSize  = (float)_config.StartSize;
        float endSize    = (float)_config.EndSize;
        float sizeXScale = (float)_config.SizeXScale;
        float sizeYScale = (float)_config.SizeYScale;
        float pixelScale = MathF.Max(4f, zoom * 12f);

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
            float w = size * (sizeXScale > 0f ? sizeXScale : 1f) * pixelScale;
            float h = size * (sizeYScale > 0f ? sizeYScale : 1f) * pixelScale;
            RenderColor col = ApplyColorJitter(EvaluateGradient(t, sc, mc, ec, midPt, _config.AlphaCurve), p.ColorJitter);

            buffer[i] = new SpriteDrawCall
            {
                Texture  = texture,
                X        = centerX + p.Position.X * zoom,
                Y        = centerY + p.Position.Y * zoom,
                Width    = MathF.Max(2f, w),
                Height   = MathF.Max(2f, h),
                OriginX  = w * 0.5f,
                OriginY  = h * 0.5f,
                Rotation = p.Rotation,
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

    private void Emit()
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
                pos = new Vector3(MathF.Cos(ang) * rad, 0f, MathF.Sin(ang) * rad);
                dir = ConeDirection(MathF.PI / 180f * (float)_config.SpreadDegrees * 0.5f);
                break;
            }

            case ParticleEmitShape.Ring:
            {
                float ang        = _rng.NextSingle() * MathF.Tau;
                pos = new Vector3(MathF.Cos(ang) * emitR, 0f, MathF.Sin(ang) * emitR);
                float radialBias = (float)_config.SpreadDegrees / 180f - 1f;
                var tangent  = Vector3.Normalize(new Vector3(-MathF.Sin(ang), 0f,  MathF.Cos(ang)));
                var radial   = new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang));
                dir = Vector3.Normalize(tangent + Vector3.UnitY * 0.6f + radial * radialBias * 0.5f);
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
        if (_config.DownwardEmit) dir.Y = -MathF.Abs(dir.Y);

        float startRot = RandSym() * (float)_config.RotationVariance * 180f;
        float rotVel   = (float)_config.RotationSpeed * (1f + RandSym() * 0.3f);

        // Apply the authored emitter orientation before translating to the owning Object /
        // preview origin. In-flight particles keep their world trajectories when the emitter moves.
        pos = Vector3.Transform(pos, _emitterRotation) + _originOffset;
        dir = Vector3.TransformNormal(dir, _emitterRotation);
        if (dir.LengthSquared() > 1e-8f) dir = Vector3.Normalize(dir);

        _particles[_count++] = new ParticleState
        {
            Position         = pos,
            Velocity         = dir * speed,
            Age              = 0f,
            Life             = life,
            Rotation         = startRot,
            RotationVelocity = rotVel,
            PreviousSpeedScale = 1f,
            ColorJitter      = RandSym() * (float)Math.Clamp(_config.ColorJitter, 0d, 1d),
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Vector3 ConeDirection(float halfAngle)
    {
        float cosA     = MathF.Cos(Math.Clamp(halfAngle, 0f, MathF.PI));
        float cosTheta = Lerp(cosA, 1f, _rng.NextSingle());
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        float phi      = _rng.NextSingle() * MathF.Tau;
        return new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
    }

    private Vector3 RandomOnSphere()
    {
        float z   = RandSym();
        float phi = _rng.NextSingle() * MathF.Tau;
        float r   = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(phi), z, r * MathF.Sin(phi));
    }

    private void EnsureCapacity()
    {
        int cap = Math.Max(1, _config.MaxParticles);
        if (_particles.Length == cap) return;
        var resized = new ParticleState[cap];
        int keep = Math.Min(_count, cap);
        Array.Copy(_particles, resized, keep);
        _particles = resized;
        _instanceBuffer = new MeshInstanceData[cap];
        _frameIndexBuffer = new int[cap];
        _groupScratch = new MeshInstanceData[cap];
        _count = keep;
    }

    private float RandSym() => _rng.NextSingle() * 2f - 1f;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

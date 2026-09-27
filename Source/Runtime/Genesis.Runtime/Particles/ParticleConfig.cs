using System;
using System.Collections.Generic;
using System.Linq;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

// ── Enums ────────────────────────────────────────────────────────────────────

/// <summary>How new particles are distributed when they spawn.</summary>
public enum ParticleEmitShape
{
    /// <summary>All particles launch from the origin in a near-vertical column.</summary>
    Point,
    /// <summary>Upward cone with <see cref="ParticleConfig.SpreadDegrees"/> half-angle.</summary>
    Cone,
    /// <summary>Launched outward in every direction from the origin (sphere shell).</summary>
    Sphere,
    /// <summary>Spawned across a flat horizontal disc and launched upward.</summary>
    Disc,
    /// <summary>
    /// Spawned on a horizontal ring at <see cref="ParticleConfig.EmitRadius"/>.
    /// <see cref="ParticleConfig.SpreadDegrees"/> biases toward inward (0) or outward (360) radial velocity.
    /// </summary>
    Ring,
    /// <summary>Spawned throughout an authored three-dimensional box.</summary>
    Box,
    /// <summary>Spawned across the surface of <see cref="ParticleConfig.MeshSurfaceAsset"/>.</summary>
    MeshSurface,
}

/// <summary>How billboard quads are composited.</summary>
public enum ParticleBlendMode
{
    /// <summary>Standard premultiplied alpha — correct for smoke, water, soft glows.</summary>
    Alpha,
    /// <summary>Additive — colour is added to whatever is behind; great for fire, sparks, magic.</summary>
    Additive,
    /// <summary>Darkening blend for soot, decals and stylised shadow particles.</summary>
    Multiply,
}

/// <summary>How a rendered particle is oriented in three-dimensional space.</summary>
public enum ParticleAlignment
{
    Billboard,
    Velocity,
    Horizontal,
    Mesh3D,
}

public enum ParticleRendererKind
{
    Billboard = 0,
    Trail = 1,
    Ribbon = 2,
    Beam = 3,
}

public enum ParticleSimulationSpace
{
    World = 0,
    Local = 1,
}

[Flags]
public enum ParticleEventTrigger
{
    None = 0,
    Birth = 1,
    Death = 2,
    Collision = 4,
}

public enum ParticleBoundsMode
{
    Automatic = 0,
    Custom = 1,
}

/// <summary>Response when a particle crosses the configured collision plane.</summary>
public enum ParticleCollisionMode
{
    None,
    Bounce,
    Die,
    Stick,
}

/// <summary>Editor preview target. Runtime emitters remain attached through ordinary ECS components.</summary>
public enum ParticlePreviewTargetType
{
    None,
    Terrain,
    Model,
    Object,
}

/// <summary>Reusable authoring curves for lifetime-normalised particle properties.</summary>
public enum ParticleInterpolationCurve
{
    Linear,
    EaseIn,
    EaseOut,
    SmoothStep,
}

public static class ParticleCurveMath
{
    public static float Evaluate(ParticleInterpolationCurve curve, float time)
    {
        float t = System.Math.Clamp(time, 0f, 1f);
        return curve switch
        {
            ParticleInterpolationCurve.EaseIn => t * t,
            ParticleInterpolationCurve.EaseOut => 1f - (1f - t) * (1f - t),
            ParticleInterpolationCurve.SmoothStep => t * t * (3f - 2f * t),
            _ => t,
        };
    }
}

// ── Serialisable colour ───────────────────────────────────────────────────────

/// <summary>Serialisable RGBA colour used in particle gradients. All channels 0–1.</summary>
public sealed class ParticleColor
{
    public float R { get; set; } = 1f;
    public float G { get; set; } = 1f;
    public float B { get; set; } = 1f;
    public float A { get; set; } = 1f;

    public ParticleColor() { }

    public ParticleColor(float r, float g, float b, float a)
    {
        R = r; G = g; B = b; A = a;
    }

    public RenderColor ToRenderColor() => new(R, G, B, A);
    public ParticleColor Clone() => new(R, G, B, A);
}

/// <summary>A draggable colour or alpha key on the lifetime gradient.</summary>
public sealed class ParticleGradientStop
{
    public double Position { get; set; }
    public ParticleColor Color { get; set; } = new();

    public ParticleGradientStop Clone() => new()
    {
        Position = Position,
        Color = Color?.Clone() ?? new ParticleColor(),
    };
}

/// <summary>Cubic Bezier remap used by the visual lifetime curve editor.</summary>
public sealed class ParticleBezierCurve
{
    public double X1 { get; set; } = 0.25;
    public double Y1 { get; set; } = 0.25;
    public double X2 { get; set; } = 0.75;
    public double Y2 { get; set; } = 0.75;

    public float Evaluate(float time)
    {
        float target = Math.Clamp(time, 0f, 1f);
        float lo = 0f;
        float hi = 1f;
        for (int i = 0; i < 10; i++)
        {
            float t = (lo + hi) * 0.5f;
            if (Bezier(t, 0f, (float)X1, (float)X2, 1f) < target) lo = t;
            else hi = t;
        }
        float u = (lo + hi) * 0.5f;
        return Bezier(u, 0f, (float)Y1, (float)Y2, 1f);
    }

    public ParticleBezierCurve Clone() => new() { X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2 };

    private static float Bezier(float t, float a, float b, float c, float d)
    {
        float inv = 1f - t;
        return inv * inv * inv * a + 3f * inv * inv * t * b + 3f * inv * t * t * c + t * t * t * d;
    }
}

/// <summary>Optional point light emitted by an effect as a whole.</summary>
public sealed class ParticleLightConfig
{
    public bool Enabled { get; set; }
    public ParticleColor Color { get; set; } = new(1f, 0.48f, 0.12f, 1f);
    public double Radius { get; set; } = 7.5;
    public double Intensity { get; set; } = 2.5;
    public double Falloff { get; set; } = 2.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; } = 0.8;
    public double OffsetZ { get; set; }
    public double FlickerAmount { get; set; } = 0.18;
    public double FlickerFrequency { get; set; } = 8.0;

    public ParticleLightConfig Clone() => new()
    {
        Enabled = Enabled,
        Color = Color?.Clone() ?? new ParticleColor(1f, 0.48f, 0.12f, 1f),
        Radius = Radius,
        Intensity = Intensity,
        Falloff = Falloff,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        OffsetZ = OffsetZ,
        FlickerAmount = FlickerAmount,
        FlickerFrequency = FlickerFrequency,
    };
}

public sealed class ParticleEventLink
{
    public string SourceEmitterId { get; set; } = "primary";
    public string TargetEmitterId { get; set; } = "";
    public ParticleEventTrigger Trigger { get; set; } = ParticleEventTrigger.Death;
    public double Probability { get; set; } = 1.0;
    public int Count { get; set; } = 1;
    public double InheritVelocity { get; set; }

    public ParticleEventLink Clone() => new()
    {
        SourceEmitterId = SourceEmitterId,
        TargetEmitterId = TargetEmitterId,
        Trigger = Trigger,
        Probability = Probability,
        Count = Count,
        InheritVelocity = InheritVelocity,
    };
}

/// <summary>An additional independently editable emitter in a particle effect.</summary>
public sealed class ParticleEmitterLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Emitter";
    public bool Enabled { get; set; } = true;
    public ParticleConfig Config { get; set; } = new();

    public ParticleEmitterLayer Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Config = Config?.Clone(includeEmitters: false) ?? new ParticleConfig(),
    };
}

// ── Main config ───────────────────────────────────────────────────────────────

/// <summary>
/// Authoring parameters for a particle effect.  All fields have defaults and are
/// backward-compatible — new fields in serialised JSON simply take their default values.
/// </summary>
public sealed class ParticleConfig
{
    // The root config remains the first emitter for compatibility with existing .particle files.
    public string EffectName { get; set; } = "Particle Effect";
    public string EmitterId { get; set; } = "primary";
    public string EmitterName { get; set; } = "Primary";
    public bool EmitterEnabled { get; set; } = true;
    public double Duration { get; set; } = 3.0;
    public List<ParticleEmitterLayer> Emitters { get; set; } = [];
    public List<ParticleEventLink> EventLinks { get; set; } = [];
    public ParticleLightConfig Light { get; set; } = new();
    // ── Emission ─────────────────────────────────────────────────────────────

    public int MaxParticles { get; set; } = 1000;
    public double EmitRate { get; set; } = 60;            // particles/second (looping)
    public int BurstCount { get; set; } = 0;              // one-shot count (non-looping)
    public bool Loop { get; set; } = true;
    public ParticleEmitShape Shape { get; set; } = ParticleEmitShape.Cone;
    public double SpreadDegrees { get; set; } = 22;       // half-angle for Cone; inward bias for Ring
    public double EmitRadius { get; set; } = 0.6;         // radius for Disc / Ring shapes
    public double BoxSizeX { get; set; } = 1;
    public double BoxSizeY { get; set; } = 1;
    public double BoxSizeZ { get; set; } = 1;
    public string MeshSurfaceAsset { get; set; } = "";

    // ── Motion ───────────────────────────────────────────────────────────────

    public double Speed { get; set; } = 4;
    public double SpeedVariance { get; set; } = 0.35;     // 0–1 fraction of Speed
    public double Gravity { get; set; } = 1.5;            // world units/s² on +Y (positive = rises)
    public double GravityX { get; set; }
    public double GravityZ { get; set; }
    public double Drag { get; set; } = 0.4;               // linear velocity damping per second
    public double WindX { get; set; } = 0;                // constant world-space drift on X
    public double WindZ { get; set; } = 0;                // constant world-space drift on Z
    public double TurbulenceStrength { get; set; } = 0;   // random velocity nudge magnitude per second
    /// <summary>Flip the initial launch to point downward (rain/precipitation that falls from the sky).</summary>
    public bool DownwardEmit { get; set; } = false;

    // ── Lifetime ─────────────────────────────────────────────────────────────

    public double Lifetime { get; set; } = 1.4;
    public double LifetimeVariance { get; set; } = 0.25;  // 0–1 fraction of Lifetime

    // ── Appearance ───────────────────────────────────────────────────────────

    public double StartSize { get; set; } = 0.5;
    public double EndSize { get; set; } = 0.06;
    public ParticleInterpolationCurve SizeCurve { get; set; } = ParticleInterpolationCurve.Linear;
    public double Emissive { get; set; } = 1.8;

    public ParticleBlendMode BlendMode { get; set; } = ParticleBlendMode.Alpha;

    /// <summary>Start colour (t=0). RGB drives tint; A drives opacity.</summary>
    public ParticleColor StartColor { get; set; } = new(1f, 0.85f, 0.32f, 1f);
    /// <summary>Optional midpoint colour (t=<see cref="ColorMidpoint"/>). Null disables the 3-stop gradient.</summary>
    public ParticleColor MidColor { get; set; } = null;
    /// <summary>Normalised position (0–1) of <see cref="MidColor"/> along the lifetime.</summary>
    public double ColorMidpoint { get; set; } = 0.5;
    /// <summary>End colour (t=1).</summary>
    public ParticleColor EndColor { get; set; } = new(0.9f, 0.18f, 0.05f, 0f);
    /// <summary>Remaps lifetime only for opacity interpolation; RGB remains on the colour gradient.</summary>
    public ParticleInterpolationCurve AlphaCurve { get; set; } = ParticleInterpolationCurve.Linear;
    public bool UseCustomSizeCurve { get; set; }
    public bool UseCustomSpeedCurve { get; set; }
    public bool UseCustomAlphaCurve { get; set; }
    public bool UseCustomVelocityCurve { get; set; }
    public ParticleBezierCurve SizeOverLifetime { get; set; } = new();
    public ParticleBezierCurve SpeedOverLifetime { get; set; } = new();
    public ParticleBezierCurve AlphaOverLifetime { get; set; } = new();
    public ParticleBezierCurve VelocityOverLifetime { get; set; } = new();
    public List<ParticleGradientStop> GradientStops { get; set; } = [];
    public double ColorJitter { get; set; }

    // ── Rotation ─────────────────────────────────────────────────────────────

    /// <summary>Degrees per second that each billboard spins around its centre.</summary>
    public double RotationSpeed { get; set; } = 0;
    /// <summary>Each particle gets a random start rotation in ±(RotationVariance × 180°).</summary>
    public double RotationVariance { get; set; } = 0;

    // ── Size scaling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Multiplier on the X axis of the billboard quad relative to the base size.
    /// Use &lt;1 for thin vertical streaks (rain), &gt;1 for wide horizontal shapes.
    /// </summary>
    public double SizeXScale { get; set; } = 1.0;

    /// <summary>
    /// Multiplier on the Y axis of the billboard quad relative to the base size.
    /// Use &gt;1 for tall vertical streaks (rain), &lt;1 for flat horizontal shapes.
    /// </summary>
    public double SizeYScale { get; set; } = 1.0;

    // ── Texture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Absolute path to a PNG/JPG texture (chosen from Sprites/ or Textures/).
    /// Leave empty for an untextured solid-colour quad.
    /// The texture size does NOT affect particle world size — that is always set by
    /// <see cref="StartSize"/> / <see cref="EndSize"/>.
    /// </summary>
    public string TexturePath { get; set; } = "";

    // ── Flipbook animation ───────────────────────────────────────────────────

    /// <summary>
    /// When true the texture is treated as a sprite-sheet grid of
    /// <see cref="FlipbookColumns"/> × <see cref="FlipbookRows"/> frames that are
    /// cycled over each particle's lifetime at <see cref="FlipbookFps"/> fps.
    /// </summary>
    public bool UseFlipbook { get; set; } = false;

    /// <summary>Number of animation columns in the sprite sheet.</summary>
    public int FlipbookColumns { get; set; } = 4;

    /// <summary>Number of animation rows in the sprite sheet.</summary>
    public int FlipbookRows { get; set; } = 4;

    /// <summary>Animation playback speed in frames per second.</summary>
    public double FlipbookFps { get; set; } = 12.0;
    public ParticleAlignment Alignment { get; set; } = ParticleAlignment.Billboard;
    public ParticleRendererKind RendererKind { get; set; } = ParticleRendererKind.Billboard;
    public ParticleSimulationSpace SimulationSpace { get; set; } = ParticleSimulationSpace.World;
    public string MeshParticleAsset { get; set; } = "";
    public double TrailDuration { get; set; } = 0.45;
    public double TrailWidth { get; set; } = 1.0;
    public double RibbonMaxSegmentLength { get; set; } = 2.0;
    public double VelocityStretch { get; set; } = 0.12;
    public double BeamEndX { get; set; }
    public double BeamEndY { get; set; } = 3.0;
    public double BeamEndZ { get; set; }
    public double BeamNoise { get; set; } = 0.08;
    public ParticleBoundsMode BoundsMode { get; set; } = ParticleBoundsMode.Automatic;
    public double BoundsCenterX { get; set; }
    public double BoundsCenterY { get; set; }
    public double BoundsCenterZ { get; set; }
    public double BoundsSizeX { get; set; } = 8.0;
    public double BoundsSizeY { get; set; } = 8.0;
    public double BoundsSizeZ { get; set; } = 8.0;

    // ── Collision ────────────────────────────────────────────────────────────

    public ParticleCollisionMode CollisionMode { get; set; }
    public double CollisionPlaneHeight { get; set; }
    public double CollisionBounce { get; set; } = 0.45;
    public bool CollideWithTerrain { get; set; } = true;
    public bool CollideWithGeometry { get; set; } = true;

    // ── World placement ──────────────────────────────────────────────────────

    /// <summary>
    /// When true, the simulation recentres new spawns under the camera's XZ position every
    /// frame (see <see cref="Genesis.Runtime.Particles.ParticleSimulation.UpdateCameraPosition"/>).
    /// Not exposed as an editor toggle — it's an inherent property of sky precipitation presets
    /// (Rain, Snow) so weather always follows the player by default, with no per-instance choice.
    /// </summary>
    public bool FollowCameraXZ { get; set; } = false;

    /// <summary>Editor sandbox: orthographic 2D preview with sprite backdrop.</summary>
    public bool Preview2D { get; set; }

    /// <summary>Sprite resource shown behind particles in <see cref="Preview2D"/> mode.</summary>
    public string BackdropSprite { get; set; } = "Player";
    public ParticlePreviewTargetType PreviewTargetType { get; set; }
    public string PreviewTargetAsset { get; set; } = "";

    // ── Meta ─────────────────────────────────────────────────────────────────

    public string Notes { get; set; } = "";

    /// <summary>Optional PGSL script attached to this particle asset. Authoring is wired through the
    /// unified code editor; runtime execution of attached particle scripts is pending (see ToDo.md).</summary>
    public string Script { get; set; } = "";

    public ParticleConfig Clone() => Clone(includeEmitters: true);

    public ParticleConfig CloneEmitter() => Clone(includeEmitters: false);

    internal ParticleConfig Clone(bool includeEmitters) => new()
    {
        EffectName        = EffectName,
        EmitterId         = EmitterId,
        EmitterName       = EmitterName,
        EmitterEnabled    = EmitterEnabled,
        Duration          = Duration,
        Emitters          = includeEmitters ? Emitters?.Select(emitter => emitter.Clone()).ToList() ?? [] : [],
        EventLinks        = includeEmitters ? EventLinks?.Select(link => link.Clone()).ToList() ?? [] : [],
        Light             = Light?.Clone() ?? new ParticleLightConfig(),
        MaxParticles       = MaxParticles,
        EmitRate           = EmitRate,
        BurstCount         = BurstCount,
        Loop               = Loop,
        Shape              = Shape,
        SpreadDegrees      = SpreadDegrees,
        EmitRadius         = EmitRadius,
        BoxSizeX           = BoxSizeX,
        BoxSizeY           = BoxSizeY,
        BoxSizeZ           = BoxSizeZ,
        MeshSurfaceAsset   = MeshSurfaceAsset,
        Speed              = Speed,
        SpeedVariance      = SpeedVariance,
        Gravity            = Gravity,
        GravityX           = GravityX,
        GravityZ           = GravityZ,
        Drag               = Drag,
        WindX              = WindX,
        WindZ              = WindZ,
        TurbulenceStrength = TurbulenceStrength,
        DownwardEmit       = DownwardEmit,
        Lifetime           = Lifetime,
        LifetimeVariance   = LifetimeVariance,
        StartSize          = StartSize,
        EndSize            = EndSize,
        SizeCurve          = SizeCurve,
        Emissive           = Emissive,
        BlendMode          = BlendMode,
        StartColor         = StartColor?.Clone() ?? new ParticleColor(),
        MidColor           = MidColor?.Clone(),
        ColorMidpoint      = ColorMidpoint,
        EndColor           = EndColor?.Clone() ?? new ParticleColor(),
        AlphaCurve         = AlphaCurve,
        UseCustomSizeCurve = UseCustomSizeCurve,
        UseCustomSpeedCurve = UseCustomSpeedCurve,
        UseCustomAlphaCurve = UseCustomAlphaCurve,
        UseCustomVelocityCurve = UseCustomVelocityCurve,
        SizeOverLifetime   = SizeOverLifetime?.Clone() ?? new ParticleBezierCurve(),
        SpeedOverLifetime  = SpeedOverLifetime?.Clone() ?? new ParticleBezierCurve(),
        AlphaOverLifetime  = AlphaOverLifetime?.Clone() ?? new ParticleBezierCurve(),
        VelocityOverLifetime = VelocityOverLifetime?.Clone() ?? new ParticleBezierCurve(),
        GradientStops      = GradientStops?.Select(stop => stop.Clone()).ToList() ?? [],
        ColorJitter        = ColorJitter,
        RotationSpeed      = RotationSpeed,
        RotationVariance   = RotationVariance,
        SizeXScale         = SizeXScale,
        SizeYScale         = SizeYScale,
        TexturePath        = TexturePath,
        UseFlipbook        = UseFlipbook,
        FlipbookColumns    = FlipbookColumns,
        FlipbookRows       = FlipbookRows,
        FlipbookFps        = FlipbookFps,
        Alignment          = Alignment,
        RendererKind       = RendererKind,
        SimulationSpace    = SimulationSpace,
        MeshParticleAsset  = MeshParticleAsset,
        TrailDuration      = TrailDuration,
        TrailWidth         = TrailWidth,
        RibbonMaxSegmentLength = RibbonMaxSegmentLength,
        VelocityStretch    = VelocityStretch,
        BeamEndX           = BeamEndX,
        BeamEndY           = BeamEndY,
        BeamEndZ           = BeamEndZ,
        BeamNoise          = BeamNoise,
        BoundsMode         = BoundsMode,
        BoundsCenterX      = BoundsCenterX,
        BoundsCenterY      = BoundsCenterY,
        BoundsCenterZ      = BoundsCenterZ,
        BoundsSizeX        = BoundsSizeX,
        BoundsSizeY        = BoundsSizeY,
        BoundsSizeZ        = BoundsSizeZ,
        CollisionMode      = CollisionMode,
        CollisionPlaneHeight = CollisionPlaneHeight,
        CollisionBounce    = CollisionBounce,
        CollideWithTerrain = CollideWithTerrain,
        CollideWithGeometry = CollideWithGeometry,
        FollowCameraXZ     = FollowCameraXZ,
        Preview2D          = Preview2D,
        BackdropSprite     = BackdropSprite,
        PreviewTargetType  = PreviewTargetType,
        PreviewTargetAsset = PreviewTargetAsset,
        Notes              = Notes,
        Script             = Script,
    };
}

using System.Globalization;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Core.Editing.Particles;

/// <summary>The four stages of a particle's life, in the order the module stack shows them.</summary>
public enum ParticleModuleGroup
{
    Spawn,
    Initialise,
    Update,
    Render,
}

public enum ParticleModuleFieldKind
{
    Number,
    Toggle,
    Choice,
    Colour,
    Asset,
    /// <summary>A constant, or a random value between two numbers picked for each particle at birth.</summary>
    Range,
    /// <summary>A curve over the particle's life (<see cref="ParticleModuleCurves"/>).</summary>
    Curve,
    /// <summary>The colour and opacity gradient over the particle's life.</summary>
    Gradient,
}

/// <summary>A constant (equal ends) or a random value between two numbers, picked for each particle at birth.</summary>
public readonly record struct ParticleValueRange(double Minimum, double Maximum)
{
    public bool IsConstant => Math.Abs(Maximum - Minimum) < 1e-9;
}

/// <summary>What a module reads and writes: the selected emitter, the effect it belongs to, and the preview's dimension.</summary>
public readonly record struct ParticleModuleContext(ParticleConfig Emitter, ParticleConfig Effect, bool Is2D)
{
    public static ParticleModuleContext For(ParticleConfig emitter) => new(emitter, emitter, emitter.Preview2D);
}

/// <summary>One field of a module, bound to the emitter's runtime settings.</summary>
public sealed class ParticleModuleField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required ParticleModuleFieldKind Kind { get; init; }
    public string Hint { get; init; } = "";
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public int Decimals { get; init; } = 2;
    /// <summary>Changes the editor's preview only; a game never reads it.</summary>
    public bool PreviewOnly { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    public ResourceKind AssetKind { get; init; } = ResourceKind.Unknown;
    public ParticleCurveChannel Channel { get; init; }
    public Func<ParticleModuleContext, bool> Visible { get; init; } = _ => true;
    public required Func<ParticleModuleContext, object?> Read { get; init; }
    public required Action<ParticleModuleContext, object?> Write { get; init; }
}

/// <summary>
/// One module of the stack: a named part of an emitter (Gravity, Colour over life, Trail...) and
/// the runtime settings it owns. The stack only lists what the runtime honours, so every module
/// shown changes the effect a game plays.
/// </summary>
public sealed class ParticleModuleDefinition
{
    public required string Id { get; init; }
    public required ParticleModuleGroup Group { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    /// <summary>Every emitter has it (a lifetime, a shape, a start size): it cannot be switched off or removed.</summary>
    public bool Required { get; init; }
    /// <summary>The settings this module keeps while it is switched off and sets back on Reset.</summary>
    public IReadOnlyList<string> Owns { get; init; } = [];
    /// <summary>Modules the runtime cannot play at the same time as this one; switching it on switches them off.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public Func<ParticleModuleContext, bool> Available { get; init; } = _ => true;
    public IReadOnlyList<ParticleModuleField> Fields { get; init; } = [];
    /// <summary>Its switched-off values depend on another module's (the end size follows the start size).</summary>
    internal bool FollowsOthers { get; init; }
    internal Action<ParticleConfig>? SwitchOffValues { get; init; }
    internal Action<ParticleConfig>? SwitchOnValues { get; init; }
    internal Action<ParticleConfig>? StarterValues { get; init; }
    internal Func<ParticleConfig, bool>? IsOnCheck { get; init; }

    public ParticleModuleField Field(string key) => Fields.FirstOrDefault(field =>
        string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"The {Title} module has no field '{key}'.", nameof(key));
}

/// <summary>The modules the runtime honours, by group. Soft particles, sorting and attraction to a point are not here: the runtime has no such settings.</summary>
public static class ParticleModuleCatalog
{
    public static IReadOnlyList<ParticleModuleDefinition> All { get; } = Build();

    public static ParticleModuleDefinition Find(string id) => All.FirstOrDefault(module =>
        string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There is no particle module '{id}'.", nameof(id));

    public static string GroupTitle(ParticleModuleGroup group) => group switch
    {
        ParticleModuleGroup.Spawn => "Spawn",
        ParticleModuleGroup.Initialise => "Initialise",
        ParticleModuleGroup.Update => "Update",
        _ => "Render",
    };

    private static List<ParticleModuleDefinition> Build() =>
    [
        // ── Spawn ────────────────────────────────────────────────────────────────
        new()
        {
            Id = "spawn.emitter", Group = ParticleModuleGroup.Spawn, Title = "Emitter", Required = true,
            Summary = "How many particles can be alive at once, and whether they move with their Object.",
            Owns = ["MaxParticles", "SimulationSpace"],
            Fields =
            [
                Number("maxParticles", "Most alive", 1, 100_000, 0, c => c.MaxParticles, (c, v) => c.MaxParticles = (int)Math.Round(v)),
                Choice("space", "Particles stay in", [(ParticleSimulationSpace.World, "The world"), (ParticleSimulationSpace.Local, "The Object (move with it)")],
                    c => c.SimulationSpace, (c, v) => c.SimulationSpace = v),
                new()
                {
                    Key = "previewLength", Label = "Preview length (s)", Kind = ParticleModuleFieldKind.Number,
                    Minimum = 0.05, Maximum = 3600, Decimals = 2, PreviewOnly = true,
                    Hint = "How much of the effect the timeline scrubs. Preview only: a game plays the effect for as long as its Object lives.",
                    Read = context => context.Effect.Duration,
                    Write = (context, value) => context.Effect.Duration = Math.Clamp(ToNumber(value), 0.05, 3600),
                },
            ],
        },
        new()
        {
            Id = "spawn.rate", Group = ParticleModuleGroup.Spawn, Title = "Spawn rate",
            Summary = "Particles born every second while the effect plays. The runtime plays a rate or a burst: switching this on switches Burst off.",
            Owns = ["EmitRate"], Excludes = ["spawn.burst"],
            IsOnCheck = c => c.Loop,
            SwitchOffValues = c => c.Loop = false,
            SwitchOnValues = c => c.Loop = true,
            StarterValues = c => { if (c.EmitRate <= 0) c.EmitRate = 60; },
            Fields = [Number("rate", "Per second", 0, 5000, 0, c => c.EmitRate, (c, v) => c.EmitRate = v)],
        },
        new()
        {
            Id = "spawn.burst", Group = ParticleModuleGroup.Spawn, Title = "Burst",
            Summary = "Particles born all at once when the effect starts or restarts. Switching this on switches Spawn rate off.",
            Owns = ["BurstCount"], Excludes = ["spawn.rate"],
            IsOnCheck = c => !c.Loop && c.BurstCount > 0,
            SwitchOffValues = c => c.BurstCount = 0,
            SwitchOnValues = c => c.Loop = false,
            StarterValues = c => { if (c.BurstCount <= 0) c.BurstCount = 150; },
            Fields = [Number("count", "Particles", 1, 100_000, 0, c => c.BurstCount, (c, v) => c.BurstCount = (int)Math.Round(v))],
        },

        // ── Initialise ───────────────────────────────────────────────────────────
        new()
        {
            Id = "init.lifetime", Group = ParticleModuleGroup.Initialise, Title = "Lifetime", Required = true,
            Summary = "How long each particle lives.",
            Owns = ["Lifetime", "LifetimeVariance"],
            Fields = [Range("life", "Seconds", 0.01, 3600, 0.99, c => c.Lifetime, c => c.LifetimeVariance,
                (c, centre, variance) => { c.Lifetime = centre; c.LifetimeVariance = variance; })],
        },
        new()
        {
            Id = "init.speed", Group = ParticleModuleGroup.Initialise, Title = "Start speed",
            Summary = "How fast each particle is launched, in units a second.",
            Owns = ["Speed", "SpeedVariance"],
            IsOnCheck = c => c.Speed > 0,
            SwitchOffValues = c => { c.Speed = 0; c.SpeedVariance = 0; },
            StarterValues = c => { c.Speed = 4; c.SpeedVariance = 0.35; },
            Fields = [Range("speed", "Units / s", 0, 100, 1, c => c.Speed, c => c.SpeedVariance,
                (c, centre, variance) => { c.Speed = centre; c.SpeedVariance = variance; })],
        },
        new()
        {
            Id = "init.direction", Group = ParticleModuleGroup.Initialise, Title = "Direction", Required = true,
            Summary = "Which way particles are launched from the shape.",
            Owns = ["SpreadDegrees", "DownwardEmit"],
            Fields =
            [
                Number("spread", "Spread (°)", 0, 360, 1, c => c.SpreadDegrees, (c, v) => c.SpreadDegrees = v,
                    context => context.Emitter.Shape is ParticleEmitShape.Cone or ParticleEmitShape.Disc or ParticleEmitShape.Box,
                    "How far from straight up a particle may be launched."),
                Number("swirl", "Inward ↔ outward (°)", 0, 360, 1, c => c.SpreadDegrees, (c, v) => c.SpreadDegrees = v,
                    context => context.Emitter.Shape == ParticleEmitShape.Ring,
                    "0 swirls inward, 180 runs along the ring, 360 flies outward."),
                Toggle("downward", "Launch downward", c => c.DownwardEmit, (c, v) => c.DownwardEmit = v),
            ],
        },
        new()
        {
            Id = "init.size", Group = ParticleModuleGroup.Initialise, Title = "Start size", Required = true,
            Summary = "Each particle's size at birth.",
            Owns = ["StartSize", "SizeXScale", "SizeYScale"],
            Fields =
            [
                Number("size", "Size", 0.01, 20, 2, c => c.StartSize, (c, v) => c.StartSize = v),
                Number("width", "Width ×", 0.01, 100, 2, c => c.SizeXScale, (c, v) => c.SizeXScale = v),
                Number("height", "Height ×", 0.01, 100, 2, c => c.SizeYScale, (c, v) => c.SizeYScale = v),
            ],
        },
        new()
        {
            Id = "init.colour", Group = ParticleModuleGroup.Initialise, Title = "Start colour", Required = true,
            Summary = "Each particle's colour at birth (the first key of Colour over life).",
            Owns = ["StartColor", "ColorJitter"],
            // Reset gives the start colour its default; the gradient's first key is the same colour.
            SwitchOnValues = c => SetStartColour(c, c.StartColor ?? new ParticleColor()),
            Fields =
            [
                new()
                {
                    Key = "colour", Label = "Colour", Kind = ParticleModuleFieldKind.Colour,
                    Read = context => FirstColour(context.Emitter).Clone(),
                    Write = (context, value) => SetStartColour(context.Emitter, ToColour(value)),
                },
                Number("tint", "Random tint", 0, 1, 2, c => c.ColorJitter, (c, v) => c.ColorJitter = v,
                    hint: "Shifts each particle's colour a little, at random."),
            ],
        },
        new()
        {
            Id = "init.rotation", Group = ParticleModuleGroup.Initialise, Title = "Rotation",
            Summary = "Each particle's start angle and spin.",
            Owns = ["RotationSpeed", "RotationVariance"],
            SwitchOffValues = c => { c.RotationSpeed = 0; c.RotationVariance = 0; },
            StarterValues = c => { c.RotationSpeed = 90; c.RotationVariance = 1; },
            Fields =
            [
                Number("spin", "Spin (°/s)", -720, 720, 1, c => c.RotationSpeed, (c, v) => c.RotationSpeed = v),
                Number("angle", "Start angle ± (°)", 0, 720, 1, c => c.RotationVariance * 180, (c, v) => c.RotationVariance = v / 180),
            ],
        },
        new()
        {
            Id = "init.shape", Group = ParticleModuleGroup.Initialise, Title = "Shape", Required = true,
            Summary = "Where particles are born.",
            Owns = ["Shape", "EmitRadius", "BoxSizeX", "BoxSizeY", "BoxSizeZ", "MeshSurfaceAsset"],
            Fields =
            [
                Choice("shape", "Shape",
                [
                    (ParticleEmitShape.Point, "Point"), (ParticleEmitShape.Cone, "Cone"), (ParticleEmitShape.Sphere, "Sphere"),
                    (ParticleEmitShape.Disc, "Circle"), (ParticleEmitShape.Ring, "Ring"), (ParticleEmitShape.Box, "Box"),
                    (ParticleEmitShape.MeshSurface, "Mesh surface"),
                ], c => c.Shape, (c, v) => c.Shape = v),
                Number("radius", "Radius", 0, 1000, 2, c => c.EmitRadius, (c, v) => c.EmitRadius = v,
                    context => context.Emitter.Shape is ParticleEmitShape.Disc or ParticleEmitShape.Ring),
                Number("boxX", "Width", 0.01, 1000, 2, c => c.BoxSizeX, (c, v) => c.BoxSizeX = v,
                    context => context.Emitter.Shape == ParticleEmitShape.Box),
                Number("boxY", "Height", 0.01, 1000, 2, c => c.BoxSizeY, (c, v) => c.BoxSizeY = v,
                    context => context.Emitter.Shape == ParticleEmitShape.Box),
                Number("boxZ", "Depth", 0.01, 1000, 2, c => c.BoxSizeZ, (c, v) => c.BoxSizeZ = v,
                    context => context.Emitter.Shape == ParticleEmitShape.Box && !context.Is2D),
                Asset("mesh", "Model", ResourceKind.Model, c => c.MeshSurfaceAsset, (c, v) => c.MeshSurfaceAsset = v,
                    context => context.Emitter.Shape == ParticleEmitShape.MeshSurface),
            ],
        },

        // ── Update ───────────────────────────────────────────────────────────────
        new()
        {
            Id = "update.gravity", Group = ParticleModuleGroup.Update, Title = "Gravity",
            Summary = "A constant pull. Positive lifts particles; negative pulls them down.",
            Owns = ["Gravity", "GravityX", "GravityZ"],
            SwitchOffValues = c => { c.Gravity = 0; c.GravityX = 0; c.GravityZ = 0; },
            StarterValues = c => c.Gravity = -4,
            Fields =
            [
                Number("up", "Up / down", -100, 100, 2, c => c.Gravity, (c, v) => c.Gravity = v),
                Number("x", "Sideways X", -100, 100, 2, c => c.GravityX, (c, v) => c.GravityX = v),
                Number("z", "Sideways Z", -100, 100, 2, c => c.GravityZ, (c, v) => c.GravityZ = v, context => !context.Is2D),
            ],
        },
        new()
        {
            Id = "update.drag", Group = ParticleModuleGroup.Update, Title = "Drag",
            Summary = "Slows particles down, a little more each second.",
            Owns = ["Drag"],
            SwitchOffValues = c => c.Drag = 0,
            StarterValues = c => c.Drag = 0.8,
            Fields = [Number("drag", "Per second", 0, 20, 2, c => c.Drag, (c, v) => c.Drag = v)],
        },
        new()
        {
            Id = "update.turbulence", Group = ParticleModuleGroup.Update, Title = "Turbulence",
            Summary = "Random nudges that make particles wander.",
            Owns = ["TurbulenceStrength"],
            SwitchOffValues = c => c.TurbulenceStrength = 0,
            StarterValues = c => c.TurbulenceStrength = 1.5,
            Fields = [Number("strength", "Strength", 0, 20, 2, c => c.TurbulenceStrength, (c, v) => c.TurbulenceStrength = v)],
        },
        new()
        {
            Id = "update.wind", Group = ParticleModuleGroup.Update, Title = "Wind",
            Summary = "A steady drift, in units a second.",
            Owns = ["WindX", "WindZ"],
            SwitchOffValues = c => { c.WindX = 0; c.WindZ = 0; },
            StarterValues = c => c.WindX = 1,
            Fields =
            [
                Number("x", "Across X", -100, 100, 2, c => c.WindX, (c, v) => c.WindX = v),
                Number("z", "Across Z", -100, 100, 2, c => c.WindZ, (c, v) => c.WindZ = v, context => !context.Is2D),
            ],
        },
        new()
        {
            Id = "update.colour", Group = ParticleModuleGroup.Update, Title = "Colour over life",
            Summary = "Colour and opacity from birth to death. Off, particles keep their start colour.",
            Owns = ["GradientStops", "EndColor", "MidColor", "ColorMidpoint", "AlphaCurve", "UseCustomAlphaCurve", "AlphaOverLifetime"],
            FollowsOthers = true,
            IsOnCheck = GradientVaries,
            SwitchOffValues = c => CollapseGradient(c, FirstColour(c)),
            StarterValues = c =>
            {
                ParticleColor start = FirstColour(c);
                ParticleColor end = new(start.R, start.G, start.B, 0f);
                c.GradientStops = [new() { Position = 0, Color = start.Clone() }, new() { Position = 1, Color = end }];
                c.EndColor = end.Clone(); c.MidColor = null;
            },
            Fields =
            [
                new()
                {
                    Key = "gradient", Label = "Colour", Kind = ParticleModuleFieldKind.Gradient,
                    Hint = "Drag a key along the strip · double-click: add or recolour · right-click: remove · wheel: opacity",
                    Read = context => context.Emitter.GradientStops,
                    Write = (context, value) => SetGradient(context.Emitter, value),
                },
                Curve("fade", "Opacity timing", ParticleCurveChannel.Alpha),
            ],
        },
        new()
        {
            Id = "update.size", Group = ParticleModuleGroup.Update, Title = "Size over life",
            Summary = "Grows or shrinks each particle from its start size to the end size. Off, particles keep their start size.",
            Owns = ["EndSize", "SizeCurve", "UseCustomSizeCurve", "SizeOverLifetime"],
            FollowsOthers = true,
            IsOnCheck = c => Math.Abs(c.EndSize - c.StartSize) > 1e-6,
            SwitchOffValues = c => { c.EndSize = c.StartSize; c.SizeCurve = ParticleInterpolationCurve.Linear; c.UseCustomSizeCurve = false; },
            StarterValues = c => c.EndSize = Math.Round(Math.Max(0, c.StartSize) * 0.2, 3),
            Fields =
            [
                Number("end", "End size", 0, 20, 2, c => c.EndSize, (c, v) => c.EndSize = v),
                Curve("curve", "Start → end", ParticleCurveChannel.Size),
            ],
        },
        new()
        {
            Id = "update.speed", Group = ParticleModuleGroup.Update, Title = "Speed over life",
            Summary = "Multiplies each particle's speed through its life.",
            Owns = ["UseCustomSpeedCurve", "SpeedOverLifetime"],
            IsOnCheck = c => c.UseCustomSpeedCurve,
            SwitchOffValues = c => c.UseCustomSpeedCurve = false,
            SwitchOnValues = c => c.UseCustomSpeedCurve = true,
            StarterValues = c => c.SpeedOverLifetime = new ParticleBezierCurve
            {
                Keys = [new() { Time = 0, Value = 1, Smooth = true }, new() { Time = 1, Value = 0.25 }],
            },
            Fields = [Curve("curve", "× speed", ParticleCurveChannel.Speed)],
        },
        new()
        {
            Id = "update.velocity", Group = ParticleModuleGroup.Update, Title = "Velocity over life",
            Summary = "Multiplies how far each particle moves each moment of its life.",
            Owns = ["UseCustomVelocityCurve", "VelocityOverLifetime"],
            IsOnCheck = c => c.UseCustomVelocityCurve,
            SwitchOffValues = c => c.UseCustomVelocityCurve = false,
            SwitchOnValues = c => c.UseCustomVelocityCurve = true,
            StarterValues = c => c.VelocityOverLifetime = new ParticleBezierCurve
            {
                Keys = [new() { Time = 0, Value = 1 }, new() { Time = 0.6, Value = 1, Smooth = true }, new() { Time = 1, Value = 0 }],
            },
            Fields = [Curve("curve", "× movement", ParticleCurveChannel.Velocity)],
        },
        new()
        {
            Id = "update.collision", Group = ParticleModuleGroup.Update, Title = "Floor collision",
            Summary = "What a particle does when it reaches the floor height (or a terrain or model under it, in 3D).",
            Owns = ["CollisionMode", "CollisionPlaneHeight", "CollisionBounce", "CollideWithTerrain", "CollideWithGeometry"],
            IsOnCheck = c => c.CollisionMode != ParticleCollisionMode.None,
            SwitchOffValues = c => c.CollisionMode = ParticleCollisionMode.None,
            SwitchOnValues = c => { if (c.CollisionMode == ParticleCollisionMode.None) c.CollisionMode = ParticleCollisionMode.Bounce; },
            Fields =
            [
                Choice("response", "On contact", [(ParticleCollisionMode.Bounce, "Bounce"), (ParticleCollisionMode.Die, "Die"), (ParticleCollisionMode.Stick, "Stick")],
                    c => c.CollisionMode, (c, v) => c.CollisionMode = v),
                Number("height", "Floor height", -10_000, 10_000, 2, c => c.CollisionPlaneHeight, (c, v) => c.CollisionPlaneHeight = v),
                Number("bounce", "Bounciness", 0, 1.5, 2, c => c.CollisionBounce, (c, v) => c.CollisionBounce = v,
                    context => context.Emitter.CollisionMode == ParticleCollisionMode.Bounce),
                Toggle("terrain", "Terrain", c => c.CollideWithTerrain, (c, v) => c.CollideWithTerrain = v, context => !context.Is2D),
                Toggle("geometry", "Models", c => c.CollideWithGeometry, (c, v) => c.CollideWithGeometry = v, context => !context.Is2D),
            ],
        },

        // ── Render ───────────────────────────────────────────────────────────────
        new()
        {
            Id = "render.sprite", Group = ParticleModuleGroup.Render, Title = "Sprite", Required = true,
            Summary = "How each particle is drawn over what is behind it.",
            Owns = ["BlendMode", "Emissive"],
            Fields =
            [
                Choice("blend", "Blend", [(ParticleBlendMode.Alpha, "Alpha"), (ParticleBlendMode.Additive, "Additive (glow)"), (ParticleBlendMode.Multiply, "Multiply (darken)")],
                    c => c.BlendMode, (c, v) => c.BlendMode = v),
                Number("glow", "Brightness", 0, 20, 2, c => c.Emissive, (c, v) => c.Emissive = v, context => !context.Is2D,
                    "How brightly the particle lights itself in a 3D scene."),
                Choice("facing", "Faces", [(ParticleAlignment.Billboard, "The camera"), (ParticleAlignment.Horizontal, "Up (lies flat)")],
                    c => c.Alignment, (c, v) => c.Alignment = v,
                    context => context.Emitter.RendererKind == ParticleRendererKind.Billboard
                        && context.Emitter.Alignment is ParticleAlignment.Billboard or ParticleAlignment.Horizontal),
            ],
        },
        new()
        {
            Id = "render.image", Group = ParticleModuleGroup.Render, Title = "Image",
            Summary = "A picture from the Image library on each particle. Off, particles are soft round dots.",
            Owns = ["TexturePath", "PixelSampling"],
            IsOnCheck = c => !string.IsNullOrWhiteSpace(c.TexturePath),
            SwitchOffValues = c => { c.TexturePath = string.Empty; c.PixelSampling = false; },
            Fields =
            [
                Asset("image", "Picture", ResourceKind.Image, c => c.TexturePath, (c, v) => c.TexturePath = v),
                Toggle("pixel", "Pixel sampling", c => c.PixelSampling, (c, v) => c.PixelSampling = v,
                    hint: "Sample the picture pixel by pixel: crisp pixel art."),
            ],
        },
        new()
        {
            Id = "render.flipbook", Group = ParticleModuleGroup.Render, Title = "Flipbook",
            Summary = "Plays the picture as a grid of animation frames over each particle's life.",
            Owns = ["UseFlipbook", "FlipbookColumns", "FlipbookRows", "FlipbookFps", "FlipbookRandomStart"],
            Available = context => context.Emitter.RendererKind == ParticleRendererKind.Billboard,
            IsOnCheck = c => c.UseFlipbook,
            SwitchOffValues = c => c.UseFlipbook = false,
            SwitchOnValues = c => c.UseFlipbook = true,
            Fields =
            [
                Number("columns", "Columns", 1, 64, 0, c => c.FlipbookColumns, (c, v) => c.FlipbookColumns = (int)Math.Round(v)),
                Number("rows", "Rows", 1, 64, 0, c => c.FlipbookRows, (c, v) => c.FlipbookRows = (int)Math.Round(v)),
                Number("fps", "Frames / s", 0, 240, 1, c => c.FlipbookFps, (c, v) => c.FlipbookFps = v),
                Toggle("random", "Random first frame", c => c.FlipbookRandomStart, (c, v) => c.FlipbookRandomStart = v),
            ],
        },
        new()
        {
            Id = "render.stretch", Group = ParticleModuleGroup.Render, Title = "Stretch by velocity",
            Summary = "Points each particle along its motion and stretches it with its speed: sparks, rain.",
            Owns = ["VelocityStretch"], Excludes = ["render.mesh"],
            Available = context => context.Emitter.RendererKind == ParticleRendererKind.Billboard,
            IsOnCheck = c => c.Alignment == ParticleAlignment.Velocity,
            SwitchOffValues = c => { if (c.Alignment == ParticleAlignment.Velocity) c.Alignment = ParticleAlignment.Billboard; },
            SwitchOnValues = c => c.Alignment = ParticleAlignment.Velocity,
            StarterValues = c => { if (c.VelocityStretch <= 0) c.VelocityStretch = 0.12; },
            Fields = [Number("stretch", "Stretch", 0, 100, 2, c => c.VelocityStretch, (c, v) => c.VelocityStretch = v)],
        },
        new()
        {
            Id = "render.mesh", Group = ParticleModuleGroup.Render, Title = "Mesh particles",
            Summary = "Draws each particle as a Model instead of a sprite: debris, leaves, coins.",
            Owns = ["MeshParticleAsset"], Excludes = ["render.stretch"],
            Available = context => !context.Is2D && context.Emitter.RendererKind == ParticleRendererKind.Billboard,
            IsOnCheck = c => c.Alignment == ParticleAlignment.Mesh3D,
            SwitchOffValues = c => { if (c.Alignment == ParticleAlignment.Mesh3D) c.Alignment = ParticleAlignment.Billboard; },
            SwitchOnValues = c => c.Alignment = ParticleAlignment.Mesh3D,
            Fields = [Asset("mesh", "Model", ResourceKind.Model, c => c.MeshParticleAsset, (c, v) => c.MeshParticleAsset = v)],
        },
        new()
        {
            Id = "render.trail", Group = ParticleModuleGroup.Render, Title = "Trail",
            Summary = "Each particle leaves a fading strip behind it. The runtime draws a trail, a ribbon or a beam, one at a time.",
            Owns = ["TrailDuration", "TrailWidth"], Excludes = ["render.ribbon", "render.beam"],
            IsOnCheck = c => c.RendererKind == ParticleRendererKind.Trail,
            SwitchOffValues = c => { if (c.RendererKind == ParticleRendererKind.Trail) c.RendererKind = ParticleRendererKind.Billboard; },
            SwitchOnValues = c => c.RendererKind = ParticleRendererKind.Trail,
            Fields =
            [
                Number("length", "Length (s)", 0.01, 60, 2, c => c.TrailDuration, (c, v) => c.TrailDuration = v),
                Number("width", "Width × size", 0.01, 100, 2, c => c.TrailWidth, (c, v) => c.TrailWidth = v),
            ],
        },
        new()
        {
            Id = "render.ribbon", Group = ParticleModuleGroup.Render, Title = "Ribbon",
            Summary = "Joins the live particles, oldest to newest, into one strip.",
            Owns = ["RibbonMaxSegmentLength", "TrailWidth"], Excludes = ["render.trail", "render.beam"],
            IsOnCheck = c => c.RendererKind == ParticleRendererKind.Ribbon,
            SwitchOffValues = c => { if (c.RendererKind == ParticleRendererKind.Ribbon) c.RendererKind = ParticleRendererKind.Billboard; },
            SwitchOnValues = c => c.RendererKind = ParticleRendererKind.Ribbon,
            Fields =
            [
                Number("width", "Width × size", 0.01, 100, 2, c => c.TrailWidth, (c, v) => c.TrailWidth = v),
                Number("segment", "Longest join", 0.01, 1000, 2, c => c.RibbonMaxSegmentLength, (c, v) => c.RibbonMaxSegmentLength = v),
            ],
        },
        new()
        {
            Id = "render.beam", Group = ParticleModuleGroup.Render, Title = "Beam",
            Summary = "Joins each particle to an end point set from its Object: lasers, lightning.",
            Owns = ["BeamEndX", "BeamEndY", "BeamEndZ", "BeamNoise", "TrailWidth"], Excludes = ["render.trail", "render.ribbon"],
            IsOnCheck = c => c.RendererKind == ParticleRendererKind.Beam,
            SwitchOffValues = c => { if (c.RendererKind == ParticleRendererKind.Beam) c.RendererKind = ParticleRendererKind.Billboard; },
            SwitchOnValues = c => c.RendererKind = ParticleRendererKind.Beam,
            Fields =
            [
                Number("endX", "End X", -10_000, 10_000, 2, c => c.BeamEndX, (c, v) => c.BeamEndX = v),
                Number("endY", "End Y", -10_000, 10_000, 2, c => c.BeamEndY, (c, v) => c.BeamEndY = v),
                Number("endZ", "End Z", -10_000, 10_000, 2, c => c.BeamEndZ, (c, v) => c.BeamEndZ = v, context => !context.Is2D),
                Number("noise", "Jitter", 0, 1000, 2, c => c.BeamNoise, (c, v) => c.BeamNoise = v),
                Number("width", "Width × size", 0.01, 100, 2, c => c.TrailWidth, (c, v) => c.TrailWidth = v),
            ],
        },
    ];

    // ── Field builders ──────────────────────────────────────────────────────────

    private static readonly Func<ParticleModuleContext, bool> Always = _ => true;

    private static ParticleModuleField Number(string key, string label, double minimum, double maximum, int decimals,
        Func<ParticleConfig, double> get, Action<ParticleConfig, double> set,
        Func<ParticleModuleContext, bool>? visible = null, string hint = "") => new()
    {
        Key = key, Label = label, Kind = ParticleModuleFieldKind.Number, Minimum = minimum, Maximum = maximum,
        Decimals = decimals, Hint = hint, Visible = visible ?? Always,
        Read = context => get(context.Emitter),
        Write = (context, value) => set(context.Emitter, Math.Clamp(ToNumber(value), minimum, maximum)),
    };

    private static ParticleModuleField Toggle(string key, string label, Func<ParticleConfig, bool> get, Action<ParticleConfig, bool> set,
        Func<ParticleModuleContext, bool>? visible = null, string hint = "") => new()
    {
        Key = key, Label = label, Kind = ParticleModuleFieldKind.Toggle, Hint = hint, Visible = visible ?? Always,
        Read = context => get(context.Emitter),
        Write = (context, value) => set(context.Emitter, value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out bool parsed) => parsed,
            _ => throw new FormatException("Expected true or false."),
        }),
    };

    private static ParticleModuleField Choice<T>(string key, string label, (T Value, string Label)[] items,
        Func<ParticleConfig, T> get, Action<ParticleConfig, T> set, Func<ParticleModuleContext, bool>? visible = null)
        where T : struct, Enum => new()
    {
        Key = key, Label = label, Kind = ParticleModuleFieldKind.Choice, Visible = visible ?? Always,
        Choices = items.Select(item => item.Label).ToArray(),
        Read = context =>
        {
            T current = get(context.Emitter);
            return items.FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, current)).Label ?? current.ToString();
        },
        Write = (context, value) =>
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            foreach ((T item, string itemLabel) in items)
                if (string.Equals(itemLabel, text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    set(context.Emitter, item);
                    return;
                }
            throw new FormatException($"{label} cannot be '{text}'. Choose {string.Join(", ", items.Select(item => item.Label))}.");
        },
    };

    private static ParticleModuleField Asset(string key, string label, ResourceKind kind, Func<ParticleConfig, string> get,
        Action<ParticleConfig, string> set, Func<ParticleModuleContext, bool>? visible = null) => new()
    {
        Key = key, Label = label, Kind = ParticleModuleFieldKind.Asset, AssetKind = kind, Visible = visible ?? Always,
        Read = context => get(context.Emitter) ?? string.Empty,
        Write = (context, value) =>
        {
            string reference = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
            if (reference.Contains('/') || reference.Contains('\\'))
                throw new FormatException("Name the resource, not a file path.");
            set(context.Emitter, reference);
        },
    };

    private static ParticleModuleField Range(string key, string label, double minimum, double maximum, double largestVariance,
        Func<ParticleConfig, double> centre, Func<ParticleConfig, double> variance, Action<ParticleConfig, double, double> set) => new()
    {
        Key = key, Label = label, Kind = ParticleModuleFieldKind.Range, Minimum = minimum, Maximum = maximum, Decimals = 2,
        Hint = "A constant, or a random value between two numbers picked for each particle.",
        Read = context =>
        {
            double middle = centre(context.Emitter);
            double spread = Math.Clamp(variance(context.Emitter), 0, largestVariance);
            return new ParticleValueRange(Math.Max(minimum, middle * (1 - spread)), Math.Min(maximum, middle * (1 + spread)));
        },
        Write = (context, value) =>
        {
            ParticleValueRange range = value switch
            {
                ParticleValueRange given => given,
                _ => new ParticleValueRange(ToNumber(value), ToNumber(value)),
            };
            if (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum)) throw new FormatException("Enter finite numbers.");
            double low = Math.Clamp(Math.Min(range.Minimum, range.Maximum), minimum, maximum);
            double high = Math.Clamp(Math.Max(range.Minimum, range.Maximum), minimum, maximum);
            double middle = (low + high) * 0.5;
            // The runtime picks centre × (1 ± variance): the two ends are where that lands.
            double spread = middle <= 1e-9 ? 0 : Math.Clamp((high - low) / (high + low), 0, largestVariance);
            set(context.Emitter, middle, spread);
        },
    };

    private static ParticleModuleField Curve(string key, string label, ParticleCurveChannel channel) => new()
    {
        Key = key, Label = label, Kind = ParticleModuleFieldKind.Curve, Channel = channel,
        Hint = ParticleModuleCurves.Describe(channel),
        Choices = ParticleModuleCurves.ModesFor(channel),
        Read = context => ParticleModuleCurves.Mode(context.Emitter, channel),
        Write = (context, value) => ParticleModuleCurves.SetMode(context.Emitter, channel,
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    // ── Values shared between modules ───────────────────────────────────────────

    internal static double ToNumber(object? value)
    {
        double number = value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string text => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };
        return double.IsFinite(number) ? number : throw new FormatException("Enter a finite number.");
    }

    private static ParticleColor ToColour(object? value)
    {
        if (value is not ParticleColor colour) throw new FormatException("Expected a particle colour.");
        if (!float.IsFinite(colour.R) || !float.IsFinite(colour.G) || !float.IsFinite(colour.B) || !float.IsFinite(colour.A))
            throw new FormatException("Colour channels must be finite.");
        return new ParticleColor(Math.Clamp(colour.R, 0f, 1f), Math.Clamp(colour.G, 0f, 1f), Math.Clamp(colour.B, 0f, 1f), Math.Clamp(colour.A, 0f, 1f));
    }

    /// <summary>The colour a particle is born with: the first gradient key, as the runtime reads it.</summary>
    internal static ParticleColor FirstColour(ParticleConfig config)
    {
        ParticleGradientStop? first = null;
        foreach (ParticleGradientStop stop in config.GradientStops ?? [])
            if (stop?.Color is not null && (first is null || stop.Position < first.Position)) first = stop;
        return config.GradientStops is { Count: >= 2 } && first is not null
            ? first.Color
            : config.StartColor ?? new ParticleColor();
    }

    private static void SetStartColour(ParticleConfig config, ParticleColor colour)
    {
        config.StartColor = colour.Clone();
        ParticleGradientStop? first = null;
        foreach (ParticleGradientStop stop in config.GradientStops ?? [])
            if (stop?.Color is not null && (first is null || stop.Position < first.Position)) first = stop;
        if (first is not null) first.Color = colour.Clone();
    }

    private static void CollapseGradient(ParticleConfig config, ParticleColor colour)
    {
        ParticleColor start = colour.Clone();
        config.StartColor = start.Clone();
        config.GradientStops = [new() { Position = 0, Color = start.Clone() }, new() { Position = 1, Color = start.Clone() }];
        config.EndColor = start.Clone();
        config.MidColor = null;
        config.ColorMidpoint = 0.5;
        config.AlphaCurve = ParticleInterpolationCurve.Linear;
        config.UseCustomAlphaCurve = false;
    }

    private static bool GradientVaries(ParticleConfig config)
    {
        IEnumerable<ParticleColor> colours = config.GradientStops is { Count: >= 2 }
            ? config.GradientStops.Where(stop => stop?.Color is not null).Select(stop => stop.Color)
            : new[] { config.StartColor, config.MidColor, config.EndColor }.Where(colour => colour is not null)!;
        ParticleColor? first = null;
        foreach (ParticleColor colour in colours)
        {
            if (first is null) { first = colour; continue; }
            if (Math.Abs(colour.R - first.R) > 1e-4 || Math.Abs(colour.G - first.G) > 1e-4
                || Math.Abs(colour.B - first.B) > 1e-4 || Math.Abs(colour.A - first.A) > 1e-4) return true;
        }
        return false;
    }

    private static void SetGradient(ParticleConfig config, object? value)
    {
        if (value is not IEnumerable<ParticleGradientStop> stops) throw new FormatException("Expected gradient keys.");
        List<ParticleGradientStop> copy = stops.Where(stop => stop?.Color is not null).Select(stop => stop.Clone()).ToList();
        if (copy.Count is < 2 or > 128) throw new FormatException("A gradient needs 2 to 128 keys.");
        foreach (ParticleGradientStop stop in copy)
        {
            if (!double.IsFinite(stop.Position)) throw new FormatException("Gradient key positions must be finite.");
            stop.Position = Math.Clamp(stop.Position, 0, 1);
            stop.Color = ToColour(stop.Color);
        }
        config.GradientStops = copy.OrderBy(stop => stop.Position).ToList();
        config.EndColor = config.GradientStops[^1].Color.Clone();
    }
}

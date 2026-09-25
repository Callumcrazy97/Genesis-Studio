using System.Collections.Generic;

namespace Genesis.Runtime.Particles;

/// <summary>
/// Carefully-tuned ready-made effects.  Each preset exercises different combinations of
/// emission shape, blend mode, rotation, turbulence, and gradient stops so the editor
/// opens with something striking and the library covers every major visual need.
/// </summary>
public static class ParticlePresets
{
    public static readonly string[] Names =
    {
        "Campfire Vortex",
        "Fire",         // hot rising flame
        "Smoke",        // soft grey plume
        "Embers",       // campfire sparks — additive arcs
        "Sparks",       // brighter velocity-aligned ember streaks
        "Explosion",    // one-shot blast
        "Magic",        // omni-directional glowing motes
        "Fountain",     // ballistic water jet
        "Waterfall Mist",
        "Waterfall Ripple",
        "Snow",         // gentle falling flakes
        "Rain",         // heavy downpour streaks
        "Portal",       // swirling additive ring
        "Comet",        // fast bright trail
        "DustMotes",    // cheap atmospheric dust, catches light shafts
    };

    public static ParticleConfig Create(string name) => name switch
    {
        "Campfire Vortex" => CampfireVortex(),
        "Smoke"     => Smoke(),
        "Embers"    => Embers(),
        "Sparks"    => Sparks(),
        "Explosion" => Explosion(),
        "Magic"     => Magic(),
        "Fountain"  => Fountain(),
        "Waterfall Mist" => WaterfallMist(),
        "Waterfall Ripple" => WaterfallRipple(),
        "Snow"      => Snow(),
        "Rain"      => Rain(),
        "Portal"    => Portal(),
        "Comet"     => Comet(),
        "DustMotes" => DustMotes(),
        _           => Fire(),
    };

    /// <summary>A complete layered campfire effect: flame core, smoke plume, sparks and light.</summary>
    public static ParticleConfig CampfireVortex()
    {
        ParticleConfig flame = Fire();
        flame.EffectName = "Campfire Vortex";
        flame.EmitterName = "Flame Core";
        flame.Duration = 3.0;
        flame.Light = new ParticleLightConfig
        {
            Enabled = true,
            Color = new ParticleColor(1f, 0.42f, 0.08f, 1f),
            Radius = 8.5,
            Intensity = 3.1,
            Falloff = 2.0,
            OffsetY = 0.85,
            FlickerAmount = 0.24,
            FlickerFrequency = 8.5,
        };

        ParticleConfig smoke = Smoke();
        smoke.EmitterName = "Smoke Plume";
        smoke.EmitRate = 52;
        smoke.StartSize = 0.32;
        smoke.EndSize = 1.65;
        smoke.Gravity = 0.7;

        ParticleConfig sparks = Embers();
        sparks.EmitterName = "Flying Sparks";
        sparks.EmitRate = 44;
        sparks.StartSize = 0.08;
        sparks.EndSize = 0.015;
        sparks.Alignment = ParticleAlignment.Velocity;
        sparks.SizeYScale = 2.4;

        flame.Emitters =
        [
            new ParticleEmitterLayer { Name = "Smoke Plume", Config = smoke },
            new ParticleEmitterLayer { Name = "Flying Sparks", Config = sparks },
        ];
        flame.Notes = "Layered fire, smoke and velocity-aligned sparks with a warm flickering point light.";
        return flame;
    }

    // ── Fire ──────────────────────────────────────────────────────────────────

    public static ParticleConfig Fire() => new()
    {
        MaxParticles  = 2400,
        EmitRate      = 220,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 18,
        EmitRadius    = 0.35,
        Speed         = 3.0,
        SpeedVariance = 0.45,
        Gravity       = 2.2,
        Drag          = 0.65,
        TurbulenceStrength = 0.6,
        Lifetime      = 1.1,
        LifetimeVariance = 0.3,
        StartSize     = 0.6,
        EndSize       = 0.05,
        Emissive      = 2.8,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(1.0f, 0.97f, 0.55f, 0.95f),  // hot white-yellow core
        MidColor      = new(1.0f, 0.50f, 0.08f, 0.75f),  // orange mid
        ColorMidpoint = 0.45,
        EndColor      = new(0.7f, 0.10f, 0.02f, 0.0f),   // dark red fade
        RotationSpeed = 0,
        RotationVariance = 0,
        Notes = "Campfire flame: hot disc source, additive blend, 3-stop gradient white→orange→red.",
    };

    // ── Smoke ─────────────────────────────────────────────────────────────────

    public static ParticleConfig Smoke() => new()
    {
        MaxParticles  = 800,
        EmitRate      = 45,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 22,
        EmitRadius    = 0.25,
        Speed         = 1.4,
        SpeedVariance = 0.45,
        Gravity       = 0.45,
        Drag          = 0.88,
        WindX         = 0.15,
        TurbulenceStrength = 0.25,
        Lifetime      = 3.2,
        LifetimeVariance = 0.35,
        StartSize     = 0.4,
        EndSize       = 2.0,
        Emissive      = 0.0,
        BlendMode     = ParticleBlendMode.Alpha,
        StartColor    = new(0.60f, 0.62f, 0.66f, 0.55f),
        MidColor      = new(0.35f, 0.37f, 0.40f, 0.30f),
        ColorMidpoint = 0.55,
        EndColor      = new(0.18f, 0.19f, 0.22f, 0.0f),
        RotationSpeed     = 8,
        RotationVariance  = 1.0,
        Notes = "Rising smoke plume: swelling alpha quad, slow rotation, slight wind drift.",
    };

    // ── Embers ────────────────────────────────────────────────────────────────

    public static ParticleConfig Embers() => new()
    {
        MaxParticles  = 600,
        EmitRate      = 35,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 60,
        EmitRadius    = 0.4,
        Speed         = 5.5,
        SpeedVariance = 0.65,
        Gravity       = -5.5,                // pulled back down in arcs
        Drag          = 0.12,
        TurbulenceStrength = 0.3,
        Lifetime      = 1.4,
        LifetimeVariance = 0.5,
        StartSize     = 0.18,
        EndSize       = 0.03,
        Emissive      = 4.0,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(1.0f, 0.96f, 0.75f, 1.0f),  // white-hot
        MidColor      = new(1.0f, 0.55f, 0.05f, 0.85f), // orange
        ColorMidpoint = 0.4,
        EndColor      = new(0.5f, 0.05f, 0.0f, 0.0f),   // dim red → gone
        RotationSpeed     = 45,
        RotationVariance  = 1.0,
        Notes = "Campfire ember sparks: additive, arcing trajectories, white-hot to dim red.",
    };

    public static ParticleConfig Sparks()
    {
        ParticleConfig sparks = Embers();
        sparks.EmitterName = "Sparks";
        sparks.Alignment = ParticleAlignment.Velocity;
        sparks.SizeXScale = 0.55;
        sparks.SizeYScale = 2.8;
        sparks.EmitRate = 52;
        return sparks;
    }

    // ── Explosion ─────────────────────────────────────────────────────────────

    public static ParticleConfig Explosion() => new()
    {
        MaxParticles  = 1400,
        EmitRate      = 0,
        BurstCount    = 900,
        Loop          = false,
        Shape         = ParticleEmitShape.Sphere,
        SpreadDegrees = 360,
        Speed         = 10.0,
        SpeedVariance = 0.55,
        Gravity       = -6.0,
        Drag          = 1.6,
        TurbulenceStrength = 0.5,
        Lifetime      = 1.3,
        LifetimeVariance = 0.4,
        StartSize     = 0.6,
        EndSize       = 0.02,
        Emissive      = 3.5,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(1.0f, 0.98f, 0.80f, 1.0f),
        MidColor      = new(1.0f, 0.45f, 0.05f, 0.9f),
        ColorMidpoint = 0.35,
        EndColor      = new(0.6f, 0.08f, 0.01f, 0.0f),
        RotationSpeed     = 0,
        RotationVariance  = 0,
        Notes = "One-shot blast. Press Burst (or Restart) to re-fire.",
    };

    // ── Magic ─────────────────────────────────────────────────────────────────

    public static ParticleConfig Magic() => new()
    {
        MaxParticles  = 2600,
        EmitRate      = 180,
        Loop          = true,
        Shape         = ParticleEmitShape.Sphere,
        SpreadDegrees = 360,
        Speed         = 2.2,
        SpeedVariance = 0.6,
        Gravity       = -0.2,
        Drag          = 0.4,
        TurbulenceStrength = 0.15,
        Lifetime      = 1.7,
        LifetimeVariance = 0.4,
        StartSize     = 0.24,
        EndSize       = 0.02,
        Emissive      = 3.0,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(0.40f, 0.95f, 1.0f, 1.0f),
        MidColor      = new(0.80f, 0.40f, 1.0f, 0.8f),
        ColorMidpoint = 0.55,
        EndColor      = new(0.55f, 0.10f, 0.90f, 0.0f),
        RotationSpeed    = 20,
        RotationVariance = 1.0,
        Notes = "Omni-directional glowing motes: cyan → violet, additive.",
    };

    // ── Fountain ─────────────────────────────────────────────────────────────

    public static ParticleConfig Fountain() => new()
    {
        MaxParticles  = 3000,
        EmitRate      = 260,
        Loop          = true,
        Shape         = ParticleEmitShape.Cone,
        SpreadDegrees = 9,
        Speed         = 7.5,
        SpeedVariance = 0.2,
        Gravity       = -9.0,
        Drag          = 0.02,
        Lifetime      = 2.0,
        LifetimeVariance = 0.15,
        StartSize     = 0.18,
        EndSize       = 0.10,
        Emissive      = 1.1,
        BlendMode     = ParticleBlendMode.Alpha,
        StartColor    = new(0.65f, 0.85f, 1.0f, 1.0f),
        EndColor      = new(0.20f, 0.45f, 0.95f, 0.0f),
        Notes = "Ballistic water jet: fast narrow cone, strong gravity.",
    };

    /// <summary>Generic waterfall impact spray. Terrain rivers instantiate this through a built-in preset URI.</summary>
    public static ParticleConfig WaterfallMist() => new()
    {
        EffectName = "Waterfall Mist",
        EmitterName = "Impact Mist",
        MaxParticles = 640,
        EmitRate = 72,
        Loop = true,
        Shape = ParticleEmitShape.Disc,
        EmitRadius = .55,
        SpreadDegrees = 78,
        Speed = 1.25,
        SpeedVariance = .65,
        Gravity = .45,
        Drag = .8,
        TurbulenceStrength = .35,
        Lifetime = 1.8,
        LifetimeVariance = .35,
        StartSize = .18,
        EndSize = 1.35,
        BlendMode = ParticleBlendMode.Alpha,
        StartColor = new(.88f, .96f, 1f, .58f),
        MidColor = new(.78f, .9f, .98f, .3f),
        ColorMidpoint = .42,
        EndColor = new(.72f, .84f, .92f, 0f),
        RotationSpeed = 14,
        RotationVariance = 1,
        Notes = "Reusable white-water mist emitted at a waterfall impact hook.",
    };

    /// <summary>Generic expanding, horizontal water-impact rings.</summary>
    public static ParticleConfig WaterfallRipple() => new()
    {
        EffectName = "Waterfall Ripple",
        EmitterName = "Impact Ripples",
        MaxParticles = 96,
        EmitRate = 5,
        Loop = true,
        Shape = ParticleEmitShape.Disc,
        EmitRadius = .18,
        SpreadDegrees = 4,
        Speed = .08,
        SpeedVariance = .2,
        Gravity = 0,
        Drag = 1.5,
        Lifetime = 1.4,
        LifetimeVariance = .25,
        StartSize = .25,
        EndSize = 2.8,
        SizeYScale = .12,
        Alignment = ParticleAlignment.Horizontal,
        BlendMode = ParticleBlendMode.Alpha,
        StartColor = new(.88f, .97f, 1f, .7f),
        MidColor = new(.68f, .88f, 1f, .3f),
        ColorMidpoint = .35,
        EndColor = new(.62f, .82f, .95f, 0f),
        Notes = "Reusable expanding surface ripple emitted at a waterfall impact hook.",
    };

    // ── Snow ──────────────────────────────────────────────────────────────────

    public static ParticleConfig Snow() => new()
    {
        MaxParticles  = 1800,
        EmitRate      = 120,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 5,
        EmitRadius    = 5.0,
        Speed         = 0.4,
        SpeedVariance = 0.5,
        Gravity       = -0.55,   // falls downward
        Drag          = 0.35,
        WindX         = 0.4,
        TurbulenceStrength = 0.08,
        Lifetime      = 8.0,
        LifetimeVariance = 0.3,
        StartSize     = 0.14,
        EndSize       = 0.12,
        Emissive      = 0.3,
        BlendMode     = ParticleBlendMode.Alpha,
        StartColor    = new(0.92f, 0.95f, 1.0f, 0.0f),  // fade in
        MidColor      = new(0.95f, 0.97f, 1.0f, 0.9f),
        ColorMidpoint = 0.15,
        EndColor      = new(0.80f, 0.85f, 0.95f, 0.0f),
        RotationSpeed     = 4,
        RotationVariance  = 1.0,
        FollowCameraXZ = true,
        Notes = "Gentle snowfall: large disc spawn, slow drift, fade-in and fade-out. Follows the camera XZ by default.",
    };

    // ── Rain ──────────────────────────────────────────────────────────────────

    public static ParticleConfig RainDrizzle() => ScaleRain(Rain(), 0.45f, 0.55f, 0.35f, 0.45f);
    public static ParticleConfig Rain() => RainCore(1f);
    public static ParticleConfig RainHeavy() => ScaleRain(RainCore(1f), 1.35f, 1.25f, 1.15f, 1.2f);

    private static ParticleConfig RainCore(float vis) => new()
    {
        MaxParticles  = 1400,
        EmitRate      = 480 * vis,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 8,
        EmitRadius    = 14.0,
        Speed         = 9.0,
        SpeedVariance = 0.18,
        Gravity       = -6.0,
        Drag          = 0.03,
        WindX         = 0.9,
        DownwardEmit  = true,
        Lifetime      = 1.35,
        LifetimeVariance = 0.15,
        StartSize     = 0.10,
        EndSize       = 0.08,
        SizeXScale    = 0.42,
        SizeYScale    = 3.4,
        Emissive      = 0.22,
        BlendMode     = ParticleBlendMode.Alpha,
        StartColor    = new(0.72f, 0.80f, 0.94f, 0.15f),
        MidColor      = new(0.74f, 0.82f, 0.96f, 0.82f),
        ColorMidpoint = 0.12,
        EndColor      = new(0.68f, 0.76f, 0.92f, 0.05f),
        FollowCameraXZ = true,
        Notes = "Visible rain streaks aligned to fall direction (not camera billboards).",
    };

    private static ParticleConfig ScaleRain(ParticleConfig c, float rate, float count, float sx, float sy)
    {
        c.EmitRate *= rate;
        c.MaxParticles = (int)(c.MaxParticles * count);
        c.SizeXScale *= sx;
        c.SizeYScale *= sy;
        return c;
    }

    /// <summary>Soft mist sheet — visible when looking horizontally.</summary>
    public static ParticleConfig RainMist() => new()
    {
        MaxParticles  = 220,
        EmitRate      = 90,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 18,
        EmitRadius    = 11.0,
        Speed         = 1.2,
        SpeedVariance = 0.35,
        Gravity       = -1.5,
        Drag          = 0.25,
        WindX         = 0.5,
        DownwardEmit  = true,
        Lifetime      = 2.2,
        LifetimeVariance = 0.3,
        StartSize     = 0.55,
        EndSize       = 0.35,
        SizeXScale    = 1.4,
        SizeYScale    = 0.35,
        Emissive      = 0.08,
        BlendMode     = ParticleBlendMode.Alpha,
        StartColor    = new(0.78f, 0.84f, 0.95f, 0.0f),
        MidColor      = new(0.80f, 0.86f, 0.96f, 0.22f),
        ColorMidpoint = 0.25,
        EndColor      = new(0.75f, 0.82f, 0.94f, 0.0f),
        FollowCameraXZ = true,
    };

    // ── Portal ────────────────────────────────────────────────────────────────

    public static ParticleConfig Portal() => new()
    {
        MaxParticles  = 2200,
        EmitRate      = 200,
        Loop          = true,
        Shape         = ParticleEmitShape.Ring,
        SpreadDegrees = 90,      // balanced swirl
        EmitRadius    = 1.8,
        Speed         = 2.0,
        SpeedVariance = 0.5,
        Gravity       = 0.0,
        Drag          = 0.5,
        TurbulenceStrength = 0.1,
        Lifetime      = 1.5,
        LifetimeVariance = 0.4,
        StartSize     = 0.22,
        EndSize       = 0.04,
        Emissive      = 3.5,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(0.20f, 0.80f, 1.0f, 1.0f),
        MidColor      = new(0.80f, 0.30f, 1.0f, 0.9f),
        ColorMidpoint = 0.45,
        EndColor      = new(0.40f, 0.05f, 0.80f, 0.0f),
        RotationSpeed     = 60,
        RotationVariance  = 1.0,
        Notes = "Swirling magic portal: ring spawn, teal→violet additive gradient.",
    };

    // ── Comet ─────────────────────────────────────────────────────────────────

    public static ParticleConfig Comet() => new()
    {
        MaxParticles  = 800,
        EmitRate      = 180,
        Loop          = true,
        Shape         = ParticleEmitShape.Point,
        SpreadDegrees = 8,
        Speed         = 0.3,
        SpeedVariance = 0.5,
        Gravity       = -0.15,
        Drag          = 2.2,
        TurbulenceStrength = 0.05,
        WindX         = -6.0,    // comet screaming sideways
        Lifetime      = 0.5,
        LifetimeVariance = 0.3,
        StartSize     = 0.30,
        EndSize       = 0.02,
        Emissive      = 4.5,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(1.0f, 0.98f, 0.92f, 1.0f),
        MidColor      = new(0.60f, 0.85f, 1.0f, 0.8f),
        ColorMidpoint = 0.3,
        EndColor      = new(0.25f, 0.40f, 1.0f, 0.0f),
        Notes = "Fast-moving comet trail: short-lived bright streaks fading to blue.",
    };

    // ── Dust Motes ────────────────────────────────────────────────────────────

    public static ParticleConfig DustMotes() => new()
    {
        MaxParticles  = 500,
        EmitRate      = 18,
        Loop          = true,
        Shape         = ParticleEmitShape.Disc,
        SpreadDegrees = 90,
        EmitRadius    = 6.0,
        Speed         = 0.12,
        SpeedVariance = 0.8,
        Gravity       = -0.02,
        Drag          = 0.25,
        TurbulenceStrength = 0.18,
        Lifetime      = 9.0,
        LifetimeVariance = 0.4,
        StartSize     = 0.025,
        EndSize       = 0.02,
        Emissive      = 0.6,
        BlendMode     = ParticleBlendMode.Additive,
        StartColor    = new(1.0f, 0.95f, 0.80f, 0.0f),
        MidColor      = new(1.0f, 0.92f, 0.70f, 0.35f),
        ColorMidpoint = 0.15,
        EndColor      = new(1.0f, 0.90f, 0.65f, 0.0f),
        RotationSpeed     = 2,
        RotationVariance  = 1.0,
        Notes = "Cheap atmospheric dust: tiny slow additive motes drifting in turbulence — reads as floating dust catching light shafts when combined with shadow-aware volumetric fog.",
    };

    public static IReadOnlyList<string> All => Names;
}

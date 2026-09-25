using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Single source of truth tying background colour, hemisphere ambient, and fog
    /// colour together (terrain-and-rendering-fix-plan.md, Issue 4/5).
    ///
    /// Previously the background was a flat clear colour, ambient was a near-zero
    /// scalar (0.03) unrelated to the sky, and fog had its own independent colour.
    /// That let any one of the three drift from the others — e.g. changing the
    /// background tinted lit geometry only via the fog lerp, and shadowed faces with
    /// near-zero ambient read as pure black ("underwater" look / the terrain "skull").
    ///
    /// A <see cref="SkyModel"/> derives background, fog colour, and a two-term
    /// (sky/ground) hemisphere ambient from one set of horizon/zenith/ground colours,
    /// so all three always look like they belong to the same sky.
    /// </summary>
    public readonly struct SkyModel
    {
        /// Sky colour directly overhead — feeds the "from above" ambient hemisphere term.
        public readonly Vector3 ZenithColor;
        /// Sky colour at the horizon — feeds the background clear colour and the
        /// default fog colour (so fog reads as aerial perspective, not a repaint).
        public readonly Vector3 HorizonColor;
        /// Bounced/ground light colour — feeds the "from below" ambient hemisphere term.
        public readonly Vector3 GroundColor;
        /// Overall ambient brightness multiplier. Replaces the old flat 0.03 scalar;
        /// high enough that shadowed faces are dim, not black.
        public readonly float AmbientIntensity;

        public SkyModel(Vector3 zenithColor, Vector3 horizonColor, Vector3 groundColor, float ambientIntensity)
        {
            ZenithColor = zenithColor;
            HorizonColor = horizonColor;
            GroundColor = groundColor;
            AmbientIntensity = ambientIntensity;
        }

        public static SkyModel Default => new SkyModel(
            zenithColor: new Vector3(0.32f, 0.45f, 0.66f),
            horizonColor: new Vector3(0.55f, 0.62f, 0.72f),
            groundColor: new Vector3(0.20f, 0.18f, 0.15f),
            ambientIntensity: 0.35f);

        /// Hemisphere ambient term lit by the sky (used when a surface normal points up).
        public Vector3 AmbientSky => ZenithColor * AmbientIntensity;

        /// Hemisphere ambient term lit by ground bounce (used when a normal points down).
        public Vector3 AmbientGround => GroundColor * AmbientIntensity;

        /// Background clear colour — the horizon, since that's what most of the screen
        /// reads as in an outdoor scene.
        public Vector4 BackgroundColor => new Vector4(HorizonColor, 1f);

        /// Default fog colour — also the horizon, so turning on fog reads as "more sky
        /// between you and distant objects" rather than an arbitrary repaint.
        public Vector4 FogColor => new Vector4(HorizonColor, 1f);

        /// Returns a copy scaled to a different overall ambient intensity (e.g. for a
        /// UI brightness slider) while keeping the same hue relationships.
        public SkyModel WithAmbientIntensity(float intensity) =>
            new SkyModel(ZenithColor, HorizonColor, GroundColor, intensity);
    }
}

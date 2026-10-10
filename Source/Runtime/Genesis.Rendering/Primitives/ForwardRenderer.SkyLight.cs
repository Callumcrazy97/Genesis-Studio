using System;
using System.Diagnostics;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Hemisphere sky light (request 64a). In the default zenith mode the ambient terms are the
    // state's own colours, exactly as before. In hemisphere mode they are the light of the sky dome
    // the composite draws and of the sunlit ground (SkyLightMath), recomputed only when the sky,
    // the light or the settings change, and the forward shader's environment reflection mirrors the
    // drawn sky instead of the ambient colours.
    internal sealed partial class ForwardRenderer
    {
        private readonly record struct SkyLightKey(
            SkyDome Dome, float Strength, Vector3 Tint, float Saturation);

        private SkyLightKey _skyLightKey;
        private SkyLightTerms _skyLightTerms;
        private bool _skyLightValid;

        /// <summary>CPU milliseconds the last sky light recomputation took (0 until one has run).</summary>
        public double LastSkyLightMs { get; private set; }

        /// <summary>The sky light terms in use (hemisphere mode), for tests and the debug screen.</summary>
        internal SkyLightTerms CurrentSkyLight => _skyLightTerms;

        /// <summary>
        /// The ambient terms for EngineCB, in the lighting colour space, and the sky light's
        /// constants: x of <paramref name="skyParams"/> is 1 in hemisphere mode and 0 otherwise.
        /// </summary>
        private void ResolveSkyLight(in Mesh3DState s, out Vector3 ambientSky, out Vector3 ambientGround,
            out Vector4 skyParams, out Vector4 mirrorZenith, out Vector4 mirrorHorizon, out Vector4 mirrorGround)
        {
            if (s.SkyLightMode != SkyLightModes.Hemisphere)
            {
                ambientSky = ToLinearColor(s.AmbientColor);
                ambientGround = ToLinearColor(s.AmbientGroundColor);
                skyParams = mirrorZenith = mirrorHorizon = mirrorGround = Vector4.Zero;
                return;
            }

            Vector3 tint = s.SkyLightTint == Vector3.Zero ? Vector3.One : s.SkyLightTint;
            var key = new SkyLightKey(
                SkyDomeFor(s),
                float.IsFinite(s.SkyLightStrength) ? s.SkyLightStrength : SkyLightDefaults.Strength,
                tint,
                float.IsFinite(s.SkyLightSaturation) ? s.SkyLightSaturation : SkyLightDefaults.Saturation);
            if (!_skyLightValid || key != _skyLightKey)
            {
                long started = Stopwatch.GetTimestamp();
                _skyLightTerms = SkyLightMath.Compute(key.Dome, key.Strength, key.Tint, key.Saturation);
                LastSkyLightMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _skyLightKey = key;
                _skyLightValid = true;
            }

            ambientSky = _skyLightTerms.Up;
            ambientGround = _skyLightTerms.Down;
            skyParams = new Vector4(1f, 0f, 0f, 0f);
            mirrorZenith = new Vector4(_skyLightTerms.MirrorZenith, 0f);
            mirrorHorizon = new Vector4(_skyLightTerms.MirrorHorizon, 0f);
            mirrorGround = new Vector4(_skyLightTerms.MirrorGround, 0f);
        }

        /// <summary>The sky the composite draws for this state, in the lighting colour space.</summary>
        private SkyDome SkyDomeFor(in Mesh3DState s)
        {
            Vector3 skySun = s.AuthoredSkyEnabled ? s.SkySunDirection : s.LightDirection;
            return new SkyDome(
                ToLinearColor(s.SkyZenithColor),
                ToLinearColor(s.SkyHorizonColor),
                ToLinearColor(s.BackgroundColor),
                s.AuthoredSkyEnabled,
                s.AtmosphereLutEnabled && _atmosphereLutTexture.IsValid
                    && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase),
                AtmosphereLutMath.SunHeightFromLightDirection(skySun),
                -s.LightDirection.Y,
                ToLinearColor(s.SunColor) * s.SunIntensity);
        }
    }
}

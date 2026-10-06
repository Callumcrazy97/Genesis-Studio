using System;
using System.Numerics;
using Genesis.Runtime.Climate;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scene
{
    public static class EnvironmentMapper
    {
        public static Mesh3DState ToMesh3DState(SceneEnvironment env, Camera3D camera, RendererOptions options, bool wireframe, RenderDebugView debug)
        {
            // A host may not supply renderer options (it's an optional knob); fall back to
            // the defaults (frustum + back-face culling on) rather than dereferencing null.
            options ??= new RendererOptions();

            env.GetLighting(out Vector3 lightDir, out Vector4 ambientSky, out Vector4 ambientGround, out float lightingWeight);

            Mesh3DState state = new Mesh3DState
            {
                LightingEnabled       = env.LightingEnabled,
                LightingWeight        = lightingWeight,
                LightDirection        = lightDir,
                SunColor              = new Vector3(env.SunColor.X, env.SunColor.Y, env.SunColor.Z),
                SunIntensity          = env.SunEnabled ? env.SunIntensity : 1f,
                AmbientColor          = new Vector3(ambientSky.X, ambientSky.Y, ambientSky.Z),
                AmbientGroundColor    = new Vector3(ambientGround.X, ambientGround.Y, ambientGround.Z),
                // Previously unmapped — Mesh3DState.BackgroundColor silently defaulted to
                // black for every scene driven through SceneEnvironment, which is one of
                // the contributors to Issue 4 (background tint / inconsistent sky colour).
                BackgroundColor       = new Vector3(env.BackgroundColor.X, env.BackgroundColor.Y, env.BackgroundColor.Z),
                FogEnabled            = env.FogEnabled,
                FogStart              = env.FogStart,
                FogEnd                = env.FogEnd,
                FogColor              = env.FogColor,
                EmissiveIntensity     = env.GlobalEmissive,
                FrustumCullingEnabled = options.FrustumCulling,
                CullBackFaces         = options.FaceCulling,
                CullFrontFaces        = options.FaceCulling && options.CullFrontFaces,
                FrontCounterClockwise = options.FrontCounterClockwise,
                ShadowsEnabled        = env.ShadowsEnabled,
                ShadowStrength        = Math.Clamp(env.ShadowStrength, 0f, 1f),
                ShadowBias            = env.ShadowBias,
                ShadowOrthoSize       = env.ShadowOrthoSize,
                ShadowCascadeCount    = env.ShadowCascadeCount >= 3 || env.ShadowDistance > 0f ? 3 : 2,
                // The cascade is centred a third of its width ahead of the camera, so a width of
                // distance / 0.86 reaches that far forward.
                ShadowFarExtent       = env.ShadowDistance > 0f ? env.ShadowDistance / 0.86f : 0f,
                GtaoEnabled           = env.GtaoEnabled,
                FogDensity            = env.FogDensity,
                FogHeightBase         = env.FogHeightBase,
                FogHeightFalloff      = env.FogHeightFalloff,
                FogAerialBlend        = env.FogAerialBlend,
                FogSunPreserve        = env.FogSunPreserve,
                FogNoiseStrength      = env.FogNoiseStrength,
                FogScreenSpace        = env.FogScreenSpace,
                VolumetricFogEnabled  = env.VolumetricFogEnabled,
                VolumetricFogQuality  = env.VolumetricFogQuality,
                VolumetricTemporalBlend = env.VolumetricTemporalBlend,
                Wireframe             = wireframe,
                CameraFarPlane        = camera.FarPlane,
                DebugView             = debug,
                ShowFloor             = env.ShowFloor,
                FloorColor            = new Vector3(env.FloorColor.X, env.FloorColor.Y, env.FloorColor.Z),
                ShowSunVisual         = env.SunEnabled && env.ShowSunVisual,
                CameraForward         = camera.Forward,
                StylizedLightingEnabled = env.StylizedEnabled,
                StylizedToonSteps       = env.StylizedToonSteps,
                StylizedDiffuseWrap     = env.StylizedDiffuseWrap,
                StylizedSpecularStrength = env.StylizedSpecularStrength,
                StylizedRimStrength     = env.StylizedRimStrength,
                StylizedSaturation      = env.StylizedSaturation,
                EnvironmentReflection   = env.EnvironmentReflection,
            };
            // AF2.6: PGSL / headless authoring bridge (room Climate/Atmosphere overwrite after).
            SkyAuthoringDefaults.Apply(ref state);
            return state;
        }

        /// <summary>
        /// AF2.6: stamp Climate / Atmosphere options onto Mesh3DState so raymarch / celestial
        /// packs match room authoring. When climate/atmosphere are null, leaves SkyAuthoringDefaults
        /// (already applied by <see cref="ToMesh3DState"/>) in place.
        /// </summary>
        /// <summary>
        /// Lights a frame with a lightning strike: the sky whitens and everything under it is lit
        /// from above, shadows and all, for as long as the flash lasts.
        /// </summary>
        public static void ApplyLightningFlash(ref Mesh3DState state, float flash)
        {
            if (!(flash > 0f)) return;
            flash = MathF.Min(flash, 1f);
            Vector3 light = new(0.78f, 0.84f, 1f);
            state.AmbientColor += light * (flash * 1.6f);
            state.SkyHorizonColor = Vector3.Lerp(state.SkyHorizonColor, light, flash * 0.7f);
            state.SkyZenithColor = Vector3.Lerp(state.SkyZenithColor, light * 0.8f, flash * 0.55f);
            state.BackgroundColor = Vector3.Lerp(state.BackgroundColor, light, flash * 0.7f);
        }

        public static void StampClimateAtmosphere(
            ref Mesh3DState state,
            EnvironmentService climate,
            AtmosphereService atmosphere)
        {
            if (climate != null)
            {
                state.DayOfYear = SkyAuthoringDefaults.ClampDayOfYear(climate.Options.DayOfYear);
                state.LatitudeDegrees =
                    SkyAuthoringDefaults.ClampLatitude(climate.Options.LatitudeDegrees);
                EnvironmentFrame frame = climate.Current;
                state.WeatherWindRain = new Vector4(frame.LocalWind.X, frame.LocalWind.Z, frame.LocalRain, 1);
                state.WeatherSurface = new Vector4(frame.GroundWetness, frame.LocalTemperatureC, frame.SnowAccumulation, 0);
                state.AuthoredSkyEnabled = true;
                state.AtmosphereLutEnabled = true;
                state.CelestialExtrasEnabled = true;
                state.SkySunDirection = frame.SunDirection;
                state.SkyTimeOfDayHours = frame.TimeOfDayHours;
                state.SkyElapsedSeconds = (float)frame.ElapsedRealSeconds;
                state.SkyHorizonColor = new Vector3(frame.BackgroundColor.X, frame.BackgroundColor.Y, frame.BackgroundColor.Z);
                state.SkyZenithColor = frame.AmbientSky;
                state.ShowSunVisual = true;
                Vector3 primary = Vector3.Lerp(frame.SunDirection, frame.MoonDirection, frame.NightFactor);
                state.LightDirection = primary.LengthSquared() > 1e-6f ? Vector3.Normalize(primary) : -Vector3.UnitY;
                state.AmbientGroundColor = frame.AmbientGround;
                state.FogColor = frame.FogColor;
            }

            if (atmosphere != null)
            {
                AtmosphereOptions options = atmosphere.Options;
                state.CloudBaseHeight =
                    SkyAuthoringDefaults.ClampBaseHeight(options.CloudBaseHeight);
                state.CloudThickness =
                    SkyAuthoringDefaults.ClampThickness(options.CloudThickness);
                state.CloudCoverageScale =
                    SkyAuthoringDefaults.ClampCoverageScale(options.CloudCoverageScale);
                state.CloudDensityScale =
                    SkyAuthoringDefaults.ClampDensityScale(options.CloudDensityScale);
                state.SunDiscScale = options.SunDiscScale > 0f ? Math.Clamp(options.SunDiscScale, 0.25f, 8f) : 1f;
                state.HideSunDisc = options.HideSunDisc;
                state.HideMoonDisc = options.HideMoonDisc;
                // The dynamic sky draws its own sun (disc and glow) in the sky composite: hide that too.
                if (options.HideSunDisc) state.ShowSunVisual = false;
                if (climate != null)
                {
                    AtmosphereFrame frame = atmosphere.Current;
                    state.RaymarchedCloudsEnabled = options.VolumetricClouds;
                    state.CloudTemporalEnabled = options.VolumetricClouds;
                    state.CloudQuality = Math.Clamp(options.CloudQuality, 0, 3);
                    state.SunIntensity = MathF.Max(0.03f, frame.SunIntensity * 1.15f);
                    state.SunColor = frame.SunColor;
                    state.BackgroundColor = frame.HorizonColor;
                    state.SkyHorizonColor = frame.HorizonColor;
                    state.SkyZenithColor = frame.ZenithColor;
                    // The room's ambient intensity scales the sky's own light; it used to be ignored here.
                    float ambientScale = float.IsFinite(options.AmbientScale) ? Math.Clamp(options.AmbientScale, 0f, 8f) : 1f;
                    state.AmbientColor = frame.ZenithColor * (0.38f * ambientScale);
                    state.AmbientGroundColor *= ambientScale;
                    float weatherFog = options.WeatherFog(climate.Current.Weather.FogDensity);
                    state.FogEnabled = frame.Haze > 0.02f || weatherFog > 0.01f;
                    state.FogDensity = MathF.Max(0.002f, weatherFog + frame.Haze * 0.012f);
                    if (options.VisibilityMetres > 0f)
                    {
                        // A stated visibility replaces the short-range density: exp(-3 * d / visibility)
                        // leaves 5% contrast at the visibility distance. Weather still thickens it.
                        state.FogEnabled = true;
                        state.FogDensity = 3f / MathF.Max(50f, options.VisibilityMetres) + weatherFog;
                        // Air thins over hundreds of metres, not the few a ground mist does, so
                        // mountain tops stay clearer than the valleys below them.
                        state.FogHeightFalloff = 1f / 1200f;
                        state.FogAerialBlend = 0.75f;
                        state.FogHorizonReduction = 1f;
                    }
                }
            }
        }
    }
}

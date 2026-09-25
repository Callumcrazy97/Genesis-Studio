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
                ShadowCascadeCount    = env.ShadowCascadeCount >= 3 ? 3 : 2,
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
                    state.AmbientColor = frame.ZenithColor * 0.38f;
                    state.FogEnabled = frame.Haze > 0.02f || climate.Current.Weather.FogDensity > 0.01f;
                    state.FogDensity = MathF.Max(0.002f, climate.Current.Weather.FogDensity + frame.Haze * 0.012f);
                }
            }
        }
    }
}

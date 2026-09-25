using System;
using System.Numerics;
using Genesis.Runtime.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scene
{
    public sealed class SceneEnvironment
    {
        // Defaults to the shared sky model's horizon colour so background, ambient,
        // and fog start out visually consistent (terrain-and-rendering-fix-plan.md
        // Issue 4/5) instead of three independently-authored colours.
        public Vector4 BackgroundColor { get; set; } = SkyModel.Default.BackgroundColor;

        public bool LightingEnabled { get; set; } = true;

        public bool  SunEnabled { get; set; } = true;
        public bool  ShowSunVisual { get; set; } = true;
        public float SunYawDegrees { get; set; } = 135f;
        public float SunPitchDegrees { get; set; } = 42f;
        public float SunIntensity { get; set; } = 1.1f;
        public Vector4 SunColor { get; set; } = new Vector4(1f, 0.95f, 0.82f, 1f);
        public float SunAmbientStrength { get; set; } = 0.35f;

        public Vector4 AmbientColor { get; set; } = new Vector4(SkyModel.Default.AmbientSky, 1f);
        /// <summary>PGSL/Engine override applied without disabling the authored sun.</summary>
        public Vector3? AmbientOverrideColor { get; set; }
        /// Hemisphere "ground" ambient term (see <see cref="EnvironmentMapper"/>).
        public Vector4 AmbientGroundColor { get; set; } = new Vector4(SkyModel.Default.AmbientGround, 1f);
        public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -0.85f, -0.35f));
        /// <summary>PGSL/Engine direction override applied ahead of authored sun angles.</summary>
        public Vector3? LightDirectionOverride { get; set; }

        public bool  ShadowsEnabled { get; set; } = true;
        public float ShadowStrength { get; set; } = 1f;
        public float ShadowBias { get; set; } = 0.00015f;
        public float ShadowOrthoSize { get; set; } = 120f;
        /// <summary>2 = default CSM; 3 = AF1.1 far cascade.</summary>
        public int ShadowCascadeCount { get; set; } = 2;
        /// <summary>AF1.2 half-res GTAO (optional, default off).</summary>
        public bool GtaoEnabled { get; set; }

        public bool  FogEnabled { get; set; }
        public float FogStart { get; set; } = 35f;
        public float FogEnd { get; set; } = 120f;
        public float FogDensity { get; set; } = 0.012f;
        public float FogHeightBase { get; set; }
        public float FogHeightFalloff { get; set; } = 0.35f;
        public float FogAerialBlend { get; set; } = 0.45f;
        public float FogSunPreserve { get; set; } = 0.85f;
        public float FogNoiseStrength { get; set; } = 0.04f;
        public bool  FogScreenSpace { get; set; }
        public bool  VolumetricFogEnabled { get; set; }
        /// 0=Low, 1=Medium, 2=High.
        public int   VolumetricFogQuality { get; set; } = 1;
        public float VolumetricTemporalBlend { get; set; } = 0.8f;
        public Vector4 FogColor { get; set; } = SkyModel.Default.FogColor;

        public bool  ShowFloor { get; set; } = false;
        public Vector4 FloorColor { get; set; } = new Vector4(0.32f, 0.34f, 0.38f, 1f);

        public float GlobalEmissive { get; set; }

        // ── Stylized / toon lighting (see StylizedLighting presets) ────────────────
        public bool  StylizedEnabled { get; set; }
        public float StylizedToonSteps { get; set; } = 1f;
        public float StylizedDiffuseWrap { get; set; } = 0.5f;
        public float StylizedSpecularStrength { get; set; } = 0.2f;
        public float StylizedRimStrength { get; set; } = 0.18f;
        public float StylizedSaturation { get; set; } = 1f;

        public Vector3 GetSunLightDirection()
        {
            float yaw   = SunYawDegrees * MathUtil.DegToRad;
            float pitch = SunPitchDegrees * MathUtil.DegToRad;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(pitch) * MathF.Sin(yaw),
                -MathF.Sin(pitch),
                MathF.Cos(pitch) * MathF.Cos(yaw)));
        }

        public void GetLighting(out Vector3 lightDirection, out Vector4 ambient, out float lightingWeight)
            => GetLighting(out lightDirection, out ambient, out _, out lightingWeight);

        public void GetLighting(out Vector3 lightDirection, out Vector4 ambientSky, out Vector4 ambientGround, out float lightingWeight)
        {
            lightingWeight = LightingEnabled ? 1f : 0f;
            if (SunEnabled)
            {
                lightDirection = LightDirectionOverride ?? GetSunLightDirection();
                ambientSky = AmbientOverrideColor is Vector3 ambientOverride
                    ? new Vector4(ambientOverride, 1f)
                    : SunColor * SunAmbientStrength;
                ambientGround = AmbientGroundColor;
            }
            else
            {
                lightDirection = LightDirectionOverride ?? Vector3.Normalize(LightDirection);
                ambientSky = AmbientOverrideColor is Vector3 ambientOverride
                    ? new Vector4(ambientOverride, 1f)
                    : AmbientColor;
                ambientGround = AmbientGroundColor;
            }
        }
    }
}

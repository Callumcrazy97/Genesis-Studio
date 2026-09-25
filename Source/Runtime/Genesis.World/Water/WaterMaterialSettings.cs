using System.Numerics;

namespace Genesis.World.Water
{
    /// <summary>CPU-side water material parameters packed for the GPU water shader.</summary>
    public struct WaterMaterialSettings
    {
        public Vector3 ShallowColor;
        public float Opacity;

        public Vector3 DeepColor;
        public float DepthFade;

        /// <summary>UV scroll speed for ripple normal maps.</summary>
        public float FlowSpeed;

        /// <summary>Fresnel exponent (higher = tighter grazing highlight).</summary>
        public float FresnelPower;

        /// <summary>World-units band width for shoreline foam.</summary>
        public float FoamWidth;

        /// <summary>Vertex wave displacement amplitude (lake/ocean).</summary>
        public float WaveAmplitude;

        /// <summary>River flow direction multiplier along spline UV.</summary>
        public float RiverFlowScale;

        public Vector3 SkyHorizonColor;
        public Vector3 SkyZenithColor;

        /// <summary>UV flow direction for river/current normal scroll (normalized by shader).</summary>
        public Vector2 FlowDirection;

        /// <summary>Waterfall / rapid foam boost 0..1.</summary>
        public float RapidsIntensity;

        /// <summary>Ambient temperature 0..1 for CPU tinting (optional).</summary>
        public float TemperatureNorm;

        public static WaterMaterialSettings Default => new()
        {
            ShallowColor = new Vector3(0.18f, 0.62f, 0.72f),
            DeepColor = new Vector3(0.04f, 0.18f, 0.38f),
            Opacity = 0.5f,
            DepthFade = 12f,
            FlowSpeed = 0.35f,
            FresnelPower = 4f,
            FoamWidth = 1.25f,
            WaveAmplitude = 0.08f,
            RiverFlowScale = 1.5f,
            SkyHorizonColor = new Vector3(0.55f, 0.72f, 0.92f),
            SkyZenithColor = new Vector3(0.12f, 0.28f, 0.55f),
        };

        public Vector4 PackShallow() => new(ShallowColor, Opacity);

        public Vector4 PackDeep() => new(DeepColor, DepthFade);

        public Vector4 PackParams() => new(FlowSpeed, FresnelPower, FoamWidth, WaveAmplitude);

        public Vector4 PackSkyHorizon() => new(SkyHorizonColor.X, SkyHorizonColor.Y, FlowDirection.X, FlowDirection.Y);

        public Vector4 PackSkyZenith() => new(SkyZenithColor, RapidsIntensity);
    }
}

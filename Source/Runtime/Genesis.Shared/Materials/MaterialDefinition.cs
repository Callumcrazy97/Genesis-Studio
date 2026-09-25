using System;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json;

namespace Genesis.Shared.Materials
{
    public enum MaterialHeightMode { None, Parallax, ParallaxOcclusion, VertexDisplacement }
    public enum TextureColorSpace { Linear, Srgb }

    public sealed class MaterialMapSet
    {
        [JsonProperty("albedo")] public string Albedo { get; set; }
        [JsonProperty("normal")] public string Normal { get; set; }
        [JsonProperty("height")] public string Height { get; set; }
        [JsonProperty("orm")] public string Orm { get; set; }
        [JsonProperty("emission")] public string Emission { get; set; }
        [JsonProperty("extras")] public string Extras { get; set; }
        [JsonProperty("flow")] public string Flow { get; set; }
    }

    public sealed class MaterialDefinition
    {
        public const int CurrentVersion = 1;
        [JsonProperty("version")] public int Version { get; set; } = CurrentVersion;
        [JsonProperty("name")] public string Name { get; set; } = "Material";
        [JsonProperty("maps")] public MaterialMapSet Maps { get; set; } = new();
        [JsonProperty("heightMode")] public MaterialHeightMode HeightMode { get; set; }
        [JsonProperty("normalScale")] public float NormalScale { get; set; } = 1f;
        [JsonProperty("heightScale")] public float HeightScale { get; set; } = 0.035f;
        [JsonProperty("parallaxMinSteps")] public int ParallaxMinSteps { get; set; } = 8;
        [JsonProperty("parallaxMaxSteps")] public int ParallaxMaxSteps { get; set; } = 24;
        [JsonProperty("emissionIntensity")] public float EmissionIntensity { get; set; } = 1f;
        [JsonProperty("clearcoatStrength")] public float ClearcoatStrength { get; set; } = 1f;
        [JsonProperty("subsurfaceStrength")] public float SubsurfaceStrength { get; set; } = 1f;
        [JsonProperty("subsurfaceColor")] public float[] SubsurfaceColor { get; set; } = { 1f, 0.35f, 0.25f };
        [JsonProperty("flowSpeed")] public float FlowSpeed { get; set; }
        [JsonProperty("flowStrength")] public float FlowStrength { get; set; } = 1f;
        [JsonProperty("uvScale")] public float UvScale { get; set; } = 1f;

        public void Normalize()
        {
            Version = Math.Max(1, Version); Maps ??= new MaterialMapSet();
            NormalScale = Math.Clamp(NormalScale, 0f, 8f); HeightScale = Math.Clamp(HeightScale, -1f, 1f);
            ParallaxMinSteps = Math.Clamp(ParallaxMinSteps, 1, 64);
            ParallaxMaxSteps = Math.Clamp(ParallaxMaxSteps, ParallaxMinSteps, 128);
            EmissionIntensity = Math.Clamp(EmissionIntensity, 0f, 32f);
            ClearcoatStrength = Math.Clamp(ClearcoatStrength, 0f, 4f);
            SubsurfaceStrength = Math.Clamp(SubsurfaceStrength, 0f, 4f);
            FlowSpeed = Math.Clamp(FlowSpeed, -32f, 32f); FlowStrength = Math.Clamp(FlowStrength, 0f, 8f);
            UvScale = Math.Clamp(UvScale, 0.001f, 1024f);
            if (SubsurfaceColor == null || SubsurfaceColor.Length < 3) SubsurfaceColor = new[] { 1f, 0.35f, 0.25f };
        }
    }

    public struct MaterialGpuHandles
    {
        public TextureHandle Albedo, Normal, Height, Orm, Emission, Extras, Flow;
        public MaterialHeightMode HeightMode;
        public Vector4 SurfaceParams, DetailParams, SubsurfaceColorSteps;
        public bool IsValid => Albedo.IsValid || Normal.IsValid || Orm.IsValid || Height.IsValid;
    }
}

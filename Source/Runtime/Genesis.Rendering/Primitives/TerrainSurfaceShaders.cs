namespace Genesis.Rendering.Primitives;

/// <summary>Uses the normal forward lighting/fog path, with full-resolution tiled terrain layers.</summary>
public static class TerrainSurfaceShaders
{
    public static readonly string Source = ForwardShaders.Source
        .Replace("struct PSOut", LayerSource + "\nstruct PSOut", System.StringComparison.Ordinal)
        .Replace("float3 base = MaterialColor.rgb * (voxelTiled ? tex.rgb : IN.Color.rgb * tex.rgb);",
            "float3 base = MaterialColor.rgb * SampleTerrainLayers(materialUv);", System.StringComparison.Ordinal)
        .Replace("clip(tex.a - alphaCutoff);", "tex.a = 1.0;", System.StringComparison.Ordinal);

    private const string LayerSource = """
        cbuffer TerrainLayerConstants : register(b5)
        {
            float4 TerrainRepeatU;
            float4 TerrainRepeatV;
            float4 TerrainClamp;
            float4 TerrainLayerPadding;
        };
        Texture2D TerrainLayer1 : register(t21);
        Texture2D TerrainLayer2 : register(t22);
        Texture2D TerrainLayer3 : register(t23);
        float2 TerrainLayerUv(float2 uv, int index)
        {
            float2 mapped = uv * float2(TerrainRepeatU[index], TerrainRepeatV[index]);
            return TerrainClamp[index] > 0.5 ? clamp(mapped, 0.0005, 0.9995) : mapped;
        }
        float3 SampleTerrainLayers(float2 uv)
        {
            float4 weights = max(FlowMap.Sample(AlbedoSamp, uv), 0.0);
            float total = dot(weights, float4(1, 1, 1, 1));
            weights = total > 0.0001 ? weights / total : float4(1, 0, 0, 0);
            float3 layered = AlbedoTex.Sample(AlbedoSamp, TerrainLayerUv(uv, 0)).rgb * weights.x
                + TerrainLayer1.Sample(AlbedoSamp, TerrainLayerUv(uv, 1)).rgb * weights.y
                + TerrainLayer2.Sample(AlbedoSamp, TerrainLayerUv(uv, 2)).rgb * weights.z
                + TerrainLayer3.Sample(AlbedoSamp, TerrainLayerUv(uv, 3)).rgb * weights.w;
            return LinearColorPipeline() ? SrgbToLinear3(layered) : layered;
        }
        """;
}

namespace Genesis.Rendering.Primitives;

/// <summary>Uses the normal forward lighting/fog path, with full-resolution tiled terrain layers.</summary>
public static class TerrainSurfaceShaders
{
    public static readonly string Source = ForwardShaders.Source
        .Replace("struct PSOut", LayerSource + "\nstruct PSOut", System.StringComparison.Ordinal)
        .Replace("float3 base = MaterialColor.rgb * (voxelTiled ? tex.rgb : IN.Color.rgb * tex.rgb);",
            "float3 base = MaterialColor.rgb * SampleTerrainLayers(materialUv, IN.WorldPos, cameraPos);", System.StringComparison.Ordinal)
        .Replace("clip(tex.a - alphaCutoff);", "tex.a = 1.0;", System.StringComparison.Ordinal);

    /// <summary>
    /// Terrains at least this wide get the long-view treatment: layers blend to a broader repeat
    /// with distance and carry a slow variation in tone, so a hillside a kilometre away is neither
    /// a visible grid of tiles nor one flat colour.
    /// </summary>
    public const float LongViewWidth = 1000f;

    private const string LayerSource = """
        cbuffer TerrainLayerConstants : register(b5)
        {
            float4 TerrainRepeatU;
            float4 TerrainRepeatV;
            float4 TerrainClamp;
            // x: long-view tiling blend, y: broad tone variation; both 0 on small terrains.
            float4 TerrainLongView;
        };
        Texture2D TerrainLayer1 : register(t21);
        Texture2D TerrainLayer2 : register(t22);
        Texture2D TerrainLayer3 : register(t23);
        float2 TerrainLayerUv(float2 uv, int index)
        {
            float2 mapped = uv * float2(TerrainRepeatU[index], TerrainRepeatV[index]);
            return TerrainClamp[index] > 0.5 ? clamp(mapped, 0.0005, 0.9995) : mapped;
        }
        // The same image about nine times larger and slightly turned, so its repeats never line
        // up with the close-range ones.
        float2 TerrainBroadUv(float2 uv, int index)
        {
            float2 mapped = TerrainLayerUv(uv, index);
            if (TerrainClamp[index] > 0.5) return mapped;
            return float2(mapped.x * 0.113 - mapped.y * 0.031, mapped.y * 0.113 + mapped.x * 0.031);
        }
        float TerrainHash(float2 p)
        {
            return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
        }
        float TerrainNoise(float2 p)
        {
            float2 cell = floor(p);
            float2 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = TerrainHash(cell);
            float b = TerrainHash(cell + float2(1.0, 0.0));
            float c = TerrainHash(cell + float2(0.0, 1.0));
            float d = TerrainHash(cell + float2(1.0, 1.0));
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
        }
        float3 SampleTerrainLayers(float2 uv, float3 worldPos, float3 cameraPos)
        {
            float4 weights = max(FlowMap.Sample(AlbedoSamp, uv), 0.0);
            float total = dot(weights, float4(1, 1, 1, 1));
            weights = total > 0.0001 ? weights / total : float4(1, 0, 0, 0);
            float3 layered = AlbedoTex.Sample(AlbedoSamp, TerrainLayerUv(uv, 0)).rgb * weights.x
                + TerrainLayer1.Sample(AlbedoSamp, TerrainLayerUv(uv, 1)).rgb * weights.y
                + TerrainLayer2.Sample(AlbedoSamp, TerrainLayerUv(uv, 2)).rgb * weights.z
                + TerrainLayer3.Sample(AlbedoSamp, TerrainLayerUv(uv, 3)).rgb * weights.w;
            if (TerrainLongView.x > 0.001)
            {
                float far = TerrainLongView.x * smoothstep(30.0, 320.0, distance(worldPos, cameraPos));
                if (far > 0.001)
                {
                    float3 broad = AlbedoTex.Sample(AlbedoSamp, TerrainBroadUv(uv, 0)).rgb * weights.x
                        + TerrainLayer1.Sample(AlbedoSamp, TerrainBroadUv(uv, 1)).rgb * weights.y
                        + TerrainLayer2.Sample(AlbedoSamp, TerrainBroadUv(uv, 2)).rgb * weights.z
                        + TerrainLayer3.Sample(AlbedoSamp, TerrainBroadUv(uv, 3)).rgb * weights.w;
                    layered = lerp(layered, broad, far);
                }
            }
            if (TerrainLongView.y > 0.001)
            {
                float tone = TerrainNoise(worldPos.xz * 0.0031) * 0.5
                    + TerrainNoise(worldPos.xz * 0.013 + 17.3) * 0.32
                    + TerrainNoise(worldPos.xz * 0.071 + 5.9) * 0.18;
                layered *= lerp(1.0, 0.74 + tone * 0.52, TerrainLongView.y);
            }
            return LinearColorPipeline() ? SrgbToLinear3(layered) : layered;
        }
        """;
}

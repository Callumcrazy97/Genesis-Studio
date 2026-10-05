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
    /// Up to eight layers, each with its own tiled albedo, normal and ORM, read from three atlas
    /// textures (t21-t23) so the slot count matches the four-layer shader on every backend. Layers 1-4
    /// weigh from FlowMap (t11) and layers 5-8 from HeightMap (t8), which terrain never uses for
    /// displacement. With a height-blend sharpness above zero, layers meet along their albedo alpha
    /// (height) instead of fading linearly.
    /// </summary>
    public static readonly string LayerAtlasSource = ForwardShaders.Source
        .Replace("struct PSOut", AtlasSource + "\nstruct PSOut", System.StringComparison.Ordinal)
        .Replace("float3 nmSample = NormalMap.Sample(AlbedoSamp, materialUv).rgb;",
            "TerrainBlend terrainBlend = ComputeTerrainBlend(materialUv, IN.WorldPos, cameraPos);\n    float3 nmSample = SampleTerrainNormal(terrainBlend);", System.StringComparison.Ordinal)
        .Replace("float3 base = MaterialColor.rgb * (voxelTiled ? tex.rgb : IN.Color.rgb * tex.rgb);",
            "float3 base = MaterialColor.rgb * terrainBlend.Albedo;", System.StringComparison.Ordinal)
        .Replace("clip(tex.a - alphaCutoff);", "tex.a = 1.0;", System.StringComparison.Ordinal)
        .Replace("float3 orm = MaterialFeatures.x > 0.5 ? OrmMap.Sample(AlbedoSamp, materialUv).rgb : float3(1.0, 0.72, 0.0);",
            "float3 orm = SampleTerrainOrm(terrainBlend);", System.StringComparison.Ordinal);

    /// <summary>Paint layers the atlas shader blends.</summary>
    public const int AtlasLayerCount = 8;

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

    private const string AtlasSource = """
        cbuffer TerrainLayerConstants : register(b5)
        {
            float4 TerrainRepeatLo;   // repeats of layers 1-4 across the terrain
            float4 TerrainRepeatHi;   // layers 5-8
            // x/y: addressing of layers 1-4 / 5-8 in base-4 digits (0 repeat, 1 tile, 2 clamp),
            // z: terrain depth / width (a Tile layer's V repeat), w: height-blend sharpness (0 = linear).
            float4 TerrainModes;
            // x/y: atlas columns/rows, z: long-view tiling blend, w: broad tone variation.
            float4 TerrainAtlasInfo;
        };
        Texture2D TerrainAlbedoAtlas : register(t21);
        Texture2D TerrainNormalAtlas : register(t22);
        Texture2D TerrainOrmAtlas    : register(t23);
        struct TerrainBlend
        {
            float4 W0;
            float4 W1;
            float3 Albedo;
            float2 Uv;
            float2 UvDx;
            float2 UvDy;
        };
        float TerrainLayerWeight(TerrainBlend blend, int index)
        {
            return index < 4 ? blend.W0[index] : blend.W1[index - 4];
        }
        float TerrainLayerMode(int index)
        {
            float packed = index < 4 ? TerrainModes.x : TerrainModes.y;
            int digit = index - (index < 4 ? 0 : 4);
            float scale = digit == 0 ? 1.0 : (digit == 1 ? 4.0 : (digit == 2 ? 16.0 : 64.0));
            return fmod(floor(packed / scale), 4.0);
        }
        float2 TerrainBroadTurn(float2 p)
        {
            return float2(p.x * 0.113 - p.y * 0.031, p.y * 0.113 + p.x * 0.031);
        }
        // Atlas position of one layer's tile. Each cell holds the image with a wrapped border of a
        // sixteenth of its size; gradients are clamped so the chosen mip never reaches past it.
        void TerrainAtlasCoords(int index, float2 uv, float2 dx, float2 dy, bool broad,
            out float2 atlasUv, out float2 atlasDx, out float2 atlasDy)
        {
            float repeat = index < 4 ? TerrainRepeatLo[index] : TerrainRepeatHi[index - 4];
            float mode = TerrainLayerMode(index);
            float2 scale = float2(repeat, (mode > 0.5 && mode < 1.5) ? repeat * TerrainModes.z : repeat);
            float2 tile = uv * scale, tdx = dx * scale, tdy = dy * scale;
            if (mode > 1.5)
                tile = clamp(tile, 0.0005, 0.9995);
            else
            {
                if (broad) { tile = TerrainBroadTurn(tile); tdx = TerrainBroadTurn(tdx); tdy = TerrainBroadTurn(tdy); }
                tile = frac(tile);
            }
            const float footprint = 1.0 / 24.0;
            float lx = length(tdx), ly = length(tdy);
            tdx *= lx > footprint ? footprint / lx : 1.0;
            tdy *= ly > footprint ? footprint / ly : 1.0;
            float2 grid = max(TerrainAtlasInfo.xy, float2(1.0, 1.0));
            float2 cell = float2(fmod((float)index, grid.x), floor((float)index / grid.x));
            const float border = 1.0 / 18.0;
            const float content = 16.0 / 18.0;
            atlasUv = (cell + border + tile * content) / grid;
            atlasDx = tdx * content / grid;
            atlasDy = tdy * content / grid;
        }
        float TerrainAtlasNoiseHash(float2 p)
        {
            return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
        }
        float TerrainAtlasNoise(float2 p)
        {
            float2 cell = floor(p);
            float2 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = TerrainAtlasNoiseHash(cell);
            float b = TerrainAtlasNoiseHash(cell + float2(1.0, 0.0));
            float c = TerrainAtlasNoiseHash(cell + float2(0.0, 1.0));
            float d = TerrainAtlasNoiseHash(cell + float2(1.0, 1.0));
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
        }
        TerrainBlend ComputeTerrainBlend(float2 uv, float3 worldPos, float3 cameraPos)
        {
            TerrainBlend blend;
            blend.Albedo = float3(0, 0, 0);
            blend.Uv = uv;
            blend.UvDx = ddx(uv);
            blend.UvDy = ddy(uv);
            float4 w0 = max(FlowMap.Sample(AlbedoSamp, uv), 0.0);
            float4 w1 = max(HeightMap.Sample(AlbedoSamp, uv), 0.0);
            float total = dot(w0, float4(1, 1, 1, 1)) + dot(w1, float4(1, 1, 1, 1));
            if (total > 0.0001) { w0 /= total; w1 /= total; }
            else { w0 = float4(1, 0, 0, 0); w1 = float4(0, 0, 0, 0); }
            blend.W0 = w0;
            blend.W1 = w1;
            float far = TerrainAtlasInfo.z > 0.001 ? TerrainAtlasInfo.z * smoothstep(30.0, 320.0, distance(worldPos, cameraPos)) : 0.0;
            float4 samples[8];
            [unroll] for (int i = 0; i < 8; i++)
            {
                samples[i] = float4(0, 0, 0, 0);
                [branch] if (TerrainLayerWeight(blend, i) > 0.0001)
                {
                    float2 a, adx, ady;
                    TerrainAtlasCoords(i, uv, blend.UvDx, blend.UvDy, false, a, adx, ady);
                    float4 near = TerrainAlbedoAtlas.SampleGrad(AlbedoSamp, a, adx, ady);
                    if (far > 0.001)
                    {
                        TerrainAtlasCoords(i, uv, blend.UvDx, blend.UvDy, true, a, adx, ady);
                        near.rgb = lerp(near.rgb, TerrainAlbedoAtlas.SampleGrad(AlbedoSamp, a, adx, ady).rgb, far);
                    }
                    samples[i] = near;
                }
            }
            // Height blend: the layer standing highest (paint weight plus its albedo alpha) wins,
            // the others fade out within a band that narrows as sharpness rises.
            if (TerrainModes.w > 0.001)
            {
                float band = lerp(0.5, 0.02, saturate(TerrainModes.w));
                float peak = 0.0;
                [unroll] for (int p = 0; p < 8; p++)
                {
                    float w = TerrainLayerWeight(blend, p);
                    if (w > 0.0001) peak = max(peak, w + samples[p].a);
                }
                float sum = 0.0;
                float4 h0 = float4(0, 0, 0, 0), h1 = float4(0, 0, 0, 0);
                [unroll] for (int q = 0; q < 8; q++)
                {
                    float w = TerrainLayerWeight(blend, q);
                    float v = w > 0.0001 ? max(w + samples[q].a - (peak - band), 0.0) : 0.0;
                    if (q < 4) h0[q] = v; else h1[q - 4] = v;
                    sum += v;
                }
                if (sum > 0.0001) { blend.W0 = h0 / sum; blend.W1 = h1 / sum; }
            }
            float3 layered = float3(0, 0, 0);
            [unroll] for (int k = 0; k < 8; k++)
                layered += samples[k].rgb * TerrainLayerWeight(blend, k);
            if (TerrainAtlasInfo.w > 0.001)
            {
                float tone = TerrainAtlasNoise(worldPos.xz * 0.0031) * 0.5
                    + TerrainAtlasNoise(worldPos.xz * 0.013 + 17.3) * 0.32
                    + TerrainAtlasNoise(worldPos.xz * 0.071 + 5.9) * 0.18;
                layered *= lerp(1.0, 0.74 + tone * 0.52, TerrainAtlasInfo.w);
            }
            blend.Albedo = LinearColorPipeline() ? SrgbToLinear3(layered) : layered;
            return blend;
        }
        // Each layer's own normal map, tiled like its albedo, blended in tangent space.
        float3 SampleTerrainNormal(TerrainBlend blend)
        {
            float3 n = float3(0, 0, 0);
            [unroll] for (int i = 0; i < 8; i++)
            {
                float w = TerrainLayerWeight(blend, i);
                [branch] if (w > 0.0001)
                {
                    float2 a, adx, ady;
                    TerrainAtlasCoords(i, blend.Uv, blend.UvDx, blend.UvDy, false, a, adx, ady);
                    float3 s = TerrainNormalAtlas.SampleGrad(AlbedoSamp, a, adx, ady).rgb;
                    float3 d = s * 2.0 - 1.0;
                    if (s.z < 0.5 / 255.0) d.z = sqrt(saturate(1.0 - dot(d.xy, d.xy)));
                    n += d * w;
                }
            }
            n = dot(n, n) > 0.000001 ? normalize(n) : float3(0, 0, 1);
            return n * 0.5 + 0.5;
        }
        float3 SampleTerrainOrm(TerrainBlend blend)
        {
            float3 orm = float3(0, 0, 0);
            [unroll] for (int i = 0; i < 8; i++)
            {
                float w = TerrainLayerWeight(blend, i);
                [branch] if (w > 0.0001)
                {
                    float2 a, adx, ady;
                    TerrainAtlasCoords(i, blend.Uv, blend.UvDx, blend.UvDy, false, a, adx, ady);
                    orm += TerrainOrmAtlas.SampleGrad(AlbedoSamp, a, adx, ady).rgb * w;
                }
            }
            return orm;
        }
        """;
}

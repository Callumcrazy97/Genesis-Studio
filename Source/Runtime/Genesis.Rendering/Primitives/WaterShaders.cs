namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// HLSL source for the stylized water surface pass.
    /// </summary>
    /// <remarks>
    /// This lived in <c>Genesis.World.Water.WaterShader</c>, which forced Genesis.Rendering to
    /// reference Genesis.World for one <c>using</c>. Because that made the reverse reference a
    /// cycle, Genesis.World had to <c>Compile Remove</c> VoxelWorldRenderer/VoxelPlayerController/
    /// VoxelWorldSetup and Genesis.Runtime re-included them by path. A shader string belongs with
    /// the other shader strings, so it moved here and the knot came out — which matters more now
    /// that additional backend assemblies would otherwise inherit it.
    ///
    /// Still shared by content with <c>Source/Runtime/Shaders/Water.hlsl</c>; the cbuffer prefixes
    /// it declares are checked against the managed structs by
    /// <c>Render.Shader.ConstantBufferLayoutMatchesHlsl</c>.
    /// </remarks>
    public static class WaterShaders
    {
        public const string Source = @"
cbuffer PerFrameConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 LightViewProjection;
    row_major float4x4 LightViewProjectionNear;
    float4             CameraPosTime;
};

cbuffer EngineConstants : register(b1)
{
    float4     LightDirEnabled;
    float4     FogParams;
    float4     FogColor;
    float4     AmbientColor;
    float4     AmbientGroundColor;
    float4     SunColorIntensity;
    float4     FogParams2;
    float4     ShadowParams;
    float4     EffectParams;
    float4     VolumetricParams;
    float4     ViewportParams;
    float4     ShadowCascadeParams;
    float4     PointLightCounts;
};

cbuffer DrawConstants : register(b2)
{
    row_major float4x4 WorldMatrix;
    float4             MaterialColor;
    float4             MaterialParams;
    uint               InstanceOffset;
    // Scalars rather than float3: std140 would align a 3-component vector to 16 bytes and the block
    // could not be expressed in GLSL. Same 12 bytes on every backend.
    float              _padDraw0;
    float              _padDraw1;
    float              _padDraw2;
};

cbuffer WaterConstants : register(b3)
{
    float4 DeepColorDepthFade;
    float4 WaterParams;
    float4 SkyHorizonFlow;
    float4 SkyZenithPad;
    row_major float4x4 ReflectionViewProjection;
    float4 ReflectionParams;
    float4 WeatherWindRain;
    float4 DepthParams;   // x = scene depth is reversed
};

Texture2D            AlbedoTex       : register(t0);
Texture2D            WaterNormalA   : register(t1);
Texture2D            WaterNormalB   : register(t3);
Texture2D            PlanarReflectionTex : register(t7);
Texture2D            SceneDepthTex  : register(t6);
#define GENESIS_DEPTH_REVERSED (DepthParams.x > 0.5)
" + SceneDepthHlsl.Helpers + @"
SamplerState         WaterSamp      : register(s0);

struct VSIn
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float4 Color    : COLOR;
    float2 UV       : TEXCOORD0;
};

struct VSOut
{
    float4 SvPos    : SV_Position;
    float3 WorldPos : TEXCOORD1;
    float3 Normal   : TEXCOORD2;
    float4 Color    : TEXCOORD3;
    float2 UV       : TEXCOORD4;
};

" + FroxelFogShaders.ApplySource + @"

VSOut VS_Water(VSIn IN)
{
    float time = CameraPosTime.w;
    float waveAmp = WaterParams.w * (WeatherWindRain.w > 0.5 ? 1.0 + length(WeatherWindRain.xy) * 0.06 : 1.0);
    float3 pos = IN.Position;

    if (waveAmp > 0.0001 && IN.Normal.y > 0.5)
    {
        float w1 = sin(pos.x * 0.35 + time * 1.2) * waveAmp;
        float w2 = cos(pos.z * 0.28 + time * 0.9) * waveAmp * 0.65;
        pos.y += w1 + w2;
    }

    float4 worldPos = mul(float4(pos, 1.0), WorldMatrix);
    VSOut OUT;
    OUT.SvPos    = mul(worldPos, ViewProjection);
    OUT.WorldPos = worldPos.xyz;
    OUT.Normal   = normalize(mul(float4(IN.Normal, 0.0), WorldMatrix).xyz);
    OUT.Color    = IN.Color;
    OUT.UV       = IN.UV;
    return OUT;
}

float Hash21(float2 p)
{
    uint n = asuint(floor(p.x)) * 1597334677u ^ asuint(floor(p.y)) * 3812015801u;
    n ^= n >> 16u;
    n *= 0x7feb352du;
    n ^= n >> 15u;
    n *= 0x846ca68bu;
    n ^= n >> 16u;
    return n * 2.3283064365386963e-10;
}

float LinearizeDepth(float depth, float nearPlane, float farPlane)
{
    return SceneDepthToView(depth, nearPlane, farPlane);
}

float3 SampleWaterNormal(float2 uv, float time, float flowSpeed, float2 flowDir, float rapids)
{
    float len = length(flowDir);
    if (len > 0.01) flowDir /= len;
    else flowDir = float2(0.0, 1.0);

    float spd = flowSpeed * (1.0 + rapids * 2.2);
    float2 ortho = float2(-flowDir.y, flowDir.x);
    float2 scrollA = flowDir * time * spd + ortho * time * spd * 0.38;
    float2 scrollB = ortho * time * spd * 0.55 - flowDir * time * spd * 0.22;

    float3 nA = WaterNormalA.Sample(WaterSamp, uv * 3.5 + scrollA).rgb * 2.0 - 1.0;
    float3 nB = WaterNormalB.Sample(WaterSamp, uv * 7.0 + scrollB).rgb * 2.0 - 1.0;
    float3 n  = normalize(float3(nA.xy + nB.xy, nA.z * nB.z));
    return n;
}

float3 FakeSkyReflection(float3 viewDir, float3 worldNormal, float3 horizon, float3 zenith)
{
    float3 r = reflect(viewDir, worldNormal);
    float t = saturate(r.y * 0.5 + 0.5);
    return lerp(horizon, zenith, t);
}

struct PSOut
{
    float4 Color       : SV_Target0;
    float  SkipPostFog : SV_Target1;
};

PSOut PS_Water(VSOut IN)
{
    float time = CameraPosTime.w;
    float3 cameraPos = CameraPosTime.xyz;
    float3 viewDir = normalize(IN.WorldPos - cameraPos);

    float2 flowDir = SkyHorizonFlow.zw;
    float flen = length(flowDir);
    if (flen > 0.01) flowDir /= flen;
    else flowDir = float2(0.0, 1.0);
    // River and waterfall meshes both author +V along the flow. Keeping the
    // normal-map scroll in UV space makes it follow bends and vertical sheets.
    float3 tangentNormal = SampleWaterNormal(IN.UV, time, WaterParams.x, flowDir, SkyZenithPad.w);
    // Ripples a few metres across turn to moire when a sea runs to the horizon. Past a couple of
    // hundred metres they give way to a broad swell, and that to flat water further still.
    float rippleDistance = distance(IN.WorldPos, cameraPos);
    float ripples = 1.0 - smoothstep(35.0, 240.0, rippleDistance);
    if (ripples < 0.999)
    {
        // Four trains of waves on headings that share no symmetry: a tiled normal map enlarged
        // to this scale shows its own grid, and these never line up into one.
        float2 p = IN.WorldPos.xz;
        float2 d1 = float2(0.866, 0.5), d2 = float2(-0.31, 0.95), d3 = float2(0.62, -0.78), d4 = float2(-0.97, -0.24);
        float2 swell = d1 * cos(dot(p, d1) * 0.071 + time * 0.9) * 0.5
                     + d2 * cos(dot(p, d2) * 0.113 + time * 1.1) * 0.35
                     + d3 * cos(dot(p, d3) * 0.047 + time * 0.7) * 0.6
                     + d4 * cos(dot(p, d4) * 0.173 + time * 1.3) * 0.25;
        float swellAmount = (1.0 - ripples) * 0.16 * (1.0 - smoothstep(350.0, 1700.0, rippleDistance));
        tangentNormal = float3(tangentNormal.xy * ripples + swell * swellAmount, 1.0);
    }
    float rain = WeatherWindRain.w > 0.5 ? saturate(WeatherWindRain.z) : 0;
    tangentNormal.xy += float2(sin(IN.WorldPos.x * 19 + time * 27), cos(IN.WorldPos.z * 23 - time * 31)) * rain * 0.07;
    tangentNormal = normalize(tangentNormal);
    // Build a stable local basis so the same dual-normal flow works on horizontal
    // rivers and on vertical waterfall ribbons.
    float3 basisSeed = abs(IN.Normal.y) < 0.92 ? float3(0, 1, 0) : float3(0, 0, 1);
    float3 tangent = normalize(cross(basisSeed, IN.Normal));
    float3 bitangent = normalize(cross(IN.Normal, tangent));
    float3 n = normalize(IN.Normal + (tangent * tangentNormal.x + bitangent * tangentNormal.y) * 0.55);

    float2 screenUv = IN.SvPos.xy / max(ViewportParams.xy, float2(1, 1));
    float sceneDepth = SceneDepthTex.SampleLevel(WaterSamp, screenUv, 0).r;

    // This pass samples scene depth as an SRV and does not bind it as a DSV (D3D
    // forbids both). Replicate the old LessEqual test-only occlusion here.
    // A buffer that is not bound reads 0. With reversed depth 0 is also the sky: either way
    // there is nothing in front of the water there.
    bool sceneHasSurface = GENESIS_DEPTH_REVERSED ? sceneDepth > 0.0 : sceneDepth > 0.0001;
    if (sceneHasSurface && SceneDepthBehind(IN.SvPos.z, sceneDepth) > 0.0)
        discard;

    // If no depth buffer is bound (sceneDepth==0), assume deep water so depth fade
    // still works gracefully.
    // The camera's own clip planes, so the depth of water over the bed is right at any view
    // distance. Older callers leave them zero and get the previous fixed range.
    float nearPlane = ReflectionParams.z > 0.0 ? ReflectionParams.z : 0.1;
    float farPlane = ReflectionParams.w > 0.0 ? ReflectionParams.w : max(FogParams.z, 250.0);
    float waterEyeZ = LinearizeDepth(IN.SvPos.z, nearPlane, farPlane);
    float sceneEyeZ = sceneHasSurface
        ? LinearizeDepth(sceneDepth, nearPlane, farPlane)
        : waterEyeZ + farPlane;
    float columnDepth = max(sceneEyeZ - waterEyeZ, 0.0);

    float depthFade = max(DeepColorDepthFade.w, 0.001);
    float depthT = saturate(columnDepth / depthFade);

    float3 atlas = AlbedoTex.Sample(WaterSamp, IN.UV).rgb;
    float3 shallow = MaterialColor.rgb * IN.Color.rgb * atlas;
    float3 deep = DeepColorDepthFade.rgb;
    float3 absorptionCoeff = float3(0.45, 0.15, 0.08) / depthFade;
    float3 transmittance = exp(-absorptionCoeff * columnDepth);
    float3 waterColor = shallow * transmittance + deep * (1.0 - transmittance);

    float fresnel = pow(1.0 - saturate(dot(-viewDir, n)), 5.0);
    float3 horizonCol = float3(SkyHorizonFlow.xy, lerp(SkyHorizonFlow.x, SkyZenithPad.y, 0.45));
    float3 skyRefl = FakeSkyReflection(viewDir, n, horizonCol, SkyZenithPad.rgb);
    if (ReflectionParams.x > 0.5)
    {
        float4 projected = mul(float4(IN.WorldPos, 1.0), ReflectionViewProjection);
        float2 reflectionUv = projected.xy / max(projected.w, 0.0001) * float2(0.5, -0.5) + 0.5;
        reflectionUv += n.xz * ReflectionParams.y;
        if (projected.w > 0 && all(reflectionUv >= 0) && all(reflectionUv <= 1))
            skyRefl = PlanarReflectionTex.SampleLevel(WaterSamp, reflectionUv, 0).rgb;
    }

    float foamWidth = max(WaterParams.z, 0.05);
    float foam = (1.0 - saturate(columnDepth / foamWidth))
               * (0.35 + 0.65 * saturate(fresnel));
    float foamNoise = Hash21(IN.WorldPos.xz + time);
    foam += SkyZenithPad.w * (0.45 + 0.35 * foamNoise);
    float edgeDistance = min(IN.UV.x, 1.0 - IN.UV.x);
    float edgeFoam = (1.0 - smoothstep(0.01, 0.12, edgeDistance)) * saturate(SkyZenithPad.w * 1.5);
    foam += edgeFoam;
    waterColor = lerp(waterColor, float3(0.92, 0.97, 1.0), saturate(foam) * (0.35 + foamNoise * 0.25));

    float3 lightDir = normalize(-LightDirEnabled.xyz);
    float3 viewToCamera = normalize(cameraPos - IN.WorldPos);
    float3 halfVector = normalize(lightDir + viewToCamera);
    float ndotl = saturate(dot(n, lightDir));
    float ndotv = saturate(dot(n, viewToCamera));
    float ndoth = saturate(dot(n, halfVector));
    float vdoth = saturate(dot(viewToCamera, halfVector));
    float roughness = lerp(0.075, 0.24, saturate(SkyZenithPad.w));
    float alphaRoughness = roughness * roughness;
    float alphaSquared = alphaRoughness * alphaRoughness;
    float denominator = ndoth * ndoth * (alphaSquared - 1.0) + 1.0;
    float distribution = alphaSquared / max(3.14159265 * denominator * denominator, 0.0001);
    float k = (roughness + 1.0) * (roughness + 1.0) * 0.125;
    float geometryV = ndotv / max(ndotv * (1.0 - k) + k, 0.0001);
    float geometryL = ndotl / max(ndotl * (1.0 - k) + k, 0.0001);
    float3 fresnelSpecular = 0.02 + (1.0 - 0.02) * pow(1.0 - vdoth, 5.0);
    float3 specular = distribution * geometryV * geometryL * fresnelSpecular
                    / max(4.0 * ndotv * ndotl, 0.0001);
    float sunAmt = SunColorIntensity.w * ndotl;
    float3 lit = lerp(AmbientColor.rgb, SunColorIntensity.rgb, sunAmt + 0.18);
    waterColor *= lit * (0.55 + 0.45 * saturate(SunColorIntensity.w));
    waterColor += specular * SunColorIntensity.rgb * SunColorIntensity.w * ndotl;
    // Reflected radiance has already been lit in the scene pass.
    waterColor = lerp(waterColor, skyRefl, 0.02 + 0.98 * fresnel);

    float shoreFade = smoothstep(0.0, max(foamWidth * 0.35, 0.08), columnDepth);
    float alpha = MaterialColor.a * IN.Color.a * lerp(0.18, 1.0, shoreFade);

    // Water writes no depth, so the composite would fog it against the lake bed behind it. It fogs
    // itself through the froxel volume at its own surface instead and flags the pixel as done.
    float fogDone = 0.0;
    if (FogParams.x > 0.5 && FroxelApplyParams.x > 0.5)
    {
        float4 fog = FroxelFog(IN.SvPos.xy * ViewportParams.zw, cameraPos, IN.WorldPos,
            mul(float4(IN.WorldPos, 1.0), ViewProjection).w);
        waterColor = lerp(waterColor, waterColor * fog.a + fog.rgb, saturate(FogColor.a));
        fogDone = 1.0;
    }

    PSOut result;
    // Linear pipeline, direct-to-display pass: encode here because no composite follows.
    if (VolumetricParams.z > 1.5)
    {
        float3 encoded = saturate(waterColor);
        waterColor = lerp(1.055 * pow(encoded, 1.0 / 2.4) - 0.055, encoded * 12.92, step(encoded, 0.0031308));
    }
    result.Color = float4(waterColor, alpha);
    // The HDR scene target always opens a fog-skip attachment. WebGPU requires the FS to
    // write every colour target; a literal 0 is DCE'd and strips SV_Target1 from the signature.
    result.SkipPostFog = fogDone + 1e-10 * result.Color.a;
    return result;
}
";
    }
}

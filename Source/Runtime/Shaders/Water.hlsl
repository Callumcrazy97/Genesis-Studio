
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
};

Texture2D            AlbedoTex       : register(t0);
Texture2D            WaterNormalA   : register(t1);
Texture2D            WaterNormalB   : register(t3);
Texture2D            PlanarReflectionTex : register(t7);
#if GENESIS_WEBGPU
Texture2D<float>     SceneDepthTex  : register(t6);
SamplerComparisonState WaterDepthSamp : register(s1);
#else
Texture2D            SceneDepthTex  : register(t6);
#endif
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
    float z = depth;
    return (nearPlane * farPlane) / (farPlane - z * (farPlane - nearPlane));
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
#if GENESIS_WEBGPU
    // WaterDepthSamp is LessEqual: SampleCmp returns 1 when storedDepth <= reference.
    float depthLow = 0.0;
    float depthHigh = 1.0;
    [unroll]
    for (int depthStep = 0; depthStep < 12; depthStep++)
    {
        float depthMid = (depthLow + depthHigh) * 0.5;
        float atOrBelowRef = SceneDepthTex.SampleCmpLevelZero(WaterDepthSamp, screenUv, depthMid);
        if (atOrBelowRef > 0.5) depthHigh = depthMid;
        else depthLow = depthMid;
    }
    float sceneDepth = (depthLow + depthHigh) * 0.5;
#else
    float sceneDepth = SceneDepthTex.SampleLevel(WaterSamp, screenUv, 0).r;
#endif

    // This pass samples scene depth as an SRV and does not bind it as a DSV (D3D
    // forbids both). Replicate the old LessEqual test-only occlusion here.
    if (sceneDepth > 0.0001 && IN.SvPos.z > sceneDepth)
        discard;

    // If no depth buffer is bound (sceneDepth==0), assume deep water so depth fade
    // still works gracefully.
    float nearPlane = 0.1;
    float farPlane = max(FogParams.z, 250.0);
    float waterEyeZ = LinearizeDepth(IN.SvPos.z, nearPlane, farPlane);
    float sceneEyeZ = sceneDepth > 0.0001
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

    float fog = 0.0;
    if (FogParams.x > 0.5)
    {
        float viewDepth = length(IN.WorldPos - cameraPos);
        float expFog = 1.0 - exp(-pow(viewDepth * FogParams.w, 2.0));
        float rangeFog = smoothstep(FogParams.y, FogParams.z, viewDepth);
        fog = saturate(max(expFog, rangeFog * saturate(FogParams.w * 4.0)));
    }
    waterColor = lerp(waterColor, FogColor.rgb, fog * saturate(FogColor.a));

    PSOut result;
    result.Color = float4(waterColor, alpha);
    // The HDR scene target always opens a fog-skip attachment. WebGPU requires the FS to
    // write every colour target; a literal 0 is DCE'd and strips SV_Target1 from the signature.
    result.SkipPostFog = 1e-10 * result.Color.a;
    return result;
}

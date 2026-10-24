namespace Genesis.Rendering.Primitives;

/// <summary>
/// The light probe (ForwardRenderer.LightProbes): six views from one point, 90 degrees each, side by
/// side in a 3 x 2 atlas (+X, -X, +Y on the top row, -Y, +Z, -Z below). PS_Compose copies one view
/// into its tile, with the sky the forward shader would mirror where nothing was drawn. PS_Average
/// makes the 6 x 1 ambient row: each texel the light arriving from one side, for the bounced light.
/// </summary>
/// <remarks>
/// Each face looks along its axis with the up vector of <see cref="FaceUp"/>, as a left-handed
/// look-at does; ForwardShaders.ProbeMirror reads the tiles back with the same bases.
/// </remarks>
internal static class LightProbeShaders
{
    public const string Source = @"
cbuffer LightProbeConstants : register(b0)
{
    float4 FaceParams;     // x = face, y = scene depth is reversed, z = 1 / face pixels
    float4 AmbientSky;     // the room's ambient terms and the sky the forward shader mirrors
    float4 AmbientGround;
    float4 SkyParams;      // x = 1 for the hemisphere sky light
    float4 SkyZenith;
    float4 SkyHorizon;
    float4 SkyGround;
};

Texture2D        FaceColor : register(t0);
Texture2D<float> FaceDepth : register(t1);
Texture2D        Atlas     : register(t2);
SamplerState     LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = p;
    return o;
}

void FaceBasis(int face, out float3 forward, out float3 up)
{
    if (face == 0)      { forward = float3( 1, 0, 0); up = float3(0, 1, 0); }
    else if (face == 1) { forward = float3(-1, 0, 0); up = float3(0, 1, 0); }
    else if (face == 2) { forward = float3(0,  1, 0); up = float3(0, 0, -1); }
    else if (face == 3) { forward = float3(0, -1, 0); up = float3(0, 0, 1); }
    else if (face == 4) { forward = float3(0, 0,  1); up = float3(0, 1, 0); }
    else                { forward = float3(0, 0, -1); up = float3(0, 1, 0); }
}

float3 FaceDirection(int face, float2 uv)
{
    float3 forward, up;
    FaceBasis(face, forward, up);
    float3 right = cross(up, forward);
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return normalize(forward + ndc.x * right + ndc.y * up);
}

// The forward shader's environment mirror without a probe (ForwardShaders: envMirror).
float3 SkyMirror(float3 d)
{
    float3 mirror = lerp(AmbientGround.rgb, AmbientSky.rgb, saturate(d.y * 0.5 + 0.5));
    if (SkyParams.x > 0.5)
    {
        float3 mirrorSky = lerp(SkyHorizon.rgb, SkyZenith.rgb, pow(max(d.y, 1e-4), 0.45));
        mirror = lerp(SkyGround.rgb, mirrorSky, saturate(d.y * 8.0 + 0.5));
    }
    return mirror;
}

float4 PS_Compose(VSOut IN) : SV_Target
{
    // The tile is as large as the view: one texel each, read without filtering (depth may not filter).
    int3 texel = int3(IN.uv / FaceParams.z, 0);
    float depth = FaceDepth.Load(texel);
    bool empty = FaceParams.y > 0.5 ? depth <= 0.0 : depth >= 1.0;
    float3 color = empty
        ? SkyMirror(FaceDirection((int)FaceParams.x, IN.uv))
        : FaceColor.Load(texel).rgb;
    return float4(max(color, 0.0), 1.0);
}

// The light arriving from one side: its tile's average over the solid angle each pixel covers.
float4 PS_Average(VSOut IN) : SV_Target
{
    int face = min((int)(IN.uv.x * 6.0), 5);
    float2 tile = float2(face % 3, face / 3);
    float3 sum = 0;
    float weight = 0;
    [loop] for (int y = 0; y < 16; y++)
    {
        [loop] for (int x = 0; x < 16; x++)
        {
            float2 uv = (float2(x, y) + 0.5) / 16.0;
            float2 ndc = uv * 2.0 - 1.0;
            float w = pow(1.0 + dot(ndc, ndc), -1.5);
            sum += Atlas.SampleLevel(LinearClamp, (tile + uv) / float2(3.0, 2.0), 0).rgb * w;
            weight += w;
        }
    }
    return float4(sum / max(weight, 1e-4), 1.0);
}
";

    /// <summary>The direction face <paramref name="face"/> looks along: +X, -X, +Y, -Y, +Z, -Z.</summary>
    public static System.Numerics.Vector3 FaceForward(int face) => face switch
    {
        0 => System.Numerics.Vector3.UnitX,
        1 => -System.Numerics.Vector3.UnitX,
        2 => System.Numerics.Vector3.UnitY,
        3 => -System.Numerics.Vector3.UnitY,
        4 => System.Numerics.Vector3.UnitZ,
        _ => -System.Numerics.Vector3.UnitZ,
    };

    /// <summary>The up vector of face <paramref name="face"/>, as the shaders' FaceBasis has it.</summary>
    public static System.Numerics.Vector3 FaceUp(int face) => face switch
    {
        2 => -System.Numerics.Vector3.UnitZ,
        3 => System.Numerics.Vector3.UnitZ,
        _ => System.Numerics.Vector3.UnitY,
    };
}

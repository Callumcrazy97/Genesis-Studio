namespace Genesis.Rendering.Primitives
{
    internal static class SpriteShaders
    {
        public const string Source = @"
cbuffer SpriteConstants : register(b0)
{
    row_major float4x4 Transform;
    uint               InstanceOffset;
    // Tail padding spelled as scalars, not uint3. Identical 12 bytes either way, but std140 aligns
    // a 3-component vector to 16, so a uint3 here would sit at offset 68 and make the whole block
    // inexpressible in GLSL — which is what the OpenGL backend translates to.
    uint               _padCb0;
    uint               _padCb1;
    uint               _padCb2;
};

cbuffer SpriteFogConstants : register(b1)
{
    float4 FogColor;   // rgb = fog colour, a = configured maximum fog alpha
    float4 FogParams;  // x=enabled, y=depth/start, z=inverse thickness, w=max alpha
};

struct SpriteInstanceData
{
    float4 PosOrigin; // xy = position, zw = origin
    float4 SizeRot;   // xy = width/height, zw = cos/sin rotation
    float4 Color;
    float4 DepthPad;  // x = clip-space Z, y = authored 2D Z/layer depth
    float4 UvRect;    // u0,v0,u1,v1 — zero extent = full texture
};

StructuredBuffer<SpriteInstanceData> SpriteInstances : register(t1);

Texture2D    SpriteTex  : register(t0);
SamplerState SpriteSamp : register(s0);

struct VSIn
{
    float2 Pos : POSITION;
    float2 UV  : TEXCOORD0;
};

struct VSOut
{
    float4 SvPos    : SV_Position;
    float2 UV       : TEXCOORD0;
    float4 Color    : COLOR;
    float  FogDepth : TEXCOORD1;
};

VSOut VS(VSIn IN, uint instanceId : SV_InstanceID)
{
    uint idx = instanceId + InstanceOffset;
    SpriteInstanceData inst = SpriteInstances[idx];

    float2 origin = inst.PosOrigin.zw;
    float  w      = inst.SizeRot.x;
    float  h      = inst.SizeRot.y;
    float  cosR   = inst.SizeRot.z;
    float  sinR   = inst.SizeRot.w;

    float lx = IN.Pos.x * w - origin.x;
    float ly = IN.Pos.y * h - origin.y;

    float wx = inst.PosOrigin.x + lx * cosR - ly * sinR;
    float wy = inst.PosOrigin.y + lx * sinR + ly * cosR;
    float wz = inst.DepthPad.x;

    VSOut OUT;
    OUT.SvPos = mul(float4(wx, wy, wz, 1.0), Transform);
    float4 uv = inst.UvRect;
    if (uv.z <= uv.x || uv.w <= uv.y) uv = float4(0, 0, 1, 1);
    OUT.UV       = float2(lerp(uv.x, uv.z, IN.UV.x), lerp(uv.y, uv.w, IN.UV.y));
    OUT.Color    = inst.Color;
    OUT.FogDepth = inst.DepthPad.y;
    return OUT;
}

float4 PS(VSOut IN) : SV_Target
{
    float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV);
    float4 col = tex * IN.Color;
    // wgpu/Naga requires every VS interpolator location to exist on the FS. DXC -O3 can
    // drop FogDepth because it is only read behind a uniform FogParams branch.
    if (IN.FogDepth + IN.UV.x + IN.Color.x < -1e20)
        col.a = 0;
    clip(col.a - 0.001);

    // 2D uses authored Z/layer order as its depth axis. Higher depth is farther away, matching
    // the existing sprite sort convention. Fog alpha controls only the RGB blend; it must never
    // darken sprite alpha or multiply the source toward black.
    float fogAmount = 0.0;
    if (FogParams.x > 0.5 && FogParams.w > 0.0)
        fogAmount = saturate((IN.FogDepth - FogParams.y) * FogParams.z) * saturate(FogParams.w);

    col.rgb = lerp(col.rgb, FogColor.rgb, fogAmount);
    return col;
}
";
    }
}

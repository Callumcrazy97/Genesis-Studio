namespace Genesis.Rendering.Primitives
{
    internal static class ShaderPreviewFullscreenShaders
    {
        public const string Source = @"
struct PreviewVSOut
{
    float4 SvPos : SV_Position;
    float2 UV    : TEXCOORD0;
};

PreviewVSOut PreviewVS(uint id : SV_VertexID)
{
    PreviewVSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.SvPos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.UV    = p;
    return o;
}
";
    }
}

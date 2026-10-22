namespace Genesis.Rendering.Primitives;

/// <summary>
/// Culling a batch's instances on the GPU: each instance's bounding sphere is tested against the
/// camera's side planes, and those in view are written one after another into a second instance
/// buffer, counting themselves into the batch's indirect draw arguments. The batch is then drawn
/// with those arguments, so instances out of view cost the GPU nothing and the CPU never learns
/// how many there were.
/// </summary>
internal static class GpuCullingShaders
{
    public const string Source = @"
cbuffer CullConstants : register(b0)
{
    // The camera's left, right, bottom and top planes, normalised: a point is inside when
    // dot(plane.xyz, p) + plane.w >= 0. Everything behind the camera is outside one of them.
    float4 Planes[4];
    // The mesh's bounding sphere in its own space (xyz centre, w radius, padded).
    float4 Sphere;
    uint First;           // the batch's first instance, in both buffers
    uint Count;           // its instances
    uint IndexCount;      // the mesh's indices
    uint ArgumentOffset;  // where the batch's five draw arguments are, in bytes
};

struct InstanceData { row_major float4x4 World; float4 Color; float4 AtlasData; };
StructuredBuffer<InstanceData> Source : register(t0);
RWStructuredBuffer<InstanceData> Visible : register(u0);
RWByteAddressBuffer Arguments : register(u1);

// DrawIndexedInstanced's arguments: index count, instances (counted by Cull), first index,
// base vertex, first instance.
[numthreads(1, 1, 1)]
void ResetArguments(uint3 id : SV_DispatchThreadID)
{
    Arguments.Store(ArgumentOffset, IndexCount);
    Arguments.Store(ArgumentOffset + 4, 0);
    Arguments.Store(ArgumentOffset + 8, 0);
    Arguments.Store(ArgumentOffset + 12, 0);
    Arguments.Store(ArgumentOffset + 16, 0);
}

[numthreads(64, 1, 1)]
void Cull(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Count) return;
    InstanceData instance = Source[First + id.x];
    row_major float4x4 world = instance.World;
    // An instance without a matrix of its own is drawn with the batch's (as the vertex shader
    // does): always kept.
    if (length(world[0]) >= 0.001)
    {
        float3 centre = mul(float4(Sphere.xyz, 1.0), world).xyz;
        float scale = max(length(world[0].xyz), max(length(world[1].xyz), length(world[2].xyz)));
        float radius = Sphere.w * scale;
        [unroll] for (int i = 0; i < 4; i++)
        {
            if (dot(Planes[i].xyz, centre) + Planes[i].w < -radius) return;
        }
    }
    uint slot;
    Arguments.InterlockedAdd(ArgumentOffset + 4, 1, slot);
    Visible[First + slot] = instance;
}
";
}

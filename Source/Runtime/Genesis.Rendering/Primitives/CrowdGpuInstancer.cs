using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
namespace Genesis.Rendering.Primitives;

public readonly record struct CrowdStatistics(int Registered, int Visible, int Culled, int Batches);

/// <summary>
/// Retained rigid/mesh-part crowd instances feeding the existing structured-buffer instanced renderer.
/// No per-agent DrawMesh calls. Registration and updates belong to the render/scene thread. Meshes and
/// textures remain owned by the caller; this class never releases shared resources.
/// </summary>
public sealed class CrowdGpuInstancer
{
    private sealed class Instance { public Matrix4x4 World; public RenderColor Tint; public bool Visible; }
    private sealed class Group
    {
        public MeshDrawCall Template; public Vector3 Center; public float Radius;
        public readonly Dictionary<long,Instance> Instances = new(); public MeshInstanceData[] Scratch = Array.Empty<MeshInstanceData>();
    }
    private readonly Dictionary<int,Group> _groups = new(); private int _nextGroup = 1;
    private int _instanceCount;
    public bool FrustumCulling { get; set; } = true;
    public int MaximumInstances { get; }
    public CrowdStatistics Statistics { get; private set; }
    public CrowdGpuInstancer(int maximumInstances = 131072)
    {
        if (maximumInstances < 1 || maximumInstances > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumInstances));
        MaximumInstances = maximumInstances;
    }
    public int CreateGroup(in MeshDrawCall template, Vector3 localBoundsCenter, float localBoundsRadius)
    {
        if (!template.Mesh.IsValid) throw new ArgumentException("Group needs an uploaded mesh.", nameof(template));
        if (!Finite(localBoundsCenter) || !float.IsFinite(localBoundsRadius) || localBoundsRadius <= 0) throw new ArgumentOutOfRangeException(nameof(localBoundsRadius));
        const MeshDrawFlags unsupported = MeshDrawFlags.Transparent | MeshDrawFlags.Additive | MeshDrawFlags.Multiply | MeshDrawFlags.Water | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoDepthTest;
        if ((template.Flags & unsupported) != 0 || template.SkinPalette.IsValid || template.OrmMap.IsValid || template.HeightMap.IsValid ||
            template.EmissionMap.IsValid || template.ExtrasMap.IsValid || template.FlowMap.IsValid)
            throw new ArgumentException("Crowd groups use the rigid opaque instancing path. Use ordinary draw calls for skin palettes, complex PBR maps, water or transparency.", nameof(template));
        int id = _nextGroup++; _groups.Add(id, new Group { Template=template, Center=localBoundsCenter, Radius=localBoundsRadius }); return id;
    }
    public void SetInstance(int groupId, long instanceId, in Matrix4x4 world, RenderColor tint, bool visible = true)
    {
        if (!_groups.TryGetValue(groupId,out var group)) throw new KeyNotFoundException("Unknown crowd group.");
        float determinant=world.GetDeterminant();
        if (!Finite(world) || !float.IsFinite(determinant) || determinant <= 0 || MathF.Abs(world.M14)+MathF.Abs(world.M24)+MathF.Abs(world.M34)>1e-5f || MathF.Abs(world.M44-1)>1e-5f)
            throw new ArgumentException("Crowd transforms must be finite, affine and non-mirrored.", nameof(world));
        if (!float.IsFinite(tint.R) || !float.IsFinite(tint.G) || !float.IsFinite(tint.B) || !float.IsFinite(tint.A)) throw new ArgumentException("Invalid crowd tint.",nameof(tint));
        if (!group.Instances.TryGetValue(instanceId,out var instance))
        {
            if (_instanceCount >= MaximumInstances) throw new InvalidOperationException("Crowd capacity exceeded; no agent was silently dropped.");
            instance=new Instance(); group.Instances.Add(instanceId,instance); _instanceCount++;
        }
        instance.World=world; instance.Tint=tint; instance.Visible=visible;
    }
    public bool RemoveInstance(int groupId,long instanceId)
    { if (!_groups.TryGetValue(groupId,out var group) || !group.Instances.Remove(instanceId)) return false; _instanceCount--; return true; }
    public bool RemoveGroup(int groupId)
    { if(!_groups.Remove(groupId,out var group))return false; _instanceCount-=group.Instances.Count; return true; }
    public void Clear() { _groups.Clear(); _instanceCount=0; Statistics=default; }
    public int Submit(IRenderController renderer, in Matrix4x4 viewProjection)
    {
        ArgumentNullException.ThrowIfNull(renderer); var frustum=new CameraFrustum(viewProjection);
        int visible=0,culled=0,batches=0;
        foreach(var group in _groups.Values)
        {
            int total=group.Instances.Count;
            if(group.Scratch.Length<total) Array.Resize(ref group.Scratch,Math.Min(MaximumInstances,Math.Max(total,Math.Max(16,group.Scratch.Length*2))));
            int count=0;
            foreach(var item in group.Instances.Values)
            {
                if(!item.Visible) { culled++; continue; }
                Vector3 center=Vector3.Transform(group.Center,item.World);
                // Frobenius norm is a conservative upper bound even for affine shears.
                var m=item.World; float scale=MathF.Sqrt(m.M11*m.M11+m.M12*m.M12+m.M13*m.M13+
                    m.M21*m.M21+m.M22*m.M22+m.M23*m.M23+m.M31*m.M31+m.M32*m.M32+m.M33*m.M33);
                if(FrustumCulling && !frustum.ContainsSphere(center,group.Radius*scale)) { culled++; continue; }
                var a=item.Tint; var b=group.Template.Tint;
                group.Scratch[count++]=new MeshInstanceData(item.World,new RenderColor(a.R*b.R,a.G*b.G,a.B*b.B,a.A*b.A));
            }
            if(count==0)continue;
            renderer.DrawMeshInstances(group.Template,group.Scratch.AsSpan(0,count)); visible+=count; batches++;
        }
        Statistics=new(_instanceCount,visible,culled,batches); return visible;
    }
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    private static bool Finite(Matrix4x4 m)=>float.IsFinite(m.M11)&&float.IsFinite(m.M12)&&float.IsFinite(m.M13)&&float.IsFinite(m.M14)&&
        float.IsFinite(m.M21)&&float.IsFinite(m.M22)&&float.IsFinite(m.M23)&&float.IsFinite(m.M24)&&float.IsFinite(m.M31)&&float.IsFinite(m.M32)&&
        float.IsFinite(m.M33)&&float.IsFinite(m.M34)&&float.IsFinite(m.M41)&&float.IsFinite(m.M42)&&float.IsFinite(m.M43)&&float.IsFinite(m.M44);
}

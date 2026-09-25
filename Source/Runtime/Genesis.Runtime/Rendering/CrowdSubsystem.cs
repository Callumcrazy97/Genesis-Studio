using Genesis.Rendering.Primitives;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
namespace Genesis.Runtime.Rendering;
public sealed class CrowdSubsystem : IInstanceSceneSubsystem
{
    public CrowdGpuInstancer Instances { get; }
    public CrowdSubsystem(int capacity=131072) => Instances=new(capacity);
    public void Update(RuntimeScene scene,GameTime time) { }
    public void FixedUpdate(RuntimeScene scene,float fixedDelta) { }
    public void SubmitMeshes(RuntimeScene scene,MeshDrawCall[] buffer,ref int count,IRenderController renderer) { }
    public int SubmitInstances(RuntimeScene scene,IRenderController renderer)
    {
        var vp=scene.Camera3D.ViewMatrix*scene.Camera3D.ProjectionMatrix;
        return Instances.Submit(renderer,vp);
    }
    public void Dispose()=>Instances.Clear();
}

using Genesis.Shared.Interfaces;
namespace Genesis.Runtime.Scene;
/// <summary>Bulk GPU submission separate from fixed-capacity per-object MeshDrawCall buffers.</summary>
public interface IInstanceSceneSubsystem : ISceneSubsystem
{
    int SubmitInstances(RuntimeScene scene, IRenderController renderer);
}

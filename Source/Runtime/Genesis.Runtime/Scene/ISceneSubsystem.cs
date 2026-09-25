using System;
using Genesis.Runtime.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scene
{
    public interface ISceneSubsystem : IDisposable
    {
        void Update(Genesis.Runtime.RuntimeScene scene, GameTime time);
        void FixedUpdate(Genesis.Runtime.RuntimeScene scene, float fixedDelta);
        void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer);
    }
}

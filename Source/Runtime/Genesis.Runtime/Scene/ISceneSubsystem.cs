using System;
using Genesis.Runtime.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scene
{
    public interface IPostPhysicsSceneSubsystem
    {
        void AfterPhysics(RuntimeScene scene, float fixedDelta);
    }

    /// <summary>
    /// A subsystem with work to do before its room is first shown: collision to build, files to
    /// read. While a room is being prepared behind a loading screen nothing in it moves, so that
    /// work can be done on worker threads and waited for, instead of on the game's thread in the
    /// room's first frames.
    /// </summary>
    public interface IRoomWarmUpSubsystem
    {
        /// <summary>
        /// Called once a frame while the room waits to be shown. Starts or advances the
        /// preparation without holding the frame up, and returns true once nothing is left.
        /// </summary>
        bool WarmUp(RuntimeScene scene);
    }

    public interface ISceneSubsystem : IDisposable
    {
        void Update(Genesis.Runtime.RuntimeScene scene, GameTime time);
        void FixedUpdate(Genesis.Runtime.RuntimeScene scene, float fixedDelta);
        void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer);
    }
}

using System.Numerics;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>Collects Draw3D / procedural mesh submissions for the 3D render pass.</summary>
    public sealed class ObjectDrawSubsystem : ISceneSubsystem
    {
        private readonly string _projectPath;

        public ObjectDrawSubsystem(string projectPath) => _projectPath = projectPath ?? "";

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void Update(RuntimeScene scene, GameTime time) { }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer)
        {
            if (scene?.World == null || renderer == null) return;
            Vector3 eye = scene.Camera3D.Position;
            Vector3 forward = scene.Camera3D.Forward;
            Matrix4x4 viewProjection = scene.Camera3D.ViewMatrix * scene.Camera3D.ProjectionMatrix;
            ObjectDrawPass.SubmitMeshes3D(scene.World, _projectPath, buffer, ref count, renderer, eye, forward, viewProjection);
        }

        public void Render2D(RuntimeScene scene, IRenderCommandSink commands, IRenderController renderer, float camX, float camY, float zoom)
        {
            if (scene?.World == null || renderer == null || commands == null) return;
            ObjectDrawPass.Render2D(scene.World, _projectPath, commands, renderer, camX, camY, zoom);
        }

        public void Dispose() { }
    }
}

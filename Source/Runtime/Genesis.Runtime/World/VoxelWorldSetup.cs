using Genesis.Runtime;
using Genesis.Runtime.Scene;

namespace Genesis.World
{
    public static class VoxelWorldSetup
    {
        /// <summary>Adds voxel renderer, wires streaming, and applies scene defaults.</summary>
        public static VoxelWorldRenderer AddVoxelWorld(
            RuntimeScene scene,
            VoxelWorld world,
            IVoxelGenerator generator,
            VoxelPalette palette = null)
        {
            var renderer = new VoxelWorldRenderer(world, generator, palette);
            renderer.AttachStreaming(scene.Streaming);
            scene.AddSubsystem(renderer);
            SceneDefaults.ApplyVoxelWorld(scene);
            return renderer;
        }
    }
}

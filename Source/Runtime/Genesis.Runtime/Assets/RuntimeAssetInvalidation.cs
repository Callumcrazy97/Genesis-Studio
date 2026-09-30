using Genesis.Runtime.Particles;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Assets
{
    /// <summary>One cache-invalidation boundary shared by Studio previews and the F5 player.</summary>
    public static class RuntimeAssetInvalidation
    {
        public static void Invalidate(string projectPath, IRenderController renderer = null)
        {
            // Frame-path caches (sprite frames, textures, models) treat a new generation as
            // immediate expiry, so explicit invalidation never waits for their fallback poll.
            Genesis.Shared.Assets.RuntimeAssetPolicy.Invalidate();
            ResourceNames.Invalidate(projectPath);
            SpriteAssetLoader.ClearCache();
            Genesis.Runtime.Scene.RoomTileCollisionMap.ClearCache();
            ParticleAssetLoader.ClearCache();
            ScriptAssetRegistry.ClearCache();
            TexturePathResolver.InvalidateIndex(projectPath);
            ObjectDrawPass.InvalidateAssets(renderer);
        }
    }
}

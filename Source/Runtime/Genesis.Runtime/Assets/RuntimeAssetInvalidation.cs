using System.Collections.Generic;
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
            InvalidateCaches(projectPath, renderer);
            ScriptAssetRegistry.ClearCache();
        }

        /// <summary>
        /// The same for a game that is running (live reload). Its Scripts are read again at once
        /// rather than dropped: a game calls them every frame, and dropped they were unknown
        /// until something loaded them again, which a room rebuild that was turned down (a room
        /// file still being written) or a terrain-only reload never did. A Script that does not
        /// compile keeps its earlier version. Returns the Scripts that did; when the Scripts
        /// could not be read at all, <paramref name="scriptFailure"/> says why and all are kept.
        /// </summary>
        public static IReadOnlyList<string> InvalidateRunningGame(string projectPath, IRenderController renderer, out string scriptFailure)
        {
            InvalidateCaches(projectPath, renderer);
            return ScriptAssetRegistry.Refresh(projectPath, out scriptFailure);
        }

        private static void InvalidateCaches(string projectPath, IRenderController renderer)
        {
            // Frame-path caches (sprite frames, textures, models) treat a new generation as
            // immediate expiry, so explicit invalidation never waits for their fallback poll.
            Genesis.Shared.Assets.RuntimeAssetPolicy.Invalidate();
            ResourceNames.Invalidate(projectPath);
            SpriteAssetLoader.ClearCache();
            Genesis.Runtime.Scene.RoomTileCollisionMap.ClearCache();
            ParticleAssetLoader.ClearCache();
            TexturePathResolver.InvalidateIndex(projectPath);
            ObjectDrawPass.InvalidateAssets(renderer);
        }
    }
}

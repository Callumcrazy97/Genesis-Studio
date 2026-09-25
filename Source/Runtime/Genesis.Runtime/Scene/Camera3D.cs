using Genesis.Rendering.D3dMath;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// Runtime compatibility name for the canonical backend-neutral camera now owned by
    /// Genesis.Shared. Existing runtime/editor callers keep the Camera3D API unchanged.
    /// </summary>
    public sealed class Camera3D : Camera
    {
        public Frustum GetFrustum() => new Frustum(ViewProjection);
    }
}

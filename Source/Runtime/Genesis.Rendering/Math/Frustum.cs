using System.Numerics;
using Genesis.Shared.Rendering;

namespace Genesis.Rendering.D3dMath
{
    /// <summary>
    /// Compatibility facade for the canonical frustum now owned by Genesis.Shared.
    /// </summary>
    public readonly struct Frustum
    {
        private readonly CameraFrustum _shared;

        public readonly Vector4 Left;
        public readonly Vector4 Right;
        public readonly Vector4 Bottom;
        public readonly Vector4 Top;
        public readonly Vector4 Near;
        public readonly Vector4 Far;

        public Frustum(in Matrix4x4 viewProjection)
        {
            _shared = new CameraFrustum(viewProjection);
            Left = _shared.Left;
            Right = _shared.Right;
            Bottom = _shared.Bottom;
            Top = _shared.Top;
            Near = _shared.Near;
            Far = _shared.Far;
        }

        public bool ContainsSphere(Vector3 center, float radius) => _shared.ContainsSphere(center, radius);

        public bool IntersectsAabb(Vector3 min, Vector3 max) => _shared.IntersectsAabb(min, max);

        public bool ContainsBox(Vector3 min, Vector3 max) => _shared.ContainsBox(min, max);
    }

    public static class MatrixScaleHelper
    {
        public static float MaxScale(in Matrix4x4 matrix) => Conventions.MaxScale(matrix);
    }
}

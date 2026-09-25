using System;
using System.Numerics;

namespace Genesis.Shared.Rendering
{
    /// <summary>
    /// Canonical Genesis frustum extracted from a row-vector view-projection matrix.
    /// </summary>
    public readonly struct CameraFrustum
    {
        public readonly Vector4 Left;
        public readonly Vector4 Right;
        public readonly Vector4 Bottom;
        public readonly Vector4 Top;
        public readonly Vector4 Near;
        public readonly Vector4 Far;

        public CameraFrustum(in Matrix4x4 viewProjection)
        {
            Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
            Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
            Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
            Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

            Left = NormalizePlane(c3 + c0);
            Right = NormalizePlane(c3 - c0);
            Bottom = NormalizePlane(c3 + c1);
            Top = NormalizePlane(c3 - c1);
            Near = NormalizePlane(c2);
            Far = NormalizePlane(c3 - c2);
        }

        public bool ContainsSphere(Vector3 center, float radius)
        {
            if (Dot(Left, center) < -radius) return false;
            if (Dot(Right, center) < -radius) return false;
            if (Dot(Bottom, center) < -radius) return false;
            if (Dot(Top, center) < -radius) return false;
            if (Dot(Near, center) < -radius) return false;
            if (Dot(Far, center) < -radius) return false;
            return true;
        }

        public bool IntersectsAabb(Vector3 min, Vector3 max)
        {
            ReadOnlySpan<Vector4> planes = stackalloc Vector4[] { Left, Right, Bottom, Top, Near, Far };
            foreach (Vector4 plane in planes)
            {
                Vector3 p;
                p.X = plane.X >= 0f ? max.X : min.X;
                p.Y = plane.Y >= 0f ? max.Y : min.Y;
                p.Z = plane.Z >= 0f ? max.Z : min.Z;
                if (Dot(plane, p) < 0f) return false;
            }

            return true;
        }

        public bool ContainsBox(Vector3 min, Vector3 max) => IntersectsAabb(min, max);

        private static float Dot(Vector4 plane, Vector3 point) =>
            plane.X * point.X + plane.Y * point.Y + plane.Z * point.Z + plane.W;

        private static Vector4 NormalizePlane(Vector4 plane)
        {
            float length = MathF.Sqrt(
                plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
            return length > 1e-6f ? plane / length : plane;
        }
    }
}

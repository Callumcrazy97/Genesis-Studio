using System.Numerics;
using SharedConventions = Genesis.Shared.Rendering.Conventions;

namespace Genesis.Rendering.D3dMath
{
    /// <summary>
    /// Compatibility facade for existing renderer code. Matrix convention ownership now lives in
    /// Genesis.Shared.Rendering.Conventions so future backends consume the same definitions.
    /// </summary>
    public static class D3dMatrixHelper
    {
        public static Matrix4x4 CreatePerspectiveLh(
            float fovRadians,
            float aspect,
            float nearPlane,
            float farPlane) =>
            SharedConventions.CreatePerspective(fovRadians, aspect, nearPlane, farPlane);

        public static Matrix4x4 CreateLookAtLh(Vector3 eye, Vector3 target, Vector3 up) =>
            SharedConventions.CreateLookAt(eye, target, up);

        public static Matrix4x4 CreateOrthographicLh(
            float width,
            float height,
            float nearPlane,
            float farPlane) =>
            SharedConventions.CreateOrthographic(width, height, nearPlane, farPlane);
    }
}

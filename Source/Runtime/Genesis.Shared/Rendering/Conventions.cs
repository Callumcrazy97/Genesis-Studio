using System;
using System.Numerics;

namespace Genesis.Shared.Rendering
{
    /// <summary>
    /// Genesis's backend-neutral coordinate, projection, depth, viewport, and winding conventions.
    /// </summary>
    /// <remarks>
    /// Phase 1 deliberately records the rendering contract Genesis already uses so moving the
    /// convention into Shared does not change existing scenes. A later depth/handedness migration
    /// must change this type and its conformance tests together rather than letting each backend
    /// invent its own rules.
    /// </remarks>
    public static class Conventions
    {
        /// <summary>World up axis.</summary>
        public static Vector3 Up => Vector3.UnitY;

        /// <summary>Default camera forward. Yaw zero looks down world -Z.</summary>
        public static Vector3 Forward => -Vector3.UnitZ;

        /// <summary>
        /// Camera-right for Genesis's current left-handed view basis at the default orientation.
        /// This is -X because the existing view matrix uses cross(Up, Forward).
        /// </summary>
        public static Vector3 Right => -Vector3.UnitX;

        /// <summary>Genesis currently uses conventional D3D 0..1 depth, not reversed-Z.</summary>
        public const bool ReversedZ = false;

        /// <summary>Depth-buffer clear value for the current LessEqual depth policy.</summary>
        public const float DepthClear = 1f;

        /// <summary>NDC depth of the near plane under the current projection.</summary>
        public const float DepthAtNearPlane = 0f;

        /// <summary>NDC depth of the finite far plane under the current projection.</summary>
        public const float DepthAtFarPlane = 1f;

        /// <summary>Mesh fronts are counter-clockwise, with cross products aligned to outward normals.</summary>
        public const bool FrontCounterClockwise = true;

        /// <summary>Create the API-neutral viewport used by Genesis's current backends.</summary>
        public static RenderViewport CreateViewport(float width, float height)
        {
            if (width <= 0f) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0f) throw new ArgumentOutOfRangeException(nameof(height));
            return new RenderViewport(0f, 0f, width, height, 0f, 1f);
        }

        /// <summary>
        /// Convert row-vector clip space to a top-left-origin pixel coordinate and 0..1 depth.
        /// </summary>
        public static Vector3 ClipToPixel(Vector4 clip, float width, float height)
        {
            if (clip.W <= 0f)
            {
                throw new ArgumentException(
                    "Clip-space W must be positive; the point is at or behind the eye.",
                    nameof(clip));
            }

            RenderViewport viewport = CreateViewport(width, height);
            Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
            return new Vector3(
                viewport.X + (ndc.X + 1f) * viewport.Width * 0.5f,
                viewport.Y + (1f - ndc.Y) * viewport.Height * 0.5f,
                ndc.Z);
        }

        /// <summary>Create Genesis's current finite-far, left-handed perspective projection.</summary>
        public static Matrix4x4 CreatePerspective(float fovRadians, float aspect, float nearPlane, float farPlane)
        {
            float yScale = 1f / MathF.Tan(fovRadians * 0.5f);
            float xScale = yScale / aspect;
            float zf = farPlane / (farPlane - nearPlane);
            return new Matrix4x4(
                xScale, 0f,     0f,              0f,
                0f,     yScale, 0f,              0f,
                0f,     0f,     zf,              1f,
                0f,     0f,     -nearPlane * zf, 0f);
        }

        /// <summary>Create Genesis's current left-handed row-vector view matrix.</summary>
        public static Matrix4x4 CreateLookAt(Vector3 eye, Vector3 target, Vector3 up)
        {
            Vector3 z = Vector3.Normalize(target - eye);
            Vector3 x = Vector3.Normalize(Vector3.Cross(up, z));
            Vector3 y = Vector3.Cross(z, x);
            return new Matrix4x4(
                x.X, y.X, z.X, 0f,
                x.Y, y.Y, z.Y, 0f,
                x.Z, y.Z, z.Z, 0f,
                -Vector3.Dot(x, eye),
                -Vector3.Dot(y, eye),
                -Vector3.Dot(z, eye),
                1f);
        }

        /// <summary>Create Genesis's current finite-depth left-handed orthographic projection.</summary>
        public static Matrix4x4 CreateOrthographic(float width, float height, float nearPlane, float farPlane)
        {
            float zf = 1f / (farPlane - nearPlane);
            return new Matrix4x4(
                2f / width, 0f,          0f,              0f,
                0f,         2f / height, 0f,              0f,
                0f,         0f,          zf,              0f,
                0f,         0f,          -nearPlane * zf, 1f);
        }

        /// <summary>Create Genesis's current off-centre left-handed orthographic projection.</summary>
        public static Matrix4x4 CreateOrthographicOffCenter(
            float left,
            float right,
            float bottom,
            float top,
            float nearPlane,
            float farPlane)
        {
            float zf = 1f / (farPlane - nearPlane);
            return new Matrix4x4(
                2f / (right - left), 0f,                    0f,              0f,
                0f,                  2f / (top - bottom),   0f,              0f,
                0f,                  0f,                    zf,              0f,
                (left + right) / (left - right),
                (top + bottom) / (bottom - top),
                -nearPlane * zf,
                1f);
        }

        /// <summary>Return the current camera forward direction for yaw/pitch radians.</summary>
        public static Vector3 DirectionFromYawPitch(float yaw, float pitch) => new(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            -MathF.Cos(yaw) * MathF.Cos(pitch));

        /// <summary>Largest axis scale carried by a row-vector world matrix.</summary>
        public static float MaxScale(in Matrix4x4 matrix)
        {
            float sx = new Vector3(matrix.M11, matrix.M12, matrix.M13).LengthSquared();
            float sy = new Vector3(matrix.M21, matrix.M22, matrix.M23).LengthSquared();
            float sz = new Vector3(matrix.M31, matrix.M32, matrix.M33).LengthSquared();
            return MathF.Sqrt(MathF.Max(sx, MathF.Max(sy, sz)));
        }
    }

    /// <summary>Backend-neutral viewport description.</summary>
    public readonly record struct RenderViewport(
        float X,
        float Y,
        float Width,
        float Height,
        float MinDepth,
        float MaxDepth);
}

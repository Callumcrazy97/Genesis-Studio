using System;
using System.Numerics;
using SharedConventions = Genesis.Shared.Rendering.Conventions;

namespace Genesis.Runtime.Core
{
    public static class MathUtil
    {
        public const float DegToRad = MathF.PI / 180f;
        public const float RadToDeg = 180f / MathF.PI;

        public static float ToRadians(float degrees) => degrees * DegToRad;
        public static float ToDegrees(float radians) => radians * RadToDeg;

        public static float NormalizeDegrees(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }

        public static float WrapAngle(float radians)
        {
            radians %= MathF.Tau;
            if (radians > MathF.PI) radians -= MathF.Tau;
            if (radians < -MathF.PI) radians += MathF.Tau;
            return radians;
        }

        public static Matrix4x4 PerspectiveFovLH(float fovRadians, float aspect, float zNear, float zFar) =>
            SharedConventions.CreatePerspective(fovRadians, aspect, zNear, zFar);

        public static Matrix4x4 OrthoLH(float width, float height, float zNear, float zFar) =>
            SharedConventions.CreateOrthographic(width, height, zNear, zFar);

        public static Matrix4x4 OrthoOffCenterLH(
            float left,
            float right,
            float bottom,
            float top,
            float zNear,
            float zFar) =>
            SharedConventions.CreateOrthographicOffCenter(left, right, bottom, top, zNear, zFar);

        public static Matrix4x4 LookAtLH(Vector3 eye, Vector3 target, Vector3 up) =>
            SharedConventions.CreateLookAt(eye, target, up);

        public static Vector3 DirectionFromYawPitch(float yaw, float pitch) =>
            SharedConventions.DirectionFromYawPitch(yaw, pitch);

        public static float MaxScale(in Matrix4x4 matrix) => SharedConventions.MaxScale(matrix);
    }
}

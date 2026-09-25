using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>Row-vector, zero-to-one depth reflection projection, shared across GPU backends.</summary>
public static class PlanarReflectionMath
{
    public static bool TryCreate(Matrix4x4 view, Matrix4x4 projection, Vector3 camera, float height,
        out Matrix4x4 reflectedView, out Matrix4x4 clippedProjection, out Vector3 reflectedCamera)
    {
        Matrix4x4 mirror = Matrix4x4.CreateReflection(new Plane(Vector3.UnitY, -height));
        reflectedView = mirror * view;
        reflectedCamera = Vector3.Transform(camera, mirror);
        clippedProjection = projection;
        if (!float.IsFinite(height) || camera.Y <= height + .05f) return false;
        Matrix4x4 vp = reflectedView * projection;
        if (!Matrix4x4.Invert(vp, out Matrix4x4 inverse) || !Matrix4x4.Invert(reflectedView, out var inverseView)) return false;
        Vector4 plane = new(0, 1, 0, -height - .02f);
        Vector4 clipPlane = Vector4.Transform(plane, Matrix4x4.Transpose(inverse));
        Vector4 corner = Vector4.Transform(new Vector4(clipPlane.X >= 0 ? 1 : -1, clipPlane.Y >= 0 ? 1 : -1, 1, 1), inverse);
        float denominator = Vector4.Dot(plane, corner);
        if (!float.IsFinite(denominator) || denominator <= 1e-6f) return false;
        Vector4 near = plane / denominator;
        vp.M13 = near.X; vp.M23 = near.Y; vp.M33 = near.Z; vp.M43 = near.W;
        clippedProjection = inverseView * vp;
        return true;
    }
}

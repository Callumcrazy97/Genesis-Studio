using System;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// 3D vector and angle maths. Pure functions with no engine dependency, so unlike most of the 3D
// surface these are fully real rather than best-effort.
//
// Conventions, stated once here and repeated in the descriptions where they matter:
//   • Angles cross the script boundary in DEGREES; radians are internal only.
//   • Yaw 0 degrees looks down +Z, and yaw increases turning toward +X. This matches the view
//     convention the renderer already uses.
//   • PGSL cannot return a tuple, so vector-valued results are split into X/Y/Z commands.
//   • Degenerate input returns 0, never NaN. A NaN loose in a script variable poisons every
//     later calculation silently, which is far worse than a visible zero.
public static partial class PgslCommands
{
    #region 3D Math

    private const double DegreesPerRadian = 180.0 / Math.PI;
    private const double RadiansPerDegree = Math.PI / 180.0;

    [PgslCommand("DistanceBetweenPoints3D", "DistanceBetweenPoints3D(x1, y1, z1, x2, y2, z2) -> number", "Straight-line distance in 3D", "3D Math")]
    public static double DistanceBetweenPoints3D(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double dz = z2 - z1;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    [PgslCommand("PointDirection3D", "PointDirection3D(x1, y1, z1, x2, y2, z2) -> number", "Yaw in degrees toward a point, on the XZ plane", "3D Math")]
    public static double PointDirection3D(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        double dx = x2 - x1;
        double dz = z2 - z1;
        if (Math.Abs(dx) < 1e-12 && Math.Abs(dz) < 1e-12) return 0;

        // Yaw 0 looks down +Z, so Z is the "adjacent" axis here, not X.
        double degrees = Math.Atan2(dx, dz) * DegreesPerRadian;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    [PgslCommand("AngleBetweenPoints3D", "AngleBetweenPoints3D(x1, y1, z1, x2, y2, z2) -> number", "Alias of PointDirection3D", "3D Math")]
    public static double AngleBetweenPoints3D(double x1, double y1, double z1, double x2, double y2, double z2) =>
        PointDirection3D(x1, y1, z1, x2, y2, z2);

    [PgslCommand("PointPitch3D", "PointPitch3D(x1, y1, z1, x2, y2, z2) -> number", "Pitch in degrees toward a point; positive is upward", "3D Math")]
    public static double PointPitch3D(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double dz = z2 - z1;
        double horizontal = Math.Sqrt((dx * dx) + (dz * dz));
        if (horizontal < 1e-12 && Math.Abs(dy) < 1e-12) return 0;
        return Math.Atan2(dy, horizontal) * DegreesPerRadian;
    }

    [PgslCommand("AngleBetweenVectors3D", "AngleBetweenVectors3D(x1, y1, z1, x2, y2, z2) -> number", "Angle in degrees between two directions", "3D Math")]
    public static double AngleBetweenVectors3D(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        double lengthA = Math.Sqrt((x1 * x1) + (y1 * y1) + (z1 * z1));
        double lengthB = Math.Sqrt((x2 * x2) + (y2 * y2) + (z2 * z2));
        if (lengthA < 1e-12 || lengthB < 1e-12) return 0;

        // Clamp before Acos: floating-point error can push the quotient a hair past ±1, and Acos
        // of 1.0000000001 is NaN.
        double cosine = Math.Clamp(((x1 * x2) + (y1 * y2) + (z1 * z2)) / (lengthA * lengthB), -1d, 1d);
        return Math.Acos(cosine) * DegreesPerRadian;
    }

    [PgslCommand("DotProduct3D", "DotProduct3D(x1, y1, z1, x2, y2, z2) -> number", "Dot product of two vectors", "3D Math")]
    public static double DotProduct3D(double x1, double y1, double z1, double x2, double y2, double z2) =>
        (x1 * x2) + (y1 * y2) + (z1 * z2);

    [PgslCommand("CrossProductX", "CrossProductX(x1, y1, z1, x2, y2, z2) -> number", "X of the cross product", "3D Math")]
    public static double CrossProductX(double x1, double y1, double z1, double x2, double y2, double z2) =>
        (y1 * z2) - (z1 * y2);

    [PgslCommand("CrossProductY", "CrossProductY(x1, y1, z1, x2, y2, z2) -> number", "Y of the cross product", "3D Math")]
    public static double CrossProductY(double x1, double y1, double z1, double x2, double y2, double z2) =>
        (z1 * x2) - (x1 * z2);

    [PgslCommand("CrossProductZ", "CrossProductZ(x1, y1, z1, x2, y2, z2) -> number", "Z of the cross product", "3D Math")]
    public static double CrossProductZ(double x1, double y1, double z1, double x2, double y2, double z2) =>
        (x1 * y2) - (y1 * x2);

    [PgslCommand("VectorLength3D", "VectorLength3D(x, y, z) -> number", "Magnitude of a vector", "3D Math")]
    public static double VectorLength3D(double x, double y, double z) =>
        Math.Sqrt((x * x) + (y * y) + (z * z));

    [PgslCommand("VectorNormaliseX", "VectorNormaliseX(x, y, z) -> number", "X of the unit vector; 0 for a zero vector", "3D Math")]
    public static double VectorNormaliseX(double x, double y, double z) => Normalise(x, y, z, 0);

    [PgslCommand("VectorNormaliseY", "VectorNormaliseY(x, y, z) -> number", "Y of the unit vector; 0 for a zero vector", "3D Math")]
    public static double VectorNormaliseY(double x, double y, double z) => Normalise(x, y, z, 1);

    [PgslCommand("VectorNormaliseZ", "VectorNormaliseZ(x, y, z) -> number", "Z of the unit vector; 0 for a zero vector", "3D Math")]
    public static double VectorNormaliseZ(double x, double y, double z) => Normalise(x, y, z, 2);

    private static double Normalise(double x, double y, double z, int component)
    {
        double length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (length < 1e-12) return 0;
        return component switch
        {
            0 => x / length,
            1 => y / length,
            _ => z / length,
        };
    }

    [PgslCommand("ForwardX", "ForwardX(yaw, pitch) -> number", "X of the forward unit vector; yaw 0 looks down +Z", "3D Math")]
    public static double ForwardX(double yaw, double pitch) =>
        Math.Sin(yaw * RadiansPerDegree) * Math.Cos(pitch * RadiansPerDegree);

    [PgslCommand("ForwardY", "ForwardY(yaw, pitch) -> number", "Y of the forward unit vector", "3D Math")]
    public static double ForwardY(double yaw, double pitch) => Math.Sin(pitch * RadiansPerDegree);

    [PgslCommand("ForwardZ", "ForwardZ(yaw, pitch) -> number", "Z of the forward unit vector; yaw 0 looks down +Z", "3D Math")]
    public static double ForwardZ(double yaw, double pitch) =>
        Math.Cos(yaw * RadiansPerDegree) * Math.Cos(pitch * RadiansPerDegree);

    [PgslCommand("RightX", "RightX(yaw) -> number", "X of the right-hand unit vector for a yaw", "3D Math")]
    public static double RightX(double yaw) => Math.Cos(yaw * RadiansPerDegree);

    [PgslCommand("RightZ", "RightZ(yaw) -> number", "Z of the right-hand unit vector for a yaw", "3D Math")]
    public static double RightZ(double yaw) => -Math.Sin(yaw * RadiansPerDegree);

    [PgslCommand("LerpVectorX", "LerpVectorX(x1, y1, z1, x2, y2, z2, t) -> number", "X of a linear blend between two points", "3D Math")]
    public static double LerpVectorX(double x1, double y1, double z1, double x2, double y2, double z2, double t) =>
        x1 + ((x2 - x1) * t);

    [PgslCommand("LerpVectorY", "LerpVectorY(x1, y1, z1, x2, y2, z2, t) -> number", "Y of a linear blend between two points", "3D Math")]
    public static double LerpVectorY(double x1, double y1, double z1, double x2, double y2, double z2, double t) =>
        y1 + ((y2 - y1) * t);

    [PgslCommand("LerpVectorZ", "LerpVectorZ(x1, y1, z1, x2, y2, z2, t) -> number", "Z of a linear blend between two points", "3D Math")]
    public static double LerpVectorZ(double x1, double y1, double z1, double x2, double y2, double z2, double t) =>
        z1 + ((z2 - z1) * t);

    [PgslCommand("RotateAroundYX", "RotateAroundYX(x, z, degrees) -> number", "X after rotating a point about the Y axis", "3D Math")]
    public static double RotateAroundYX(double x, double z, double degrees)
    {
        double radians = degrees * RadiansPerDegree;
        return (x * Math.Cos(radians)) + (z * Math.Sin(radians));
    }

    [PgslCommand("RotateAroundYZ", "RotateAroundYZ(x, z, degrees) -> number", "Z after rotating a point about the Y axis", "3D Math")]
    public static double RotateAroundYZ(double x, double z, double degrees)
    {
        double radians = degrees * RadiansPerDegree;
        return (z * Math.Cos(radians)) - (x * Math.Sin(radians));
    }

    [PgslCommand("MoveTowards3DX", "MoveTowards3DX(x1, y1, z1, x2, y2, z2, distance) -> number", "X after stepping toward a point", "3D Math")]
    public static double MoveTowards3DX(double x1, double y1, double z1, double x2, double y2, double z2, double distance) =>
        x1 + (Normalise(x2 - x1, y2 - y1, z2 - z1, 0) * distance);

    [PgslCommand("MoveTowards3DY", "MoveTowards3DY(x1, y1, z1, x2, y2, z2, distance) -> number", "Y after stepping toward a point", "3D Math")]
    public static double MoveTowards3DY(double x1, double y1, double z1, double x2, double y2, double z2, double distance) =>
        y1 + (Normalise(x2 - x1, y2 - y1, z2 - z1, 1) * distance);

    [PgslCommand("MoveTowards3DZ", "MoveTowards3DZ(x1, y1, z1, x2, y2, z2, distance) -> number", "Z after stepping toward a point", "3D Math")]
    public static double MoveTowards3DZ(double x1, double y1, double z1, double x2, double y2, double z2, double distance) =>
        z1 + (Normalise(x2 - x1, y2 - y1, z2 - z1, 2) * distance);

    [PgslCommand("AngleDifference", "AngleDifference(a, b) -> number", "Shortest signed difference between two angles, -180..180", "3D Math")]
    public static double AngleDifference(double a, double b)
    {
        double difference = (a - b) % 360;
        if (difference > 180) difference -= 360;
        if (difference < -180) difference += 360;
        return difference;
    }

    [PgslCommand("AngleNormalise", "AngleNormalise(degrees) -> number", "Wrap an angle into 0..360", "3D Math")]
    public static double AngleNormalise(double degrees)
    {
        double wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    #endregion
}

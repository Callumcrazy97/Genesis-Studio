using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Genesis.Runtime.Scene;

/// <summary>
/// Authored room transforms remain local TRS. Editor and initial runtime placement share
/// scale/rotation inheritance; shear is not part of the room or ECS transform schema.
/// </summary>
public static class RoomHierarchyTransforms
{
    /// <summary>Enabled state and dimensional visibility propagate down the authored hierarchy.</summary>
    public static bool IsActive(RoomAsset room, RoomNode node)
    {
        if (room == null || node == null) return false;
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        while (node != null)
        {
            if (!visited.Add(node.Id) || !node.Enabled || !node.Supports(room.Dimension)) return false;
            if (room.Layers.FirstOrDefault(layer => layer.Id == node.LayerId)?.Enabled == false) return false;
            node = Parent(room, node);
        }
        return true;
    }

    public static Matrix4x4 Matrix(RoomTransform value) => Matrix4x4.CreateScale(value.ScaleX, value.ScaleY, value.ScaleZ)
        * Matrix4x4.CreateFromQuaternion(Rotation(value)) * Matrix4x4.CreateTranslation(value.X, value.Y, value.Z);

    public static Quaternion Rotation(RoomTransform value) => Quaternion.CreateFromYawPitchRoll(
        value.RotationY * MathF.PI / 180f, value.RotationX * MathF.PI / 180f, value.RotationZ * MathF.PI / 180f);

    public static RoomTransform World(RoomAsset room, RoomNode node)
    {
        var chain = new Stack<RoomNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (node != null && visited.Add(node.Id))
        {
            chain.Push(node);
            node = Parent(room, node);
        }
        RoomTransform world = chain.Count == 0 ? new RoomTransform() : Clone(chain.Pop().Transform);
        while (chain.Count > 0) world = Compose(world, chain.Pop().Transform);
        return world;
    }

    public static RoomNode Parent(RoomAsset room, RoomNode node) => string.IsNullOrEmpty(node.ParentId)
        ? null : room.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, node.ParentId, StringComparison.OrdinalIgnoreCase));

    public static RoomTransform ParentWorld(RoomAsset room, RoomNode node)
    {
        RoomNode parent = Parent(room, node);
        return parent == null ? new RoomTransform() : World(room, parent);
    }

    public static RoomTransform Compose(RoomTransform parent, RoomTransform local)
    {
        Vector3 position = Vector3.Transform(new(local.X, local.Y, local.Z), Matrix(parent));
        Quaternion rotation = Quaternion.Normalize(Quaternion.Concatenate(Rotation(local), Rotation(parent)));
        return Create(position, rotation, new Vector3(parent.ScaleX * local.ScaleX,
            parent.ScaleY * local.ScaleY, parent.ScaleZ * local.ScaleZ));
    }

    /// <summary>Inverse of the room's TRS inheritance, used for world-space editor operations.</summary>
    public static bool TryLocal(RoomTransform world, RoomTransform parent, out RoomTransform local)
    {
        local = null;
        if (!Matrix4x4.Invert(Matrix(parent), out Matrix4x4 inverse)) return false;
        Vector3 position = Vector3.Transform(new(world.X, world.Y, world.Z), inverse);
        Quaternion rotation = Quaternion.Normalize(Quaternion.Concatenate(Rotation(world), Quaternion.Inverse(Rotation(parent))));
        local = Create(position, rotation, new Vector3(world.ScaleX / parent.ScaleX,
            world.ScaleY / parent.ScaleY, world.ScaleZ / parent.ScaleZ));
        return IsFinite(local);
    }

    /// <summary>
    /// Reparenting must also be representable as a true affine basis change. Reject shear
    /// and singular parents instead of quietly replacing the user's world pose.
    /// </summary>
    public static bool TryReparent(RoomTransform world, RoomTransform parent, out RoomTransform local)
    {
        local = null;
        if (!Matrix4x4.Invert(Matrix(parent), out Matrix4x4 inverse)) return false;
        Matrix4x4 relative = Matrix(world) * inverse;
        if (!Matrix4x4.Decompose(relative, out Vector3 scale, out Quaternion rotation, out Vector3 position)) return false;
        RoomTransform candidate = Create(position, Quaternion.Normalize(rotation), scale);
        if (!IsFinite(candidate) || !NearlyEqual(Matrix(candidate), relative)
            || !NearlyEqual(Matrix(Compose(parent, candidate)), Matrix(world))) return false;
        local = candidate;
        return true;
    }

    public static bool HasAncestor(RoomAsset room, RoomNode node, ISet<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Id };
        while ((node = Parent(room, node)) != null && seen.Add(node.Id))
            if (ids.Contains(node.Id)) return true;
        return false;
    }

    public static Vector3 EulerDegrees(Quaternion rotation)
    {
        Matrix4x4 matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        float pitch = MathF.Asin(Math.Clamp(-matrix.M32, -1f, 1f));
        float yaw, roll;
        if (MathF.Abs(matrix.M32) > .999999f) { yaw = MathF.Atan2(-matrix.M13, matrix.M11); roll = 0; }
        else { yaw = MathF.Atan2(matrix.M31, matrix.M33); roll = MathF.Atan2(matrix.M12, matrix.M22); }
        return new Vector3(pitch, yaw, roll) * (180f / MathF.PI);
    }

    public static RoomTransform Create(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Vector3 euler = EulerDegrees(rotation);
        return new RoomTransform { X = position.X, Y = position.Y, Z = position.Z,
            RotationX = euler.X, RotationY = euler.Y, RotationZ = euler.Z,
            ScaleX = scale.X, ScaleY = scale.Y, ScaleZ = scale.Z };
    }

    private static bool IsFinite(RoomTransform value) => value.Position.All(float.IsFinite)
        && value.Rotation.All(float.IsFinite) && value.Scale.All(float.IsFinite);

    private static RoomTransform Clone(RoomTransform value) => new()
    {
        X = value.X, Y = value.Y, Z = value.Z,
        RotationX = value.RotationX, RotationY = value.RotationY, RotationZ = value.RotationZ,
        ScaleX = value.ScaleX, ScaleY = value.ScaleY, ScaleZ = value.ScaleZ,
    };

    public static bool NearlyEqual(Matrix4x4 a, Matrix4x4 b, float tolerance = .0001f)
    {
        float[] first = [a.M11, a.M12, a.M13, a.M14, a.M21, a.M22, a.M23, a.M24,
            a.M31, a.M32, a.M33, a.M34, a.M41, a.M42, a.M43, a.M44];
        float[] second = [b.M11, b.M12, b.M13, b.M14, b.M21, b.M22, b.M23, b.M24,
            b.M31, b.M32, b.M33, b.M34, b.M41, b.M42, b.M43, b.M44];
        for (int i = 0; i < first.Length; i++)
            if (!float.IsFinite(first[i]) || !float.IsFinite(second[i])
                || MathF.Abs(first[i] - second[i]) > tolerance * MathF.Max(1f, MathF.Abs(second[i]))) return false;
        return true;
    }
}

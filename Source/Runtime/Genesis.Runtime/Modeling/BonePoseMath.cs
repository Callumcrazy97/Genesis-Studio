#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// The bone turns that look-ats and reaches put on top of a pose, in the model's own space: shared by
/// an Animation Controller's bones (AnimationBoneLookAt, AnimationBoneReach) and by any model
/// instance's look and reach (<see cref="ModelBoneTargets"/>).
/// </summary>
internal static class BonePoseMath
{
    /// <summary>
    /// Each bone's model-space transform from a local pose, into <paramref name="world"/>, in
    /// whatever order the rig lists its bones (imported rigs can list children before their parents).
    /// </summary>
    public static void ModelSpace(List<GModelBone> bones, Matrix4x4[] local, Matrix4x4[] world, bool[] resolved)
    {
        Array.Clear(resolved);
        for (int i = 0; i < local.Length; i++) Resolve(bones, local, world, resolved, i, 0);
    }

    private static Matrix4x4 Resolve(List<GModelBone> bones, Matrix4x4[] local, Matrix4x4[] world, bool[] resolved, int bone, int depth)
    {
        if (resolved[bone]) return world[bone];
        int parent = bones[bone].ParentIndex;
        world[bone] = parent >= 0 && parent < local.Length && parent != bone && depth < local.Length
            ? local[bone] * Resolve(bones, local, world, resolved, parent, depth + 1) : local[bone];
        resolved[bone] = true;
        return world[bone];
    }

    public static bool IsBone(int index, int count) => index >= 0 && index < count;

    /// <summary>
    /// A bone by name, any case; failing that, one whose name ends with it after a rig's prefix
    /// ("mixamorig:Head" for "Head"). -1 when there is none.
    /// </summary>
    public static int Find(List<GModelBone> bones, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (int i = 0; i < bones.Count; i++)
            if (string.Equals(bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < bones.Count; i++)
        {
            string candidate = bones[i].Name ?? "";
            int cut = candidate.LastIndexOfAny(PrefixMarks);
            if (cut >= 0 && string.Equals(candidate[(cut + 1)..], name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static readonly char[] PrefixMarks = { ':', '|' };

    /// <summary>
    /// Two-bone reach: the upper bone turns so the middle joint lands where the two lengths meet the
    /// point (keeping the way the limb already bends), then the lower bone turns onto the point. A
    /// point out of reach is reached towards, the limb straight. <paramref name="world"/> must hold
    /// the pose's model-space transforms. False when the bone has no two bones above it.
    /// </summary>
    public static bool TwoBoneReach(List<GModelBone> bones, Matrix4x4[] local, Matrix4x4[] world, int end, Vector3 target, float weight)
    {
        int count = local.Length;
        if (!IsBone(end, count) || !(weight > 0f)) return false;
        int mid = bones[end].ParentIndex;
        int upper = IsBone(mid, count) ? bones[mid].ParentIndex : -1;
        if (!IsBone(upper, count) || upper == mid || upper == end) return false;
        Vector3 a = world[upper].Translation, b = world[mid].Translation, c = world[end].Translation;
        float upperLength = Vector3.Distance(a, b), lowerLength = Vector3.Distance(b, c);
        Vector3 toTarget = target - a;
        float distance = toTarget.Length();
        if (upperLength < 1e-5f || lowerLength < 1e-5f || distance < 1e-5f) return false;
        Vector3 along = toTarget / distance;
        distance = Math.Clamp(distance, MathF.Abs(upperLength - lowerLength) + 1e-4f, upperLength + lowerLength - 1e-4f);
        // The way the limb bends: the middle joint's side of the line to the point.
        Vector3 bend = (b - a) - Vector3.Dot(b - a, along) * along;
        if (bend.LengthSquared() < 1e-10f) bend = (c - b) - Vector3.Dot(c - b, along) * along;
        if (bend.LengthSquared() < 1e-10f) bend = Vector3.Cross(along, MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        bend = Vector3.Normalize(bend);
        float cosine = Math.Clamp((upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * upperLength * distance), -1f, 1f);
        Vector3 joint = a + along * (upperLength * cosine) + bend * (upperLength * MathF.Sqrt(1f - cosine * cosine));
        weight = MathF.Min(weight, 1f);
        Quaternion first = Quaternion.Slerp(Quaternion.Identity, Between(b - a, joint - a), weight);
        Matrix4x4 aroundA = Matrix4x4.CreateTranslation(-a) * Matrix4x4.CreateFromQuaternion(first) * Matrix4x4.CreateTranslation(a);
        Matrix4x4 upperWorld = world[upper] * aroundA, midWorld = world[mid] * aroundA, endWorld = world[end] * aroundA;
        Vector3 movedJoint = midWorld.Translation, movedEnd = endWorld.Translation;
        Vector3 reachPoint = a + along * distance;
        Quaternion second = Quaternion.Slerp(Quaternion.Identity, Between(movedEnd - movedJoint, reachPoint - movedJoint), weight);
        midWorld *= Matrix4x4.CreateTranslation(-movedJoint) * Matrix4x4.CreateFromQuaternion(second) * Matrix4x4.CreateTranslation(movedJoint);
        int above = bones[upper].ParentIndex;
        Matrix4x4 aboveWorld = IsBone(above, count) ? world[above] : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(aboveWorld, out Matrix4x4 aboveInverse) || !Matrix4x4.Invert(upperWorld, out Matrix4x4 upperInverse)) return false;
        local[upper] = upperWorld * aboveInverse;
        local[mid] = midWorld * upperInverse;
        return true;
    }

    /// <summary>
    /// Turns a bone about its own position towards a point: across (yaw) and up or down (pitch) from
    /// the way it faces in the pose, each kept within its limit (radians), by <paramref name="weight"/>
    /// of the way. The bone faces the model's <paramref name="modelForward"/> in the bind pose, its up
    /// the model's up. <paramref name="world"/> must hold the pose's model-space transforms.
    /// </summary>
    public static bool LookAt(List<GModelBone> bones, Matrix4x4[] local, Matrix4x4[] world, int bone, Vector3 target,
        Vector3 modelForward, float yawLimit, float pitchLimit, float weight)
    {
        int count = local.Length;
        if (!IsBone(bone, count) || !(weight > 0f)) return false;
        Matrix4x4 bind = Matrix4x4.Identity;
        for (int i = bone, steps = 0; IsBone(i, count) && steps <= count; i = bones[i].ParentIndex, steps++)
            bind *= bones[i].BindLocal;
        if (!Matrix4x4.Invert(bind, out Matrix4x4 bindInverse)) return false;
        Matrix4x4 pose = world[bone];
        Vector3 position = pose.Translation;
        Vector3 facing = Vector3.TransformNormal(Vector3.TransformNormal(modelForward, bindInverse), pose);
        Vector3 up = Vector3.TransformNormal(Vector3.TransformNormal(Vector3.UnitY, bindInverse), pose);
        Vector3 wanted = target - position;
        if (facing.LengthSquared() < 1e-10f || wanted.LengthSquared() < 1e-8f) return false;
        facing = Vector3.Normalize(facing);
        up -= Vector3.Dot(up, facing) * facing;
        if (up.LengthSquared() < 1e-10f) up = Vector3.Cross(facing, MathF.Abs(facing.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ);
        up = Vector3.Normalize(up);
        Vector3 side = Vector3.Cross(up, facing);
        wanted = Vector3.Normalize(wanted);
        weight = MathF.Min(weight, 1f);
        float yaw = Math.Clamp(MathF.Atan2(Vector3.Dot(wanted, side), Vector3.Dot(wanted, facing)), -yawLimit, yawLimit) * weight;
        float pitch = Math.Clamp(MathF.Asin(Math.Clamp(Vector3.Dot(wanted, up), -1f, 1f)), -pitchLimit, pitchLimit) * weight;
        if (MathF.Abs(yaw) < 1e-6f && MathF.Abs(pitch) < 1e-6f) return false;
        // Across first, about the head's up; then up or down about its side as turned: no roll.
        Quaternion across = Quaternion.CreateFromAxisAngle(up, yaw);
        Quaternion raise = Quaternion.CreateFromAxisAngle(Vector3.Transform(side, across), -pitch);
        Quaternion turn = Quaternion.Concatenate(across, raise);
        Matrix4x4 turned = pose * Matrix4x4.CreateTranslation(-position) * Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(position);
        int parent = bones[bone].ParentIndex;
        Matrix4x4 parentWorld = IsBone(parent, count) ? world[parent] : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out Matrix4x4 parentInverse)) return false;
        local[bone] = turned * parentInverse;
        return true;
    }

    /// <summary>The shortest turn taking one direction onto another.</summary>
    public static Quaternion Between(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f) return Quaternion.Identity;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-12f) axis = Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }
}

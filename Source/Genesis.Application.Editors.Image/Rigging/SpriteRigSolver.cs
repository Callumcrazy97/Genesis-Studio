using System.Numerics;

namespace Genesis.Application.Editors.Image.Rigging;

public sealed class SpriteRigBone
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Bone";
    public int ParentIndex { get; set; } = -1;
    public Vector2 BindPosition { get; set; }
    public float BindRotation { get; set; }
    public Vector2 BindScale { get; set; } = Vector2.One;
    public float Length { get; set; } = 16f;
    public Matrix3x2 LocalBind { get; internal set; } = Matrix3x2.Identity;
    public Matrix3x2 WorldBind { get; internal set; } = Matrix3x2.Identity;
    public Matrix3x2 InverseBind { get; internal set; } = Matrix3x2.Identity;
}

public readonly record struct SpriteBonePose(Vector2 Position, float Rotation, Vector2 Scale)
{
    public static SpriteBonePose Identity => new(Vector2.Zero, 0f, Vector2.One);
}

public readonly record struct SpriteVertexWeight(int BoneIndex, float Weight);

public sealed class SpriteDeformVertex
{
    public Vector2 Position { get; set; }
    public Vector2 Uv { get; set; }
    public List<SpriteVertexWeight> Weights { get; } = [];

    public void NormalizeAndPrune(int maximumInfluences = 4, float minimumWeight = 0.0001f)
    {
        List<SpriteVertexWeight> valid = Weights
            .Where(weight => weight.BoneIndex >= 0 && weight.Weight >= minimumWeight)
            .OrderByDescending(weight => weight.Weight)
            .Take(Math.Clamp(maximumInfluences, 1, 8))
            .ToList();
        float total = valid.Sum(weight => weight.Weight);
        Weights.Clear();
        if (total <= 0f) return;
        Weights.AddRange(valid.Select(weight => new SpriteVertexWeight(weight.BoneIndex, weight.Weight / total)));
    }
}

public sealed class SpriteRig
{
    public List<SpriteRigBone> Bones { get; } = [];
    public List<SpriteDeformVertex> Vertices { get; } = [];
    public List<ushort> Indices { get; } = [];
}

/// <summary>Industry-style 2D bind-pose, hierarchy, weight and IK baseline.</summary>
public static class SpriteRigSolver
{
    public static IReadOnlyList<string> Validate(SpriteRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        List<string> errors = [];
        for (int i = 0; i < rig.Bones.Count; i++)
        {
            SpriteRigBone bone = rig.Bones[i];
            if (bone.ParentIndex >= rig.Bones.Count)
                errors.Add($"Bone '{bone.Name}' has invalid parent {bone.ParentIndex}.");
            if (bone.ParentIndex == i)
                errors.Add($"Bone '{bone.Name}' cannot parent itself.");
            if (HasCycle(rig.Bones, i))
                errors.Add($"Bone '{bone.Name}' is part of a hierarchy cycle.");
        }

        for (int i = 0; i < rig.Vertices.Count; i++)
        {
            SpriteDeformVertex vertex = rig.Vertices[i];
            if (vertex.Weights.Count > 4)
                errors.Add($"Vertex {i} has more than four bone influences.");
            float total = vertex.Weights.Sum(weight => weight.Weight);
            if (vertex.Weights.Count > 0 && MathF.Abs(total - 1f) > 0.001f)
                errors.Add($"Vertex {i} weights are not normalized ({total:F4}).");
            if (vertex.Weights.Any(weight => (uint)weight.BoneIndex >= (uint)rig.Bones.Count))
                errors.Add($"Vertex {i} references a missing bone.");
        }
        if (rig.Indices.Count % 3 != 0)
            errors.Add("Deform mesh index count must be divisible by three.");
        if (rig.Indices.Any(index => index >= rig.Vertices.Count))
            errors.Add("Deform mesh contains an out-of-range index.");
        return errors;
    }

    public static void BuildBindPose(SpriteRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        IReadOnlyList<string> errors = ValidateHierarchy(rig.Bones);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        for (int i = 0; i < rig.Bones.Count; i++)
        {
            SpriteRigBone bone = rig.Bones[i];
            bone.LocalBind = Transform(bone.BindPosition, bone.BindRotation, bone.BindScale);
            bone.WorldBind = bone.ParentIndex >= 0
                ? bone.LocalBind * rig.Bones[bone.ParentIndex].WorldBind
                : bone.LocalBind;
            if (!Matrix3x2.Invert(bone.WorldBind, out Matrix3x2 inverse))
                throw new InvalidOperationException($"Bone '{bone.Name}' has a singular bind transform.");
            bone.InverseBind = inverse;
        }
    }

    public static Matrix3x2[] BuildSkinPalette(SpriteRig rig, IReadOnlyList<SpriteBonePose> localPoses)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(localPoses);
        if (localPoses.Count != rig.Bones.Count)
            throw new ArgumentException("Pose count must match bone count.", nameof(localPoses));

        Matrix3x2[] worlds = new Matrix3x2[rig.Bones.Count];
        Matrix3x2[] palette = new Matrix3x2[rig.Bones.Count];
        for (int i = 0; i < rig.Bones.Count; i++)
        {
            SpriteRigBone bone = rig.Bones[i];
            SpriteBonePose pose = localPoses[i];
            Matrix3x2 local = Transform(
                bone.BindPosition + pose.Position,
                bone.BindRotation + pose.Rotation,
                bone.BindScale * pose.Scale);
            worlds[i] = bone.ParentIndex >= 0 ? local * worlds[bone.ParentIndex] : local;
            palette[i] = bone.InverseBind * worlds[i];
        }
        return palette;
    }

    public static Vector2[] Deform(SpriteRig rig, IReadOnlyList<Matrix3x2> palette)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(palette);
        Vector2[] output = new Vector2[rig.Vertices.Count];
        for (int i = 0; i < rig.Vertices.Count; i++)
        {
            SpriteDeformVertex vertex = rig.Vertices[i];
            if (vertex.Weights.Count == 0)
            {
                output[i] = vertex.Position;
                continue;
            }

            Vector2 deformed = Vector2.Zero;
            float total = 0f;
            foreach (SpriteVertexWeight weight in vertex.Weights.Take(4))
            {
                if ((uint)weight.BoneIndex >= (uint)palette.Count || weight.Weight <= 0f) continue;
                deformed += Vector2.Transform(vertex.Position, palette[weight.BoneIndex]) * weight.Weight;
                total += weight.Weight;
            }
            output[i] = total > 0f ? deformed / total : vertex.Position;
        }
        return output;
    }

    public static void SolveTwoBoneIk(
        SpriteRig rig,
        IList<SpriteBonePose> poses,
        int rootIndex,
        int childIndex,
        Vector2 target,
        bool bendPositive)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(poses);
        if ((uint)rootIndex >= (uint)rig.Bones.Count || (uint)childIndex >= (uint)rig.Bones.Count)
            return;
        if (rig.Bones[childIndex].ParentIndex != rootIndex)
            throw new InvalidOperationException("Two-bone IK child must be parented to root.");

        SpriteRigBone root = rig.Bones[rootIndex];
        SpriteRigBone child = rig.Bones[childIndex];
        float a = Math.Max(0.001f, root.Length);
        float b = Math.Max(0.001f, child.Length);
        Vector2 rootPosition = root.BindPosition + poses[rootIndex].Position;
        Vector2 delta = target - rootPosition;
        float distance = Math.Clamp(delta.Length(), 0.001f, a + b - 0.001f);
        float cosChild = Math.Clamp((a * a + b * b - distance * distance) / (2f * a * b), -1f, 1f);
        float childAngle = MathF.PI - MathF.Acos(cosChild);
        if (!bendPositive) childAngle = -childAngle;
        float cosRoot = Math.Clamp((a * a + distance * distance - b * b) / (2f * a * distance), -1f, 1f);
        float rootOffset = MathF.Acos(cosRoot) * (bendPositive ? 1f : -1f);
        float targetAngle = MathF.Atan2(delta.Y, delta.X);
        poses[rootIndex] = poses[rootIndex] with { Rotation = targetAngle - rootOffset - root.BindRotation };
        poses[childIndex] = poses[childIndex] with { Rotation = childAngle - child.BindRotation };
    }

    public static void AutoWeightByDistance(SpriteRig rig, float falloff = 2f)
    {
        BuildBindPose(rig);
        float power = Math.Max(0.1f, falloff);
        foreach (SpriteDeformVertex vertex in rig.Vertices)
        {
            vertex.Weights.Clear();
            for (int i = 0; i < rig.Bones.Count; i++)
            {
                SpriteRigBone bone = rig.Bones[i];
                Vector2 origin = Vector2.Transform(Vector2.Zero, bone.WorldBind);
                Vector2 end = Vector2.Transform(new Vector2(bone.Length, 0f), bone.WorldBind);
                float distance = DistanceToSegment(vertex.Position, origin, end);
                float weight = 1f / MathF.Pow(MathF.Max(0.25f, distance), power);
                vertex.Weights.Add(new SpriteVertexWeight(i, weight));
            }
            vertex.NormalizeAndPrune();
        }
    }

    private static Matrix3x2 Transform(Vector2 position, float rotation, Vector2 scale) =>
        Matrix3x2.CreateScale(scale)
        * Matrix3x2.CreateRotation(rotation)
        * Matrix3x2.CreateTranslation(position);

    private static bool HasCycle(IReadOnlyList<SpriteRigBone> bones, int start)
    {
        HashSet<int> visited = [];
        int current = start;
        while (current >= 0 && current < bones.Count)
        {
            if (!visited.Add(current)) return true;
            current = bones[current].ParentIndex;
        }
        return false;
    }

    private static IReadOnlyList<string> ValidateHierarchy(IReadOnlyList<SpriteRigBone> bones)
    {
        List<string> errors = [];
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].ParentIndex >= i)
                errors.Add($"Bone '{bones[i].Name}' parent must appear earlier in hierarchy order.");
            if (HasCycle(bones, i))
                errors.Add($"Bone '{bones[i].Name}' is part of a hierarchy cycle.");
        }
        return errors;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.LengthSquared() <= 0.0001f
            ? 0f
            : Math.Clamp(Vector2.Dot(point - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }
}

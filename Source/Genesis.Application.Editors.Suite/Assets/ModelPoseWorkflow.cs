using System.Numerics;
using Genesis.Runtime.Modeling;
using Newtonsoft.Json;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Pose authoring operates on joint locals, never on the inverse bind matrices or mesh.</summary>
public static partial class ModelPoseWorkflow
{
    public static T Copy<T>(T value) => value switch
    {
        GModelAsset asset => (T)(object)CopyAsset(asset),
        GModelMesh mesh => (T)(object)CopyMesh(mesh),
        GModelRig rig => (T)(object)CopyRig(rig),
        GModelAnimationClip clip => (T)(object)CopyClip(clip),
        GModelPose pose => (T)(object)new GModelPose { Id = pose.Id, Name = pose.Name, LocalBoneTransforms = (Matrix4x4[])pose.LocalBoneTransforms.Clone() },
        _ => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))
            ?? throw new InvalidOperationException("The model working copy could not be created."),
    };

    public static Matrix4x4[] BindPose(GModelAsset asset) => asset.Rig.Bones.Select(b => b.BindLocal).ToArray();

    public static GModelPose SavePose(GModelAsset asset, string name, Matrix4x4[] locals, string? id = null)
    {
        ValidatePose(asset, locals);
        name = RequiredName(name);
        var pose = asset.Poses.FirstOrDefault(p => p.Id == id);
        if (asset.Poses.Any(p => p != pose && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A pose with this name already exists. Select it and use Update pose.");
        if (pose is null) { pose = new GModelPose(); asset.Poses.Add(pose); }
        pose.Name = name;
        pose.LocalBoneTransforms = (Matrix4x4[])locals.Clone();
        return pose;
    }

    public static void DeletePose(GModelAsset asset, string id)
    {
        string[] used = asset.PoseAnimations.Where(a => a.Keys.Any(k => k.PoseId == id)).Select(a => a.Name).ToArray();
        if (used.Length > 0) throw new InvalidOperationException("This pose is used by " + string.Join(", ", used)
            + ". Change or remove those pose assignments first.");
        asset.Poses.RemoveAll(p => p.Id == id);
    }

    public static GModelAnimationClip Generate(GModelAsset asset, GModelPoseAnimation animation)
    {
        animation.Name = RequiredName(animation.Name);
        if (animation.FrameCount is < 1 or > 10000 || !float.IsFinite(animation.Fps) || animation.Fps is < 1 or > 240)
            throw new InvalidOperationException("Choose 1–10,000 frames and 1–240 FPS.");
        // Bound the actual dense output, not just the timeline length.
        if ((long)animation.FrameCount * asset.Rig.Bones.Count > 2_000_000)
            throw new InvalidOperationException("This clip is too large. Use fewer frames or split it into smaller clips.");
        if (animation.Keys.Count == 0) throw new InvalidOperationException("Assign a saved pose to at least one frame.");
        var keys = animation.Keys.OrderBy(k => k.Frame).ToArray();
        if (keys.Any(k => k.Frame < 1 || k.Frame > animation.FrameCount) || keys.Select(k => k.Frame).Distinct().Count() != keys.Length)
            throw new InvalidOperationException("Each pose assignment needs a unique frame within the animation length.");
        var poses = new Dictionary<string, Matrix4x4[]>();
        foreach (var key in keys)
        {
            var pose = asset.Poses.FirstOrDefault(p => p.Id == key.PoseId)
                ?? throw new InvalidOperationException($"Choose a saved pose for frame {key.Frame}.");
            ValidatePose(asset, pose.LocalBoneTransforms);
            poses[key.PoseId] = pose.LocalBoneTransforms;
        }
        if (asset.Animations.Any(c => c.PoseAnimationId != animation.Id && c.Name.Equals(animation.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An existing clip uses this name. Choose another name to preserve that clip.");
        var clip = new GModelAnimationClip { Name = animation.Name, PoseAnimationId = animation.Id, Fps = animation.Fps, Loop = animation.Loop };
        int segment = 0;
        for (int frame = 1; frame <= animation.FrameCount; frame++)
        {
            while (segment + 1 < keys.Length && keys[segment + 1].Frame <= frame) segment++;
            var from = keys[segment];
            var to = keys[Math.Min(segment + 1, keys.Length - 1)];
            float t = to.Frame == from.Frame ? 0 : Math.Clamp((frame - from.Frame) / (float)(to.Frame - from.Frame), 0, 1);
            t = from.Interpolation switch { GModelPoseInterpolation.Hold => 0, GModelPoseInterpolation.Smooth => t * t * (3 - 2 * t), _ => t };
            var locals = new Matrix4x4[asset.Rig.Bones.Count];
            for (int bone = 0; bone < locals.Length; bone++) locals[bone] = Interpolate(poses[from.PoseId][bone], poses[to.PoseId][bone], t);
            clip.Frames.Add(new GModelAnimationFrame { LocalBoneTransforms = locals });
        }
        // Validate and build first. A bad assignment cannot destroy the previous generated clip.
        int index = asset.Animations.FindIndex(c => c.PoseAnimationId == animation.Id);
        if (index < 0) asset.Animations.Add(clip); else asset.Animations[index] = clip;
        int recipe = asset.PoseAnimations.FindIndex(a => a.Id == animation.Id);
        if (recipe < 0) asset.PoseAnimations.Add(Copy(animation)); else asset.PoseAnimations[recipe] = Copy(animation);
        return clip;
    }

    public static Matrix4x4 Interpolate(Matrix4x4 from, Matrix4x4 to, float t)
    {
        if (t <= 0) return from;
        if (t >= 1) return to;
        Decompose(from, out var sa, out var qa, out var pa);
        Decompose(to, out var sb, out var qb, out var pb);
        return Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, t))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.Slerp(qa, qb, t)))
            * Matrix4x4.CreateTranslation(Vector3.Lerp(pa, pb, t));
    }

    /// <summary>Inverse of System.Numerics.CreateFromYawPitchRoll (Y, X, Z), including gimbal lock.</summary>
    public static Vector3 EulerDegrees(Quaternion quaternion)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(quaternion));
        float pitch = MathF.Asin(Math.Clamp(-matrix.M32, -1, 1));
        float yaw, roll;
        if (MathF.Abs(matrix.M32) > .999999f)
        {
            yaw = MathF.Atan2(-matrix.M13, matrix.M11); roll = 0;
        }
        else
        {
            yaw = MathF.Atan2(matrix.M31, matrix.M33);
            roll = MathF.Atan2(matrix.M12, matrix.M22);
        }
        return new Vector3(pitch, yaw, roll) * (180 / MathF.PI);
    }

    /// <summary>Selected-only mode compensates direct children, preserving all descendant world transforms.</summary>
    public static Matrix4x4[] Transform(GModelAsset asset, Matrix4x4[] source, int bone, Matrix4x4 local, bool followChildren)
    {
        ValidatePose(asset, source);
        if (bone < 0 || bone >= source.Length) throw new InvalidOperationException("Select a joint first.");
        Decompose(local, out _, out _, out _);
        var result = (Matrix4x4[])source.Clone();
        result[bone] = local;
        if (!followChildren)
        {
            var before = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, source);
            var after = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, result);
            if (!Matrix4x4.Invert(after[bone], out var inverse)) throw new InvalidOperationException("This transform has zero scale.");
            for (int i = 0; i < result.Length; i++)
                if (asset.Rig.Bones[i].ParentIndex == bone) result[i] = before[i] * inverse;
        }
        return result;
    }

    public static void ValidatePose(GModelAsset asset, Matrix4x4[] locals)
    {
        if (!asset.Rig.IsValid || locals.Length != asset.Rig.Bones.Count)
            throw new InvalidOperationException("The pose does not match this skeleton. Create or bind the rig first.");
        foreach (var matrix in locals) Decompose(matrix, out _, out _, out _);
    }

    private static void Decompose(Matrix4x4 matrix, out Vector3 scale, out Quaternion rotation, out Vector3 position)
    {
        if (!Matrix4x4.Decompose(matrix, out scale, out rotation, out position)
            || !float.IsFinite(scale.LengthSquared()) || !float.IsFinite(rotation.LengthSquared()) || !float.IsFinite(position.LengthSquared())
            || MathF.Abs(scale.X * scale.Y * scale.Z) < 1e-9f)
            throw new InvalidOperationException("A joint has an invalid transform. Use finite values and non-zero scale.");
        rotation = Quaternion.Normalize(rotation);
    }

    private static string RequiredName(string name) => string.IsNullOrWhiteSpace(name)
        ? throw new InvalidOperationException("Enter a name first.") : name.Trim();
}

#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;

namespace Genesis.Runtime.Imaging;

public static class PixelRigData
{
    public static PixelRigPoint Copy(PixelRigPoint source) => new() { X=source.X, Y=source.Y };
    public static PixelRigBone Copy(PixelRigBone source) => new()
    {
        Id=source.Id, Name=source.Name, ParentId=source.ParentId,
        StartJointId=source.StartJointId, CentreJointId=source.CentreJointId, EndJointId=source.EndJointId,
        Start=Copy(source.Start), End=Copy(source.End),
    };
    public static PixelRigJoint Copy(PixelRigJoint source) => new()
    { Id=source.Id, Name=source.Name, Centre=Copy(source.Centre), Radius=source.Radius, Pinned=source.Pinned };
    public static PixelRigPose Copy(PixelRigPose source) => new()
    { Id=source.Id, Name=source.Name, Bones=source.Bones.Select(Copy).ToList(), Joints=source.Joints.Select(Copy).ToList() };
    public static PixelRigAnimation Copy(PixelRigAnimation source) => new()
    {
        Id=source.Id, Name=source.Name, FramesPerSecond=source.FramesPerSecond, Loop=source.Loop,
        Keys=source.Keys.Select(key => new PixelRigKeyframe { Frame=key.Frame, PoseId=key.PoseId }).ToList(),
        GeneratedFrameIds=new List<string>(source.GeneratedFrameIds),
    };
    public static PixelRigDefinition Copy(PixelRigDefinition source) => new()
    {
        Id=source.Id, Name=source.Name, Width=source.Width, Height=source.Height,
        LayerId=source.LayerId, SourceFrameId=source.SourceFrameId, BindPixels=(byte[])source.BindPixels.Clone(),
        FillJointGaps=source.FillJointGaps, Bones=source.Bones.Select(Copy).ToList(), Joints=source.Joints.Select(Copy).ToList(),
        Poses=source.Poses.Select(Copy).ToList(), Animations=source.Animations.Select(Copy).ToList(),
    };

    /// <summary>Reject broken/unbounded imported rigs before the allocator or rasterizer uses them.</summary>
    public static void Validate(PixelRigDefinition rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Width < 1 || rig.Height < 1 || (long)rig.Width * rig.Height > 4_194_304
            || rig.BindPixels is null || rig.BindPixels.Length != (long)rig.Width * rig.Height * 4
            || rig.Bones is null || rig.Bones.Count is < 1 or > 128 || rig.Joints is null || rig.Joints.Count > 256
            || rig.Poses is null || rig.Poses.Count > 512 || rig.Animations is null || rig.Animations.Count > 128)
            throw new ArgumentException("A pixel rig needs RGBA bind artwork (up to 4,194,304 pixels), 1–128 bones, at most 256 joints, 512 poses and 128 animations.");
        if (rig.Bones.Any(b=>b is null) || rig.Joints.Any(j=>j is null)
            || rig.Poses.Any(p=>p is null || p.Bones is null || p.Joints is null
                || p.Bones.Count>128 || p.Joints.Count>256 || p.Bones.Any(b=>b is null) || p.Joints.Any(j=>j is null))
            || rig.Animations.Any(a=>a is null || a.Keys is null || a.GeneratedFrameIds is null || a.Keys.Any(k=>k is null)))
            throw new ArgumentException("Pixel rig lists cannot contain null or oversized pose data.");
        static void Unique(IEnumerable<string> values, string label)
        {
            HashSet<string> ids=new(StringComparer.Ordinal);
            foreach (string id in values)
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new ArgumentException($"Invalid or duplicate {label} ID.");
        }
        Unique(rig.Bones.Select(b=>b.Id), "bone"); Unique(rig.Joints.Select(j=>j.Id), "joint");
        Unique(rig.Poses.Select(p=>p.Id), "pose"); Unique(rig.Animations.Select(a=>a.Id), "animation");
        foreach (PixelRigPose pose in rig.Poses)
        {
            Unique(pose.Bones.Select(b=>b.Id), "pose bone"); Unique(pose.Joints.Select(j=>j.Id), "pose joint");
            if (pose.Bones.Any(b=>rig.Bones.All(rest=>rest.Id!=b.Id)) || pose.Joints.Any(j=>rig.Joints.All(rest=>rest.Id!=j.Id)))
                throw new ArgumentException("A saved pose references a missing bind bone or joint.");
        }
        foreach (PixelRigBone bone in rig.Bones.Concat(rig.Poses.SelectMany(p=>p.Bones)))
        {
            Point(bone.Start); Point(bone.End);
            foreach (string? jointId in new[] { bone.StartJointId, bone.EndJointId, bone.CentreJointId })
                if (!string.IsNullOrEmpty(jointId) && rig.Joints.All(j=>j.Id!=jointId))
                    throw new ArgumentException("Pixel rig bone references a missing joint.");
            if (!string.IsNullOrEmpty(bone.ParentId) && rig.Bones.All(b=>b.Id!=bone.ParentId))
                throw new ArgumentException("Pixel rig references a missing parent bone.");
        }
        foreach (PixelRigJoint joint in rig.Joints.Concat(rig.Poses.SelectMany(p=>p.Joints)))
        {
            Point(joint.Centre);
            if (!double.IsFinite(joint.Radius) || joint.Radius < 0 || joint.Radius > 1_000_000)
                throw new ArgumentException("Pixel rig joint radius must be finite and non-negative.");
        }
        void CheckCycles(IReadOnlyList<PixelRigBone> pose)
        {
            foreach (PixelRigBone bone in rig.Bones)
            {
                HashSet<string> visited=new(StringComparer.Ordinal);
                PixelRigBone? current=pose.FirstOrDefault(b=>b.Id==bone.Id) ?? bone;
                while (current is not null)
                {
                    if (!visited.Add(current.Id)) throw new ArgumentException("Pixel rig contains a parent cycle.");
                    string? parentId = current.ParentId;
                    current=pose.FirstOrDefault(b=>b.Id==parentId) ?? rig.Bones.FirstOrDefault(b=>b.Id==parentId);
                }
            }
        }
        CheckCycles(rig.Bones);
        foreach (PixelRigPose pose in rig.Poses) CheckCycles(pose.Bones);
        foreach (PixelRigAnimation animation in rig.Animations)
        {
            if (animation.FramesPerSecond is < 1 or > 240 || animation.Keys is null || animation.Keys.Count > 8192)
                throw new ArgumentException("Pixel rig animation rate must be 1–240 FPS, with at most 8192 keys.");
            if (animation.Keys.Any(k=>k.Frame < 1 || k.Frame > 1_000_000 || rig.Poses.All(p=>p.Id!=k.PoseId))
                || animation.Keys.Select(k=>k.Frame).Distinct().Count()!=animation.Keys.Count)
                throw new ArgumentException("Pixel rig animation has duplicate frames or references a missing pose.");
        }
    }

    private static void Point(PixelRigPoint? point)
    {
        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X)>1_000_000 || Math.Abs(point.Y)>1_000_000)
            throw new ArgumentException("Pixel rig coordinates must be finite and inside the supported range.");
    }
}

#nullable enable
using System.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Genesis.Runtime.Imaging;

/// <summary>Edits a pose while preserving shared joint anchors and explicitly chosen propagation.</summary>
public static class PixelRigPoseEditor
{
    public static HashSet<string> AffectedBones(IReadOnlyList<PixelRigBone> bones, string selected, bool followChildren, string? fixedJoint=null)
    {
        var ids = new HashSet<string> { selected };
        var ancestors=new HashSet<string>();
        var parent=bones.FirstOrDefault(b=>b.Id==selected)?.ParentId;
        while(parent!=null && ancestors.Add(parent)) parent=bones.FirstOrDefault(b=>b.Id==parent)?.ParentId;
        if (followChildren)
            for (int i=0;i<bones.Count;i++)
            {
                var movingJoints=bones.Where(b=>ids.Contains(b.Id)).SelectMany(b=>new[]{b.StartJointId,b.EndJointId,b.CentreJointId})
                    .Where(id=>id!=null && id!=fixedJoint).ToHashSet();
                foreach (var bone in bones.Where(b=>!ancestors.Contains(b.Id)))
                    if ((bone.ParentId != null && ids.Contains(bone.ParentId))
                        || new[]{bone.StartJointId,bone.EndJointId,bone.CentreJointId}.Any(id=>id!=null&&movingJoints.Contains(id))) ids.Add(bone.Id);
            }
        return ids;
    }

    public static void Transform(List<PixelRigBone> bones, List<PixelRigJoint> joints, string selected,
        Matrix3x2 transform, bool followChildren, string? fixedJoint = null)
    {
        var affected = AffectedBones(bones,selected,followChildren,fixedJoint);
        var movingJoints = bones.Where(b=>affected.Contains(b.Id))
            .SelectMany(b=>new[]{b.StartJointId,b.EndJointId,b.CentreJointId}).ToHashSet();
        foreach (var bone in bones.Where(b=>affected.Contains(b.Id)))
        {
            bone.Start = PixelRigRasterizer.Value(Vector2.Transform(PixelRigRasterizer.Vector(bone.Start),transform));
            bone.End = PixelRigRasterizer.Value(Vector2.Transform(PixelRigRasterizer.Vector(bone.End),transform));
        }
        foreach (var joint in joints.Where(j=>movingJoints.Contains(j.Id) && j.Id!=fixedJoint && !j.Pinned))
            joint.Centre = PixelRigRasterizer.Value(Vector2.Transform(PixelRigRasterizer.Vector(joint.Centre),transform));
        Constrain(bones,joints);
    }

    public static void MoveJoint(List<PixelRigBone> bones, List<PixelRigJoint> joints, string id, Vector2 point)
    {
        var joint = joints.First(j=>j.Id==id);
        if (!joint.Pinned) joint.Centre = PixelRigRasterizer.Value(point);
        Constrain(bones,joints);
    }

    public static void Constrain(List<PixelRigBone> bones, IReadOnlyList<PixelRigJoint> joints)
    {
        foreach (var bone in bones)
        {
            var start = joints.FirstOrDefault(j=>j.Id==bone.StartJointId);
            var end = joints.FirstOrDefault(j=>j.Id==bone.EndJointId);
            var centre = joints.FirstOrDefault(j=>j.Id==bone.CentreJointId);
            if (start!=null) bone.Start = PixelRigData.Copy(start.Centre);
            if (end!=null) bone.End = PixelRigData.Copy(end.Centre);
            if (centre!=null && start==null && end==null)
            {
                Vector2 offset = PixelRigRasterizer.Vector(centre.Centre)-(PixelRigRasterizer.Vector(bone.Start)+PixelRigRasterizer.Vector(bone.End))/2;
                bone.Start = PixelRigRasterizer.Value(PixelRigRasterizer.Vector(bone.Start)+offset);
                bone.End = PixelRigRasterizer.Value(PixelRigRasterizer.Vector(bone.End)+offset);
            }
        }
    }
}

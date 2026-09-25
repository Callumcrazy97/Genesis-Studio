using Genesis.Application.Core.Images;
using Genesis.Runtime.Imaging;

namespace Genesis.Application.Editors.Image.Rigging;

/// <summary>Document adapter only. Deformation, propagation and joint infill are engine operations.</summary>
internal static class PixelRigBridge
{
    internal static PixelRigPoint ToEngine(ImageVector2 p) => new() { X=p.X,Y=p.Y };
    internal static ImageVector2 ToEditor(PixelRigPoint p) => new() { X=p.X,Y=p.Y };
    internal static PixelRigBone ToEngine(ImagePixelBone b) => new()
    { Id=b.Id,Name=b.Name,ParentId=b.ParentId,StartJointId=b.StartJointId,CentreJointId=b.CentreJointId,EndJointId=b.EndJointId,Start=ToEngine(b.Start),End=ToEngine(b.End) };
    internal static ImagePixelBone ToEditor(PixelRigBone b) => new()
    { Id=b.Id,Name=b.Name,ParentId=b.ParentId,StartJointId=b.StartJointId,CentreJointId=b.CentreJointId,EndJointId=b.EndJointId,Start=ToEditor(b.Start),End=ToEditor(b.End) };
    internal static PixelRigJoint ToEngine(ImagePixelJoint j) => new()
    { Id=j.Id,Name=j.Name,Centre=ToEngine(j.Centre),Radius=j.Radius,Pinned=j.Pinned };
    internal static ImagePixelJoint ToEditor(PixelRigJoint j) => new()
    { Id=j.Id,Name=j.Name,Centre=ToEditor(j.Centre),Radius=j.Radius,Pinned=j.Pinned };
    internal static PixelRigAnimation ToEngine(ImagePoseAnimation a) => new()
    { Id=a.Id,Name=a.Name,FramesPerSecond=a.FramesPerSecond,Loop=a.Loop,GeneratedFrameIds=[..a.GeneratedFrameIds],
        Keys=a.Keys.Select(k=>new PixelRigKeyframe {Frame=k.Frame,PoseId=k.PoseId}).ToList() };
    internal static PixelRigDefinition ToEngine(ImagePixelRig r) => new()
    {
        Id=r.Id,Name=r.Name,Width=r.Width,Height=r.Height,LayerId=r.LayerId,SourceFrameId=r.SourceFrameId,
        BindPixels=r.BindPixels,FillJointGaps=r.FillJointGaps,Bones=r.Bones.Select(ToEngine).ToList(),Joints=r.Joints.Select(ToEngine).ToList(),
        Poses=r.Poses.Select(p=>new PixelRigPose {Id=p.Id,Name=p.Name,Bones=p.Bones.Select(ToEngine).ToList(),Joints=p.Joints.Select(ToEngine).ToList()}).ToList(),
        Animations=r.Animations.Select(ToEngine).ToList(),
    };
    internal static void CopyPoseBack(List<ImagePixelBone> bones, List<ImagePixelJoint> joints,
        IReadOnlyList<PixelRigBone> pose, IReadOnlyList<PixelRigJoint> anchors)
    {
        // Keep editor list/item identities: the active tree/grid may hold these instances.
        foreach (ImagePixelBone bone in bones)
        {
            PixelRigBone? source=pose.FirstOrDefault(b=>b.Id==bone.Id);
            if(source is null)continue;
            bone.Start=ToEditor(source.Start);bone.End=ToEditor(source.End);
        }
        foreach(ImagePixelJoint joint in joints)
        {
            PixelRigJoint? source=anchors.FirstOrDefault(j=>j.Id==joint.Id);
            if(source is null)continue;
            joint.Centre=ToEditor(source.Centre);joint.Radius=source.Radius;joint.Pinned=source.Pinned;
        }
    }
}

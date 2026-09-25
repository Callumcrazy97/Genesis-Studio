using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Runtime.Imaging;
using EnginePoseEditor = Genesis.Runtime.Imaging.PixelRigPoseEditor;

namespace Genesis.Application.Editors.Image.Rigging;

/// <summary>Editor document adapter for the engine's connected-bone and pinned-joint solver.</summary>
public static class PixelRigPoseEditor
{
    public static HashSet<string> AffectedBones(IReadOnlyList<ImagePixelBone> bones,string selected,bool followChildren,string? fixedJoint=null)
        => EnginePoseEditor.AffectedBones(bones.Select(PixelRigBridge.ToEngine).ToList(),selected,followChildren,fixedJoint);
    public static void Transform(List<ImagePixelBone> bones,List<ImagePixelJoint> joints,string selected,Matrix3x2 transform,bool followChildren,string? fixedJoint=null)
    {
        List<PixelRigBone> pose=bones.Select(PixelRigBridge.ToEngine).ToList();
        List<PixelRigJoint> anchors=joints.Select(PixelRigBridge.ToEngine).ToList();
        EnginePoseEditor.Transform(pose,anchors,selected,transform,followChildren,fixedJoint);
        PixelRigBridge.CopyPoseBack(bones,joints,pose,anchors);
    }
    public static void MoveJoint(List<ImagePixelBone> bones,List<ImagePixelJoint> joints,string id,Vector2 point)
    {
        List<PixelRigBone> pose=bones.Select(PixelRigBridge.ToEngine).ToList();
        List<PixelRigJoint> anchors=joints.Select(PixelRigBridge.ToEngine).ToList();
        EnginePoseEditor.MoveJoint(pose,anchors,id,point);
        PixelRigBridge.CopyPoseBack(bones,joints,pose,anchors);
    }
    public static void Constrain(List<ImagePixelBone> bones,IReadOnlyList<ImagePixelJoint> joints)
    {
        List<PixelRigBone> pose=bones.Select(PixelRigBridge.ToEngine).ToList();
        List<PixelRigJoint> anchors=joints.Select(PixelRigBridge.ToEngine).ToList();
        EnginePoseEditor.Constrain(pose,anchors);
        PixelRigBridge.CopyPoseBack(bones,[],pose,anchors);
    }
}

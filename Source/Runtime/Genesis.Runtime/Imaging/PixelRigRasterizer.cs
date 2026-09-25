#nullable enable
using System.Numerics;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace Genesis.Runtime.Imaging;

/// <summary>Nearest-bone binding with inverse rasterization: opaque pixels retain RGBA without forward-splat holes.</summary>
public sealed class PixelRigRasterizer
{
    private readonly PixelRigDefinition _rig;
    private readonly int[] _owners;
    private readonly Rectangle[] _bounds;
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static Vector2 Vector(PixelRigPoint v) => new((float)v.X, (float)v.Y);
    public static PixelRigPoint Value(Vector2 v) => new() { X = v.X, Y = v.Y };

    /// <summary>
    /// Bring erasures made on the rig's source cel into its saved bind image without replacing
    /// retained artwork. A rig opened from another frame or layer must keep its own source.
    /// </summary>
    public static bool RefreshSourceTransparency(
        PixelRigDefinition rig,
        byte[] source,
        int width,
        int height,
        string layerId,
        string frameId)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length % 4 != 0 || rig.Width != width || rig.Height != height
            || rig.BindPixels.Length != source.Length
            || !string.Equals(rig.LayerId, layerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rig.SourceFrameId, frameId, StringComparison.OrdinalIgnoreCase))
            return false;

        bool changed = false;
        for (int index = 0; index < source.Length; index += 4)
        {
            if (source[index + 3] != 0 || rig.BindPixels[index + 3] == 0) continue;
            Array.Clear(rig.BindPixels, index, 4);
            changed = true;
        }
        return changed;
    }

    public PixelRigRasterizer(PixelRigDefinition rig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rig);
        _rig = rig;
        if (rig.Width <= 0 || rig.Height <= 0 || (long)rig.Width*rig.Height > 4_194_304 || rig.BindPixels.Length != rig.Width*rig.Height*4 || rig.Bones.Count is < 1 or > 128)
            throw new ArgumentException("Bind a layer of up to four million pixels to 1–128 bones first.");
        _owners = new int[rig.Width*rig.Height]; Array.Fill(_owners, -1);
        _bounds = new Rectangle[rig.Bones.Count];
        for (int y = 0; y < rig.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < rig.Width; x++)
            {
                int index = y*rig.Width+x;
                if (rig.BindPixels[index*4+3] == 0) continue;
                float best = float.MaxValue;
                for (int b = 0; b < rig.Bones.Count; b++)
                {
                    var bone = rig.Bones[b]; Vector2 a = Vector(bone.Start), delta = Vector(bone.End)-a;
                    float t = Math.Clamp(Vector2.Dot(new Vector2(x+.5f,y+.5f)-a,delta)/Math.Max(.0001f,delta.LengthSquared()),0,1);
                    float distance = Vector2.DistanceSquared(new Vector2(x+.5f,y+.5f),a+delta*t);
                    if (distance < best) { best = distance; _owners[index] = b; }
                }
                int owner = _owners[index];
                var pixel = new Rectangle(x,y,1,1); _bounds[owner] = _bounds[owner].IsEmpty ? pixel : Rectangle.Union(_bounds[owner],pixel);
            }
        }
    }

    public byte[] Render(IReadOnlyList<PixelRigBone> pose, CancellationToken cancellationToken = default,
        IReadOnlyList<PixelRigJoint>? poseJoints = null)
    {
        byte[] result = new byte[_rig.BindPixels.Length];
        RenderInto(result, pose, cancellationToken, poseJoints);
        return result;
    }

    /// <summary>Renders into a reusable RGBA canvas; bind pixels and transparency are never mutated.</summary>
    public void RenderInto(byte[] result, IReadOnlyList<PixelRigBone> pose,
        CancellationToken cancellationToken = default, IReadOnlyList<PixelRigJoint>? poseJoints = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(pose);
        if (result.Length != _rig.BindPixels.Length || ReferenceEquals(result, _rig.BindPixels))
            throw new ArgumentException("Supply a separate RGBA destination matching the bound canvas.", nameof(result));
        cancellationToken.ThrowIfCancellationRequested();
        if (_rig.Bones.All(rest => { var target=pose.FirstOrDefault(b=>b.Id==rest.Id)??rest;return target.Start.X==rest.Start.X&&target.Start.Y==rest.Start.Y&&target.End.X==rest.End.X&&target.End.Y==rest.End.Y; }))
        {
            Buffer.BlockCopy(_rig.BindPixels, 0, result, 0, result.Length);
            return;
        }
        Array.Clear(result);
        var transforms = _rig.Bones.Select(rest =>
        {
            var target = pose.FirstOrDefault(b => b.Id == rest.Id) ?? rest;
            Vector2 a = Vector(rest.Start), b = Vector(rest.End), c = Vector(target.Start), d = Vector(target.End);
            float restAngle = MathF.Atan2(b.Y-a.Y,b.X-a.X), targetAngle = MathF.Atan2(d.Y-c.Y,d.X-c.X);
            float scale = Math.Max(.001f,Vector2.Distance(c,d))/Math.Max(.001f,Vector2.Distance(a,b));
            var forward = Matrix3x2.CreateTranslation(-a)*Matrix3x2.CreateRotation(-restAngle)
                *Matrix3x2.CreateScale(scale,1)*Matrix3x2.CreateRotation(targetAngle)*Matrix3x2.CreateTranslation(c);
            Matrix3x2.Invert(forward,out var inverse); return (forward,inverse);
        }).ToArray();
        for (int bone = 0; bone < transforms.Length; bone++)
        {
            var bounds = _bounds[bone]; if (bounds.IsEmpty) continue;
            Matrix3x2 matrix = transforms[bone].forward;
            Vector2 a = Vector2.Transform(new Vector2(bounds.Left, bounds.Top), matrix);
            Vector2 b = Vector2.Transform(new Vector2(bounds.Right, bounds.Top), matrix);
            Vector2 c = Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), matrix);
            Vector2 d = Vector2.Transform(new Vector2(bounds.Left, bounds.Bottom), matrix);
            Vector2 min = Vector2.Min(Vector2.Min(a,b), Vector2.Min(c,d));
            Vector2 max = Vector2.Max(Vector2.Max(a,b), Vector2.Max(c,d));
            int left=Math.Max(0,(int)MathF.Floor(min.X)), top=Math.Max(0,(int)MathF.Floor(min.Y));
            int right=Math.Min(_rig.Width,(int)MathF.Ceiling(max.X)), bottom=Math.Min(_rig.Height,(int)MathF.Ceiling(max.Y));
            for (int y=top;y<bottom;y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for(int x=left;x<right;x++)
                {
                    Vector2 p=Vector2.Transform(new Vector2(x+.5f,y+.5f),transforms[bone].inverse);
                    int sx=(int)MathF.Floor(p.X),sy=(int)MathF.Floor(p.Y);
                    if((uint)sx>=(uint)_rig.Width||(uint)sy>=(uint)_rig.Height||_owners[sy*_rig.Width+sx]!=bone)continue;
                    Buffer.BlockCopy(_rig.BindPixels,(sy*_rig.Width+sx)*4,result,(y*_rig.Width+x)*4,4);
                }
            }
        }
        if (_rig.FillJointGaps) FillJointGaps(result,pose,poseJoints,transforms,cancellationToken);
    }

    // Lay a patch of the original joint artwork behind the deformed parts. Sampling alpha from
    // the source preserves cut-out backgrounds; only vacant pixels inside the joint circle fill.
    private void FillJointGaps(byte[] result, IReadOnlyList<PixelRigBone> pose,
        IReadOnlyList<PixelRigJoint>? poseJoints, (Matrix3x2 forward,Matrix3x2 inverse)[] transforms,
        CancellationToken token)
    {
        foreach (var rest in _rig.Joints)
        {
            token.ThrowIfCancellationRequested();
            var anchor = _rig.Bones.FirstOrDefault(b=>b.EndJointId==rest.Id)
                ?? _rig.Bones.FirstOrDefault(b=>b.StartJointId==rest.Id || b.CentreJointId==rest.Id);
            if (anchor==null) continue;
            int index = _rig.Bones.IndexOf(anchor);
            var target = poseJoints?.FirstOrDefault(j=>j.Id==rest.Id);
            Vector2 centre = target!=null ? Vector(target.Centre) : Vector2.Transform(Vector(rest.Centre),transforms[index].forward);
            float scale = MathF.Sqrt(transforms[index].forward.M11*transforms[index].forward.M11+transforms[index].forward.M12*transforms[index].forward.M12);
            float radius = Math.Clamp((float)(target?.Radius ?? rest.Radius*scale),.25f,Math.Max(_rig.Width,_rig.Height)*2f);
            float angle = MathF.Atan2(transforms[index].forward.M12,transforms[index].forward.M11);
            Matrix3x2 inverse = Matrix3x2.CreateTranslation(-centre)*Matrix3x2.CreateRotation(-angle)
                *Matrix3x2.CreateTranslation(Vector(rest.Centre));
            int left = Math.Max(0,(int)MathF.Floor(centre.X-radius)),right = Math.Min(_rig.Width,(int)MathF.Ceiling(centre.X+radius));
            int top = Math.Max(0,(int)MathF.Floor(centre.Y-radius)),bottom = Math.Min(_rig.Height,(int)MathF.Ceiling(centre.Y+radius));
            for (int y=top;y<bottom;y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x=left;x<right;x++)
                {
                    int destination=(y*_rig.Width+x)*4;
                    Vector2 point=new(x+.5f,y+.5f);
                    if (result[destination+3]!=0 || Vector2.DistanceSquared(point,centre)>radius*radius) continue;
                    Vector2 source=Vector2.Transform(point,inverse); int sx=(int)MathF.Floor(source.X),sy=(int)MathF.Floor(source.Y);
                    if ((uint)sx>=(uint)_rig.Width || (uint)sy>=(uint)_rig.Height) continue;
                    Buffer.BlockCopy(_rig.BindPixels,(sy*_rig.Width+sx)*4,result,destination,4);
                }
            }
        }
    }

    public static List<PixelRigBone> Interpolate(PixelRigDefinition rig, PixelRigAnimation animation, int frame)
    {
        var keys = animation.Keys.OrderBy(key => key.Frame).ToArray();
        if (keys.Length == 0 || keys.Any(key => key.Frame < 1 || rig.Poses.All(pose => pose.Id != key.PoseId)) || keys.Select(k => k.Frame).Distinct().Count() != keys.Length)
            throw new ArgumentException("Assign saved poses to unique frame numbers starting at 1.");
        var left = keys.LastOrDefault(key => key.Frame <= frame) ?? keys[0];
        var right = keys.FirstOrDefault(key => key.Frame >= frame) ?? keys[^1];
        float t = left.Frame == right.Frame ? 0 : (frame-left.Frame)/(float)(right.Frame-left.Frame);
        var a = rig.Poses.First(p => p.Id == left.PoseId); var b = rig.Poses.First(p => p.Id == right.PoseId);
        var result = rig.Bones.Select(PixelRigData.Copy).ToList();
        // Connection edits belong to a saved pose too. If a connection changes between keys,
        // interpolate that bone in world space instead of silently restoring the bind hierarchy.
        foreach(var bone in result)
        {
            var pa=a.Bones.FirstOrDefault(p=>p.Id==bone.Id)??bone;
            var pb=b.Bones.FirstOrDefault(p=>p.Id==bone.Id)??bone;
            bone.ParentId=pa.ParentId==pb.ParentId?pa.ParentId:null;
            bone.StartJointId=pa.StartJointId==pb.StartJointId?pa.StartJointId:null;
            bone.EndJointId=pa.EndJointId==pb.EndJointId?pa.EndJointId:null;
            bone.CentreJointId=pa.CentreJointId==pb.CentreJointId?pa.CentreJointId:null;
        }
        var completed = new HashSet<string>(); var visiting = new HashSet<string>();
        static float Angle(PixelRigBone bone) { var d=Vector(bone.End)-Vector(bone.Start); return MathF.Atan2(d.Y,d.X); }
        static Vector2 Rotate(Vector2 p,float angle)=>Vector2.Transform(p,Matrix3x2.CreateRotation(angle));
        void Sample(PixelRigBone bone)
        {
            if(completed.Contains(bone.Id))return;
            if(!visiting.Add(bone.Id))throw new ArgumentException("Rig hierarchy contains a cycle.");
            var pa=a.Bones.FirstOrDefault(p=>p.Id==bone.Id)??rig.Bones.First(p=>p.Id==bone.Id);
            var pb=b.Bones.FirstOrDefault(p=>p.Id==bone.Id)??rig.Bones.First(p=>p.Id==bone.Id);
            float aa=Angle(pa),ab=Angle(pb); Vector2 sa=Vector(pa.Start),sb=Vector(pb.Start);
            var parent=result.FirstOrDefault(p=>p.Id==bone.ParentId);
            float parentAngle=0,parentLength=1;Vector2 parentStart=Vector2.Zero;
            if(parent!=null)
            {
                Sample(parent);
                var ra=a.Bones.FirstOrDefault(p=>p.Id==parent.Id)??rig.Bones.First(p=>p.Id==parent.Id);
                var rb=b.Bones.FirstOrDefault(p=>p.Id==parent.Id)??rig.Bones.First(p=>p.Id==parent.Id);
                float angleA=Angle(ra),angleB=Angle(rb);
                sa=Rotate(sa-Vector(ra.Start),-angleA)/Math.Max(.001f,Vector2.Distance(Vector(ra.Start),Vector(ra.End)));
                sb=Rotate(sb-Vector(rb.Start),-angleB)/Math.Max(.001f,Vector2.Distance(Vector(rb.Start),Vector(rb.End)));
                aa-=angleA;ab-=angleB;parentAngle=Angle(parent);parentLength=Vector2.Distance(Vector(parent.Start),Vector(parent.End));parentStart=Vector(parent.Start);
            }
            float angle=parentAngle+aa+MathF.IEEERemainder(ab-aa,2*MathF.PI)*t;
            Vector2 start=parentStart+Rotate(Vector2.Lerp(sa,sb,t)*parentLength,parentAngle);
            float la=Vector2.Distance(Vector(pa.Start),Vector(pa.End)),lb=Vector2.Distance(Vector(pb.Start),Vector(pb.End));
            bone.Start=Value(start);bone.End=Value(start+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*(la+(lb-la)*t));
            visiting.Remove(bone.Id);completed.Add(bone.Id);
        }
        foreach(var bone in result)Sample(bone);
        var pinned=rig.Joints.Select(rest=>
        {
            var from=a.Joints.FirstOrDefault(j=>j.Id==rest.Id)??rest;
            var to=b.Joints.FirstOrDefault(j=>j.Id==rest.Id)??rest;
            var joint=PixelRigData.Copy(rest); joint.Pinned=from.Pinned&&to.Pinned;
            joint.Centre=Value(Vector2.Lerp(Vector(from.Centre),Vector(to.Centre),t)); return joint;
        }).Where(j=>j.Pinned).ToList();
        PixelRigPoseEditor.Constrain(result,pinned);
        return result;
    }

    public static List<PixelRigJoint> InterpolateJoints(PixelRigDefinition rig, PixelRigAnimation animation, int frame)
    {
        var keys = animation.Keys.OrderBy(key => key.Frame).ToArray();
        if (keys.Length == 0) return rig.Joints.Select(PixelRigData.Copy).ToList();
        var left = keys.LastOrDefault(key => key.Frame <= frame) ?? keys[0];
        var right = keys.FirstOrDefault(key => key.Frame >= frame) ?? keys[^1];
        float amount = left.Frame == right.Frame ? 0 : (frame-left.Frame)/(float)(right.Frame-left.Frame);
        var a = rig.Poses.FirstOrDefault(pose => pose.Id == left.PoseId);
        var b = rig.Poses.FirstOrDefault(pose => pose.Id == right.PoseId);
        if (a == null || b == null) return rig.Joints.Select(PixelRigData.Copy).ToList();
        var bones = Interpolate(rig,animation,frame);
        return rig.Joints.Select(rest =>
        {
            var from = a.Joints.FirstOrDefault(joint => joint.Id == rest.Id) ?? rest;
            var to = b.Joints.FirstOrDefault(joint => joint.Id == rest.Id) ?? rest;
            Vector2 centre = Vector2.Lerp(Vector(from.Centre),Vector(to.Centre),amount);
            // Parent-relative bone interpolation follows an arc, so a separately lerped joint
            // would drift away from its attached bone between the authored keys.
            var anchor = bones.FirstOrDefault(bone=>bone.EndJointId==rest.Id)
                ?? bones.FirstOrDefault(bone=>bone.StartJointId==rest.Id || bone.CentreJointId==rest.Id);
            if (anchor!=null) centre=anchor.EndJointId==rest.Id ? Vector(anchor.End)
                : anchor.StartJointId==rest.Id ? Vector(anchor.Start) : (Vector(anchor.Start)+Vector(anchor.End))/2;
            var joint = PixelRigData.Copy(rest); joint.Centre = Value(centre); joint.Radius = from.Radius+(to.Radius-from.Radius)*amount; return joint;
        }).ToList();
    }
}

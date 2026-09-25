using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelRigViewportControl
{
    public int DirectPoseMode { get; set; }
    public bool SelectedRigElementIsBone { get; private set; }
    private Matrix4x4[]? _directBefore,_directWorlds;
    private int _directBone=-1,_directParent=-1,_directHandle;
    private Vector3 _directStart;
    private Vector3 _directPlaneNormal;
    public string DirectGestureDescription { get; private set; } = "";

    private int NearestJoint(Point point,Matrix4x4[] worlds,float tolerance)
    {
        var mouse=_viewport.ControlToSurface(point);float best=tolerance*tolerance;int found=-1;
        for(int i=0;i<worlds.Length;i++)
        {
            var screen=_viewport.WorldToSurface(Vector3.Transform(worlds[i].Translation,AnimationPreviewWorld()));if(screen.Z<0||screen.Z>1)continue;
            float d=Vector2.DistanceSquared(new(mouse.X,mouse.Y),new(screen.X,screen.Y));if(d<best){best=d;found=i;}
        }
        return found;
    }
    public bool DirectPointerDown(Point point)=>HandleDirectPose(new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0),true);
    public void DirectPointerMove(Point point)=>HandleDirectPose(new MouseEventArgs(MouseButtons.Left,0,point.X,point.Y,0),false);
    public void DirectPointerUp(Point point)=>FinishDirectPose(new MouseEventArgs(MouseButtons.Left,0,point.X,point.Y,0));
    private bool HandleDirectPose(MouseEventArgs e,bool pressed)
    {
        if(!_animationDraft||!PoseEditingEnabled||_rigged?.Rig.IsValid!=true||ActiveAnimationClip is null||e.Button!=MouseButtons.Left)return false;
        if(pressed)
        {
            var worlds=ModelRigBridge.BoneWorldTransforms(_rigged,_clip,_animTime);var mouse=_viewport.ControlToSurface(e.Location);Vector2 p=new(mouse.X,mouse.Y);
            float best=14*14;int selected=-1,hitParent=-1,handle=2;
            bool midpointHit = false;
            foreach(int i in Enumerable.Range(0,worlds.Length).OrderBy(i=>i==SelectedAnimationBoneIndex?0:1))
            {
                int candidateParent=_rigged.Rig.Bones[i].ParentIndex;if(candidateParent<0||candidateParent>=worlds.Length)continue;
                var a=_viewport.WorldToSurface(Vector3.Transform(worlds[candidateParent].Translation,AnimationPreviewWorld()));var b=_viewport.WorldToSurface(Vector3.Transform(worlds[i].Translation,AnimationPreviewWorld()));
                if(a.Z<0||a.Z>1||b.Z<0||b.Z>1)continue;
                Vector2 av=new(a.X,a.Y),bv=new(b.X,b.Y),ab=bv-av;float t=ab.LengthSquared()>1e-6f?Math.Clamp(Vector2.Dot(p-av,ab)/ab.LengthSquared(),0,1):0;
                // The visible midpoint handle wins over overlapping joint circles and tips.
                bool midpoint = Vector2.DistanceSquared(p, (av + bv) * .5f) <= 8 * 8;
                float distance=Vector2.DistanceSquared(p,av+ab*t);
                if (midpointHit && !midpoint || midpoint == midpointHit && distance >= best) continue;
                if (!midpoint && distance >= 14 * 14) continue;
                midpointHit = midpoint;
                selected=i;hitParent=candidateParent;handle=t<.2f?0:t>.8f?1:2;best=distance;
                if (midpoint) handle = 2;
            }
            int joint = NearestJoint(e.Location, worlds, 14);
            int circle = HitJointCircle(p, worlds);
            bool selectedJoint = false;
            if (!midpointHit && circle >= 0 && (selected < 0 || joint == circle && circle != selected))
            { selected = circle; hitParent = _rigged.Rig.Bones[circle].ParentIndex; handle = 1; selectedJoint = true; }
            // Layout edits move the picked joint itself, not the incoming bone and its
            // other endpoint. Pose-mode tips retain their rotation behaviour.
            if (PreviewRigLayout && !midpointHit && (joint >= 0 || circle >= 0))
            { selected = joint >= 0 ? joint : circle; hitParent = -1; handle = 1; selectedJoint = true; }
            if(selected<0){selected=joint;hitParent=-1;}
            if(selected<0)return false;
            SelectAnimationNode(_rigged.Rig.Bones[selected].Name);_directBone=selected;_directParent=hitParent;_directHandle=handle;
            SelectedRigElementIsBone = hitParent >= 0 && !selectedJoint;
            Vector3 origin = hitParent >= 0 && handle == 2 ? (worlds[selected].Translation + worlds[hitParent].Translation) * .5f : worlds[selected].Translation;
            _directPlaneNormal = DragPlaneNormal();
            if(!PlanePoint(e.Location,origin,out _directStart))
            { PlacementFeedback?.Invoke("This plane is edge-on. Choose View or turn the camera before dragging."); return true; }
            bool moving = hitParent < 0 || DirectPoseMode == 1 || DirectPoseMode == 0 && handle == 2;
            DirectGestureDescription = $"{(moving ? "Moving" : DirectPoseMode == 3 ? "Resizing" : "Rotating")} {_rigged.Rig.Bones[selected].Name}";
            PlacementFeedback?.Invoke(DirectGestureDescription + ". Esc cancels; Undo restores the previous pose.");
            _directBefore=CaptureWorkingPose();_directWorlds=worlds;_animPlaying=false;_viewport.NavigationEnabled=false;_viewport.Host.Capture=true;return true;
        }
        if(_directBefore is null||_directWorlds is null||_directBone<0||!PlanePoint(e.Location,_directStart,out var point))return false;
        var source=_directWorlds;var desired=(Matrix4x4[])source.Clone();
        int bone=_directBone,parent=_directParent;bool move=parent<0||DirectPoseMode==1||(DirectPoseMode==0&&_directHandle==2);
        Matrix4x4 transform;
        if(move)transform=Matrix4x4.CreateTranslation(point-_directStart);
        else
        {
            Vector3 pivot=source[_directHandle==0?bone:parent].Translation;
            Vector3 start=_directStart-pivot,end=point-pivot;
            if(start.LengthSquared()<1e-8f||end.LengthSquared()<1e-8f)return true;
            if(DirectPoseMode==3)
            {
                float factor=Math.Clamp(end.Length()/start.Length(),.05f,20);Vector3 axis=Vector3.Normalize(source[bone].Translation-source[parent].Translation);
                float f=factor-1;var stretch=new Matrix4x4(1+f*axis.X*axis.X,f*axis.X*axis.Y,f*axis.X*axis.Z,0,f*axis.Y*axis.X,1+f*axis.Y*axis.Y,f*axis.Y*axis.Z,0,f*axis.Z*axis.X,f*axis.Z*axis.Y,1+f*axis.Z*axis.Z,0,0,0,0,1);
                transform=Matrix4x4.CreateTranslation(-pivot)*stretch*Matrix4x4.CreateTranslation(pivot);
            }
            else
            {
                Vector3 axis=_directPlaneNormal;
                start -= axis * Vector3.Dot(start, axis); end -= axis * Vector3.Dot(end, axis);
                if (start.LengthSquared() < 1e-8f || end.LengthSquared() < 1e-8f) return true;
                float angle=MathF.Atan2(Vector3.Dot(axis,Vector3.Cross(start,end)),Vector3.Dot(start,end));
                transform=Matrix4x4.CreateTranslation(-pivot)*Matrix4x4.CreateFromAxisAngle(axis,angle)*Matrix4x4.CreateTranslation(pivot);
            }
        }
        desired[bone]=source[bone]*transform;
        // The parent owns the upstream skin segment. Rotating its basis here twists the
        // torso even when the pivot has not moved. Only reposition the shared endpoint.
        if(parent>=0)desired[parent].Translation=Vector3.Transform(source[parent].Translation, transform);
        if(PropagateAnimationEdit)
            for(int i=0;i<source.Length;i++){int ancestor=_rigged.Rig.Bones[i].ParentIndex;while(ancestor>=0){if(ancestor==bone){desired[i]=source[i]*transform;break;}ancestor=_rigged.Rig.Bones[ancestor].ParentIndex;}}
        if((ModifierKeys&Keys.Shift)!=0 && PreviewRigLayout)
        {
            int moving=_directHandle==0&&parent>=0?parent:bone;int snap=NearestJoint(e.Location,source,18);
            if(snap>=0&&snap!=bone&&snap!=parent)desired[moving].Translation=source[snap].Translation;
        }
        var locals=new Matrix4x4[desired.Length];
        for(int i=0;i<desired.Length;i++){int pIndex=_rigged.Rig.Bones[i].ParentIndex;var inverse=Matrix4x4.Identity;if(pIndex>=0)Matrix4x4.Invert(desired[pIndex],out inverse);locals[i]=desired[i]*inverse;}
        RestoreAnimationPose(ActiveAnimationClip,CurrentAnimationFrame,locals);return true;
    }
    private void FinishDirectPose(MouseEventArgs e)
    {
        if(e.Button!=MouseButtons.Left||_directBefore is null||ActiveAnimationClip is null)return;
        var after = CaptureWorkingPose();
        if (!_directBefore.SequenceEqual(after)) RecordPoseEdit(ActiveAnimationClip,CurrentAnimationFrame,_directBefore,after);
        _directBefore=null;_directWorlds=null;DirectGestureDescription="";_viewport.NavigationEnabled=true;_viewport.Host.Capture=false;
    }
    private bool CancelDirectPose()
    {
        if(_directBefore is null||ActiveAnimationClip is null)return false;
        RestoreAnimationPose(ActiveAnimationClip,CurrentAnimationFrame,_directBefore);_directBefore=null;_directWorlds=null;DirectGestureDescription="";_viewport.NavigationEnabled=true;_viewport.Host.Capture=false;return true;
    }
    private int HitJointCircle(Vector2 mouse, Matrix4x4[] worlds)
    {
        Matrix4x4.Invert(_viewport.ViewMatrix, out var camera);
        var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, camera));
        float closest = 14; int found = -1;
        for (int i = 0; i < worlds.Length; i++)
        {
            float radius = _rigged!.Rig.Bones[i].JointRadius;
            if (radius <= 0) continue;
            var world = Vector3.Transform(worlds[i].Translation, AnimationPreviewWorld());
            var centre = _viewport.WorldToSurface(world); if (centre.Z < 0 || centre.Z > 1) continue;
            var edge = _viewport.WorldToSurface(world + right * radius);
            float distance = Vector2.Distance(mouse, new(centre.X, centre.Y));
            float pixels = Vector2.Distance(new(centre.X, centre.Y), new(edge.X, edge.Y));
            float hit = Math.Min(distance, Math.Abs(distance - pixels));
            if (hit < closest) { closest = hit; found = i; }
        }
        return found;
    }
    private void DrawDirectHandles(IRenderController renderer,Matrix4x4[] worlds,Matrix4x4 world)
    {
        var colour=new RenderColor(.3f,.85f,1);int selected=SelectedAnimationBoneIndex;
        for(int i=0;i<worlds.Length;i++)
        {
            var position=Vector3.Transform(worlds[i].Translation,world);var p=_viewport.WorldToSurface(position);if(p.Z<0||p.Z>1)continue;
            float radius=_rigged!.Rig.Bones[i].JointRadius;
            if(radius>0)
            {
                Matrix4x4.Invert(_viewport.ViewMatrix,out var camera);Vector3 right=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX,camera));var edge=_viewport.WorldToSurface(position+right*radius);
                EditorTransformGizmo.DrawCircleOutline(renderer,new(p.X,p.Y),Vector2.Distance(new(p.X,p.Y),new(edge.X,edge.Y)),i==selected?RenderColor.White:colour,1.3f);
                renderer.DrawLine(p.X-4,p.Y,p.X+4,p.Y,colour,1);renderer.DrawLine(p.X,p.Y-4,p.X,p.Y+4,colour,1);
            }
            if(i!=selected)continue;
            renderer.DrawRect(p.X-4,p.Y-4,8,8,RenderColor.White,false,-9600);
            int parent=_rigged.Rig.Bones[i].ParentIndex;if(parent<0)continue;
            var a=_viewport.WorldToSurface(Vector3.Transform(worlds[parent].Translation,world));
            renderer.DrawLine(a.X,a.Y,p.X,p.Y,RenderColor.White,2);
            renderer.DrawRect((a.X+p.X)/2-4,(a.Y+p.Y)/2-4,8,8,colour,true,-9601);
        }
    }
}

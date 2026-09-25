using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelRigViewportControl
{
    internal bool DrawChildJoints { get; set; }
    internal bool DrawJointCircles { get; set; }
    internal string JointDrawingPlane { get; set; } = "View";
    internal event Action<Vector3,Vector3>? BoneDrawn;
    internal event Action<Vector3,float>? JointPlaced;
    internal event Action<string>? PlacementFeedback;
    private ModelRigPlacementSurface? _jointPlacementSurface;
    private bool _drawingJoint;
    private Vector3 _jointDrawOrigin,_jointDrawTip;
    private Vector3 _jointRadiusNormal;
    private bool _jointDrawTipValid;

    public bool TryPickRigInterior(Point point, out Vector3 center)
    {
        center = default;
        if (_rigged is null) return false;
        _jointPlacementSurface ??= new ModelRigPlacementSurface(_rigged);
        var ray = _viewport.PickRay(point);
        return _jointPlacementSurface.TryPick(ray.Origin, ray.Direction, AnimationPreviewWorld(), out center);
    }

    public bool DrawingPointerDown(Point point)
    {
        HandleJointDrawing(new(MouseButtons.Left, 1, point.X, point.Y, 0), true);
        return _drawingJoint;
    }
    public void DrawingPointerMove(Point point) => HandleJointDrawing(new(MouseButtons.Left, 0, point.X, point.Y, 0), false);
    public void DrawingPointerUp(Point point) => FinishJointDrawing(new(MouseButtons.Left, 0, point.X, point.Y, 0));

    private bool PlacementPoint(Point point, out Vector3 center)
    {
        if (_rigged is not null)
        {
            var worlds = ModelRigBridge.BoneWorldTransforms(_rigged, _clip, _animTime);
            int closest = NearestJoint(point, worlds, 14);
            if (closest >= 0) { center = worlds[closest].Translation; return true; }
        }
        return TryPickRigInterior(point, out center);
    }

    private bool HandleJointDrawing(MouseEventArgs e,bool pressed)
    {
        if(!_animationDraft||!PreviewRigLayout||(!DrawChildJoints&&!DrawJointCircles)||e.Button!=MouseButtons.Left)return false;
        if(pressed)
        {
            if (!PlacementPoint(e.Location, out _jointDrawOrigin))
            {
                PlacementFeedback?.Invoke("Start on the mesh or an existing joint. Orbit the view to reach another body part.");
                return true;
            }
            var ray = _viewport.PickRay(e.Location);
            if (!Matrix4x4.Invert(AnimationPreviewWorld(), out var inverse)) return true;
            _jointRadiusNormal = Vector3.Normalize(Vector3.TransformNormal(ray.Direction, inverse));
            _jointDrawTip=_jointDrawOrigin;_drawingJoint=true;_viewport.NavigationEnabled=false;_viewport.Host.Capture=true;
        }
        if (_drawingJoint) UpdateJointTip(e.Location);
        return true;
    }

    private void UpdateJointTip(Point point)
    {
        // A joint's radius is drawn in the view plane at its picked interior depth. A bone's
        // far endpoint must get its own mesh depth, even when the drag crosses different limbs.
        _jointDrawTipValid = DrawJointCircles
            ? RadiusPoint(point, out _jointDrawTip)
            : PlacementPoint(point, out _jointDrawTip);
    }

    private bool RadiusPoint(Point point, out Vector3 tip)
    {
        tip = _jointDrawOrigin;
        if (!Matrix4x4.Invert(AnimationPreviewWorld(), out var inverse)) return false;
        var ray = _viewport.PickRay(point);
        var origin = Vector3.Transform(ray.Origin, inverse);
        var direction = Vector3.TransformNormal(ray.Direction, inverse);
        float denominator = Vector3.Dot(direction, _jointRadiusNormal);
        if (Math.Abs(denominator) < .0001f) return false;
        float distance = Vector3.Dot(_jointDrawOrigin - origin, _jointRadiusNormal) / denominator;
        if (distance <= 0) return false;
        tip = origin + direction * distance;
        return true;
    }
    private Vector3 DragPlaneNormal()
    {
        if (JointDrawingPlane == "XY") return Vector3.UnitZ;
        if (JointDrawingPlane == "XZ") return Vector3.UnitY;
        if (JointDrawingPlane == "YZ") return Vector3.UnitX;
        Matrix4x4.Invert(_viewport.ViewMatrix, out var camera);
        Matrix4x4.Invert(AnimationPreviewWorld(), out var inverse);
        return Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(Vector3.UnitZ, camera), inverse));
    }

    private bool PlanePoint(Point mouse, Vector3 origin, out Vector3 hit)
    {
        hit = origin;
        if (!Matrix4x4.Invert(AnimationPreviewWorld(), out var inverse)) return false;
        var ray = _viewport.PickRay(mouse);
        var rayOrigin = Vector3.Transform(ray.Origin, inverse);
        var direction = Vector3.Normalize(Vector3.TransformNormal(ray.Direction, inverse));
        float denominator = Vector3.Dot(direction, _directPlaneNormal);
        if (Math.Abs(denominator) < .08f) return false;
        float distance = Vector3.Dot(origin - rayOrigin, _directPlaneNormal) / denominator;
        if (distance <= 0 || !float.IsFinite(distance)) return false;
        hit = rayOrigin + direction * distance;
        return true;
    }
    private void FinishJointDrawing(MouseEventArgs e)
    {
        if(!_drawingJoint||e.Button!=MouseButtons.Left)return;
        UpdateJointTip(e.Location);
        _drawingJoint=false;_viewport.NavigationEnabled=true;_viewport.Host.Capture=false;
        if (!_jointDrawTipValid)
        {
            PlacementFeedback?.Invoke("Bone not placed. Release on the mesh or an existing joint to set its depth.");
            return;
        }
        if(DrawJointCircles)JointPlaced?.Invoke(_jointDrawOrigin,Math.Max(.005f,Vector3.Distance(_jointDrawOrigin,_jointDrawTip)));
        else if(Vector3.DistanceSquared(_jointDrawOrigin,_jointDrawTip)>1e-8f)BoneDrawn?.Invoke(_jointDrawOrigin,_jointDrawTip);
    }
    internal bool CancelJointDrawing()
    {
        if(_drawingJoint){_drawingJoint=false;_viewport.NavigationEnabled=true;_viewport.Host.Capture=false;return true;}
        if(DrawChildJoints||DrawJointCircles){DrawChildJoints=DrawJointCircles=false;return true;}return false;
    }
    private void DrawJointStroke(IRenderController renderer)
    {
        if(!_drawingJoint)return;var world=AnimationPreviewWorld();
        var start=_viewport.WorldToSurface(Vector3.Transform(_jointDrawOrigin,world));var end=_viewport.WorldToSurface(Vector3.Transform(_jointDrawTip,world));
        var colour=new RenderColor(.35f,.9f,1f);
        if (_jointDrawTipValid)
        {
            if(DrawJointCircles)EditorTransformGizmo.DrawCircleOutline(renderer,new(start.X,start.Y),Vector2.Distance(new(start.X,start.Y),new(end.X,end.Y)),colour,1.5f);
            else renderer.DrawLine(start.X,start.Y,end.X,end.Y,colour,2);
        }
        renderer.DrawLine(start.X-4,start.Y,start.X+4,start.Y,colour,1);renderer.DrawLine(start.X,start.Y-4,start.X,start.Y+4,colour,1);
    }
}

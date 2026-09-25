using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelRigWizardDialog
{
    private Vector3 _originalBend;
    private Vector3 Project(Vector3 point) => _preview.Viewport.WorldToSurface(point - _draft.Pivot.Position);
    private void DrawReview(IRenderController renderer)
    {
        if (CurrentStep != 2 || Setup.Joints.Count == 0) return;
        foreach (var joint in Setup.Joints)
        {
            var screen = Project(joint.Position);
            bool selected = joint.Role == _selectedRole;
            var color = joint.Issue.Length > 0 && !joint.Reviewed ? new RenderColor(1, .66f, .2f) : new RenderColor(.3f, .9f, 1);
            float radius = selected ? 7 : 4;
            EditorTransformGizmo.DrawCircleOutline(renderer, new(screen.X, screen.Y), radius, color, selected ? 2 : 1);
            if (selected || joint.Pinned) renderer.DrawText(joint.Role + (joint.Pinned ? " · pinned" : ""), screen.X + 10, screen.Y - 8, 12, color);
        }
        var chain = Setup.Chains.FirstOrDefault(c => c.Roles.Contains(_selectedRole));
        if (chain is null) return;
        var jointPosition = Setup.Joints.Single(j => j.Role == chain.Roles[0]).Position;
        var a = Project(jointPosition); var b = Project(jointPosition + chain.BendDirection * BendLength());
        var orange = new RenderColor(1, .55f, .2f);
        renderer.DrawLine(a.X, a.Y, b.X, b.Y, orange, 2);
        EditorTransformGizmo.DrawCircleOutline(renderer, new(b.X, b.Y), 6, orange, 2);
        renderer.DrawText("Bend", b.X + 8, b.Y, 12, orange);
    }
    private float BendLength() => Math.Max(.001f, _draft.Bounds.Size.Length() * .12f);
    private void ReviewDown(object? sender, MouseEventArgs e)
    {
        if (_busy || CurrentStep != 2 || e.Button != MouseButtons.Left) return;
        if (_bendMode)
        {
            var chain = Setup.Chains.FirstOrDefault(c => c.Roles.Contains(_selectedRole));
            if (chain is null) return;
            _originalBend = chain.BendDirection;
            _dragOrigin = Setup.Joints.Single(j => j.Role == chain.Roles[0]).Position + chain.BendDirection * BendLength();
        }
        else
        {
            var mouse = _preview.Viewport.ControlToSurface(e.Location);
            var nearest = Setup.Joints.OrderBy(j => Vector2.Distance(new(Project(j.Position).X, Project(j.Position).Y), new(mouse.X, mouse.Y))).FirstOrDefault();
            if (nearest is null || Vector2.Distance(new(Project(nearest.Position).X, Project(nearest.Position).Y), new(mouse.X, mouse.Y)) > 14) return;
            _selectedRole = nearest.Role; _dragOrigin = nearest.Position;
            if (Setup.ReuseExistingRig) { GoToStep(2); return; }
        }
        _dragNormal = _preview.Viewport.PickRay(e.Location).Direction;
        _dragging = true; _preview.Viewport.NavigationEnabled = false; _preview.Viewport.Host.Capture = true;
    }
    private void CancelReviewDrag()
    {
        if (_bendMode)
        {
            var chain = Setup.Chains.FirstOrDefault(c => c.Roles.Contains(_selectedRole)); if (chain is not null) chain.BendDirection = _originalBend;
        }
        else Setup.Joints.Single(j => j.Role == _selectedRole).Position = _dragOrigin;
        _dragging = false; _preview.Viewport.NavigationEnabled = true; _preview.Viewport.Host.Capture = false; AdoptPreview();
    }
    private void ReviewMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var ray = _preview.Viewport.PickRay(e.Location);
        float denominator = Vector3.Dot(ray.Direction, _dragNormal);
        if (MathF.Abs(denominator) < .01f) return;
        float distance = Vector3.Dot(_dragOrigin - _draft.Pivot.Position - ray.Origin, _dragNormal) / denominator;
        if (distance <= 0) return;
        var point = ray.Origin + ray.Direction * distance + _draft.Pivot.Position;
        if (_bendMode)
        {
            var chain = Setup.Chains.First(c => c.Roles.Contains(_selectedRole));
            var origin = Setup.Joints.Single(j => j.Role == chain.Roles[0]).Position;
            if (Vector3.DistanceSquared(point, origin) > 1e-10f) chain.BendDirection = Vector3.Normalize(point - origin);
        }
        else
        {
            // Keep the drag lightweight; the rig and GPU preview are rebuilt only on release.
            var joint = Setup.Joints.Single(j => j.Role == _selectedRole); joint.Position = point;
        }
    }
    private void ReviewUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false; _preview.Viewport.NavigationEnabled = true; _preview.Viewport.Host.Capture = false;
        if (!_bendMode)
        {
            var joint = Setup.Joints.Single(j => j.Role == _selectedRole);
            ModelRigWizardWorkflow.EditLandmark(_draft, joint.Role, joint.Position, _mirror);
        }
        GoToStep(2);
    }
}

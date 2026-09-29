using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Physics;
using Genesis.Physics.Systems;
using Genesis.Runtime;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private sealed class RoomPhysicsPreview : IDisposable
    {
        public readonly RuntimeScene Scene = new("Room physics inspection");
        public readonly ObjectDrawAssetRegistry.PreviewScope Assets = new();
        public RoomPhysicsPreview(string projectRoot, RoomAsset room)
        {
            try
            {
                RoomAsset copy = room.DeepClone();
                using (Assets.Activate())
                {
                    new RoomSceneBuilder(projectRoot).Build(Scene, copy);
                    if (Scene.Physics is { } physics)
                    {
                        physics.EnableThreadDispatcher = false; physics.CaptureDebugContacts = true;
                        new PhysicsRegistrationSystem(physics).FixedUpdate(Scene.World, 0);
                    }
                    if (RoomTerrainSubsystem.ShouldRegister(copy))
                    {
                        RoomTerrainSubsystem terrain = Scene.AddSubsystem(new RoomTerrainSubsystem(projectRoot, copy, null!));
                        terrain.FixedUpdate(Scene, 0);
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Step(float delta) { using (Assets.Activate()) Scene.UpdateFixed(delta); }
        public void Dispose() { using (Assets.Activate()) Scene.Dispose(); Assets.Clear(); }
    }

    private RoomPhysicsPreview? _physicsPreview;
    private FlowLayoutPanel? _physicsDebugBar;
    private ToolStripMenuItem? _physicsDebugMenu;
    private Label? _physicsDebugSummary;
    private Button? _physicsPreviewRun;
    private System.Windows.Forms.Timer? _physicsPreviewTimer;
    private bool _physicsOverlay, _physicsProbeArmed;
    private bool _physicsShowColliders = true, _physicsShowContacts = true, _physicsShowRay = true;
    private (Vector3 Start, Vector3 End, bool Hit, Vector3 Normal)? _physicsProbe;
    private int _physicsPreviewSteps;
    private IReadOnlyList<PhysicsDebugCollider> _physicsDebugColliders = [];
    public bool PhysicsOverlayVisible => _physicsOverlay;
    public IReadOnlyList<PhysicsDebugCollider> PhysicsOverlayColliders => _physicsDebugColliders;
    public IReadOnlyList<PhysicsDebugContact> PhysicsOverlayContacts => _physicsPreview?.Scene.Physics?.DebugContacts ?? [];
    public PhysicsRaycastHit? LastPhysicsProbe { get; private set; }

    private void BuildRoomPhysicsDebug(ToolStripDropDownButton view, Panel viewportHost)
    {
        _physicsDebugMenu = new("Physics overlay") { CheckOnClick = true, ToolTipText = "Inspect actual colliders, contacts and raycasts on a temporary copy of this Room." };
        _physicsDebugMenu.Click += (_, _) => SetPhysicsOverlayVisible(_physicsDebugMenu.Checked); view.DropDownItems.Add(_physicsDebugMenu);
        _physicsDebugBar = new() { Name = "RoomPhysicsDebugBar", Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            BackColor = EditorChrome.Surface, Padding = new Padding(8, 4, 8, 4), Visible = false };
        AddButton("Step", "RoomPhysicsStep", () => StepPhysicsPreview());
        _physicsPreviewRun = AddButton("Run", "RoomPhysicsRun", () => SetPhysicsPreviewRunning(_physicsPreviewTimer?.Enabled != true));
        AddButton("Restart", "RoomPhysicsRestart", RestartPhysicsPreview);
        AddButton("Probe ray", "RoomPhysicsProbe", () => { _physicsProbeArmed = true; UpdateStatus("Click the Room view to probe a physics ray. Esc cancels."); });
        AddToggle("Colliders", true, value => _physicsShowColliders = value);
        AddToggle("Contacts", true, value => _physicsShowContacts = value);
        AddToggle("Ray", true, value => _physicsShowRay = value);
        _physicsDebugSummary = new() { Name = "RoomPhysicsDebugSummary", AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = EditorChrome.Muted, Margin = new Padding(8) };
        _physicsDebugBar.Controls.Add(_physicsDebugSummary);
        viewportHost.Controls.Add(_physicsDebugBar); _physicsDebugBar.BringToFront();
        _physicsPreviewTimer = new() { Interval = 16 };
        _physicsPreviewTimer.Tick += (_, _) => StepPhysicsPreview();
        Disposed += (_, _) => { _physicsPreviewTimer?.Dispose(); _physicsPreview?.Dispose(); _physicsPreview = null; };

        Button AddButton(string label, string name, Action action)
        {
            Button button = new() { Name = name, Text = label, AutoSize = true }; EditorChrome.StyleField(button);
            button.Click += (_, _) => action(); _physicsDebugBar.Controls.Add(button); return button;
        }
        void AddToggle(string text, bool state, Action<bool> change)
        {
            CheckBox check = new() { Text = text, Checked = state, AutoSize = true, Margin = new Padding(8) };
            check.CheckedChanged += (_, _) => { change(check.Checked); _viewport.Invalidate(); }; _physicsDebugBar.Controls.Add(check);
        }
    }

    public void SetPhysicsOverlayVisible(bool visible)
    {
        _physicsOverlay = visible; _physicsProbeArmed = false;
        if (_physicsDebugBar is not null) _physicsDebugBar.Visible = visible;
        if (_physicsDebugMenu is not null) _physicsDebugMenu.Checked = visible;
        if (visible) RestartPhysicsPreview();
        else { SetPhysicsPreviewRunning(false); _physicsPreview?.Dispose(); _physicsPreview = null; _physicsDebugColliders = []; }
        _viewport.Invalidate();
    }
    public void RestartPhysicsPreview()
    {
        SetPhysicsPreviewRunning(false); _physicsPreview?.Dispose(); _physicsPreview = null;
        _physicsDebugColliders = []; _physicsProbe = null; LastPhysicsProbe = null; _physicsPreviewSteps = 0;
        if (!_physicsOverlay) return;
        try { _physicsPreview = new(ProjectRoot, _room); RefreshPhysicsDebugData(); }
        catch (Exception ex) { if (_physicsDebugSummary is not null) _physicsDebugSummary.Text = "Physics preview: " + ex.Message; }
        _viewport.Invalidate();
    }
    public void SetPhysicsPreviewRunning(bool running)
    {
        if (_physicsPreviewTimer is not null) _physicsPreviewTimer.Enabled = running && _physicsOverlay && _physicsPreview is not null;
        if (_physicsPreviewRun is not null) _physicsPreviewRun.Text = _physicsPreviewTimer?.Enabled == true ? "Pause" : "Run";
    }
    public void StepPhysicsPreview()
    {
        if (!_physicsOverlay || _physicsPreview is null) return;
        try
        {
            _physicsPreview.Step(1f / Math.Clamp(_room.Settings.FixedFps, 1, 1000)); _physicsPreviewSteps++;
            RefreshPhysicsDebugData(); _viewport.Invalidate();
        }
        catch (Exception ex) { SetPhysicsPreviewRunning(false); if (_physicsDebugSummary is not null) _physicsDebugSummary.Text = "Physics preview: " + ex.Message; }
    }
    private void RefreshPhysicsDebugData()
    {
        _physicsDebugColliders = _physicsPreview?.Scene.Physics?.CaptureColliderDebug() ?? [];
        if (_physicsDebugSummary is not null) _physicsDebugSummary.Text = $"{_physicsDebugColliders.Count} colliders · {PhysicsOverlayContacts.Count} contacts · step {_physicsPreviewSteps}. "
            + "Saved bodies and Model colliders; scripts run in game. Placed Objects stay unchanged."
            + (_physicsDebugColliders.Any(collider => collider.DetailLimited) ? " Dense mesh wires are sampled." : "");
    }
    public bool ProbePhysicsRay(Vector3 origin, Vector3 direction, float distance = 200)
    {
        if (!_physicsOverlay || _physicsPreview?.Scene.Physics is not { } physics || direction.LengthSquared() < 1e-8f) return false;
        direction = Vector3.Normalize(direction);
        bool found = physics.Raycast(_physicsPreview.Scene.World, origin, direction, distance, out PhysicsRaycastHit hit);
        LastPhysicsProbe = found ? hit : null; _physicsProbe = (origin, found ? hit.Point : origin + direction * distance, found, found ? hit.Normal : Vector3.Zero);
        _viewport.Invalidate(); return found;
    }
    private bool TryProbePhysicsPointer(Point point, MouseButtons button)
    {
        if (!_physicsOverlay || !_physicsProbeArmed || button != MouseButtons.Left) return false;
        _physicsProbeArmed = false;
        if (_viewport.Mode2D)
        {
            Vector2 p = _viewport.ControlToWorld2D(point); ProbePhysicsRay(new(p.X / SpritePhysicsBinding.PixelsPerMetre, -p.Y / SpritePhysicsBinding.PixelsPerMetre, 10), -Vector3.UnitZ, 20);
        }
        else { var ray = _viewport.PickRay(point); ProbePhysicsRay(ray.Origin, ray.Direction); }
        UpdateStatus(LastPhysicsProbe is { } hit ? $"Physics ray hit at {hit.Distance:0.###} metres." : "Physics ray missed the registered colliders.");
        return true;
    }
    private void DrawRoomPhysicsDebug(IRenderController renderer)
    {
        if (!_physicsOverlay) return;
        if (_physicsShowColliders) foreach (PhysicsDebugCollider collider in _physicsDebugColliders)
        {
            RenderColor color = collider.IsSensor ? new(1, .7f, .15f) : collider.IsStatic ? new(.15f, .85f, .9f) : new(.3f, 1, .45f);
            foreach (PhysicsDebugLine edge in collider.Lines) Line(edge.Start, edge.End, color);
        }
        if (_physicsShowContacts) foreach (PhysicsDebugContact contact in PhysicsOverlayContacts)
        {
            Cross(contact.Position, new(1, .3f, .65f)); Line(contact.Position, contact.Position + contact.Normal * .35f, new(1, .3f, .65f));
        }
        if (_physicsShowRay && _physicsProbe is { } probe)
        {
            RenderColor color = probe.Hit ? new(1, .85f, .1f) : new(1, .35f, .25f);
            Line(probe.Start, probe.End, color); Cross(probe.End, color);
            if (probe.Hit) Line(probe.End, probe.End + probe.Normal * .5f, color);
        }
        void Cross(Vector3 point, RenderColor color)
        { Line(point - Vector3.UnitX * .07f, point + Vector3.UnitX * .07f, color); Line(point - Vector3.UnitY * .07f, point + Vector3.UnitY * .07f, color); }
        Vector3 Project(Vector3 point)
        {
            if (!_viewport.Mode2D) return _viewport.WorldToSurface(point);
            Vector2 p = _viewport.World2DToSurface(new(point.X * SpritePhysicsBinding.PixelsPerMetre, -point.Y * SpritePhysicsBinding.PixelsPerMetre)); return new(p, .5f);
        }
        void Line(Vector3 a, Vector3 b, RenderColor color)
        {
            Vector3 start = Project(a), end = Project(b);
            if (!float.IsFinite(start.LengthSquared()) || !float.IsFinite(end.LengthSquared()) || start.Z < 0 || start.Z > 1 || end.Z < 0 || end.Z > 1) return;
            renderer.DrawLine(start.X, start.Y, end.X, end.Y, color, 1.5f, depth: -9300);
        }
    }
}

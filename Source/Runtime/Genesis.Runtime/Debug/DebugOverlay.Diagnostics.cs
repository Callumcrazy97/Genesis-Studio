using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Spatial;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Debugger;

/// <summary>
/// The debug screen's "why does it look like that" pages: Draw (colliders and hitboxes, GUI layer
/// outlines, the diagnostic view modes and their legend), GUI (which User Interfaces and HUD scripts
/// drew, how many draw calls each, where) and Object (the report on one instance: what it is and
/// why it is or is not drawn). What they draw over the frame shows while the screen is open, with
/// the panel closed too; nothing here runs while the screen is closed.
/// </summary>
public sealed partial class DebugOverlay
{
    /// <summary>The game's debug screen: the last one made and not yet disposed (Engine.Debug.*).</summary>
    public static DebugOverlay Current { get; private set; }

    private readonly DebugColliderOverlay _colliderOverlay = new();
    private int _lastWidth = 1280, _lastHeight = 720;
    private RectangleF _stripBounds, _panelBounds;
    private DebugObjectReport _report;
    private long _reportStamp;

    private static readonly CultureInfo DiagnosticsCulture = CultureInfo.InvariantCulture;

    private static readonly Vector4[] LayerColours =
    {
        new(1f, 0.85f, 0.2f, 1f), new(0.3f, 0.9f, 1f, 1f), new(1f, 0.4f, 0.8f, 1f),
        new(0.5f, 1f, 0.4f, 1f), new(1f, 0.55f, 0.25f, 1f), new(0.7f, 0.6f, 1f, 1f),
    };

    /// <summary>
    /// For unattended runs (game.run): Engine.Debug settings to start with, separated by ';'
    /// (<c>Engine.Debug.Overlay.Colliders.Enabled=true;Engine.Debug.View.Mode=lod</c>), plus
    /// <c>panel=Draw</c> to open the full panel on a tab and <c>report=4</c> to write, after that many
    /// seconds, one line a placed instance to the game's log saying whether it is drawn and why not.
    /// </summary>
    public const string AutomationEnvironmentVariable = "GENESIS_DEBUG_SCREEN";

    /// <summary>A line for the game's log (the Player writes them to project_player.log).</summary>
    public event Action<string> DiagnosticLine;

    private string _automationApplied;
    private float _reportAfter = -1f, _diagnosticsClock;

    /// <summary>Colliders of each kind drawn in the last frame.</summary>
    public int CollidersDrawn(DebugColliderKind kind) => _colliderOverlay.Count(kind);

    private void ApplyAutomationSettings()
    {
        string text = Environment.GetEnvironmentVariable(AutomationEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(text)) return;
        var settings = new List<KeyValuePair<string, string>>();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=');
            string name = (equals < 0 ? part : part[..equals]).Trim();
            string value = equals < 0 ? "true" : part[(equals + 1)..].Trim();
            if (name.Equals("panel", StringComparison.OrdinalIgnoreCase))
            {
                foreach (DebugHudPanel panel in Enum.GetValues<DebugHudPanel>())
                    if (panel.ToString().Equals(value, StringComparison.OrdinalIgnoreCase) || PanelName(panel).Equals(value, StringComparison.OrdinalIgnoreCase))
                    {
                        IsVisible = true;
                        SelectPanel(panel);
                    }
                continue;
            }
            if (name.Equals("report", StringComparison.OrdinalIgnoreCase))
            {
                _reportAfter = float.TryParse(value, NumberStyles.Float, DiagnosticsCulture, out float seconds) ? Math.Max(0f, seconds) : 3f;
                continue;
            }
            settings.Add(new KeyValuePair<string, string>(name, value));
        }
        var problems = new List<string>();
        int applied = EngineSettings.Apply(settings, new Genesis.Shared.Scripting.PgslContext(), problems);
        foreach (string problem in problems) Log("[WARN] " + problem);
        _automationApplied = $"debug screen: {applied} setting(s) from {AutomationEnvironmentVariable} ({text})"
            + (problems.Count > 0 ? "; " + string.Join("; ", problems) : "");
    }

    // Once a frame from Advance: the automation's notes and its timed report.
    private void AdvanceDiagnostics(float dt)
    {
        if (_automationApplied != null && DiagnosticLine != null)
        {
            DiagnosticLine.Invoke(_automationApplied);
            _automationApplied = null;
        }
        if (_reportAfter < 0f) return;
        _diagnosticsClock += Math.Max(0f, dt);
        if (_diagnosticsClock < _reportAfter) return;
        _reportAfter = -1f;
        WriteObjectReports();
    }

    /// <summary>Writes one line for each placed instance to the game's log: drawn (and at what level of detail), or why not.</summary>
    public int WriteObjectReports()
    {
        if (_activeScene == null) return 0;
        DebugProjection projection = DebugProjection.For(_activeScene, _lastWidth, _lastHeight);
        int written = 0, drawn = 0;
        foreach (int id in InspectableInstances())
        {
            if (written >= 3000) break;
            DebugObjectReport report = DebugObjectReport.For(_activeScene, _scriptHost, id, projection);
            if (report == null) continue;
            written++;
            if (report.Drawn) drawn++;
            DiagnosticLine?.Invoke($"debug object #{id} {report.Name}: "
                + (report.Drawn ? (report.LodLevel >= 0 ? $"drawn, level of detail {report.LodLevel}" : "drawn") : "not drawn, " + report.Reason));
        }
        DiagnosticLine?.Invoke($"debug objects: {written} reported, {drawn} drawn, {written - drawn} not drawn");
        return written;
    }

    /// <summary>All colliders drawn in the last frame.</summary>
    public int CollidersDrawnTotal => _colliderOverlay.Drawn;

    private void OnScreenToggled()
    {
        DebugScreenSettings.Refresh();
        if (!_isVisible)
        {
            GuiLayerTrace.Enabled = false;
            GuiLayerTrace.Clear();
        }
    }

    // What the screen draws over the game itself, before its strip and panel: colliders, the GUI
    // layers' outlines, the object picked, the view's legend.
    private void DrawWorldDiagnostics(IHudCanvas hud, int width, int height)
    {
        _lastWidth = width;
        _lastHeight = height;
        // The GUI drawn this frame (before the screen) is now the last frame's.
        GuiLayerTrace.EndFrame();
        if (DebugScreenSettings.CollidersShown) _colliderOverlay.Draw(hud, _activeScene, width, height);
        if (DebugScreenSettings.GuiBoundsShown) DrawGuiOutlines(hud);
        if (DebugScreenSettings.Inspect) DrawInspectedHighlight(hud, width, height);
        if (DebugScreenSettings.ActiveView != DebugViewMode.Off) DrawViewLegend(hud, height);
    }

    // After the strip and panel have taken their clicks: a click on the game picks what is under it.
    private void FinishDiagnostics()
    {
        GuiLayerTrace.Enabled = _isVisible && (DebugScreenSettings.GuiBoundsShown || (ShowExpandedPanels && ActivePanel == DebugHudPanel.Gui));
        if (!DebugScreenSettings.Inspect || !_mouseClicked || _clickConsumed) return;
        if (_stripBounds.Contains(_lastMousePos.X, _lastMousePos.Y)) return;
        if (ShowExpandedPanels && _panelBounds.Contains(_lastMousePos.X, _lastMousePos.Y)) return;
        int picked = PickAt(_lastMousePos);
        if (picked > 0)
        {
            DebugScreenSettings.InspectInstance = picked;
            _reportStamp = 0;
            Log($"[INFO] Inspecting instance #{picked}");
        }
    }

    // ── Picking and stepping ────────────────────────────────────────────────

    /// <summary>The instance under a window point: the frontmost hitbox in 2D, the nearest model or body hit in 3D; 0 for none.</summary>
    public int PickAt(Vector2 screen)
    {
        EcsWorld world = _activeScene?.World;
        if (world == null) return 0;
        DebugProjection projection = DebugProjection.For(_activeScene, _lastWidth, _lastHeight);
        string project = PgslCommands.ProjectPath;
        if (projection.Is2D)
        {
            if (!projection.TryRoomPoint(screen, out Vector2 point)) return 0;
            int best = 0;
            float bestDepth = float.MaxValue;
            foreach (var item in world.Query<TransformComponent, Draw2DComponent>())
            {
                if (!item.C2.Visible || !ObjectDrawAssetRegistry.TryGet(item.Entity, out ObjectDrawAssetEntry assets) || assets.Is3D == true) continue;
                RectangleF box = SpriteCollisionBounds.ForEntity(world, item.Entity, project);
                // Lower depth is drawn in front.
                if (!box.Contains(point.X, point.Y) || item.C2.Depth > bestDepth) continue;
                bestDepth = item.C2.Depth;
                best = item.Entity.Id;
            }
            return best;
        }

        if (!projection.TryRay(screen, out Vector3 origin, out Vector3 direction)) return 0;
        int nearest = 0, insideBest = 0;
        float nearestT = float.MaxValue, insideVolume = float.MaxValue;
        void Consider(int id, Matrix4x4 local, Vector3 min, Vector3 max)
        {
            if (!Matrix4x4.Invert(local, out Matrix4x4 inverse)) return;
            Vector3 o = Vector3.Transform(origin, inverse);
            Vector3 d = Vector3.TransformNormal(direction, inverse);
            if (!RayBox(o, d, min, max, out float enter, out float exit)) return;
            Vector3 size = max - min;
            // The box's space is the world's through an affine matrix, so a point's place along the
            // ray is the same in both: enter is a distance along the world ray.
            if (enter > 0f)
            {
                if (enter < nearestT) { nearestT = enter; nearest = id; }
            }
            else if (exit > 0f)
            {
                float volume = Math.Abs(size.X * size.Y * size.Z);
                if (volume < insideVolume) { insideVolume = volume; insideBest = id; }
            }
        }

        foreach (var item in world.Query<TransformComponent, Draw3DComponent>())
        {
            if (!item.C2.Visible || !world.Has<ModelRendererComponent>(item.Entity)) continue;
            ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(item.Entity);
            if (string.IsNullOrWhiteSpace(model.ModelAsset)) continue;
            if (!ObjectDrawPass.Models.TryGetBounds(project, model.ModelAsset, out Vector3 min, out Vector3 max, includePivot: true)) continue;
            Consider(item.Entity.Id, RuntimeModelRenderSystem.TransformMatrix(item.C1, model), min, max);
        }
        foreach (var item in world.Query<RigidBodyComponent, Transform3DComponent>())
        {
            if (world.Has<ModelRendererComponent>(item.Entity)) continue;
            ref RigidBodyComponent body = ref item.C1;
            Matrix4x4 pose = Matrix4x4.CreateFromQuaternion(item.C2.Rotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(item.C2.Rotation) : Quaternion.Identity)
                * Matrix4x4.CreateTranslation(item.C2.Position);
            Vector3 half = body.HalfExtents;
            Consider(item.Entity.Id, pose, body.LocalOffset - half, body.LocalOffset + half);
        }
        return nearest > 0 ? nearest : insideBest;
    }

    // Slab test of a ray against a box: where it enters and leaves, in the ray's own units.
    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float enter, out float exit)
    {
        enter = float.MinValue;
        exit = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b) (a, b) = (b, a);
            enter = MathF.Max(enter, a);
            exit = MathF.Min(exit, b);
            if (enter > exit) return false;
        }
        return exit >= 0f;
    }

    /// <summary>The instances the object report steps through, by id: whatever is placed and drawn or has a body.</summary>
    public List<int> InspectableInstances()
    {
        var ids = new List<int>();
        EcsWorld world = _activeScene?.World;
        if (world == null) return ids;
        foreach (var item in world.Query<TransformComponent>())
        {
            Entity entity = item.Entity;
            if (world.Has<Draw2DComponent>(entity) || world.Has<Draw3DComponent>(entity) || world.Has<RigidBodyComponent>(entity)) ids.Add(entity.Id);
        }
        foreach (var item in world.Query<Transform3DComponent>())
        {
            Entity entity = item.Entity;
            if (world.Has<TransformComponent>(entity)) continue;
            if (world.Has<RigidBodyComponent>(entity) || world.Has<MeshDrawComponent>(entity) || world.Has<LodMeshComponent>(entity)) ids.Add(entity.Id);
        }
        ids.Sort();
        return ids;
    }

    /// <summary>Moves the object report to the next (1) or previous (-1) instance, and turns the report on.</summary>
    public void StepInspect(int direction)
    {
        DebugScreenSettings.Inspect = true;
        List<int> ids = InspectableInstances();
        if (ids.Count == 0) { DebugScreenSettings.InspectInstance = 0; return; }
        int current = DebugScreenSettings.InspectInstance;
        int index = ids.BinarySearch(current);
        int next;
        if (index >= 0) next = (index + Math.Sign(direction) + ids.Count) % ids.Count;
        else
        {
            int insertAt = ~index;
            next = direction >= 0 ? insertAt % ids.Count : (insertAt - 1 + ids.Count) % ids.Count;
        }
        DebugScreenSettings.InspectInstance = ids[next];
        _reportStamp = 0;
    }

    /// <summary>The object report on the instance being inspected, or null.</summary>
    public DebugObjectReport InspectReport()
    {
        int id = DebugScreenSettings.InspectInstance;
        if (id <= 0 || _activeScene == null) return null;
        long now = Environment.TickCount64;
        // Worked out four times a second: it loads model bounds and walks materials.
        if (_report != null && _report.Instance == id && now - _reportStamp < 250) return _report;
        _report = DebugObjectReport.For(_activeScene, _scriptHost, id, DebugProjection.For(_activeScene, _lastWidth, _lastHeight));
        _reportStamp = now;
        return _report;
    }

    /// <summary>The report as Engine.Debug.Inspect.Report gives it: fresh, one "Label: value" a line.</summary>
    public string InspectReportText()
    {
        _reportStamp = 0;
        return InspectReport()?.ToText() ?? string.Empty;
    }

    /// <summary>The GUI layers of the last frame as text (Engine.Debug.Overlay.Gui.Report).</summary>
    public static string GuiLayersText()
    {
        var text = new StringBuilder();
        foreach (GuiLayerTrace.Layer layer in GuiLayerTrace.Last)
        {
            text.Append(layer.Pass).Append(": ").Append(layer.Owner).Append(" #").Append(layer.Instance.ToString(DiagnosticsCulture))
                .Append(", ").Append(layer.DrawCalls.ToString(DiagnosticsCulture)).Append(" draw calls, ").Append(Area(layer.Bounds));
            foreach (GuiLayerTrace.UiDraw ui in layer.Documents)
                text.Append("; UI ").Append(ui.Asset).Append(" (").Append(ui.Elements.ToString(DiagnosticsCulture)).Append(" elements)");
            text.Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string Area(RectangleF area) => area.IsEmpty ? "nothing drawn"
        : string.Create(DiagnosticsCulture, $"{area.X:0},{area.Y:0} {area.Width:0}x{area.Height:0}");

    // ── Over the frame ──────────────────────────────────────────────────────

    private static void DrawGuiOutlines(IHudCanvas hud)
    {
        IReadOnlyList<GuiLayerTrace.Layer> layers = GuiLayerTrace.Last;
        for (int index = 0; index < layers.Count; index++)
        {
            GuiLayerTrace.Layer layer = layers[index];
            if (layer.Bounds.IsEmpty) continue;
            Vector4 colour = LayerColours[index % LayerColours.Length];
            RectangleF area = layer.Bounds;
            hud.Rect(area.X, area.Y, Math.Max(2f, area.Width), Math.Max(2f, area.Height), colour, filled: false);
            foreach (GuiLayerTrace.UiDraw ui in layer.Documents)
                if (!ui.Bounds.IsEmpty)
                    hud.Rect(ui.Bounds.X + 2f, ui.Bounds.Y + 2f, Math.Max(2f, ui.Bounds.Width - 4f), Math.Max(2f, ui.Bounds.Height - 4f), colour with { W = 0.55f }, filled: false);
            string label = $"{layer.Owner} #{layer.Instance} · {layer.DrawCalls} calls";
            float labelY = Math.Max(0f, area.Y - 14f);
            hud.Rect(area.X, labelY, hud.MeasureText(label, 9f).X + 8f, 14f, DebugOverlayPalette.Canvas, filled: true);
            hud.Text(label, area.X + 4f, labelY + 1f, 9f, colour);
        }
    }

    private void DrawInspectedHighlight(IHudCanvas hud, int width, int height)
    {
        int id = DebugScreenSettings.InspectInstance;
        EcsWorld world = _activeScene?.World;
        if (id <= 0 || world == null) return;
        Entity entity = world.GetEntity(id);
        if (entity.IsNull || !world.IsAlive(entity)) return;
        DebugProjection projection = DebugProjection.For(_activeScene, width, height);
        Vector4 colour = DebugOverlayPalette.Warning;
        string project = PgslCommands.ProjectPath;
        if (projection.Is2D)
        {
            if (!world.Has<TransformComponent>(entity)) return;
            RectangleF box = SpriteCollisionBounds.ForEntity(world, entity, project);
            foreach (RoomRenderSubsystem.View2D view in projection.Views2D)
            {
                Vector2 a = DebugProjection.Map(view, box.Left, box.Top), b = DebugProjection.Map(view, box.Right, box.Bottom);
                hud.Rect(MathF.Min(a.X, b.X) - 2f, MathF.Min(a.Y, b.Y) - 2f, MathF.Abs(b.X - a.X) + 4f, MathF.Abs(b.Y - a.Y) + 4f, colour, filled: false);
            }
            return;
        }
        if (!projection.Has3D) return;
        Matrix4x4 local;
        Vector3 min, max;
        if (world.Has<TransformComponent>(entity) && world.Has<ModelRendererComponent>(entity)
            && world.GetRef<ModelRendererComponent>(entity).ModelAsset is { Length: > 0 } modelName
            && ObjectDrawPass.Models.TryGetBounds(project, modelName, out min, out max, includePivot: true))
            local = RuntimeModelRenderSystem.TransformMatrix(world.GetRef<TransformComponent>(entity), world.GetRef<ModelRendererComponent>(entity));
        else if (world.Has<Transform3DComponent>(entity))
        {
            ref Transform3DComponent transform = ref world.GetRef<Transform3DComponent>(entity);
            Vector3 half = world.Has<RigidBodyComponent>(entity) ? world.GetRef<RigidBodyComponent>(entity).HalfExtents : new Vector3(0.5f);
            Vector3 offset = world.Has<RigidBodyComponent>(entity) ? world.GetRef<RigidBodyComponent>(entity).LocalOffset : Vector3.Zero;
            min = offset - half;
            max = offset + half;
            local = Matrix4x4.CreateFromQuaternion(transform.Rotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(transform.Rotation) : Quaternion.Identity)
                * Matrix4x4.CreateTranslation(transform.Position);
        }
        else if (world.Has<TransformComponent>(entity))
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            min = new Vector3(-0.5f);
            max = new Vector3(0.5f);
            local = Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);
        }
        else return;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int index = 0; index < 8; index++)
            corners[index] = Vector3.Transform(new Vector3((index & 1) == 0 ? min.X : max.X, (index & 2) == 0 ? min.Y : max.Y, (index & 4) == 0 ? min.Z : max.Z), local);
        for (int index = 0; index < 8; index++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((index & bit) == 0 && projection.TrySegment(corners[index], corners[index | bit], out Vector2 a, out Vector2 b))
                    hud.Line(a.X, a.Y, b.X, b.Y, colour, 2f);
    }

    private static void DrawViewLegend(IHudCanvas hud, int height)
    {
        DebugViewMode mode = DebugScreenSettings.ActiveView;
        IReadOnlyList<(Vector4? Colour, string Text)> legend = DebugScreenSettings.Legend(mode);
        float boxW = 300f, boxH = 26f + legend.Count * 15f;
        float x = 10f, y = Math.Max(40f, height - boxH - 12f);
        hud.Rect(x, y, boxW, boxH, DebugOverlayPalette.Canvas, filled: true);
        hud.Rect(x, y, boxW, boxH, DebugOverlayPalette.Border, filled: false);
        hud.Text("VIEW: " + DebugScreenSettings.LabelOf(mode).ToUpperInvariant() + (DebugScreenSettings.IsDrawn(mode) ? "" : " (not drawn yet)"),
            x + 8f, y + 6f, 10f, DebugScreenSettings.IsDrawn(mode) ? DebugOverlayPalette.Accent : DebugOverlayPalette.Warning);
        float lineY = y + 22f;
        foreach ((Vector4? colour, string text) in legend)
        {
            float textX = x + 8f;
            if (colour is { } swatch)
            {
                hud.Rect(textX, lineY + 1f, 10f, 10f, swatch, filled: true);
                textX += 16f;
            }
            hud.Text(text, textX, lineY, 9f, DebugOverlayPalette.Text);
            lineY += 15f;
        }
    }

    // ── Tabs ────────────────────────────────────────────────────────────────

    private bool Toggle(IHudCanvas hud, float x, float y, float w, string label, bool on)
        => DrawButton(hud, x, y, w, 22f, (on ? "✓ " : "□ ") + label, on, DebugOverlayPalette.Accent);

    private void DrawDrawTab(IHudCanvas hud, float x, float y, float w, float h)
    {
        float bottom = y + h;
        hud.Text("COLLIDERS AND HITBOXES", x, y, 10f, DebugOverlayPalette.Accent);
        y += 16f;
        if (Toggle(hud, x, y, 120f, "Colliders", DebugScreenSettings.Colliders))
            DebugScreenSettings.Colliders = !DebugScreenSettings.Colliders;
        float bx = x + 126f;
        (string Label, DebugColliderKind Kind, Func<bool> Get, Action<bool> Set)[] kinds =
        {
            ("Bodies", DebugColliderKind.DynamicBody, () => DebugScreenSettings.ColliderBodies, value => DebugScreenSettings.ColliderBodies = value),
            ("Characters", DebugColliderKind.Character, () => DebugScreenSettings.ColliderCharacters, value => DebugScreenSettings.ColliderCharacters = value),
            ("Triggers", DebugColliderKind.Trigger, () => DebugScreenSettings.ColliderTriggers, value => DebugScreenSettings.ColliderTriggers = value),
            ("Tiles", DebugColliderKind.TileSolid, () => DebugScreenSettings.ColliderTiles, value => DebugScreenSettings.ColliderTiles = value),
            ("Hitboxes", DebugColliderKind.Hitbox, () => DebugScreenSettings.ColliderHitboxes, value => DebugScreenSettings.ColliderHitboxes = value),
        };
        foreach (var kind in kinds)
        {
            float bw = Math.Max(70f, hud.MeasureText(kind.Label, 10f).X + 30f);
            if (bx + bw > x + w) break;
            if (Toggle(hud, bx, y, bw, kind.Label, kind.Get())) kind.Set(!kind.Get());
            bx += bw + 4f;
        }
        y += 28f;
        // The legend, with what was drawn of each kind in the last frame.
        float lx = x;
        foreach (DebugColliderKind kind in Enum.GetValues<DebugColliderKind>())
        {
            string label = $"{DebugColliderOverlay.Label(kind)} {_colliderOverlay.Count(kind)}";
            float lw = hud.MeasureText(label, 9f).X + 22f;
            if (lx + lw > x + w) { lx = x; y += 15f; }
            hud.Rect(lx, y + 2f, 10f, 10f, DebugColliderOverlay.Colour(kind), filled: true);
            hud.Text(label, lx + 14f, y, 9f, DebugOverlayPalette.Text);
            lx += lw + 6f;
        }
        y += 16f;
        if (_colliderOverlay.Skipped > 0)
        {
            hud.Text($"{_colliderOverlay.Skipped} more not drawn (over {DebugColliderOverlay.MaxShapes} a frame)", x, y, 9f, DebugOverlayPalette.Warning);
            y += 15f;
        }
        y += 8f;

        hud.Text("OVERLAYS", x, y, 10f, DebugOverlayPalette.Accent);
        y += 16f;
        if (Toggle(hud, x, y, 150f, "GUI layer outlines", DebugScreenSettings.GuiBounds)) DebugScreenSettings.GuiBounds = !DebugScreenSettings.GuiBounds;
        if (Toggle(hud, x + 156f, y, 110f, "Wireframe", ShowWireframe)) ShowWireframe = !ShowWireframe;
        if (Toggle(hud, x + 272f, y, 130f, "Object report", DebugScreenSettings.Inspect)) DebugScreenSettings.Inspect = !DebugScreenSettings.Inspect;
        if (Toggle(hud, x + 408f, y, 110f, "Overlays", DebugScreenSettings.Overlay)) DebugScreenSettings.Overlay = !DebugScreenSettings.Overlay;
        y += 34f;

        hud.Text("VIEW MODE (the frame drawn to diagnose it)", x, y, 10f, DebugOverlayPalette.Accent);
        y += 16f;
        bx = x;
        if (DrawButton(hud, bx, y, 44f, 22f, "Off", !DebugScreenSettings.View, DebugOverlayPalette.Accent)) DebugScreenSettings.View = false;
        bx += 48f;
        foreach (DebugViewMode mode in DebugScreenSettings.AllModes)
        {
            string label = DebugScreenSettings.LabelOf(mode);
            float bw = hud.MeasureText(label, 10f).X + 14f;
            if (bx + bw > x + w) { bx = x; y += 26f; }
            bool active = DebugScreenSettings.View && DebugScreenSettings.ViewMode == mode;
            if (DrawButton(hud, bx, y, bw, 22f, label, active, DebugScreenSettings.IsDrawn(mode) ? DebugOverlayPalette.Accent : DebugOverlayPalette.Warning))
            {
                DebugScreenSettings.ViewMode = mode;
                Log($"[INFO] Debug view: {label}");
            }
            bx += bw + 4f;
        }
        y += 30f;
        if (DebugScreenSettings.View)
        {
            foreach ((Vector4? colour, string text) in DebugScreenSettings.Legend(DebugScreenSettings.ViewMode))
            {
                if (y > bottom - 14f) break;
                float textX = x;
                if (colour is { } swatch) { hud.Rect(textX, y + 1f, 10f, 10f, swatch, filled: true); textX += 16f; }
                hud.Text(text, textX, y, 9.5f, DebugOverlayPalette.Text);
                y += 15f;
            }
        }
        else if (y < bottom - 14f)
            hud.Text("Orange modes have no shader branch yet and draw the frame as shaded.", x, y, 9.5f, DebugOverlayPalette.Muted);
    }

    private void DrawGuiTab(IHudCanvas hud, float x, float y, float w, float h)
    {
        float bottom = y + h;
        IReadOnlyList<GuiLayerTrace.Layer> layers = GuiLayerTrace.Last;
        int calls = layers.Sum(layer => layer.DrawCalls);
        int documents = layers.Sum(layer => layer.Documents.Count);
        if (Toggle(hud, x, y, 160f, "Outline each layer", DebugScreenSettings.GuiBounds)) DebugScreenSettings.GuiBounds = !DebugScreenSettings.GuiBounds;
        Size target = GuiLayerTrace.Target;
        hud.Text($"{layers.Count} layers, {calls} draw calls, {documents} User Interfaces drawn · GUI pass over the window {target.Width}x{target.Height}"
            + (_activeScene?.Subsystems.OfType<RoomRenderSubsystem>().FirstOrDefault() is { IsTwoD: true } room && room.Views2D.Count > 1 ? $", room drawn in {room.Views2D.Count} views" : ""),
            x + 168f, y + 5f, 9.5f, DebugOverlayPalette.Muted);
        y += 30f;
        float c1 = x + 70f, c2 = x + w * 0.46f, c3 = c2 + 54f;
        hud.Text("PASS", x, y, 9f, DebugOverlayPalette.Accent);
        hud.Text("OWNER", c1, y, 9f, DebugOverlayPalette.Accent);
        hud.Text("CALLS", c2, y, 9f, DebugOverlayPalette.Accent);
        hud.Text("AREA · USER INTERFACES", c3, y, 9f, DebugOverlayPalette.Accent);
        y += 16f;
        if (layers.Count == 0)
        {
            hud.Text("No GUI drawn in the last frame (no Draw GUI event or HUD drew anything).", x, y, 9.5f, DebugOverlayPalette.Muted);
            return;
        }
        for (int index = 0; index < layers.Count && y < bottom - 14f; index++)
        {
            GuiLayerTrace.Layer layer = layers[index];
            Vector4 colour = LayerColours[index % LayerColours.Length];
            hud.Rect(x - 4f, y + 2f, 2f, 10f, colour, filled: true);
            hud.Text(layer.Pass, x, y, 9.5f, DebugOverlayPalette.Muted);
            hud.Text(Truncate($"{layer.Owner} #{layer.Instance}", Math.Max(10, (int)((c2 - c1 - 8f) / 6f))), c1, y, 9.5f, DebugOverlayPalette.Text);
            hud.Text(layer.DrawCalls.ToString(DiagnosticsCulture), c2, y, 9.5f, DebugOverlayPalette.Text);
            hud.Text(Area(layer.Bounds), c3, y, 9.5f, DebugOverlayPalette.Text);
            y += 15f;
            foreach (GuiLayerTrace.UiDraw ui in layer.Documents)
            {
                if (y > bottom - 14f) break;
                hud.Text(Truncate($"UI {ui.Asset}: {ui.Elements} elements at {Area(ui.Bounds)} (laid out for {ui.Target.Width:0}x{ui.Target.Height:0})", 90),
                    c1 + 12f, y, 9f, DebugOverlayPalette.Muted);
                y += 14f;
            }
        }
    }

    private void DrawObjectTab(IHudCanvas hud, float x, float y, float w, float h)
    {
        float bottom = y + h;
        if (Toggle(hud, x, y, 150f, "Pick with the mouse", DebugScreenSettings.Inspect)) DebugScreenSettings.Inspect = !DebugScreenSettings.Inspect;
        if (DrawButton(hud, x + 156f, y, 80f, 22f, "◀ Previous", false)) StepInspect(-1);
        if (DrawButton(hud, x + 240f, y, 64f, 22f, "Next ▶", false)) StepInspect(1);
        if (DrawButton(hud, x + 308f, y, 56f, 22f, "Clear", false)) DebugScreenSettings.InspectInstance = 0;
        y += 30f;
        DebugObjectReport report = InspectReport();
        if (report == null)
        {
            hud.Text("No instance picked. Turn on Pick with the mouse and click one in the game, or step with Next.", x, y, 9.5f, DebugOverlayPalette.Muted);
            return;
        }
        hud.Rect(x - 4f, y - 2f, w + 8f, 22f, report.Drawn ? DebugOverlayPalette.Raised : new Vector4(0.35f, 0.12f, 0.14f, 0.9f), filled: true);
        hud.Text(report.Drawn ? $"{report.Name} #{report.Instance}: drawn" : $"{report.Name} #{report.Instance}: NOT DRAWN, {report.Reason}",
            x, y + 3f, 10.5f, report.Drawn ? DebugOverlayPalette.Success : DebugOverlayPalette.Error);
        y += 28f;
        int valueChars = Math.Max(20, (int)((w - 120f) / 5.8f));
        foreach ((string label, string value) in report.Lines)
        {
            if (y > bottom - 14f) break;
            hud.Text(label, x, y, 9.5f, DebugOverlayPalette.Muted);
            hud.Text(Truncate(value, valueChars), x + 112f, y, 9.5f, DebugOverlayPalette.Text);
            y += 15f;
        }
    }
}

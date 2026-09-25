using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private enum RiverHandle { Point, Width, Depth }

    private readonly List<WaterSplinePoint> _riverDraft = [];
    private readonly CheckBox _riverCarveCheck = new() { AutoSize = true, Checked = true, Text = "Carve Riverbed" };
    private readonly NumericUpDown _riverWidthBox = RiverNumber(.1m, 10000m, 4m);
    private readonly NumericUpDown _riverDepthBox = RiverNumber(.05m, 1000m, 1m);
    private readonly NumericUpDown _riverDropBox = RiverNumber(.1m, 1000m, 1.5m);
    private bool _riverControlsReady;
    private bool _syncingRiverControls;
    private int _riverSelectedPoint = -1;
    private int _riverDraggingPoint = -1;
    private RiverHandle _riverDraggingHandle;
    private Point _riverDragStartClient;
    private float _riverDragStartValue;
    private string? _editingRiverId;

    private bool IsWaterGestureActive => _waterDragging || _waterSpraying || _riverDraggingPoint >= 0;

    private static NumericUpDown RiverNumber(decimal minimum, decimal maximum, decimal value) => new()
    {
        DecimalPlaces = 2,
        Increment = .1m,
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Width = 244,
    };

    private void AddRiverAuthoringControls(FlowLayoutPanel page)
    {
        if (!_riverControlsReady)
        {
            _riverControlsReady = true;
            foreach (NumericUpDown field in new[] { _riverWidthBox, _riverDepthBox, _riverDropBox })
                EditorChrome.StyleField(field);
            _riverWidthBox.ValueChanged += (_, _) => UpdateSelectedRiverPoint(width: (float)_riverWidthBox.Value, depth: null);
            _riverDepthBox.ValueChanged += (_, _) => UpdateSelectedRiverPoint(width: null, depth: (float)_riverDepthBox.Value);
            _riverDropBox.ValueChanged += (_, _) => _viewport.Invalidate();
        }

        page.Controls.Add(MakeContextCaption("Spline River"));
        page.Controls.Add(MakeRiverField("Width at selected point", _riverWidthBox));
        page.Controls.Add(MakeRiverField("Channel depth", _riverDepthBox));
        page.Controls.Add(MakeRiverField("Waterfall drop threshold", _riverDropBox));
        page.Controls.Add(_riverCarveCheck);
        page.Controls.Add(MakeContextAction(
            "Finish River",
            "Create the flowing river, carve its optional channel, and generate cliff cascades",
            CommitRiverDraft));
        page.Controls.Add(MakeContextAction(
            "Edit Selected Spline",
            "Load the selected river's control points for direct point, width, and depth editing",
            BeginEditSelectedRiver));
        page.Controls.Add(MakeContextAction(
            "Remove Point",
            "Remove the selected spline control point",
            RemoveSelectedRiverPoint));
        page.Controls.Add(MakeContextAction(
            "Cancel River",
            "Discard the in-progress spline without changing the terrain",
            ClearRiverDraft));
    }

    private static Panel MakeRiverField(string caption, NumericUpDown field)
    {
        Panel panel = new() { BackColor = Color.Transparent, Height = 54, Margin = new Padding(0, 0, 0, 4), Width = 244 };
        panel.Controls.Add(field);
        field.Dock = DockStyle.Bottom;
        panel.Controls.Add(new Label
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 20,
            Text = caption,
        });
        return panel;
    }

    private void RiverPointerDown(Point client, Vector3 world)
    {
        if (TryHitRiverHandle(client, out int pointIndex, out RiverHandle handle))
        {
            _riverSelectedPoint = pointIndex;
            _riverDraggingPoint = pointIndex;
            _riverDraggingHandle = handle;
            _riverDragStartClient = client;
            _riverDragStartValue = handle == RiverHandle.Width
                ? _riverDraft[pointIndex].Width
                : _riverDraft[pointIndex].Depth;
            _viewport.NavigationEnabled = false;
            SyncRiverFields();
            return;
        }

        WaterSplinePoint point = new()
        {
            Position = world with { Y = _terrain.SampleHeight(world.X, world.Z) + .045f },
            Width = (float)_riverWidthBox.Value,
            Depth = (float)_riverDepthBox.Value,
        };
        _riverDraft.Add(point);
        _riverSelectedPoint = _riverDraft.Count - 1;
        SyncRiverFields();
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void RiverPointerMove(Point client, Vector3? world)
    {
        if (_riverDraggingPoint < 0 || _riverDraggingPoint >= _riverDraft.Count) return;
        WaterSplinePoint point = _riverDraft[_riverDraggingPoint];
        switch (_riverDraggingHandle)
        {
            case RiverHandle.Point when world is { } position:
                point.Position = position with { Y = _terrain.SampleHeight(position.X, position.Z) + .045f };
                break;
            case RiverHandle.Width when world is { } position:
                point.Width = Math.Clamp(Vector2.Distance(
                    new Vector2(point.Position.X, point.Position.Z),
                    new Vector2(position.X, position.Z)) * 2f, .1f, 10000f);
                break;
            case RiverHandle.Depth:
                point.Depth = Math.Clamp(_riverDragStartValue + (client.Y - _riverDragStartClient.Y) * .035f, .05f, 1000f);
                break;
        }
        SyncRiverFields();
    }

    private void EndRiverPointerDrag()
    {
        _riverDraggingPoint = -1;
        _viewport.NavigationEnabled = true;
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void UpdateSelectedRiverPoint(float? width, float? depth)
    {
        if (_syncingRiverControls || _riverSelectedPoint < 0 || _riverSelectedPoint >= _riverDraft.Count) return;
        WaterSplinePoint point = _riverDraft[_riverSelectedPoint];
        if (width is { } nextWidth) point.Width = nextWidth;
        if (depth is { } nextDepth) point.Depth = nextDepth;
        _viewport.Invalidate();
    }

    private void SyncRiverFields()
    {
        if (_riverSelectedPoint < 0 || _riverSelectedPoint >= _riverDraft.Count) return;
        _syncingRiverControls = true;
        _riverWidthBox.Value = Math.Clamp((decimal)_riverDraft[_riverSelectedPoint].Width, _riverWidthBox.Minimum, _riverWidthBox.Maximum);
        _riverDepthBox.Value = Math.Clamp((decimal)_riverDraft[_riverSelectedPoint].Depth, _riverDepthBox.Minimum, _riverDepthBox.Maximum);
        _syncingRiverControls = false;
    }

    private void RemoveSelectedRiverPoint()
    {
        if (_riverSelectedPoint < 0 || _riverSelectedPoint >= _riverDraft.Count) return;
        _riverDraft.RemoveAt(_riverSelectedPoint);
        _riverSelectedPoint = Math.Min(_riverSelectedPoint, _riverDraft.Count - 1);
        SyncRiverFields();
        _viewport.Invalidate();
    }

    private void ClearRiverDraft()
    {
        _riverDraft.Clear();
        _riverSelectedPoint = -1;
        _riverDraggingPoint = -1;
        _editingRiverId = null;
        _viewport.NavigationEnabled = true;
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void BeginEditSelectedRiver()
    {
        TerrainWaterDefinition? selected = _nature.WaterBodies.FirstOrDefault(body =>
            body.Kind == TerrainWaterKind.River
            && string.Equals(body.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            _statusLabel.Text = "Select a river in the viewport or Water library before editing its spline.";
            return;
        }

        _riverDraft.Clear();
        _riverDraft.AddRange(selected.RiverPoints.Select(point => point.Clone()));
        _editingRiverId = selected.Id;
        _riverSelectedPoint = _riverDraft.Count > 0 ? 0 : -1;
        _riverCarveCheck.Checked = selected.CarveRiverbed;
        _riverDropBox.Value = Math.Clamp((decimal)selected.WaterfallDropThreshold, _riverDropBox.Minimum, _riverDropBox.Maximum);
        _waterTool = WaterAuthoringTool.River;
        _pendingWaterKind = TerrainWaterKind.River;
        _waterKindCombo.SelectedItem = nameof(TerrainWaterKind.River);
        SyncRiverFields();
        SyncToolbar();
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void CommitRiverDraft()
    {
        if (_riverDraft.Count < 2)
        {
            _statusLabel.Text = "A river needs at least two spline points.";
            return;
        }

        TerrainWaterDefinition river = TerrainRiverSystem.CreateRiver(
            _terrain,
            _riverDraft,
            _riverCarveCheck.Checked,
            (float)_riverDropBox.Value);
        TerrainWaterDefinition? existing = _editingRiverId is null
            ? null
            : _nature.WaterBodies.FirstOrDefault(body => string.Equals(body.Id, _editingRiverId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            river.Name = $"River {_nature.WaterBodies.Count(body => body.Kind == TerrainWaterKind.River) + 1}";
            CommitAuthoredWater(river, river.CarveRiverbed);
        }
        else
        {
            river.Id = existing.Id;
            river.Name = existing.Name;
            ReplaceAuthoredRiver(river, river.CarveRiverbed);
        }
        ClearRiverDraft();
    }

    private void ReplaceAuthoredRiver(TerrainWaterDefinition river, bool carve)
    {
        ushort[] heightsBefore = (ushort[])_terrain.HeightsData.Clone();
        byte[] splatBefore = (byte[])_terrain.SplatmapData.Clone();
        List<TerrainWaterDefinition> waterBefore = Clone(_nature.WaterBodies);
        FoliageField foliageBefore = _foliage;
        Dictionary<string, HeldFoliageInstance[]> holdBefore = CloneHold(_foliageHeldByWater);
        if (carve) TerrainRiverSystem.CarveRiverbed(_terrain, river.RiverPoints);
        List<TerrainWaterDefinition> waterAfter = Clone(waterBefore);
        int index = waterAfter.FindIndex(body => string.Equals(body.Id, river.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        waterAfter[index] = river.Clone();
        ushort[] heightsAfter = (ushort[])_terrain.HeightsData.Clone();
        byte[] splatAfter = (byte[])_terrain.SplatmapData.Clone();
        Dictionary<string, HeldFoliageInstance[]> holdAfter = CloneHold(_foliageHeldByWater);
        FoliageField foliageAfter = FoliageScatter.ReconcileWithWater(foliageBefore, waterBefore, waterAfter, holdAfter);

        void Apply(bool after)
        {
            _terrain.RestoreState(after ? heightsAfter : heightsBefore, after ? splatAfter : splatBefore);
            _nature.WaterBodies = Clone(after ? waterAfter : waterBefore);
            _foliage = after ? foliageAfter : foliageBefore;
            _foliageHeldByWater = CloneHold(after ? holdAfter : holdBefore);
            _meshDirty = true;
            _natureMeshDirty = true;
            ResetFoliagePreview();
            RefreshComponentsPanel();
            UpdateStatus();
            _viewport.Invalidate(true);
        }

        Apply(true);
        PushEdit($"Edit river '{river.Name}'", () => Apply(true), () => Apply(false));
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Water, river.Id);
    }

    private void FillConformingBasin(Vector3 world)
    {
        float level = _waterLevelSet ? _waterLevel : world.Y + MathF.Max(.05f, _terrain.CellSize * .1f);
        bool[] mask = TerrainRiverSystem.TraceBasin(_terrain, world.X, world.Z, level, MathF.Max(.04f, _terrain.CellSize * .08f));
        int width = _terrain.ResolutionX;
        bool reachesBoundary = false;
        int count = 0;
        for (int z = 0; z < _terrain.ResolutionZ; z++)
        for (int x = 0; x < width; x++)
        {
            if (!mask[z * width + x]) continue;
            count++;
            reachesBoundary |= x == 0 || z == 0 || x == width - 1 || z == _terrain.ResolutionZ - 1;
        }
        if (count == 0 || reachesBoundary)
        {
            _statusLabel.Text = reachesBoundary
                ? "The contour is open at this elevation. Lower the Water Level or choose a closed basin."
                : "No enclosed basin was found at the clicked elevation.";
            return;
        }

        _waterMask = mask;
        _waterRegionActive = false;
        _pendingWaterKind = TerrainWaterKind.Water;
        _waterKindCombo.SelectedItem = nameof(TerrainWaterKind.Water);
        SetWaterLevel(level);
        FillWaterSelection(_carveBasinCheck.Checked);
    }

    private bool TryHitRiverHandle(Point client, out int pointIndex, out RiverHandle handle)
    {
        int bestIndex = -1;
        RiverHandle bestHandle = RiverHandle.Point;
        float best = 13f * 13f;
        for (int i = 0; i < _riverDraft.Count; i++)
        {
            Vector3 center = _viewport.WorldToSurface(_riverDraft[i].Position + Vector3.UnitY * .12f);
            Vector3 width = _viewport.WorldToSurface(_riverDraft[i].Position + RiverSide(i) * (_riverDraft[i].Width * .5f) + Vector3.UnitY * .12f);
            PointF depth = new(center.X, center.Y + 24f + MathF.Min(80f, _riverDraft[i].Depth * 6f));
            Check(width.X, width.Y, i, RiverHandle.Width);
            Check(depth.X, depth.Y, i, RiverHandle.Depth);
            Check(center.X, center.Y, i, RiverHandle.Point);
        }
        pointIndex = bestIndex;
        handle = bestHandle;
        return bestIndex >= 0;

        void Check(float x, float y, int index, RiverHandle candidate)
        {
            float dx = client.X - x, dy = client.Y - y;
            float distance = dx * dx + dy * dy;
            if (distance >= best) return;
            best = distance;
            bestIndex = index;
            bestHandle = candidate;
        }
    }

    private Vector3 RiverSide(int index)
    {
        Vector3 before = _riverDraft[Math.Max(0, index - 1)].Position;
        Vector3 after = _riverDraft[Math.Min(_riverDraft.Count - 1, index + 1)].Position;
        Vector3 tangent = after - before;
        tangent.Y = 0f;
        if (tangent.LengthSquared() < 1e-8f) tangent = Vector3.UnitZ;
        tangent = Vector3.Normalize(tangent);
        return Vector3.Normalize(Vector3.Cross(Vector3.UnitY, tangent));
    }

    private void DrawRiverPreview(IRenderController renderer)
    {
        IReadOnlyList<WaterSplinePoint>? points = _riverDraft.Count > 0
            ? _riverDraft
            : _nature.WaterBodies.FirstOrDefault(body => body.Kind == TerrainWaterKind.River
                && string.Equals(body.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase))?.RiverPoints;
        if (points is null || points.Count == 0) return;

        RenderColor flow = new(.1f, .78f, 1f, .95f);
        RenderColor bank = new(.35f, .85f, 1f, .72f);
        Vector3? previous = null;
        IReadOnlyList<WaterSplinePoint> centerline = points.Count >= 2
            ? TerrainRiverSystem.SampleSpline(points, 12)
            : points;
        foreach (WaterSplinePoint sample in centerline)
        {
            Vector3 screen = _viewport.WorldToSurface(sample.Position + Vector3.UnitY * .12f);
            if (previous is { } prior && prior.Z is > 0f and < 1f && screen.Z is > 0f and < 1f)
                renderer.DrawLine(prior.X, prior.Y, screen.X, screen.Y, flow, 3f, depth: -9000);
            previous = screen;
        }

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 screen = _viewport.WorldToSurface(points[i].Position + Vector3.UnitY * .12f);
            Vector3 side = PreviewRiverSide(points, i);
            Vector3 left = _viewport.WorldToSurface(points[i].Position - side * (points[i].Width * .5f) + Vector3.UnitY * .1f);
            Vector3 right = _viewport.WorldToSurface(points[i].Position + side * (points[i].Width * .5f) + Vector3.UnitY * .1f);
            if (left.Z is > 0f and < 1f && right.Z is > 0f and < 1f)
                renderer.DrawLine(left.X, left.Y, right.X, right.Y, bank, 1.4f, depth: -8999);
            DrawHandle(renderer, screen.X, screen.Y, i == _riverSelectedPoint ? new RenderColor(1f, .82f, .2f, 1f) : flow, 5f);

            if (_riverDraft.Count == 0) continue;
            Vector3 width = _viewport.WorldToSurface(points[i].Position + side * (points[i].Width * .5f) + Vector3.UnitY * .12f);
            DrawHandle(renderer, width.X, width.Y, new RenderColor(1f, .65f, .18f, 1f), 4f);
            float depthY = screen.Y + 24f + MathF.Min(80f, points[i].Depth * 6f);
            renderer.DrawLine(screen.X, screen.Y, screen.X, depthY, new RenderColor(.78f, .42f, 1f, .8f), 1.3f, depth: -8998);
            DrawHandle(renderer, screen.X, depthY, new RenderColor(.78f, .42f, 1f, 1f), 4f);
        }

        if (_riverDraft.Count < 2) return;
        TerrainWaterDefinition preview = TerrainRiverSystem.CreateRiver(_terrain, _riverDraft, false, (float)_riverDropBox.Value);
        foreach (WaterfallParams cascade in preview.Cascades)
        {
            Vector3 top = _viewport.WorldToSurface((cascade.TopLeft + cascade.TopRight) * .5f);
            Vector3 bottom = _viewport.WorldToSurface(cascade.ImpactPosition);
            if (top.Z is > 0f and < 1f && bottom.Z is > 0f and < 1f)
                renderer.DrawLine(top.X, top.Y, bottom.X, bottom.Y, new RenderColor(.92f, .97f, 1f, .95f), 4f, depth: -9001);
            DrawHandle(renderer, bottom.X, bottom.Y, new RenderColor(1f, .45f, .2f, 1f), 7f);
        }
    }

    private static Vector3 PreviewRiverSide(IReadOnlyList<WaterSplinePoint> points, int index)
    {
        Vector3 tangent = points[Math.Min(points.Count - 1, index + 1)].Position - points[Math.Max(0, index - 1)].Position;
        tangent.Y = 0f;
        if (tangent.LengthSquared() < 1e-8f) tangent = Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Vector3.Normalize(tangent)));
    }

    private static void DrawHandle(IRenderController renderer, float x, float y, RenderColor color, float size)
    {
        renderer.DrawLine(x - size, y, x + size, y, color, 2f, depth: -9002);
        renderer.DrawLine(x, y - size, x, y + size, color, 2f, depth: -9002);
    }
}

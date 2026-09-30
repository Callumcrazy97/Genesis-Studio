using System.Numerics;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private Label? _activeToolName;
    private readonly List<ComboBox> _brushFalloffPickers = [];
    private bool _selectionDrawing;
    private readonly List<Vector2> _paintSelection = [];
    public string BrushFalloff { get; set; } = "Smooth";

    private void InstallActiveToolCard()
    {
        _activeToolName = new Label { Dock = DockStyle.Top, Font = EditorChrome.SmallFont, ForeColor = EditorChrome.Muted,
            Text = "Select: Terrain", Padding = new Padding(8, 6, 8, 6), BackColor = ReferenceSurface };
        _toolPanel.Controls.Add(_activeToolName);
        _activeToolName.SendToBack();
        foreach (string mode in new[] { nameof(TerrainEditorMode.Sculpt), nameof(TerrainEditorMode.Paint) })
        {
            FlowLayoutPanel page = _modeHost.Controls.OfType<FlowLayoutPanel>().Single(panel => panel.Name == "TerrainMode" + mode);
            ComboBox falloff = new UiKit.ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Brush falloff" };
            falloff.Items.AddRange(["Smooth falloff", "Linear falloff", "Hard edge"]); falloff.SelectedIndex = 0;
            falloff.SelectedIndexChanged += (_, _) =>
            {
                BrushFalloff = new[] { "Smooth", "Linear", "Hard" }[falloff.SelectedIndex];
                foreach (ComboBox other in _brushFalloffPickers) other.SelectedIndex = falloff.SelectedIndex;
            };
            EditorChrome.StyleField(falloff);
            _brushFalloffPickers.Add(falloff);
            page.Controls.Add(falloff);
        }
        Disposed += (_, _) => { _createMenu?.Dispose(); _terrainGhost.InvalidateAssets(_viewport.Host.Renderer); };
    }

    private void RefreshActiveToolCard()
    {
        if (_activeToolName is null) return;
        string asset = SelectedPlacedEntity() is { } placed ? TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity))?.Name ?? "Entity" : "Terrain";
        string tool = _pendingTerrain is { } terrain ? "Place: " + terrain.Recipe.Name : _placementEntityPath is { } path ? "Place: " + (TryLoadEntityDocument(path)?.Name ?? ResourceDisplayName.Format(path))
            : _drawingSection is { } drawing ? (_selectionDrawing ? "Select: " : "Create: ") + drawing
            : ActiveMode == TerrainEditorMode.Sculpt ? "Sculpt: " + ActiveBrush
            : ActiveMode == TerrainEditorMode.Paint ? "Paint: " + _settings.Layers[Math.Clamp(SelectedLayer, 0, _settings.Layers.Count - 1)].Name
            : ActiveMode == TerrainEditorMode.Foliage && !_foliageBrushArmed ? "Foliage: Choose an asset"
            : ModeCaption(ActiveMode) + ": " + asset;
        _activeToolName.Text = tool;
        ApplyInterfaceLayout();
    }
    private void ActivateSelectTool()
    {
        _placementEntityPath = null; SetMode(TerrainEditorMode.Select); RefreshActiveToolCard();
    }

    private void ArmSelectedEntity()
    {
        string? reference = SelectedPlacedEntity()?.Entity;
        if (reference is null && _selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity) reference = _selectedComponentId;
        if (string.IsNullOrWhiteSpace(reference)) { _statusLabel.Text = "Select an entity in Available Assets, then click Place."; return; }
        SetMode(TerrainEditorMode.Select); _placementEntityPath = ResolveEntityFullPath(reference); RefreshActiveToolCard();
        _statusLabel.Text = "Click to place · Ctrl keeps placing · Esc cancels";
    }

    public void BeginPaintSelection(TerrainCreationSource source)
    {
        BeginSectionDrawing(source); _selectionDrawing = true; RefreshActiveToolCard();
    }

    public void SetPaintSelection(IEnumerable<Vector2> points) { _paintSelection.Clear(); _paintSelection.AddRange(points); _viewport.Invalidate(); }

    private static bool InsidePolygon(IReadOnlyList<Vector2> points, float x, float z)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i]; var b = points[j];
            if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    public void FillPaintSelection()
    {
        int channel = Math.Clamp(SelectedLayer, 0, 3);
        BeginStroke(0, 0);
        for (int z = 0; z < _terrain.ResolutionZ; z++)
        for (int x = 0; x < _terrain.ResolutionX; x++)
        {
            if (_paintSelection.Count >= 3 && !InsidePolygon(_paintSelection, _terrain.OriginX + x * _terrain.CellSize, _terrain.OriginZ + z * _terrain.CellSize)) continue;
            int at = (z * _terrain.ResolutionX + x) * 4;
            for (int c = 0; c < 4; c++) _terrain.SplatmapData[at + c] = (byte)(c == channel ? 255 : 0);
        }
        InvalidatePaint(); EndStroke(); SetMode(TerrainEditorMode.Paint); _viewport.Invalidate();
    }

    private float FalloffAt(float distance, float radius)
    {
        float t = Math.Clamp(1 - distance / Math.Max(.001f, radius), 0, 1);
        return BrushFalloff == "Hard" ? 1 : BrushFalloff == "Linear" ? t : t * t * (3 - 2 * t);
    }

    private void ApplyConfiguredBrush(float worldX, float worldZ, TerrainBrush brush)
    {
        float radius = BrushRadius, strength = BrushStrength;
        var bounds = BrushCellBounds(worldX, worldZ, radius);
        // Smooth reads a snapshot of the brush footprint plus a one-sample border, not a copy of
        // the entire heightfield per dab.
        int windowMinX = Math.Max(0, bounds.MinX - 1), windowMinZ = Math.Max(0, bounds.MinZ - 1);
        int windowMaxX = Math.Min(_terrain.ResolutionX - 1, bounds.MaxX + 1), windowMaxZ = Math.Min(_terrain.ResolutionZ - 1, bounds.MaxZ + 1);
        int windowWidth = Math.Max(0, windowMaxX - windowMinX + 1);
        float[]? smooth = brush == TerrainBrush.Smooth ? new float[windowWidth * Math.Max(0, windowMaxZ - windowMinZ + 1)] : null;
        if (smooth is not null) for (int z = windowMinZ; z <= windowMaxZ; z++) for (int x = windowMinX; x <= windowMaxX; x++) smooth[(z - windowMinZ) * windowWidth + (x - windowMinX)] = _terrain.GetHeight(x, z);
        float rim = 0;
        if (brush == TerrainBrush.InFill) for (int i = 0; i < 16; i++) rim += _terrain.SampleHeight(worldX + MathF.Cos(i * MathF.Tau / 16) * radius, worldZ + MathF.Sin(i * MathF.Tau / 16) * radius) / 16;
        for (int z = bounds.MinZ; z <= bounds.MaxZ; z++) for (int x = bounds.MinX; x <= bounds.MaxX; x++)
        {
            float wx = _terrain.OriginX + x * _terrain.CellSize, wz = _terrain.OriginZ + z * _terrain.CellSize;
            float distance = Vector2.Distance(new(wx, wz), new(worldX, worldZ));
            if (distance > radius || (_paintSelection.Count >= 3 && !InsidePolygon(_paintSelection, wx, wz))) continue;
            float blend = Math.Clamp(strength * FalloffAt(distance, radius), 0, 1);
            int at = z * _terrain.ResolutionX + x;
            if (brush == TerrainBrush.Paint)
            {
                int channel = Math.Clamp(SelectedLayer, 0, 3), sum = 0;
                for (int c = 0; c < 4; c++) { int value = (int)MathF.Round(float.Lerp(_terrain.SplatmapData[at * 4 + c], c == channel ? 255 : 0, blend)); _terrain.SplatmapData[at * 4 + c] = (byte)value; sum += value; }
                _terrain.SplatmapData[at * 4 + channel] = (byte)Math.Clamp(_terrain.SplatmapData[at * 4 + channel] + 255 - sum, 0, 255);
                continue;
            }
            float current = _terrain.GetHeight(x, z), target = current;
            if (brush == TerrainBrush.Raise) target += 1.6f;
            if (brush == TerrainBrush.Lower) target -= 1.6f;
            if (brush == TerrainBrush.Flatten) target = _flattenTarget;
            if (brush == TerrainBrush.InFill) target = Math.Max(current, rim);
            if (brush == TerrainBrush.Erase && _eraseBaseline.Length == _terrain.HeightsData.Length) target = _terrain.MinHeight + _eraseBaseline[at] / (float)ushort.MaxValue * (_terrain.MaxHeight - _terrain.MinHeight);
            if (smooth is not null)
            {
                float sum = 0; int count = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) { int sx = Math.Clamp(x + dx, windowMinX, windowMaxX), sz = Math.Clamp(z + dz, windowMinZ, windowMaxZ); sum += smooth[(sz - windowMinZ) * windowWidth + (sx - windowMinX)]; count++; }
                target = sum / count;
            }
            _terrain.SetHeight(x, z, float.Lerp(current, target, blend));
        }
        if (brush == TerrainBrush.Paint)
            InvalidatePaint(Rectangle.FromLTRB(bounds.MinX, bounds.MinZ, bounds.MaxX + 1, bounds.MaxZ + 1));
        else QueueSculptRegion(bounds.MinX, bounds.MinZ, bounds.MaxX, bounds.MaxZ);
    }
}

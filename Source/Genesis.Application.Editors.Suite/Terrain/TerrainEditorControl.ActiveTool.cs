using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.World.Terrain;

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

    private static bool InsidePolygon(IReadOnlyList<Vector2> points, float x, float z) => TerrainSculpt.InsidePolygon(points, x, z);

    public void FillPaintSelection()
    {
        int channel = Math.Clamp(SelectedLayer, 0, TerrainAsset.MaximumPaintLayers - 1);
        if (channel >= 4) _terrain.EnsureExtendedSplatmap();
        BeginStroke(0, 0);
        for (int z = 0; z < _terrain.ResolutionZ; z++)
        for (int x = 0; x < _terrain.ResolutionX; x++)
        {
            if (_paintSelection.Count >= 3 && !InsidePolygon(_paintSelection, _terrain.OriginX + x * _terrain.CellSize, _terrain.OriginZ + z * _terrain.CellSize)) continue;
            _terrain.SetLayerAt(z * _terrain.ResolutionX + x, channel);
        }
        InvalidatePaint(); EndStroke(); SetMode(TerrainEditorMode.Paint); _viewport.Invalidate();
    }

    private void ApplyConfiguredBrush(float worldX, float worldZ, TerrainBrush brush)
    {
        // The shared brush (TerrainSculpt), so the automation commands sculpt exactly as the mouse does.
        var bounds = TerrainSculpt.Apply(_terrain, Enum.Parse<TerrainSculptBrush>(brush.ToString()), worldX, worldZ, new TerrainSculptDab
        {
            Radius = BrushRadius,
            Strength = BrushStrength,
            Falloff = BrushFalloff,
            Layer = SelectedLayer,
            FlattenTarget = _flattenTarget,
            Selection = _paintSelection,
            EraseBaseline = _eraseBaseline,
        });
        if (brush == TerrainBrush.Paint)
            InvalidatePaint(Rectangle.FromLTRB(bounds.MinX, bounds.MinZ, bounds.MaxX + 1, bounds.MaxZ + 1));
        else QueueSculptRegion(bounds.MinX, bounds.MinZ, bounds.MaxX, bounds.MaxZ);
    }
}

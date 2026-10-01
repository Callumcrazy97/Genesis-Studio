using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private TerrainCreationResult? _pendingTerrain;
    private bool _pendingTerrainBase;
    private Vector3? _pendingTerrainPosition;
    private readonly RuntimeModelRenderSystem _terrainGhost = new(
        assetFreshnessIntervalMilliseconds: 500,
        textureFreshnessIntervalMilliseconds: 1000);

    /// <summary>The last world generated from a code recipe, for status text and tests.</summary>
    public TerrainWorldResult? LastGeneratedWorld { get; private set; }

    public void ApplyHeightfield(TerrainCreationResult result, Vector3 position)
    {
        // The wizard previews code at no more than 384 cells a side. Creating the terrain runs the
        // recipe at the spacing it asked for, with its whole-terrain steps (sea, rivers, paint...).
        if (TerrainWorldGenerator.Handles(result.Recipe))
        {
            Cursor previous = Cursor.Current ?? Cursors.Default;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                var progress = new Progress<string>(message => _statusLabel.Text = message);
                ApplyWorld(TerrainWorldGenerator.Generate(result.Recipe, CancellationToken.None, progress, position));
            }
            finally { Cursor.Current = previous; }
            return;
        }

        TerrainAsset before = _terrain, after = TerrainHeightfieldBridge.ToHeightfield(result, position);
        var beforeLayers = CloneLayers(_settings.Layers);
        void Swap(TerrainAsset asset)
        {
            _terrain = asset; _eraseBaseline = (ushort[])asset.HeightsData.Clone();
            _settings.Resolution = [asset.ResolutionX, asset.ResolutionZ]; _settings.CellSize = asset.CellSize;
            _settings.MinHeight = asset.MinHeight; _settings.MaxHeight = asset.MaxHeight;
            _paintSelection.Clear(); _waterMask = null;
            ReleaseTerrainMeshes(); _meshDirty = true; _nextMaterialCheck = DateTime.MinValue;
            UpdateSectionCameraRange(); RebuildSelectionInspector(); _viewport.Invalidate();
        }
        Swap(after); MarkDirty();
        PushEdit("Apply heightmap terrain", () => Swap(after), () => { _settings.Layers = CloneLayers(beforeLayers); Swap(before); });
    }

    /// <summary>
    /// Replaces the terrain with a generated world: heights and paint, layers, the sea and rivers,
    /// scatter layers, placed objects and settlement landmarks. One undo step restores what was there.
    /// </summary>
    public void ApplyWorld(TerrainWorldResult world)
    {
        ArgumentNullException.ThrowIfNull(world);
        TerrainAsset before = _terrain, after = world.Terrain;
        var beforeLayers = CloneLayers(_settings.Layers);
        var afterLayers = WorldLayers(world.Plan, beforeLayers);
        var beforeNature = (Water: TerrainNatureSerializer.Clone(_nature.WaterBodies), Scatter: TerrainNatureSerializer.Clone(_nature.ScatterLayers),
            Placed: ClonePlaced(_nature.PlacedEntities), Points: ClonePoints(_nature.PointsOfInterest));
        var afterNature = (Water: TerrainNatureSerializer.Clone(world.Water), Scatter: TerrainNatureSerializer.Clone(world.Scatter),
            Placed: ClonePlaced(world.Placed), Points: ClonePoints(world.PointsOfInterest));
        void Swap(TerrainAsset asset, List<TerrainLayerDocument> layers,
            (List<TerrainWaterDefinition> Water, List<TerrainScatterLayer> Scatter, List<TerrainPlacedEntity> Placed, List<TerrainPointOfInterest> Points) nature)
        {
            _terrain = asset; _eraseBaseline = (ushort[])asset.HeightsData.Clone();
            _settings.Resolution = [asset.ResolutionX, asset.ResolutionZ]; _settings.CellSize = asset.CellSize;
            _settings.MinHeight = asset.MinHeight; _settings.MaxHeight = asset.MaxHeight;
            _settings.Layers = CloneLayers(layers);
            _nature.WaterBodies = TerrainNatureSerializer.Clone(nature.Water);
            _nature.ScatterLayers = TerrainNatureSerializer.Clone(nature.Scatter);
            _nature.PlacedEntities = ClonePlaced(nature.Placed);
            _nature.PointsOfInterest = ClonePoints(nature.Points);
            _paintSelection.Clear(); _waterMask = null;
            ReleaseTerrainMeshes(); _meshDirty = true; _nextMaterialCheck = DateTime.MinValue;
            RebuildLayerList(); RefreshComponentsPanel(); _entityListPanel.RefreshEntities();
            UpdateSectionCameraRange(); RebuildSelectionInspector(); _viewport.Invalidate();
        }

        Swap(after, afterLayers, afterNature); MarkDirty();
        LastGeneratedWorld = world;
        _statusLabel.Text = "Created world · " + world.Summary;
        PushEdit("Create world from code", () => Swap(after, afterLayers, afterNature), () => Swap(before, beforeLayers, beforeNature));
    }

    /// <summary>Layers named by the recipe; a recipe that paints without naming any gets the four natural ones.</summary>
    private static List<TerrainLayerDocument> WorldLayers(TerrainWorldPlan plan, List<TerrainLayerDocument> existing)
    {
        if (plan.Paint is null && plan.Layers.All(layer => layer is null)) return existing;
        (string Name, float[] Colour)[] natural =
        [
            ("Grass", [0.33f, 0.47f, 0.22f]), ("Rock", [0.43f, 0.42f, 0.40f]),
            ("Sand", [0.76f, 0.69f, 0.50f]), ("Snow", [0.93f, 0.95f, 0.97f]),
        ];
        var layers = new List<TerrainLayerDocument>();
        for (int slot = 0; slot < 4; slot++)
        {
            TerrainWorldLayer? named = plan.Layers[slot];
            layers.Add(new TerrainLayerDocument
            {
                Name = string.IsNullOrWhiteSpace(named?.Name) ? natural[slot].Name : named!.Name,
                Color = natural[slot].Colour,
                Image = named?.Image ?? "",
                // "Tile" addressing repeats the image every Tiling metres, whatever the terrain's size.
                Addressing = "Tile",
                Tiling = named?.TileMetres ?? 6f,
            });
        }

        return layers;
    }

    private bool TerrainPlacementPoint(Point point, out Vector3 position)
    {
        if (PickTerrain(point, out position)) return true;
        var ray = _viewport.PickRay(point);
        if (Math.Abs(ray.Direction.Y) < .00001f) return false;
        float distance = -ray.Origin.Y / ray.Direction.Y;
        position = ray.Origin + ray.Direction * distance;
        return distance > 0 && distance < 100000;
    }

    public void ArmTerrainPlacement(TerrainCreationResult result, bool replaceBase)
    {
        _pendingTerrain = result; _pendingTerrainBase = replaceBase; _pendingTerrainPosition = null;
        _placementEntityPath = null; SetMode(TerrainEditorMode.Select);
        _statusLabel.Text = "Place: " + result.Recipe.Name + " · click terrain or ground · Escape cancels";
        _viewport.Host.Focus(); RefreshActiveToolCard();
    }

    private bool PlacePendingTerrain(Point point)
    {
        if (_pendingTerrain is not { } result) return false;
        if (!TerrainPlacementPoint(point, out var position)) return true;
        if (_pendingTerrainBase) ApplyHeightfield(result, position); else AddTerrainSection(result, position);
        _pendingTerrain = null; _pendingTerrainPosition = null; FrameSection(result, position); RefreshActiveToolCard();
        return true;
    }

    private void DrawTerrainPlacement(IRenderController renderer)
    {
        if (_pendingTerrain is { } result && _pendingTerrainPosition is { } position)
            _terrainGhost.DrawAsset(result.Model, ProjectRoot, Matrix4x4.CreateTranslation(position), default, renderer);
    }
}

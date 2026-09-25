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
    private readonly RuntimeModelRenderSystem _terrainGhost = new();

    public void ApplyHeightfield(TerrainCreationResult result, Vector3 position)
    {
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

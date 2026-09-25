using System.Numerics;
using System.Windows.Forms;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    public enum PathAuthoringTool
    {
        PaintPath,
    }

    private readonly Dictionary<PathAuthoringTool, Button> _pathToolButtons = [];
    private PathAuthoringTool _pathTool = PathAuthoringTool.PaintPath;
    private bool _pathStroking;
    private readonly List<Vector3> _pathStrokePoints = [];

    public PathAuthoringTool ActivePathTool => _pathTool;

    private void HandlePathPointerDown(Point client)
    {
        if (_pathTool != PathAuthoringTool.PaintPath) return;
        if (!PickTerrain(client, out Vector3 world)) return;

        _cursorWorld = world;
        _cursorValid = true;
        _pathStroking = true;
        _pathStrokePoints.Clear();
        _pathStrokePoints.Add(new Vector3(world.X, world.Y, world.Z));
        _viewport.NavigationEnabled = false;
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void HandlePathPointerMove(Point client, MouseButtons button)
    {
        _cursorValid = PickTerrain(client, out _cursorWorld);
        if (!_pathStroking || button != MouseButtons.Left || !_cursorValid) return;

        Vector3 last = _pathStrokePoints[^1];
        float spacing = MathF.Max(_terrain.CellSize * 0.75f, 1f);
        float dx = _cursorWorld.X - last.X;
        float dz = _cursorWorld.Z - last.Z;
        if (dx * dx + dz * dz >= spacing * spacing)
        {
            _pathStrokePoints.Add(new Vector3(_cursorWorld.X, _cursorWorld.Y, _cursorWorld.Z));
            _viewport.Invalidate();
        }

        UpdateStatus();
    }

    private void HandlePathPointerUp()
    {
        if (!_pathStroking) return;
        _pathStroking = false;
        _viewport.NavigationEnabled = true;
        CommitPaintedPath();
        UpdateStatus();
        _viewport.Invalidate(true);
    }

    private void CommitPaintedPath()
    {
        if (_pathStrokePoints.Count < 2)
        {
            _pathStrokePoints.Clear();
            return;
        }

        TerrainPathSettings settings = _nature.PathSettings;
        List<TerrainPathDefinition> oldPaths = Clone(_nature.Paths);
        ushort[] oldHeights = (ushort[])_terrain.HeightsData.Clone();
        byte[] oldSplat = (byte[])_terrain.SplatmapData.Clone();

        List<Vector3> points = [];
        foreach (Vector3 sample in _pathStrokePoints)
        {
            float y = _terrain.SampleHeight(sample.X, sample.Z) + 0.03f;
            points.Add(new Vector3(sample.X, y, sample.Z));
        }

        TerrainPathDefinition path = new()
        {
            Name = $"Trail {oldPaths.Count + 1}",
            Kind = settings.Kind,
            Width = settings.Width,
            Points = points,
        };

        List<TerrainPathDefinition> nextPaths = Clone(oldPaths);
        nextPaths.Add(path);

        TerrainPathNetwork network = new TerrainPathNetwork(nextPaths);
        network.ApplyTo(_terrain, settings.GradeStrength, settings.SplatChannel);

        ushort[] nextHeights = (ushort[])_terrain.HeightsData.Clone();
        byte[] nextSplat = (byte[])_terrain.SplatmapData.Clone();

        void ApplyPaths(List<TerrainPathDefinition> paths, ushort[] heights, byte[] splat)
        {
            _terrain.RestoreState(heights, splat);
            _nature.Paths = Clone(paths);
            _pathNetwork = new TerrainPathNetwork(_nature.Paths);
            _meshDirty = true;
            _natureMeshDirty = true;
            RefreshComponentsPanel();
            UpdateStatus();
            _viewport.Invalidate(true);
        }

        _nature.Paths = Clone(nextPaths);
        _pathNetwork = network;
        _meshDirty = true;
        _natureMeshDirty = true;
        MarkDirty();
        PushEdit(
            $"Paint path '{path.Name}'",
            () => ApplyPaths(nextPaths, nextHeights, nextSplat),
            () => ApplyPaths(oldPaths, oldHeights, oldSplat));
        RefreshComponentsPanel();
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Path, path.Id);
        _pathStrokePoints.Clear();
    }

    private void DrawPathPaintPreview(IRenderController renderer)
    {
        if (_pathStrokePoints.Count == 0 && ActiveMode != TerrainEditorMode.Paths) return;

        RenderColor color = new(0.83f, 0.69f, 0.46f, 0.95f);
        IReadOnlyList<Vector3> samples = _pathStrokePoints.Count > 0
            ? _pathStrokePoints
            : _cursorValid ? [new Vector3(_cursorWorld.X, _cursorWorld.Y, _cursorWorld.Z)] : Array.Empty<Vector3>();

        Vector3 previous = default;
        bool hasPrevious = false;
        foreach (Vector3 sample in samples)
        {
            float y = _terrain.SampleHeight(sample.X, sample.Z) + 0.18f;
            Vector3 surface = _viewport.WorldToSurface(new Vector3(sample.X, y, sample.Z));
            if (hasPrevious && previous.Z is >= 0f and <= 1f && surface.Z is >= 0f and <= 1f)
            {
                renderer.DrawLine(previous.X, previous.Y, surface.X, surface.Y, color, 2.5f, depth: -8996);
            }

            previous = surface;
            hasPrevious = true;
        }

        if (_cursorValid && ActiveMode == TerrainEditorMode.Paths)
        {
            float halfWidth = MathF.Max(0.5f, _nature.PathSettings.Width * 0.5f);
            const int segments = 32;
            Vector3 centre = _cursorWorld;
            Vector3 prevRing = default;
            bool hasRing = false;
            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * MathF.Tau;
                float wx = centre.X + MathF.Cos(angle) * halfWidth;
                float wz = centre.Z + MathF.Sin(angle) * halfWidth;
                Vector3 ring = _viewport.WorldToSurface(new Vector3(wx, _terrain.SampleHeight(wx, wz) + 0.12f, wz));
                if (hasRing && prevRing.Z is >= 0f and <= 1f && ring.Z is >= 0f and <= 1f)
                {
                    renderer.DrawLine(prevRing.X, prevRing.Y, ring.X, ring.Y, new RenderColor(color.R, color.G, color.B, 0.35f), 1f, depth: -8997);
                }

                prevRing = ring;
                hasRing = true;
            }
        }
    }
}

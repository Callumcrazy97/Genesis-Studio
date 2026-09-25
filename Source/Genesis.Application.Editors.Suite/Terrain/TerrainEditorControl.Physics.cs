using System.Numerics;
using System.Windows.Forms;
using Genesis.Physics;
using Genesis.Runtime.Project;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private TerrainPhysicsPreview? _physicsPreview;
    private System.Windows.Forms.Timer? _physicsTimer;
    private bool _physicsPlaying;
    private readonly List<(Vector3 A, Vector3 B)> _overlayEdgeScratch = [];

    public bool ShowColliderOverlay { get; set; }

    public bool ShowWaterVolumeOverlay { get; set; }

    public int ColliderRebuildGeneration => _physicsPreview?.RebuildGeneration ?? 0;

    public int ColliderOverlayEdgeCount => TerrainColliderMesh.CountOverlayEdges(_terrain);

    public int WaterVolumeOverlayEdgeCount
    {
        get
        {
            int count = 0;
            foreach (TerrainWaterDefinition water in _nature.WaterBodies)
            {
                _ = water;
                count += 16;
            }

            return count;
        }
    }

    public bool PlayableIsActive => _physicsPreview?.PlayableIsActive == true;

    public Vector3 PlayablePosition => _physicsPreview?.PlayablePosition ?? Vector3.Zero;

    public float PlayableSubmergedFraction => _physicsPreview?.PlayableSubmergedFraction ?? 0f;

    public CharacterMotorState PlayableMotorState =>
        _physicsPreview?.PlayableMotorState ?? CharacterMotorState.Falling;

    public bool PlayableEnteredWater => _physicsPreview?.PlayableEnteredWater == true;

    public void RebuildPhysicsPreview()
    {
        EnsurePhysicsPreview().Rebuild(_terrain, _nature.WaterBodies);
        _viewport.Invalidate();
        UpdateStatus();
    }

    public bool TryRaycastCollider(float worldX, float worldZ, out float height)
    {
        EnsurePhysicsPreview();
        float ceiling = MathF.Max(_terrain.MaxHeight, _terrain.SampleHeight(worldX, worldZ)) + 80f;
        return _physicsPreview!.TryRaycastDown(worldX, worldZ, ceiling, out height);
    }

    public void DropPlayableIntoWater(string? waterId = null)
    {
        EnsurePhysicsPreview();
        string? id = waterId;
        if (string.IsNullOrWhiteSpace(id)
            && _selectedComponentKind is TerrainComponentsPanel.ComponentKind.Water
            && !string.IsNullOrWhiteSpace(_selectedComponentId))
        {
            id = _selectedComponentId;
        }

        _physicsPreview!.DropIntoWater(_terrain, _nature.WaterBodies, id);
        _physicsPlaying = true;
        StartPhysicsTimer();
        SetMode(TerrainEditorMode.Water);
        _viewport.Invalidate();
        UpdateStatus();
    }

    public void StepPhysicsPreview(float dt, int steps = 1)
    {
        EnsurePhysicsPreview();
        if (!_physicsPreview!.PlayableIsActive)
        {
            _physicsPreview.DropIntoWater(_terrain, _nature.WaterBodies, _selectedComponentId);
        }

        _physicsPreview.Step(dt, steps);
        _viewport.Invalidate();
        UpdateStatus();
    }

    public void ResetPlayablePreview()
    {
        _physicsPlaying = false;
        _physicsPreview?.ResetPlayable();
        _viewport.Invalidate();
        UpdateStatus();
    }

    protected override void OnJournalChanged()
    {
        base.OnJournalChanged();
        if (_physicsPreview != null)
        {
            _physicsPreview.Rebuild(_terrain, _nature.WaterBodies);
        }
    }

    private TerrainPhysicsPreview EnsurePhysicsPreview()
    {
        _physicsPreview ??= new TerrainPhysicsPreview();
        if (_physicsPreview.RebuildGeneration == 0)
        {
            _physicsPreview.Rebuild(_terrain, _nature.WaterBodies);
        }

        return _physicsPreview;
    }

    private void StartPhysicsTimer()
    {
        if (_physicsTimer != null)
        {
            return;
        }

        _physicsTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _physicsTimer.Tick += (_, _) =>
        {
            if (!_physicsPlaying || _physicsPreview is not { PlayableIsActive: true })
            {
                return;
            }

            _physicsPreview.Step(1f / 60f);
            _viewport.Invalidate();
            UpdateStatus();
        };
        _physicsTimer.Start();
    }

    private void DrawPhysicsOverlays(IRenderController renderer)
    {
        if (ShowColliderOverlay)
        {
            _overlayEdgeScratch.Clear();
            TerrainColliderMesh.AppendOverlayEdges(_terrain, _overlayEdgeScratch);
            RenderColor colour = new(0.28f, 0.92f, 0.42f, 0.85f);
            foreach ((Vector3 a, Vector3 b) in _overlayEdgeScratch)
            {
                DrawWorldOverlayLine(renderer, a, b, colour, 1.25f, -9050);
            }
        }

        if (ShowWaterVolumeOverlay)
        {
            RenderColor volume = new(0.18f, 0.72f, 1f, 0.95f);
            RenderColor surface = new(0.55f, 0.92f, 1f, 1f);
            foreach (TerrainWaterDefinition water in _nature.WaterBodies)
            {
                if (!ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind.Water, water.Id))
                {
                    continue;
                }

                PhysicsWaterVolume bounds = TerrainColliderMesh.CreateVolume(water, Matrix4x4.Identity);
                _overlayEdgeScratch.Clear();
                TerrainColliderMesh.AppendVolumeEdges(bounds, _overlayEdgeScratch);
                for (int i = 0; i < _overlayEdgeScratch.Count; i++)
                {
                    (Vector3 a, Vector3 b) = _overlayEdgeScratch[i];
                    bool isSurface = i >= _overlayEdgeScratch.Count - 4;
                    DrawWorldOverlayLine(renderer, a, b, isSurface ? surface : volume, isSurface ? 2f : 1.5f, -9040);
                }
            }
        }

        if (_physicsPreview is { PlayableIsActive: true })
        {
            Vector3 position = _physicsPreview.PlayablePosition;
            float radius = 0.35f;
            float half = 0.9f;
            RenderColor capsule = _physicsPreview.PlayableMotorState switch
            {
                CharacterMotorState.Swimming => new RenderColor(0.2f, 0.82f, 1f, 1f),
                CharacterMotorState.Grounded => new RenderColor(0.95f, 0.82f, 0.2f, 1f),
                _ => new RenderColor(0.95f, 0.35f, 0.78f, 1f),
            };
            DrawWorldOverlayLine(renderer, position + Vector3.UnitY * half, position - Vector3.UnitY * half, capsule, 2.5f, -9030);
            DrawWorldOverlayLine(renderer, position - Vector3.UnitX * radius, position + Vector3.UnitX * radius, capsule, 2f, -9030);
            DrawWorldOverlayLine(renderer, position - Vector3.UnitZ * radius, position + Vector3.UnitZ * radius, capsule, 2f, -9030);
        }
    }

    private void DrawWorldOverlayLine(
        IRenderController renderer,
        Vector3 a,
        Vector3 b,
        RenderColor colour,
        float width,
        int depth)
    {
        Vector3 sa = _viewport.WorldToSurface(a);
        Vector3 sb = _viewport.WorldToSurface(b);
        if (sa.Z is < 0f or > 1f || sb.Z is < 0f or > 1f)
        {
            return;
        }

        renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, colour, width, depth);
    }

    private void DisposePhysicsPreview()
    {
        _physicsPlaying = false;
        _physicsTimer?.Stop();
        _physicsTimer?.Dispose();
        _physicsTimer = null;
        _physicsPreview?.Dispose();
        _physicsPreview = null;
    }
}

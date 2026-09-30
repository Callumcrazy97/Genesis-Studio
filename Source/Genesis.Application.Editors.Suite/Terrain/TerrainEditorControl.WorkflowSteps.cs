using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private WorkflowBar? _terrainWorkflow;

    /// <summary>The guided steps: Shape › Sculpt › Paint › Decorate › Use in game.</summary>
    public WorkflowBar? TerrainWorkflow => _terrainWorkflow;

    private void BuildTerrainWorkflowBar(EditorCommandBar toolbar)
    {
        _terrainWorkflow = EditorWorkflow.AttachBelow(toolbar, "TerrainWorkflow",
        [
            new("Shape", "Shape", "Start from a landform, a heightmap or an outline you draw.",
                () => SetMode(TerrainEditorMode.Generate)),
            new("Sculpt", "Sculpt", "Drag on the terrain to raise, lower, smooth or flatten the ground.",
                () => SetMode(TerrainEditorMode.Sculpt)),
            new("Paint", "Paint", "Choose a layer, then drag to paint grass, rock or sand onto the ground.",
                () => SetMode(TerrainEditorMode.Paint)),
            new("Decorate", "Decorate", "Scatter trees and plants. Paths, water and objects are in Options › More terrain tools.",
                () => SetMode(TerrainEditorMode.Foliage)),
            new("UseInGame", "Use in game", "Save, then create a 3D Room that uses this terrain.",
                ShowTerrainGameGuide),
        ]);
        SyncTerrainWorkflowStep();
        RefreshTerrainWorkflowHint();
    }

    /// <summary>Keeps the bar on the step that owns the active mode (the rail can change it too).</summary>
    private void SyncTerrainWorkflowStep()
    {
        if (_terrainWorkflow is null)
        {
            return;
        }

        string? step = _showTerrainGuide ? "UseInGame" : ActiveMode switch
        {
            TerrainEditorMode.Generate => "Shape",
            TerrainEditorMode.Sculpt => "Sculpt",
            TerrainEditorMode.Paint => "Paint",
            TerrainEditorMode.Paths or TerrainEditorMode.Foliage or TerrainEditorMode.Water
                or TerrainEditorMode.Environment or TerrainEditorMode.Entities => "Decorate",
            _ => null,
        };
        if (step is not null)
        {
            _terrainWorkflow.SetCurrent(step);
        }
    }
}

using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private WorkflowBar? _roomWorkflow;
    private EditorCommandBar? _roomCommandBar;
    private bool _roomWorkflowThreeD;

    /// <summary>
    /// The guided steps. 3D: Ground › Place › Sky › Camera › Use in game; 2D: Tiles › Place ›
    /// Background › Camera › Use in game. The step ids are shared, so the current step survives a
    /// switch between 2D and 3D.
    /// </summary>
    public WorkflowBar? RoomWorkflow => _roomWorkflow;

    private void BuildRoomWorkflowBar(EditorCommandBar toolbar)
    {
        _roomCommandBar = toolbar;
        RebuildRoomWorkflowBar();
    }

    private void RebuildRoomWorkflowBar()
    {
        if (_roomCommandBar is null) return;
        string? current = _roomWorkflow?.CurrentStepId;
        _roomWorkflow?.Dispose();
        _roomWorkflowThreeD = ViewMode3D;
        _roomWorkflow = EditorWorkflow.AttachBelow(_roomCommandBar, "RoomWorkflow", ViewMode3D
            ?
            [
                new("Ground", "Ground", "Place a Terrain for the ground, or skip this step for an indoor Room.",
                    () => OpenRoomSection(RoomNavSection.Tilesets)),
                new("Place", "Place", "Choose an Object on the left, then click in the Room to place it.",
                    () => OpenRoomSection(RoomNavSection.Objects)),
                new("Sky", "Sky", "Set the sky, time of day and weather.",
                    () => OpenRoomSection(RoomNavSection.Backgrounds)),
                new("Camera", "Camera", "Choose what the player sees: turn on a view and pick what it follows.",
                    () => OpenRoomSection(RoomNavSection.Views)),
                new("UseInGame", "Use in game", "Save, make this the starting Room, then press Run to play it.",
                    ShowRoomGameGuide),
            ]
            :
            [
                new("Ground", "Tiles", "Choose a tile set on the left, pick a tile, then drag in the Room to paint.",
                    () => OpenRoomSection(RoomNavSection.Tilesets)),
                new("Place", "Place", "Choose an Object on the left, then click in the Room to place it.",
                    () => OpenRoomSection(RoomNavSection.Objects)),
                new("Sky", "Background", "Add a saved Image behind the Room and choose how it scrolls.",
                    () => OpenRoomSection(RoomNavSection.Backgrounds)),
                new("Camera", "Camera", "Choose what the player sees: turn on a view and pick what it follows.",
                    () => OpenRoomSection(RoomNavSection.Views)),
                new("UseInGame", "Use in game", "Save, make this the starting Room, then press Run to play it.",
                    ShowRoomGameGuide),
            ]);
        if (current is not null) _roomWorkflow.SetCurrent(current);
    }

    private void OpenRoomSection(RoomNavSection section)
    {
        ShowRoomAuthoring();
        _navigation.SetSection(section);
    }

    /// <summary>Follows the rail and the 2D/3D switch; shows the section's own tip as the instruction.</summary>
    private void SyncRoomWorkflowStep(string sectionTip)
    {
        if (_roomWorkflow is null) return;
        if (_roomWorkflowThreeD != ViewMode3D) RebuildRoomWorkflowBar();
        if (_roomWorkflow is null) return;
        if (_showRoomGuide)
        {
            _roomWorkflow.SetCurrent("UseInGame");
            return;
        }

        string? step = _navigation.CurrentSection switch
        {
            RoomNavSection.Tilesets => "Ground",
            RoomNavSection.Objects or RoomNavSection.Instances => "Place",
            RoomNavSection.Backgrounds => "Sky",
            RoomNavSection.Views => "Camera",
            _ => null,
        };
        if (step is not null) _roomWorkflow.SetCurrent(step);

        // The step's own sentence reads best for its main section; Instances and Settings keep
        // their more specific tips.
        bool mainSection = _navigation.CurrentSection is RoomNavSection.Tilesets or RoomNavSection.Objects
            or RoomNavSection.Backgrounds or RoomNavSection.Views;
        _roomWorkflow.SetInstruction(mainSection ? null : sectionTip);
    }
}

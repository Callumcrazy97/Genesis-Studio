using Genesis.Application.Core.Projects;
using Genesis.Application.Core.UI;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    // The presets themselves are RoomScenePresets in Core, shared with automation's room.create and
    // room.setEnvironment, so a card and a command give the same room.
    private StarterGallery? _scenePresets;

    /// <summary>The Sky page's scene preset cards.</summary>
    public StarterGallery? ScenePresetGallery => _scenePresets;

    private void AddScenePresets(System.Windows.Forms.Panel skybox)
    {
        _scenePresets = new StarterGallery("RoomScenePresets")
        {
            Compact = true,
            Dock = System.Windows.Forms.DockStyle.Top,
            FitsContent = true,
            Subheading = "One click sets the sky, time of day and weather. Fine-tune them below.",
        };
        _scenePresets.SetItems(RoomScenePresets.All.Select(preset => new StarterItem(preset.Id, preset.Title, preset.Description)
        {
            Glyph = preset.Glyph,
            Swatch = UiTokens.FromHex(preset.Swatch),
        }));
        _scenePresets.ItemChosen += (_, item) => ApplyScenePreset(item.Id);
        skybox.Controls.Add(_scenePresets);

        // Top-docked siblings stack in reverse z-order: sending the gallery to the back docks it
        // first, above the Sky and time, Weather and Clouds groups.
        _scenePresets.SendToBack();
    }

    /// <summary>
    /// The toolbar's 2D/3D switch. A room with no sky yet that becomes 3D opens on a sunny day (as the
    /// Terrain editor's room and automation's room.create do) rather than on a dark void, in the same
    /// undoable edit; the Sky cards change it, and Indoor takes the sky away again.
    /// </summary>
    public void SwitchDimensionFromToolbar(bool threeD)
    {
        if (!threeD || ViewMode3D || _room.Environment.DynamicSky)
        {
            ViewMode3D = threeD;
            return;
        }
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        RoomEnvironment next = CloneEnvironment(_room.Environment);
        RoomScenePresets.Find(RoomScenePresets.DefaultOutdoor)!.Apply(next);
        void ToThreeD() { ApplyRoomDimension(RoomDimension.ThreeD); ApplyEnvironment(next); }
        ToThreeD();
        PushEdit("Switch room to 3D", ToThreeD, () => { ApplyEnvironment(previous); ApplyRoomDimension(RoomDimension.TwoD); });
    }

    /// <summary>Applies a scene preset as one undoable edit. Returns false for an unknown id.</summary>
    public bool ApplyScenePreset(string id)
    {
        RoomScenePreset? preset = RoomScenePresets.All.FirstOrDefault(candidate => candidate.Id == id);
        if (preset is null) return false;
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        RoomEnvironment next = CloneEnvironment(_room.Environment);
        preset.Apply(next);
        ApplyEnvironment(next);
        PushEdit("Scene: " + preset.Title, () => ApplyEnvironment(next), () => ApplyEnvironment(previous));
        if (_scenePresets is not null) _scenePresets.SelectedId = id;
        return true;
    }
}

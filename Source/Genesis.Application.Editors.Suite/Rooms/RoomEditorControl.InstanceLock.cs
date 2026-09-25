using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    /// <summary>Locks an individual instance independently of its layer. Unlocking never unlocks a layer.</summary>
    public bool SetNodeLocked(RoomNode node, bool locked)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_room.Nodes.Contains(node) || !CanInspectNodeInActiveContext(node) || node.Locked == locked) return false;
        bool before = node.Locked;
        void Apply(bool value)
        {
            node.Locked = value;
            RefreshPhase4Ui();
            NotifyRoomInspectorStateChanged();
        }
        Apply(locked);
        PushEdit(locked ? $"Lock '{node.Name}'" : $"Unlock '{node.Name}'", () => Apply(locked), () => Apply(before));
        return true;
    }
}

using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    public bool RenameRoomLayer(RoomLayer layer, string name)
    {
        string after = name.Trim();
        if (!_room.Layers.Contains(layer) || after.Length == 0 || layer.Name == after) return false;
        string before = layer.Name;
        void Apply(string value) { layer.Name = value; RefreshPhase4Ui(); }
        Apply(after);
        PushEdit("Rename room layer", () => Apply(after), () => Apply(before));
        return true;
    }

    /// <summary>Remove an empty layer or move its instances to the adjacent unlocked layer.</summary>
    public bool RemoveRoomLayer(RoomLayer layer)
    {
        if (!_room.Layers.Contains(layer) || layer.Locked || _room.Layers.Count < 2) return false;
        RoomLayer? fallback = _room.Layers.FirstOrDefault(candidate => candidate != layer && !candidate.Locked);
        if (fallback is null) return false;
        int index = _room.Layers.IndexOf(layer);
        RoomNode[] moved = _room.Nodes.Where(node => node.LayerId == layer.Id).ToArray();
        void Apply()
        {
            foreach (RoomNode node in moved) node.LayerId = fallback.Id;
            _room.Layers.Remove(layer);
            RefreshPhase4Ui();
        }
        void Revert()
        {
            if (!_room.Layers.Contains(layer)) _room.Layers.Insert(Math.Min(index, _room.Layers.Count), layer);
            foreach (RoomNode node in moved) node.LayerId = layer.Id;
            RefreshPhase4Ui();
        }
        Apply();
        PushEdit("Remove room layer", Apply, Revert);
        return true;
    }
}

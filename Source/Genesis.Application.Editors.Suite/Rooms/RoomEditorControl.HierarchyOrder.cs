using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    /// <summary>List order is hierarchy order; Order remains the authored 2D depth.</summary>
    public bool MoveHierarchyInstance(RoomNode node, RoomNode? parent, RoomLayer layer,
        RoomNode? neighbour = null, bool after = false)
    {
        if (node.Kind != RoomNodeKind.GameObject || !CanEditNodeInActiveContext(node)
            || !ReferenceEquals(layer, _navigation.ObjectsPanel.ActiveObjectLayer)
            || parent is not null && (parent.Kind != RoomNodeKind.GameObject || !CanEditNodeInActiveContext(parent))
            || neighbour is not null && !CanEditNodeInActiveContext(neighbour)
            || !_room.Nodes.Contains(node) || !_room.Layers.Contains(layer) || layer.Locked || IsNodeLocked(node)
            || parent is not null && (!_room.Nodes.Contains(parent) || IsNodeLocked(parent) || WouldCycle(node, parent))
            || neighbour is not null && (!_room.Nodes.Contains(neighbour) || neighbour == node || IsNodeLocked(neighbour))) return false;
        string parentId = parent?.Id ?? string.Empty;
        if (neighbour is not null && (neighbour.ParentId != parentId || neighbour.LayerId != layer.Id)) return false;
        RoomTransform localBefore = CloneTransform(node.Transform);
        RoomTransform localAfter = localBefore;
        if (node.ParentId != parentId && !RoomHierarchyTransforms.TryReparent(GetNodeWorldTransform(node),
                parent is null ? new RoomTransform() : GetNodeWorldTransform(parent), out localAfter))
        {
            UpdateStatus("Cannot preserve this pose under that parent's scale.");
            return false;
        }
        string previousParent = node.ParentId, previousLayer = node.LayerId;
        RoomNode[] beforeOrder = _room.Nodes.ToArray();
        var nextOrder = beforeOrder.ToList();
        nextOrder.Remove(node);
        int insertion = neighbour is null ? nextOrder.Count : nextOrder.IndexOf(neighbour) + (after ? 1 : 0);
        nextOrder.Insert(insertion, node);
        RoomNode[] afterOrder = nextOrder.ToArray();
        if (previousParent == parentId && previousLayer == layer.Id && beforeOrder.SequenceEqual(afterOrder)) return false;
        void Apply(RoomNode[] order, string nextParent, string nextLayer, RoomTransform transform)
        {
            _room.Nodes.Clear(); _room.Nodes.AddRange(order);
            node.ParentId = nextParent; node.LayerId = nextLayer;
            CopyTransform(transform, node.Transform); RefreshPhase4Ui();
        }
        Apply(afterOrder, parentId, layer.Id, localAfter);
        PushEdit($"Move '{node.Name}' in hierarchy",
            () => Apply(afterOrder, parentId, layer.Id, localAfter),
            () => Apply(beforeOrder, previousParent, previousLayer, localBefore));
        return true;
    }
}

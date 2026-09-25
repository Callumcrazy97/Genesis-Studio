using System.Numerics;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private readonly Dictionary<string, RoomTransform> _dragStartWorldTransforms = new(StringComparer.OrdinalIgnoreCase);

    public RoomTransform GetNodeWorldTransform(RoomNode node) => RoomHierarchyTransforms.World(_room, node);
    public Matrix4x4 GetNodeWorldMatrix(RoomNode node) => RoomHierarchyTransforms.Matrix(GetNodeWorldTransform(node));

    private bool SetNodeWorldTransform(RoomNode node, RoomTransform world)
    {
        if (RoomHierarchyTransforms.Parent(_room, node) is null)
        { CopyTransform(world, node.Transform); return true; }
        if (!RoomHierarchyTransforms.TryLocal(world, RoomHierarchyTransforms.ParentWorld(_room, node), out RoomTransform local))
        { UpdateStatus("This transform cannot be represented under the parent's current rotation and scale."); return false; }
        CopyTransform(local, node.Transform);
        return true;
    }

    private IReadOnlyList<RoomNode> SelectionTransformRoots()
    {
        IReadOnlyList<RoomNode> editable = EditableSelection();
        HashSet<string> ids = editable.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return editable.Where(node => !RoomHierarchyTransforms.HasAncestor(_room, node, ids)).ToArray();
    }

    private void ApplyHierarchyGroupDelta(RoomTransform before, RoomTransform after,
        IReadOnlyDictionary<string, RoomTransform> startWorld)
    {
        Vector3 pivotBefore = new(before.X, before.Y, before.Z), pivotAfter = new(after.X, after.Y, after.Z);
        Quaternion rotation = Quaternion.Normalize(Quaternion.Concatenate(
            Quaternion.Inverse(RoomHierarchyTransforms.Rotation(before)), RoomHierarchyTransforms.Rotation(after)));
        Vector3 scale = new(SafeRatio(after.ScaleX, before.ScaleX), SafeRatio(after.ScaleY, before.ScaleY), SafeRatio(after.ScaleZ, before.ScaleZ));
        foreach (RoomNode node in SelectionTransformRoots())
        {
            if (!startWorld.TryGetValue(node.Id, out RoomTransform? start)) continue;
            Vector3 offset = new Vector3(start.X, start.Y, start.Z) - pivotBefore;
            RoomTransform target = RoomHierarchyTransforms.Create(pivotAfter + Vector3.Transform(offset * scale, rotation),
                Quaternion.Concatenate(RoomHierarchyTransforms.Rotation(start), rotation),
                new Vector3(start.ScaleX, start.ScaleY, start.ScaleZ) * scale);
            SetNodeWorldTransform(node, target);
        }
    }

    private static (Vector3 Min, Vector3 Max) TransformBounds(Vector3 min, Vector3 max, Matrix4x4 world)
    {
        Vector3 resultMin = new(float.MaxValue), resultMax = new(float.MinValue);
        for (int mask = 0; mask < 8; mask++)
        {
            Vector3 corner = Vector3.Transform(new Vector3((mask & 1) == 0 ? min.X : max.X,
                (mask & 2) == 0 ? min.Y : max.Y, (mask & 4) == 0 ? min.Z : max.Z), world);
            resultMin = Vector3.Min(resultMin, corner); resultMax = Vector3.Max(resultMax, corner);
        }
        return (resultMin, resultMax);
    }
}

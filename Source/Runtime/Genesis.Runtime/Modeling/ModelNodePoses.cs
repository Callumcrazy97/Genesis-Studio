using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Modeling;

/// <summary>A turn and an offset added to one model node on one instance (a propeller, a gun's slide).</summary>
public struct ModelNodePose
{
    public Quaternion Rotation;
    public Vector3 Translation;

    public static ModelNodePose Identity => new() { Rotation = Quaternion.Identity };
}

/// <summary>
/// Moves a model's non-skinned nodes per instance. A node's meshes are baked into model space at
/// import, so a posed node draws its meshes with the change from its bind pose to its posed pose;
/// a pose on a node also moves the nodes below it. Skinned meshes follow their bones instead.
/// </summary>
public static class ModelNodePoses
{
    /// <summary>
    /// For each node, the matrix that takes its baked meshes from the bind pose to the posed one;
    /// null when none of the poses names a node of this model.
    /// </summary>
    public static Matrix4x4[] Deltas(GModelAsset asset, IReadOnlyDictionary<string, ModelNodePose> poses)
    {
        if (asset?.Nodes is not { Count: > 0 } nodes || poses is not { Count: > 0 }) return null;
        int count = nodes.Count;
        var bind = new Matrix4x4[count];
        var posed = new Matrix4x4[count];
        var done = new bool[count];
        bool any = false;
        for (int i = 0; i < count; i++)
            any |= poses.ContainsKey(nodes[i].Name ?? string.Empty);
        if (!any) return null;
        for (int i = 0; i < count; i++) Resolve(i, 0);
        var deltas = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
            deltas[i] = Matrix4x4.Invert(bind[i], out Matrix4x4 inverse) ? inverse * posed[i] : Matrix4x4.Identity;
        return deltas;

        void Resolve(int index, int depth)
        {
            if (done[index]) return;
            GModelNode node = nodes[index];
            Matrix4x4 local = node.LocalTransform;
            Matrix4x4 posedLocal = local;
            if (poses.TryGetValue(node.Name ?? string.Empty, out ModelNodePose pose))
            {
                posedLocal = Matrix4x4.CreateFromQuaternion(pose.Rotation) * local;
                posedLocal.Translation += pose.Translation;
            }
            int parent = node.ParentIndex;
            if (parent >= 0 && parent < count && parent != index && depth < 128)
            {
                Resolve(parent, depth + 1);
                bind[index] = local * bind[parent];
                posed[index] = posedLocal * posed[parent];
            }
            else
            {
                bind[index] = local;
                posed[index] = posedLocal;
            }
            done[index] = true;
        }
    }
}

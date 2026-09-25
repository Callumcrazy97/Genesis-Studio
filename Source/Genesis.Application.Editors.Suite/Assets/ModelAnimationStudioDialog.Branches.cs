using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelAnimationStudioDialog
{
    public void DrawBone(Vector3 start, Vector3 end)
    {
        if (Vector3.DistanceSquared(start, end) < 1e-8f) return;
        int from = FindJoint(start), to = FindJoint(end);
        if (from < 0 && to < 0)
        {
            AddJointAt(-1, start); from = _asset.Rig.Bones.Count - 1;
            AddJointAt(from, end);
        }
        else if (from < 0) AddJointAt(to, start);
        else if (to < 0) AddJointAt(from, end);
        else if (from != to) JoinBranches(from, to);
        SaveRig();
        SetStatus("Bone connected. Existing branches are kept. Draw more bones or joints, then click Bind To Mesh.");
    }

    private void JoinBranches(int from, int to)
    {
        var bones = _asset.Rig.Bones;
        if (bones[from].ParentIndex == to || bones[to].ParentIndex == from) return;
        int Root(int bone) { while (bones[bone].ParentIndex >= 0) bone = bones[bone].ParentIndex; return bone; }
        int fromRoot = Root(from), toRoot = Root(to);
        if (fromRoot == toRoot)
            throw new InvalidOperationException("These joints are already connected through the skeleton. A new bone would create a loop.");

        // Keep the larger existing skeleton rooted as it is. Reroot the smaller branch at
        // the clicked endpoint, reversing its parent path without dropping any bone edge.
        int fromCount = Enumerable.Range(0, bones.Count).Count(i => Root(i) == fromRoot);
        int toCount = Enumerable.Range(0, bones.Count).Count(i => Root(i) == toRoot);
        int parent = fromCount >= toCount ? from : to;
        int child = fromCount >= toCount ? to : from;
        int[] parents = bones.Select(b => b.ParentIndex).ToArray();
        int previous = parent, cursor = child;
        while (cursor >= 0)
        {
            int next = parents[cursor]; parents[cursor] = previous; previous = cursor; cursor = next;
        }

        Matrix4x4[] ConvertPose(Matrix4x4[] source)
        {
            var world = GModelPrimitiveFactory.ComputeWorldTransforms(bones, source);
            var locals = new Matrix4x4[world.Length];
            for (int i = 0; i < world.Length; i++)
            {
                if (parents[i] < 0) locals[i] = world[i];
                else
                {
                    if (!Matrix4x4.Invert(world[parents[i]], out var inverse))
                        throw new InvalidOperationException("The joint transform has zero scale.");
                    locals[i] = world[i] * inverse;
                }
            }
            return locals;
        }
        // Compute everything before changing the hierarchy, including saved poses and clips.
        var layout = ConvertPose(_poseLocals);
        var bind = ConvertPose(ModelPoseWorkflow.BindPose(_asset));
        var poses = _asset.Poses.Select(p => ConvertPose(p.LocalBoneTransforms)).ToArray();
        var clips = _asset.Animations.Select(clip => clip.Frames.Select(f => ConvertPose(f.LocalBoneTransforms)).ToArray()).ToArray();
        for (int i = 0; i < bones.Count; i++) { bones[i].ParentIndex = parents[i]; bones[i].BindLocal = bind[i]; }
        for (int i = 0; i < poses.Length; i++) _asset.Poses[i].LocalBoneTransforms = poses[i];
        for (int i = 0; i < clips.Length; i++)
        {
            _asset.Animations[i].Tracks.Clear();
            for (int frame = 0; frame < clips[i].Length; frame++) _asset.Animations[i].Frames[frame].LocalBoneTransforms = clips[i][frame];
        }
        GModelPrimitiveFactory.RebuildInverseBindMatrices(_asset.Rig);
        _poseLocals = layout; _layoutDirty = true;
        _preview.AdoptAnimationAsset(_asset, _working.Name); ShowPose(_poseLocals);
    }
}

using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// Converts a model from a right-handed source (glTF, Blender's exports) to Genesis's left-handed
    /// world by mirroring it along Z. Without this a right-handed model is drawn as its mirror image:
    /// text reads backwards and a right-handed figure holds things in its left hand.
    /// </summary>
    /// <remarks>
    /// Every point and direction has its Z negated, and every transform M becomes S·M·S (S the Z
    /// mirror), which keeps transforms proper rotations: node, bone, inverse-bind, socket, pose and
    /// clip matrices, TRS keys (a rotation q becomes (-x, -y, z, w)), pivots and colliders.
    /// Triangles are reversed so their fronts still face out, and tangents flip their handedness sign.
    /// A model facing +Z in its source faces -Z, Genesis's forward, afterwards.
    /// </remarks>
    public static class GModelHandedness
    {
        public const string MetadataKey = "import.handedness";
        private static readonly Matrix4x4 Mirror = Matrix4x4.CreateScale(1f, 1f, -1f);

        /// <summary>Mirrors <paramref name="asset"/> in place along Z.</summary>
        public static void ConvertFromRightHanded(GModelAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);
            foreach (GModelNode node in asset.Nodes) node.LocalTransform = M(node.LocalTransform);
            foreach (GModelSocket socket in asset.Sockets) socket.LocalTransform = M(socket.LocalTransform);
            foreach (GModelCollider collider in asset.Colliders) collider.Center = V(collider.Center);
            foreach (GModelPose pose in asset.Poses) MirrorAll(pose.LocalBoneTransforms);
            ConvertRig(asset.Rig);
            foreach (GModelRigPreset preset in asset.RigLibrary) ConvertRig(preset.Rig);
            if (asset.Pivot is GModelPivot pivot)
            {
                pivot.Position = V(pivot.Position);
                pivot.Rotation = Q(pivot.Rotation);
            }

            foreach (GModelAnimationClip clip in asset.Animations)
            {
                foreach (GModelAnimationFrame frame in clip.Frames) MirrorAll(frame.LocalBoneTransforms);
                foreach (GModelAnimationTrack track in clip.Tracks)
                {
                    foreach (GModelTrsKey key in track.Keys)
                    {
                        key.Translation = V(key.Translation);
                        key.Rotation = Q(key.Rotation);
                    }
                }
            }

            foreach (GModelMesh mesh in asset.Meshes) ConvertMesh(mesh);

            asset.ImportSettings ??= new GModelImportSettings();
            asset.ImportSettings.ConvertRightHanded = true;
            asset.Metadata[MetadataKey] = "converted from right-handed (mirrored along Z)";
            asset.RecalculateBounds();
        }

        private static void ConvertRig(GModelRig rig)
        {
            if (rig is null) return;
            MirrorAll(rig.InverseBindMatrices);
            foreach (GModelBone bone in rig.Bones) bone.BindLocal = M(bone.BindLocal);
        }

        private static void ConvertMesh(GModelMesh mesh)
        {
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                mesh.Vertices[i].Position = V(mesh.Vertices[i].Position);
                mesh.Vertices[i].Normal = V(mesh.Vertices[i].Normal);
            }
            for (int i = 0; i < mesh.SkinnedVertices.Length; i++)
            {
                mesh.SkinnedVertices[i].Position = V(mesh.SkinnedVertices[i].Position);
                mesh.SkinnedVertices[i].Normal = V(mesh.SkinnedVertices[i].Normal);
            }
            for (int i = 0; i < mesh.Tangents.Length; i++)
            {
                Vector4 t = mesh.Tangents[i];
                mesh.Tangents[i] = new Vector4(t.X, t.Y, -t.Z, -t.W);
            }
            for (int i = 0; i < mesh.SubdivisionCageVertices.Length; i++)
            {
                mesh.SubdivisionCageVertices[i].Position = V(mesh.SubdivisionCageVertices[i].Position);
                mesh.SubdivisionCageVertices[i].Normal = V(mesh.SubdivisionCageVertices[i].Normal);
            }
            foreach (GModelMorphTarget morph in mesh.MorphTargets)
            {
                for (int i = 0; i < morph.PositionDeltas.Length; i++) morph.PositionDeltas[i] = V(morph.PositionDeltas[i]);
                for (int i = 0; i < morph.NormalDeltas.Length; i++) morph.NormalDeltas[i] = V(morph.NormalDeltas[i]);
            }
            ReverseWinding(mesh.Indices);
            ReverseWinding(mesh.SubdivisionCageIndices);
        }

        private static void ReverseWinding(ushort[] indices)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        }

        private static void MirrorAll(Matrix4x4[] matrices)
        {
            if (matrices is null) return;
            for (int i = 0; i < matrices.Length; i++) matrices[i] = M(matrices[i]);
        }

        private static Matrix4x4 M(Matrix4x4 value) => Mirror * value * Mirror;

        private static Vector3 V(Vector3 value) => new(value.X, value.Y, -value.Z);

        private static Quaternion Q(Quaternion value) => new(-value.X, -value.Y, value.Z, value.W);
    }
}

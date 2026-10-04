using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// Clips a Model borrows from other Models (its <c>animationLibraries</c>), so characters on one
    /// skeleton share one set of clips instead of each carrying a copy.
    /// </summary>
    /// <remarks>
    /// Clips are matched to the model by bone (node) name. When the skeletons are the same (the same
    /// names in the same order, posed the same at rest) the clip objects themselves are shared, so
    /// many models hold one copy. Otherwise each frame is retargeted: every bone keeps its own rest
    /// offset plus the library's movement from rest, and takes the library's rotation, so a clip made
    /// on one body plays on another of different proportions. Borrowed clips are never saved into
    /// the model's own file (<see cref="GModelAsset.LibraryClipNames"/>), and a clip of the same
    /// name the model has itself wins.
    /// </remarks>
    public static class ModelAnimationLibraries
    {
        private const float SameBindTolerance = 1e-4f;

        /// <summary>Adds <paramref name="library"/>'s clips that <paramref name="asset"/> does not have.</summary>
        public static int AddClips(GModelAsset asset, GModelAsset library)
        {
            if (asset is null || library?.Animations is not { Count: > 0 }) return 0;
            int added = 0;
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GModelAnimationClip clip in asset.Animations) own.Add(clip.Name ?? string.Empty);

            foreach (GModelAnimationClip clip in library.Animations)
            {
                if (clip?.Frames is not { Count: > 0 } || !own.Add(clip.Name ?? string.Empty)) continue;
                GModelAnimationClip borrowed = Borrow(asset, library, clip);
                if (borrowed is null) continue;
                asset.Animations.Add(borrowed);
                asset.LibraryClipNames.Add(borrowed.Name ?? string.Empty);
                added++;
            }
            return added;
        }

        private static GModelAnimationClip Borrow(GModelAsset asset, GModelAsset library, GModelAnimationClip clip)
        {
            int length = clip.Frames[0].LocalBoneTransforms?.Length ?? 0;
            if (length == 0) return null;
            // Imported clips hold one transform per node; clips made on a rig hold one per bone.
            bool byNode = length == library.Nodes.Count;
            if (!byNode && length != (library.Rig?.Bones?.Count ?? -1)) return null;
            (string[] libraryNames, Matrix4x4[] libraryBind) = Skeleton(library, byNode);
            (string[] targetNames, Matrix4x4[] targetBind) = Skeleton(asset, byNode);
            if (targetNames.Length == 0) return null;

            if (Same(libraryNames, libraryBind, targetNames, targetBind)) return clip;

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < libraryNames.Length; i++) index.TryAdd(libraryNames[i] ?? string.Empty, i);
            int[] map = new int[targetNames.Length];
            int matched = 0;
            for (int i = 0; i < targetNames.Length; i++)
            {
                map[i] = index.TryGetValue(targetNames[i] ?? string.Empty, out int found) ? found : -1;
                if (map[i] >= 0) matched++;
            }
            if (matched == 0) return null;

            var retargeted = new GModelAnimationClip
            {
                Name = clip.Name,
                Fps = clip.Fps,
                Loop = clip.Loop,
            };
            foreach (GModelAnimationFrame frame in clip.Frames)
            {
                var transforms = new Matrix4x4[targetNames.Length];
                for (int i = 0; i < transforms.Length; i++)
                {
                    int source = map[i];
                    transforms[i] = source >= 0 && source < frame.LocalBoneTransforms.Length
                        ? Retarget(frame.LocalBoneTransforms[source], libraryBind[source], targetBind[i])
                        : targetBind[i];
                }
                var copy = new GModelAnimationFrame { LocalBoneTransforms = transforms };
                foreach (KeyValuePair<string, float> weight in frame.MorphWeights) copy.MorphWeights[weight.Key] = weight.Value;
                retargeted.Frames.Add(copy);
            }
            return retargeted;
        }

        private static (string[] Names, Matrix4x4[] Bind) Skeleton(GModelAsset asset, bool byNode)
        {
            if (byNode)
            {
                var names = new string[asset.Nodes.Count];
                var bind = new Matrix4x4[asset.Nodes.Count];
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = asset.Nodes[i].Name;
                    bind[i] = asset.Nodes[i].LocalTransform;
                }
                return (names, bind);
            }
            List<GModelBone> bones = asset.Rig?.Bones ?? new List<GModelBone>();
            var boneNames = new string[bones.Count];
            var boneBind = new Matrix4x4[bones.Count];
            for (int i = 0; i < boneNames.Length; i++)
            {
                boneNames[i] = bones[i].Name;
                boneBind[i] = bones[i].BindLocal;
            }
            return (boneNames, boneBind);
        }

        private static bool Same(string[] aNames, Matrix4x4[] aBind, string[] bNames, Matrix4x4[] bBind)
        {
            if (aNames.Length != bNames.Length) return false;
            for (int i = 0; i < aNames.Length; i++)
            {
                if (!string.Equals(aNames[i], bNames[i], StringComparison.OrdinalIgnoreCase)) return false;
                for (int c = 0; c < 16; c++)
                    if (MathF.Abs(aBind[i][c / 4, c % 4] - bBind[i][c / 4, c % 4]) > SameBindTolerance) return false;
            }
            return true;
        }

        /// <summary>The library pose on the target bone: target rest offset plus library movement, library rotation.</summary>
        private static Matrix4x4 Retarget(Matrix4x4 pose, Matrix4x4 libraryBind, Matrix4x4 targetBind)
        {
            if (!Matrix4x4.Decompose(pose, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
                || !Matrix4x4.Decompose(libraryBind, out Vector3 libraryScale, out _, out Vector3 libraryTranslation)
                || !Matrix4x4.Decompose(targetBind, out Vector3 targetScale, out _, out Vector3 targetTranslation))
                return pose;
            Vector3 ratio = new(
                MathF.Abs(libraryScale.X) > 1e-6f ? scale.X / libraryScale.X : 1f,
                MathF.Abs(libraryScale.Y) > 1e-6f ? scale.Y / libraryScale.Y : 1f,
                MathF.Abs(libraryScale.Z) > 1e-6f ? scale.Z / libraryScale.Z : 1f);
            return Matrix4x4.CreateScale(targetScale * ratio)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(targetTranslation + (translation - libraryTranslation));
        }
    }
}

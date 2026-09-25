using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    public static class GModelPrimitiveFactory
    {
        /// <summary>Preset quadruped rig + clip bake version. Bump to force one-time migration on load.</summary>
        public const int CurrentRigVersion = 5;

        private static readonly string[] PresetClips =
        {
            "Idle", "Walk", "Run", "Jump", "Fall", "Sit Down", "Stand Up", "Crawl", "Attack", "Die",
        };

        public static GModelAsset CreateCube(string name, float size = 1f)
        {
            (MeshVertex[] vertices, ushort[] indices) = MeshGeometry.BuildCube(RenderColor.White, size);
            var asset = new GModelAsset
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Model" : name,
                ImportRequired = false,
                ImportMessage = "",
            };
            asset.Materials.Add(new GModelMaterial { Name = "Default", BaseColor = Vector4.One });
            asset.Meshes.Add(new GModelMesh
            {
                Name = "Cube",
                MaterialIndex = 0,
                Vertices = vertices,
                Indices = indices,
            });
            asset.RecalculateBounds();
            return asset;
        }

        public static GModelAsset CreatePlaceholder(string name, string message)
        {
            var asset = CreateCube(name, 1f);
            asset.ImportRequired = true;
            asset.ImportMessage = message ?? "Reimport required";
            asset.Materials[0].BaseColor = new Vector4(1f, 0.15f, 0.4f, 1f);
            return asset;
        }

        public static void AddFoxTemplateRigAndClips(GModelAsset asset)
            => ApplyTemplateRigAndClips(asset, "Quadruped", "Fox");

        public static void ApplyTemplateRigAndClips(GModelAsset asset, string category, string subcategory)
        {
            if (asset == null) return;

            category = string.IsNullOrWhiteSpace(category) ? "Quadruped" : category.Trim();
            subcategory = string.IsNullOrWhiteSpace(subcategory) ? "Fox" : subcategory.Trim();
            asset.Rig = new GModelRig { TemplateName = GModelRigTemplates.ComposeId(category, subcategory) };

            if (category.Equals("Humanoid", StringComparison.OrdinalIgnoreCase))
                BuildHumanoidRig(asset, subcategory);
            else
                BuildQuadrupedRig(asset, subcategory);

            FitRigToMesh(asset);
            RebuildInverseBindMatrices(asset.Rig);
            BindSkinToMesh(asset);
            asset.Rig.RigVersion = CurrentRigVersion;

            asset.Animations.Clear();
            foreach (string clip in PresetClips)
                asset.Animations.Add(CreateBuiltInClip(clip, asset.Rig, ClipFrameCount(clip), 60f));
        }

        public static void ApplyTemplateRigAndClips(GModelAsset asset, string templateName)
        {
            GModelRigTemplates.ParseId(templateName, out string category, out string subcategory);
            ApplyTemplateRigAndClips(asset, category, subcategory);
        }

        public static void RebuildPresetClipsForCurrentRig(GModelAsset asset)
        {
            if (asset?.Rig?.Bones == null || asset.Rig.Bones.Count == 0)
                return;

            RebuildInverseBindMatrices(asset.Rig);
            asset.Animations.Clear();
            foreach (string clip in PresetClips)
                asset.Animations.Add(CreateBuiltInClip(clip, asset.Rig, ClipFrameCount(clip), 60f));
            asset.LastSkinDiagnostics = ValidateSkin(asset);
        }

        public static void RebuildInverseBindMatrices(GModelRig rig)
        {
            if (rig?.Bones == null || rig.Bones.Count == 0)
            {
                if (rig != null) rig.InverseBindMatrices = Array.Empty<Matrix4x4>();
                return;
            }

            Matrix4x4[] bindWorld = ComputeWorldTransforms(rig.Bones, rig.Bones.ConvertAll(b => b.BindLocal).ToArray());
            var inverse = new Matrix4x4[bindWorld.Length];
            for (int i = 0; i < bindWorld.Length; i++)
            {
                if (!Matrix4x4.Invert(bindWorld[i], out inverse[i]))
                    inverse[i] = Matrix4x4.Identity;
            }
            rig.InverseBindMatrices = inverse;
        }

        public static Matrix4x4[] EvaluateSkinPalette(GModelRig rig, Matrix4x4[] locals)
        {
            if (rig?.Bones == null || rig.Bones.Count == 0)
                return Array.Empty<Matrix4x4>();

            int count = rig.Bones.Count;
            var palette = new Matrix4x4[count];
            Matrix4x4[] world = ComputeWorldTransforms(rig.Bones, locals);
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 inverseBind = rig.InverseBindMatrices != null && i < rig.InverseBindMatrices.Length
                    ? rig.InverseBindMatrices[i]
                    : Matrix4x4.Identity;
                // System.Numerics uses row vectors: bind-space position * inverseBind * currentWorld.
                // The previous order only happened to work for commuting transforms and broke
                // imported rigs whose joints combine translation and rotation.
                palette[i] = inverseBind * world[i];
            }
            return palette;
        }

        public static Matrix4x4[] EvaluateBindPosePalette(GModelRig rig)
        {
            if (rig?.Bones == null || rig.Bones.Count == 0)
                return Array.Empty<Matrix4x4>();

            var locals = new Matrix4x4[rig.Bones.Count];
            for (int i = 0; i < locals.Length; i++)
                locals[i] = rig.Bones[i].BindLocal;
            return EvaluateSkinPalette(rig, locals);
        }

        public static void EnsureRigIntegrity(GModelAsset asset)
        {
            if (asset?.Rig?.IsValid != true)
                return;

            bool skinned = asset.Meshes?.Any(m => m.IsSkinned) == true;
            bool inverseStale = asset.Rig.InverseBindMatrices == null
                || asset.Rig.InverseBindMatrices.Length != asset.Rig.Bones.Count;

            if (inverseStale && skinned)
                RebuildInverseBindMatrices(asset.Rig);

            if (asset.RigWizard is not null || asset.Rig.RigVersion >= CurrentRigVersion || string.IsNullOrWhiteSpace(asset.Rig.TemplateName))
                return;

            GModelRigTemplates.ParseId(asset.Rig.TemplateName, out string category, out string subcategory);
            if (string.IsNullOrWhiteSpace(category))
                return;

            ApplyTemplateRigAndClips(asset, category, subcategory);
        }

        public static Matrix4x4[] ComputeWorldTransforms(IReadOnlyList<GModelBone> bones, Matrix4x4[] localTransforms)
        {
            int count = Math.Max(0, bones?.Count ?? 0);
            var world = new Matrix4x4[count];
            var state = new byte[count];
            for (int i = 0; i < count; i++) Resolve(i);
            return world;

            void Resolve(int index)
            {
                if (state[index] == 2) return;
                // Malformed imported data must not recurse forever. A cycle is treated as a root
                // at the repeated node; validation/import diagnostics can still report it.
                if (state[index] == 1)
                {
                    world[index] = Local(index);
                    state[index] = 2;
                    return;
                }
                state[index] = 1;
                Matrix4x4 local = localTransforms != null && index < localTransforms.Length
                    ? localTransforms[index]
                    : bones[index].BindLocal;
                int parent = bones[index].ParentIndex;
                if (parent >= 0 && parent < count && parent != index)
                {
                    Resolve(parent);
                    world[index] = local * world[parent];
                }
                else world[index] = local;
                state[index] = 2;
            }

            Matrix4x4 Local(int index) => localTransforms != null && index < localTransforms.Length
                ? localTransforms[index]
                : bones[index].BindLocal;
        }

        public static Matrix4x4[] EvaluateAnimatedLocals(GModelAsset asset, RuntimeModelAnimationState animation)
        {
            if (asset?.Rig?.Bones == null || asset.Rig.Bones.Count == 0)
                return Array.Empty<Matrix4x4>();

            if (animation.Controller != null)
                return PreserveRootTransforms(asset, animation.Controller.Evaluate(asset), animation.PreserveRootTransform);

            Matrix4x4[] current = EvaluateClipLocals(asset, animation.ClipName, animation.TimeSeconds,
                animation.Fps, animation.Loop);
            if (string.IsNullOrWhiteSpace(animation.PreviousClipName) || animation.BlendFactor >= 1f)
                return PreserveRootTransforms(asset, current, animation.PreserveRootTransform);

            Matrix4x4[] previous = EvaluateClipLocals(asset, animation.PreviousClipName,
                animation.PreviousTimeSeconds, animation.Fps, animation.Loop);
            float blend = Math.Clamp(animation.BlendFactor, 0f, 1f);
            for (int i = 0; i < current.Length && i < previous.Length; i++)
                current[i] = BlendLocalTransform(previous[i], current[i], blend);
            return PreserveRootTransforms(asset, current, animation.PreserveRootTransform);
        }

        private static Matrix4x4[] PreserveRootTransforms(
            GModelAsset asset,
            Matrix4x4[] locals,
            bool preserve)
        {
            if (!preserve || asset?.Rig?.Bones == null) return locals;
            for (int index = 0; index < locals.Length && index < asset.Rig.Bones.Count; index++)
                if (asset.Rig.Bones[index].ParentIndex < 0)
                    locals[index] = asset.Rig.Bones[index].BindLocal;
            return locals;
        }

        private static Matrix4x4[] EvaluateClipLocals(
            GModelAsset asset,
            string clipName,
            float timeSeconds,
            float fpsOverride,
            bool loop)
        {
            if (asset?.Rig?.Bones == null || asset.Rig.Bones.Count == 0)
                return Array.Empty<Matrix4x4>();

            int count = asset.Rig.Bones.Count;
            var locals = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
                locals[i] = asset.Rig.Bones[i].BindLocal;

            if (string.IsNullOrWhiteSpace(clipName) || asset.Animations == null)
                return locals;

            GModelAnimationClip clip = asset.Animations.FirstOrDefault(c =>
                string.Equals(c.Name, clipName, StringComparison.OrdinalIgnoreCase));
            if (clip?.Frames == null || clip.Frames.Count == 0)
                return locals;

            float fps = fpsOverride > 0f ? fpsOverride : MathF.Max(1f, clip.Fps);
            int frame = (int)MathF.Floor(timeSeconds * fps);
            if (loop || clip.Loop)
                frame = ((frame % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count;
            else
                frame = Math.Clamp(frame, 0, clip.Frames.Count - 1);

            Matrix4x4[] frameLocals = clip.Frames[frame].LocalBoneTransforms;
            for (int i = 0; i < count; i++)
                locals[i] = frameLocals != null && i < frameLocals.Length ? frameLocals[i] : asset.Rig.Bones[i].BindLocal;
            return locals;
        }

        private static Matrix4x4 BlendLocalTransform(Matrix4x4 from, Matrix4x4 to, float amount)
        {
            if (!Matrix4x4.Decompose(from, out Vector3 fromScale, out Quaternion fromRotation, out Vector3 fromTranslation)
                || !Matrix4x4.Decompose(to, out Vector3 toScale, out Quaternion toRotation, out Vector3 toTranslation))
                return amount < 0.5f ? from : to;

            Vector3 scale = Vector3.Lerp(fromScale, toScale, amount);
            Vector3 translation = Vector3.Lerp(fromTranslation, toTranslation, amount);
            Quaternion rotation = Quaternion.Slerp(fromRotation, toRotation, amount);
            return Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation);
        }

        /// <summary>Repositions bind-pose joints to mesh vertex clusters so rigs fit arbitrary imported meshes.</summary>
        public static void FitRigToMesh(GModelAsset asset)
        {
            if (asset?.Rig?.Bones == null || asset.Rig.Bones.Count == 0 || asset.Meshes == null)
                return;

            if (!asset.Meshes.Any(m => (m.Vertices?.Length ?? 0) > 0 || (m.SkinnedVertices?.Length ?? 0) > 0))
                return;

            bool quadruped = asset.Rig.TemplateName?.StartsWith("Quadruped", StringComparison.OrdinalIgnoreCase) ?? false;

            if (quadruped)
                FitQuadrupedRigToMesh(asset);
            else if (asset.Rig.TemplateName?.StartsWith("Humanoid", StringComparison.OrdinalIgnoreCase) == true)
                FitHumanoidRigToMesh(asset);
        }

        public static void BindSkinToMesh(GModelAsset asset)
            => BindSkinToMesh(asset, System.Threading.CancellationToken.None);

        public static void BindSkinToMesh(GModelAsset asset, System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset?.Rig?.Bones == null || asset.Rig.Bones.Count == 0 || asset.Meshes == null)
                return;

            if (string.IsNullOrWhiteSpace(asset.Rig.TemplateName))
            {
                GModelSurfaceBinding.Bind(asset, cancellationToken);
                asset.LastSkinDiagnostics = ValidateSkin(asset);
                return;
            }

            Matrix4x4[] bindWorld = ComputeWorldTransforms(asset.Rig.Bones, asset.Rig.Bones.Select(b => b.BindLocal).ToArray());
            List<BoneSegment> segments = BuildBoneSegments(asset.Rig.Bones, bindWorld, asset);
            var regionFilter = CreateBoneRegionFilter(asset);
            int maxInfluences = asset.SkinBindingMode switch
            {
                GModelSkinBindingMode.Rigid1 => 1,
                GModelSkinBindingMode.Smooth4 => 4,
                _ => 2,
            };

            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh.Vertices == null || mesh.Vertices.Length == 0) continue;
                mesh.SkinnedVertices = mesh.Vertices.Select(v => new SkinnedMeshVertex
                {
                    Position = v.Position,
                    Normal = v.Normal,
                    Color = v.Color,
                    UV = v.UV,
                    JointWeights = SegmentBoneWeights(v.Position, segments, maxInfluences, regionFilter, out Vector4 jointIndices),
                    JointIndices = jointIndices,
                }).ToArray();
                mesh.IsSkinned = true;
            }

            if (asset.SkinBindingMode == GModelSkinBindingMode.Rigid1)
                StabilizeRigidTriangleWeights(asset, regionFilter);

            asset.LastSkinDiagnostics = ValidateSkin(asset);
        }

        private static void StabilizeRigidTriangleWeights(GModelAsset asset, Func<Vector3, HashSet<int>> regionFilter)
        {
            if (asset?.Meshes == null) return;
            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh?.SkinnedVertices == null || mesh.SkinnedVertices.Length == 0)
                    continue;

                UnifyCoincidentRigidVertices(mesh);
                UnifyRigidTriangles(mesh, asset, regionFilter);
                UnifyCoincidentRigidVertices(mesh);
                UnifyRigidTriangles(mesh, asset, regionFilter);
            }
        }

        private static void UnifyRigidTriangles(GModelMesh mesh, GModelAsset asset, Func<Vector3, HashSet<int>> regionFilter)
        {
            if (mesh.Indices == null || mesh.Indices.Length < 3 || mesh.SkinnedVertices == null)
                return;

            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int a = mesh.Indices[i];
                int b = mesh.Indices[i + 1];
                int c = mesh.Indices[i + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= mesh.SkinnedVertices.Length ||
                    b >= mesh.SkinnedVertices.Length ||
                    c >= mesh.SkinnedVertices.Length)
                    continue;

                int ja = DominantJoint(mesh.SkinnedVertices[a]);
                int jb = DominantJoint(mesh.SkinnedVertices[b]);
                int jc = DominantJoint(mesh.SkinnedVertices[c]);
                if (ja == jb && jb == jc)
                    continue;

                Vector3 centroid = (mesh.SkinnedVertices[a].Position + mesh.SkinnedVertices[b].Position + mesh.SkinnedVertices[c].Position) / 3f;
                int joint = ChooseRigidTriangleJoint(asset, centroid, ja, jb, jc, regionFilter);
                SetRigidInfluence(ref mesh.SkinnedVertices[a], joint);
                SetRigidInfluence(ref mesh.SkinnedVertices[b], joint);
                SetRigidInfluence(ref mesh.SkinnedVertices[c], joint);
            }
        }

        private static int ChooseRigidTriangleJoint(GModelAsset asset, Vector3 centroid, int a, int b, int c, Func<Vector3, HashSet<int>> regionFilter)
        {
            if (a == b || a == c) return a;
            if (b == c) return b;

            HashSet<int> allowed = regionFilter?.Invoke(centroid);
            if (allowed != null)
            {
                if (allowed.Contains(a)) return a;
                if (allowed.Contains(b)) return b;
                if (allowed.Contains(c)) return c;
            }

            return a;
        }

        private static void UnifyCoincidentRigidVertices(GModelMesh mesh)
        {
            if (mesh?.SkinnedVertices == null || mesh.SkinnedVertices.Length == 0)
                return;

            var groups = new Dictionary<(int X, int Y, int Z), List<int>>();
            for (int i = 0; i < mesh.SkinnedVertices.Length; i++)
            {
                Vector3 p = mesh.SkinnedVertices[i].Position;
                var key = ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
                if (!groups.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }
                list.Add(i);
            }

            foreach (List<int> group in groups.Values)
            {
                if (group.Count <= 1) continue;
                int joint = group
                    .Select(i => DominantJoint(mesh.SkinnedVertices[i]))
                    .GroupBy(j => j)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .First()
                    .Key;
                foreach (int index in group)
                    SetRigidInfluence(ref mesh.SkinnedVertices[index], joint);
            }
        }

        private static int DominantJoint(SkinnedMeshVertex vertex)
        {
            Vector4 weights = vertex.JointWeights;
            Vector4 joints = vertex.JointIndices;
            float best = weights.X;
            int joint = (int)MathF.Round(joints.X);
            if (weights.Y > best) { best = weights.Y; joint = (int)MathF.Round(joints.Y); }
            if (weights.Z > best) { best = weights.Z; joint = (int)MathF.Round(joints.Z); }
            if (weights.W > best) { joint = (int)MathF.Round(joints.W); }
            return Math.Max(0, joint);
        }

        private static void SetRigidInfluence(ref SkinnedMeshVertex vertex, int joint)
        {
            vertex.JointIndices = new Vector4(Math.Max(0, joint), 0f, 0f, 0f);
            vertex.JointWeights = new Vector4(1f, 0f, 0f, 0f);
        }

        public static GModelSkinDiagnostics ValidateSkin(GModelAsset asset)
        {
            var diagnostics = new GModelSkinDiagnostics();
            if (asset?.Meshes == null)
            {
                diagnostics.Summary = "No model asset.";
                diagnostics.Warnings.Add(diagnostics.Summary);
                return diagnostics;
            }

            diagnostics.MeshCount = asset.Meshes.Count;
            diagnostics.SkinnedMeshCount = asset.Meshes.Count(m => m?.IsSkinned == true);
            int boneCount = asset.Rig?.Bones?.Count ?? 0;
            if (asset.Rig?.IsValid == true)
            {
                if (asset.Rig.InverseBindMatrices == null || asset.Rig.InverseBindMatrices.Length != boneCount)
                    diagnostics.MissingInverseBindMatrices = Math.Max(1, boneCount - (asset.Rig.InverseBindMatrices?.Length ?? 0));
                if (asset.Rig.InverseBindMatrices != null)
                {
                    foreach (Matrix4x4 m in asset.Rig.InverseBindMatrices)
                        if (!IsFinite(m)) diagnostics.NonFiniteMatrices++;
                }
            }
            else if (diagnostics.SkinnedMeshCount > 0)
            {
                diagnostics.MissingInverseBindMatrices++;
            }

            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh?.SkinnedVertices == null || mesh.SkinnedVertices.Length == 0)
                    continue;
                foreach (SkinnedMeshVertex v in mesh.SkinnedVertices)
                {
                    diagnostics.VertexCount++;
                    if (!IsFinite(v.Position) || !IsFinite(v.Normal))
                        diagnostics.NonFiniteVertices++;

                    float sum = v.JointWeights.X + v.JointWeights.Y + v.JointWeights.Z + v.JointWeights.W;
                    float error = MathF.Abs(sum - 1f);
                    diagnostics.MaxWeightError = MathF.Max(diagnostics.MaxWeightError, error);
                    if (sum <= 0.0001f)
                        diagnostics.UnweightedVertices++;
                    if (error > 0.01f)
                        diagnostics.UnnormalizedVertices++;

                    int[] seen = new int[4];
                    int seenCount = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        float weight = GetComponent(v.JointWeights, i);
                        if (weight <= 0.0001f) continue;
                        int joint = (int)MathF.Round(GetComponent(v.JointIndices, i));
                        if (joint < 0 || joint >= boneCount)
                            diagnostics.InvalidJointReferences++;
                        for (int s = 0; s < seenCount; s++)
                            if (seen[s] == joint)
                                diagnostics.DuplicateJointReferences++;
                        if (seenCount < seen.Length)
                            seen[seenCount++] = joint;
                    }
                }
            }

            diagnostics.MaxAnimatedDisplacement = EstimateMaxAnimatedDisplacement(asset);
            diagnostics.MaxAnimatedEdgeStretch = EstimateMaxAnimatedEdgeStretch(asset, out int stretchedEdges);
            diagnostics.StretchedTriangleEdges = stretchedEdges;
            if (diagnostics.MissingInverseBindMatrices > 0)
                diagnostics.Warnings.Add("Missing or stale inverse bind matrices.");
            if (diagnostics.NonFiniteMatrices > 0 || diagnostics.NonFiniteVertices > 0)
                diagnostics.Warnings.Add("Non-finite matrix or vertex data detected.");
            if (diagnostics.UnweightedVertices > 0)
                diagnostics.Warnings.Add("One or more vertices have no valid skin weight.");
            if (diagnostics.UnnormalizedVertices > 0)
                diagnostics.Warnings.Add("One or more vertices have weights that do not sum to 1.");
            if (diagnostics.InvalidJointReferences > 0 || diagnostics.DuplicateJointReferences > 0)
                diagnostics.Warnings.Add("Invalid or duplicate joint references detected.");

            float diag = MathF.Max(0.001f, asset.Bounds?.Size.Length() ?? 1f);
            if (diagnostics.MaxAnimatedDisplacement > diag * 2.25f)
                diagnostics.Warnings.Add("Animated vertex displacement exceeds the model bounds safety threshold.");
            if (asset.SkinBindingMode == GModelSkinBindingMode.Rigid1 &&
                (diagnostics.MaxAnimatedEdgeStretch > 1.15f || diagnostics.StretchedTriangleEdges > 0))
                diagnostics.Warnings.Add($"Animated triangle stretch detected (max {diagnostics.MaxAnimatedEdgeStretch:0.###}, edges {diagnostics.StretchedTriangleEdges}).");

            diagnostics.Passed = diagnostics.Warnings.Count == 0;
            diagnostics.Summary = diagnostics.Passed
                ? $"Skin OK: {diagnostics.SkinnedMeshCount}/{diagnostics.MeshCount} skinned mesh(es), {diagnostics.VertexCount} weighted vertices."
                : string.Join(" ", diagnostics.Warnings);
            return diagnostics;
        }

        private static void FitQuadrupedRigToMesh(GModelAsset asset)
        {
            GModelBounds bounds = asset.Bounds ?? new GModelBounds();
            Vector3 min = bounds.Min;
            Vector3 size = bounds.Size;
            size = new Vector3(Math.Max(size.X, 0.001f), Math.Max(size.Y, 0.001f), Math.Max(size.Z, 0.001f));
            ResolveBodyAxes(size, out int forwardAxis, out int lateralAxis);

            QuadrupedFitResult highForwardHead = BuildQuadrupedFit(asset, min, size, forwardAxis, lateralAxis, headAtHighForward: true);
            QuadrupedFitResult lowForwardHead = BuildQuadrupedFit(asset, min, size, forwardAxis, lateralAxis, headAtHighForward: false);
            QuadrupedFitResult fit = highForwardHead.Score <= lowForwardHead.Score ? highForwardHead : lowForwardHead;

            ApplyWorldTargetsToBindPose(asset.Rig, fit.Targets);
        }

        private sealed class QuadrupedFitResult
        {
            public bool HeadAtHighForward;
            public Vector3[] Targets = Array.Empty<Vector3>();
            public int[] Counts = Array.Empty<int>();
            public float Score;
        }

        private static QuadrupedFitResult BuildQuadrupedFit(
            GModelAsset asset,
            Vector3 min,
            Vector3 size,
            int forwardAxis,
            int lateralAxis,
            bool headAtHighForward)
        {
            int boneCount = asset.Rig.Bones.Count;
            var sums = new Vector3[boneCount];
            var counts = new int[boneCount];
            var points = new List<Vector3>[boneCount];
            for (int i = 0; i < boneCount; i++)
                points[i] = new List<Vector3>();

            foreach (GModelMesh mesh in asset.Meshes)
            {
                foreach (Vector3 position in EnumerateMeshPositions(mesh))
                {
                    Vector3 n = (position - min) / size;
                    int primary = ClassifyQuadrupedVertex(n, forwardAxis, lateralAxis, headAtHighForward);
                    if (primary < 0 || primary >= boneCount) continue;
                    sums[primary] += position;
                    counts[primary]++;
                    points[primary].Add(position);
                }
            }

            Matrix4x4[] fallbackWorld = ComputeWorldTransforms(asset.Rig.Bones, asset.Rig.Bones.Select(b => b.BindLocal).ToArray());
            var targets = new Vector3[boneCount];
            for (int i = 0; i < boneCount; i++)
                targets[i] = counts[i] > 0 ? sums[i] / counts[i] : fallbackWorld[i].Translation;

            List<Vector3> torsoPoints = new();
            if (points.Length > 0) torsoPoints.AddRange(points[0]);
            if (points.Length > 1) torsoPoints.AddRange(points[1]);
            if (torsoPoints.Count > 0)
            {
                Vector3 torso = AveragePoints(torsoPoints);
                targets[0] = new Vector3(torso.X, Math.Clamp(torso.Y, min.Y + size.Y * 0.34f, min.Y + size.Y * 0.62f), torso.Z);
                targets[1] = points.Length > 1 && points[1].Count > 0
                    ? RobustRegionAverage(points[1], min, size, 0.34f, 0.78f, targets[1])
                    : targets[0] + new Vector3(0f, size.Y * 0.12f, 0f);
            }

            if (boneCount > 3)
            {
                Vector3 head = RobustRegionAverage(points[3], min, size, 0.36f, 0.86f, targets[3]);
                targets[3] = head;
            }

            if (boneCount > 2)
            {
                Vector3 neck = RobustRegionAverage(points[2], min, size, 0.34f, 0.78f, targets[2]);
                if (boneCount > 3)
                    neck = Vector3.Lerp(neck, Vector3.Lerp(targets[1], targets[3], 0.55f), 0.65f);
                targets[2] = neck;
            }

            if (boneCount > 4 && points[4].Count > 0)
            {
                FindQuadrupedTailChainTargets(points[4], min, size, forwardAxis, headAtHighForward,
                    out Vector3 tailBase, out Vector3 tailMid, out Vector3 tailTip);
                targets[4] = tailBase;
                if (boneCount > 9) targets[9] = tailMid;
                if (boneCount > 10) targets[10] = tailTip;
            }

            for (int leg = 5; leg <= 8 && leg < boneCount; leg++)
            {
                if (counts[leg] <= 0) continue;
                Vector3 footColumn = RobustRegionAverage(points[leg], min, size, 0f, 0.46f, sums[leg] / counts[leg]);
                int parent = asset.Rig.Bones[leg].ParentIndex;
                float hipY = parent == 1 ? targets[1].Y : targets[0].Y;
                targets[leg] = new Vector3(footColumn.X, hipY, footColumn.Z);
            }

            return new QuadrupedFitResult
            {
                HeadAtHighForward = headAtHighForward,
                Targets = targets,
                Counts = counts,
                Score = ScoreQuadrupedFit(points, targets, min, size, forwardAxis, headAtHighForward),
            };
        }

        private static float ScoreQuadrupedFit(
            IReadOnlyList<Vector3>[] points,
            Vector3[] targets,
            Vector3 min,
            Vector3 size,
            int forwardAxis,
            bool headAtHighForward)
        {
            int total = points.Sum(p => p.Count);
            if (total <= 0) return float.MaxValue;

            float score = 0f;
            score += MissingPenalty(points, 0, 400f);
            score += MissingPenalty(points, 1, 500f);
            score += MissingPenalty(points, 2, 700f);
            score += MissingPenalty(points, 3, 900f);
            score += MissingPenalty(points, 4, 600f);
            for (int leg = 5; leg <= 8 && leg < points.Length; leg++)
                score += MissingPenalty(points, leg, 350f);

            if (points.Length > 8)
            {
                float meanLeg = (points[5].Count + points[6].Count + points[7].Count + points[8].Count) / 4f;
                if (meanLeg > 0f)
                {
                    for (int leg = 5; leg <= 8; leg++)
                    {
                        float delta = points[leg].Count - meanLeg;
                        score += delta * delta / MathF.Max(1f, meanLeg * meanLeg) * 25f;
                    }
                }
            }

            if (targets.Length > 4)
            {
                float headF = NormalizedForward(targets[3], min, size, forwardAxis, headAtHighForward);
                float neckF = NormalizedForward(targets[2], min, size, forwardAxis, headAtHighForward);
                float spineF = NormalizedForward(targets[1], min, size, forwardAxis, headAtHighForward);
                float tailF = NormalizedForward(targets[4], min, size, forwardAxis, headAtHighForward);
                score += MathF.Max(0f, neckF - headF) * 350f;
                score += MathF.Max(0f, spineF - neckF) * 250f;
                score += MathF.Max(0f, tailF - spineF) * 350f;
                score += MathF.Abs(headF - 0.78f) * 25f;
                score += MathF.Abs(tailF - 0.08f) * 20f;
            }

            if (points.Length > 3 && points[3].Count > 0)
            {
                float headY = AverageNormalizedY(points[3], min, size);
                score += MathF.Abs(headY - 0.60f) * 30f;
            }

            return score;
        }

        private static float MissingPenalty(IReadOnlyList<Vector3>[] points, int index, float value)
            => index >= points.Length || points[index].Count == 0 ? value : 0f;

        private static Vector3 RobustRegionAverage(
            IReadOnlyList<Vector3> points,
            Vector3 min,
            Vector3 size,
            float minNormalizedY,
            float maxNormalizedY,
            Vector3 fallback)
        {
            if (points == null || points.Count == 0)
                return fallback;

            var filtered = new List<Vector3>(points.Count);
            foreach (Vector3 p in points)
            {
                float y = (p.Y - min.Y) / MathF.Max(0.001f, size.Y);
                if (y >= minNormalizedY && y <= maxNormalizedY)
                    filtered.Add(p);
            }

            return filtered.Count > 0 ? AveragePoints(filtered) : AveragePoints(points);
        }

        private static Vector3 FindQuadrupedTailTarget(
            IReadOnlyList<Vector3> tailPoints,
            Vector3 min,
            Vector3 size,
            int forwardAxis,
            bool headAtHighForward,
            Vector3 fallback)
        {
            if (tailPoints == null || tailPoints.Count == 0)
                return fallback;

            Vector3 tip = tailPoints[0];
            float best = NormalizedForward(tip, min, size, forwardAxis, headAtHighForward);
            foreach (Vector3 p in tailPoints)
            {
                float forward = NormalizedForward(p, min, size, forwardAxis, headAtHighForward);
                if (forward < best)
                {
                    best = forward;
                    tip = p;
                }
            }

            return tip;
        }

        private static void FindQuadrupedTailChainTargets(
            IReadOnlyList<Vector3> tailPoints,
            Vector3 min,
            Vector3 size,
            int forwardAxis,
            bool headAtHighForward,
            out Vector3 tailBase,
            out Vector3 tailMid,
            out Vector3 tailTip)
        {
            if (tailPoints == null || tailPoints.Count == 0)
            {
                tailBase = tailMid = tailTip = Vector3.Zero;
                return;
            }

            var ordered = tailPoints
                .Select(p => (Point: p, Forward: NormalizedForward(p, min, size, forwardAxis, headAtHighForward)))
                .OrderBy(x => x.Forward)
                .ToArray();

            tailTip = AverageQuantile(ordered, 0.00f, 0.16f);
            tailMid = AverageQuantile(ordered, 0.38f, 0.62f);
            tailBase = AverageQuantile(ordered, 0.78f, 1.00f);
        }

        private static Vector3 AverageQuantile((Vector3 Point, float Forward)[] ordered, float start, float end)
        {
            if (ordered == null || ordered.Length == 0)
                return Vector3.Zero;
            int a = Math.Clamp((int)MathF.Floor(start * ordered.Length), 0, ordered.Length - 1);
            int b = Math.Clamp((int)MathF.Ceiling(end * ordered.Length), a + 1, ordered.Length);
            Vector3 sum = Vector3.Zero;
            int count = 0;
            for (int i = a; i < b; i++)
            {
                sum += ordered[i].Point;
                count++;
            }
            return sum / Math.Max(1, count);
        }

        private static float AverageNormalizedY(IReadOnlyList<Vector3> points, Vector3 min, Vector3 size)
        {
            float sum = 0f;
            foreach (Vector3 p in points)
                sum += (p.Y - min.Y) / MathF.Max(0.001f, size.Y);
            return sum / Math.Max(1, points.Count);
        }

        private static float NormalizedForward(Vector3 position, Vector3 min, Vector3 size, int forwardAxis, bool headAtHighForward)
        {
            float raw = GetAxisComponent(position - min, forwardAxis) / MathF.Max(0.001f, GetAxisComponent(size, forwardAxis));
            raw = Math.Clamp(raw, 0f, 1f);
            return headAtHighForward ? raw : 1f - raw;
        }

        private static bool InferQuadrupedHeadAtHighForward(GModelAsset asset, Vector3 min, Vector3 size, int forwardAxis, int lateralAxis)
        {
            if (asset?.Rig?.Bones != null && asset.Rig.Bones.Count > 0)
            {
                int headIndex = asset.Rig.Bones.FindIndex(b => b.Name.Contains("Head", StringComparison.OrdinalIgnoreCase));
                int tailIndex = asset.Rig.Bones.FindIndex(b => b.Name.Contains("Tail", StringComparison.OrdinalIgnoreCase));
                if (headIndex >= 0 && tailIndex >= 0)
                {
                    Matrix4x4[] world = ComputeWorldTransforms(asset.Rig.Bones, asset.Rig.Bones.Select(b => b.BindLocal).ToArray());
                    float head = GetAxisComponent(world[headIndex].Translation - min, forwardAxis);
                    float tail = GetAxisComponent(world[tailIndex].Translation - min, forwardAxis);
                    if (MathF.Abs(head - tail) > 0.0001f)
                        return head > tail;
                }
            }

            QuadrupedFitResult highForwardHead = BuildQuadrupedFit(asset, min, size, forwardAxis, lateralAxis, headAtHighForward: true);
            QuadrupedFitResult lowForwardHead = BuildQuadrupedFit(asset, min, size, forwardAxis, lateralAxis, headAtHighForward: false);
            return highForwardHead.Score <= lowForwardHead.Score;
        }

        private static Vector3 RefineBoneCentroid(
            GModelAsset asset,
            Vector3 min,
            Vector3 size,
            int forwardAxis,
            int lateralAxis,
            bool headAtHighForward,
            int boneIndex,
            float maxNormalizedY,
            Vector3 fallback)
        {
            var points = new List<Vector3>();
            foreach (GModelMesh mesh in asset.Meshes)
            {
                foreach (Vector3 position in EnumerateMeshPositions(mesh))
                {
                    Vector3 n = (position - min) / size;
                    if (n.Y > maxNormalizedY) continue;
                    if (ClassifyQuadrupedVertex(n, forwardAxis, lateralAxis, headAtHighForward) == boneIndex)
                        points.Add(position);
                }
            }

            return points.Count > 0 ? AveragePoints(points) : fallback;
        }

        private static Vector3 RefineTailBase(GModelAsset asset, Vector3 min, Vector3 size, int forwardAxis, int lateralAxis, bool headAtHighForward, Vector3 fallback)
        {
            var basePoints = new List<Vector3>();
            foreach (GModelMesh mesh in asset.Meshes)
            {
                foreach (Vector3 position in EnumerateMeshPositions(mesh))
                {
                    Vector3 n = (position - min) / size;
                    if (ClassifyQuadrupedVertex(n, forwardAxis, lateralAxis, headAtHighForward) != 4)
                        continue;
                    float forward = headAtHighForward ? GetAxisComponent(n, forwardAxis) : 1f - GetAxisComponent(n, forwardAxis);
                    if (forward > 0.18f && forward < 0.42f)
                        basePoints.Add(position);
                }
            }

            return basePoints.Count > 0 ? AveragePoints(basePoints) : fallback;
        }

        private static void FitHumanoidRigToMesh(GModelAsset asset)
        {
            GModelBounds bounds = asset.Bounds ?? new GModelBounds();
            Vector3 min = bounds.Min;
            Vector3 size = bounds.Size;
            size = new Vector3(Math.Max(size.X, 0.001f), Math.Max(size.Y, 0.001f), Math.Max(size.Z, 0.001f));

            int boneCount = asset.Rig.Bones.Count;
            var sums = new Vector3[boneCount];
            var counts = new int[boneCount];

            foreach (GModelMesh mesh in asset.Meshes)
            {
                foreach (Vector3 position in EnumerateMeshPositions(mesh))
                {
                    Vector3 n = (position - min) / size;
                    int primary = ClassifyHumanoidVertex(n);
                    if (primary < 0 || primary >= boneCount) continue;
                    sums[primary] += position;
                    counts[primary]++;
                }
            }

            Matrix4x4[] fallbackWorld = ComputeWorldTransforms(asset.Rig.Bones, asset.Rig.Bones.Select(b => b.BindLocal).ToArray());
            var targets = new Vector3[boneCount];
            for (int i = 0; i < boneCount; i++)
                targets[i] = counts[i] > 0 ? sums[i] / counts[i] : fallbackWorld[i].Translation;

            for (int leg = 5; leg <= 6 && leg < boneCount; leg++)
            {
                if (counts[leg] <= 0) continue;
                Vector3 foot = sums[leg] / counts[leg];
                targets[leg] = new Vector3(foot.X, targets[0].Y, foot.Z);
            }

            ApplyWorldTargetsToBindPose(asset.Rig, targets);
        }

        private static int ClassifyHumanoidVertex(Vector3 n)
        {
            if (n.Y < 0.42f) return n.X < 0.5f ? 5 : 6;
            if (n.Y > 0.72f) return 2;
            if (n.Y > 0.52f && (n.X < 0.28f || n.X > 0.72f)) return n.X < 0.5f ? 3 : 4;
            if (n.Y > 0.45f) return 1;
            return 0;
        }

        private static void ApplyWorldTargetsToBindPose(GModelRig rig, Vector3[] targetWorld)
        {
            if (rig?.Bones == null || targetWorld == null) return;
            int count = rig.Bones.Count;
            var world = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                int parent = rig.Bones[i].ParentIndex;
                Vector3 parentTranslation = parent >= 0 && parent < i ? world[parent].Translation : Vector3.Zero;
                Vector3 localTranslation = i < targetWorld.Length ? targetWorld[i] - parentTranslation : Vector3.Zero;
                rig.Bones[i].BindLocal = Matrix4x4.CreateTranslation(localTranslation);
                world[i] = parent >= 0 && parent < i ? rig.Bones[i].BindLocal * world[parent] : rig.Bones[i].BindLocal;
            }
        }

        private static IEnumerable<Vector3> EnumerateMeshPositions(GModelMesh mesh)
        {
            if (mesh?.Vertices != null)
            {
                foreach (MeshVertex v in mesh.Vertices)
                    yield return v.Position;
                yield break;
            }

            if (mesh?.SkinnedVertices != null)
            {
                foreach (SkinnedMeshVertex v in mesh.SkinnedVertices)
                    yield return v.Position;
            }
        }

        private static Vector3 AveragePoints(IReadOnlyList<Vector3> points)
        {
            Vector3 sum = Vector3.Zero;
            foreach (Vector3 p in points)
                sum += p;
            return sum / points.Count;
        }

        private static int ClassifyQuadrupedVertex(Vector3 n, int forwardAxis, int lateralAxis)
            => ClassifyQuadrupedVertex(n, forwardAxis, lateralAxis, headAtHighForward: true);

        private static int ClassifyQuadrupedVertex(Vector3 n, int forwardAxis, int lateralAxis, bool headAtHighForward)
        {
            float forward = GetAxisComponent(n, forwardAxis);
            if (!headAtHighForward)
                forward = 1f - forward;
            float lateral = GetAxisComponent(n, lateralAxis);

            if (n.Y < 0.45f)
            {
                if (lateral < 0.46f && forward > 0.40f) return 5; // FrontLeg.L
                if (lateral > 0.54f && forward > 0.40f) return 6; // FrontLeg.R
                if (lateral < 0.46f && forward < 0.60f) return 7; // RearLeg.L
                if (lateral > 0.54f && forward < 0.60f) return 8; // RearLeg.R
            }

            if (forward > 0.62f && n.Y > 0.38f) return 3; // Head
            if (forward > 0.52f && n.Y > 0.32f) return 2; // Neck
            if (forward < 0.30f) return 4; // Tail
            if (n.Y > 0.40f) return 1; // Spine
            return 0; // Root
        }

        private static void ResolveBodyAxes(Vector3 size, out int forwardAxis, out int lateralAxis)
        {
            if (size.X > size.Z)
            {
                forwardAxis = 0;
                lateralAxis = 2;
            }
            else
            {
                forwardAxis = 2;
                lateralAxis = 0;
            }
        }

        private static float GetAxisComponent(Vector3 n, int axis) => axis switch
        {
            0 => n.X,
            1 => n.Y,
            _ => n.Z,
        };

        private static Vector3 AxisVector(int axis, float value) => axis switch
        {
            0 => new Vector3(value, 0f, 0f),
            1 => new Vector3(0f, value, 0f),
            _ => new Vector3(0f, 0f, value),
        };

        private static void BuildQuadrupedRig(GModelAsset asset, string species)
        {
            GModelBounds bounds = asset.Bounds ?? new GModelBounds();
            Vector3 size = bounds.Size;
            if (size.LengthSquared() < 0.0001f) size = Vector3.One;
            Vector3 center = bounds.Center;
            float height = Math.Max(0.3f, size.Y);
            ResolveBodyAxes(size, out int forwardAxis, out int lateralAxis);
            float length = Math.Max(0.5f, GetAxisComponent(size, forwardAxis));
            float width = Math.Max(0.25f, GetAxisComponent(size, lateralAxis));
            float leg = height * 0.45f;
            float rootY = bounds.Min.Y + leg;

            float snout = species.Equals("Fox", StringComparison.OrdinalIgnoreCase) ? 0.38f
                : species.Equals("Wolf", StringComparison.OrdinalIgnoreCase) ? 0.34f
                : species.Equals("Cat", StringComparison.OrdinalIgnoreCase) ? 0.28f
                : 0.30f;
            float tailBack = species.Equals("Fox", StringComparison.OrdinalIgnoreCase) ? 0.48f
                : species.Equals("Cat", StringComparison.OrdinalIgnoreCase) ? 0.36f
                : 0.42f;
            float bodyWidth = species.Equals("Dog", StringComparison.OrdinalIgnoreCase) ? 0.26f : 0.22f;

            SampleLegAnchors(asset, bounds, forwardAxis, lateralAxis, leg,
                out Vector3 frontL, out Vector3 frontR, out Vector3 rearL, out Vector3 rearR);

            asset.Rig.TemplateName = GModelRigTemplates.ComposeId("Quadruped", species);
            asset.Rig.Bones.Clear();
            asset.Rig.Bones.Add(new GModelBone { Name = "Root", ParentIndex = -1, BindLocal = Matrix4x4.CreateTranslation(center.X, rootY, center.Z) });

            Vector3 spineOffset = new(0f, height * 0.16f, 0f);
            asset.Rig.Bones.Add(new GModelBone { Name = "Spine", ParentIndex = 0, BindLocal = Matrix4x4.CreateTranslation(spineOffset) });

            Vector3 neckOffset = new Vector3(0f, height * 0.10f, 0f) + AxisVector(forwardAxis, length * snout * 0.35f);
            asset.Rig.Bones.Add(new GModelBone { Name = "Neck", ParentIndex = 1, BindLocal = Matrix4x4.CreateTranslation(neckOffset) });

            Vector3 headOffset = new Vector3(0f, height * 0.12f, 0f) + AxisVector(forwardAxis, length * snout * 0.45f);
            asset.Rig.Bones.Add(new GModelBone { Name = "Head", ParentIndex = 2, BindLocal = Matrix4x4.CreateTranslation(headOffset) });

            Vector3 tailOffset = new Vector3(0f, height * 0.06f, 0f) + AxisVector(forwardAxis, -length * tailBack);
            asset.Rig.Bones.Add(new GModelBone { Name = "Tail", ParentIndex = 1, BindLocal = Matrix4x4.CreateTranslation(tailOffset) });

            asset.Rig.Bones.Add(CreateLegBone("FrontLeg.L", 0, frontL, center, leg));
            asset.Rig.Bones.Add(CreateLegBone("FrontLeg.R", 0, frontR, center, leg));
            asset.Rig.Bones.Add(CreateLegBone("RearLeg.L", 1, rearL, center));
            asset.Rig.Bones.Add(CreateLegBone("RearLeg.R", 1, rearR, center));

            Vector3 tailMidOffset = new Vector3(0f, height * 0.08f, 0f) + AxisVector(forwardAxis, -length * tailBack * 0.33f);
            asset.Rig.Bones.Add(new GModelBone { Name = "TailMid", ParentIndex = 4, BindLocal = Matrix4x4.CreateTranslation(tailMidOffset) });

            Vector3 tailTipOffset = new Vector3(0f, height * 0.08f, 0f) + AxisVector(forwardAxis, -length * tailBack * 0.33f);
            asset.Rig.Bones.Add(new GModelBone { Name = "TailTip", ParentIndex = 9, BindLocal = Matrix4x4.CreateTranslation(tailTipOffset) });
        }

        private static GModelBone CreateLegBone(string name, int parentIndex, Vector3 anchorWorld, Vector3 rootCenter, float legDrop = 0f)
        {
            Vector3 local = new(anchorWorld.X - rootCenter.X, parentIndex == 0 ? -legDrop : 0f, anchorWorld.Z - rootCenter.Z);
            return new GModelBone
            {
                Name = name,
                ParentIndex = parentIndex,
                BindLocal = Matrix4x4.CreateTranslation(local),
            };
        }

        private static void SampleLegAnchors(
            GModelAsset asset,
            GModelBounds bounds,
            int forwardAxis,
            int lateralAxis,
            float legHeight,
            out Vector3 frontL,
            out Vector3 frontR,
            out Vector3 rearL,
            out Vector3 rearR)
        {
            float length = Math.Max(0.5f, GetAxisComponent(bounds.Size, forwardAxis));
            float width = Math.Max(0.25f, GetAxisComponent(bounds.Size, lateralAxis));
            float bodyWidth = width * 0.22f;
            float rootY = bounds.Min.Y + legHeight;

            frontL = BuildDefaultFoot(bounds.Center, rootY, forwardAxis, lateralAxis, -bodyWidth, length * 0.22f);
            frontR = BuildDefaultFoot(bounds.Center, rootY, forwardAxis, lateralAxis, bodyWidth, length * 0.22f);
            rearL = BuildDefaultFoot(bounds.Center, rootY, forwardAxis, lateralAxis, -bodyWidth, -length * 0.22f);
            rearR = BuildDefaultFoot(bounds.Center, rootY, forwardAxis, lateralAxis, bodyWidth, -length * 0.22f);

            if (asset.Meshes == null || asset.Meshes.Count == 0)
                return;

            var samples = new List<Vector3>[4];
            for (int i = 0; i < 4; i++) samples[i] = new List<Vector3>();
            float footY = bounds.Min.Y + bounds.Size.Y * 0.18f;

            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh.Vertices == null) continue;
                foreach (MeshVertex v in mesh.Vertices)
                {
                    if (v.Position.Y > footY)
                        continue;

                    Vector3 n = (v.Position - bounds.Min) / bounds.Size;
                    float forward = GetAxisComponent(n, forwardAxis);
                    float lateral = GetAxisComponent(n, lateralAxis);
                    int bucket = forward > 0.5f
                        ? (lateral < 0.5f ? 0 : 1)
                        : (lateral < 0.5f ? 2 : 3);
                    samples[bucket].Add(v.Position);
                }
            }

            frontL = AverageOrDefault(samples[0], frontL);
            frontR = AverageOrDefault(samples[1], frontR);
            rearL = AverageOrDefault(samples[2], rearL);
            rearR = AverageOrDefault(samples[3], rearR);
        }

        private static Vector3 BuildDefaultFoot(Vector3 center, float rootY, int forwardAxis, int lateralAxis, float lateralOffset, float forwardOffset)
        {
            Vector3 foot = new(center.X, rootY, center.Z);
            foot += AxisVector(lateralAxis, lateralOffset);
            foot += AxisVector(forwardAxis, forwardOffset);
            foot.Y = rootY - 0.01f;
            return foot;
        }

        private static Vector3 AverageOrDefault(List<Vector3> points, Vector3 fallback)
        {
            if (points == null || points.Count == 0)
                return fallback;
            Vector3 sum = Vector3.Zero;
            foreach (Vector3 p in points)
                sum += p;
            return sum / points.Count;
        }

        private static void BuildHumanoidRig(GModelAsset asset, string species)
        {
            GModelBounds bounds = asset.Bounds ?? new GModelBounds();
            Vector3 size = bounds.Size;
            if (size.LengthSquared() < 0.0001f) size = Vector3.One;
            Vector3 center = bounds.Center;
            float height = Math.Max(0.5f, size.Y);
            float width = Math.Max(0.25f, size.X);

            asset.Rig.TemplateName = GModelRigTemplates.ComposeId("Humanoid", species);
            asset.Rig.Bones.Clear();
            asset.Rig.Bones.Add(new GModelBone { Name = "Hips", ParentIndex = -1, BindLocal = Matrix4x4.CreateTranslation(center.X, bounds.Min.Y + height * 0.48f, center.Z) });
            asset.Rig.Bones.Add(new GModelBone { Name = "Spine", ParentIndex = 0, BindLocal = Matrix4x4.CreateTranslation(0f, height * 0.18f, 0f) });
            asset.Rig.Bones.Add(new GModelBone { Name = "Head", ParentIndex = 1, BindLocal = Matrix4x4.CreateTranslation(0f, height * 0.28f, 0f) });
            asset.Rig.Bones.Add(new GModelBone { Name = "Arm.L", ParentIndex = 1, BindLocal = Matrix4x4.CreateTranslation(-width * 0.42f, height * 0.08f, 0f) });
            asset.Rig.Bones.Add(new GModelBone { Name = "Arm.R", ParentIndex = 1, BindLocal = Matrix4x4.CreateTranslation(width * 0.42f, height * 0.08f, 0f) });
            asset.Rig.Bones.Add(new GModelBone { Name = "Leg.L", ParentIndex = 0, BindLocal = Matrix4x4.CreateTranslation(-width * 0.16f, -height * 0.48f, 0f) });
            asset.Rig.Bones.Add(new GModelBone { Name = "Leg.R", ParentIndex = 0, BindLocal = Matrix4x4.CreateTranslation(width * 0.16f, -height * 0.48f, 0f) });
        }

        private static int ClipFrameCount(string clipName) => clipName switch
        {
            "Jump" => 48,
            "Fall" => 36,
            "Sit Down" => 42,
            "Stand Up" => 42,
            "Attack" => 36,
            "Die" => 72,
            "Crawl" => 48,
            _ => 60,
        };

        private static GModelAnimationClip CreateBuiltInClip(string name, GModelRig rig, int frameCount, float fps)
        {
            var clip = new GModelAnimationClip { Name = name, Fps = fps, Loop = name != "Die" };
            int boneCount = Math.Max(1, rig?.Bones?.Count ?? 1);
            bool quadruped = boneCount >= 9;

            for (int frame = 0; frame < frameCount; frame++)
            {
                float t = frame / MathF.Max(1f, frameCount - 1f);
                Matrix4x4[] bones = BindPoseArray(rig, boneCount);
                if (quadruped)
                    ApplyQuadrupedClip(name, bones, t, frame, frameCount);
                else if (boneCount >= 6)
                    ApplyHumanoidClip(name, bones, t, frame, frameCount);
                clip.Frames.Add(new GModelAnimationFrame { LocalBoneTransforms = bones });
            }

            // Store editable TRS keys and then bake runtime matrices from those keys. This keeps
            // runtime compatibility while ensuring Model Studio authoring never has to interpolate
            // raw Matrix4x4 values.
            clip.Tracks = BuildTrsTracksFromFrames(clip.Frames, boneCount);
            clip.Frames = BakeFramesFromTrsTracks(clip.Tracks, rig, frameCount);
            return clip;
        }

        public static Matrix4x4 InterpolateTrs(GModelTrsKey a, GModelTrsKey b, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            Vector3 translation = Vector3.Lerp(a?.Translation ?? Vector3.Zero, b?.Translation ?? Vector3.Zero, amount);
            Vector3 scale = Vector3.Lerp(a?.Scale ?? Vector3.One, b?.Scale ?? Vector3.One, amount);
            Quaternion rotation = SlerpShortest(a?.Rotation ?? Quaternion.Identity, b?.Rotation ?? Quaternion.Identity, amount);
            return ComposeTrs(translation, rotation, scale);
        }

        private static List<GModelAnimationTrack> BuildTrsTracksFromFrames(IReadOnlyList<GModelAnimationFrame> frames, int boneCount)
        {
            var tracks = new List<GModelAnimationTrack>(boneCount);
            for (int bone = 0; bone < boneCount; bone++)
            {
                var track = new GModelAnimationTrack { BoneIndex = bone };
                for (int frame = 0; frame < (frames?.Count ?? 0); frame++)
                {
                    Matrix4x4 local = frames[frame].LocalBoneTransforms != null && bone < frames[frame].LocalBoneTransforms.Length
                        ? frames[frame].LocalBoneTransforms[bone]
                        : Matrix4x4.Identity;
                    track.Keys.Add(DecomposeTrsKey(frame, local));
                }
                tracks.Add(track);
            }
            return tracks;
        }

        private static List<GModelAnimationFrame> BakeFramesFromTrsTracks(IReadOnlyList<GModelAnimationTrack> tracks, GModelRig rig, int frameCount)
        {
            int boneCount = Math.Max(1, rig?.Bones?.Count ?? 1);
            var frames = new List<GModelAnimationFrame>(frameCount);
            for (int frame = 0; frame < frameCount; frame++)
            {
                var locals = BindPoseArray(rig, boneCount);
                if (tracks != null)
                {
                    foreach (GModelAnimationTrack track in tracks)
                    {
                        if (track == null || track.BoneIndex < 0 || track.BoneIndex >= locals.Length)
                            continue;
                        locals[track.BoneIndex] = EvaluateTrack(track, frame);
                    }
                }
                frames.Add(new GModelAnimationFrame { LocalBoneTransforms = locals });
            }
            return frames;
        }

        private static Matrix4x4 EvaluateTrack(GModelAnimationTrack track, int frame)
        {
            if (track?.Keys == null || track.Keys.Count == 0)
                return Matrix4x4.Identity;
            GModelTrsKey previous = track.Keys[0];
            GModelTrsKey next = track.Keys[^1];
            foreach (GModelTrsKey key in track.Keys)
            {
                if (key.Frame <= frame)
                    previous = key;
                if (key.Frame >= frame)
                {
                    next = key;
                    break;
                }
            }
            if (previous.Frame == next.Frame)
                return ComposeTrs(previous.Translation, previous.Rotation, previous.Scale);
            float t = (frame - previous.Frame) / (float)Math.Max(1, next.Frame - previous.Frame);
            return InterpolateTrs(previous, next, t);
        }

        private static GModelTrsKey DecomposeTrsKey(int frame, Matrix4x4 matrix)
        {
            if (!Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            {
                scale = Vector3.One;
                rotation = Quaternion.Identity;
                translation = matrix.Translation;
            }
            if (rotation.LengthSquared() > 0.0001f)
                rotation = Quaternion.Normalize(rotation);
            else
                rotation = Quaternion.Identity;
            return new GModelTrsKey
            {
                Frame = frame,
                Translation = translation,
                Rotation = rotation,
                Scale = new Vector3(
                    MathF.Abs(scale.X) < 0.0001f ? 1f : scale.X,
                    MathF.Abs(scale.Y) < 0.0001f ? 1f : scale.Y,
                    MathF.Abs(scale.Z) < 0.0001f ? 1f : scale.Z),
            };
        }

        private static Matrix4x4 ComposeTrs(Vector3 translation, Quaternion rotation, Vector3 scale)
            => Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation))
             * Matrix4x4.CreateTranslation(translation);

        private static Quaternion SlerpShortest(Quaternion a, Quaternion b, float amount)
        {
            if (Quaternion.Dot(a, b) < 0f)
                b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
            return Quaternion.Normalize(Quaternion.Slerp(a, b, amount));
        }

        private static void ApplyQuadrupedClip(string name, Matrix4x4[] bones, float t, int frame, int frameCount)
        {
            float wave = MathF.Sin(t * MathF.Tau);
            float fast = MathF.Sin(t * MathF.Tau * 2f);
            float phase = MathF.Sin((t + 0.25f) * MathF.Tau);

            switch (name)
            {
                case "Idle":
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(wave * 0.03f));
                    ApplyLocalRotation(bones, 2, Matrix4x4.CreateRotationX(wave * 0.04f));
                    ApplyLocalRotation(bones, 3, Matrix4x4.CreateRotationX(wave * 0.05f));
                    break;

                case "Walk":
                    ApplyLegCycle(bones, wave, 0.20f, 5);
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, MathF.Abs(wave) * 0.01f, 0f));
                    break;

                case "Run":
                    ApplyLegCycle(bones, wave, 0.42f, 5);
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(fast * 0.08f));
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, MathF.Abs(fast) * 0.03f, 0f));
                    if (phase > 0.2f)
                    {
                        ApplyLocalRotation(bones, 7, Matrix4x4.CreateRotationX(phase * 0.35f));
                        ApplyLocalRotation(bones, 8, Matrix4x4.CreateRotationX(-phase * 0.35f));
                    }
                    break;

                case "Jump":
                    float crouch = SmoothStep(0f, 0.25f, t);
                    float launch = SmoothStep(0.25f, 0.55f, t);
                    float apex = SmoothStep(0.55f, 1f, t);
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, launch * 0.35f - crouch * 0.08f, launch * 0.12f));
                    ApplyLocalRotation(bones, 5, Matrix4x4.CreateRotationX(crouch * 0.45f - launch * 0.25f));
                    ApplyLocalRotation(bones, 6, Matrix4x4.CreateRotationX(crouch * 0.45f - launch * 0.25f));
                    ApplyLocalRotation(bones, 7, Matrix4x4.CreateRotationX(crouch * 0.55f - apex * 0.20f));
                    ApplyLocalRotation(bones, 8, Matrix4x4.CreateRotationX(crouch * 0.55f - apex * 0.20f));
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(-launch * 0.12f));
                    ApplyLocalRotation(bones, 3, Matrix4x4.CreateRotationX(-launch * 0.08f));
                    break;

                case "Fall":
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, 0.18f + wave * 0.02f, 0f));
                    ApplyLocalRotation(bones, 5, Matrix4x4.CreateRotationX(0.25f + wave * 0.05f));
                    ApplyLocalRotation(bones, 6, Matrix4x4.CreateRotationX(0.25f + wave * 0.05f));
                    ApplyLocalRotation(bones, 7, Matrix4x4.CreateRotationX(-0.35f));
                    ApplyLocalRotation(bones, 8, Matrix4x4.CreateRotationX(-0.35f));
                    break;

                case "Sit Down":
                    float sit = SmoothStep(0f, 1f, t);
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, -sit * 0.28f, -sit * 0.06f));
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(sit * 0.22f));
                    ApplyLocalRotation(bones, 7, Matrix4x4.CreateRotationX(sit * 0.85f));
                    ApplyLocalRotation(bones, 8, Matrix4x4.CreateRotationX(sit * 0.85f));
                    ApplyLocalRotation(bones, 5, Matrix4x4.CreateRotationX(sit * 0.18f));
                    ApplyLocalRotation(bones, 6, Matrix4x4.CreateRotationX(sit * 0.18f));
                    break;

                case "Stand Up":
                    float rise = 1f - SmoothStep(0f, 1f, t);
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, -rise * 0.28f, -rise * 0.06f));
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(rise * 0.22f));
                    ApplyLocalRotation(bones, 7, Matrix4x4.CreateRotationX(rise * 0.85f));
                    ApplyLocalRotation(bones, 8, Matrix4x4.CreateRotationX(rise * 0.85f));
                    ApplyLocalRotation(bones, 5, Matrix4x4.CreateRotationX(rise * 0.18f));
                    ApplyLocalRotation(bones, 6, Matrix4x4.CreateRotationX(rise * 0.18f));
                    break;

                case "Crawl":
                    float crawlWave = MathF.Sin(t * MathF.Tau * 1.5f);
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, -0.22f, crawlWave * 0.04f));
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(0.35f));
                    ApplyLegCycle(bones, crawlWave, 0.12f, 5);
                    ApplyLocalRotation(bones, 3, Matrix4x4.CreateRotationX(0.18f));
                    break;

                case "Attack":
                    float strike = SmoothStep(0.15f, 0.45f, t) * (1f - SmoothStep(0.55f, 0.85f, t));
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, strike * 0.06f, strike * 0.10f));
                    ApplyLocalRotation(bones, 3, Matrix4x4.CreateRotationX(-strike * 0.75f));
                    ApplyLocalRotation(bones, 2, Matrix4x4.CreateRotationX(-strike * 0.35f));
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(-strike * 0.18f));
                    break;

                case "Die":
                    float fall = SmoothStep(0.2f, 1f, t);
                    ApplyLocalTranslation(bones, 0, new Vector3(fall * 0.08f, -fall * 0.35f, 0f));
                    ApplyLocalRotation(bones, 0, Matrix4x4.CreateRotationZ(fall * 1.1f));
                    ApplyLocalRotation(bones, 1, Matrix4x4.CreateRotationX(fall * 0.35f));
                    ApplyLocalRotation(bones, 7, Matrix4x4.CreateRotationX(fall * 0.5f));
                    ApplyLocalRotation(bones, 8, Matrix4x4.CreateRotationX(fall * 0.5f));
                    break;
            }

            // Imported quadrupeds often have dense, fluffy tails without authored skin loops.
            // Keep the built-in clips conservative: tail bones are visible for posing, but the
            // default generated clips do not auto-wag them because that can read as deformation.
        }

        private static void ApplyHumanoidClip(string name, Matrix4x4[] bones, float t, int frame, int frameCount)
        {
            float wave = MathF.Sin(t * MathF.Tau);
            float fast = MathF.Sin(t * MathF.Tau * 2f);

            switch (name)
            {
                case "Walk":
                case "Run":
                case "Crawl":
                    // Stride angles in radians. These were 0.22 (≈12.6°) for Walk, which is far
                    // below a real gait (~30°) and rendered as a near-idle shuffle — the preset
                    // looked broken even though the rig and skinning were correct (NEXT-036).
                    float amount = name switch
                    {
                        "Run" => 0.85f,     // ≈49° — long, driving stride
                        "Crawl" => 0.35f,   // ≈20° — short, low reach
                        _ => 0.55f,         // Walk ≈31°
                    };
                    ApplyLocalRotation(bones, 5, Matrix4x4.CreateRotationX(wave * amount));
                    ApplyLocalRotation(bones, 6, Matrix4x4.CreateRotationX(-wave * amount));
                    // Arms counter-swing against the legs, as they do in a real gait.
                    ApplyLocalRotation(bones, 3, Matrix4x4.CreateRotationX(-wave * amount * 0.6f));
                    ApplyLocalRotation(bones, 4, Matrix4x4.CreateRotationX(wave * amount * 0.6f));
                    // Body rises and falls twice per stride cycle, which is what makes a walk
                    // read as weight transfer rather than legs sliding.
                    if (name != "Crawl")
                    {
                        ApplyLocalTranslation(bones, 0, new Vector3(0f, MathF.Abs(fast) * 0.06f, 0f));
                    }

                    break;
                case "Idle":
                    ApplyLocalRotation(bones, 2, Matrix4x4.CreateRotationX(wave * 0.03f));
                    break;
                case "Attack":
                    ApplyLocalRotation(bones, 2, Matrix4x4.CreateRotationX(-MathF.Max(0f, wave) * 0.35f));
                    ApplyLocalRotation(bones, 3, Matrix4x4.CreateRotationX(-MathF.Max(0f, fast) * 0.55f));
                    break;
                case "Jump":
                    ApplyLocalTranslation(bones, 0, new Vector3(0f, SmoothStep(0.2f, 0.7f, t) * 0.4f, 0f));
                    break;
                case "Die":
                    ApplyLocalRotation(bones, 0, Matrix4x4.CreateRotationZ(SmoothStep(0.3f, 1f, t) * 1.2f));
                    break;
            }
        }

        private static void ApplyLegCycle(Matrix4x4[] bones, float wave, float amount, int firstLegIndex = 4)
        {
            ApplyLocalRotation(bones, firstLegIndex, Matrix4x4.CreateRotationX(wave * amount));
            ApplyLocalRotation(bones, firstLegIndex + 1, Matrix4x4.CreateRotationX(-wave * amount));
            ApplyLocalRotation(bones, firstLegIndex + 2, Matrix4x4.CreateRotationX(-wave * amount));
            ApplyLocalRotation(bones, firstLegIndex + 3, Matrix4x4.CreateRotationX(wave * amount));
        }

        private sealed class BoneSegment
        {
            public int Index;
            public Vector3 Head;
            public Vector3 Tail;
        }

        private static List<BoneSegment> BuildBoneSegments(IReadOnlyList<GModelBone> bones, Matrix4x4[] bindWorld, GModelAsset asset = null)
        {
            var segments = new List<BoneSegment>(bones.Count);
            for (int i = 0; i < bones.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(asset?.Rig?.TemplateName))
                {
                    int parent = bones[i].ParentIndex;
                    segments.Add(new BoneSegment { Index = i, Head = parent >= 0 && parent < bindWorld.Length ? bindWorld[parent].Translation : bindWorld[i].Translation,
                        Tail = bindWorld[i].Translation });
                    continue;
                }
                Vector3 head = bindWorld[i].Translation;
                Vector3 tail = head;
                bool hasChild = false;
                for (int c = 0; c < bones.Count; c++)
                {
                    if (bones[c].ParentIndex == i)
                    {
                        tail = bindWorld[c].Translation;
                        hasChild = true;
                        break;
                    }
                }

                if (!hasChild)
                    tail = SampleLeafBoneTip(asset, bones[i].Name, i, head, bindWorld);

                if ((tail - head).LengthSquared() < 1e-6f)
                    tail = head + Vector3.TransformNormal(new Vector3(0f, -0.15f, 0f), bindWorld[i]);

                segments.Add(new BoneSegment { Index = i, Head = head, Tail = tail });
            }
            return segments;
        }

        private static Vector3 SampleLeafBoneTip(GModelAsset asset, string boneName, int boneIndex, Vector3 head, Matrix4x4[] bindWorld)
        {
            if (asset?.Meshes == null || asset.Bounds == null)
                return head + Vector3.TransformNormal(new Vector3(0f, -0.15f, 0f), bindWorld[boneIndex]);

            GModelBounds bounds = asset.Bounds;
            Vector3 min = bounds.Min;
            Vector3 size = bounds.Size;
            ResolveBodyAxes(size, out int forwardAxis, out int lateralAxis);
            int lateralAxisResolved = lateralAxis;
            bool headAtHighForward = InferQuadrupedHeadAtHighForward(asset, min, size, forwardAxis, lateralAxisResolved);

            var points = new List<Vector3>();
            foreach (GModelMesh mesh in asset.Meshes)
            {
                foreach (Vector3 position in EnumerateMeshPositions(mesh))
                {
                    Vector3 n = (position - min) / size;
                    int region = ClassifyQuadrupedVertex(n, forwardAxis, lateralAxisResolved, headAtHighForward);
                    if (boneName.Contains("Leg", StringComparison.OrdinalIgnoreCase))
                    {
                        if (region == boneIndex)
                            points.Add(position);
                    }
                    else if (boneName.Contains("Tail", StringComparison.OrdinalIgnoreCase))
                    {
                        if (region == 4 || region == boneIndex)
                            points.Add(position);
                    }
                    else if (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase))
                    {
                        if (region == boneIndex)
                            points.Add(position);
                    }
                }
            }

            if (points.Count == 0)
                return head + Vector3.TransformNormal(new Vector3(0f, -0.15f, 0f), bindWorld[boneIndex]);

            if (boneName.Contains("Tail", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 tip = points[0];
                float best = NormalizedForward(tip, min, size, forwardAxis, headAtHighForward);
                foreach (Vector3 p in points)
                {
                    float forward = NormalizedForward(p, min, size, forwardAxis, headAtHighForward);
                    if (forward < best)
                    {
                        best = forward;
                        tip = p;
                    }
                }
                return tip;
            }

            if (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase))
            {
                Vector3 tip = points[0];
                float best = NormalizedForward(tip, min, size, forwardAxis, headAtHighForward);
                foreach (Vector3 p in points)
                {
                    float forward = NormalizedForward(p, min, size, forwardAxis, headAtHighForward);
                    if (forward > best)
                    {
                        best = forward;
                        tip = p;
                    }
                }
                return tip;
            }

            return AveragePoints(points);
        }

        private static Vector4 SegmentBoneWeights(Vector3 position, List<BoneSegment> segments, int maxInfluences,
            Func<Vector3, HashSet<int>> regionFilter, out Vector4 jointIndices)
        {
            if (segments == null || segments.Count == 0)
            {
                jointIndices = Vector4.Zero;
                return new Vector4(1f, 0f, 0f, 0f);
            }

            HashSet<int> allowed = regionFilter?.Invoke(position);
            var ranked = segments
                .Where(s => allowed == null || allowed.Contains(s.Index))
                .Select(s => new { s.Index, Distance = DistanceToSegment(position, s.Head, s.Tail) })
                .OrderBy(x => x.Distance)
                .Take(Math.Clamp(maxInfluences, 1, 4))
                .ToArray();
            if (ranked.Length == 0)
            {
                ranked = segments
                    .Select(s => new { s.Index, Distance = DistanceToSegment(position, s.Head, s.Tail) })
                    .OrderBy(x => x.Distance)
                    .Take(Math.Clamp(maxInfluences, 1, 4))
                    .ToArray();
            }

            float total = ranked.Sum(x => 1f / MathF.Max(0.0001f, x.Distance));
            float W(int slot) => slot < ranked.Length ? (1f / MathF.Max(0.0001f, ranked[slot].Distance)) / total : 0f;
            float I(int slot) => slot < ranked.Length ? ranked[slot].Index : 0f;
            jointIndices = new Vector4(I(0), I(1), I(2), I(3));
            Vector4 weights = new(W(0), W(1), W(2), W(3));
            NormalizeWeights(ref weights);
            return weights;
        }

        private static Func<Vector3, HashSet<int>> CreateBoneRegionFilter(GModelAsset asset)
        {
            if (asset?.Rig?.Bones == null || asset.Rig.Bones.Count == 0 || asset.Bounds == null)
                return null;

            int count = asset.Rig.Bones.Count;
            Vector3 size = new(
                MathF.Max(0.001f, asset.Bounds.Size.X),
                MathF.Max(0.001f, asset.Bounds.Size.Y),
                MathF.Max(0.001f, asset.Bounds.Size.Z));
            if (asset.Rig.TemplateName?.StartsWith("Quadruped", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (asset.SkinBindingMode == GModelSkinBindingMode.Smooth4)
                    return null;

                ResolveBodyAxes(size, out int forwardAxis, out int lateralAxis);
                bool headAtHighForward = InferQuadrupedHeadAtHighForward(asset, asset.Bounds.Min, size, forwardAxis, lateralAxis);
                // Determine template orientation once per bind, not once per vertex.
                return position => ClassifyQuadrupedVertex((position - asset.Bounds.Min) / size, forwardAxis, lateralAxis, headAtHighForward) switch
                {
                    3 => Existing(count, 3, 2),          // head stays on head/neck
                    2 => Existing(count, 2, 1, 3),       // neck blends only spine/head
                    4 => Existing(count, 4),             // imported fluffy tails stay rigid; tail chain is visual/pose guidance for now
                    5 => Existing(count, 5),             // legs stay rigid by default; animation rotates the limb bone
                    6 => Existing(count, 6),
                    7 => Existing(count, 7),
                    8 => Existing(count, 8),
                    1 => Existing(count, 1, 0, 2, 4),
                    _ => Existing(count, 0, 1),
                };
            }

            if (asset.Rig.TemplateName?.StartsWith("Humanoid", StringComparison.OrdinalIgnoreCase) == true)
            {
                return position => ClassifyHumanoidVertex((position - asset.Bounds.Min) / size) switch
                {
                    2 => Existing(count, 2, 1),
                    3 => Existing(count, 3, 1),
                    4 => Existing(count, 4, 1),
                    5 => Existing(count, 5, 0),
                    6 => Existing(count, 6, 0),
                    1 => Existing(count, 1, 0, 2, 3, 4),
                    _ => Existing(count, 0, 1),
                };
            }

            return null;
        }

        private static HashSet<int> Existing(int boneCount, params int[] indices)
        {
            var set = new HashSet<int>();
            foreach (int index in indices)
                if (index >= 0 && index < boneCount)
                    set.Add(index);
            return set.Count == 0 ? null : set;
        }

        private static void NormalizeWeights(ref Vector4 weights)
        {
            float sum = weights.X + weights.Y + weights.Z + weights.W;
            if (sum <= 0.0001f)
            {
                weights = new Vector4(1f, 0f, 0f, 0f);
                return;
            }
            weights /= sum;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 head, Vector3 tail)
        {
            Vector3 ab = tail - head;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return Vector3.Distance(point, head);
            float u = Math.Clamp(Vector3.Dot(point - head, ab) / len2, 0f, 1f);
            return Vector3.Distance(point, head + ab * u);
        }

        private static float EstimateMaxAnimatedDisplacement(GModelAsset asset)
        {
            if (asset?.Rig?.IsValid != true || asset.Meshes == null || asset.Animations == null || asset.Animations.Count == 0)
                return 0f;

            Matrix4x4[] bindPalette = EvaluateBindPosePalette(asset.Rig);
            float max = 0f;
            foreach (GModelAnimationClip clip in asset.Animations.Take(3))
            {
                if (clip?.Frames == null || clip.Frames.Count == 0) continue;
                int stepFrame = Math.Max(1, clip.Frames.Count / 4);
                for (int frameIndex = 0; frameIndex < clip.Frames.Count; frameIndex += stepFrame)
                {
                    Matrix4x4[] locals = clip.Frames[frameIndex].LocalBoneTransforms;
                    Matrix4x4[] palette = EvaluateSkinPalette(asset.Rig, locals);
                    foreach (GModelMesh mesh in asset.Meshes)
                    {
                        if (mesh?.SkinnedVertices == null || mesh.SkinnedVertices.Length == 0) continue;
                        int stepVertex = Math.Max(1, mesh.SkinnedVertices.Length / 2000);
                        for (int i = 0; i < mesh.SkinnedVertices.Length; i += stepVertex)
                        {
                            SkinnedMeshVertex v = mesh.SkinnedVertices[i];
                            Vector3 bind = SkinPosition(v, bindPalette);
                            Vector3 animated = SkinPosition(v, palette);
                            if (IsFinite(animated))
                                max = MathF.Max(max, Vector3.Distance(bind, animated));
                        }
                    }
                }
            }
            return max;
        }

        private static float EstimateMaxAnimatedEdgeStretch(GModelAsset asset, out int stretchedEdges)
        {
            stretchedEdges = 0;
            if (asset?.Rig?.IsValid != true || asset.Meshes == null || asset.Animations == null || asset.Animations.Count == 0)
                return 1f;

            Matrix4x4[] bindPalette = EvaluateBindPosePalette(asset.Rig);
            float maxRatio = 1f;
            foreach (GModelAnimationClip clip in asset.Animations.Take(3))
            {
                if (clip?.Frames == null || clip.Frames.Count == 0) continue;
                int stepFrame = Math.Max(1, clip.Frames.Count / 4);
                for (int frameIndex = 0; frameIndex < clip.Frames.Count; frameIndex += stepFrame)
                {
                    Matrix4x4[] locals = clip.Frames[frameIndex].LocalBoneTransforms;
                    Matrix4x4[] palette = EvaluateSkinPalette(asset.Rig, locals);
                    foreach (GModelMesh mesh in asset.Meshes)
                    {
                        if (mesh?.SkinnedVertices == null ||
                            mesh.SkinnedVertices.Length == 0 ||
                            mesh.Indices == null ||
                            mesh.Indices.Length < 3)
                            continue;

                        Vector3[] bind = new Vector3[mesh.SkinnedVertices.Length];
                        Vector3[] animated = new Vector3[mesh.SkinnedVertices.Length];
                        for (int i = 0; i < mesh.SkinnedVertices.Length; i++)
                        {
                            bind[i] = SkinPosition(mesh.SkinnedVertices[i], bindPalette);
                            animated[i] = SkinPosition(mesh.SkinnedVertices[i], palette);
                        }

                        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                        {
                            AccumulateEdgeStretch(bind, animated, mesh.Indices[i], mesh.Indices[i + 1], ref maxRatio, ref stretchedEdges);
                            AccumulateEdgeStretch(bind, animated, mesh.Indices[i + 1], mesh.Indices[i + 2], ref maxRatio, ref stretchedEdges);
                            AccumulateEdgeStretch(bind, animated, mesh.Indices[i + 2], mesh.Indices[i], ref maxRatio, ref stretchedEdges);
                        }
                    }
                }
            }

            return maxRatio;
        }

        private static void AccumulateEdgeStretch(
            Vector3[] bind,
            Vector3[] animated,
            int a,
            int b,
            ref float maxRatio,
            ref int stretchedEdges)
        {
            if (bind == null || animated == null || a < 0 || b < 0 || a >= bind.Length || b >= bind.Length)
                return;
            float rest = Vector3.Distance(bind[a], bind[b]);
            if (rest <= 1e-5f)
                return;
            float posed = Vector3.Distance(animated[a], animated[b]);
            float ratio = MathF.Max(posed / rest, rest / MathF.Max(1e-6f, posed));
            if (!float.IsFinite(ratio))
                return;
            maxRatio = MathF.Max(maxRatio, ratio);
            if (ratio > 1.15f)
                stretchedEdges++;
        }

        private static Vector3 SkinPosition(SkinnedMeshVertex vertex, Matrix4x4[] palette)
        {
            Vector4 weights = vertex.JointWeights;
            NormalizeWeights(ref weights);
            Vector3 result = Vector3.Zero;
            for (int i = 0; i < 4; i++)
            {
                float weight = GetComponent(weights, i);
                if (weight <= 0.0001f) continue;
                int joint = (int)MathF.Round(GetComponent(vertex.JointIndices, i));
                if (joint < 0 || joint >= (palette?.Length ?? 0)) continue;
                result += Vector3.Transform(vertex.Position, palette[joint]) * weight;
            }
            return result;
        }

        private static float GetComponent(Vector4 v, int index) => index switch
        {
            0 => v.X,
            1 => v.Y,
            2 => v.Z,
            _ => v.W,
        };

        private static bool IsFinite(Vector3 v)
            => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static bool IsFinite(Matrix4x4 m)
            => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
            && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
            && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
            && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

        private static int PrimaryBoneIndex(Vector4 weights, Vector4 indices)
        {
            int best = 0;
            float bestW = weights.X;
            if (weights.Y > bestW) { bestW = weights.Y; best = 1; }
            if (weights.Z > bestW) { bestW = weights.Z; best = 2; }
            if (weights.W > bestW) { best = 3; }
            return best switch
            {
                0 => (int)indices.X,
                1 => (int)indices.Y,
                2 => (int)indices.Z,
                _ => (int)indices.W,
            };
        }

        private static Matrix4x4[] BindPoseArray(GModelRig rig, int count)
        {
            var arr = new Matrix4x4[Math.Max(1, count)];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = rig?.Bones != null && i < rig.Bones.Count ? rig.Bones[i].BindLocal : Matrix4x4.Identity;
            return arr;
        }

        private static void ApplyLocalRotation(Matrix4x4[] bones, int index, Matrix4x4 rotation)
        {
            if (bones == null || index < 0 || index >= bones.Length) return;
            bones[index] = bones[index] * rotation;
        }

        private static void ApplyLocalTranslation(Matrix4x4[] bones, int index, Vector3 translation)
        {
            if (bones == null || index < 0 || index >= bones.Length) return;
            bones[index] = bones[index] * Matrix4x4.CreateTranslation(translation);
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / MathF.Max(0.0001f, edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    public sealed class ModelGpuCache
    {
        private sealed class RendererCache
        {
            public readonly Dictionary<GModelAsset, CachedAsset> Assets = new();
            public MeshHandle PlaceholderMesh;
        }

        public sealed class CachedAsset
        {
            public readonly List<CachedMesh> Meshes = new();
            public readonly Dictionary<string, List<CachedMesh>> MorphMeshes = new(StringComparer.Ordinal);
            public readonly Queue<string> MorphOrder = new();
            public SkinPaletteHandle IdentityPalette;
            public int PaletteSize;
            public readonly Dictionary<string, SkinPaletteHandle> AnimationPalettes = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, WeakReference<AnimationController>> ControllerOwners = new();
            public int ControllerUpdates;
        }

        public sealed class CachedMesh
        {
            public MeshHandle Mesh;
            public bool IsSkinned;
            public int MaterialIndex;
            public int Lod;
        }

        private readonly ConditionalWeakTable<IRenderController, RendererCache> _renderers = new();

        public void Clear(IRenderController renderer)
        {
            if (renderer == null || !_renderers.TryGetValue(renderer, out RendererCache cache)) return;
            foreach (CachedAsset asset in cache.Assets.Values)
            {
                foreach (CachedMesh mesh in asset.Meshes)
                    if (mesh.Mesh.IsValid && mesh.Mesh.Id != cache.PlaceholderMesh.Id) renderer.ReleaseMesh(mesh.Mesh);
                foreach (List<CachedMesh> variant in asset.MorphMeshes.Values)
                    foreach (CachedMesh mesh in variant)
                        if (mesh.Mesh.IsValid && mesh.Mesh.Id != cache.PlaceholderMesh.Id) renderer.ReleaseMesh(mesh.Mesh);
                if (asset.IdentityPalette.IsValid) renderer.ReleaseSkinPalette(asset.IdentityPalette);
                foreach (SkinPaletteHandle palette in asset.AnimationPalettes.Values)
                    if (palette.IsValid) renderer.ReleaseSkinPalette(palette);
            }
            if (cache.PlaceholderMesh.IsValid) renderer.ReleaseMesh(cache.PlaceholderMesh);
            _renderers.Remove(renderer);
        }

        public CachedAsset GetOrCreate(IRenderController renderer, GModelAsset asset)
        {
            if (renderer == null || asset == null) return null;
            RendererCache cache = _renderers.GetOrCreateValue(renderer);
            if (cache.Assets.TryGetValue(asset, out CachedAsset cached))
                return cached;

            cached = new CachedAsset();
            if (asset.Meshes != null)
            {
                foreach (GModelMesh mesh in asset.Meshes)
                {
                    if (mesh == null || mesh.Indices == null || mesh.Indices.Length == 0)
                        continue;

                    bool skinned = mesh.IsSkinned && mesh.SkinnedVertices != null && mesh.SkinnedVertices.Length > 0;
                    foreach ((int materialIndex, ushort[] indices) in MaterialBatches(mesh))
                    {
                        MeshHandle handle = skinned
                            ? renderer.RegisterSkinnedMesh(mesh.SkinnedVertices, indices)
                            : mesh.Vertices != null && mesh.Vertices.Length > 0
                                ? renderer.RegisterMesh(mesh.Vertices, indices)
                                : MeshHandle.Invalid;
                        if (!handle.IsValid) continue;
                        cached.Meshes.Add(new CachedMesh
                        {
                            Mesh = handle,
                            IsSkinned = skinned,
                            MaterialIndex = materialIndex,
                            Lod = mesh.Lod,
                        });
                    }
                }
            }

            if (asset.Rig?.IsValid == true)
            {
                cached.PaletteSize = Math.Max(1, asset.Rig.Bones.Count);
                cached.IdentityPalette = renderer.CreateSkinPalette(cached.PaletteSize);
                renderer.UpdateSkinPalette(cached.IdentityPalette, IdentityPalette(cached.PaletteSize));
            }

            if (cached.Meshes.Count == 0)
            {
                if (!cache.PlaceholderMesh.IsValid)
                    cache.PlaceholderMesh = MeshGeometry.RegisterCube(renderer, new RenderColor(1f, 0.15f, 0.35f, 1f), 1f);
                cached.Meshes.Add(new CachedMesh { Mesh = cache.PlaceholderMesh, MaterialIndex = 0 });
            }

            cache.Assets[asset] = cached;
            return cached;
        }

        /// <summary>
        /// Returns a bounded, quantized GPU mesh variant for the requested morph weights. The
        /// canonical mesh remains immutable and variants are shared between entities using the
        /// same effective weights.
        /// </summary>
        public IReadOnlyList<CachedMesh> ResolveMeshes(
            IRenderController renderer,
            GModelAsset asset,
            CachedAsset cached,
            IReadOnlyDictionary<string, float> morphWeights)
        {
            if (renderer == null || asset == null || cached == null) return Array.Empty<CachedMesh>();
            string key = ModelMorphEvaluator.Fingerprint(asset, morphWeights);
            if (string.IsNullOrEmpty(key)) return cached.Meshes;
            if (cached.MorphMeshes.TryGetValue(key, out List<CachedMesh> known)) return known;

            const int maximumVariants = 24;
            while (cached.MorphMeshes.Count >= maximumVariants && cached.MorphOrder.Count > 0)
            {
                string oldest = cached.MorphOrder.Dequeue();
                if (!cached.MorphMeshes.Remove(oldest, out List<CachedMesh> removed)) continue;
                foreach (CachedMesh mesh in removed)
                    if (mesh.Mesh.IsValid) renderer.ReleaseMesh(mesh.Mesh);
            }

            List<CachedMesh> created = [];
            foreach (GModelMesh mesh in asset.Meshes ?? [])
            {
                if (mesh == null || mesh.Indices == null || mesh.Indices.Length == 0) continue;
                bool skinned = mesh.IsSkinned && mesh.SkinnedVertices is { Length: > 0 };
                MeshVertex[] plain = skinned ? [] : ModelMorphEvaluator.Apply(mesh, morphWeights);
                SkinnedMeshVertex[] animated = skinned ? ModelMorphEvaluator.ApplySkinned(mesh, morphWeights) : [];
                foreach ((int materialIndex, ushort[] indices) in MaterialBatches(mesh))
                {
                    MeshHandle handle = skinned
                        ? renderer.RegisterSkinnedMesh(animated, indices)
                        : plain.Length > 0 ? renderer.RegisterMesh(plain, indices) : MeshHandle.Invalid;
                    if (!handle.IsValid) continue;
                    created.Add(new CachedMesh
                    {
                        Mesh = handle,
                        IsSkinned = skinned,
                        MaterialIndex = materialIndex,
                        Lod = mesh.Lod,
                    });
                }
            }
            if (created.Count == 0) return cached.Meshes;
            cached.MorphMeshes[key] = created;
            cached.MorphOrder.Enqueue(key);
            return created;
        }

        private static IEnumerable<(int MaterialIndex, ushort[] Indices)> MaterialBatches(GModelMesh mesh)
        {
            int triangles = mesh.Indices.Length / 3;
            if (mesh.TriangleMaterialIndices == null || mesh.TriangleMaterialIndices.Length != triangles)
            {
                yield return (mesh.MaterialIndex, mesh.Indices);
                yield break;
            }

            var groups = new SortedDictionary<int, List<ushort>>();
            for (int triangle = 0; triangle < triangles; triangle++)
            {
                int material = Math.Max(0, mesh.TriangleMaterialIndices[triangle]);
                if (!groups.TryGetValue(material, out List<ushort> indices))
                {
                    indices = new List<ushort>();
                    groups.Add(material, indices);
                }
                indices.Add(mesh.Indices[triangle * 3]);
                indices.Add(mesh.Indices[triangle * 3 + 1]);
                indices.Add(mesh.Indices[triangle * 3 + 2]);
            }
            foreach ((int material, List<ushort> indices) in groups)
                if (indices.Count > 0) yield return (material, indices.ToArray());
        }

        public SkinPaletteHandle UpdatePalette(
            IRenderController renderer,
            GModelAsset asset,
            CachedAsset cached,
            RuntimeModelAnimationState animation)
        {
            if (renderer == null || asset?.Rig?.IsValid != true || cached == null)
                return SkinPaletteHandle.Invalid;

            if (!cached.IdentityPalette.IsValid)
            {
                cached.PaletteSize = Math.Max(1, asset.Rig.Bones.Count);
                cached.IdentityPalette = renderer.CreateSkinPalette(cached.PaletteSize);
            }

            string key = PaletteKey(asset, animation);
            if (animation.Controller != null)
            {
                if (++cached.ControllerUpdates % 64 == 0)
                {
                    var expired = new List<string>();
                    foreach (var pair in cached.ControllerOwners)
                        if (!pair.Value.TryGetTarget(out _)) expired.Add(pair.Key);
                    foreach (string stale in expired)
                    {
                        if (cached.AnimationPalettes.Remove(stale, out var stalePalette) && stalePalette.IsValid)
                            renderer.ReleaseSkinPalette(stalePalette);
                        cached.ControllerOwners.Remove(stale);
                    }
                }
                if (!cached.ControllerOwners.ContainsKey(key))
                    cached.ControllerOwners.Add(key, new WeakReference<AnimationController>(animation.Controller));
            }
            if (string.IsNullOrEmpty(key))
            {
                Matrix4x4[] bindPalette = GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig);
                renderer.UpdateSkinPalette(cached.IdentityPalette, bindPalette);
                return cached.IdentityPalette;
            }

            if (!cached.AnimationPalettes.TryGetValue(key, out SkinPaletteHandle handle) || !handle.IsValid)
            {
                handle = renderer.CreateSkinPalette(cached.PaletteSize);
                cached.AnimationPalettes[key] = handle;
            }

            Matrix4x4[] matrices = EvaluatePalette(asset, animation, cached.PaletteSize);
            renderer.UpdateSkinPalette(handle, matrices);
            return handle;
        }

        private static string PaletteKey(GModelAsset asset, RuntimeModelAnimationState animation)
        {
            if (animation.Controller != null) return "controller:" + animation.Controller.Id;
            if (string.IsNullOrWhiteSpace(animation.ClipName) || asset.Animations == null)
                return "";

            GModelAnimationClip clip = FindClip(asset, animation.ClipName);
            if (clip?.Frames == null || clip.Frames.Count == 0)
                return "";

            int frame = FrameIndex(clip, animation.TimeSeconds, animation.Fps, animation.Loop);
            string policy = animation.PreserveRootTransform ? "|keep-root" : string.Empty;
            if (string.IsNullOrWhiteSpace(animation.PreviousClipName) || animation.BlendFactor >= 1f)
                return $"{clip.Name}|{frame}{policy}";

            GModelAnimationClip previous = FindClip(asset, animation.PreviousClipName);
            if (previous?.Frames == null || previous.Frames.Count == 0)
                return $"{clip.Name}|{frame}{policy}";
            int previousFrame = FrameIndex(previous, animation.PreviousTimeSeconds, animation.Fps, animation.Loop);
            int blendStep = (int)MathF.Round(Math.Clamp(animation.BlendFactor, 0f, 1f) * 255f);
            return $"{previous.Name}|{previousFrame}>{clip.Name}|{frame}@{blendStep}{policy}";
        }

        private static int FrameIndex(GModelAnimationClip clip, float timeSeconds, float fpsOverride, bool loop)
        {
            float fps = fpsOverride > 0f ? fpsOverride : MathF.Max(1f, clip.Fps);
            int frame = (int)MathF.Floor(timeSeconds * fps);
            return loop || clip.Loop
                ? ((frame % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count
                : Math.Clamp(frame, 0, clip.Frames.Count - 1);
        }

        private static Matrix4x4[] EvaluatePalette(GModelAsset asset, RuntimeModelAnimationState animation, int paletteSize)
        {
            var palette = IdentityPalette(paletteSize);
            if (asset.Rig?.Bones == null || asset.Rig.Bones.Count == 0)
                return palette;

            int count = Math.Min(palette.Length, asset.Rig.Bones.Count);
            Matrix4x4[] evaluatedLocals = GModelPrimitiveFactory.EvaluateAnimatedLocals(asset, animation);

            Matrix4x4[] world = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, evaluatedLocals);
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 inverseBind = asset.Rig.InverseBindMatrices != null && i < asset.Rig.InverseBindMatrices.Length
                    ? asset.Rig.InverseBindMatrices[i]
                    : Matrix4x4.Identity;
                if (asset.Rig.InverseBindMatrices == null || i >= asset.Rig.InverseBindMatrices.Length)
                    System.Diagnostics.Debug.WriteLine($"ModelGpuCache: missing inverse bind for bone {i} on '{asset.Name}'");
                palette[i] = inverseBind * world[i];
            }
            return palette;
        }

        private static GModelAnimationClip FindClip(GModelAsset asset, string name)
        {
            if (asset?.Animations == null || string.IsNullOrWhiteSpace(name))
                return null;
            foreach (GModelAnimationClip c in asset.Animations)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        private static Matrix4x4[] IdentityPalette(int count)
        {
            var matrices = new Matrix4x4[Math.Max(1, count)];
            for (int i = 0; i < matrices.Length; i++)
                matrices[i] = Matrix4x4.Identity;
            return matrices;
        }
    }
}

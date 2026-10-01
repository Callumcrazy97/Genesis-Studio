using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Geometry;
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
            /// <summary>Last use of each clip/frame palette, so unused poses can be released.</summary>
            public readonly Dictionary<string, long> PaletteLastUsed = new(StringComparer.OrdinalIgnoreCase);
            public int ControllerUpdates;
            public bool BindPoseApplied;
            /// <summary>Simplified versions being worked out in the background, until they are uploaded.</summary>
            internal Task<AutoLodMesh[]> AutoLodBuild;
            /// <summary>True once the simplified versions are on the GPU and can be drawn.</summary>
            public bool AutoLodsReady { get; internal set; }
            /// <summary>True while simplified versions are still being worked out.</summary>
            public bool AutoLodsPending => AutoLodBuild != null;
            /// <summary>True once a draw has asked for a simplified version.</summary>
            public bool AutoLodsRequested { get; internal set; }
        }

        /// <summary>One simplified version of one drawn mesh, ready to upload.</summary>
        internal sealed class AutoLodMesh
        {
            public int Target;
            public int Level;
            public MeshVertex[] Vertices;
            /// <summary>Set instead of <see cref="Vertices"/> for a mesh that is animated by a skeleton.</summary>
            public SkinnedMeshVertex[] SkinnedVertices;
            public ushort[] Indices;
        }

        public sealed class CachedMesh
        {
            public string SourceName;
            /// <summary>Index of the source <see cref="GModelMesh"/> in the asset.</summary>
            public int SourceIndex;
            public MeshHandle Mesh;
            public bool IsSkinned;
            public int MaterialIndex;
            public int Lod;
            /// <summary>
            /// Automatically simplified versions for levels 1 to 3, or null when the mesh is drawn
            /// in full at every size. A level that saved too little reuses the one before it.
            /// </summary>
            public MeshHandle[] AutoLods;
            /// <summary>The drawn triangle list, kept until the simplifier has been handed it.</summary>
            internal ushort[] SourceIndices;
            /// <summary>Triangles drawn at full detail and at each automatic level.</summary>
            public int[] LevelTriangles;

            /// <summary>The mesh to draw for an automatic level, falling back to full detail.</summary>
            public MeshHandle ForLevel(int level)
            {
                if (level <= 0 || AutoLods == null) return Mesh;
                MeshHandle handle = AutoLods[Math.Min(level, AutoLods.Length) - 1];
                return handle.IsValid ? handle : Mesh;
            }
        }

        private readonly ConditionalWeakTable<IRenderController, RendererCache> _renderers = new();

        /// <summary>Share of the triangles each automatic level aims to keep.</summary>
        public static readonly float[] AutoLodRatios = { 0.5f, 0.2f, 0.07f };

        /// <summary>
        /// How far each level may move the surface, as a share of the mesh's size. A level is shown
        /// only when the model is small enough on screen for this to be about a pixel.
        /// </summary>
        public static readonly float[] AutoLodErrors = { 0.006f, 0.016f, 0.045f };

        /// <summary>Meshes with fewer triangles than this are already cheap and are left alone.</summary>
        public const int AutoLodMinimumTriangles = 96;

        /// <summary>Set false to draw every model in full at any distance.</summary>
        public static bool AutoLodEnabled { get; set; } = true;

        /// <summary>
        /// Set false to leave animated (skinned) meshes in full at any distance. A simplified
        /// version keeps a subset of the original vertices, each with its own bone weights, so it
        /// follows the skeleton exactly as the full mesh does.
        /// </summary>
        public static bool AutoLodSkinned { get; set; } = true;

        /// <summary>Clip/frame palettes kept per asset before idle ones are released.</summary>
        private const int MaximumClipPalettes = 128;
        private const long IdlePaletteMilliseconds = 2000;

        /// <summary>
        /// Releases GPU resources for every renderer. Used when an invalidation does not name its
        /// renderer: skipping the release there leaked every model's meshes on each Studio edit.
        /// </summary>
        public void ClearAll()
        {
            var renderers = new List<IRenderController>();
            foreach (KeyValuePair<IRenderController, RendererCache> pair in _renderers)
                renderers.Add(pair.Key);
            foreach (IRenderController renderer in renderers)
                Clear(renderer);
        }

        public void Clear(IRenderController renderer)
        {
            if (renderer == null || !_renderers.TryGetValue(renderer, out RendererCache cache)) return;
            foreach (CachedAsset asset in cache.Assets.Values)
            {
                foreach (CachedMesh mesh in asset.Meshes)
                {
                    if (mesh.Mesh.IsValid && mesh.Mesh.Id != cache.PlaceholderMesh.Id) renderer.ReleaseMesh(mesh.Mesh);
                    ReleaseAutoLods(renderer, mesh);
                }
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
            {
                if (cached.AutoLodBuild is { IsCompleted: true }) UploadAutoLods(renderer, cached);
                return cached;
            }

            cached = new CachedAsset();
            if (asset.Meshes != null)
            {
                for (int sourceIndex = 0; sourceIndex < asset.Meshes.Count; sourceIndex++)
                {
                    GModelMesh mesh = asset.Meshes[sourceIndex];
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
                            SourceName = mesh.Name,
                            SourceIndex = sourceIndex,
                            Mesh = handle,
                            IsSkinned = skinned,
                            MaterialIndex = materialIndex,
                            Lod = mesh.Lod,
                            SourceIndices = indices,
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
        /// Asks for an asset's simplified versions. Called the first time a draw wants one, so
        /// editors and previews, which always draw in full, never pay for the simplification.
        /// </summary>
        public void RequestAutoLods(GModelAsset asset, CachedAsset cached)
        {
            if (asset == null || cached == null || cached.AutoLodsRequested) return;
            cached.AutoLodsRequested = true;
            StartAutoLods(asset, cached);
        }

        /// <summary>
        /// Starts simplifying an asset's meshes on a worker. An asset that brings its own levels
        /// keeps exactly what was authored.
        /// </summary>
        private static void StartAutoLods(GModelAsset asset, CachedAsset cached)
        {
            if (!AutoLodEnabled || asset.Meshes == null) return;
            foreach (GModelMesh mesh in asset.Meshes)
                if (mesh != null && mesh.Lod != 0) return;

            var jobs = new List<(int Target, MeshVertex[] Vertices, SkinnedMeshVertex[] Skinned, ushort[] Indices)>();
            for (int i = 0; i < cached.Meshes.Count; i++)
            {
                CachedMesh entry = cached.Meshes[i];
                if (entry.SourceIndex < 0 || entry.SourceIndex >= asset.Meshes.Count) continue;
                GModelMesh source = asset.Meshes[entry.SourceIndex];
                if (source == null || entry.SourceIndices == null) continue;
                if (entry.SourceIndices.Length / 3 < AutoLodMinimumTriangles) continue;
                if (entry.IsSkinned)
                {
                    if (AutoLodSkinned && source.SkinnedVertices is { Length: > 0 })
                        jobs.Add((i, null, source.SkinnedVertices, entry.SourceIndices));
                }
                else if (source.Vertices != null)
                {
                    jobs.Add((i, source.Vertices, null, entry.SourceIndices));
                }
            }

            foreach (CachedMesh entry in cached.Meshes) entry.SourceIndices = null;
            if (jobs.Count == 0) return;
            cached.AutoLodBuild = Task.Run(() =>
            {
                var built = new List<AutoLodMesh>();
                foreach ((int target, MeshVertex[] vertices, SkinnedMeshVertex[] skinned, ushort[] indices) in jobs)
                {
                    if (skinned != null) BuildSkinnedAutoLods(target, skinned, indices, built);
                    else BuildAutoLods(target, vertices, indices, built);
                }

                return built.ToArray();
            });
        }

        /// <summary>
        /// The simplified versions an animated mesh would be given, without touching the GPU. Every
        /// vertex kept is one of the original vertices, bone weights and all.
        /// </summary>
        public static IReadOnlyList<(int Level, SkinnedMeshVertex[] Vertices, ushort[] Indices)> BuildSkinnedAutoLodMeshes(
            SkinnedMeshVertex[] vertices, ushort[] indices)
        {
            var levels = new List<(int, SkinnedMeshVertex[], ushort[])>();
            if (vertices == null || indices == null || indices.Length / 3 < AutoLodMinimumTriangles) return levels;
            var built = new List<AutoLodMesh>();
            BuildSkinnedAutoLods(0, vertices, indices, built);
            foreach (AutoLodMesh lod in built) levels.Add((lod.Level, lod.SkinnedVertices, lod.Indices));
            return levels;
        }

        internal static void BuildSkinnedAutoLods(int target, SkinnedMeshVertex[] vertices, ushort[] indices, List<AutoLodMesh> built)
        {
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) positions[i] = vertices[i].Position;
            BuildLevels<SkinnedMeshVertex>(vertices, positions, indices, (level, kept, keptIndices) =>
                built.Add(new AutoLodMesh { Target = target, Level = level, SkinnedVertices = kept, Indices = keptIndices }));
        }

        /// <summary>
        /// The simplified versions a mesh would be given, without touching the GPU: level 0 is the
        /// first step down from full detail. Empty when the mesh is too small to be worth reducing.
        /// </summary>
        public static IReadOnlyList<(int Level, MeshVertex[] Vertices, ushort[] Indices)> BuildAutoLodMeshes(
            MeshVertex[] vertices, ushort[] indices)
        {
            var levels = new List<(int, MeshVertex[], ushort[])>();
            if (vertices == null || indices == null || indices.Length / 3 < AutoLodMinimumTriangles) return levels;
            var built = new List<AutoLodMesh>();
            BuildAutoLods(0, vertices, indices, built);
            foreach (AutoLodMesh lod in built) levels.Add((lod.Level, lod.Vertices, lod.Indices));
            return levels;
        }

        internal static void BuildAutoLods(int target, MeshVertex[] vertices, ushort[] indices, List<AutoLodMesh> built)
        {
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) positions[i] = vertices[i].Position;
            BuildLevels<MeshVertex>(vertices, positions, indices, (level, kept, keptIndices) =>
                built.Add(new AutoLodMesh { Target = target, Level = level, Vertices = kept, Indices = keptIndices }));
        }

        private static void BuildLevels<TVertex>(TVertex[] vertices, Vector3[] positions, ushort[] indices,
            Action<int, TVertex[], ushort[]> add)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < positions.Length; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }

            float size = vertices.Length == 0 ? 0f : Vector3.Distance(min, max) * 0.5f;
            int[] current = Array.ConvertAll(indices, index => (int)index);
            int full = current.Length / 3;
            int previous = full;
            for (int level = 0; level < AutoLodRatios.Length; level++)
            {
                int wanted = Math.Max(8, (int)(full * AutoLodRatios[level]));
                // Each level starts from the one before, so the levels nest and the work is not repeated.
                int[] simplified = MeshSimplifier.Simplify(positions, current, wanted, size * AutoLodErrors[level]);
                int triangles = simplified.Length / 3;
                if (triangles < 4 || triangles > previous * 0.85f) continue;
                if (!MeshSimplifier.Compact<TVertex>(vertices, simplified, out TVertex[] keptVertices, out ushort[] keptIndices))
                    continue;
                add(level, keptVertices, keptIndices);
                current = simplified;
                previous = triangles;
            }
        }

        private static void UploadAutoLods(IRenderController renderer, CachedAsset cached)
        {
            Task<AutoLodMesh[]> build = cached.AutoLodBuild;
            cached.AutoLodBuild = null;
            if (!build.IsCompletedSuccessfully) return;
            foreach (AutoLodMesh lod in build.Result)
            {
                if (lod.Target < 0 || lod.Target >= cached.Meshes.Count) continue;
                MeshHandle handle = lod.SkinnedVertices != null
                    ? renderer.RegisterSkinnedMesh(lod.SkinnedVertices, lod.Indices)
                    : renderer.RegisterMesh(lod.Vertices, lod.Indices);
                if (!handle.IsValid) continue;
                CachedMesh mesh = cached.Meshes[lod.Target];
                mesh.AutoLods ??= new MeshHandle[AutoLodRatios.Length];
                mesh.LevelTriangles ??= new int[AutoLodRatios.Length + 1];
                mesh.AutoLods[lod.Level] = handle;
                mesh.LevelTriangles[lod.Level + 1] = lod.Indices.Length / 3;
            }

            // A level that was not worth making draws as the nearest finer one.
            foreach (CachedMesh mesh in cached.Meshes)
            {
                if (mesh.AutoLods == null) continue;
                for (int level = 0; level < mesh.AutoLods.Length; level++)
                {
                    if (mesh.AutoLods[level].IsValid || level == 0) continue;
                    mesh.AutoLods[level] = mesh.AutoLods[level - 1];
                    mesh.LevelTriangles[level + 1] = mesh.LevelTriangles[level];
                }
            }

            cached.AutoLodsReady = true;
        }

        private static void ReleaseAutoLods(IRenderController renderer, CachedMesh mesh)
        {
            if (mesh.AutoLods == null) return;
            int last = -1;
            foreach (MeshHandle handle in mesh.AutoLods)
            {
                if (!handle.IsValid || handle.Id == last) continue;
                last = handle.Id;
                renderer.ReleaseMesh(handle);
            }

            mesh.AutoLods = null;
        }

        /// <summary>Blocks until an asset's simplified versions are uploaded. For tests and captures.</summary>
        public void SettleAutoLods(IRenderController renderer, GModelAsset asset)
        {
            CachedAsset cached = GetOrCreate(renderer, asset);
            RequestAutoLods(asset, cached);
            if (cached?.AutoLodBuild == null) return;
            try { cached.AutoLodBuild.Wait(); } catch (AggregateException) { }
            UploadAutoLods(renderer, cached);
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
                    if (mesh.Mesh.IsValid && !cached.Meshes.Contains(mesh)) renderer.ReleaseMesh(mesh.Mesh);
            }

            List<CachedMesh> created = [];
            bool morphed = false;
            IReadOnlyList<GModelMesh> sourceMeshes = asset.Meshes ?? [];
            for (int sourceIndex = 0; sourceIndex < sourceMeshes.Count; sourceIndex++)
            {
                GModelMesh mesh = sourceMeshes[sourceIndex];
                if (mesh == null || mesh.Indices == null || mesh.Indices.Length == 0) continue;
                if (mesh.MorphTargets is not { Count: > 0 })
                {
                    // Meshes without morph targets are identical in every variant; share the canonical
                    // upload instead of cloning and re-registering them for each new weight quantum.
                    foreach (CachedMesh canonical in cached.Meshes)
                        if (canonical.SourceIndex == sourceIndex) created.Add(canonical);
                    continue;
                }
                morphed = true;
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
                        SourceName = mesh.Name,
                        SourceIndex = sourceIndex,
                        Mesh = handle,
                        IsSkinned = skinned,
                        MaterialIndex = materialIndex,
                        Lod = mesh.Lod,
                    });
                }
            }
            if (!morphed || created.Count == 0) return cached.Meshes;
            cached.MorphMeshes[key] = created;
            cached.MorphOrder.Enqueue(key);
            return created;
        }

        /// <summary>The triangle list a mesh draws for each of its materials.</summary>
        public static IEnumerable<(int MaterialIndex, ushort[] Indices)> MaterialBatches(GModelMesh mesh)
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
                // The bind pose never changes for a cached asset; evaluate it once rather than on
                // every draw of every idle rigged instance.
                if (!cached.BindPoseApplied)
                {
                    Matrix4x4[] bindPalette = GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig);
                    renderer.UpdateSkinPalette(cached.IdentityPalette, bindPalette);
                    cached.BindPoseApplied = true;
                }
                return cached.IdentityPalette;
            }

            bool controllerDriven = animation.Controller != null;
            if (cached.AnimationPalettes.TryGetValue(key, out SkinPaletteHandle handle) && handle.IsValid)
            {
                if (!controllerDriven)
                {
                    // A clip/frame/blend key fully determines the pose (clip sampling is frame
                    // quantized), so an existing palette already holds it. Re-evaluating the whole
                    // skeleton for every instance on the same frame was pure CPU waste.
                    cached.PaletteLastUsed[key] = Environment.TickCount64;
                    return handle;
                }
            }
            else
            {
                if (!controllerDriven) ReleaseIdleClipPalettes(renderer, cached);
                handle = renderer.CreateSkinPalette(cached.PaletteSize);
                cached.AnimationPalettes[key] = handle;
            }

            if (!controllerDriven) cached.PaletteLastUsed[key] = Environment.TickCount64;
            Matrix4x4[] matrices = EvaluatePalette(asset, animation, cached.PaletteSize);
            renderer.UpdateSkinPalette(handle, matrices);
            return handle;
        }

        /// <summary>
        /// Every distinct clip frame and crossfade step creates a palette. Without a bound, long
        /// sessions accumulated one GPU buffer per pose ever shown. Only palettes idle for a while
        /// are released, so nothing submitted this frame loses its buffer.
        /// </summary>
        private static void ReleaseIdleClipPalettes(IRenderController renderer, CachedAsset cached)
        {
            if (cached.PaletteLastUsed.Count < MaximumClipPalettes) return;
            long cutoff = Environment.TickCount64 - IdlePaletteMilliseconds;
            List<string> idle = null;
            foreach (KeyValuePair<string, long> pair in cached.PaletteLastUsed)
                if (pair.Value < cutoff) (idle ??= new List<string>()).Add(pair.Key);
            if (idle == null) return;
            foreach (string key in idle)
            {
                cached.PaletteLastUsed.Remove(key);
                if (cached.AnimationPalettes.Remove(key, out SkinPaletteHandle palette) && palette.IsValid)
                    renderer.ReleaseSkinPalette(palette);
            }
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

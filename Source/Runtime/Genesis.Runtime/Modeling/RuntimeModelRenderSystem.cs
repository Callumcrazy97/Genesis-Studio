using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;
using EcsWorld = Genesis.Runtime.ECS.World;
using Genesis.Runtime.ECS.Components;

namespace Genesis.Runtime.Modeling
{
    public sealed class RuntimeModelRenderSystem
    {
        private readonly RuntimeModelAssetRegistry _assets;
        private readonly ModelGpuCache _gpu;
        private readonly Dictionary<(string Project, string Model), GModelAsset> _frameAssets = new();
        /// <summary>
        /// When true, a material's texture that is not loaded yet is read and decoded on a worker
        /// thread, and a mesh is left out of the frame until every texture it uses has arrived:
        /// it appears a few frames late, whole, rather than holding one frame up for all of them.
        /// Off unless a game's host turns it on. Editors, previews and captures draw a model in
        /// the frame that asks for it.
        /// </summary>
        public static bool BackgroundTextures { get; set; }

        private sealed class CachedTexture
        {
            /// <summary>True while a worker is still reading the texture: ask again next frame.</summary>
            public bool Pending;
            public TextureHandle Handle;
            public long NextFreshnessCheckMilliseconds;
            public int FrameGeneration;
            public long AssetGeneration;
        }
        // Weakly keyed by renderer: several of these systems are static, and a strong key pinned
        // every disposed editor/preview renderer (and its GPU cache) for the life of the process.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRenderController,
            Dictionary<(string Project, string Texture, TextureColorSpace ColorSpace), CachedTexture>> _textures = new();
        private readonly int _textureFreshnessIntervalMilliseconds;
        private int _frameGeneration;
        private bool _frameOpen;

        /// <summary>Resolve and freshness-check each distinct asset once during a scene submission.</summary>
        public void BeginFrame() { _frameAssets.Clear(); _frameGeneration++; _frameOpen = true; }
        public void EndFrame() { _frameAssets.Clear(); _frameOpen = false; }

        private GModelAsset LoadModel(string project, string model)
        {
            if (!_frameOpen) return _assets.Load(project, model);
            var key = (project, model);
            if (!_frameAssets.TryGetValue(key, out GModelAsset asset))
                _frameAssets[key] = asset = _assets.Load(project, model);
            return asset;
        }

        public RuntimeModelRenderSystem(
            RuntimeModelAssetRegistry assets = null,
            ModelGpuCache gpu = null,
            int assetFreshnessIntervalMilliseconds = 0,
            int textureFreshnessIntervalMilliseconds = 0)
        {
            _assets = assets ?? new RuntimeModelAssetRegistry(assetFreshnessIntervalMilliseconds);
            _gpu = gpu ?? new ModelGpuCache();
            _textureFreshnessIntervalMilliseconds = Math.Max(0, textureFreshnessIntervalMilliseconds);
        }

        public void InvalidateAssets(IRenderController renderer = null)
        {
            _assets.Clear();
            _frameAssets.Clear();
            _textures.Clear();
            if (renderer != null) _gpu.Clear(renderer);
            else _gpu.ClearAll();
        }

        public bool TryGetBounds(string projectPath, string modelName, out Vector3 min, out Vector3 max, bool includePivot = false)
        {
            GModelAsset asset = LoadModel(projectPath, modelName);
            min = asset.Bounds?.Min ?? new Vector3(-0.5f);
            max = asset.Bounds?.Max ?? new Vector3(0.5f);
            if (includePivot)
            {
                Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
                min -= pivot;
                max -= pivot;
            }
            return asset.HasRenderableMeshes && !asset.ImportRequired;
        }

        public int SubmitWorld(
            EcsWorld world,
            string projectPath,
            MeshDrawCall[] buffer,
            int startCount,
            IRenderController renderer)
        {
            if (world == null || renderer == null || buffer == null) return startCount;
            int count = startCount;
            world.Query<TransformComponent, Draw3DComponent, ModelRendererComponent>((entity, ref transform, ref draw3d, ref model) =>
            {
                if (!draw3d.Visible || string.IsNullOrWhiteSpace(model.ModelAsset) || count >= buffer.Length)
                    return;

                RuntimeModelAnimationState anim = default;
                if (world.Has<ModelAnimatorComponent>(entity))
                {
                    ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
                    anim = RuntimeModelAnimationState.From(animator, model.KeepPreviousTransform);
                }
                if (world.Has<ModelMorphComponent>(entity))
                {
                    ref ModelMorphComponent morph = ref world.GetRef<ModelMorphComponent>(entity);
                    if (morph.Enabled && morph.Weights is { Count: > 0 })
                        anim = anim.WithMorphWeights(morph.Weights, anim.PreserveRootTransform);
                }

                var queue = ModelRenderQueue.Rent();
                Enqueue(queue, projectPath, model.ModelAsset, model.MaterialOverride,
                    TransformMatrix(transform, model), draw3d, model, anim, renderer);
                count = queue.CopyTo(buffer, count);
            });
            return count;
        }

        public bool DrawModel(
            string projectPath,
            string modelName,
            string materialOverride,
            Matrix4x4 world,
            Draw3DComponent draw3d,
            ModelRendererComponent rendererComponent,
            RuntimeModelAnimationState animation,
            IRenderController renderer)
        {
            var queue = ModelRenderQueue.Rent();
            if (!Enqueue(queue, projectPath, modelName, materialOverride, world, draw3d, rendererComponent, animation, renderer))
                return false;
            queue.Draw(renderer);
            return true;
        }

        public bool Enqueue(
            IMeshDrawList queue,
            string projectPath,
            string modelName,
            string materialOverride,
            Matrix4x4 world,
            Draw3DComponent draw3d,
            ModelRendererComponent rendererComponent,
            RuntimeModelAnimationState animation,
            IRenderController renderer)
        {
            if (queue == null || renderer == null || string.IsNullOrWhiteSpace(modelName))
                return false;

            GModelAsset asset = LoadModel(projectPath, modelName);
            return EnqueueAsset(queue, asset, projectPath, materialOverride, world, draw3d,
                rendererComponent, animation, renderer);
        }

        /// <summary>The asset a Model resource name resolves to, from the shared cache.</summary>
        public GModelAsset LoadAsset(string projectPath, string modelName) =>
            string.IsNullOrWhiteSpace(modelName) ? null : LoadModel(projectPath, modelName);

        /// <summary>
        /// Adds a model's draw calls at one automatic level of detail, at the origin. Systems that
        /// draw many copies (scattered forests) use these as templates and supply the transforms.
        /// </summary>
        /// <param name="drawn">Receives the cached mesh behind each draw call, in the same order.</param>
        public bool EnqueueAtLevel(
            IMeshDrawList queue,
            string projectPath,
            string modelName,
            int level,
            bool castShadows,
            IRenderController renderer,
            List<ModelGpuCache.CachedMesh> drawn = null)
        {
            if (queue == null || renderer == null || string.IsNullOrWhiteSpace(modelName)) return false;
            GModelAsset asset = LoadModel(projectPath, modelName);
            return EnqueueAsset(queue, asset, projectPath, string.Empty, Matrix4x4.Identity,
                new Draw3DComponent { Visible = true, CastShadows = castShadows, ReceiveShadows = true },
                new ModelRendererComponent { CastShadows = castShadows, ReceiveShadows = true, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f },
                default, renderer, forcedLevel: Math.Max(0, level), drawn: drawn);
        }

        /// <summary>True once a model's automatically simplified meshes can be drawn.</summary>
        public bool AutoLodsReady(string projectPath, string modelName, IRenderController renderer)
        {
            GModelAsset asset = LoadAsset(projectPath, modelName);
            ModelGpuCache.CachedAsset cached = asset == null ? null : _gpu.GetOrCreate(renderer, asset);
            return cached != null && (cached.AutoLodsReady || !cached.AutoLodsPending);
        }

        /// <summary>Draws an in-memory canonical asset, used by Model Editor before/while saving.</summary>
        public bool DrawAsset(
            GModelAsset asset,
            string projectPath,
            Matrix4x4 world,
            RuntimeModelAnimationState animation,
            IRenderController renderer,
            int lodPolicy = 0)
        {
            if (asset == null || renderer == null) return false;
            var queue = ModelRenderQueue.Rent();
            bool added = EnqueueAsset(
                queue, asset, projectPath, string.Empty, world,
                new Draw3DComponent { Visible = true, CastShadows = true },
                new ModelRendererComponent
                {
                    CastShadows = true,
                    ScaleX = 1f,
                    ScaleY = 1f,
                    ScaleZ = 1f,
                    LodPolicy = Math.Max(0, lodPolicy),
                },
                animation, renderer);
            if (added) queue.Draw(renderer);
            return added;
        }

        /// <summary>Draws a translucent, untextured animation pose for 3D onion-skin authoring.</summary>
        public bool DrawAssetGhost(
            GModelAsset asset,
            string projectPath,
            Matrix4x4 world,
            RuntimeModelAnimationState animation,
            RenderColor tint,
            float alpha,
            IRenderController renderer)
        {
            if (asset == null || renderer == null) return false;
            var queue = ModelRenderQueue.Rent();
            RuntimeModelAnimationState ghost = new(
                animation.ClipName, animation.TimeSeconds, animation.Fps, animation.Loop,
                flatUntextured: true);
            bool added = EnqueueAsset(
                queue, asset, projectPath, string.Empty, world,
                new Draw3DComponent { Visible = true, CastShadows = false },
                new ModelRendererComponent { ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f },
                ghost, renderer, tint, Math.Clamp(alpha, 0.02f, 0.9f));
            if (added) queue.Draw(renderer);
            return added;
        }

        private bool EnqueueAsset(
            IMeshDrawList queue,
            GModelAsset asset,
            string projectPath,
            string materialOverride,
            Matrix4x4 world,
            Draw3DComponent draw3d,
            ModelRendererComponent rendererComponent,
            RuntimeModelAnimationState animation,
            IRenderController renderer,
            RenderColor? tintOverride = null,
            float alphaMultiplier = 1f,
            int forcedLevel = -1,
            List<ModelGpuCache.CachedMesh> drawn = null)
        {
            if (queue == null || renderer == null || asset == null) return false;
            ModelGpuCache.CachedAsset gpuAsset = _gpu.GetOrCreate(renderer, asset);
            if (gpuAsset == null || gpuAsset.Meshes.Count == 0)
                return false;

            IReadOnlyDictionary<string, float> morphWeights = ModelMorphEvaluator.ResolveWeights(asset, animation);
            IReadOnlyList<ModelGpuCache.CachedMesh> renderMeshes = _gpu.ResolveMeshes(renderer, asset, gpuAsset, morphWeights);

            SkinPaletteHandle palette = _gpu.UpdatePalette(renderer, asset, gpuAsset, animation);
            ModelHairSelection hair = ModelHairRuntime.Resolve(asset, rendererComponent.Hair);
            Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
            Matrix4x4 pivotedWorld = pivot.LengthSquared() > 1e-12f
                ? Matrix4x4.CreateTranslation(-pivot) * world
                : world;

            // Level of detail. A positive policy pins an authored level. Otherwise the level follows
            // the model's size on screen: authored levels when the asset has them, automatically
            // simplified meshes when it does not. A negative policy always draws in full.
            int viewLevel = Math.Max(0, forcedLevel);
            if (forcedLevel < 0 && rendererComponent.LodPolicy == 0 && ModelLodView.Active && !animation.FlatUntextured && asset.Bounds != null)
            {
                Vector3 centre = Vector3.Transform((asset.Bounds.Min + asset.Bounds.Max) * 0.5f, pivotedWorld);
                float radius = Vector3.Distance(asset.Bounds.Min, asset.Bounds.Max) * 0.5f * MatrixScaleHelper.MaxScale(pivotedWorld);
                viewLevel = Math.Max(0, ModelLodView.LevelFor(centre, radius));
                ModelLodView.Count(viewLevel);
            }

            if (viewLevel > 0 && !gpuAsset.AutoLodsRequested) _gpu.RequestAutoLods(asset, gpuAsset);
            int activeLod = ResolveLodLevel(asset, Math.Max(viewLevel, Math.Max(0, rendererComponent.LodPolicy)));
            bool automatic = viewLevel > 0 && gpuAsset.AutoLodsReady && ReferenceEquals(renderMeshes, gpuAsset.Meshes);
            foreach (ModelGpuCache.CachedMesh mesh in renderMeshes)
            {
                if (mesh.Lod != activeLod) continue;
                if (rendererComponent.HiddenMeshes?.Contains(mesh.SourceName) == true) continue;
                if (!hair.IsVisible(mesh.SourceName)) continue;
                GModelMaterial material = ResolveMaterial(asset, mesh.MaterialIndex);
                MeshDrawFlags flags = MeshDrawFlags.None;
                TextureHandle texture = TextureHandle.Invalid;
                TextureHandle normalMap = TextureHandle.Invalid;
                TextureHandle ormMap = TextureHandle.Invalid;
                TextureHandle emissionMap = TextureHandle.Invalid;
                RenderColor tint = new(0.78f, 0.77f, 0.70f, 1f);
                float alpha = 1f;
                float emissive = 0f;
                // Scatter and other callers that keep what is enqueued as a template need it whole, now.
                bool background = BackgroundTextures && forcedLevel < 0;
                bool texturesPending = false;

                if (animation.FlatUntextured)
                {
                    flags |= MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.Emissive
                        | MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite;
                    tint = tintOverride ?? new RenderColor(0.78f, 0.77f, 0.70f, 1f);
                    alpha = Math.Clamp(alphaMultiplier, 0.02f, 0.9f);
                }
                else
                {
                    if (!draw3d.CastShadows || !rendererComponent.CastShadows) flags |= MeshDrawFlags.NoShadow;
                    if (!draw3d.ReceiveShadows || !rendererComponent.ReceiveShadows)
                        flags |= MeshDrawFlags.NoReceiveShadow;
                    if (material?.DoubleSided == true) flags |= MeshDrawFlags.NoCull;
                    if (material?.AlphaMode == GModelAlphaMode.Blend)
                        flags |= MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow;
                    bool hasOverride = !string.IsNullOrWhiteSpace(materialOverride);
                    texture = animation.IgnoreTextures ? TextureHandle.Invalid : LoadMaterialTexture(
                        renderer,
                        projectPath,
                        hasOverride ? materialOverride : material?.AlbedoTexture,
                        TextureColorSpace.Srgb, background, ref texturesPending);
                    if (!animation.IgnoreTextures && !hasOverride && material is not null)
                    {
                        normalMap = LoadMaterialTexture(renderer, projectPath, material.NormalTexture, TextureColorSpace.Linear, background, ref texturesPending);
                        ormMap = LoadMaterialTexture(renderer, projectPath, material.MetallicRoughnessTexture, TextureColorSpace.Linear, background, ref texturesPending);
                        emissionMap = LoadMaterialTexture(renderer, projectPath, material.EmissiveTexture, TextureColorSpace.Srgb, background, ref texturesPending);
                    }

                    // A worker is still reading one of them. Asking has started them all; the mesh waits.
                    if (texturesPending) continue;
                    tint = hasOverride ? RenderColor.White : ToRenderColor(material?.BaseColor ?? Vector4.One);
                    alpha = hasOverride ? 1f : material?.BaseColor.W ?? 1f;
                    Vector3 authoredEmission = material?.EmissiveFactor ?? Vector3.Zero;
                    emissive = MathF.Max(authoredEmission.X, MathF.Max(authoredEmission.Y, authoredEmission.Z));
                    // This instance's own light: a material given a strength of its own, then
                    // every material scaled together (windows coming on at dusk).
                    if (material != null && rendererComponent.MaterialEmission != null
                        && rendererComponent.MaterialEmission.TryGetValue(material.Name, out float ownEmission))
                        emissive = MathF.Max(0f, ownEmission);
                    if (rendererComponent.EmissionScale is float emissionScale) emissive *= MathF.Max(0f, emissionScale);
                    if (emissive > 0.001f)
                        flags |= MeshDrawFlags.Emissive | MeshDrawFlags.NoShadow;
                    // A glow over the whole model (a hit flash, a selection) leaves its shadow alone.
                    if (rendererComponent.Glow > 0.001f)
                    {
                        emissive += rendererComponent.Glow;
                        flags |= MeshDrawFlags.Emissive;
                    }
                }

                flags = MeshRasterDefaults.ApplyOverride(flags, asset.Culling, asset.WindingOrder);
                if (!animation.FlatUntextured && hair.TryGetColor(mesh.SourceName, out Vector3 hairColor))
                    tint = new RenderColor(hairColor.X, hairColor.Y, hairColor.Z, tint.A);
                if (material != null && rendererComponent.MaterialTints != null
                    && rendererComponent.MaterialTints.TryGetValue(material.Name, out Vector4 instanceTint))
                {
                    tint = new RenderColor(tint.R * instanceTint.X, tint.G * instanceTint.Y,
                        tint.B * instanceTint.Z, tint.A * instanceTint.W);
                    alpha *= instanceTint.W;
                }
                if (!animation.FlatUntextured && rendererComponent.Tint is Vector4 wholeTint)
                {
                    tint = new RenderColor(tint.R * wholeTint.X, tint.G * wholeTint.Y, tint.B * wholeTint.Z, tint.A * wholeTint.W);
                    alpha *= wholeTint.W;
                    // A model faded by its tint is drawn through, as a material authored to blend is.
                    if (wholeTint.W < 0.999f)
                        flags |= MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow;
                }
                flags = MeshRasterDefaults.ApplyOverride(
                    flags,
                    rendererComponent.Culling,
                    rendererComponent.WindingOrder);

                drawn?.Add(mesh);
                queue.Add(new MeshDrawCall
                {
                    // An animated mesh steps down one level later than a static one: a character
                    // is looked at more closely than a rock, and its joints bend.
                    Mesh = automatic ? mesh.ForLevel(mesh.IsSkinned ? viewLevel - 1 : viewLevel) : mesh.Mesh,
                    SkinPalette = mesh.IsSkinned ? palette : SkinPaletteHandle.Invalid,
                    World = pivotedWorld,
                    Texture = texture,
                    NormalMap = normalMap,
                    OrmMap = ormMap,
                    EmissionMap = emissionMap,
                    SurfaceParams = new Vector4(normalMap.IsValid ? 1f : 0f, 0f, emissive, 0f),
                    DetailParams = new Vector4(0f, 0f, 0f, MaterialUvScale(material)),
                    Tint = tint,
                    Alpha = alpha,
                    Emissive = emissive,
                    Flags = flags,
                });
            }
            return true;
        }

        /// <summary>Resolves a fixed authored LOD policy to an available level.</summary>
        public static int ResolveLodLevel(GModelAsset asset, int requested)
        {
            int desired = Math.Max(0, requested);
            if (asset?.Meshes is not { Count: > 0 }) return desired;

            int lowest = int.MaxValue;
            int best = int.MinValue;
            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh is null) continue;
                int level = mesh.Lod;
                if (level == desired) return desired;
                if (level < lowest) lowest = level;
                if (level <= desired && level > best) best = level;
            }
            if (best != int.MinValue) return best;
            return lowest == int.MaxValue ? desired : lowest;
        }

        public static Matrix4x4 TransformMatrix(TransformComponent transform, ModelRendererComponent model)
        {
            Vector3 scale = new(
                SafeScale(transform.ScaleX) * SafeScale(model.ScaleX),
                SafeScale(transform.ScaleY) * SafeScale(model.ScaleY),
                SafeScale(transform.ScaleZ) * SafeScale(model.ScaleZ));
            // Room loading mirrors yaw into the legacy angle for script compatibility.
            // Only angle-only legacy transforms should interpret that value as roll.
            float roll = transform.RotationX == 0f && transform.RotationY == 0f && transform.RotationZ == 0f
                ? transform.Rotation : transform.RotationZ;
            Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
                Deg(transform.RotationY),
                Deg(transform.RotationX),
                Deg(roll));
            return Matrix4x4.CreateScale(scale)
                 * rotation
                 * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);
        }

        private static GModelMaterial ResolveMaterial(GModelAsset asset, int index)
        {
            if (asset?.Materials == null || asset.Materials.Count == 0)
                return null;
            if (index < 0 || index >= asset.Materials.Count)
                index = 0;
            return asset.Materials[index];
        }

        private static RenderColor ToRenderColor(Vector4 c)
            => new(c.X, c.Y, c.Z, c.W <= 0f ? 1f : c.W);

        public static float MaterialUvScale(GModelMaterial material)
        {
            if (material?.Metadata is null
                || !material.Metadata.TryGetValue("UvScale", out string text)
                || !float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            {
                return 1f;
            }

            return Math.Clamp(value, 0.01f, 256f);
        }

        private TextureHandle LoadMaterialTexture(IRenderController renderer, string projectPath, string texturePath,
            TextureColorSpace colorSpace, bool background, ref bool pending)
        {
            var textures = _textures.GetValue(renderer, _ => new());
            var key = (projectPath, texturePath, colorSpace);
            long now = Environment.TickCount64;
            long assetGeneration = Genesis.Shared.Assets.RuntimeAssetPolicy.Generation;
            // A texture still being read is asked for once in each frame, however many meshes use
            // it, and again in the next frame; one that has arrived is trusted as before.
            if (textures.TryGetValue(key, out CachedTexture cached)
                && cached.AssetGeneration == assetGeneration
                && ((_frameOpen && cached.FrameGeneration == _frameGeneration)
                    || (!cached.Pending && now < cached.NextFreshnessCheckMilliseconds))
                && (!cached.Handle.IsValid || renderer.IsTextureLive(cached.Handle)))
            {
                pending |= cached.Pending;
                return cached.Handle;
            }

            TextureHandle texture = ResolveMaterialTexture(renderer, projectPath, texturePath, colorSpace, background, out bool waiting);
            pending |= waiting;
            cached ??= new CachedTexture();
            cached.Pending = waiting;
            cached.Handle = texture;
            cached.FrameGeneration = _frameGeneration;
            cached.AssetGeneration = assetGeneration;
            // Spread file-system freshness work across frames instead of producing a periodic
            // all-material hitch in large rooms. Project dependency notifications bump the asset
            // generation and so refresh immediately; this interval covers external edits.
            cached.NextFreshnessCheckMilliseconds = Genesis.Shared.Assets.RuntimeAssetPolicy.NextCheck(
                now, _textureFreshnessIntervalMilliseconds, key.GetHashCode());
            textures[key] = cached;
            return texture;
        }

        private static TextureHandle ResolveMaterialTexture(IRenderController renderer, string projectPath, string texturePath,
            TextureColorSpace colorSpace, bool background, out bool pending)
        {
            pending = false;
            string resolved = ResolveTexturePath(projectPath, texturePath);
            if (string.IsNullOrWhiteSpace(resolved)) return TextureHandle.Invalid;
            if (Genesis.Runtime.Assets.SpriteAssetLoader.IsSpriteDescriptorPath(resolved))
            {
                Genesis.Shared.Assets.SpriteRuntimeAsset image = Genesis.Runtime.Assets.SpriteAssetLoader.Load(resolved);
                resolved = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(resolved, image, 0);
            }
            Genesis.Shared.Assets.AssetIoCounters.Check();
            if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved)) return TextureHandle.Invalid;
            return background
                ? renderer.LoadTextureInBackground(resolved, colorSpace, out pending)
                : renderer.LoadTexture(resolved, colorSpace);
        }

        private static string ResolveTexturePath(string projectPath, string texturePath)
            => TexturePathResolver.Resolve(projectPath, texturePath);

        private static float SafeScale(float value) => value == 0f ? 1f : value;
        private static float Deg(float degrees) => degrees * (MathF.PI / 180f);
    }
}

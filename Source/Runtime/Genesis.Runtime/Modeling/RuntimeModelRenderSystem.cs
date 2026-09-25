using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;
using Genesis.Runtime.ECS.Components;

namespace Genesis.Runtime.Modeling
{
    public sealed class RuntimeModelRenderSystem
    {
        private readonly RuntimeModelAssetRegistry _assets;
        private readonly ModelGpuCache _gpu;
        private readonly Dictionary<(string Project, string Model), GModelAsset> _frameAssets = new();
        private readonly Dictionary<(IRenderController Renderer, string Project, string Texture), TextureHandle> _frameTextures = new();
        private bool _frameOpen;

        /// <summary>Resolve and freshness-check each distinct asset once during a scene submission.</summary>
        public void BeginFrame() { _frameAssets.Clear(); _frameTextures.Clear(); _frameOpen = true; }
        public void EndFrame() { _frameAssets.Clear(); _frameTextures.Clear(); _frameOpen = false; }

        private GModelAsset LoadModel(string project, string model)
        {
            if (!_frameOpen) return _assets.Load(project, model);
            var key = (project, model);
            if (!_frameAssets.TryGetValue(key, out GModelAsset asset))
                _frameAssets[key] = asset = _assets.Load(project, model);
            return asset;
        }

        public RuntimeModelRenderSystem(RuntimeModelAssetRegistry assets = null, ModelGpuCache gpu = null)
        {
            _assets = assets ?? new RuntimeModelAssetRegistry();
            _gpu = gpu ?? new ModelGpuCache();
        }

        public void InvalidateAssets(IRenderController renderer = null)
        {
            _assets.Clear();
            _frameAssets.Clear();
            _frameTextures.Clear();
            if (renderer != null) _gpu.Clear(renderer);
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
                    float blend = animator.BlendDuration <= 0f
                        ? 1f
                        : Math.Clamp(animator.BlendElapsed / animator.BlendDuration, 0f, 1f);
                    anim = new RuntimeModelAnimationState(
                        animator.ClipName,
                        animator.TimeSeconds,
                        animator.ClipFps,
                        animator.Loop,
                        previousClipName: animator.PreviousClipName,
                        previousTimeSeconds: animator.PreviousTimeSeconds,
                        blendFactor: blend,
                        preserveRootTransform: model.KeepPreviousTransform,
                        controller: animator.Controller);
                }
                if (world.Has<ModelMorphComponent>(entity))
                {
                    ref ModelMorphComponent morph = ref world.GetRef<ModelMorphComponent>(entity);
                    if (morph.Enabled && morph.Weights is { Count: > 0 })
                        anim = new RuntimeModelAnimationState(
                            anim.ClipName, anim.TimeSeconds, anim.Fps, anim.Loop, anim.FlatUntextured,
                            anim.PreviousClipName, anim.PreviousTimeSeconds, anim.BlendFactor,
                            anim.PreserveRootTransform, anim.Controller, anim.IgnoreTextures, morph.Weights);
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
            float alphaMultiplier = 1f)
        {
            if (queue == null || renderer == null || asset == null) return false;
            ModelGpuCache.CachedAsset gpuAsset = _gpu.GetOrCreate(renderer, asset);
            if (gpuAsset == null || gpuAsset.Meshes.Count == 0)
                return false;

            IReadOnlyDictionary<string, float> morphWeights = ModelMorphEvaluator.ResolveWeights(asset, animation);
            IReadOnlyList<ModelGpuCache.CachedMesh> renderMeshes = _gpu.ResolveMeshes(renderer, asset, gpuAsset, morphWeights);

            SkinPaletteHandle palette = _gpu.UpdatePalette(renderer, asset, gpuAsset, animation);
            int activeLod = ResolveLodLevel(asset, rendererComponent.LodPolicy);
            Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
            Matrix4x4 pivotedWorld = pivot.LengthSquared() > 1e-12f
                ? Matrix4x4.CreateTranslation(-pivot) * world
                : world;
            foreach (ModelGpuCache.CachedMesh mesh in renderMeshes)
            {
                if (mesh.Lod != activeLod) continue;
                GModelMaterial material = ResolveMaterial(asset, mesh.MaterialIndex);
                MeshDrawFlags flags = MeshDrawFlags.None;
                TextureHandle texture = TextureHandle.Invalid;
                TextureHandle normalMap = TextureHandle.Invalid;
                TextureHandle ormMap = TextureHandle.Invalid;
                TextureHandle emissionMap = TextureHandle.Invalid;
                RenderColor tint = new(0.78f, 0.77f, 0.70f, 1f);
                float alpha = 1f;
                float emissive = 0f;

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
                        hasOverride ? materialOverride : material?.AlbedoTexture);
                    if (!animation.IgnoreTextures && !hasOverride && material is not null)
                    {
                        normalMap = LoadMaterialTexture(renderer, projectPath, material.NormalTexture);
                        ormMap = LoadMaterialTexture(renderer, projectPath, material.MetallicRoughnessTexture);
                        emissionMap = LoadMaterialTexture(renderer, projectPath, material.EmissiveTexture);
                    }
                    tint = hasOverride ? RenderColor.White : ToRenderColor(material?.BaseColor ?? Vector4.One);
                    alpha = hasOverride ? 1f : material?.BaseColor.W ?? 1f;
                    Vector3 authoredEmission = material?.EmissiveFactor ?? Vector3.Zero;
                    emissive = MathF.Max(authoredEmission.X, MathF.Max(authoredEmission.Y, authoredEmission.Z));
                    if (emissive > 0.001f)
                        flags |= MeshDrawFlags.Emissive | MeshDrawFlags.NoShadow;
                }

                flags = MeshRasterDefaults.ApplyOverride(flags, asset.Culling, asset.WindingOrder);
                flags = MeshRasterDefaults.ApplyOverride(
                    flags,
                    rendererComponent.Culling,
                    rendererComponent.WindingOrder);

                queue.Add(new MeshDrawCall
                {
                    Mesh = mesh.Mesh,
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
            int[] available = asset?.Meshes?.Select(mesh => mesh.Lod).Distinct().OrderBy(level => level).ToArray()
                ?? Array.Empty<int>();
            if (available.Length == 0 || available.Contains(desired)) return desired;
            return available.Where(level => level <= desired).DefaultIfEmpty(available[0]).Max();
        }

        public static Matrix4x4 TransformMatrix(TransformComponent transform, ModelRendererComponent model)
        {
            Vector3 scale = new(
                SafeScale(transform.ScaleX) * SafeScale(model.ScaleX),
                SafeScale(transform.ScaleY) * SafeScale(model.ScaleY),
                SafeScale(transform.ScaleZ) * SafeScale(model.ScaleZ));
            Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
                Deg(transform.RotationY),
                Deg(transform.RotationX),
                Deg(transform.RotationZ != 0f ? transform.RotationZ : transform.Rotation));
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

        private static float MaterialUvScale(GModelMaterial material)
        {
            if (material?.Metadata is null
                || !material.Metadata.TryGetValue("UvScale", out string text)
                || !float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                return 1f;
            }

            return Math.Clamp(value, 0.01f, 256f);
        }

        private TextureHandle LoadMaterialTexture(IRenderController renderer, string projectPath, string texturePath)
        {
            if (!_frameOpen) return ResolveMaterialTexture(renderer, projectPath, texturePath);
            var key = (renderer, projectPath, texturePath);
            if (!_frameTextures.TryGetValue(key, out TextureHandle texture))
                _frameTextures[key] = texture = ResolveMaterialTexture(renderer, projectPath, texturePath);
            return texture;
        }

        private static TextureHandle ResolveMaterialTexture(IRenderController renderer, string projectPath, string texturePath)
        {
            string resolved = ResolveTexturePath(projectPath, texturePath);
            if (string.IsNullOrWhiteSpace(resolved)) return TextureHandle.Invalid;
            if (Genesis.Runtime.Assets.SpriteAssetLoader.IsSpriteDescriptorPath(resolved))
            {
                Genesis.Shared.Assets.SpriteRuntimeAsset image = Genesis.Runtime.Assets.SpriteAssetLoader.Load(resolved);
                resolved = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(resolved, image, 0);
            }
            return string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved)
                ? TextureHandle.Invalid
                : renderer.LoadTexture(resolved);
        }

        private static string ResolveTexturePath(string projectPath, string texturePath)
            => TexturePathResolver.Resolve(projectPath, texturePath);

        private static float SafeScale(float value) => value == 0f ? 1f : value;
        private static float Deg(float degrees) => degrees * (MathF.PI / 180f);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.Culling;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Textures;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;
using Genesis.Shared.Rendering;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Draw pass for object Draw2D/Draw3D components. Scripts configure; components draw.
    /// </summary>
    public static partial class ObjectDrawPass
    {
        private static readonly ConditionalWeakTable<IRenderController, RenderCache> RenderCaches = new();
        // The project watcher invalidates this cache immediately when authored assets change.
        // Bounded fallback checks cover external writers without resolving hundreds of identical
        // model and texture paths during every submitted frame.
        private static readonly RuntimeModelRenderSystem ModelRenderer = new(
            assets: RuntimeModelAssetRegistry.Shared,
            textureFreshnessIntervalMilliseconds: 1000);

        /// <summary>The model system every scene draw shares, so models are loaded and uploaded once.</summary>
        internal static RuntimeModelRenderSystem Models => ModelRenderer;

        private sealed class RenderCache
        {
            public readonly Dictionary<ShaderCacheKey, ShaderCacheEntry> Shaders = new();
            public MeshHandle CubeMesh = MeshHandle.Invalid;
            public MeshHandle TerrainPlane = MeshHandle.Invalid;
            public readonly Dictionary<string, ImageMaterialCacheEntry> ImageMaterials = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<SpriteFrameKey, SpriteFrameBinding> SpriteFrames = new();
            public readonly Dictionary<SpriteFrameKey, ImageMaterialCacheEntry> ImageMaterialRequests = new();
        }

        private readonly record struct SpriteFrameKey(string ProjectPath, string Image, int Frame,
            TextureColorSpace ColorSpace = TextureColorSpace.Display);

        /// <summary>
        /// Everything a sprite draw needs from disk, resolved once per (project, image, frame).
        /// Sprite draws used to resolve the same descriptor four times per object per frame
        /// (texture, pivot, UV rectangle, atlas remap), each resolution stat-ing the descriptor and
        /// frame image: roughly 20 file-system calls per sprite per frame, in shipped games too.
        /// Entries re-validate only after an explicit invalidation (<see cref="RuntimeAssetPolicy.Generation"/>)
        /// or the bounded, jittered fallback interval, which exported games disable entirely.
        /// </summary>
        private sealed class SpriteFrameBinding
        {
            public long Generation = long.MinValue;
            public long NextCheckMilliseconds;
            public bool Valid;
            public string TexturePath = string.Empty;
            public long TextureStampTicks;
            public TextureHandle Handle = TextureHandle.Invalid;
            public int Width = 32;
            public int Height = 32;
            public SpriteRuntimeAsset Asset;
            public Vector4 UvRect;
            public int AtlasVersion = int.MinValue;
            public TextureHandle AtlasHandle = TextureHandle.Invalid;
            public Vector4 AtlasRect;
        }

        private readonly record struct ShaderCacheKey(
            ShaderAssetPipeline Pipeline,
            string ProjectPath,
            string Shader,
            string Variant);

        private sealed class ShaderCacheEntry
        {
            public RuntimeShaderHandle Handle;
            public RuntimeShaderHandle[] PassHandles = Array.Empty<RuntimeShaderHandle>();
            public DateTime LastWriteUtc;
            public string SourcePath = string.Empty;
            public long NextFreshnessCheckMilliseconds;
            public long Generation;
            public ShaderAssetPipeline Pipeline;
            public ShaderAssetDocument Document;
            public Vector4 Row0, Row1, Row2, Row3;

            /// <summary>
            /// The document's resources with its variant applied, worked out once for this entry
            /// (the entry is replaced when the shader file changes) rather than for every draw of
            /// every frame, each of which built a dictionary, a list and a copy of each binding.
            /// </summary>
            public IReadOnlyList<ShaderResourceBinding> ResolvedResources;
        }

        private sealed class ShaderPassDrawList(
            IMeshDrawList target,
            ShaderCacheEntry shader,
            AuthoredShaderTextures textures) : IMeshDrawList
        {
            public int Count => target.Count;
            public void Clear() => target.Clear();
            public int CopyTo(MeshDrawCall[] buffer, int startIndex) => target.CopyTo(buffer, startIndex);
            public void Add(in MeshDrawCall call)
            {
                // A material with a shader of its own keeps it; the Object's shader covers the rest.
                if (call.Shader.IsValid)
                {
                    target.Add(call);
                    return;
                }
                foreach (RuntimeShaderHandle handle in shader.PassHandles)
                {
                    MeshDrawCall pass = call;
                    pass.AuthoredTextures = textures;
                    ApplyResolvedShader(shader, handle, ref pass);
                    target.Add(pass);
                }
            }
        }

        /// <summary>Releases renderer-owned live asset state before a room/preview is rebound.</summary>
        public static void InvalidateAssets(IRenderController renderer)
        {
            PixelRigSprite.InvalidateRenderer(renderer);
            ModelRenderer.InvalidateAssets(renderer);
            if (renderer == null || !RenderCaches.TryGetValue(renderer, out RenderCache cache)) return;
            foreach (SpriteFrameBinding frame in cache.SpriteFrames.Values)
                if (frame.Handle.IsValid) renderer.ReleaseTexture(frame.Handle);
            foreach (ShaderCacheEntry shader in cache.Shaders.Values)
            {
                ShaderPreviewProfile profile = shader.Pipeline == ShaderAssetPipeline.Mesh
                    ? ShaderPreviewProfile.MeshPipeline : ShaderPreviewProfile.SpritePipeline;
                foreach (RuntimeShaderHandle handle in shader.PassHandles)
                    if (handle.IsValid) renderer.ReleaseRuntimeShader(handle, profile);
            }
            if (cache.CubeMesh.IsValid) renderer.ReleaseMesh(cache.CubeMesh);
            if (cache.TerrainPlane.IsValid) renderer.ReleaseMesh(cache.TerrainPlane);
            foreach (ImageMaterialCacheEntry material in cache.ImageMaterials.Values) ReleaseImageMaterial(renderer, material);
            RenderCaches.Remove(renderer);
        }

        public static void SubmitMeshes3D(
            EcsWorld world,
            string projectPath,
            MeshDrawCall[] buffer,
            ref int count,
            IRenderController renderer,
            Vector3 cameraEye,
            Vector3 cameraForward,
            Matrix4x4 viewProjection = default)
        {
            if (world == null || renderer == null || buffer == null) return;

            ModelRenderer.BeginFrame();
            try
            {
                SubmitMeshes3DFrame(world, projectPath, buffer, ref count, renderer,
                    cameraEye, cameraForward, viewProjection);
            }
            finally
            {
                ModelRenderer.EndFrame();
            }
        }

        private static void SubmitMeshes3DFrame(
            EcsWorld world,
            string projectPath,
            MeshDrawCall[] buffer,
            ref int count,
            IRenderController renderer,
            Vector3 cameraEye,
            Vector3 cameraForward,
            Matrix4x4 viewProjection)
        {

            bool cull = RenderAutoState.FrustumCulling;
            bool occlude = RenderAutoState.OcclusionCulling;
            bool haveVp = viewProjection.M44 != 0f || viewProjection.M11 != 0f;
            Frustum frustum = default;
            if ((cull || occlude) && haveVp)
                frustum = new Frustum(viewProjection);
            else
                cull = false;

            int drawCount = count;
            world.Query<TransformComponent, Draw3DComponent>((entity, ref transform, ref draw3d) =>
            {
                if (!draw3d.Visible || drawCount >= buffer.Length) return;

                Vector3 pos = new(transform.X, transform.Y, transform.Z);
                bool measured = false;
                float radius = BoundsHelper.BoundingRadiusFromScale(
                    transform.ScaleX, transform.ScaleY, transform.ScaleZ, baseRadius: 1.2f);
                // Authored geometry can be much larger than a unit cube and offset from its
                // Object origin. Test the same pivot, scale and rotation used by the draw.
                if (world.Has<ModelRendererComponent>(entity))
                {
                    ref ModelRendererComponent modelBounds = ref world.GetRef<ModelRendererComponent>(entity);
                    bool bounded;
                    Vector3 min = default, max = default;
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("model bounds for culling"))
                        bounded = !string.IsNullOrWhiteSpace(modelBounds.ModelAsset)
                            && ModelRenderer.TryGetBounds(projectPath, modelBounds.ModelAsset, out min, out max, includePivot: true);
                    if (bounded)
                    {
                        Matrix4x4 matrix = RuntimeModelRenderSystem.TransformMatrix(transform, modelBounds);
                        pos = Vector3.Transform((min + max) * .5f, matrix);
                        radius = Vector3.Distance(min, max) * .5f * MatrixScaleHelper.MaxScale(matrix);
                        measured = true;
                    }
                }
                if (cull && !IsPosedByAnimation(world, entity)
                    && !Visibility.IsVisible(frustum, viewProjection, pos, radius, occlude))
                    return;
                // A model that would cover a pixel or two costs a draw call and shows nothing.
                if (measured && ModelLodView.Active && ModelLodView.LevelFor(pos, radius) < 0)
                {
                    ModelLodView.Count(-1);
                    return;
                }

                bool hasAssets = ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets);
                if (hasAssets && assets.Is3D == false) return;

                if (draw3d.Procedural && ProceduralMeshDrawRegistry.TryGet(entity, out MeshDrawCall[] procedural))
                {
                    int added = 0;
                    for (int i = 0; i < procedural.Length && drawCount < buffer.Length; i++)
                    {
                        MeshDrawCall call = procedural[i];
                        if (hasAssets)
                            added += AppendMeshShaderPasses(renderer, projectPath, assets, call, buffer, ref drawCount);
                        else
                        {
                            buffer[drawCount++] = call;
                            added++;
                        }
                    }
                    RenderAutoState.SubmittedMeshes += added;
                    return;
                }

                if (world.Has<ModelRendererComponent>(entity))
                {
                    ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(entity);
                    if (!string.IsNullOrWhiteSpace(model.ModelAsset))
                    {
                        RuntimeModelAnimationState anim = ReadAnimation(world, entity, model.KeepPreviousTransform);
                        var queue = ModelRenderQueue.Rent();
                        bool enqueued;
                        using (Genesis.Shared.Diagnostics.LoadProfile.Begin("models enqueued"))
                            enqueued = ModelRenderer.Enqueue(queue, projectPath, model.ModelAsset, model.MaterialOverride,
                                RuntimeModelRenderSystem.TransformMatrix(transform, model), draw3d, model, anim, renderer);
                        if (enqueued)
                        {
                            int before = drawCount;
                            drawCount = queue.CopyTo(buffer, drawCount);
                            if (hasAssets && assets.TerrainTextureMode != null)
                                for (int index = before; index < drawCount; index++)
                                    ApplyTerrainImageMaterial(renderer, projectPath, assets, ReadSpriteFrameIndex(world, entity), ref buffer[index]);
                            bool shaded;
                            ShaderCacheEntry shader = default;
                            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("Object's mesh shader"))
                                shaded = hasAssets && drawCount > before
                                    && TryResolveShader(renderer, projectPath, assets, ShaderAssetPipeline.Mesh, out shader);
                            if (shaded)
                            {
                                int originalCount = drawCount - before;
                                int passCount = Math.Max(1, shader.PassHandles.Length);
                                int retainedOriginals = Math.Min(originalCount, (buffer.Length - before) / passCount);
                                BindAuthoredTextures(renderer, projectPath, assets, shader, ShaderAssetPipeline.Mesh, ref buffer[before].AuthoredTextures);
                                AuthoredShaderTextures textures = buffer[before].AuthoredTextures;
                                for (int original = retainedOriginals - 1; original >= 0; original--)
                                {
                                    MeshDrawCall source = buffer[before + original];
                                    source.AuthoredTextures = textures;
                                    for (int pass = passCount - 1; pass >= 0; pass--)
                                    {
                                        MeshDrawCall expanded = source;
                                        ApplyResolvedShader(shader, shader.PassHandles[pass], ref expanded);
                                        buffer[before + original * passCount + pass] = expanded;
                                    }
                                }
                                drawCount = before + retainedOriginals * passCount;
                            }
                            RenderAutoState.SubmittedMeshes += drawCount - before;
                            return;
                        }
                    }
                }

                if (!hasAssets)
                    return;

                string image = ResolveImageName(entity, world, assets);
                if (!HasAuthoredGeometry(assets, image)) return;
                int frameIndex = ReadSpriteFrameIndex(world, entity);
                TextureHandle tex = TextureHandle.Invalid;
                if (assets.TerrainTextureMode == null && !string.IsNullOrWhiteSpace(image)) TryGetTexture(renderer, projectPath, image, frameIndex, out tex, out _, out _, TextureColorSpace.Srgb);

                MeshDrawCall cube = assets.TerrainTextureMode == null
                    ? ImageCube(renderer, assets, transform, draw3d, tex)
                    : TerrainTexture(renderer, projectPath, assets, transform, draw3d, cameraEye, frameIndex);
                int cubePasses = AppendMeshShaderPasses(renderer, projectPath, assets, cube, buffer, ref drawCount);
                RenderAutoState.SubmittedMeshes += cubePasses;
                if ((cube.Flags & MeshDrawFlags.NoShadow) == 0) RenderAutoState.ShadowCastersSubmitted++;
            });
            count = drawCount;
        }

        /// <summary>
        /// World position of the first live instance of <paramref name="objectName"/>, for a
        /// viewport following a target. Uses the same identity rule as PGSL collision queries, so a
        /// viewport cannot follow a different "Player" from the one a script finds.
        /// </summary>
        public static bool TryFindInstancePosition(
            EcsWorld world,
            string objectName,
            out float x,
            out float y,
            out float z)
        {
            float foundX = 0f, foundY = 0f, foundZ = 0f;
            bool found = false;

            if (world != null && !string.IsNullOrWhiteSpace(objectName))
            {
                for (int pass = 0; pass < 2 && !found; pass++)
                world.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
                {
                    if (found) return;
                    if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets)) return;
                    bool identity = string.Equals(assets.RoomNodeId, objectName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(assets.InstanceName, objectName, StringComparison.OrdinalIgnoreCase);
                    if (pass == 0 ? !identity : !Genesis.Runtime.Scene.ObjectNameMatcher.Matches(assets.Prefab, objectName)) return;

                    foundX = transform.X;
                    foundY = transform.Y;
                    foundZ = transform.Z;
                    if (world.Has<Genesis.Shared.ECS.Components.Transform3DComponent>(entity))
                    {
                        Vector3 position = world.GetRef<Genesis.Shared.ECS.Components.Transform3DComponent>(entity).Position;
                        foundX = position.X; foundY = position.Y; foundZ = position.Z;
                    }
                    found = true;
                });
            }

            x = foundX; y = foundY; z = foundZ;
            return found;
        }

        public static void Render2D(
            EcsWorld world,
            string projectPath,
            IRenderCommandSink commands,
            IRenderController renderer,
            float camX,
            float camY,
            float zoom)
        {
            if (world == null || renderer == null || commands == null) return;
            world.Query<TransformComponent, Draw2DComponent>((entity, ref transform, ref draw2d) =>
            {
                if (!draw2d.Visible) return;
                if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets))
                    return;
                if (assets.Is3D == true) return;
                if (QueuePixelRig(world, entity, projectPath, commands, renderer, assets, transform, draw2d, camX, camY, zoom)) return;

                string image = ResolveImageName(entity, world, assets);
                if (string.IsNullOrWhiteSpace(image)) return;
                int frameIndex = ReadSpriteFrameIndex(world, entity);
                float blend = SpriteTransitionBlend(assets);
                if (assets.HasSpriteTransition)
                    QueueSprite2D(projectPath, commands, renderer, assets, transform, draw2d,
                        assets.SpriteTransitionPreviousImage, assets.SpriteTransitionPreviousFrame,
                        assets.SpriteAlpha * (1f - blend), camX, camY, zoom);
                QueueSprite2D(projectPath, commands, renderer, assets, transform, draw2d,
                    image, frameIndex, assets.SpriteAlpha * blend, camX, camY, zoom);
            });
        }

        public static void DrawEntity3D(
            EcsWorld world,
            Entity entity,
            string projectPath,
            IRenderController renderer,
            Vector3 cameraEye,
            Vector3 cameraForward)
        {
            // Queue then flush — never leave per-entity ModelRenderQueue allocations on the hot path.
            var queue = ModelRenderQueue.Rent();
            EnqueueEntity3D(world, entity, projectPath, renderer, cameraEye, cameraForward, queue);
            queue.Draw(renderer);
        }

        public static void EnqueueEntity3D(
            EcsWorld world,
            Entity entity,
            string projectPath,
            IRenderController renderer,
            Vector3 cameraEye,
            Vector3 cameraForward,
            IMeshDrawList queue)
        {
            if (queue == null || world == null || !world.IsAlive(entity) || !world.Has<Draw3DComponent>(entity)) return;

            ref Draw3DComponent draw3d = ref world.GetRef<Draw3DComponent>(entity);
            if (!draw3d.Visible) return;
            bool hasVisualAssets = ObjectDrawAssetRegistry.TryGet(entity, out var visualAssets);
            if (hasVisualAssets && visualAssets.Is3D == false) return;

            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);

            if (draw3d.Procedural && ProceduralMeshDrawRegistry.TryGet(entity, out MeshDrawCall[] procedural))
            {
                for (int i = 0; i < procedural.Length; i++)
                {
                    if (hasVisualAssets)
                        AppendMeshShaderPasses(renderer, projectPath, visualAssets, procedural[i], queue);
                    else
                        queue.Add(procedural[i]);
                }
                return;
            }

            if (world.Has<ModelRendererComponent>(entity))
            {
                ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(entity);
                IMeshDrawList destination = queue;
                if (hasVisualAssets
                    && TryResolveShader(renderer, projectPath, visualAssets, ShaderAssetPipeline.Mesh, out ShaderCacheEntry modelShader))
                {
                    AuthoredShaderTextures textures = default;
                    BindAuthoredTextures(renderer, projectPath, visualAssets, modelShader, ShaderAssetPipeline.Mesh, ref textures);
                    destination = new ShaderPassDrawList(queue, modelShader, textures);
                }
                if (!string.IsNullOrWhiteSpace(model.ModelAsset)
                    && ModelRenderer.Enqueue(hasVisualAssets && visualAssets.TerrainTextureMode != null
                        ? new ImageMaterialDrawList(destination, renderer, projectPath, visualAssets, ReadSpriteFrameIndex(world, entity)) : destination,
                        projectPath, model.ModelAsset, model.MaterialOverride,
                        RuntimeModelRenderSystem.TransformMatrix(transform, model), draw3d, model,
                        ReadAnimation(world, entity, model.KeepPreviousTransform), renderer))
                {
                    return;
                }
            }

            if (!hasVisualAssets)
                return;
            ObjectDrawAssetEntry assets = visualAssets;

            string image = ResolveImageName(entity, world, assets);
            if (!HasAuthoredGeometry(assets, image)) return;
            int frameIndex = ReadSpriteFrameIndex(world, entity);
            TextureHandle tex = TextureHandle.Invalid;
            if (assets.TerrainTextureMode == null && !string.IsNullOrWhiteSpace(image)) TryGetTexture(renderer, projectPath, image, frameIndex, out tex, out _, out _, TextureColorSpace.Srgb);

            MeshDrawCall cube = assets.TerrainTextureMode == null
                ? ImageCube(renderer, assets, transform, draw3d, tex)
                : TerrainTexture(renderer, projectPath, assets, transform, draw3d, cameraEye, frameIndex);
            AppendMeshShaderPasses(renderer, projectPath, assets, cube, queue);
        }

        private static bool HasAuthoredGeometry(ObjectDrawAssetEntry assets, string image)
            => !string.IsNullOrWhiteSpace(image) || !string.IsNullOrWhiteSpace(assets.Model)
                || !string.IsNullOrWhiteSpace(assets.Material) || !string.IsNullOrWhiteSpace(assets.Shader)
                || assets.TerrainTextureMode != null;

        private static MeshDrawCall ImageCube(IRenderController renderer, ObjectDrawAssetEntry assets,
            TransformComponent transform, Draw3DComponent draw, TextureHandle texture)
        {
            var cache = RenderCaches.GetOrCreateValue(renderer);
            if (!cache.CubeMesh.IsValid) cache.CubeMesh = MeshGeometry.RegisterCube(renderer, RenderColor.White);
            const float radians = MathF.PI / 180;
            MeshDrawFlags flags = MeshRasterDefaults.ApplyOverride(MeshDrawFlags.None, assets.Culling, assets.WindingOrder);
            if (!draw.CastShadows) flags |= MeshDrawFlags.NoShadow;
            if (!draw.ReceiveShadows) flags |= MeshDrawFlags.NoReceiveShadow;
            return new MeshDrawCall
            {
                Mesh = cache.CubeMesh, Texture = texture, Tint = RenderColor.White, Alpha = assets.SpriteAlpha, Flags = flags,
                World = Matrix4x4.CreateScale(SafeScale(transform.ScaleX), SafeScale(transform.ScaleY), SafeScale(transform.ScaleZ))
                    * Matrix4x4.CreateFromYawPitchRoll(transform.RotationY * radians, transform.RotationX * radians, transform.RotationZ * radians)
                    * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z),
            };
        }

        public static void DrawEntity2D(
            EcsWorld world,
            Entity entity,
            string projectPath,
            IRenderController renderer,
            float camX,
            float camY,
            float zoom)
        {
            var sink = new FrameRenderQueue();
            EnqueueEntity2D(world, entity, projectPath, renderer, camX, camY, zoom, sink);
            sink.Flush(renderer, includeMeshes: false, includeSprites: true);
        }

        public static void EnqueueEntity2D(
            EcsWorld world,
            Entity entity,
            string projectPath,
            IRenderController renderer,
            float camX,
            float camY,
            float zoom,
            IRenderCommandSink commands)
        {
            if (commands == null || world == null || !world.IsAlive(entity) || !world.Has<Draw2DComponent>(entity)) return;
            ref Draw2DComponent draw2d = ref world.GetRef<Draw2DComponent>(entity);
            if (!draw2d.Visible) return;

            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets))
                return;

            if (assets.Is3D == true) return;
            if (QueuePixelRig(world, entity, projectPath, commands, renderer, assets, transform, draw2d, camX, camY, zoom)) return;
            string image = ResolveImageName(entity, world, assets);
            if (string.IsNullOrWhiteSpace(image)) return;
            int frameIndex = ReadSpriteFrameIndex(world, entity);
            float blend = SpriteTransitionBlend(assets);
            if (assets.HasSpriteTransition)
                QueueSprite2D(projectPath, commands, renderer, assets, transform, draw2d,
                    assets.SpriteTransitionPreviousImage, assets.SpriteTransitionPreviousFrame,
                    assets.SpriteAlpha * (1f - blend), camX, camY, zoom);
            QueueSprite2D(projectPath, commands, renderer, assets, transform, draw2d,
                image, frameIndex, assets.SpriteAlpha * blend, camX, camY, zoom);
        }

        private static bool QueuePixelRig(EcsWorld world, Entity entity, string projectPath,
            IRenderCommandSink commands, IRenderController renderer, ObjectDrawAssetEntry assets,
            TransformComponent transform, Draw2DComponent draw, float camX, float camY, float zoom)
        {
            if (!world.Has<PixelRigSpriteComponent>(entity)) return false;
            PixelRigSprite binding = world.GetRef<PixelRigSpriteComponent>(entity).Binding;
            if (binding == null || binding.IsDisposed) return false;
            if (assets.SpriteAlpha <= 0) return true;
            TextureHandle texture = binding.GetTexture(renderer);
            if (!texture.IsValid) return true;
            float scaleX = SafeScale(transform.ScaleX) * zoom, scaleY = SafeScale(transform.ScaleY) * zoom;
            PixelRigPlayer player = binding.Player;
            assets.SpritePixelWidth = player.Width; assets.SpritePixelHeight = player.Height;
            assets.SpriteOriginX = binding.OriginX / player.Width; assets.SpriteOriginY = binding.OriginY / player.Height;
            SpriteDrawCall call = new()
            {
                Texture = texture, X = camX + transform.X * zoom, Y = camY + transform.Y * zoom,
                Width = player.Width * scaleX, Height = player.Height * scaleY,
                OriginX = binding.OriginX * scaleX, OriginY = binding.OriginY * scaleY,
                Rotation = transform.Rotation, Alpha = Math.Clamp(assets.SpriteAlpha, 0f, 1f),
                Tint = RenderColor.White, Depth = (int)draw.Depth, UvRect = new Vector4(0, 0, 1, 1),
            };
            // Dynamic pixels must not be redirected to the immutable sprite atlas. Authored
            // shader passes and the existing frame queue still own material/depth/viewport state.
            DrawSpriteShaderPasses(commands, renderer, projectPath, assets, call);
            return true;
        }

        private static float SpriteTransitionBlend(ObjectDrawAssetEntry assets) =>
            assets?.HasSpriteTransition == true
                ? Math.Clamp(assets.SpriteTransitionElapsed / Math.Max(0.0001f, assets.SpriteTransitionDuration), 0f, 1f)
                : 1f;

        public static void QueueSprite2D(
            string projectPath,
            IRenderCommandSink commands,
            IRenderController renderer,
            ObjectDrawAssetEntry assets,
            TransformComponent transform,
            Draw2DComponent draw2d,
            string image,
            int frameIndex,
            float alpha,
            float camX,
            float camY,
            float zoom,
            RenderColor? tint = null,
            System.Drawing.RectangleF? destination = null,
            System.Drawing.RectangleF? sourcePart = null,
            Vector4 clip = default)
        {
            if (alpha <= 0f || string.IsNullOrWhiteSpace(image)) return;
            SpriteFrameBinding frame = ResolveSpriteFrame(renderer, projectPath, image, frameIndex);
            if (frame == null) return;
            TextureHandle texture = frame.Handle;
            int textureWidth = frame.Width;
            int textureHeight = frame.Height;

            float width = textureWidth * SafeScale(transform.ScaleX) * zoom;
            float height = textureHeight * SafeScale(transform.ScaleY) * zoom;
            ResolveSpriteDisplayPivot(frame, image, frameIndex, textureWidth, textureHeight,
                width, height, out float originX, out float originY);
            if (destination is System.Drawing.RectangleF bounds)
            {
                width = bounds.Width;
                height = bounds.Height;
                originX = originY = 0;
                transform.X = bounds.X;
                transform.Y = bounds.Y;
            }
            // The debugger consumes the same resolved frame geometry as rendering. Keeping this
            // on the per-entity draw entry avoids guessing size, pivot, or collider from names.
            assets.SpritePixelWidth = textureWidth;
            assets.SpritePixelHeight = textureHeight;
            assets.SpriteOriginX = width > 0f ? originX / width : 0.5f;
            assets.SpriteOriginY = height > 0f ? originY / height : 0.5f;
            SpriteDrawCall call = new()
            {
                Texture = texture,
                X = camX + transform.X * zoom,
                Y = camY + transform.Y * zoom,
                Width = width,
                Height = height,
                OriginX = originX,
                OriginY = originY,
                Rotation = transform.Rotation,
                Alpha = Math.Clamp(alpha, 0f, 1f),
                Tint = tint ?? RenderColor.White,
                Depth = (int)draw2d.Depth,
                UvRect = frame.UvRect,
                ClipRect = clip,
            };
            if (sourcePart is System.Drawing.RectangleF part)
            {
                // A fraction of the frame: narrow its texture rectangle before the atlas remap,
                // which maps whatever rectangle it is given.
                Vector4 uv = call.UvRect == Vector4.Zero ? new Vector4(0f, 0f, 1f, 1f) : call.UvRect;
                float u0 = Math.Clamp(part.Left, 0f, 1f), u1 = Math.Clamp(part.Right, 0f, 1f);
                float v0 = Math.Clamp(part.Top, 0f, 1f), v1 = Math.Clamp(part.Bottom, 0f, 1f);
                if (u1 <= u0 || v1 <= v0) return;
                call.UvRect = new Vector4(
                    uv.X + ((uv.Z - uv.X) * u0), uv.Y + ((uv.W - uv.Y) * v0),
                    uv.X + ((uv.Z - uv.X) * u1), uv.Y + ((uv.W - uv.Y) * v1));
            }
            RemapToTextureGroupAtlas(frame, ref call);
            DrawSpriteShaderPasses(commands, renderer, projectPath, assets, call);
        }

        private static void RemapToTextureGroupAtlas(SpriteFrameBinding frame, ref SpriteDrawCall call)
        {
            int version = RuntimeTextureAtlas.Version;
            if (frame.AtlasVersion != version)
            {
                frame.AtlasVersion = version;
                if (!RuntimeTextureAtlas.TryGetPlacement(frame.TexturePath, out frame.AtlasHandle, out frame.AtlasRect))
                    frame.AtlasHandle = TextureHandle.Invalid;
            }
            if (!frame.AtlasHandle.IsValid) return;
            call.Texture = frame.AtlasHandle;
            call.UvRect = RuntimeTextureAtlas.Remap(frame.AtlasRect, call.UvRect);
        }

        private static void ApplyShader(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            ShaderAssetPipeline expectedPipeline,
            ref SpriteDrawCall call)
        {
            if (!TryResolveShader(renderer, projectPath, assets, expectedPipeline, out ShaderCacheEntry shader)) return;
            ApplyResolvedShader(shader, shader.Handle, ref call);
            BindAuthoredTextures(renderer, projectPath, assets, shader, expectedPipeline, ref call.AuthoredTextures);
        }

        private static void ApplyShader(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            ShaderAssetPipeline expectedPipeline,
            ref MeshDrawCall call)
        {
            if (!TryResolveShader(renderer, projectPath, assets, expectedPipeline, out ShaderCacheEntry shader)) return;
            ApplyResolvedShader(shader, shader.Handle, ref call);
            BindAuthoredTextures(renderer, projectPath, assets, shader, expectedPipeline, ref call.AuthoredTextures);
        }

        private static void ApplyResolvedShader(
            ShaderCacheEntry shader,
            RuntimeShaderHandle handle,
            ref SpriteDrawCall call)
        {
            call.Shader = handle;
            call.ShaderParams0 = shader.Row0; call.ShaderParams1 = shader.Row1;
            call.ShaderParams2 = shader.Row2; call.ShaderParams3 = shader.Row3;
        }

        private static void ApplyResolvedShader(
            ShaderCacheEntry shader,
            RuntimeShaderHandle handle,
            ref MeshDrawCall call)
        {
            call.Shader = handle;
            call.ShaderParams0 = shader.Row0; call.ShaderParams1 = shader.Row1;
            call.ShaderParams2 = shader.Row2; call.ShaderParams3 = shader.Row3;
        }

        private static int AppendMeshShaderPasses(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            in MeshDrawCall source,
            MeshDrawCall[] buffer,
            ref int count)
        {
            if (count >= buffer.Length) return 0;
            MeshDrawCall call = source;
            if (!TryResolveShader(renderer, projectPath, assets, ShaderAssetPipeline.Mesh, out ShaderCacheEntry shader))
            {
                buffer[count++] = call;
                return 1;
            }

            BindAuthoredTextures(renderer, projectPath, assets, shader, ShaderAssetPipeline.Mesh, ref call.AuthoredTextures);
            int added = 0;
            foreach (RuntimeShaderHandle handle in shader.PassHandles)
            {
                if (count >= buffer.Length) break;
                MeshDrawCall pass = call;
                ApplyResolvedShader(shader, handle, ref pass);
                buffer[count++] = pass;
                added++;
            }
            return added;
        }

        private static void AppendMeshShaderPasses(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            in MeshDrawCall source,
            IMeshDrawList target)
        {
            MeshDrawCall call = source;
            if (!TryResolveShader(renderer, projectPath, assets, ShaderAssetPipeline.Mesh, out ShaderCacheEntry shader))
            {
                target.Add(call);
                return;
            }

            BindAuthoredTextures(renderer, projectPath, assets, shader, ShaderAssetPipeline.Mesh, ref call.AuthoredTextures);
            foreach (RuntimeShaderHandle handle in shader.PassHandles)
            {
                MeshDrawCall pass = call;
                ApplyResolvedShader(shader, handle, ref pass);
                target.Add(pass);
            }
        }

        private static void DrawSpriteShaderPasses(
            IRenderCommandSink commands,
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            in SpriteDrawCall source)
        {
            SpriteDrawCall call = source;
            if (!TryResolveShader(renderer, projectPath, assets, ShaderAssetPipeline.Sprite, out ShaderCacheEntry shader))
            {
                commands.DrawSprite(call);
                return;
            }

            BindAuthoredTextures(renderer, projectPath, assets, shader, ShaderAssetPipeline.Sprite, ref call.AuthoredTextures);
            foreach (RuntimeShaderHandle handle in shader.PassHandles)
            {
                SpriteDrawCall pass = call;
                ApplyResolvedShader(shader, handle, ref pass);
                commands.DrawSprite(pass);
            }
        }

        private static void BindAuthoredTextures(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            ShaderCacheEntry shader,
            ShaderAssetPipeline pipeline,
            ref AuthoredShaderTextures textures)
        {
            ShaderAssetDocument document = shader?.Document;
            if (document == null) return;
            // The usual mesh shader consumes only pipeline-owned textures. Avoid constructing
            // a merged dictionary and list for every Object when it declares no authored
            // resources or active resource variant.
            if (document.Resources is not { Count: > 0 }
                && (string.IsNullOrWhiteSpace(document.ActiveVariant)
                    || document.FindActiveVariant()?.ResourceOverrides is not { Count: > 0 }))
            {
                return;
            }
            IReadOnlyList<ShaderResourceBinding> resources = shader.ResolvedResources ??= document.ResolveResources();
            for (int index = 0; index < resources.Count; index++)
            {
                ShaderResourceBinding resource = resources[index];
                if (!ShaderResourceReflection.IsTextureKind(resource.Kind)) continue;
                if (ShaderResourceReflection.IsPipelineOwned(pipeline, resource)) continue;
                string binding = resource.Binding;
                if (assets.ShaderResources.TryGetValue(resource.Name, out string overridePath)
                    && !string.IsNullOrWhiteSpace(overridePath))
                {
                    binding = overridePath;
                }

                if (string.IsNullOrWhiteSpace(binding)) continue;
                if (!TryGetTexture(renderer, projectPath, binding, 0, out TextureHandle handle, out _, out _))
                    continue;
                textures.Add(resource.Slot, handle);
            }
        }

        /// <summary>
        /// Compiles (or reuses) an authored mesh pixel shader and writes it onto a draw call.
        /// Returns false when the asset is missing, is not a mesh pipeline, or fails to compile.
        /// </summary>
        public static bool TryApplyMeshShader(
            IRenderController renderer,
            string projectPath,
            string shaderAsset,
            ref MeshDrawCall call) =>
            TryApplyMeshShader(renderer, projectPath, shaderAsset, null, null, ref call);

        /// <summary>
        /// The same with an instance's own shader values and texture overrides (a script's mesh draw
        /// uses the values its instance set with ShaderSetParameter).
        /// </summary>
        public static bool TryApplyMeshShader(
            IRenderController renderer,
            string projectPath,
            string shaderAsset,
            IReadOnlyDictionary<string, float[]> parameters,
            IReadOnlyDictionary<string, string> resources,
            ref MeshDrawCall call)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(shaderAsset)) return false;
            try
            {
                _materialShaderLookup ??= new ObjectDrawAssetEntry();
                _materialShaderLookup.Shader = shaderAsset;
                _materialShaderLookup.ShaderParameters.Clear();
                _materialShaderLookup.ShaderResources.Clear();
                if (parameters != null)
                    foreach (KeyValuePair<string, float[]> parameter in parameters)
                        _materialShaderLookup.ShaderParameters[parameter.Key] = parameter.Value;
                if (resources != null)
                    foreach (KeyValuePair<string, string> resource in resources)
                        _materialShaderLookup.ShaderResources[resource.Key] = resource.Value;
                if (!TryResolveShader(renderer, projectPath, _materialShaderLookup, ShaderAssetPipeline.Mesh, out ShaderCacheEntry shader)
                    || shader == null
                    || !shader.Handle.IsValid)
                {
                    return false;
                }

                // The textures the shader declares, as for a placed Object's or a material's shader:
                // a script's DrawModelShader3D sees the same resources.
                BindAuthoredTextures(renderer, projectPath, _materialShaderLookup, shader, ShaderAssetPipeline.Mesh, ref call.AuthoredTextures);
                call.Shader = shader.Handle;
                call.ShaderParams0 = shader.Row0;
                call.ShaderParams1 = shader.Row1;
                call.ShaderParams2 = shader.Row2;
                call.ShaderParams3 = shader.Row3;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Binds an authored mesh shader onto a water volume draw and drops the engine water pass
        /// so the assigned pixel shader actually runs.
        /// </summary>
        public static bool TryApplyAuthoredWaterShader(
            IRenderController renderer,
            string projectPath,
            string shaderAsset,
            ref MeshDrawCall call)
        {
            if (!TryApplyMeshShader(renderer, projectPath, shaderAsset, ref call)) return false;
            call.Flags = MeshDrawFlags.Transparent
                | MeshDrawFlags.NoCull
                | MeshDrawFlags.NoShadow
                | MeshDrawFlags.NoDepthWrite;
            return true;
        }

        [ThreadStatic] private static ObjectDrawAssetEntry _materialShaderLookup;

        /// <summary>
        /// Applies a material's own mesh Shader resource to one of its draws (its first pass, with the
        /// shader's own parameter values). False leaves the draw as it was.
        /// </summary>
        internal static bool TryApplyMaterialShader(
            IRenderController renderer, string projectPath, string shaderName, ref MeshDrawCall call)
        {
            if (string.IsNullOrWhiteSpace(shaderName)) return false;
            _materialShaderLookup ??= new ObjectDrawAssetEntry();
            _materialShaderLookup.Shader = shaderName.Trim();
            _materialShaderLookup.ShaderParameters.Clear();
            _materialShaderLookup.ShaderResources.Clear();
            if (!TryResolveShader(renderer, projectPath, _materialShaderLookup, ShaderAssetPipeline.Mesh, out ShaderCacheEntry shader))
                return false;
            RuntimeShaderHandle handle = shader.PassHandles.Length > 0 ? shader.PassHandles[0] : shader.Handle;
            if (!handle.IsValid) return false;
            BindAuthoredTextures(renderer, projectPath, _materialShaderLookup, shader, ShaderAssetPipeline.Mesh, ref call.AuthoredTextures);
            ApplyResolvedShader(shader, handle, ref call);
            return true;
        }

        private static bool TryResolveShader(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            ShaderAssetPipeline expectedPipeline,
            out ShaderCacheEntry result)
        {
            result = null;
            if (renderer == null || assets == null || string.IsNullOrWhiteSpace(assets.Shader)) return false;
            RenderCache cache = RenderCaches.GetOrCreateValue(renderer);
            string variantKey = assets.ShaderVariant ?? string.Empty;
            ShaderCacheKey key = new(expectedPipeline, projectPath, assets.Shader, variantKey);
            long now = Environment.TickCount64;
            long generation = RuntimeAssetPolicy.Generation;
            if (cache.Shaders.TryGetValue(key, out ShaderCacheEntry cached)
                && cached.Generation == generation
                && now < cached.NextFreshnessCheckMilliseconds)
            {
                ShaderParameterReflection.Pack(cached.Document, assets.ShaderParameters,
                    out cached.Row0, out cached.Row1, out cached.Row2, out cached.Row3);
                result = cached;
                return cached.Handle.IsValid;
            }

            // The start-up warm-up is still making this shader on a worker: draw with the engine's
            // own shading for these frames rather than hold the frame up until it is made.
            if (cached is null && ProjectShaderWarmup.IsPending(assets.Shader)) return false;

            string path = ResourceNames.Resolve(projectPath, assets.Shader, ResourceType.Shader);
            AssetIoCounters.Check(2);
            if (!File.Exists(path)) return false;
            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (cached is not null
                && string.Equals(cached.SourcePath, path, StringComparison.OrdinalIgnoreCase)
                && cached.LastWriteUtc == modified)
            {
                ShaderParameterReflection.Pack(cached.Document, assets.ShaderParameters,
                    out cached.Row0, out cached.Row1, out cached.Row2, out cached.Row3);
                cached.NextFreshnessCheckMilliseconds = RuntimeAssetPolicy.NextCheck(
                    now, RuntimeAssetPolicy.FramePathIntervalMilliseconds, key.GetHashCode());
                cached.Generation = generation;
                result = cached;
                return cached.Handle.IsValid;
            }

            if (cached is not null)
            {
                cache.Shaders.Remove(key);
                ShaderPreviewProfile cachedProfile = expectedPipeline == ShaderAssetPipeline.Mesh
                    ? ShaderPreviewProfile.MeshPipeline
                    : ShaderPreviewProfile.SpritePipeline;
                foreach (RuntimeShaderHandle cachedHandle in cached.PassHandles)
                    if (cachedHandle.IsValid) renderer.ReleaseRuntimeShader(cachedHandle, cachedProfile);
            }

            ShaderAssetDocument document = ShaderAssetDocument.Load(path);
            if (document.Pipeline != expectedPipeline) return false;
            if (!string.IsNullOrWhiteSpace(assets.ShaderVariant))
                document.ActiveVariant = assets.ShaderVariant;
            ShaderParameterReflection.Synchronize(document);
            ShaderParameterReflection.Pack(document, assets.ShaderParameters,
                out Vector4 row0, out Vector4 row1, out Vector4 row2, out Vector4 row3);
            ShaderPreviewProfile profile = expectedPipeline == ShaderAssetPipeline.Mesh
                ? ShaderPreviewProfile.MeshPipeline : ShaderPreviewProfile.SpritePipeline;
            int activePass = document.ActivePassIndex;
            string activeVariant = document.ActiveVariant;
            List<RuntimeShaderHandle> passHandles = [];
            try
            {
                foreach (ShaderPassDefinition pass in document.Passes.Where(pass => pass.Enabled))
                {
                    document.Source = pass.Source;
                    document.Entry = string.IsNullOrWhiteSpace(pass.Entry) ? "MainPS" : pass.Entry;
                    document.VertexEntry = pass.VertexEntry?.Trim() ?? string.Empty;
                    string source = document.ResolveCompiledSource();
                    RuntimeShaderHandle passHandle = expectedPipeline == ShaderAssetPipeline.Mesh
                        ? renderer.RegisterRuntimeMeshPass(source, pass, path, projectPath)
                        : string.IsNullOrWhiteSpace(document.VertexEntry)
                        ? renderer.RegisterRuntimeShader(source, document.Entry, profile, path, projectPath)
                        : renderer.RegisterRuntimeShaderProgram(
                            source,
                            document.VertexEntry,
                            document.Entry,
                            profile,
                            path,
                            projectPath);
                    if (passHandle.IsValid) passHandles.Add(passHandle);
                }
            }
            catch
            {
                foreach (RuntimeShaderHandle passHandle in passHandles)
                    renderer.ReleaseRuntimeShader(passHandle, profile);
                throw;
            }
            finally
            {
                document.ActivePassIndex = Math.Clamp(activePass, 0, Math.Max(0, document.Passes.Count - 1));
                document.ActiveVariant = activeVariant;
                if (document.Passes.Count > 0)
                {
                    ShaderPassDefinition active = document.Passes[document.ActivePassIndex];
                    document.Source = active.Source;
                    document.Entry = active.Entry;
                    document.VertexEntry = active.VertexEntry;
                }
            }

            RuntimeShaderHandle handle = passHandles.Count > 0 ? passHandles[0] : RuntimeShaderHandle.Invalid;
            result = new ShaderCacheEntry
            {
                Handle = handle,
                PassHandles = passHandles.ToArray(),
                SourcePath = path,
                NextFreshnessCheckMilliseconds = RuntimeAssetPolicy.NextCheck(
                    now, RuntimeAssetPolicy.FramePathIntervalMilliseconds, key.GetHashCode()),
                Generation = generation,
                LastWriteUtc = modified,
                Pipeline = expectedPipeline,
                Document = document,
                Row0 = row0, Row1 = row1, Row2 = row2, Row3 = row3,
            };
            cache.Shaders[key] = result;
            return handle.IsValid;
        }

        public static void ApplyDrawComponents(
            EcsWorld world,
            Entity entity,
            string spriteName,
            string modelName,
            float spriteAlpha,
            Vector3 modelScale,
            bool is3D,
            bool hasDraw2D,
            bool hasDraw3D,
            bool procedural3D,
            string materialPath = null)
        {
            var assets = new ObjectDrawAssetEntry
            {
                Image = spriteName,
                Model = modelName,
                Is3D = is3D,
                Material = materialPath,
                SpriteAlpha = spriteAlpha,
                ModelScaleX = modelScale.X,
                ModelScaleY = modelScale.Y,
                ModelScaleZ = modelScale.Z,
            };
            ObjectDrawAssetRegistry.Set(entity, assets);

            // A prefab-level `sprite` is a first-class runtime sprite, not merely a texture hint.
            // Without SpriteComponent the object could draw frame zero, but PGSL SpriteSet/
            // AnimationPlay had nowhere to persist frame/tag/speed state. Collision scripts also
            // fell back to a generic centred 32x32 box because SyncToContext could not recover the
            // authored sprite. That made foot-origin platformer actors embed in the floor and made
            // animated actors appear to slide. Keep explicitly-authored SpriteComponent settings,
            // but materialise the component for the common GameMaker-style root sprite path.
            if (!string.IsNullOrWhiteSpace(spriteName) && !world.Has<SpriteComponent>(entity))
            {
                world.Set(entity, new SpriteComponent
                {
                    ImageSpeed = 0f,
                    AnimationTagIndex = -1,
                    AnimationLoopOverride = -1,
                    Alpha = spriteAlpha,
                    Depth = 0,
                });
            }

            if (hasDraw2D || !string.IsNullOrWhiteSpace(spriteName))
            {
                Draw2DComponent draw2d = world.Has<Draw2DComponent>(entity)
                    ? world.GetRef<Draw2DComponent>(entity)
                    : new Draw2DComponent { Visible = true, Depth = 0f };
                world.Set(entity, draw2d);
            }

            if (is3D || hasDraw3D || !string.IsNullOrWhiteSpace(modelName) || procedural3D)
            {
                Draw3DComponent draw3d = world.Has<Draw3DComponent>(entity)
                    ? world.GetRef<Draw3DComponent>(entity)
                    : new Draw3DComponent
                    {
                        Visible = true,
                        CastShadows = true,
                        ReceiveShadows = true,
                    };
                draw3d.Procedural = procedural3D;
                world.Set(entity, draw3d);

                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    world.Set(entity, new ModelRendererComponent
                    {
                        ModelAsset = modelName,
                        MaterialOverride = materialPath,
                        ScaleX = modelScale.X == 0f ? 1f : modelScale.X,
                        ScaleY = modelScale.Y == 0f ? 1f : modelScale.Y,
                        ScaleZ = modelScale.Z == 0f ? 1f : modelScale.Z,
                        CastShadows = draw3d.CastShadows,
                        ReceiveShadows = draw3d.ReceiveShadows,
                    });
                }
            }
            else if (is3D && !string.IsNullOrWhiteSpace(spriteName))
            {
                Draw3DComponent draw3d = world.Has<Draw3DComponent>(entity)
                    ? world.GetRef<Draw3DComponent>(entity)
                    : new Draw3DComponent
                    {
                        Visible = true,
                        CastShadows = false,
                        ReceiveShadows = true,
                    };
                draw3d.Procedural = false;
                world.Set(entity, draw3d);
            }
        }

        public static void ConfigureFromPrefabProps(
            Entity entity,
            string image,
            string model,
            float spriteAlpha,
            float scaleX,
            float scaleY,
            float scaleZ,
            bool visible,
            bool castShadows,
            bool receiveShadows)
        {
            var entry = ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry existing)
                ? existing
                : new ObjectDrawAssetEntry();
            if (!string.IsNullOrWhiteSpace(image)) entry.Image = image.Trim();
            if (!string.IsNullOrWhiteSpace(model)) entry.Model = model.Trim();
            entry.SpriteAlpha = spriteAlpha;
            entry.ModelScaleX = scaleX;
            entry.ModelScaleY = scaleY;
            entry.ModelScaleZ = scaleZ;
            ObjectDrawAssetRegistry.Set(entity, entry);
        }

        private static string ResolveImageName(Entity entity, EcsWorld world, ObjectDrawAssetEntry assets)
        {
            if (!string.IsNullOrWhiteSpace(assets?.Image))
                return assets.Image.Trim();
            return null;
        }

        /// <summary>
        /// True when a clip or controller is posing this Object's model this frame.
        /// </summary>
        /// <remarks>
        /// A model's stored box is its bind pose. A clip can carry the character out of it (sitting,
        /// lying down, a jump with root motion) and the pose is only known once it has been
        /// evaluated, so such an Object is not rejected by that box: the renderer tests each of its
        /// meshes against the mesh's posed bound instead.
        /// </remarks>
        internal static bool IsPosedByAnimation(EcsWorld world, Entity entity)
        {
            if (!world.Has<ModelAnimatorComponent>(entity)) return false;
            ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
            return animator.Controller != null || !string.IsNullOrWhiteSpace(animator.ClipName);
        }

        private static RuntimeModelAnimationState ReadAnimation(
            EcsWorld world,
            Entity entity,
            bool preserveRootTransform = false)
        {
            RuntimeModelAnimationState state = default;
            if (world != null && world.IsAlive(entity) && world.Has<ModelAnimatorComponent>(entity))
            {
                ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
                state = RuntimeModelAnimationState.From(animator, preserveRootTransform);
            }
            if (world != null && world.IsAlive(entity) && world.Has<ModelMorphComponent>(entity))
            {
                ref ModelMorphComponent morph = ref world.GetRef<ModelMorphComponent>(entity);
                if (morph.Enabled && morph.Weights is { Count: > 0 })
                    state = state.WithMorphWeights(morph.Weights, preserveRootTransform);
            }
            return state;
        }

        private static int ReadSpriteFrameIndex(EcsWorld world, Entity entity)
        {
            if (world != null && world.IsAlive(entity) && world.Has<SpriteComponent>(entity))
                return world.GetRef<SpriteComponent>(entity).ImageIndex;
            return 0;
        }

        /// <summary>An Image's first frame as a texture (for script-built meshes); invalid when there is none.</summary>
        internal static TextureHandle ResolveImageTexture(IRenderController renderer, string projectPath, string image) =>
            TryGetTexture(renderer, projectPath, image, 0, out TextureHandle handle, out _, out _) ? handle : TextureHandle.Invalid;

        private static bool TryGetTexture(
            IRenderController renderer,
            string projectPath,
            string imageName,
            int frameIndex,
            out TextureHandle handle,
            out int width,
            out int height,
            TextureColorSpace colorSpace = TextureColorSpace.Display)
        {
            handle = TextureHandle.Invalid;
            width = 32;
            height = 32;
            SpriteFrameBinding frame = ResolveSpriteFrame(renderer, projectPath, imageName, frameIndex, colorSpace);
            if (frame == null) return false;
            handle = frame.Handle;
            width = frame.Width;
            height = frame.Height;
            return handle.IsValid;
        }

        private static SpriteFrameBinding ResolveSpriteFrame(
            IRenderController renderer,
            string projectPath,
            string imageName,
            int frameIndex,
            TextureColorSpace colorSpace = TextureColorSpace.Display)
        {
            if (string.IsNullOrWhiteSpace(imageName) || renderer == null) return null;
            RenderCache cache = RenderCaches.GetOrCreateValue(renderer);
            SpriteFrameKey key = new(projectPath ?? string.Empty, imageName, frameIndex, colorSpace);
            long now = Environment.TickCount64;
            long generation = RuntimeAssetPolicy.Generation;
            if (cache.SpriteFrames.TryGetValue(key, out SpriteFrameBinding frame)
                && frame.Generation == generation
                && now < frame.NextCheckMilliseconds)
            {
                if (!frame.Valid) return null;
                if (renderer.IsTextureLive(frame.Handle)) return frame;
                // Editor previews can release a shared texture while gameplay still has its draw
                // entry. Resolve through the renderer's asset cache again so that entry cannot
                // retain a released handle and turn a live cross-editor sprite white.
                frame.Handle = renderer.LoadTexture(frame.TexturePath, key.ColorSpace);
                return frame.Handle.IsValid ? frame : null;
            }

            if (frame == null)
            {
                frame = new SpriteFrameBinding();
                cache.SpriteFrames[key] = frame;
            }
            RefreshSpriteFrame(frame, renderer, projectPath, imageName, frameIndex, colorSpace);
            frame.Generation = generation;
            frame.NextCheckMilliseconds = RuntimeAssetPolicy.NextCheck(
                now, RuntimeAssetPolicy.FramePathIntervalMilliseconds, key.GetHashCode());
            return frame.Valid ? frame : null;
        }

        private static void RefreshSpriteFrame(
            SpriteFrameBinding frame,
            IRenderController renderer,
            string projectPath,
            string imageName,
            int frameIndex,
            TextureColorSpace colorSpace)
        {
            frame.Valid = false;
            frame.Asset = null;
            string texturePath;
            try
            {
                string descriptor = SpriteAssetLoader.ResolveDescriptorPath(projectPath, imageName);
                if (string.IsNullOrWhiteSpace(descriptor)) return;
                if (SpriteAssetLoader.IsSpriteDescriptorPath(descriptor))
                {
                    AssetIoCounters.Check();
                    if (!File.Exists(descriptor)) return;
                    frame.Asset = SpriteAssetLoader.Load(descriptor);
                    SpriteAssetLoader.RegisterAsset(imageName, frame.Asset);
                    texturePath = SpriteAssetLoader.ResolveFrameTexturePath(descriptor, frame.Asset, frameIndex);
                }
                else
                {
                    // Private raw-payload compatibility: the reference already names the image file.
                    texturePath = descriptor;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                return;
            }

            AssetIoCounters.Check(2);
            if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath)) return;
            long stamp = File.GetLastWriteTimeUtc(texturePath).Ticks;
            if (!string.Equals(texturePath, frame.TexturePath, StringComparison.OrdinalIgnoreCase)
                || stamp != frame.TextureStampTicks)
            {
                TryReadImageSize(texturePath, out frame.Width, out frame.Height);
                frame.TexturePath = texturePath;
                frame.TextureStampTicks = stamp;
                frame.AtlasVersion = int.MinValue;
            }

            frame.Handle = renderer.LoadTexture(texturePath, colorSpace);
            if (!frame.Handle.IsValid) return;
            frame.UvRect = frame.Asset != null
                ? SpriteOriginUtility.ResolveFrameUvRect(frame.Asset, frameIndex, frame.Width, frame.Height)
                : Vector4.Zero;
            frame.Valid = true;
        }

        private static void ResolveSpriteDisplayPivot(
            SpriteFrameBinding frame,
            string imageName,
            int frameIndex,
            int textureWidth,
            int textureHeight,
            float displayWidth,
            float displayHeight,
            out float originX,
            out float originY)
        {
            originX = displayWidth * 0.5f;
            originY = displayHeight * 0.5f;
            SpriteRuntimeAsset asset = frame.Asset;
            if (asset == null) return;
            SpriteRuntimeOrigin origin = SpritePlayback.ResolveOrigin(asset, frameIndex);

            // NEXT-092: an origin that resolves off the sprite makes an object vanish while every
            // other part of it — movement, audio, collision — keeps working, which is the hardest
            // kind of failure to diagnose from the game. Say so once per sprite instead.
            if (SpriteOriginUtility.IsImplausibleNormalizedOrigin(origin)
                && ReportedOriginWarnings.Add(imageName))
            {
                Genesis.Runtime.Debugger.RuntimeDiagnostics.ReportAssetProblem(
                    SpriteOriginUtility.DescribeImplausibleOrigin(origin, imageName));
            }

            (originX, originY) = SpriteOriginUtility.ResolveDisplayPivot(
                origin,
                textureWidth,
                textureHeight,
                displayWidth,
                displayHeight);
        }

        /// <summary>One report per sprite, so a per-frame draw path cannot flood the log.</summary>
        private static readonly HashSet<string> ReportedOriginWarnings =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<(string Project, string Image), (int Width, int Height, long Generation, long Next)> ImageSizes = new();

        /// <summary>
        /// The pixel size of an Image's first frame as a sprite draws it, without a renderer: for
        /// scripts that lay pictures out. Asked of the disk again after an asset invalidation,
        /// otherwise at most once per polling interval.
        /// </summary>
        public static bool TryGetImageFrameSize(string projectPath, string imageName, out int width, out int height)
        {
            width = height = 0;
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(imageName)) return false;
            var key = (projectPath, imageName);
            long now = Environment.TickCount64;
            long generation = RuntimeAssetPolicy.Generation;
            lock (ImageSizes)
            {
                if (ImageSizes.TryGetValue(key, out var known) && known.Generation == generation && now < known.Next)
                {
                    width = known.Width;
                    height = known.Height;
                    return width > 0;
                }
            }

            try
            {
                string descriptor = SpriteAssetLoader.ResolveDescriptorPath(projectPath, imageName);
                if (!string.IsNullOrWhiteSpace(descriptor) && File.Exists(descriptor))
                {
                    SpriteRuntimeAsset asset = null;
                    string texturePath = descriptor;
                    if (SpriteAssetLoader.IsSpriteDescriptorPath(descriptor))
                    {
                        asset = SpriteAssetLoader.Load(descriptor);
                        texturePath = SpriteAssetLoader.ResolveFrameTexturePath(descriptor, asset, 0);
                    }
                    if (!string.IsNullOrEmpty(texturePath) && File.Exists(texturePath))
                    {
                        TryReadImageSize(texturePath, out width, out height);
                        Vector4 uv = asset != null ? SpriteOriginUtility.ResolveFrameUvRect(asset, 0, width, height) : Vector4.Zero;
                        if (uv != Vector4.Zero)
                        {
                            width = Math.Max(1, (int)MathF.Round(width * (uv.Z - uv.X)));
                            height = Math.Max(1, (int)MathF.Round(height * (uv.W - uv.Y)));
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                width = height = 0;
            }

            lock (ImageSizes)
                ImageSizes[key] = (width, height, generation,
                    RuntimeAssetPolicy.NextCheck(now, RuntimeAssetPolicy.FramePathIntervalMilliseconds, key.GetHashCode()));
            return width > 0;
        }

        private static void TryReadImageSize(string path, out int w, out int h)
        {
            w = 32;
            h = 32;
            try
            {
                // Header only: decoding every pixel just to learn the size doubled the cost of the
                // first draw of each sprite frame (and of every edit).
                AssetIoCounters.Read();
                using var fs = File.OpenRead(path);
                StbImageSharp.ImageInfo? info = StbImageSharp.ImageInfo.FromStream(fs);
                if (info is { } header && header.Width > 0 && header.Height > 0)
                {
                    w = header.Width;
                    h = header.Height;
                    return;
                }
                fs.Position = 0;
                var image = StbImageSharp.ImageResult.FromStream(fs, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                w = Math.Max(1, image.Width);
                h = Math.Max(1, image.Height);
            }
            catch { /* keep defaults */ }
        }

        private static float SafeScale(float s) => s == 0f ? 1f : s;
    }
}

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
using Genesis.Shared.Rendering;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Draw pass for object Draw2D/Draw3D components. Scripts configure; components draw.
    /// </summary>
    public static class ObjectDrawPass
    {
        private static readonly ConditionalWeakTable<IRenderController, RenderCache> RenderCaches = new();
        private static readonly RuntimeModelRenderSystem ModelRenderer = new();

        private sealed class RenderCache
        {
            public readonly Dictionary<string, TextureCacheEntry> Textures = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, ShaderCacheEntry> Shaders = new(StringComparer.OrdinalIgnoreCase);
            public MeshHandle CubeMesh = MeshHandle.Invalid;
        }

        private sealed class ShaderCacheEntry
        {
            public RuntimeShaderHandle Handle;
            public RuntimeShaderHandle[] PassHandles = Array.Empty<RuntimeShaderHandle>();
            public DateTime LastWriteUtc;
            public ShaderAssetPipeline Pipeline;
            public ShaderAssetDocument Document;
            public Vector4 Row0, Row1, Row2, Row3;
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
                foreach (RuntimeShaderHandle handle in shader.PassHandles)
                {
                    MeshDrawCall pass = call;
                    pass.AuthoredTextures = textures;
                    ApplyResolvedShader(shader, handle, ref pass);
                    target.Add(pass);
                }
            }
        }

        private sealed class TextureCacheEntry
        {
            public TextureHandle Handle;
            public int Width = 32;
            public int Height = 32;
        }

        /// <summary>Releases renderer-owned live asset state before a room/preview is rebound.</summary>
        public static void InvalidateAssets(IRenderController renderer)
        {
            PixelRigSprite.InvalidateRenderer(renderer);
            ModelRenderer.InvalidateAssets(renderer);
            if (renderer == null || !RenderCaches.TryGetValue(renderer, out RenderCache cache)) return;
            foreach (TextureCacheEntry texture in cache.Textures.Values)
                if (texture.Handle.IsValid) renderer.ReleaseTexture(texture.Handle);
            foreach (ShaderCacheEntry shader in cache.Shaders.Values)
            {
                ShaderPreviewProfile profile = shader.Pipeline == ShaderAssetPipeline.Mesh
                    ? ShaderPreviewProfile.MeshPipeline : ShaderPreviewProfile.SpritePipeline;
                foreach (RuntimeShaderHandle handle in shader.PassHandles)
                    if (handle.IsValid) renderer.ReleaseRuntimeShader(handle, profile);
            }
            if (cache.CubeMesh.IsValid) renderer.ReleaseMesh(cache.CubeMesh);
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
                float radius = BoundsHelper.BoundingRadiusFromScale(
                    transform.ScaleX, transform.ScaleY, transform.ScaleZ, baseRadius: 1.2f);
                if (cull && !Visibility.IsVisible(frustum, viewProjection, pos, radius, occlude))
                    return;

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
                        if (ModelRenderer.Enqueue(queue, projectPath, model.ModelAsset, model.MaterialOverride,
                            RuntimeModelRenderSystem.TransformMatrix(transform, model), draw3d, model, anim, renderer))
                        {
                            int before = drawCount;
                            drawCount = queue.CopyTo(buffer, drawCount);
                            if (hasAssets && drawCount > before
                                && TryResolveShader(renderer, projectPath, assets, ShaderAssetPipeline.Mesh, out ShaderCacheEntry shader))
                            {
                                int originalCount = drawCount - before;
                                int passCount = Math.Max(1, shader.PassHandles.Length);
                                int retainedOriginals = Math.Min(originalCount, (buffer.Length - before) / passCount);
                                BindAuthoredTextures(renderer, projectPath, assets, shader.Document, ShaderAssetPipeline.Mesh, ref buffer[before].AuthoredTextures);
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
                int frameIndex = ReadSpriteFrameIndex(world, entity);
                TextureHandle tex = TextureHandle.Invalid;
                if (!string.IsNullOrWhiteSpace(image)) TryGetTexture(renderer, projectPath, image, frameIndex, out tex, out _, out _);

                MeshDrawCall cube = ImageCube(renderer, assets, transform, draw3d, tex);
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
                    BindAuthoredTextures(renderer, projectPath, visualAssets, modelShader.Document, ShaderAssetPipeline.Mesh, ref textures);
                    destination = new ShaderPassDrawList(queue, modelShader, textures);
                }
                if (!string.IsNullOrWhiteSpace(model.ModelAsset)
                    && ModelRenderer.Enqueue(destination, projectPath, model.ModelAsset, model.MaterialOverride,
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
            int frameIndex = ReadSpriteFrameIndex(world, entity);
            TextureHandle tex = TextureHandle.Invalid;
            if (!string.IsNullOrWhiteSpace(image)) TryGetTexture(renderer, projectPath, image, frameIndex, out tex, out _, out _);

            MeshDrawCall cube = ImageCube(renderer, assets, transform, draw3d, tex);
            AppendMeshShaderPasses(renderer, projectPath, assets, cube, queue);
        }

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
            RenderColor? tint = null)
        {
            if (alpha <= 0f || string.IsNullOrWhiteSpace(image)
                || !TryGetTexture(renderer, projectPath, image, frameIndex,
                    out TextureHandle texture, out int textureWidth, out int textureHeight))
            {
                return;
            }

            float width = textureWidth * SafeScale(transform.ScaleX) * zoom;
            float height = textureHeight * SafeScale(transform.ScaleY) * zoom;
            ResolveSpriteDisplayPivot(projectPath, image, frameIndex, textureWidth, textureHeight,
                width, height, out float originX, out float originY);
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
                UvRect = ResolveFrameUvRect(projectPath, image, frameIndex, textureWidth, textureHeight),
            };
            RemapToTextureGroupAtlas(projectPath, image, frameIndex, ref call);
            DrawSpriteShaderPasses(commands, renderer, projectPath, assets, call);
        }

        private static void RemapToTextureGroupAtlas(
            string projectPath,
            string image,
            int frameIndex,
            ref SpriteDrawCall call)
        {
            string path = ResolveImagePath(projectPath, image, frameIndex);
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (RuntimeTextureAtlas.TryRemap(path, call.UvRect, out TextureHandle atlas, out Vector4 atlasUv))
            {
                call.Texture = atlas;
                call.UvRect = atlasUv;
            }
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
            BindAuthoredTextures(renderer, projectPath, assets, shader.Document, expectedPipeline, ref call.AuthoredTextures);
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
            BindAuthoredTextures(renderer, projectPath, assets, shader.Document, expectedPipeline, ref call.AuthoredTextures);
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

            BindAuthoredTextures(renderer, projectPath, assets, shader.Document, ShaderAssetPipeline.Mesh, ref call.AuthoredTextures);
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

            BindAuthoredTextures(renderer, projectPath, assets, shader.Document, ShaderAssetPipeline.Mesh, ref call.AuthoredTextures);
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

            BindAuthoredTextures(renderer, projectPath, assets, shader.Document, ShaderAssetPipeline.Sprite, ref call.AuthoredTextures);
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
            ShaderAssetDocument document,
            ShaderAssetPipeline pipeline,
            ref AuthoredShaderTextures textures)
        {
            if (document == null) return;
            foreach (ShaderResourceBinding resource in document.ResolveResources())
            {
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
            ref MeshDrawCall call)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(shaderAsset)) return false;
            try
            {
                var assets = new ObjectDrawAssetEntry { Shader = shaderAsset };
                if (!TryResolveShader(renderer, projectPath, assets, ShaderAssetPipeline.Mesh, out ShaderCacheEntry shader)
                    || shader == null
                    || !shader.Handle.IsValid)
                {
                    return false;
                }

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

        private static bool TryResolveShader(
            IRenderController renderer,
            string projectPath,
            ObjectDrawAssetEntry assets,
            ShaderAssetPipeline expectedPipeline,
            out ShaderCacheEntry result)
        {
            result = null;
            if (renderer == null || assets == null || string.IsNullOrWhiteSpace(assets.Shader)) return false;
            string path = ResourceNames.Resolve(projectPath, assets.Shader, ResourceType.Shader);
            if (!File.Exists(path)) return false;

            RenderCache cache = RenderCaches.GetOrCreateValue(renderer);
            string variantKey = assets.ShaderVariant ?? string.Empty;
            string key = expectedPipeline + "|" + path + "|" + variantKey;
            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (cache.Shaders.TryGetValue(key, out ShaderCacheEntry cached) && cached.LastWriteUtc == modified)
            {
                ShaderParameterReflection.Pack(cached.Document, assets.ShaderParameters,
                    out Vector4 cached0, out Vector4 cached1, out Vector4 cached2, out Vector4 cached3);
                result = new ShaderCacheEntry
                {
                    Handle = cached.Handle, PassHandles = cached.PassHandles, LastWriteUtc = cached.LastWriteUtc,
                    Pipeline = cached.Pipeline, Document = cached.Document,
                    Row0 = cached0, Row1 = cached1, Row2 = cached2, Row3 = cached3,
                };
                return cached.Handle.IsValid;
            }

            if (cached is not null)
            {
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
                    RuntimeShaderHandle passHandle = string.IsNullOrWhiteSpace(document.VertexEntry)
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

            if (passHandles.Count == 0) return false;
            RuntimeShaderHandle handle = passHandles[0];
            result = new ShaderCacheEntry
            {
                Handle = handle,
                PassHandles = passHandles.ToArray(),
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

        private static RuntimeModelAnimationState ReadAnimation(
            EcsWorld world,
            Entity entity,
            bool preserveRootTransform = false)
        {
            RuntimeModelAnimationState state = default;
            if (world != null && world.IsAlive(entity) && world.Has<ModelAnimatorComponent>(entity))
            {
                ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
                float blend = animator.BlendDuration <= 0f
                    ? 1f
                    : Math.Clamp(animator.BlendElapsed / animator.BlendDuration, 0f, 1f);
                state = new RuntimeModelAnimationState(
                    animator.ClipName,
                    animator.TimeSeconds,
                    animator.ClipFps,
                    animator.Loop,
                    previousClipName: animator.PreviousClipName,
                    previousTimeSeconds: animator.PreviousTimeSeconds,
                    blendFactor: blend,
                    preserveRootTransform: preserveRootTransform,
                    controller: animator.Controller);
            }
            if (world != null && world.IsAlive(entity) && world.Has<ModelMorphComponent>(entity))
            {
                ref ModelMorphComponent morph = ref world.GetRef<ModelMorphComponent>(entity);
                if (morph.Enabled && morph.Weights is { Count: > 0 })
                    state = new RuntimeModelAnimationState(
                        state.ClipName, state.TimeSeconds, state.Fps, state.Loop, state.FlatUntextured,
                        state.PreviousClipName, state.PreviousTimeSeconds, state.BlendFactor,
                        preserveRootTransform, state.Controller, state.IgnoreTextures, morph.Weights);
            }
            return state;
        }

        private static int ReadSpriteFrameIndex(EcsWorld world, Entity entity)
        {
            if (world != null && world.IsAlive(entity) && world.Has<SpriteComponent>(entity))
                return world.GetRef<SpriteComponent>(entity).ImageIndex;
            return 0;
        }

        private static bool TryGetTexture(
            IRenderController renderer,
            string projectPath,
            string imageName,
            int frameIndex,
            out TextureHandle handle,
            out int width,
            out int height)
        {
            handle = TextureHandle.Invalid;
            width = 32;
            height = 32;
            if (string.IsNullOrWhiteSpace(imageName) || renderer == null) return false;

            string path = ResolveImagePath(projectPath, imageName, frameIndex);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            Dictionary<string, TextureCacheEntry> textureCache = RenderCaches.GetOrCreateValue(renderer).Textures;
            if (!textureCache.TryGetValue(key, out TextureCacheEntry entry))
            {
                handle = renderer.LoadTexture(path);
                if (!handle.IsValid) return false;
                entry = new TextureCacheEntry { Handle = handle };
                TryReadImageSize(path, out entry.Width, out entry.Height);
                textureCache[key] = entry;
            }

            handle = entry.Handle;
            width = entry.Width;
            height = entry.Height;
            return handle.IsValid;
        }

        private static string ResolveImagePath(string projectPath, string imageName, int frameIndex = 0)
        {
            if (string.IsNullOrWhiteSpace(imageName)) return null;
            string path = SpriteAssetLoader.ResolveFrameTexturePath(projectPath, imageName, frameIndex);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private static Vector4 ResolveFrameUvRect(
            string projectPath,
            string imageName,
            int frameIndex,
            int textureWidth,
            int textureHeight)
        {
            string spritePath = SpriteAssetLoader.ResolveDescriptorPath(projectPath, imageName);
            if (!SpriteAssetLoader.IsSpriteDescriptorPath(spritePath) || !File.Exists(spritePath))
                return Vector4.Zero;

            SpriteRuntimeAsset asset = SpriteAssetLoader.Load(spritePath);
            if (frameIndex < 0 || frameIndex >= asset.Frames.Count)
                return Vector4.Zero;

            SpriteRuntimeRectangle rect = asset.Frames[frameIndex].SourceRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
                return Vector4.Zero;

            if (rect.X <= 0 && rect.Y <= 0
                && rect.Width >= textureWidth && rect.Height >= textureHeight)
                return Vector4.Zero;

            float u0 = rect.X / (float)Math.Max(1, textureWidth);
            float v0 = rect.Y / (float)Math.Max(1, textureHeight);
            float u1 = (rect.X + rect.Width) / (float)Math.Max(1, textureWidth);
            float v1 = (rect.Y + rect.Height) / (float)Math.Max(1, textureHeight);
            return new Vector4(u0, v0, u1, v1);
        }

        private static void ResolveSpriteDisplayPivot(
            string projectPath,
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
            string spritePath = SpriteAssetLoader.ResolveDescriptorPath(projectPath, imageName);
            if (!SpriteAssetLoader.IsSpriteDescriptorPath(spritePath) || !File.Exists(spritePath))
                return;

            SpriteRuntimeAsset asset = SpriteAssetLoader.Load(spritePath);
            SpriteAssetLoader.RegisterAsset(imageName, asset);
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

        private static void TryReadImageSize(string path, out int w, out int h)
        {
            w = 32;
            h = 32;
            try
            {
                using var fs = File.OpenRead(path);
                var image = StbImageSharp.ImageResult.FromStream(fs, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                w = Math.Max(1, image.Width);
                h = Math.Max(1, image.Height);
            }
            catch { /* keep defaults */ }
        }

        private static float SafeScale(float s) => s == 0f ? 1f : s;
    }
}

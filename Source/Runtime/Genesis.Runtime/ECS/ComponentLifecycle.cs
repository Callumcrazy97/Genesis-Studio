using System;
using System.Collections.Generic;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.ECS
{
    /// <summary>Runtime lifecycle hooks for ECS components (Phase 8 practical scope).</summary>
    public interface IRuntimeComponentLifecycle
    {
        void OnAttach(EcsWorld world, Entity entity);
        void OnDetach(EcsWorld world, Entity entity);
        void OnUpdate(EcsWorld world, Entity entity, float deltaTime);
        void OnSerialize(Dictionary<string, object> props);
        void OnDeserialize(Dictionary<string, object> props);
    }

    /// <summary>Dispatches attach/detach/update/serialize hooks for core runtime components.</summary>
    public static class ComponentLifecycle
    {
        private static readonly Dictionary<string, IRuntimeComponentLifecycle> Hooks =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["TransformComponent"] = new TransformLifecycle(),
                ["SpriteComponent"] = new SpriteLifecycle(),
                ["ModelRendererComponent"] = new ModelRendererLifecycle(),
                ["ModelAnimatorComponent"] = new ModelAnimatorLifecycle(),
                ["PhysicsComponent"] = new PhysicsLifecycle(),
                ["ScriptComponent"] = new ScriptLifecycle(),
            };

        public static void OnAttach(EcsWorld world, Entity entity, string componentType)
        {
            if (Hooks.TryGetValue(componentType ?? "", out var hook))
                hook.OnAttach(world, entity);
        }

        public static void OnDetach(EcsWorld world, Entity entity, string componentType)
        {
            if (Hooks.TryGetValue(componentType ?? "", out var hook))
                hook.OnDetach(world, entity);
        }

        public static void OnUpdate(EcsWorld world, Entity entity, float dt)
        {
            if (world.Has<PixelRigSpriteComponent>(entity))
                world.GetRef<PixelRigSpriteComponent>(entity).Binding?.Player.Advance(dt);
            if (world.Has<TransformComponent>(entity))
                Hooks["TransformComponent"].OnUpdate(world, entity, dt);
            if (world.Has<SpriteComponent>(entity))
                Hooks["SpriteComponent"].OnUpdate(world, entity, dt);
            if (world.Has<ModelRendererComponent>(entity))
                Hooks["ModelRendererComponent"].OnUpdate(world, entity, dt);
            if (world.Has<ModelAnimatorComponent>(entity))
                Hooks["ModelAnimatorComponent"].OnUpdate(world, entity, dt);
            if (world.Has<PhysicsComponent>(entity))
                Hooks["PhysicsComponent"].OnUpdate(world, entity, dt);
            if (world.Has<ScriptComponent>(entity))
                Hooks["ScriptComponent"].OnUpdate(world, entity, dt);
        }

        public static void OnSerialize(string componentType, Dictionary<string, object> props)
        {
            if (Hooks.TryGetValue(componentType ?? "", out var hook))
                hook.OnSerialize(props);
        }

        public static void OnDeserialize(string componentType, Dictionary<string, object> props)
        {
            if (Hooks.TryGetValue(componentType ?? "", out var hook))
                hook.OnDeserialize(props);
        }

        private sealed class TransformLifecycle : IRuntimeComponentLifecycle
        {
            public void OnAttach(EcsWorld world, Entity entity) { }
            public void OnDetach(EcsWorld world, Entity entity) { }
            public void OnUpdate(EcsWorld world, Entity entity, float dt) { }
            public void OnSerialize(Dictionary<string, object> props) { }
            public void OnDeserialize(Dictionary<string, object> props) { }
        }

        private sealed class SpriteLifecycle : IRuntimeComponentLifecycle
        {
            public void OnAttach(EcsWorld world, Entity entity)
            {
                if (!world.Has<SpriteComponent>(entity))
                    world.Set(entity, new SpriteComponent
                    {
                        Alpha = 1f,
                        AnimationTagIndex = -1,
                        AnimationLoopOverride = -1,
                    });
            }

            public void OnDetach(EcsWorld world, Entity entity) { }

            public void OnUpdate(EcsWorld world, Entity entity, float dt)
            {
                ref SpriteComponent sprite = ref world.GetRef<SpriteComponent>(entity);
                if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets))
                    return;

                if (assets.HasSpriteTransition)
                {
                    assets.SpriteTransitionElapsed += MathF.Max(0f, dt);
                    if (assets.SpriteTransitionElapsed >= assets.SpriteTransitionDuration)
                    {
                        assets.SpriteTransitionPreviousImage = string.Empty;
                        assets.SpriteTransitionPreviousFrame = 0;
                        assets.SpriteTransitionElapsed = assets.SpriteTransitionDuration;
                    }
                }

                if (string.IsNullOrWhiteSpace(assets.Image) ||
                    !SpriteAssetLoader.TryGetCachedAsset(assets.Image, out SpriteRuntimeAsset asset))
                {
                    return;
                }

                SpritePlayback.Advance(ref sprite, asset, dt);
            }

            public void OnSerialize(Dictionary<string, object> props) { }
            public void OnDeserialize(Dictionary<string, object> props) { }
        }

        private sealed class ModelRendererLifecycle : IRuntimeComponentLifecycle
        {
            public void OnAttach(EcsWorld world, Entity entity)
            {
                if (!world.Has<ModelRendererComponent>(entity))
                    world.Set(entity, new ModelRendererComponent
                    {
                        ScaleX = 1f,
                        ScaleY = 1f,
                        ScaleZ = 1f,
                        CastShadows = true,
                        ReceiveShadows = true,
                    });
            }
            public void OnDetach(EcsWorld world, Entity entity) { }
            public void OnUpdate(EcsWorld world, Entity entity, float dt) { }
            public void OnSerialize(Dictionary<string, object> props) { }
            public void OnDeserialize(Dictionary<string, object> props) { }
        }

        private sealed class ModelAnimatorLifecycle : IRuntimeComponentLifecycle
        {
            private readonly Modeling.RuntimeModelAssetRegistry AnimationAssets = new();
            public void OnAttach(EcsWorld world, Entity entity)
            {
                if (!world.Has<ModelAnimatorComponent>(entity))
                    world.Set(entity, new ModelAnimatorComponent
                    {
                        ClipFps = 60f,
                        PlaybackSpeed = 1f,
                        Playing = true,
                        Loop = true,
                    });
                else
                {
                    ref var animator = ref world.GetRef<ModelAnimatorComponent>(entity);
                    // Legacy prefab documents predate PlaybackSpeed. Their missing numeric value
                    // deserialises as zero, but authored pause state is represented by Playing.
                    if (animator.PlaybackSpeed == 0f) animator.PlaybackSpeed = 1f;
                    if (animator.ClipFps <= 0f) animator.ClipFps = 60f;
                }
            }
            public void OnDetach(EcsWorld world, Entity entity) { }
            public void OnUpdate(EcsWorld world, Entity entity, float dt)
            {
                if (!world.Has<ModelAnimatorComponent>(entity)) return;
                ref var animator = ref world.GetRef<ModelAnimatorComponent>(entity);
                if (!animator.Playing) { animator.Controller?.ClearRootMotion(); return; }
                if (animator.Controller != null)
                {
                    Modeling.GModelAsset asset = null;
                    if (world.Has<ModelRendererComponent>(entity))
                    {
                        string model = world.GetRef<ModelRendererComponent>(entity).ModelAsset;
                        if (!string.IsNullOrWhiteSpace(model))
                            asset = AnimationAssets.Load(Scripting.PgslCommands.ProjectPath, model);
                    }
                    animator.Controller.Advance(dt, animator.PlaybackSpeed, asset);
                    if (animator.Controller.RootMotionEnabled && world.Has<TransformComponent>(entity))
                        Scripting.PgslCommands.ApplyAnimationRootMotion(world, entity, animator.Controller);
                }
                float step = MathF.Max(0f, dt) * animator.PlaybackSpeed;
                animator.TimeSeconds += step;
                if (!string.IsNullOrWhiteSpace(animator.PreviousClipName))
                {
                    animator.PreviousTimeSeconds += step;
                    animator.BlendElapsed += MathF.Max(0f, dt);
                    if (animator.BlendDuration <= 0f || animator.BlendElapsed >= animator.BlendDuration)
                    {
                        animator.PreviousClipName = string.Empty;
                        animator.PreviousTimeSeconds = 0f;
                        animator.BlendElapsed = animator.BlendDuration;
                    }
                }
            }
            public void OnSerialize(Dictionary<string, object> props) { }
            public void OnDeserialize(Dictionary<string, object> props) { }
        }

        private sealed class PhysicsLifecycle : IRuntimeComponentLifecycle
        {
            public void OnAttach(EcsWorld world, Entity entity) { }
            public void OnDetach(EcsWorld world, Entity entity) { }
            public void OnUpdate(EcsWorld world, Entity entity, float dt) { }
            public void OnSerialize(Dictionary<string, object> props) { }
            public void OnDeserialize(Dictionary<string, object> props) { }
        }

        private sealed class ScriptLifecycle : IRuntimeComponentLifecycle
        {
            public void OnAttach(EcsWorld world, Entity entity) { }
            public void OnDetach(EcsWorld world, Entity entity) { }
            public void OnUpdate(EcsWorld world, Entity entity, float dt) { }
            public void OnSerialize(Dictionary<string, object> props) { }
            public void OnDeserialize(Dictionary<string, object> props) { }
        }
    }
}

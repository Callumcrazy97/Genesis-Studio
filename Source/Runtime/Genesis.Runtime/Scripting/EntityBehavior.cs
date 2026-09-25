using System;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// Base class for native C# gameplay behaviours that bind to an ECS entity.
    /// Replaces per-event PGSL scripts. A subclass is authored under a project's
    /// <c>Scripts/Objects/</c> folder, compiled at launch by
    /// <see cref="CSharpScriptCompiler"/>, and instantiated by
    /// <see cref="ScriptHostSystem"/> for any entity whose prefab binds it via the
    /// <c>ScriptComponent</c>.
    ///
    /// Lifecycle (called by ScriptHostSystem):
    ///   OnCreate()            once, right after the entity is spawned
    ///   OnUpdate(float dt)    every frame
    ///   OnCollision(Entity)   when the physics system reports a contact
    ///   OnDestroy()           once, just before the entity is removed
    ///
    /// NOTE on components: Ember components are <c>struct</c>s. Use
    /// <see cref="GetComponent{T}"/> (returns a ref so writes persist) rather than
    /// caching a copy in a field.
    /// </summary>
    public abstract class EntityBehavior
    {
        /// <summary>The ECS world this behaviour's entity lives in.</summary>
        public EcsWorld World { get; internal set; }

        /// <summary>The entity this behaviour instance is attached to.</summary>
        public Entity Entity { get; internal set; }

        /// <summary>True while the owning entity is still alive in the world.</summary>
        public bool IsAlive => World != null && World.IsAlive(Entity);

        /// <summary>
        /// Engine services for this behaviour: input, camera, timing, mesh upload, project
        /// asset loading and HUD drawing — all provided by the host. Never null (defaults to
        /// a no-op context until a host injects the real one), so scripts can call it freely.
        /// </summary>
        protected IGameContext Game { get; private set; } = NullGameContext.Instance;

        /// <summary>Injected by <see cref="ScriptHostSystem"/>; the host owns the real context.</summary>
        internal void SetContext(IGameContext context)
            => Game = context ?? NullGameContext.Instance;

        // ----- Lifecycle hooks (override as needed) ---------------------------

        public virtual void OnCreate() { }
        public virtual void OnGameStart() { }
        public virtual void OnRoomStart() { }
        public virtual void OnStepBegin(float dt) { }
        public virtual void OnUpdate(float dt) { }
        public virtual void OnInputEvents() { }
        public virtual void OnStepEnd(float dt) { }
        public virtual void OnCollision(Entity other) { }
        public virtual void OnRoomEnd() { }
        public virtual void OnGameEnd() { }
        public virtual void OnDestroy() { }

        /// <summary>
        /// Draw a 2D HUD/overlay in pixel space. Called once per render frame by the host,
        /// after the 3D scene. Use for crosshairs, hotbars, debug text and menus.
        /// </summary>
        public virtual void OnDrawHud(IHudCanvas hud) { }

        /// <summary>
        /// Draw textured HUD sprites on top of the D2D text layer (item icons, cursor).
        /// Called after <see cref="OnDrawHud"/> compositing completes.
        /// </summary>
        public virtual void OnDrawHudOverlay(IRenderController renderer) { }

        /// <summary>
        /// Submit procedural mesh draws for this entity's Draw3D component.
        /// Call from OnUpdate (or OnCreate for static meshes); Draw3D draws them.
        /// </summary>
        protected void SubmitProceduralMeshes(ReadOnlySpan<MeshDrawCall> draws)
        {
            if (World == null || !World.IsAlive(Entity)) return;
            MeshDrawCall[] copy = draws.Length > 0 ? draws.ToArray() : Array.Empty<MeshDrawCall>();
            ProceduralMeshDrawRegistry.Set(Entity, copy);
            if (World.Has<Draw3DComponent>(Entity))
            {
                ref Draw3DComponent d = ref World.GetRef<Draw3DComponent>(Entity);
                d.Procedural = true;
                World.Set(Entity, d);
            }
        }

        /// <summary>
        /// Submit per-frame engine render hooks (dynamic lights, fog, chunk bounds).
        /// Object mesh/sprite drawing is blocked here — use Draw2D/Draw3D components.
        /// </summary>
        public virtual void OnRenderFrame(IRenderController renderer) { }

        /// <summary>The actual engine pixel-rig player bound to this object, or null when unbound.</summary>
        protected PixelRigPlayer SpriteRig => SpriteRigRuntime.Get(World, Entity);

        /// <summary>Bind an Image Editor pixel rig from a saved image resource. Call in OnCreate.</summary>
        protected bool BindSpriteRig(string imageResource, string rigName = "")
        {
            bool bound = SpriteRigRuntime.BindFile(World, Entity, Game.ResolveAssetPath(imageResource), rigName, out string error);
            if (!bound) Game.Log("Sprite rig: " + error);
            return bound;
        }

        protected void ClearSpriteRig() => SpriteRigRuntime.Clear(World, Entity);

        // ----- Component helpers ---------------------------------------------

        /// <summary>Returns a writable reference to a component on this entity.</summary>
        protected ref T GetComponent<T>() where T : struct, IComponent
            => ref World.GetRef<T>(Entity);

        protected bool HasComponent<T>() where T : struct, IComponent
            => World.Has<T>(Entity);

        protected void SetComponent<T>(in T value) where T : struct, IComponent
            => World.Set<T>(Entity, value);

        /// <summary>Queues this entity for destruction (safe to call mid-update).</summary>
        protected void DestroyEntity() => World.DestroyEntity(Entity);
    }
}

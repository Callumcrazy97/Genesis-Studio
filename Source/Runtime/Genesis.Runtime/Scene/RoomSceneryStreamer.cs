using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.Core;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

/// <summary>
/// Loads a room's scenery as the camera approaches it and unloads it as the camera leaves.
/// </summary>
/// <remarks>
/// <para>
/// Scenery is a placed Object that only shows a model: no script, no events, no physics preset,
/// nothing that persists. Such an object has no state to lose, so it can be created when it comes
/// within the room's scenery distance and destroyed when it falls out of it. A world with tens of
/// thousands of houses, fences and props then costs what the few hundred near the camera cost.
/// </para>
/// <para>
/// Everything else in the room (anything scripted, anything that moves or is remembered) loads
/// with the room as it always did.
/// </para>
/// <para>
/// Plain scenery stays solid where things move, however far from the camera: near a moving body,
/// a script character or a collision focus, scenery that is not loaded gets a stand-in made of
/// its collider alone (no model, nothing drawn), sharing its model's collision mesh.
/// </para>
/// </remarks>
public sealed class RoomSceneryStreamer : ISceneSubsystem, IRoomWarmUpSubsystem
{
    private const float CellSize = 128f;

    /// <summary>Most places colliders are kept around at once: collision foci, then characters, then bodies.</summary>
    private const int MaxFocus = 512;

    /// <summary>A stand-in is let go only beyond this many times its focus's radius, so an edge is not crossed back and forth.</summary>
    private const float KeepFactor = 1.3f;

    /// <summary>A focus further than this from the origin (metres) is ignored: its cells would not fit an int.</summary>
    private const float FarthestFocus = 1e8f;

    private sealed class Item
    {
        public RoomNode Node;
        public Vector3 Position;
        public Entity Entity = Entity.Null;
        public bool Loaded;
        /// <summary>Set when an object turned out to have a moving body: it stays loaded for good.</summary>
        public bool Pinned;
        /// <summary>The Object asked to be streamed although it has scripts or a body.</summary>
        public bool Streamable;
        /// <summary>The Object's own distance (its <c>streamDistance</c>); 0 uses the room's.</summary>
        public float Distance;
        /// <summary>A plain scenery object with a fixed collider, made only when something comes near.</summary>
        public bool NearCollider;
        /// <summary>
        /// The object's collider alone, standing in for it while it is not loaded and something
        /// moving is near: no model and no scripts.
        /// </summary>
        public Entity Stand = Entity.Null;
        /// <summary>The fixed collider the object has, read the first time a stand-in is needed.</summary>
        public ColliderRecipe Recipe;
        /// <summary>The object has no fixed collider, so it never needs a stand-in.</summary>
        public bool NoCollider;
        /// <summary>The last pass that found something moving near the object.</summary>
        public int Wanted;
    }

    /// <summary>What a stand-in is made from: the collider exactly as the loaded object has it.</summary>
    private sealed class ColliderRecipe
    {
        public RigidBodyComponent Body;
        public Transform3DComponent Transform;
        public MeshColliderComponent Geometry;
        public bool HasGeometry;
    }

    /// <summary>A scenery collider is made when the camera comes this near (metres, flat).</summary>
    public float ColliderRadiusAroundCamera { get; set; } = 200f;

    /// <summary>
    /// ... or when a moving body or a script character comes this near (metres, flat). Beyond the
    /// scenery distance the collider is a stand-in. 0 keeps scenery solid only near the camera and
    /// near collision foci. The room's <c>sceneryCollisionDistance</c> sets it.
    /// </summary>
    public float ColliderRadiusAroundBodies { get; set; } = 64f;

    /// <summary>Most stand-in colliders kept at once for scenery that is not loaded.</summary>
    public int MaxFarColliders { get; set; } = 2048;

    /// <summary>Time one frame may spend making stand-in colliders once the room is running; the rest wait for the next frame.</summary>
    public double FarColliderMillisecondsPerFrame { get; set; } = 1.0;

    /// <summary>Scenery colliders that exist now (for diagnostics and tests).</summary>
    public int CollidersAwake { get; private set; }

    /// <summary>Stand-in colliders that exist now for scenery that is not loaded.</summary>
    public int FarColliders => _far.Count;

    /// <summary>Places colliders were kept around at the last look: collision foci, characters and moving bodies.</summary>
    public int FocusCount => _focus.Count;

    private readonly List<CollisionFocus> _focus = new();
    private readonly List<Item> _far = new();
    private int _colliderFrame;
    private int _pass;
    private bool _farBacklog;

    // The largest distance any item asks for: how far around the camera to look.
    private float _farthestItem;

    private float DistanceOf(Item item) => item.Distance > 0f ? item.Distance : Distance;

    private readonly List<Item> _items = new();
    private readonly Dictionary<(int X, int Z), List<Item>> _cells = new();
    private readonly List<Item> _loaded = new();
    private RoomSceneBuilder _builder;
    private RoomAsset _room;
    private int _sweep;
    private bool _primed;

    public RoomSceneryStreamer(float distance)
    {
        Distance = MathF.Max(8f, distance);
    }

    /// <summary>Scenery nearer than this to the camera is loaded.</summary>
    public float Distance { get; set; }

    /// <summary>Scenery is unloaded once it is this many times the distance away, so a boundary is not crossed back and forth.</summary>
    public float UnloadFactor { get; set; } = 1.2f;

    /// <summary>Most objects created in one frame once the room is running.</summary>
    public int LoadsPerFrame { get; set; } = 24;

    /// <summary>
    /// Time one frame may spend creating objects once the room is running. Creating a building
    /// fits a collider to its model, which is the larger part of the cost; a village coming into
    /// range is therefore spread over the frames it takes rather than landing in one.
    /// </summary>
    public double LoadMillisecondsPerFrame { get; set; } = 1.5;

    /// <summary>Scenery objects the room holds, loaded or not.</summary>
    public int Total => _items.Count;

    /// <summary>Scenery objects that exist right now.</summary>
    public int Loaded => _loaded.Count;

    /// <summary>Scenery objects that exist now and run scripts or have bodies of their own.</summary>
    public int LoadedStreamable
    {
        get
        {
            int count = 0;
            foreach (Item item in _loaded) if (item.Streamable) count++;
            return count;
        }
    }

    /// <summary>Records a scenery object the room builder has left for later.</summary>
    internal void Add(RoomNode node, Vector3 position, bool streamable = false, float distance = 0f)
    {
        float own = float.IsFinite(distance) && distance > 0f ? MathF.Max(8f, distance) : 0f;
        var item = new Item { Node = node, Position = position, Streamable = streamable, Distance = own };
        _farthestItem = MathF.Max(_farthestItem, own);
        _items.Add(item);
        var key = Cell(position);
        if (!_cells.TryGetValue(key, out List<Item> cell)) _cells[key] = cell = new List<Item>();
        cell.Add(item);
    }

    public void Attach(RoomSceneBuilder builder, RoomAsset room)
    {
        _builder = builder;
        _room = room;
    }

    private static (int X, int Z) Cell(Vector3 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Z / CellSize));

    /// <summary>Time one frame may spend creating objects while the room waits behind a loading screen.</summary>
    public double WarmUpMillisecondsPerFrame { get; set; } = 6;

    /// <summary>
    /// Fills the view a few milliseconds at a time while the room waits behind a loading screen,
    /// and returns true when nothing in range is left to create. The room's first update then
    /// finds its work done; it still creates, at once, whatever is in range of where the camera
    /// is by then, so a room never opens on an empty street.
    /// </summary>
    public bool WarmUp(RuntimeScene scene)
    {
        if (_builder == null || scene?.World == null || _items.Count == 0 || _primed) return true;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        return Fill(scene, int.MaxValue, WarmUpMillisecondsPerFrame, started)
            && WarmKinds(scene, WarmUpMillisecondsPerFrame, started);
    }

    /// <summary>Creates what is in range of the camera. Returns false when it stopped because its count or its time ran out.</summary>
    private bool Fill(RuntimeScene scene, int budget, double milliseconds, long started)
    {
        Vector3 camera = scene.Camera3D.Position;
        float load = MathF.Max(Distance, _farthestItem);
        int x0 = (int)MathF.Floor((camera.X - load) / CellSize), x1 = (int)MathF.Floor((camera.X + load) / CellSize);
        int z0 = (int)MathF.Floor((camera.Z - load) / CellSize), z1 = (int)MathF.Floor((camera.Z + load) / CellSize);
        for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        {
            if (!_cells.TryGetValue((cx, cz), out List<Item> cell)) continue;
            foreach (Item item in cell)
            {
                if (item.Loaded) continue;
                float own = DistanceOf(item);
                if (FlatDistanceSquared(item.Position, camera) > own * own) continue;
                Load(scene, item);
                if (--budget <= 0) return false;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds) return false;
            }
        }

        return true;
    }

    public void Update(RuntimeScene scene, GameTime time)
    {
        if (_builder == null || scene?.World == null || _items.Count == 0) return;
        Vector3 camera = scene.Camera3D.Position;
        float unloadFactor = MathF.Max(1.05f, UnloadFactor);

        // The first update fills the view at once; a room should not open on an empty street.
        bool running = _primed;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _primed = true;
        Fill(scene, running ? Math.Max(1, LoadsPerFrame) : int.MaxValue, running ? LoadMillisecondsPerFrame : double.MaxValue, started);

        if (!running) WarmKinds(scene, double.MaxValue, started);
        UpdateColliders(scene, now: !running);

        // Look at a slice of what is loaded each frame; leaving is never urgent.
        int checks = Math.Min(_loaded.Count, 96);
        for (int i = 0; i < checks && _loaded.Count > 0; i++)
        {
            _sweep = (_sweep + 1) % _loaded.Count;
            Item item = _loaded[_sweep];
            if (item.Pinned) continue;
            // Something with a script may have walked: judge it by where it is, not where it began.
            Vector3 where = item.Position;
            if (item.Streamable && scene.World.IsAlive(item.Entity) && scene.World.Has<Genesis.Runtime.ECS.Components.TransformComponent>(item.Entity))
            {
                ref Genesis.Runtime.ECS.Components.TransformComponent transform = ref scene.World.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(item.Entity);
                where = new Vector3(transform.X, transform.Y, transform.Z);
            }

            float unload = DistanceOf(item) * unloadFactor;
            if (FlatDistanceSquared(where, camera) <= unload * unload) continue;
            if (Unload(scene, item))
            {
                _loaded[_sweep] = _loaded[^1];
                _loaded.RemoveAt(_loaded.Count - 1);
            }
        }
    }

    /// <summary>
    /// Creates and removes one of each kind of Object that nothing near the camera has needed yet.
    /// The first of a kind pays for what every later one shares (its model, the collider fitted
    /// to it); this pays that while the room loads instead of in the frame a distant village
    /// first comes into range.
    /// </summary>
    /// <returns>False when it stopped because its time ran out; the next call carries on from there.</returns>
    private bool WarmKinds(RuntimeScene scene, double milliseconds, long started)
    {
        foreach (Item item in _loaded) _warmedKinds.Add(item.Node.GameObject?.Prefab ?? "");
        for (; _kindCursor < _items.Count; _kindCursor++)
        {
            Item item = _items[_kindCursor];
            // Not for an Object with scripts: its Create and Destroy events would run for nothing.
            if (item.Loaded || item.Streamable || !_warmedKinds.Add(item.Node.GameObject?.Prefab ?? "")) continue;
            Load(scene, item);
            if (Unload(scene, item)) _loaded.Remove(item);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds)
            {
                _kindCursor++;
                return false;
            }
        }

        return true;
    }

    private readonly HashSet<string> _warmedKinds = new(StringComparer.OrdinalIgnoreCase);
    private int _kindCursor;

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private void Load(RuntimeScene scene, Item item)
    {
        EcsWorld world = scene.World;
        // Something moving was near enough for a stand-in: the object's own collider takes over.
        bool stood = !item.Stand.IsNull;
        if (stood)
        {
            DropStand(scene, item);
            _far.Remove(item);
        }

        item.Entity = _builder.SpawnScenery(world, _room, item.Node);
        item.Loaded = true;
        item.NearCollider = false;
        // Plain scenery's fixed collider waits until the camera or something moving comes near: a
        // town in view costs its drawing, not the physics of every building in it.
        if (!item.Streamable && !item.Entity.IsNull && world.IsAlive(item.Entity) && world.Has<RigidBodyComponent>(item.Entity))
        {
            ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(item.Entity);
            if (body.Motion == PhysicsMotionType.Static && body.RegistrationId == 0)
            {
                item.NearCollider = true;
                // At once, not at the next physics step: a frame without one would leave a gap.
                if (stood && scene.Physics != null && world.Has<Transform3DComponent>(item.Entity))
                {
                    body.Dormant = false;
                    scene.Physics.RegisterEntity(world, item.Entity, ref body, ref world.GetRef<Transform3DComponent>(item.Entity));
                }
                else body.Dormant = true;
            }
        }
        _loaded.Add(item);
    }

    /// <summary>
    /// Wakes the colliders of scenery near the camera or something moving and lets far ones sleep,
    /// and keeps stand-ins for scenery that is not loaded near what moves.
    /// </summary>
    private void UpdateColliders(RuntimeScene scene, bool now)
    {
        bool due = now || ++_colliderFrame % 8 == 0;
        if (!due && !_farBacklog) return;
        CollectFocus(scene);
        if (due) WakeLoadedColliders(scene);
        UpdateFarColliders(scene, now ? WarmUpMillisecondsPerFrame : FarColliderMillisecondsPerFrame);
    }

    /// <summary>
    /// Where colliders are kept: around what a script named, then around its characters, then
    /// around moving bodies, so a world full of loose crates does not crowd out the bots.
    /// </summary>
    private void CollectFocus(RuntimeScene scene)
    {
        EcsWorld world = scene.World;
        _focus.Clear();
        CollisionFoci.Collect(world, _focus, MaxFocus);
        float bodyNear = ColliderRadiusAroundBodies;
        if (!float.IsFinite(bodyNear) || bodyNear <= 0f) return;
        CollisionFoci.CollectCharacters(world, _focus, bodyNear, MaxFocus);
        if (_focus.Count >= MaxFocus) return;
        world.Query<RigidBodyComponent, Transform3DComponent>((Entity _, ref RigidBodyComponent body, ref Transform3DComponent transform) =>
        {
            if (body.Motion != PhysicsMotionType.Static && body.RegistrationId != 0 && _focus.Count < MaxFocus)
                _focus.Add(new CollisionFocus(transform.Position, bodyNear));
        });
    }

    private void WakeLoadedColliders(RuntimeScene scene)
    {
        EcsWorld world = scene.World;
        Vector3 camera = scene.Camera3D.Position;
        float cameraNear = ColliderRadiusAroundCamera;
        int awake = 0;
        foreach (Item item in _loaded)
        {
            if (!item.NearCollider || item.Entity.IsNull || !world.IsAlive(item.Entity) || !world.Has<RigidBodyComponent>(item.Entity))
                continue;
            ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(item.Entity);
            // Wake inside the radius; sleep only beyond it with a margin, so an edge is not crossed back and forth.
            float margin = body.Dormant ? 1f : KeepFactor;
            bool near = FlatDistanceSquared(item.Position, camera) <= cameraNear * cameraNear * margin * margin;
            for (int i = 0; !near && i < _focus.Count; i++)
            {
                float reach = _focus[i].Radius * margin;
                near = FlatDistanceSquared(item.Position, _focus[i].Position) <= reach * reach;
            }
            if (near)
            {
                body.Dormant = false;
                awake++;
            }
            else if (!body.Dormant)
            {
                if (body.RegistrationId != 0) scene.Physics?.UnregisterEntity(world, item.Entity, ref body);
                body.Dormant = true;
            }
        }
        CollidersAwake = awake;
    }

    /// <summary>
    /// Makes a stand-in collider for each plain scenery object that is not loaded and has
    /// something moving within its focus's radius, and lets go of those nothing is near any more.
    /// </summary>
    private void UpdateFarColliders(RuntimeScene scene, double milliseconds)
    {
        _farBacklog = false;
        _pass++;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int made = 0;
        foreach (CollisionFocus focus in _focus)
        {
            float keep = focus.Radius * KeepFactor;
            if (!float.IsFinite(keep) || keep <= 0f) continue;
            // A body flung into the void (or one whose simulation failed) is near no scenery.
            if (!(MathF.Abs(focus.Position.X) < FarthestFocus) || !(MathF.Abs(focus.Position.Z) < FarthestFocus)) continue;
            int x0 = (int)MathF.Floor((focus.Position.X - keep) / CellSize), x1 = (int)MathF.Floor((focus.Position.X + keep) / CellSize);
            int z0 = (int)MathF.Floor((focus.Position.Z - keep) / CellSize), z1 = (int)MathF.Floor((focus.Position.Z + keep) / CellSize);
            for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!_cells.TryGetValue((cx, cz), out List<Item> cell)) continue;
                foreach (Item item in cell)
                {
                    if (item.Loaded || item.Streamable || item.NoCollider || item.Wanted == _pass) continue;
                    bool standing = !item.Stand.IsNull;
                    float reach = standing ? keep : focus.Radius;
                    if (FlatDistanceSquared(item.Position, focus.Position) > reach * reach) continue;
                    if (standing)
                    {
                        item.Wanted = _pass;
                        continue;
                    }

                    if (_far.Count >= MaxFarColliders) continue;
                    // Within the frame's time; whatever is left is made on the next frame.
                    if (made > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds)
                    {
                        _farBacklog = true;
                        continue;
                    }

                    if (!MakeStand(scene, item)) continue;
                    item.Wanted = _pass;
                    made++;
                }
            }
        }

        for (int i = _far.Count - 1; i >= 0; i--)
        {
            Item item = _far[i];
            if (item.Wanted == _pass && !item.Loaded) continue;
            DropStand(scene, item);
            _far[i] = _far[^1];
            _far.RemoveAt(_far.Count - 1);
        }
    }

    /// <summary>Makes an object's collider alone where it stands. False when it has no fixed collider.</summary>
    private bool MakeStand(RuntimeScene scene, Item item)
    {
        if (_builder == null) return false;
        ColliderRecipe recipe = item.Recipe ??= Describe(scene, item);
        if (recipe == null)
        {
            item.NoCollider = true;
            return false;
        }

        EcsWorld world = scene.World;
        Entity stand = world.CreateEntity();
        world.Set(stand, new EntityLifecycleComponent { Enabled = true });
        world.Set(stand, recipe.Transform);
        world.Set(stand, recipe.Body);
        if (recipe.HasGeometry) world.Set(stand, recipe.Geometry);
        if (scene.Physics != null)
        {
            try
            {
                scene.Physics.RegisterEntity(world, stand, ref world.GetRef<RigidBodyComponent>(stand), ref world.GetRef<Transform3DComponent>(stand));
            }
            catch (System.IO.InvalidDataException)
            {
                // A model whose collider cannot be built: leave it to fail where it is loaded, as before.
                world.GetRef<RigidBodyComponent>(stand).Dormant = true;
                world.DestroyEntity(stand);
                item.NoCollider = true;
                return false;
            }
        }

        item.Stand = stand;
        _far.Add(item);
        return true;
    }

    /// <summary>
    /// The collider an object has, read by creating it as loading does and removing it again, so
    /// a stand-in is exactly what the loaded object would be. Null when it has no fixed collider.
    /// </summary>
    private ColliderRecipe Describe(RuntimeScene scene, Item item)
    {
        EcsWorld world = scene.World;
        Entity entity;
        try
        {
            entity = _builder.SpawnScenery(world, _room, item.Node);
        }
        catch (Exception error) when (error is System.IO.InvalidDataException or System.IO.IOException)
        {
            // Its model cannot be read or fitted: it fails where it is loaded, as before, not here.
            System.Diagnostics.Trace.WriteLine($"Scenery '{item.Node.Name}' has no collider away from the camera: {error.Message}");
            return null;
        }

        if (entity.IsNull || !world.IsAlive(entity)) return null;
        try
        {
            return ReadRecipe(world, entity);
        }
        finally
        {
            // Destroying waits for the next flush, after this frame is drawn: until then it is
            // neither drawn nor registered.
            if (world.Has<Genesis.Runtime.ECS.Components.Draw3DComponent>(entity))
                world.GetRef<Genesis.Runtime.ECS.Components.Draw3DComponent>(entity).Visible = false;
            if (world.Has<RigidBodyComponent>(entity))
            {
                ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(entity);
                if (body.RegistrationId != 0) scene.Physics?.UnregisterEntity(world, entity, ref body);
                body.Dormant = true;
            }

            Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.Remove(entity);
            world.DestroyEntity(entity);
        }
    }

    private static ColliderRecipe ReadRecipe(EcsWorld world, Entity entity)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<RigidBodyComponent>(entity) || !world.Has<Transform3DComponent>(entity))
            return null;
        RigidBodyComponent body = world.GetRef<RigidBodyComponent>(entity);
        if (body.Motion != PhysicsMotionType.Static || !body.Collision) return null;
        body.RegistrationId = 0;
        body.Dormant = false;
        Transform3DComponent placed = world.GetRef<Transform3DComponent>(entity);
        Transform3DComponent transform = Transform3DComponent.Default;
        transform.Position = placed.Position;
        transform.Rotation = placed.Rotation;
        transform.Scale = placed.Scale;
        var recipe = new ColliderRecipe { Body = body, Transform = transform };
        if (world.Has<MeshColliderComponent>(entity))
        {
            recipe.Geometry = world.GetRef<MeshColliderComponent>(entity);
            recipe.HasGeometry = true;
        }

        return recipe;
    }

    private static void DropStand(RuntimeScene scene, Item item)
    {
        EcsWorld world = scene.World;
        Entity stand = item.Stand;
        item.Stand = Entity.Null;
        if (stand.IsNull || !world.IsAlive(stand)) return;
        if (world.Has<RigidBodyComponent>(stand))
        {
            ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(stand);
            if (body.RegistrationId != 0) scene.Physics?.UnregisterEntity(world, stand, ref body);
            body.Dormant = true;
        }

        world.DestroyEntity(stand);
    }

    /// <returns>False when the object must stay: it has gained a body that moves.</returns>
    private bool Unload(RuntimeScene scene, Item item)
    {
        EcsWorld world = scene.World;
        bool solid = false;
        if (!item.Entity.IsNull && world.IsAlive(item.Entity))
        {
            if (world.Has<RigidBodyComponent>(item.Entity))
            {
                ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(item.Entity);
                // Plain scenery that has somehow come to move is not what it claimed to be; an
                // Object that asked to be streamed has said its body may go with it.
                if (body.Motion != PhysicsMotionType.Static && !item.Streamable)
                {
                    item.Pinned = true;
                    return false;
                }

                // Something is near enough for its collider to be awake: a stand-in replaces it at once.
                solid = item.NearCollider && body.RegistrationId != 0;
                if (solid) item.Recipe ??= ReadRecipe(world, item.Entity);
                if (body.RegistrationId != 0) scene.Physics?.UnregisterEntity(world, item.Entity, ref body);
            }

            if (item.Streamable) _builder?.DetachScripts(item.Entity);

            // What the object was drawn from is kept per entity; let it go with the object.
            Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.Remove(item.Entity);
            world.DestroyEntity(item.Entity);
        }

        item.Entity = Entity.Null;
        item.Loaded = false;
        if (solid && item.Recipe != null && item.Stand.IsNull && _far.Count < MaxFarColliders) MakeStand(scene, item);
        return true;
    }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

    public void Dispose()
    {
        // The room that owns these objects is being unloaded and destroys them itself, the
        // stand-in colliders with them.
        _loaded.Clear();
        _far.Clear();
        _focus.Clear();
        _items.Clear();
        _cells.Clear();
        _warmedKinds.Clear();
        _kindCursor = 0;
        _builder = null;
    }
}

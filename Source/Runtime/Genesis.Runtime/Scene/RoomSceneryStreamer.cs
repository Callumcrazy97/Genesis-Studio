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
/// </remarks>
public sealed class RoomSceneryStreamer : ISceneSubsystem, IRoomWarmUpSubsystem
{
    private const float CellSize = 128f;

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
    }

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
    internal void Add(RoomNode node, Vector3 position, bool streamable = false)
    {
        var item = new Item { Node = node, Position = position, Streamable = streamable };
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
        float load = Distance, loadSquared = load * load;
        int x0 = (int)MathF.Floor((camera.X - load) / CellSize), x1 = (int)MathF.Floor((camera.X + load) / CellSize);
        int z0 = (int)MathF.Floor((camera.Z - load) / CellSize), z1 = (int)MathF.Floor((camera.Z + load) / CellSize);
        for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        {
            if (!_cells.TryGetValue((cx, cz), out List<Item> cell)) continue;
            foreach (Item item in cell)
            {
                if (item.Loaded || FlatDistanceSquared(item.Position, camera) > loadSquared) continue;
                Load(scene.World, item);
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
        float load = Distance;
        float unload = load * MathF.Max(1.05f, UnloadFactor), unloadSquared = unload * unload;

        // The first update fills the view at once; a room should not open on an empty street.
        bool running = _primed;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _primed = true;
        Fill(scene, running ? Math.Max(1, LoadsPerFrame) : int.MaxValue, running ? LoadMillisecondsPerFrame : double.MaxValue, started);

        if (!running) WarmKinds(scene, double.MaxValue, started);

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

            if (FlatDistanceSquared(where, camera) <= unloadSquared) continue;
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
            Load(scene.World, item);
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

    private void Load(EcsWorld world, Item item)
    {
        item.Entity = _builder.SpawnScenery(world, _room, item.Node);
        item.Loaded = true;
        _loaded.Add(item);
    }

    /// <returns>False when the object must stay: it has gained a body that moves.</returns>
    private bool Unload(RuntimeScene scene, Item item)
    {
        EcsWorld world = scene.World;
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

                if (body.RegistrationId != 0) scene.Physics?.UnregisterEntity(world, item.Entity, ref body);
            }

            if (item.Streamable) _builder?.DetachScripts(item.Entity);

            // What the object was drawn from is kept per entity; let it go with the object.
            Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.Remove(item.Entity);
            world.DestroyEntity(item.Entity);
        }

        item.Entity = Entity.Null;
        item.Loaded = false;
        return true;
    }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

    public void Dispose()
    {
        // The room that owns these objects is being unloaded and destroys them itself.
        _loaded.Clear();
        _items.Clear();
        _cells.Clear();
        _warmedKinds.Clear();
        _kindCursor = 0;
        _builder = null;
    }
}

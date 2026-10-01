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
public sealed class RoomSceneryStreamer : ISceneSubsystem
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

    /// <summary>Records a scenery object the room builder has left for later.</summary>
    internal void Add(RoomNode node, Vector3 position)
    {
        var item = new Item { Node = node, Position = position };
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

    public void Update(RuntimeScene scene, GameTime time)
    {
        if (_builder == null || scene?.World == null || _items.Count == 0) return;
        Vector3 camera = scene.Camera3D.Position;
        float load = Distance, loadSquared = load * load;
        float unload = load * MathF.Max(1.05f, UnloadFactor), unloadSquared = unload * unload;

        // The first update fills the view at once; a room should not open on an empty street.
        bool running = _primed;
        int budget = running ? Math.Max(1, LoadsPerFrame) : int.MaxValue;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _primed = true;

        int x0 = (int)MathF.Floor((camera.X - load) / CellSize), x1 = (int)MathF.Floor((camera.X + load) / CellSize);
        int z0 = (int)MathF.Floor((camera.Z - load) / CellSize), z1 = (int)MathF.Floor((camera.Z + load) / CellSize);
        for (int cz = z0; cz <= z1 && budget > 0; cz++)
        for (int cx = x0; cx <= x1 && budget > 0; cx++)
        {
            if (!_cells.TryGetValue((cx, cz), out List<Item> cell)) continue;
            foreach (Item item in cell)
            {
                if (item.Loaded || FlatDistanceSquared(item.Position, camera) > loadSquared) continue;
                Load(scene.World, item);
                if (--budget <= 0) break;
                if (running && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= LoadMillisecondsPerFrame)
                {
                    budget = 0;
                    break;
                }
            }
        }

        if (!running) WarmKinds(scene);

        // Look at a slice of what is loaded each frame; leaving is never urgent.
        int checks = Math.Min(_loaded.Count, 96);
        for (int i = 0; i < checks && _loaded.Count > 0; i++)
        {
            _sweep = (_sweep + 1) % _loaded.Count;
            Item item = _loaded[_sweep];
            if (item.Pinned || FlatDistanceSquared(item.Position, camera) <= unloadSquared) continue;
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
    private void WarmKinds(RuntimeScene scene)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Item item in _loaded) known.Add(item.Node.GameObject?.Prefab ?? "");
        foreach (Item item in _items)
        {
            if (item.Loaded || !known.Add(item.Node.GameObject?.Prefab ?? "")) continue;
            Load(scene.World, item);
            if (Unload(scene, item)) _loaded.Remove(item);
        }
    }

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
    private static bool Unload(RuntimeScene scene, Item item)
    {
        EcsWorld world = scene.World;
        if (!item.Entity.IsNull && world.IsAlive(item.Entity))
        {
            if (world.Has<RigidBodyComponent>(item.Entity))
            {
                ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(item.Entity);
                if (body.Motion != PhysicsMotionType.Static)
                {
                    item.Pinned = true;
                    return false;
                }

                if (body.RegistrationId != 0) scene.Physics?.UnregisterEntity(world, item.Entity, ref body);
            }

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
        _builder = null;
    }
}

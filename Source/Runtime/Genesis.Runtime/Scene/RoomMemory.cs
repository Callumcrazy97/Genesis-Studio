using System;
using System.Collections.Generic;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

/// <summary>
/// What the Objects a room placed were like when they were last seen, room by room, for the
/// game's session: where each was, its script's variables, and whether a script destroyed it. A
/// streamed Object is remembered when it goes out of range (<see cref="RoomSceneryStreamer"/>);
/// every Object a persistent room placed is remembered when the game leaves the room. Either
/// comes back as it was left, and what was destroyed stays gone.
/// </summary>
public static class RoomMemory
{
    internal sealed class Entry
    {
        public bool Gone;
        public bool Placed;
        public Genesis.Runtime.ECS.Components.TransformComponent Transform;
        public bool HasTransform3D;
        public Transform3DComponent Transform3D;
        public IReadOnlyDictionary<string, object> Variables;
    }

    private static readonly Dictionary<(string Room, string Node), Entry> Entries = new();

    // The nodes each persistent room placed when it was last built, to know which are gone.
    private static readonly Dictionary<string, HashSet<string>> Placed = new(StringComparer.Ordinal);

    /// <summary>
    /// Objects come back as they were left and destroyed ones stay gone (true unless set):
    /// <c>Engine.Room.Streaming.Remember</c>. Off, each comes back as its room first made it.
    /// </summary>
    public static bool Remember { get; set; } = true;

    /// <summary>Objects remembered now, in every room.</summary>
    public static int Count
    {
        get { lock (Entries) return Entries.Count; }
    }

    /// <summary>Forgets every room: each comes back as it was made.</summary>
    public static void ForgetAll()
    {
        lock (Entries)
        {
            Entries.Clear();
            Placed.Clear();
        }
    }

    /// <summary>Forgets one room (one reloaded after an edit).</summary>
    public static void ForgetRoom(string room)
    {
        room ??= "";
        lock (Entries)
        {
            var keys = new List<(string Room, string Node)>();
            foreach ((string Room, string Node) key in Entries.Keys)
                if (string.Equals(key.Room, room, StringComparison.Ordinal)) keys.Add(key);
            foreach ((string Room, string Node) key in keys) Entries.Remove(key);
            Placed.Remove(room);
        }
    }

    internal static Entry Recall(string room, string node)
    {
        lock (Entries) return Entries.TryGetValue((room ?? "", node ?? ""), out Entry entry) ? entry : null;
    }

    internal static void Keep(string room, string node, Entry entry)
    {
        lock (Entries) Entries[(room ?? "", node ?? "")] = entry;
    }

    internal static void Gone(string room, string node) => Keep(room, node, new Entry { Gone = true });

    /// <summary>Records the nodes a persistent room placed as it was built.</summary>
    internal static void PlacedBy(string room, IEnumerable<string> nodes)
    {
        lock (Entries) Placed[room ?? ""] = new HashSet<string>(nodes, StringComparer.Ordinal);
    }

    internal static Entry Capture(EcsWorld world, Entity entity, Genesis.Runtime.Scripting.PgslBehavior script)
    {
        var entry = new Entry();
        if (world.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity))
        {
            entry.Transform = world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
            entry.Placed = true;
        }
        if (world.Has<Transform3DComponent>(entity))
        {
            entry.Transform3D = world.GetRef<Transform3DComponent>(entity);
            entry.HasTransform3D = true;
        }
        entry.Variables = script?.GetVariablesSnapshot();
        return entry;
    }

    /// <summary>Puts an object back where it was; before its Create event, so the event sees it there.</summary>
    internal static void Place(EcsWorld world, Entity entity, Entry entry)
    {
        if (entry is not { Placed: true }) return;
        if (world.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity))
            world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity) = entry.Transform;
        if (entry.HasTransform3D && world.Has<Transform3DComponent>(entity))
            world.GetRef<Transform3DComponent>(entity) = entry.Transform3D;
    }

    /// <summary>
    /// The game is leaving <paramref name="room"/>, a persistent room: every Object it placed is
    /// remembered as it is now, and each it placed that is no longer there as gone. Objects kept
    /// for the next room (persistent Objects) are not, as they go on living.
    /// </summary>
    public static void Leave(EcsWorld world, string room, Func<Entity, Genesis.Runtime.Scripting.PgslBehavior> scriptOf)
    {
        if (!Remember || world == null || string.IsNullOrEmpty(room)) return;
        HashSet<string> placed;
        lock (Entries)
            if (!Placed.TryGetValue(room, out placed)) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<(Entity Entity, string Node)>();
        world.Query<Genesis.Runtime.ECS.Components.TransformComponent>(
            (Entity entity, ref Genesis.Runtime.ECS.Components.TransformComponent _) =>
            {
                if (!Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var drawn)
                    || drawn.RoomNodeId is not { Length: > 0 } node || !placed.Contains(node)
                    || !string.Equals(drawn.RoomName, room, StringComparison.Ordinal)) return;
                found.Add((entity, node));
            });
        foreach ((Entity entity, string node) in found)
        {
            if (!seen.Add(node)) continue;
            if (world.Has<Genesis.Runtime.ECS.Components.PersistentObjectComponent>(entity)) continue;
            Keep(room, node, Capture(world, entity, scriptOf?.Invoke(entity)));
        }
        foreach (string node in placed)
            if (!seen.Contains(node)) Gone(room, node);
    }
}

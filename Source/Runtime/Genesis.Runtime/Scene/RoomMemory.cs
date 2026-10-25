using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>For an Object a script made in a persistent room: the Object it is an instance of.</summary>
        public string Prefab;
    }

    // Objects a script made in a persistent room are kept under these node names.
    internal const string MadePrefix = "made:";

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
        var made = new List<(Entity Entity, string Prefab)>();
        world.Query<Genesis.Runtime.ECS.Components.TransformComponent>(
            (Entity entity, ref Genesis.Runtime.ECS.Components.TransformComponent _) =>
            {
                if (!Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var drawn)) return;
                // What a script made here (CreateInstance, or brought back from an earlier visit),
                // unless it rides on another instance (a part, a held sword): that one makes it again.
                // One made in a Create or Room Start event is made again by that event: not kept.
                if (string.IsNullOrEmpty(drawn.RoomNodeId) || drawn.RoomNodeId.StartsWith(MadePrefix, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(drawn.Prefab) && !drawn.MadeBySetup
                        && !world.Has<Genesis.Runtime.ECS.Components.ModelSocketAttachmentComponent>(entity))
                        made.Add((entity, drawn.Prefab));
                    return;
                }
                if (drawn.RoomNodeId is not { Length: > 0 } node || !placed.Contains(node)
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

        // The Objects scripts made in the room, kept afresh each time the game leaves it.
        lock (Entries)
        {
            var old = new List<(string Room, string Node)>();
            foreach ((string Room, string Node) key in Entries.Keys)
                if (string.Equals(key.Room, room, StringComparison.Ordinal) && key.Node.StartsWith(MadePrefix, StringComparison.Ordinal)) old.Add(key);
            foreach ((string Room, string Node) key in old) Entries.Remove(key);
        }
        int index = 0;
        foreach ((Entity entity, string prefab) in made)
        {
            if (world.Has<Genesis.Runtime.ECS.Components.PersistentObjectComponent>(entity)) continue;
            Entry entry = Capture(world, entity, scriptOf?.Invoke(entity));
            entry.Prefab = prefab;
            Keep(room, MadePrefix + index++, entry);
        }
    }

    /// <summary>The Objects scripts made in <paramref name="room"/> the last time the game left it.</summary>
    internal static List<Entry> MadeIn(string room)
    {
        var made = new List<(int Index, Entry Entry)>();
        lock (Entries)
            foreach (KeyValuePair<(string Room, string Node), Entry> pair in Entries)
                if (string.Equals(pair.Key.Room, room ?? "", StringComparison.Ordinal) && pair.Key.Node.StartsWith(MadePrefix, StringComparison.Ordinal)
                    && pair.Value.Prefab is { Length: > 0 } && int.TryParse(pair.Key.Node.AsSpan(MadePrefix.Length), out int index))
                    made.Add((index, pair.Value));
        made.Sort((a, b) => a.Index.CompareTo(b.Index));
        return made.ConvertAll(item => item.Entry);
    }

    // ── As text, for a game's save slot ──────────────────────────────────────────────

    /// <summary>Everything remembered, as text a game keeps in its save (Engine.Room.Streaming.Save).</summary>
    public static string Write()
    {
        var rooms = new Newtonsoft.Json.Linq.JObject();
        Newtonsoft.Json.Linq.JObject RoomOf(string name)
        {
            if (rooms[name] is not Newtonsoft.Json.Linq.JObject room)
                rooms[name] = room = new Newtonsoft.Json.Linq.JObject { ["objects"] = new Newtonsoft.Json.Linq.JObject() };
            return room;
        }
        lock (Entries)
        {
            foreach (KeyValuePair<string, HashSet<string>> pair in Placed)
                RoomOf(pair.Key)["placed"] = new Newtonsoft.Json.Linq.JArray(pair.Value);
            foreach (KeyValuePair<(string Room, string Node), Entry> pair in Entries)
                ((Newtonsoft.Json.Linq.JObject)RoomOf(pair.Key.Room)["objects"])[pair.Key.Node] = ToJson(pair.Value);
        }
        return new Newtonsoft.Json.Linq.JObject { ["genesisRooms"] = 1, ["rooms"] = rooms }.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Replaces what is remembered with what <see cref="Write"/> gave; false when the text is not that.</summary>
    public static bool Read(string text)
    {
        Newtonsoft.Json.Linq.JObject root;
        try { root = Newtonsoft.Json.Linq.JObject.Parse(text ?? ""); }
        catch (Newtonsoft.Json.JsonException) { return false; }
        if ((int?)root["genesisRooms"] != 1 || root["rooms"] is not Newtonsoft.Json.Linq.JObject rooms) return false;
        var entries = new Dictionary<(string Room, string Node), Entry>();
        var placed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        try
        {
            foreach (Newtonsoft.Json.Linq.JProperty room in rooms.Properties())
            {
                if (room.Value["placed"] is Newtonsoft.Json.Linq.JArray nodes)
                    placed[room.Name] = new HashSet<string>(nodes.Values<string>(), StringComparer.Ordinal);
                if (room.Value["objects"] is Newtonsoft.Json.Linq.JObject objects)
                    foreach (Newtonsoft.Json.Linq.JProperty node in objects.Properties())
                        entries[(room.Name, node.Name)] = FromJson((Newtonsoft.Json.Linq.JObject)node.Value);
            }
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or InvalidCastException or FormatException or ArgumentException)
        {
            return false;
        }
        lock (Entries)
        {
            Entries.Clear();
            Placed.Clear();
            foreach (KeyValuePair<(string Room, string Node), Entry> pair in entries) Entries[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, HashSet<string>> pair in placed) Placed[pair.Key] = pair.Value;
        }
        return true;
    }

    private static Newtonsoft.Json.Linq.JObject ToJson(Entry entry)
    {
        var json = new Newtonsoft.Json.Linq.JObject();
        if (entry.Gone) { json["gone"] = true; return json; }
        if (entry.Prefab is { Length: > 0 }) json["object"] = entry.Prefab;
        if (entry.Placed) json["transform"] = Newtonsoft.Json.Linq.JObject.FromObject(entry.Transform);
        if (entry.HasTransform3D) json["transform3D"] = Newtonsoft.Json.Linq.JObject.FromObject(entry.Transform3D);
        if (entry.Variables is { Count: > 0 })
        {
            var variables = new Newtonsoft.Json.Linq.JObject();
            foreach (KeyValuePair<string, object> variable in entry.Variables)
            {
                // Numbers, words, true/false and lists of them; anything else is the game's own to keep.
                try { variables[variable.Key] = variable.Value == null ? Newtonsoft.Json.Linq.JValue.CreateNull() : Newtonsoft.Json.Linq.JToken.FromObject(variable.Value); }
                catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or ArgumentException or InvalidOperationException) { }
            }
            json["variables"] = variables;
        }
        return json;
    }

    private static Entry FromJson(Newtonsoft.Json.Linq.JObject json)
    {
        var entry = new Entry { Gone = (bool?)json["gone"] == true, Prefab = (string)json["object"] };
        if (json["transform"] is Newtonsoft.Json.Linq.JObject transform)
        {
            entry.Transform = transform.ToObject<Genesis.Runtime.ECS.Components.TransformComponent>();
            entry.Placed = true;
        }
        if (json["transform3D"] is Newtonsoft.Json.Linq.JObject transform3D)
        {
            entry.Transform3D = transform3D.ToObject<Transform3DComponent>();
            entry.HasTransform3D = true;
        }
        if (json["variables"] is Newtonsoft.Json.Linq.JObject variables)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (Newtonsoft.Json.Linq.JProperty variable in variables.Properties()) values[variable.Name] = Value(variable.Value);
            entry.Variables = values;
        }
        return entry;
    }

    // A saved value as the VM keeps it: numbers as doubles, lists as lists.
    private static object Value(Newtonsoft.Json.Linq.JToken token) => token.Type switch
    {
        Newtonsoft.Json.Linq.JTokenType.Integer or Newtonsoft.Json.Linq.JTokenType.Float => (double)token,
        Newtonsoft.Json.Linq.JTokenType.Boolean => (bool)token,
        Newtonsoft.Json.Linq.JTokenType.String => (string)token,
        Newtonsoft.Json.Linq.JTokenType.Array => ((Newtonsoft.Json.Linq.JArray)token).Select(Value).ToList(),
        _ => null,
    };
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Net;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Net
{
    /// <summary>
    /// Keeps objects in step between a host and the players joined to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of object are shared. The host <em>replicates</em> objects that belong to the
    /// world (creatures, doors, dropped items): each joined player sees a copy that follows the
    /// host's. A player <em>owns</em> objects it controls itself (its character): it moves them
    /// locally with no delay, the host is told, and every other player sees a copy.
    /// </para>
    /// <para>
    /// A player is only sent what is near it. Each player reports where its camera is, and the
    /// host sends an object's arrival, movement and departure only while the object is within the
    /// interest distance of that player. A world with thousands of replicated objects costs each
    /// player the few dozen around them.
    /// </para>
    /// <para>
    /// Copies are visual: they are created from the Object's own definition but run none of its
    /// scripts. Movement is sent at a fixed rate and smoothed on arrival, and the animation a
    /// model is playing is sent when it changes, so a copy walks when its object walks. There is
    /// no prediction, rollback or cheat prevention; a player's own objects are trusted.
    /// </para>
    /// <para>
    /// Every copy glides: a player's copies of the host's objects, and the host's copies of the
    /// players' own objects alike. Each frame a copy moves part of the way to where its object was
    /// last reported, so its position, rotation and 2D angle change every frame instead of
    /// jumping twenty times a second. The copy's transform is what is drawn and what the scripts
    /// on that machine read, so a script's hit test against a copy uses where the copy is seen,
    /// about a fourteenth of a second behind the report. The host passes on what the owner
    /// reported, not its own smoothed copy, so the other players' copies are smoothed once.
    /// </para>
    /// <para>
    /// A 2D object's look is sent the same way: its sprite, frame, scale (a negative one is a
    /// flip), angle, alpha and visibility, each only when it changes, so a value that stays the
    /// same costs nothing after the object has arrived. A copy holds the frame it is sent instead
    /// of playing the sprite's animation itself.
    /// </para>
    /// <para>
    /// A shared object also carries a few named values (health, a name, a team) that the machine
    /// controlling it sets and every machine holding a copy can read. They are sent when they
    /// change and with the object when it arrives.
    /// </para>
    /// </remarks>
    public sealed class NetworkReplication
    {
        // Negative tags are reserved for the engine; game messages use positive ones.
        public const int TagSpawn = -7001, TagDespawn = -7002, TagSnapshot = -7003, TagFocus = -7004,
            TagOwnedSpawn = -7005, TagOwnedState = -7006, TagOwnedDespawn = -7007, TagResync = -7008,
            TagClip = -7009, TagOwnedClip = -7010, TagValues = -7011, TagOwnedValues = -7012,
            TagSprites = -7013, TagOwnedSprite = -7014;

        /// <summary>Most named values one object carries, and the longest name and text.</summary>
        public const int MaxValues = 32, MaxValueName = 48, MaxValueText = 512;

        private const int SnapshotEntriesPerMessage = 36, SpriteEntriesPerMessage = 64, MaxSpriteName = 256;

        /// <summary>
        /// Movement is sent unreliably, so the last message of a move may be lost and leave a copy
        /// short of where the object stopped. A stopped object's position is sent this many more
        /// times before the sender goes quiet.
        /// </summary>
        private const int SettleRepeats = 4;

        /// <summary>
        /// Below these a sender treats an object as standing still (squared distance, and degrees),
        /// and a gliding copy this close is put exactly where its object is.
        /// </summary>
        private const float StillDistanceSquared = 1e-6f, StillDegrees = 0.01f;

        /// <summary>How quickly a copy closes on where its object was last reported, per second.</summary>
        private const float GlideRate = 14f;

        /// <summary>The animation a model is playing, as far as a copy needs to know it.</summary>
        private readonly record struct ClipState(string Clip, bool Loop, float Speed)
        {
            public static readonly ClipState None = new("", true, 1f);
        }

        /// <summary>What a 2D object looks like, as far as a copy needs to know it.</summary>
        private readonly record struct SpriteState(
            bool Is2D, bool HasFrames, string Sprite, int Frame, float XScale, float YScale, float Angle, float Alpha, bool Visible)
        {
            public static readonly SpriteState None = default;
        }

        /// <summary>Which parts of a <see cref="SpriteState"/> a message carries.</summary>
        [Flags]
        private enum SpriteFields : byte
        {
            None = 0, Sprite = 1, Frame = 2, Scale = 4, Angle = 8, Alpha = 16, Visible = 32,
        }

        /// <summary>Where the machine that controls an object last said it is; the copy here glides there.</summary>
        private struct GlideTarget
        {
            public Vector3 Position, Rotation;
            /// <summary>A 2D object's angle, once the copy has been sent one.</summary>
            public float Angle;
            public bool HasAngle;
        }

        /// <summary>Something this machine shares or holds a copy of, and the values it carries.</summary>
        private abstract class Tracked
        {
            public Entity Entity;
            public string Prefab;
            public Dictionary<string, object> Values;
            /// <summary>Names whose values have changed since they were last sent.</summary>
            public List<string> Dirty;
        }

        private sealed class Shared : Tracked
        {
            /// <summary>This send's changed values, written once and sent to each player that knows the object.</summary>
            public byte[] ValuesPayload;
            public ClipState Clip = ClipState.None;
            public bool ClipChanged;
            public SpriteState Sprite = SpriteState.None;
            /// <summary>What of the look changed this send, and those parts written once for every player.</summary>
            public SpriteFields SpriteChanged;
            public byte[] SpritePayload;
            /// <summary>0 for the host's own; otherwise the player who owns it.</summary>
            public int OwnerPeer;
            /// <summary>A player's own object: where the player last said it is. Not used for the host's own.</summary>
            public GlideTarget Target;
            public Vector3 LastSentPosition, LastSentRotation;
            public bool Moved;
            public int Repeats;
        }

        private sealed class PeerState
        {
            public Vector3 Focus;
            public bool HasFocus;
            public readonly HashSet<int> Known = new();
            /// <summary>The player's own objects: its local number to the host's network id.</summary>
            public readonly Dictionary<int, int> Owned = new();
        }

        private sealed class Copy : Tracked
        {
            /// <summary>0 for the host's own object; otherwise the player it belongs to, as the host numbers them.</summary>
            public int OwnerPeer;
            public GlideTarget Target;
        }

        private sealed class Mine : Tracked
        {
            public ClipState Clip = ClipState.None;
            public SpriteState Sprite = SpriteState.None;
            public bool Announced;
            public Vector3 LastSentPosition, LastSentRotation;
            public int Repeats;
        }

        private readonly Dictionary<int, Shared> _shared = new();
        private readonly Dictionary<int, PeerState> _peers = new();
        private readonly Dictionary<int, Copy> _copies = new();
        private readonly Dictionary<int, Mine> _mine = new();
        private readonly List<int> _scratch = new();
        private readonly Dictionary<Entity, Tracked> _index = new();
        private readonly List<Tracked> _copyList = new();
        private bool _indexStale = true;
        /// <summary>Messages that arrived before this machine had a world to apply them to.</summary>
        private readonly List<NetMessage> _early = new();
        private IGameNetwork _network;
        private EcsWorld _world;
        private int _nextNetId = 1, _nextLocalId = 1;
        private float _sendClock, _focusClock, _resyncClock = 1f;
        private bool _wasConnected, _resyncWanted;

        /// <summary>A player is sent the objects within this distance of its camera.</summary>
        public float InterestDistance { get; set; } = 250f;

        /// <summary>
        /// False sends every shared object to every player, whatever the distance. A 2D room has
        /// no 3D camera to measure from, and is small enough to share whole.
        /// </summary>
        public bool InterestEnabled { get; set; } = true;

        /// <summary>Movement updates sent per second.</summary>
        public float SendRate { get; set; } = 20f;

        /// <summary>
        /// Creates the visual copy of an Object by name at a place. The Player supplies this; a
        /// copy must not run the Object's scripts.
        /// </summary>
        public Func<EcsWorld, string, Vector3, Vector3, Vector3, Entity> CreateCopy { get; set; }

        /// <summary>Objects this host shares, including the copies it holds of players' own objects.</summary>
        public int SharedCount => _shared.Count;

        /// <summary>
        /// Copies of other machines' objects that exist here. On the host these are the players'
        /// own objects; on a player's machine, the host's objects and the other players'.
        /// </summary>
        public int CopyCount { get { Index(); return _copyList.Count; } }

        /// <summary>The copy at a position in the list of copies, or nothing.</summary>
        public Entity CopyAt(int index)
        {
            Index();
            return index >= 0 && index < _copyList.Count ? _copyList[index].Entity : Entity.Null;
        }

        /// <summary>True when the object is this machine's copy of something another machine controls.</summary>
        public bool IsCopy(Entity entity) =>
            Index().TryGetValue(entity, out Tracked record) && record is Copy or Shared { OwnerPeer: not 0 };

        /// <summary>
        /// The player a shared object or copy belongs to, as the host numbers its players; 0 when
        /// it is the host's own, or is not shared.
        /// </summary>
        public int OwnerOf(Entity entity) =>
            !Index().TryGetValue(entity, out Tracked record) ? 0
            : record is Copy copy ? copy.OwnerPeer
            : record is Shared shared ? shared.OwnerPeer : 0;

        /// <summary>The Object a shared object or copy was made from, or an empty string.</summary>
        public string ObjectOf(Entity entity) =>
            Index().TryGetValue(entity, out Tracked record) ? record.Prefab ?? "" : "";

        /// <summary>
        /// Sets a named value on an object this machine controls: one the host replicates, or
        /// one a player owns. Every machine holding a copy can read it. A number or text.
        /// </summary>
        /// <returns>False when the object is not shared from here, or a limit is passed.</returns>
        public bool SetValue(Entity entity, string name, object value)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxValueName) return false;
            if (value is string text) { if (text.Length > MaxValueText) return false; }
            else if (value is not double) return false;
            if (!Index().TryGetValue(entity, out Tracked record)) return false;
            // Only the machine that controls an object says what its values are.
            if (record is Copy or Shared { OwnerPeer: not 0 }) return false;
            record.Values ??= new Dictionary<string, object>(StringComparer.Ordinal);
            if (record.Values.TryGetValue(name, out object old))
            {
                if (Equals(old, value)) return true;
            }
            else if (record.Values.Count >= MaxValues) return false;

            record.Values[name] = value;
            record.Dirty ??= new List<string>();
            if (!record.Dirty.Contains(name)) record.Dirty.Add(name);
            return true;
        }

        /// <summary>Reads a named value from a shared object or a copy of one.</summary>
        public bool TryGetValue(Entity entity, string name, out object value)
        {
            value = null;
            return !string.IsNullOrEmpty(name) && Index().TryGetValue(entity, out Tracked record)
                && record.Values != null && record.Values.TryGetValue(name, out value);
        }

        private Dictionary<Entity, Tracked> Index()
        {
            if (!_indexStale) return _index;
            _index.Clear();
            _copyList.Clear();
            foreach (Shared shared in _shared.Values)
            {
                _index[shared.Entity] = shared;
                if (shared.OwnerPeer != 0) _copyList.Add(shared);
            }

            foreach (Mine mine in _mine.Values) _index[mine.Entity] = mine;
            foreach (Copy copy in _copies.Values)
            {
                _index[copy.Entity] = copy;
                _copyList.Add(copy);
            }

            _indexStale = false;
            return _index;
        }

        /// <summary>Objects this machine owns and shows to others.</summary>
        public int OwnedCount => _mine.Count;

        public void Attach(IGameNetwork network)
        {
            if (ReferenceEquals(_network, network)) return;
            Detach();
            _network = network;
            if (network == null) return;
            network.OnMessageReceived += OnMessage;
            network.OnPeerDisconnected += OnPeerDisconnected;
        }

        public void Detach()
        {
            if (_network == null) return;
            _network.OnMessageReceived -= OnMessage;
            _network.OnPeerDisconnected -= OnPeerDisconnected;
            _network = null;
        }

        /// <summary>Host: share this object with every player near it. Returns its network id, or 0.</summary>
        public int Replicate(Entity entity, string prefab)
        {
            if (entity.IsNull || string.IsNullOrWhiteSpace(prefab)) return 0;
            foreach (KeyValuePair<int, Shared> pair in _shared)
                if (pair.Value.Entity.Equals(entity)) return pair.Key;
            int id = _nextNetId++;
            _shared[id] = new Shared { Entity = entity, Prefab = prefab };
            _indexStale = true;
            return id;
        }

        /// <summary>
        /// Any machine: this object is controlled here, and every other player should see a copy.
        /// On the host this is the same as <see cref="Replicate"/>.
        /// </summary>
        public int Own(Entity entity, string prefab)
        {
            if (entity.IsNull || string.IsNullOrWhiteSpace(prefab)) return 0;
            if (_network is { IsHost: true }) return Replicate(entity, prefab);
            foreach (KeyValuePair<int, Mine> pair in _mine)
                if (pair.Value.Entity.Equals(entity)) return pair.Key;
            int id = _nextLocalId++;
            _mine[id] = new Mine { Entity = entity, Prefab = prefab };
            _indexStale = true;
            return id;
        }

        /// <summary>Stops sharing an object. Copies of it elsewhere are removed.</summary>
        public void Forget(Entity entity)
        {
            foreach (KeyValuePair<int, Shared> pair in _shared)
            {
                if (!pair.Value.Entity.Equals(entity)) continue;
                DropShared(pair.Key);
                return;
            }

            foreach (KeyValuePair<int, Mine> pair in _mine)
            {
                if (!pair.Value.Entity.Equals(entity)) continue;
                if (pair.Value.Announced && _network is { IsConnected: true })
                    _network.Send(NetPeer.Host.Id, TagOwnedDespawn, Write(writer => writer.Write(pair.Key)));
                _mine.Remove(pair.Key);
                _indexStale = true;
                return;
            }
        }

        /// <summary>Call once a frame, after the network has been pumped.</summary>
        /// <param name="focus">Where this machine's player is looking from; the host uses it to decide what to send.</param>
        public void Update(EcsWorld world, Vector3 focus, float deltaSeconds)
        {
            // A new world (another room) holds none of the objects that were shared in the last.
            if (_world != null && world != null && !ReferenceEquals(_world, world)) ForgetWorld();
            _world = world;
            if (_network == null || world == null) return;
            if (_early.Count > 0)
            {
                // The network was pumped before the first update: apply what arrived then.
                NetMessage[] early = _early.ToArray();
                _early.Clear();
                foreach (NetMessage message in early) OnMessage(message);
            }

            bool connected = _network.IsConnected;
            if (!connected)
            {
                if (_wasConnected) Reset(world);
                _wasConnected = false;
                return;
            }

            _wasConnected = true;
            _sendClock += MathF.Max(0f, deltaSeconds);
            _focusClock += MathF.Max(0f, deltaSeconds);
            float interval = 1f / Math.Clamp(SendRate, 1f, 120f);
            bool send = _sendClock >= interval;
            if (send) _sendClock = 0f;

            if (_network.IsHost)
            {
                if (send) HostSend(world, focus);
            }
            else
            {
                if (_focusClock >= 0.2f)
                {
                    _focusClock = 0f;
                    _network.Send(NetPeer.Host.Id, TagFocus, Write(writer => WriteVector(writer, focus)), NetDelivery.Unreliable);
                }

                if (send) ClientSend(world);
            }

            // Copies glide to where they were last reported, so twenty updates a second look smooth.
            // On the host the copies are the players' own objects; it glides them the same way.
            float blend = 1f - MathF.Exp(-MathF.Max(0f, deltaSeconds) * GlideRate);
            if (_network.IsHost)
            {
                foreach (Shared shared in _shared.Values)
                {
                    // One that has gone is dropped by the next send.
                    if (shared.OwnerPeer == 0 || !world.IsAlive(shared.Entity) || !world.Has<TransformComponent>(shared.Entity)) continue;
                    Glide(world, shared.Entity, ref shared.Target, blend);
                }
            }

            _scratch.Clear();
            foreach (KeyValuePair<int, Copy> pair in _copies)
            {
                Copy copy = pair.Value;
                if (!world.IsAlive(copy.Entity) || !world.Has<TransformComponent>(copy.Entity)) { _scratch.Add(pair.Key); continue; }
                Glide(world, copy.Entity, ref copy.Target, blend);
            }

            // A copy that was removed here (a script, a reloaded room) while its object lives on:
            // ask the host to say again what is near, at most once a second.
            foreach (int id in _scratch) _copies.Remove(id);
            if (_scratch.Count > 0) { _resyncWanted = true; _indexStale = true; }
            _resyncClock += MathF.Max(0f, deltaSeconds);
            if (_resyncWanted && !_network.IsHost && _resyncClock >= 1f)
            {
                _resyncWanted = false;
                _resyncClock = 0f;
                _network.Send(NetPeer.Host.Id, TagResync, new byte[1]);
            }
        }

        /// <summary>
        /// The world has been replaced. Nothing shared in the old one exists any more: the other
        /// machines are told, and the game shares its objects again as it creates them.
        /// </summary>
        private void ForgetWorld()
        {
            _indexStale = true;
            bool connected = _network is { IsConnected: true };
            if (_network is { IsHost: true })
            {
                foreach (KeyValuePair<int, PeerState> peer in _peers)
                {
                    if (connected)
                    {
                        foreach (int id in peer.Value.Known) _network.Send(peer.Key, TagDespawn, Write(w => w.Write(id)));
                        if (peer.Value.Owned.Count > 0) _network.Send(peer.Key, TagResync, new byte[1]);
                    }

                    peer.Value.Known.Clear();
                    peer.Value.Owned.Clear();
                }

                _shared.Clear();
                return;
            }

            _copies.Clear();
            foreach (KeyValuePair<int, Mine> pair in _mine)
                if (pair.Value.Announced && connected) _network.Send(NetPeer.Host.Id, TagOwnedDespawn, Write(w => w.Write(pair.Key)));
            _mine.Clear();
            if (connected) _network.Send(NetPeer.Host.Id, TagResync, new byte[1]);
        }

        // ── Host ──────────────────────────────────────────────────────────────

        private void HostSend(EcsWorld world, Vector3 hostFocus)
        {
            // Objects that no longer exist leave every player's view.
            _scratch.Clear();
            foreach (KeyValuePair<int, Shared> pair in _shared)
            {
                Shared shared = pair.Value;
                if (!world.IsAlive(shared.Entity) || !world.Has<TransformComponent>(shared.Entity)) { _scratch.Add(pair.Key); continue; }
                // A player's own object is passed on as the player reported it, not as the host's
                // copy has glided so far: the other players' copies glide from the report themselves.
                Vector3 position = shared.Target.Position, rotation = shared.Target.Rotation;
                if (shared.OwnerPeer == 0) ReadTransform(world, shared.Entity, out position, out rotation);
                bool moved = Vector3.DistanceSquared(position, shared.LastSentPosition) > StillDistanceSquared
                    || Vector3.DistanceSquared(rotation, shared.LastSentRotation) > StillDegrees * StillDegrees;
                if (moved) { shared.LastSentPosition = position; shared.LastSentRotation = rotation; shared.Repeats = SettleRepeats; }
                else if (shared.Repeats > 0) shared.Repeats--;
                shared.Moved = moved || shared.Repeats > 0;
                ClipState clip = ReadClip(world, shared.Entity);
                shared.ClipChanged = clip != shared.Clip;
                shared.Clip = clip;
                SpriteState sprite = ReadSprite(world, shared.Entity);
                if (shared.OwnerPeer != 0 && shared.Target.HasAngle && sprite.Is2D) sprite = sprite with { Angle = shared.Target.Angle };
                shared.SpriteChanged = SpriteChanges(shared.Sprite, sprite);
                shared.Sprite = sprite;
                shared.SpritePayload = null;
            }

            foreach (int id in _scratch)
            {
                int owner = _shared.TryGetValue(id, out Shared gone) ? gone.OwnerPeer : 0;
                DropShared(id);
                // The copy of a player's own object was removed here but the player still has the
                // object: ask the player to announce it again.
                if (owner != 0) _network.Send(owner, TagResync, new byte[1]);
            }

            foreach (NetPeer peer in _network.Peers)
            {
                if (!_peers.TryGetValue(peer.Id, out PeerState state)) _peers[peer.Id] = state = new PeerState();
                // Until a player has said where it is, assume it stands where the host does.
                Vector3 centre = state.HasFocus ? state.Focus : hostFocus;
                float enter = InterestDistance * InterestDistance, leave = InterestDistance * 1.15f * (InterestDistance * 1.15f);
                MemoryStream stream = null;
                BinaryWriter writer = null;
                int entries = 0;
                void Flush()
                {
                    if (entries == 0) return;
                    writer.Flush();
                    byte[] bytes = stream.ToArray();
                    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), entries);
                    _network.Send(peer.Id, TagSnapshot, bytes, NetDelivery.Unreliable);
                    stream.Dispose();
                    stream = null;
                    entries = 0;
                }

                // Changes to 2D looks go reliably, many objects to a message: a frame that is lost
                // would leave a copy showing the wrong one until the next change.
                MemoryStream spriteStream = null;
                BinaryWriter spriteWriter = null;
                int spriteEntries = 0;
                void FlushSprites()
                {
                    if (spriteEntries == 0) return;
                    spriteWriter.Flush();
                    byte[] bytes = spriteStream.ToArray();
                    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), spriteEntries);
                    _network.Send(peer.Id, TagSprites, bytes);
                    spriteStream.Dispose();
                    spriteStream = null;
                    spriteEntries = 0;
                }

                foreach (KeyValuePair<int, Shared> pair in _shared)
                {
                    Shared shared = pair.Value;
                    if (shared.OwnerPeer == peer.Id) continue;       // a player is not sent its own objects
                    float distance = InterestEnabled ? Vector3.DistanceSquared(shared.LastSentPosition, centre) : 0f;
                    bool known = state.Known.Contains(pair.Key);
                    if (!known && distance <= enter)
                    {
                        state.Known.Add(pair.Key);
                        ReadScale(world, shared.Entity, out Vector3 scale);
                        _network.Send(peer.Id, TagSpawn, Write(w =>
                        {
                            w.Write(pair.Key); w.Write(shared.Prefab);
                            WriteVector(w, shared.LastSentPosition); WriteVector(w, shared.LastSentRotation); WriteVector(w, scale);
                            WriteClip(w, shared.Clip);
                            w.Write(shared.OwnerPeer);
                            WriteValues(w, shared, onlyChanged: false);
                            WriteSprite(w, shared.Sprite, AllSpriteFields(shared.Sprite));
                        }));
                        continue;
                    }

                    if (known && distance > leave)
                    {
                        state.Known.Remove(pair.Key);
                        _network.Send(peer.Id, TagDespawn, Write(w => w.Write(pair.Key)));
                        continue;
                    }

                    if (known && shared.ClipChanged)
                        _network.Send(peer.Id, TagClip, Write(w => { w.Write(pair.Key); WriteClip(w, shared.Clip); }));
                    if (known && shared.Dirty is { Count: > 0 })
                        _network.Send(peer.Id, TagValues,
                            shared.ValuesPayload ??= Write(w => { w.Write(pair.Key); WriteValues(w, shared, onlyChanged: true); }));
                    if (known && shared.SpriteChanged != SpriteFields.None)
                    {
                        if (spriteStream == null)
                        {
                            spriteStream = new MemoryStream(64);
                            spriteWriter = new BinaryWriter(spriteStream);
                            spriteWriter.Write(0);
                        }

                        spriteWriter.Write(pair.Key);
                        spriteWriter.Write(shared.SpritePayload ??= Write(w => WriteSprite(w, shared.Sprite, shared.SpriteChanged)));
                        if (++spriteEntries >= SpriteEntriesPerMessage) FlushSprites();
                    }

                    if (!known || !shared.Moved) continue;
                    if (stream == null)
                    {
                        stream = new MemoryStream(SnapshotEntriesPerMessage * 28 + 4);
                        writer = new BinaryWriter(stream);
                        writer.Write(0);
                    }

                    writer.Write(pair.Key);
                    WriteVector(writer, shared.LastSentPosition);
                    WriteVector(writer, shared.LastSentRotation);
                    if (++entries >= SnapshotEntriesPerMessage) Flush();
                }

                Flush();
                FlushSprites();
            }

            // Every player that should hear of a changed value has now been told.
            foreach (Shared shared in _shared.Values)
            {
                shared.Dirty?.Clear();
                shared.ValuesPayload = null;
            }
        }

        private void DropShared(int id)
        {
            if (!_shared.Remove(id)) return;
            _indexStale = true;
            foreach (KeyValuePair<int, PeerState> peer in _peers)
            {
                if (peer.Value.Known.Remove(id) && _network is { IsConnected: true })
                    _network.Send(peer.Key, TagDespawn, Write(w => w.Write(id)));
                foreach (KeyValuePair<int, int> owned in peer.Value.Owned)
                    if (owned.Value == id) { peer.Value.Owned.Remove(owned.Key); break; }
            }
        }

        private void OnPeerDisconnected(NetPeer peer)
        {
            if (_network is not { IsHost: true })
            {
                // The host has gone: nothing it showed us is being kept up to date any more.
                if (_world != null) Reset(_world);
                return;
            }

            if (!_peers.Remove(peer.Id, out PeerState state)) return;
            foreach (int netId in new List<int>(state.Owned.Values))
            {
                if (_shared.TryGetValue(netId, out Shared shared) && _world != null && _world.IsAlive(shared.Entity))
                    _world.DestroyEntity(shared.Entity);
                DropShared(netId);
            }
        }

        // ── Player ────────────────────────────────────────────────────────────

        private void ClientSend(EcsWorld world)
        {
            _scratch.Clear();
            foreach (KeyValuePair<int, Mine> pair in _mine)
            {
                Mine mine = pair.Value;
                if (!world.IsAlive(mine.Entity) || !world.Has<TransformComponent>(mine.Entity)) { _scratch.Add(pair.Key); continue; }
                ReadTransform(world, mine.Entity, out Vector3 position, out Vector3 rotation);
                ClipState clip = ReadClip(world, mine.Entity);
                SpriteState sprite = ReadSprite(world, mine.Entity);
                if (!mine.Announced)
                {
                    ReadScale(world, mine.Entity, out Vector3 scale);
                    _network.Send(NetPeer.Host.Id, TagOwnedSpawn, Write(w =>
                    {
                        w.Write(pair.Key); w.Write(mine.Prefab);
                        WriteVector(w, position); WriteVector(w, rotation); WriteVector(w, scale);
                        WriteClip(w, clip);
                        WriteValues(w, mine, onlyChanged: false);
                        WriteSprite(w, sprite, AllSpriteFields(sprite));
                    }));
                    mine.Dirty?.Clear();
                    mine.Announced = true;
                    mine.LastSentPosition = position;
                    mine.LastSentRotation = rotation;
                    mine.Clip = clip;
                    mine.Sprite = sprite;
                    continue;
                }

                if (clip != mine.Clip)
                {
                    mine.Clip = clip;
                    _network.Send(NetPeer.Host.Id, TagOwnedClip, Write(w => { w.Write(pair.Key); WriteClip(w, clip); }));
                }

                SpriteFields spriteChanged = SpriteChanges(mine.Sprite, sprite);
                if (spriteChanged != SpriteFields.None)
                {
                    mine.Sprite = sprite;
                    _network.Send(NetPeer.Host.Id, TagOwnedSprite, Write(w => { w.Write(pair.Key); WriteSprite(w, sprite, spriteChanged); }));
                }

                if (mine.Dirty is { Count: > 0 })
                {
                    _network.Send(NetPeer.Host.Id, TagOwnedValues, Write(w => { w.Write(pair.Key); WriteValues(w, mine, onlyChanged: true); }));
                    mine.Dirty.Clear();
                }

                bool moved = Vector3.DistanceSquared(position, mine.LastSentPosition) > StillDistanceSquared
                    || Vector3.DistanceSquared(rotation, mine.LastSentRotation) > StillDegrees * StillDegrees;
                if (moved) mine.Repeats = SettleRepeats;
                else if (mine.Repeats > 0) mine.Repeats--;
                if (!moved && mine.Repeats == 0) continue;
                mine.LastSentPosition = position;
                mine.LastSentRotation = rotation;
                _network.Send(NetPeer.Host.Id, TagOwnedState, Write(w => { w.Write(pair.Key); WriteVector(w, position); WriteVector(w, rotation); }),
                    NetDelivery.Unreliable);
            }

            foreach (int id in _scratch)
            {
                if (_mine[id].Announced) _network.Send(NetPeer.Host.Id, TagOwnedDespawn, Write(w => w.Write(id)));
                _mine.Remove(id);
                _indexStale = true;
            }
        }

        // ── Messages ──────────────────────────────────────────────────────────

        private void OnMessage(NetMessage message)
        {
            if (message.Tag > TagSpawn || message.Tag < TagOwnedSprite || message.Payload == null) return;
            if (_world == null)
            {
                // Losing an arrival here would leave the sender believing it had been shown.
                if (_early.Count < 1024) _early.Add(message);
                return;
            }

            try
            {
                using var reader = new BinaryReader(new MemoryStream(message.Payload));
                bool host = _network is { IsHost: true };
                switch (message.Tag)
                {
                    case TagFocus when host:
                    {
                        if (!_peers.TryGetValue(message.From.Id, out PeerState state)) _peers[message.From.Id] = state = new PeerState();
                        state.Focus = ReadVector(reader);
                        state.HasFocus = true;
                        break;
                    }
                    case TagOwnedSpawn when host:
                    {
                        int localId = reader.ReadInt32();
                        string prefab = reader.ReadString();
                        Vector3 position = ReadVector(reader), rotation = ReadVector(reader), scale = ReadVector(reader);
                        ClipState clip = ReadClip(reader);
                        if (!_peers.TryGetValue(message.From.Id, out PeerState state)) _peers[message.From.Id] = state = new PeerState();
                        if (state.Owned.ContainsKey(localId) || CreateCopy == null) break;
                        Entity entity = CreateCopy(_world, prefab, position, rotation, scale);
                        if (entity.IsNull) break;
                        ApplyClip(_world, entity, clip);
                        int netId = _nextNetId++;
                        var arrived = new Shared
                        {
                            Entity = entity, Prefab = prefab, OwnerPeer = message.From.Id,
                            LastSentPosition = position, LastSentRotation = rotation,
                            Target = new GlideTarget { Position = position, Rotation = rotation },
                        };
                        ReadValues(reader, arrived, changed: false);
                        SpriteState look = ReadSprite(reader, out SpriteFields lookFields);
                        // Made as it looks; later angles are glided to (see Glide).
                        ApplySprite(_world, entity, lookFields, look);
                        if ((lookFields & SpriteFields.Angle) != 0) { arrived.Target.Angle = look.Angle; arrived.Target.HasAngle = true; }
                        _shared[netId] = arrived;
                        _indexStale = true;
                        state.Owned[localId] = netId;
                        break;
                    }
                    case TagOwnedSprite when host:
                    {
                        int localId = reader.ReadInt32();
                        SpriteState look = ReadSprite(reader, out SpriteFields lookFields);
                        // Shown on the host's copy; the host then tells the other players as it would for its own objects.
                        if (_peers.TryGetValue(message.From.Id, out PeerState state) && state.Owned.TryGetValue(localId, out int netId)
                            && _shared.TryGetValue(netId, out Shared shared))
                            ApplySprite(_world, shared.Entity, GlideAngle(ref shared.Target, lookFields, look), look);
                        break;
                    }
                    case TagSprites when !host:
                    {
                        int count = reader.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            int netId = reader.ReadInt32();
                            SpriteState look = ReadSprite(reader, out SpriteFields lookFields);
                            if (_copies.TryGetValue(netId, out Copy copy))
                                ApplySprite(_world, copy.Entity, GlideAngle(ref copy.Target, lookFields, look), look);
                        }

                        break;
                    }
                    case TagOwnedValues when host:
                    {
                        int localId = reader.ReadInt32();
                        // Kept on the host's copy, and passed on to the other players as the host's own values are.
                        if (_peers.TryGetValue(message.From.Id, out PeerState state) && state.Owned.TryGetValue(localId, out int netId)
                            && _shared.TryGetValue(netId, out Shared shared))
                            ReadValues(reader, shared, changed: true);
                        break;
                    }
                    case TagValues when !host:
                    {
                        int netId = reader.ReadInt32();
                        if (_copies.TryGetValue(netId, out Copy copy)) ReadValues(reader, copy, changed: false);
                        break;
                    }
                    case TagOwnedClip when host:
                    {
                        int localId = reader.ReadInt32();
                        ClipState clip = ReadClip(reader);
                        // Set on the host's copy; the host then tells the other players as it would for its own objects.
                        if (_peers.TryGetValue(message.From.Id, out PeerState state) && state.Owned.TryGetValue(localId, out int netId)
                            && _shared.TryGetValue(netId, out Shared shared) && _world.IsAlive(shared.Entity))
                            ApplyClip(_world, shared.Entity, clip);
                        break;
                    }
                    case TagClip when !host:
                    {
                        int netId = reader.ReadInt32();
                        ClipState clip = ReadClip(reader);
                        if (_copies.TryGetValue(netId, out Copy copy) && _world.IsAlive(copy.Entity)) ApplyClip(_world, copy.Entity, clip);
                        break;
                    }
                    case TagOwnedState when host:
                    {
                        int localId = reader.ReadInt32();
                        Vector3 position = ReadVector(reader), rotation = ReadVector(reader);
                        // The host's copy glides there each frame (see Update), as a player's copies do.
                        if (_peers.TryGetValue(message.From.Id, out PeerState state) && state.Owned.TryGetValue(localId, out int netId)
                            && _shared.TryGetValue(netId, out Shared shared))
                        {
                            shared.Target.Position = position;
                            shared.Target.Rotation = rotation;
                        }

                        break;
                    }
                    case TagOwnedDespawn when host:
                    {
                        int localId = reader.ReadInt32();
                        if (_peers.TryGetValue(message.From.Id, out PeerState state) && state.Owned.TryGetValue(localId, out int netId))
                        {
                            if (_shared.TryGetValue(netId, out Shared shared) && _world.IsAlive(shared.Entity)) _world.DestroyEntity(shared.Entity);
                            DropShared(netId);
                        }

                        break;
                    }
                    case TagResync when host:
                    {
                        // The player has lost copies: forget what it was sent, so it is sent again.
                        if (_peers.TryGetValue(message.From.Id, out PeerState state)) state.Known.Clear();
                        break;
                    }
                    case TagResync when !host:
                    {
                        // The host has lost its copies of this player's objects: announce them again.
                        foreach (Mine mine in _mine.Values) mine.Announced = false;
                        break;
                    }
                    case TagSpawn when !host:
                    {
                        int netId = reader.ReadInt32();
                        string prefab = reader.ReadString();
                        Vector3 position = ReadVector(reader), rotation = ReadVector(reader), scale = ReadVector(reader);
                        ClipState clip = ReadClip(reader);
                        if (_copies.ContainsKey(netId) || CreateCopy == null) break;
                        Entity entity = CreateCopy(_world, prefab, position, rotation, scale);
                        if (entity.IsNull) break;
                        ApplyClip(_world, entity, clip);
                        var copy = new Copy
                        {
                            Entity = entity, Prefab = prefab, OwnerPeer = reader.ReadInt32(),
                            Target = new GlideTarget { Position = position, Rotation = rotation },
                        };
                        ReadValues(reader, copy, changed: false);
                        SpriteState look = ReadSprite(reader, out SpriteFields lookFields);
                        // Made as it looks; later angles are glided to (see Glide).
                        ApplySprite(_world, entity, lookFields, look);
                        if ((lookFields & SpriteFields.Angle) != 0) { copy.Target.Angle = look.Angle; copy.Target.HasAngle = true; }
                        _copies[netId] = copy;
                        _indexStale = true;
                        break;
                    }
                    case TagDespawn when !host:
                    {
                        int netId = reader.ReadInt32();
                        if (_copies.Remove(netId, out Copy copy))
                        {
                            _indexStale = true;
                            if (_world.IsAlive(copy.Entity)) _world.DestroyEntity(copy.Entity);
                        }

                        break;
                    }
                    case TagSnapshot when !host:
                    {
                        int count = reader.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            int netId = reader.ReadInt32();
                            Vector3 position = ReadVector(reader), rotation = ReadVector(reader);
                            if (!_copies.TryGetValue(netId, out Copy copy)) continue;
                            copy.Target.Position = position;
                            copy.Target.Rotation = rotation;
                        }

                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
            {
                // A truncated or foreign packet must not take the game down.
            }
        }

        /// <summary>Forgets everything learned from the network; this machine's own objects are kept to be announced again.</summary>
        private void Reset(EcsWorld world)
        {
            _indexStale = true;
            foreach (Copy copy in _copies.Values)
                if (world.IsAlive(copy.Entity)) world.DestroyEntity(copy.Entity);
            _copies.Clear();
            foreach (PeerState peer in _peers.Values)
                foreach (int netId in peer.Owned.Values)
                    if (_shared.Remove(netId, out Shared shared) && world.IsAlive(shared.Entity)) world.DestroyEntity(shared.Entity);
            _peers.Clear();
            foreach (Mine mine in _mine.Values) mine.Announced = false;
        }

        // ── Transforms and encoding ───────────────────────────────────────────

        private static void ReadTransform(EcsWorld world, Entity entity, out Vector3 position, out Vector3 rotation)
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            position = new Vector3(transform.X, transform.Y, transform.Z);
            rotation = new Vector3(transform.RotationX, transform.RotationY, transform.RotationZ);
        }

        private static void ReadScale(EcsWorld world, Entity entity, out Vector3 scale)
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            scale = new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ);
        }

        /// <summary>
        /// Moves a copy one frame's share of the way to where its object was last reported: its
        /// position, its rotation and a 2D object's angle. A copy within what a sender treats as
        /// standing still is put exactly there.
        /// </summary>
        private static void Glide(EcsWorld world, Entity entity, ref GlideTarget target, float blend)
        {
            ReadTransform(world, entity, out Vector3 position, out Vector3 rotation);
            Vector3 next = Vector3.DistanceSquared(position, target.Position) <= StillDistanceSquared
                ? target.Position : Vector3.Lerp(position, target.Position, blend);
            SetTransform(world, entity, next, LerpAngles(rotation, target.Rotation, blend));
            if (!target.HasAngle) return;
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            transform.Rotation = LerpAngle(transform.Rotation, target.Angle, blend);
        }

        /// <summary>
        /// A 2D angle that arrives is glided to, not set at once: it becomes the copy's target and
        /// is taken out of what is applied. The other parts of a look are set as they arrive: a
        /// sprite, a frame or a flip has nothing between, and a blink or flash of alpha would blur.
        /// </summary>
        private static SpriteFields GlideAngle(ref GlideTarget target, SpriteFields fields, SpriteState look)
        {
            if ((fields & SpriteFields.Angle) == 0) return fields;
            target.Angle = look.Angle;
            target.HasAngle = true;
            return fields & ~SpriteFields.Angle;
        }

        private static void SetTransform(EcsWorld world, Entity entity, Vector3 position, Vector3 rotation)
        {
            if (!world.Has<TransformComponent>(entity)) return;
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            transform.X = position.X; transform.Y = position.Y; transform.Z = position.Z;
            transform.RotationX = rotation.X; transform.RotationY = rotation.Y; transform.RotationZ = rotation.Z;
            if (!world.Has<Transform3DComponent>(entity)) return;
            ref Transform3DComponent spatial = ref world.GetRef<Transform3DComponent>(entity);
            spatial.Position = position;
            spatial.Rotation = Quaternion.CreateFromYawPitchRoll(
                rotation.Y * MathF.PI / 180f, rotation.X * MathF.PI / 180f, rotation.Z * MathF.PI / 180f);
        }

        private static ClipState ReadClip(EcsWorld world, Entity entity)
        {
            if (!world.Has<ModelAnimatorComponent>(entity)) return ClipState.None;
            ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
            // A paused animation is a speed of nothing; the copy then holds its pose too.
            return new ClipState(animator.ClipName ?? "", animator.Loop, animator.Playing ? animator.PlaybackSpeed : 0f);
        }

        /// <summary>Makes a copy play what its object plays, blending from what it played before.</summary>
        private static void ApplyClip(EcsWorld world, Entity entity, ClipState clip)
        {
            if (!world.Has<ModelAnimatorComponent>(entity))
            {
                if (clip.Clip.Length == 0) return;
                world.Set(entity, new ModelAnimatorComponent { ClipFps = 0f, PlaybackSpeed = 1f, Playing = true, Loop = true });
            }

            ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
            if (!string.Equals(animator.ClipName ?? "", clip.Clip, StringComparison.Ordinal))
            {
                animator.PreviousClipName = animator.ClipName ?? "";
                animator.PreviousTimeSeconds = animator.TimeSeconds;
                animator.BlendDuration = animator.PreviousClipName.Length > 0 ? 0.15f : 0f;
                animator.BlendElapsed = 0f;
                animator.ClipName = clip.Clip;
                animator.TimeSeconds = 0f;
            }

            animator.Loop = clip.Loop;
            animator.Playing = clip.Speed != 0f;
            if (clip.Speed != 0f) animator.PlaybackSpeed = clip.Speed;
        }

        /// <summary>The look of an object drawn in 2D; <see cref="SpriteState.None"/> for anything else.</summary>
        private static SpriteState ReadSprite(EcsWorld world, Entity entity)
        {
            if (!world.Has<Draw2DComponent>(entity)) return SpriteState.None;
            bool visible = world.GetRef<Draw2DComponent>(entity).Visible;
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            string image = Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var assets) ? assets.Image ?? "" : "";
            bool frames = world.Has<SpriteComponent>(entity);
            int frame = 0;
            float alpha = 1f;
            if (frames)
            {
                ref SpriteComponent sprite = ref world.GetRef<SpriteComponent>(entity);
                frame = sprite.ImageIndex;
                alpha = sprite.Alpha;
            }

            return new SpriteState(true, frames, image, frame, transform.ScaleX, transform.ScaleY, transform.Rotation, alpha, visible);
        }

        /// <summary>Everything a copy needs to look like the object when it is made.</summary>
        private static SpriteFields AllSpriteFields(SpriteState state) =>
            !state.Is2D ? SpriteFields.None
            : SpriteFields.Sprite | SpriteFields.Scale | SpriteFields.Angle | SpriteFields.Visible
                | (state.HasFrames ? SpriteFields.Frame | SpriteFields.Alpha : SpriteFields.None);

        /// <summary>The parts of an object's look that are not what was last sent.</summary>
        private static SpriteFields SpriteChanges(SpriteState sent, SpriteState now)
        {
            if (!now.Is2D) return SpriteFields.None;
            if (!sent.Is2D || sent.HasFrames != now.HasFrames) return AllSpriteFields(now);
            SpriteFields changed = SpriteFields.None;
            if (!string.Equals(sent.Sprite, now.Sprite, StringComparison.Ordinal)) changed |= SpriteFields.Sprite;
            if (now.HasFrames && sent.Frame != now.Frame) changed |= SpriteFields.Frame;
            if (sent.XScale != now.XScale || sent.YScale != now.YScale) changed |= SpriteFields.Scale;
            if (sent.Angle != now.Angle) changed |= SpriteFields.Angle;
            if (now.HasFrames && sent.Alpha != now.Alpha) changed |= SpriteFields.Alpha;
            if (sent.Visible != now.Visible) changed |= SpriteFields.Visible;
            return changed;
        }

        /// <summary>One byte saying which parts follow, then only those parts.</summary>
        private static void WriteSprite(BinaryWriter writer, SpriteState state, SpriteFields fields)
        {
            writer.Write((byte)fields);
            if ((fields & SpriteFields.Sprite) != 0) writer.Write(state.Sprite ?? "");
            if ((fields & SpriteFields.Frame) != 0) writer.Write(state.Frame);
            if ((fields & SpriteFields.Scale) != 0) { writer.Write(state.XScale); writer.Write(state.YScale); }
            if ((fields & SpriteFields.Angle) != 0) writer.Write(state.Angle);
            if ((fields & SpriteFields.Alpha) != 0) writer.Write(state.Alpha);
            if ((fields & SpriteFields.Visible) != 0) writer.Write(state.Visible);
        }

        private static SpriteState ReadSprite(BinaryReader reader, out SpriteFields fields)
        {
            fields = (SpriteFields)reader.ReadByte();
            string sprite = (fields & SpriteFields.Sprite) != 0 ? reader.ReadString() : "";
            int frame = (fields & SpriteFields.Frame) != 0 ? reader.ReadInt32() : 0;
            float xScale = 1f, yScale = 1f;
            if ((fields & SpriteFields.Scale) != 0) { xScale = reader.ReadSingle(); yScale = reader.ReadSingle(); }
            float angle = (fields & SpriteFields.Angle) != 0 ? reader.ReadSingle() : 0f;
            float alpha = (fields & SpriteFields.Alpha) != 0 ? reader.ReadSingle() : 1f;
            bool visible = (fields & SpriteFields.Visible) == 0 || reader.ReadBoolean();
            // What another machine sends is held to sensible limits.
            if (sprite.Length > MaxSpriteName) fields &= ~SpriteFields.Sprite;
            if (!float.IsFinite(xScale) || !float.IsFinite(yScale)) fields &= ~SpriteFields.Scale;
            if (!float.IsFinite(angle)) fields &= ~SpriteFields.Angle;
            if (!float.IsFinite(alpha)) fields &= ~SpriteFields.Alpha;
            return new SpriteState(true, (fields & (SpriteFields.Frame | SpriteFields.Alpha)) != 0, sprite, Math.Max(0, frame),
                xScale, yScale, angle, Math.Clamp(alpha, 0f, 1f), visible);
        }

        /// <summary>Makes a copy look as its object does, in the parts that were sent.</summary>
        private static void ApplySprite(EcsWorld world, Entity entity, SpriteFields fields, SpriteState state)
        {
            if (fields == SpriteFields.None || !world.IsAlive(entity)) return;
            if ((fields & SpriteFields.Sprite) != 0)
            {
                if (Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var assets)) assets.Image = state.Sprite;
                else if (state.Sprite.Length > 0)
                    Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.Set(entity, new Genesis.Runtime.Rendering.ObjectDrawAssetEntry { Image = state.Sprite });
            }

            bool frames = (fields & (SpriteFields.Frame | SpriteFields.Alpha)) != 0;
            if (frames && !world.Has<SpriteComponent>(entity))
                world.Set(entity, new SpriteComponent { Alpha = 1f, AnimationTagIndex = -1, AnimationLoopOverride = -1 });
            if (world.Has<SpriteComponent>(entity))
            {
                // The copy holds the frame it is sent; its own playback would move it on.
                ref SpriteComponent sprite = ref world.GetRef<SpriteComponent>(entity);
                sprite.FrameFromNetwork = true;
                if ((fields & SpriteFields.Frame) != 0) sprite.ImageIndex = state.Frame;
                if ((fields & SpriteFields.Alpha) != 0) sprite.Alpha = state.Alpha;
            }

            if ((fields & (SpriteFields.Scale | SpriteFields.Angle)) != 0 && world.Has<TransformComponent>(entity))
            {
                ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
                if ((fields & SpriteFields.Scale) != 0) { transform.ScaleX = state.XScale; transform.ScaleY = state.YScale; }
                if ((fields & SpriteFields.Angle) != 0) transform.Rotation = state.Angle;
            }

            if ((fields & SpriteFields.Visible) != 0 && world.Has<Draw2DComponent>(entity))
                world.GetRef<Draw2DComponent>(entity).Visible = state.Visible;
        }

        private static void WriteValues(BinaryWriter writer, Tracked record, bool onlyChanged)
        {
            if (record.Values == null || (onlyChanged && record.Dirty is not { Count: > 0 }))
            {
                writer.Write((byte)0);
                return;
            }

            if (onlyChanged)
            {
                writer.Write((byte)record.Dirty.Count);
                foreach (string name in record.Dirty) WriteValue(writer, name, record.Values[name]);
                return;
            }

            writer.Write((byte)record.Values.Count);
            foreach (KeyValuePair<string, object> pair in record.Values) WriteValue(writer, pair.Key, pair.Value);
        }

        private static void WriteValue(BinaryWriter writer, string name, object value)
        {
            writer.Write(name);
            if (value is string text) { writer.Write((byte)1); writer.Write(text); }
            else { writer.Write((byte)0); writer.Write((double)value); }
        }

        /// <param name="changed">True when what is read must be passed on to other machines.</param>
        private static void ReadValues(BinaryReader reader, Tracked record, bool changed)
        {
            int count = reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                object value = reader.ReadByte() == 1 ? reader.ReadString() : reader.ReadDouble();
                // What another machine sends is held to the limits this one would be.
                if (name.Length == 0 || name.Length > MaxValueName || value is string { Length: > MaxValueText }) continue;
                record.Values ??= new Dictionary<string, object>(StringComparer.Ordinal);
                if (!record.Values.ContainsKey(name) && record.Values.Count >= MaxValues) continue;
                record.Values[name] = value;
                if (!changed) continue;
                record.Dirty ??= new List<string>();
                if (!record.Dirty.Contains(name)) record.Dirty.Add(name);
            }
        }

        private static void WriteClip(BinaryWriter writer, ClipState clip)
        {
            writer.Write(clip.Clip ?? ""); writer.Write(clip.Loop); writer.Write(clip.Speed);
        }

        private static ClipState ReadClip(BinaryReader reader) => new(reader.ReadString(), reader.ReadBoolean(), reader.ReadSingle());

        private static Vector3 LerpAngles(Vector3 from, Vector3 to, float amount) =>
            new(LerpAngle(from.X, to.X, amount), LerpAngle(from.Y, to.Y, amount), LerpAngle(from.Z, to.Z, amount));

        private static float LerpAngle(float from, float to, float amount)
        {
            float difference = (to - from) % 360f;
            if (difference > 180f) difference -= 360f;
            if (difference < -180f) difference += 360f;
            // Close enough is exactly there: a float glide would otherwise stop a hair short.
            return MathF.Abs(difference) <= StillDegrees ? to : from + difference * amount;
        }

        private static byte[] Write(Action<BinaryWriter> body)
        {
            using var stream = new MemoryStream(64);
            using (var writer = new BinaryWriter(stream)) body(writer);
            return stream.ToArray();
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
        }

        private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}

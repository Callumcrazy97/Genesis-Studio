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
    /// </remarks>
    public sealed class NetworkReplication
    {
        // Negative tags are reserved for the engine; game messages use positive ones.
        public const int TagSpawn = -7001, TagDespawn = -7002, TagSnapshot = -7003, TagFocus = -7004,
            TagOwnedSpawn = -7005, TagOwnedState = -7006, TagOwnedDespawn = -7007, TagResync = -7008,
            TagClip = -7009, TagOwnedClip = -7010;

        private const int SnapshotEntriesPerMessage = 36;

        /// <summary>
        /// Movement is sent unreliably, so the last message of a move may be lost and leave a copy
        /// short of where the object stopped. A stopped object's position is sent this many more
        /// times before the sender goes quiet.
        /// </summary>
        private const int SettleRepeats = 4;

        /// <summary>The animation a model is playing, as far as a copy needs to know it.</summary>
        private readonly record struct ClipState(string Clip, bool Loop, float Speed)
        {
            public static readonly ClipState None = new("", true, 1f);
        }

        private sealed class Shared
        {
            public Entity Entity;
            public string Prefab;
            public ClipState Clip = ClipState.None;
            public bool ClipChanged;
            /// <summary>0 for the host's own; otherwise the player who owns it.</summary>
            public int OwnerPeer;
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

        private sealed class Copy
        {
            public Entity Entity;
            public Vector3 TargetPosition, TargetRotation;
        }

        private sealed class Mine
        {
            public Entity Entity;
            public string Prefab;
            public ClipState Clip = ClipState.None;
            public bool Announced;
            public Vector3 LastSentPosition, LastSentRotation;
            public int Repeats;
        }

        private readonly Dictionary<int, Shared> _shared = new();
        private readonly Dictionary<int, PeerState> _peers = new();
        private readonly Dictionary<int, Copy> _copies = new();
        private readonly Dictionary<int, Mine> _mine = new();
        private readonly List<int> _scratch = new();
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

        /// <summary>Copies of other machines' objects that exist here.</summary>
        public int CopyCount => _copies.Count;

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
            float blend = 1f - MathF.Exp(-MathF.Max(0f, deltaSeconds) * 14f);
            _scratch.Clear();
            foreach (KeyValuePair<int, Copy> pair in _copies)
            {
                Copy copy = pair.Value;
                if (!world.IsAlive(copy.Entity) || !world.Has<TransformComponent>(copy.Entity)) { _scratch.Add(pair.Key); continue; }
                ReadTransform(world, copy.Entity, out Vector3 position, out Vector3 rotation);
                SetTransform(world, copy.Entity, Vector3.Lerp(position, copy.TargetPosition, blend),
                    LerpAngles(rotation, copy.TargetRotation, blend));
            }

            // A copy that was removed here (a script, a reloaded room) while its object lives on:
            // ask the host to say again what is near, at most once a second.
            foreach (int id in _scratch) _copies.Remove(id);
            if (_scratch.Count > 0) _resyncWanted = true;
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
                ReadTransform(world, shared.Entity, out Vector3 position, out Vector3 rotation);
                bool moved = Vector3.DistanceSquared(position, shared.LastSentPosition) > 1e-6f
                    || Vector3.DistanceSquared(rotation, shared.LastSentRotation) > 1e-4f;
                if (moved) { shared.LastSentPosition = position; shared.LastSentRotation = rotation; shared.Repeats = SettleRepeats; }
                else if (shared.Repeats > 0) shared.Repeats--;
                shared.Moved = moved || shared.Repeats > 0;
                ClipState clip = ReadClip(world, shared.Entity);
                shared.ClipChanged = clip != shared.Clip;
                shared.Clip = clip;
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
            }
        }

        private void DropShared(int id)
        {
            if (!_shared.Remove(id)) return;
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
                if (!mine.Announced)
                {
                    ReadScale(world, mine.Entity, out Vector3 scale);
                    _network.Send(NetPeer.Host.Id, TagOwnedSpawn, Write(w =>
                    {
                        w.Write(pair.Key); w.Write(mine.Prefab);
                        WriteVector(w, position); WriteVector(w, rotation); WriteVector(w, scale);
                        WriteClip(w, clip);
                    }));
                    mine.Announced = true;
                    mine.LastSentPosition = position;
                    mine.LastSentRotation = rotation;
                    mine.Clip = clip;
                    continue;
                }

                if (clip != mine.Clip)
                {
                    mine.Clip = clip;
                    _network.Send(NetPeer.Host.Id, TagOwnedClip, Write(w => { w.Write(pair.Key); WriteClip(w, clip); }));
                }

                bool moved = Vector3.DistanceSquared(position, mine.LastSentPosition) > 1e-6f
                    || Vector3.DistanceSquared(rotation, mine.LastSentRotation) > 1e-4f;
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
            }
        }

        // ── Messages ──────────────────────────────────────────────────────────

        private void OnMessage(NetMessage message)
        {
            if (message.Tag > TagSpawn || message.Tag < TagOwnedClip || message.Payload == null) return;
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
                        _shared[netId] = new Shared
                        {
                            Entity = entity, Prefab = prefab, OwnerPeer = message.From.Id,
                            LastSentPosition = position, LastSentRotation = rotation,
                        };
                        state.Owned[localId] = netId;
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
                        if (_peers.TryGetValue(message.From.Id, out PeerState state) && state.Owned.TryGetValue(localId, out int netId)
                            && _shared.TryGetValue(netId, out Shared shared) && _world.IsAlive(shared.Entity))
                            SetTransform(_world, shared.Entity, position, rotation);
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
                        _copies[netId] = new Copy { Entity = entity, TargetPosition = position, TargetRotation = rotation };
                        break;
                    }
                    case TagDespawn when !host:
                    {
                        int netId = reader.ReadInt32();
                        if (_copies.Remove(netId, out Copy copy) && _world.IsAlive(copy.Entity)) _world.DestroyEntity(copy.Entity);
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
                            copy.TargetPosition = position;
                            copy.TargetRotation = rotation;
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
                world.Set(entity, new ModelAnimatorComponent { ClipFps = 60f, PlaybackSpeed = 1f, Playing = true, Loop = true });
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
            return from + difference * amount;
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

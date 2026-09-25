using System;
using System.Collections.Generic;
// Alias LiteNetLib types to avoid collision with Genesis.Shared.Net.NetPeer.
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetPeer  = LiteNetLib.NetPeer;
using LSocketError = System.Net.Sockets.SocketError;
using Genesis.Shared.Net;
using GPeer = Genesis.Shared.Net.NetPeer;

namespace Genesis.Net
{
    // ════════════════════════════════════════════════════════════════════════════
    //   LiteNetGameNetwork — the LiteNetLib-backed IGameNetwork.
    //
    //   LiteNetLib is a reliable-UDP C# library with NAT punchthrough. This wrapper
    //   exposes it behind the transport-agnostic IGameNetwork interface so gameplay
    //   code never sees LiteNetLib types. The host runs a NetManager in server mode;
    //   a client runs one in client mode. Incoming packets are queued and dispatched
    //   on the main thread by Update() (LiteNetLib events are dispatched by PollEvents).
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reliable-UDP networking via LiteNetLib, exposed through <see cref="IGameNetwork"/>.
    /// One instance per process: either hosting or connected (not both).
    /// </summary>
    public sealed class LiteNetGameNetwork : IGameNetwork, INetEventListener
    {
        private sealed class PeerEntry
        {
            public int Id;
            public LiteNetPeer Peer;
            public string Address;
        }

        private readonly string AppKey;
        private readonly int _maximumPeers;
        public const int MaximumPayloadBytes = 262144;
        private const int MaximumQueuedMessages = 4096;
        private const int MaximumQueuedPayloadBytes = 16 * 1024 * 1024;
        private int _queuedPayloadBytes;
        private readonly int _firstPeerId = 1;
        private int _nextPeerId;
        private readonly Dictionary<int, PeerEntry> _peersById = new();
        private readonly Dictionary<int, PeerEntry> _peersByLiteId = new();
        private readonly List<GPeer> _peerSnapshot = new();

        private NetManager _manager;
        private readonly NetDataWriter _writer = new();
        private readonly Queue<NetMessage> _inbox = new();
        private readonly Queue<(GPeer peer, bool connected)> _connectionEvents = new();

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public int Port { get; private set; }
        public int PeerCount => _peersById.Count;

        public IReadOnlyList<GPeer> Peers
        {
            get
            {
                _peerSnapshot.Clear();
                foreach (var e in _peersById.Values)
                    _peerSnapshot.Add(new GPeer(e.Id, e.Address));
                return _peerSnapshot;
            }
        }

        public event Action<NetMessage> OnMessageReceived;
        public event Action<GPeer> OnPeerConnected;
        public event Action<GPeer> OnPeerDisconnected;

        public LiteNetGameNetwork(string applicationKey = "genesis-runtime-2", int maximumPeers = 64)
        {
            if (string.IsNullOrWhiteSpace(applicationKey) || applicationKey.Length > 128) throw new ArgumentException("Invalid application key.", nameof(applicationKey));
            if (maximumPeers < 1 || maximumPeers > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPeers));
            AppKey = applicationKey; _maximumPeers = maximumPeers;
            _manager = new NetManager(this)
            {
                AutoRecycle = true,
                UnsyncedEvents = false,  // callbacks run only during PollEvents on the owning game thread
                UpdateTime = 15,
                BroadcastReceiveEnabled = true,
            };
        }

        // ── Host / connect / disconnect ──────────────────────────────────────────

        public void Host(int port)
        {
            Disconnect();
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (!_manager.Start(port)) throw new InvalidOperationException($"Could not bind UDP port {port}.");
            IsHost = true;
            IsConnected = true;
            Port = port;
            _nextPeerId = _firstPeerId;
        }

        public void Connect(string ip, int port)
        {
            Disconnect();
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("Host address is required.", nameof(ip));
            if (!_manager.Start()) throw new InvalidOperationException("Could not start the UDP client.");
            _manager.Connect(ip, port, AppKey);
            IsHost = false;
            Port = port;
            _nextPeerId = _firstPeerId;   // clients assign ids just like hosts do
        }

        public void Disconnect()
        {
            var disconnected = new List<GPeer>();
            foreach (var entry in _peersById.Values) disconnected.Add(new GPeer(entry.Id, entry.Address));
            _manager?.Stop();
            _peersById.Clear();
            _peersByLiteId.Clear();
            IsHost = false;
            IsConnected = false;
            Port = 0;
            _inbox.Clear(); _queuedPayloadBytes = 0; _connectionEvents.Clear(); _peerSnapshot.Clear();
            foreach (var peer in disconnected) OnPeerDisconnected?.Invoke(peer);
        }

        // ── Send / broadcast ──────────────────────────────────────────────────────

        public void Send(int peerId, int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered)
        {
            if (peerId == 0) { Broadcast(tag, payload, delivery); return; }
            if (peerId == -1 && !IsHost)
                foreach (var candidate in _peersById.Values) { peerId = candidate.Id; break; }
            if (!_peersById.TryGetValue(peerId, out var entry) || entry.Peer == null) return;
            var packet = BuildPacket(tag, payload);
            entry.Peer.Send(packet, MapDelivery(delivery));
        }

        public void Broadcast(int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered)
        {
            if (!IsHost || _manager == null) return;
            var packet = BuildPacket(tag, payload);
            _manager.SendToAll(packet, MapDelivery(delivery));
        }

        private NetDataWriter BuildPacket(int tag, byte[] payload)
        {
            _writer.Reset();
            _writer.Put(tag);
            int length = payload?.Length ?? 0;
            if (length > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload), "Network payload is too large.");
            _writer.Put(length); // int32 length: unlike PutBytesWithLength, this supports > 64 KiB chunks.
            if (length > 0) _writer.Put(payload);
            return _writer;
        }

        private static DeliveryMethod MapDelivery(NetDelivery d) => d switch
        {
            NetDelivery.Unreliable        => DeliveryMethod.Unreliable,
            NetDelivery.ReliableUnordered => DeliveryMethod.ReliableUnordered,
            _                              => DeliveryMethod.ReliableOrdered,
        };

        // ── Main-thread pump ──────────────────────────────────────────────────────

        public void Update()
        {
            _manager?.PollEvents();

            // Drain connection events on the main thread.
            while (_connectionEvents.Count > 0)
            {
                var (peer, connected) = _connectionEvents.Dequeue();
                if (connected) OnPeerConnected?.Invoke(peer);
                else OnPeerDisconnected?.Invoke(peer);
            }

            // Drain inbox on the main thread.
            while (_inbox.Count > 0)
            {
                var message = _inbox.Dequeue(); _queuedPayloadBytes -= message.Payload.Length;
                OnMessageReceived?.Invoke(message);
            }
        }

        // ── INetEventListener (called by PollEvents on the game thread — queue for ordered dispatch) ─

        void INetEventListener.OnPeerConnected(LiteNetPeer peer)
        {
            int id = _nextPeerId++;
            var entry = new PeerEntry { Id = id, Peer = peer, Address = peer?.ToString() ?? "unknown" };
            _peersById[id] = entry;
            _peersByLiteId[peer.Id] = entry;
            _connectionEvents.Enqueue((new GPeer(id, entry.Address), true));
            // For a client, a successful peer connection means we're now connected.
            if (!IsHost) IsConnected = true;
        }

        void INetEventListener.OnPeerDisconnected(LiteNetPeer peer, DisconnectInfo info)
        {
            if (peer != null && _peersByLiteId.TryGetValue(peer.Id, out var entry))
            {
                _peersById.Remove(entry.Id);
                _peersByLiteId.Remove(peer.Id);
                _connectionEvents.Enqueue((new GPeer(entry.Id, entry.Address), false));
            }
            // If we were a client and the host drops, mark disconnected.
            if (!IsHost) IsConnected = false;
        }

        void INetEventListener.OnNetworkReceive(LiteNetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            try
            {
                if (reader.AvailableBytes < 8 || reader.AvailableBytes > MaximumPayloadBytes + 8 || _inbox.Count >= MaximumQueuedMessages) return;
                int tag = reader.GetInt();
                int length = reader.GetInt();
                if (length < 0 || length > MaximumPayloadBytes || length != reader.AvailableBytes ||
                    _queuedPayloadBytes + length > MaximumQueuedPayloadBytes) return;
                byte[] payload = new byte[length]; reader.GetBytes(payload, length);
                int fromId = (peer != null && _peersByLiteId.TryGetValue(peer.Id, out var e)) ? e.Id : 0;
                string addr = peer?.ToString() ?? "unknown";
                _inbox.Enqueue(new NetMessage(tag, new GPeer(fromId, addr), payload));
                _queuedPayloadBytes += length;
            }
            catch { /* malformed packet — ignore */ }
        }

        void INetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, LSocketError socketError) { }
        void INetEventListener.OnNetworkReceiveUnconnected(System.Net.IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        void INetEventListener.OnNetworkLatencyUpdate(LiteNetPeer peer, int latency) { }
        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (!IsHost || _peersById.Count >= _maximumPeers) request.Reject();
            else request.AcceptIfKey(AppKey);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}

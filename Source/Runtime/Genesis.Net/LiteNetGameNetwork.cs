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
    /// <remarks>
    /// A host also answers machines on the local network that look for games of the same title and
    /// version (<see cref="Discover"/>): a small unconnected request, broadcast and sent to this
    /// computer, answered with the host's name, port and number of players.
    /// It keeps where this machine's own session stands and why the last attempt ended
    /// (<see cref="ConnectionState"/>, <see cref="LastError"/>), from LiteNetLib's disconnect
    /// reasons, the socket errors it reports, and the reason a host of this engine gives when it
    /// turns a connection away.
    /// </remarks>
    public sealed partial class LiteNetGameNetwork : IGameNetwork, IGameNetworkDiscovery, IGameNetworkStatus, INetEventListener
    {
        private sealed class PeerEntry
        {
            public int Id;
            public LiteNetPeer Peer;
            public string Address;
            /// <summary>The peer's id at the relay, for a game played through one (Peer is then null).</summary>
            public int RelayId;
        }

        /// <summary>A game that answered this machine's request, and when it last did.</summary>
        private sealed class Discovered
        {
            public long Instance;
            public NetDiscoveredHost Host;
            public int AddressRank;
            public long SeenMilliseconds;
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

        // ── Finding games on the local network ──
        private const string DiscoveryRequest = "genesis-discover", DiscoveryAnswer = "genesis-host";
        /// <summary>How often a machine looking for games asks again, and how long a game that stops answering stays listed.</summary>
        private const long DiscoveryIntervalMilliseconds = 1000, DiscoveryExpiryMilliseconds = 4000;
        private const int MaximumDiscoveredHosts = 64, MaximumAdvertisedName = 64;
        private readonly List<Discovered> _discovered = new();
        private readonly List<NetDiscoveredHost> _discoveredSnapshot = new();
        private bool _discoveredChanged;
        private readonly NetDataWriter _discoveryWriter = new();
        /// <summary>Tells this machine's own requests and answers apart from other machines'.</summary>
        private readonly long _instance = Random.Shared.NextInt64(1, long.MaxValue);
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _discoverPort;
        private long _nextDiscoveryMilliseconds;
        private System.Net.IPEndPoint _discoverLoopback;
        /// <summary>The socket was opened only to look for games, and is closed when looking stops.</summary>
        private bool _openedForDiscovery;
        private string _advertisedName = "";

        /// <summary>
        /// Which game this is, such as its project id. A machine looking for games hears only hosts
        /// with the same key (and the same application key), so two different games on one network
        /// do not list each other. Empty matches only other empty keys.
        /// </summary>
        public string GameKey { get; set; } = "";

        /// <summary>The game's title: the name a host advertises until a script names it otherwise.</summary>
        public string GameTitle { get; set; } = "";

        public string AdvertisedName
        {
            get => string.IsNullOrWhiteSpace(_advertisedName) ? GameTitle ?? "" : _advertisedName;
            set => _advertisedName = Shorten(value?.Trim() ?? "", MaximumAdvertisedName);
        }

        public IReadOnlyList<NetDiscoveredHost> DiscoveredHosts
        {
            get
            {
                if (_discoveredChanged)
                {
                    _discoveredSnapshot.Clear();
                    foreach (Discovered found in _discovered) _discoveredSnapshot.Add(found.Host);
                    _discoveredChanged = false;
                }

                return _discoveredSnapshot;
            }
        }

        /// <summary>
        /// Binds to this machine only (127.0.0.1) instead of every network adapter. For two
        /// copies of a game on one computer, and for tests: nothing outside the machine can reach
        /// the socket, so Windows does not ask to allow it through the firewall. Set before
        /// <see cref="Host"/> or <see cref="Connect"/>.
        /// </summary>
        public bool LocalOnly { get; set; }

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

        /// <summary>
        /// The connection key the Player uses. It names the wire format: a build that encodes shared
        /// objects differently has a different key, so it is refused instead of misread.
        /// "genesis-runtime-3" (15 October 2026) added 2D looks to shared objects.
        /// </summary>
        public const string DefaultApplicationKey = "genesis-runtime-3";

        public LiteNetGameNetwork(string applicationKey = DefaultApplicationKey, int maximumPeers = 64)
            : this(applicationKey, maximumPeers, null)
        {
        }

        /// <summary>
        /// A network whose packets pass through <paramref name="packetLayer"/> on their way in and out
        /// (LiteNetLib's extra layer: a checksum, encryption, or a test's stand-in for a router).
        /// </summary>
        public LiteNetGameNetwork(string applicationKey, int maximumPeers, LiteNetLib.Layers.PacketLayerBase packetLayer)
        {
            if (string.IsNullOrWhiteSpace(applicationKey) || applicationKey.Length > 128) throw new ArgumentException("Invalid application key.", nameof(applicationKey));
            if (maximumPeers < 1 || maximumPeers > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPeers));
            AppKey = applicationKey; _maximumPeers = maximumPeers;
            _manager = new NetManager(this, packetLayer)
            {
                AutoRecycle = true,
                UnsyncedEvents = false,  // callbacks run only during PollEvents on the owning game thread
                UpdateTime = 15,
                BroadcastReceiveEnabled = true,
                // Requests for games sent to this computer, and the answers to this machine's own.
                UnconnectedMessagesEnabled = true,
            };
        }

        // ── Host / connect / disconnect ──────────────────────────────────────────

        public void Host(int port)
        {
            Disconnect();
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            bool started = LocalOnly
                ? _manager.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, port)
                : _manager.Start(port);
            if (!started)
            {
                Fail("port in use");
                throw new InvalidOperationException($"Could not bind UDP port {port}.");
            }

            IsHost = true;
            IsConnected = true;
            Port = port;
            _nextPeerId = _firstPeerId;
            _state = LinkState.Hosting;
        }

        public void Connect(string ip, int port)
        {
            Disconnect();
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("Host address is required.", nameof(ip));
            bool started = LocalOnly
                ? _manager.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, 0)
                : _manager.Start();
            if (!started)
            {
                Fail("unreachable");
                throw new InvalidOperationException("Could not start the UDP client.");
            }

            try
            {
                _target = _manager.Connect(ip, port, AppKey);
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException or FormatException)
            {
                // A name that does not resolve to an address.
                Fail("bad address");
                throw;
            }

            IsHost = false;
            Port = port;
            _nextPeerId = _firstPeerId;   // clients assign ids just like hosts do
            if (_target == null) Fail("bad address");
            else _state = LinkState.Connecting;
        }

        // ── Where this machine's session stands ──────────────────────────────────

        private enum LinkState { Idle, Hosting, Connecting, Connected, Failed, Disconnected }

        private LinkState _state;
        private string _lastError = "";
        /// <summary>The host being joined, and what the operating system said about reaching it.</summary>
        private System.Net.IPEndPoint _target;
        private LSocketError _targetSocketError;
        private readonly NetDataWriter _refusalWriter = new();
        private const string Refusal = "genesis-refusal";

        public string ConnectionState => _state switch
        {
            LinkState.Hosting => "hosting",
            LinkState.Connecting => "connecting",
            LinkState.Connected => "connected",
            LinkState.Failed => "failed",
            LinkState.Disconnected => "disconnected",
            _ => "idle",
        };

        public string LastError => _lastError;

        private void Fail(string reason)
        {
            _state = LinkState.Failed;
            _lastError = reason;
        }

        /// <summary>Why a joined (or joining) session ended, in a few words a game can show; null when this machine left.</summary>
        private string DescribeDisconnect(DisconnectInfo info)
        {
            switch (info.Reason)
            {
                case DisconnectReason.DisconnectPeerCalled:
                    return null;
                case DisconnectReason.ConnectionRejected:
                    // A host of this engine says why it turned the connection away.
                    try
                    {
                        if (info.AdditionalData is { AvailableBytes: > 0 } data
                            && string.Equals(data.GetString(32), Refusal, StringComparison.Ordinal))
                        {
                            string why = data.GetString(32);
                            if (why is "wrong version" or "full") return why;
                        }
                    }
                    catch { /* an older engine's refusal carries nothing */ }
                    return "refused";
                case DisconnectReason.ConnectionFailed:
                    // No answer at all. The operating system may have said why: nothing listens on
                    // that port, or there is no way to that machine.
                    return _targetSocketError switch
                    {
                        LSocketError.ConnectionReset or LSocketError.ConnectionRefused => "refused",
                        LSocketError.HostUnreachable or LSocketError.NetworkUnreachable => "unreachable",
                        _ => "timed out",
                    };
                case DisconnectReason.Timeout:
                    return "timed out";
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                    return "unreachable";
                case DisconnectReason.RemoteConnectionClose:
                    return "host closed";
                case DisconnectReason.InvalidProtocol:
                    return "wrong version";
                case DisconnectReason.UnknownHost:
                    return "bad address";
                default:
                    return "disconnected";
            }
        }

        private NetDataWriter RefusalFor(string reason)
        {
            _refusalWriter.Reset();
            _refusalWriter.Put(Refusal);
            _refusalWriter.Put(reason);
            return _refusalWriter;
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
            _state = LinkState.Idle; _lastError = ""; _target = null; _targetSocketError = LSocketError.Success;
            ForgetRelay();
            // Looking for games belongs to the time before a session: hosting or joining ends it.
            StopDiscovery();
            foreach (var peer in disconnected) OnPeerDisconnected?.Invoke(peer);
        }

        // ── Finding games on the local network ───────────────────────────────────

        public bool Discover(int port)
        {
            if (port <= 0)
            {
                bool idle = _openedForDiscovery && !IsHost && !IsConnected && _peersById.Count == 0;
                StopDiscovery();
                if (idle) _manager?.Stop();
                return true;
            }

            if (port > 65535 || _manager == null) return false;
            if (!_manager.IsRunning)
            {
                // Nothing hosted or joined yet: open a socket to ask from and to hear the answers on.
                bool started = LocalOnly
                    ? _manager.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, 0)
                    : _manager.Start();
                if (!started) return false;
                _openedForDiscovery = true;
            }

            // Asked again for the same port (a script calling this every frame): keep the pace.
            if (_discoverPort == port) return true;
            _discoverPort = port;
            _discoverLoopback = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
            AskForGames();
            return true;
        }

        private void StopDiscovery()
        {
            _discoverPort = 0;
            _openedForDiscovery = false;
            if (_discovered.Count > 0) { _discovered.Clear(); _discoveredChanged = true; }
        }

        private void AskForGames()
        {
            _nextDiscoveryMilliseconds = _clock.ElapsedMilliseconds + DiscoveryIntervalMilliseconds;
            if (_discoverPort == 0 || _manager is not { IsRunning: true }) return;
            _discoveryWriter.Reset();
            _discoveryWriter.Put(DiscoveryRequest);
            _discoveryWriter.Put(AppKey);
            _discoveryWriter.Put(GameKey ?? "");
            _discoveryWriter.Put(_instance);
            try
            {
                // A host on this computer, which a broadcast does not reach when it listens on this
                // machine only; then every machine on the local network.
                _manager.SendUnconnectedMessage(_discoveryWriter, _discoverLoopback);
                if (!LocalOnly) _manager.SendBroadcast(_discoveryWriter, _discoverPort);
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or InvalidOperationException or ObjectDisposedException)
            {
                // No network to ask on (a cable out, an adapter going down): try again next time.
            }
        }

        /// <summary>A host answers a machine looking for its game, unless the request is its own.</summary>
        private void AnswerDiscovery(System.Net.IPEndPoint from, NetDataReader reader)
        {
            string appKey = reader.GetString(128), gameKey = reader.GetString(256);
            long asker = reader.GetLong();
            if (!IsHost || asker == _instance || !string.Equals(appKey, AppKey, StringComparison.Ordinal)
                || !string.Equals(gameKey, GameKey ?? "", StringComparison.Ordinal)) return;
            _discoveryWriter.Reset();
            _discoveryWriter.Put(DiscoveryAnswer);
            _discoveryWriter.Put(AppKey);
            _discoveryWriter.Put(GameKey ?? "");
            _discoveryWriter.Put(_instance);
            _discoveryWriter.Put(AdvertisedName);
            _discoveryWriter.Put(PeerCount + 1);
            _discoveryWriter.Put(Port);
            _manager.SendUnconnectedMessage(_discoveryWriter, from);
        }

        private void HearAnswer(System.Net.IPEndPoint from, NetDataReader reader)
        {
            string appKey = reader.GetString(128), gameKey = reader.GetString(256);
            long host = reader.GetLong();
            string name = reader.GetString(MaximumAdvertisedName * 4);
            int players = reader.GetInt(), port = reader.GetInt();
            if (_discoverPort == 0 || host == _instance || port < 1 || port > 65535
                || !string.Equals(appKey, AppKey, StringComparison.Ordinal)
                || !string.Equals(gameKey, GameKey ?? "", StringComparison.Ordinal)) return;

            System.Net.IPAddress address = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
            // One game may answer more than once: through this computer's loopback, on the local
            // network, and over IPv6. The address another machine would type is the one kept.
            bool v4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
            int rank = (v4 ? 2 : 0) + (System.Net.IPAddress.IsLoopback(address) ? 0 : 1);
            var heard = new NetDiscoveredHost(address.ToString(), port, Shorten(name ?? "", MaximumAdvertisedName), Math.Max(1, players));
            long now = _clock.ElapsedMilliseconds;
            foreach (Discovered found in _discovered)
            {
                if (found.Instance != host) continue;
                if (rank < found.AddressRank)
                    heard = new NetDiscoveredHost(found.Host.Address, found.Host.Port, heard.Name, heard.Players);
                else found.AddressRank = rank;
                found.Host = heard;
                found.SeenMilliseconds = now;
                _discoveredChanged = true;
                return;
            }

            if (_discovered.Count >= MaximumDiscoveredHosts) return;
            _discovered.Add(new Discovered { Instance = host, Host = heard, AddressRank = rank, SeenMilliseconds = now });
            _discoveredChanged = true;
        }

        /// <summary>Asks again on time, and forgets games that have stopped answering.</summary>
        private void UpdateDiscovery()
        {
            long now = _clock.ElapsedMilliseconds;
            if (_discoverPort != 0 && now >= _nextDiscoveryMilliseconds) AskForGames();
            for (int i = _discovered.Count - 1; i >= 0; i--)
            {
                if (now - _discovered[i].SeenMilliseconds <= DiscoveryExpiryMilliseconds) continue;
                _discovered.RemoveAt(i);
                _discoveredChanged = true;
            }
        }

        private static string Shorten(string text, int length) => text.Length <= length ? text : text.Substring(0, length);

        // ── Send / broadcast ──────────────────────────────────────────────────────

        public void Send(int peerId, int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered)
        {
            if (peerId == 0) { Broadcast(tag, payload, delivery); return; }
            if (peerId == -1 && !IsHost)
                foreach (var candidate in _peersById.Values) { peerId = candidate.Id; break; }
            if (!_peersById.TryGetValue(peerId, out var entry)) return;
            if (entry.RelayId != 0) { SendToPlayer(entry, tag, payload, delivery); return; }
            if (entry.Peer == null) return;
            var packet = BuildPacket(tag, payload);
            entry.Peer.Send(packet, MapDelivery(delivery));
        }

        public void Broadcast(int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered)
        {
            if (!IsHost || _manager == null) return;
            if (_relay != null) { BroadcastOnline(tag, payload, delivery); return; }
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
            if (_discoverPort != 0 || _discovered.Count > 0) UpdateDiscovery();
            if (_links.Count > 0) UpdateDirect();

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
            if (RelayConnected(peer) || DirectConnected(peer)) return;
            int id = _nextPeerId++;
            var entry = new PeerEntry { Id = id, Peer = peer, Address = peer?.ToString() ?? "unknown" };
            _peersById[id] = entry;
            _peersByLiteId[peer.Id] = entry;
            _connectionEvents.Enqueue((new GPeer(id, entry.Address), true));
            // For a client, a successful peer connection means we're now connected.
            if (!IsHost)
            {
                IsConnected = true;
                _state = LinkState.Connected;
                _lastError = "";
            }
        }

        void INetEventListener.OnPeerDisconnected(LiteNetPeer peer, DisconnectInfo info)
        {
            if (RelayDisconnected(peer, info) || DirectDisconnected(peer)) return;
            if (peer != null && _peersByLiteId.TryGetValue(peer.Id, out var entry))
            {
                _peersById.Remove(entry.Id);
                _peersByLiteId.Remove(peer.Id);
                _connectionEvents.Enqueue((new GPeer(entry.Id, entry.Address), false));
            }
            // If we were a client and the host drops, mark disconnected.
            if (!IsHost)
            {
                IsConnected = false;
                bool wasConnected = _state == LinkState.Connected;
                string why = DescribeDisconnect(info);
                if (why == null) _state = LinkState.Idle;
                else
                {
                    _state = wasConnected ? LinkState.Disconnected : LinkState.Failed;
                    _lastError = why;
                }
            }
        }

        void INetEventListener.OnNetworkReceive(LiteNetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            if (RelayReceive(peer, reader) || DirectReceive(peer, reader)) return;
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

        void INetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, LSocketError socketError)
        {
            // Kept for the reason given if joining fails: "nothing listens there" reads better than a time-out.
            if (_state == LinkState.Connecting) _targetSocketError = socketError;
        }
        void INetEventListener.OnNetworkReceiveUnconnected(System.Net.IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            if (endPoint == null || reader == null || reader.AvailableBytes > 2048) return;
            try
            {
                string kind = reader.GetString(32);
                if (string.Equals(kind, DiscoveryRequest, StringComparison.Ordinal)) AnswerDiscovery(endPoint, reader);
                else if (string.Equals(kind, DiscoveryAnswer, StringComparison.Ordinal)) HearAnswer(endPoint, reader);
                else if (string.Equals(kind, PunchKind, StringComparison.Ordinal) && _links.Count > 0) HearPunch(endPoint, reader);
            }
            catch { /* not a request or answer of ours, or cut short — ignore */ }
        }
        void INetEventListener.OnNetworkLatencyUpdate(LiteNetPeer peer, int latency) { }
        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            // The machine joining is told why it was turned away; an older engine ignores the reason.
            if (!IsHost) { request.Reject(RefusalFor("not hosting")); return; }
            string key;
            try { key = request.Data.GetString(128); }
            catch { key = null; }
            // A player met through the relay, connecting on the direct path the two have opened.
            if (key != null && DirectRequest(request, key)) return;
            if (!string.Equals(key, AppKey, StringComparison.Ordinal)) request.Reject(RefusalFor("wrong version"));
            else if (_peersById.Count >= _maximumPeers) request.Reject(RefusalFor("full"));
            else request.Accept();
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}

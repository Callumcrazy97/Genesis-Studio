using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Genesis.Net
{
    /// <summary>
    /// The frames a relay and the games that use it exchange (tags below zero; a game's own tags are
    /// zero or above).
    /// </summary>
    internal static class RelayProtocol
    {
        public const int HostRequest = -1001;   // game → relay: (int max players) — make a session
        public const int Hosted = -1002;        // relay → host: (string code)
        public const int JoinRequest = -1003;   // game → relay: (string code)
        public const int Joined = -1004;        // relay → joiner: (int host id)
        public const int Refused = -1005;       // relay → game: (string reason)
        public const int PeerJoined = -1006;    // relay → host: (int peer id)
        public const int PeerLeft = -1007;      // relay → session: (int peer id)
        public const int Forward = -1010;       // game → relay: (int target id, 0 = everyone else; int tag; int length; bytes)
        public const int Forwarded = -1011;     // relay → game: (int source id; int tag; int length; bytes)
        public const int CodeLength = 6;
        // Letters and digits that cannot be mistaken for one another when read out.
        public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    }

    /// <summary>
    /// A relay for games played over the internet: a host asks it for a session and gets a short code;
    /// players join with the code; everything they send each other goes through the relay, so it works
    /// behind any router or firewall that lets the game reach the relay. Run one on a machine others can
    /// reach (the Player's <c>--relay port</c>); a game connects with the same application key as its own.
    /// </summary>
    public sealed class GenesisRelayServer : INetEventListener, IDisposable
    {
        private sealed class Session
        {
            public string Code;
            public string Key;
            public NetPeer Host;
            public int MaximumPlayers;
            public readonly List<NetPeer> Members = [];
        }

        private readonly NetManager _manager;
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Session> _sessionOf = [];
        private readonly Dictionary<int, string> _keyOf = [];
        private readonly NetDataWriter _writer = new();
        private readonly Random _random = new();

        /// <summary>Sessions open now, and the games connected.</summary>
        public int SessionCount => _sessions.Count;
        public int ConnectionCount => _manager.ConnectedPeersCount;
        public int Port => _manager.LocalPort;

        public GenesisRelayServer()
        {
            _manager = new NetManager(this) { AutoRecycle = true, UnsyncedEvents = false, UpdateTime = 15 };
        }

        /// <summary>Starts listening; <paramref name="localOnly"/> listens on this machine alone (tests).</summary>
        public bool Start(int port, bool localOnly = false) => localOnly
            ? _manager.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, port)
            : _manager.Start(port);

        /// <summary>Handles what arrived; call often (the relay's own loop, or a test's).</summary>
        public void Update() => _manager.PollEvents();

        public void Dispose() => _manager.Stop();

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            string key;
            try { key = request.Data.GetString(128); }
            catch { key = null; }
            if (string.IsNullOrWhiteSpace(key)) { request.Reject(); return; }
            NetPeer peer = request.Accept();
            if (peer != null) _keyOf[peer.Id] = key;
        }

        void INetEventListener.OnPeerConnected(NetPeer peer) { }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            _keyOf.Remove(peer.Id);
            if (!_sessionOf.Remove(peer.Id, out Session session)) return;
            if (session.Host == peer)
            {
                // The host is gone: so is the session.
                foreach (NetPeer member in session.Members)
                {
                    _sessionOf.Remove(member.Id);
                    Control(member, RelayProtocol.PeerLeft, w => w.Put(PublicId(peer)));
                }
                _sessions.Remove(session.Code);
                return;
            }
            session.Members.Remove(peer);
            Control(session.Host, RelayProtocol.PeerLeft, w => w.Put(PublicId(peer)));
            foreach (NetPeer member in session.Members) Control(member, RelayProtocol.PeerLeft, w => w.Put(PublicId(peer)));
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            try
            {
                if (reader.AvailableBytes < 8) return;
                int tag = reader.GetInt();
                int length = reader.GetInt();
                if (length < 0 || length != reader.AvailableBytes || length > LiteNetGameNetwork.MaximumPayloadBytes + 12) return;
                switch (tag)
                {
                    case RelayProtocol.HostRequest:
                        HostSession(peer, Math.Clamp(reader.GetInt(), 2, 256));
                        break;
                    case RelayProtocol.JoinRequest:
                        JoinSession(peer, reader.GetString(16));
                        break;
                    case RelayProtocol.Forward:
                        ForwardFrom(peer, reader, delivery);
                        break;
                }
            }
            catch { /* a malformed frame is dropped */ }
        }

        private void HostSession(NetPeer host, int maximumPlayers)
        {
            if (_sessionOf.ContainsKey(host.Id)) return;
            string code;
            do
            {
                var letters = new char[RelayProtocol.CodeLength];
                for (int i = 0; i < letters.Length; i++) letters[i] = RelayProtocol.CodeAlphabet[_random.Next(RelayProtocol.CodeAlphabet.Length)];
                code = new string(letters);
            }
            while (_sessions.ContainsKey(code));
            var session = new Session { Code = code, Key = _keyOf.GetValueOrDefault(host.Id) ?? string.Empty, Host = host, MaximumPlayers = maximumPlayers };
            _sessions[code] = session;
            _sessionOf[host.Id] = session;
            Control(host, RelayProtocol.Hosted, w => w.Put(code));
        }

        private void JoinSession(NetPeer peer, string code)
        {
            if (_sessionOf.ContainsKey(peer.Id)) return;
            if (string.IsNullOrWhiteSpace(code) || !_sessions.TryGetValue(code.Trim(), out Session session))
            {
                Control(peer, RelayProtocol.Refused, w => w.Put("no such game"));
                return;
            }
            if (!string.Equals(session.Key, _keyOf.GetValueOrDefault(peer.Id), StringComparison.Ordinal))
            {
                Control(peer, RelayProtocol.Refused, w => w.Put("wrong version"));
                return;
            }
            if (session.Members.Count + 1 >= session.MaximumPlayers)
            {
                Control(peer, RelayProtocol.Refused, w => w.Put("full"));
                return;
            }
            session.Members.Add(peer);
            _sessionOf[peer.Id] = session;
            Control(peer, RelayProtocol.Joined, w => w.Put(PublicId(session.Host)));
            Control(session.Host, RelayProtocol.PeerJoined, w => w.Put(PublicId(peer)));
        }

        // A game's frame to another member of its session (0: every other member).
        private void ForwardFrom(NetPeer source, NetPacketReader reader, DeliveryMethod delivery)
        {
            if (!_sessionOf.TryGetValue(source.Id, out Session session)) return;
            int target = reader.GetInt();
            int tag = reader.GetInt();
            int length = reader.GetInt();
            if (length < 0 || length != reader.AvailableBytes) return;
            byte[] payload = new byte[length];
            reader.GetBytes(payload, length);
            _writer.Reset();
            _writer.Put(RelayProtocol.Forwarded);
            _writer.Put(length + 12);
            _writer.Put(PublicId(source));
            _writer.Put(tag);
            _writer.Put(length);
            if (length > 0) _writer.Put(payload);
            if (target != 0)
            {
                NetPeer to = PublicId(session.Host) == target ? session.Host : session.Members.Find(member => PublicId(member) == target);
                to?.Send(_writer, delivery);
                return;
            }
            if (session.Host != source) session.Host.Send(_writer, delivery);
            foreach (NetPeer member in session.Members)
                if (member != source) member.Send(_writer, delivery);
        }

        // The id games know a peer by: LiteNetLib's from 0, so here from 1 (0 means everyone).
        private static int PublicId(NetPeer peer) => peer.Id + 1;

        private void Control(NetPeer peer, int tag, Action<NetDataWriter> write)
        {
            var body = new NetDataWriter();
            write(body);
            _writer.Reset();
            _writer.Put(tag);
            _writer.Put(body.Length);
            _writer.Put(body.Data, 0, body.Length);
            peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }

        void INetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
        void INetEventListener.OnNetworkReceiveUnconnected(System.Net.IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }
}

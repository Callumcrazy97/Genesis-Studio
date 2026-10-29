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
        // Direct paths (29 October 2026). A relay or a game from before them ignores these frames,
        // so either side being older only means every message stays on the relay.
        public const int DirectOffer = -1020;   // game → relay: (bool willing) — may be put in touch with the others directly
        public const int Introduce = -1021;     // relay → game: (int peer id; string address; int port; long token) — that peer's UDP address as the relay sees it
        public const int IntroduceAgain = -1022; // host → relay: (int peer id) — introduce that player and the host again, for another try
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
    /// <remarks>
    /// Two members of a session that both offer to (<see cref="RelayProtocol.DirectOffer"/>) are told
    /// each other's UDP address as the relay sees it, with a shared secret, so they can try to reach
    /// each other directly (NAT punch-through); a game that does not offer is never given away.
    /// The relay keeps the session either way, and carries whatever does not go direct.
    /// </remarks>
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
        // Games that offered to be put in touch directly, and how often each member has been introduced.
        private readonly HashSet<int> _offersDirect = [];
        private readonly Dictionary<int, int> _introductions = [];
        /// <summary>The most times one player and its host are introduced: a host asking without end is ignored.</summary>
        private const int MaximumIntroductions = 32;

        /// <summary>Sessions open now, and the games connected.</summary>
        public int SessionCount => _sessions.Count;
        public int ConnectionCount => _manager.ConnectedPeersCount;
        public int Port => _manager.LocalPort;

        /// <summary>Game frames passed from one member of a session to the others since the relay started.</summary>
        public long FramesForwarded { get; private set; }

        /// <summary>Pairs of members told each other's address so they can try a direct path.</summary>
        public int IntroductionsSent { get; private set; }

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
            _offersDirect.Remove(peer.Id);
            _introductions.Remove(peer.Id);
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
                if (length < 0 || length != reader.AvailableBytes || length > LiteNetGameNetwork.MaximumPayloadBytes + 12 + LiteNetGameNetwork.StreamHeaderBytes) return;
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
                    case RelayProtocol.DirectOffer:
                        OfferDirect(peer, reader.GetBool());
                        break;
                    case RelayProtocol.IntroduceAgain:
                        IntroduceAgain(peer, reader.GetInt());
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
            Introduce(session, peer);
        }

        // ── Direct paths: members who both offer are told each other's address ──

        private void OfferDirect(NetPeer peer, bool willing)
        {
            if (!willing)
            {
                _offersDirect.Remove(peer.Id);
                return;
            }
            if (!_offersDirect.Add(peer.Id) || !_sessionOf.TryGetValue(peer.Id, out Session session)) return;
            // Players talk to their host, so the pairs are the host and each player.
            if (session.Host == peer)
                foreach (NetPeer member in session.Members) Introduce(session, member);
            else Introduce(session, peer);
        }

        private void IntroduceAgain(NetPeer host, int memberId)
        {
            if (!_sessionOf.TryGetValue(host.Id, out Session session) || session.Host != host) return;
            NetPeer member = session.Members.Find(candidate => PublicId(candidate) == memberId);
            if (member != null) Introduce(session, member);
        }

        /// <summary>
        /// Tells a session's host and one of its players where the other is, as the relay sees them
        /// (the address their router gives this relay), with a secret only the two of them know.
        /// </summary>
        private void Introduce(Session session, NetPeer member)
        {
            NetPeer host = session.Host;
            if (!_offersDirect.Contains(host.Id) || !_offersDirect.Contains(member.Id)) return;
            int count = _introductions.GetValueOrDefault(member.Id);
            if (count >= MaximumIntroductions) return;
            _introductions[member.Id] = count + 1;
            // The secret proves to each of the two that a packet or a connection is the other's.
            long token = BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) & long.MaxValue | 1;
            Control(host, RelayProtocol.Introduce, w => WriteIntroduction(w, member, token));
            Control(member, RelayProtocol.Introduce, w => WriteIntroduction(w, host, token));
            IntroductionsSent++;
        }

        private static void WriteIntroduction(NetDataWriter writer, NetPeer about, long token)
        {
            System.Net.IPAddress address = about.Address.IsIPv4MappedToIPv6 ? about.Address.MapToIPv4() : about.Address;
            writer.Put(PublicId(about));
            writer.Put(address.ToString());
            writer.Put(about.Port);
            writer.Put(token);
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
            FramesForwarded++;
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

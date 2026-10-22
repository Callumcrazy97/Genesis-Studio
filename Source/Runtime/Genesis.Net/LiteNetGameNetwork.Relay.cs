using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetPeer = LiteNetLib.NetPeer;
using GPeer = Genesis.Shared.Net.NetPeer;
using Genesis.Shared.Net;

namespace Genesis.Net
{
    // Games over the internet through a relay (GenesisRelayServer). The host asks the relay for a
    // session and is given a short code; players join with the code. This machine keeps one
    // connection, to the relay; the other players are peers like any other (their messages, sends
    // and broadcasts are the same), carried through the relay, so a game works behind any router
    // that lets it reach the relay.
    public sealed partial class LiteNetGameNetwork
    {
        private LiteNetPeer _relay;
        private bool _relayHosting;
        private string _relayJoinCode;
        private int _relayMaximumPlayers;
        // Players' ids at the relay → their ids here.
        private readonly Dictionary<int, int> _relayToLocal = [];
        private readonly NetDataWriter _relayWriter = new();

        /// <summary>The code players join an online game with, once the relay has given it; empty otherwise.</summary>
        public string SessionCode { get; private set; } = string.Empty;

        /// <summary>Whether this machine plays through a relay.</summary>
        public bool IsOnline => _relay != null;

        /// <summary>
        /// Hosts a game through the relay at <paramref name="relayAddress"/>: <see cref="SessionCode"/>
        /// gives the code to share once it arrives (a moment later), and players who join with it
        /// connect as peers do.
        /// </summary>
        public void HostOnline(string relayAddress, int relayPort, int maximumPlayers = 8)
        {
            ConnectToRelay(relayAddress, relayPort);
            _relayHosting = true;
            _relayMaximumPlayers = Math.Clamp(maximumPlayers, 2, 256);
            IsHost = true;
        }

        /// <summary>Joins the game hosted through the relay with this code; connected once the relay has put this machine in it.</summary>
        public void JoinOnline(string relayAddress, int relayPort, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A game code is required.", nameof(code));
            ConnectToRelay(relayAddress, relayPort);
            _relayJoinCode = code.Trim().ToUpperInvariant();
            IsHost = false;
        }

        private void ConnectToRelay(string relayAddress, int relayPort)
        {
            Disconnect();
            if (relayPort < 1 || relayPort > 65535) throw new ArgumentOutOfRangeException(nameof(relayPort));
            if (string.IsNullOrWhiteSpace(relayAddress)) throw new ArgumentException("The relay's address is required.", nameof(relayAddress));
            bool started = LocalOnly
                ? _manager.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, 0)
                : _manager.Start();
            if (!started)
            {
                Fail("unreachable");
                throw new InvalidOperationException("Could not start the UDP client.");
            }
            _relay = _manager.Connect(relayAddress, relayPort, AppKey);
            if (_relay == null) { Fail("bad address"); return; }
            Port = relayPort;
            _nextPeerId = _firstPeerId;
            _state = LinkState.Connecting;
        }

        private void ForgetRelay()
        {
            _relay = null;
            _relayHosting = false;
            _relayJoinCode = null;
            _relayToLocal.Clear();
            SessionCode = string.Empty;
        }

        // The relay answered: ask for a session, or to join one.
        private bool RelayConnected(LiteNetPeer peer)
        {
            if (_relay == null || peer != _relay) return false;
            if (_relayHosting) RelayControl(RelayProtocol.HostRequest, writer => writer.Put(_relayMaximumPlayers));
            else RelayControl(RelayProtocol.JoinRequest, writer => writer.Put(_relayJoinCode ?? string.Empty));
            return true;
        }

        private bool RelayDisconnected(LiteNetPeer peer, DisconnectInfo info)
        {
            if (_relay == null || peer != _relay) return false;
            // Losing the relay is losing everyone.
            foreach (PeerEntry entry in _peersById.Values) _connectionEvents.Enqueue((new GPeer(entry.Id, entry.Address), false));
            _peersById.Clear();
            _relayToLocal.Clear();
            bool wasPlaying = IsConnected;
            IsConnected = false;
            // A refusal (no such game, full) already said why.
            if (_state != LinkState.Failed || _lastError.Length == 0)
            {
                string why = DescribeDisconnect(info);
                _state = wasPlaying ? LinkState.Disconnected : LinkState.Failed;
                _lastError = why ?? (wasPlaying ? "relay closed" : "relay unreachable");
            }
            _relay = null;
            return true;
        }

        // What the relay sends: its answers, players arriving and leaving, and their messages.
        private bool RelayReceive(LiteNetPeer peer, NetPacketReader reader)
        {
            if (_relay == null || peer != _relay) return false;
            try
            {
                if (reader.AvailableBytes < 8) return true;
                int tag = reader.GetInt();
                int length = reader.GetInt();
                if (length < 0 || length != reader.AvailableBytes) return true;
                switch (tag)
                {
                    case RelayProtocol.Hosted:
                        SessionCode = reader.GetString(16);
                        IsConnected = true;
                        _state = LinkState.Hosting;
                        break;
                    case RelayProtocol.Joined:
                        AddRelayPeer(reader.GetInt(), "host");
                        IsConnected = true;
                        _state = LinkState.Connected;
                        _lastError = "";
                        break;
                    case RelayProtocol.Refused:
                        _lastError = reader.GetString(64);
                        _state = LinkState.Failed;
                        _manager.DisconnectAll();
                        break;
                    case RelayProtocol.PeerJoined:
                        AddRelayPeer(reader.GetInt(), "player");
                        break;
                    case RelayProtocol.PeerLeft:
                        if (_relayToLocal.Remove(reader.GetInt(), out int gone) && _peersById.Remove(gone, out PeerEntry left))
                            _connectionEvents.Enqueue((new GPeer(left.Id, left.Address), false));
                        break;
                    case RelayProtocol.Forwarded:
                    {
                        int source = reader.GetInt();
                        int messageTag = reader.GetInt();
                        int size = reader.GetInt();
                        if (size < 0 || size != reader.AvailableBytes || size > MaximumPayloadBytes || _inbox.Count >= MaximumQueuedMessages
                            || _queuedPayloadBytes + size > MaximumQueuedPayloadBytes) break;
                        byte[] payload = new byte[size];
                        reader.GetBytes(payload, size);
                        int from = _relayToLocal.TryGetValue(source, out int known) ? known : AddRelayPeer(source, "player");
                        _inbox.Enqueue(new NetMessage(messageTag, new GPeer(from, _peersById[from].Address), payload));
                        _queuedPayloadBytes += size;
                        break;
                    }
                }
            }
            catch { /* a malformed frame is dropped */ }
            return true;
        }

        private int AddRelayPeer(int relayId, string what)
        {
            if (_relayToLocal.TryGetValue(relayId, out int existing)) return existing;
            int id = _nextPeerId++;
            var entry = new PeerEntry { Id = id, Peer = null, RelayId = relayId, Address = $"{what} {relayId} via relay" };
            _peersById[id] = entry;
            _relayToLocal[relayId] = id;
            _connectionEvents.Enqueue((new GPeer(id, entry.Address), true));
            return id;
        }

        private void SendThroughRelay(int relayTarget, int tag, byte[] payload, NetDelivery delivery)
        {
            if (_relay == null) return;
            int length = payload?.Length ?? 0;
            if (length > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload), "Network payload is too large.");
            _relayWriter.Reset();
            _relayWriter.Put(RelayProtocol.Forward);
            _relayWriter.Put(length + 12);
            _relayWriter.Put(relayTarget);
            _relayWriter.Put(tag);
            _relayWriter.Put(length);
            if (length > 0) _relayWriter.Put(payload);
            _relay.Send(_relayWriter, MapDelivery(delivery));
        }

        private void RelayControl(int tag, Action<NetDataWriter> write)
        {
            var body = new NetDataWriter();
            write(body);
            _relayWriter.Reset();
            _relayWriter.Put(tag);
            _relayWriter.Put(body.Length);
            _relayWriter.Put(body.Data, 0, body.Length);
            _relay.Send(_relayWriter, DeliveryMethod.ReliableOrdered);
        }
    }
}

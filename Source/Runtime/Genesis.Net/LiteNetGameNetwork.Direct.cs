using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetPeer = LiteNetLib.NetPeer;
using LConnectionState = LiteNetLib.ConnectionState;
using GPeer = Genesis.Shared.Net.NetPeer;
using Genesis.Shared.Net;

namespace Genesis.Net
{
    // Direct paths between players who met through a relay (NAT punch-through, 29 October 2026).
    //
    // The relay tells a host and a player each other's UDP address as it sees them, with a secret
    // they share. Both send small packets to that address from the socket they reach the relay on,
    // ten a second for a bounded time: each opens its own router for the other's packets. Once each
    // has heard the other say it was heard, the player connects to the host there, and the game's
    // traffic between the two moves to that connection. A router that gives every destination a new
    // port (a symmetric NAT) never lets the packets meet, and the try runs out: everything stays on
    // the relay, as it always did.
    //
    // Nothing is lost or reordered by a switch. From the first switch on, every reliable message to
    // that player is numbered, whichever path it takes, and the receiver hands them on in number
    // order and drops any it already had. Number 0 goes through the relay behind everything sent
    // there before, so a message sent directly cannot overtake one still on its way through the
    // relay. Numbered messages sent directly are kept until the transport says they arrived; when the
    // direct path is lost they are sent again through the relay. Unreliable messages take whichever
    // path is open and promise nothing, as before.
    public sealed partial class LiteNetGameNetwork : IGameNetworkDirect, IDeliveryEventListener
    {
        private enum PathState { Relay, Punching, Connecting, Direct }

        /// <summary>A reliable message numbered for the stream to one player, kept until the transport says it arrived.</summary>
        private sealed class Numbered
        {
            public DirectLink Link;
            public int Number;
            /// <summary>The stream frame's body: number, tag, length and payload.</summary>
            public byte[] Body;
        }

        /// <summary>This machine's way to one player met through the relay (the host, for a player).</summary>
        private sealed class DirectLink
        {
            public int LocalId, RelayId;
            public PathState State;
            /// <summary>Where the packets go: the address the relay saw, then wherever the other's packets come from.</summary>
            public IPEndPoint Target;
            public long Token;
            /// <summary>A packet from the other has arrived; the other says it has heard ours.</summary>
            public bool Heard, Confirmed;
            public long DeadlineMilliseconds, NextPunchMilliseconds, RetryAtMilliseconds;
            public int Tries;
            /// <summary>The direct connection, made or being made.</summary>
            public LiteNetPeer Peer;
            // Sending: reliable messages are numbered from the first switch to a direct path on.
            public bool Numbering;
            public int NextNumber;
            public readonly Queue<Numbered> Unacknowledged = new();
            // Receiving: the number to hand on next, and those that arrived ahead of it.
            public int NextExpected;
            public readonly SortedDictionary<int, (int Tag, byte[] Payload)> Early = new();
        }

        private const string PunchKind = "genesis-punch", DirectKind = "genesis-direct";
        /// <summary>A numbered message (int number; int tag; int length; bytes), on either path; 0 starts the stream.</summary>
        internal const int StreamTag = -1201;
        private const int StreamStartTag = -1202;
        /// <summary>What numbering adds to a message.</summary>
        internal const int StreamHeaderBytes = 12;
        private const long PunchIntervalMilliseconds = 100, RetryPauseMilliseconds = 10000;
        private const int MaximumEarly = 4096;

        // By the player's id here.
        private readonly Dictionary<int, DirectLink> _links = [];
        // Every direct connection made or being made, until the transport says it closed; null once given up.
        private readonly Dictionary<LiteNetPeer, DirectLink> _directPeers = new(ReferenceEqualityComparer.Instance);
        private readonly NetDataWriter _directWriter = new();
        private bool _directEnabled = true;
        private int _directAttempts = 3;
        private double _directTimeoutSeconds = 5;

        // ── Settings and where things stand ──────────────────────────────────────

        public bool DirectEnabled
        {
            get => _directEnabled;
            set
            {
                if (_directEnabled == value) return;
                _directEnabled = value;
                foreach (DirectLink link in _links.Values)
                {
                    link.Tries = 0;
                    link.RetryAtMilliseconds = 0;
                    if (!value && link.State != PathState.Relay) Abandon(link, retry: false);
                }
                // The relay introduces only games that offer, so turning this off also stops this machine's address being given out.
                if (_relay is { ConnectionState: LConnectionState.Connected }) RelayControl(RelayProtocol.DirectOffer, writer => writer.Put(value));
            }
        }

        public int DirectAttempts
        {
            get => _directAttempts;
            set => _directAttempts = Math.Clamp(value, 1, 10);
        }

        public double DirectTimeoutSeconds
        {
            get => _directTimeoutSeconds;
            set { if (double.IsFinite(value)) _directTimeoutSeconds = Math.Clamp(value, 0.5, 60); }
        }

        public string DirectActive
        {
            get
            {
                if (_relay == null || !IsConnected) return "";
                foreach (PeerEntry entry in _peersById.Values)
                    if (entry.RelayId != 0 && (!_links.TryGetValue(entry.Id, out DirectLink link) || link.State != PathState.Direct)) return "relay";
                return DirectCount > 0 ? "direct" : "relay";
            }
        }

        public int DirectCount
        {
            get
            {
                int count = 0;
                foreach (DirectLink link in _links.Values) if (link.State == PathState.Direct) count++;
                return count;
            }
        }

        public string PathTo(int peerId)
        {
            if (peerId == -1 && !IsHost)
                foreach (PeerEntry candidate in _peersById.Values) { peerId = candidate.Id; break; }
            if (_relay == null || !_peersById.TryGetValue(peerId, out PeerEntry entry) || entry.RelayId == 0) return "";
            return _links.TryGetValue(peerId, out DirectLink link) && link.State == PathState.Direct ? "direct" : "relay";
        }

        private DirectLink LinkFor(int localId, int relayId)
        {
            if (!_links.TryGetValue(localId, out DirectLink link))
                _links[localId] = link = new DirectLink { LocalId = localId, RelayId = relayId };
            return link;
        }

        // ── Meeting: the relay's introduction, and the packets both send ─────────

        /// <summary>The relay connected: this machine offers to be put in touch directly, unless the game said not to.</summary>
        private void OfferDirect()
        {
            if (_directEnabled) RelayControl(RelayProtocol.DirectOffer, writer => writer.Put(true));
        }

        /// <summary>The relay says where a player (or the host) is: start a try, if one is left.</summary>
        private void Introduced(int relayId, string address, int port, long token)
        {
            if (!_directEnabled || token == 0 || port < 1 || port > 65535 || !IPAddress.TryParse(address, out IPAddress ip)
                || !_relayToLocal.TryGetValue(relayId, out int localId)) return;
            DirectLink link = LinkFor(localId, relayId);
            if (link.State == PathState.Direct || link.Tries >= _directAttempts) return;
            if (link.State != PathState.Relay) Abandon(link, retry: false);
            long now = _clock.ElapsedMilliseconds;
            link.Tries++;
            link.Target = new IPEndPoint(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, port);
            link.Token = token;
            link.Heard = link.Confirmed = false;
            link.State = PathState.Punching;
            link.DeadlineMilliseconds = now + (long)(_directTimeoutSeconds * 1000);
            link.NextPunchMilliseconds = now;
            link.RetryAtMilliseconds = 0;
        }

        /// <summary>Keeps tries going: packets on time, a try that runs out given up, and the host's next try asked for.</summary>
        private void UpdateDirect()
        {
            long now = _clock.ElapsedMilliseconds;
            foreach (DirectLink link in _links.Values)
            {
                switch (link.State)
                {
                    case PathState.Punching:
                    case PathState.Connecting:
                        if (now >= link.DeadlineMilliseconds)
                        {
                            Abandon(link, retry: true);
                            break;
                        }
                        if (now >= link.NextPunchMilliseconds) Punch(link, now);
                        if (link.State == PathState.Punching && link.Heard && link.Confirmed) OpenDirect(link);
                        break;
                    case PathState.Direct:
                        // The transport pings every second; a direct path silent for half its time-out
                        // is given up now, so messages wait less before they go round by the relay.
                        if (link.Peer != null && link.Peer.TimeSinceLastPacket > _manager.DisconnectTimeout / 2f) Abandon(link, retry: true);
                        break;
                    case PathState.Relay:
                        if (link.RetryAtMilliseconds == 0 || now < link.RetryAtMilliseconds) break;
                        link.RetryAtMilliseconds = 0;
                        // The host asks; the relay introduces both again with a new secret.
                        if (IsHost && _directEnabled && _relay is { ConnectionState: LConnectionState.Connected })
                            RelayControl(RelayProtocol.IntroduceAgain, writer => writer.Put(link.RelayId));
                        break;
                }
            }
        }

        private void Punch(DirectLink link, long now)
        {
            link.NextPunchMilliseconds = now + PunchIntervalMilliseconds;
            _directWriter.Reset();
            _directWriter.Put(PunchKind);
            _directWriter.Put(AppKey);
            _directWriter.Put(link.Token);
            _directWriter.Put(link.Heard);
            try
            {
                _manager.SendUnconnectedMessage(_directWriter, link.Target);
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or InvalidOperationException or ObjectDisposedException)
            {
                // No network to send on just now: the next packet tries again.
            }
        }

        /// <summary>A packet from a player (or the host) this machine is trying to reach.</summary>
        private void HearPunch(IPEndPoint from, NetDataReader reader)
        {
            string appKey = reader.GetString(128);
            long token = reader.GetLong();
            bool heardUs = reader.GetBool();
            if (!string.Equals(appKey, AppKey, StringComparison.Ordinal) || token == 0) return;
            foreach (DirectLink link in _links.Values)
            {
                if (link.Token != token || link.State is not (PathState.Punching or PathState.Connecting)) continue;
                bool first = !link.Heard;
                link.Heard = true;
                if (heardUs) link.Confirmed = true;
                // Where its packets come from is where it can be reached: a router may have given this
                // path another port than the one the relay saw.
                if (link.State == PathState.Punching)
                {
                    IPAddress address = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
                    if (!address.Equals(link.Target.Address) || from.Port != link.Target.Port) link.Target = new IPEndPoint(address, from.Port);
                }
                // Answer at once, so the other side knows it was heard.
                if (first) Punch(link, _clock.ElapsedMilliseconds);
                return;
            }
        }

        /// <summary>Packets pass both ways: the player connects to the host there; the host waits for it.</summary>
        private void OpenDirect(DirectLink link)
        {
            link.State = PathState.Connecting;
            if (IsHost) return;
            _directWriter.Reset();
            _directWriter.Put(AppKey);
            _directWriter.Put(DirectKind);
            _directWriter.Put(link.Token);
            LiteNetPeer peer = _manager.Connect(link.Target, _directWriter);
            if (peer == null) return;
            link.Peer = peer;
            _directPeers[peer] = link;
        }

        /// <summary>A connection request that carries a try's secret: the player this host is trying to reach.</summary>
        private bool DirectRequest(ConnectionRequest request, string key)
        {
            if (request.Data.AvailableBytes == 0) return false;
            string kind;
            long token;
            try
            {
                kind = request.Data.GetString(32);
                if (!string.Equals(kind, DirectKind, StringComparison.Ordinal)) return false;
                token = request.Data.GetLong();
            }
            catch
            {
                return false;
            }

            DirectLink found = null;
            if (IsHost && string.Equals(key, AppKey, StringComparison.Ordinal) && token != 0)
                foreach (DirectLink link in _links.Values)
                    if (link.Token == token && link.State is PathState.Punching or PathState.Connecting) { found = link; break; }
            if (found == null)
            {
                request.Reject();
                return true;
            }

            LiteNetPeer peer = request.Accept();
            if (peer == null) return true;
            if (found.Peer != null && found.Peer != peer)
            {
                _directPeers[found.Peer] = null;
                found.Peer.Disconnect();
            }
            found.Peer = peer;
            found.State = PathState.Connecting;
            _directPeers[peer] = found;
            return true;
        }

        // ── The direct connection's life ─────────────────────────────────────────

        private bool DirectConnected(LiteNetPeer peer)
        {
            if (peer == null || !_directPeers.TryGetValue(peer, out DirectLink link)) return false;
            if (link == null || link.Peer != peer)
            {
                // A try given up before its connection was made.
                _directPeers[peer] = null;
                peer.Disconnect();
                return true;
            }

            link.State = PathState.Direct;
            link.RetryAtMilliseconds = 0;
            if (!link.Numbering)
            {
                // Number 0 goes through the relay, behind every message sent there before.
                link.Numbering = true;
                link.NextNumber = 1;
                SendStreamThroughRelay(link, NumberedBody(0, StreamStartTag, null));
            }
            return true;
        }

        private bool DirectDisconnected(LiteNetPeer peer)
        {
            if (peer == null || !_directPeers.Remove(peer, out DirectLink link)) return false;
            if (link != null && link.Peer == peer)
            {
                link.Peer = null;
                Abandon(link, retry: true);
            }
            return true;
        }

        /// <summary>
        /// Gives up a direct path, made or being made: back to the relay, and what went along it and
        /// was not delivered goes again through the relay, in order (the other side drops any it had).
        /// </summary>
        private void Abandon(DirectLink link, bool retry)
        {
            if (link.Peer != null)
            {
                _directPeers[link.Peer] = null;
                link.Peer.Disconnect();
                link.Peer = null;
            }
            link.State = PathState.Relay;
            foreach (Numbered numbered in link.Unacknowledged) SendStreamThroughRelay(link, numbered.Body);
            link.Unacknowledged.Clear();
            link.RetryAtMilliseconds = retry && IsHost && _directEnabled && link.Tries < _directAttempts
                ? _clock.ElapsedMilliseconds + RetryPauseMilliseconds : 0;
        }

        /// <summary>A player left the session: its direct path goes with it.</summary>
        private void ForgetLink(int localId)
        {
            if (!_links.Remove(localId, out DirectLink link) || link.Peer == null) return;
            _directPeers[link.Peer] = null;
            link.Peer.Disconnect();
        }

        /// <summary>The session is over; <paramref name="close"/> closes the direct connections still open.</summary>
        private void ForgetDirect(bool close)
        {
            foreach (DirectLink link in _links.Values)
            {
                if (link.Peer == null) continue;
                if (close)
                {
                    _directPeers[link.Peer] = null;
                    link.Peer.Disconnect();
                }
            }
            _links.Clear();
            if (!close) _directPeers.Clear();
        }

        void IDeliveryEventListener.OnMessageDelivered(LiteNetPeer peer, object userData)
        {
            // Reliable ordered messages arrive in order, so this one arriving means every earlier one has.
            if (userData is not Numbered numbered) return;
            Queue<Numbered> waiting = numbered.Link.Unacknowledged;
            while (waiting.Count > 0 && waiting.Peek().Number <= numbered.Number) waiting.Dequeue();
        }

        // ── Sending to a player met through the relay ────────────────────────────

        private void SendToPlayer(PeerEntry entry, int tag, byte[] payload, NetDelivery delivery)
        {
            if (!_links.TryGetValue(entry.Id, out DirectLink link) || !link.Numbering)
            {
                SendThroughRelay(entry.RelayId, tag, payload, delivery);
                return;
            }
            int length = payload?.Length ?? 0;
            if (length > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload), "Network payload is too large.");
            bool direct = link.State == PathState.Direct && link.Peer != null;
            if (delivery == NetDelivery.Unreliable)
            {
                if (direct) link.Peer.Send(BuildPacket(tag, payload), DeliveryMethod.Unreliable);
                else SendThroughRelay(entry.RelayId, tag, payload, delivery);
                return;
            }

            // Reliable, ordered or not: numbered, so a switch of path neither loses nor repeats it
            // (an unordered message is delivered in order too, which it may be).
            int number = link.NextNumber++;
            var numbered = new Numbered { Link = link, Number = number, Body = NumberedBody(number, tag, payload) };
            if (!direct)
            {
                SendStreamThroughRelay(link, numbered.Body);
                return;
            }
            link.Unacknowledged.Enqueue(numbered);
            NetDataWriter packet = StreamPacket(numbered.Body);
            link.Peer.SendWithDeliveryEvent(packet.Data, 0, packet.Length, 0, DeliveryMethod.ReliableOrdered, numbered);
        }

        /// <summary>The host's message to every player: one frame for the relay to pass on, or one per player once any is numbered.</summary>
        private void BroadcastOnline(int tag, byte[] payload, NetDelivery delivery)
        {
            bool numbering = false;
            foreach (DirectLink link in _links.Values) numbering |= link.Numbering;
            if (!numbering)
            {
                SendThroughRelay(0, tag, payload, delivery);
                return;
            }
            foreach (PeerEntry entry in _peersById.Values)
                if (entry.RelayId != 0) SendToPlayer(entry, tag, payload, delivery);
        }

        private byte[] NumberedBody(int number, int tag, byte[] payload)
        {
            int length = payload?.Length ?? 0;
            _directWriter.Reset();
            _directWriter.Put(number);
            _directWriter.Put(tag);
            _directWriter.Put(length);
            if (length > 0) _directWriter.Put(payload);
            return _directWriter.CopyData();
        }

        private NetDataWriter StreamPacket(byte[] body)
        {
            _writer.Reset();
            _writer.Put(StreamTag);
            _writer.Put(body.Length);
            _writer.Put(body);
            return _writer;
        }

        private void SendStreamThroughRelay(DirectLink link, byte[] body) =>
            SendThroughRelay(link.RelayId, StreamTag, body, NetDelivery.ReliableOrdered);

        // ── Receiving from a player met through the relay ────────────────────────

        private bool DirectReceive(LiteNetPeer peer, NetPacketReader reader)
        {
            if (peer == null || !_directPeers.TryGetValue(peer, out DirectLink link)) return false;
            // A path given up: anything numbered on it comes again through the relay.
            if (link == null || link.Peer != peer) return true;
            try
            {
                if (reader.AvailableBytes < 8) return true;
                int tag = reader.GetInt();
                int length = reader.GetInt();
                if (length < 0 || length != reader.AvailableBytes) return true;
                if (tag == StreamTag)
                {
                    ReceiveNumbered(link, reader);
                    return true;
                }
                if (length > MaximumPayloadBytes) return true;
                byte[] payload = new byte[length];
                reader.GetBytes(payload, length);
                HandOn(link, tag, payload);
            }
            catch { /* malformed packet — ignore */ }
            return true;
        }

        /// <summary>A numbered message from either path: handed on in number order, once.</summary>
        private void ReceiveNumbered(DirectLink link, NetDataReader reader)
        {
            if (reader.AvailableBytes < StreamHeaderBytes) return;
            int number = reader.GetInt();
            int tag = reader.GetInt();
            int length = reader.GetInt();
            if (length < 0 || length > MaximumPayloadBytes || length != reader.AvailableBytes) return;
            // Already had: sent again through the relay after the direct path was lost.
            if (number < link.NextExpected || link.Early.ContainsKey(number)) return;
            byte[] payload = new byte[length];
            reader.GetBytes(payload, length);
            if (number != link.NextExpected)
            {
                if (link.Early.Count < MaximumEarly) link.Early[number] = (tag, payload);
                return;
            }
            HandOn(link, tag, payload);
            link.NextExpected++;
            while (link.Early.Remove(link.NextExpected, out (int Tag, byte[] Payload) next))
            {
                HandOn(link, next.Tag, next.Payload);
                link.NextExpected++;
            }
        }

        private void HandOn(DirectLink link, int tag, byte[] payload)
        {
            if (tag == StreamStartTag || _inbox.Count >= MaximumQueuedMessages || _queuedPayloadBytes + payload.Length > MaximumQueuedPayloadBytes) return;
            string address = _peersById.TryGetValue(link.LocalId, out PeerEntry entry) ? entry.Address : "player";
            _inbox.Enqueue(new NetMessage(tag, new GPeer(link.LocalId, address), payload));
            _queuedPayloadBytes += payload.Length;
        }
    }
}

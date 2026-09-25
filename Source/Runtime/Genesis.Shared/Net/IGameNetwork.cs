using System;
using System.Collections.Generic;

namespace Genesis.Shared.Net
{
    // ════════════════════════════════════════════════════════════════════════════
    //   IGameNetwork — backend-agnostic multiplayer networking service.
    //
    //   The runtime host (ProjectPlayerApp) constructs the LiteNetLib-backed
    //   implementation and exposes it via the Engine.Net.* commands. Gameplay code
    //   (C# behaviours or PGSL) talks only to this interface, so the transport can
    //   be swapped without touching game code.
    //
    //   Model: server-authoritative host + connecting clients, reliable UDP, with a
    //   simple tagged-message API. The acceptance test is a 2D game where one
    //   machine hosts (IP:port) and another joins over LAN/Internet.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Delivery guarantees for a sent message.</summary>
    public enum NetDelivery
    {
        /// <summary>Fire-and-forget UDP — fastest, may drop/duplicate/reorder.</summary>
        Unreliable,
        /// <summary>Arrives at most once, no ordering guarantee.</summary>
        ReliableUnordered,
        /// <summary>Arrives exactly once, in send order per channel.</summary>
        ReliableOrdered,
    }

    /// <summary>A connected remote endpoint (a hosted client or the joined host).</summary>
    public readonly struct NetPeer
    {
        public readonly int Id;
        public readonly string Address;   // "ip:port" for display
        public NetPeer(int id, string address) { Id = id; Address = address; }
        public static readonly NetPeer Host = new(-1, "host");
        public bool IsValid => Id != 0;
    }

    /// <summary>An incoming network message, handed to <see cref="IGameNetwork.OnMessageReceived"/>.</summary>
    public readonly struct NetMessage
    {
        /// <summary>The tag identifying this message type (matches <c>[Networked(tag)]</c> or Engine.Net.Send's first arg).</summary>
        public readonly int Tag;
        /// <summary>The peer that sent this message, identified by the transport-assigned positive ID.</summary>
        public readonly NetPeer From;
        /// <summary>The raw payload bytes (read with a NetReader).</summary>
        public readonly byte[] Payload;

        public NetMessage(int tag, NetPeer from, byte[] payload)
        {
            Tag = tag; From = from; Payload = payload;
        }
    }

    /// <summary>
    /// Backend-agnostic multiplayer networking service. All gameplay code talks to
    /// this interface; the concrete LiteNetLib implementation is in Genesis.Net.
    /// </summary>
    public interface IGameNetwork : IDisposable
    {
        /// <summary>True when this process is the host (server).</summary>
        bool IsHost { get; }
        /// <summary>True when connected (host is always "connected" once started).</summary>
        bool IsConnected { get; }
        /// <summary>The port currently hosted on, or the port joined to. 0 when inactive.</summary>
        int Port { get; }
        /// <summary>Number of connected peers (clients connected to host, or 1 for a joined client).</summary>
        int PeerCount { get; }

        /// <summary>Begin hosting on <paramref name="port"/>. Subsequent connections are accepted.</summary>
        void Host(int port);

        /// <summary>Connect to a host at <paramref name="ip"/>:<paramref name="port"/>.</summary>
        void Connect(string ip, int port);

        /// <summary>Disconnect and stop hosting/connecting.</summary>
        void Disconnect();

        /// <summary>Send a tagged message to one peer (Id 0 = broadcast to all when host).</summary>
        void Send(int peerId, int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered);

        /// <summary>Broadcast a tagged message to all connected peers (host only).</summary>
        void Broadcast(int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered);

        /// <summary>Enumerate currently-connected peers.</summary>
        IReadOnlyList<NetPeer> Peers { get; }

        /// <summary>Raised during Update on the owning game thread. Call Update and all transport methods from that thread.</summary>
        event Action<NetMessage> OnMessageReceived;
        /// <summary>Raised when a peer connects (Id, Address).</summary>
        event Action<NetPeer> OnPeerConnected;
        /// <summary>Raised when a peer disconnects.</summary>
        event Action<NetPeer> OnPeerDisconnected;

        /// <summary>Pump the network: poll for incoming packets and fire events. Call once per frame on the main thread.</summary>
        void Update();
    }

    /// <summary>
    /// No-op implementation for sandboxes/tests/offline. Engine.Net.* calls run
    /// without crashing; Host/Connect report as no-ops.
    /// </summary>
    public sealed class NullGameNetwork : IGameNetwork
    {
        public static readonly NullGameNetwork Instance = new NullGameNetwork();
        private NullGameNetwork() { }

        public bool IsHost => false;
        public bool IsConnected => false;
        public int Port => 0;
        public int PeerCount => 0;
        public IReadOnlyList<NetPeer> Peers => Array.Empty<NetPeer>();

        public void Host(int port) { }
        public void Connect(string ip, int port) { }
        public void Disconnect() { }
        public void Send(int peerId, int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered) { }
        public void Broadcast(int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered) { }

        public event Action<NetMessage> OnMessageReceived { add { } remove { } }
        public event Action<NetPeer> OnPeerConnected { add { } remove { } }
        public event Action<NetPeer> OnPeerDisconnected { add { } remove { } }

        public void Update() { }
        public void Dispose() { }
    }

    /// <summary>
    /// Simple little-endian reader for message payloads. Pair with <see cref="NetWriter"/>.
    /// Keeps gameplay code allocation-light and transport-agnostic.
    /// </summary>
    public struct NetReader
    {
        private readonly byte[] _buf;
        private int _pos;
        public NetReader(byte[] buf) { _buf = buf; _pos = 0; }
        public int Remaining => _buf?.Length - _pos ?? 0;

        public int ReadInt() { int v = BitConverter.ToInt32(_buf, _pos); _pos += 4; return v; }
        public float ReadFloat() { float v = BitConverter.ToSingle(_buf, _pos); _pos += 4; return v; }
        public bool ReadBool() { bool v = _buf[_pos] != 0; _pos++; return v; }
        public double ReadDouble() { double v = BitConverter.ToDouble(_buf, _pos); _pos += 8; return v; }
    }

    /// <summary>Simple little-endian writer for message payloads.</summary>
    public sealed class NetWriter
    {
        private readonly System.IO.MemoryStream _ms = new();
        private readonly System.IO.BinaryWriter _w;
        public NetWriter() { _w = new System.IO.BinaryWriter(_ms, System.Text.Encoding.UTF8, leaveOpen: false); }
        public void WriteInt(int v) => _w.Write(v);
        public void WriteFloat(float v) => _w.Write(v);
        public void WriteBool(bool v) => _w.Write(v);
        public void WriteDouble(double v) => _w.Write(v);
        public byte[] ToArray() => _ms.ToArray();
    }
}

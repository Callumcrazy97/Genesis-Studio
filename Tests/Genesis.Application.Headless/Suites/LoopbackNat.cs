using System.Net;
using System.Net.Sockets;
using LiteNetLib.Layers;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A stand-in for a home router (NAT) on this computer, for the direct-path cases. Give a game's
/// network <see cref="Layer"/>: every packet it sends then leaves through this router, from the
/// router's own "public" socket, and a packet from outside is let in only as that kind of router
/// would. Nothing leaves 127.0.0.1.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Full cone: one public port for every destination; once the game has sent anything, anyone may send in.</item>
/// <item>Restricted: as full cone, but only from addresses the game has sent to (on this one-address machine, the same as full cone).</item>
/// <item>Port restricted: only from the exact address and port the game has sent to.</item>
/// <item>Symmetric: a new public port for each destination, and only that destination may send in on it.</item>
/// </list>
/// The game's packets reach the router's inside socket with their real destination appended, and
/// what the router lets in reaches the game with its real source appended; the layer swaps those
/// for the addresses, so the game sees what it would see behind a real router.
/// Call <see cref="Pump"/> often (each step of a test).
/// </remarks>
internal sealed class LoopbackNat : IDisposable
{
    public enum Kind { FullCone, Restricted, PortRestricted, Symmetric }

    private const int TrailerBytes = 6;
    // Windows reports a packet that found nothing listening as an error on the next receive; a router does not care.
    private const int SioUdpConnectionReset = -1744830452;

    private readonly Kind _kind;
    private readonly Socket _inside;
    private readonly IPEndPoint _insideEndPoint;
    private IPEndPoint? _game;
    private readonly Socket? _outside;
    private readonly Dictionary<IPEndPoint, Socket> _outsideFor = new();
    private readonly HashSet<IPEndPoint> _sentTo = new();
    private readonly HashSet<IPAddress> _sentToAddress = new();
    private readonly byte[] _buffer = new byte[70000];

    /// <summary>Give this to the game's network (LiteNetGameNetwork's constructor).</summary>
    public PacketLayerBase Layer { get; }

    /// <summary>While set, the router passes packets to and from this address only (the relay): every other path is cut.</summary>
    public IPEndPoint? OnlyTo { get; set; }

    /// <summary>Packets from outside the router turned away.</summary>
    public int Refused { get; private set; }

    /// <summary>The router's one public address (the cone kinds); null for a symmetric router, which has one per destination.</summary>
    public IPEndPoint? PublicEndPoint => (IPEndPoint?)_outside?.LocalEndPoint;

    public LoopbackNat(Kind kind)
    {
        _kind = kind;
        _inside = Open();
        _insideEndPoint = (IPEndPoint)_inside.LocalEndPoint!;
        if (kind != Kind.Symmetric) _outside = Open();
        Layer = new RouterLayer(_insideEndPoint);
    }

    private static Socket Open()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.IOControl(SioUdpConnectionReset, new byte[] { 0, 0, 0, 0 }, null);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    /// <summary>Passes on whatever has arrived, both ways.</summary>
    public void Pump()
    {
        // The game's packets, on their way out.
        while (TryReceive(_inside, out int length, out IPEndPoint from))
        {
            if (length < TrailerBytes) continue;
            _game = from;
            length -= TrailerBytes;
            IPEndPoint to = ReadTrailer(_buffer, length);
            if (OnlyTo != null && !to.Equals(OnlyTo)) continue;
            _sentTo.Add(to);
            _sentToAddress.Add(to.Address);
            Socket outside = _outside ?? OutsideFor(to);
            try { outside.SendTo(_buffer, 0, length, SocketFlags.None, to); }
            catch (SocketException) { /* nothing there: a router sends and forgets */ }
        }

        // Packets from outside, on their way in.
        if (_outside != null) LetIn(_outside, null);
        foreach ((IPEndPoint destination, Socket socket) in _outsideFor) LetIn(socket, destination);
    }

    private Socket OutsideFor(IPEndPoint destination)
    {
        if (!_outsideFor.TryGetValue(destination, out Socket? socket)) _outsideFor[destination] = socket = Open();
        return socket;
    }

    private void LetIn(Socket socket, IPEndPoint? onlyFrom)
    {
        while (TryReceive(socket, out int length, out IPEndPoint from))
        {
            bool allowed = _kind switch
            {
                Kind.FullCone => _sentTo.Count > 0,
                Kind.Restricted => _sentToAddress.Contains(from.Address),
                Kind.PortRestricted => _sentTo.Contains(from),
                _ => onlyFrom != null && onlyFrom.Equals(from),
            };
            if (OnlyTo != null && !from.Equals(OnlyTo)) allowed = false;
            if (!allowed || _game == null || length + TrailerBytes > _buffer.Length)
            {
                Refused++;
                continue;
            }
            WriteTrailer(_buffer, length, from);
            try { _inside.SendTo(_buffer, 0, length + TrailerBytes, SocketFlags.None, _game); }
            catch (SocketException) { /* the game has gone */ }
        }
    }

    private bool TryReceive(Socket socket, out int length, out IPEndPoint from)
    {
        length = 0;
        from = null!;
        while (socket.Available > 0)
        {
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                length = socket.ReceiveFrom(_buffer, ref any);
            }
            catch (SocketException)
            {
                continue;
            }
            from = (IPEndPoint)any;
            return true;
        }
        return false;
    }

    private static void WriteTrailer(byte[] data, int at, IPEndPoint endPoint)
    {
        IPAddress address = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        byte[] bytes = address.GetAddressBytes();
        Buffer.BlockCopy(bytes, 0, data, at, 4);
        data[at + 4] = (byte)(endPoint.Port >> 8);
        data[at + 5] = (byte)endPoint.Port;
    }

    private static IPEndPoint ReadTrailer(byte[] data, int at) =>
        new(new IPAddress(new ReadOnlySpan<byte>(data, at, 4)), (data[at + 4] << 8) | data[at + 5]);

    public void Dispose()
    {
        _inside.Dispose();
        _outside?.Dispose();
        foreach (Socket socket in _outsideFor.Values) socket.Dispose();
    }

    /// <summary>The game's side: sends everything to the router's inside socket, and takes in only what the router passes on.</summary>
    private sealed class RouterLayer : PacketLayerBase
    {
        private readonly IPEndPoint _router;

        public RouterLayer(IPEndPoint router) : base(TrailerBytes)
        {
            _router = router;
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            if (offset + length + TrailerBytes > data.Length) Array.Resize(ref data, offset + length + TrailerBytes);
            WriteTrailer(data, offset + length, endPoint);
            length += TrailerBytes;
            endPoint = _router;
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length < TrailerBytes || !endPoint.Equals(_router))
            {
                length = 0;
                return;
            }
            length -= TrailerBytes;
            endPoint = ReadTrailer(data, length);
        }
    }
}

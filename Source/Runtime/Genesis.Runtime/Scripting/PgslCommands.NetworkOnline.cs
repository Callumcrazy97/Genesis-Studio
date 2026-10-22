using System;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Games over the internet: through a relay (the Player run with --relay port on a machine the
// players can reach), a host is given a short code and players join with it. Messages, peers and
// replication then work as they do on a local network.
public static partial class PgslCommands
{
    [PgslCommand("NetHostOnline", "NetHostOnline(relayAddress, relayPort, maxPlayers?) -> bool",
        "Host a game over the internet through a relay; NetSessionCode gives the code to share a moment later (NetConnectState says hosting then). False when this network cannot, or the relay's address is wrong", "Networking")]
    public static bool NetHostOnline(string relayAddress, double relayPort, double maxPlayers = 8)
    {
        if (ActiveNetwork == null || !double.IsFinite(relayPort) || !double.IsFinite(maxPlayers)) return false;
        try
        {
            ActiveNetwork.HostOnline(relayAddress, (int)Math.Clamp(relayPort, 1, 65535), (int)Math.Clamp(maxPlayers, 2, 256));
            return true;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException
            or System.Net.Sockets.SocketException or FormatException)
        {
            return false;
        }
    }

    [PgslCommand("NetJoinOnline", "NetJoinOnline(relayAddress, relayPort, code) -> bool",
        "Join a game hosted through a relay by the code its host shares; connected once NetConnectState says connected (NetLastError says why not: no such game, full, wrong version)", "Networking")]
    public static bool NetJoinOnline(string relayAddress, double relayPort, string code)
    {
        if (ActiveNetwork == null || !double.IsFinite(relayPort)) return false;
        try
        {
            ActiveNetwork.JoinOnline(relayAddress, (int)Math.Clamp(relayPort, 1, 65535), code);
            return true;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException
            or System.Net.Sockets.SocketException or FormatException)
        {
            return false;
        }
    }

    [PgslCommand("NetSessionCode", "NetSessionCode() -> string",
        "The code players join this online game with (NetHostOnline); empty until the relay has given it", "Networking")]
    public static string NetSessionCode() => ActiveNetwork?.SessionCode ?? string.Empty;
}

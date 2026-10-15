using System;
using System.Collections.Generic;
using Genesis.Shared.Net;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Before a session: finding a game to join on the local network. The network the Player makes
// (LiteNetLib) can do it; one that cannot (a test's in-memory link) makes these commands report
// nothing rather than fail.
public static partial class PgslCommands
{
    #region Finding games on the local network

    private static IGameNetworkDiscovery Discovery => ActiveNetwork as IGameNetworkDiscovery;

    private static bool TryFoundHost(double index, out NetDiscoveredHost host)
    {
        host = default;
        IReadOnlyList<NetDiscoveredHost> found = Discovery?.DiscoveredHosts;
        if (found == null || !double.IsFinite(index)) return false;
        int at = (int)index;
        if (at < 0 || at >= found.Count) return false;
        host = found[at];
        return true;
    }

    [PgslCommand("NetDiscover", "NetDiscover(port) -> bool",
        "Look for games of this title hosted on this port on the local network and this computer, asking again every second until NetDiscover(0), NetHost or NetConnect. List them with NetFoundCount", "Networking")]
    public static bool NetDiscover(double port)
    {
        IGameNetworkDiscovery discovery = Discovery;
        if (discovery == null || !double.IsFinite(port)) return false;
        try
        {
            return discovery.Discover(port < 1 ? 0 : (int)Math.Min(port, 65535));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [PgslCommand("NetFoundCount", "NetFoundCount() -> number",
        "How many games answered NetDiscover in the last few seconds; a game that stops answering drops off after about four", "Networking")]
    public static double NetFoundCount() => Discovery?.DiscoveredHosts?.Count ?? 0;

    [PgslCommand("NetFoundAddress", "NetFoundAddress(index) -> string",
        "The address of a game NetDiscover found, counting from 0, to pass to NetConnect; empty when there is none", "Networking")]
    public static string NetFoundAddress(double index) => TryFoundHost(index, out NetDiscoveredHost host) ? host.Address ?? "" : "";

    [PgslCommand("NetFoundPort", "NetFoundPort(index) -> number", "The port a game NetDiscover found is hosted on; 0 when there is none", "Networking")]
    public static double NetFoundPort(double index) => TryFoundHost(index, out NetDiscoveredHost host) ? host.Port : 0;

    [PgslCommand("NetFoundName", "NetFoundName(index) -> string",
        "The name a game NetDiscover found advertises: its title, or what its host set with NetHostName", "Networking")]
    public static string NetFoundName(double index) => TryFoundHost(index, out NetDiscoveredHost host) ? host.Name ?? "" : "";

    [PgslCommand("NetFoundPlayers", "NetFoundPlayers(index) -> number",
        "How many players are in a game NetDiscover found, its host included; 0 when there is none", "Networking")]
    public static double NetFoundPlayers(double index) => TryFoundHost(index, out NetDiscoveredHost host) ? host.Players : 0;

    [PgslCommand("NetHostName", "NetHostName(name)",
        "The name this machine advertises to players looking for games while it hosts; an empty name gives back the game's title", "Networking")]
    public static void NetHostName(string name)
    {
        IGameNetworkDiscovery discovery = Discovery;
        if (discovery != null) discovery.AdvertisedName = name ?? "";
    }

    #endregion

    #region Where this machine's session stands

    [PgslCommand("NetConnectState", "NetConnectState() -> string",
        "\"idle\", \"hosting\", \"connecting\", \"connected\", \"failed\" (joining did not succeed) or \"disconnected\" (a joined game ended); NetLastError says why", "Networking")]
    public static string NetConnectState()
    {
        IGameNetwork network = ActiveNetwork;
        if (network is IGameNetworkStatus status) return status.ConnectionState ?? "idle";
        return network == null ? "idle" : network.IsHost ? "hosting" : network.IsConnected ? "connected" : "idle";
    }

    [PgslCommand("NetLastError", "NetLastError() -> string",
        "Why hosting or joining failed or a joined game ended: \"refused\", \"timed out\", \"wrong version\", \"full\", \"host closed\", \"unreachable\", \"bad address\", \"port in use\"; empty while nothing has gone wrong", "Networking")]
    public static string NetLastError() => (ActiveNetwork as IGameNetworkStatus)?.LastError ?? "";

    private static string _localAddresses;
    private static long _localAddressesRead = long.MinValue;

    [PgslCommand("NetLocalAddresses", "NetLocalAddresses() -> string",
        "This computer's addresses on the local network (IPv4), separated by commas, the one most likely to work first: what other players type to join a game hosted here", "Networking")]
    public static string NetLocalAddresses()
    {
        // Listing the adapters takes a moment; a lobby that shows this every frame reads it twice a second.
        long now = Environment.TickCount64;
        if (_localAddresses != null && now - _localAddressesRead < 500) return _localAddresses;
        _localAddresses = ReadLocalAddresses();
        _localAddressesRead = now;
        return _localAddresses;
    }

    private static string ReadLocalAddresses()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || adapter.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                        or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                System.Net.NetworkInformation.IPInterfaceProperties properties = adapter.GetIPProperties();
                // The adapter that leads somewhere (it has a gateway) is the home or office network;
                // the rest are usually virtual machines' and VPNs' own.
                bool gateway = false;
                foreach (System.Net.NetworkInformation.GatewayIPAddressInformation route in properties.GatewayAddresses)
                    if (route.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !route.Address.Equals(System.Net.IPAddress.Any))
                        gateway = true;
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    System.Net.IPAddress address = unicast.Address;
                    if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(address)) continue;
                    byte[] bytes = address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;   // self-assigned: no network answered
                    string text = address.ToString();
                    List<string> into = gateway ? withGateway : others;
                    if (!withGateway.Contains(text) && !others.Contains(text)) into.Add(text);
                }
            }
        }
        catch (Exception exception) when (exception is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        {
            // No adapters to list: this computer can still be joined from itself.
        }

        withGateway.AddRange(others);
        return withGateway.Count > 0 ? string.Join(", ", withGateway) : "127.0.0.1";
    }

    #endregion
}

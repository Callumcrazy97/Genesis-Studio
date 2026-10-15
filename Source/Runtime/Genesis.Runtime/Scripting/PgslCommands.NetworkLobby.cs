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
}

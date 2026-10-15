using System.Collections.Generic;

namespace Genesis.Shared.Net
{
    // ════════════════════════════════════════════════════════════════════════════
    //   What a game needs before it is in a session: finding a game to join on the
    //   local network. A transport that can do this implements the interface beside
    //   IGameNetwork; the script commands ask for it and do nothing without it, so
    //   IGameNetwork itself (and every implementation of it) is unchanged.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>A game on the local network that answered <see cref="IGameNetworkDiscovery.Discover"/>.</summary>
    public readonly struct NetDiscoveredHost
    {
        /// <summary>The host's address as <see cref="IGameNetwork.Connect"/> takes it, such as "192.168.1.20".</summary>
        public readonly string Address;
        /// <summary>The port the game is hosted on.</summary>
        public readonly int Port;
        /// <summary>The name the host advertises: the game's title unless a script named it.</summary>
        public readonly string Name;
        /// <summary>Players in that game, the host included.</summary>
        public readonly int Players;

        public NetDiscoveredHost(string address, int port, string name, int players)
        {
            Address = address; Port = port; Name = name; Players = players;
        }
    }

    /// <summary>Finding games of the same title on the local network.</summary>
    public interface IGameNetworkDiscovery
    {
        /// <summary>The name this machine advertises while it hosts. Empty gives back the game's own title.</summary>
        string AdvertisedName { get; set; }

        /// <summary>
        /// Starts asking the local network, and this computer, for games hosting on <paramref name="port"/>,
        /// and keeps asking about once a second. 0 stops. Hosting or joining stops it too.
        /// </summary>
        /// <returns>False when this machine cannot ask (no socket could be opened).</returns>
        bool Discover(int port);

        /// <summary>The games that answered within the last few seconds, in the order they first answered.</summary>
        IReadOnlyList<NetDiscoveredHost> DiscoveredHosts { get; }
    }
}

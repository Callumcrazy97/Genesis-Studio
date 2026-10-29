namespace Genesis.Shared.Net
{
    // ════════════════════════════════════════════════════════════════════════════
    //   Direct paths between players who met through a relay (NAT punch-through).
    //   A transport that can do this implements the interface beside IGameNetwork;
    //   the script commands (Engine.Network.Direct) ask for it and report nothing
    //   without it, so IGameNetwork itself is unchanged.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Players who meet through a relay try to reach each other directly; game traffic moves to the
    /// direct path once packets pass both ways, and goes back to the relay when it is lost, with no
    /// message lost or reordered either way.
    /// </summary>
    public interface IGameNetworkDirect
    {
        /// <summary>Whether to try for direct paths (true unless set). False keeps every message on the relay, as before.</summary>
        bool DirectEnabled { get; set; }

        /// <summary>How many tries each player gets: one when they meet, more after a failed try or a lost path (1 to 10).</summary>
        int DirectAttempts { get; set; }

        /// <summary>How long one try sends packets both ways before it gives up, in seconds (0.5 to 60).</summary>
        double DirectTimeoutSeconds { get; set; }

        /// <summary>"direct" when every player is reached directly, "relay" otherwise; empty when not playing through a relay.</summary>
        string DirectActive { get; }

        /// <summary>How many players are reached directly now.</summary>
        int DirectCount { get; }

        /// <summary>The path to one player: "direct" or "relay"; empty for an id that is not a player met through the relay.</summary>
        string PathTo(int peerId);
    }
}

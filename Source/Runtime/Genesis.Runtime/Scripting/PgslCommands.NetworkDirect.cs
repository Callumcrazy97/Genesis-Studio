using System;
using Genesis.Shared.Net;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Engine.Network.Direct: players who meet through a relay (NetHostOnline, NetJoinOnline) try to
// reach each other directly, and their game traffic moves there when packets pass both ways; when
// they cannot (a router that allows no way in) or the direct path is lost, it stays on or goes back
// to the relay, with no message lost or reordered. A network that cannot (a test's in-memory link)
// reports the relay and keeps nothing.
public static partial class PgslCommands
{
    private static IGameNetworkDirect DirectPaths => ActiveNetwork as IGameNetworkDirect;

    [PgslCommand("Enabled", "Engine.Network.Direct.Enabled = false", "Whether players who meet through a relay try to reach each other directly, so their messages skip the relay's distance (true unless set); false keeps every message on the relay and never gives this machine's address to the others", "Network", Namespace = "Engine.Network.Direct", Flat = false)]
    public static bool NetworkDirectEnabled
    {
        get => DirectPaths?.DirectEnabled ?? true;
        set { if (DirectPaths is { } direct) direct.DirectEnabled = value; }
    }

    [PgslCommand("Active", "Engine.Network.Direct.Active", "The path game messages take: \"direct\" when every player is reached directly, \"relay\" when any goes through the relay; \"\" when this game is not playing through a relay (read)", "Network", Namespace = "Engine.Network.Direct", Flat = false)]
    public static string NetworkDirectActive => DirectPaths?.DirectActive ?? "";

    [PgslCommand("Attempts", "Engine.Network.Direct.Attempts = 3", "How many tries players who meet through a relay get to reach each other directly, 1 to 10 (3 unless set): one when they meet, and another ten seconds after a try fails or a direct path is lost", "Network", Namespace = "Engine.Network.Direct", Flat = false)]
    public static double NetworkDirectAttempts
    {
        get => DirectPaths?.DirectAttempts ?? 3;
        set { if (DirectPaths is { } direct && double.IsFinite(value)) direct.DirectAttempts = (int)Math.Clamp(Math.Round(value), 1, 10); }
    }

    [PgslCommand("Timeout", "Engine.Network.Direct.Timeout = 5", "How long one try sends packets both ways before it gives up and the players stay on the relay, 0.5 to 60 seconds (5 unless set)", "Network", Namespace = "Engine.Network.Direct", Flat = false)]
    public static double NetworkDirectTimeout
    {
        get => DirectPaths?.DirectTimeoutSeconds ?? 5;
        set { if (DirectPaths is { } direct && double.IsFinite(value)) direct.DirectTimeoutSeconds = Math.Clamp(value, 0.5, 60); }
    }

    [PgslCommand("Count", "Engine.Network.Direct.Count", "How many players this machine reaches directly now, not through the relay (read)", "Network", Namespace = "Engine.Network.Direct", Flat = false)]
    public static double NetworkDirectCount => DirectPaths?.DirectCount ?? 0;

    [PgslCommand("Path", "Engine.Network.Direct.Path(peerId) -> string", "The path to one player (a player: -1, the host): \"direct\" or \"relay\"; \"\" for an id that is not a player met through a relay", "Network", Namespace = "Engine.Network.Direct", Flat = false)]
    public static string NetworkDirectPath(double peerId) =>
        DirectPaths is { } direct && double.IsFinite(peerId) ? direct.PathTo((int)peerId) ?? "" : "";

    [PgslCommand("NetDirectPath", "NetDirectPath(peerId) -> string",
        "The path to a player met through a relay (a player: -1, the host): \"direct\" or \"relay\"; \"\" for any other id. Engine.Network.Direct has the rest", "Networking")]
    public static string NetDirectPath(double peerId) => NetworkDirectPath(peerId);

    [PgslCommand("NetDirectCount", "NetDirectCount() -> number",
        "How many players met through a relay this machine reaches directly now", "Networking")]
    public static double NetDirectCount() => NetworkDirectCount;
}

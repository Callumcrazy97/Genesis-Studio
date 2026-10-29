using System.Diagnostics;
using System.Net;
using System.Numerics;
using Genesis.Net;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Net;
using Genesis.Shared.ECS;
using Genesis.Shared.Net;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Games over the internet through a relay: on this machine, a relay gives a host a code, two
/// players join with it, messages go both ways and the host's broadcast reaches both, a wrong code
/// is refused with a reason, and a player leaving is noticed. Then direct paths (NAT punch-through):
/// behind stand-in routers on this machine (<see cref="LoopbackNat"/>), a host and a player open a
/// direct path through full-cone and port-restricted routers and stay on the relay behind symmetric
/// ones; ordered messages and shared objects carry on across the switch and back when the direct
/// path is cut; turned off, everything stays on the relay; the Engine.Network.Direct names work.
/// </summary>
internal static class NetworkOnlineSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Online.HostAndJoinByCodeThroughARelay", () =>
        {
            int port = 47000 + Random.Shared.Next(2000);
            using var relay = new GenesisRelayServer();
            HeadlessHarness.Assert(relay.Start(port, localOnly: true), "The relay could not open its port.");
            using var host = new LiteNetGameNetwork { LocalOnly = true };
            using var first = new LiteNetGameNetwork { LocalOnly = true };
            using var second = new LiteNetGameNetwork { LocalOnly = true };
            using var stranger = new LiteNetGameNetwork { LocalOnly = true };
            var hostHeard = new List<NetMessage>();
            var firstHeard = new List<NetMessage>();
            var secondHeard = new List<NetMessage>();
            var hostJoins = new List<int>();
            var hostLeaves = new List<int>();
            host.OnMessageReceived += hostHeard.Add;
            first.OnMessageReceived += firstHeard.Add;
            second.OnMessageReceived += secondHeard.Add;
            host.OnPeerConnected += peer => hostJoins.Add(peer.Id);
            host.OnPeerDisconnected += peer => hostLeaves.Add(peer.Id);

            void Pump(Func<bool> done, string what)
            {
                var clock = Stopwatch.StartNew();
                while (!done() && clock.Elapsed.TotalSeconds < 10)
                {
                    relay.Update(); host.Update(); first.Update(); second.Update(); stranger.Update();
                    Thread.Sleep(5);
                }
                HeadlessHarness.Assert(done(), "Timed out waiting for " + what + ".");
            }

            host.HostOnline("127.0.0.1", port, 4);
            Pump(() => host.SessionCode.Length == 6, "the relay to give the host a code");
            HeadlessHarness.Assert(host.IsHost && host.ConnectionState == "hosting", "The host is not hosting: " + host.ConnectionState);

            first.JoinOnline("127.0.0.1", port, host.SessionCode.ToLowerInvariant());
            second.JoinOnline("127.0.0.1", port, host.SessionCode);
            Pump(() => first.IsConnected && second.IsConnected && hostJoins.Count == 2, "two players to join");

            first.Send(-1, 7, [1, 2, 3]);
            second.Send(-1, 8, [9]);
            Pump(() => hostHeard.Count == 2, "the players' messages to reach the host");
            HeadlessHarness.Assert(hostHeard.Any(m => m.Tag == 7 && m.Payload.SequenceEqual(new byte[] { 1, 2, 3 }))
                && hostHeard.Any(m => m.Tag == 8 && m.Payload.SequenceEqual(new byte[] { 9 })) && hostHeard[0].From.Id != hostHeard[1].From.Id,
                "The host did not hear each player's message from that player.");

            host.Send(hostHeard.First(m => m.Tag == 7).From.Id, 11, [42]);
            host.Broadcast(12, [5, 5]);
            Pump(() => firstHeard.Count == 2 && secondHeard.Count == 1, "the host's messages to reach the players");
            HeadlessHarness.Assert(firstHeard.Any(m => m.Tag == 11 && m.Payload[0] == 42) && firstHeard.Any(m => m.Tag == 12)
                && secondHeard.Single().Tag == 12, "A message to one player, or a broadcast to both, went astray.");

            stranger.JoinOnline("127.0.0.1", port, "ZZZZZZ");
            Pump(() => stranger.ConnectionState == "failed", "a wrong code to be refused");
            HeadlessHarness.Assert(stranger.LastError == "no such game", "A wrong code was refused with: " + stranger.LastError);

            second.Disconnect();
            Pump(() => hostLeaves.Count == 1, "the host to notice a player leaving");
            HeadlessHarness.Assert(relay.SessionCount == 1, "The relay lost the session when a player left.");
        });

        RunDirect(ctx);
    }

    private static void RunDirect(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Direct.FullConeRoutersMoveTrafficToADirectPathAndBackWithNothingLost", () =>
        {
            using var session = new DirectSession(LoopbackNat.Kind.FullCone);
            Entity deer = Place(session.HostWorld, new Vector3(10, 0, 0));
            session.HostReplication.Replicate(deer, "Deer");
            int broadcasts = 0;
            session.Player.OnMessageReceived += message => { if (message.Tag == 23) broadcasts++; };
            session.Meet();

            // The two talk from the moment they meet, through the switch to the direct path.
            session.Until(() => session.Host.DirectActive == "direct" && session.Player.DirectActive == "direct",
                () => "No direct path opened through full-cone routers: " + session.Describe(), 8);
            HeadlessHarness.Assert(session.Relay.IntroductionsSent == 1 && session.Host.DirectCount == 1 && session.Player.DirectCount == 1
                && session.Host.PathTo(session.PlayerId) == "direct" && session.Player.PathTo(-1) == "direct",
                "The paths do not say direct: " + session.Describe());
            session.AssertInOrder("across the switch to the direct path");
            session.Until(() => session.PlayerReplication.CopyCount == 1, () => "The player was not sent the host's deer.");

            // Now messages, a broadcast and the deer's movement skip the relay.
            session.For(0.3);
            long forwarded = session.Relay.FramesForwarded;
            session.HostWorld.GetRef<TransformComponent>(deer).X = 40f;
            session.Host.Broadcast(23, [7]);
            session.Until(() => broadcasts == 1 && session.PlayerSees(40f), () => $"On the direct path the broadcast ({broadcasts}) or the deer's move did not arrive.");
            session.For(0.5);
            session.AssertInOrder("on the direct path");
            HeadlessHarness.Assert(session.Relay.FramesForwarded == forwarded,
                $"{session.Relay.FramesForwarded - forwarded} frames still went through the relay while the direct path was open.");

            // The direct path is cut (the routers now pass only the relay's packets): back to the
            // relay once the transport notices, with the messages sent into the cut path sent again.
            session.HostNat!.OnlyTo = session.RelayEndPoint;
            session.PlayerNat!.OnlyTo = session.RelayEndPoint;
            var noticed = Stopwatch.StartNew();
            session.Until(() => session.Host.DirectActive == "relay" && session.Player.DirectActive == "relay",
                () => "The cut direct path was not given up: " + session.Describe(), 15);
            noticed.Stop();
            // Movement is unreliable: what was sent into the cut path is gone, the next move arrives.
            session.HostWorld.GetRef<TransformComponent>(deer).X = -25f;
            session.Until(() => session.PlayerSees(-25f), () => "Back on the relay the deer's copy did not follow it.");
            session.AssertInOrder("after the direct path was lost");
            HeadlessHarness.Assert(session.Relay.FramesForwarded > forwarded && session.Host.PeerCount == 1 && session.Player.IsConnected,
                "Back on the relay nothing went through it, or the session ended: " + session.Describe());
            HeadlessHarness.Assert(noticed.Elapsed.TotalSeconds < 4.5, $"Losing the direct path took {noticed.Elapsed.TotalSeconds:F1} s to notice; a silent path should be given up in about 2.5 s.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Direct.PortRestrictedRoutersOpenADirectPath", () =>
        {
            using var session = new DirectSession(LoopbackNat.Kind.PortRestricted);
            session.Meet();
            session.Until(() => session.Host.DirectActive == "direct" && session.Player.DirectActive == "direct",
                () => "No direct path opened through port-restricted routers: " + session.Describe(), 8);
            session.AssertInOrder("across the switch through port-restricted routers");
            // The router is really shut to anyone the game has not sent to: a stranger's packet is turned away.
            int refused = session.HostNat!.Refused;
            using (var stranger = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                stranger.Send([1, 2, 3], 3, session.HostNat.PublicEndPoint!);
                session.Until(() => session.HostNat.Refused > refused, () => "The port-restricted router let a stranger's packet in.", 2);
            }
            HeadlessHarness.Assert(session.Host.DirectActive == "direct", "A stranger's packet upset the direct path: " + session.Describe());
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Direct.SymmetricRoutersStayOnTheRelay", () =>
        {
            using var session = new DirectSession(LoopbackNat.Kind.Symmetric, timeout: 1.5);
            session.Meet();
            session.For(2.5);
            HeadlessHarness.Assert(session.Relay.IntroductionsSent == 1 && session.Host.DirectActive == "relay" && session.Player.DirectActive == "relay"
                && session.Host.DirectCount == 0 && session.Host.PathTo(session.PlayerId) == "relay",
                "Behind symmetric routers the players should have tried once and stayed on the relay: " + session.Describe());
            HeadlessHarness.Assert(session.HostNat!.Refused > 0 && session.PlayerNat!.Refused > 0, "The symmetric routers turned none of the other's packets away.");
            long forwarded = session.Relay.FramesForwarded;
            session.For(0.3);
            HeadlessHarness.Assert(session.Relay.FramesForwarded > forwarded, "The messages did not go through the relay.");
            session.AssertInOrder("on the relay behind symmetric routers");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Direct.TurnedOffEverythingStaysOnTheRelay", () =>
        {
            // No routers: on this machine a direct path would open at once, so only the setting keeps it shut.
            using var session = new DirectSession(null);
            session.Player.DirectEnabled = false;
            session.Meet();
            session.For(1.5);
            HeadlessHarness.Assert(session.Relay.IntroductionsSent == 0, "The relay gave out the address of a player that did not offer it.");
            HeadlessHarness.Assert(session.Host.DirectActive == "relay" && session.Player.DirectActive == "relay" && session.Host.PathTo(session.PlayerId) == "relay",
                "With direct paths off a path is not the relay: " + session.Describe());
            session.AssertInOrder("on the relay with direct paths off");
            HeadlessHarness.Assert(session.Relay.FramesForwarded >= session.HostSent + session.PlayerSent,
                $"The relay forwarded {session.Relay.FramesForwarded} frames for {session.HostSent + session.PlayerSent} messages.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Network.Direct.EngineApiReadsAndSetsTheDirectPath", () =>
        {
            using var network = new LiteNetGameNetwork { LocalOnly = true };
            IGameNetwork previous = Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork;
            Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = network;
            try
            {
                var context = new Genesis.Shared.Scripting.PgslContext();
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.EngineSettings.TryRead("Engine.Network.Direct.Enabled", context, out object? enabled) && enabled is true,
                    "Engine.Network.Direct.Enabled does not read true unless set.");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.EngineSettings.TryFind("Engine.Network.Direct.Timeout", out var timeout)
                    && timeout.Scope == Genesis.Runtime.Scripting.EngineSettingScope.World && Genesis.Runtime.Scripting.EngineSettings.DefaultText(timeout) == "5"
                    && Genesis.Runtime.Scripting.EngineSettings.TryRange(timeout, out double low, out double high) && low == 0.5 && high == 60,
                    "Engine.Network.Direct.Timeout is not a world setting the Inspector can offer, 0.5 to 60 with 5 unless set.");
                var problems = new List<string>();
                int applied = Genesis.Runtime.Scripting.EngineSettings.Apply(new Dictionary<string, string>
                {
                    ["Engine.Network.Direct.Timeout"] = "2.5",
                    ["Engine.Network.Direct.Attempts"] = "40",
                    ["Engine.Network.Direct.Enabled"] = "false",
                }, context, problems);
                HeadlessHarness.Assert(applied == 3 && network.DirectTimeoutSeconds == 2.5 && network.DirectAttempts == 10 && !network.DirectEnabled,
                    $"A room's Engine.Network.Direct settings did not reach the network ({applied} set: {string.Join("; ", problems)}).");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetworkDirectActive == "" && Genesis.Runtime.Scripting.PgslCommands.NetDirectPath(1) == ""
                    && Genesis.Runtime.Scripting.PgslCommands.NetDirectCount() == 0,
                    "A game not playing through a relay reports a direct path.");
                // The reference lists the names written with a namespace of their own as well as the ones made from flat commands.
                var rows = Genesis.Runtime.Scripting.PgslApi.Reference().ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
                string[] wanted = ["Engine.Network.Direct.Enabled", "Engine.Network.Direct.Active", "Engine.Network.Direct.Attempts", "Engine.Network.Direct.Timeout",
                    "Engine.Network.Direct.Count", "Engine.Network.Direct.Path", "Engine.Network.DirectPath", "Engine.Network.DirectCount"];
                HeadlessHarness.Assert(wanted.All(rows.ContainsKey), "Missing standard names: " + string.Join(", ", wanted.Where(name => !rows.ContainsKey(name))));
                HeadlessHarness.Assert(rows["Engine.Network.Direct.Enabled"].CanSet && rows["Engine.Network.Direct.Active"].IsProperty && !rows["Engine.Network.Direct.Active"].CanSet
                    && !rows["Engine.Network.Direct.Path"].IsProperty,
                    "Engine.Network.Direct.Enabled should be a value a script sets, Active one it only reads, and Path a command.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = previous;
            }
        });
    }

    private static Entity Place(EcsWorld world, Vector3 position)
    {
        Entity entity = world.CreateEntity();
        world.Set(entity, new TransformComponent { X = position.X, Y = position.Y, Z = position.Z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        return entity;
    }

    /// <summary>
    /// A relay, a host and a player on this machine, each game behind its own stand-in router (or
    /// none), sharing objects, and numbering the messages they send each other to check the order.
    /// </summary>
    private sealed class DirectSession : IDisposable
    {
        public readonly GenesisRelayServer Relay = new();
        public readonly IPEndPoint RelayEndPoint;
        public readonly LoopbackNat? HostNat, PlayerNat;
        public readonly LiteNetGameNetwork Host, Player;
        public readonly EcsWorld HostWorld = new(), PlayerWorld = new();
        public readonly NetworkReplication HostReplication = new(), PlayerReplication = new();
        public int HostSent, PlayerSent, PlayerId;
        public readonly List<int> HostHeard = new(), PlayerHeard = new();

        public DirectSession(LoopbackNat.Kind? routers, double timeout = 2)
        {
            int port = 47000 + Random.Shared.Next(2000);
            HeadlessHarness.Assert(Relay.Start(port, localOnly: true), "The relay could not open its port.");
            RelayEndPoint = new IPEndPoint(IPAddress.Loopback, port);
            if (routers is { } kind)
            {
                HostNat = new LoopbackNat(kind);
                PlayerNat = new LoopbackNat(kind);
            }
            // One try each, so a case does not wait out the pause before another.
            Host = new LiteNetGameNetwork(LiteNetGameNetwork.DefaultApplicationKey, 64, HostNat?.Layer!) { LocalOnly = true, DirectTimeoutSeconds = timeout, DirectAttempts = 1 };
            Player = new LiteNetGameNetwork(LiteNetGameNetwork.DefaultApplicationKey, 64, PlayerNat?.Layer!) { LocalOnly = true, DirectTimeoutSeconds = timeout, DirectAttempts = 1 };
            Host.OnMessageReceived += message => { if (message.Tag == 21) HostHeard.Add(BitConverter.ToInt32(message.Payload)); };
            Player.OnMessageReceived += message => { if (message.Tag == 22) PlayerHeard.Add(BitConverter.ToInt32(message.Payload)); };
            HostReplication.Attach(Host);
            PlayerReplication.Attach(Player);
            PlayerReplication.CreateCopy = (world, prefab, position, rotation, scale) => Place(world, position);
        }

        /// <summary>One step of every part; with <paramref name="chatter"/> each game first sends the other its next numbered message.</summary>
        public void Step(bool chatter)
        {
            if (chatter && PlayerId != 0)
            {
                Player.Send(-1, 21, BitConverter.GetBytes(PlayerSent++));
                Host.Send(PlayerId, 22, BitConverter.GetBytes(HostSent++));
            }
            HostNat?.Pump();
            PlayerNat?.Pump();
            Relay.Update();
            Host.Update();
            HostReplication.Update(HostWorld, Vector3.Zero, 0.02f);
            HostWorld.FlushDeferred();
            Player.Update();
            PlayerReplication.Update(PlayerWorld, Vector3.Zero, 0.02f);
            PlayerWorld.FlushDeferred();
            HostNat?.Pump();
            PlayerNat?.Pump();
            Thread.Sleep(4);
        }

        public void Until(Func<bool> done, Func<string> failure, double seconds = 10, bool chatter = true)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds) Step(chatter);
            HeadlessHarness.Assert(done(), failure());
        }

        public void For(double seconds, bool chatter = true)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds) Step(chatter);
        }

        public void Meet()
        {
            Host.HostOnline("127.0.0.1", RelayEndPoint.Port, 4);
            Until(() => Host.SessionCode.Length == 6, () => "The relay gave the host no code: " + Describe(), chatter: false);
            Player.JoinOnline("127.0.0.1", RelayEndPoint.Port, Host.SessionCode);
            Until(() => Player.IsConnected && Host.PeerCount == 1, () => "The player did not join through the relay: " + Describe(), chatter: false);
            PlayerId = Host.Peers[0].Id;
        }

        public bool PlayerSees(float x)
        {
            bool seen = false;
            PlayerWorld.Query<TransformComponent>((Entity _, ref TransformComponent transform) => seen |= MathF.Abs(transform.X - x) < 0.5f);
            return seen;
        }

        /// <summary>Every numbered message arrived, once each, in the order it was sent.</summary>
        public void AssertInOrder(string when)
        {
            Until(() => HostHeard.Count >= PlayerSent && PlayerHeard.Count >= HostSent,
                () => $"Messages went missing {when}: the host heard {HostHeard.Count} of {PlayerSent}, the player {PlayerHeard.Count} of {HostSent}.", chatter: false);
            For(0.1, chatter: false);
            HeadlessHarness.Assert(HostHeard.SequenceEqual(Enumerable.Range(0, PlayerSent)), $"The host's messages {when} are out of order or repeated: {FirstWrong(HostHeard)}.");
            HeadlessHarness.Assert(PlayerHeard.SequenceEqual(Enumerable.Range(0, HostSent)), $"The player's messages {when} are out of order or repeated: {FirstWrong(PlayerHeard)}.");
        }

        private static string FirstWrong(List<int> heard)
        {
            for (int i = 0; i < heard.Count; i++)
                if (heard[i] != i) return $"message {i} of {heard.Count} was number {heard[i]}";
            return $"{heard.Count} arrived";
        }

        public string Describe() =>
            $"host {Host.ConnectionState} {Host.LastError} path '{Host.DirectActive}' direct {Host.DirectCount}; player {Player.ConnectionState} {Player.LastError} path '{Player.DirectActive}' direct {Player.DirectCount}; "
            + $"introductions {Relay.IntroductionsSent}, forwarded {Relay.FramesForwarded}; refused by routers {HostNat?.Refused ?? 0}/{PlayerNat?.Refused ?? 0}";

        public void Dispose()
        {
            Player.Dispose();
            Host.Dispose();
            Relay.Dispose();
            HostNat?.Dispose();
            PlayerNat?.Dispose();
        }
    }
}

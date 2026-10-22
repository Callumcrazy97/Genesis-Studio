using System.Diagnostics;
using Genesis.Net;
using Genesis.Shared.Net;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Games over the internet through a relay: on this machine, a relay gives a host a code, two
/// players join with it, messages go both ways and the host's broadcast reaches both, a wrong code
/// is refused with a reason, and a player leaving is noticed.
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
    }
}

using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Net;
using Genesis.Shared.ECS;
using Genesis.Shared.Net;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless;

/// <summary>
/// Shared objects between a host and joined players, over an in-memory link so the check needs
/// no sockets: who is sent what, that copies follow, and that leaving cleans up.
/// </summary>
internal static class NetworkReplicationSuite
{
    /// <summary>One end of an in-memory network: a host with any number of players, or a player.</summary>
    private sealed class LoopNetwork : IGameNetwork
    {
        private readonly Queue<NetMessage> _inbox = new();
        private readonly Dictionary<int, LoopNetwork> _links = new();
        private readonly List<NetPeer> _peers = new();
        private LoopNetwork? _host;
        private int _idAtHost;

        public LoopNetwork(bool host) { IsHost = host; IsConnected = host; }

        public bool IsHost { get; }
        public bool IsConnected { get; private set; }
        public int Port => 7777;
        public int PeerCount => _peers.Count;
        public IReadOnlyList<NetPeer> Peers => _peers;
        public int Sent { get; private set; }

        public event Action<NetMessage>? OnMessageReceived;
        public event Action<NetPeer>? OnPeerConnected;
        public event Action<NetPeer>? OnPeerDisconnected;

        public void Join(LoopNetwork host, int id)
        {
            _host = host; _idAtHost = id; IsConnected = true;
            _peers.Add(NetPeer.Host);
            host._links[id] = this;
            host._peers.Add(new NetPeer(id, "loop:" + id));
            host.OnPeerConnected?.Invoke(new NetPeer(id, "loop:" + id));
        }

        public void Leave()
        {
            if (_host == null) return;
            LoopNetwork host = _host;
            host._links.Remove(_idAtHost);
            host._peers.RemoveAll(peer => peer.Id == _idAtHost);
            host.OnPeerDisconnected?.Invoke(new NetPeer(_idAtHost, "loop:" + _idAtHost));
            _host = null; IsConnected = false; _peers.Clear();
            OnPeerDisconnected?.Invoke(NetPeer.Host);
        }

        public void Host(int port) { }
        public void Connect(string ip, int port) { }
        public void Disconnect() => Leave();

        public void Send(int peerId, int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered)
        {
            if (peerId == 0) { Broadcast(tag, payload, delivery); return; }
            Sent++;
            if (IsHost) { if (_links.TryGetValue(peerId, out LoopNetwork? player)) player._inbox.Enqueue(new NetMessage(tag, NetPeer.Host, payload)); }
            else _host?._inbox.Enqueue(new NetMessage(tag, new NetPeer(_idAtHost, "loop:" + _idAtHost), payload));
        }

        public void Broadcast(int tag, byte[] payload, NetDelivery delivery = NetDelivery.ReliableOrdered)
        {
            if (!IsHost) return;
            foreach (LoopNetwork player in _links.Values) { Sent++; player._inbox.Enqueue(new NetMessage(tag, NetPeer.Host, payload)); }
        }

        public void Update()
        {
            while (_inbox.Count > 0) OnMessageReceived?.Invoke(_inbox.Dequeue());
        }

        public void Dispose() { }
    }

    /// <summary>A machine on a real connection: LiteNetLib over UDP, bound to this computer only.</summary>
    private sealed class SocketMachine : IDisposable
    {
        public readonly EcsWorld World = new();
        public readonly Genesis.Net.LiteNetGameNetwork Network = new("genesis-replication-check") { LocalOnly = true };
        public readonly NetworkReplication Replication = new();
        public Vector3 Focus;
        public readonly List<string> CopiesMade = new();

        public SocketMachine()
        {
            Replication.Attach(Network);
            Replication.CreateCopy = (world, prefab, position, rotation, scale) =>
            {
                CopiesMade.Add(prefab);
                return Machine.Place(world, position);
            };
        }

        public void Step()
        {
            Network.Update();
            Replication.Update(World, Focus, 0.06f);
            World.FlushDeferred();
        }

        public List<Vector3> AllPositions()
        {
            var positions = new List<Vector3>();
            World.Query<TransformComponent>((Entity _, ref TransformComponent transform) =>
                positions.Add(new Vector3(transform.X, transform.Y, transform.Z)));
            return positions;
        }

        public void Dispose() => Network.Dispose();
    }

    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static void RunSockets(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Net.Replication.WorksOverARealConnectionOnThisMachine", () =>
        {
            using var host = new SocketMachine();
            using var anna = new SocketMachine();
            using var ben = new SocketMachine();
            SocketMachine[] machines = [host, anna, ben];

            // Real packets take real time: step every machine until something is true, or give up.
            void Until(Func<bool> done, Func<string> failure, double seconds = 8)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && watch.Elapsed.TotalSeconds < seconds)
                {
                    foreach (SocketMachine machine in machines) machine.Step();
                    Thread.Sleep(2);
                }

                HeadlessHarness.Assert(done(), failure());
            }

            void Settle(int steps)
            {
                for (int i = 0; i < steps; i++)
                {
                    foreach (SocketMachine machine in machines) machine.Step();
                    Thread.Sleep(2);
                }
            }

            int port = FreeUdpPort();
            host.Network.Host(port);
            HeadlessHarness.Assert(host.Network.IsHost && host.Network.IsConnected && host.Network.Port == port, "The host did not start.");

            Entity nearDeer = Machine.Place(host.World, new Vector3(10, 0, 0));
            Entity farDeer = Machine.Place(host.World, new Vector3(5000, 0, 0));
            host.World.Set(nearDeer, new ModelAnimatorComponent { ClipName = "Graze", ClipFps = 30f, PlaybackSpeed = 1f, Playing = true, Loop = true });
            host.Replication.Replicate(nearDeer, "Deer");
            host.Replication.Replicate(farDeer, "Deer");

            anna.Network.Connect("127.0.0.1", port);
            ben.Network.Connect("127.0.0.1", port);
            Until(() => anna.Network.IsConnected && ben.Network.IsConnected && host.Network.PeerCount == 2,
                () => $"Two players did not connect to the host on this machine (host has {host.Network.PeerCount} players).");

            // Each player is sent the deer beside it, with the animation it is playing, and not the far one.
            Until(() => anna.Replication.CopyCount == 1 && ben.Replication.CopyCount == 1,
                () => $"The players were not sent the near deer: Anna has {anna.Replication.CopyCount} copies, Ben {ben.Replication.CopyCount}.");
            string playing = "";
            anna.World.Query<ModelAnimatorComponent>((Entity _, ref ModelAnimatorComponent animator) => playing = animator.ClipName);
            HeadlessHarness.Assert(playing == "Graze" && anna.CopiesMade.SequenceEqual(["Deer"]),
                $"Anna's copy should be a grazing deer; it is '{string.Join(",", anna.CopiesMade)}' playing '{playing}'.");

            // The deer walks off: movement arrives as unreliable packets, the new animation reliably.
            ref TransformComponent deer = ref host.World.GetRef<TransformComponent>(nearDeer);
            deer.X = 40f;
            host.World.GetRef<ModelAnimatorComponent>(nearDeer).ClipName = "Walk";
            Until(() => anna.AllPositions().Any(position => MathF.Abs(position.X - 40f) < 0.5f)
                    && ben.AllPositions().Any(position => MathF.Abs(position.X - 40f) < 0.5f),
                () => $"The copies did not follow the deer to x = 40 (Anna sees it at {anna.AllPositions().FirstOrDefault().X:F1}).");
            playing = "";
            ben.World.Query<ModelAnimatorComponent>((Entity _, ref ModelAnimatorComponent animator) => playing = animator.ClipName);
            HeadlessHarness.Assert(playing == "Walk", $"Ben's copy of the walking deer plays '{playing}'.");

            // Anna's own character reaches the host and Ben, and never comes back to her.
            Entity annaSelf = Machine.Place(anna.World, new Vector3(2, 0, 3));
            anna.Replication.Own(annaSelf, "Player");
            Until(() => host.Replication.SharedCount == 3 && ben.Replication.CopyCount == 2,
                () => $"Anna's character did not reach the others: the host shares {host.Replication.SharedCount}, Ben sees {ben.Replication.CopyCount}.");
            anna.World.GetRef<TransformComponent>(annaSelf).X = 30f;
            Until(() => ben.AllPositions().Any(position => MathF.Abs(position.X - 30f) < 0.5f && MathF.Abs(position.Z - 3f) < 0.5f),
                () => "Ben's copy of Anna did not follow her.");
            Settle(20);
            HeadlessHarness.Assert(anna.Replication.CopyCount == 1, $"Anna was sent a copy of herself ({anna.Replication.CopyCount} copies).");

            // Ben travels: the far deer replaces what he could see at the start.
            ben.Focus = new Vector3(5000, 0, 0);
            Until(() => ben.Replication.CopyCount == 1 && MathF.Abs(ben.AllPositions().Single().X - 5000f) < 0.5f,
                () => $"Five kilometres away Ben should see only the far deer; he has {ben.Replication.CopyCount} copies.");
            ben.Focus = Vector3.Zero;
            Until(() => ben.Replication.CopyCount == 2, () => $"Back at the start Ben sees {ben.Replication.CopyCount} copies, not the deer and Anna.");

            // Anna disconnects: the host removes her character and tells Ben.
            anna.Network.Disconnect();
            Until(() => host.Network.PeerCount == 1 && host.Replication.SharedCount == 2 && ben.Replication.CopyCount == 1,
                () => $"After Anna left the host has {host.Network.PeerCount} players and shares {host.Replication.SharedCount}; Ben sees {ben.Replication.CopyCount}.");
            anna.Step();
            HeadlessHarness.Assert(anna.Replication.CopyCount == 0, "Anna kept copies of the host's objects after disconnecting.");

            // The host stops: Ben's copies of its objects go.
            host.Network.Disconnect();
            Until(() => !ben.Network.IsConnected && ben.Replication.CopyCount == 0,
                () => $"Ben still has {ben.Replication.CopyCount} copies after the host stopped (connected: {ben.Network.IsConnected}).", 15);
        });
    }

    private sealed class Machine
    {
        public readonly EcsWorld World = new();
        public readonly LoopNetwork Network;
        public readonly NetworkReplication Replication = new();
        public Vector3 Focus;
        public readonly List<string> CopiesMade = new();

        public Machine(bool host)
        {
            Network = new LoopNetwork(host);
            Replication.Attach(Network);
            Replication.CreateCopy = (world, prefab, position, rotation, scale) =>
            {
                CopiesMade.Add(prefab);
                return Place(world, position);
            };
        }

        public static Entity Place(EcsWorld world, Vector3 position)
        {
            Entity entity = world.CreateEntity();
            world.Set(entity, new TransformComponent { X = position.X, Y = position.Y, Z = position.Z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            return entity;
        }

        public Vector3 PositionOf(Entity entity)
        {
            ref TransformComponent transform = ref World.GetRef<TransformComponent>(entity);
            return new Vector3(transform.X, transform.Y, transform.Z);
        }

        public List<Vector3> AllPositions()
        {
            var positions = new List<Vector3>();
            World.Query<TransformComponent>((Entity _, ref TransformComponent transform) =>
                positions.Add(new Vector3(transform.X, transform.Y, transform.Z)));
            return positions;
        }
    }

    /// <summary>The value a machine reads from its copy of an Object, or null when it has no such copy or value.</summary>
    private static object? Seen(NetworkReplication replication, string prefab, string name)
    {
        for (int i = 0; i < replication.CopyCount; i++)
        {
            Entity copy = replication.CopyAt(i);
            if (replication.ObjectOf(copy) == prefab) return replication.TryGetValue(copy, name, out object value) ? value : null;
        }

        return null;
    }

    private static void RunValues(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Net.Replication.SharedObjectsCarryValuesEveryMachineCanRead", () =>
        {
            var host = new Machine(host: true);
            var anna = new Machine(host: false);
            var ben = new Machine(host: false);
            Machine[] machines = [host, anna, ben];
            void Run(int frames)
            {
                for (int frame = 0; frame < frames; frame++)
                    foreach (Machine machine in machines)
                    {
                        machine.Network.Update();
                        machine.Replication.Update(machine.World, machine.Focus, 0.06f);
                        machine.World.FlushDeferred();
                    }
            }

            // Values set before anyone joins arrive with the object.
            Entity stag = Machine.Place(host.World, new Vector3(10, 0, 0));
            host.Replication.Replicate(stag, "Deer");
            HeadlessHarness.Assert(host.Replication.SetValue(stag, "health", 80.0) && host.Replication.SetValue(stag, "name", "Old Stag"),
                "The host could not give its own shared object a value.");
            HeadlessHarness.Assert(!host.Replication.SetValue(Machine.Place(host.World, Vector3.Zero), "health", 1.0),
                "An object that is not shared accepted a shared value.");
            anna.Network.Join(host.Network, 1);
            ben.Network.Join(host.Network, 2);
            Run(12);
            HeadlessHarness.Assert(Equals(Seen(anna.Replication, "Deer", "health"), 80.0) && Equals(Seen(ben.Replication, "Deer", "name"), "Old Stag"),
                $"The stag's values did not arrive with it: Anna reads health {Seen(anna.Replication, "Deer", "health") ?? "nothing"}, " +
                $"Ben reads name {Seen(ben.Replication, "Deer", "name") ?? "nothing"}.");

            // A change is sent once to each player that has the object, and an unchanged value not at all.
            Run(10);
            int before = host.Network.Sent;
            host.Replication.SetValue(stag, "health", 55.0);
            Run(4);
            HeadlessHarness.Assert(Equals(Seen(anna.Replication, "Deer", "health"), 55.0) && Equals(Seen(ben.Replication, "Deer", "health"), 55.0),
                $"A changed value did not reach the players (Anna reads {Seen(anna.Replication, "Deer", "health") ?? "nothing"}).");
            HeadlessHarness.Assert(host.Network.Sent - before == 2, $"One changed value cost {host.Network.Sent - before} messages for two players; it should cost 2.");
            before = host.Network.Sent;
            HeadlessHarness.Assert(host.Replication.SetValue(stag, "health", 55.0), "Setting a value to what it already is was refused.");
            Run(4);
            HeadlessHarness.Assert(host.Network.Sent == before, "A value set to what it already was sent a message.");

            // Only the machine that controls an object says what its values are.
            Entity annasStag = anna.Replication.CopyAt(0);
            HeadlessHarness.Assert(anna.Replication.IsCopy(annasStag) && anna.Replication.OwnerOf(annasStag) == 0
                && !anna.Replication.SetValue(annasStag, "health", 1.0),
                "A player changed a value on its copy of the host's object.");

            // A player's own character: its values reach the host and the other player.
            Entity annaSelf = Machine.Place(anna.World, new Vector3(2, 0, 3));
            anna.Replication.Own(annaSelf, "Player");
            HeadlessHarness.Assert(anna.Replication.SetValue(annaSelf, "name", "Anna") && anna.Replication.SetValue(annaSelf, "hp", 100.0),
                "A player could not give its own character a value.");
            Run(12);
            HeadlessHarness.Assert(host.Replication.CopyCount == 1 && Equals(Seen(host.Replication, "Player", "name"), "Anna")
                && Equals(Seen(ben.Replication, "Player", "hp"), 100.0),
                $"Anna's values did not reach the others: the host holds {host.Replication.CopyCount} copies and reads name " +
                $"{Seen(host.Replication, "Player", "name") ?? "nothing"}; Ben reads hp {Seen(ben.Replication, "Player", "hp") ?? "nothing"}.");
            Entity annaAtHost = host.Replication.CopyAt(0);
            Entity annaAtBen = Entity.Null;
            for (int i = 0; i < ben.Replication.CopyCount; i++)
                if (ben.Replication.ObjectOf(ben.Replication.CopyAt(i)) == "Player") annaAtBen = ben.Replication.CopyAt(i);
            HeadlessHarness.Assert(host.Replication.OwnerOf(annaAtHost) == 1 && ben.Replication.OwnerOf(annaAtBen) == 1,
                $"Anna's character should be known as player 1's: the host says {host.Replication.OwnerOf(annaAtHost)}, Ben says {ben.Replication.OwnerOf(annaAtBen)}.");
            HeadlessHarness.Assert(!host.Replication.SetValue(annaAtHost, "hp", 1.0), "The host changed a value on a player's own character.");
            anna.Replication.SetValue(annaSelf, "hp", 40.0);
            Run(6);
            HeadlessHarness.Assert(Equals(Seen(host.Replication, "Player", "hp"), 40.0) && Equals(Seen(ben.Replication, "Player", "hp"), 40.0),
                $"Anna's changed value did not reach the others (Ben reads {Seen(ben.Replication, "Player", "hp") ?? "nothing"}).");

            // A copy made again after the player has been away holds what is true now.
            ben.Focus = new Vector3(5000, 0, 0);
            Run(20);
            HeadlessHarness.Assert(ben.Replication.CopyCount == 0, $"Five kilometres away Ben still holds {ben.Replication.CopyCount} copies.");
            host.Replication.SetValue(stag, "health", 30.0);
            Run(4);
            ben.Focus = Vector3.Zero;
            Run(20);
            HeadlessHarness.Assert(Equals(Seen(ben.Replication, "Deer", "health"), 30.0) && Equals(Seen(ben.Replication, "Player", "name"), "Anna"),
                $"Ben came back to stale values: stag health {Seen(ben.Replication, "Deer", "health") ?? "nothing"}, " +
                $"Anna's name {Seen(ben.Replication, "Player", "name") ?? "nothing"}.");

            // Limits: a long name, long text, and more values than an object may carry.
            HeadlessHarness.Assert(!host.Replication.SetValue(stag, new string('n', NetworkReplication.MaxValueName + 1), 1.0)
                && !host.Replication.SetValue(stag, "story", new string('t', NetworkReplication.MaxValueText + 1))
                && !host.Replication.SetValue(stag, "", 1.0),
                "A value past the limits was accepted.");
            int accepted = 0;
            for (int i = 0; i < NetworkReplication.MaxValues + 8; i++)
                if (host.Replication.SetValue(stag, "extra" + i, (double)i)) accepted++;
            HeadlessHarness.Assert(accepted == NetworkReplication.MaxValues - 2,
                $"The stag took {accepted} more values on top of its two; it may carry {NetworkReplication.MaxValues} in all.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Net.Scripts.StartASessionAndSendAndReceiveMessages", () =>
        {
            using var host = new SocketMachine();
            using var anna = new SocketMachine();
            SocketMachine[] machines = [host, anna];
            void Until(Func<bool> done, Func<string> failure, double seconds = 8)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && watch.Elapsed.TotalSeconds < seconds)
                {
                    foreach (SocketMachine machine in machines) machine.Step();
                    Thread.Sleep(2);
                }

                HeadlessHarness.Assert(done(), failure());
            }

            var annaHeard = new List<NetMessage>();
            var hostHeard = new List<NetMessage>();
            anna.Network.OnMessageReceived += message => { if (message.Tag > 0) annaHeard.Add(message); };
            host.Network.OnMessageReceived += message => { if (message.Tag > 0) hostHeard.Add(message); };
            try
            {
                // The host's scripts start the session.
                Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = host.Network;
                int port = FreeUdpPort();
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetAvailable() && Genesis.Runtime.Scripting.PgslCommands.NetHost(port)
                    && Genesis.Runtime.Scripting.PgslCommands.NetIsHost() && Genesis.Runtime.Scripting.PgslCommands.NetPort() == port,
                    "A script could not start hosting.");
                anna.Network.Connect("127.0.0.1", port);
                Until(() => anna.Network.IsConnected && Genesis.Runtime.Scripting.PgslCommands.NetPeerCount() == 1,
                    () => "A player did not connect to the session a script hosted.");
                double annaId = Genesis.Runtime.Scripting.PgslCommands.NetPeerId(0);
                HeadlessHarness.Assert(annaId > 0 && Genesis.Runtime.Scripting.PgslCommands.NetPeerId(1) == 0, $"The host's script sees its player as id {annaId}.");

                // Two messages arrive; a script takes the one it asks for, then whatever is left.
                anna.Network.Send(NetPeer.Host.Id, 7, System.Text.Encoding.UTF8.GetBytes("hello"));
                anna.Network.Send(NetPeer.Host.Id, 8, BitConverter.GetBytes(42.5));
                Until(() => Genesis.Runtime.Scripting.PgslCommands.NetPending() == 2,
                    () => $"The host's scripts were handed {Genesis.Runtime.Scripting.PgslCommands.NetPending()} of the 2 messages sent.");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetReceive(8)
                    && Genesis.Runtime.Scripting.PgslCommands.NetMessageNumber() == 42.5
                    && Genesis.Runtime.Scripting.PgslCommands.NetMessageTag() == 8
                    && Genesis.Runtime.Scripting.PgslCommands.NetMessageFrom() == annaId,
                    $"The number sent with tag 8 read back as {Genesis.Runtime.Scripting.PgslCommands.NetMessageNumber()} " +
                    $"from {Genesis.Runtime.Scripting.PgslCommands.NetMessageFrom()}.");
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.NetReceive(8), "A message was handed to scripts twice.");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetReceive(0)
                    && Genesis.Runtime.Scripting.PgslCommands.NetMessageText() == "hello"
                    && Genesis.Runtime.Scripting.PgslCommands.NetMessageTag() == 7
                    && Genesis.Runtime.Scripting.PgslCommands.NetPending() == 0,
                    $"The text sent with tag 7 read back as '{Genesis.Runtime.Scripting.PgslCommands.NetMessageText()}'.");

                // The script answers the machine that wrote to it.
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetSendText(annaId, 9, "welcome"), "A script could not reply.");
                Until(() => annaHeard.Count == 1, () => "The script's reply did not arrive.");
                HeadlessHarness.Assert(annaHeard[0].Tag == 9 && System.Text.Encoding.UTF8.GetString(annaHeard[0].Payload) == "welcome",
                    "The script's reply arrived changed.");

                // The engine's own traffic (shared objects) is not handed to scripts.
                host.Replication.Replicate(Machine.Place(host.World, new Vector3(5, 0, 0)), "Deer");
                Until(() => anna.Replication.CopyCount == 1, () => "The shared deer did not reach the player.");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetPending() == 0 && hostHeard.Count == 2 && annaHeard.Count == 1,
                    "Sharing an object put the engine's own messages in front of scripts.");

                // A player's scripts: whatever peer they name, their messages go to the host.
                Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = anna.Network;
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.NetIsHost() && Genesis.Runtime.Scripting.PgslCommands.NetIsConnected()
                    && Genesis.Runtime.Scripting.PgslCommands.NetSendNumber(0, 11, 3), "A player's script could not send.");
                Until(() => hostHeard.Count == 3, () => "A player's script sent a message the host never received.");
                HeadlessHarness.Assert(hostHeard[2].Tag == 11 && BitConverter.ToDouble(hostHeard[2].Payload, 0) == 3, "The player's number arrived changed.");
                host.Network.Send((int)annaId, 12, System.Text.Encoding.UTF8.GetBytes("17.25"));
                Until(() => Genesis.Runtime.Scripting.PgslCommands.NetPending() == 1, () => "The player's scripts were not handed the host's message.");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.NetReceive(12)
                    && Genesis.Runtime.Scripting.PgslCommands.NetMessageNumber() == 17.25, "Text that is a number did not read as one.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveNetwork = null;
            }
        });
    }

    public static void Run(HeadlessContext context)
    {
        RunSockets(context);
        RunValues(context);
        HeadlessHarness.RunCase(context.Report, "Engine.Net.Replication.PlayersSeeNearbySharedObjectsAndEachOther", () =>
        {
            var host = new Machine(host: true);
            var anna = new Machine(host: false);
            var ben = new Machine(host: false);
            Machine[] machines = [host, anna, ben];
            void Run(int frames)
            {
                for (int frame = 0; frame < frames; frame++)
                    foreach (Machine machine in machines)
                    {
                        machine.Network.Update();
                        machine.Replication.Update(machine.World, machine.Focus, 0.06f);
                        machine.World.FlushDeferred();
                    }
            }

            // The host's world: a deer near the start and another five kilometres away.
            Entity nearDeer = Machine.Place(host.World, new Vector3(10, 0, 0));
            Entity farDeer = Machine.Place(host.World, new Vector3(5000, 0, 0));
            HeadlessHarness.Assert(host.Replication.Replicate(nearDeer, "Deer") > 0 && host.Replication.Replicate(farDeer, "Deer") > 0
                && host.Replication.Replicate(nearDeer, "Deer") == host.Replication.Replicate(nearDeer, "Deer"),
                "Sharing an object did not give it a stable network id.");

            anna.Network.Join(host.Network, 1);
            ben.Network.Join(host.Network, 2);
            Run(12);
            HeadlessHarness.Assert(anna.Replication.CopyCount == 1 && ben.Replication.CopyCount == 1,
                $"Each player near the start should see only the near deer; Anna has {anna.Replication.CopyCount} copies and Ben {ben.Replication.CopyCount}.");
            HeadlessHarness.Assert(anna.CopiesMade.SequenceEqual(["Deer"]), "The copy was not made from the shared object's own definition.");

            // The deer walks; the copies follow it and play what it plays.
            host.World.Set(nearDeer, new ModelAnimatorComponent { ClipName = "Walk", ClipFps = 30f, PlaybackSpeed = 1f, Playing = true, Loop = true });
            ref TransformComponent deer = ref host.World.GetRef<TransformComponent>(nearDeer);
            deer.X = 40f; deer.RotationY = 90f;
            Run(40);
            Vector3 seen = anna.AllPositions().Single();
            HeadlessHarness.Assert(MathF.Abs(seen.X - 40f) < 0.5f, $"Anna's copy of the deer is at x = {seen.X:F1}; the deer is at 40.");
            string playing = "";
            anna.World.Query<ModelAnimatorComponent>((Entity _, ref ModelAnimatorComponent animator) => playing = animator.ClipName);
            HeadlessHarness.Assert(playing == "Walk", $"Anna's copy of the walking deer plays '{playing}'.");

            // Anna's character is hers: she moves it, the host and Ben see a copy.
            Entity annaSelf = Machine.Place(anna.World, new Vector3(2, 0, 3));
            HeadlessHarness.Assert(anna.Replication.Own(annaSelf, "Player") > 0, "A player could not claim its own character.");
            Run(12);
            HeadlessHarness.Assert(host.Replication.SharedCount == 3 && host.CopiesMade.SequenceEqual(["Player"]),
                $"The host should hold the two deer and a copy of Anna's character; it shares {host.Replication.SharedCount} objects.");
            HeadlessHarness.Assert(ben.Replication.CopyCount == 2 && anna.Replication.CopyCount == 1,
                $"Ben should see the deer and Anna ({ben.Replication.CopyCount} copies); Anna must not be sent a copy of herself ({anna.Replication.CopyCount}).");
            ref TransformComponent self = ref anna.World.GetRef<TransformComponent>(annaSelf);
            self.X = 30f;
            Run(40);
            HeadlessHarness.Assert(ben.AllPositions().Any(position => MathF.Abs(position.X - 30f) < 0.5f && MathF.Abs(position.Z - 3f) < 0.5f),
                "Ben's copy of Anna did not follow her.");

            // She breaks into a run: the host's copy and Ben's copy both run.
            anna.World.Set(annaSelf, new ModelAnimatorComponent { ClipName = "Run", ClipFps = 30f, PlaybackSpeed = 1.5f, Playing = true, Loop = true });
            Run(12);
            var clips = new List<string>();
            ben.World.Query<ModelAnimatorComponent>((Entity _, ref ModelAnimatorComponent animator) => clips.Add(animator.ClipName + "@" + animator.PlaybackSpeed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
            HeadlessHarness.Assert(clips.Contains("Run@1.5") && clips.Contains("Walk@1.0"),
                $"Ben should see Anna running and the deer walking; his copies play {string.Join(", ", clips)}.");

            // A copy removed on one machine while its object lives on comes back.
            Entity lost = Entity.Null;
            ben.World.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
            {
                if (MathF.Abs(transform.X - 40f) < 0.5f) lost = entity;
            });
            ben.World.DestroyEntity(lost);
            ben.World.FlushDeferred();
            Run(30);
            HeadlessHarness.Assert(ben.Replication.CopyCount == 2 && ben.AllPositions().Any(position => MathF.Abs(position.X - 40f) < 0.5f),
                $"Ben lost his copy of the deer and was not sent it again ({ben.Replication.CopyCount} copies).");

            // The same for the host's copy of a player's own character.
            Entity annaAtHost = Entity.Null;
            host.World.Query<TransformComponent>((Entity entity, ref TransformComponent _) =>
            {
                if (!entity.Equals(nearDeer) && !entity.Equals(farDeer)) annaAtHost = entity;
            });
            host.World.DestroyEntity(annaAtHost);
            host.World.FlushDeferred();
            Run(20);
            HeadlessHarness.Assert(host.Replication.SharedCount == 3 && host.CopiesMade.Count == 2 && ben.Replication.CopyCount == 2,
                $"The host lost its copy of Anna and did not get her back: it shares {host.Replication.SharedCount} objects, " +
                $"made {host.CopiesMade.Count} copies, and Ben sees {ben.Replication.CopyCount}.");

            // Ben travels to the far deer: the near things leave his view and the far deer enters it.
            int sentBefore = host.Network.Sent;
            ben.Focus = new Vector3(5000, 0, 0);
            Run(20);
            HeadlessHarness.Assert(ben.Replication.CopyCount == 1 && MathF.Abs(ben.AllPositions().Single().X - 5000f) < 0.5f,
                $"Five kilometres away Ben should see only the far deer; he has {ben.Replication.CopyCount} copies.");

            // Nothing moves: nothing is sent but the players' own positions.
            Run(10);
            int quietStart = host.Network.Sent;
            Run(30);
            HeadlessHarness.Assert(host.Network.Sent == quietStart,
                $"The host sent {host.Network.Sent - quietStart} messages while nothing moved.");
            HeadlessHarness.Assert(host.Network.Sent > sentBefore, "Travelling did not change what Ben was sent.");

            // Anna leaves: her character goes from the host and from Ben's view when he returns.
            ben.Focus = Vector3.Zero;
            Run(20);
            HeadlessHarness.Assert(ben.Replication.CopyCount == 2, $"Back at the start Ben should see the deer and Anna again ({ben.Replication.CopyCount}).");
            anna.Network.Leave();
            Run(20);
            HeadlessHarness.Assert(host.Replication.SharedCount == 2, $"Anna's character was not removed from the host when she left ({host.Replication.SharedCount} shared).");
            HeadlessHarness.Assert(ben.Replication.CopyCount == 1, $"Ben still sees {ben.Replication.CopyCount} copies after Anna left; only the deer should remain.");
            HeadlessHarness.Assert(anna.Replication.CopyCount == 0 && anna.AllPositions().Count == 1,
                "Anna kept copies of the host's objects after leaving.");

            // A removed object disappears everywhere.
            host.World.DestroyEntity(nearDeer);
            host.World.FlushDeferred();
            Run(10);
            HeadlessHarness.Assert(ben.Replication.CopyCount == 0 && ben.AllPositions().Count == 0,
                "Ben still sees a deer the host has removed.");
        });
    }
}

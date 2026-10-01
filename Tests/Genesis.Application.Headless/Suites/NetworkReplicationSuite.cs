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

    public static void Run(HeadlessContext context)
    {
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

using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Net;

namespace Genesis.Runtime.Net
{
    /// <summary>
    /// LAN multiplayer scaffold: transform snapshots + ownership + interest (AOI) radius.
    /// Host builds send lists; clients apply snapshots. Prediction is out of scope.
    /// </summary>
    public sealed class ReplicationScaffold
    {
        public const int TagTransformSnapshot = 1001;
        public const int TagOwnership = 1002;

        private readonly Dictionary<int, ReplicatedEntity> _entities = new();
        private Vector3 _observer;
        private float _interestRadius = 64f;

        public float InterestRadius
        {
            get => _interestRadius;
            set => _interestRadius = MathF.Max(1f, value);
        }

        public void SetObserver(Vector3 position) => _observer = position;

        public void Register(int netId, Vector3 position, bool ownedLocally)
        {
            _entities[netId] = new ReplicatedEntity
            {
                NetId = netId,
                Position = position,
                OwnedLocally = ownedLocally,
            };
        }

        public void UpdateLocal(int netId, Vector3 position)
        {
            if (_entities.TryGetValue(netId, out var e))
            {
                e.Position = position;
                _entities[netId] = e;
            }
        }

        public void ApplySnapshot(int netId, Vector3 position)
        {
            if (_entities.TryGetValue(netId, out var e) && e.OwnedLocally)
                return;
            Register(netId, position, ownedLocally: false);
        }

        /// <summary>Entities inside AOI that the host should send this frame.</summary>
        public List<int> BuildInterestSendList()
        {
            var list = new List<int>(_entities.Count);
            float r2 = _interestRadius * _interestRadius;
            foreach (var kv in _entities)
            {
                if (Vector3.DistanceSquared(kv.Value.Position, _observer) <= r2)
                    list.Add(kv.Key);
            }
            return list;
        }

        public byte[] EncodeTransform(int netId)
        {
            if (!_entities.TryGetValue(netId, out var e))
                return Array.Empty<byte>();
            var w = new NetWriter();
            w.WriteInt(netId);
            w.WriteFloat(e.Position.X);
            w.WriteFloat(e.Position.Y);
            w.WriteFloat(e.Position.Z);
            return w.ToArray();
        }

        public static bool TryDecodeTransform(byte[] payload, out int netId, out Vector3 position)
        {
            netId = 0;
            position = default;
            if (payload == null || payload.Length < 16) return false;
            var r = new NetReader(payload);
            netId = r.ReadInt();
            position = new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
            return true;
        }

        public void HostBroadcastInterest(IGameNetwork net)
        {
            if (net == null || !net.IsHost) return;
            foreach (int id in BuildInterestSendList())
            {
                byte[] payload = EncodeTransform(id);
                if (payload.Length == 0) continue;
                net.Broadcast(TagTransformSnapshot, payload);
            }
        }

        private struct ReplicatedEntity
        {
            public int NetId;
            public Vector3 Position;
            public bool OwnedLocally;
        }
    }
}

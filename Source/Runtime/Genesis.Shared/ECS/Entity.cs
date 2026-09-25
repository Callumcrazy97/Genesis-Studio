namespace Genesis.Shared.ECS
{
    public readonly struct Entity : System.IEquatable<Entity>
    {
        public readonly int Id;
        public readonly int Version;

        public Entity(int id, int version) { Id = id; Version = version; }

        public bool IsNull => Id < 0;
        public static readonly Entity Null = new Entity(-1, -1);

        public bool Equals(Entity other) => Id == other.Id && Version == other.Version;
        public override bool Equals(object obj) => obj is Entity e && Equals(e);
        public override int GetHashCode() => System.HashCode.Combine(Id, Version);
        public static bool operator ==(Entity a, Entity b) =>  a.Equals(b);
        public static bool operator !=(Entity a, Entity b) => !a.Equals(b);
        public override string ToString() => $"Entity({Id}v{Version})";
    }
}

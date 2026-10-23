using System.Collections.Generic;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Changes to the scene written down to be made later, in the order given (PGSL SceneQueue*): a
/// worker job updating its share of the instances queues their new positions and values, and the
/// game thread makes them all at JobTake; Step events that queue instead of writing see the frame as
/// it began, and their changes land together after the frame's Step events.
/// </summary>
internal sealed class SceneCommandBuffer
{
    private enum Kind : byte { Position, Variable, Destroy, StaticMove }

    private struct Record
    {
        public Kind Kind;
        public int Id;
        public double A, B, C, D, E, F, G;
        public string Name;
        public object Value;
    }

    /// <summary>The most changes a buffer holds; later ones are refused.</summary>
    public const int MaxCommands = 1_000_000;

    private readonly List<Record> _records = new();

    public int Count => _records.Count;

    public bool Position(int id, double x, double y, double z) =>
        Add(new Record { Kind = Kind.Position, Id = id, A = x, B = y, C = z });

    public bool Variable(int id, string name, object value) =>
        Add(new Record { Kind = Kind.Variable, Id = id, Name = name, Value = value });

    public bool Destroy(int id) => Add(new Record { Kind = Kind.Destroy, Id = id });

    public bool StaticMove(int id, double x, double y, double z, double sx, double sy, double sz, double yaw) =>
        Add(new Record { Kind = Kind.StaticMove, Id = id, A = x, B = y, C = z, D = sx, E = sy, F = sz, G = yaw });

    private bool Add(Record record)
    {
        if (_records.Count >= MaxCommands) return false;
        _records.Add(record);
        return true;
    }

    public void Clear() => _records.Clear();

    /// <summary>Makes every change in order on the game thread, then forgets them; returns how many were made.</summary>
    public int Apply()
    {
        int made = 0;
        // Taken first: a change that runs a script (a Destroy event) may queue more, for the next apply.
        Record[] records = _records.ToArray();
        _records.Clear();
        foreach (Record record in records)
        {
            switch (record.Kind)
            {
                case Kind.Position:
                    if (PgslCommands.PlaceInstanceById(record.Id, record.A, record.B, record.C)) made++;
                    break;
                case Kind.Variable:
                    if (PgslBehavior.FindById(record.Id)?.Vm is null) break;
                    PgslCommands.InstanceVariableSet(record.Id, record.Name, record.Value);
                    made++;
                    break;
                case Kind.Destroy:
                    PgslCommands.InstanceDestroy(record.Id);
                    made++;
                    break;
                case Kind.StaticMove:
                    if (PgslCommands.MeshStaticMove(record.Id, record.A, record.B, record.C, record.D, record.E, record.F, record.G)) made++;
                    break;
            }
        }
        return made;
    }
}

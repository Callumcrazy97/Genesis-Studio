using System;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Scene changes queued to be made later (SceneCommandBuffer). In a worker job they go to the job's
// own buffer and are made, in order, when the game takes the job (JobTake); so jobs can update their
// share of the instances side by side and the game applies the results. Anywhere else they go to the
// game's buffer, made after the frame's Step events (or at once with SceneQueueApply).
public static partial class PgslCommands
{
    private static readonly SceneCommandBuffer GameSceneCommands = new();

    /// <summary>Changes waiting in the game's buffer (made after the frame's Step events).</summary>
    internal static int SceneCommandsWaiting => GameSceneCommands.Count;

    /// <summary>Makes the game buffer's changes now; the scene calls it once Step events have run.</summary>
    internal static int ApplySceneCommands() => GameSceneCommands.Count == 0 ? 0 : GameSceneCommands.Apply();

    /// <summary>Forgets queued changes (a new game or room).</summary>
    internal static void ClearSceneCommands() => GameSceneCommands.Clear();

    // A worker job's own buffer (made when it first queues), else the game's.
    private static SceneCommandBuffer SceneBuffer()
    {
        PgslContext ctx = GetContext();
        if (ctx?.JobMeshes == null) return GameSceneCommands;
        return ctx.SceneCommands ??= SceneCommandBuffer.Rent();
    }

    private static bool SceneId(double id, out int value)
    {
        value = 0;
        if (!double.IsFinite(id) || id < 0 || id > int.MaxValue) return false;
        value = (int)id;
        return true;
    }

    [PgslCommand("SceneQueuePosition", "SceneQueuePosition(id, x, y, z) -> bool",
        "Queue an instance's move, made later in order: in a worker job when the game takes the job (JobTake), elsewhere after this frame's Step events. Lets jobs update instances side by side",
        "Instances")]
    public static bool SceneQueuePosition(double id, double x, double y, double z) =>
        SceneId(id, out int instance) && Finite3(x, y, z) && SceneBuffer().Position(instance, x, y, z);

    [PgslCommand("SceneQueuePositionsFromNumList", "SceneQueuePositionsFromNumList(instances, positions) -> number",
        "Queue many moves at once: instances is a number list of ids, positions x, y, z for each in turn. Returns how many were queued",
        "Instances")]
    public static double SceneQueuePositionsFromNumList(double instances, double positions)
    {
        if (NumRead(instances) is not { } ids || NumRead(positions) is not { } xyz) return 0;
        SceneCommandBuffer buffer = SceneBuffer();
        int count = Math.Min(ids.Count, xyz.Count / 3), queued = 0;
        for (int i = 0; i < count; i++)
        {
            double x = xyz.Items[i * 3], y = xyz.Items[i * 3 + 1], z = xyz.Items[i * 3 + 2];
            if (SceneId(ids.Items[i], out int instance) && Finite3(x, y, z) && buffer.Position(instance, x, y, z)) queued++;
        }
        return queued;
    }

    [PgslCommand("SceneQueueVariable", "SceneQueueVariable(id, name, value) -> bool",
        "Queue setting a variable of an instance to a number, text or true/false, made later in order (as SceneQueuePosition)",
        "Instances")]
    public static bool SceneQueueVariable(double id, string name, object value) =>
        SceneId(id, out int instance) && !string.IsNullOrWhiteSpace(name) && value is double or bool or string or int or float
        && SceneBuffer().Variable(instance, name.Trim(), value is int or float ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : value);

    [PgslCommand("SceneQueueDestroy", "SceneQueueDestroy(id) -> bool", "Queue destroying an instance, made later in order (as SceneQueuePosition)", "Instances")]
    public static bool SceneQueueDestroy(double id) => SceneId(id, out int instance) && SceneBuffer().Destroy(instance);

    [PgslCommand("SceneQueueStaticMove", "SceneQueueStaticMove(id, x, y, z, sx, sy, sz, yaw) -> bool",
        "Queue moving a placed mesh (MeshStaticAdd), made later in order (as SceneQueuePosition)", "Meshes")]
    public static bool SceneQueueStaticMove(double id, double x, double y, double z, double sx, double sy, double sz, double yaw) =>
        SceneId(id, out int placed) && Finite3(x, y, z) && Finite3(sx, sy, sz) && double.IsFinite(yaw)
        && SceneBuffer().StaticMove(placed, x, y, z, sx, sy, sz, yaw);

    [PgslCommand("SceneQueueCount", "SceneQueueCount() -> number", "Changes queued and not yet made (in a job, the job's own)", "Instances")]
    public static double SceneQueueCount() => SceneBuffer().Count;

    [PgslCommand("SceneQueueApply", "SceneQueueApply() -> number", "Make the game's queued changes now instead of after the Step events; returns how many were made", "Instances")]
    public static double SceneQueueApply() => ApplySceneCommands();
}

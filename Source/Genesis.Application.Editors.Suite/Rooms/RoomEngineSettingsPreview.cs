using System.Reflection;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// A room's engine settings (Engine.Rendering.Fog.Start, Engine.Lighting.Sun.Pitch...) shown in the
/// Room editor's view as the game will show them, without running it. The room's look is made the
/// game's way twice, without its settings and with them (each set as a script sets it), and every
/// value of the frame state the settings change is laid over the editor's own view; nothing is
/// written per setting. A setting is set only where it can be read first, and each is put back
/// afterwards, so values the whole of Studio shares (depth of field, render scale, threads) are as
/// they were and one view's preview never reaches another.
/// </summary>
internal static class RoomEngineSettingsPreview
{
    internal sealed record Change(FieldInfo Field, object? Value);

    private static readonly FieldInfo[] Fields = typeof(Mesh3DState).GetFields(BindingFlags.Instance | BindingFlags.Public);

    /// <summary>The frame-state values <paramref name="settings"/> change in <paramref name="room"/>'s look.</summary>
    public static IReadOnlyList<Change> Changes(string projectRoot, RoomAsset room, IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is not { Count: > 0 } || room is null) return [];
        try
        {
            object without = StateOf(projectRoot, room, null);
            object with = StateOf(projectRoot, room, settings);
            var changes = new List<Change>();
            foreach (FieldInfo field in Fields)
            {
                object? value = field.GetValue(with);
                if (!Equals(field.GetValue(without), value)) changes.Add(new Change(field, value));
            }
            return changes;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NullReferenceException
                                              or FormatException or IOException)
        {
            // The view shows the room without its settings rather than not at all.
            return [];
        }
    }

    /// <summary>Lays <paramref name="changes"/> over a frame state the editor has made.</summary>
    public static void Apply(ref Mesh3DState state, IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return;
        object boxed = state;
        foreach (Change change in changes) change.Field.SetValue(boxed, change.Value);
        state = (Mesh3DState)boxed;
    }

    private static Mesh3DState StateOf(string projectRoot, RoomAsset room, IReadOnlyDictionary<string, string>? settings)
    {
        using var scene = new RuntimeScene("Room look preview");
        RoomSceneBuilder.ApplySceneSettings(scene, room);
        if (settings == null) return EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, null, false, default);

        var game = new ProjectGameContext(projectRoot, scene, null, null, room, null);
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        var context = new PgslContext();
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = projectRoot;
        try
        {
            // Each value as it is now (the setting, and the area its Enabled turns on), to put back.
            void Remember(string name)
            {
                if (!before.ContainsKey(name) && EngineSettings.TryRead(name, context, out object? value) && value is not null)
                    before[name] = EngineSettings.Format(value);
            }
            foreach ((string name, string text) in settings)
            {
                Remember(name);
                if (!before.ContainsKey(name)) continue;
                applied[name] = text;
                if (name.EndsWith(".Enabled", StringComparison.OrdinalIgnoreCase) && name.LastIndexOf('.', name.Length - ".Enabled".Length - 1) is int dot and > 0)
                    Remember(name[..dot] + ".Enabled");
            }
            EngineSettings.Apply(applied, context);
            return EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, null, false, default);
        }
        finally
        {
            EngineSettings.Apply(before, context);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }
}

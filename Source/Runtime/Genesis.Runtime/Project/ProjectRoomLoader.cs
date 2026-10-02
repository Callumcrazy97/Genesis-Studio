using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;

namespace Genesis.Runtime.Project;

/// <summary>Loads world services before Object Create and room lifecycle events can query them.</summary>
public static class ProjectRoomLoader
{
    /// <summary>How long a room read ahead with <see cref="Preload"/> waits for the room change.</summary>
    public const long PreloadKeepMilliseconds = 10 * 60 * 1000;

    /// <summary>
    /// Starts reading another room's models on worker threads, so a later change to that room
    /// finds them read. The game carries on meanwhile. Returns how many models the room names,
    /// or 0 when there is no such room or it is not a 3D room.
    /// </summary>
    public static int Preload(string projectPath, string roomName)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(roomName)) return 0;
        try
        {
            string file = ProjectRoomResolver.ResolveRoomFile(projectPath, roomName);
            if (file == null) return 0;
            RoomAsset room = RoomAssetLoader.Parse(file);
            return new RoomSceneBuilder(projectPath).PrefetchModels(room, PreloadKeepMilliseconds);
        }
        catch (System.Exception exception) when (exception is System.IO.IOException or System.IO.InvalidDataException
            or System.UnauthorizedAccessException or Newtonsoft.Json.JsonException or System.ArgumentException)
        {
            System.Console.WriteLine($"[Room] '{roomName}' could not be read ahead: {exception.Message}");
            return 0;
        }
    }

    public static RoomBuildResult Build(string projectPath, RuntimeScene scene, RoomAsset room,
        ScriptHostSystem scripts, ProjectGameContext context, bool beginGame)
    {
        context.SetRoom(room);
        var builder = new RoomSceneBuilder(projectPath, scripts);
        // Worker threads read the room's models while this thread loads the terrain and creates
        // the objects; the first frame then finds them read instead of reading each in turn.
        builder.PrefetchModels(room);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (RoomTerrainSubsystem.ShouldRegister(room))
            scene.AddSubsystem(new RoomTerrainSubsystem(projectPath, room, context));
        double terrainMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (RoomWeatherEffectsSubsystem.ShouldRegister(room))
            scene.AddSubsystem(new RoomWeatherEffectsSubsystem(room.Environment, context.Audio));
        float sceneryDistance = room.Environment?.SceneryDistance ?? 0f;
        RoomSceneryStreamer scenery = room.Dimension == RoomDimension.ThreeD && float.IsFinite(sceneryDistance) && sceneryDistance > 0f
            ? new RoomSceneryStreamer(sceneryDistance)
            : null;
        builder.Scenery = scenery;
        RoomBuildResult result = builder.Build(scene, room);
        if (scenery != null)
        {
            result.DeferredScenery = scenery.Total;
            if (scenery.Total > 0)
            {
                scenery.Attach(builder, room);
                scene.AddSubsystem(scenery);
            }
        }

        result.TerrainMilliseconds = terrainMilliseconds;
        long roomStart = System.Diagnostics.Stopwatch.GetTimestamp();
        scripts.BeginRoom(beginGame);
        result.RoomStartMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(roomStart).TotalMilliseconds;
        return result;
    }
}

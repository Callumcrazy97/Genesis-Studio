using System.Collections.Generic;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Diagnostics;

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
        var result = new RoomBuildResult();
        foreach ((float Progress, bool Wait) _ in BuildSteps(projectPath, scene, room, scripts, context, beginGame, result, spread: false)) { }
        return result;
    }

    /// <summary>
    /// Loads a room a piece at a time. Each value returned says how far along the room is, from 0
    /// to 1, and whether the next piece is waiting on a worker thread: when it is, the caller
    /// should let a frame be drawn before asking again. With <paramref name="spread"/> false
    /// nothing is left to workers and no value asks to wait, so taking every value at once loads
    /// the room in one step, as <see cref="Build"/> does.
    /// </summary>
    public static IEnumerable<(float Progress, bool Wait)> BuildSteps(string projectPath, RuntimeScene scene, RoomAsset room,
        ScriptHostSystem scripts, ProjectGameContext context, bool beginGame, RoomBuildResult result, bool spread)
    {
        var builder = new RoomSceneBuilder(projectPath, scripts);
        using (LoadProfile.Begin("room: set the room and queue its models to read ahead"))
        {
            context.SetRoom(room);
            // Worker threads read the room's models while this thread loads the terrain and creates
            // the objects; the first frame then finds them read instead of reading each in turn.
            builder.PrefetchModels(room);
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (RoomTerrainSubsystem.ShouldRegister(room))
        {
            if (spread)
            {
                // Reading a terrain touches nothing of the scene and no graphics card, so a worker
                // reads it while this thread goes on drawing frames.
                System.Threading.Tasks.Task<RoomTerrainSubsystem> reading =
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        using (LoadProfile.Begin("room: read the terrain"))
                            return new RoomTerrainSubsystem(projectPath, room, context);
                    });
                while (!reading.IsCompleted) yield return (0.02f, true);
                scene.AddSubsystem(reading.GetAwaiter().GetResult());
            }
            else
            {
                using (LoadProfile.Begin("room: read the terrain"))
                    scene.AddSubsystem(new RoomTerrainSubsystem(projectPath, room, context));
            }
        }

        double terrainMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (RoomWeatherEffectsSubsystem.ShouldRegister(room))
        {
            using (LoadProfile.Begin("room: weather effects"))
                scene.AddSubsystem(new RoomWeatherEffectsSubsystem(room.Environment, context.Audio));
        }
        float sceneryDistance = room.Environment?.SceneryDistance ?? 0f;
        float sceneryCollision = room.Environment?.SceneryCollisionDistance ?? 64f;
        RoomSceneryStreamer scenery = room.Dimension == RoomDimension.ThreeD && float.IsFinite(sceneryDistance) && sceneryDistance > 0f
            ? new RoomSceneryStreamer(sceneryDistance)
            {
                ColliderRadiusAroundBodies = float.IsFinite(sceneryCollision) ? System.Math.Clamp(sceneryCollision, 0f, CollisionFoci.MaximumRadius) : 64f,
            }
            : null;
        builder.Scenery = scenery;
        foreach (float placed in builder.BuildSteps(scene, room, result))
            yield return (0.05f + 0.9f * placed, false);
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
        // The room-start events are a piece of their own.
        yield return (0.97f, false);
        long roomStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (LoadProfile.Begin("room: room-start events"))
            scripts.BeginRoom(beginGame);
        result.RoomStartMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(roomStart).TotalMilliseconds;
    }
}

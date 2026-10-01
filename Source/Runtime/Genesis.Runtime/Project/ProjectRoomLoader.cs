using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;

namespace Genesis.Runtime.Project;

/// <summary>Loads world services before Object Create and room lifecycle events can query them.</summary>
public static class ProjectRoomLoader
{
    public static RoomBuildResult Build(string projectPath, RuntimeScene scene, RoomAsset room,
        ScriptHostSystem scripts, ProjectGameContext context, bool beginGame)
    {
        context.SetRoom(room);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (RoomTerrainSubsystem.ShouldRegister(room))
            scene.AddSubsystem(new RoomTerrainSubsystem(projectPath, room, context));
        double terrainMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RoomBuildResult result = new RoomSceneBuilder(projectPath, scripts).Build(scene, room);
        result.TerrainMilliseconds = terrainMilliseconds;
        long roomStart = System.Diagnostics.Stopwatch.GetTimestamp();
        scripts.BeginRoom(beginGame);
        result.RoomStartMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(roomStart).TotalMilliseconds;
        return result;
    }
}

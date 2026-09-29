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
        if (RoomTerrainSubsystem.ShouldRegister(room))
            scene.AddSubsystem(new RoomTerrainSubsystem(projectPath, room, context));
        RoomBuildResult result = new RoomSceneBuilder(projectPath, scripts).Build(scene, room);
        scripts.BeginRoom(beginGame);
        return result;
    }
}

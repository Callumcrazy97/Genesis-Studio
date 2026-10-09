using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A game's own sky (a square sun, a moon with phases, stars, a dome): script meshes drawn in the sky
// layer, behind everything the world draws, around the camera and at any distance.
public static partial class PgslCommands
{
    [PgslCommand("DrawMeshSetSky", "DrawMeshSetSky(enabled)",
        "Draw this instance's later script-mesh draws in the sky layer, until set off or DrawMeshResetState: first, right after the frame is cleared to the sky, so the world covers them; centred on the camera (the draw's x, y, z are an offset from the eye) and at any size or distance, never cut by the far plane; unlit in their own colours, under the sky's haze and clouds. For a game's own sun, moon, stars or sky dome; off by default",
        "Meshes")]
    public static void DrawMeshSetSky(bool enabled) => EditMeshOptions((ref ScriptMeshDrawOptions options) => options.Sky = enabled);
}

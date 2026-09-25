using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Headless.Suites;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using WinFormsApplication = System.Windows.Forms.Application;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Showcase;

/// <summary>
/// Renders the room while it is being simulated, by pushing live ECS transforms back onto the room
/// nodes each frame and drawing through the Room Editor's 2D view.
/// </summary>
/// <remarks>
/// The simulation is genuinely the runtime — a real ECS world, the real <c>ScriptHostSystem</c>, and
/// the object's own PGSL Step event reading virtualised key state. What this class supplies is only
/// the *view*: the editor's 2D renderer already draws the background, the tile layers and each
/// object's sprite exactly as authored, so reusing it shows the running game through the same
/// picture the designer was just looking at. That is the WYSIWYG claim the project is built around,
/// and it avoids standing up a second render path purely to record a GIF.
///
/// Stated plainly because it matters when reading the output: positions in the recording come from
/// the ECS, colours and layout come from the editor's renderer.
/// </remarks>
internal sealed class RoomPlaybackRenderer : IDisposable
{
    private readonly Form _host;
    private readonly RoomEditorControl _room;
    private readonly EcsWorld _world;
    private readonly IReadOnlyDictionary<string, Entity> _entitiesByNodeId;

    public RoomPlaybackRenderer(string roomPath, string projectRoot, EcsWorld world, RoomBuildResult built)
    {
        _world = world;
        _entitiesByNodeId = built.EntitiesByNodeId;

        _room = new RoomEditorControl(roomPath, projectRoot) { Dock = DockStyle.Fill };
        _host = new Form
        {
            Text = "Genesis — Playing Level1",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            ClientSize = new Size(1180, 700),
            ShowInTaskbar = false,
        };
        _host.Controls.Add(_room);
        GateSuite.ShowHost(_host);

        // Nothing is selected during playback: gizmos and the selection box are authoring chrome
        // and would read as part of the game.
        _room.Select(null);
        Pump(8, 25);
    }

    /// <summary>Copies live entity transforms onto their nodes and grabs the frame.</summary>
    public Bitmap RenderFrame()
    {
        foreach (RoomNode node in _room.Room.Nodes)
        {
            if (node.Kind != RoomNodeKind.GameObject) continue;
            if (!_entitiesByNodeId.TryGetValue(node.Id, out Entity entity)) continue;
            if (!_world.Has<TransformComponent>(entity)) continue;

            ref TransformComponent transform = ref _world.GetRef<TransformComponent>(entity);
            node.Transform.X = transform.X;
            node.Transform.Y = transform.Y;
        }

        _room.Invalidate(true);
        return ShowcaseRunner.Grab(_host);
    }

    private static void Pump(int iterations, int delayMilliseconds) =>
        ShowcaseRunner.Pump(iterations, delayMilliseconds);

    public void Dispose()
    {
        _host.Close();
        _host.Dispose();
        WinFormsApplication.DoEvents();
    }
}

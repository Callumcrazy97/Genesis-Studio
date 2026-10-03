using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private Panel? _roomWorkspaceHost;
    private Label? _roomStartingSteps;
    private FlowLayoutPanel? _roomGameGuide;
    private bool _showRoomGuide;
    private bool _layingOutRoomWorkflow;

    /// <summary>The shell updates its retained project manifest before persisting the starting Room.</summary>

    private void BuildRoomWorkflowHost(Control authoring)
    {
        _roomWorkspaceHost = new Panel { Dock = DockStyle.Fill, Name = "RoomWorkspace" };
        _roomStartingSteps = new Label { Dock = DockStyle.Top, Name = "RoomStartingSteps",
            BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Muted, Padding = new Padding(10, 6, 10, 6) };
        _roomWorkspaceHost.Controls.Add(authoring);
        _roomWorkspaceHost.Controls.Add(_roomStartingSteps);
        _roomWorkspaceHost.SizeChanged += (_, _) => RefreshRoomWorkflowHint();
        Controls.Add(_roomWorkspaceHost);
        RefreshRoomWorkflowHint();
    }

    private void RefreshRoomWorkflowHint()
    {
        if (_roomStartingSteps is null || _roomWorkspaceHost is null || _layingOutRoomWorkflow) return;
        _layingOutRoomWorkflow = true;
        try
        {
            _roomStartingSteps.Text = _navigation.CurrentSection switch
            {
                RoomNavSection.Objects => "1. Choose an Object on the left.  2. Click in the room to place it; select it to change its properties.  3. Save, then Play. Use in game explains the full workflow.",
                RoomNavSection.Tilesets when !ViewMode3D => "1. Choose a tile set on the left.  2. Choose a tile in its sheet.  3. Drag in the room to paint; right-drag to erase. Add makes another tile layer; each layer can have its own depth and collision.",
                RoomNavSection.Tilesets => "Choose a Terrain asset on the left, then click in the room to place it. Select a placed terrain below the assets to edit its properties.",
                RoomNavSection.Instances => "Select a placed instance in the hierarchy or room. The eye controls visibility; the lock protects it from editing. Use the Inspector for position, scale and instance variables.",
                RoomNavSection.Backgrounds => ViewMode3D ? "Configure the sky, time and weather for this Room." : "Choose a saved Image and add it as a background. Select its layer to change layout, depth, opacity and scrolling.",
                RoomNavSection.Views => "Enable a camera view, choose its source area and optionally a follow target. Source is the area of the Room it sees; output is its rectangle on the game window.",
                RoomNavSection.Settings => "Set the Room size, display and physics here. 2D positions and sizes use pixels; positive downward gravity makes physics bodies fall toward the floor.",
                _ => "Build your Room, Save, then Play. Use in game explains how saved editor resources work together."
            };
            _roomStartingSteps.Font = EditorChrome.SmallFont;
            _roomStartingSteps.Height = _roomStartingSteps.Padding.Vertical + TextRenderer.MeasureText(_roomStartingSteps.Text,
                _roomStartingSteps.Font, new Size(Math.Max(120, _roomWorkspaceHost.ClientSize.Width - _roomStartingSteps.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height;
            _mainSplit.Visible = !_showRoomGuide;
            // Retired in favour of the workflow bar, which carries this section's tip instead.
            _roomStartingSteps.Visible = !_showRoomGuide && _roomWorkflow is null;
            SyncRoomWorkflowStep(_roomStartingSteps.Text);
            if (_roomGameGuide is not { } guide) return;
            guide.Visible = _showRoomGuide;
            int width = Math.Max(120, guide.ClientSize.Width - guide.Padding.Horizontal - 24);
            foreach (Control child in guide.Controls)
            {
                child.Width = width;
                child.Font = child.Tag as string == "heading" ? EditorChrome.HeadingFont : EditorChrome.BaseFont;
                child.Height = child is Label label
                    ? TextRenderer.MeasureText(label.Text, child.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 10
                    : child.Font.Height + 16;
            }
            guide.AutoScrollMinSize = new Size(0, guide.Controls.Cast<Control>().Where(child => child.Visible)
                .Select(child => child.Bottom - guide.AutoScrollPosition.Y + child.Margin.Bottom + guide.Padding.Bottom + 24).DefaultIfEmpty(0).Max());
            if (_showRoomGuide) guide.BringToFront();
        }
        finally { _layingOutRoomWorkflow = false; }
    }

    public override void ApplyInterfaceLayout()
    {
        base.ApplyInterfaceLayout();
        RefreshRoomWorkflowHint();
        ApplyResponsiveLayout();
        _navigation.TilesetsPanel.ApplyInterfaceLayout();
        _navigation.BackgroundsPanel.ApplyInterfaceLayout();
        _editorToolbar.ApplyInterfaceLayout();
        _inspector.ApplyInterfaceLayout();
        _workspaceSettings?.ApplyInterfaceLayout();
    }

    private void ShowRoomAuthoring()
    {
        if (!_showRoomGuide) return;
        _showRoomGuide = false;
        RefreshRoomWorkflowHint();
        ApplyResponsiveLayout();
    }

    private void ShowRoomGameGuide()
    {
        if (_roomWorkspaceHost is null) return;
        if (_roomGameGuide is null)
        {
            _roomGameGuide = new FlowLayoutPanel { Name = "RoomUseInGame", Dock = DockStyle.Fill, AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18, 14, 18, 28), BackColor = EditorChrome.Surface };
            _roomGameGuide.SizeChanged += (_, _) => RefreshRoomWorkflowHint();
            _roomWorkspaceHost.Controls.Add(_roomGameGuide);
        }
        foreach (Control child in _roomGameGuide.Controls.Cast<Control>().ToArray()) child.Dispose();
        void Text(string text, bool heading = false) => _roomGameGuide.Controls.Add(new Label
        { Text = text, Tag = heading ? "heading" : null, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted });
        void Button(string name, string text, Action action, bool enabled = true)
        {
            Button button = new() { Name = name, Text = text, Enabled = enabled };
            EditorChrome.StyleField(button); button.Click += (_, _) => action(); _roomGameGuide.Controls.Add(button);
        }
        void Open(RoomNavSection section) { ShowRoomAuthoring(); _navigation.SetSection(section); }
        Text("Build and play this Room", true);
        Text("1. Place Objects and paint the floor", true);
        Text("Objects are saved game entities: choose one in Objects, then click in the Room. Change its behaviour in the Object editor with Builder or Code. Instance variables here override only the selected instance. Image animation and rigging, Physics bodies, Pathing, Audio, Shaders and UI resources are used by the Object's events; saving those resources updates their consumers.");
        Button("RoomGuideObjects", "Return to Objects", () => Open(RoomNavSection.Objects));
        Text("In Tilesets, choose a saved Image tile set, choose a tile, then drag to paint. Right-drag erases. Add creates a separate tile layer. Image tile collision flags and the layer's Collision setting determine which tiles are solid. Sprite positions use the Image's saved origin in both this editor and gameplay: a bottom origin places feet on the floor. Change the origin in Image rather than compensating with a different Room position.");
        Button("RoomGuideTiles", "Return to Tilesets", () => Open(RoomNavSection.Tilesets));
        Text("2. Set the camera and physics", true);
        Text("Views controls what the game camera sees and which Object it follows. Source is the visible area of the Room; output is its rectangle on the game window. Settings controls Room size and gravity. In 2D, gravity is shown in pixels per second squared with positive values downward; it affects Objects with Physics2D bodies. Scripted movement can use its own gravity.");
        Button("RoomGuideViews", "Open camera Views", () => Open(RoomNavSection.Views));
        Button("RoomGuideSettings", "Open Room Settings", () => Open(RoomNavSection.Settings));
        Text("3. Save, Play and export", true);
        Text("Play in this toolbar saves and tests this Room in Genesis Player. Pause and Stop appear while it is running. Studio Run starts the project's first Room: drag Rooms in the Assets tree to put them in order. Select the renderer from the bottom-right Studio status bar before testing another backend.");
        Text("For a complete jumping platformer, create a Mushroom Meadow project and edit its Player, enemies and levels. Use the project's Export command to package the game, then test the exported game and each renderer you intend to support.");
        Button("RoomGuideReturn", "Return to Room editing", ShowRoomAuthoring);
        _showRoomGuide = true; RefreshRoomWorkflowHint();
    }
}

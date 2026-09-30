using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private Panel _toolPanel = null!;
    private SplitContainer _terrainObjectSplit = null!;
    private TableLayoutPanel? _terrainWorkspaceHost;
    private Label? _terrainStartingSteps;
    private FlowLayoutPanel? _terrainGameGuide;
    private bool _showTerrainGuide;

    private void BuildTerrainWorkflowHost(Panel right, ToolStrip rail)
    {
        _terrainWorkspaceHost = new TableLayoutPanel { Dock = DockStyle.Fill, Name = "TerrainWorkspace", Tag = "font-measured-layout",
            ColumnCount = 4, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        _terrainWorkspaceHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        _terrainWorkspaceHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        _terrainWorkspaceHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _terrainWorkspaceHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        _terrainWorkspaceHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _terrainWorkspaceHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _terrainStartingSteps = new Label { Dock = DockStyle.Top, Name = "TerrainStartingSteps", Padding = new Padding(10, 6, 10, 6),
            BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Muted };
        foreach (Control control in new Control[] { _viewport, right, _toolPanel, rail, _terrainStartingSteps })
        { control.Dock = DockStyle.Fill; control.Margin = Padding.Empty; }
        _terrainWorkspaceHost.Controls.Add(_viewport, 2, 1);
        _terrainWorkspaceHost.Controls.Add(right, 3, 1);
        _terrainWorkspaceHost.Controls.Add(_toolPanel, 1, 1);
        _terrainWorkspaceHost.Controls.Add(rail, 0, 1);
        _terrainWorkspaceHost.Controls.Add(_terrainStartingSteps, 0, 0);
        _terrainWorkspaceHost.SetColumnSpan(_terrainStartingSteps, 4);
        _terrainWorkspaceHost.SizeChanged += (_, _) => ApplyInterfaceLayout();
        Controls.Add(_terrainWorkspaceHost);
        _viewport.BringToFront();
        HandleCreated += (_, _) => BeginInvoke(() => { if (!IsDisposed) ApplyInterfaceLayout(); });
    }

    private void RefreshTerrainWorkflowHint()
    {
        if (_terrainStartingSteps is null || _terrainWorkspaceHost is null) return;
        _terrainStartingSteps.Text = ActiveMode switch
        {
            TerrainEditorMode.Generate => "1. Create terrain from a preset, heightmap or outline. 2. Sculpt and Paint. 3. Save and Use in game to add it to a Room.",
            TerrainEditorMode.Sculpt => "Choose a brush, then drag on the terrain. Radius sets its size; Strength sets how much each stroke changes. Ctrl+Z undoes a stroke.",
            TerrainEditorMode.Paint => "Choose a layer, then drag to paint. A region limits the brush and Fill; without a region, Fill covers the whole terrain. Edit layer materials in Objects and Inspector.",
            TerrainEditorMode.Water when _waterWorkflowCombo?.SelectedIndex == 1 => "Click Draw river, then click terrain to add points. Drag the handles to adjust width and depth; Finish River saves the spline. Select a saved river to edit it.",
            TerrainEditorMode.Water when _waterWorkflowCombo?.SelectedIndex == 2 => "Select a saved water body, set its swimming physics and depth in the Inspector, then Drop into water to test it. Pause or Reset controls the test capsule.",
            TerrainEditorMode.Water => "Choose Spray or Region, mark the area, set Level, then Fill water. Select a saved body to set its depth and swimming physics in the Inspector.",
            TerrainEditorMode.Paths => "Choose Paint Path and drag a trail, or set the Path Network fields and generate connected routes. Save includes the path network.",
            TerrainEditorMode.Foliage => "Create a plant or tree, select it, then click to place. Ecological scatter provides regional density and brush tools.",
            TerrainEditorMode.Entities => "Choose a kind of terrain object, give it a name and resources, then place it in the view. Saved terrain parts belong to this terrain.",
            _ => "Select and move objects in the view. Use Options → Panels for Objects and Inspector. Create, Sculpt and Paint are on the left; Save and Use in game connects the terrain to a Room."
        };
        if (_terrainWorkflow is not null)
        {
            // The workflow bar now carries the steps and this mode's tip; the old hint row retires.
            _terrainWorkflow.SetInstruction(ActiveMode == TerrainEditorMode.Generate || _showTerrainGuide
                ? null
                : _terrainStartingSteps.Text);
            _terrainStartingSteps.Visible = false;
        }

        _terrainStartingSteps.Font = EditorChrome.SmallFont;
        int hintHeight = _terrainWorkflow is not null ? 0 : TextRenderer.MeasureText(_terrainStartingSteps.Text, _terrainStartingSteps.Font,
            new Size(Math.Max(120, _terrainWorkspaceHost.ClientSize.Width - _terrainStartingSteps.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + _terrainStartingSteps.Padding.Vertical;
        _terrainStartingSteps.Visible = !_showTerrainGuide && _terrainWorkflow is null;
        _terrainWorkspaceHost.RowStyles[0].Height = _showTerrainGuide ? 0 : hintHeight;
        _terrainWorkspaceHost.ColumnStyles[0].Width = _modeRail.Visible ? 82 * TerrainInterfaceScale : 0;
        _terrainWorkspaceHost.ColumnStyles[1].Width = _toolPanel.Visible ? 300 * TerrainInterfaceScale : 0;
        _terrainWorkspaceHost.ColumnStyles[3].Width = _rightPanel.Visible ? 340 * TerrainInterfaceScale : 0;
        _viewport.Visible = !_showTerrainGuide;
        if (_terrainGameGuide is not { } guide) return;
        guide.Visible = _showTerrainGuide;
        FitTerrainPage(guide);
        if (_showTerrainGuide) guide.BringToFront();
    }

    private void ShowTerrainAuthoring()
    {
        _showTerrainGuide = false;
        SyncTerrainWorkflowStep();
        ApplyInterfaceLayout();
    }

    private void ShowTerrainGameGuide()
    {
        if (_terrainWorkspaceHost is null) return;
        if (_terrainGameGuide is null)
        {
            _terrainGameGuide = MakeContextPage();
            _terrainGameGuide.Name = "TerrainUseInGame";
            _terrainGameGuide.Dock = DockStyle.Fill;
            _terrainGameGuide.SizeChanged += (_, _) => ApplyInterfaceLayout();
            _terrainWorkspaceHost.Controls.Add(_terrainGameGuide, 0, 1);
            _terrainWorkspaceHost.SetColumnSpan(_terrainGameGuide, 4);
        }
        foreach (Control child in _terrainGameGuide.Controls.Cast<Control>().ToArray()) child.Dispose();
        void Text(string text)
        {
            Label label = MakeContextSummary(); label.Text = text; _terrainGameGuide.Controls.Add(label);
        }
        _terrainGameGuide.Controls.Add(MakeContextCaption("Build and play this terrain"));
        Text("1. Create a landscape, Sculpt its heights and Paint its layers. Add paths, water, foliage and other objects as needed. Save writes the heightfield and its owned resources together.");
        Text("2. Add the saved terrain to a 3D Room. The button below creates a Room containing this terrain at its saved origin and opens Room editing. It does not add a player controller; use the Gate Climbing template for a playable 3D starting project.");
        TextBox name = new() { Name = "TerrainRoomName", Text = ResourceNames.Name(ProjectRoot, ResourcePath) + " Room" };
        EditorChrome.StyleField(name); _terrainGameGuide.Controls.Add(name);
        Label result = MakeContextSummary();
        Button create = MakeContextAction("Save and create 3D Room", "Create and open a Room using this saved terrain", () =>
        {
            try { result.Text = "Created " + ResourceNames.Name(ProjectRoot, CreateTerrainRoom(name.Text)) + ". Add a player Object and configure Views, then Play."; }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException) { result.Text = exception.Message; }
            ApplyInterfaceLayout();
        });
        create.Name = "TerrainCreateRoom";
        _terrainGameGuide.Controls.Add(create); _terrainGameGuide.Controls.Add(result);
        Text("3. In Room, choose Terrain and click to place another instance. Select an instance to edit its placement; open the terrain to edit its landscape. Save those edits to refresh open consumers. Room Play tests that Room; Studio Run starts the project's starting Room.");
        Text("For moving characters, assign a saved Model and animation in Object, then use its events for movement and gameplay. Use Pathing for routes, Physics for collision and Audio or Particle resources for effects. Export and test the resulting Player on the renderers you intend to support.");
        _terrainGameGuide.Controls.Add(MakeContextAction("Return to terrain editing", "Close these instructions", ShowTerrainAuthoring));
        _showTerrainGuide = true;
        SyncTerrainWorkflowStep();
        ApplyInterfaceLayout();
    }

    public string CreateTerrainRoom(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        Save();
        if (IsDirty) throw new InvalidOperationException("Save the Terrain before creating a Room.");
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Room), ResourceKind.Room, validName);
        RoomAsset room = RoomAsset.Create(validName, RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode { Name = ResourceNames.Name(ProjectRoot, ResourcePath), Kind = RoomNodeKind.Terrain,
            LayerId = room.Layers[0].Id, EnabledIn2D = false,
            Terrain = new RoomTerrainData { Asset = ResourceNames.Name(ProjectRoot, ResourcePath) } });
        RoomAssetLoader.Save(room, path);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        ResourceNames.Invalidate(ProjectRoot);
        RequestOpenLinkedResource(path);
        return path;
    }

    private void BuildTerrainShellPanels(Panel right)
    {
        _toolPanel = EditorChrome.SidePanel(276, DockStyle.Left);
        _toolPanel.Name = "TerrainToolPanel";
        _contextHeader.Height = 42;
        _toolPanel.Controls.Add(_modeHost);
        _toolPanel.Controls.Add(_contextHeader);

        _terrainObjectSplit = new SplitContainer
        {
            Name = "TerrainObjectsInspectorSplit", Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal, BackColor = EditorChrome.Canvas,
            Size = new Size(320, 700), SplitterWidth = 6, SplitterDistance = 310,
            Panel1MinSize = 150, Panel2MinSize = 180,
        };
        _componentsPanel.Dock = DockStyle.Fill;
        _terrainObjectSplit.Panel1.Controls.Add(_componentsPanel);
        _selectionInspector.Dock = DockStyle.Fill;
        _terrainObjectSplit.Panel2.Controls.Add(_selectionInspector);
        var inspectorTitle = EditorChrome.SectionLabel("Contextual Inspector");
        inspectorTitle.Text = "Inspector";
        inspectorTitle.Font = EditorChrome.HeadingFont;
        inspectorTitle.ForeColor = Color.WhiteSmoke;
        inspectorTitle.BackColor = Color.FromArgb(35, 38, 41);
        inspectorTitle.AutoSize = true;
        _terrainObjectSplit.Panel2.Controls.Add(inspectorTitle);
        right.Controls.Add(_terrainObjectSplit);
        _selectionInspector.CreateObjectRequested += (_, _) => OnEntityCreateRequested(this, TerrainEntityType.Foliage);
        _selectionInspector.EditResourcesRequested += (_, _) =>
        {
            string? path = SelectedPlacedEntity()?.Entity ?? _componentsPanel.CurrentSelection?.Id;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(ResolveEntityFullPath(path))) return;
            ShowEntityWizard(ResolveEntityFullPath(path), null, false);
            _entityWizard?.GoToPage(2);
        };
        _selectionInspector.PlaceCopyRequested += (_, _) =>
        {
            string? path = SelectedPlacedEntity()?.Entity ?? _componentsPanel.CurrentSelection?.Id;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(ResolveEntityFullPath(path))) return;
            _placementEntityPath = ResolveEntityFullPath(path);
            SetMode(TerrainEditorMode.Select);
        };
    }

    private void FrameTerrain()
    {
        float extent = MathF.Max(_terrain.ResolutionX - 1, _terrain.ResolutionZ - 1) * _terrain.CellSize;
        float centerX = _terrain.OriginX + (_terrain.ResolutionX - 1) * _terrain.CellSize * .5f;
        float centerZ = _terrain.OriginZ + (_terrain.ResolutionZ - 1) * _terrain.CellSize * .5f;
        _viewport.Camera.Target = new Vector3(centerX,
            _terrain.SampleHeight(centerX, centerZ) + MathF.Min(12, extent * .08f), centerZ);
        _viewport.Camera.Distance = Math.Clamp(extent * 1.25f, 24f, 1600f);
        _viewport.Invalidate();
    }

    private void AddTerrainPlayback(ToolStrip toolbar, Action changed)
    {
        var play = EditorChrome.ToolButton("Play", "Play or pause the terrain preview", () => { });
        play.Click += (_, _) =>
        {
            if (_viewportSession.Clock.Playing) _viewportSession.Clock.Pause();
            else _viewportSession.Clock.Play();
            changed();
        };
        _viewportSession.Clock.Changed += (_, _) =>
        {
            if (!play.IsDisposed) play.Text = _viewportSession.Clock.Playing ? "Pause" : "Play";
        };
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(play);
        toolbar.Items.Add(EditorChrome.ToolButton("Stop", "Stop and rewind preview", () => { _viewportSession.Clock.Stop(); changed(); }));
    }

    private FlowLayoutPanel BuildObjectToolsPage()
    {
        FlowLayoutPanel page = MakeContextPage();
        page.Controls.Add(MakeContextCaption("Add terrain object"));
        foreach (TerrainEntityType type in Enum.GetValues<TerrainEntityType>())
        {
            TerrainEntityType captured = type;
            string label = type == TerrainEntityType.Fluid ? "Water" : type.ToString();
            page.Controls.Add(MakeContextAction(label + "…", "Create a terrain-owned " + label.ToLowerInvariant(),
                () => OnEntityCreateRequested(this, captured)));
        }
        Label hint = MakeContextSummary();
        hint.Height = 110;
        hint.Text = "Give each object a name and editor icon, then attach images, shaders, models, audio or physics resources in its wizard.";
        page.Controls.Add(hint);
        return page;
    }
}

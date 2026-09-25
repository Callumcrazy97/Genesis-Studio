using System.Drawing;
using System.Numerics;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private Panel _toolPanel = null!;
    private SplitContainer _terrainObjectSplit = null!;

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
        inspectorTitle.Text = "Contextual Inspector";
        inspectorTitle.Font = new Font("Segoe UI", 10.5f);
        inspectorTitle.ForeColor = Color.WhiteSmoke;
        inspectorTitle.BackColor = Color.FromArgb(35, 38, 41);
        inspectorTitle.Height = 32;
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
        float extent = MathF.Max(_terrain.ResolutionX, _terrain.ResolutionZ) * _terrain.CellSize;
        _viewport.Camera.Target = new Vector3(
            _terrain.OriginX + (_terrain.ResolutionX - 1) * _terrain.CellSize * .5f,
            (_terrain.MinHeight + _terrain.MaxHeight) * .18f,
            _terrain.OriginZ + (_terrain.ResolutionZ - 1) * _terrain.CellSize * .5f);
        _viewport.Camera.Distance = Math.Clamp(extent * .8f, 24f, 1600f);
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

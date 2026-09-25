using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private TerrainCreationPanel _creationPanel = null!;

    private void WireEmbeddedWizards()
    {
        _creationPanel = new TerrainCreationPanel(ResourceDisplayName.Format(ResourcePath))
        {
            Dock = DockStyle.Top,
            Height = 460,
        };
        SyncCreationPanelFromSettings();
    }

    private void SyncCreationPanelFromSettings()
    {
        if (_creationPanel is null) return;
        if (Enum.TryParse(_settings.Preset, out TerrainPreset preset))
        {
            _creationPanel.SetPreset(preset);
        }

        _creationPanel.SetSeed(_settings.Seed);
        _creationPanel.SetProcesses(
            _settings.ErosionIterations,
            _settings.ErosionStrength,
            _settings.TerraceStrength,
            _settings.RiverCount,
            _settings.RiverDepth);
    }

    private FlowLayoutPanel BuildGeneratePage()
    {
        FlowLayoutPanel page = MakeContextPage();
        page.AutoScroll = true;
        page.Controls.Add(_generateSummary);
        page.Controls.Add(MakeContextCaption("Generation Wizard"));
        page.Controls.Add(MakeContextAction("Create terrain…", "Choose a landscape preset and review its generation settings", OpenCreationWizard));
        page.Controls.Add(MakeContextCaption("Quick Processes"));
        page.Controls.Add(MakeContextAction("Regenerate from Seed", "Rebuild the authored preset deterministically", RegenerateFromSeed));
        page.Controls.Add(MakeContextAction("Thermal Erosion", "Relax unstable slopes", () => ApplyErosion(8, 0.35f)));
        page.Controls.Add(MakeContextAction("Geological Terracing", "Create authored height bands", () => ApplyTerracing(0.6f, 12)));
        page.Controls.Add(MakeContextAction("Carve River", "Carve one seeded meandering river", () => CarveRivers(1, 8f)));
        return page;
    }

    private FlowLayoutPanel BuildPathsPage()
    {
        FlowLayoutPanel page = MakeContextPage();
        page.AutoScroll = true;
        page.Controls.Add(_pathsSummary);
        page.Controls.Add(MakeContextCaption("Viewport Authoring"));
        Button paintPathButton = MakeContextAction(
            "Paint Path",
            "Drag on terrain to spray a trail using the width and kind above",
            () =>
            {
                _pathTool = PathAuthoringTool.PaintPath;
                SetMode(TerrainEditorMode.Paths);
            });
        _pathToolButtons.Add(PathAuthoringTool.PaintPath, paintPathButton);
        page.Controls.Add(paintPathButton);
        page.Controls.Add(MakeContextCaption("Path Network"));
        Control inspector = Inspector.InspectorBuilder.BuildForObject(
            _nature.PathSettings,
            "Generation is deterministic and grades/paints the terrain with full undo support.");
        inspector.Dock = DockStyle.Top;
        inspector.MinimumSize = new Size(240, 280);
        page.Controls.Add(inspector);
        page.Controls.Add(MakeContextAction(
            "Generate Connected Paths",
            "Build routes from the settings above and grade them into the terrain",
            () => GeneratePaths(_nature.PathSettings)));
        page.Controls.Add(MakeContextAction("Legacy Path Dialog…", "Open the modal path generator", OpenPathDialog));
        page.Controls.Add(MakeContextAction("Export World Map…", "Bake terrain, paths, water, and discovery to PNG", ExportWorldMap));
        return page;
    }

    private readonly FlowLayoutPanel _foliageAssets = new()
    {
        Width = 244, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
    };

    private FlowLayoutPanel BuildFoliagePage()
    {
        var page = MakeContextPage();
        page.Controls.Add(MakeContextCaption("Foliage assets"));
        page.Controls.Add(MakeContextAction("+ New foliage…", "Create a custom plant", () => OnEntityCreateRequested(this, TerrainEntityType.Foliage)));
        page.Controls.Add(MakeContextAction("+ New tree…", "Create a custom tree", () => OnEntityCreateRequested(this, TerrainEntityType.Tree)));
        page.Controls.Add(_foliageAssets);
        page.Controls.Add(MakeContextAction("Ecological scatter…", "Regional scatter tools and density settings", () =>
        {
            using var dialog = new DpiAwareForm
            {
                Text = "Ecological scatter", ClientSize = new Size(310, 690), StartPosition = FormStartPosition.CenterParent,
                BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text,
            };
            // Build once: these controls also hold the scatter state used by the brush.
            _foliageScatterPage ??= BuildFoliageScatterPage();
            _foliageScatterPage.Dock = DockStyle.Fill; dialog.Controls.Add(_foliageScatterPage);
            dialog.FormClosed += (_, _) => dialog.Controls.Remove(_foliageScatterPage);
            dialog.ShowDialog(FindForm());
        }));
        return page;
    }

    private FlowLayoutPanel? _foliageScatterPage;
    private bool _foliageBrushArmed;

    private void ArmLibraryAsset(string reference)
    {
        if (_nature.PlacedEntities.Any(placed => placed.Id == reference)) return;
        string path = Path.GetFullPath(ResolveEntityFullPath(reference));
        if (TryLoadEntityDocument(path) is null) return;
        _placementEntityPath = path;
        string relative = ResourceNames.Name(ProjectRoot, path);
        _selectedComponentKind = TerrainComponentsPanel.ComponentKind.Entity; _selectedComponentId = relative;
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Entity, relative);
        SetMode(TerrainEditorMode.Select); RefreshActiveToolCard();
        _statusLabel.Text = "Click terrain to place the asset · Ctrl keeps placing · Esc cancels";
    }

    private void RefreshFoliageAssets()
    {
        foreach (Control control in _foliageAssets.Controls.Cast<Control>().ToArray()) control.Dispose();
        foreach (string reference in _settings.Entities.Concat(Directory.Exists(ResourcePath + ".parts")
            ? Directory.EnumerateFiles(ResourcePath + ".parts", "*.terrainpart.json") : [])
            .Select(reference => Path.GetFullPath(ResolveEntityFullPath(reference))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var document = TryLoadEntityDocument(ResolveEntityFullPath(reference));
            if (document?.Type is not (TerrainEntityType.Foliage or TerrainEntityType.Tree)) continue;
            var tile = MakeContextAction(document.Name, "Click to place this foliage asset", () => ArmLibraryAsset(reference));
            tile.TextAlign = ContentAlignment.MiddleLeft; tile.Height = 52;
            tile.Text = "❧  " + document.Name;
            if (!string.IsNullOrWhiteSpace(document.Icon))
            {
                try
                {
                    string raster = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(ProjectRoot, document.Icon, 0);
                    using var bitmap = new Bitmap(raster); tile.Image = new Bitmap(bitmap, 32, 32);
                    tile.Text = "   " + document.Name; tile.ImageAlign = ContentAlignment.MiddleLeft;
                    tile.TextImageRelation = TextImageRelation.ImageBeforeText;
                    tile.Disposed += (_, _) => tile.Image?.Dispose();
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or System.Text.Json.JsonException) { }
            }
            _foliageAssets.Controls.Add(tile);
        }
        if (_foliageAssets.Controls.Count == 0)
            _foliageAssets.Controls.Add(new Label { Text = "Create a foliage asset to start placing plants and trees.", Width = 230, Height = 56, ForeColor = EditorChrome.Muted });
    }

    private FlowLayoutPanel BuildFoliageScatterPage()
    {
        FlowLayoutPanel page = MakeContextPage();
        page.AutoScroll = true;
        page.Controls.Add(_foliageSummary);
        page.Controls.Add(MakeContextCaption("Ecological Scatter"));
        Control scatterInspector = Inspector.InspectorBuilder.BuildForObject(
            _nature.FoliageSettings,
            "The same deterministic cell streaming, LOD and performance budgets run in this preview and in gameplay.");
        scatterInspector.Dock = DockStyle.Top;
        scatterInspector.MinimumSize = new Size(240, 320);
        page.Controls.Add(scatterInspector);
        page.Controls.Add(MakeContextAction(
            "Scatter Foliage",
            "Generate a deterministic ecological field from the settings above",
            () =>
            {
                ScatterFoliage(_nature.FoliageSettings);
                RefreshComponentsPanel();
            }));
        page.Controls.Add(MakeContextAction("Legacy Scatter Dialog…", "Open the modal foliage scatter dialog", OpenFoliageDialog));
        page.Controls.Add(MakeContextCaption("Regional Authoring"));
        page.Controls.Add(MakeComboPanel("Species", _foliageSpeciesCombo));
        foreach (FoliageBrushMode mode in Enum.GetValues<FoliageBrushMode>())
        {
            FoliageBrushMode captured = mode;
            Button button = MakeContextAction(
                mode.ToString(),
                mode switch
                {
                    FoliageBrushMode.Paint => "Replace a region with the selected species and density",
                    FoliageBrushMode.Place => "Place one selected species at the nearest legal point",
                    _ => "Erase all foliage inside the brush radius",
                },
                () =>
                {
                    _foliageBrushArmed = true;
                    _foliageBrush.Mode = captured;
                    RefreshActiveToolCard();
                    SyncToolbar();
                    UpdateStatus();
                });
            _foliageToolButtons.Add(mode, button);
            page.Controls.Add(button);
        }

        page.Controls.Add(MakeSliderPanel("Radius", _foliageRadiusSlider));
        page.Controls.Add(MakeSliderPanel("Density", _foliageDensitySlider));
        return page;
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Rooms;

public enum RoomNavSection
{
    Settings,
    Backgrounds,
    Objects,
    Instances,
    Tilesets,
    Views,
}

/// <summary>
/// Left navigation host: vertical icon tab strip (Settings, Backgrounds, Objects, Tilesets, Views)
/// and container hosting the active section subpanel.
/// </summary>
public sealed class RoomEditorNavigation : Panel
{
    private readonly RoomEditorControl _editor;
    private readonly Panel _rail;
    private readonly Panel _subpanelHost;
    private readonly Dictionary<RoomNavSection, Button> _railButtons = [];
    private readonly RoomSettingsPanel _settingsPanel;
    private readonly RoomBackgroundsPanel _backgroundsPanel;
    private readonly RoomObjectsPanel _objectsPanel;
    private readonly RoomTilesetsPanel _tilesetsPanel;
    private readonly RoomViewsPanel _viewsPanel;
    private RoomNavSection _currentSection = RoomNavSection.Objects;
    private Control? _skybox;
    private Control? _terrain;
    private Control? _cameraViews;
    private Control? _settingsSummary;
    private bool _threeD;

    public event Action<RoomNavSection>? SectionChanged;

    public RoomEditorNavigation(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Surface;

        // Subpanels
        _settingsPanel = new RoomSettingsPanel(editor);
        _backgroundsPanel = new RoomBackgroundsPanel(editor);
        _objectsPanel = new RoomObjectsPanel(editor);
        _tilesetsPanel = new RoomTilesetsPanel(editor);
        _viewsPanel = new RoomViewsPanel(editor);

        // Subpanel Host
        _subpanelHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = EditorChrome.Surface,
        };
        _subpanelHost.Controls.Add(_objectsPanel);
        _subpanelHost.Controls.Add(_settingsPanel);
        _subpanelHost.Controls.Add(_backgroundsPanel);
        _subpanelHost.Controls.Add(_tilesetsPanel);
        _subpanelHost.Controls.Add(_viewsPanel);

        // Vertical Tab Strip (Rail)
        _rail = new Panel
        {
            Dock = DockStyle.Left,
            Width = 100,
            BackColor = EditorChrome.Canvas,
            Padding = new Padding(2, 6, 2, 6),
        };

        AddRailButton(RoomNavSection.Objects, "◇", "Objects");
        AddRailButton(RoomNavSection.Instances, "☷", "Instances");
        AddRailButton(RoomNavSection.Settings, "⚙", "Settings");
        AddRailButton(RoomNavSection.Views, "▣", "Views");
        AddRailButton(RoomNavSection.Backgrounds, "☀", "Backgrounds");
        AddRailButton(RoomNavSection.Tilesets, "▦", "Tilesets");

        Controls.Add(_subpanelHost);
        Controls.Add(_rail);

        SetSection(RoomNavSection.Objects);
    }

    public RoomNavSection CurrentSection => _currentSection;

    public RoomSettingsPanel SettingsPanel => _settingsPanel;

    public RoomBackgroundsPanel BackgroundsPanel => _backgroundsPanel;

    public RoomObjectsPanel ObjectsPanel => _objectsPanel;

    public RoomTilesetsPanel TilesetsPanel => _tilesetsPanel;

    public RoomViewsPanel ViewsPanel => _viewsPanel;

    public IReadOnlyList<string> SectionTitles => _railButtons.Values.Select(button => button.Text.Split('\n')[^1]).ToArray();

    public void SetWorkspacePanels(Control skybox, Control terrain, Control cameraViews, Control settingsSummary)
    {
        _skybox = skybox;
        _terrain = terrain;
        _cameraViews = cameraViews;
        _settingsSummary = settingsSummary;
        foreach (Control panel in new[] { skybox, terrain, cameraViews, settingsSummary })
        {
            panel.Dock = DockStyle.Fill;
            _subpanelHost.Controls.Add(panel);
        }
        SetDimension(_editor.Room.Dimension == Genesis.Runtime.Scene.RoomDimension.ThreeD);
    }

    public void SetDimension(bool threeD)
    {
        bool changed = _threeD != threeD;
        _threeD = threeD;
        _railButtons[RoomNavSection.Backgrounds].Text = "☀\n" + (threeD ? "Skybox" : "Backgrounds");
        _railButtons[RoomNavSection.Tilesets].Text = "▦\n" + (threeD ? "Terrain" : "Tilesets");
        if (changed) _objectsPanel.RefreshLayers();
        if (changed) ShowSection();
    }

    public void SetSection(RoomNavSection section)
    {
        _currentSection = section;

        // Update button styles
        foreach (var (sec, btn) in _railButtons)
        {
            bool active = sec == section;
            btn.BackColor = active ? EditorChrome.Accent : Color.Transparent;
            btn.ForeColor = active ? Color.White : EditorChrome.Muted;
        }

        ShowSection();

        if (section == RoomNavSection.Settings) _settingsPanel.SyncFromRoom();
        else if (section == RoomNavSection.Backgrounds) _backgroundsPanel.SyncFromSelectedSlot();
        else if (section == RoomNavSection.Tilesets) _tilesetsPanel.RefreshLayers();
        else if (section == RoomNavSection.Views) _viewsPanel.SyncFromRoom();

        SectionChanged?.Invoke(section);
    }

    private void ShowSection()
    {
        if (_currentSection is RoomNavSection.Objects or RoomNavSection.Instances)
            _objectsPanel.SetInstancesMode(_currentSection == RoomNavSection.Instances);
        Control active = _currentSection switch
        {
            RoomNavSection.Settings => _settingsSummary ?? _settingsPanel,
            RoomNavSection.Backgrounds => _threeD && _skybox is not null ? _skybox : _backgroundsPanel,
            RoomNavSection.Tilesets => _threeD && _terrain is not null ? _terrain : _tilesetsPanel,
            RoomNavSection.Views => _cameraViews ?? _viewsPanel,
            _ => _objectsPanel,
        };
        foreach (Control panel in _subpanelHost.Controls) panel.Visible = panel == active;
        active.BringToFront();
    }

    private void AddRailButton(RoomNavSection section, string icon, string title)
    {
        Button btn = new()
        {
            Dock = DockStyle.Top,
            Height = 56,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Text = $"{icon}\n{title}",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = EditorChrome.SmallFont,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 4),
        };
        btn.Click += (_, _) => SetSection(section);
        _railButtons[section] = btn;

        // Dock back-to-front
        _rail.Controls.Add(btn);
        _rail.Controls.SetChildIndex(btn, 0);
    }
}

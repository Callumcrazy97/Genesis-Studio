using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private const int ResponsiveBreakpoint = EditorChrome.NarrowWidthBreakpoint;

    private Panel _rightPanel = null!;
    private ToolStrip _modeRail = null!;
    private ToolStripButton _componentsToggle = null!;
    private ToolStripButton _inspectorToggle = null!;
    private bool _narrowLayout;
    private bool _componentsPanelVisible = true;
    private bool _inspectorPanelVisible = true;

    public bool IsNarrowLayout => _narrowLayout;

    private void WireResponsiveChrome(ToolStrip toolbar)
    {
        _componentsToggle = EditorChrome.ToolButton(
            "Tools",
            "Show the active terrain tools",
            () => { },
            toggle: true);
        _componentsToggle.Checked = true;
        _componentsToggle.Alignment = ToolStripItemAlignment.Right;
        _componentsToggle.CheckedChanged += (_, _) =>
        {
            _componentsPanelVisible = _componentsToggle.Checked;
            if (_narrowLayout && _componentsToggle.Checked && _inspectorToggle is not null) _inspectorToggle.Checked = false;
            ApplyResponsiveLayout();
        };

        _inspectorToggle = EditorChrome.ToolButton(
            "Objects",
            "Show Terrain Wizard and Inspector",
            () => { },
            toggle: true);
        _inspectorToggle.Checked = true;
        _inspectorToggle.Alignment = ToolStripItemAlignment.Right;
        _inspectorToggle.CheckedChanged += (_, _) =>
        {
            _inspectorPanelVisible = _inspectorToggle.Checked;
            if (_narrowLayout && _inspectorToggle.Checked) _componentsToggle.Checked = false;
            ApplyResponsiveLayout();
        };

        _componentsToggle.Visible = false;
        _inspectorToggle.Visible = false;
        toolbar.Items.Add(new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right });
        toolbar.Items.Add(_componentsToggle);
        toolbar.Items.Add(_inspectorToggle);

        SizeChanged += (_, _) => ApplyResponsiveLayout();
        HandleCreated += (_, _) => BeginInvoke(ApplyResponsiveLayout);
    }

    private void RegisterResponsivePanels(Panel rightPanel, ToolStrip modeRail)
    {
        _rightPanel = rightPanel;
        _modeRail = modeRail;
    }

    private void ApplyResponsiveLayout()
    {
        if (IsDisposed || ClientSize.Width <= 0 || _rightPanel is null || _toolPanel is null) return;

        bool narrow = ClientSize.Width < ResponsiveBreakpoint;
        bool enteringNarrow = narrow && !_narrowLayout;
        _narrowLayout = narrow;
        _componentsToggle.Visible = _narrowLayout;
        _inspectorToggle.Visible = _narrowLayout;

        if (_narrowLayout)
        {
            if (enteringNarrow)
            {
                _componentsPanelVisible = false;
                _inspectorPanelVisible = false;
                _componentsToggle.Checked = false;
                _inspectorToggle.Checked = false;
            }

            _toolPanel.Visible = _componentsPanelVisible;
            _rightPanel.Visible = _inspectorPanelVisible;
            _modeRail.Visible = true;
        }
        else
        {
            _toolPanel.Visible = true;
            _rightPanel.Visible = true;
            _modeRail.Visible = true;
            _componentsPanelVisible = true;
            _inspectorPanelVisible = true;
            _componentsToggle.Checked = true;
            _inspectorToggle.Checked = true;
        }
    }
}

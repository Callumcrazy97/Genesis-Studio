using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private bool _layingOutTerrain;
    private bool _terrainLayoutQueued;
    private static Color ReferenceSurface => EditorChrome.Surface;
    private float TerrainInterfaceScale => Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);

    private void InstallReferenceLayout(ToolStrip toolbar, ToolStrip rail, Panel right)
    {
        right.BackColor = _toolPanel.BackColor = ReferenceSurface;
        _contextHeader.Visible = false;
        rail.BackColor = EditorChrome.Raised;
        foreach (var pair in _modeButtons)
        {
            pair.Value.Text = ModeCaption(pair.Key);
            pair.Value.AccessibleName = ModeCaption(pair.Key);
            pair.Value.Font = EditorChrome.BaseFont;
        }
        foreach (Control page in _modeHost.Controls)
        {
            page.Name = "TerrainMode" + _modeHost.ModeNames[_modeHost.Controls.IndexOf(page)];
            page.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(page);
        }
        InstallActiveToolCard();
        RestyleReferenceToolbar(toolbar);
        _modeHost.SizeChanged += (_, _) => ApplyInterfaceLayout();
        _modeHost.SizeChanged += (_, _) => QueueTerrainLayout();
        foreach (Control page in _modeHost.Controls) page.SizeChanged += (_, _) => QueueTerrainLayout();
        FontChanged += (_, _) => ApplyInterfaceLayout();
    }

    private void QueueTerrainLayout()
    {
        if (!IsHandleCreated || IsDisposed || _terrainLayoutQueued) return;
        _terrainLayoutQueued = true;
        BeginInvoke(() => { _terrainLayoutQueued = false; if (!IsDisposed) ApplyInterfaceLayout(); });
    }

    private void SyncReferencePalette()
    {
        RefreshActiveToolCard();
        ApplyInterfaceLayout();
    }

    private void RestyleReferenceToolbar(ToolStrip toolbar)
    {
        ToolStripItem[] previous = toolbar.Items.Cast<ToolStripItem>().ToArray();
        toolbar.Items.Clear();
        toolbar.Items.Add(EditorChrome.ToolButton("Frame", "Fit the terrain in the view", FrameTerrain));
        toolbar.Items.Add(EditorChrome.ToolButton("Use in game", "Save this terrain and add it to a 3D Room", ShowTerrainGameGuide));
        foreach (ToolStripItem item in previous.Where(item => item.Text is "Play" or "Stop"))
            toolbar.Items.Add(item);
        ToolStripDropDownButton options = new("Options") { Name = "TerrainOptions" };
        foreach (ToolStripDropDownItem menu in previous.OfType<ToolStripDropDownItem>())
        {
            // Preserve the original menu and its opening handlers so checked/enabled state stays live.
            if (menu.Text is "View" or "Camera" or "Gizmo") options.DropDownItems.Add(menu);
        }
        ToolStripMenuItem modes = new("More terrain tools");
        foreach (TerrainEditorMode mode in new[] { TerrainEditorMode.Paths, TerrainEditorMode.Foliage, TerrainEditorMode.Water, TerrainEditorMode.Environment })
        {
            TerrainEditorMode captured = mode;
            ToolStripMenuItem item = new(ModeCaption(mode)) { Tag = mode };
            item.Click += (_, _) => SetMode(captured);
            modes.DropDownOpening += (_, _) => item.Checked = ActiveMode == captured;
            modes.DropDownItems.Add(item);
        }
        options.DropDownItems.Add(modes);
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Export world map…", "Bake the terrain and its authored features to PNG", ExportWorldMap));
        ToolStripMenuItem panels = new("Panels");
        foreach (var entry in new[] { ("Tools", _componentsToggle), ("Objects and Inspector", _inspectorToggle) })
        {
            ToolStripMenuItem item = new(entry.Item1) { CheckOnClick = true, Checked = entry.Item2.Checked };
            item.CheckedChanged += (_, _) => entry.Item2.Checked = item.Checked;
            entry.Item2.CheckedChanged += (_, _) => item.Checked = entry.Item2.Checked;
            panels.DropDownItems.Add(item);
        }
        options.DropDownItems.Add(panels);
        toolbar.Items.Add(options);
        foreach (ToolStripItem item in previous)
            if (item.Owner is null && item != _componentsToggle && item != _inspectorToggle) item.Dispose();
    }

    public override void ApplyInterfaceLayout()
    {
        if (_layingOutTerrain || _rightPanel is null || _toolPanel is null || _viewport is null) return;
        _layingOutTerrain = true;
        try
        {
            float scale = TerrainInterfaceScale;
            _toolPanel.Padding = Padding.Empty;
            int railWidth=(int)(82 * scale);
            foreach (ToolStripButton mode in _modeButtons.Values)
            {
                mode.Font = EditorChrome.BaseFont;
                mode.Size = new Size(railWidth - 6, EditorChrome.BaseFont.Height + 24);
            }
            _contextHeader.Font = EditorChrome.HeadingFont;
            _contextHeader.Height = _contextHeader.Font.Height + 14;
            if (_activeToolName is not null)
            {
                _activeToolName.Font = EditorChrome.SmallFont;
                _activeToolName.Height = TextRenderer.MeasureText(_activeToolName.Text, _activeToolName.Font,
                    new Size(Math.Max(120, (int)(300 * scale) - 20), int.MaxValue), TextFormatFlags.WordBreak).Height + 14;
            }
            foreach (FlowLayoutPanel page in _modeHost.Controls.OfType<FlowLayoutPanel>()) FitTerrainPage(page);
            ApplyResponsiveLayout();
            RefreshTerrainWorkflowHint();
            _terrainWorkspaceHost?.PerformLayout();
            _toolPanel.PerformLayout();
            _modeHost.PerformLayout();
            foreach (FlowLayoutPanel page in _modeHost.Controls.OfType<FlowLayoutPanel>()) FitTerrainPage(page);
        }
        finally { _layingOutTerrain = false; }
    }

    private void FitTerrainPage(FlowLayoutPanel page)
    {
        page.PerformLayout();
        int width = Math.Max(120, page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
        foreach (Control control in page.Controls)
        {
            int rowWidth = width - control.Margin.Horizontal;
            control.Width = rowWidth;
            if (control is Label label)
            {
                label.MaximumSize = label.MinimumSize = new Size(rowWidth, 0);
                label.AutoSize = true;
                label.Font = control == _generateSummary || control == _pathsSummary || control == _foliageSummary || control == _waterSummary || control == _environmentSummary
                    ? EditorChrome.SmallFont : label.Font;
            }
            else if (control is Button) control.Height = control.Font.Height + 18;
            else if (control is ComboBox combo) combo.ItemHeight = combo.Font.Height + 8;
            else if (control is TableLayoutPanel table)
            {
                foreach (Button button in table.Controls.OfType<Button>())
                    button.Height = button.Font.Height + (table == _paintLayerGrid ? 36 : 18);
            }
            else if (control is FlowLayoutPanel flow)
            {
                flow.MaximumSize = flow.MinimumSize = new Size(rowWidth, 0);
                flow.AutoSize = true;
                if (flow.WrapContents)
                {
                    foreach (Button button in flow.Controls.OfType<Button>())
                    {
                        button.Width = (width - 14) / 2;
                        button.Height = button.Font.Height + 18;
                    }
                }
                else FitTerrainPage(flow);
            }
            else if (control is Panel { Tag: "TerrainSlider" } slider)
                slider.Height = EditorChrome.SmallFont.Height + 40;
        }
        // Setting AutoScrollMinSize enables scrolling even on an auto-sized nested group.
        // Only the outer tool page should own the scrollbar.
        if (page.AutoScroll)
            page.AutoScrollMinSize = new Size(0, page.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => control.Bottom - page.AutoScrollPosition.Y + control.Margin.Bottom + page.Padding.Bottom + 16).DefaultIfEmpty(0).Max());
    }
}

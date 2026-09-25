using System.Drawing;

namespace Genesis.Application.Editors.Suite.Assets;

public partial class ModelViewerControl
{
    private void LayoutSidebar()
    {
        if (_layingOutSidebar || _sidebarFlow is null || _detailsSection is null || _sourceSection is null
            || _hierarchySection is null || _materialsSection is null || _sidebarFlow.Parent is null) return;
        _layingOutSidebar = true;
        _sidebarFlow.SuspendLayout();
        try
        {
            int width = Math.Max(100, _sidebarFlow.ClientSize.Width - _sidebarFlow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            foreach (Control section in _sidebarFlow.Controls) section.Width = width;
            int source = TextHeight(_sourceInfo, width), details = TextHeight(_details, width);
            int minimumTree = Math.Max(4 * _hierarchy.ItemHeight, 110);
            int materials = Math.Clamp(_materials.Items.Count * _materials.ItemHeight + 8, 2 * _materials.ItemHeight + 8, 6 * _materials.ItemHeight + 8);
            _sourceSection.SetContentHeight(source);
            _detailsSection.SetContentHeight(details);
            _materialsSection.SetContentHeight(materials);
            int fixedHeight = _sidebarFlow.Padding.Vertical + _sidebarFlow.Controls.Cast<Control>().Sum(c => c.Margin.Vertical)
                + _sourceSection.Height + _detailsSection.Height + _materialsSection.Height
                + (_hierarchySection.Height - _hierarchySection.Content.Height);
            int tree = Math.Max(minimumTree, _sidebarFlow.ClientSize.Height - fixedHeight);
            _hierarchySection.SetContentHeight(tree);
        }
        finally { _sidebarFlow.ResumeLayout(); _layingOutSidebar = false; }
    }

    private static int TextHeight(Label label, int width)
    {
        var size = TextRenderer.MeasureText(label.Text, label.Font,
            new Size(Math.Max(1, width - label.Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return size.Height + label.Padding.Vertical + 8;
    }

    private void AddSidebarDivider()
    {
        var divider = new Panel { Name = "ModelViewerDivider", Dock = DockStyle.Right, Width = 6,
            Cursor = Cursors.VSplit, BackColor = EditorChrome.Border };
        int origin = 0; float originalWidth = 0;
        divider.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            origin = divider.PointToScreen(e.Location).X; originalWidth = Body.ColumnStyles[0].Width; divider.Capture = true;
        };
        divider.MouseMove += (_, e) =>
        {
            if (!divider.Capture || e.Button != MouseButtons.Left) return;
            SetSidebarWidth((int)(originalWidth + divider.PointToScreen(e.Location).X - origin));
        };
        divider.MouseUp += (_, _) => divider.Capture = false;
        LeftPanel.Controls.Add(divider); divider.BringToFront();
    }

    public void SetSidebarWidth(int width)
    {
        Body.ColumnStyles[0].Width = Math.Clamp(width, 200, Math.Max(200, Body.Width / 2));
        LayoutSidebar();
    }
}

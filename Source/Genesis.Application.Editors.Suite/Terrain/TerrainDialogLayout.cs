using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

internal static class TerrainDialogLayout
{
    public static TableLayoutPanel Fields(Control parent, IEnumerable<(string Caption, Control Field)> rows)
    {
        TableLayoutPanel table = new()
        {
            AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(12),
            BackColor = EditorChrome.Surface,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach ((string caption, Control field) in rows)
        {
            if (!string.IsNullOrWhiteSpace(caption))
                table.Controls.Add(new Label { Text = caption, AutoSize = true, Dock = DockStyle.Top,
                    ForeColor = EditorChrome.Muted, Margin = new Padding(0, 4, 0, 4) });
            field.Dock = DockStyle.Top;
            field.Margin = new Padding(0, 0, 0, 12);
            EditorChrome.StyleField(field);
            table.Controls.Add(field);
        }
        parent.Controls.Add(table);
        bool fitting = false;
        void Fit()
        {
            if (fitting || table.IsDisposed) return;
            fitting = true;
            try
            {
                int width = Math.Max(160, parent.ClientSize.Width - table.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
                foreach (Control field in table.Controls)
                {
                    if (field is Label or CheckBox) field.MaximumSize = new Size(width, 0);
                    if (field is ComboBox combo) combo.ItemHeight = combo.Font.Height + 8;
                    if (field is Button button) button.MinimumSize = new Size(0, button.Font.Height + 18);
                }
            }
            finally { fitting = false; }
        }
        parent.SizeChanged += (_, _) => Fit();
        table.FontChanged += (_, _) => Fit();
        table.Layout += (_, _) => Fit();
        Fit();
        return table;
    }

    public static void Actions(Form dialog, string acceptText = "Apply")
    {
        FlowLayoutPanel footer = new()
        {
            AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10), BackColor = EditorChrome.Surface,
        };
        Button accept = new() { Text = acceptText, AutoSize = true, DialogResult = DialogResult.OK };
        Button cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        foreach (Button button in new[] { accept, cancel })
        {
            EditorChrome.StyleField(button);
            button.MinimumSize = new Size(0, button.Font.Height + 18);
            button.FontChanged += (_, _) => button.MinimumSize = new Size(0, button.Font.Height + 18);
            footer.Controls.Add(button);
        }
        dialog.Controls.Add(footer);
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
    }

    public static void Settings(Form dialog, object settings, string note, string acceptText)
    {
        Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
        Control fields = Inspector.InspectorBuilder.BuildForObject(settings, note, inline: true);
        scroll.Controls.Add(fields);
        dialog.Controls.Add(scroll);
        Actions(dialog, acceptText);
    }
}

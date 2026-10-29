using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Shaders;

namespace Genesis.Application.Editors.Suite.Assets.ShaderNodes;

/// <summary>
/// The node view's Add menu: a search box over the node catalog. Typing filters by title,
/// category and description; Enter, a click or a double-click adds the highlighted node.
/// </summary>
internal sealed class ShaderNodeSearchMenu : ToolStripDropDown
{
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly List<ShaderGraphNodeDefinition> _shown = [];
    private readonly Func<ShaderGraphNodeDefinition, bool> _accepts;

    public ShaderNodeSearchMenu(Func<ShaderGraphNodeDefinition, bool>? accepts = null)
    {
        _accepts = accepts ?? (_ => true);
        Padding = Padding.Empty;
        AutoClose = true;
        Panel panel = new()
        {
            BackColor = EditorChrome.Surface,
            Padding = new Padding(6),
            Size = new Size(320, 360),
            Name = "ShaderNodeSearch",
        };
        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search nodes (add, texture, fresnel…)", AccessibleName = "Search nodes" };
        EditorChrome.StyleField(_search);
        _list = new ListBox
        {
            BackColor = EditorChrome.Raised,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ForeColor = EditorChrome.Text,
            IntegralHeight = false,
            ItemHeight = Math.Max(34, EditorChrome.BaseFont.Height + EditorChrome.SmallFont.Height + 8),
            AccessibleName = "Nodes",
        };
        _list.DrawItem += DrawItem;
        _list.MouseClick += (_, _) => Choose();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Choose(); e.Handled = true; }
        };
        _search.TextChanged += (_, _) => Refill();
        _search.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Down: MoveSelection(1); e.Handled = true; break;
                case Keys.Up: MoveSelection(-1); e.Handled = true; break;
                case Keys.Enter: Choose(); e.Handled = e.SuppressKeyPress = true; break;
                case Keys.Escape: Close(ToolStripDropDownCloseReason.Keyboard); e.Handled = true; break;
            }
        };
        panel.Controls.Add(_list);
        panel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = EditorChrome.Surface });
        panel.Controls.Add(_search);
        ToolStripControlHost host = new(panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = panel.Size };
        Items.Add(host);
        Opened += (_, _) => _search.Focus();
        Refill();
    }

    /// <summary>The node chosen from the menu.</summary>
    public event Action<ShaderGraphNodeDefinition>? Chosen;

    public IReadOnlyList<ShaderGraphNodeDefinition> Shown => _shown;

    public string SearchText
    {
        get => _search.Text;
        set => _search.Text = value;
    }

    private void Refill()
    {
        _shown.Clear();
        _shown.AddRange(ShaderGraphCatalog.Search(_search.Text).Where(_accepts));
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (ShaderGraphNodeDefinition definition in _shown) _list.Items.Add(definition.Title);
        _list.EndUpdate();
        if (_shown.Count > 0) _list.SelectedIndex = 0;
    }

    private void MoveSelection(int delta)
    {
        if (_shown.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _shown.Count - 1);
    }

    private void Choose()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _shown.Count) return;
        ShaderGraphNodeDefinition chosen = _shown[index];
        Close(ToolStripDropDownCloseReason.ItemClicked);
        Chosen?.Invoke(chosen);
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        ShaderGraphNodeDefinition definition = _shown[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (SolidBrush background = new(selected ? EditorChrome.AccentSoft : EditorChrome.Raised))
            e.Graphics.FillRectangle(background, e.Bounds);
        Rectangle title = new(e.Bounds.X + 8, e.Bounds.Y + 3, e.Bounds.Width - 16, EditorChrome.BaseFont.Height);
        TextRenderer.DrawText(e.Graphics, definition.Title, EditorChrome.BaseFont, title, EditorChrome.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, definition.Category, EditorChrome.SmallFont, title, EditorChrome.Muted,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        Rectangle detail = new(e.Bounds.X + 8, title.Bottom + 1, e.Bounds.Width - 16, EditorChrome.SmallFont.Height + 2);
        TextRenderer.DrawText(e.Graphics, definition.Description, EditorChrome.SmallFont, detail, EditorChrome.Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

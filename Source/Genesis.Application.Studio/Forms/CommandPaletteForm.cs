using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Commands;
using Genesis.Application.Core.UI;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>A keyboard-first command picker. It returns an ID; only the catalog executes actions.</summary>
internal sealed class CommandPaletteForm : DpiAwareForm
{
    private readonly StudioCommandCatalog<ShellCommandContext> _catalog;
    private readonly ShellCommandContext _context;
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Type a command, category or shortcut…" };
    private readonly ListView _results = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _description = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _run = new() { Text = "Run Command", AutoSize = true };
    private readonly Button _close = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

    public CommandPaletteForm(StudioCommandCatalog<ShellCommandContext> catalog, ShellCommandContext context)
    {
        _catalog = catalog; _context = context;
        Text = "Studio Commands & Shortcuts";
        ClientSize = new Size(820, 500);
        MinimumSize = new Size(560, 340);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false; MaximizeBox = false;
        _search.AccessibleName = "Search Studio commands";
        _results.AccessibleName = "Matching Studio commands";
        _results.AccessibleDescription = "Commands remain visible when unavailable; the final column explains why.";
        _description.AccessibleName = "Selected command description";
        _run.AccessibleName = "Run selected command";
        _results.Columns.Add("Command"); _results.Columns.Add("Shortcut"); _results.Columns.Add("Availability");
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label help = new()
        {
            Text = "Search shell commands · ↑/↓ selects · Enter runs · Escape closes · local editor shortcuts remain local",
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8),
        };
        FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_close); buttons.Controls.Add(_run);
        layout.Controls.Add(_search, 0, 0); layout.Controls.Add(help, 0, 1);
        layout.Controls.Add(_results, 0, 2); layout.Controls.Add(_description, 0, 3); layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        CancelButton = _close;
        AcceptButton = _run;
        _search.TextChanged += (_, _) => RefreshMatches();
        _results.SelectedIndexChanged += (_, _) => UpdateSelection();
        _results.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _results.HitTest(e.Location).Item is not null) ChooseSelection();
        };
        _results.Resize += (_, _) => SizeColumns();
        _run.Click += (_, _) => ChooseSelection();
        Shown += (_, _) => { SizeColumns(); RefreshMatches(); _search.Focus(); };
        ThemeService.Apply(this);
        RefreshMatches();
    }

    public string? SelectedCommandId { get; private set; }
    internal TextBox SearchBox => _search;
    internal ListView Results => _results;
    internal Button RunButton => _run;

    private void RefreshMatches()
    {
        _results.BeginUpdate();
        try
        {
            _results.Items.Clear();
            foreach (CommandSearchResult<ShellCommandContext> result in _catalog.Search(_search.Text, _context)
                         .Where(result => result.Command.Id != "studio.commands"))
            {
                StudioCommand<ShellCommandContext> command = result.Command;
                ListViewItem item = new(command.Category + " · " + command.Title) { Tag = command.Id };
                item.SubItems.Add(string.Join(" / ", command.Shortcuts.Select(shortcut => shortcut.DisplayText)));
                item.SubItems.Add(result.State.Enabled ? "Available" : result.State.Reason);
                item.ForeColor = result.State.Enabled ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted;
                _results.Items.Add(item);
            }
            if (_results.Items.Count > 0) _results.Items[0].Selected = true;
        }
        finally { _results.EndUpdate(); }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_results.SelectedItems.Count == 0)
        {
            _description.Text = "No matching commands. Try a title, category or shortcut such as Save, Project or Ctrl+S.";
            _run.Enabled = false;
            return;
        }
        string id = (string)_results.SelectedItems[0].Tag!;
        CommandAvailability state = _catalog.GetAvailability(id, _context);
        _run.Enabled = state.Enabled;
        _description.Text = _catalog.Find(id)!.Description + (state.Enabled ? string.Empty : "\nUnavailable: " + state.Reason);
    }

    internal void ChooseSelection()
    {
        UpdateSelection();
        if (!_run.Enabled || _results.SelectedItems.Count == 0) return;
        SelectedCommandId = (string)_results.SelectedItems[0].Tag!;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (keyData == Keys.Enter) { ChooseSelection(); return true; }
        if (keyData is Keys.Down or Keys.Up or Keys.PageDown or Keys.PageUp)
        {
            if (_results.Items.Count > 0)
            {
                int current = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : 0;
                int step = keyData is Keys.PageDown or Keys.PageUp ? 8 : 1;
                if (keyData is Keys.Up or Keys.PageUp) step = -step;
                int next = Math.Clamp(current + step, 0, _results.Items.Count - 1);
                if (_results.SelectedItems.Count > 0) _results.SelectedItems[0].Selected = false;
                _results.Items[next].Selected = true; _results.Items[next].EnsureVisible();
            }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SizeColumns()
    {
        int width = Math.Max(1, _results.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        _results.Columns[0].Width = (int)(width * .42);
        _results.Columns[1].Width = (int)(width * .23);
        _results.Columns[2].Width = width - _results.Columns[0].Width - _results.Columns[1].Width;
    }
}

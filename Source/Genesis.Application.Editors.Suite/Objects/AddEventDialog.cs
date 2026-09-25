using System.Drawing;
using System.Windows.Forms;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// Pick an event to add to an object, grouped the way the catalogue groups them.
/// </summary>
/// <remarks>
/// There are 39 events across 8 categories. A flat list would be a wall, and the old editor's
/// answer — show every one permanently in the tree — meant scrolling past 35 empty rows to reach
/// the four an object actually uses. So the editor shows only the events in use, and this wizard is
/// how you add another.
///
/// Events already on the object are shown greyed with a marker rather than hidden, because "have I
/// already got a Step event?" is a question you ask while looking at this list.
/// </remarks>
public sealed class AddEventDialog : DpiAwareForm
{
    private readonly TreeView _tree = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _template = new();
    private readonly Label _description = new();
    private readonly Button _add = new();
    private readonly IReadOnlyCollection<string> _existing;

    public AddEventDialog(IReadOnlyCollection<string> existingEventIds)
    {
        _existing = existingEventIds ?? [];

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = EditorChrome.Canvas;
        ClientSize = new Size(560, 578);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Add Event";

        Label heading = new()
        {
            AutoSize = true,
            Font = new Font(EditorChrome.BaseFont.FontFamily, 12f, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Location = new Point(20, 16),
            Text = "Choose an event",
        };
        Controls.Add(heading);

        _tree.BackColor = EditorChrome.Surface;
        _tree.BorderStyle = BorderStyle.FixedSingle;
        _tree.ForeColor = EditorChrome.Text;
        _tree.HideSelection = false;
        _tree.ItemHeight = DpiLayout.Scale(this, 22);
        _search.Location = new Point(20, 50);
        _search.Size = new Size(520, 30);
        _search.PlaceholderText = "Search events, categories, or descriptions…";
        _search.TextChanged += (_, _) => Populate();
        EditorChrome.StyleField(_search);
        Controls.Add(_search);

        _tree.Location = new Point(20, 88);
        _tree.ShowLines = false;
        _tree.ShowRootLines = false;
        _tree.Size = new Size(520, 320);
        _tree.AfterSelect += (_, _) => SyncSelection();
        _tree.DoubleClick += (_, _) =>
        {
            if (_add.Enabled) Accept();
        };
        Controls.Add(_tree);

        _description.AutoSize = false;
        _description.ForeColor = EditorChrome.Muted;
        _description.Font = EditorChrome.SmallFont;
        _description.Location = new Point(22, 416);
        _description.Size = new Size(516, 40);
        Controls.Add(_description);

        _add.Enabled = false;
        Controls.Add(new Label
        {
            AutoSize = false,
            ForeColor = EditorChrome.Muted,
            Location = new Point(22, 468),
            Size = new Size(118, 28),
            Text = "Starting content",
            TextAlign = ContentAlignment.MiddleLeft,
        });
        _template.DropDownStyle = ComboBoxStyle.DropDownList;
        _template.Items.AddRange(["Helpful starter", "Empty event"]);
        _template.SelectedIndex = 0;
        _template.Location = new Point(142, 468);
        _template.Size = new Size(190, 30);
        EditorChrome.StyleField(_template);
        Controls.Add(_template);

        _add.Location = new Point(316, 520);
        _add.Size = new Size(105, 34);
        _add.Text = "Add Event";
        EditorChrome.StyleField(_add);
        _add.Click += (_, _) => Accept();
        Controls.Add(_add);

        Button cancel = new()
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(431, 520),
            Size = new Size(105, 34),
            Text = "Cancel",
        };
        EditorChrome.StyleField(cancel);
        Controls.Add(cancel);

        AcceptButton = _add;
        CancelButton = cancel;

        Populate();
    }

    /// <summary>The chosen event id, once the dialog closes with OK.</summary>
    public string? SelectedEventId { get; private set; }

    /// <summary>Whether the editor should seed the event with its useful context-specific starter.</summary>
    public bool UseStarterCode => _template.SelectedIndex != 1;

    /// <summary>Select an event by id. Used by tests to drive the same path a click takes.</summary>
    public bool Select(string eventId)
    {
        foreach (TreeNode category in _tree.Nodes)
        {
            foreach (TreeNode node in category.Nodes)
            {
                if (node.Tag as string == eventId)
                {
                    category.Expand();
                    _tree.SelectedNode = node;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Confirm the current selection. Returns false when nothing addable is selected.</summary>
    public bool Accept()
    {
        if (_tree.SelectedNode?.Tag is not string id || _existing.Contains(id)) return false;

        SelectedEventId = id;
        DialogResult = DialogResult.OK;
        Close();
        return true;
    }

    private void Populate()
    {
        string? selected = _tree.SelectedNode?.Tag as string;
        string query = _search.Text.Trim();
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            foreach (string category in ObjectEventCatalog.Categories)
            {
                TreeNode group = new(category) { ForeColor = EditorChrome.Text };
                foreach (ObjectEventDefinition definition in ObjectEventCatalog.InCategory(category))
                {
                    if (query.Length > 0
                        && !definition.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !definition.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !definition.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !category.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    bool already = _existing.Contains(definition.Id);
                    TreeNode node = new(already ? definition.Label + "   (already added)" : definition.Label)
                    {
                        Tag = definition.Id,
                        ForeColor = already ? EditorChrome.Muted : EditorChrome.Text,
                    };
                    group.Nodes.Add(node);
                    if (string.Equals(selected, definition.Id, StringComparison.OrdinalIgnoreCase))
                        _tree.SelectedNode = node;
                }

                if (group.Nodes.Count > 0) _tree.Nodes.Add(group);
            }

            // Expand the categories a designer reaches for first; Alarms and User Events are long
            // and rarely the reason someone opened this dialog.
            foreach (TreeNode group in _tree.Nodes)
            {
                if (query.Length > 0 || group.Text is "Create" or "Step" or "Draw") group.Expand();
            }
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private void SyncSelection()
    {
        if (_tree.SelectedNode?.Tag is not string id)
        {
            _add.Enabled = false;
            _description.Text = string.Empty;
            return;
        }

        ObjectEventDefinition? definition = ObjectEventCatalog.Find(id);
        bool already = _existing.Contains(id);
        _add.Enabled = !already;
        _description.Text = already
            ? $"'{definition?.Label}' is already on this object — select it in the editor instead."
            : definition?.Description ?? string.Empty;
        _description.ForeColor = already ? EditorChrome.Warning : EditorChrome.Muted;
    }
}

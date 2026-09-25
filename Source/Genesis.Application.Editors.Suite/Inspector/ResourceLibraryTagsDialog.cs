using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Inspector;

/// <summary>Edits a draft only. The caller owns conflict detection, persistence and undo.</summary>
public sealed class ResourceLibraryTagsDialog : DpiAwareForm
{
    private readonly TextBox _input = new();
    private readonly TextBox _suggestionSearch = new();
    private readonly ListBox _suggestions = new();
    private readonly Label _validation = new();
    private readonly Button _apply = new();
    private readonly IReadOnlyList<string> _vocabulary;
    private IReadOnlyList<string> _draft = [];
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> SelectedTags => _draft;
    internal TextBox TagInput => _input;
    internal Button ApplyButton => _apply;

    public ResourceLibraryTagsDialog(string resourceName, IReadOnlyList<string> tags, IEnumerable<string> vocabulary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray();
        Text = "Library Tags — " + resourceName;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 460);
        MinimumSize = new Size(600, 410);
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = EditorChrome.Surface;
        ForeColor = EditorChrome.Text;
        Padding = new Padding(14);

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Label explanation = new()
        {
            Dock = DockStyle.Fill,
            Text = "Organise " + resourceName + " using library tags. Enter one tag per line, or separate with commas.\nThese do not change animation tags, gameplay tags, resource names or image usage flags.",
            ForeColor = EditorChrome.Text,
        };
        layout.Controls.Add(explanation, 0, 0); layout.SetColumnSpan(explanation, 2);
        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.AcceptsTab = false;
        _input.ScrollBars = ScrollBars.Vertical;
        _input.MaxLength = 4096;
        _input.Dock = DockStyle.Fill;
        _input.AccessibleName = "Library tags, one per line";
        _input.Text = string.Join(Environment.NewLine, tags);
        EditorChrome.StyleField(_input);
        layout.Controls.Add(_input, 0, 1);

        TableLayoutPanel suggestions = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        suggestions.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        suggestions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        suggestions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _suggestionSearch.Dock = DockStyle.Fill;
        _suggestionSearch.PlaceholderText = "Find existing project tag…";
        _suggestionSearch.AccessibleName = "Filter existing library tags";
        EditorChrome.StyleField(_suggestionSearch);
        _suggestions.Dock = DockStyle.Fill;
        _suggestions.IntegralHeight = false;
        _suggestions.AccessibleName = "Existing project library tags";
        EditorChrome.StyleField(_suggestions);
        Button add = new() { Text = "Add selected tag", Dock = DockStyle.Fill, Enabled = false };
        EditorChrome.StyleField(add);
        _suggestions.SelectedIndexChanged += (_, _) => add.Enabled = _suggestions.SelectedItem is string;
        _suggestions.DoubleClick += (_, _) => AddSuggestion();
        add.Click += (_, _) => AddSuggestion();
        _suggestionSearch.TextChanged += (_, _) => FilterSuggestions();
        suggestions.Controls.Add(_suggestionSearch, 0, 0);
        suggestions.Controls.Add(_suggestions, 0, 1);
        suggestions.Controls.Add(add, 0, 2);
        layout.Controls.Add(suggestions, 1, 1);

        _validation.Dock = DockStyle.Fill;
        _validation.AccessibleName = "Library tag validation";
        _validation.ForeColor = EditorChrome.Muted;
        layout.Controls.Add(_validation, 0, 2); layout.SetColumnSpan(_validation, 2);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _apply.Text = "Apply tags"; _apply.AutoSize = true; _apply.Height = 30;
        EditorChrome.StyleField(_apply);
        _apply.Click += (_, _) => { if (ValidateDraft()) { DialogResult = DialogResult.OK; Close(); } };
        Button cancel = new() { Text = "Cancel", AutoSize = true, Height = 30, DialogResult = DialogResult.Cancel };
        EditorChrome.StyleField(cancel);
        actions.Controls.Add(_apply); actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 3); layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = _apply; CancelButton = cancel;
        _input.TextChanged += (_, _) => ValidateDraft();
        FilterSuggestions(); ValidateDraft();
        Shown += (_, _) => _input.Focus();
    }

    private void FilterSuggestions()
    {
        _suggestions.BeginUpdate();
        try
        {
            _suggestions.Items.Clear();
            foreach (string tag in _vocabulary.Where(tag => tag.Contains(_suggestionSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(256))
                _suggestions.Items.Add(tag);
        }
        finally { _suggestions.EndUpdate(); }
    }

    private void AddSuggestion()
    {
        if (_suggestions.SelectedItem is not string tag || !ValidateDraft()) return;
        if (_draft.Contains(tag, StringComparer.OrdinalIgnoreCase)) { _input.Focus(); return; }
        // Append through SelectedText, so the native draft textbox retains an Undo operation.
        _input.SelectionStart = _input.TextLength; _input.SelectionLength = 0;
        _input.SelectedText = (_input.TextLength == 0 || _input.Text.EndsWith('\n') ? "" : Environment.NewLine) + tag;
        _input.Focus();
    }

    private bool ValidateDraft()
    {
        try
        {
            _draft = ResourceLibraryTags.ParseInput(_input.Text);
            _validation.Text = $"{_draft.Count} / {ResourceLibraryTags.MaximumTags} tags · at most {ResourceLibraryTags.MaximumTagLength} characters each.\nBlank removes all library tags. Escape/Cancel leaves the resource unchanged.";
            _apply.Enabled = true;
            return true;
        }
        catch (ArgumentException error)
        {
            _validation.Text = error.Message;
            _apply.Enabled = false;
            return false;
        }
    }
}

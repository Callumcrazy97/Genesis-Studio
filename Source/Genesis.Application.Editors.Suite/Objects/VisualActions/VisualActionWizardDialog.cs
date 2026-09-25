using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.Inspector;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

/// <summary>Search, configure, place, and optionally save one reusable PGSL action block.</summary>
public sealed class VisualActionWizardDialog : DpiAwareForm
{
    private readonly string _projectRoot;
    private readonly VisualActionPresetStore _presets;
    private readonly IReadOnlyList<VisualActionCommand> _commands;
    private readonly TextBox _search = new();
    private readonly ComboBox _category = new();
    private readonly ListBox _results = new();
    private readonly Label _title = new();
    private readonly Label _signature = new();
    private readonly Label _description = new();
    private readonly TableLayoutPanel _parameters = new();
    private readonly TextBox _actionName = new();
    private readonly ComboBox _placement = new();
    private readonly CheckBox _savePreset = new();
    private readonly TextBox _presetName = new();
    private readonly Button _create = new();
    private readonly Label _validation = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private VisualActionCommand? _selected;

    public VisualActionWizardDialog(
        string projectRoot,
        VisualActionPresetStore presets,
        string? preselectCommand = null)
    {
        _projectRoot = projectRoot;
        _presets = presets;
        _commands = VisualActionCatalog.GetCommands();

        Text = "Add Visual Action";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 660);
        MinimumSize = new Size(760, 520);
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;

        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 360,
            SplitterWidth = 5,
        };
        BuildSearch(split.Panel1);
        BuildConfiguration(split.Panel2);
        Controls.Add(split);

        PopulateCategories();
        FilterCommands();
        if (!string.IsNullOrWhiteSpace(preselectCommand)) SelectCommand(preselectCommand);
    }

    public VisualActionTemplate? SelectedTemplate { get; private set; }

    public VisualActionPlacement Placement => _placement.SelectedItem is VisualActionPlacement placement
        ? placement
        : VisualActionPlacement.Bottom;

    public bool SaveAsPreset => _savePreset.Checked;

    public bool SelectCommand(string commandName)
    {
        VisualActionCommand? command = _commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, commandName, StringComparison.OrdinalIgnoreCase));
        if (command is null) return false;
        if (!_results.Items.Contains(command))
        {
            _search.Clear();
            _category.SelectedIndex = 0;
            FilterCommands();
        }
        _results.SelectedItem = command;
        return ReferenceEquals(_results.SelectedItem, command);
    }

    public bool SetArgument(string name, string value)
    {
        if (_selected?.Parameters.Any(parameter =>
                string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)) != true)
        {
            return false;
        }
        _values[name] = value;
        return true;
    }

    public bool SetActionName(string name)
    {
        _actionName.Text = name ?? string.Empty;
        return true;
    }

    public bool CreateSelection()
    {
        if (_selected is null) return false;
        string name = _actionName.Text.Trim();
        if (name.Length == 0)
        {
            _validation.Text = "Give this action a short name.";
            return false;
        }

        List<VisualActionParameter> parameters = _selected.Parameters.Select(parameter =>
            parameter with
            {
                Value = _values.GetValueOrDefault(parameter.Name, parameter.Value),
            }).ToList();
        VisualActionTemplate template = new(
            name,
            _selected.Name,
            _selected.Category,
            _selected.Description,
            parameters);
        if (_savePreset.Checked)
        {
            string presetName = _presetName.Text.Trim();
            if (presetName.Length == 0)
            {
                _validation.Text = "Enter a name for the reusable preset.";
                return false;
            }
            try
            {
                _presets.Save(template with { Name = presetName });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _validation.Text = "Could not save the preset: " + exception.Message;
                return false;
            }
        }

        SelectedTemplate = template;
        DialogResult = DialogResult.OK;
        Close();
        return true;
    }

    private void BuildSearch(Control parent)
    {
        parent.BackColor = EditorChrome.Surface;
        parent.Padding = new Padding(14);
        Panel filters = new()
        {
            Dock = DockStyle.Top,
            Height = 78,
        };
        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "Search actions, categories, or parameters…";
        _search.Height = 30;
        _search.TextChanged += (_, _) => FilterCommands();
        EditorChrome.StyleField(_search);
        _category.Dock = DockStyle.Bottom;
        _category.DropDownStyle = ComboBoxStyle.DropDownList;
        _category.SelectedIndexChanged += (_, _) => FilterCommands();
        EditorChrome.StyleField(_category);
        filters.Controls.Add(_category);
        filters.Controls.Add(_search);

        _results.Dock = DockStyle.Fill;
        _results.DisplayMember = nameof(VisualActionCommand.Name);
        _results.IntegralHeight = false;
        _results.BackColor = EditorChrome.Canvas;
        _results.ForeColor = EditorChrome.Text;
        _results.BorderStyle = BorderStyle.FixedSingle;
        _results.SelectedIndexChanged += (_, _) => SelectResult();
        _results.DoubleClick += (_, _) =>
        {
            if (_selected is { Parameters.Count: 0 }) CreateSelection();
        };

        Label hint = new()
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            ForeColor = EditorChrome.Muted,
            Padding = new Padding(3, 10, 0, 0),
            Text = "Only implemented commands are offered. Hand-written PGSL remains untouched.",
        };
        parent.Controls.Add(_results);
        parent.Controls.Add(hint);
        parent.Controls.Add(filters);
        _results.BringToFront();
    }

    private void BuildConfiguration(Control parent)
    {
        parent.BackColor = EditorChrome.Canvas;
        parent.Padding = new Padding(18);

        Panel header = new() { Dock = DockStyle.Top, Height = 112 };
        _title.Dock = DockStyle.Top;
        _title.Height = 32;
        _title.Font = new Font(EditorChrome.BaseFont.FontFamily, 13f, FontStyle.Bold);
        _title.ForeColor = EditorChrome.Text;
        _title.Text = "Choose an action";
        _signature.Dock = DockStyle.Top;
        _signature.Height = 28;
        _signature.Font = EditorChrome.CodeFont;
        _signature.ForeColor = EditorChrome.Accent;
        _description.Dock = DockStyle.Fill;
        _description.AutoEllipsis = true;
        _description.ForeColor = EditorChrome.Muted;
        header.Controls.Add(_description);
        header.Controls.Add(_signature);
        header.Controls.Add(_title);

        _parameters.AutoScroll = true;
        _parameters.AutoSize = false;
        _parameters.ColumnCount = 2;
        _parameters.Dock = DockStyle.Fill;
        _parameters.Padding = new Padding(0, 8, 8, 8);
        _parameters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _parameters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Panel options = new()
        {
            Dock = DockStyle.Bottom,
            Height = 164,
            Padding = new Padding(0, 10, 0, 0),
        };
        TableLayoutPanel optionGrid = new()
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Height = 94,
        };
        optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _actionName.Dock = DockStyle.Fill;
        EditorChrome.StyleField(_actionName);
        _placement.Dock = DockStyle.Fill;
        _placement.DropDownStyle = ComboBoxStyle.DropDownList;
        _placement.Items.AddRange(Enum.GetValues<VisualActionPlacement>().Cast<object>().ToArray());
        _placement.SelectedItem = VisualActionPlacement.Bottom;
        EditorChrome.StyleField(_placement);
        optionGrid.Controls.Add(Caption("Action name"), 0, 0);
        optionGrid.Controls.Add(_actionName, 1, 0);
        optionGrid.Controls.Add(Caption("Insert"), 0, 1);
        optionGrid.Controls.Add(_placement, 1, 1);

        FlowLayoutPanel presetRow = new()
        {
            Dock = DockStyle.Top,
            Height = 34,
            WrapContents = false,
        };
        _savePreset.AutoSize = true;
        _savePreset.Text = "Save as reusable preset";
        _savePreset.CheckedChanged += (_, _) =>
        {
            _presetName.Enabled = _savePreset.Checked;
            if (_savePreset.Checked && _presetName.TextLength == 0) _presetName.Text = _actionName.Text;
        };
        _presetName.Enabled = false;
        _presetName.PlaceholderText = "Preset name";
        _presetName.Width = 230;
        EditorChrome.StyleField(_presetName);
        presetRow.Controls.Add(_savePreset);
        presetRow.Controls.Add(_presetName);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 38,
            WrapContents = false,
        };
        _create.AutoSize = true;
        _create.Enabled = false;
        _create.Text = "Add Action";
        _create.Click += (_, _) => CreateSelection();
        Button cancel = new() { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };
        EditorChrome.StyleField(_create);
        EditorChrome.StyleField(cancel);
        _validation.AutoEllipsis = true;
        _validation.Dock = DockStyle.Fill;
        _validation.ForeColor = EditorChrome.Error;
        _validation.TextAlign = ContentAlignment.MiddleLeft;
        buttons.Controls.Add(_create);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_validation);
        options.Controls.Add(buttons);
        options.Controls.Add(presetRow);
        options.Controls.Add(optionGrid);

        parent.Controls.Add(_parameters);
        parent.Controls.Add(options);
        parent.Controls.Add(header);
        _parameters.BringToFront();
        AcceptButton = _create;
        CancelButton = cancel;
    }

    private void PopulateCategories()
    {
        _category.Items.Clear();
        _category.Items.Add("All categories");
        foreach (string category in _commands.Select(command => command.Category)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            _category.Items.Add(category);
        }
        _category.SelectedIndex = 0;
    }

    private void FilterCommands()
    {
        string query = _search.Text.Trim();
        string? category = _category.SelectedIndex > 0 ? _category.SelectedItem?.ToString() : null;
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        VisualActionCommand[] filtered = _commands.Where(command =>
                (category is null || command.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                && terms.All(term => Matches(command, term)))
            .ToArray();
        _results.BeginUpdate();
        _results.Items.Clear();
        _results.Items.AddRange(filtered.Cast<object>().ToArray());
        _results.EndUpdate();
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
        else SelectResult();
    }

    private static bool Matches(VisualActionCommand command, string term) =>
        command.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || command.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
        || command.Signature.Contains(term, StringComparison.OrdinalIgnoreCase)
        || command.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || IsSubsequence(command.Name, term);

    private static bool IsSubsequence(string candidate, string term)
    {
        int matched = 0;
        foreach (char character in candidate)
        {
            if (matched < term.Length
                && char.ToUpperInvariant(character) == char.ToUpperInvariant(term[matched]))
            {
                matched++;
            }
        }
        return matched == term.Length;
    }

    private void SelectResult()
    {
        _selected = _results.SelectedItem as VisualActionCommand;
        _values.Clear();
        _parameters.SuspendLayout();
        foreach (Control control in _parameters.Controls.Cast<Control>().ToArray()) control.Dispose();
        _parameters.Controls.Clear();
        _parameters.RowStyles.Clear();
        _parameters.RowCount = 0;
        if (_selected is null)
        {
            _title.Text = "No matching actions";
            _signature.Text = string.Empty;
            _description.Text = "Try a broader search or choose another category.";
            _create.Enabled = false;
            _parameters.ResumeLayout(true);
            return;
        }

        _title.Text = _selected.Name;
        _signature.Text = _selected.Signature;
        _description.Text = _selected.Description;
        _actionName.Text = Humanize(_selected.Name);
        _create.Enabled = true;
        int row = 0;
        foreach (VisualActionParameter parameter in _selected.Parameters)
        {
            _values[parameter.Name] = parameter.Value;
            _parameters.RowCount++;
            _parameters.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Label label = Caption(Humanize(parameter.Name));
            Control drawer = PropertyDrawerRegistry.CreateControl(new PropertyDrawerContext(
                parameter.Name,
                typeof(string),
                parameter.Value,
                value => _values[parameter.Name] = Convert.ToString(value) ?? string.Empty,
                Choices: parameter.Choices,
                AssetKind: parameter.AssetKind,
                ProjectRoot: _projectRoot,
                DialogOwner: this,
                ControlName: "ActionArgument_" + parameter.Name,
                Description: $"PGSL expression for {_selected.Name}.{parameter.Name}"));
            StyleDrawer(drawer);
            drawer.Dock = DockStyle.Fill;
            drawer.Margin = new Padding(0, 4, 0, 4);
            _parameters.Controls.Add(label, 0, row);
            _parameters.Controls.Add(drawer, 1, row);
            row++;
        }
        if (_selected.Parameters.Count == 0)
        {
            _parameters.RowCount = 1;
            _parameters.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Label noParameters = new()
            {
                Dock = DockStyle.Fill,
                ForeColor = EditorChrome.Muted,
                Padding = new Padding(0, 12, 0, 0),
                Text = "This action has no parameters.",
            };
            _parameters.Controls.Add(noParameters, 0, 0);
            _parameters.SetColumnSpan(noParameters, 2);
        }
        _parameters.ResumeLayout(true);
    }

    private static Label Caption(string text) => new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        ForeColor = EditorChrome.Text,
        Margin = new Padding(0, 4, 10, 4),
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static string Humanize(string value)
    {
        string result = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ');
        return result.Length == 0 ? "Action" : char.ToUpperInvariant(result[0]) + result[1..];
    }

    private static void StyleDrawer(Control root)
    {
        if (root is not TableLayoutPanel and not Panel) EditorChrome.StyleField(root);
        foreach (Control child in root.Controls) StyleDrawer(child);
    }
}

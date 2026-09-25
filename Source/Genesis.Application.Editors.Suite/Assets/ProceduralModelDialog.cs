using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Preset and seed controls for non-destructive tree/rock generation.</summary>
internal sealed class ProceduralModelDialog : DpiAwareForm
{
    private readonly bool _tree;
    private readonly ThemedComboBox _preset = MakeCombo();
    private readonly ThemedComboBox _quality = MakeCombo();
    private readonly TextBox _seed = new() { Width = 170 };
    private readonly Dictionary<string, NumericUpDown> _numbers = new(StringComparer.Ordinal);

    private ProceduralModelDialog(bool tree)
    {
        _tree = tree;
        Text = tree ? "Generate Tree" : "Generate Rock";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(430, tree ? 430 : 390);
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;

        foreach (string value in tree
                     ? Enum.GetNames<ProceduralTreePreset>()
                     : Enum.GetNames<ProceduralRockPreset>())
        {
            _preset.Items.Add(SplitName(value));
        }

        foreach (string value in Enum.GetNames<ProceduralModelQuality>())
        {
            _quality.Items.Add(value);
        }

        EditorChrome.StyleField(_preset);
        EditorChrome.StyleField(_quality);
        EditorChrome.StyleField(_seed);

        TableLayoutPanel fields = new()
        {
            AutoScroll = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 8),
            RowCount = 0,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43f));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57f));
        AddRow(fields, "Preset", _preset);
        AddRow(fields, "Quality", _quality);
        AddRow(fields, "Seed", _seed);

        if (tree)
        {
            AddNumber(fields, "Height scale", "height", 0.2m, 4m, 1m);
            AddNumber(fields, "Canopy scale", "canopy", 0.15m, 4m, 1m);
            AddNumber(fields, "Branch density", "branches", 0.1m, 3m, 1m);
            AddNumber(fields, "Leaf density", "leaves", 0m, 3m, 1m);
            AddNumber(fields, "Wind bias", "wind", -1.5m, 1.5m, 0m);
        }
        else
        {
            AddNumber(fields, "Width", "width", 0.2m, 20m, 2.4m);
            AddNumber(fields, "Height", "height", 0.2m, 20m, 1.8m);
            AddNumber(fields, "Depth", "depth", 0.2m, 20m, 2.1m);
            AddNumber(fields, "Roughness", "roughness", 0m, 1.5m, 0.34m);
            AddNumber(fields, "Asymmetry", "asymmetry", 0m, 1.5m, 0.22m);
        }

        Label hint = new()
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(18, 9, 18, 0),
            ForeColor = EditorChrome.Muted,
            Text = tree
                ? "Seeded curved branches, root flare, and clustered foliage. Parameters remain editable after saving."
                : "Seeded low-poly boulders, cliff shards, mossy stones, or crystal clusters.",
        };

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(8),
        };
        Button generate = new() { Text = "Generate", DialogResult = DialogResult.OK, Width = 96 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88 };
        EditorChrome.StyleField(generate);
        EditorChrome.StyleField(cancel);
        actions.Controls.Add(generate);
        actions.Controls.Add(cancel);

        Controls.Add(fields);
        Controls.Add(hint);
        Controls.Add(actions);
        AcceptButton = generate;
        CancelButton = cancel;
    }

    public static ProceduralModelDialog ForTree(ProceduralTreeOptions options)
    {
        ProceduralModelDialog dialog = new(tree: true);
        dialog.SetTree(options);
        return dialog;
    }

    public static ProceduralModelDialog ForRock(ProceduralRockOptions options)
    {
        ProceduralModelDialog dialog = new(tree: false);
        dialog.SetRock(options);
        return dialog;
    }

    public ProceduralTreeOptions TreeOptions => new()
    {
        Preset = (ProceduralTreePreset)_preset.SelectedIndex,
        Quality = (ProceduralModelQuality)_quality.SelectedIndex,
        Seed = ParseSeed(_seed.Text, 0x5A17_2026UL),
        HeightScale = Number("height"),
        CanopyScale = Number("canopy"),
        BranchDensity = Number("branches"),
        LeafDensity = Number("leaves"),
        WindBias = Number("wind"),
    };

    public ProceduralRockOptions RockOptions => new()
    {
        Preset = (ProceduralRockPreset)_preset.SelectedIndex,
        Quality = (ProceduralModelQuality)_quality.SelectedIndex,
        Seed = ParseSeed(_seed.Text, 0xB01D_E22026UL),
        Width = Number("width"),
        Height = Number("height"),
        Depth = Number("depth"),
        Roughness = Number("roughness"),
        Asymmetry = Number("asymmetry"),
    };

    private void SetTree(ProceduralTreeOptions options)
    {
        _preset.SelectedIndex = (int)options.Preset;
        _quality.SelectedIndex = (int)options.Quality;
        _seed.Text = options.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetNumber("height", options.HeightScale);
        SetNumber("canopy", options.CanopyScale);
        SetNumber("branches", options.BranchDensity);
        SetNumber("leaves", options.LeafDensity);
        SetNumber("wind", options.WindBias);
    }

    private void SetRock(ProceduralRockOptions options)
    {
        _preset.SelectedIndex = (int)options.Preset;
        _quality.SelectedIndex = (int)options.Quality;
        _seed.Text = options.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetNumber("width", options.Width);
        SetNumber("height", options.Height);
        SetNumber("depth", options.Depth);
        SetNumber("roughness", options.Roughness);
        SetNumber("asymmetry", options.Asymmetry);
    }

    private float Number(string key) => (float)_numbers[key].Value;

    private void SetNumber(string key, float value)
    {
        NumericUpDown number = _numbers[key];
        number.Value = Math.Clamp((decimal)value, number.Minimum, number.Maximum);
    }

    private void AddNumber(
        TableLayoutPanel panel,
        string label,
        string key,
        decimal minimum,
        decimal maximum,
        decimal value)
    {
        NumericUpDown number = new()
        {
            DecimalPlaces = 2,
            Increment = 0.05m,
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = 170,
        };
        EditorChrome.StyleField(number);
        _numbers.Add(key, number);
        AddRow(panel, label, number);
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control field)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
        panel.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            ForeColor = EditorChrome.Muted,
            Text = label,
        }, 0, row);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(field, 1, row);
    }

    private static ThemedComboBox MakeCombo() => new()
    {
        Width = 170,
    };

    private static ulong ParseSeed(string text, ulong fallback)
    {
        string value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong hexadecimal))
        {
            return hexadecimal;
        }

        return ulong.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out ulong number)
            ? number
            : fallback;
    }

    private static string SplitName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

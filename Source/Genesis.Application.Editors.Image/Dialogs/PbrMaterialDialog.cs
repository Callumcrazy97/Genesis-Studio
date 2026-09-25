using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

public sealed class PbrMaterialDialog : DpiAwareForm
{
    private readonly ImageThemedComboBox _preset = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _normal = Number(5, 1600, 250, 100);
    private readonly NumericUpDown _height = Number(5, 400, 100, 100);
    private readonly NumericUpDown _roughness = Number(0, 100, 72, 100);
    private readonly NumericUpDown _metallic = Number(0, 100, 0, 100);
    private readonly CheckBox _seamless = new() { Text = "Blend opposite edges for seamless tiling", Checked = true, AutoSize = true };

    public PbrMaterialDialog()
    {
        Text = "Generate PBR Material Set";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(410, 292);
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = ImageEditorChrome.Surface;
        ForeColor = ImageEditorChrome.Text;
        _preset.Items.AddRange(Enum.GetNames<PbrMaterialPreset>());
        _preset.SelectedItem = PbrMaterialPreset.Organic.ToString();
        _preset.SelectedIndexChanged += (_, _) => ApplyPreset();
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(layout, "Preset", _preset, 0);
        Add(layout, "Normal strength", _normal, 1);
        Add(layout, "Height contrast", _height, 2);
        Add(layout, "Base roughness", _roughness, 3);
        Add(layout, "Metallic", _metallic, 4);
        _seamless.ForeColor = ImageEditorChrome.Text;
        layout.Controls.Add(_seamless, 0, 5); layout.SetColumnSpan(_seamless, 2);
        Label note = new()
        {
            Text = "Creates editable Normal, Roughness, Metallic, Height and Occlusion channel layers.",
            AutoSize = true,
            ForeColor = ImageEditorChrome.Muted,
        };
        layout.Controls.Add(note, 0, 6); layout.SetColumnSpan(note, 2);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "Generate", DialogResult = DialogResult.OK, AutoSize = true });
        buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
        layout.Controls.Add(buttons, 0, 7); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = (Button)buttons.Controls[0];
        CancelButton = (Button)buttons.Controls[1];
    }

    public PbrMaterialSettings Settings => new()
    {
        Preset = Enum.TryParse(_preset.SelectedItem?.ToString(), out PbrMaterialPreset preset) ? preset : PbrMaterialPreset.Organic,
        NormalStrength = (float)_normal.Value,
        HeightContrast = (float)_height.Value,
        Roughness = (float)_roughness.Value,
        Metallic = (float)_metallic.Value,
        Seamless = _seamless.Checked,
    };

    private void ApplyPreset()
    {
        if (!Enum.TryParse(_preset.SelectedItem?.ToString(), out PbrMaterialPreset preset)) return;
        PbrMaterialSettings settings = PbrMaterialSettings.FromPreset(preset);
        _normal.Value = (decimal)settings.NormalStrength;
        _height.Value = (decimal)settings.HeightContrast;
        _roughness.Value = (decimal)settings.Roughness;
        _metallic.Value = (decimal)settings.Metallic;
        _seamless.Checked = settings.Seamless;
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals) => new()
    {
        Minimum = min / decimals, Maximum = max / decimals, Value = value / decimals,
        DecimalPlaces = decimals == 100 ? 2 : 0, Increment = decimals == 100 ? 0.05m : 1m, Dock = DockStyle.Fill,
    };

    private static void Add(TableLayoutPanel layout, string label, Control control, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(control, 1, row);
    }
}

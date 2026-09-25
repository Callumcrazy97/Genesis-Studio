using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors;
using Genesis.Runtime.Scene;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>Compact assignment dialog for the six adaptive environment-audio roles.</summary>
public sealed class RoomSoundscapeDialog : DpiAwareForm
{
    private readonly string _projectRoot;
    private readonly Dictionary<string, TextBox> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NumericUpDown> _levels = new(StringComparer.OrdinalIgnoreCase);

    public RoomSoundscapeDialog(RoomEnvironment environment, string projectRoot)
    {
        _projectRoot = projectRoot ?? string.Empty;
        Text = "Adaptive Environment Soundscape";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(700, 390);
        MaximizeBox = false; MinimizeBox = false;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(14), RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        AddRole(layout, "Wind", environment.WindAudio, 0);
        AddRole(layout, "Rain", environment.RainAudio, 1);
        AddRole(layout, "Water", environment.WaterAudio, 2);
        AddRole(layout, "Fire", environment.FireAudio, 3);
        AddRole(layout, "Wildlife", environment.WildlifeAudio, 4);
        AddRole(layout, "Night", environment.NightAudio, 5);
        RoomSoundscapeLevels levels = (environment.SoundscapeLevels ?? new()).Clone();
        AddLevel(layout, "Wind", levels.Wind, 0); AddLevel(layout, "Rain", levels.Rain, 1);
        AddLevel(layout, "Water", levels.Water, 2); AddLevel(layout, "Fire", levels.Fire, 3);
        AddLevel(layout, "Wildlife", levels.Wildlife, 4); AddLevel(layout, "Night", levels.Night, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = "Master %", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        AddLevel(layout, "Master", levels.Master, 6);
        Label hint = new()
        {
            Text = "Levels are percentages. Ambience follows weather, time, shelter and nearby water / points of interest, including when Dynamic Sky is off. Water, fire and wildlife are spatial.",
            AutoSize = true, ForeColor = EditorChrome.Muted,
        };
        layout.Controls.Add(hint, 0, 7); layout.SetColumnSpan(hint, 4);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        Button ok = new() { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 8); layout.SetColumnSpan(buttons, 4);
        Controls.Add(layout); AcceptButton = ok; CancelButton = cancel;
        BackColor = EditorChrome.Surface;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        StyleControls(layout);
    }

    public void ApplyTo(RoomEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        environment.WindAudio = Value("Wind"); environment.RainAudio = Value("Rain");
        environment.WaterAudio = Value("Water"); environment.FireAudio = Value("Fire");
        environment.WildlifeAudio = Value("Wildlife"); environment.NightAudio = Value("Night");
        environment.SoundscapeLevels = new RoomSoundscapeLevels
        {
            Master = Level("Master"), Wind = Level("Wind"), Rain = Level("Rain"), Water = Level("Water"),
            Fire = Level("Fire"), Wildlife = Level("Wildlife"), Night = Level("Night"),
        };
    }

    private float Level(string role) => (float)_levels[role].Value / 100f;
    private static void StyleControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button or TextBox or NumericUpDown) EditorChrome.StyleField(control);
            if (control.HasChildren) StyleControls(control);
        }
    }
    private void AddLevel(TableLayoutPanel layout, string role, float value, int row)
    {
        var number = new NumericUpDown { Name = "Soundscape" + role + "Level", Minimum = 0, Maximum = 100,
            Value = (decimal)(RoomSoundscapeLevels.Sanitize(value) * 100f), Dock = DockStyle.Fill, AccessibleName = role + " volume percent" };
        _levels[role] = number; layout.Controls.Add(number, 3, row);
    }

    private void AddRole(TableLayoutPanel layout, string role, string value, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        TextBox path = new() { Name = "Soundscape" + role + "Resource", Text = value ?? string.Empty, Dock = DockStyle.Fill, AccessibleName = role + " audio resource" };
        Button browse = new() { Name = "Soundscape" + role + "Picker", Text = "Browse…", Dock = DockStyle.Fill, AccessibleName = "Choose " + role.ToLowerInvariant() + " audio resource" };
        browse.Click += (_, _) => Browse(path);
        _paths[role] = path;
        layout.Controls.Add(new Label { Text = role, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(path, 1, row); layout.Controls.Add(browse, 2, row);
    }

    private void Browse(TextBox target)
    {
        ProjectAssetEntry? selected = AssetPickerService.PickAsset(
            new AssetPickerRequest(_projectRoot, ResourceKind.Audio, target.Text,
                "Choose soundscape audio", AllowNone: true), this);
        if (selected is not null) target.Text = selected.Reference;
    }

    private string Value(string role) => _paths.TryGetValue(role, out TextBox? path) ? path.Text.Trim() : string.Empty;
}

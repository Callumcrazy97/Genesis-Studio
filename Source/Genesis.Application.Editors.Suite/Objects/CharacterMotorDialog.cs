using System.Drawing;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>Designer-facing movement, slope, step and swimming settings for character presets.</summary>
public sealed class CharacterMotorDialog : DpiAwareForm
{
    private readonly Dictionary<string, NumericUpDown> _fields = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Key, string Label, decimal Default, decimal Min, decimal Max, decimal Step)[] Definitions =
    [
        ("walkSpeed", "Walk speed", 8m, 0m, 1000m, 0.1m),
        ("sprintMultiplier", "Sprint multiplier", 1.6m, 1m, 20m, 0.1m),
        ("groundAcceleration", "Ground acceleration", 48m, 0m, 1000m, 1m),
        ("airAcceleration", "Air acceleration", 16m, 0m, 1000m, 1m),
        ("jumpSpeed", "Jump speed", 6.5m, 0m, 1000m, 0.1m),
        ("stepHeight", "Step height", 0.35m, 0m, 10m, 0.05m),
        ("maximumSlopeDegrees", "Maximum slope", 48m, 0m, 89m, 1m),
        ("swimSpeed", "Swim speed", 5m, 0m, 1000m, 0.1m),
        ("swimVerticalSpeed", "Swim vertical speed", 4m, 0m, 1000m, 0.1m),
        ("swimAcceleration", "Swim acceleration", 18m, 0m, 1000m, 1m),
        ("swimDrag", "Swim drag", 2.5m, 0m, 100m, 0.1m),
    ];

    public CharacterMotorDialog(JObject? current)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = EditorChrome.Canvas;
        ClientSize = new Size(500, 530);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Character movement";

        Controls.Add(new Label
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Location = new Point(20, 14),
            Size = new Size(458, 34),
            Text = "These values drive terrain slopes, automatic steps, jumping and movement while submerged.",
        });

        int y = 54;
        foreach ((string key, string label, decimal defaultValue, decimal minimum, decimal maximum, decimal increment) in Definitions)
        {
            Controls.Add(new Label
            {
                AutoSize = false,
                ForeColor = EditorChrome.Text,
                Location = new Point(22, y + 4),
                Size = new Size(245, 24),
                Text = label,
            });
            NumericUpDown input = new()
            {
                DecimalPlaces = increment < 1m ? 2 : 0,
                Increment = increment,
                Minimum = minimum,
                Maximum = maximum,
                Location = new Point(278, y),
                Size = new Size(196, 28),
                Value = Clamp(current?[key]?.Value<decimal>() ?? defaultValue, minimum, maximum),
            };
            EditorChrome.StyleField(input);
            Controls.Add(input);
            _fields[key] = input;
            y += 36;
        }

        Button ok = new() { DialogResult = DialogResult.OK, Location = new Point(250, 478), Size = new Size(105, 34), Text = "Apply" };
        Button cancel = new() { DialogResult = DialogResult.Cancel, Location = new Point(367, 478), Size = new Size(105, 34), Text = "Cancel" };
        EditorChrome.StyleField(ok);
        EditorChrome.StyleField(cancel);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public JObject Settings
    {
        get
        {
            JObject result = new();
            foreach ((string key, NumericUpDown input) in _fields)
                result[key] = (float)input.Value;
            return result;
        }
    }

    public static JObject Defaults
    {
        get
        {
            JObject result = new();
            foreach ((string key, _, decimal defaultValue, _, _, _) in Definitions)
                result[key] = (float)defaultValue;
            return result;
        }
    }

    private static decimal Clamp(decimal value, decimal minimum, decimal maximum) =>
        Math.Min(maximum, Math.Max(minimum, value));
}

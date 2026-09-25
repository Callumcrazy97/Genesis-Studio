using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>One built-in physics behaviour an object can take on.</summary>
/// <param name="Id">Stored on the object document.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What it does, in a sentence.</param>
public sealed record PhysicsPreset(string Id, string Name, string Description);

/// <summary>
/// Asks which physics an object uses, offering the engine's built-in presets.
/// </summary>
/// <remarks>
/// Ticking "Uses Physics" is a question, not an answer — a platformer character, a pushable crate
/// and a static wall all "use physics" and behave nothing alike. Presets name the common answers so
/// a designer picks a behaviour rather than assembling one from numbers they have to guess at.
/// </remarks>
public sealed class ObjectPhysicsDialog : DpiAwareForm
{
    /// <summary>The built-in behaviours. Ids are stored, so renaming a Name is safe.</summary>
    public static IReadOnlyList<PhysicsPreset> Presets { get; } =
    [
        new("PlatformerCharacter", "Platformer character",
            "Gravity, ground contact and jumping. Does not rotate."),
        new("TopDownCharacter", "Top-down character",
            "No gravity, moves freely, blocked by solids."),
        new("RigidBody", "Rigid body",
            "Falls, tumbles and collides. For crates, barrels and debris."),
        new("StaticSolid", "Static solid",
            "Never moves; other bodies collide with it. Walls and floors."),
        new("Trigger", "Trigger volume",
            "Detects overlaps but blocks nothing. Checkpoints and pickups."),
        new("Projectile", "Projectile",
            "Fast-moving, light, collides once. Bullets and thrown objects."),
    ];

    private readonly ListBox _list = new();
    private readonly Label _description = new();

    public ObjectPhysicsDialog(string? currentPresetId)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = EditorChrome.Canvas;
        ClientSize = new Size(480, 400);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Physics";

        Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(EditorChrome.BaseFont.FontFamily, 12f, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Location = new Point(20, 16),
            Text = "How does this object behave?",
        });

        Controls.Add(new Label
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Location = new Point(22, 44),
            Size = new Size(440, 20),
            Text = "The physics components are derived from this choice — nothing to configure by hand.",
        });

        _list.BackColor = EditorChrome.Surface;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.ForeColor = EditorChrome.Text;
        _list.ItemHeight = DpiLayout.Scale(this, 22);
        _list.Location = new Point(20, 72);
        _list.Size = new Size(440, 200);
        foreach (PhysicsPreset preset in Presets)
        {
            _list.Items.Add(preset.Name);
        }

        _list.SelectedIndexChanged += (_, _) => SyncDescription();
        Controls.Add(_list);

        _description.AutoSize = false;
        _description.Font = EditorChrome.SmallFont;
        _description.ForeColor = EditorChrome.Muted;
        _description.Location = new Point(22, 282);
        _description.Size = new Size(436, 44);
        Controls.Add(_description);

        Button ok = new()
        {
            DialogResult = DialogResult.OK,
            Location = new Point(236, 342),
            Size = new Size(105, 34),
            Text = "Use this",
        };
        EditorChrome.StyleField(ok);
        Controls.Add(ok);

        Button cancel = new()
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(351, 342),
            Size = new Size(105, 34),
            Text = "Cancel",
        };
        EditorChrome.StyleField(cancel);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;

        int index = currentPresetId is null
            ? 0
            : Math.Max(0, Presets.ToList().FindIndex(preset =>
                string.Equals(preset.Id, currentPresetId, StringComparison.OrdinalIgnoreCase)));
        _list.SelectedIndex = index;
    }

    /// <summary>The chosen preset id.</summary>
    public string SelectedPresetId =>
        Presets[Math.Clamp(_list.SelectedIndex, 0, Presets.Count - 1)].Id;

    /// <summary>Select a preset by id. Used by tests.</summary>
    public bool Select(string presetId)
    {
        int index = Presets.ToList().FindIndex(preset =>
            string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        _list.SelectedIndex = index;
        return true;
    }

    private void SyncDescription() =>
        _description.Text = _list.SelectedIndex >= 0 && _list.SelectedIndex < Presets.Count
            ? Presets[_list.SelectedIndex].Description
            : string.Empty;
}

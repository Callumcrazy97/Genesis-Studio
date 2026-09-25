using System.Numerics;
using System.Windows.Forms;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    public bool FogPreviewEnabled { get; private set; }
    public float FogPreviewStart { get; private set; } = 20f;
    public float FogPreviewEnd { get; private set; } = 200f;
    public float FogPreviewDensity { get; private set; } = .012f;
    private ToolStripMenuItem? _fogPreviewMenu;

    public void SetFogPreview(bool enabled, float start = 20f, float end = 200f, float density = .012f)
    {
        if (!float.IsFinite(start) || !float.IsFinite(end) || !float.IsFinite(density)) return;
        FogPreviewEnabled = enabled;
        FogPreviewStart = Math.Max(0, start);
        FogPreviewEnd = Math.Max(FogPreviewStart + .01f, end);
        FogPreviewDensity = Math.Clamp(density, 0, 1);
        if (_fogPreviewMenu is not null) _fogPreviewMenu.Checked = enabled;
        _viewport?.Invalidate();
    }

    private void AddFogViewControls(ToolStripDropDownButton view)
    {
        _fogPreviewMenu = new ToolStripMenuItem("Fog") { CheckOnClick = true, Name = "RoomViewFog" };
        _fogPreviewMenu.Click += (_, _) => SetFogPreview(_fogPreviewMenu.Checked,
            FogPreviewStart, FogPreviewEnd, FogPreviewDensity);
        view.DropDownItems.Add(_fogPreviewMenu);
        view.DropDownItems.Add("Fog settings…", null, (_, _) =>
        {
            using var dialog = new DpiAwareForm
            {
                Text = "Room fog preview", Width = 360, Height = 260, StartPosition = FormStartPosition.CenterParent,
                BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text, Font = EditorChrome.BaseFont,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, AutoSize = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            var enabled = new CheckBox { Text = "Enable fog", Checked = FogPreviewEnabled, AutoSize = true };
            layout.Controls.Add(enabled, 0, 0); layout.SetColumnSpan(enabled, 2);
            NumericUpDown Field(string label, float value, decimal max, int places, int row)
            {
                var number = new NumericUpDown { Minimum = 0, Maximum = max, DecimalPlaces = places,
                    Increment = places == 3 ? .001m : 1, Value = Math.Clamp((decimal)value, 0, max), Dock = DockStyle.Fill };
                EditorChrome.StyleField(number);
                layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
                layout.Controls.Add(number, 1, row);
                return number;
            }
            var start = Field("Start (m)", FogPreviewStart, 100000, 1, 1);
            var end = Field("End (m)", FogPreviewEnd, 100000, 1, 2);
            var density = Field("Density", FogPreviewDensity, 1, 3, 3);
            var hint = new Label { Text = "Editor preview only. Does not change game weather.", AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0) };
            layout.Controls.Add(hint, 0, 4); layout.SetColumnSpan(hint, 2);
            var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Dock = DockStyle.Fill };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
            EditorChrome.StyleField(apply); EditorChrome.StyleField(cancel);
            layout.Controls.Add(cancel, 0, 5); layout.Controls.Add(apply, 1, 5);
            dialog.Controls.Add(layout); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                SetFogPreview(enabled.Checked, (float)start.Value, (float)end.Value, (float)density.Value);
        });
    }

    private void ApplyRoomFogPreview(ref Mesh3DState state)
    {
        state.FogEnabled = state.FogScreenSpace = FogPreviewEnabled;
        state.VolumetricFogEnabled = FogPreviewEnabled;
        if (!FogPreviewEnabled) return;
        state.FogStart = FogPreviewStart; state.FogEnd = FogPreviewEnd; state.FogDensity = FogPreviewDensity;
        state.FogColor = new Vector4(.58f, .66f, .78f, 1f);
    }
}

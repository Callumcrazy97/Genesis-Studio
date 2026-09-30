using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Genesis.Application.Editors.Image;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

internal sealed class ModelHairDialog : Form
{
    private readonly ModelHairProfile _profile;
    private readonly ListBox _styles = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _kind = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox _meshes = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly CheckBox _default = new() { Text = "Use this style by default", AutoSize = true };
    private bool _loading;
    private int _selected = -1;

    public ModelHairProfile Result => _profile;

    public ModelHairDialog(GModelAsset asset)
    {
        try { _profile = ModelHairProfile.Read(asset); }
        catch { _profile = new(); }
        Text = "Hair styles"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 520); MinimumSize = new Size(650, 450);
        BackColor = EditorChrome.Surface; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(layout);
        var help = new Label { Dock = DockStyle.Fill, Text = "Import hair and beard as separate meshes on the character's rig. Group them into styles here; gameplay can switch styles and colours without changing the shared model. None is always available.", AutoSize = false };
        layout.Controls.Add(help, 0, 0); layout.SetColumnSpan(help, 2);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        left.Controls.Add(_styles, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var add = new Button { Text = "Add", Width = 85 }; var remove = new Button { Text = "Remove", Width = 85 };
        buttons.Controls.AddRange([add, remove]); left.Controls.Add(buttons, 0, 1); layout.Controls.Add(left, 0, 1);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0), ColumnCount = 2, RowCount = 4 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fields.Controls.Add(new Label { Text = "Name", AutoSize = true }, 0, 0); fields.Controls.Add(_name, 1, 0);
        fields.Controls.Add(new Label { Text = "Part", AutoSize = true }, 0, 1); fields.Controls.Add(_kind, 1, 1);
        _kind.Items.AddRange(["Scalp", "Facial"]); fields.Controls.Add(_default, 1, 2);
        fields.Controls.Add(_meshes, 0, 3); fields.SetColumnSpan(_meshes, 2); layout.Controls.Add(fields, 1, 1);
        _meshes.Items.AddRange(asset.Meshes.Select(m => (object)m.Name).Distinct().ToArray());
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Apply", Width = 96, Height = 30 }; var cancel = new Button { Text = "Cancel", Width = 96, Height = 30, DialogResult = DialogResult.Cancel };
        footer.Controls.AddRange([save, cancel]); layout.Controls.Add(footer, 0, 2); layout.SetColumnSpan(footer, 2);
        AcceptButton = save; CancelButton = cancel;
        foreach (Control input in new Control[] { _styles, _name, _kind, _meshes, add, remove, save, cancel }) EditorChrome.StyleField(input);
        add.Click += (_, _) => { Commit(); _profile.Styles.Add(new ModelHairStyle { Name = "New style " + (_profile.Styles.Count + 1) }); RefreshList(_profile.Styles.Count - 1); };
        remove.Click += (_, _) => { if (_selected < 0 || _selected >= _profile.Styles.Count) return; _profile.Styles.RemoveAt(_selected); _selected = -1; RepairDefaults(); RefreshList(0); };
        _styles.SelectedIndexChanged += (_, _) => { if (_loading) return; Commit(); LoadSelection(_styles.SelectedIndex); };
        save.Click += (_, _) =>
        {
            Commit();
            try { _profile.Validate(asset); DialogResult = DialogResult.OK; Close(); }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, "Hair styles", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        RefreshList(0);
    }

    private void Commit()
    {
        if (_loading || _selected < 0) return;
        if (_selected == _profile.Styles.Count) { _profile.EyebrowMeshes = _meshes.CheckedItems.Cast<string>().ToList(); return; }
        if (_selected > _profile.Styles.Count) return;
        ModelHairStyle style = _profile.Styles[_selected];
        bool wasScalp = style.Kind == "Scalp" && _profile.DefaultScalp == style.Name;
        bool wasFacial = style.Kind == "Facial" && _profile.DefaultFacial == style.Name;
        style.Name = _name.Text.Trim(); style.Kind = _kind.SelectedItem as string ?? "Scalp";
        style.Meshes = _meshes.CheckedItems.Cast<string>().ToList();
        if (wasScalp) _profile.DefaultScalp = "None";
        if (wasFacial) _profile.DefaultFacial = "None";
        if (_default.Checked)
        {
            if (style.Kind == "Scalp") _profile.DefaultScalp = style.Name;
            else _profile.DefaultFacial = style.Name;
        }
        _loading = true;
        _styles.Items[_selected] = style.Kind + " · " + style.Name;
        _loading = false;
    }

    private void RepairDefaults()
    {
        if (!_profile.Exists("Scalp", _profile.DefaultScalp)) _profile.DefaultScalp = "None";
        if (!_profile.Exists("Facial", _profile.DefaultFacial)) _profile.DefaultFacial = "None";
    }

    private void RefreshList(int selected)
    {
        _loading = true; _selected = -1; _styles.Items.Clear();
        foreach (ModelHairStyle style in _profile.Styles) _styles.Items.Add(style.Kind + " · " + style.Name);
        _styles.Items.Add("Eyebrows · always visible");
        _styles.SelectedIndex = _styles.Items.Count == 0 ? -1 : Math.Clamp(selected, 0, _styles.Items.Count - 1);
        _loading = false; LoadSelection(_styles.SelectedIndex);
    }

    private void LoadSelection(int index)
    {
        _loading = true; _selected = index;
        bool brows = index == _profile.Styles.Count;
        ModelHairStyle? style = index >= 0 && !brows ? _profile.Styles[index] : null;
        _name.Text = brows ? "Eyebrows" : style?.Name ?? ""; _kind.SelectedItem = style?.Kind ?? "Scalp";
        _default.Checked = style is not null && (style.Kind == "Scalp" ? _profile.DefaultScalp : _profile.DefaultFacial) == style.Name;
        for (int i = 0; i < _meshes.Items.Count; i++) _meshes.SetItemChecked(i, (brows ? _profile.EyebrowMeshes : style?.Meshes)?.Contains((string)_meshes.Items[i]) == true);
        _name.Enabled = _kind.Enabled = _default.Enabled = style is not null;
        _meshes.Enabled = brows || style is not null;
        _loading = false;
    }
}

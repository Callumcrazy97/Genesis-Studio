#nullable enable
using System;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Image;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

internal sealed class ModelSocketDialog : Form
{
    private readonly TextBox _name = new();
    private readonly ComboBox _anchor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown[] _position = new NumericUpDown[3];
    private readonly NumericUpDown[] _rotation = new NumericUpDown[3];
    private readonly NumericUpDown[] _scale = new NumericUpDown[3];

    public ModelSocketDialog(GModelAsset asset, GModelSocket? source = null)
    {
        Text = source is null ? "New Attachment Socket" : "Edit Attachment Socket";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 344);
        MinimumSize = new Size(500, 383);
        BackColor = EditorChrome.Surface;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;

        TableLayoutPanel fields = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 8),
            ColumnCount = 2,
            RowCount = 6,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(fields);

        AddField(fields, "Name", _name, 0);
        AddField(fields, "Relative to", _anchor, 1);
        _anchor.Items.Add(new AnchorChoice("Model root", -1, -1));
        var bones = asset.Rig?.Bones ?? [];
        for (int index = 0; index < bones.Count; index++)
            _anchor.Items.Add(new AnchorChoice("Bone · " + bones[index].Name, index, -1));
        var nodes = asset.Nodes ?? [];
        for (int index = 0; index < nodes.Count; index++)
            _anchor.Items.Add(new AnchorChoice("Node · " + nodes[index].Name, -1, index));

        fields.Controls.Add(AxisLabel("Position"), 0, 2);
        fields.Controls.Add(AxisRow(_position, -100000m, 100000m, 0m), 1, 2);
        fields.Controls.Add(AxisLabel("Rotation"), 0, 3);
        fields.Controls.Add(AxisRow(_rotation, -36000m, 36000m, 0m), 1, 3);
        fields.Controls.Add(AxisLabel("Scale"), 0, 4);
        fields.Controls.Add(AxisRow(_scale, .0001m, 10000m, 1m), 1, 4);

        FlowLayoutPanel commands = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };
        Button save = new() { Text = source is null ? "Create" : "Apply", Width = 96, Height = 30 };
        Button cancel = new() { Text = "Cancel", Width = 96, Height = 30, DialogResult = DialogResult.Cancel };
        EditorChrome.StyleField(save);
        EditorChrome.StyleField(cancel);
        save.BackColor = EditorChrome.Accent;
        save.ForeColor = Color.White;
        save.Click += (_, _) => Accept();
        commands.Controls.Add(save);
        commands.Controls.Add(cancel);
        fields.Controls.Add(commands, 0, 5);
        fields.SetColumnSpan(commands, 2);
        AcceptButton = save;
        CancelButton = cancel;

        _name.Text = source?.Name ?? UniqueName(asset);
        _anchor.SelectedIndex = FindAnchor(source);
        Matrix4x4 local = source?.LocalTransform ?? Matrix4x4.Identity;
        if (!Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 position))
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            position = Vector3.Zero;
        }
        Vector3 euler = EulerDegrees(rotation);
        Set(_position, position);
        Set(_rotation, euler);
        Set(_scale, scale);
    }

    public GModelSocket? Result { get; private set; }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Enter a socket name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _name.Focus();
            return;
        }
        AnchorChoice anchor = _anchor.SelectedItem as AnchorChoice ?? new AnchorChoice("Model root", -1, -1);
        Vector3 position = Read(_position);
        Vector3 rotation = Read(_rotation);
        Vector3 scale = Read(_scale);
        Result = new GModelSocket
        {
            Name = _name.Text.Trim(),
            BoneIndex = anchor.Bone,
            NodeIndex = anchor.Node,
            LocalTransform = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromYawPitchRoll(Radians(rotation.Y), Radians(rotation.X), Radians(rotation.Z))
                * Matrix4x4.CreateTranslation(position),
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private int FindAnchor(GModelSocket? socket)
    {
        if (socket is null) return 0;
        for (int index = 0; index < _anchor.Items.Count; index++)
            if (_anchor.Items[index] is AnchorChoice choice
                && choice.Bone == socket.BoneIndex && choice.Node == socket.NodeIndex) return index;
        return 0;
    }

    private static void AddField(TableLayoutPanel fields, string caption, Control input, int row)
    {
        fields.Controls.Add(AxisLabel(caption), 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 6);
        EditorChrome.StyleField(input);
        fields.Controls.Add(input, 1, row);
    }

    private static Label AxisLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = EditorChrome.Muted,
    };

    private static Control AxisRow(NumericUpDown[] fields, decimal minimum, decimal maximum, decimal value)
    {
        TableLayoutPanel row = new() { Dock = DockStyle.Fill, ColumnCount = 6, Margin = Padding.Empty };
        for (int index = 0; index < 3; index++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            row.Controls.Add(new Label { Text = "XYZ"[index].ToString(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = EditorChrome.Muted }, index * 2, 0);
            NumericUpDown number = new()
            {
                DecimalPlaces = 3,
                Increment = .025m,
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 5, 8, 5),
            };
            EditorChrome.StyleField(number);
            fields[index] = number;
            row.Controls.Add(number, index * 2 + 1, 0);
        }
        return row;
    }

    private static void Set(NumericUpDown[] controls, Vector3 value)
    {
        float[] values = [value.X, value.Y, value.Z];
        for (int index = 0; index < controls.Length; index++)
            controls[index].Value = Math.Clamp((decimal)values[index], controls[index].Minimum, controls[index].Maximum);
    }

    private static Vector3 Read(NumericUpDown[] controls) =>
        new((float)controls[0].Value, (float)controls[1].Value, (float)controls[2].Value);

    private static Vector3 EulerDegrees(Quaternion value)
    {
        value = Quaternion.Normalize(value);
        float pitchSin = 2f * (value.W * value.X - value.Y * value.Z);
        float pitch = MathF.Abs(pitchSin) >= 1f ? MathF.CopySign(MathF.PI / 2f, pitchSin) : MathF.Asin(pitchSin);
        float yaw = MathF.Atan2(2f * (value.W * value.Y + value.Z * value.X), 1f - 2f * (value.X * value.X + value.Y * value.Y));
        float roll = MathF.Atan2(2f * (value.W * value.Z + value.X * value.Y), 1f - 2f * (value.X * value.X + value.Z * value.Z));
        const float degrees = 180f / MathF.PI;
        return new Vector3(pitch * degrees, yaw * degrees, roll * degrees);
    }

    private static float Radians(float degrees) => degrees * (MathF.PI / 180f);

    private static string UniqueName(GModelAsset asset)
    {
        int number = 1;
        string name;
        do name = "Socket " + number++;
        while (asset.Sockets?.Exists(socket => string.Equals(socket.Name, name, StringComparison.OrdinalIgnoreCase)) == true);
        return name;
    }

    private sealed record AnchorChoice(string Label, int Bone, int Node)
    {
        public override string ToString() => Label;
    }
}

using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;

namespace Genesis.Application.Editors.Suite.Inspector;

public sealed class Vector2Drawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => context.PropertyType == typeof(Vector2);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        Vector2 value = context.InitialValue is Vector2 vector ? vector : Vector2.Zero;
        return VectorDrawerRow.Create(
            [("X", value.X, Color.FromArgb(230, 82, 82)), ("Y", value.Y, Color.FromArgb(86, 190, 104))],
            values => context.ValueChanged(new Vector2(values[0], values[1])));
    }
}

public sealed class Vector3Drawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => context.PropertyType == typeof(Vector3);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        Vector3 value = context.InitialValue is Vector3 vector ? vector : Vector3.Zero;
        return VectorDrawerRow.Create(
            [("X", value.X, Color.FromArgb(230, 82, 82)), ("Y", value.Y, Color.FromArgb(86, 190, 104)),
                ("Z", value.Z, Color.FromArgb(72, 145, 235))],
            values => context.ValueChanged(new Vector3(values[0], values[1], values[2])));
    }
}

internal static class VectorDrawerRow
{
    public static Control Create(
        IReadOnlyList<(string Axis, float Value, Color Colour)> components,
        Action<float[]> changed)
    {
        TableLayoutPanel row = new()
        {
            ColumnCount = components.Count,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        float[] values = components.Select(component => component.Value).ToArray();
        bool syncing = false;
        for (int index = 0; index < components.Count; index++)
        {
            int captured = index;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / components.Count));
            TableLayoutPanel axis = new()
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Margin = new Padding(index == 0 ? 0 : 3, 0, 0, 0),
                RowCount = 1,
            };
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label label = new()
            {
                Cursor = Cursors.SizeWE,
                Dock = DockStyle.Fill,
                Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
                ForeColor = components[index].Colour,
                Text = components[index].Axis,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            NumericUpDown number = new()
            {
                DecimalPlaces = 3,
                Dock = DockStyle.Fill,
                Increment = 0.05m,
                Minimum = -1_000_000m,
                Maximum = 1_000_000m,
                Value = Math.Clamp((decimal)components[index].Value, -1_000_000m, 1_000_000m),
            };
            number.ValueChanged += (_, _) =>
            {
                if (syncing) return;
                values[captured] = (float)number.Value;
                changed([.. values]);
            };
            Point dragOrigin = Point.Empty;
            decimal dragValue = 0m;
            label.MouseDown += (_, args) =>
            {
                if (args.Button != MouseButtons.Left) return;
                dragOrigin = label.PointToScreen(args.Location);
                dragValue = number.Value;
                label.Capture = true;
            };
            label.MouseMove += (_, args) =>
            {
                if (!label.Capture || (args.Button & MouseButtons.Left) == 0) return;
                int delta = label.PointToScreen(args.Location).X - dragOrigin.X;
                decimal next = Math.Clamp(dragValue + (delta * number.Increment), number.Minimum, number.Maximum);
                syncing = true;
                number.Value = next;
                syncing = false;
                values[captured] = (float)next;
                changed([.. values]);
            };
            label.MouseUp += (_, _) => label.Capture = false;
            axis.Controls.Add(label, 0, 0);
            axis.Controls.Add(number, 1, 0);
            row.Controls.Add(axis, index, 0);
        }
        return row;
    }
}

public sealed class IntegralDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => DrawerTypes.IsIntegral(context.PropertyType);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        decimal minimum = context.Minimum ?? -1_000_000m;
        decimal maximum = context.Maximum ?? 1_000_000m;
        decimal value = DrawerTypes.DecimalValue(context.InitialValue, 0m, minimum, maximum);
        NumericUpDown numeric = new()
        {
            DecimalPlaces = 0,
            Increment = Math.Max(1m, context.Increment ?? 1m),
            Maximum = maximum,
            Minimum = minimum,
            Value = value,
        };
        numeric.ValueChanged += (_, _) => context.ValueChanged(
            DrawerTypes.ConvertIntegral(numeric.Value, context.PropertyType));
        return numeric;
    }
}

public sealed class FloatingPointDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => DrawerTypes.IsFloatingPoint(context.PropertyType);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        decimal minimum = context.Minimum ?? -1_000_000m;
        decimal maximum = context.Maximum ?? 1_000_000m;
        NumericUpDown numeric = new()
        {
            DecimalPlaces = Math.Clamp(context.DecimalPlaces ?? 3, 0, 8),
            Increment = Math.Max(0.00000001m, context.Increment ?? 0.05m),
            Maximum = maximum,
            Minimum = minimum,
            Value = DrawerTypes.DecimalValue(context.InitialValue, 0m, minimum, maximum),
        };
        numeric.ValueChanged += (_, _) => context.ValueChanged(
            DrawerTypes.ConvertFloatingPoint(numeric.Value, context.PropertyType));
        return numeric;
    }
}

/// <summary>Coupled slider/input used when the resource declares a meaningful finite range.</summary>
public sealed class RangedNumericDrawer : IPropertyDrawer
{
    private const int SliderSteps = 1000;

    public bool CanDraw(PropertyDrawerContext context) =>
        (DrawerTypes.IsIntegral(context.PropertyType) || DrawerTypes.IsFloatingPoint(context.PropertyType))
        && context.Minimum.HasValue
        && context.Maximum.HasValue
        && context.Maximum.Value > context.Minimum.Value;

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        decimal minimum = context.Minimum!.Value;
        decimal maximum = context.Maximum!.Value;
        decimal current = DrawerTypes.DecimalValue(context.InitialValue, minimum, minimum, maximum);
        bool integral = DrawerTypes.IsIntegral(context.PropertyType);
        TableLayoutPanel row = new()
        {
            ColumnCount = 2,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        TrackBar slider = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            LargeChange = 100,
            Maximum = SliderSteps,
            Minimum = 0,
            SmallChange = 10,
            TickStyle = TickStyle.None,
            Value = ToSlider(current, minimum, maximum),
        };
        NumericUpDown number = new()
        {
            DecimalPlaces = integral ? 0 : Math.Clamp(context.DecimalPlaces ?? 3, 0, 8),
            Dock = DockStyle.Fill,
            Increment = integral ? Math.Max(1m, context.Increment ?? 1m) : Math.Max(0.00000001m, context.Increment ?? 0.05m),
            Maximum = maximum,
            Minimum = minimum,
            Value = current,
        };
        bool syncing = false;
        void Commit(decimal value)
        {
            context.ValueChanged(integral
                ? DrawerTypes.ConvertIntegral(value, context.PropertyType)
                : DrawerTypes.ConvertFloatingPoint(value, context.PropertyType));
        }
        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            decimal value = FromSlider(slider.Value, minimum, maximum);
            if (integral) value = decimal.Round(value, 0);
            number.Value = Math.Clamp(value, number.Minimum, number.Maximum);
            syncing = false;
            Commit(number.Value);
        };
        number.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            slider.Value = ToSlider(number.Value, minimum, maximum);
            syncing = false;
            Commit(number.Value);
        };
        row.Controls.Add(slider, 0, 0);
        row.Controls.Add(number, 1, 0);
        return row;
    }

    private static int ToSlider(decimal value, decimal minimum, decimal maximum) =>
        (int)Math.Clamp(decimal.Round((value - minimum) / (maximum - minimum) * SliderSteps), 0, SliderSteps);

    private static decimal FromSlider(int value, decimal minimum, decimal maximum) =>
        minimum + (maximum - minimum) * value / SliderSteps;
}

public sealed class ChoiceDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) =>
        context.PropertyType.IsEnum || context.Choices is { Count: > 0 };

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        ComboBox combo = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        IEnumerable<string> choices = context.Choices is { Count: > 0 }
            ? context.Choices
            : Enum.GetNames(context.PropertyType);
        combo.Items.AddRange(choices.Cast<object>().ToArray());
        string current = Convert.ToString(context.InitialValue, CultureInfo.InvariantCulture) ?? string.Empty;
        if (current.Length > 0 && !combo.Items.Cast<object>().Any(item =>
                string.Equals(item.ToString(), current, StringComparison.OrdinalIgnoreCase)))
        {
            combo.Items.Insert(0, current);
        }
        combo.SelectedItem = combo.Items.Cast<object>().FirstOrDefault(item =>
            string.Equals(item.ToString(), current, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChangeCommitted += (_, _) =>
        {
            string selected = combo.SelectedItem?.ToString() ?? string.Empty;
            context.ValueChanged(context.PropertyType.IsEnum
                ? Enum.Parse(context.PropertyType, selected, ignoreCase: true)
                : selected);
        };
        return combo;
    }
}

public sealed class BoolDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => context.PropertyType == typeof(bool);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        CheckBox check = new()
        {
            Checked = context.InitialValue is bool value && value,
            AutoSize = true,
            Dock = DockStyle.Left,
            Text = string.Empty,
        };
        check.CheckedChanged += (_, _) => context.ValueChanged(check.Checked);
        return check;
    }
}

public sealed class StringDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => context.PropertyType == typeof(string);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        TextBox text = new()
        {
            Text = Convert.ToString(context.InitialValue, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        void Commit() => context.ValueChanged(text.Text);
        text.Leave += (_, _) => Commit();
        text.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            Commit();
            args.SuppressKeyPress = true;
        };
        return text;
    }
}

public sealed class ColorDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) => context.PropertyType == typeof(Color);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        Color value = context.InitialValue is Color color ? color : Color.White;
        Button button = new()
        {
            AutoEllipsis = true,
            BackColor = value,
            ForeColor = value.GetBrightness() < 0.5f ? Color.White : Color.Black,
            FlatStyle = FlatStyle.Flat,
            Tag = "property-color",
            Text = ToHex(value),
            UseVisualStyleBackColor = false,
        };
        button.Click += (_, _) =>
        {
            using ColorDialog dialog = new() { Color = button.BackColor, FullOpen = true };
            if (dialog.ShowDialog(context.DialogOwner) != DialogResult.OK) return;
            value = dialog.Color;
            button.BackColor = value;
            button.ForeColor = value.GetBrightness() < 0.5f ? Color.White : Color.Black;
            button.Text = ToHex(value);
            context.ValueChanged(value);
        };
        return button;
    }

    private static string ToHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}

public sealed class AssetReferenceDrawer : IPropertyDrawer
{
    public bool CanDraw(PropertyDrawerContext context) =>
        context.PropertyType == typeof(string)
        && context.AssetKind.HasValue
        && !string.IsNullOrWhiteSpace(context.ProjectRoot);

    public Control CreateDrawer(PropertyDrawerContext context)
    {
        TableLayoutPanel row = new()
        {
            ColumnCount = 2,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        TextBox value = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Text = ResourceNames.Name(context.ProjectRoot!, Convert.ToString(context.InitialValue, CultureInfo.InvariantCulture) ?? string.Empty),
        };
        Button browse = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 0, 0, 0),
            Text = "…",
        };
        browse.Click += (_, _) =>
        {
            ProjectAssetEntry? selected = AssetPickerService.PickAsset(
                new AssetPickerRequest(
                    context.ProjectRoot!,
                    context.AssetKind!.Value,
                    value.Text),
                context.DialogOwner ?? browse.FindForm());
            if (selected is null) return;
            value.Text = selected.Reference;
            context.ValueChanged(value.Text);
        };
        row.Controls.Add(value, 0, 0);
        row.Controls.Add(browse, 1, 0);
        return row;
    }
}

internal static class DrawerTypes
{
    public static bool IsIntegral(Type type) =>
        type == typeof(byte) || type == typeof(sbyte)
        || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong);

    public static bool IsFloatingPoint(Type type) =>
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    public static decimal DecimalValue(object? value, decimal fallback, decimal minimum, decimal maximum)
    {
        try
        {
            return Math.Clamp(Convert.ToDecimal(value, CultureInfo.InvariantCulture), minimum, maximum);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return Math.Clamp(fallback, minimum, maximum);
        }
    }

    public static object ConvertIntegral(decimal value, Type type)
    {
        if (type == typeof(byte)) return decimal.ToByte(value);
        if (type == typeof(sbyte)) return decimal.ToSByte(value);
        if (type == typeof(short)) return decimal.ToInt16(value);
        if (type == typeof(ushort)) return decimal.ToUInt16(value);
        if (type == typeof(uint)) return decimal.ToUInt32(value);
        if (type == typeof(long)) return decimal.ToInt64(value);
        if (type == typeof(ulong)) return decimal.ToUInt64(value);
        return decimal.ToInt32(value);
    }

    public static object ConvertFloatingPoint(decimal value, Type type)
    {
        if (type == typeof(float)) return decimal.ToSingle(value);
        if (type == typeof(double)) return decimal.ToDouble(value);
        return value;
    }
}

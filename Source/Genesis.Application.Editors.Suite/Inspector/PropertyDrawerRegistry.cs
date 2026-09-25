using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Inspector;

/// <summary>
/// Maintains all registered IPropertyDrawers and resolves the correct drawer for a given type.
/// </summary>
public static class PropertyDrawerRegistry
{
    private static readonly List<IPropertyDrawer> Drawers = [];
    private sealed class RefreshState { public bool Updating; }
    private static readonly ConditionalWeakTable<Control, RefreshState> RefreshStates = new();

    static PropertyDrawerRegistry()
    {
        Register(new Vector2Drawer());
        Register(new Vector3Drawer());
        Register(new BoolDrawer());
        Register(new IntegralDrawer());
        Register(new FloatingPointDrawer());
        Register(new RangedNumericDrawer());
        Register(new StringDrawer());
        Register(new ColorDrawer());
        Register(new ChoiceDrawer());
        Register(new AssetReferenceDrawer());
    }

    public static void Register(IPropertyDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        if (!Drawers.Contains(drawer))
        {
            Drawers.Add(drawer);
        }
    }

    public static IPropertyDrawer? Resolve(PropertyDrawerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Newer registrations override defaults, allowing plugins/editor modules to specialise a
        // field without replacing this central registry.
        for (int index = Drawers.Count - 1; index >= 0; index--)
        {
            if (Drawers[index].CanDraw(context))
            {
                return Drawers[index];
            }
        }
        return null;
    }

    public static Control CreateControl(PropertyDrawerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RefreshState state = new();
        Action<object?> changed = context.ValueChanged;
        context = context with { ValueChanged = value => { if (!state.Updating) changed(value); } };
        IPropertyDrawer? drawer = Resolve(context);
        Control control;
        if (drawer is not null)
        {
            control = drawer.CreateDrawer(context);
        }
        else
        {
            control = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Text = context.InitialValue?.ToString() ?? $"Unsupported {context.PropertyType.Name}",
                TextAlign = ContentAlignment.MiddleLeft,
            };
        }

        if (!string.IsNullOrWhiteSpace(context.ControlName)) control.Name = context.ControlName;
        control.Enabled = !context.ReadOnly;
        if (!string.IsNullOrWhiteSpace(context.Description))
        {
            control.AccessibleDescription = context.Description;
        }
        RefreshStates.Add(control, state);
        return control;
    }

    /// <summary>
    /// Update a drawer without recreating controls or routing a second edit back to the model.
    /// Vector and ranged drawers still run their internal synchronisation so the next user edit
    /// starts with the refreshed axes/slider, not the values captured when the drawer was built.
    /// </summary>
    public static bool RefreshValue(Control control, object? value)
    {
        if (control.IsDisposed) return false;
        RefreshState state = RefreshStates.GetValue(control, _ => new RefreshState());
        bool wasUpdating = state.Updating;
        state.Updating = true;
        try
        {
            if (value is Vector2 vector2)
                return SetAxes(control, [vector2.X, vector2.Y]);
            if (value is Vector3 vector3)
                return SetAxes(control, [vector3.X, vector3.Y, vector3.Z]);
            switch (control)
            {
                case NumericUpDown number:
                    decimal next = DrawerTypes.DecimalValue(value, number.Value, number.Minimum, number.Maximum);
                    if (number.Value != next) number.Value = next;
                    return true;
                case CheckBox check when value is bool flag:
                    if (check.Checked != flag) check.Checked = flag;
                    return true;
                case ComboBox combo:
                    string choice = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    object? item = combo.Items.Cast<object>().FirstOrDefault(candidate =>
                        string.Equals(candidate.ToString(), choice, StringComparison.OrdinalIgnoreCase));
                    if (item is not null && !Equals(combo.SelectedItem, item)) combo.SelectedItem = item;
                    return true;
                case TextBoxBase text:
                    // Do not replace uncommitted typing or move its caret during an external refresh.
                    if (text.ContainsFocus && !text.ReadOnly && text.Modified) return true;
                    string content = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (text.Text != content) text.Text = content;
                    return true;
                case Button button when value is Color color:
                    button.BackColor = color;
                    button.ForeColor = color.GetBrightness() < .5f ? Color.White : Color.Black;
                    button.Text = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
                    return true;
                case Label label:
                    string labelText = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (label.Text != labelText) label.Text = labelText;
                    return true;
            }
            // Coupled slider/input and asset-reference drawers have a single authoritative field.
            Control? child = Descendants(control).FirstOrDefault(candidate => candidate is NumericUpDown)
                ?? Descendants(control).FirstOrDefault(candidate => candidate is TextBoxBase);
            return child is not null && RefreshValue(child, value);
        }
        finally { state.Updating = wasUpdating; }
    }

    private static bool SetAxes(Control control, IReadOnlyList<float> values)
    {
        NumericUpDown[] axes = Descendants(control).OfType<NumericUpDown>().ToArray();
        if (axes.Length != values.Count) return false;
        for (int index = 0; index < axes.Length; index++) RefreshValue(axes[index], values[index]);
        return true;
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>Compatibility entry point for older reflective surfaces.</summary>
    public static Control CreateControl(
        Type type,
        string propertyName,
        object? initialValue,
        Action<object> onValueChanged) =>
        CreateControl(new PropertyDrawerContext(
            propertyName,
            type,
            initialValue,
            value => onValueChanged(value ?? DefaultValue(type))));

    private static object DefaultValue(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type)! : string.Empty;
}

using System.Globalization;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

// How an element is laid out and named in the Inspector: a Panel's or Scroll List's Layout, Spacing
// and Padding, the Padding of boxes that hold text, the Style an element takes from the UI's theme,
// and a Text Input's Placeholder and Max length. The game reads the same document fields, and the
// canvas places and paints with the same layout code (UiLayoutPlacement).
public sealed partial class UiEditorControl
{
    private readonly ComboBox _layout = new UiKit.ThemedComboBox();
    private readonly NumericUpDown _spacing = Number(0, 100000, 0);
    private readonly NumericUpDown _padding = Number(0, 100000, 0);
    private readonly ComboBox _styleName = new UiKit.ThemedComboBox();
    private readonly TextBox _placeholder = new();
    private readonly NumericUpDown _maxLength = Number(0, 100000, 0);
    private Control? _layoutRow, _spacingRow, _paddingRow, _styleRow, _placeholderRow, _maxLengthRow;

    /// <summary>The element types whose children a Layout places one after another.</summary>
    private static bool IsContainer(UiElementType type) => type is UiElementType.Panel or UiElementType.ScrollList;

    /// <summary>Where Padding means something: a container's inner edge, and the edge text keeps from its box.</summary>
    private static bool HasPadding(UiElementType type) => IsContainer(type) || HasText(type);

    private void AddContainerFields(TableLayoutPanel fields)
    {
        _layout.Name = "UiLayout"; _spacing.Name = "UiSpacing"; _padding.Name = "UiPadding";
        fields.Controls.Add(_layoutRow = Field("Layout", _layout));
        fields.Controls.Add(_spacingRow = Field("Spacing between children", _spacing));
        fields.Controls.Add(_paddingRow = Field("Padding", _padding));
        foreach (Control control in new Control[] { _layout, _spacing, _padding }) EditorChrome.StyleField(control);
        _layout.DropDownStyle = ComboBoxStyle.DropDownList;
        _layout.DataSource = Enum.GetValues<UiLayoutMode>();
        _layout.SelectedIndexChanged += (_, _) => ApplyInspector();
        _spacing.ValueChanged += (_, _) => ApplyInspector();
        _padding.ValueChanged += (_, _) => ApplyInspector();
    }

    private void AddTextInputFields(TableLayoutPanel fields)
    {
        _placeholder.Name = "UiPlaceholder"; _maxLength.Name = "UiMaxLength";
        _maxLength.DecimalPlaces = 0;
        fields.Controls.Add(_placeholderRow = Field("Placeholder", _placeholder));
        fields.Controls.Add(_maxLengthRow = Field("Max length (0 = any)", _maxLength));
        foreach (Control control in new Control[] { _placeholder, _maxLength }) EditorChrome.StyleField(control);
        _placeholder.TextChanged += (_, _) => ApplyInspector();
        _maxLength.ValueChanged += (_, _) => ApplyInspector();
    }

    private void AddThemeStyleField(TableLayoutPanel fields)
    {
        _styleName.Name = "UiStyle";
        fields.Controls.Add(_styleRow = Field("Style (theme)", _styleName));
        EditorChrome.StyleField(_styleName);
        // Any name may be typed: a theme another UI brings at run time (UiSetTheme) can hold styles this one does not.
        _styleName.DropDownStyle = ComboBoxStyle.DropDown;
        _styleName.TextChanged += (_, _) => ApplyInspector();
    }

    private void SetStructureContextualFields(UiElementType? type)
    {
        bool container = type.HasValue && IsContainer(type.Value);
        foreach (Control? row in new[] { _layoutRow, _spacingRow })
            if (row is not null) row.Visible = container;
        if (_paddingRow is not null) _paddingRow.Visible = type.HasValue && HasPadding(type.Value);
        if (_styleRow is not null) _styleRow.Visible = type.HasValue;
        foreach (Control? row in new[] { _placeholderRow, _maxLengthRow })
            if (row is not null) row.Visible = type == UiElementType.TextInput;
    }

    private void RefreshStructureFields(UiElement element)
    {
        _layout.SelectedItem = element.Layout;
        _spacing.Value = Clamp(_spacing, element.Spacing);
        _padding.Value = Clamp(_padding, element.Padding);
        _placeholder.Text = element.Placeholder;
        _maxLength.Value = Math.Clamp(element.MaxLength, (int)_maxLength.Minimum, (int)_maxLength.Maximum);
        _styleName.Items.Clear();
        _styleName.Items.AddRange(ThemeStyleNames(element).Select(name => (object)name).ToArray());
        _styleName.Text = element.Style;
    }

    private void ApplyStructureFields(UiElement element)
    {
        if (IsContainer(element.Type))
        {
            element.Layout = _layout.SelectedItem is UiLayoutMode mode ? mode : UiLayoutMode.None;
            element.Spacing = (float)_spacing.Value;
        }
        if (HasPadding(element.Type)) element.Padding = (float)_padding.Value;
        element.Style = _styleName.Text.Trim();
        if (element.Type == UiElementType.TextInput)
        {
            element.Placeholder = _placeholder.Text;
            element.MaxLength = (int)_maxLength.Value;
        }
    }

    /// <summary>The styles of this UI's theme in name order, an empty name (no style) first, and the name the element has if the theme lacks it.</summary>
    private string[] ThemeStyleNames(UiElement element)
    {
        List<string> names = [string.Empty];
        if (_document.Styles is { } styles) names.AddRange(styles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        if (element.Style.Length > 0 && !names.Contains(element.Style, StringComparer.OrdinalIgnoreCase)) names.Add(element.Style);
        return [.. names];
    }

    private IEnumerable<ResourceInspectorLiveValue> StructureLiveValues(UiElement element)
    {
        yield return new("UI layout", "Ui.Layout", "Layout", element.Layout.ToString(), Choices: Enum.GetNames<UiLayoutMode>());
        yield return new("UI layout", "Ui.Spacing", "Spacing", element.Spacing, Minimum: 0, Maximum: 100000);
        yield return new("UI layout", "Ui.Padding", "Padding", element.Padding, Minimum: 0, Maximum: 100000);
        string[] styles = ThemeStyleNames(element);
        yield return new("UI theme", "Ui.Style", "Style", element.Style, Choices: styles.Length > 1 ? styles : null);
        yield return new("UI text input", "Ui.Placeholder", "Placeholder", element.Placeholder);
        yield return new("UI text input", "Ui.MaxLength", "Max length", element.MaxLength, Minimum: 0, Maximum: 100000, DecimalPlaces: 0);
    }

    /// <summary>Applies one of these properties; null when the path is not one of them.</summary>
    private static bool? TryApplyStructureValue(UiElement element, string propertyPath, string text)
    {
        bool number = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scalar) && float.IsFinite(scalar);
        switch (propertyPath)
        {
            case "Ui.Layout":
                if (!Enum.TryParse(text, true, out UiLayoutMode mode) || !Enum.IsDefined(mode)) return false;
                element.Layout = mode; return true;
            case "Ui.Spacing": if (!number || scalar is < 0 or > 100000) return false; element.Spacing = scalar; return true;
            case "Ui.Padding": if (!number || scalar is < 0 or > 100000) return false; element.Padding = scalar; return true;
            case "Ui.Style": element.Style = text.Trim(); return true;
            case "Ui.Placeholder": element.Placeholder = text; return true;
            case "Ui.MaxLength":
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length is < 0 or > 100000) return false;
                element.MaxLength = length; return true;
            default: return null;
        }
    }
}

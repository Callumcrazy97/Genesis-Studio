using System.Globalization;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

// Menu styling in the Inspector: corners and borders, gradient fills, text alignment and letter
// spacing, slider/toggle ranges, image fit and crop, and the hover/pressed/selected/disabled looks.
public sealed partial class UiEditorControl
{
    private const string NormalState = "Normal";
    private readonly NumericUpDown _cornerRadius = Number(0, 4096, 0);
    private readonly NumericUpDown _borderWidth = Number(0, 512, 0);
    private readonly Button _borderColor = new() { Text = "Border…" };
    private readonly ComboBox _fill = new UiKit.ThemedComboBox();
    private readonly Button _gradientEnd = new() { Text = "Gradient end…" };
    private readonly ComboBox _textAlign = new UiKit.ThemedComboBox();
    private readonly ComboBox _textVerticalAlign = new UiKit.ThemedComboBox();
    private readonly NumericUpDown _letterSpacing = Number(-64, 256, 0);
    private readonly NumericUpDown _minimum = Number(-100000, 100000, 0);
    private readonly NumericUpDown _step = Number(0, 100000, 0);
    private readonly ComboBox _imageFit = new UiKit.ThemedComboBox();
    private readonly NumericUpDown _cropX = Number(0, 1, 0);
    private readonly NumericUpDown _cropY = Number(0, 1, 0);
    private readonly NumericUpDown _cropWidth = Number(.001m, 1, 1);
    private readonly NumericUpDown _cropHeight = Number(.001m, 1, 1);
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly ComboBox _stateChoice = new UiKit.ThemedComboBox();
    private readonly Button _stateBackground = new() { Text = "Background…" };
    private readonly Button _stateForeground = new() { Text = "Text…" };
    private readonly Button _stateBorder = new() { Text = "Border…" };
    private readonly Button _stateClear = new() { Text = "Clear look" };
    private Control? _cornerRow, _borderRow, _borderColorRow, _fillRow, _gradientEndRow, _alignRow, _letterSpacingRow;
    private Control? _rangeRow, _imageFitRow, _cropPositionRow, _cropSizeRow, _stateRow, _stateColoursRow;

    /// <summary>The state the canvas shows the selected element in, chosen in the Inspector's Look list.</summary>
    public string PreviewState => _stateChoice.SelectedItem as string is { } state && state != NormalState ? state : string.Empty;

    private void AddStyleFields(TableLayoutPanel fields)
    {
        foreach (Button button in StyleColourButtons) button.FlatStyle = FlatStyle.Flat;
        fields.Controls.Add(_cornerRow = Field("Corner radius", _cornerRadius));
        fields.Controls.Add(_borderRow = Field("Border width", _borderWidth));
        fields.Controls.Add(_borderColorRow = Field("Border colour", _borderColor));
        fields.Controls.Add(_fillRow = Field("Fill", _fill));
        fields.Controls.Add(_gradientEndRow = Field("Gradient end colour", _gradientEnd));
        fields.Controls.Add(_alignRow = Pair("Text alignment", _textAlign, _textVerticalAlign, "Across", "Down"));
        fields.Controls.Add(_letterSpacingRow = Field("Letter spacing", _letterSpacing));
        fields.Controls.Add(_rangeRow = Pair("Range", _minimum, _step, "Minimum", "Step"));
        fields.Controls.Add(_imageFitRow = Field("Image fit", _imageFit));
        fields.Controls.Add(_cropPositionRow = Pair("Crop start (0–1)", _cropX, _cropY, "X", "Y"));
        fields.Controls.Add(_cropSizeRow = Pair("Crop size (0–1)", _cropWidth, _cropHeight, "W", "H"));
        fields.Controls.Add(_enabled);
        fields.Controls.Add(_stateRow = Field("Look (state)", _stateChoice));
        TableLayoutPanel stateColours = new() { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1 };
        for (int i = 0; i < 4; i++) stateColours.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        stateColours.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (Button button in new[] { _stateBackground, _stateForeground, _stateBorder, _stateClear })
        {
            button.Dock = DockStyle.Fill; button.FlatStyle = FlatStyle.Flat; button.Margin = new Padding(0, 0, 3, 0);
            stateColours.Controls.Add(button);
        }
        fields.Controls.Add(_stateColoursRow = Field("State colours", stateColours));

        foreach (NumericUpDown number in new[] { _cropX, _cropY, _cropWidth, _cropHeight }) { number.DecimalPlaces = 3; number.Increment = .05m; }
        _step.DecimalPlaces = 2;
        foreach (Control control in new Control[] { _cornerRadius, _borderWidth, _fill, _textAlign, _textVerticalAlign, _letterSpacing,
                     _minimum, _step, _imageFit, _cropX, _cropY, _cropWidth, _cropHeight, _enabled, _stateChoice })
            EditorChrome.StyleField(control);
        foreach (ComboBox combo in new[] { _fill, _textAlign, _textVerticalAlign, _imageFit, _stateChoice }) combo.DropDownStyle = ComboBoxStyle.DropDownList;
        _fill.DataSource = Enum.GetValues<UiFill>();
        _textAlign.DataSource = Enum.GetValues<UiHorizontalAlign>();
        _textVerticalAlign.DataSource = Enum.GetValues<UiVerticalAlign>();
        _imageFit.DataSource = Enum.GetValues<UiImageFit>();
        _stateChoice.Items.Add(NormalState);
        _stateChoice.Items.AddRange(UiElement.StateNames);
        _stateChoice.SelectedIndex = 0;

        foreach (NumericUpDown number in new[] { _cornerRadius, _borderWidth, _letterSpacing, _minimum, _step, _cropX, _cropY, _cropWidth, _cropHeight })
            number.ValueChanged += (_, _) => ApplyInspector();
        foreach (ComboBox combo in new[] { _fill, _textAlign, _textVerticalAlign, _imageFit })
            combo.SelectedIndexChanged += (_, _) => ApplyInspector();
        _enabled.CheckedChanged += (_, _) => ApplyInspector();
        _stateChoice.SelectedIndexChanged += (_, _) =>
        {
            _canvas.PreviewState = PreviewState;
            if (!_syncing) RefreshInspector();
        };
        _borderColor.Click += (_, _) => PickColor("BorderColor");
        _gradientEnd.Click += (_, _) => PickColor("GradientEnd");
        _stateBackground.Click += (_, _) => PickStateColour("Background");
        _stateForeground.Click += (_, _) => PickStateColour("Foreground");
        _stateBorder.Click += (_, _) => PickStateColour("BorderColor");
        _stateClear.Click += (_, _) =>
        {
            if (_canvas.SelectedElement is UiElement element && PreviewState.Length > 0 && element.ClearStateStyle(PreviewState))
            { RefreshInspector(); _canvas.Invalidate(); JournalChange("Clear UI state look"); }
        };
    }

    private IEnumerable<Button> StyleColourButtons => [_borderColor, _gradientEnd, _stateBackground, _stateForeground, _stateBorder];

    private static bool HasBox(UiElementType type) => type is UiElementType.Panel or UiElementType.Button or UiElementType.ProgressBar
        or UiElementType.Slider or UiElementType.Toggle;

    private static bool HasRange(UiElementType type) => type is UiElementType.ProgressBar or UiElementType.Slider or UiElementType.Toggle;

    /// <summary>The border width an element draws when none is authored: buttons and progress bars outline one pixel.</summary>
    private static float EffectiveBorderWidth(UiElement element) =>
        element.BorderWidth ?? (element.Type is UiElementType.Button or UiElementType.ProgressBar ? 1f : 0f);

    private void SetStyleContextualFields(UiElementType? type)
    {
        bool box = type.HasValue && HasBox(type.Value);
        foreach (Control? row in new[] { _cornerRow, _borderRow, _borderColorRow, _fillRow, _gradientEndRow })
            if (row is not null) row.Visible = box;
        bool text = type.HasValue && HasText(type.Value);
        if (_alignRow is not null) _alignRow.Visible = text;
        if (_letterSpacingRow is not null) _letterSpacingRow.Visible = text;
        if (_rangeRow is not null) _rangeRow.Visible = type == UiElementType.Slider;
        foreach (Control? row in new[] { _imageFitRow, _cropPositionRow, _cropSizeRow })
            if (row is not null) row.Visible = type == UiElementType.Image;
        _enabled.Visible = type.HasValue;
        if (_stateRow is not null) _stateRow.Visible = type.HasValue;
        if (_stateColoursRow is not null) _stateColoursRow.Visible = type.HasValue && PreviewState.Length > 0;
    }

    private void RefreshStyleFields(UiElement element)
    {
        _cornerRadius.Value = Clamp(_cornerRadius, element.CornerRadius);
        _borderWidth.Value = Clamp(_borderWidth, EffectiveBorderWidth(element));
        _fill.SelectedItem = element.Fill;
        _textAlign.SelectedItem = element.TextAlign;
        _textVerticalAlign.SelectedItem = element.TextVerticalAlign;
        _letterSpacing.Value = Clamp(_letterSpacing, element.LetterSpacing);
        _minimum.Value = Clamp(_minimum, element.Minimum);
        _step.Value = Clamp(_step, element.Step);
        _imageFit.SelectedItem = element.ImageFit;
        _cropX.Value = Clamp(_cropX, element.CropX);
        _cropY.Value = Clamp(_cropY, element.CropY);
        _cropWidth.Value = Clamp(_cropWidth, element.CropWidth);
        _cropHeight.Value = Clamp(_cropHeight, element.CropHeight);
        _enabled.Checked = element.Enabled;
        UiLook look = UiLook.Resolve(element, string.Empty);
        _borderColor.BackColor = look.Border;
        _gradientEnd.BackColor = look.GradientEnd;
        UiLook state = UiLook.Resolve(element, PreviewState);
        _stateBackground.BackColor = state.Background;
        _stateForeground.BackColor = state.Foreground;
        _stateBorder.BackColor = state.Border;
        _stateClear.Enabled = element.StateStyle(PreviewState) is not null;
    }

    private void ApplyStyleFields(UiElement element)
    {
        if (HasBox(element.Type))
        {
            element.CornerRadius = (float)_cornerRadius.Value;
            float border = (float)_borderWidth.Value;
            if (border != EffectiveBorderWidth(element)) element.BorderWidth = border;
            element.Fill = _fill.SelectedItem is UiFill fill ? fill : UiFill.Solid;
        }
        if (HasText(element.Type))
        {
            element.TextAlign = _textAlign.SelectedItem is UiHorizontalAlign align ? align : UiHorizontalAlign.Auto;
            element.TextVerticalAlign = _textVerticalAlign.SelectedItem is UiVerticalAlign vertical ? vertical : UiVerticalAlign.Auto;
            element.LetterSpacing = (float)_letterSpacing.Value;
        }
        if (element.Type == UiElementType.Slider)
        {
            float minimum = (float)_minimum.Value;
            if (minimum < element.Maximum) element.Minimum = minimum;
            element.Step = (float)_step.Value;
        }
        if (element.Type == UiElementType.Image)
        {
            element.ImageFit = _imageFit.SelectedItem is UiImageFit fit ? fit : UiImageFit.Stretch;
            element.CropX = (float)_cropX.Value; element.CropY = (float)_cropY.Value;
            element.CropWidth = (float)_cropWidth.Value; element.CropHeight = (float)_cropHeight.Value;
        }
        element.Enabled = _enabled.Checked;
    }

    private void PickStateColour(string property)
    {
        if (_canvas.SelectedElement is not UiElement element || PreviewState.Length == 0) return;
        UiLook current = UiLook.Resolve(element, PreviewState);
        Color original = property switch { "Background" => current.Background, "Foreground" => current.Foreground, _ => current.Border };
        using ColorDialog dialog = new() { FullOpen = true, Color = original };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        TryApplyLiveInspectorValue("Ui." + PreviewState + "." + property, "#" + dialog.Color.ToArgb().ToString("X8"));
    }

    private IEnumerable<ResourceInspectorLiveValue> StyleLiveValues(UiElement element)
    {
        yield return new("UI style", "Ui.CornerRadius", "Corner radius", element.CornerRadius, Minimum: 0, Maximum: 4096);
        yield return new("UI style", "Ui.BorderWidth", "Border width", EffectiveBorderWidth(element), Minimum: 0, Maximum: 512);
        yield return new("UI style", "Ui.BorderColor", "Border colour", element.BorderColor);
        yield return new("UI style", "Ui.Fill", "Fill", element.Fill.ToString(), Choices: Enum.GetNames<UiFill>());
        yield return new("UI style", "Ui.GradientEnd", "Gradient end colour", element.GradientEnd);
        yield return new("UI style", "Ui.TextAlign", "Text across", element.TextAlign.ToString(), Choices: Enum.GetNames<UiHorizontalAlign>());
        yield return new("UI style", "Ui.TextVerticalAlign", "Text down", element.TextVerticalAlign.ToString(), Choices: Enum.GetNames<UiVerticalAlign>());
        yield return new("UI style", "Ui.LetterSpacing", "Letter spacing", element.LetterSpacing, Minimum: -64, Maximum: 256);
        yield return new("UI style", "Ui.Minimum", "Minimum", element.Minimum, Minimum: -100000, Maximum: 100000);
        yield return new("UI style", "Ui.Step", "Step", element.Step, Minimum: 0, Maximum: 100000);
        yield return new("UI style", "Ui.ImageFit", "Image fit", element.ImageFit.ToString(), Choices: Enum.GetNames<UiImageFit>());
        yield return new("UI style", "Ui.CropX", "Crop X", element.CropX, Minimum: 0, Maximum: 1);
        yield return new("UI style", "Ui.CropY", "Crop Y", element.CropY, Minimum: 0, Maximum: 1);
        yield return new("UI style", "Ui.CropWidth", "Crop width", element.CropWidth, Minimum: .001m, Maximum: 1);
        yield return new("UI style", "Ui.CropHeight", "Crop height", element.CropHeight, Minimum: .001m, Maximum: 1);
        yield return new("UI style", "Ui.Enabled", "Enabled", element.Enabled);
        foreach (string state in UiElement.StateNames)
        {
            UiStateStyle? style = element.StateStyle(state);
            yield return new("UI " + state.ToLowerInvariant() + " look", "Ui." + state + ".Background", state + " background", style?.Background ?? string.Empty);
            yield return new("UI " + state.ToLowerInvariant() + " look", "Ui." + state + ".GradientEnd", state + " gradient end", style?.GradientEnd ?? string.Empty);
            yield return new("UI " + state.ToLowerInvariant() + " look", "Ui." + state + ".Foreground", state + " text colour", style?.Foreground ?? string.Empty);
            yield return new("UI " + state.ToLowerInvariant() + " look", "Ui." + state + ".Accent", state + " accent", style?.Accent ?? string.Empty);
            yield return new("UI " + state.ToLowerInvariant() + " look", "Ui." + state + ".BorderColor", state + " border colour", style?.BorderColor ?? string.Empty);
        }
    }

    /// <summary>Applies one style property; null when the path is not a style property.</summary>
    private bool? TryApplyStyleValue(UiElement element, string propertyPath, string text)
    {
        bool number = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scalar) && float.IsFinite(scalar);
        bool colour = text.Length == 0 || IsColour(text);
        switch (propertyPath)
        {
            case "Ui.CornerRadius": if (!number || scalar < 0) return false; element.CornerRadius = scalar; return true;
            case "Ui.BorderWidth": if (!number || scalar < 0) return false; element.BorderWidth = scalar; return true;
            case "Ui.BorderColor": if (!colour) return false; element.BorderColor = text; return true;
            case "Ui.Fill": if (!Enum.TryParse(text, true, out UiFill fill) || !Enum.IsDefined(fill)) return false; element.Fill = fill; return true;
            case "Ui.GradientEnd": if (!colour) return false; element.GradientEnd = text; return true;
            case "Ui.TextAlign":
                if (!Enum.TryParse(text, true, out UiHorizontalAlign align) || !Enum.IsDefined(align)) return false;
                element.TextAlign = align; return true;
            case "Ui.TextVerticalAlign":
                if (!Enum.TryParse(text, true, out UiVerticalAlign vertical) || !Enum.IsDefined(vertical)) return false;
                element.TextVerticalAlign = vertical; return true;
            case "Ui.LetterSpacing": if (!number || scalar is < -64 or > 256) return false; element.LetterSpacing = scalar; return true;
            case "Ui.Minimum": if (!number || scalar >= element.Maximum) return false; element.Minimum = scalar; return true;
            case "Ui.Step": if (!number || scalar < 0) return false; element.Step = scalar; return true;
            case "Ui.ImageFit":
                if (!Enum.TryParse(text, true, out UiImageFit fit) || !Enum.IsDefined(fit)) return false;
                element.ImageFit = fit; return true;
            case "Ui.CropX": if (!number || scalar is < 0 or > 1) return false; element.CropX = scalar; return true;
            case "Ui.CropY": if (!number || scalar is < 0 or > 1) return false; element.CropY = scalar; return true;
            case "Ui.CropWidth": if (!number || scalar is <= 0 or > 1) return false; element.CropWidth = scalar; return true;
            case "Ui.CropHeight": if (!number || scalar is <= 0 or > 1) return false; element.CropHeight = scalar; return true;
            case "Ui.Enabled": if (!bool.TryParse(text, out bool enabled)) return false; element.Enabled = enabled; return true;
        }
        string[] parts = propertyPath.Split('.');
        if (parts.Length != 3 || parts[0] != "Ui" || !UiElement.StateNames.Contains(parts[1])) return null;
        if (!colour) return false;
        UiStateStyle style = element.EnsureStateStyle(parts[1]);
        switch (parts[2])
        {
            case "Background": style.Background = text; break;
            case "GradientEnd": style.GradientEnd = text; break;
            case "Foreground": style.Foreground = text; break;
            case "Accent": style.Accent = text; break;
            case "BorderColor": style.BorderColor = text; break;
            default: return false;
        }
        if (style.IsEmpty) element.ClearStateStyle(parts[1]);
        return true;
    }

    private static bool IsStyleApplicable(UiElementType type, string property) => property switch
    {
        "Ui.CornerRadius" or "Ui.BorderWidth" or "Ui.BorderColor" or "Ui.Fill" or "Ui.GradientEnd" => HasBox(type),
        "Ui.TextAlign" or "Ui.TextVerticalAlign" or "Ui.LetterSpacing" => HasText(type),
        "Ui.Minimum" or "Ui.Step" => type == UiElementType.Slider,
        "Ui.ImageFit" or "Ui.CropX" or "Ui.CropY" or "Ui.CropWidth" or "Ui.CropHeight" => type == UiElementType.Image,
        _ => true,
    };
}

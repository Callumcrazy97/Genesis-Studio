using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Docking;

/// <summary>Shared editable identity block shown above every entity-like resource Inspector.</summary>
internal sealed class ResourceIdentityHeader : Panel
{
    private readonly CheckBox _active = new() { Text = string.Empty, Width = 24 };
    private readonly TextBox _name = new() { BorderStyle = BorderStyle.None, ReadOnly = true, AccessibleDescription = "Resource name. Rename with F2 in the resource browser." };
    private readonly CheckBox _static = new() { Text = "Static", Width = 62 };
    private readonly ComboBox _tag = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _layer = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Panel _prefab = new();
    private ResourceInspectorPropertySurface? _surface;
    private ResourceItem? _resource;
    private bool _binding;

    public ResourceIdentityHeader()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Padding = new Padding(10);
        TableLayoutPanel rows = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            RowCount = 3,
        };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        TableLayoutPanel title = new() { ColumnCount = 3, Dock = DockStyle.Fill, RowCount = 1 };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        _active.AccessibleName = "Active";
        _active.Dock = DockStyle.Fill;
        _name.Dock = DockStyle.Fill;
        _static.Dock = DockStyle.Fill;
        title.Controls.Add(_active, 0, 0);
        title.Controls.Add(_name, 1, 0);
        title.Controls.Add(_static, 2, 0);

        TableLayoutPanel classification = new() { ColumnCount = 4, Dock = DockStyle.Fill, RowCount = 1 };
        classification.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        classification.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        classification.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        classification.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        classification.Controls.Add(Caption("Tag"), 0, 0);
        classification.Controls.Add(_tag, 1, 0);
        classification.Controls.Add(Caption("Layer"), 2, 0);
        classification.Controls.Add(_layer, 3, 0);

        _prefab.Dock = DockStyle.Fill;
        Label prefabLabel = Caption("Prefab:");
        prefabLabel.Dock = DockStyle.Left;
        prefabLabel.Width = 50;
        Button open = Button("Open", 58);
        Button select = Button("Select", 62);
        Button overrides = Button("Overrides ▾", 96);
        open.Click += (_, _) => { if (_resource is not null) OpenRequested?.Invoke(this, _resource); };
        select.Click += (_, _) => _name.Focus();
        overrides.Enabled = false;
        _prefab.Controls.Add(overrides);
        _prefab.Controls.Add(select);
        _prefab.Controls.Add(open);
        _prefab.Controls.Add(prefabLabel);
        overrides.Dock = select.Dock = open.Dock = DockStyle.Left;

        rows.Controls.Add(title, 0, 0);
        rows.Controls.Add(classification, 0, 1);
        rows.Controls.Add(_prefab, 0, 2);
        Controls.Add(rows);

        _active.CheckedChanged += (_, _) => Commit("active", _active.Checked);
        _static.CheckedChanged += (_, _) => Commit("static", _static.Checked);
        _tag.Leave += (_, _) => Commit("tag", _tag.Text.Trim());
        _layer.Leave += (_, _) => Commit("layer", _layer.Text.Trim());
        ApplyTheme();
    }

    public event EventHandler<ResourceItem>? OpenRequested;

    internal void ClearSelection()
    {
        _resource = null; _surface = null; Visible = false;
    }

    public void Bind(ResourceItem resource, ResourceInspectorPropertySurface surface)
    {
        _resource = resource;
        _surface = surface;
        bool supported = !resource.IsFolder && resource.Kind is ResourceKind.GameObject or ResourceKind.Room
            or ResourceKind.Model or ResourceKind.Terrain or ResourceKind.TerrainEntity;
        Visible = supported;
        if (!supported) return;
        _binding = true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resource.FullPath));
            JsonElement root = document.RootElement;
            _name.Text = resource.Name;
            _active.Checked = BoolValue(root, "active", true);
            _static.Checked = BoolValue(root, "static", false);
            SetChoice(_tag, TextValue(root, "tag", "Untagged"));
            SetChoice(_layer, TextValue(root, "layer", "Default"));
            _prefab.Visible = resource.Kind == ResourceKind.GameObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _name.Text = ResourceDisplayName.Format(resource.Name);
            _name.AccessibleDescription = exception.Message;
        }
        finally
        {
            _binding = false;
        }
    }

    public void ApplyTheme()
    {
        BackColor = ThemeService.Palette.Surface;
        ForeColor = ThemeService.Palette.Text;
        foreach (Control control in Descendants(this))
        {
            control.ForeColor = control is Label ? ThemeService.Palette.TextMuted : ThemeService.Palette.Text;
            if (control is TextBox or ComboBox) control.BackColor = ThemeService.Palette.SurfaceRaised;
            if (control is Button button)
            {
                button.BackColor = ThemeService.Palette.SurfaceRaised;
                button.FlatAppearance.BorderColor = ThemeService.Palette.Border;
            }
        }
    }

    private void Commit(string path, object value)
    {
        if (!_binding) _surface?.SetValue(path, value);
    }

    private static Label Caption(string text) => new()
    {
        Dock = DockStyle.Fill,
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static Button Button(string text, int width) => new()
    {
        FlatStyle = FlatStyle.Flat,
        Text = text,
        Width = width,
    };

    private static string TextValue(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool BoolValue(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static void SetChoice(ComboBox combo, string value)
    {
        combo.Items.Clear();
        combo.Items.Add(value);
        combo.Text = value;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}

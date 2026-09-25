using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Unity-like selection inspector: identity, transform, assigned assets, condition.</summary>
internal sealed class TerrainSelectionInspector : Panel
{
    private readonly FlowLayoutPanel _stack = new();
    private readonly ResourceInspectorPropertySurface _unifiedSurface = new();
    private readonly TextBox _propertyFilter = new() { PlaceholderText = "Filter properties…" };
    private bool _suspend;

    public TerrainSelectionInspector()
    {
        BackColor = Color.FromArgb(35, 38, 41);
        Dock = DockStyle.Top;
        Height = 340;
        Padding = new Padding(0, 0, 0, 4);
        _stack.AutoScroll = true;
        _stack.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_stack);
        _stack.BackColor = BackColor;
        _stack.Dock = DockStyle.Fill;
        _stack.FlowDirection = FlowDirection.TopDown;
        _stack.Padding = new Padding(6, 6, 6, 8);
        _stack.WrapContents = false;
        void FitRows()
        {
            int width = Math.Max(180, _stack.ClientSize.Width - _stack.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (Control control in _stack.Controls)
            {
                control.Width = width - control.Margin.Horizontal;
                if (control is Label label && label.AutoSize) label.MaximumSize = new Size(control.Width, 0);
            }
        }
        _stack.ControlAdded += (_, _) => FitRows(); _stack.SizeChanged += (_, _) => FitRows();
        Controls.Add(_stack);
    }

    public event EventHandler? EditIdentityRequested;
    public event EventHandler? EditResourcesRequested;
    public event EventHandler? PlaceCopyRequested;
    public event EventHandler? CreateObjectRequested;
    public event EventHandler<string>? OpenAssetRequested;
    public event EventHandler<TerrainSelectionFields>? FieldsChanged;

    public void AddMaterialAction(string image, string description, Action edit)
    {
        _stack.Controls.Add(Group("Material / PBR"));
        _stack.Controls.Add(Hint(string.IsNullOrWhiteSpace(image) ? "No Image assigned" : ResourceDisplayName.Format(image)));
        _stack.Controls.Add(Hint(description));
        var button = new Button { Text = "Edit material / Generate PBR…", Width = 252, Height = 34, Margin = new Padding(0, 6, 0, 8) };
        EditorChrome.StyleField(button); button.Click += (_, _) => edit(); _stack.Controls.Add(button);
    }

    public void ShowEmpty(string modeCaption)
    {
        _suspend = true;
        _stack.SuspendLayout();
        _stack.Controls.Clear();
        _stack.Controls.Add(new Label { Text = "Common Properties", Font = new Font("Segoe UI", 10f), ForeColor = Color.WhiteSmoke,
            AutoSize = false, Width = 260, Height = 30, Margin = new Padding(0, 0, 0, 10) });
        _stack.Controls.Add(Hint("Select a terrain object to edit its position, scale and assigned resources."));
        var add = new Button { Text = "Add Terrain Object", Width = 264, Height = 36, BackColor = Color.FromArgb(44, 112, 155), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f), Margin = new Padding(0, 12, 0, 8) };
        add.Click += (_, _) => CreateObjectRequested?.Invoke(this, EventArgs.Empty);
        _stack.Controls.Add(add);
        _stack.ResumeLayout(true);
        _suspend = false;
    }

    public void ShowTerrainMaterials(IEnumerable<(string Name, string Image, Action Edit)> layers)
    {
        foreach (Control control in _stack.Controls.Cast<Control>().ToArray())
        {
            if (!ReferenceEquals(control, _unifiedSurface)
                && !ReferenceEquals(control, _propertyFilter)) control.Dispose();
        }
        _stack.Controls.Clear();
        _stack.Controls.Add(Group("Terrain · Layer materials"));
        _stack.Controls.Add(Hint("Paint swatches select a brush. Edit layer images, colour, mapping and PBR here."));
        foreach (var layer in layers)
        {
            _stack.Controls.Add(Group(layer.Name));
            _stack.Controls.Add(Hint(string.IsNullOrWhiteSpace(layer.Image) ? "Colour layer" : ResourceDisplayName.Format(layer.Image)));
            var edit = new Button { Text = "Image / Colour / Mapping / PBR…", Width = 252, Height = 34, Margin = new Padding(0, 2, 0, 10) };
            EditorChrome.StyleField(edit); edit.Click += (_, _) => layer.Edit(); _stack.Controls.Add(edit);
        }
    }

    public void ShowSelection(TerrainSelectionFields fields)
    {
        _suspend = true;
        _stack.SuspendLayout();
        _stack.Controls.Clear();
        _stack.Controls.Add(IdentityBar(fields.Title, fields.KindLabel, canEdit: fields.CanEditIdentity));
        _propertyFilter.Width = 268;
        _propertyFilter.Margin = new Padding(0, 0, 0, 8);
        EditorChrome.StyleField(_propertyFilter);
        _propertyFilter.TextChanged -= FilterUnifiedSurface;
        _propertyFilter.TextChanged += FilterUnifiedSurface;
        _stack.Controls.Add(_propertyFilter);
        List<ResourceInspectorLiveValue> values = [];
        if (fields.HasTransform)
        {
            values.Add(new("Transform", "Transform.Position", "Position", fields.Position));
            values.Add(new("Transform", "Transform.Rotation", "Rotation", fields.Rotation));
            values.Add(new("Transform", "Transform.Scale", "Scale", fields.Scale,
                Minimum: 0.05m, Maximum: 64m, Increment: 0.05m, DecimalPlaces: 2));
        }
        if (fields.HasShader)
            values.Add(new("Shader & Material", "Shader.Asset", "Shader", fields.ShaderPath,
                AssetKind: ResourceKind.Shader));
        if (fields.HasModel)
        {
            values.Add(new("Mesh Renderer", "Model.Asset", "Model", fields.ModelPath,
                AssetKind: ResourceKind.Model));
            values.Add(new("Mesh Renderer", "Model.AnimationClip", "Animation clip", fields.AnimationClip));
        }
        if (fields.HasRasterSettings)
        {
            values.Add(new("Mesh Renderer", "Rendering.Culling", "Culling", fields.Culling,
                Choices: Enum.GetNames<Genesis.Shared.Interfaces.FaceCullingOverride>()));
            values.Add(new("Mesh Renderer", "Rendering.WindingOrder", "Winding order", fields.WindingOrder,
                Choices: Enum.GetNames<Genesis.Shared.Interfaces.FrontFaceWindingOverride>()));
            values.Add(new("Mesh Renderer", "Rendering.CastShadows", "Cast shadows", fields.CastShadows));
            values.Add(new("Mesh Renderer", "Rendering.ReceiveShadows", "Receive shadows", fields.ReceiveShadows));
        }
        if (fields.HasCondition)
            values.Add(new("Script: Placement Condition", "Condition.If", "If", fields.IfExpression));

        _unifiedSurface.EditRouter = request =>
        {
            fields = request.PropertyPath switch
            {
                "Transform.Position" when request.Value is Vector3 value => fields with { Position = value },
                "Transform.Rotation" when request.Value is Vector3 value => fields with { Rotation = value },
                "Transform.Scale" => fields with { Scale = Convert.ToSingle(request.Value) },
                "Shader.Asset" => fields with { ShaderPath = Convert.ToString(request.Value) ?? string.Empty },
                "Model.Asset" => fields with { ModelPath = Convert.ToString(request.Value) ?? string.Empty },
                "Model.AnimationClip" => fields with { AnimationClip = Convert.ToString(request.Value) ?? string.Empty },
                "Rendering.Culling" => fields with { Culling = Convert.ToString(request.Value) ?? string.Empty },
                "Rendering.WindingOrder" => fields with { WindingOrder = Convert.ToString(request.Value) ?? string.Empty },
                "Rendering.CastShadows" => fields with { CastShadows = Convert.ToBoolean(request.Value) },
                "Rendering.ReceiveShadows" => fields with { ReceiveShadows = Convert.ToBoolean(request.Value) },
                "Condition.If" => fields with { IfExpression = Convert.ToString(request.Value) ?? string.Empty },
                _ => fields,
            };
            RaiseFields(fields);
            return true;
        };
        string projectRoot = Tag as string ?? string.Empty;
        ResourceItem context = new()
        {
            Name = fields.Title,
            FullPath = Path.Combine(projectRoot, "Assets", "Terrain.terrain.json"),
            RelativePath = "Assets/Terrain.terrain.json",
            Kind = ResourceKind.Terrain,
            IsFolder = false,
        };
        _unifiedSurface.Width = 268;
        _unifiedSurface.InspectLive(context, values);
        _unifiedSurface.SetFilter(_propertyFilter.Text);
        _stack.Controls.Add(_unifiedSurface);

        if (fields.HasRasterSettings)
        {
            Button resources = new() { Text = "Edit resources…", Width = 268, Height = 34, Margin = new Padding(0, 8, 0, 6) };
            EditorChrome.StyleField(resources);
            resources.Click += (_, _) => EditResourcesRequested?.Invoke(this, EventArgs.Empty);
            _stack.Controls.Add(resources);
            Button place = new() { Text = "Place another…", Width = 268, Height = 34, Margin = new Padding(0, 0, 0, 8) };
            EditorChrome.StyleField(place);
            place.Click += (_, _) => PlaceCopyRequested?.Invoke(this, EventArgs.Empty);
            _stack.Controls.Add(place);
        }

        if (fields.HasCondition)
        {
            _stack.Controls.Add(Hint(
                string.IsNullOrWhiteSpace(fields.ThenClip) && string.IsNullOrWhiteSpace(fields.ElseClip)
                    ? "Then / Else stay visual AnimationPlay blocks on the Terrain Entity."
                    : $"Then AnimationPlay(\"{fields.ThenClip}\")  Else AnimationPlay(\"{fields.ElseClip}\")"));
        }

        _stack.ResumeLayout(true);
        _suspend = false;
    }

    private void FilterUnifiedSurface(object? sender, EventArgs args) =>
        _unifiedSurface.SetFilter(_propertyFilter.Text);

    public void ShowDefinition(string name, string type)
    {
        _stack.Controls.Clear();
        _stack.Controls.Add(IdentityBar(name, type + " · definition", canEdit: true));
        _stack.Controls.Add(Hint("This definition belongs to the terrain. Place it in the viewport, then select the placed object to change its transform."));
        Button resources = new() { Text = "Edit resources…", Width = 268, Height = 34 };
        EditorChrome.StyleField(resources);
        resources.Click += (_, _) => EditResourcesRequested?.Invoke(this, EventArgs.Empty);
        _stack.Controls.Add(resources);
        Button place = new() { Text = "Place in terrain…", Width = 268, Height = 34 };
        EditorChrome.StyleField(place);
        place.Click += (_, _) => PlaceCopyRequested?.Invoke(this, EventArgs.Empty);
        _stack.Controls.Add(place);
    }

    private Control IdentityBar(string title, string kind, bool canEdit)
    {
        Panel bar = new()
        {
            BackColor = EditorChrome.Raised,
            Height = 48,
            Margin = new Padding(0, 0, 0, 8),
            Width = 268,
        };
        Label name = new()
        {
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            Location = new Point(10, 6),
            Size = new Size(200, 18),
            Text = title,
        };
        Label type = new()
        {
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Location = new Point(10, 26),
            Size = new Size(200, 16),
            Text = kind,
        };
        bar.Controls.Add(name);
        bar.Controls.Add(type);
        if (canEdit)
        {
            Button edit = new()
            {
                FlatStyle = FlatStyle.Flat,
                Font = EditorChrome.SmallFont,
                Location = new Point(220, 10),
                Size = new Size(40, 28),
                Text = "Edit",
            };
            EditorChrome.StyleField(edit);
            edit.Click += (_, _) => EditIdentityRequested?.Invoke(this, EventArgs.Empty);
            bar.Controls.Add(edit);
        }

        return bar;
    }

    private Control AssetRow(string path, ResourceKind kind, Action<string> changed)
    {
        string display = string.IsNullOrWhiteSpace(path)
            ? "(none)"
            : ResourceDisplayName.Format(path);
        TableLayoutPanel row = new()
        {
            ColumnCount = 3,
            Height = 28,
            Margin = new Padding(0, 0, 0, 6),
            Width = 268,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        Label value = new()
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = EditorChrome.Text,
            Text = display,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Button browse = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 0, 0), Text = "…" };
        Button edit = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 0, 0), Text = "Edit" };
        EditorChrome.StyleField(browse);
        EditorChrome.StyleField(edit);
        browse.Click += (_, _) =>
        {
            if (Tag is not string projectRoot || string.IsNullOrWhiteSpace(projectRoot))
            {
                return;
            }

            Genesis.Application.Editors.Suite.ProjectAssetEntry? selected = AssetPickerService.PickAsset(
                new AssetPickerRequest(projectRoot, kind, path),
                FindForm());
            if (selected is null)
            {
                return;
            }

            changed(selected.Reference);
        };
        edit.Enabled = !string.IsNullOrWhiteSpace(path);
        edit.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                OpenAssetRequested?.Invoke(this, path);
            }
        };
        row.Controls.Add(value, 0, 0);
        row.Controls.Add(browse, 1, 0);
        row.Controls.Add(edit, 2, 0);
        return row;
    }

    private Control XyzRow(string caption, Vector3 value, Action<Vector3> changed)
    {
        Panel host = new()
        {
            Height = 44,
            Margin = new Padding(0, 0, 0, 4),
            Width = 268,
        };
        Label label = new()
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 16,
            Text = caption,
            Width = 268,
        };
        NumericUpDown x = AxisBox(value.X);
        NumericUpDown y = AxisBox(value.Y);
        NumericUpDown z = AxisBox(value.Z);
        x.Location = new Point(0, 18);
        y.Location = new Point(90, 18);
        z.Location = new Point(180, 18);
        void Push()
        {
            if (_suspend)
            {
                return;
            }

            changed(new Vector3((float)x.Value, (float)y.Value, (float)z.Value));
        }

        x.ValueChanged += (_, _) => Push();
        y.ValueChanged += (_, _) => Push();
        z.ValueChanged += (_, _) => Push();
        host.Controls.Add(label);
        host.Controls.Add(x);
        host.Controls.Add(y);
        host.Controls.Add(z);
        return host;
    }

    private Control ScalarRow(
        string caption,
        float value,
        decimal min,
        decimal max,
        decimal increment,
        int decimals,
        Action<float> changed)
    {
        Panel host = new()
        {
            Height = 44,
            Margin = new Padding(0, 0, 0, 4),
            Width = 268,
        };
        Label label = new()
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 16,
            Text = caption,
            Width = 268,
        };
        NumericUpDown box = AxisBox(value, min, max, increment, decimals);
        box.Location = new Point(0, 18);
        box.Width = 86;
        box.ValueChanged += (_, _) =>
        {
            if (!_suspend)
            {
                changed((float)box.Value);
            }
        };
        host.Controls.Add(label);
        host.Controls.Add(box);
        return host;
    }

    private Control TextRow(string caption, string value, Action<string> changed)
    {
        Panel host = new()
        {
            Height = 44,
            Margin = new Padding(0, 0, 0, 4),
            Width = 268,
        };
        Label label = new()
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 16,
            Text = caption,
            Width = 268,
        };
        TextBox box = new()
        {
            Location = new Point(0, 18),
            Size = new Size(268, 22),
            Text = value ?? "",
        };
        EditorChrome.StyleField(box);
        box.Leave += (_, _) =>
        {
            if (!_suspend)
            {
                changed(box.Text);
            }
        };
        host.Controls.Add(label);
        host.Controls.Add(box);
        return host;
    }

    private Control CheckRow(string caption, bool value, Action<bool> changed)
    {
        CheckBox box = new()
        {
            AutoSize = true,
            Checked = value,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Text,
            Margin = new Padding(0, 2, 0, 8),
            Text = caption,
        };
        box.CheckedChanged += (_, _) =>
        {
            if (!_suspend) changed(box.Checked);
        };
        return box;
    }

    private Control ChoiceRow(
        string caption,
        string value,
        string[] choices,
        Action<string> changed)
    {
        Panel host = new()
        {
            Height = 44,
            Margin = new Padding(0, 0, 0, 4),
            Width = 268,
        };
        Label label = new()
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 16,
            Text = caption,
            Width = 268,
        };
        ThemedComboBox box = new()
        {
            Location = new Point(0, 18),
            Size = new Size(268, 26),
        };
        box.Items.AddRange(choices);
        box.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice, value, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        EditorChrome.StyleField(box);
        box.SelectedIndexChanged += (_, _) =>
        {
            if (!_suspend && box.SelectedItem is string selected)
                changed(selected);
        };
        host.Controls.Add(label);
        host.Controls.Add(box);
        return host;
    }

    private static NumericUpDown AxisBox(
        float value,
        decimal min = -100000m,
        decimal max = 100000m,
        decimal increment = 0.1m,
        int decimals = 2)
    {
        decimal clamped = Math.Clamp((decimal)value, min, max);
        NumericUpDown box = new()
        {
            DecimalPlaces = decimals,
            Increment = increment,
            Maximum = max,
            Minimum = min,
            Size = new Size(84, 22),
            Value = clamped,
        };
        EditorChrome.StyleField(box);
        return box;
    }

    private static Label Group(string text) => new()
    {
        AutoSize = false,
        Font = EditorChrome.HeadingFont,
        ForeColor = EditorChrome.Muted,
        Height = 20,
        Margin = new Padding(0, 6, 0, 2),
        Text = text.ToUpperInvariant(),
        Width = 268,
    };

    private static Label Hint(string text) => new()
    {
        AutoSize = false,
        Font = EditorChrome.SmallFont,
        ForeColor = EditorChrome.Muted,
        Height = 36,
        Margin = new Padding(0, 0, 0, 4),
        Text = text,
        Width = 268,
    };

    private void RaiseFields(TerrainSelectionFields fields)
    {
        if (_suspend)
        {
            return;
        }

        FieldsChanged?.Invoke(this, fields);
    }
}

internal sealed record TerrainSelectionFields(
    string Title,
    string KindLabel,
    bool CanEditIdentity,
    bool HasTransform,
    Vector3 Position,
    Vector3 Rotation,
    float Scale,
    bool HasShader,
    string ShaderPath,
    bool HasModel,
    string ModelPath,
    string AnimationClip,
    bool HasCondition,
    string IfExpression,
    string ThenClip,
    string ElseClip,
    string Culling = "Default",
    string WindingOrder = "Default",
    bool HasRasterSettings = false,
    bool CastShadows = true,
    bool ReceiveShadows = true);

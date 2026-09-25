using System.Globalization;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEntityWizardPanel
{
    private event Action<TerrainEntityComponent, string>? ComponentAssetChanged;
    private readonly HashSet<TerrainEntityComponent> _collapsedComponents = [];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconPreview.Image?.Dispose(); _iconPreview.Image = null;
            _nameBox.Dispose(); _iconCombo.Dispose(); _iconPreview.Dispose();
        }
        base.Dispose(disposing);
    }

    public void MoveComponent(int index, int offset)
    {
        int destination = index + offset;
        if (index < 0 || index >= _document.Components.Count || destination < 0 || destination >= _document.Components.Count) return;
        var component = _document.Components[index];
        _document.Components.RemoveAt(index); _document.Components.Insert(destination, component);
        RefreshComponentList();
    }

    private void AddComponentActions(TerrainEntityComponent component, Panel header, Label title, Panel outer, Control fields)
    {
        int index = _document.Components.IndexOf(component);
        foreach (var (caption, direction) in new[] { ("↓", 1), ("↑", -1) })
        {
            var button = new Button { Text = caption, Width = 36, Dock = DockStyle.Right,
                Enabled = index + direction >= 0 && index + direction < _document.Components.Count,
                AccessibleName = direction < 0 ? "Move component up" : "Move component down" };
            EditorChrome.StyleField(button);
            button.Click += (_, _) => MoveComponent(_document.Components.IndexOf(component), direction);
            header.Controls.Add(button); button.BringToFront();
        }
        title.Cursor = Cursors.Hand;
        void LayoutCard()
        {
            fields.Visible = !_collapsedComponents.Contains(component);
            title.Text = (fields.Visible ? "▾ " : "▸ ") + TerrainEntityComponentKinds.DisplayName(component.Type);
            outer.Height = header.Height + (fields.Visible ? fields.Height : 0) + 30;
        }
        title.Click += (_, _) => { if (!_collapsedComponents.Add(component)) _collapsedComponents.Remove(component); LayoutCard(); };
        fields.SizeChanged += (_, _) => LayoutCard();
        LayoutCard();
    }

    private Control NumberSetting(TerrainEntityComponent component, string key, string caption, decimal fallback, decimal min, decimal max, int top)
    {
        var row = new Panel { Height = 54, Width = 180, Top = top };
        row.Controls.Add(new Label { Text = caption, Height = 22, Dock = DockStyle.Top, ForeColor = EditorChrome.Muted });
        var field = new NumericUpDown { Left = 0, Top = 24, Width = 140, Minimum = min, Maximum = max, DecimalPlaces = 3, Increment = .05m };
        EditorChrome.StyleField(field);
        field.Value = decimal.TryParse(component.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) ? Math.Clamp(value, min, max) : fallback;
        field.ValueChanged += (_, _) => component.Set(key, field.Value.ToString(CultureInfo.InvariantCulture));
        row.Controls.Add(field); return row;
    }

    private Control BuildScriptFields(TerrainEntityComponent component)
    {
        var panel = new Panel { Height = 80, Width = 800 };
        panel.Controls.Add(BuildAssetFieldRow(component, "Script", "PGSL script", ResourceKind.PgslScript, "Scripts",
            path => MakeSurfaceEditor(new PgslScriptEditorControl(path, _projectRoot)), 0));
        var values = new FlowLayoutPanel { Left = 0, Top = 42, Width = 780, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        panel.Controls.Add(values);
        panel.SizeChanged += (_, _) => values.MaximumSize = new Size(Math.Max(180, panel.ClientSize.Width), 0);
        void RefreshValues()
        {
            foreach (Control control in values.Controls.Cast<Control>().ToArray()) control.Dispose();
            string reference = component.Get("Script");
            if (!string.IsNullOrWhiteSpace(reference))
            {
                try
                {
                    foreach (var variable in PgslInspectableVariables.Reflect(File.ReadAllText(ResourceNames.Resolve(_projectRoot, reference, ResourceType.Script))))
                    {
                        string key = "Variables." + variable.Name;
                        if (variable.Value is bool flag)
                        {
                            var check = new CheckBox { Text = variable.Name, Width = 180, Height = 48, Checked = bool.TryParse(component.Get(key), out bool selected) ? selected : flag };
                            check.CheckedChanged += (_, _) => component.Set(key, check.Checked ? "true" : "false"); values.Controls.Add(check);
                        }
                        else if (variable.Value is string text)
                        {
                            var row = new Panel { Width = 220, Height = 54 };
                            row.Controls.Add(new Label { Text = variable.Name, Dock = DockStyle.Top, Height = 22, ForeColor = EditorChrome.Muted });
                            var field = new TextBox { Text = component.Get(key, text), Top = 24, Width = 210 }; EditorChrome.StyleField(field);
                            field.TextChanged += (_, _) => component.Set(key, field.Text); row.Controls.Add(field); values.Controls.Add(row);
                        }
                        else if (decimal.TryParse(Convert.ToString(variable.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
                            values.Controls.Add(NumberSetting(component, key, variable.Name, Math.Clamp(number, -1000000, 1000000), -1000000, 1000000, 0));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { values.Controls.Add(new Label { Text = exception.Message, AutoSize = true }); }
            }
            if (values.Controls.Count == 0) values.Controls.Add(new Label { Text = "File-scope PGSL variables appear here.", AutoSize = true, ForeColor = EditorChrome.Muted });
            panel.Height = 48 + values.PreferredSize.Height;
        }
        void Changed(TerrainEntityComponent sender, string key) { if (sender == component && key == "Script") RefreshValues(); }
        ComponentAssetChanged += Changed; panel.Disposed += (_, _) => ComponentAssetChanged -= Changed;
        values.SizeChanged += (_, _) => panel.Height = 48 + values.Height;
        RefreshValues(); return panel;
    }

    private Control BuildShaderFields(TerrainEntityComponent component)
    {
        var panel = new Panel { Height = 70, Width = 800 };
        panel.Controls.Add(BuildAssetFieldRow(component, "Shader", "Shader", ResourceKind.Shader, "Shaders",
            path => MakeSurfaceEditor(new ShaderEditorControl(path, _projectRoot)), 0));
        var values = new FlowLayoutPanel { Left = 0, Top = 42, Width = 780, Height = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        panel.Controls.Add(values);
        panel.SizeChanged += (_, _) => values.MaximumSize = new Size(Math.Max(180, panel.ClientSize.Width), 0);
        void RefreshValues()
        {
            foreach (Control control in values.Controls.Cast<Control>().ToArray()) control.Dispose();
            values.Controls.Clear();
            string reference = component.Get("Shader");
            if (!string.IsNullOrWhiteSpace(reference))
            {
                try
                {
                    var shader = ShaderAssetDocument.Load(ResourceNames.Resolve(_projectRoot, reference, ResourceType.Shader));
                    ShaderParameterReflection.Synchronize(shader);
                    foreach (var parameter in shader.Parameters)
                    {
                        for (int index = 0; index < Math.Max(1, parameter.Value.Length); index++)
                        {
                            string key = $"Parameter.{parameter.Name}.{index}";
                            decimal fallback = index < parameter.Value.Length && float.IsFinite(parameter.Value[index])
                                ? Math.Clamp((decimal)Math.Clamp(parameter.Value[index], -1000000, 1000000), -1000000, 1000000) : 0;
                            values.Controls.Add(NumberSetting(component, key, parameter.Name + (parameter.Value.Length > 1 ? $" [{index}]" : ""), fallback, -1000000, 1000000, 0));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException)
                { values.Controls.Add(new Label { Text = exception.Message, AutoSize = true }); }
            }
            if (values.Controls.Count == 0) values.Controls.Add(new Label { Text = "Exposed shader parameters appear here.", AutoSize = true, ForeColor = EditorChrome.Muted });
            panel.Height = 48 + values.PreferredSize.Height;
        }
        void Changed(TerrainEntityComponent sender, string key) { if (sender == component && key == "Shader") RefreshValues(); }
        ComponentAssetChanged += Changed; panel.Disposed += (_, _) => ComponentAssetChanged -= Changed;
        values.SizeChanged += (_, _) => panel.Height = 48 + values.Height;
        RefreshValues(); return panel;
    }

    private Control BuildPbrSettings(TerrainEntityComponent component, int top)
    {
        var panel = new Panel { Top = top, Height = 84, Width = 780, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        var status = new Label { Text = "Choose an Image resource to inspect material channels.", Dock = DockStyle.Top, Height = 26, ForeColor = EditorChrome.Muted };
        var generate = new Button { Text = "Generate PBR…", Top = 34, Width = 150, Height = 32 };
        EditorChrome.StyleField(generate); panel.Controls.Add(generate); panel.Controls.Add(status);
        void Refresh()
        {
            string reference = component.Get("Texture");
            generate.Enabled = !string.IsNullOrWhiteSpace(reference);
            status.Text = TerrainImageMaterial.Describe(_projectRoot, reference);
        }
        generate.Click += async (_, _) =>
        {
            using var dialog = new Genesis.Application.Editors.Image.Dialogs.PbrMaterialDialog();
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            generate.Enabled = false; status.Text = "Generating missing material channels…";
            try { await Task.Run(() => TerrainImageMaterial.GenerateMissing(_projectRoot, component.Get("Texture"), dialog.Settings)); }
            catch (Exception exception) { if (!panel.IsDisposed) status.Text = exception.Message; return; }
            finally { if (!panel.IsDisposed) generate.Enabled = true; }
            if (!panel.IsDisposed) Refresh();
        };
        void Changed(TerrainEntityComponent sender, string key) { if (sender == component && key == "Texture") Refresh(); }
        ComponentAssetChanged += Changed; panel.Disposed += (_, _) => ComponentAssetChanged -= Changed;
        Refresh(); return panel;
    }
}

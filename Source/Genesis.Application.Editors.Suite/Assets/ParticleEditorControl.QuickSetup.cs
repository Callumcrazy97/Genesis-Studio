using System.Text;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private FlowLayoutPanel? _particleQuickSetup;
    private FlowLayoutPanel? _particleGameGuide;
    private ThemedComboBox? _quickEmitterChoice;
    private ToolStripMenuItem? _advancedPropertiesMenu;
    private ToolStripMenuItem? _editDefinitionMenu;
    private Action? _showParticleInspector;
    private bool _syncingQuickEmitter;
    private bool _sizingParticleQuickPages;
    private Button? _particleInspectorBack;

    private FlowLayoutPanel MakeParticleQuickPage(string name)
    {
        FlowLayoutPanel page = new()
        {
            Name = name, Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(12), BackColor = EditorChrome.Surface,
        };
        page.SizeChanged += (_, _) => SizeParticleQuickPages();
        page.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(page);
        return page;
    }

    private FlowLayoutPanel BuildParticleQuickSetup()
    {
        FlowLayoutPanel page = MakeParticleQuickPage("ParticleQuickSetup");
        AddParticleGuideText(page, "1. Choose an effect", heading: true);
        AddParticleGuideText(page, "Use Start with… to choose a ready-made effect.");
        AddParticleGuideText(page, "2. Tune the effect", heading: true);
        _quickEmitterChoice = new ThemedComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Choose particle layer to tune",
        };
        EditorChrome.StyleField(_quickEmitterChoice);
        _quickEmitterChoice.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingQuickEmitter && _quickEmitterChoice.SelectedIndex >= 0)
                SelectEmitter(_quickEmitterChoice.SelectedIndex);
        };
        AddInspectorRow(page, "Effect layer", _quickEmitterChoice);

        // Reuse the actual authored controls, including their validation and history. There is
        // one field per property rather than a second beginner form that can drift out of sync.
        MoveParticleQuickRow(page, _shapeCombo, "Shape");
        foreach ((string key, string label) in new[]
        {
            ("rate", "Particles / sec"), ("life", "Lifetime (s)"), ("speed", "Speed"),
            ("spread", "Spread (°)"), ("gravity", "Gravity"),
            ("startSize", "Start size"), ("endSize", "End size"),
        }) MoveParticleQuickRow(page, _numericControls[key], label);
        FlowLayoutPanel material = InspectorPage("Material");
        foreach ((string oldLabel, string newLabel) in new[]
            { ("Gradient start", "Start color"), ("Gradient end", "End color") })
        {
            Control row = material.Controls.Cast<Control>().Single(control =>
                control.Controls.OfType<Label>().Any(label => label.Text == oldLabel));
            row.Controls.OfType<Label>().Single().Text = newLabel;
            page.Controls.Add(row);
        }
        EditorCommandBar emission = EditorChrome.MakeToolbar();
        emission.Items.Add(_loopButton);
        page.Controls.Add(emission);
        MoveParticleQuickRow(page, _numericControls["burstCount"], "One-shot count");
        AddParticleGuideText(page, "Continuous emission uses Particles / sec. Turn it off to play One-shot count on Restart. Positive gravity lifts particles; negative gravity pulls them down. Effect units become 12 pixels in a 2D game.");
        Button advanced = MakeInspectorButton("More effect settings…", () =>
        {
            _advancedPropertiesMenu!.Checked = true;
            ShowAdvancedParticleProperties(true);
        });
        advanced.Name = "ParticleMoreEffectSettings";
        page.Controls.Add(advanced);
        AddParticleGuideText(page, "3. Use it in your game", heading: true);
        AddParticleGuideText(page, "Use in game on the toolbar can create an Object ready to place in a Room, or show the PGSL command to spawn this effect.");
        return page;
    }

    private static void MoveParticleQuickRow(FlowLayoutPanel page, Control field, string label)
    {
        Control row = field.Parent ?? throw new InvalidOperationException("A particle field has no row.");
        row.Controls.OfType<Label>().Single().Text = label;
        page.Controls.Add(row);
    }

    private static void AddParticleGuideText(FlowLayoutPanel page, string text, bool heading = false)
    {
        page.Controls.Add(new Label
        {
            Text = text, Name = heading ? "ParticleGuideHeading" : "ParticleGuideText",
            ForeColor = heading ? EditorChrome.Accent : EditorChrome.Muted,
            Font = heading ? EditorChrome.HeadingFont : EditorChrome.SmallFont,
            AutoSize = false, Margin = new Padding(0, heading ? 10 : 0, 0, 10),
        });
    }

    private FlowLayoutPanel BuildParticleGameGuide()
    {
        FlowLayoutPanel page = MakeParticleQuickPage("ParticleUseInGame");
        AddParticleGuideText(page, "Use this effect in a Room", heading: true);
        AddParticleGuideText(page, "Create an Object with this Particle Effect attached. Your latest effect settings will be saved before the Object opens.");
        TextBox name = new()
        {
            Text = ResourceNames.Name(ProjectRoot, ResourcePath) + " Effect",
            AccessibleName = "New particle effect Object name",
        };
        EditorChrome.StyleField(name);
        AddInspectorRow(page, "Object name", name);
        Label result = new() { Name = "ParticleObjectCreationResult", ForeColor = EditorChrome.Muted,
            Text = "Place the new Object in a Room, then Run. Move or scale its instance to position or resize the effect." };
        Button create = MakeInspectorButton("Create effect Object", () =>
        {
            try
            {
                string path = CreateEffectObject(name.Text);
                result.Text = "Created " + ResourceNames.Name(ProjectRoot, path)
                    + ". In Room Editor, choose Objects and drag this Object from Assets into the room. Run to see it play.";
                SizeParticleQuickPages();
            }
            catch (Exception error) when (error is ArgumentException or IOException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                result.Text = error.Message;
                SizeParticleQuickPages();
            }
        });
        create.Name = "ParticleCreateEffectObject";
        page.Controls.Add(create);
        page.Controls.Add(result);
        AddParticleGuideText(page, "Attach it to an existing Object", heading: true);
        AddParticleGuideText(page, "Open the Object, choose Components… → Add → Particle Effect, and choose "
            + ResourceNames.Name(ProjectRoot, ResourcePath) + " as its Asset. FollowEntity keeps the effect attached as the Object moves.");
        AddParticleGuideText(page, "Spawn it from PGSL", heading: true);
        AddParticleGuideText(page, "In an Object event, x and y are that instance's position. Keep the returned id if you want to stop or change the emitter later.");
        TextBox code = new()
        {
            Name = "ParticleGameplayCode", ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Horizontal, WordWrap = false,
            Text = "var effect = SpawnParticleEmitter(" + System.Text.Json.JsonSerializer.Serialize(
                ResourceNames.Name(ProjectRoot, ResourcePath)) + ", x, y, 0, 1);",
            AccessibleName = "PGSL to spawn this particle resource",
        };
        EditorChrome.StyleField(code);
        page.Controls.Add(code);
        return page;
    }

    /// <summary>Creates a normal runtime Object that references this saved effect.</summary>
    public string CreateEffectObject(string name)
    {
        string validatedName = ResourceNames.ValidateName(name);
        Save(); // Includes definition validation; never create a dangling Object for a failed draft.
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
        document["dimension"] = _effect.Preview2D ? "TwoD" : "ThreeD";
        ObjectCompositionModel composition = new(document);
        composition.SetAsset("ParticleComponent", ResourceNames.Name(ProjectRoot, ResourcePath));
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject),
            ResourceKind.GameObject, validatedName);
        string content = ResourceReferenceRewriter.Normalize(ProjectRoot, path,
            document.ToString(Newtonsoft.Json.Formatting.Indented));
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        ResourceNames.Invalidate(ProjectRoot);
        RequestOpenLinkedResource(path);
        return path;
    }

    private void ShowParticleGameGuide()
    {
        SetAuthoringMode(ParticleAuthoringMode.Properties);
        if (_authoringMode != ParticleAuthoringMode.Properties) return;
        _particleQuickSetup!.Visible = false;
        _inspectorTabs.Visible = false;
        _particleGameGuide!.Visible = true;
        _particleInspectorBack!.Visible = true;
        _particleGameGuide.BringToFront();
        _showParticleInspector?.Invoke();
        SizeParticleQuickPages();
    }

    private void ShowParticleQuickSetup()
    {
        if (_advancedPropertiesMenu!.Checked) _advancedPropertiesMenu.Checked = false;
        _particleGameGuide!.Visible = false;
        _inspectorTabs.Visible = false;
        _particleQuickSetup!.Visible = true;
        _particleInspectorBack!.Visible = false;
        _particleQuickSetup.BringToFront();
        _showParticleInspector?.Invoke();
        SizeParticleQuickPages();
    }

    private void ShowAdvancedParticleProperties(bool show)
    {
        if (_particleQuickSetup is null) return;
        SetAuthoringMode(ParticleAuthoringMode.Properties);
        if (_authoringMode != ParticleAuthoringMode.Properties) return;
        _particleGameGuide!.Visible = false;
        _particleQuickSetup.Visible = !show;
        _inspectorTabs.Visible = show;
        _particleInspectorBack!.Visible = show;
        if (show) _inspectorTabs.BringToFront();
        else _particleQuickSetup.BringToFront();
        _showParticleInspector?.Invoke();
    }

    private void RefreshParticleQuickEmitterChoice()
    {
        if (_quickEmitterChoice is null) return;
        _syncingQuickEmitter = true;
        try
        {
            string[] names = Enumerable.Range(0, ParticleEffectEditing.Count(_effect))
                .Select(index => ParticleEffectEditing.Name(_effect, index)).ToArray();
            if (!_quickEmitterChoice.Items.Cast<string>().SequenceEqual(names))
            {
                _quickEmitterChoice.Items.Clear();
                _quickEmitterChoice.Items.AddRange(names);
            }
            _quickEmitterChoice.SelectedIndex = _selectedEmitterIndex;
            _quickEmitterChoice.Enabled = names.Length > 1;
            _quickEmitterChoice.Parent!.Visible = names.Length > 1;
        }
        finally { _syncingQuickEmitter = false; }
    }

    private void SizeParticleQuickPages()
    {
        if (_sizingParticleQuickPages) return;
        _sizingParticleQuickPages = true;
        try
        {
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            foreach (FlowLayoutPanel page in new[] { _particleQuickSetup, _particleGameGuide }.OfType<FlowLayoutPanel>())
            {
                int width = Math.Max(180, page.ClientSize.Width - page.Padding.Horizontal
                    - SystemInformation.VerticalScrollBarWidth - 6);
                foreach (Control row in page.Controls)
                {
                    row.Width = width;
                    if (row is TableLayoutPanel table)
                    {
                        ArrangeParticleFieldRow(table, width, 132 * scale);
                    }
                    else if (row is Label label)
                    {
                        label.Font = label.Name == "ParticleGuideHeading" ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                        label.Height = TextRenderer.MeasureText(label.Text, label.Font,
                            new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
                    }
                    else if (row is Button button)
                        button.Height = EditorChrome.BaseFont.Height + 20;
                    else if (row is TextBox code && code.Multiline)
                        code.Height = EditorChrome.BaseFont.Height * 3 + SystemInformation.HorizontalScrollBarHeight + 8;
                }
            }
            if (_particleInspectorBack is not null) _particleInspectorBack.Height = EditorChrome.BaseFont.Height + 20;
        }
        finally { _sizingParticleQuickPages = false; }
    }

    private static void ArrangeParticleFieldRow(TableLayoutPanel table, int width, float captionWidth)
    {
        Label? label = table.Controls.OfType<Label>().FirstOrDefault();
        Control? field = table.Controls.Cast<Control>().FirstOrDefault(control => control != label);
        if (label is null || field is null) return;
        label.Font = EditorChrome.SmallFont;
        field.Font = EditorChrome.BaseFont;
        int fieldHeight = Math.Max(field.PreferredSize.Height, EditorChrome.BaseFont.Height + 12);
        bool stacked = width < captionWidth + TextRenderer.MeasureText("0000.00", field.Font).Width + 36;
        table.SuspendLayout();
        try
        {
            table.ColumnCount = stacked ? 1 : 2;
            table.RowCount = stacked ? 2 : 1;
            table.ColumnStyles.Clear(); table.RowStyles.Clear();
            table.ColumnStyles.Add(new ColumnStyle(stacked ? SizeType.Percent : SizeType.Absolute, stacked ? 100 : captionWidth));
            if (!stacked) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int captionHeight = TextRenderer.MeasureText(label.Text, label.Font,
                new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4;
            if (stacked) table.RowStyles.Add(new RowStyle(SizeType.Absolute, captionHeight));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.SetCellPosition(label, new TableLayoutPanelCellPosition(0, 0));
            table.SetCellPosition(field, new TableLayoutPanelCellPosition(stacked ? 0 : 1, stacked ? 1 : 0));
            table.Height = stacked ? captionHeight + fieldHeight + 8 : Math.Max(fieldHeight + 8, captionHeight + 8);
        }
        finally { table.ResumeLayout(true); }
    }
}

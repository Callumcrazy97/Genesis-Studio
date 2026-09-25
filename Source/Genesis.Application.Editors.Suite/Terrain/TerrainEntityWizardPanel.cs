using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Create/Edit wizard panel for a Terrain Entity. The four guided pages cover identity,
/// category, components, and review. Embedded in the Terrain Editor Entities mode and wrapped by
/// <see cref="TerrainEntityWizardDialog"/> for modal use.
/// </summary>
public sealed partial class TerrainEntityWizardPanel : UserControl
{
    private static readonly JsonSerializerOptions DocumentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _resourcePath;
    private readonly string _projectRoot;
    private readonly TerrainEntityDocument _document;
    private bool _syncingIcon;

    private readonly Panel _contentHost;
    private readonly Label _pageLabel;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;

    private readonly TextBox _nameBox;
    private readonly Dictionary<TerrainEntityType, Panel> _typeCards = [];
    private readonly ThemedComboBox _iconCombo;
    private readonly PictureBox _iconPreview;

    private FlowLayoutPanel? _componentList;
    private int _page;
    private readonly TerrainAssetPreview _livePreview;
    public TerrainAssetPreview LivePreview => _livePreview;

    public event EventHandler? Saved;
    public event EventHandler? Cancelled;

    public TerrainEntityWizardPanel(string resourcePath, string projectRoot, TerrainEntityType? presetType)
    {
        _resourcePath = resourcePath;
        _projectRoot = projectRoot;
        _document = LoadOrCreate(presetType);

        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        Dock = DockStyle.Fill;
        MinimumSize = new Size(640, 420);

        Panel headerBar = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Top, Height = 40 };
        _pageLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Padding = new Padding(14, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        headerBar.Controls.Add(_pageLabel);

        Panel footer = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 52 };
        _cancelButton = new Button { Dock = DockStyle.Right, Text = "Cancel", Width = 96 };
        _saveButton = new Button { Dock = DockStyle.Right, Text = "Save", Width = 96 };
        _nextButton = new Button { Dock = DockStyle.Right, Text = "Next →", Width = 96 };
        _backButton = new Button { Dock = DockStyle.Left, Text = "← Back", Width = 96 };
        foreach (Button button in new[] { _cancelButton, _saveButton, _nextButton, _backButton })
        {
            EditorChrome.StyleField(button);
            button.Margin = new Padding(6);
        }

        _saveButton.Click += (_, _) => SaveAndClose();
        _nextButton.Click += (_, _) => GoToPage(Math.Min(3, _page + 1));
        _backButton.Click += (_, _) => GoToPage(Math.Max(0, _page - 1));
        _cancelButton.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
        footer.Controls.Add(_cancelButton);
        footer.Controls.Add(_saveButton);
        footer.Controls.Add(_nextButton);
        footer.Controls.Add(_backButton);

        _contentHost = new Panel { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };

        // Page 1 controls constructed once; reused whenever we return to page 0.
        _nameBox = new TextBox { Text = _document.Name };
        EditorChrome.StyleField(_nameBox);
        _nameBox.TextChanged += (_, _) => _document.Name = _nameBox.Text;

        _iconCombo = new ThemedComboBox { Width = 260, DisplayMember = nameof(ProjectAssetEntry.DisplayName) };
        _iconCombo.Enabled = false;
        EditorChrome.StyleField(_iconCombo);
        _iconPreview = new PictureBox
        {
            BackColor = EditorChrome.Canvas,
            BorderStyle = BorderStyle.None,
            Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        PopulateIconCombo();
        _iconCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingIcon) return;
            _document.Icon = _iconCombo.SelectedItem is ProjectAssetEntry entry ? entry.Reference : "";
            UpdateIconPreview();
        };

        _livePreview = new TerrainAssetPreview(projectRoot, () => _document);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1100, 700), SplitterDistance = 700, Panel1MinSize = 530, Panel2MinSize = 270 };
        split.Panel1.Controls.Add(_contentHost); split.Panel2.Controls.Add(_livePreview);
        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(headerBar);
        ThemeSelfOnLoad();

        GoToPage(0);
    }

    /// <summary>The path of the resource this wizard just saved (Result reflects post-save state).</summary>
    public string ResourcePath => _resourcePath;

    // ── Test-driving surface (headless suite) — mirrors the real button/field actions ──────
    public string EntityName => _document.Name;
    public TerrainEntityType EntityType => _document.Type;
    public IReadOnlyList<TerrainEntityComponent> Components => _document.Components;
    public Control ContentHost => _contentHost;
    public void SetName(string name) => _nameBox.Text = name;
    public void SetIcon(string projectRelativeImage)
    {
        _document.Icon = projectRelativeImage;
        PopulateIconCombo();
        UpdateIconPreview();
    }
    public void SetType(TerrainEntityType type) { _document.Type = type; RefreshTypeCardSelection(); }
    public void SaveAndCloseForTest() => SaveAndClose();

    private void ThemeSelfOnLoad()
    {
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
    }

    private TerrainEntityDocument LoadOrCreate(TerrainEntityType? presetType)
    {
        try
        {
            if (File.Exists(_resourcePath))
            {
                string json = File.ReadAllText(_resourcePath);
                TerrainEntityDocument? loaded = JsonSerializer.Deserialize<TerrainEntityDocument>(json, DocumentJsonOptions);
                if (loaded is not null)
                {
                    if (presetType.HasValue)
                    {
                        loaded.Type = presetType.Value;
                    }

                    return loaded;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Fall through to a fresh default document.
        }

        return new TerrainEntityDocument { Type = presetType ?? TerrainEntityType.Foliage };
    }

    // ── Paging ───────────────────────────────────────────────────────────────────

    /// <summary>Switches among Identity, Category, Components, and Review — the same action
    /// the footer's Back/Next buttons invoke, exposed for headless test driving.</summary>
    public void GoToPage(int page)
    {
        _page = page;
        // Keep identity inputs while disposing old cards and their asset-change subscriptions.
        foreach (Control input in new Control[] { _nameBox, _iconCombo, _iconPreview }) input.Parent?.Controls.Remove(input);
        foreach (Control oldPage in _contentHost.Controls.Cast<Control>().ToArray()) oldPage.Dispose();
        _contentHost.Controls.Clear();
        _typeCards.Clear();
        _contentHost.Controls.Add(page switch { 0 => BuildPageOne(), 1 => BuildCategoryPage(), 2 => BuildPageTwo(), _ => BuildCompletionPage() });
        _pageLabel.Text = new[] { "1. Identity", "2. Category", "3. Components", "4. Review & Create" }[Math.Clamp(page, 0, 3)];
        _backButton.Visible = page > 0;
        _nextButton.Visible = page < 3;
        _saveButton.Visible = page == 3;
        _saveButton.Text = "Create / Save";
    }

    private Control BuildPageOne()
    {
        Panel page = new() { AutoScroll = true, Dock = DockStyle.Fill, Padding = new Padding(16) };

        Label nameLabel = SectionCaption("Name");
        nameLabel.Location = new Point(24, 20);
        _nameBox.Location = new Point(24, 44);
        _nameBox.Width = 400;

        Label typeLabel = SectionCaption("Type — click a card");
        typeLabel.Location = new Point(24, 90);

        FlowLayoutPanel cards = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Location = new Point(24, 114),
            Size = new Size(860, 130),
        };
        cards.Dispose();

        Label iconLabel = SectionCaption("Icon — thumbnail shown in the Terrain Editor's library");
        iconLabel.Location = new Point(24, 94);
        _iconCombo.Location = new Point(112, 124);
        _iconPreview.Location = new Point(24, 124);
        Button newIconButton = new() { Location = new Point(112, 164), Size = new Size(104, 30), Text = "New Icon…" };
        EditorChrome.StyleField(newIconButton);
        newIconButton.Click += (_, _) => GenerateAsset(
            ResourceKind.Image,
            "Sprites",
            "NewIcon",
            path =>
            {
                ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(path);
                ImageDocumentSession session = new(loaded.Document, path, ImageDocumentAccess.Editor);
                ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
                ImageEditorControl editor = new(session, workspace) { Dock = DockStyle.Fill };
                return (editor, () => editor.Save());
            },
            path =>
            {
                _document.Icon = ResourceNames.Name(_projectRoot, path);
                PopulateIconCombo();
                SelectIconInCombo();
                UpdateIconPreview();
            });

        UpdateIconPreview();

        page.Controls.Add(nameLabel);
        page.Controls.Add(_nameBox);
        typeLabel.Dispose();
        page.Controls.Add(iconLabel);
        page.Controls.Add(_iconCombo);
        page.Controls.Add(_iconPreview);
        page.Controls.Add(newIconButton);
        Button browseIcon = new() { Location = new Point(224, 164), Size = new Size(148, 30), Text = "Browse Images…" };
        EditorChrome.StyleField(browseIcon);
        browseIcon.Click += (_, _) =>
        {
            var entry = AssetPickerService.PickAsset(new AssetPickerRequest(_projectRoot, ResourceKind.Image, _document.Icon), FindForm());
            if (entry is null) return;
            SetIcon(entry.Reference);
        };
        page.Controls.Add(browseIcon);
        return page;
    }

    private Control BuildCategoryPage()
    {
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24), WrapContents = true };
        foreach (var type in Enum.GetValues<TerrainEntityType>()) page.Controls.Add(BuildTypeCard(type));
        RefreshTypeCardSelection(); return page;
    }

    private Control BuildCompletionPage()
    {
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
        page.Controls.Add(new Label { Text = _document.Name, Font = EditorChrome.BaseFont, ForeColor = EditorChrome.Text, AutoSize = true });
        page.Controls.Add(new Label { Text = $"{_document.Type} · {_document.Components.Count} components", AutoSize = true, ForeColor = EditorChrome.Muted, Margin = new Padding(0, 12, 0, 12) });
        foreach (var component in _document.Components) page.Controls.Add(new Label { Text = (component.Enabled ? "✓ " : "○ ") + TerrainEntityComponentKinds.DisplayName(component.Type), AutoSize = true, ForeColor = EditorChrome.Text });
        page.Controls.Add(new Label { Text = "Create / Save adds this asset to its library category and arms viewport placement.\n\nThe preview plays model clips, animated textures, shaders and particles. Interactive collisions and script events are not simulated here.", AutoSize = true, MaximumSize = new Size(450, 0), ForeColor = EditorChrome.Muted, Margin = new Padding(0, 24, 0, 0) });
        return page;
    }

    private Panel BuildTypeCard(TerrainEntityType type)
    {
        Panel card = new()
        {
            BackColor = EditorChrome.Raised,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 12, 0),
            Size = new Size(150, 110),
            Tag = type,
        };
        Label glyph = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Font = new Font(EditorChrome.HeadingFont.FontFamily, 22f),
            ForeColor = EditorChrome.Accent,
            Height = 64,
            Text = GlyphFor(type),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Label label = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            Text = type == TerrainEntityType.Fluid ? "Water" : type == TerrainEntityType.Object ? "Prop / Object" : type.ToString(),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        card.Controls.Add(label);
        card.Controls.Add(glyph);

        void Select()
        {
            _document.Type = type;
            RefreshTypeCardSelection();
        }

        card.Click += (_, _) => Select();
        glyph.Click += (_, _) => Select();
        label.Click += (_, _) => Select();
        _typeCards[type] = card;
        return card;
    }

    private void RefreshTypeCardSelection()
    {
        foreach ((TerrainEntityType type, Panel card) in _typeCards)
        {
            bool selected = type == _document.Type;
            card.BackColor = selected ? EditorChrome.Hover : EditorChrome.Raised;
            card.BorderStyleSafe(selected);
        }
    }

    private static string GlyphFor(TerrainEntityType type) => type switch
    {
        TerrainEntityType.Terrain => "⌁",
        TerrainEntityType.Foliage => "❋",
        TerrainEntityType.Object => "⬡",
        TerrainEntityType.Fluid => "≈",
        TerrainEntityType.Environment => "☀",
        _ => "?",
    };

    private void PopulateIconCombo()
    {
        _syncingIcon = true;
        try
        {
        _iconCombo.Items.Clear();
        _iconCombo.Items.Add("(none)");
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(_projectRoot, ResourceKind.Image))
        {
            _iconCombo.Items.Add(entry);
        }

        SelectIconInCombo();
        }
        finally { _syncingIcon = false; }
    }

    private void SelectIconInCombo()
    {
        if (string.IsNullOrWhiteSpace(_document.Icon))
        {
            _iconCombo.SelectedIndex = 0;
            return;
        }

        for (int i = 1; i < _iconCombo.Items.Count; i++)
        {
            if (_iconCombo.Items[i] is ProjectAssetEntry entry &&
                string.Equals(entry.Reference, _document.Icon, StringComparison.OrdinalIgnoreCase))
            {
                _iconCombo.SelectedIndex = i;
                return;
            }
        }

        _iconCombo.SelectedIndex = 0;
    }

    private void UpdateIconPreview()
    {
        _iconPreview.Image?.Dispose();
        _iconPreview.Image = null;
        string? path = ProjectAssetIndex.ResolveSpriteImage(_projectRoot, _document.Icon);
        if (path is not null && File.Exists(path))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using var image = new Bitmap(stream);
                _iconPreview.Image = new Bitmap(image);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException)
            {
                _iconPreview.Image = null;
            }
        }
    }

    private static Label SectionCaption(string text) => new()
    {
        AutoSize = true,
        BackColor = Color.Transparent,
        Font = EditorChrome.SmallFont,
        ForeColor = EditorChrome.Muted,
        Text = text.ToUpperInvariant(),
    };

    // ── Page 2: components ──────────────────────────────────────────────────────

    private Control BuildPageTwo()
    {
        Panel page = new() { Dock = DockStyle.Fill };

        Panel toolbar = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Top, Height = 40 };
        ThemedComboBox kindCombo = new() { Location = new Point(10, 7), Width = 160 };
        EditorChrome.StyleField(kindCombo);
        foreach (string kind in TerrainEntityComponentKinds.All)
        {
            kindCombo.Items.Add(TerrainEntityComponentKinds.DisplayName(kind));
        }

        kindCombo.SelectedIndex = 0;
        Button addButton = new() { Location = new Point(178, 6), Size = new Size(150, 28), Text = "+ Add component" };
        EditorChrome.StyleField(addButton);
        addButton.Click += (_, _) => AddComponent(TerrainEntityComponentKinds.All[kindCombo.SelectedIndex]);
        toolbar.Controls.Add(kindCombo);
        toolbar.Controls.Add(addButton);

        _componentList = new FlowLayoutPanel
        {
            AutoScroll = true,
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(12),
            WrapContents = false,
        };
        _componentList.SizeChanged += (_, _) =>
        {
            foreach (Control card in _componentList.Controls)
                card.Width = Math.Max(220, _componentList.ClientSize.Width - 40);
        };

        page.Controls.Add(_componentList);
        page.Controls.Add(toolbar);
        RefreshComponentList();
        return page;
    }

    /// <summary>Adds a component of the given kind (one of <see cref="TerrainEntityComponentKinds.All"/>)
    /// with its type-specific defaults — the "＋ Add Component" button's own action, exposed
    /// directly so headless tests drive the real add path instead of re-deriving it.</summary>
    public void AddComponent(string kind)
    {
        TerrainEntityComponent component = new() { Type = kind };
        ApplyComponentDefaults(component);
        _document.Components.Add(component);
        RefreshComponentList();
    }

    private static void ApplyComponentDefaults(TerrainEntityComponent component)
    {
        switch (component.Type)
        {
            case TerrainEntityComponentKinds.Texture:
                component.Set("Mode", nameof(TerrainEntityTextureMode.Billboard2D));
                component.Set("AnimationFps", "0");
                break;
            case TerrainEntityComponentKinds.Model:
                component.Set("AnimationClip", "");
                component.Set("AnimationFps", "60");
                component.Set("Loop", "true");
                component.Set("CastShadows", "true");
                component.Set("ReceiveShadows", "true");
                break;
            case TerrainEntityComponentKinds.Condition:
                component.Set("If", "");
                component.Set("ThenClip", "");
                component.Set("ElseClip", "");
                component.Set("ThenSource", "");
                component.Set("ElseSource", "");
                break;
            case TerrainEntityComponentKinds.AudioEmitter:
                component.Set("Volume", "1");
                component.Set("Mode", nameof(TerrainEntityAudioMode.ThreeD));
                component.Set("MinDistance", "2");
                component.Set("MaxDistance", "40");
                component.Set("Falloff", nameof(TerrainEntityAudioFalloff.Linear));
                break;
            case TerrainEntityComponentKinds.Physics:
                component.Set("Shape", nameof(TerrainEntityPhysicsShape.Box));
                component.Set("AffectedByGravity", "false");
                component.Set("Solid", "true");
                component.Set("Friction", "0.5");
                component.Set("Restitution", "0");
                break;
        }
    }

    private void RefreshComponentList()
    {
        if (_componentList is null || _componentList.IsDisposed)
        {
            return;
        }

        _componentList.SuspendLayout();
        foreach (Control control in _componentList.Controls.OfType<Control>().ToArray())
        {
            control.Dispose();
        }

        _componentList.Controls.Clear();

        if (_document.Components.Count == 0)
        {
            Label empty = new()
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = EditorChrome.Muted,
                Text = "No components yet — add one above.",
            };
            _componentList.Controls.Add(empty);
        }

        foreach (TerrainEntityComponent component in _document.Components)
        {
            _componentList.Controls.Add(BuildComponentPanel(component));
        }

        int listWidth = Math.Max(220, _componentList.ClientSize.Width - 40);
        foreach (Control control in _componentList.Controls)
        {
            if (control is Panel panel)
            {
                panel.Width = listWidth;
            }
        }

        _componentList.ResumeLayout();
    }

    private Panel BuildComponentPanel(TerrainEntityComponent component)
    {
        Panel outer = new()
        {
            BackColor = EditorChrome.Surface,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12),
            MinimumSize = new Size(220, 0),
        };

        Panel header = new() { BackColor = Color.Transparent, Dock = DockStyle.Top, Height = 28 };
        Label title = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Left,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Text = TerrainEntityComponentKinds.DisplayName(component.Type),
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 170,
        };
        CheckBox enabled = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Checked = component.Enabled,
            Dock = DockStyle.Left,
            ForeColor = EditorChrome.Text,
            Text = "Enabled",
        };
        enabled.CheckedChanged += (_, _) => component.Enabled = enabled.Checked;
        Button remove = new() { Dock = DockStyle.Right, Text = "Remove", Width = 84 };
        EditorChrome.StyleField(remove);
        remove.Click += (_, _) =>
        {
            _document.Components.Remove(component);
            RefreshComponentList();
        };
        header.Controls.Add(remove);
        header.Controls.Add(enabled);
        header.Controls.Add(title);

        Control fields = BuildComponentFields(component);
        fields.Dock = DockStyle.Top;
        AddComponentActions(component, header, title, outer, fields);

        outer.Controls.Add(fields);
        outer.Controls.Add(header);
        fields.SizeChanged += (_, _) => outer.Height = header.Height + (_collapsedComponents.Contains(component) ? 0 : fields.Height) + 30;
        outer.Height = header.Height + (_collapsedComponents.Contains(component) ? 0 : fields.Height) + 30;
        return outer;
    }

    private Control BuildComponentFields(TerrainEntityComponent component) => component.Type switch
    {
        TerrainEntityComponentKinds.Shader => BuildShaderFields(component),
        TerrainEntityComponentKinds.Script => BuildScriptFields(component),
        TerrainEntityComponentKinds.ParticleEmitter => BuildAssetOnlyFields(component, "Particle", "Particle", ResourceKind.Particle, "Particles",
            path => MakeSurfaceEditor(new ParticleEditorControl(path, _projectRoot))),
        TerrainEntityComponentKinds.Model => BuildModelFields(component),
        TerrainEntityComponentKinds.Texture => BuildTextureFields(component),
        TerrainEntityComponentKinds.AudioEmitter => BuildAudioFields(component),
        TerrainEntityComponentKinds.Physics => BuildPhysicsFields(component),
        TerrainEntityComponentKinds.Condition => BuildConditionFields(component),
        _ => new Panel { Height = 1 },
    };

    /// <summary>Adapts any <see cref="EditorSurfaceControl"/> to the uniform (Control, Save) pair
    /// <see cref="BuildAssetFieldRow"/> hosts — the Image Editor doesn't share that base class
    /// (different assembly hierarchy), so the row builder works on the lowest common shape.</summary>
    private static (Control Editor, Action OnSave) MakeSurfaceEditor(EditorSurfaceControl editor)
    {
        editor.Dock = DockStyle.Fill;
        return (editor, editor.Save);
    }

    private Control BuildAssetOnlyFields(
        TerrainEntityComponent component,
        string propKey,
        string label,
        ResourceKind kind,
        string folder,
        Func<string, (Control Editor, Action OnSave)> makeEditor)
    {
        Panel panel = new() { Height = 34, Width = 800 };
        panel.Controls.Add(BuildAssetFieldRow(component, propKey, label, kind, folder, makeEditor, y: 0));
        return panel;
    }

    private Control BuildModelFields(TerrainEntityComponent component)
    {
        Panel panel = new() { Height = 132, Width = 800 };
        panel.Controls.Add(BuildAssetFieldRow(
            component, "Model", "Model", ResourceKind.Model, "Models",
            path => MakeSurfaceEditor(new ModelEditorControl(path, _projectRoot)),
            y: 0));

        Label clipLabel = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Location = new Point(0, 38),
            Text = "ANIMATION",
        };
        ThemedComboBox clipCombo = new() { Location = new Point(0, 56), Width = 220 };
        EditorChrome.StyleField(clipCombo);
        void RefreshClips()
        {
            string current = component.Get("AnimationClip");
            clipCombo.Items.Clear();
            clipCombo.Items.Add("(None)");
            foreach (string name in ListModelClipNames(component.Get("Model")))
            {
                clipCombo.Items.Add(name);
            }

            int index = 0;
            for (int i = 0; i < clipCombo.Items.Count; i++)
            {
                if (string.Equals(clipCombo.Items[i]?.ToString(), current, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            clipCombo.SelectedIndex = index;
        }

        RefreshClips();
        clipCombo.SelectedIndexChanged += (_, _) =>
        {
            string selected = clipCombo.SelectedItem?.ToString() ?? "";
            component.Set("AnimationClip", selected == "(None)" ? "" : selected);
        };

        Label fpsLabel = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Location = new Point(236, 38),
            Text = "FPS",
        };
        NumericUpDown fpsField = new()
        {
            DecimalPlaces = 0,
            Increment = 1,
            Maximum = 240,
            Minimum = 1,
            Location = new Point(236, 56),
            Width = 70,
        };
        EditorChrome.StyleField(fpsField);
        fpsField.Value = decimal.TryParse(component.Get("AnimationFps", "60"), out decimal fps) ? Math.Clamp(fps, 1m, 240m) : 60m;
        fpsField.ValueChanged += (_, _) =>
            component.Set("AnimationFps", fpsField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        CheckBox loop = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Location = new Point(320, 58),
            Text = "Loop",
        };
        loop.Checked = !string.Equals(component.Get("Loop", "true"), "false", StringComparison.OrdinalIgnoreCase);
        loop.CheckedChanged += (_, _) => component.Set("Loop", loop.Checked ? "true" : "false");

        CheckBox castShadows = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Location = new Point(0, 94),
            Text = "Cast shadows",
            Checked = !string.Equals(component.Get("CastShadows", "true"), "false", StringComparison.OrdinalIgnoreCase),
        };
        castShadows.CheckedChanged += (_, _) =>
            component.Set("CastShadows", castShadows.Checked ? "true" : "false");

        CheckBox receiveShadows = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Location = new Point(140, 94),
            Text = "Receive shadows",
            Checked = !string.Equals(component.Get("ReceiveShadows", "true"), "false", StringComparison.OrdinalIgnoreCase),
        };
        receiveShadows.CheckedChanged += (_, _) =>
            component.Set("ReceiveShadows", receiveShadows.Checked ? "true" : "false");

        panel.Controls.Add(clipLabel);
        panel.Controls.Add(clipCombo);
        panel.Controls.Add(fpsLabel);
        panel.Controls.Add(fpsField);
        panel.Controls.Add(loop);
        panel.Controls.Add(castShadows);
        panel.Controls.Add(receiveShadows);
        panel.Height += 62;
        panel.Controls.Add(NumberSetting(component, "Scale", "Scale", 1, .001m, 1000, 132));
        return panel;
    }

    private Control BuildConditionFields(TerrainEntityComponent component)
    {
        Panel panel = new() { Height = 430, Width = 800 };
        Label ifLabel = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Location = new Point(0, 0),
            Text = "IF (PGSL)",
        };
        TextBox ifBox = new() { Location = new Point(0, 18), Width = 780, Text = component.Get("If") };
        EditorChrome.StyleField(ifBox);
        ifBox.TextChanged += (_, _) => component.Set("If", ifBox.Text);

        panel.Controls.Add(ifLabel);
        panel.Controls.Add(ifBox);
        panel.Controls.Add(BuildBranchHost(
            component, "THEN", "ThenSource", "ThenClip", 48));
        panel.Controls.Add(BuildBranchHost(
            component, "ELSE", "ElseSource", "ElseClip", 238));
        return panel;
    }

    private Control BuildBranchHost(
        TerrainEntityComponent component,
        string caption,
        string sourceKey,
        string clipKey,
        int y)
    {
        Panel host = new()
        {
            Location = new Point(0, y),
            Size = new Size(780, 184),
        };
        Label label = new()
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 18,
            Text = caption + " (visual actions)",
        };
        string source = component.Get(sourceKey);
        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(component.Get(clipKey)))
        {
            source = TerrainVisualCondition.PlayAnimationSource(component.Get(clipKey));
            component.Set(sourceKey, source);
        }

        VisualActionBuilderControl builder = new(_projectRoot);
        builder.Dock = DockStyle.Fill;
        builder.LoadSource(source, groupName: caption);
        builder.SourceChanged += (_, args) =>
        {
            component.Set(sourceKey, args.Source);
            string clip = TerrainVisualCondition.ClipFromActions(args.Source, component.Get(clipKey));
            component.Set(clipKey, clip);
        };

        host.Controls.Add(builder);
        host.Controls.Add(label);
        builder.BringToFront();
        return host;
    }

    public void SetComponentProperty(int index, string key, string value)
    {
        if (index < 0 || index >= _document.Components.Count)
        {
            return;
        }

        TerrainEntityComponent component = _document.Components[index];
        component.Set(key, value);
        if (string.Equals(key, "ThenSource", StringComparison.OrdinalIgnoreCase))
        {
            component.Set("ThenClip", TerrainVisualCondition.ClipFromActions(value, component.Get("ThenClip")));
        }
        else if (string.Equals(key, "ElseSource", StringComparison.OrdinalIgnoreCase))
        {
            component.Set("ElseClip", TerrainVisualCondition.ClipFromActions(value, component.Get("ElseClip")));
        }
        else if (string.Equals(key, "ThenClip", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(component.Get("ThenSource")))
        {
            component.Set("ThenSource", TerrainVisualCondition.PlayAnimationSource(value));
        }
        else if (string.Equals(key, "ElseClip", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(component.Get("ElseSource")))
        {
            component.Set("ElseSource", TerrainVisualCondition.PlayAnimationSource(value));
        }

        if (_componentList is not null)
        {
            RefreshComponentList();
        }
    }

    private IReadOnlyList<string> ListModelClipNames(string modelReference)
    {
        if (string.IsNullOrWhiteSpace(modelReference))
        {
            return [];
        }

        try
        {
            GModelAsset asset = new RuntimeModelAssetRegistry().Load(_projectRoot, modelReference);
            return asset.Animations
                .Select(clip => clip.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return [];
        }
    }

    private Control BuildTextureFields(TerrainEntityComponent component)
    {
        Panel panel = new() { Height = 244, Width = 800 };
        panel.Controls.Add(BuildAssetFieldRow(
            component, "Texture", "Texture", ResourceKind.Image, "Sprites",
            path =>
            {
                ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(path);
                ImageDocumentSession session = new(loaded.Document, path, ImageDocumentAccess.Editor);
                ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
                ImageEditorControl editor = new(session, workspace) { Dock = DockStyle.Fill };
                return (Editor: (Control)editor, OnSave: (Action)(() => editor.Save()));
            },
            y: 0));

        Label modeLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(0, 38), Text = "MODE" };
        ThemedComboBox modeCombo = new() { Location = new Point(0, 56), Width = 180 };
        EditorChrome.StyleField(modeCombo);
        foreach (TerrainEntityTextureMode mode in new[] { TerrainEntityTextureMode.Billboard2D, TerrainEntityTextureMode.Plane3D })
        {
            modeCombo.Items.Add(mode);
        }

        modeCombo.SelectedItem = Enum.TryParse(component.Get("Mode"), out TerrainEntityTextureMode current) ? current : TerrainEntityTextureMode.Billboard2D;
        modeCombo.SelectedIndexChanged += (_, _) => component.Set("Mode", ((TerrainEntityTextureMode)modeCombo.SelectedItem!).ToString());
        panel.Controls.Add(NumberSetting(component, "Scale", "Scale", 1, .001m, 1000, 98));
        var frameCount = NumberSetting(component, "FrameCount", "Frame count (0 = all)", 0, 0, 4096, 98); frameCount.Left = 200; panel.Controls.Add(frameCount);
        foreach (NumericUpDown number in frameCount.Controls.OfType<NumericUpDown>()) { number.DecimalPlaces = 0; number.Increment = 1; }
        panel.Controls.Add(BuildPbrSettings(component, 154));

        Label fpsLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(200, 38), Text = "ANIMATION FPS (0 = static)" };
        NumericUpDown fpsField = new() { DecimalPlaces = 1, Increment = 1m, Maximum = 60m, Minimum = 0m, Location = new Point(200, 56), Width = 90 };
        EditorChrome.StyleField(fpsField);
        fpsField.Value = decimal.TryParse(component.Get("AnimationFps", "0"), out decimal fps) ? Math.Clamp(fps, 0m, 60m) : 0m;
        fpsField.ValueChanged += (_, _) => component.Set("AnimationFps", fpsField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        panel.Controls.Add(modeLabel);
        panel.Controls.Add(modeCombo);
        panel.Controls.Add(fpsLabel);
        panel.Controls.Add(fpsField);
        return panel;
    }

    private Control BuildAudioFields(TerrainEntityComponent component)
    {
        Panel panel = new() { Height = 104, Width = 800 };
        panel.Controls.Add(BuildAssetFieldRow(
            component, "Audio", "Audio", ResourceKind.Audio, "Audio",
            path => MakeSurfaceEditor(new AudioEditorControl(path, _projectRoot)),
            y: 0));

        Label volumeLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(0, 38), Text = "VOLUME" };
        NumericUpDown volumeField = new() { DecimalPlaces = 2, Increment = 0.05m, Maximum = 1m, Minimum = 0m, Location = new Point(0, 56), Width = 80 };
        EditorChrome.StyleField(volumeField);
        volumeField.Value = decimal.TryParse(component.Get("Volume", "1"), out decimal volume) ? Math.Clamp(volume, 0m, 1m) : 1m;
        volumeField.ValueChanged += (_, _) => component.Set("Volume", volumeField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Label modeLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(96, 38), Text = "MODE" };
        ThemedComboBox modeCombo = new() { Location = new Point(96, 56), Width = 90 };
        EditorChrome.StyleField(modeCombo);
        modeCombo.Items.AddRange(["2D (everywhere)", "3D (spatial)"]);
        bool is3D = component.Get("Mode", nameof(TerrainEntityAudioMode.ThreeD)) == nameof(TerrainEntityAudioMode.ThreeD);
        modeCombo.SelectedIndex = is3D ? 1 : 0;

        Label minLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(196, 38), Text = "MIN DIST" };
        NumericUpDown minField = new() { DecimalPlaces = 1, Maximum = 1000m, Minimum = 0m, Location = new Point(196, 56), Width = 70 };
        Label maxLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(276, 38), Text = "MAX DIST" };
        NumericUpDown maxField = new() { DecimalPlaces = 1, Maximum = 2000m, Minimum = 0m, Location = new Point(276, 56), Width = 70 };
        Label falloffLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(356, 38), Text = "FALLOFF" };
        ThemedComboBox falloffCombo = new() { Location = new Point(356, 56), Width = 110 };
        EditorChrome.StyleField(minField);
        EditorChrome.StyleField(maxField);
        EditorChrome.StyleField(falloffCombo);
        falloffCombo.Items.AddRange([nameof(TerrainEntityAudioFalloff.Linear), nameof(TerrainEntityAudioFalloff.Logarithmic)]);
        falloffCombo.SelectedItem = component.Get("Falloff", nameof(TerrainEntityAudioFalloff.Linear));
        minField.Value = decimal.TryParse(component.Get("MinDistance", "2"), out decimal minD) ? minD : 2m;
        maxField.Value = decimal.TryParse(component.Get("MaxDistance", "40"), out decimal maxD) ? maxD : 40m;

        void SyncSpatialEnabled()
        {
            bool spatial = modeCombo.SelectedIndex == 1;
            minField.Enabled = maxField.Enabled = falloffCombo.Enabled = spatial;
            component.Set("Mode", spatial ? nameof(TerrainEntityAudioMode.ThreeD) : nameof(TerrainEntityAudioMode.TwoD));
        }

        modeCombo.SelectedIndexChanged += (_, _) => SyncSpatialEnabled();
        minField.ValueChanged += (_, _) => component.Set("MinDistance", minField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        maxField.ValueChanged += (_, _) => component.Set("MaxDistance", maxField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        falloffCombo.SelectedIndexChanged += (_, _) => component.Set("Falloff", (string)falloffCombo.SelectedItem!);
        SyncSpatialEnabled();

        panel.Controls.Add(volumeLabel);
        panel.Controls.Add(volumeField);
        panel.Controls.Add(modeLabel);
        panel.Controls.Add(modeCombo);
        panel.Controls.Add(minLabel);
        panel.Controls.Add(minField);
        panel.Controls.Add(maxLabel);
        panel.Controls.Add(maxField);
        panel.Controls.Add(falloffLabel);
        panel.Controls.Add(falloffCombo);
        return panel;
    }

    private Control BuildPhysicsFields(TerrainEntityComponent component)
    {
        Panel panel = new() { Height = 330, Width = 800 };

        Label shapeLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(0, 0), Text = "SHAPE" };
        ThemedComboBox shapeCombo = new() { Location = new Point(0, 18), Width = 100 };
        EditorChrome.StyleField(shapeCombo);
        foreach (TerrainEntityPhysicsShape shape in Enum.GetValues<TerrainEntityPhysicsShape>())
        {
            shapeCombo.Items.Add(shape);
        }

        shapeCombo.SelectedItem = Enum.TryParse(component.Get("Shape"), out TerrainEntityPhysicsShape currentShape) ? currentShape : TerrainEntityPhysicsShape.Box;
        shapeCombo.SelectedIndexChanged += (_, _) => component.Set("Shape", ((TerrainEntityPhysicsShape)shapeCombo.SelectedItem!).ToString());

        CheckBox gravityCheck = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Text, Location = new Point(112, 20), Text = "Affected by gravity" };
        gravityCheck.Checked = component.Get("AffectedByGravity") == "true";
        gravityCheck.CheckedChanged += (_, _) => component.Set("AffectedByGravity", gravityCheck.Checked ? "true" : "false");

        CheckBox solidCheck = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Text, Location = new Point(266, 20), Text = "Solid (collides)" };
        solidCheck.Checked = component.Get("Solid", "true") == "true";
        solidCheck.CheckedChanged += (_, _) => component.Set("Solid", solidCheck.Checked ? "true" : "false");

        Label frictionLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(410, 0), Text = "FRICTION" };
        NumericUpDown frictionField = new() { DecimalPlaces = 2, Increment = 0.05m, Maximum = 2m, Minimum = 0m, Location = new Point(410, 18), Width = 70 };
        Label restitutionLabel = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Location = new Point(490, 0), Text = "RESTITUTION" };
        NumericUpDown restitutionField = new() { DecimalPlaces = 2, Increment = 0.05m, Maximum = 1m, Minimum = 0m, Location = new Point(490, 18), Width = 70 };
        EditorChrome.StyleField(frictionField);
        EditorChrome.StyleField(restitutionField);
        frictionField.Value = decimal.TryParse(component.Get("Friction", "0.5"), out decimal friction) ? friction : 0.5m;
        restitutionField.Value = decimal.TryParse(component.Get("Restitution", "0"), out decimal restitution) ? restitution : 0m;
        frictionField.ValueChanged += (_, _) => component.Set("Friction", frictionField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        restitutionField.ValueChanged += (_, _) => component.Set("Restitution", restitutionField.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        panel.Controls.Add(shapeLabel);
        panel.Controls.Add(shapeCombo);
        panel.Controls.Add(gravityCheck);
        panel.Controls.Add(solidCheck);
        panel.Controls.Add(frictionLabel);
        panel.Controls.Add(frictionField);
        panel.Controls.Add(restitutionLabel);
        panel.Controls.Add(restitutionField);
        foreach (Control field in panel.Controls) field.Top += 50;
        panel.Controls.Add(BuildAssetFieldRow(component, "Physics", "Physics", ResourceKind.Physics, "Physics",
            path => MakeSurfaceEditor(new PhysicsEditorControl(path, _projectRoot)), y: 0));
        panel.Controls.Add(NumberSetting(component, "Density", "Density (kg/m³)", 1000, .001m, 100000, 104));
        var trigger = new CheckBox { Text = "Trigger events", AutoSize = true, Left = 200, Top = 132, Checked = component.Get("IsTrigger") == "true" };
        trigger.CheckedChanged += (_, _) => component.Set("IsTrigger", trigger.Checked ? "true" : "false");
        panel.Controls.Add(trigger);
        panel.Controls.Add(BuildAssetFieldRow(component, "OnEnterScript", "On enter", ResourceKind.PgslScript, "Scripts",
            path => MakeSurfaceEditor(new Genesis.Application.Editors.Suite.Scripts.PgslScriptEditorControl(path, _projectRoot)), 180));
        panel.Controls.Add(BuildAssetFieldRow(component, "OnExitScript", "On exit", ResourceKind.PgslScript, "Scripts",
            path => MakeSurfaceEditor(new Genesis.Application.Editors.Suite.Scripts.PgslScriptEditorControl(path, _projectRoot)), 226));
        panel.Controls.Add(new Label { Text = "Attach PGSL handlers for custom contact responses. Runtime terrain event dispatch is pending.",
            Left = 0, Top = 275, AutoSize = true, ForeColor = EditorChrome.Muted });
        return panel;
    }

    /// <summary>
    /// One "Browse existing / New…" asset-reference row. "New…" creates the resource file and
    /// swaps the wizard body to a live editor for it (see <see cref="GenerateAsset"/>); Texture
    /// fields route to the Image Editor specifically since sprites need the workspace/session
    /// construction the other editors don't.
    /// </summary>
    private Control BuildAssetFieldRow(
        TerrainEntityComponent component,
        string propKey,
        string label,
        ResourceKind kind,
        string folder,
        Func<string, (Control Editor, Action OnSave)> makeEditor,
        int y)
    {
        Panel row = new() { Height = 34, Location = new Point(0, y), Width = 800, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        Label caption = new() { AutoSize = false, BackColor = Color.Transparent, Font = EditorChrome.SmallFont, ForeColor = EditorChrome.Muted, Location = new Point(0, 6), Size = new Size(96, 20), Text = label };
        ThemedComboBox combo = new() { Location = new Point(104, 0), Width = 460, DisplayMember = nameof(ProjectAssetEntry.DisplayName) };
        combo.Enabled = false;
        EditorChrome.StyleField(combo);
        bool syncing = false;
        void Populate()
        {
            syncing = true;
            try
            {
            combo.Items.Clear();
            combo.Items.Add("(none)");
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(_projectRoot, kind))
            {
                combo.Items.Add(entry);
            }

            string current = component.Get(propKey);
            if (!string.IsNullOrWhiteSpace(current) && !combo.Items.OfType<ProjectAssetEntry>().Any(entry => string.Equals(entry.Reference, current, StringComparison.OrdinalIgnoreCase)))
            {
                string fullPath = ResourceNames.Resolve(_projectRoot, current);
                if (File.Exists(fullPath)) combo.Items.Add(new ProjectAssetEntry(ResourceDisplayName.Format(current), fullPath, current, kind));
            }
            for (int i = 1; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ProjectAssetEntry entry && string.Equals(entry.Reference, current, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            combo.SelectedIndex = 0;
            }
            finally { syncing = false; }
        }

        Populate();
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (syncing) return;
            component.Set(propKey, combo.SelectedItem is ProjectAssetEntry entry ? entry.Reference : "");
            ComponentAssetChanged?.Invoke(component, propKey);
        };

        Button newButton = new()
        {
            Name = $"TerrainEntity{propKey}NewButton",
            AccessibleName = $"Create new {label} resource",
            Location = new Point(576, 0),
            Size = new Size(80, 28),
            Text = "New…",
        };
        EditorChrome.StyleField(newButton);
        Button browseButton = new()
        {
            Name = $"TerrainEntity{propKey}BrowseButton",
            AccessibleName = $"Choose existing {label} resource",
            Location = new Point(496, 0),
            Size = new Size(72, 28),
            Text = "Browse…",
        };
        EditorChrome.StyleField(browseButton);
        browseButton.Click += (_, _) =>
        {
            ProjectAssetEntry? selected = AssetPickerService.PickAsset(
                new AssetPickerRequest(_projectRoot, kind, component.Get(propKey), $"Choose {label}",
                    AllowNone: true), FindForm());
            if (selected is null) return;
            component.Set(propKey, selected.Reference);
            Populate();
            ComponentAssetChanged?.Invoke(component, propKey);
        };
        newButton.Click += (_, _) => GenerateAsset(
            kind,
            folder,
            "New" + label,
            makeEditor,
            path =>
            {
                component.Set(propKey, ResourceNames.Name(_projectRoot, path));
                Populate();
                ComponentAssetChanged?.Invoke(component, propKey);
            });

        row.Controls.Add(caption);
        row.Controls.Add(combo);
        row.Controls.Add(browseButton);
        row.Controls.Add(newButton);
        row.SizeChanged += (_, _) =>
        {
            newButton.Left = Math.Max(310, row.ClientSize.Width - 84);
            browseButton.Left = newButton.Left - 78;
            combo.Width = Math.Max(110, browseButton.Left - combo.Left - 8);
        };
        return row;
    }

    /// <summary>
    /// Creates a new resource of <paramref name="kind"/> under Assets/<paramref name="folder"/>,
    /// then swaps the wizard's entire body to the live editor <paramref name="makeEditor"/>
    /// builds for it — "in-wizard asset generation with live editing" per spec. "Done" saves
    /// and restores the previous page, calling <paramref name="onDone"/> with the new path.
    /// </summary>
    private void GenerateAsset(
        ResourceKind kind,
        string folder,
        string baseName,
        Func<string, (Control Editor, Action OnSave)> makeEditor,
        Action<string> onDone)
    {
        string path;
        try
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(_projectRoot);
            string parent = Path.Combine(resources.AssetsRoot, folder);
            // Not every destination folder exists in a fresh project (e.g. "Sprites" — new
            // projects seed "Art" instead) — CreateResource requires the folder to already
            // exist, so ensure it first.
            if (!Directory.Exists(parent))
            {
                resources.CreateFolder(resources.AssetsRoot, folder);
            }

            path = resources.CreateResource(parent, kind, baseName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Control owner = (Control?)FindForm() ?? this;
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(owner, $"Could not create the asset: {exception.Message}", "Terrain Entity", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        (Control editorControl, Action? onSave) = makeEditor(path);
        int savedPage = _page;
        Control previousBody = _contentHost.Controls.Count > 0 ? _contentHost.Controls[0] : new Panel();

        Panel host = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        Panel doneBar = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 42 };
        Label hint = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Padding = new Padding(10, 0, 0, 0),
            Text = $"Editing new {kind} live — Done saves it and returns to the wizard.",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Button doneButton = new() { Dock = DockStyle.Right, Text = "Done", Width = 110 };
        EditorChrome.StyleField(doneButton);
        doneButton.Click += (_, _) =>
        {
            onSave?.Invoke();
            editorControl.Dispose();
            _contentHost.Controls.Clear();
            _contentHost.Controls.Add(previousBody);
            GoToPage(savedPage);
            onDone(path);
        };
        doneBar.Controls.Add(hint);
        doneBar.Controls.Add(doneButton);
        host.Controls.Add(editorControl);
        host.Controls.Add(doneBar);

        _contentHost.Controls.Clear();
        _contentHost.Controls.Add(host);
        _pageLabel.Text = $"Creating {kind} — {baseName}";
        _backButton.Visible = false;
        _nextButton.Visible = false;
        _saveButton.Visible = false;
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(_document.Name))
        {
            _document.Name = "Terrain Entity";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_resourcePath)!);
        File.WriteAllText(_resourcePath, JsonSerializer.Serialize(_document, DocumentJsonOptions));
        Saved?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Small helper so type cards can show a selection border without a custom Panel subclass.</summary>
internal static class PanelSelectionExtensions
{
    public static void BorderStyleSafe(this Panel panel, bool selected)
    {
        panel.Paint -= PaintBorderHandler;
        if (selected)
        {
            panel.Paint += PaintBorderHandler;
        }

        panel.Invalidate();
    }

    private static void PaintBorderHandler(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel panel)
        {
            return;
        }

        using System.Drawing.Pen pen = new(EditorChrome.Accent, 2f);
        e.Graphics.DrawRectangle(pen, 1, 1, panel.Width - 3, panel.Height - 3);
    }
}


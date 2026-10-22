using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;
using Genesis.Application.Core;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// The layout of Preferences: grouped pages in a side list, a search that looks through every
/// setting, and each page made of section cards whose rows say in plain words what a setting does.
/// The values themselves are loaded and saved in PreferencesForm.cs exactly as before.
/// </summary>
public sealed partial class PreferencesForm
{
    private const string StudioGroup = "STUDIO";
    private const string EngineGroup = "ENGINE ON THIS PC";
    private const string ProjectGroup = "THIS PROJECT";

    private static readonly (string Name, string Glyph, string Group, string Title, string Description)[] Categories =
    [
        ("General", "⚙", StudioGroup, "General", "Starting Studio, keeping your work safe, and your GitHub account."),
        ("Appearance", "◑", StudioGroup, "Appearance", "Colours or a picture behind the workspace, text size and motion."),
        ("Shortcuts", "⌨", StudioGroup, "Shortcuts", "The keys for every command, shared by the menus and the editors."),
        ("Running", "▶", EngineGroup, "Running games", "How Run and Debug start your game on this PC."),
        ("Graphics", "◆", EngineGroup, "Graphics", "How this PC draws editor views and games: the renderer, light, sky and colour."),
        ("Performance", "▤", EngineGroup, "Performance", "Limits that keep big scenes fast on this PC."),
        ("Game", "◉", ProjectGroup, "Game", "Saved in the project, so they travel with the game to every PC and player."),
        ("Project", "▣", ProjectGroup, "Project", "The project's name, its online backup and what PC it needs."),
    ];

    private static readonly string[] StudioCategoryNames = ["General", "Appearance", "Shortcuts", "Running", "Graphics", "Performance"];
    private static readonly string[] ProjectCategoryNames = ["Game", "Project"];

    /// <summary>Old page names, still accepted by <see cref="SelectCategory"/>.</summary>
    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.Ordinal)
    {
        ["Editing"] = "General",
        ["Runtime"] = "Running",
        ["Rendering"] = "Graphics",
    };

    private SettingsSection? _themeSection;
    private SettingRow? _addThemeImageRow;

    private (ListBox Categories, Panel Content) BuildShell()
    {
        TableLayoutPanel layout = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 3,
            Tag = "canvas",
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(layout);

        // Header: title, a line under it, and the search box on the right.
        Panel heading = new() { BackColor = ThemeService.Palette.Surface, Dock = DockStyle.Fill, Tag = "surface" };
        Label title = new() { AutoSize = true, Font = ThemeService.HeadingFont, ForeColor = ThemeService.Palette.Text, Text = "Preferences" };
        Label subtitle = new()
        {
            AutoSize = true,
            ForeColor = ThemeService.Palette.TextMuted,
            Text = _project is null ? "Studio and this PC." : $"Studio, this PC and '{_project.Manifest.Name}'.",
        };
        Panel searchBox = new() { Tag = "surface", BackColor = ThemeService.Palette.Surface };
        _categoryFilter.BorderStyle = BorderStyle.None;
        _categoryFilter.BackColor = ThemeService.Palette.SurfaceRaised;
        _categoryFilter.ForeColor = ThemeService.Palette.Text;
        _categoryFilter.PlaceholderText = "Search settings…";
        _categoryFilter.AccessibleName = "Search settings";
        _categoryFilter.TextChanged += (_, _) => ApplySearch();
        searchBox.Controls.Add(_categoryFilter);
        searchBox.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new(0, 0, searchBox.Width - 1, searchBox.Height - 1);
            using GraphicsPath path = UiDrawing.RoundedRectangle(box, searchBox.Height / 2);
            using SolidBrush fill = new(ThemeService.Palette.SurfaceRaised);
            using Pen edge = new(_categoryFilter.Focused ? ThemeService.Palette.Accent : ThemeService.Palette.Border);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(edge, path);
            TextRenderer.DrawText(e.Graphics, "⌕", Font, new Rectangle(DpiLayout.Scale(this, 10), 0, DpiLayout.Scale(this, 20), searchBox.Height),
                ThemeService.Palette.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        _categoryFilter.GotFocus += (_, _) => searchBox.Invalidate();
        _categoryFilter.LostFocus += (_, _) => searchBox.Invalidate();
        heading.Controls.AddRange([title, subtitle, searchBox]);
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        ListBox categories = new()
        {
            BackColor = ThemeService.Palette.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawVariable,
            Font = ThemeService.InterfaceFont,
            ForeColor = ThemeService.Palette.Text,
            IntegralHeight = false,
            Name = "PreferencesCategories",
        };
        categories.Items.AddRange(_allCategories);
        categories.MeasureItem += MeasureCategory;
        categories.DrawItem += DrawCategory;
        categories.SelectedIndexChanged += (_, _) => ShowSelectedPage();
        Panel navigation = new() { BackColor = ThemeService.Palette.Surface, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10), Tag = "surface" };
        navigation.Controls.Add(categories);
        layout.Controls.Add(navigation, 0, 1);

        Panel content = new() { BackColor = ThemeService.Palette.Canvas, Dock = DockStyle.Fill, Tag = "canvas" };
        layout.Controls.Add(content, 1, 1);

        // Footer: defaults on the left; Apply, Cancel and OK on the right.
        Panel footer = new() { BackColor = ThemeService.Palette.Surface, Dock = DockStyle.Fill, Padding = new Padding(16), Tag = "surface" };
        ModernButton reset = new() { Dock = DockStyle.Left, Size = new Size(160, 38), Text = "Restore defaults", Glyph = "↺" };
        reset.Click += (_, _) => RestoreDefaults();
        ModernButton cancel = new() { DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Size = new Size(104, 38), Text = "Cancel" };
        ModernButton ok = new() { Accent = true, Dock = DockStyle.Right, Size = new Size(104, 38), Text = "OK" };
        ok.Click += (_, _) =>
        {
            if (TrySaveAndApply())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        ModernButton apply = new() { Dock = DockStyle.Right, Name = "ApplyPreferences", Size = new Size(104, 38), Text = "Apply" };
        apply.Click += (_, _) => TrySaveAndApply();
        Panel gapA = new() { Dock = DockStyle.Right, Width = 10, Tag = "surface" };
        Panel gapB = new() { Dock = DockStyle.Right, Width = 10, Tag = "surface" };
        footer.Controls.AddRange([reset, ok, gapA, cancel, gapB, apply]);
        layout.Controls.Add(footer, 0, 2);
        layout.SetColumnSpan(footer, 2);
        CancelButton = cancel;

        bool arranging = false;
        _layoutMeasured = () =>
        {
            if (arranging || IsDisposed) return;
            arranging = true;
            try
            {
                int S(int value) => DpiLayout.Scale(this, value);
                layout.SuspendLayout();
                title.Font = ThemeService.HeadingFont;
                subtitle.Font = ThemeService.InterfaceFont;
                title.Location = new Point(S(24), S(14));
                subtitle.Location = new Point(S(26), title.Bottom + S(2));
                layout.RowStyles[0].Height = subtitle.Bottom + S(14);
                int searchWidth = Math.Min(S(340), Math.Max(S(180), ClientSize.Width / 3));
                int searchHeight = _categoryFilter.Font.Height + S(14);
                searchBox.Bounds = new Rectangle(ClientSize.Width - searchWidth - S(24), (int)(layout.RowStyles[0].Height - searchHeight) / 2, searchWidth, searchHeight);
                _categoryFilter.Bounds = new Rectangle(S(34), (searchHeight - _categoryFilter.PreferredHeight) / 2, searchWidth - S(48), _categoryFilter.PreferredHeight);
                using Font bold = new(Font, FontStyle.Bold);
                layout.ColumnStyles[0].Width = Math.Clamp(TextRenderer.MeasureText(EngineGroup, bold).Width + S(56), S(210), S(300));
                foreach (Button button in footer.Controls.OfType<Button>()) ShellDialogLayout.FitButton(button);
                layout.RowStyles[2].Height = Font.Height + S(48);
                RefreshCategoryHeights();
                layout.ResumeLayout(true);
                ArrangeCurrentPage();
            }
            finally { arranging = false; }
        };
        ClientSizeChanged += (_, _) => ApplyInterfaceLayout();
        Shown += (_, _) => { _categories.TopIndex = 0; ApplyInterfaceLayout(); };
        return (categories, content);
    }

    private void BuildPages()
    {
        AddPage(BuildGeneralPage());
        AddPage(BuildAppearancePage());
        AddPage(BuildShortcutsPage());
        AddPage(BuildRunningPage());
        AddPage(BuildGraphicsPage());
        AddPage(BuildPerformancePage());
        if (_project is not null)
        {
            AddPage(BuildGamePage());
            AddPage(BuildProjectPage());
        }
    }

    private void AddPage(PreferencePage page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages[page.Name] = page;
        _contentHost.Controls.Add(page);
    }

    private static PreferencePage NewPage(string name)
    {
        var info = Categories.First(category => category.Name == name);
        return new PreferencePage(name, info.Title, info.Description);
    }

    /// <summary>A row of buttons or a box and a button, wrapping when the window is narrow.</summary>
    private static FlowLayoutPanel ControlRow(params Control[] controls)
    {
        FlowLayoutPanel row = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            Tag = "transparent",
            WrapContents = true,
        };
        foreach (Control control in controls)
        {
            control.Margin = new Padding(0, 2, 8, 2);
            if (control is ModernButton button)
            {
                ShellDialogLayout.FitButton(button);
                // Sized for its text again when the dialog's font grows (larger text settings).
                button.FontChanged += (_, _) => ShellDialogLayout.FitButton(button);
            }
            row.Controls.Add(control);
        }
        return row;
    }

    private static Label WideText(string text, Label? label = null)
    {
        label ??= new Label();
        label.AutoSize = false;
        label.BackColor = Color.Transparent;
        label.ForeColor = ThemeService.Palette.TextMuted;
        label.Text = text;
        label.Height = 40;
        label.UseMnemonic = false;
        return label;
    }

    // ── General ────────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildGeneralPage()
    {
        PreferencePage page = NewPage("General");
        SettingsSection startup = page.Section("Starting Studio");
        startup.Add("Show the launch screen", "The emblem and loading steps while Studio starts.", _showSplash);
        startup.Add("Reopen the last project", "Skip the Project Hub and open what you worked on last.", _reopenLast);

        SettingsSection safety = page.Section("Keeping your work safe");
        safety.Add("Ask before deleting", "Confirm before resources go to the project trash, and before other steps that cannot be undone.", _confirmDelete);
        safety.Add("Notice changes made outside Studio", "Reload resources when another program changes their files.", _externalChanges);
        ConfigureNumeric(_autoSaveMinutes, 1, 60, 1, 120);
        ConfigureNumeric(_backupDays, 1, 365, 1, 120);
        safety.Add("Autosave", "Save edited resources by themselves.", _autoSave);
        safety.Add("Autosave every (minutes)", null, _autoSaveMinutes, 120);
        safety.Add("Back up before project upgrades", "Copy the project before a migration changes its files.", _backups, searchText: "backups");
        safety.Add("Keep those copies for (days)", null, _backupDays, 120);

        BuildGitHubRows(page.Section("GitHub"));
        return page;
    }

    // ── Appearance ─────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildAppearancePage()
    {
        PreferencePage page = NewPage("Appearance");
        SettingsSection theme = page.Section("Theme");
        _themeSection = theme;
        ConfigureThemeMode(_colourThemeMode, "Colours");
        ConfigureThemeMode(_imageThemeMode, "Picture");
        _colourThemeMode.CheckedChanged += (_, _) => ThemeModeChanged();
        _imageThemeMode.CheckedChanged += (_, _) => ThemeModeChanged();
        theme.Add("Style", "Flat colours, or a picture that Studio takes its colours from.", ControlRow(_colourThemeMode, _imageThemeMode), searchText: "image colour theme mode");

        ConfigureCombo(_colourTheme, [.. ThemeCatalog.ColourNames.Select(ThemeCatalog.ColourDisplayName)], 260);
        _colourTheme.SelectedIndexChanged += (_, _) => RefreshThemePreview();
        _colourThemeField = theme.Add("Colour theme", null, _colourTheme);

        _imageThemeGallery.Size = new Size(560, 138);
        _imageThemeGallery.SelectedThemeChanged += (_, _) => RefreshThemePreview();
        _imageThemeGallery.SetImages(ThemeCatalog.Images);
        _imageThemeField = theme.AddWide(_imageThemeGallery, "Pictures", searchText: "theme images gallery");

        _addThemeImage.Text = "Add a picture…";
        _addThemeImage.Glyph = "＋";
        _addThemeImage.Click += (_, _) => AddThemeImage();
        _addThemeImageRow = theme.AddWide(ControlRow(_addThemeImage), searchText: "add theme image");

        _themePreview.Size = new Size(560, 220);
        _themePreview.Paint += PaintThemePreview;
        theme.AddWide(_themePreview, "Preview", searchText: "theme preview");

        SettingsSection interfaceSection = page.Section("Text and layout");
        ConfigureCombo(_density, ["Compact", "Comfortable", "Spacious"], 200);
        ConfigureNumeric(_scale, 75, 200, 5, 120);
        ConfigureNumeric(_codeFontSize, 8, 24, 1, 120);
        interfaceSection.Add("Density", "How much space panels and lists leave around things.", _density, 200);
        interfaceSection.Add("Interface size (%)", "Scales all of Studio's text and controls.", _scale, 120, "scale");
        interfaceSection.Add("Code text size", "The size of text in the PGSL and shader editors.", _codeFontSize, 120, "font");
        interfaceSection.Add("Interface animations", "Fades on hover, cards that appear in turn, rows that slide into the Assets tree.", _animations, searchText: "motion");
        RefreshThemePreview();
        return page;
    }

    private static void ConfigureThemeMode(RadioButton mode, string text)
    {
        mode.AutoSize = true;
        mode.BackColor = Color.Transparent;
        mode.ForeColor = ThemeService.Palette.Text;
        mode.Text = text;
    }

    // ── Shortcuts ──────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildShortcutsPage()
    {
        PreferencePage page = NewPage("Shortcuts");
        SettingsSection commands = page.Section("Commands");
        TextBox find = new() { PlaceholderText = "Find a command or key…", AccessibleName = "Find a shortcut" };
        ListView shortcuts = new()
        {
            BackColor = ThemeService.Palette.Surface,
            BorderStyle = BorderStyle.None,
            ForeColor = ThemeService.Palette.Text,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            View = View.Details,
        };
        shortcuts.Columns.Add("Command", 360);
        shortcuts.Columns.Add("Keys", 180);
        (string Command, string Binding)[] bindings = [.. _settings.Current.Shortcuts.Bindings
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, pair.Value))];
        void Fill()
        {
            string text = find.Text.Trim();
            shortcuts.BeginUpdate();
            shortcuts.Items.Clear();
            foreach ((string command, string binding) in bindings)
                if (text.Length == 0 || command.Contains(text, StringComparison.OrdinalIgnoreCase) || binding.Contains(text, StringComparison.OrdinalIgnoreCase))
                    shortcuts.Items.Add(new ListViewItem([command, binding]));
            shortcuts.EndUpdate();
        }
        find.TextChanged += (_, _) => Fill();
        shortcuts.Resize += (_, _) =>
        {
            int available = Math.Max(1, shortcuts.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            shortcuts.Columns[0].Width = Math.Max(DpiLayout.Scale(shortcuts, 180), (int)(available * 0.68f));
            shortcuts.Columns[1].Width = Math.Max(1, available - shortcuts.Columns[0].Width);
        };
        Fill();
        commands.AddWide(new TextFieldHost(find, 320), searchText: "shortcut keys");
        commands.AddWide(new SettingsList(shortcuts, 440), searchText: string.Join(' ', bindings.Select(binding => binding.Command)));
        return page;
    }

    // ── Running ────────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildRunningPage()
    {
        PreferencePage page = NewPage("Running");
        SettingsSection run = page.Section("Run and Debug");
        ConfigureCombo(_buildConfiguration, ["Debug", "Release"], 200);
        ConfigureCombo(_architecture, ["win-x64"], 200);
        _architecture.Enabled = false;
        run.Add("Build", "Debug keeps checks and logging; Release runs as your players will see it.", _buildConfiguration, 200, "configuration");
        run.Add("Player", "Windows x64, the only player Genesis builds today.", _architecture, 200, "architecture");
        run.Add("VSync in editor previews", "Previews wait for the display, which saves power and stops tearing.", _previewVsync);
        run.Add("Pause previews in the background", "Stop previews while Studio is not the active window.", _pauseUnfocused, searchText: "focus");

        SettingsSection debug = page.Section("Debugging");
        debug.Add("Collect diagnostics", "Gather timings while a game runs, for the Profiler.", _runtimeDiagnostics, searchText: "profiler runtime");
        debug.Add("Record when debugging starts", "Start a diagnostics recording as soon as Debug runs.", _recordWhenDebugging);
        debug.Add("Show the Engine debug category", "Engine internals in the debug overlay, for engine development.", _engineDebugCategory, searchText: "developer");
        return page;
    }

    // ── Graphics ───────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildGraphicsPage()
    {
        PreferencePage page = NewPage("Graphics");
        SettingsSection renderer = page.Section("Renderer");
        // Built from the catalogue so a backend's label cannot claim something its descriptor contradicts.
        string[] backends =
        [
            .. RenderBackendCatalog.All.Select(backend => backend.IsImplemented
                ? backend.DisplayName
                : $"{backend.DisplayName} (not available yet: uses Direct3D 11)"),
        ];
        ConfigureCombo(_renderBackend, backends, 320);
        renderer.Add("Graphics API", "Draws the editor views and your games. If this PC cannot start it, Direct3D 11 is used and the log says why.",
            _renderBackend, 320, "backend directx vulkan opengl");
        ModernButton testBackends = new() { Text = "Test this renderer…", Glyph = "▶" };
        testBackends.Click += (_, _) =>
        {
            RenderBackendOption backend = BackendAt(_renderBackend.SelectedIndex).Backend;
            using PgslCommandReferenceForm commands = new();
            commands.PrepareBackendVisualTest(backend);
            commands.Shown += async (_, _) => await commands.RunVisualTestAsync();
            commands.ShowDialog(this);
        };
        renderer.Add("Visual test", "Opens the live 3D test scene on the chosen renderer.", ControlRow(testBackends), searchText: "test backends");

        SettingsSection light = page.Section("Light and shadow");
        ConfigureNumeric(_shadowStrength, 0, 100, 1, 120);
        _shadowStrength.DecimalPlaces = 0;
        ConfigureNumeric(_shadowCascadeCount, 2, 3, 1, 120);
        _shadowCascadeCount.DecimalPlaces = 0;
        _lightingEnabled.CheckedChanged += (_, _) => RefreshLightingPreferenceState();
        _shadowsEnabled.CheckedChanged += (_, _) => RefreshLightingPreferenceState();
        _bloomEnabled.CheckedChanged += (_, _) => RefreshLightingPreferenceState();
        light.Add("Lighting", "The master switch for sunlight, ambient light and light emitters. Everything below needs it.", _lightingEnabled);
        light.Add("Shadows", null, _shadowsEnabled);
        light.Add("Shadow strength (%)", "How dark shadows are.", _shadowStrength, 120);
        light.Add("Shadow detail bands", "Three adds a far band, so distant shadows stay sharp.", _shadowCascadeCount, 120, "cascades");
        light.Add("Ambient occlusion", "Soft shading in creases and corners (GTAO). Skipped by the Software renderer.", _gtaoEnabled, searchText: "gtao ao");
        light.Add("Contact shadows", "Short, sharp shadows where things touch the ground, from the sun.", _contactShadowsEnabled);
        light.Add("Light beams", "Glow and shafts around the strongest few lights.", _localVolumetricsEnabled, searchText: "volumetric scatter");

        SettingsSection sky = page.Section("Sky and atmosphere");
        ConfigureCombo(_cloudQuality, ["Performance", "Balanced", "High", "Cinematic"], 200);
        sky.Add("Atmospheric sky", "A sky that scatters light like air, instead of a flat colour.", _atmosphereLutEnabled, searchText: "atmosphere lut");
        sky.Add("Volumetric clouds", "Clouds with depth, drawn every frame.", _raymarchedCloudsEnabled, searchText: "raymarched");
        sky.Add("Smooth clouds over time", "Blends clouds across frames so they cost less.", _cloudTemporalEnabled, searchText: "temporal");
        sky.Add("Cloud quality", "Only the clouds' internal resolution changes (High is half resolution).", _cloudQuality, 200);
        sky.Add("Stars, Milky Way and moon", "A night sky with a moon that shows its phase.", _celestialExtrasEnabled, searchText: "celestial");
        sky.Add("Smoke dims what is behind it", "See-through smoke darkens the view through it.", _smokeExtinctionEnabled, searchText: "extinction particles");
        sky.Add("Water reflections", "One reflective water surface, at half resolution.", _waterReflections);

        SettingsSection colour = page.Section("Colour and glow");
        ConfigureNumeric(_exposure, 0.05m, 8m, 0.05m, 120);
        _exposure.DecimalPlaces = 2;
        ConfigureNumeric(_contrast, 0.05m, 4m, 0.05m, 120);
        _contrast.DecimalPlaces = 2;
        ConfigureNumeric(_saturation, 0m, 4m, 0.05m, 120);
        _saturation.DecimalPlaces = 2;
        ConfigureNumeric(_vignetteStrength, 0m, 1m, 0.05m, 120);
        _vignetteStrength.DecimalPlaces = 2;
        ConfigureNumeric(_bloomThreshold, 0m, 16m, 0.1m, 120);
        _bloomThreshold.DecimalPlaces = 2;
        ConfigureNumeric(_bloomIntensity, 0m, 2m, 0.01m, 120);
        _bloomIntensity.DecimalPlaces = 2;
        colour.Add("Exposure", "Overall brightness; 1.00 leaves it unchanged.", _exposure, 120);
        colour.Add("Contrast", "1.00 leaves it unchanged.", _contrast, 120);
        colour.Add("Saturation", "Colour strength; 1.00 leaves it unchanged, 0 is greyscale.", _saturation, 120);
        colour.Add("Vignette", "Darkens the edges of the picture; 0 is off.", _vignetteStrength, 120);
        colour.Add("Glow (bloom)", "Bright things bleed light. Off by default.", _bloomEnabled, searchText: "hdr bloom");
        colour.Add("Glow starts at", "How bright something must be to glow.", _bloomThreshold, 120, "bloom threshold");
        colour.Add("Glow strength", null, _bloomIntensity, 120, "bloom intensity");

        SettingsSection geometry = page.Section("Geometry");
        ConfigureCombo(_faceCulling, ["Back", "Front", "None"], 200);
        ConfigureCombo(_frontFaceWinding, ["Clockwise", "CounterClockwise"], 200);
        geometry.Add("Hide faces", "Which side of each triangle is not drawn. A resource can choose its own in the Inspector.", _faceCulling, 200, "cull culling");
        geometry.Add("Front faces turn", "The winding that counts as a triangle's front.", _frontFaceWinding, 200, "winding");
        geometry.Add("Reversed depth", "Keeps a close near plane in long views, so far scenes do not flicker.", _reversedDepth);
        return page;
    }

    // ── Performance ────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildPerformancePage()
    {
        PreferencePage page = NewPage("Performance");
        SettingsSection limits = page.Section("Scene limits");
        ConfigureNumeric(_spriteInstanceCap, RenderCapacityDefaults.MinInstanceCap, RenderCapacityDefaults.HardwareSpriteInstanceCap, 256, 140);
        ConfigureNumeric(_meshInstanceCap, RenderCapacityDefaults.MinInstanceCap, RenderCapacityDefaults.HardwareMeshInstanceCap, 256, 140);
        ConfigureNumeric(_sceneLocalLightCap, RenderCapacityDefaults.MinSceneLocalLightCap, RenderCapacityDefaults.MaxSceneLocalLightCap, 8, 140);
        ConfigureNumeric(_omniShadowBudget, RenderCapacityDefaults.MinOmniShadowBudget, RenderCapacityDefaults.MaxOmniShadowBudget, 1, 140);
        ConfigureNumeric(_localVolumetricLightBudget, RenderCapacityDefaults.MinLocalVolumetricLightBudget, RenderCapacityDefaults.MaxLocalVolumetricLightBudget, 1, 140);
        limits.Add("2D sprites drawn at once", "Sprites uploaded to the GPU per frame.", _spriteInstanceCap, 140, "sprite instance cap");
        limits.Add("3D meshes drawn at once", "Mesh copies uploaded to the GPU per frame.", _meshInstanceCap, 140, "mesh instance cap");
        limits.Add("Lights in a scene", "Each part of the screen still uses at most 32 of them.", _sceneLocalLightCap, 140, "local light cap");
        limits.Add("Point lights with shadows", "From 0 to 4.", _omniShadowBudget, 140, "omni shadow budget");
        limits.Add("Lights with beams", "From 0 to 4.", _localVolumetricLightBudget, 140, "volumetric light budget");

        SettingsSection draws = page.Section("Draw calls");
        ConfigureCombo(_drawCallMode, [RenderCapacityDefaults.DrawCallModeAuto, RenderCapacityDefaults.DrawCallModeManual], 200);
        ConfigureNumeric(_worldDrawBudget, RenderCapacityDefaults.MinWorldDrawBudget, RenderCapacityDefaults.MaxWorldDrawBudget, 1, 140);
        _drawCallMode.SelectedIndexChanged += (_, _) => RefreshDrawBudgetPreferenceState();
        draws.Add("Draw call budget", "Auto picks one for this PC; Manual uses the number below.", _drawCallMode, 200, "mode");
        draws.Add("World draw calls", "Manual only. Shadows, post effects, the HUD and the sky are not counted.", _worldDrawBudget, 140, "world draw budget");
        return page;
    }

    // ── Game (saved in the project) ────────────────────────────────────────────────────────

    private PreferencePage BuildGamePage()
    {
        PreferencePage page = NewPage("Game");
        SettingsSection play = page.Section("Playing");
        ConfigureCombo(_antiAliasing, ["Off", "FXAA", "SMAA", "TAA"], 200);
        play.Add("Esc closes the game", "Turn off when the game uses Esc itself, for a pause menu.", _allowEscapeToClose, searchText: "escape");
        play.Add("Smooth 3D edges", "Anti-aliasing before the GUI is drawn, so text stays sharp. FXAA is cheapest; SMAA is sharper; TAA blends frames over time for the smoothest edges and fine detail. RenderSetAntiAliasing changes it in a game.",
            _antiAliasing, 200, "anti-aliasing fxaa smaa taa temporal");
        play.Add("Blend the GUI in linear light", "GUI shapes, text and images mix with the screen as light does. Off draws exactly as before.", _blendGuiInLinearLight);

        SettingsSection fog = page.Section("Engine fog");
        _fogColor.Size = new Size(120, 32);
        _fogColor.MaxLength = 7;
        _fogColor.PlaceholderText = "#RRGGBB";
        _fogColor.TextChanged += (_, _) => SyncFogSwatch();
        ConfigureNumeric(_fogDepth, -10240, 10240, 1, 140);
        _fogDepth.DecimalPlaces = 1;
        ConfigureNumeric(_fogThickness, 0.1m, 20480, 1, 140);
        _fogThickness.DecimalPlaces = 1;
        ConfigureNumeric(_fogAlpha, 0, 100, 1, 140);
        _fogAlpha.DecimalPlaces = 0;
        fog.Add("Fog", "The engine's default fog for this game.", _fogEnabled);
        fog.Add("Colour", null, BuildFogColourPicker(), 300, "fog colour");
        fog.Add("Starts at", "3D: distance from the camera. 2D: layer depth.", _fogDepth, 140, "fog depth start");
        fog.Add("Thickness", "How far the fog takes to become solid.", _fogThickness, 140, "fog thickness");
        fog.Add("Opacity (%)", null, _fogAlpha, 140, "fog alpha");

        SettingsSection icon = page.Section("Project icon");
        ConfigureNumeric(_projectIconFps, 0, 60, 1, 80);
        _projectIconFps.DecimalPlaces = 0;
        _projectIconFps.ValueChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_projectIconPath)) _projectIconPreview.LoadIcon(_projectIconPath, (int)_projectIconFps.Value);
        };
        ModernButton pickIcon = new() { Text = "Set icon…" };
        pickIcon.Click += (_, _) =>
        {
            ContextMenuStrip menu = new();
            menu.Items.Add("Import from disk…", null, (_, _) => PickProjectIconFromDisk());
            menu.Items.Add("Import from project resources…", null, (_, _) => PickProjectIconFromProject());
            menu.Items.Add("Clear", null, (_, _) => { _projectIconPath = string.Empty; _projectIconPreview.LoadIcon("", 0); });
            menu.Show(pickIcon, new Point(0, pickIcon.Height));
        };
        icon.Add("Icon", "Shown on the Project Hub card. An animated image plays at the speed below.", ControlRow(_projectIconPreview, pickIcon), searchText: "project icon");
        icon.Add("Animation speed (frames per second)", null, _projectIconFps, 80, "fps");
        return page;
    }

    // ── Project ────────────────────────────────────────────────────────────────────────────

    private PreferencePage BuildProjectPage()
    {
        PreferencePage page = NewPage("Project");
        BuildRenameRows(page.Section("Name"));
        BuildBackupRows(page.Section("Online backup"));

        SettingsSection requirements = page.Section("What PC it needs");
        _systemRequirementsSummary.ForeColor = ThemeService.Palette.Text;
        _systemRequirementsSummary.BackColor = Color.Transparent;
        _systemRequirementsSummary.Height = 110;
        ModernButton refresh = new() { Name = "SystemRequirementsRefresh", Text = "Refresh estimate", Glyph = "↻" };
        refresh.Click += (_, _) => RefreshSystemRequirementsEstimate();
        requirements.AddWide(_systemRequirementsSummary, null,
            "An estimate for an outdoor showcase scene with this project's renderer, limits, shadows and texture groups. Not a benchmark of this PC.",
            "system requirements estimate");
        requirements.AddWide(ControlRow(refresh));
        RefreshSystemRequirementsEstimate();
        return page;
    }

    // ── Navigation and search ──────────────────────────────────────────────────────────────

    private static string GroupOf(string category) => Categories.FirstOrDefault(info => info.Name == category).Group ?? StudioGroup;

    private bool StartsGroup(int index) =>
        index == 0 || GroupOf(_categories.Items[index]?.ToString() ?? "") != GroupOf(_categories.Items[index - 1]?.ToString() ?? "");

    private int CategoryRowHeight => Math.Max(DpiLayout.Scale(this, 36), Font.Height + DpiLayout.Scale(18, DeviceDpi));

    private int GroupHeaderHeight => Font.Height + DpiLayout.Scale(this, 16);

    private void MeasureCategory(object? sender, MeasureItemEventArgs e) =>
        e.ItemHeight = CategoryRowHeight + (e.Index >= 0 && StartsGroup(e.Index) ? GroupHeaderHeight : 0);

    /// <summary>Owner-drawn variable rows keep their first measured height; re-adding the items measures them again.</summary>
    private void RefreshCategoryHeights()
    {
        object? selected = _categories.SelectedItem;
        object[] items = [.. _categories.Items.Cast<object>()];
        _categories.BeginUpdate();
        _categories.Items.Clear();
        _categories.Items.AddRange(items);
        if (selected is not null) _categories.SelectedItem = selected;
        _categories.EndUpdate();
    }

    private void DrawCategory(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        ThemePalette palette = ThemeService.Palette;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush clear = new(palette.Surface)) g.FillRectangle(clear, e.Bounds);
        string name = _categories.Items[e.Index]?.ToString() ?? string.Empty;
        var info = Categories.FirstOrDefault(category => category.Name == name);
        Rectangle row = e.Bounds;
        if (StartsGroup(e.Index))
        {
            int header = GroupHeaderHeight;
            using Font small = new(ThemeService.InterfaceFont.FontFamily, Math.Max(7f, ThemeService.InterfaceFont.Size - 1.5f), FontStyle.Bold);
            TextRenderer.DrawText(g, info.Group ?? string.Empty, small, new Rectangle(row.Left + DpiLayout.Scale(this, 10), row.Top, row.Width, header),
                palette.TextMuted, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            row = new Rectangle(row.Left, row.Top + header, row.Width, row.Height - header);
        }
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Rectangle pill = Rectangle.Inflate(row, -2, -2);
        if (selected)
        {
            using GraphicsPath path = UiDrawing.RoundedRectangle(pill, 9);
            using SolidBrush fill = new(UiDrawing.Blend(palette.Accent, palette.Surface, 0.78f));
            g.FillPath(fill, path);
            int bar = Math.Max(8, pill.Height / 2);
            using SolidBrush accent = new(palette.Accent);
            g.FillRectangle(accent, new Rectangle(pill.X + 4, pill.Y + (pill.Height - bar) / 2, 3, bar));
        }
        int glyphSize = Math.Max(20, pill.Height - DpiLayout.Scale(this, 12));
        Rectangle glyph = new(pill.X + DpiLayout.Scale(this, 14), pill.Y + (pill.Height - glyphSize) / 2, glyphSize, glyphSize);
        using (GraphicsPath tile = UiDrawing.RoundedRectangle(glyph, 6))
        using (SolidBrush tileBrush = new(selected ? palette.Accent : palette.SurfaceRaised))
            g.FillPath(tileBrush, tile);
        using (Font symbol = new("Segoe UI Symbol", Math.Max(7f, ThemeService.InterfaceFont.Size - 0.5f)))
            TextRenderer.DrawText(g, info.Glyph ?? "•", symbol, glyph, selected ? Color.White : palette.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using Font text = selected ? new Font(ThemeService.InterfaceFont, FontStyle.Bold) : new Font(ThemeService.InterfaceFont, FontStyle.Regular);
        int textLeft = glyph.Right + DpiLayout.Scale(this, 10);
        TextRenderer.DrawText(g, info.Title ?? name, text, new Rectangle(textLeft, pill.Y, pill.Right - textLeft - 4, pill.Height),
            selected ? palette.Text : palette.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void ShowSelectedPage()
    {
        string? selected = _categories.SelectedItem?.ToString();
        foreach ((string name, Control page) in _pages)
        {
            page.Visible = string.Equals(name, selected, StringComparison.Ordinal);
            if (!page.Visible) continue;
            page.BringToFront();
            if (page is PreferencePage preferencePage) preferencePage.Arrange();
        }
        if (string.Equals(selected, "Project", StringComparison.Ordinal)) RefreshSystemRequirementsEstimate();
    }

    private void ArrangeCurrentPage()
    {
        foreach (PreferencePage page in _pages.Values.OfType<PreferencePage>().Where(page => page.Visible)) page.Arrange();
    }

    /// <summary>Shows only the pages and rows that match the search; the page's own name matching shows all of it.</summary>
    private void ApplySearch()
    {
        string text = _categoryFilter.Text.Trim();
        string? selected = _categories.SelectedItem?.ToString();
        List<string> matching = [];
        foreach (string name in _allCategories)
        {
            if (!_pages.TryGetValue(name, out Control? control) || control is not PreferencePage page) continue;
            var info = Categories.First(category => category.Name == name);
            bool nameMatches = text.Length == 0 || info.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
            bool any = page.ApplyFilter(nameMatches ? string.Empty : text);
            if (nameMatches || any) matching.Add(name);
        }
        _categories.BeginUpdate();
        _categories.Items.Clear();
        _categories.Items.AddRange([.. matching]);
        if (selected is not null && matching.Contains(selected)) _categories.SelectedItem = selected;
        else if (matching.Count > 0) _categories.SelectedIndex = 0;
        _categories.EndUpdate();
        ArrangeCurrentPage();
    }

    /// <summary>Headless seam: switch the visible page by name (old names such as "Rendering" still work).</summary>
    internal bool SelectCategory(string category)
    {
        if (CategoryAliases.TryGetValue(category, out string? current)) category = current;
        for (int index = 0; index < _categories.Items.Count; index++)
        {
            if (string.Equals(_categories.Items[index]?.ToString(), category, StringComparison.Ordinal))
            {
                _categories.SelectedIndex = index;
                return true;
            }
        }
        return false;
    }
}

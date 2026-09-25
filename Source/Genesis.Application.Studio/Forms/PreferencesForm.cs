using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Core;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Studio.Forms;

public sealed class PreferencesForm : DpiAwareForm
{
    private readonly SettingsService _settings;
    private readonly Panel _contentHost;
    private readonly ListBox _categories;
    private readonly TextBox _categoryFilter = new() { Name = "PreferencesCategoryFilter" };
    private readonly string[] _allCategories;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Panel _themePreview = new();
    private readonly RadioButton _colourThemeMode = new() { Name = "ColourThemeMode" };
    private readonly RadioButton _imageThemeMode = new() { Name = "ImageThemeMode" };
    private readonly ThemedComboBox _colourTheme = new() { Name = "ColourThemePicker" };
    private readonly ThemeImageGallery _imageThemeGallery = new() { Name = "ImageThemeGallery" };
    private readonly ModernButton _addThemeImage = new();
    private Control? _colourThemeField;
    private Control? _imageThemeField;
    private bool _loadingAppearance;
    private string _loadedThemeName = "Dark";

    private readonly CheckBox _showSplash = new();
    private readonly CheckBox _reopenLast = new();
    private readonly CheckBox _confirmDelete = new();
    private readonly CheckBox _externalChanges = new();
    private readonly ThemedComboBox _density = new();
    private readonly NumericUpDown _scale = new();
    private readonly NumericUpDown _codeFontSize = new();
    private readonly KitToggle _animations = new() { Name = "AnimationsToggle" };
    private readonly CheckBox _autoSave = new();
    private readonly NumericUpDown _autoSaveMinutes = new();
    private readonly CheckBox _backups = new();
    private readonly NumericUpDown _backupDays = new();
    private readonly ComboBox _buildConfiguration = new();
    private readonly ComboBox _architecture = new();
    private readonly CheckBox _previewVsync = new();
    private readonly CheckBox _pauseUnfocused = new();
    private readonly CheckBox _runtimeDiagnostics = new();
    private readonly ComboBox _renderBackend = new();
    private readonly ComboBox _faceCulling = new() { Name = "FaceCullingPicker" };
    private readonly ComboBox _frontFaceWinding = new() { Name = "FrontFaceWindingPicker" };
    private readonly CheckBox _lightingEnabled = new() { Name = "LightingEnabledPicker" };
    private readonly CheckBox _shadowsEnabled = new() { Name = "ShadowsEnabledPicker" };
    private readonly NumericUpDown _shadowStrength = new() { Name = "ShadowStrengthPicker" };
    private readonly NumericUpDown _shadowCascadeCount = new() { Name = "ShadowCascadeCountPicker" };
    private readonly CheckBox _gtaoEnabled = new() { Name = "GtaoEnabledPicker" };
    private readonly CheckBox _contactShadowsEnabled = new() { Name = "ContactShadowsEnabledPicker" };
    private readonly CheckBox _localVolumetricsEnabled = new() { Name = "LocalVolumetricsEnabledPicker" };
    private readonly CheckBox _smokeExtinctionEnabled = new() { Name = "SmokeExtinctionEnabledPicker" };
    private readonly CheckBox _bloomEnabled = new() { Name = "BloomEnabledPicker" };
    private readonly CheckBox _waterReflections = new() { Name = "WaterReflectionsPicker" };
    private readonly CheckBox _atmosphereLutEnabled = new() { Name = "AtmosphereLutEnabledPicker" };
    private readonly CheckBox _raymarchedCloudsEnabled = new() { Name = "RaymarchedCloudsEnabledPicker" };
    private readonly CheckBox _cloudTemporalEnabled = new() { Name = "CloudTemporalEnabledPicker" };
    private readonly CheckBox _celestialExtrasEnabled = new() { Name = "CelestialExtrasEnabledPicker" };
    private readonly ComboBox _cloudQuality = new() { Name = "CloudQualityPicker" };
    private readonly NumericUpDown _exposure = new() { Name = "ExposurePicker" };
    private readonly NumericUpDown _contrast = new() { Name = "ContrastPicker" };
    private readonly NumericUpDown _saturation = new() { Name = "SaturationPicker" };
    private readonly NumericUpDown _vignetteStrength = new() { Name = "VignetteStrengthPicker" };
    private readonly NumericUpDown _bloomThreshold = new() { Name = "BloomThresholdPicker" };
    private readonly NumericUpDown _bloomIntensity = new() { Name = "BloomIntensityPicker" };
    private readonly NumericUpDown _spriteInstanceCap = new() { Name = "SpriteInstanceCapPicker" };
    private readonly NumericUpDown _meshInstanceCap = new() { Name = "MeshInstanceCapPicker" };
    private readonly NumericUpDown _sceneLocalLightCap = new() { Name = "SceneLocalLightCapPicker" };
    private readonly NumericUpDown _omniShadowBudget = new() { Name = "OmniShadowBudgetPicker" };
    private readonly NumericUpDown _localVolumetricLightBudget =
        new() { Name = "LocalVolumetricLightBudgetPicker" };
    private readonly ComboBox _drawCallMode = new() { Name = "DrawCallModePicker" };
    private readonly NumericUpDown _worldDrawBudget = new() { Name = "WorldDrawBudgetPicker" };
    private readonly CheckBox _fogEnabled = new();
    private readonly TextBox _fogColor = new();
    private readonly Panel _fogColorSwatch = new();
    private readonly NumericUpDown _fogDepth = new();
    private readonly NumericUpDown _fogThickness = new();
    private readonly NumericUpDown _fogAlpha = new();
    private readonly CheckBox _allowEscapeToClose = new();

    private readonly Genesis.Application.Studio.Controls.AnimatedIconPlayer _projectIconPreview = new() { Size = new Size(128, 128), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 44, 52), Cursor = Cursors.Hand };
    private readonly NumericUpDown _projectIconFps = new();
    private string _projectIconPath = string.Empty;
    private readonly Label _systemRequirementsSummary = new()
    {
        Name = "SystemRequirementsSummary",
        AutoSize = false,
        Size = new Size(620, 110),
    };

    private readonly ProjectSession? _project;

    /// <param name="project">
    /// The open project, whose own settings get a page. Null when Preferences is opened without
    /// one (the Project Hub, the published smoke test), in which case that page is not offered ?"
    public PreferencesForm(SettingsService settings, ProjectSession? project = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _project = project;
        SuiteChromeBridge.Push();
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 650);
        MinimumSize = new Size(840, 580);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        SizeGripStyle = SizeGripStyle.Show;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Genesis Studio — Preferences";
        BackColor = ThemeService.Palette.Canvas;

        _allCategories = _project is null
            ? ["General", "Appearance", "Editing", "Runtime", "Rendering", "Shortcuts"]
            : ["General", "Appearance", "Editing", "Runtime", "Rendering", "Project", "Shortcuts"];

        TableLayoutPanel layout = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 3,
            Tag = "canvas",
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(layout);

        Panel heading = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 20, 28, 12),
            Tag = "canvas",
        };
        Label title = new()
        {
            AutoSize = true,
            Font = ThemeService.HeadingFont,
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(28, 18),
            Text = "Preferences",
        };
        heading.Controls.Add(title);
        Label subtitle = new()
        {
            AutoSize = true,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(30, 51),
            Text = "Central settings for the Studio, editors, runtime preview, and shortcuts.",
        };
        heading.Controls.Add(subtitle);
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        Panel navigation = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 12),
            Tag = "surface",
        };
        Panel filterHost = new()
        {
            Dock = DockStyle.Top,
            Height = DpiLayout.Scale(this, 40),
            Padding = new Padding(8, 8, 8, 8),
            Tag = "surface",
        };
        _categoryFilter.BackColor = ThemeService.Palette.SurfaceRaised;
        _categoryFilter.BorderStyle = BorderStyle.None;
        _categoryFilter.Dock = DockStyle.Fill;
        _categoryFilter.ForeColor = ThemeService.Palette.Text;
        _categoryFilter.PlaceholderText = "Filter settings…";
        _categoryFilter.TextChanged += (_, _) => FilterCategories();
        filterHost.Controls.Add(_categoryFilter);
        filterHost.Paint += (_, e) =>
        {
            Rectangle box = new(0, 4, filterHost.Width - 1, filterHost.Height - 9);
            using Pen pen = new(ThemeService.Palette.Border);
            e.Graphics.DrawRectangle(pen, box);
        };
        navigation.Controls.Add(filterHost);

        _categories = new ListBox
        {
            BackColor = ThemeService.Palette.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            Font = ThemeService.InterfaceFont,
            ForeColor = ThemeService.Palette.Text,
            ItemHeight = DpiLayout.Scale(this, 40),
            IntegralHeight = false,
        };
        _categories.Items.AddRange(_allCategories);
        _categories.DrawItem += DrawCategory;
        _categories.SelectedIndexChanged += (_, _) => ShowSelectedPage();
        navigation.Controls.Add(_categories);
        filterHost.BringToFront();
        layout.Controls.Add(navigation, 0, 1);

        Panel footer = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            Tag = "surface",
        };
        ModernButton reset = new()
        {
            Dock = DockStyle.Left,
            Size = new Size(142, 38),
            Text = "Restore defaults",
        };
        reset.Click += (_, _) => RestoreDefaults();
        footer.Controls.Add(reset);

        ModernButton cancel = new()
        {
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Right,
            Margin = new Padding(0, 0, 10, 0),
            Size = new Size(104, 38),
            Text = "Cancel",
        };
        footer.Controls.Add(cancel);

        ModernButton ok = new()
        {
            Accent = true,
            Dock = DockStyle.Right,
            Margin = new Padding(0, 0, 10, 0),
            Size = new Size(104, 38),
            Text = "OK",
        };
        ok.Click += (_, _) =>
        {
            if (TrySaveAndApply())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        footer.Controls.Add(ok);

        ModernButton apply = new()
        {
            Dock = DockStyle.Right,
            Margin = new Padding(0, 0, 10, 0),
            Name = "ApplyPreferences",
            Size = new Size(104, 38),
            Text = "Apply",
        };
        apply.Click += (_, _) => TrySaveAndApply();
        footer.Controls.Add(apply);
        layout.Controls.Add(footer, 0, 2);
        layout.SetColumnSpan(footer, 2);

        _contentHost = new Panel
        {
            AutoScroll = true,
            BackColor = ThemeService.Palette.Canvas,
            Dock = DockStyle.Fill,
            Padding = new Padding(36, 24, 36, 24),
            Tag = "canvas",
        };
        layout.Controls.Add(_contentHost, 1, 1);

        BuildPages();
        LoadValues(_settings.Current);
        _categories.SelectedIndex = 0;
        ThemeService.Apply(this);
    }

    private void BuildPages()
    {
        _pages["General"] = BuildGeneralPage();
        _pages["Appearance"] = BuildAppearancePage();
        _pages["Editing"] = BuildEditingPage();
        _pages["Runtime"] = BuildRuntimePage();
        _pages["Rendering"] = BuildRenderingPage();
        if (_project is not null) _pages["Project"] = BuildProjectPage();
        _pages["Shortcuts"] = BuildShortcutsPage();
        foreach (Control page in _pages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _contentHost.Controls.Add(page);
        }
    }

    private Control BuildGeneralPage()
    {
        FlowLayoutPanel page = CreatePage("General", "Startup and project safety.");
        ConfigureCheckBox(_showSplash, "Show launch splash");
        ConfigureCheckBox(_reopenLast, "Reopen the last project at startup");
        ConfigureCheckBox(_confirmDelete, "Confirm destructive actions");
        ConfigureCheckBox(_externalChanges, "Watch projects for external file changes");
        page.Controls.Add(_showSplash);
        page.Controls.Add(_reopenLast);
        page.Controls.Add(_confirmDelete);
        page.Controls.Add(_externalChanges);
        return page;
    }

    private Control BuildAppearancePage()
    {
        FlowLayoutPanel page = CreatePage(
            "Appearance",
            "Choose flat interface colours or a picture-backed workspace. Image themes derive a "
            + "readable palette from the selected picture.");

        ConfigureThemeMode(_colourThemeMode, "Colour theme", new Point(0, 0));
        ConfigureThemeMode(_imageThemeMode, "Image theme", new Point(190, 0));
        _colourThemeMode.CheckedChanged += (_, _) => ThemeModeChanged();
        _imageThemeMode.CheckedChanged += (_, _) => ThemeModeChanged();
        page.Controls.Add(BuildThemeModePicker());

        ConfigureCombo(
            _colourTheme,
            [.. ThemeCatalog.ColourNames.Select(ThemeCatalog.ColourDisplayName)],
            260);
        _colourTheme.SelectedIndexChanged += (_, _) => RefreshThemePreview();
        _colourThemeField = Field("Colour theme", _colourTheme);
        page.Controls.Add(_colourThemeField);

        _imageThemeGallery.Size = new Size(560, 138);
        _imageThemeGallery.SelectedThemeChanged += (_, _) => RefreshThemePreview();
        _imageThemeGallery.SetImages(ThemeCatalog.Images);
        _imageThemeField = BuildImageThemeField();
        page.Controls.Add(_imageThemeField);

        _addThemeImage.Margin = new Padding(0, 0, 0, 16);
        _addThemeImage.Size = new Size(190, 36);
        _addThemeImage.Text = "Add theme image…";
        _addThemeImage.Click += (_, _) => AddThemeImage();
        page.Controls.Add(_addThemeImage);
        ConfigureCombo(_density, ["Compact", "Comfortable", "Spacious"], 260);
        ConfigureNumeric(_scale, 75, 200, 5, 260);
        ConfigureNumeric(_codeFontSize, 8, 24, 1, 260);

        // Tall enough to actually show the picture. A row of swatches alone cannot tell you whether
        // an image works as a backdrop, which is the only question this preview exists to answer.
        _themePreview.Size = new Size(560, 220);
        _themePreview.Margin = new Padding(0, 0, 0, 10);
        _themePreview.Paint += PaintThemePreview;
        page.Controls.Add(_themePreview);

        page.Controls.Add(Field("Interface density", _density));
        page.Controls.Add(Field("Interface scale (%)", _scale));
        page.Controls.Add(Field("Code font size", _codeFontSize));
        page.Controls.Add(BuildToggleRow("Use subtle interface animations", _animations));
        RefreshThemePreview();
        return page;
    }

    private Control BuildThemeModePicker()
    {
        Panel picker = new()
        {
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12),
            Size = new Size(560, 34),
            Tag = "transparent",
        };
        picker.Controls.Add(_colourThemeMode);
        picker.Controls.Add(_imageThemeMode);
        return picker;
    }

    private Control BuildImageThemeField()
    {
        Panel field = new()
        {
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12),
            Size = new Size(620, 168),
            Tag = "transparent",
        };
        Label label = new()
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.Text,
            Location = Point.Empty,
            Size = new Size(420, 25),
            Text = "Theme images",
        };
        _imageThemeGallery.Location = new Point(0, 27);
        _imageThemeGallery.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        field.Resize += (_, _) =>
        {
            label.Width = Math.Max(1, field.ClientSize.Width);
            _imageThemeGallery.Width = Math.Max(1, field.ClientSize.Width);
        };
        field.Controls.Add(label);
        field.Controls.Add(_imageThemeGallery);
        return field;
    }

    private static void ConfigureThemeMode(RadioButton mode, string text, Point location)
    {
        mode.AutoSize = false;
        mode.BackColor = Color.Transparent;
        mode.ForeColor = ThemeService.Palette.Text;
        mode.Location = location;
        mode.Size = new Size(170, 30);
        mode.Text = text;
    }

    private Control BuildEditingPage()
    {
        FlowLayoutPanel page = CreatePage(
            "Editing",
            "Autosave and backups. Grid and snapping are per-editor: each surface keeps its own, "
            + "because a room grid in world units and an image grid in pixels are not the same setting.");
        ConfigureCheckBox(_autoSave, "Autosave modified assets");
        ConfigureNumeric(_autoSaveMinutes, 1, 60, 1, 260);
        ConfigureCheckBox(_backups, "Create project backups before destructive migrations");
        ConfigureNumeric(_backupDays, 1, 365, 1, 260);
        page.Controls.Add(_autoSave);
        page.Controls.Add(Field("Autosave interval (minutes)", _autoSaveMinutes));
        page.Controls.Add(_backups);
        page.Controls.Add(Field("Backup retention (days)", _backupDays));
        return page;
    }

    private Control BuildRuntimePage()
    {
        FlowLayoutPanel page = CreatePage("Runtime", "Preview and standalone player defaults.");
        ConfigureCombo(_buildConfiguration, ["Debug", "Release"], 260);
        ConfigureCombo(_architecture, ["win-x64"], 260);
        ConfigureCheckBox(_previewVsync, "Use VSync in editor previews");
        ConfigureCheckBox(_pauseUnfocused, "Pause preview when Studio loses focus");
        ConfigureCheckBox(_runtimeDiagnostics, "Collect runtime diagnostics for Profiler");
        page.Controls.Add(Field("Build configuration", _buildConfiguration));
        page.Controls.Add(Field("Player architecture", _architecture));
        page.Controls.Add(_previewVsync);
        page.Controls.Add(_pauseUnfocused);
        page.Controls.Add(_runtimeDiagnostics);
        return page;
    }

    private Control BuildRenderingPage()
    {
        FlowLayoutPanel page = CreatePage(
            "Rendering",
            "How this installation drives the GPU. Engine fog belongs to the game, so it lives on "
            + "the Project page and is saved with the project.");

        // Built from the catalogue rather than typed out, so a backend's label cannot claim
        // something its descriptor contradicts — this list said DX12 "uses DX11 fallback until
        // available" for a while after DX12 started rendering.
        string[] backends =
        [
            .. RenderBackendCatalog.All.Select(backend => backend.IsImplemented
                ? backend.DisplayName
                : $"{backend.DisplayName} (not available yet — falls back to Direct3D 11)"),
        ];

        ConfigureCombo(_renderBackend, backends, 420);
        page.Controls.Add(Field("Rendering backend", _renderBackend));

        ConfigureCombo(_faceCulling, ["Back", "Front", "None"], 420);
        ConfigureCombo(_frontFaceWinding, ["Clockwise", "CounterClockwise"], 420);
        page.Controls.Add(Field("Cull faces", _faceCulling));
        page.Controls.Add(Field("Front-face winding", _frontFaceWinding));
        page.Controls.Add(Note(
            "These are the installation defaults. Renderable resources and placed terrain objects "
            + "use Default unless their Inspector explicitly overrides culling or winding."));

        ConfigureCheckBox(_lightingEnabled, "Enable lighting globally");
        ConfigureCheckBox(_shadowsEnabled, "Enable shadows globally");
        ConfigureNumeric(_shadowStrength, 0, 100, 1, 260);
        _shadowStrength.DecimalPlaces = 0;
        ConfigureNumeric(_shadowCascadeCount, 2, 3, 1, 120);
        _shadowCascadeCount.DecimalPlaces = 0;
        _lightingEnabled.CheckedChanged += (_, _) => RefreshLightingPreferenceState();
        _shadowsEnabled.CheckedChanged += (_, _) => RefreshLightingPreferenceState();
        _bloomEnabled.CheckedChanged += (_, _) => RefreshLightingPreferenceState();
        page.Controls.Add(_lightingEnabled);
        page.Controls.Add(_shadowsEnabled);
        page.Controls.Add(Field("Shadow strength (%)", _shadowStrength));
        page.Controls.Add(Field("Shadow cascades (2 or 3)", _shadowCascadeCount));
        ConfigureCheckBox(_gtaoEnabled, "AO — GTAO (half-res ambient occlusion)");
        page.Controls.Add(_gtaoEnabled);
        ConfigureCheckBox(_contactShadowsEnabled, "CS — Contact shadows (screen-space)");
        page.Controls.Add(_contactShadowsEnabled);
        ConfigureCheckBox(_localVolumetricsEnabled, "LV — Local volumetric scatter");
        page.Controls.Add(_localVolumetricsEnabled);
        ConfigureCheckBox(_smokeExtinctionEnabled, "SE — Smoke extinction");
        page.Controls.Add(_smokeExtinctionEnabled);
        ConfigureCheckBox(_bloomEnabled, "BL — HDR bloom");
        page.Controls.Add(_bloomEnabled);
        ConfigureCheckBox(_waterReflections, "Water reflections (one plane, half resolution)");
        page.Controls.Add(_waterReflections);
        ConfigureCheckBox(_atmosphereLutEnabled, "AL — Atmosphere LUT (sky-view)");
        page.Controls.Add(_atmosphereLutEnabled);
        ConfigureCheckBox(_raymarchedCloudsEnabled, "CL — Raymarched clouds");
        page.Controls.Add(_raymarchedCloudsEnabled);
        ConfigureCheckBox(_cloudTemporalEnabled, "CT — Cloud temporal");
        page.Controls.Add(_cloudTemporalEnabled);
        ConfigureCheckBox(_celestialExtrasEnabled, "CE — Celestial extras (stars / Milky Way / moon)");
        page.Controls.Add(_celestialExtrasEnabled);
        ConfigureCombo(_cloudQuality, ["Performance", "Balanced", "High", "Cinematic"], 260);
        page.Controls.Add(Field("Cloud quality", _cloudQuality));
        ConfigureNumeric(_exposure, 0.05m, 8m, 0.05m, 160);
        _exposure.DecimalPlaces = 2;
        ConfigureNumeric(_contrast, 0.05m, 4m, 0.05m, 160);
        _contrast.DecimalPlaces = 2;
        ConfigureNumeric(_saturation, 0m, 4m, 0.05m, 160);
        _saturation.DecimalPlaces = 2;
        ConfigureNumeric(_vignetteStrength, 0m, 1m, 0.05m, 160);
        _vignetteStrength.DecimalPlaces = 2;
        ConfigureNumeric(_bloomThreshold, 0m, 16m, 0.1m, 160);
        _bloomThreshold.DecimalPlaces = 2;
        ConfigureNumeric(_bloomIntensity, 0m, 2m, 0.01m, 160);
        _bloomIntensity.DecimalPlaces = 2;
        page.Controls.Add(Field("Exposure", _exposure));
        page.Controls.Add(Field("Contrast", _contrast));
        page.Controls.Add(Field("Saturation", _saturation));
        page.Controls.Add(Field("Vignette", _vignetteStrength));
        page.Controls.Add(Field("Bloom threshold", _bloomThreshold));
        page.Controls.Add(Field("Bloom intensity", _bloomIntensity));
        page.Controls.Add(Note(
            "Lighting is the master switch for sunlight, ambient light, and Light Emitters. "
            + "Shadows can be disabled independently; strength controls their global darkness. "
            + "Three cascades add a far envelope with texel-snapped centres (AF1.1). "
            + "GTAO is optional and off by default (AF1.2); skipped on the Software backend. "
            + "Contact shadows are short-range and sun-gated (AF1.4), so they tighten contacts "
            + "the cascades miss without darkening the same creases GTAO already does. "
            + "Local volumetric scatter adds bounded local-light beams and glow (AF1.5) on top of the "
            + "existing height fog and sun shafts, for the strongest few local lights only. "
            + "Smoke extinction darkens what is seen through alpha-blended particle plumes "
            + "(AF1.6) as one Beer-Lambert term in the fog composite — never additive fire. "
            + "HDR bloom (AF1.7) is off by default; exposure/grade/vignette default to identity "
            + "so untouched goldens stay bit-stable. ACES remains the sole tonemap. "
            + "Atmosphere LUT (AF2.1) replaces flat sky colour on far pixels with a baked "
            + "transmittance / multi-scatter / sky-view atlas; off by default; Software keeps "
            + "analytic clear colour + fog. "
            + "Raymarched clouds (AF2.3) march every frame when on — never skip 3-in-4. "
            + "Cloud temporal (AF2.4) is off by default; quality only changes internal resolution "
            + "(High = half-res). "
            + "Celestial extras (AF2.5) add sidereal stars, Milky Way, and a phase-aware moon in "
            + "FogPost only — default off, Software skip, never on the cloud budget."));

        ConfigureNumeric(
            _spriteInstanceCap,
            RenderCapacityDefaults.MinInstanceCap,
            RenderCapacityDefaults.HardwareSpriteInstanceCap,
            256,
            260);
        ConfigureNumeric(
            _meshInstanceCap,
            RenderCapacityDefaults.MinInstanceCap,
            RenderCapacityDefaults.HardwareMeshInstanceCap,
            256,
            260);
        ConfigureNumeric(
            _sceneLocalLightCap,
            RenderCapacityDefaults.MinSceneLocalLightCap,
            RenderCapacityDefaults.MaxSceneLocalLightCap,
            8,
            260);
        ConfigureNumeric(
            _omniShadowBudget,
            RenderCapacityDefaults.MinOmniShadowBudget,
            RenderCapacityDefaults.MaxOmniShadowBudget,
            1,
            120);
        ConfigureNumeric(
            _localVolumetricLightBudget,
            RenderCapacityDefaults.MinLocalVolumetricLightBudget,
            RenderCapacityDefaults.MaxLocalVolumetricLightBudget,
            1,
            120);
        ConfigureCombo(_drawCallMode, [RenderCapacityDefaults.DrawCallModeAuto, RenderCapacityDefaults.DrawCallModeManual], 420);
        ConfigureNumeric(
            _worldDrawBudget,
            RenderCapacityDefaults.MinWorldDrawBudget,
            RenderCapacityDefaults.MaxWorldDrawBudget,
            1,
            260);
        _drawCallMode.SelectedIndexChanged += (_, _) => RefreshDrawBudgetPreferenceState();
        page.Controls.Add(Field("2D sprite instance cap", _spriteInstanceCap));
        page.Controls.Add(Field("3D mesh instance cap", _meshInstanceCap));
        page.Controls.Add(Field("Scene local light cap", _sceneLocalLightCap));
        page.Controls.Add(Field("Omni-shadow budget (0–4)", _omniShadowBudget));
        page.Controls.Add(
            Field("Local volumetric light budget (0–4)", _localVolumetricLightBudget));
        page.Controls.Add(Field("Draw calls", _drawCallMode));
        page.Controls.Add(Field("World draw budget", _worldDrawBudget));
        page.Controls.Add(Note(
            "Instance caps name GPU buffer uploads, not ECS objects or textures. Local lights fill "
            + "a clustered buffer up to the scene cap; each screen tile evaluates at most 32 "
            + "(R7.5). Omni-shadow budget is a bounded number of cubemap slots (AF1.3) — "
            + "independent of the scene local light cap, never a map per torch. Manual draw "
            + "calls budget variable world batches only — shadows, post, HUD and the environment "
            + "floor/sun stay outside that budget."));

        ModernButton testBackendsBtn = new()
        {
            Text = "Test Backends…",
            Size = new Size(160, 36),
            Margin = new Padding(0, 4, 0, 12),
        };
        testBackendsBtn.Click += (_, _) =>
        {
            RenderBackendOption backend = BackendAt(_renderBackend.SelectedIndex).Backend;
            using PgslCommandReferenceForm commands = new();
            commands.PrepareBackendVisualTest(backend);
            commands.Shown += async (_, _) => await commands.RunVisualTestAsync();
            commands.ShowDialog(this);
        };
        page.Controls.Add(testBackendsBtn);

        page.Controls.Add(Note(
            "The selected backend renders the editor viewports and the game alike. Test Backends "
            + "opens the live 3D Visual Test on that backend (Help → Commands). A machine that "
            + "cannot create the requested device falls back to Direct3D 11 with the reason written "
            + "to the log."));
        return page;
    }

    /// <summary>
    /// Settings stored in the project file rather than in this machine's preferences.
    /// </summary>
    /// <remarks>
    /// Shown only when a project is open, because there is nothing to write them to otherwise. The
    /// test for what belongs here is whether you would expect the setting to follow the project to
    /// another machine — fog and "can the player press Escape to quit" both describe the game.
    /// </remarks>
    private Control BuildProjectPage()
    {
        FlowLayoutPanel page = CreatePage(
            "Project",
            _project is null
                ? "Open a project to edit its settings."
                : $"Saved with '{_project.Manifest.Name}' and shared with anyone who opens it. "
                  + "In 2D, authored Z/layer order is the depth axis; in 3D, depth is camera distance.");

        ConfigureCheckBox(_allowEscapeToClose, "Allow ESC to close the game");
        ConfigureCheckBox(_fogEnabled, "Enable engine-default fog");

        _fogColor.Size = new Size(120, 32);
        _fogColor.MaxLength = 7;
        _fogColor.PlaceholderText = "#RRGGBB";
        _fogColor.TextChanged += (_, _) => SyncFogSwatch();

        ConfigureNumeric(_fogDepth, -10240, 10240, 1, 260);
        _fogDepth.DecimalPlaces = 1;
        ConfigureNumeric(_fogThickness, 0.1m, 20480, 1, 260);
        _fogThickness.DecimalPlaces = 1;
        ConfigureNumeric(_fogAlpha, 0, 100, 1, 260);
        _fogAlpha.DecimalPlaces = 0;

        page.Controls.Add(_allowEscapeToClose);
        page.Controls.Add(_fogEnabled);
        page.Controls.Add(Field("Fog colour", BuildFogColourPicker()));
        page.Controls.Add(Field("Fog depth / start (3D distance; 2D Z/layer depth)", _fogDepth));
        page.Controls.Add(Field("Fog thickness", _fogThickness));
        page.Controls.Add(Field("Fog alpha (%)", _fogAlpha));

        ConfigureNumeric(_projectIconFps, 0, 60, 1, 80);
        _projectIconFps.DecimalPlaces = 0;
        _projectIconFps.ValueChanged += (_, _) => 
        {
            if (!string.IsNullOrEmpty(_projectIconPath))
                _projectIconPreview.LoadIcon(_projectIconPath, (int)_projectIconFps.Value);
        };

        var pickIconBtn = new ModernButton { Text = "Set Icon..." };
        pickIconBtn.Click += (_, _) =>
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Import from disk...", null, (_, _) => PickProjectIconFromDisk());
            menu.Items.Add("Import from project resources...", null, (_, _) => PickProjectIconFromProject());
            menu.Items.Add("Clear", null, (_, _) => { _projectIconPath = string.Empty; _projectIconPreview.LoadIcon("", 0); });
            menu.Show(pickIconBtn, new Point(0, pickIconBtn.Height));
        };

        var iconPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        iconPanel.Controls.Add(_projectIconPreview);
        var iconControls = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        iconControls.Controls.Add(pickIconBtn);
        iconControls.Controls.Add(Field("Playback Speed (FPS)", _projectIconFps));
        iconPanel.Controls.Add(iconControls);

        page.Controls.Add(Field("Project Icon", iconPanel));

        // R7.8: canned example-scene estimate from authored caps — not a bench of this PC.
        Label requirementsHeading = new()
        {
            AutoSize = false,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 10f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Margin = new Padding(0, 12, 0, 4),
            Size = new Size(620, 24),
            Text = "System requirements",
        };
        Label requirementsHint = new()
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.TextMuted,
            Margin = new Padding(0, 0, 0, 8),
            Size = new Size(620, 36),
            Text = "Estimate for a canned outdoor showcase with this project's backend, caps, "
                + "shadows and texture groups. Not a benchmark of this PC.",
        };
        _systemRequirementsSummary.ForeColor = ThemeService.Palette.Text;
        _systemRequirementsSummary.BackColor = Color.Transparent;
        ModernButton refreshEstimate = new()
        {
            Name = "SystemRequirementsRefresh",
            Text = "Refresh estimate",
            Margin = new Padding(0, 0, 0, 8),
        };
        refreshEstimate.Click += (_, _) => RefreshSystemRequirementsEstimate();
        page.Controls.Add(requirementsHeading);
        page.Controls.Add(requirementsHint);
        page.Controls.Add(refreshEstimate);
        page.Controls.Add(_systemRequirementsSummary);
        RefreshSystemRequirementsEstimate();

        return page;
    }

    private void RefreshSystemRequirementsEstimate()
    {
        if (_project is null)
        {
            return;
        }

        // Prefer live UI values so changing Rendering caps updates Project before Apply.
        RenderingSettings live = new()
        {
            Backend = BackendAt(_renderBackend.SelectedIndex).SettingsValue,
            LightingEnabled = _lightingEnabled.Checked,
            ShadowsEnabled = _shadowsEnabled.Checked,
            ShadowStrength = (float)(_shadowStrength.Value / 100m),
            ShadowCascadeCount = (int)_shadowCascadeCount.Value,
            GtaoEnabled = _gtaoEnabled.Checked,
            ContactShadowsEnabled = _contactShadowsEnabled.Checked,
            LocalVolumetricsEnabled = _localVolumetricsEnabled.Checked,
            SmokeExtinctionEnabled = _smokeExtinctionEnabled.Checked,
            BloomEnabled = _bloomEnabled.Checked,
            WaterReflections = _waterReflections.Checked,
            AtmosphereLutEnabled = _atmosphereLutEnabled.Checked,
            RaymarchedCloudsEnabled = _raymarchedCloudsEnabled.Checked,
            CloudTemporalEnabled = _cloudTemporalEnabled.Checked,
            CelestialExtrasEnabled = _celestialExtrasEnabled.Checked,
            CloudQuality = CloudQualityIndex(_cloudQuality.Text),
            Exposure = (float)_exposure.Value,
            Contrast = (float)_contrast.Value,
            Saturation = (float)_saturation.Value,
            VignetteStrength = (float)_vignetteStrength.Value,
            BloomThreshold = (float)_bloomThreshold.Value,
            BloomIntensity = (float)_bloomIntensity.Value,
            SpriteInstanceCap = (int)_spriteInstanceCap.Value,
            MeshInstanceCap = (int)_meshInstanceCap.Value,
            SceneLocalLightCap = (int)_sceneLocalLightCap.Value,
            OmniShadowBudget = (int)_omniShadowBudget.Value,
            LocalVolumetricLightBudget = (int)_localVolumetricLightBudget.Value,
            DrawCallMode = string.IsNullOrWhiteSpace(_drawCallMode.Text)
                ? RenderCapacityDefaults.DrawCallModeAuto
                : _drawCallMode.Text,
            WorldDrawBudget = (int)_worldDrawBudget.Value,
        };

        SystemRequirementsReport report = SystemRequirementsEstimator.Estimate(
            SystemRequirementsEstimator.FromPreferences(live, _project.Manifest));
        _systemRequirementsSummary.Text = report.SummaryText;
    }

    private void PickProjectIconFromDisk()
    {
        using OpenFileDialog dlg = new()
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "Select Project Icon"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _projectIconPath = dlg.FileName;
            _projectIconPreview.LoadIcon(_projectIconPath, (int)_projectIconFps.Value);
        }
    }

    private void PickProjectIconFromProject()
    {
        if (_project == null) return;
        var entry = Genesis.Application.Editors.Suite.Inspector.AssetPickerService.PickImage(_project.RootPath, this, _projectIconPath);
        if (entry != null)
        {
            _projectIconPath = entry.Reference;
            string absolutePath = ResourceNames.Resolve(_project.RootPath, _projectIconPath, ResourceType.Image);
            _projectIconPreview.LoadIcon(absolutePath, (int)_projectIconFps.Value);
        }
    }

    private Control BuildShortcutsPage()
    {
        FlowLayoutPanel page = CreatePage("Shortcuts", "One command map shared by menus and editors.");
        ListView shortcuts = new()
        {
            BackColor = ThemeService.Palette.Surface,
            BorderStyle = BorderStyle.None,
            ForeColor = ThemeService.Palette.Text,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            Size = new Size(600, 340),
            View = View.Details,
        };
        shortcuts.Columns.Add("Command", 360);
        shortcuts.Columns.Add("Shortcut", 180);
        foreach ((string command, string binding) in _settings.Current.Shortcuts.Bindings
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            shortcuts.Items.Add(new ListViewItem([command, binding]));
        }

        shortcuts.Resize += (_, _) =>
        {
            int available = Math.Max(1, shortcuts.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            shortcuts.Columns[0].Width = Math.Max(DpiLayout.Scale(shortcuts, 180), (int)(available * 0.68f));
            shortcuts.Columns[1].Width = Math.Max(1, available - shortcuts.Columns[0].Width);
        };

        page.Controls.Add(shortcuts);
        return page;
    }

    /// <summary>Position of a backend in the combo, which mirrors <see cref="RenderBackendCatalog.All"/>.</summary>
    /// <remarks>
    /// Both ends of the round-trip go through the catalog rather than literal strings or magic
    /// indices. The previous version saved by index (<c>== 1 ? "Direct3D12" : "SilkNetDx11"</c>) and
    /// loaded by matching a display label that no longer existed, so every choice silently reverted
    /// to Direct3D 11 and any backend past the second could not be persisted at all.
    /// </remarks>
    private static int BackendIndex(RenderBackendOption backend)
    {
        for (int i = 0; i < RenderBackendCatalog.All.Count; i++)
        {
            if (RenderBackendCatalog.All[i].Backend == backend)
            {
                return i;
            }
        }

        return 0;
    }

    private static RenderBackendDescriptor BackendAt(int index) =>
        RenderBackendCatalog.All[Math.Clamp(index, 0, RenderBackendCatalog.All.Count - 1)];

    /// <summary>A muted explanatory line placed under a field.</summary>
    private static Control Note(string text) => new Label
    {
        AutoSize = false,
        BackColor = Color.Transparent,
        ForeColor = ThemeService.Palette.TextMuted,
        Margin = new Padding(0, 0, 0, 12),
        Size = new Size(620, 56),
        Text = text,
    };

    private static FlowLayoutPanel CreatePage(string title, string description)
    {
        FlowLayoutPanel page = new()
        {
            AutoScroll = true,
            BackColor = ThemeService.Palette.Canvas,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(8),
            Tag = "canvas",
            WrapContents = false,
        };
        Label eyebrow = new()
        {
            AutoSize = false,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 7.5f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Margin = new Padding(0, 0, 0, 2),
            Size = new Size(620, 18),
            Text = "PREFERENCES",
        };
        page.Controls.Add(eyebrow);
        Label heading = new()
        {
            AutoSize = false,
            Font = new Font("Segoe UI Variable Display", 18f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Margin = new Padding(0, 0, 0, 4),
            Size = new Size(620, 38),
            Text = title,
        };
        page.Controls.Add(heading);
        Label detail = new()
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.TextMuted,
            Margin = new Padding(0, 0, 0, 18),
            Size = new Size(620, 42),
            Text = description,
        };
        page.Controls.Add(detail);
        page.ClientSizeChanged += (_, _) => ResizePageChildren(page);
        page.ControlAdded += (_, _) => ResizePageChildren(page);
        return page;
    }

    private static void ResizePageChildren(FlowLayoutPanel page)
    {
        if (page.ClientSize.Width <= 0)
        {
            return;
        }

        int scrollbar = page.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        int available = Math.Max(
            DpiLayout.Scale(page, 280),
            page.ClientSize.Width - page.Padding.Horizontal - scrollbar - DpiLayout.Scale(page, 4));
        foreach (Control child in page.Controls)
        {
            // Command buttons keep a deliberate compact action width; content surfaces use the
            // whole Preferences page when the dialog is resized or maximized.
            if (child is Button)
            {
                continue;
            }

            child.Width = Math.Max(1, available - child.Margin.Horizontal);
        }
    }

    /// <summary>
    /// Fog colour as a swatch you click, with the hex kept beside it.
    /// </summary>
    /// <remarks>
    /// A bare "#RRGGBB" box asks the designer to know hex, which is a poor way to choose a colour
    /// and the only way this setting could be changed. Every other colour in Genesis — image
    /// foreground, model tint, room layer, viewport outline, particle ramp — is picked from a
    /// swatch that opens the system colour dialog, so this now does the same. The hex field stays
    /// because it is the persisted form and worth being able to read, type and paste; the two are
    /// kept in step in both directions.
    /// </remarks>
    private Control BuildFogColourPicker()
    {
        Panel row = new()
        {
            BackColor = Color.Transparent,
            Size = new Size(420, 32),
            Tag = "transparent",
        };

        // Painted rather than coloured through BackColor: the theme pass assigns a palette colour to
        // every Button and Panel it walks, which would repaint the swatch grey on load and again
        // whenever the theme changes. What it draws is the setting, so nothing else may own it.
        _fogColorSwatch.Location = new Point(0, 0);
        _fogColorSwatch.Size = new Size(64, 32);
        _fogColorSwatch.Cursor = Cursors.Hand;
        _fogColorSwatch.Click += (_, _) => PickFogColour();
        _fogColorSwatch.Paint += PaintFogSwatch;

        _fogColor.Location = new Point(72, 0);

        Label hint = new()
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(200, 6),
            Size = new Size(216, 22),
            Text = "Click the swatch to choose",
        };

        row.Controls.Add(_fogColorSwatch);
        row.Controls.Add(_fogColor);
        row.Controls.Add(hint);
        return row;
    }

    private void PickFogColour()
    {
        using ColorDialog dialog = new()
        {
            Color = ParseFogColour(_fogColor.Text),
            FullOpen = true,
            AnyColor = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Writing the hex updates the swatch through TextChanged, so there is one path that decides
        // what the colour is and the two controls cannot drift apart.
        _fogColor.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    /// <summary>Shows the typed colour on the swatch. Half-typed hex simply keeps the last valid one.</summary>
    private void SyncFogSwatch() => _fogColorSwatch.Invalidate();

    private void PaintFogSwatch(object? sender, PaintEventArgs e)
    {
        Rectangle bounds = _fogColorSwatch.ClientRectangle;
        using SolidBrush fill = new(ParseFogColour(_fogColor.Text));
        e.Graphics.FillRectangle(fill, bounds);
        using Pen border = new(ThemeService.Palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    private static Color ParseFogColour(string? hex)
    {
        string normalized = NormalizeFogHex(hex);
        return Color.FromArgb(
            Convert.ToInt32(normalized.Substring(1, 2), 16),
            Convert.ToInt32(normalized.Substring(3, 2), 16),
            Convert.ToInt32(normalized.Substring(5, 2), 16));
    }

    private static Control Field(string labelText, Control editor)
    {
        Panel field = new()
        {
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12),
            Size = new Size(620, 62),
            Tag = "transparent",
        };
        Label label = new()
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(0, 0),
            Size = new Size(420, 25),
            Text = labelText,
        };
        editor.Location = new Point(0, 27);
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        editor.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        field.Resize += (_, _) =>
        {
            label.Width = Math.Max(1, field.ClientSize.Width);
            editor.Width = Math.Max(1, field.ClientSize.Width);
        };
        field.Controls.Add(label);
        field.Controls.Add(editor);
        return field;
    }

    private static Control BuildToggleRow(string label, KitToggle toggle)
    {
        Panel row = new()
        {
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12),
            Size = new Size(620, 36),
            Tag = "transparent",
        };
        Label text = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(0, 6),
            Size = new Size(520, 24),
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        toggle.Size = new Size(44, 24);
        toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(text);
        row.Controls.Add(toggle);
        row.Resize += (_, _) =>
        {
            text.Width = Math.Max(120, row.ClientSize.Width - toggle.Width - 16);
            toggle.Location = new Point(row.ClientSize.Width - toggle.Width, 6);
        };
        toggle.Location = new Point(row.Width - toggle.Width, 6);
        return row;
    }

    private void FilterCategories()
    {
        string? selected = _categories.SelectedItem?.ToString();
        string filter = _categoryFilter.Text.Trim();
        _categories.BeginUpdate();
        try
        {
            _categories.Items.Clear();
            foreach (string category in _allCategories)
            {
                if (string.IsNullOrEmpty(filter)
                    || category.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    _categories.Items.Add(category);
                }
            }

            if (selected is not null && _categories.Items.Contains(selected))
            {
                _categories.SelectedItem = selected;
            }
            else if (_categories.Items.Count > 0)
            {
                _categories.SelectedIndex = 0;
            }
        }
        finally
        {
            _categories.EndUpdate();
        }
    }

    private static void ConfigureCheckBox(CheckBox checkBox, string text)
    {
        checkBox.AutoSize = false;
        checkBox.ForeColor = ThemeService.Palette.Text;
        checkBox.Margin = new Padding(0, 0, 0, 10);
        checkBox.Size = new Size(620, 34);
        checkBox.Text = text;
    }

    private static void ConfigureCombo(ComboBox combo, string[] values, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Items.Clear();
        combo.Items.AddRange(values);
        combo.Size = new Size(width, 32);
    }

    private static int CloudQualityIndex(string label) => label?.Trim().ToLowerInvariant() switch
    {
        "performance" => 0,
        "balanced" => 1,
        "cinematic" => 3,
        _ => 2,
    };

    private static string CloudQualityLabel(int quality) => quality switch
    {
        0 => "Performance",
        1 => "Balanced",
        3 => "Cinematic",
        _ => "High",
    };

    private static void ConfigureNumeric(
        NumericUpDown numeric,
        decimal minimum,
        decimal maximum,
        decimal increment,
        int width)
    {
        numeric.Minimum = minimum;
        numeric.Maximum = maximum;
        numeric.Increment = increment;
        numeric.Size = new Size(width, 32);
    }

    private void RefreshLightingPreferenceState()
    {
        _shadowsEnabled.Enabled = _lightingEnabled.Checked;
        _shadowStrength.Enabled = _lightingEnabled.Checked && _shadowsEnabled.Checked;
        _shadowCascadeCount.Enabled = _lightingEnabled.Checked && _shadowsEnabled.Checked;
        _gtaoEnabled.Enabled = _lightingEnabled.Checked;
        _contactShadowsEnabled.Enabled = _lightingEnabled.Checked;
        _localVolumetricsEnabled.Enabled = _lightingEnabled.Checked;
        _smokeExtinctionEnabled.Enabled = _lightingEnabled.Checked;
        _bloomEnabled.Enabled = _lightingEnabled.Checked;
        _atmosphereLutEnabled.Enabled = _lightingEnabled.Checked;
        _raymarchedCloudsEnabled.Enabled = _lightingEnabled.Checked;
        _cloudTemporalEnabled.Enabled = _lightingEnabled.Checked;
        _celestialExtrasEnabled.Enabled = _lightingEnabled.Checked;
        _cloudQuality.Enabled = _lightingEnabled.Checked;
        _exposure.Enabled = _lightingEnabled.Checked;
        _contrast.Enabled = _lightingEnabled.Checked;
        _saturation.Enabled = _lightingEnabled.Checked;
        _vignetteStrength.Enabled = _lightingEnabled.Checked;
        _bloomThreshold.Enabled = _lightingEnabled.Checked && _bloomEnabled.Checked;
        _bloomIntensity.Enabled = _lightingEnabled.Checked && _bloomEnabled.Checked;
    }

    private void RefreshDrawBudgetPreferenceState()
    {
        _worldDrawBudget.Enabled = string.Equals(
            _drawCallMode.Text,
            RenderCapacityDefaults.DrawCallModeManual,
            StringComparison.OrdinalIgnoreCase);
    }

    private void LoadValues(GenesisSettings settings)
    {
        _showSplash.Checked = settings.General.ShowSplashScreen;
        _reopenLast.Checked = settings.General.ReopenLastProject;
        _confirmDelete.Checked = settings.General.ConfirmDestructiveActions;
        _externalChanges.Checked = settings.General.CheckForExternalChanges;
        _loadingAppearance = true;
        _loadedThemeName = NormalizeThemeName(settings.Appearance.Theme);
        string themeMode = ThemeCatalog.ResolveMode(
            settings.Appearance.ThemeMode, _loadedThemeName);
        SelectCombo(
            _colourTheme,
            ThemeCatalog.ColourDisplayName(
                string.Equals(themeMode, AppearanceThemeModes.Colour, StringComparison.Ordinal)
                    ? _loadedThemeName
                    : "Dark"));
        if (!_imageThemeGallery.SelectTheme(_loadedThemeName)
            && _imageThemeGallery.Images.FirstOrDefault() is { } firstImage)
        {
            _imageThemeGallery.SelectTheme(firstImage.Name);
        }

        _colourThemeMode.Checked = string.Equals(
            themeMode, AppearanceThemeModes.Colour, StringComparison.Ordinal);
        _imageThemeMode.Checked = !_colourThemeMode.Checked;
        _loadingAppearance = false;
        UpdateThemeModeVisibility();
        SelectCombo(_density, settings.Appearance.Density);
        _scale.Value = Math.Clamp((decimal)(settings.Appearance.InterfaceScale * 100f), 75, 200);
        _codeFontSize.Value = Math.Clamp(settings.Appearance.CodeFontSize, 8, 24);
        _animations.Checked = settings.Appearance.UseAnimations;
        _autoSave.Checked = settings.Editing.AutoSave;
        _autoSaveMinutes.Value = Math.Clamp(settings.Editing.AutoSaveMinutes, 1, 60);
        _backups.Checked = settings.Editing.CreateBackups;
        _backupDays.Value = Math.Clamp(settings.Editing.BackupRetentionDays, 1, 365);
        SelectCombo(_buildConfiguration, settings.Runtime.BuildConfiguration);
        SelectCombo(_architecture, settings.Runtime.PlayerArchitecture);
        _previewVsync.Checked = settings.Runtime.VSyncInPreview;
        _pauseUnfocused.Checked = settings.Runtime.PauseWhenStudioLosesFocus;
        _runtimeDiagnostics.Checked = settings.Runtime.EnableRuntimeDiagnostics;
        // The open project wins when it names a backend; otherwise the installation default shows.
        string? projectBackend = _project?.Manifest.Rendering?.Backend;
        _renderBackend.SelectedIndex = BackendIndex(RenderBackendCatalog.ParseSettingsValue(
            string.IsNullOrWhiteSpace(projectBackend) ? settings.Rendering.Backend : projectBackend));
        SelectCombo(_faceCulling,
            MeshRasterDefaults.ParseCulling(settings.Rendering.FaceCulling, FaceCullingOverride.Back).ToString());
        SelectCombo(_frontFaceWinding,
            MeshRasterDefaults.ParseWinding(
                settings.Rendering.FrontFaceWinding,
                FrontFaceWindingOverride.CounterClockwise).ToString());
        _lightingEnabled.Checked = settings.Rendering.LightingEnabled;
        _shadowsEnabled.Checked = settings.Rendering.ShadowsEnabled;
        _shadowStrength.Value = Math.Clamp(
            (decimal)(settings.Rendering.ShadowStrength * 100f), 0m, 100m);
        _shadowCascadeCount.Value = settings.Rendering.ShadowCascadeCount >= 3 ? 3m : 2m;
        _gtaoEnabled.Checked = settings.Rendering.GtaoEnabled;
        _contactShadowsEnabled.Checked = settings.Rendering.ContactShadowsEnabled;
        _localVolumetricsEnabled.Checked = settings.Rendering.LocalVolumetricsEnabled;
        _smokeExtinctionEnabled.Checked = settings.Rendering.SmokeExtinctionEnabled;
        _bloomEnabled.Checked = settings.Rendering.BloomEnabled;
        _waterReflections.Checked = settings.Rendering.WaterReflections;
        _atmosphereLutEnabled.Checked = settings.Rendering.AtmosphereLutEnabled;
        _raymarchedCloudsEnabled.Checked = settings.Rendering.RaymarchedCloudsEnabled;
        _cloudTemporalEnabled.Checked = settings.Rendering.CloudTemporalEnabled;
        _celestialExtrasEnabled.Checked = settings.Rendering.CelestialExtrasEnabled;
        SelectCombo(_cloudQuality, CloudQualityLabel(settings.Rendering.CloudQuality));
        _exposure.Value = Math.Clamp(
            (decimal)settings.Rendering.Exposure, _exposure.Minimum, _exposure.Maximum);
        _contrast.Value = Math.Clamp(
            (decimal)settings.Rendering.Contrast, _contrast.Minimum, _contrast.Maximum);
        _saturation.Value = Math.Clamp(
            (decimal)settings.Rendering.Saturation, _saturation.Minimum, _saturation.Maximum);
        _vignetteStrength.Value = Math.Clamp(
            (decimal)settings.Rendering.VignetteStrength,
            _vignetteStrength.Minimum,
            _vignetteStrength.Maximum);
        _bloomThreshold.Value = Math.Clamp(
            (decimal)settings.Rendering.BloomThreshold,
            _bloomThreshold.Minimum,
            _bloomThreshold.Maximum);
        _bloomIntensity.Value = Math.Clamp(
            (decimal)settings.Rendering.BloomIntensity,
            _bloomIntensity.Minimum,
            _bloomIntensity.Maximum);
        RefreshLightingPreferenceState();
        _spriteInstanceCap.Value = Math.Clamp(
            settings.Rendering.SpriteInstanceCap,
            _spriteInstanceCap.Minimum,
            _spriteInstanceCap.Maximum);
        _meshInstanceCap.Value = Math.Clamp(
            settings.Rendering.MeshInstanceCap,
            _meshInstanceCap.Minimum,
            _meshInstanceCap.Maximum);
        _sceneLocalLightCap.Value = Math.Clamp(
            settings.Rendering.SceneLocalLightCap,
            _sceneLocalLightCap.Minimum,
            _sceneLocalLightCap.Maximum);
        _omniShadowBudget.Value = Math.Clamp(
            settings.Rendering.OmniShadowBudget,
            _omniShadowBudget.Minimum,
            _omniShadowBudget.Maximum);
        _localVolumetricLightBudget.Value = Math.Clamp(
            settings.Rendering.LocalVolumetricLightBudget,
            _localVolumetricLightBudget.Minimum,
            _localVolumetricLightBudget.Maximum);
        SelectCombo(
            _drawCallMode,
            string.Equals(
                settings.Rendering.DrawCallMode,
                RenderCapacityDefaults.DrawCallModeManual,
                StringComparison.OrdinalIgnoreCase)
                ? RenderCapacityDefaults.DrawCallModeManual
                : RenderCapacityDefaults.DrawCallModeAuto);
        _worldDrawBudget.Value = Math.Clamp(
            settings.Rendering.WorldDrawBudget,
            _worldDrawBudget.Minimum,
            _worldDrawBudget.Maximum);
        RefreshDrawBudgetPreferenceState();
        ProjectRenderingSettings fog = _project?.Manifest.Rendering ?? new ProjectRenderingSettings();
        _allowEscapeToClose.Checked = _project?.Manifest.Runtime?.AllowEscapeToClose ?? true;
        _fogEnabled.Checked = fog.FogEnabled;
        _fogColor.Text = NormalizeFogHex(fog.FogColorHex);

        if (_project != null && _project.Manifest != null)
        {
            _projectIconPath = _project.Manifest.ProjectIcon ?? string.Empty;
            _projectIconFps.Value = Math.Clamp(_project.Manifest.ProjectIconFps, 0, 60);
            
            if (!string.IsNullOrWhiteSpace(_projectIconPath))
            {
                string absolutePath = ResourceNames.Resolve(_project.RootPath, _projectIconPath, ResourceType.Image);
                _projectIconPreview.LoadIcon(absolutePath, (int)_projectIconFps.Value);
            }
        }

        // Explicit, because assigning the same text raises no TextChanged: reopening Preferences
        // without this shows the swatch unpainted while the hex beside it reads the real colour.
        SyncFogSwatch();
        float fogStart = fog.FogStart;
        float fogThickness = Math.Max(0.1f, fog.FogEnd - fog.FogStart);
        _fogDepth.Value = Math.Clamp((decimal)fogStart, _fogDepth.Minimum, _fogDepth.Maximum);
        _fogThickness.Value = Math.Clamp((decimal)fogThickness, _fogThickness.Minimum, _fogThickness.Maximum);
        _fogAlpha.Value = Math.Clamp((decimal)(fog.FogAlpha * 100f), 0m, 100m);
        RefreshThemePreview();
        RefreshSystemRequirementsEstimate();
    }

    private bool TrySaveAndApply()
    {
        _settings.Update(settings =>
        {
            settings.General.ShowSplashScreen = _showSplash.Checked;
            settings.General.ReopenLastProject = _reopenLast.Checked;
            settings.General.ConfirmDestructiveActions = _confirmDelete.Checked;
            settings.General.CheckForExternalChanges = _externalChanges.Checked;
            settings.Appearance.ThemeMode = SelectedThemeMode;
            settings.Appearance.Theme = SelectedThemeName;
            settings.Appearance.Density = _density.Text;
            settings.Appearance.InterfaceScale = (float)(_scale.Value / 100m);
            settings.Appearance.CodeFontSize = (int)_codeFontSize.Value;
            settings.Appearance.UseAnimations = _animations.Checked;
            settings.Editing.AutoSave = _autoSave.Checked;
            settings.Editing.AutoSaveMinutes = (int)_autoSaveMinutes.Value;
            settings.Editing.CreateBackups = _backups.Checked;
            settings.Editing.BackupRetentionDays = (int)_backupDays.Value;
            settings.Runtime.BuildConfiguration = _buildConfiguration.Text;
            settings.Runtime.PlayerArchitecture = _architecture.Text;
            settings.Runtime.VSyncInPreview = _previewVsync.Checked;
            settings.Runtime.PauseWhenStudioLosesFocus = _pauseUnfocused.Checked;
            settings.Runtime.EnableRuntimeDiagnostics = _runtimeDiagnostics.Checked;
            settings.Rendering.Backend = BackendAt(_renderBackend.SelectedIndex).SettingsValue;
            settings.Rendering.FaceCulling = _faceCulling.Text;
            settings.Rendering.FrontFaceWinding = _frontFaceWinding.Text;
            settings.Rendering.LightingEnabled = _lightingEnabled.Checked;
            settings.Rendering.ShadowsEnabled = _shadowsEnabled.Checked;
            settings.Rendering.ShadowStrength = (float)(_shadowStrength.Value / 100m);
            settings.Rendering.ShadowCascadeCount = (int)_shadowCascadeCount.Value >= 3 ? 3 : 2;
            settings.Rendering.GtaoEnabled = _gtaoEnabled.Checked;
            settings.Rendering.ContactShadowsEnabled = _contactShadowsEnabled.Checked;
            settings.Rendering.LocalVolumetricsEnabled = _localVolumetricsEnabled.Checked;
            settings.Rendering.SmokeExtinctionEnabled = _smokeExtinctionEnabled.Checked;
            settings.Rendering.BloomEnabled = _bloomEnabled.Checked;
            settings.Rendering.WaterReflections = _waterReflections.Checked;
            settings.Rendering.AtmosphereLutEnabled = _atmosphereLutEnabled.Checked;
            settings.Rendering.RaymarchedCloudsEnabled = _raymarchedCloudsEnabled.Checked;
            settings.Rendering.CloudTemporalEnabled = _cloudTemporalEnabled.Checked;
            settings.Rendering.CelestialExtrasEnabled = _celestialExtrasEnabled.Checked;
            settings.Rendering.CloudQuality = CloudQualityIndex(_cloudQuality.Text);
            settings.Rendering.Exposure = (float)_exposure.Value;
            settings.Rendering.Contrast = (float)_contrast.Value;
            settings.Rendering.Saturation = (float)_saturation.Value;
            settings.Rendering.VignetteStrength = (float)_vignetteStrength.Value;
            settings.Rendering.BloomThreshold = (float)_bloomThreshold.Value;
            settings.Rendering.BloomIntensity = (float)_bloomIntensity.Value;
            settings.Rendering.SpriteInstanceCap = (int)_spriteInstanceCap.Value;
            settings.Rendering.MeshInstanceCap = (int)_meshInstanceCap.Value;
            settings.Rendering.SceneLocalLightCap = (int)_sceneLocalLightCap.Value;
            settings.Rendering.OmniShadowBudget = (int)_omniShadowBudget.Value;
            settings.Rendering.LocalVolumetricLightBudget =
                (int)_localVolumetricLightBudget.Value;
            settings.Rendering.DrawCallMode = _drawCallMode.Text;
            settings.Rendering.WorldDrawBudget = (int)_worldDrawBudget.Value;
        });

        // Written to the project as well, so the choice travels with it. Both are updated rather
        // than only the project: with no project open there is still a sensible default to keep.
        if (_project?.Manifest.Rendering is { } projectRendering)
        {
            projectRendering.Backend = BackendAt(_renderBackend.SelectedIndex).SettingsValue;
            projectRendering.GtaoEnabled = _gtaoEnabled.Checked;
            projectRendering.ContactShadowsEnabled = _contactShadowsEnabled.Checked;
            projectRendering.LocalVolumetricsEnabled = _localVolumetricsEnabled.Checked;
            projectRendering.SmokeExtinctionEnabled = _smokeExtinctionEnabled.Checked;
            projectRendering.BloomEnabled = _bloomEnabled.Checked;
            projectRendering.AtmosphereLutEnabled = _atmosphereLutEnabled.Checked;
            projectRendering.RaymarchedCloudsEnabled = _raymarchedCloudsEnabled.Checked;
            projectRendering.CloudTemporalEnabled = _cloudTemporalEnabled.Checked;
            projectRendering.CelestialExtrasEnabled = _celestialExtrasEnabled.Checked;
            projectRendering.CloudQuality = CloudQualityIndex(_cloudQuality.Text);
            projectRendering.LocalVolumetricLightBudget = (int)_localVolumetricLightBudget.Value;
        }

        SaveProjectSettings();

        RenderingPreferencesBridge.Apply(_settings.Current);
        ThemeService.ApplySettings(_settings.Current);
        ThemeService.Apply(this);
        ThemeService.ApplyToOpenForms();
        _categories.Invalidate();
        _themePreview.Invalidate();
        _loadedThemeName = SelectedThemeName;
        RefreshSystemRequirementsEstimate();
        return true;
    }

    /// <summary>
    /// Writes the Project page back into the project file and applies it immediately.
    /// </summary>
    /// <remarks>
    /// The project manifest is the persistence gateway for anything that belongs to the game, in
    /// the same way <c>SettingsService</c> is for anything belonging to the machine. Applying right
    /// after saving is what makes fog visible in the open editors without reopening the project.
    /// </remarks>
    private void SaveProjectSettings()
    {
        if (_project is null) return;

        _project.Manifest.Runtime ??= new ProjectRuntimeSettings();
        _project.Manifest.Rendering ??= new ProjectRenderingSettings();

        _project.Manifest.Runtime.AllowEscapeToClose = _allowEscapeToClose.Checked;

        ProjectRenderingSettings fog = _project.Manifest.Rendering;
        fog.FogEnabled = _fogEnabled.Checked;
        fog.FogColorHex = NormalizeFogHex(_fogColor.Text);
        fog.FogStart = (float)_fogDepth.Value;
        fog.FogEnd = fog.FogStart + Math.Max(0.1f, (float)_fogThickness.Value);
        fog.FogAlpha = Math.Clamp((float)(_fogAlpha.Value / 100m), 0f, 1f);

        _project.Manifest.ProjectIcon = _projectIconPath;
        _project.Manifest.ProjectIconFps = (int)_projectIconFps.Value;

        new ProjectService().Save(_project);
        RenderingPreferencesBridge.ApplyProject(_project.Manifest);
    }

    private void RestoreDefaults()
    {
        GenesisSettings defaults = new();
        LoadValues(defaults);
    }

    private void ShowSelectedPage()
    {
        string? selected = _categories.SelectedItem?.ToString();
        foreach ((string name, Control page) in _pages)
        {
            page.Visible = string.Equals(name, selected, StringComparison.Ordinal);
            if (page.Visible)
            {
                page.BringToFront();
            }
        }

        if (string.Equals(selected, "Project", StringComparison.Ordinal))
        {
            RefreshSystemRequirementsEstimate();
        }
    }

    /// <summary>Headless seam: switch the visible preferences page by category name.</summary>
    internal bool SelectCategory(string category)
    {
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

    private string SelectedThemeMode => _imageThemeMode.Checked
        ? AppearanceThemeModes.Image
        : AppearanceThemeModes.Colour;

    private string SelectedThemeName => _imageThemeMode.Checked
        ? _imageThemeGallery.SelectedTheme?.Name ?? _loadedThemeName
        : ColourKeyFromDisplay(_colourTheme.Text);

    private static string ColourKeyFromDisplay(string? display) => display?.Trim() switch
    {
        "Genesis Dark" => "Dark",
        "Genesis Light" => "Light",
        _ => string.IsNullOrWhiteSpace(display) ? "Dark" : display.Trim(),
    };

    private void ThemeModeChanged()
    {
        if (_loadingAppearance)
        {
            return;
        }

        UpdateThemeModeVisibility();
        RefreshThemePreview();
    }

    private void UpdateThemeModeVisibility()
    {
        bool imageMode = _imageThemeMode.Checked;
        if (_colourThemeField is not null)
        {
            _colourThemeField.Visible = !imageMode;
        }

        if (_imageThemeField is not null)
        {
            _imageThemeField.Visible = imageMode;
        }

        _addThemeImage.Visible = imageMode;
    }

    private void RefreshThemePreview() => _themePreview.Invalidate();

    /// <summary>Shows the selected theme: its picture if it has one, and the colours it produces.</summary>
    /// <remarks>
    /// The swatch strip runs along the bottom over the image rather than beside it, so you can see
    /// at a glance whether the derived accent actually belongs to the picture it came from.
    /// </remarks>
    private void PaintThemePreview(object? sender, PaintEventArgs e)
    {
        Rectangle bounds = new(0, 0, _themePreview.Width, _themePreview.Height);
        string themeName = SelectedThemeName;
        ThemeImage? image = _imageThemeMode.Checked
            ? ThemeCatalog.FindSelectableImage(themeName)
            : null;
        ThemePalette palette = image?.Palette ?? ThemeCatalog.GetColour(themeName);

        using (SolidBrush background = new(palette.Canvas))
        {
            e.Graphics.FillRectangle(background, bounds);
        }

        if (image?.Picture is { } picture)
        {
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            Rectangle source = CoverSource(picture.Width, picture.Height, bounds.Width, bounds.Height);
            e.Graphics.DrawImage(picture, bounds, source, GraphicsUnit.Pixel);
        }
        else if (image?.Failed == true)
        {
            TextRenderer.DrawText(
                e.Graphics,
                $"{image.Name} could not be read as an image.",
                ThemeService.InterfaceFont,
                bounds,
                palette.Error,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        Color[] swatches =
        [
            palette.Canvas,
            palette.Surface,
            palette.SurfaceRaised,
            palette.Border,
            palette.Text,
            palette.Accent,
            palette.Success,
        ];
        int stripHeight = 34;
        int top = bounds.Height - stripHeight - 8;
        int width = Math.Max(24, (bounds.Width - 16) / swatches.Length);

        for (int index = 0; index < swatches.Length; index++)
        {
            Rectangle rect = new(8 + (index * width), top, width - 4, stripHeight);
            using SolidBrush brush = new(swatches[index]);
            e.Graphics.FillRectangle(brush, rect);
            using Pen pen = new(palette.Border);
            e.Graphics.DrawRectangle(pen, rect);
        }

        using Pen frame = new(palette.BorderStrong);
        e.Graphics.DrawRectangle(frame, new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1));
    }

    /// <summary>Centred crop with the target's aspect ratio, so the preview does not distort.</summary>
    private static Rectangle CoverSource(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        double sourceAspect = (double)sourceWidth / sourceHeight;
        double targetAspect = (double)targetWidth / targetHeight;

        if (sourceAspect > targetAspect)
        {
            int width = (int)Math.Round(sourceHeight * targetAspect);
            return new Rectangle((sourceWidth - width) / 2, 0, width, sourceHeight);
        }

        int height = (int)Math.Round(sourceWidth / targetAspect);
        return new Rectangle(0, (sourceHeight - height) / 2, sourceWidth, height);
    }

    /// <summary>Copies an image the user picks into their themes folder and selects it.</summary>
    /// <remarks>
    /// Copied rather than referenced in place. A theme that points at a file on a memory stick or in
    /// a downloads folder someone later tidies up becomes a broken theme, and the failure would show
    /// up at the next launch rather than at the moment it was caused.
    /// </remarks>
    private void AddThemeImage()
    {
        using OpenFileDialog picker = new()
        {
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
            Title = "Choose a theme image",
        };

        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ApplicationPaths.UserThemesDirectory);
            string destination = Path.Combine(
                ApplicationPaths.UserThemesDirectory, Path.GetFileName(picker.FileName));

            if (File.Exists(destination)
                && MessageBox.Show(
                    this,
                    $"{Path.GetFileName(destination)} is already in your themes. Replace it?",
                    "Add theme image",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            File.Copy(picker.FileName, destination, overwrite: true);
            ThemeCatalog.RefreshImages();

            string added = ThemeImage.Describe(destination);
            _imageThemeGallery.SetImages(ThemeCatalog.Images, added);
            _imageThemeMode.Checked = true;
            UpdateThemeModeVisibility();
            RefreshThemePreview();
        }
        catch (IOException exception)
        {
            MessageBox.Show(
                this,
                $"The image could not be added: {exception.Message}",
                "Add theme image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(
                this,
                $"The image could not be added: {exception.Message}",
                "Add theme image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void DrawCategory(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Rectangle bounds = e.Bounds;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush clear = new(ThemeService.Palette.Surface))
        {
            e.Graphics.FillRectangle(clear, bounds);
        }

        Rectangle pill = Rectangle.Inflate(bounds, -4, -3);
        if (selected)
        {
            Color fill = Color.FromArgb(55, ThemeService.Palette.Accent);
            using GraphicsPath path = RoundedRect(pill, 8);
            using SolidBrush brush = new(fill);
            e.Graphics.FillPath(brush, path);
            using SolidBrush accent = new(ThemeService.Palette.Accent);
            e.Graphics.FillRectangle(
                accent,
                new Rectangle(pill.X, pill.Y + 4, 3, Math.Max(8, pill.Height - 8)));
        }
        else if ((e.State & DrawItemState.HotLight) != 0)
        {
            using GraphicsPath path = RoundedRect(pill, 8);
            using SolidBrush brush = new(ThemeService.Palette.SurfaceHover);
            e.Graphics.FillPath(brush, path);
        }

        string text = _categories.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            selected ? new Font(ThemeService.InterfaceFont, FontStyle.Bold) : ThemeService.InterfaceFont,
            new Rectangle(pill.X + 16, pill.Y, pill.Width - 20, pill.Height),
            selected ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = Math.Max(2, radius * 2);
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string NormalizeFogHex(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "#9EC7F0" : value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7 || !int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
            return "#9EC7F0";
        return text.ToUpperInvariant();
    }

    private static string NormalizeThemeName(string theme)
    {
        return theme switch
        {
            "Obsidian" => "Midnight",
            "Graphite" or "Genesis Dark" => "Dark",
            "Genesis Light" => "Light",
            _ => theme,
        };
    }

    private static void SelectCombo(ComboBox combo, string value)
    {
        int index = combo.FindStringExact(value);
        combo.SelectedIndex = index >= 0 ? index : 0;
    }
}


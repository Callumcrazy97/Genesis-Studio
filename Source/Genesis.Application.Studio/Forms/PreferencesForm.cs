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

public sealed partial class PreferencesForm : DpiAwareForm
{
    private readonly SettingsService _settings;
    private Action? _layoutMeasured;
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

    private readonly CheckBox _showSplash = new PreferenceSwitch();
    private readonly CheckBox _reopenLast = new PreferenceSwitch();
    private readonly CheckBox _confirmDelete = new PreferenceSwitch();
    private readonly CheckBox _externalChanges = new PreferenceSwitch();
    private readonly ThemedComboBox _density = new();
    private readonly NumericUpDown _scale = new();
    private readonly NumericUpDown _codeFontSize = new();
    private readonly CheckBox _animations = new PreferenceSwitch { Name = "AnimationsToggle" };
    private readonly CheckBox _autoSave = new PreferenceSwitch();
    private readonly NumericUpDown _autoSaveMinutes = new();
    private readonly CheckBox _backups = new PreferenceSwitch();
    private readonly NumericUpDown _backupDays = new();
    // Every picker here is the owner-drawn themed combo: a plain list combo draws system white.
    private readonly ThemedComboBox _buildConfiguration = new();
    private readonly ThemedComboBox _architecture = new();
    private readonly CheckBox _previewVsync = new PreferenceSwitch();
    private readonly CheckBox _pauseUnfocused = new PreferenceSwitch();
    private readonly CheckBox _runtimeDiagnostics = new PreferenceSwitch();
    private readonly CheckBox _recordWhenDebugging = new PreferenceSwitch();
    private readonly CheckBox _engineDebugCategory = new PreferenceSwitch();
    private readonly ThemedComboBox _renderBackend = new();
    private readonly ThemedComboBox _faceCulling = new() { Name = "FaceCullingPicker" };
    private readonly ThemedComboBox _frontFaceWinding = new() { Name = "FrontFaceWindingPicker" };
    private readonly CheckBox _lightingEnabled = new PreferenceSwitch { Name = "LightingEnabledPicker" };
    private readonly CheckBox _shadowsEnabled = new PreferenceSwitch { Name = "ShadowsEnabledPicker" };
    private readonly NumericUpDown _shadowStrength = new() { Name = "ShadowStrengthPicker" };
    private readonly NumericUpDown _shadowCascadeCount = new() { Name = "ShadowCascadeCountPicker" };
    private readonly CheckBox _gtaoEnabled = new PreferenceSwitch { Name = "GtaoEnabledPicker" };
    private readonly CheckBox _contactShadowsEnabled = new PreferenceSwitch { Name = "ContactShadowsEnabledPicker" };
    private readonly CheckBox _localVolumetricsEnabled = new PreferenceSwitch { Name = "LocalVolumetricsEnabledPicker" };
    private readonly CheckBox _smokeExtinctionEnabled = new PreferenceSwitch { Name = "SmokeExtinctionEnabledPicker" };
    private readonly CheckBox _bloomEnabled = new PreferenceSwitch { Name = "BloomEnabledPicker" };
    private readonly CheckBox _waterReflections = new PreferenceSwitch { Name = "WaterReflectionsPicker" };
    private readonly CheckBox _reversedDepth = new PreferenceSwitch { Name = "ReversedDepthPicker" };
    private readonly CheckBox _atmosphereLutEnabled = new PreferenceSwitch { Name = "AtmosphereLutEnabledPicker" };
    private readonly CheckBox _raymarchedCloudsEnabled = new PreferenceSwitch { Name = "RaymarchedCloudsEnabledPicker" };
    private readonly CheckBox _cloudTemporalEnabled = new PreferenceSwitch { Name = "CloudTemporalEnabledPicker" };
    private readonly CheckBox _celestialExtrasEnabled = new PreferenceSwitch { Name = "CelestialExtrasEnabledPicker" };
    private readonly ThemedComboBox _cloudQuality = new() { Name = "CloudQualityPicker" };
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
    private readonly ThemedComboBox _drawCallMode = new() { Name = "DrawCallModePicker" };
    private readonly NumericUpDown _worldDrawBudget = new() { Name = "WorldDrawBudgetPicker" };
    private readonly CheckBox _fogEnabled = new PreferenceSwitch();
    private readonly TextBox _fogColor = new();
    private readonly Panel _fogColorSwatch = new();
    private readonly NumericUpDown _fogDepth = new();
    private readonly NumericUpDown _fogThickness = new();
    private readonly NumericUpDown _fogAlpha = new();
    private readonly CheckBox _allowEscapeToClose = new PreferenceSwitch();
    private readonly CheckBox _blendGuiInLinearLight = new PreferenceSwitch { Name = "BlendGuiInLinearLightPicker" };
    private readonly ThemedComboBox _antiAliasing = new() { Name = "AntiAliasingPicker" };

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
        Tag = ThemeService.MeasuredLayoutTag;

        _allCategories = _project is null ? StudioCategoryNames : [.. StudioCategoryNames, .. ProjectCategoryNames];
        (_categories, _contentHost) = BuildShell();
        BuildPages();
        LoadValues(_settings.Current);
        _categories.SelectedIndex = 0;
        ThemeService.Apply(this);
    }

    public override void ApplyInterfaceLayout() => _layoutMeasured?.Invoke();

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
            ReversedDepth = _reversedDepth.Checked,
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
        void ArrangeColour()
        {
            _fogColorSwatch.Height = Math.Max(32, _fogColor.Height);
            _fogColor.Width = TextRenderer.MeasureText("#FFFFFF", _fogColor.Font).Width + 24;
            hint.Left = _fogColor.Right + 12;
            hint.Top = 0;
            if (row.ClientSize.Width - hint.Left < TextRenderer.MeasureText(hint.Text, hint.Font).Width)
            {
                hint.Left = 0;
                hint.Top = Math.Max(_fogColor.Bottom, _fogColorSwatch.Bottom) + 6;
            }
            hint.Width = Math.Max(1, row.ClientSize.Width - hint.Left);
            hint.Height = ShellDialogLayout.TextHeight(hint, hint.Width) + 4;
            row.Height = Math.Max(hint.Bottom, _fogColorSwatch.Bottom) + 4;
        }
        row.Resize += (_, _) => ArrangeColour();
        hint.FontChanged += (_, _) => ArrangeColour();
        _fogColor.FontChanged += (_, _) => ArrangeColour();
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
        _recordWhenDebugging.Checked = settings.Runtime.RecordProfileWhenDebugging;
        _engineDebugCategory.Checked = settings.Runtime.ShowEngineDebugCategory;
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
        _reversedDepth.Checked = settings.Rendering.ReversedDepth;
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
        _blendGuiInLinearLight.Checked = fog.BlendGuiInLinearLight;
        // The Project page (and its choices) exists only while a project is open.
        if (_antiAliasing.Items.Count > 0)
            _antiAliasing.SelectedIndex = AntiAliasingModes.TryParse(fog.AntiAliasing, out AntiAliasingMode antiAliasing)
                ? (int)antiAliasing
                : 0;
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
            settings.Runtime.RecordProfileWhenDebugging = _recordWhenDebugging.Checked;
            settings.Runtime.ShowEngineDebugCategory = _engineDebugCategory.Checked;
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
            settings.Rendering.ReversedDepth = _reversedDepth.Checked;
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
        fog.BlendGuiInLinearLight = _blendGuiInLinearLight.Checked;
        fog.AntiAliasing = _antiAliasing.SelectedIndex switch { 1 => "FXAA", 2 => "SMAA", _ => "Off" };
        fog.FogColorHex = NormalizeFogHex(_fogColor.Text);
        fog.FogStart = (float)_fogDepth.Value;
        fog.FogEnd = fog.FogStart + Math.Max(0.1f, (float)_fogThickness.Value);
        fog.FogAlpha = Math.Clamp((float)(_fogAlpha.Value / 100m), 0f, 1f);

        _project.Manifest.ProjectIcon = _projectIconPath;
        _project.Manifest.ProjectIconFps = (int)_projectIconFps.Value;

        new ProjectService().Save(_project);
        RenderingPreferencesBridge.ApplyProject(_project.Manifest);
        SaveBackupSettings();
    }

    private void RestoreDefaults()
    {
        GenesisSettings defaults = new();
        LoadValues(defaults);
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
        if (_themeSection is not null)
        {
            if (_colourThemeField is SettingRow colour) _themeSection.SetRowShown(colour, !imageMode);
            if (_imageThemeField is SettingRow image) _themeSection.SetRowShown(image, imageMode);
            if (_addThemeImageRow is not null) _themeSection.SetRowShown(_addThemeImageRow, imageMode);
        }

        _themePreview.Height = DpiLayout.Scale(this, imageMode ? 220 : 60);
        ArrangeCurrentPage();
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

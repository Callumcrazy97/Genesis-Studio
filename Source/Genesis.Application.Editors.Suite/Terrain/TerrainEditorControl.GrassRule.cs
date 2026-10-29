using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Terrain;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    // A draft keeps field edits apart from the saved rule until Apply, as the scatter settings do.
    private TerrainGrassRuleSettings _grassRuleDraft = new();
    private Control? _grassRuleInspector;
    private TerrainGrassField? _grassField;
    private TerrainAsset? _grassFieldTerrain;
    private int _grassFieldGroundRevision;
    private int _grassFieldPaintRevision;
    private List<TerrainWaterDefinition>? _grassFieldWaters;

    /// <summary>What the viewport's grass around the camera held and drew in the last frame.</summary>
    public TerrainGrassStatistics LastGrassRuleStatistics => _grassField?.Statistics ?? default;

    /// <summary>The saved grass rule (a copy).</summary>
    public TerrainGrassRuleSettings GrassRule => _nature.GrassRule.Clone();

    /// <summary>Adds the grass-around-the-camera settings to the foliage page.</summary>
    private void AddGrassRuleSection(FlowLayoutPanel page)
    {
        page.Controls.Add(MakeContextCaption("Grass Around the Camera"));
        page.Controls.Add(new Label
        {
            Text = "Grows grass from the painted layers in cells around the camera as it moves, instead of storing every tuft. "
                + "Give each painted layer a density, switch it on and Apply.",
            AutoSize = true, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont,
        });
        _grassRuleDraft = GrassRuleDraft();
        Control inspector = _grassRuleInspector = Inspector.InspectorBuilder.BuildForObject(
            _grassRuleDraft, GrassRuleNote, ProjectRoot, FindForm(), inline: true);
        inspector.Dock = DockStyle.Top;
        page.Controls.Add(inspector);
        LabelGrassLayerDensities();
        page.Controls.Add(MakeContextAction(
            "Apply Grass Rule",
            "Save the grass-around-the-camera rule above with this terrain",
            () => ApplyGrassRule(_grassRuleDraft)));
    }

    /// <summary>Replaces the terrain's grass rule as one undo step.</summary>
    public void ApplyGrassRule(TerrainGrassRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TerrainGrassRuleSettings before = _nature.GrassRule.Clone();
        TerrainGrassRuleSettings after = settings.Clone();
        after.Normalize();
        void Use(TerrainGrassRuleSettings rule, bool rebind)
        {
            _nature.GrassRule = rule.Clone();
            ResetGrassField();
            // Undo and redo show the restored rule in the fields, as the scatter settings do.
            if (rebind) RebindGrassRuleForm();
            _viewport.Invalidate(true);
        }

        Use(after, rebind: false);
        PushEdit(after.Enabled ? "Apply grass around the camera" : "Switch off grass around the camera",
            () => Use(after, rebind: true),
            () => Use(before, rebind: true));
        UpdateStatus();
    }

    private const string GrassRuleNote = "The same rule grows the grass in this viewport, the room preview and the running game.";

    /// <summary>
    /// The rule the grass fields start from: the saved one, or the built-in grass while the saved one
    /// is a switched-off rule nobody chose, so ticking Enabled and Apply grows the built-in grass.
    /// </summary>
    private TerrainGrassRuleSettings GrassRuleDraft() =>
        _nature.GrassRule.LooksUnset ? _nature.GrassRule.BuiltInWithLayers() : _nature.GrassRule.Clone();

    /// <summary>Shows the saved rule in the grass fields, once they are built.</summary>
    private void RebindGrassRuleForm()
    {
        if (_grassRuleInspector?.Parent is null) return;
        _grassRuleDraft = GrassRuleDraft();
        ReplaceSettingsForm(ref _grassRuleInspector, _grassRuleDraft, GrassRuleNote);
        LabelGrassLayerDensities();
    }

    /// <summary>
    /// Foliage › Grass: the built-in meadow grass (the Nature Walk's) on the painted grass, with no
    /// settings to choose, and a stock Grass layer coloured as the ground between its tufts. One
    /// undo step.
    /// </summary>
    public void ApplyGrassTemplate()
    {
        int grassLayer = GrassLayerIndex();
        List<TerrainLayerDocument>? layersBefore = null, layersAfter = null;
        if (grassLayer >= 0 && TerrainGrassGround.IsStock(_settings.Layers[grassLayer].Color, _settings.Layers[grassLayer].Image))
        {
            layersBefore = CloneLayers(_settings.Layers);
            layersAfter = CloneLayers(layersBefore);
            layersAfter[grassLayer].Color = TerrainGrassGround.GroundColor;
        }

        TerrainGrassRuleSettings ruleBefore = _nature.GrassRule.Clone();
        TerrainGrassRuleSettings grass = _nature.GrassRule.LooksUnset ? _nature.GrassRule.BuiltInWithLayers() : new TerrainGrassRuleSettings();
        grass.Enabled = true;
        if (grassLayer > 0 && grass.Density(0) > 0f && grass.Density(grassLayer) <= 0f)
        {
            // The rule grows on the layer called Grass, wherever it is in the list.
            grass.SetDensity(0, 0f);
            grass.SetDensity(grassLayer, 1f);
        }

        grass.Normalize();
        void Use(List<TerrainLayerDocument>? layers, TerrainGrassRuleSettings rule)
        {
            if (layers is not null)
            {
                _settings.Layers = CloneLayers(layers);
                RebuildLayerList();
                _nextMaterialCheck = DateTime.MinValue;
                RefreshMaterialTiles();
            }

            _nature.GrassRule = rule.Clone();
            ResetGrassField();
            RebindGrassRuleForm();
            RefreshComponentsPanel();
            _viewport.Invalidate(true);
        }

        Use(layersAfter, grass);
        PushEdit("Grass", () => Use(layersAfter, grass), () => Use(layersBefore, ruleBefore));
        UpdateStatus();
        _statusLabel.Text = "Grass added · the built-in meadow grass grows around the camera on the painted grass";
    }

    /// <summary>The painted layer called Grass, else the first; -1 without layers.</summary>
    private int GrassLayerIndex()
    {
        if (_settings.Layers.Count == 0) return -1;
        int named = _settings.Layers.FindIndex(layer => layer.Name?.Contains("grass", StringComparison.OrdinalIgnoreCase) == true);
        return named >= 0 && named < TerrainGrassRuleSettings.LayerCount ? named : 0;
    }

    /// <summary>The rule's blade texture, loaded for this viewport's renderer.</summary>
    private TextureHandle GrassRuleTexture(IRenderController renderer, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ProjectRoot)) return TextureHandle.Invalid;
        if (string.Equals(_grassTextureName, name, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(_grassTextureRenderer, renderer))
            return _grassTexture;
        _grassTextureName = name;
        _grassTextureRenderer = renderer;
        _grassTexture = TextureHandle.Invalid;
        try
        {
            string path = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(ProjectRoot, name, 0);
            if (File.Exists(path)) _grassTexture = renderer.LoadTexture(path, Genesis.Shared.Materials.TextureColorSpace.Srgb);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException)
        {
            _statusLabel.Text = $"The grass texture '{name}' could not be loaded: {exception.Message}";
        }

        return _grassTexture;
    }

    private string? _grassTextureName;
    private IRenderController? _grassTextureRenderer;
    private TextureHandle _grassTexture;

    // ── The grass rule in Studio's Inspector ───────────────────────────────────────

    private const string GrassInspectorPrefix = "Grass.";

    /// <summary>The grass rule's settings people change most, shown in the Inspector beside the terrain's own.</summary>
    private static readonly (string Property, string Group)[] GrassInspectorProperties =
    [
        (nameof(TerrainGrassRuleSettings.Enabled), "Grass around the camera"),
        (nameof(TerrainGrassRuleSettings.Species), "Grass around the camera"),
        (nameof(TerrainGrassRuleSettings.Radius), "Grass around the camera"),
        (nameof(TerrainGrassRuleSettings.Spacing), "Grass around the camera"),
        (nameof(TerrainGrassRuleSettings.MinimumScale), "Grass around the camera"),
        (nameof(TerrainGrassRuleSettings.MaximumScale), "Grass around the camera"),
        (nameof(TerrainGrassRuleSettings.ColorEnabled), "Grass · Blade colour and texture"),
        (nameof(TerrainGrassRuleSettings.BaseColor), "Grass · Blade colour and texture"),
        (nameof(TerrainGrassRuleSettings.TipColor), "Grass · Blade colour and texture"),
        (nameof(TerrainGrassRuleSettings.ColorCurve), "Grass · Blade colour and texture"),
        (nameof(TerrainGrassRuleSettings.Texture), "Grass · Blade colour and texture"),
        (nameof(TerrainGrassRuleSettings.WindEnabled), "Grass · Wind"),
        (nameof(TerrainGrassRuleSettings.WindStrength), "Grass · Wind"),
        (nameof(TerrainGrassRuleSettings.WindSpeed), "Grass · Wind"),
        (nameof(TerrainGrassRuleSettings.WindGusts), "Grass · Wind"),
        (nameof(TerrainGrassRuleSettings.PushersEnabled), "Grass · Parting and paths"),
        (nameof(TerrainGrassRuleSettings.PushStrength), "Grass · Parting and paths"),
        (nameof(TerrainGrassRuleSettings.SpringBackSeconds), "Grass · Parting and paths"),
        (nameof(TerrainGrassRuleSettings.ClearPaths), "Grass · Parting and paths"),
        (nameof(TerrainGrassRuleSettings.PathEdge), "Grass · Parting and paths"),
    ];

    /// <summary>Adds the grass rule's main settings to the Inspector's values (paths "Grass.Name").</summary>
    private void AddGrassInspectorValues(List<ResourceInspectorLiveValue> values)
    {
        TerrainGrassRuleSettings rule = _nature.GrassRule;
        foreach ((string name, string group) in GrassInspectorProperties)
        {
            System.Reflection.PropertyInfo property = typeof(TerrainGrassRuleSettings).GetProperty(name)!;
            object value = property.GetValue(rule) ?? "";
            string description = property.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .OfType<System.ComponentModel.DescriptionAttribute>().FirstOrDefault()?.Description ?? "";
            string label = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
            values.Add(property.PropertyType.IsEnum
                ? new ResourceInspectorLiveValue(group, GrassInspectorPrefix + name, label, value.ToString()!,
                    Description: description, Choices: Enum.GetNames(property.PropertyType))
                : name == nameof(TerrainGrassRuleSettings.Texture)
                    ? new ResourceInspectorLiveValue(group, GrassInspectorPrefix + name, label, value, Description: description,
                        AssetKind: Genesis.Application.Core.Resources.ResourceKind.Image)
                    : new ResourceInspectorLiveValue(group, GrassInspectorPrefix + name, label, value, Description: description));
        }
    }

    /// <summary>Sets one of the grass rule's settings from the Inspector, as one undo step; false for another path or a bad value.</summary>
    private bool TryApplyGrassInspectorValue(string propertyPath, object? value)
    {
        if (!propertyPath.StartsWith(GrassInspectorPrefix, StringComparison.Ordinal)) return false;
        string name = propertyPath[GrassInspectorPrefix.Length..];
        if (!GrassInspectorProperties.Any(entry => entry.Property == name)) return false;
        System.Reflection.PropertyInfo property = typeof(TerrainGrassRuleSettings).GetProperty(name)!;
        TerrainGrassRuleSettings changed = _nature.GrassRule.Clone();
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
        object converted;
        Type type = property.PropertyType;
        if (type == typeof(Vector3))
        {
            if (value is Vector3 vector) converted = vector;
            else if (TryParseColour(text, out Vector3 colour)) converted = colour;
            else return false;
        }
        else if (type.IsEnum)
        {
            if (!Enum.TryParse(type, text, true, out object? parsed) || parsed is null) return false;
            converted = parsed;
        }
        else if (type == typeof(string)) converted = text;
        else converted = Convert.ChangeType(value ?? "", type, System.Globalization.CultureInfo.InvariantCulture)!;
        property.SetValue(changed, converted);
        ApplyGrassRule(changed);
        RebindGrassRuleForm();
        NotifyInspectorStateChanged();
        return true;
    }

    /// <summary>A colour as "#rrggbb" or "r, g, b" from 0 to 1.</summary>
    private static bool TryParseColour(string text, out Vector3 colour)
    {
        colour = default;
        string hex = text.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int packed))
        {
            colour = new Vector3((packed >> 16 & 255) / 255f, (packed >> 8 & 255) / 255f, (packed & 255) / 255f);
            return true;
        }

        string[] parts = text.Trim('<', '>', '(', ')', '[', ']').Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        var numbers = new float[3];
        for (int index = 0; index < 3; index++)
            if (!float.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[index])
                || !float.IsFinite(numbers[index]))
                return false;
        colour = new Vector3(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>
    /// Names each density field after its painted layer ("Layer 1 (Grass) density"), so it is clear
    /// which ground it grows on. Layers this terrain does not have keep just their number.
    /// </summary>
    private void LabelGrassLayerDensities()
    {
        if (_grassRuleInspector is null) return;
        const string prefix = "InspectorDrawer_Layer", suffix = "Density";
        Control.ControlCollection rows = _grassRuleInspector.Controls;
        // The fields are a caption followed by its field (see InspectorBuilder's inline form).
        for (int index = 1; index < rows.Count; index++)
        {
            string name = rows[index].Name;
            if (rows[index - 1] is not Label caption || name.Length <= prefix.Length + suffix.Length
                || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), out int number)
                || number < 1)
                continue;
            string layer = number <= _settings.Layers.Count ? _settings.Layers[number - 1].Name?.Trim() ?? "" : "";
            caption.Text = layer.Length > 0 ? $"Layer {number} ({layer}) density" : $"Layer {number} density";
        }
    }

    private void ResetGrassField()
    {
        _grassField?.Dispose();
        _grassField = null;
        _grassFieldTerrain = null;
        _grassFieldWaters = null;
    }

    /// <summary>Grows and draws the rule's grass around the viewport camera when the rule is on.</summary>
    private void DrawGrassRule(IRenderController renderer, Vector3 camera, Matrix4x4 viewProjection)
    {
        TerrainGrassRuleSettings rule = _nature.GrassRule;
        if (rule is not { Enabled: true } || _terrain is null)
        {
            if (_grassField is not null) ResetGrassField();
            return;
        }

        // Replaced ground, changed water or paths or a changed rule starts again.
        if (_grassField is not null && (!ReferenceEquals(_grassFieldTerrain, _terrain)
            || !ReferenceEquals(_grassFieldWaters, _nature.WaterBodies) || !_grassField.GrowsFrom(rule)
            || !_grassField.ClearsPaths(_nature.Paths)))
            ResetGrassField();

        if (_grassField is null)
        {
            _grassField = new TerrainGrassField(_terrain, rule, _nature.WaterBodies, _nature.Paths);
            _grassFieldTerrain = _terrain;
            _grassFieldWaters = _nature.WaterBodies;
            _grassFieldGroundRevision = _groundRevision;
            _grassFieldPaintRevision = MaterialPreviewRevision;
        }
        else if (_grassFieldGroundRevision != _groundRevision || _grassFieldPaintRevision != MaterialPreviewRevision)
        {
            // Sculpted or painted: regrow in place, so the grass follows the stroke without going bare.
            _grassField.Refresh();
            _grassFieldGroundRevision = _groundRevision;
            _grassFieldPaintRevision = MaterialPreviewRevision;
        }

        // Colours, texture and wind change how the grass is drawn, never where it grows.
        _grassField.Look = TerrainGrassLook.From(rule, GrassRuleTexture(renderer, rule.Texture));
        _grassField.Update(camera, Matrix4x4.Identity);
        _grassField.Draw(renderer, camera, viewProjection);
        // Keep painting frames until every cell in reach has grown.
        if (_grassField.Pending) _viewport.Invalidate(true);
    }
}

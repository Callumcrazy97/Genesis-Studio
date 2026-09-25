using System.Drawing;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Shared.Assets;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ShaderEditorControl
{
    private readonly SplitContainer _previewAndControls;
    private readonly Panel _propertiesPanel;
    private readonly Panel _presetLibrary;
    private readonly Button _presetHeader;
    private readonly ToolStripButton _controlsToggle;
    private readonly ToolStripButton _autoCompile;
    private readonly ToolStripLabel _dimensionLabel;
    private readonly FlowLayoutPanel _targetAssetGroup;
    private readonly FlowLayoutPanel _componentGroup;
    private bool _controlsVisible = true;
    private bool _codeControlsVisible;
    private bool _presetsExpanded;
    private string? _framedModel;
    private Matrix4x4 _previewModelWorld = Matrix4x4.Identity;
    private string _objectModel = string.Empty;
    private string _objectImage = string.Empty;
    private string? _resolvedObject;
    private Vector3 _objectScale = Vector3.One;

    public string DiagnosticText => string.Join(Environment.NewLine, _output.Items.Cast<ListViewItem>().Select(item => item.Text));
    public Matrix4x4 PreviewModelTransform => _previewModelWorld;
    public int PreviewFrame => _previewFrame;
    public string PreviewObjectModel => _objectModel;
    public IReadOnlyList<string> PreviewAssets => _targetAssets;
    public void SetPlayback(bool playing) { if (_playing != playing) TogglePlayback(); }
    public void ResetPlayback() => StopPreview();
    public bool ChoosePreviewAsset(string path) => SetPreviewAsset(path);

    private static string FriendlyParameterName(string name) =>
        Regex.Replace(Regex.Replace(name.Replace('_', ' '), "([a-z])([A-Z])", "$1 $2"), "^.", match => match.Value.ToUpperInvariant());

    private static string ParameterSection(ShaderParameterValue parameter) =>
        Regex.IsMatch(parameter.Name, "speed|tempo|time|frequency|phase|pulse|wave", RegexOptions.IgnoreCase) ? "Animation" : "Appearance";

    private static Label ParameterSectionHeading(string name) => new()
    {
        Text = name, Height = 32, TextAlign = ContentAlignment.MiddleLeft,
        Font = EditorChrome.HeadingFont, ForeColor = EditorChrome.Text, Padding = new Padding(8, 0, 0, 0),
        Margin = new Padding(0, 4, 0, 2),
    };

    private void FramePreviewTarget()
    {
        _framedModel = null;
        _loadedTerrainResource = null;
        _viewport.Camera2DX = _viewport.Camera2DY = 0;
        _viewport.Zoom2D = 1;
        _viewport.Invalidate(true);
    }

    private void ResizeDiagnosticsColumn()
    {
        if (_output.Columns.Count == 0) return;
        int textWidth = _output.Items.Cast<ListViewItem>().Select(item => TextRenderer.MeasureText(item.Text, _output.Font).Width).DefaultIfEmpty(0).Max();
        _output.Columns[0].Width = Math.Max(_output.ClientSize.Width - 4, textWidth + 24);
    }

    private static TerrainAsset LoadPreviewTerrain(string resource)
    {
        if (File.Exists(resource + ".gterrain")) return TerrainAsset.Load(resource + ".gterrain");
        JObject settings = JObject.Parse(File.ReadAllText(resource));
        TerrainGenParams parameters = settings.ToObject<TerrainGenParams>() ?? new TerrainGenParams();
        if (settings["resolution"] is JArray { Count: >= 2 } resolution)
        {
            parameters.ResolutionX = (int)resolution[0];
            parameters.ResolutionZ = (int)resolution[1];
        }
        return TerrainGenerator.Generate(parameters);
    }

    private void ShowPresetActions(Button save)
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("Save as preset…", null, (_, _) => save.PerformClick());
        menu.Items.Add("Rename…", null, (_, _) => _renamePresetButton.PerformClick()).Enabled = _renamePresetButton.Enabled;
        menu.Items.Add("Update preset", null, (_, _) => _updatePresetButton.PerformClick()).Enabled = _updatePresetButton.Enabled;
        menu.Items.Add("Delete preset", null, (_, _) => _deletePresetButton.PerformClick()).Enabled = _deletePresetButton.Enabled;
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(Cursor.Position);
    }

    private ShaderAssetPipeline ResolveTargetPipeline()
    {
        if (!IsObjectPreview) return PipelineForTarget(_document.TargetType);
        ResolveObjectPreview();
        if (_objectModel.Length == 0 && _objectImage.Length == 0) return _document.Pipeline;
        return _objectIs3D ? ShaderAssetPipeline.Mesh : ShaderAssetPipeline.Sprite;
    }

    private bool _objectIs3D;
    private bool IsObjectPreview => _document.TargetType == ShaderTargetType.Object
        || (_document.TargetType == ShaderTargetType.Terrain && _document.TargetComponent.StartsWith("entity:", StringComparison.OrdinalIgnoreCase));

    private static FlowLayoutPanel TargetField(Control label, Control picker)
    {
        FlowLayoutPanel group = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 12, 0) };
        group.Controls.Add(label);
        group.Controls.Add(picker);
        return group;
    }

    private void ResolveObjectPreview()
    {
        string? path = _document.TargetType == ShaderTargetType.Terrain && IsObjectPreview
            ? ProjectAssetIndex.ResolveReference(ProjectRoot, _document.TargetComponent["entity:".Length..], ResourceKind.GameObject)
            : ResolveTargetResourcePath();
        if (path == _resolvedObject) return;
        _resolvedObject = path;
        _objectModel = _objectImage = string.Empty;
        _objectScale = Vector3.One;
        _objectIs3D = false;
        if (path is null || !File.Exists(path)) return;
        try
        {
            JObject document = JObject.Parse(File.ReadAllText(path));
            ObjectCompositionModel composition = new(document);
            JObject? model = composition.Components.OfType<JObject>().FirstOrDefault(c =>
                ((string?)c["type"] is "ModelRendererComponent" or "Model") && (bool?)c["enabled"] != false);
            JObject? sprite = composition.Components.OfType<JObject>().FirstOrDefault(c =>
                ((string?)c["type"] is "SpriteComponent" or "Texture") && (bool?)c["enabled"] != false);
            if (model is not null)
            {
                JObject props = ObjectCompositionModel.Props(model);
                _objectModel = ResolveProjectReference((string?)props["ModelAsset"] ?? (string?)props["Model"], ResourceKind.Model);
                _objectScale = new Vector3((float?)props["ScaleX"] ?? 1, (float?)props["ScaleY"] ?? 1, (float?)props["ScaleZ"] ?? 1);
            }
            if (sprite is not null)
                _objectImage = ResolveProjectReference((string?)ObjectCompositionModel.Props(sprite)["Sprite"] ?? (string?)ObjectCompositionModel.Props(sprite)["Texture"], ResourceKind.Image);
            string dimension = (string?)document["dimension"] ?? string.Empty;
            _objectIs3D = dimension.Length > 0
                ? dimension.Equals("ThreeD", StringComparison.OrdinalIgnoreCase) || dimension.Equals("3D", StringComparison.OrdinalIgnoreCase)
                : _objectModel.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or Newtonsoft.Json.JsonException or InvalidOperationException)
        {
            LoadWarning = "Object preview could not be loaded: " + exception.Message;
        }
    }

    private string ResolveProjectReference(string? value, ResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return ResourceNames.Name(ProjectRoot, value, ProjectAssetIndex.TypeFor(kind));
    }

    private void RefreshTargetDimension()
    {
        ShaderAssetPipeline previous = _document.Pipeline;
        _document.Pipeline = ResolveTargetPipeline();
        _viewport.Mode2D = _document.Pipeline != ShaderAssetPipeline.Mesh;
        if (previous != _document.Pipeline)
        {
            // Only replace stock source during a pipeline transition. Custom source remains authored data.
            ShaderPresetDefinition? stock = _presets.FirstOrDefault(p => p.Source == _document.Source);
            if (stock is not null)
            {
                ShaderPresetDefinition replacement = _presets.First(p => p.TargetType == TargetForPipeline(_document.Pipeline));
                _document.Source = replacement.Source;
                _document.Preset = replacement.Name;
                _code.CodeText = replacement.Source;
            }
            // A dimension switch must never link the previous dimension's fragment shader to
            // the new vertex pipeline, even for one frame while auto-compile is debouncing.
            CompileNow();
        }
    }

    private string PreviewModelAsset()
    {
        if (!IsObjectPreview) return _document.PreviewAsset;
        ResolveObjectPreview();
        return _objectModel;
    }

    private void FramePreviewModel(string asset)
    {
        if (_framedModel == asset) return;
        _framedModel = asset;
        Vector3 min = new(-.5f), max = new(.5f);
        if (asset.Length > 0 && !_modelPreviewRenderer.TryGetBounds(ProjectRoot, asset, out min, out max, includePivot: true)) return;
        Vector3 scale = IsObjectPreview ? _objectScale : Vector3.One;
        Vector3 a = min * scale, b = max * scale;
        min = Vector3.Min(a, b); max = Vector3.Max(a, b);
        Vector3 size = max - min;
        float fit = 3f / Math.Max(.001f, Math.Max(size.X, Math.Max(size.Y, size.Z)));
        Vector3 center = (min + max) * .5f;
        _previewModelWorld = Matrix4x4.CreateScale(scale * fit)
            * Matrix4x4.CreateTranslation(-center.X * fit, -min.Y * fit, -center.Z * fit);
        _viewport.FloorHeight = 0;
        _viewport.Camera.Target = new Vector3(0, size.Y * fit * .5f, 0);
        _viewport.Camera.Distance = 4.5f;
    }
}

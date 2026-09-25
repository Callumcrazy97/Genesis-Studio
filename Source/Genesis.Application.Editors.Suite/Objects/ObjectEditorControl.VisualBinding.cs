using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

public sealed partial class ObjectEditorControl
{
    private const string VisualModelActionId = "object_visual_model";
    private readonly Dictionary<int, NumericUpDown> _visualScaleFields = [];
    private bool _visualBindingMigrated;
    private string? _initialModelSource;
    private string _initialModel = "";
    private string? _identityVisualKey;

    /// <summary>Legacy model components become visible, ordinary Create-event PGSL in the working copy.</summary>
    private void ExposeLegacyModelBinding()
    {
        var component = _composition.Find("ModelRendererComponent");
        string model = (string?)_document["model"] ?? "";
        if (component is not null)
        {
            model = (string?)component["props"]?["ModelAsset"] ?? model;
            if (string.IsNullOrWhiteSpace(model)) model = (string?)component["props"]?["Model"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(model)) return;
        _events.TryGetValue("Create", out string? source); source ??= "";
        if (!VisualActionSyntax.Parse(source).Any(block => block.CommandName == "ModelSet"))
            _events["Create"] = VisualActionSyntax.CreateBlock(ModelAssignment(model) with
                { DetachedChain = (bool?)component?["enabled"] == false ? "disabled_visual_model" : "" }, VisualModelActionId) + source;
        ClearHiddenModelBinding(); _visualBindingMigrated = true;
    }

    private static VisualActionTemplate ModelAssignment(string model) => new("Set Model", "ModelSet", "Appearance",
        "Use this 3D model; leave empty for the textured cube", [new("model", model, VisualActionValueKind.Asset, ResourceKind.Model)]);

    private void ClearHiddenModelBinding()
    {
        if (_composition.Find("ModelRendererComponent") is { } component)
        {
            var props = ObjectCompositionModel.Props(component); props["ModelAsset"] = ""; props.Remove("Model");
        }
        _document["model"] = "";
    }

    public string InitialModelBinding
    {
        get
        {
            string source = SourceForEvent("Create");
            if (source == _initialModelSource) return _initialModel;
            _initialModelSource = source;
            _initialModel = Genesis.Runtime.Scene.ObjectDefinitionResolver.TryPreviewModel(source, out string model) ? model : "";
            return _initialModel;
        }
    }

    public void SetVisualModel(string model)
    {
        string source = SourceForEvent("Create");
        var block = VisualActionSyntax.Parse(source).LastOrDefault(block => block.CommandName == "ModelSet" && block.FlowId is null);
        source = block is null ? VisualActionSyntax.CreateBlock(ModelAssignment(model), VisualModelActionId) + source
            : VisualActionSyntax.Replace(source, block, ModelAssignment(model));
        ClearHiddenModelBinding(); _thumbnailAsset = null;
        SetEventBody("Create", source); SyncIdentityAssetLabel(); RefreshSpritePreview();
    }

    public void UseImageVisual(string image)
    {
        _composition.SetAsset("SpriteComponent", image); ClearHiddenModelBinding();
        string source = SourceForEvent("Create");
        var modelBlocks = VisualActionSyntax.Parse(source).Where(block => block.CommandName == "ModelSet" && block.FlowId is null).ToArray();
        foreach (var block in modelBlocks.Reverse()) source = VisualActionSyntax.Replace(source, block, ModelAssignment(""));
        if (modelBlocks.Length > 0) SetEventBody("Create", source);
        _thumbnailAsset = null; PopulateAssetCombos(); SyncFromDocument(); RefreshSpritePreview(); MarkDirty();
    }

    private JObject VisualPreviewPrefab(JObject prefab)
    {
        var clone = (JObject)prefab.DeepClone();
        string model = IsThreeD ? InitialModelBinding : "";
        var composition = new ObjectCompositionModel(clone);
        if (model.Length > 0 || composition.Find("ModelRendererComponent") is not null)
            composition.SetAsset("ModelRendererComponent", model);
        clone["model"] = model;
        return clone;
    }

    public void SetVisualDimension(bool threeD)
    {
        _document["dimension"] = threeD ? "ThreeD" : "TwoD";
        _thumbnailAsset = null; SyncFromDocument(); RefreshSpritePreview(); MarkDirty();
    }

    private Control BuildVisualScaleRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        row.Controls.Add(new Label { Text = "Scale", Dock = DockStyle.Fill, ForeColor = EditorChrome.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        for (int axis = 0; axis < 3; axis++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            var field = new NumericUpDown { DecimalPlaces = 2, Minimum = .01m, Maximum = 10000, Increment = .1m, Value = 1, Dock = DockStyle.Fill, Margin = new Padding(2) };
            EditorChrome.StyleField(field); _visualScaleFields[axis] = field;
            field.AccessibleName = "Scale " + "XYZ"[axis];
            field.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                var transform = _composition.Find("TransformComponent");
                if (transform is null)
                {
                    transform = new JObject { ["type"] = "TransformComponent", ["enabled"] = true, ["props"] = new JObject() };
                    _composition.Components.Add(transform);
                }
                ObjectCompositionModel.Props(transform)["Scale"] = new JArray(_visualScaleFields.OrderBy(pair => pair.Key).Select(pair => (float)pair.Value.Value));
                _thumbnailAsset = null; MarkDirty();
            };
            row.Controls.Add(field, axis + 1, 0);
        }
        return row;
    }

    private void SyncVisualScaleFields()
    {
        var scale = _composition.Find("TransformComponent")?["props"]?["Scale"];
        string[]? values = scale?.Type == JTokenType.String ? scale.Value<string>()?.Split(',') : null;
        foreach (var (axis, field) in _visualScaleFields)
        {
            decimal value = scale is JArray array && array.Count > axis ? array[axis].Value<decimal>()
                : values?.Length > axis && decimal.TryParse(values[axis], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 1m;
            field.Value = Math.Clamp(value, field.Minimum, field.Maximum);
        }
    }
}

using System.Text.Json;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public sealed record VisualActionPreset(
    string Id,
    string Name,
    string Category,
    string Description,
    string CommandName,
    List<VisualActionParameter> Parameters,
    string Body,
    bool BuiltIn = false,
    string ResultVariable = "")
{
    public string DisplayName => $"{Category} — {Name}";

    public VisualActionTemplate ToTemplate() =>
        new(Name, CommandName, Category, Description, Parameters, Body, ResultVariable);

    public static VisualActionPreset FromTemplate(VisualActionTemplate template, string? id = null) =>
        new(
            id ?? "preset_" + Guid.NewGuid().ToString("N")[..12],
            template.Name,
            template.Category,
            template.Description,
            template.CommandName,
            [.. template.Parameters],
            template.Body,
            BuiltIn: false,
            ResultVariable: template.ResultVariable);
}

/// <summary>Project-local reusable action blocks stored outside Assets so they never leak into the resource tree.</summary>
public sealed class VisualActionPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly List<VisualActionPreset> _userPresets = [];

    public VisualActionPresetStore(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("A project root is required.", nameof(projectRoot));
        _path = Path.Combine(projectRoot, ".genesis", "Editor", "ActionPresets.json");
        Load();
    }

    public IReadOnlyList<VisualActionPreset> All =>
        BuiltIns.Concat(_userPresets)
            .OrderBy(preset => preset.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<VisualActionPreset> UserPresets => _userPresets.ToArray();

    public VisualActionPreset Save(VisualActionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        int existing = _userPresets.FindIndex(preset =>
            string.Equals(preset.Name, template.Name, StringComparison.OrdinalIgnoreCase));
        VisualActionPreset saved = VisualActionPreset.FromTemplate(
            template,
            existing >= 0 ? _userPresets[existing].Id : null);
        if (existing >= 0) _userPresets[existing] = saved;
        else _userPresets.Add(saved);
        Persist();
        return saved;
    }

    public bool Delete(string id)
    {
        int index = _userPresets.FindIndex(preset => preset.Id == id);
        if (index < 0) return false;
        _userPresets.RemoveAt(index);
        Persist();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            VisualActionPreset[]? loaded = JsonSerializer.Deserialize<VisualActionPreset[]>(
                File.ReadAllText(_path), JsonOptions);
            if (loaded is not null) _userPresets.AddRange(loaded.Where(preset => !preset.BuiltIn));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _userPresets.Clear();
        }
    }

    private void Persist()
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_userPresets, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    private static IReadOnlyList<VisualActionPreset> BuiltIns { get; } =
    [
        new(
            "builtin_if_else",
            "If / Else package",
            "Control Flow",
            "Create a brace-wrapped condition with Then and Else drop zones.",
            "Control.IfElse",
            [],
            "",
            BuiltIn: true),
        new(
            "builtin_draw_self",
            "Draw this object",
            "Drawing 2D",
            "Draw the Object using its current image, frame, scale and tint.",
            "DrawSelf",
            [],
            "DrawSelf();",
            BuiltIn: true),
        new(
            "builtin_move_right",
            "Move right",
            "Movement",
            "Advance the Object horizontally every Step.",
            "Custom",
            [],
            "x = x + 1;",
            BuiltIn: true),
        new(
            "builtin_destroy_self",
            "Destroy this object",
            "Instances",
            "Remove the current Object instance.",
            "InstanceDestroy",
            [new VisualActionParameter("id", "id")],
            "InstanceDestroy(id);",
            BuiltIn: true),
        new(
            "builtin_animation_play",
            "Play animation state",
            "Animation State",
            "Start a named sprite or model animation, blending model poses over the requested time.",
            "AnimationStatePlay",
            [
                new VisualActionParameter("state", "\"Idle\""),
                new VisualActionParameter("loop", "true", VisualActionValueKind.Boolean),
                new VisualActionParameter("blendSeconds", "0.15"),
            ],
            "",
            BuiltIn: true),
        new(
            "builtin_animation_graph",
            "Visual animation graph",
            "Animation State",
            "State graph for Step events: clips, speed, conditions, exit transitions and blending.",
            AnimationGraphSyntax.CommandName,
            [],
            AnimationGraphSyntax.GenerateBody(new AnimationGraphDefinition()),
            BuiltIn: true),
        new(
            "builtin_set_image",
            "Set image",
            "Resources",
            "Assign an Image resource to this Object.",
            "SpriteSet",
            [new VisualActionParameter("name", "", VisualActionValueKind.Asset, ResourceKind.Image)],
            "",
            BuiltIn: true),
        new(
            "builtin_set_model",
            "Set model",
            "Resources",
            "Assign a 3D Model resource to this Object.",
            "ModelSet",
            [new VisualActionParameter("model", "", VisualActionValueKind.Asset, ResourceKind.Model)],
            "",
            BuiltIn: true),
        new(
            "builtin_play_sound",
            "Play sound",
            "Audio",
            "Play an Audio resource and keep its channel handle available to PGSL.",
            "PlaySound",
            [
                new VisualActionParameter("path", "", VisualActionValueKind.Asset, ResourceKind.Audio),
                new VisualActionParameter("volume", "1"),
                new VisualActionParameter("pitch", "1"),
                new VisualActionParameter("loop", "false", VisualActionValueKind.Boolean),
            ],
            "",
            BuiltIn: true),
        new(
            "builtin_set_variable",
            "Set number variable",
            "Variables",
            "Set a persistent numeric PGSL variable using a literal or expression.",
            "VariableSet",
            [
                new VisualActionParameter("name", "\"variableName\""),
                new VisualActionParameter("value", "0"),
            ],
            "",
            BuiltIn: true),
        new(
            "builtin_draw_sprite",
            "Draw image",
            "Drawing 2D",
            "Draw an Image resource at a 2D position.",
            "DrawSprite",
            [
                new VisualActionParameter("spr", "", VisualActionValueKind.Asset, ResourceKind.Image),
                new VisualActionParameter("frame", "image_index"),
                new VisualActionParameter("x", "x"),
                new VisualActionParameter("y", "y"),
            ],
            "",
            BuiltIn: true),
        new(
            "builtin_draw_model",
            "Draw model",
            "Drawing 3D",
            "Draw a 3D Model resource at a world position.",
            "Engine.Rendering.DrawModel3D",
            [
                new VisualActionParameter("modelName", "", VisualActionValueKind.Asset, ResourceKind.Model),
                new VisualActionParameter("x", "x"),
                new VisualActionParameter("y", "y"),
                new VisualActionParameter("z", "z"),
                new VisualActionParameter("scale", "1"),
            ],
            "",
            BuiltIn: true),
        new(
            "builtin_draw_circle",
            "Draw circle",
            "Drawing 2D",
            "Draw a filled or outlined 2D circle.",
            "DrawCircle",
            [
                new VisualActionParameter("x", "x"),
                new VisualActionParameter("y", "y"),
                new VisualActionParameter("radius", "16"),
                new VisualActionParameter("outline", "false", VisualActionValueKind.Boolean),
            ],
            "",
            BuiltIn: true),
        new(
            "builtin_draw_box_3d",
            "Draw box",
            "Drawing 3D",
            "Draw an axis-aligned 3D box with independent dimensions.",
            "DrawBox3D",
            [
                new VisualActionParameter("x", "x"),
                new VisualActionParameter("y", "y"),
                new VisualActionParameter("z", "z"),
                new VisualActionParameter("sx", "1"),
                new VisualActionParameter("sy", "1"),
                new VisualActionParameter("sz", "1"),
            ],
            "",
            BuiltIn: true),
    ];
}

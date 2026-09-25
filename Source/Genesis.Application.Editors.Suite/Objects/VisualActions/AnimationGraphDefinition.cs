using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public sealed class AnimationGraphDefinition
{
    public string Id { get; set; } = "graph_" + Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Animation Graph";
    public string ModelAsset { get; set; } = string.Empty;
    public bool KeepPreviousTransform { get; set; }
    public List<AnimationGraphState> States { get; set; } =
    [
        new() { Id = "idle", Name = "Idle", Clip = "Idle", IsDefault = true },
    ];
    public List<AnimationGraphTransition> Transitions { get; set; } = [];
}

public sealed class AnimationGraphState
{
    public string Id { get; set; } = "state_" + Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "State";
    public string Clip { get; set; } = "Idle";
    public double Speed { get; set; } = 1;
    public bool Loop { get; set; } = true;
    public bool IsDefault { get; set; }

    public override string ToString() => IsDefault ? Name + "  [DEFAULT]" : Name;
}

public sealed class AnimationGraphTransition
{
    public string Id { get; set; } = "transition_" + Guid.NewGuid().ToString("N")[..8];
    public string FromStateId { get; set; } = string.Empty;
    public string ToStateId { get; set; } = string.Empty;
    public string Condition { get; set; } = "true";
    public bool OnAnimationEnd { get; set; }
    public double BlendSeconds { get; set; } = 0.15;

    public override string ToString() => OnAnimationEnd ? "On animation end" : Condition;
}

/// <summary>
/// Serialises an editor graph into a managed action and emits ordinary PGSL. The graph is authoring
/// metadata only: play mode continues to execute the same language and animation commands as code.
/// </summary>
public static class AnimationGraphSyntax
{
    public const string CommandName = "Visual.AnimationGraph";
    private const string MarkerPrefix = "// <animation-graph data=\"";
    private const string MarkerSuffix = "\">";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public static VisualActionTemplate CreateTemplate(AnimationGraphDefinition definition)
    {
        AnimationGraphDefinition normalized = Normalize(Clone(definition));
        return new VisualActionTemplate(
            normalized.Name,
            CommandName,
            "Animation State",
            "Visual animation controller with states, conditions, exit transitions, speed and blending.",
            [],
            GenerateBody(normalized));
    }

    public static string GenerateBody(AnimationGraphDefinition definition)
    {
        AnimationGraphDefinition graph = Normalize(Clone(definition));
        string json = JsonSerializer.Serialize(graph, JsonOptions);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        AnimationGraphState initial = graph.States.First(state => state.IsDefault);
        string key = "__animation_graph_" + Identifier(graph.Id);
        StringBuilder source = new();
        source.Append(MarkerPrefix).Append(encoded).AppendLine(MarkerSuffix);
        source.Append("Engine.Rendering.Models.KeepPreviousTransform = ")
            .Append(graph.KeepPreviousTransform ? "true" : "false").AppendLine(";");
        if (!string.IsNullOrWhiteSpace(graph.ModelAsset))
            source.Append("ModelSet(\"").Append(Escape(graph.ModelAsset)).AppendLine("\");");
        source.Append("if (AnimationStateGetText(\"").Append(key).AppendLine("\") == \"\")");
        source.AppendLine("{");
        EmitPlay(source, key, initial, blendSeconds: 0, indent: "    ");
        source.AppendLine("}");

        bool firstState = true;
        foreach (AnimationGraphState state in graph.States)
        {
            List<AnimationGraphTransition> outgoing = graph.Transitions
                .Where(transition => transition.FromStateId == state.Id)
                .Where(transition => graph.States.Any(candidate => candidate.Id == transition.ToStateId))
                .ToList();
            if (outgoing.Count == 0) continue;
            source.Append(firstState ? "if" : "else if")
                .Append(" (AnimationStateGetText(\"").Append(key).Append("\") == \"")
                .Append(Escape(state.Id)).AppendLine("\")");
            source.AppendLine("{");
            for (int index = 0; index < outgoing.Count; index++)
            {
                AnimationGraphTransition transition = outgoing[index];
                AnimationGraphState target = graph.States.First(candidate => candidate.Id == transition.ToStateId);
                string condition = transition.OnAnimationEnd
                    ? "AnimationStateHasFinished()"
                    : NormalizeCondition(transition.Condition);
                source.Append("    ").Append(index == 0 ? "if" : "else if")
                    .Append(" (").Append(condition).AppendLine(")");
                source.AppendLine("    {");
                EmitPlay(source, key, target, transition.BlendSeconds, "        ");
                source.AppendLine("    }");
            }
            source.AppendLine("}");
            firstState = false;
        }
        source.AppendLine("// </animation-graph>");
        return source.ToString().TrimEnd();
    }

    public static bool TryRead(string? body, out AnimationGraphDefinition definition)
    {
        definition = new AnimationGraphDefinition();
        if (string.IsNullOrWhiteSpace(body)) return false;
        int start = body.IndexOf(MarkerPrefix, StringComparison.Ordinal);
        if (start < 0) return false;
        start += MarkerPrefix.Length;
        int end = body.IndexOf(MarkerSuffix, start, StringComparison.Ordinal);
        if (end <= start) return false;
        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(body[start..end]));
            AnimationGraphDefinition? parsed = JsonSerializer.Deserialize<AnimationGraphDefinition>(json, JsonOptions);
            if (parsed is null) return false;
            definition = Normalize(parsed);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    public static AnimationGraphDefinition Clone(AnimationGraphDefinition definition)
    {
        string json = JsonSerializer.Serialize(definition ?? new AnimationGraphDefinition(), JsonOptions);
        return JsonSerializer.Deserialize<AnimationGraphDefinition>(json, JsonOptions)
               ?? new AnimationGraphDefinition();
    }

    public static AnimationGraphDefinition Normalize(AnimationGraphDefinition graph)
    {
        graph.Id = Identifier(string.IsNullOrWhiteSpace(graph.Id)
            ? "graph_" + Guid.NewGuid().ToString("N")[..10]
            : graph.Id);
        graph.Name = string.IsNullOrWhiteSpace(graph.Name) ? "Animation Graph" : graph.Name.Trim();
        graph.ModelAsset = (graph.ModelAsset ?? string.Empty).Trim().Replace('\\', '/');
        graph.States ??= [];
        graph.Transitions ??= [];
        if (graph.States.Count == 0) graph.States.Add(new AnimationGraphState());
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationGraphState state in graph.States)
        {
            state.Name = string.IsNullOrWhiteSpace(state.Name) ? "State" : state.Name.Trim();
            state.Id = UniqueIdentifier(string.IsNullOrWhiteSpace(state.Id) ? state.Name : state.Id, ids);
            state.Clip = string.IsNullOrWhiteSpace(state.Clip) ? state.Name : state.Clip.Trim();
            if (!double.IsFinite(state.Speed)) state.Speed = 1;
        }
        AnimationGraphState firstDefault = graph.States.FirstOrDefault(state => state.IsDefault)
                                           ?? graph.States[0];
        foreach (AnimationGraphState state in graph.States) state.IsDefault = ReferenceEquals(state, firstDefault);
        graph.Transitions.RemoveAll(transition =>
            !ids.Contains(transition.FromStateId) || !ids.Contains(transition.ToStateId));
        foreach (AnimationGraphTransition transition in graph.Transitions)
        {
            transition.Id = Identifier(string.IsNullOrWhiteSpace(transition.Id)
                ? "transition_" + Guid.NewGuid().ToString("N")[..8]
                : transition.Id);
            transition.Condition = string.IsNullOrWhiteSpace(transition.Condition) ? "true" : transition.Condition.Trim();
            transition.BlendSeconds = double.IsFinite(transition.BlendSeconds)
                ? Math.Max(0, transition.BlendSeconds)
                : 0;
        }
        return graph;
    }

    private static void EmitPlay(
        StringBuilder source,
        string key,
        AnimationGraphState state,
        double blendSeconds,
        string indent)
    {
        source.Append(indent).Append("AnimationStateSetText(\"").Append(key).Append("\", \"")
            .Append(Escape(state.Id)).AppendLine("\");");
        source.Append(indent).Append("AnimationStateSetSpeed(")
            .Append(state.Speed.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(");");
        source.Append(indent).Append("AnimationStatePlay(\"").Append(Escape(state.Clip)).Append("\", ")
            .Append(state.Loop ? "true" : "false").Append(", ")
            .Append(Math.Max(0, blendSeconds).ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(");");
    }

    private static string NormalizeCondition(string? condition) =>
        string.IsNullOrWhiteSpace(condition) ? "false" : condition.Trim();

    private static string Identifier(string value)
    {
        StringBuilder result = new();
        foreach (char character in value ?? string.Empty)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? char.ToLowerInvariant(character) : '_');
        string normalized = result.ToString().Trim('_');
        return normalized.Length == 0 ? "state" : normalized;
    }

    private static string UniqueIdentifier(string value, HashSet<string> existing)
    {
        string basis = Identifier(value);
        string candidate = basis;
        int suffix = 2;
        while (!existing.Add(candidate)) candidate = basis + "_" + suffix++;
        return candidate;
    }

    private static string Escape(string value) => (value ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}

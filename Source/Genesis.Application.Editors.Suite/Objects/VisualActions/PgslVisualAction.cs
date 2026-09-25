using System.Text;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public enum VisualActionPlacement
{
    Top,
    Cursor,
    Bottom,
}

public enum VisualActionValueKind
{
    Expression,
    Boolean,
    Asset,
}

public sealed record VisualActionParameter(
    string Name,
    string Value,
    VisualActionValueKind Kind = VisualActionValueKind.Expression,
    ResourceKind? AssetKind = null,
    IReadOnlyList<string>? Choices = null,
    BlueprintValueType? DataType = null,
    string? UnlinkedValue = null);

public sealed record VisualActionCommand(
    string Name,
    string Signature,
    string Category,
    string Description,
    IReadOnlyList<VisualActionParameter> Parameters);

public sealed record VisualActionTemplate(
    string Name,
    string CommandName,
    string Category,
    string Description,
    IReadOnlyList<VisualActionParameter> Parameters,
    string Body = "",
    string ResultVariable = "",
    string DetachedChain = "")
{
    public string BuildBody()
    {
        if (!string.IsNullOrWhiteSpace(Body))
        {
            string body = Body;
            foreach (var parameter in Parameters) body = body.Replace("{{" + parameter.Name + "}}", VisualActionSyntax.SerializeValue(parameter), StringComparison.Ordinal);
            return body.Trim();
        }
        string arguments = string.Join(", ", Parameters.Select(VisualActionSyntax.SerializeValue));
        return (string.IsNullOrWhiteSpace(ResultVariable) ? "" : ResultVariable + " = ") + $"{CommandName}({arguments});";
    }
}

public sealed record VisualActionBlock(
    string Id,
    string Name,
    string CommandName,
    string Category,
    string Description,
    IReadOnlyList<VisualActionParameter> Parameters,
    string Body,
    int SourceStart,
    int SourceLength,
    bool Managed = true,
    string? FlowId = null,
    string? FlowBranch = null,
    string ResultVariable = "",
    string TemplateBody = "",
    string DetachedChain = "")
{
    public VisualActionTemplate ToTemplate() =>
        new(Name, CommandName, Category, Description, Parameters, TemplateBody.Length > 0 ? TemplateBody : Body, ResultVariable, DetachedChain);
}

public sealed record VisualActionFlowGroup(
    string Id,
    string Name,
    string Condition,
    int SourceStart,
    int SourceLength,
    int ThenStart,
    int ThenLength,
    int ElseStart,
    int ElseLength,
    string? ParentFlowId = null,
    string? ParentBranch = null)
{
    public bool HasElse => ElseStart >= 0;
}

/// <summary>Implemented PGSL/Engine commands translated into designer-facing action definitions.</summary>
public static class VisualActionCatalog
{
    private static readonly Lazy<IReadOnlyList<VisualActionCommand>> Commands = new(BuildCommands);

    public static IReadOnlyList<VisualActionCommand> GetCommands() => Commands.Value;

    private static IReadOnlyList<VisualActionCommand> BuildCommands()
    {
        PgslCommands.WarmRegistry();
        List<VisualActionCommand> commands = [];
        commands.AddRange(PgslCommandRegistry.GetFullCatalog()
            .Where(command => command.IsImplemented && !command.IsProperty && command.Signature.Contains('('))
            .Select(command => Create(
                command.QualifiedName,
                command.Signature,
                command.Category,
                command.Description)));
        commands.AddRange(EngineCommandRegistry.GetCatalog()
            .Where(command => command.IsImplemented && command.Signature.Contains('('))
            .Select(command => Create(
                "Engine." + command.Name,
                command.Signature,
                "Engine · " + command.Category,
                command.Description)));
        return commands
            .GroupBy(command => command.Name + "|" + command.Signature, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(command => command.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static VisualActionCommand? Find(string commandName)
    {
        VisualActionCommand? exact = GetCommands().FirstOrDefault(command =>
            string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));
        return exact ?? GetCommands().FirstOrDefault(command =>
            string.Equals(ShortName(command.Name), ShortName(commandName), StringComparison.OrdinalIgnoreCase));
    }

    private static VisualActionCommand Create(
        string commandName,
        string signature,
        string? category,
        string? description)
    {
        List<VisualActionParameter> parameters = [];
        var method = typeof(PgslCommands).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(method => string.Equals(method.Name, ShortName(commandName), StringComparison.OrdinalIgnoreCase));
        foreach ((string name, string defaultValue) in VisualActionSyntax.ParseSignature(signature))
        {
            ResourceKind? assetKind = InferAssetKind(name, category ?? string.Empty);
            bool boolean = IsBoolean(name, defaultValue);
            parameters.Add(new VisualActionParameter(
                name,
                DefaultValue(name, defaultValue, assetKind, boolean),
                assetKind.HasValue
                    ? VisualActionValueKind.Asset
                    : boolean ? VisualActionValueKind.Boolean : VisualActionValueKind.Expression,
                assetKind,
                boolean ? ["true", "false"] : null,
                BlueprintActions.FromClrType(method?.GetParameters().FirstOrDefault(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))?.ParameterType)));
        }
        return new VisualActionCommand(
            commandName,
            signature,
            string.IsNullOrWhiteSpace(category) ? "General" : category,
            description ?? string.Empty,
            parameters);
    }

    private static ResourceKind? InferAssetKind(string parameter, string category)
    {
        string name = parameter.ToLowerInvariant();
        if (name is "spr" or "sprite" or "spriteindex" or "image" or "texture"
            || name == "name" && category.Contains("Sprite", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceKind.Image;
        }
        if (name.Contains("model", StringComparison.Ordinal)) return ResourceKind.Model;
        if (name.Contains("shader", StringComparison.Ordinal)) return ResourceKind.Shader;
        if (name.Contains("particle", StringComparison.Ordinal)) return ResourceKind.Particle;
        if (name is "obj" or "object" or "objectname" or "prefab") return ResourceKind.GameObject;
        if (name.Contains("room", StringComparison.Ordinal)) return ResourceKind.Room;
        if (name.Contains("terrain", StringComparison.Ordinal)) return ResourceKind.Terrain;
        if (name.Contains("route", StringComparison.Ordinal)
            || name.Contains("pathing", StringComparison.Ordinal)
            || name == "path" && category.Contains("Navigation", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceKind.Pathing;
        }
        if (name.Contains("sound", StringComparison.Ordinal)
            || name.Contains("audio", StringComparison.Ordinal)
            || name == "path" && category.Contains("Audio", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceKind.Audio;
        }
        return null;
    }

    private static bool IsBoolean(string name, string defaultValue)
    {
        if (defaultValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            || defaultValue.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string lower = name.ToLowerInvariant();
        return lower is "loop" or "enabled" or "visible" or "solid" or "filled" or "outline"
            or "captured" or "relative" or "autoplay" or "spatial" or "playing";
    }

    private static string DefaultValue(
        string name,
        string declaredDefault,
        ResourceKind? assetKind,
        bool boolean)
    {
        if (!string.IsNullOrWhiteSpace(declaredDefault)) return declaredDefault;
        if (assetKind.HasValue) return string.Empty;
        if (boolean) return "false";
        string lower = name.ToLowerInvariant();
        if (lower is "x" or "x1" or "xfrom") return "x";
        if (lower is "y" or "y1" or "yfrom") return "y";
        if (lower is "z" or "z1" or "zfrom") return "z";
        if (lower is "frame" or "imageindex" or "image_index") return "image_index";
        if (lower.Contains("alpha", StringComparison.Ordinal)
            || lower.Contains("volume", StringComparison.Ordinal)
            || lower.Contains("pitch", StringComparison.Ordinal)
            || lower.Contains("scale", StringComparison.Ordinal))
        {
            return "1";
        }
        if (lower is "color" or "colour" or "col" or "tint") return "ColourWhite";
        if (lower.Contains("text", StringComparison.Ordinal)
            || lower.Contains("name", StringComparison.Ordinal)
            || lower is "key" or "button")
        {
            return "\"\"";
        }
        return "0";
    }

    private static string ShortName(string command) =>
        command.Contains('.') ? command[(command.LastIndexOf('.') + 1)..] : command;
}

/// <summary>
/// Marker-based action round-tripping. Only explicit action regions are reordered or rewritten;
/// ordinary hand-written PGSL remains byte-for-byte outside those regions.
/// </summary>
public static partial class VisualActionSyntax
{
    [GeneratedRegex(
        @"(?ms)^[ \t]*//[ \t]*<action:(?<kind>[\w-]+)(?<attributes>[^>]*)>[ \t]*\r?\n(?<body>.*?)^[ \t]*//[ \t]*</action>[ \t]*(?:\r?\n|$)")]
    private static partial Regex ActionBlockPattern();

    [GeneratedRegex("(?<name>[A-Za-z_][\\w-]*)[ \\t]*=[ \\t]*\"(?<value>[^\"]*)\"")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"(?s)^\s*(?:(?:var\s+)?(?<result>[A-Za-z_]\w*)\s*=\s*)?(?<command>[A-Za-z_][\w.]*)\s*\((?<arguments>.*)\)\s*;\s*$")]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"(?m)^[ \t]*(?<body>(?:(?:var\s+)?[A-Za-z_]\w*\s*=\s*)?[A-Za-z_][\w.]*[ \t]*\([^;\r\n]*\)[ \t]*;)[ \t]*(?:\r?\n|$)")]
    private static partial Regex StandaloneCommandPattern();

    [GeneratedRegex(
        @"(?m)^[ \t]*//[ \t]*(?:(?<condition><condition(?<attributes>[^>]*)>)|(?<conditionEnd></condition>)|(?<branch><branch:(?<branchName>then|else)>)|(?<branchEnd></branch>))[ \t]*(?:\r?\n|$)")]
    private static partial Regex FlowMarkerPattern();

    public static IReadOnlyList<VisualActionBlock> Parse(string source)
    {
        source ??= string.Empty;
        List<VisualActionBlock> blocks = [];
        foreach (Match match in ActionBlockPattern().Matches(source))
        {
            Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in AttributePattern().Matches(match.Groups["attributes"].Value))
            {
                attributes[attribute.Groups["name"].Value] =
                    DecodeAttribute(attribute.Groups["value"].Value);
            }
            string body = match.Groups["body"].Value.Trim();
            string detachedChain = attributes.GetValueOrDefault("detached") ?? "";
            if (detachedChain.Length > 0)
            {
                var detached = Regex.Match(body, @"\Aif\s*\(false\)\s*\{(?<body>[\s\S]*)\}\s*\z");
                if (detached.Success) body = detached.Groups["body"].Value.Trim();
                else detachedChain = ""; // Handwritten edits to the guard remain authoritative.
            }
            Match commandMatch = CommandPattern().Match(body);
            string commandName = attributes.GetValueOrDefault("command")
                                 ?? (commandMatch.Success ? commandMatch.Groups["command"].Value : string.Empty);
            VisualActionCommand? command = VisualActionCatalog.Find(commandName);
            IReadOnlyList<VisualActionParameter> parameters = commandMatch.Success
                ? MergeParameters(command, SplitArguments(commandMatch.Groups["arguments"].Value))
                : [];
            string templateBody = "";
            if (attributes.TryGetValue("template", out string? encodedTemplate) && attributes.TryGetValue("parameters", out string? encodedParameters))
            {
                try
                {
                    string template = Encoding.UTF8.GetString(Convert.FromBase64String(encodedTemplate));
                    var declared = System.Text.Json.JsonSerializer.Deserialize<List<VisualActionParameter>>(Encoding.UTF8.GetString(Convert.FromBase64String(encodedParameters))) ?? [];
                    if (TryReadTemplate(template, body, declared, out var parsed)) { parameters = parsed; templateBody = template; }
                }
                catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException) { }
            }
            string id = attributes.GetValueOrDefault("id") ?? "act_" + Guid.NewGuid().ToString("N")[..10];
            if (attributes.TryGetValue("inputDefaults", out string? defaults))
            {
                try
                {
                    var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(Convert.FromBase64String(defaults)));
                    parameters = parameters.Select(parameter => parameter with { UnlinkedValue = values?.GetValueOrDefault(parameter.Name) }).ToArray();
                }
                catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException) { }
            }
            string name = attributes.GetValueOrDefault("name")
                          ?? command?.Name
                          ?? Humanize(match.Groups["kind"].Value);
            blocks.Add(new VisualActionBlock(
                id,
                name,
                commandName,
                attributes.GetValueOrDefault("category") ?? command?.Category ?? "Custom",
                attributes.GetValueOrDefault("description") ?? command?.Description ?? string.Empty,
                parameters,
                commandMatch.Success || templateBody.Length > 0 ? string.Empty : body,
                match.Index,
                match.Length,
                Managed: true,
                ResultVariable: commandMatch.Groups["result"].Value,
                TemplateBody: templateBody,
                DetachedChain: detachedChain));
        }

        // Safe, one-line command statements can be represented immediately even when they predate
        // the builder. They are converted to an explicit managed region only if the designer edits
        // them; unrelated assignments, control flow, comments, and multiline code stay raw.
        foreach (Match match in StandaloneCommandPattern().Matches(source))
        {
            if (blocks.Any(block => match.Index >= block.SourceStart
                                    && match.Index < block.SourceStart + block.SourceLength))
            {
                continue;
            }
            string body = match.Groups["body"].Value.Trim();
            Match commandMatch = CommandPattern().Match(body);
            if (!commandMatch.Success) continue;
            string commandName = commandMatch.Groups["command"].Value;
            VisualActionCommand? command = VisualActionCatalog.Find(commandName);
            if (command is null) continue;
            blocks.Add(new VisualActionBlock(
                $"implicit_{match.Index:X8}",
                Humanize(command.Name.Contains('.') ? command.Name[(command.Name.LastIndexOf('.') + 1)..] : command.Name),
                command.Name,
                command.Category,
                command.Description,
                MergeParameters(command, SplitArguments(commandMatch.Groups["arguments"].Value)),
                string.Empty,
                match.Index,
                match.Length,
                Managed: false, ResultVariable: commandMatch.Groups["result"].Value));
        }
        IReadOnlyList<VisualActionFlowGroup> flows = ParseFlows(source);
        // Keep custom source visible and editable without translating or discarding it.
        var occupied = blocks.Select(block => (Start: block.SourceStart, End: block.SourceStart + block.SourceLength))
            .Concat(flows.Select(flow => (Start: flow.SourceStart, End: flow.SourceStart + flow.SourceLength)))
            .Concat(Regex.Matches(source, @"(?m)^// @blueprint [^\r\n]*(?:\r?\n|$)").Select(match => (Start: match.Index, End: match.Index + match.Length)))
            .OrderBy(range => range.Start).ToArray();
        int cursor = 0;
        Dictionary<string, int> rawOccurrences = [];
        foreach (var range in occupied.Append((Start: source.Length, End: source.Length)))
        {
            if (range.Start > cursor)
            {
                string raw = source[cursor..range.Start];
                if (!string.IsNullOrWhiteSpace(Regex.Replace(raw, @"(?m)^\s*//[^\r\n]*", "")))
                {
                    // Source offsets change when a preceding node is moved. Keep a custom
                    // block's layout and wires attached to its content instead.
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim())))[..12];
                    int occurrence = rawOccurrences.GetValueOrDefault(hash); rawOccurrences[hash] = occurrence + 1;
                    blocks.Add(new VisualActionBlock($"raw_{hash}_{occurrence}", "Custom PGSL", "PGSL", "Code", "Custom source is preserved", [], raw.Trim(), cursor, range.Start - cursor, Managed: false));
                }
            }
            cursor = Math.Max(cursor, range.End);
        }
        return blocks.OrderBy(block => block.SourceStart)
            .Select(block => AttachFlow(block, flows))
            .ToArray();
    }

    public static IReadOnlyList<VisualActionFlowGroup> ParseFlows(string source)
    {
        source ??= string.Empty;
        Stack<FlowParseState> stack = new();
        List<VisualActionFlowGroup> raw = [];
        foreach (Match marker in FlowMarkerPattern().Matches(source))
        {
            if (marker.Groups["condition"].Success)
            {
                Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
                foreach (Match attribute in AttributePattern().Matches(marker.Groups["attributes"].Value))
                    attributes[attribute.Groups["name"].Value] = DecodeAttribute(attribute.Groups["value"].Value);
                stack.Push(new FlowParseState
                {
                    Id = attributes.GetValueOrDefault("id") ?? "flow_" + Guid.NewGuid().ToString("N")[..10],
                    Name = attributes.GetValueOrDefault("name") ?? "If / Else",
                    Condition = attributes.GetValueOrDefault("expression") ?? "true",
                    Start = marker.Index,
                });
                continue;
            }

            if (stack.Count == 0) continue;
            FlowParseState state = stack.Peek();
            if (marker.Groups["branch"].Success)
            {
                state.ActiveBranch = marker.Groups["branchName"].Value.ToLowerInvariant();
                state.ActiveBranchStart = marker.Index + marker.Length;
            }
            else if (marker.Groups["branchEnd"].Success && state.ActiveBranchStart >= 0)
            {
                if (state.ActiveBranch == "then")
                {
                    state.ThenStart = state.ActiveBranchStart;
                    state.ThenLength = marker.Index - state.ActiveBranchStart;
                }
                else
                {
                    state.ElseStart = state.ActiveBranchStart;
                    state.ElseLength = marker.Index - state.ActiveBranchStart;
                }
                state.ActiveBranch = string.Empty;
                state.ActiveBranchStart = -1;
            }
            else if (marker.Groups["conditionEnd"].Success)
            {
                stack.Pop();
                if (state.ThenStart >= 0)
                    raw.Add(new VisualActionFlowGroup(state.Id, state.Name, state.Condition,
                        state.Start, marker.Index + marker.Length - state.Start,
                        state.ThenStart, state.ThenLength, state.ElseStart, state.ElseLength));
            }
        }

        return raw.OrderBy(flow => flow.SourceStart)
            .Select(flow => AttachParentFlow(flow, raw))
            .ToArray();
    }

    public static string InsertConditionAtActionIndex(
        string source,
        string condition,
        int actionIndex,
        bool includeElse = true,
        string? name = null)
    {
        source ??= string.Empty;
        IReadOnlyList<VisualActionBlock> blocks = Parse(source);
        int target = Math.Clamp(actionIndex, 0, blocks.Count);
        int insertion = target < blocks.Count
            ? blocks[target].FlowId is { } flowId
                ? ParseFlows(source).FirstOrDefault(flow => flow.Id == flowId)?.SourceStart ?? blocks[target].SourceStart
                : blocks[target].SourceStart
            : source.Length;
        string flow = CreateCondition(condition, includeElse, name);
        if (insertion > 0 && !EndsWithNewLine(source[..insertion])) flow = Environment.NewLine + flow;
        if (insertion < source.Length && !StartsWithNewLine(source[insertion..])) flow += Environment.NewLine;
        return source.Insert(insertion, flow);
    }

    public static string InsertIntoBranch(
        string source,
        string flowId,
        string branch,
        VisualActionTemplate template)
    {
        source ??= string.Empty;
        VisualActionFlowGroup? flow = ParseFlows(source).FirstOrDefault(candidate => candidate.Id == flowId);
        if (flow is null) return source;
        bool useElse = branch.Equals("else", StringComparison.OrdinalIgnoreCase);
        int start = useElse ? flow.ElseStart : flow.ThenStart;
        int length = useElse ? flow.ElseLength : flow.ThenLength;
        if (start < 0) return source;
        string block = CreateBlock(template);
        int insertion = start + length;
        if (insertion > start && !EndsWithNewLine(source[start..insertion])) block = Environment.NewLine + block;
        return source.Insert(insertion, block);
    }

    public static string InsertConditionIntoBranch(
        string source,
        string flowId,
        string branch,
        string condition,
        bool includeElse = true)
    {
        source ??= string.Empty;
        VisualActionFlowGroup? flow = ParseFlows(source).FirstOrDefault(candidate => candidate.Id == flowId);
        if (flow is null) return source;
        bool useElse = branch.Equals("else", StringComparison.OrdinalIgnoreCase);
        int start = useElse ? flow.ElseStart : flow.ThenStart;
        int length = useElse ? flow.ElseLength : flow.ThenLength;
        if (start < 0) return source;
        string nested = CreateCondition(condition, includeElse, "Nested If / Else");
        int insertion = start + length;
        if (insertion > start && !EndsWithNewLine(source[start..insertion])) nested = Environment.NewLine + nested;
        return source.Insert(insertion, nested);
    }

    public static string SetCondition(string source, string flowId, string condition)
    {
        source ??= string.Empty;
        VisualActionFlowGroup? flow = ParseFlows(source).FirstOrDefault(candidate => candidate.Id == flowId);
        if (flow is null) return source;
        string segment = source.Substring(flow.SourceStart, flow.SourceLength);
        int headerEnd = segment.IndexOf('\n');
        if (headerEnd < 0) return source;
        segment = CreateConditionHeader(flow.Id, flow.Name, condition) + segment[(headerEnd + 1)..];
        segment = Regex.Replace(segment, @"(?m)^[ \t]*if[ \t]*\([^\r\n]*\)",
            "if (" + NormalizeCondition(condition) + ")", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return source.Remove(flow.SourceStart, flow.SourceLength).Insert(flow.SourceStart, segment);
    }

    private static string CreateCondition(string condition, bool includeElse, string? name)
    {
        string id = "flow_" + Guid.NewGuid().ToString("N")[..10];
        string header = CreateConditionHeader(id, string.IsNullOrWhiteSpace(name) ? "If / Else" : name.Trim(), condition);
        string result = header
            + $"if ({NormalizeCondition(condition)}){Environment.NewLine}{{{Environment.NewLine}"
            + $"// <branch:then>{Environment.NewLine}// </branch>{Environment.NewLine}}}";
        if (includeElse)
        {
            result += $"{Environment.NewLine}else{Environment.NewLine}{{{Environment.NewLine}"
                + $"// <branch:else>{Environment.NewLine}// </branch>{Environment.NewLine}}}";
        }
        return result + Environment.NewLine + "// </condition>" + Environment.NewLine;
    }

    private static string CreateConditionHeader(string id, string name, string condition) =>
        $"// <condition id=\"{EncodeAttribute(id)}\" name=\"{EncodeAttribute(name)}\" "
        + $"expression=\"{EncodeAttribute(NormalizeCondition(condition))}\">{Environment.NewLine}";

    private static string NormalizeCondition(string? condition) =>
        string.IsNullOrWhiteSpace(condition) ? "true" : condition.Trim();

    private static VisualActionBlock AttachFlow(
        VisualActionBlock block,
        IReadOnlyList<VisualActionFlowGroup> flows)
    {
        foreach (VisualActionFlowGroup flow in flows.OrderBy(flow => flow.SourceLength))
        {
            if (block.SourceStart >= flow.ThenStart && block.SourceStart < flow.ThenStart + flow.ThenLength)
                return block with { FlowId = flow.Id, FlowBranch = "then" };
            if (flow.HasElse && block.SourceStart >= flow.ElseStart && block.SourceStart < flow.ElseStart + flow.ElseLength)
                return block with { FlowId = flow.Id, FlowBranch = "else" };
        }
        return block;
    }

    private static VisualActionFlowGroup AttachParentFlow(
        VisualActionFlowGroup flow,
        IReadOnlyList<VisualActionFlowGroup> flows)
    {
        VisualActionFlowGroup? parent = flows
            .Where(candidate => candidate.Id != flow.Id
                && flow.SourceStart >= candidate.SourceStart
                && flow.SourceStart < candidate.SourceStart + candidate.SourceLength)
            .OrderBy(candidate => candidate.SourceLength)
            .FirstOrDefault();
        if (parent is null) return flow;
        string? branch = flow.SourceStart >= parent.ThenStart && flow.SourceStart < parent.ThenStart + parent.ThenLength
            ? "then"
            : parent.HasElse && flow.SourceStart >= parent.ElseStart && flow.SourceStart < parent.ElseStart + parent.ElseLength
                ? "else"
                : null;
        return flow with { ParentFlowId = parent.Id, ParentBranch = branch };
    }

    private sealed class FlowParseState
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Condition { get; init; } = string.Empty;
        public int Start { get; init; }
        public string ActiveBranch { get; set; } = string.Empty;
        public int ActiveBranchStart { get; set; } = -1;
        public int ThenStart { get; set; } = -1;
        public int ThenLength { get; set; }
        public int ElseStart { get; set; } = -1;
        public int ElseLength { get; set; }
    }

    private static bool TryReadTemplate(string template, string body, IReadOnlyList<VisualActionParameter> declared, out IReadOnlyList<VisualActionParameter> parameters)
    {
        string pattern = Regex.Escape(template.Trim());
        for (int i = 0; i < declared.Count; i++)
        {
            string token = Regex.Escape("{{" + declared[i].Name + "}}");
            int first = pattern.IndexOf(token, StringComparison.Ordinal);
            if (first >= 0) pattern = pattern[..first] + $"(?<p{i}>.*?)" + pattern[(first + token.Length)..];
            pattern = pattern.Replace(token, $"\\k<p{i}>", StringComparison.Ordinal);
        }
        Match match = Regex.Match(body.Trim(), "^" + pattern + "$", RegexOptions.Singleline);
        parameters = declared.Select((parameter, index) => match.Groups[$"p{index}"].Success
            ? parameter with { Value = parameter.Kind == VisualActionValueKind.Asset ? Unquote(match.Groups[$"p{index}"].Value.Trim()) : match.Groups[$"p{index}"].Value.Trim() } : parameter).ToArray();
        return match.Success;
    }

    public static string CreateBlock(VisualActionTemplate template, string? id = null)
    {
        string actionId = string.IsNullOrWhiteSpace(id)
            ? "act_" + Guid.NewGuid().ToString("N")[..10]
            : id;
        string slug = Regex.Replace(template.CommandName, @"[^A-Za-z0-9]+", "_").Trim('_').ToLowerInvariant();
        if (slug.Length == 0) slug = "custom";
        string binding = string.IsNullOrWhiteSpace(template.Body) || template.Parameters.Count == 0 ? "" :
            $" template=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(template.Body))}\" parameters=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(template.Parameters)))}\"";
        var defaults = template.Parameters.Where(parameter => parameter.UnlinkedValue is not null).ToDictionary(parameter => parameter.Name, parameter => parameter.UnlinkedValue!);
        if (defaults.Count > 0) binding += $" inputDefaults=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(defaults)))}\"";
        string body = template.BuildBody().Trim();
        if (template.DetachedChain.Length > 0)
        {
            binding += $" detached=\"{EncodeAttribute(template.DetachedChain)}\"";
            body = "if (false) {" + Environment.NewLine + body + Environment.NewLine + "}";
        }
        return $"// <action:{slug} id=\"{EncodeAttribute(actionId)}\" name=\"{EncodeAttribute(template.Name)}\" "
               + $"command=\"{EncodeAttribute(template.CommandName)}\" category=\"{EncodeAttribute(template.Category)}\" "
               + $"description=\"{EncodeAttribute(template.Description)}\"{binding}>{Environment.NewLine}"
               + body + Environment.NewLine
               + $"// </action>{Environment.NewLine}";
    }

    public static string Insert(
        string source,
        VisualActionTemplate template,
        VisualActionPlacement placement,
        int caret = 0)
    {
        source ??= string.Empty;
        int index = placement switch
        {
            VisualActionPlacement.Top => 0,
            VisualActionPlacement.Cursor => Math.Clamp(caret, 0, source.Length),
            _ => source.Length,
        };
        index = placement == VisualActionPlacement.Cursor ? LineBoundary(source, index) : index;
        string block = CreateBlock(template);
        string before = source[..index];
        string after = source[index..];
        if (before.Length > 0 && !EndsWithNewLine(before)) before += Environment.NewLine;
        if (before.Length > 0 && !before.EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal))
            before += Environment.NewLine;
        if (after.Length > 0 && !StartsWithNewLine(after)) block += Environment.NewLine;
        return before + block + after;
    }

    public static string Replace(string source, VisualActionBlock block, VisualActionTemplate replacement) =>
        source[..block.SourceStart]
        + CreateBlock(replacement, block.Id)
        + source[(block.SourceStart + block.SourceLength)..];

    public static string InsertAtActionIndex(
        string source,
        VisualActionTemplate template,
        int actionIndex)
    {
        source ??= string.Empty;
        IReadOnlyList<VisualActionBlock> blocks = Parse(source);
        int target = Math.Clamp(actionIndex, 0, blocks.Count);
        int insertion = target < blocks.Count
            ? blocks[target].SourceStart
            : blocks.Count > 0
                ? blocks[^1].SourceStart + blocks[^1].SourceLength
                : source.Length;
        string block = CreateBlock(template);
        if (insertion > 0 && !EndsWithNewLine(source[..insertion])) block = Environment.NewLine + block;
        if (insertion < source.Length && !StartsWithNewLine(source[insertion..])) block += Environment.NewLine;
        return source.Insert(insertion, block);
    }

    public static string Remove(string source, VisualActionBlock block) =>
        source.Remove(block.SourceStart, block.SourceLength);

    public static string Move(string source, string blockId, int finalIndex)
    {
        List<VisualActionBlock> original = [.. Parse(source)];
        VisualActionBlock? moving = original.FirstOrDefault(block => block.Id == blockId);
        if (moving is null) return source;
        string blockText = source.Substring(moving.SourceStart, moving.SourceLength);
        string without = source.Remove(moving.SourceStart, moving.SourceLength);
        List<VisualActionBlock> remaining = [.. Parse(without)];
        int target = Math.Clamp(finalIndex, 0, remaining.Count);
        int insertion = target == remaining.Count
            ? remaining.Count == 0
                ? without.Length
                : remaining[^1].SourceStart + remaining[^1].SourceLength
            : remaining[target].SourceStart;
        if (insertion > 0 && !EndsWithNewLine(without[..insertion])) blockText = Environment.NewLine + blockText;
        return without.Insert(insertion, blockText);
    }

    public static string MoveWithinBranch(string source, string blockId, int finalBranchIndex)
    {
        source ??= string.Empty;
        VisualActionBlock? moving = Parse(source).FirstOrDefault(block => block.Id == blockId);
        if (moving?.FlowId is null || moving.FlowBranch is null) return source;
        string blockText = source.Substring(moving.SourceStart, moving.SourceLength);
        string without = source.Remove(moving.SourceStart, moving.SourceLength);
        IReadOnlyList<VisualActionBlock> remaining = Parse(without)
            .Where(block => block.FlowId == moving.FlowId && block.FlowBranch == moving.FlowBranch)
            .OrderBy(block => block.SourceStart)
            .ToArray();
        int target = Math.Clamp(finalBranchIndex, 0, remaining.Count);
        int insertion;
        if (target < remaining.Count)
        {
            insertion = remaining[target].SourceStart;
        }
        else
        {
            VisualActionFlowGroup? flow = ParseFlows(without).FirstOrDefault(candidate => candidate.Id == moving.FlowId);
            if (flow is null) return source;
            insertion = moving.FlowBranch == "else"
                ? flow.ElseStart + flow.ElseLength
                : flow.ThenStart + flow.ThenLength;
        }
        if (insertion > 0 && !EndsWithNewLine(without[..insertion])) blockText = Environment.NewLine + blockText;
        return without.Insert(insertion, blockText);
    }

    public static string SerializeValue(VisualActionParameter parameter)
    {
        string value = parameter.Value?.Trim() ?? string.Empty;
        if (parameter.Kind != VisualActionValueKind.Asset) return value.Length == 0 ? "null" : value;
        if (value.Length == 0) return "\"\"";
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value;
        return "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
    }

    public static IReadOnlyList<(string Name, string DefaultValue)> ParseSignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return [];
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open < 0 || close <= open) return [];
        List<(string Name, string DefaultValue)> result = [];
        foreach (string raw in SplitArguments(signature[(open + 1)..close]))
        {
            string parameter = raw.Trim();
            if (parameter.Length == 0 || parameter == "...") continue;
            int equals = parameter.IndexOf('=');
            string name = equals >= 0 ? parameter[..equals].Trim() : parameter;
            string defaultValue = equals >= 0 ? parameter[(equals + 1)..].Trim() : string.Empty;
            int space = name.LastIndexOf(' ');
            if (space >= 0) name = name[(space + 1)..];
            name = Regex.Replace(name, @"[^A-Za-z0-9_]", string.Empty);
            if (name.Length > 0) result.Add((name, defaultValue));
        }
        return result;
    }

    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        List<string> result = [];
        StringBuilder current = new();
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        foreach (char character in arguments)
        {
            if (quote != '\0')
            {
                current.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\"' or '\'')
            {
                quote = character;
                current.Append(character);
                continue;
            }
            if (character is '(' or '[' or '{') depth++;
            else if (character is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            if (character == ',' && depth == 0)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        if (current.Length > 0) result.Add(current.ToString().Trim());
        return result;
    }

    private static IReadOnlyList<VisualActionParameter> MergeParameters(
        VisualActionCommand? command,
        IReadOnlyList<string> values)
    {
        List<VisualActionParameter> result = [];
        int count = Math.Max(values.Count, command?.Parameters.Count ?? 0);
        for (int index = 0; index < count; index++)
        {
            VisualActionParameter? declared = command?.Parameters.ElementAtOrDefault(index);
            string value = index < values.Count
                ? values[index]
                : declared?.Value ?? string.Empty;
            if (declared?.Kind == VisualActionValueKind.Asset) value = Unquote(value);
            result.Add(declared is null
                ? new VisualActionParameter($"Argument {index + 1}", value)
                : declared with { Value = value });
        }
        return result;
    }

    private static int LineBoundary(string source, int caret)
    {
        if (caret <= 0 || caret >= source.Length) return caret;
        int end = source.IndexOf('\n', caret);
        return end < 0 ? source.Length : end + 1;
    }

    private static bool EndsWithNewLine(string value) => value.EndsWith('\n') || value.EndsWith('\r');
    private static bool StartsWithNewLine(string value) => value.StartsWith('\n') || value.StartsWith('\r');
    private static string EncodeAttribute(string value) =>
        (value ?? string.Empty).Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string DecodeAttribute(string value) =>
        (value ?? string.Empty).Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
        ? value[1..^1].Replace("\\\"", "\"")
        : value;
    private static string Humanize(string value) => Regex.Replace(value.Replace('-', ' '), "([a-z0-9])([A-Z])", "$1 $2");
}

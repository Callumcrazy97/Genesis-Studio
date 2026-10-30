using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// What a command did: whether it worked, one sentence saying what happened, its data, the
/// project files it changed, and the problems the project's checks found afterwards.
/// </summary>
public sealed class AutomationResult
{
    public bool Ok { get; init; } = true;

    public string Message { get; init; } = string.Empty;

    public JsonNode? Data { get; init; }

    /// <summary>Project-relative paths the command created, changed, moved or removed.</summary>
    public List<string> Changed { get; } = [];

    /// <summary>What the project check and the strict script check found after a change.</summary>
    public List<AutomationIssue> Issues { get; } = [];

    public static AutomationResult Success(string message, JsonNode? data = null, params string[] changed)
    {
        var result = new AutomationResult { Message = message, Data = data };
        result.Changed.AddRange(changed.Where(path => !string.IsNullOrWhiteSpace(path)));
        return result;
    }

    public static AutomationResult Failure(string message, JsonNode? data = null) =>
        new() { Ok = false, Message = message, Data = data };

    /// <summary>
    /// This result with what the command moved to the trash to make room for its own resource: said
    /// in the message, among the paths changed and (for object data) as <c>replaced</c>.
    /// </summary>
    public AutomationResult WithReplaced(string sentence, IReadOnlyList<ReplacedResource> replaced)
    {
        var result = new AutomationResult { Ok = Ok, Message = Message.Length == 0 ? sentence : Message + " " + sentence, Data = Data };
        result.Changed.AddRange(Changed);
        result.Changed.AddRange(replaced.Select(r => r.File).Where(path => !Changed.Contains(path, StringComparer.OrdinalIgnoreCase)));
        result.Issues.AddRange(Issues);
        if (Data is JsonObject data) data["replaced"] = new JsonArray([.. replaced.Select(r => (JsonNode?)r.ToJson())]);
        return result;
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["ok"] = Ok,
            ["message"] = Message,
        };
        if (Data is not null) json["data"] = Data.DeepClone();
        if (Changed.Count > 0) json["changed"] = new JsonArray([.. Changed.Select(path => (JsonNode?)path)]);
        if (Issues.Count > 0)
        {
            json["issues"] = new JsonArray([.. Issues.Select(issue => (JsonNode?)issue.ToJson())]);
        }
        return json;
    }
}

/// <summary>One problem a check found: an error or a warning, which check, where (path and line when known).</summary>
public sealed record AutomationIssue(string Severity, string Check, string Message, string? Path = null, int? Line = null, int? Column = null)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["severity"] = Severity, ["check"] = Check, ["message"] = Message };
        if (Path is not null) json["path"] = Path;
        if (Line is not null) json["line"] = Line;
        if (Column is not null) json["column"] = Column;
        return json;
    }
}

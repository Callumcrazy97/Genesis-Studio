using System.Text.Json;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// Studio's automation layer without a window: <c>Genesis Application.exe --automation ...</c>.
/// <list type="bullet">
/// <item><c>--automation &lt;project&gt; list</c> prints every command and its input schema.</item>
/// <item><c>--automation &lt;project&gt; call &lt;command&gt; [json | @file]</c> runs one and prints its result
/// (exit code 0 when it worked).</item>
/// <item><c>--automation &lt;project&gt; mcp</c> is a Model Context Protocol server on standard input and
/// output, so an AI client (Claude Code, Fable, Nova) has the commands as tools.</item>
/// </list>
/// </summary>
public static class AutomationHost
{
    public const string Usage = """
        Genesis Studio automation (Documentation/Automation.md)
          --automation <project folder> list
          --automation <project folder> call <command> [json | @file.json]
          --automation <project folder> mcp
        """;

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        if (args.Length < 2)
        {
            error.WriteLine(Usage);
            return 2;
        }

        AutomationContext context;
        try
        {
            context = AutomationContext.Open(args[0]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or InvalidDataException)
        {
            error.WriteLine($"Could not open the project at '{args[0]}': {exception.Message}");
            return 2;
        }

        AutomationRegistry registry = AutomationRegistry.CreateDefault();
        switch (args[1].ToLowerInvariant())
        {
            case "list":
                output.WriteLine(registry.Describe().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            case "call":
                if (args.Length < 3)
                {
                    error.WriteLine(Usage);
                    return 2;
                }
                context.Caller = "cli";
                JsonObject? arguments;
                try
                {
                    string text = args.Length > 3 ? args[3] : "{}";
                    if (text.StartsWith('@')) text = File.ReadAllText(text[1..]);
                    arguments = JsonNode.Parse(text) as JsonObject;
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    error.WriteLine("The input is not a JSON object: " + exception.Message);
                    return 2;
                }
                AutomationResult result = registry.Invoke(context, args[2], arguments);
                output.WriteLine(result.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return result.Ok ? 0 : 1;
            case "mcp":
                context.Caller = "mcp";
                ServeMcp(registry, context, input, output);
                return 0;
            default:
                error.WriteLine(Usage);
                return 2;
        }
    }

    /// <summary>
    /// The Model Context Protocol over standard input and output: one JSON-RPC message per line.
    /// Commands become tools, their dots turned into underscores (<c>resource.create</c> is
    /// <c>resource_create</c>), as tool names may only hold letters, digits, '_' and '-'.
    /// </summary>
    public static void ServeMcp(AutomationRegistry registry, AutomationContext context, TextReader input, TextWriter output)
    {
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal) { [ToolName(AutomationRegistry.ListCommand)] = AutomationRegistry.ListCommand };
        foreach (IAutomationCommand command in registry.Commands) toolNames[ToolName(command.Name)] = command.Name;

        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                Write(output, Error(null, -32700, "Parse error"));
                continue;
            }
            if (request is null) continue;

            JsonNode? id = request["id"]?.DeepClone();
            string method = request["method"]?.GetValue<string>() ?? string.Empty;
            JsonObject parameters = request["params"] as JsonObject ?? new JsonObject();
            if (id is null) continue; // a notification (notifications/initialized...): nothing to answer

            JsonObject response = method switch
            {
                "initialize" => Reply(id, new JsonObject
                {
                    ["protocolVersion"] = parameters["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "genesis-studio", ["version"] = "1" },
                    ["instructions"] = "Genesis Studio's automation commands for the project at " + context.Root
                        + ". Call commands_list first. Paths are project-relative (Assets/...). Destructive commands need \"confirm\": true; "
                        + "changes are backed up, checked (project and strict scripts) and written to .genesis/Automation/audit.log.",
                }),
                "ping" => Reply(id, new JsonObject()),
                "tools/list" => Reply(id, new JsonObject { ["tools"] = Tools(registry) }),
                "tools/call" => Call(registry, context, toolNames, id, parameters),
                _ => Error(id, -32601, "Method not found: " + method),
            };
            Write(output, response);
        }
    }

    private static JsonArray Tools(AutomationRegistry registry)
    {
        var tools = new JsonArray();
        foreach (JsonNode? node in registry.Describe())
        {
            if (node is not JsonObject command) continue;
            tools.Add((JsonNode?)new JsonObject
            {
                ["name"] = ToolName(command["name"]!.GetValue<string>()),
                ["description"] = command["description"]!.GetValue<string>(),
                ["inputSchema"] = command["input"]!.DeepClone(),
            });
        }
        return tools;
    }

    private static JsonObject Call(AutomationRegistry registry, AutomationContext context, Dictionary<string, string> toolNames, JsonNode id, JsonObject parameters)
    {
        string tool = parameters["name"]?.GetValue<string>() ?? string.Empty;
        if (!toolNames.TryGetValue(tool, out string? command)) return Error(id, -32602, "Unknown tool: " + tool);
        AutomationResult result = registry.Invoke(context, command, parameters["arguments"] as JsonObject);
        return Reply(id, new JsonObject
        {
            ["content"] = new JsonArray((JsonNode?)new JsonObject { ["type"] = "text", ["text"] = result.ToJson().ToJsonString() }),
            ["isError"] = !result.Ok,
        });
    }

    public static string ToolName(string command) => command.Replace('.', '_');

    private static JsonObject Reply(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static void Write(TextWriter output, JsonObject message)
    {
        output.WriteLine(message.ToJsonString());
        output.Flush();
    }
}

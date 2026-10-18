using System.Text.Json;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The commands Studio can be asked to run, and how a call is made: the input is read into the
/// command's input class (unknown fields refused, required ones checked), a destructive command needs
/// <c>"confirm": true</c>, and after a change the project checks run and the change is written to the
/// audit log. Editors add their own commands with <see cref="Register"/>.
/// </summary>
public sealed class AutomationRegistry
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The command every caller starts with: the list of commands, what they do and what they take.</summary>
    public const string ListCommand = "commands.list";

    private readonly Dictionary<string, IAutomationCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every built-in command: resources, the project, terrains' nature, rooms.</summary>
    public static AutomationRegistry CreateDefault()
    {
        var registry = new AutomationRegistry();
        foreach (IAutomationCommand command in ResourceCommands.All()) registry.Register(command);
        foreach (IAutomationCommand command in TerrainCommands.All()) registry.Register(command);
        foreach (IAutomationCommand command in TerrainShapeCommands.All()) registry.Register(command);
        foreach (IAutomationCommand command in RoomCommands.All()) registry.Register(command);
        foreach (IAutomationCommand command in ObjectCommands.All()) registry.Register(command);
        foreach (IAutomationCommand command in GameRunCommands.All()) registry.Register(command);
        return registry;
    }

    public void Register(IAutomationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.TryAdd(command.Name, command))
            throw new InvalidOperationException($"An automation command named '{command.Name}' is already registered.");
    }

    public IReadOnlyCollection<IAutomationCommand> Commands => _commands.Values;

    public bool TryGet(string name, out IAutomationCommand command) => _commands.TryGetValue(name, out command!);

    /// <summary>Every command with what it does and its input schema: what an AI reads first.</summary>
    public JsonArray Describe()
    {
        var list = new JsonArray((JsonNode?)new JsonObject
        {
            ["name"] = ListCommand,
            ["description"] = "Lists every command, what it does and the input it takes.",
            ["destructive"] = false,
            ["input"] = AutomationSchema.For(typeof(NoInput)),
        });
        foreach (IAutomationCommand command in _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            list.Add((JsonNode?)new JsonObject
            {
                ["name"] = command.Name,
                ["description"] = command.Description + (command.Destructive ? " Needs \"confirm\": true." : string.Empty),
                ["destructive"] = command.Destructive,
                ["input"] = InputSchema(command),
            });
        }
        return list;
    }

    /// <summary>The command's own schema, plus <c>confirm</c> for a destructive one and <c>check</c> for one that changes the project.</summary>
    public static JsonObject InputSchema(IAutomationCommand command)
    {
        JsonObject schema = AutomationSchema.For(command.InputType);
        var properties = (JsonObject)schema["properties"]!;
        if (command.Destructive)
            properties["confirm"] = new JsonObject { ["type"] = "boolean", ["description"] = "Must be true: this removes or overwrites work (a backup is kept)." };
        if (command.ChangesProject)
            properties["check"] = new JsonObject { ["type"] = "boolean", ["description"] = "Run the project and strict script checks afterwards (default true)." };
        return schema;
    }

    /// <summary>Runs one command. Never throws for the caller's mistakes: they come back as a failed result.</summary>
    public AutomationResult Invoke(AutomationContext context, string name, JsonObject? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.Equals(name, ListCommand, StringComparison.OrdinalIgnoreCase))
            return AutomationResult.Success($"{_commands.Count + 1} commands.", Describe());
        if (!_commands.TryGetValue(name ?? string.Empty, out IAutomationCommand? command))
            return AutomationResult.Failure($"There is no command '{name}'. Call commands.list for the commands there are.");

        JsonObject arguments = input is null ? new JsonObject() : (JsonObject)input.DeepClone();
        bool confirmed = Take(arguments, "confirm") is true;
        bool check = Take(arguments, "check") is not false;
        if (command.Destructive && !confirmed)
            return AutomationResult.Failure($"{command.Name} removes or overwrites work: call it again with \"confirm\": true.");

        object parsed;
        try
        {
            parsed = arguments.Deserialize(command.InputType, ReadOptions) ?? Activator.CreateInstance(command.InputType)!;
        }
        catch (JsonException exception)
        {
            return AutomationResult.Failure($"{command.Name}: the input does not fit (" + exception.Message + "). Its schema: " + InputSchema(command).ToJsonString());
        }

        foreach (var property in command.InputType.GetProperties())
        {
            if (property.GetCustomAttributes(typeof(RequiredInputAttribute), false).Length == 0) continue;
            object? value = property.GetValue(parsed);
            if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
                return AutomationResult.Failure($"{command.Name} needs '{AutomationSchema.JsonName(property.Name)}'.");
        }

        AutomationResult result;
        try
        {
            result = command.Run(context, parsed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever a command meets (a bad value in a room file, a locked file...) goes back to the
            // caller as a failed result: a server for AIs must never fall over on a mistake.
            result = AutomationResult.Failure($"{command.Name}: {exception.Message}");
        }

        if (command.ChangesProject)
        {
            if (result.Ok && check && result.Changed.Count > 0) result.Issues.AddRange(context.Check());
            context.Audit(command.Name, arguments.ToJsonString(), result);
        }
        return result;
    }

    private static bool? Take(JsonObject arguments, string name)
    {
        string? key = arguments.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key is null) return null;
        JsonNode? value = arguments[key];
        arguments.Remove(key);
        return value is JsonValue v && v.TryGetValue(out bool flag) ? flag : null;
    }
}

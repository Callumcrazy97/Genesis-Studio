using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// One thing Studio can be asked to do by code: by Nova, by an AI through the MCP server or the
/// command line, by a test. Each says what it does and what it takes (a JSON schema made from its
/// input class), so a caller can list the commands and call them without reading Genesis's source.
/// </summary>
public interface IAutomationCommand
{
    /// <summary>Lower-case, dotted: <c>resource.create</c>, <c>terrain.addScatterLayer</c>.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>Removes or overwrites authored work: the caller must pass <c>"confirm": true</c>.</summary>
    bool Destructive { get; }

    /// <summary>Changes the project: the checks run afterwards and the change is written to the audit log.</summary>
    bool ChangesProject { get; }

    Type InputType { get; }

    AutomationResult Run(AutomationContext context, object input);
}

/// <summary>A command whose input is a class of properties (each with a <see cref="DescriptionAttribute"/>).</summary>
public abstract class AutomationCommand<TInput> : IAutomationCommand where TInput : class, new()
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public virtual bool Destructive => false;

    public virtual bool ChangesProject => true;

    public Type InputType => typeof(TInput);

    public AutomationResult Run(AutomationContext context, object input) => Execute(context, (TInput)input);

    protected abstract AutomationResult Execute(AutomationContext context, TInput input);
}

/// <summary>For commands that take nothing.</summary>
public sealed class NoInput;

/// <summary>Marks an input property the caller must give.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredInputAttribute : Attribute;

/// <summary>Makes a command's JSON schema from its input class: property names in camelCase, types, descriptions, which are required.</summary>
public static class AutomationSchema
{
    public static JsonObject For(Type inputType)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (PropertyInfo property in inputType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            string name = JsonName(property.Name);
            JsonObject schema = TypeSchema(property.PropertyType);
            if (property.GetCustomAttribute<DescriptionAttribute>() is { } description) schema["description"] = description.Description;
            properties[name] = schema;
            if (property.GetCustomAttribute<RequiredInputAttribute>() is not null) required.Add((JsonNode?)name);
        }
        var result = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    public static string JsonName(string propertyName) =>
        propertyName.Length == 0 ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    private static JsonObject TypeSchema(Type type)
    {
        Type plain = Nullable.GetUnderlyingType(type) ?? type;
        if (plain == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (plain == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (plain == typeof(int) || plain == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (plain == typeof(double) || plain == typeof(float)) return new JsonObject { ["type"] = "number" };
        if (plain.IsEnum)
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Enum.GetNames(plain).Select(n => (JsonNode?)n)]) };
        if (plain.IsArray) return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(plain.GetElementType()!) };
        if (plain.IsGenericType && plain.GetGenericTypeDefinition() == typeof(List<>))
            return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(plain.GetGenericArguments()[0]) };
        if (plain == typeof(JsonObject) || plain == typeof(JsonNode)) return new JsonObject { ["type"] = "object" };
        return For(plain);
    }
}

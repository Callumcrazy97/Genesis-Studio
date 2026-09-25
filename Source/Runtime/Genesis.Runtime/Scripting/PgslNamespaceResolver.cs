#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Resolves additive <c>from Engine.X:</c> / <c>Engine.X.Command()</c> syntax without changing the
/// legacy flat PGSL surface. Namespaced PGSL commands are tried first, then the matching Engine
/// command category, and finally the original global command name.
/// </summary>
public static class PgslNamespaceResolver
{
    private static readonly Lazy<Dictionary<string, (MethodInfo Method, EngineCommandAttribute Attribute)>> EngineMethods =
        new(BuildEngineMethods, isThreadSafe: true);

    public static string Resolve(string commandNamespace, string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return commandName;
        commandName = commandName.Trim();
        if (commandName.Contains('.')) return commandName;

        string ns = NormalizeNamespace(commandNamespace);
        if (ns.Length == 0) return commandName;

        string qualified = ns + "." + commandName;
        EnsurePgslRegistry();
        if (PgslCommandRegistry.TryGet(qualified) != null) return qualified;
        if (TryResolveEngineCommand(qualified, out _, out _)) return qualified;

        // Namespace blocks are additive: ordinary global PGSL functions remain available inside
        // the block when that namespace does not define a command of the same name.
        return commandName;
    }

    public static bool TryResolveEngineCommand(string commandName, out string bareName, out MethodInfo method)
    {
        bareName = null;
        method = null;
        if (string.IsNullOrWhiteSpace(commandName)) return false;

        string candidate = commandName.Trim();
        if (!candidate.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)) return false;

        string remainder = candidate[7..];
        int lastDot = remainder.LastIndexOf('.');
        string namespacePart = lastDot >= 0 ? "Engine." + remainder[..lastDot] : string.Empty;
        string leaf = lastDot >= 0 ? remainder[(lastDot + 1)..] : remainder;
        if (leaf.Length == 0 || !EngineMethods.Value.TryGetValue(leaf, out var entry)) return false;

        if (namespacePart.Length > 0)
        {
            string expectedNamespace = "Engine." + NormalizeNamespaceSegment(entry.Attribute.Category);
            if (!string.Equals(namespacePart, expectedNamespace, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        bareName = leaf;
        method = entry.Method;
        return true;
    }

    public static object InvokeEngineCommand(string commandName, object[] args)
    {
        if (!TryResolveEngineCommand(commandName, out string bareName, out MethodInfo method))
            throw new InvalidOperationException($"Unknown Engine command: {commandName}");

        object[] converted = ConvertArguments(method, args ?? Array.Empty<object>());
        return EngineCommandPipeline.Invoke(bareName, converted);
    }

    public static bool IsEngineCommandVoid(string commandName) =>
        TryResolveEngineCommand(commandName, out _, out MethodInfo method) && method.ReturnType == typeof(void);

    public static string NamespaceForEngineCategory(string category)
    {
        string segment = NormalizeNamespaceSegment(category);
        return segment.Length == 0 ? string.Empty : "Engine." + segment;
    }

    private static void EnsurePgslRegistry()
    {
        if (PgslCommandRegistry.GetCatalog().Count == 0)
            PgslCommands.WarmRegistry();
    }

    private static string NormalizeNamespace(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('.');

    private static string NormalizeNamespaceSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).ToArray());
    }

    private static Dictionary<string, (MethodInfo, EngineCommandAttribute)> BuildEngineMethods()
    {
        var result = new Dictionary<string, (MethodInfo, EngineCommandAttribute)>(StringComparer.OrdinalIgnoreCase);
        foreach (MethodInfo method in typeof(Engine).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            EngineCommandAttribute attribute = method.GetCustomAttribute<EngineCommandAttribute>();
            if (attribute == null) continue;
            string name = ExtractEngineName(attribute.Signature, method.Name);
            if (!result.ContainsKey(name)) result[name] = (method, attribute);
        }
        return result;
    }

    private static object[] ConvertArguments(MethodInfo method, object[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (args.Length > parameters.Length)
            throw new InvalidOperationException(
                $"Engine command '{method.Name}' received {args.Length} arguments, expected at most {parameters.Length}.");

        var converted = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i < args.Length)
            {
                converted[i] = ConvertArgument(args[i], parameters[i].ParameterType);
            }
            else if (parameters[i].HasDefaultValue)
            {
                converted[i] = parameters[i].DefaultValue;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Engine command '{method.Name}' is missing required argument '{parameters[i].Name}'.");
            }
        }
        return converted;
    }

    private static object ConvertArgument(object value, Type targetType)
    {
        if (value == null)
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        if (targetType.IsInstanceOfType(value)) return value;

        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsEnum)
        {
            if (value is string text) return Enum.Parse(underlying, text, ignoreCase: true);
            return Enum.ToObject(underlying, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
        if (underlying == typeof(bool))
        {
            if (value is string text && bool.TryParse(text, out bool parsed)) return parsed;
            return Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture)) > double.Epsilon;
        }
        if (underlying == typeof(string))
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    private static string ExtractEngineName(string signature, string fallback)
    {
        if (string.IsNullOrWhiteSpace(signature)) return fallback;
        int dot = signature.IndexOf('.');
        int parenthesis = signature.IndexOf('(');
        if (dot >= 0 && parenthesis > dot)
            return signature.Substring(dot + 1, parenthesis - dot - 1).Trim();
        if (parenthesis > 0) return signature[..parenthesis].Trim();
        return signature.Trim();
    }
}

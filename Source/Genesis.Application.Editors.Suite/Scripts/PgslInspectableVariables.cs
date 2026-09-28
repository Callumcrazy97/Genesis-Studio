using System.Globalization;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>Shares the runtime's file-scope literal contract with Inspector authoring.</summary>
public static partial class PgslInspectableVariables
{
    public sealed record Variable(string Name, object Value, string Literal, ResourceKind? AssetKind = null);

    public static IReadOnlyList<Variable> Reflect(string source, string? projectRoot = null)
    {
        List<Variable> variables = [];
        foreach (PgslExposedVariables.Variable declaration in PgslExposedVariables.Reflect(source))
        {
            ResourceKind? kind = null;
            if (declaration.Value is string reference)
            {
                int end = declaration.LiteralStart + declaration.LiteralLength;
                int lineEnd = source.IndexOf('\n', end);
                if (lineEnd < 0) lineEnd = source.Length;
                Match annotation = ResourceAnnotation().Match(source[end..lineEnd]);
                if (annotation.Success)
                {
                    string type = annotation.Groups["kind"].Value;
                    type = type.Equals("Object", StringComparison.OrdinalIgnoreCase) ? "GameObject"
                        : type.Equals("Script", StringComparison.OrdinalIgnoreCase) ? "PgslScript"
                        : type.Equals("UI", StringComparison.OrdinalIgnoreCase) ? "UserInterface" : type;
                    if (Enum.TryParse(type, true, out ResourceKind parsed)
                        && parsed is not (ResourceKind.Unknown or ResourceKind.Folder)) kind = parsed;
                }
                else if (!string.IsNullOrWhiteSpace(projectRoot) && !string.IsNullOrWhiteSpace(reference))
                {
                    NamedResource? asset = ResourceCatalog.For(projectRoot).Find(reference);
                    if (asset is not null)
                    {
                        kind = asset.Type switch
                        {
                            ResourceType.Object => ResourceKind.GameObject,
                            ResourceType.Script => ResourceKind.PgslScript,
                            _ => Enum.TryParse(asset.Type.ToString(), out ResourceKind parsed) ? parsed : null,
                        };
                    }
                }
            }
            variables.Add(new(declaration.Name, declaration.Value, declaration.Literal, kind));
        }
        return variables;
    }

    public static bool TrySetValue(string source, string variableName, object? value, out string updated)
    {
        updated = source;
        PgslExposedVariables.Variable? variable = PgslExposedVariables.Reflect(source)
            .FirstOrDefault(item => string.Equals(item.Name, variableName, StringComparison.Ordinal));
        if (variable is null || value is null) return false;
        if (variable.Value is string && value is not string) return false;
        string serialized = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        updated = PgslExposedVariables.ApplyOverrides(source,
            new Dictionary<string, string>(StringComparer.Ordinal) { [variableName] = serialized });
        return updated != source || Equals(variable.Value, value);
    }

    // An ordinary PGSL comment provides a picker even before an optional reference is assigned.
    [GeneratedRegex(@"^\s*;\s*//\s*@resource\s+(?<kind>[A-Za-z]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceAnnotation();
}

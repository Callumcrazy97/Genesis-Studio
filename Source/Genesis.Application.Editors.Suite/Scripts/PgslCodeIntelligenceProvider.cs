using Genesis.Application.Core.Resources;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>PGSL-specific completion and signature data shared by every PGSL editing surface.</summary>
public static class PgslCodeIntelligenceProvider
{
    static PgslCodeIntelligenceProvider()
    {
        if (PgslCommandRegistry.GetCatalog().Count == 0)
            PgslCommandRegistry.Build(typeof(Genesis.Runtime.Scripting.PgslCommands));
    }
    private static readonly IReadOnlyDictionary<string, MethodInfo> CommandMethods = typeof(Genesis.Runtime.Scripting.PgslCommands)
        .GetMethods(BindingFlags.Public | BindingFlags.Static).GroupBy(method => method.Name)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    public static IReadOnlyList<string> Keywords { get; } =
    [
        "event", "if", "else", "while", "for", "foreach", "switch", "case", "default",
        "break", "continue", "return", "var", "with", "emit", "function", "true", "false",
        "null", "and", "or", "not", "in", "repeat", "until", "do", "struct",
    ];

    public static IReadOnlyList<CodeCompletionItem> BuildCompletions(
        string projectRoot,
        string source,
        string prefix)
    {
        Dictionary<string, RankedCompletion> unique = new(StringComparer.OrdinalIgnoreCase);

        foreach (PgslCommandInfo command in PgslCommandRegistry.GetFullCatalog())
        {
            string candidate = prefix.Contains('.')
                ? command.QualifiedName
                : command.Name;
            AddIfMatch(
                unique,
                prefix,
                candidate,
                new CodeCompletionItem(
                    candidate,
                    candidate,
                    string.IsNullOrWhiteSpace(command.Category) ? "Command" : $"Command · {command.Category}",
                    command.Description + (TypedSignature(command) is string typed ? " " + typed : string.Empty)),
                kindRank: 0);
        }

        foreach (EngineCommandInfo command in EngineCommandRegistry.GetCatalog().Where(command => command.IsImplemented))
        {
            string candidate = "Engine." + command.Name;
            if (!Matches(candidate, prefix) && !Matches(command.Name, prefix))
            {
                continue;
            }

            Add(
                unique,
                candidate,
                prefix,
                new CodeCompletionItem(
                    candidate,
                    candidate,
                    string.IsNullOrWhiteSpace(command.Category)
                        ? "Engine command"
                        : $"Engine command · {command.Category}",
                    command.Description),
                kindRank: 0);
        }

        foreach (string keyword in Keywords)
        {
            AddIfMatch(
                unique,
                prefix,
                keyword,
                new CodeCompletionItem(keyword, keyword, "Keyword", "PGSL language keyword."),
                kindRank: 1);
        }

        foreach (CodeSymbol symbol in CodeContextAnalyzer.DiscoverSymbols(source))
        {
            AddIfMatch(
                unique,
                prefix,
                symbol.Name,
                new CodeCompletionItem(
                    symbol.Name,
                    symbol.Name,
                    symbol.Kind,
                    $"{symbol.Kind} declared in this file."),
                kindRank: 2);
        }

        // Completion inserts the canonical name as a string literal, valid even when a resource
        // contains spaces. Neither resource extensions nor storage paths are public syntax.
        if (prefix.Length >= 2 && !string.IsNullOrWhiteSpace(projectRoot))
        {
            foreach (ResourceDefinition definition in ResourceDefinitions.All)
            {
                foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(projectRoot, definition.Kind))
                {
                    if (!Matches(entry.DisplayName, prefix) && !Matches(entry.Reference, prefix))
                    {
                        continue;
                    }

                    string insertedName = "\"" + entry.Reference.Replace("\"", "\\\"") + "\"";
                    Add(
                        unique,
                        entry.DisplayName,
                        prefix,
                        new CodeCompletionItem(
                            entry.DisplayName,
                            insertedName,
                            definition.DisplayName,
                            entry.Reference),
                        kindRank: 3);
                }
            }
        }

        return unique.Values
            .OrderBy(value => value.MatchRank)
            .ThenBy(value => value.KindRank)
            .ThenBy(value => value.Item.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .Select(value => value.Item)
            .ToArray();
    }

    public static void ApplyRequest(
        CodeEditor editor,
        string projectRoot,
        string source,
        CodeIntelligenceRequestEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind == CodeIntelligenceRequestKind.Completion)
        {
            editor.ShowAutoComplete(
                BuildCompletions(projectRoot, source, request.Prefix),
                request.ReplacementStart,
                request.ReplacementLength);
            return;
        }

        if (TryGetSignature(request.CommandName, out string signature, out string description)
            || TryGetLocalSignature(source, request.CommandName, out signature, out description))
        {
            editor.ShowSignature(signature, request.ActiveParameterIndex, description);
        }
        else
        {
            editor.HideSignature();
        }
    }

    public static bool TryGetSignature(
        string commandName,
        out string signature,
        out string description)
    {
        if (commandName.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase))
        {
            string engineName = commandName["Engine.".Length..];
            EngineCommandInfo? engineCommand = EngineCommandRegistry.GetByName(engineName);
            if (engineCommand is not null && engineCommand.IsImplemented)
            {
                signature = engineCommand.Signature ?? ("Engine." + engineCommand.Name + "()");
                description = engineCommand.Description ?? string.Empty;
                return true;
            }
        }

        PgslCommandInfo? command = PgslCommandRegistry.TryGet(commandName);
        if (command is null)
        {
            signature = string.Empty;
            description = string.Empty;
            return false;
        }

        signature = string.IsNullOrWhiteSpace(command.Signature)
            ? command.QualifiedName + "()"
            : command.Signature;
        signature = TypedSignature(command) ?? signature;
        description = command.Description ?? string.Empty;
        return true;
    }

    internal static string? TypedSignature(PgslCommandInfo command)
    {
        string name = command.CSharpMember?.Split('.').Last() ?? command.Name;
        if (!CommandMethods.TryGetValue(name, out MethodInfo? method)) return null;
        string parameters = string.Join(", ", method.GetParameters().Select(parameter =>
            TypeHint(parameter.ParameterType) + " " + parameter.Name + (parameter.IsOptional ? " = " + Convert.ToString(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) : string.Empty)));
        return command.QualifiedName + "(" + parameters + ")"
            + (method.ReturnType == typeof(void) ? string.Empty : " -> " + TypeHint(method.ReturnType));
    }

    private static string TypeHint(Type type) => type == typeof(string) ? "string" : type == typeof(bool) ? "boolean"
        : type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long) ? "number" : type.Name;

    private static bool TryGetLocalSignature(string source, string name, out string signature, out string description)
    {
        Match function = Regex.Match(source, @"\bfunction\s+" + Regex.Escape(name) + @"\s*\((?<args>[^)]*)\)");
        signature = function.Success ? name + "(" + string.Join(", ", function.Groups["args"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(argument => "any " + argument)) + ")" : string.Empty;
        description = function.Success ? "Function declared in this file; PGSL parameters are dynamically typed." : string.Empty;
        return function.Success;
    }

    private static void AddIfMatch(
        Dictionary<string, RankedCompletion> unique,
        string prefix,
        string candidate,
        CodeCompletionItem item,
        int kindRank)
    {
        if (Matches(candidate, prefix))
        {
            Add(unique, candidate, prefix, item, kindRank);
        }
    }

    private static void Add(
        Dictionary<string, RankedCompletion> unique,
        string candidate,
        string prefix,
        CodeCompletionItem item,
        int kindRank)
    {
        int matchRank = candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        RankedCompletion ranked = new(item, matchRank, kindRank);
        if (!unique.TryGetValue(item.InsertText, out RankedCompletion? existing)
            || Compare(ranked, existing) < 0)
        {
            unique[item.InsertText] = ranked;
        }
    }

    private static int Compare(RankedCompletion left, RankedCompletion right)
    {
        int match = left.MatchRank.CompareTo(right.MatchRank);
        return match != 0 ? match : left.KindRank.CompareTo(right.KindRank);
    }

    private static bool Matches(string candidate, string prefix) =>
        candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase);

    private sealed record RankedCompletion(CodeCompletionItem Item, int MatchRank, int KindRank);
}

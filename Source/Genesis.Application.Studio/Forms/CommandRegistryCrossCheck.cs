using Genesis.Shared.Scripting;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;

namespace Genesis.Application.Studio.Forms;

/// <summary>Compares Engine.* and PGSL catalogues so Auto-Test always includes the registry scan.</summary>
public sealed record CommandCrossCheckReport(
    int EngineCount,
    int PgslCount,
    IReadOnlyList<string> MissingInPgsl,
    IReadOnlyList<string> MissingInEngine)
{
    public string ToMarkdown()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("# Command Registry Cross-Check");
        text.AppendLine();
        text.AppendLine($"Engine commands: {EngineCount}");
        text.AppendLine($"PGSL wrapper commands: {PgslCount}");
        text.AppendLine();
        text.AppendLine($"## Missing in PGSL ({MissingInPgsl.Count})");
        text.AppendLine("Defined on Engine.* but no PGSL wrapper with the same bare name:");
        foreach (string name in MissingInPgsl)
            text.AppendLine($"- `{name}`");
        text.AppendLine();
        text.AppendLine($"## Missing in Engine ({MissingInEngine.Count})");
        text.AppendLine("Defined as PGSL wrappers but no Engine.* command with the same bare name:");
        foreach (string name in MissingInEngine)
            text.AppendLine($"- `{name}`");
        return text.ToString();
    }
}

public static class CommandRegistryCrossCheck
{
    public static CommandCrossCheckReport Run()
    {
        EngineCommandRegistry.Build();
        IReadOnlyList<EngineCommandInfo> engine = EngineCommandRegistry.GetCatalog();
        IReadOnlyList<PgslCommandInfo> pgsl = PgslCommandAutoTester.Catalogue();

        HashSet<string> engineNames = engine
            .Select(command => BareName(command.Signature, command.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> pgslNames = pgsl
            .Select(command => BareName(command.Signature, command.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new CommandCrossCheckReport(
            engineNames.Count,
            pgslNames.Count,
            engineNames.Except(pgslNames, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray(),
            pgslNames.Except(engineNames, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray());
    }

    private static string BareName(string signature, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(signature) ? fallback : signature;
        int paren = source.IndexOf('(');
        if (paren > 0) source = source[..paren];
        int dot = source.LastIndexOf('.');
        if (dot >= 0 && dot < source.Length - 1) source = source[(dot + 1)..];
        return source.Trim();
    }
}

using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// One name of the engine API as the live registry has it, split as <c>Engine.Area.Part.Value</c>:
/// <c>Engine.Lighting.Enabled</c> is area Lighting with no part, <c>Engine.Rendering.Fog.Start</c>
/// is area Rendering, part Fog, value Start, and <c>Engine.Display.Resolution.Dynamic.Enabled</c>
/// is area Display, part Resolution.Dynamic.
/// </summary>
internal sealed record EngineApiMember(
    string Name, string Area, string Part, string Value, bool IsProperty, bool CanSet, string Signature, string Description)
{
    /// <summary>The area, or the area and its part: the unit that has an Enabled.</summary>
    public string Scope => Part.Length == 0 ? Area : Area + "." + Part;

    public bool IsEnabled => string.Equals(Value, "Enabled", StringComparison.Ordinal) && IsProperty;
}

/// <summary>The engine API's names split into areas and parts, read from the same registry the Inspector and F6 read.</summary>
internal static class EngineApiModel
{
    private static IReadOnlyList<EngineApiMember>? _members;

    /// <summary>Every Engine.Area.Name a script can use, in name order.</summary>
    public static IReadOnlyList<EngineApiMember> Members => _members ??= Build();

    private static List<EngineApiMember> Build()
    {
        var members = new List<EngineApiMember>();
        foreach ((string name, bool isProperty, bool canSet, string signature, string description) in PgslApi.Reference())
        {
            string[] segments = name.Split('.');
            if (segments.Length < 3 || !string.Equals(segments[0], "Engine", StringComparison.Ordinal)) continue;
            string part = segments.Length == 3 ? "" : string.Join('.', segments[2..^1]);
            members.Add(new EngineApiMember(name, segments[1], part, segments[^1], isProperty, canSet, signature, description));
        }
        return members;
    }

    /// <summary>Settable properties of one area or part (<see cref="EngineApiMember.Scope"/>), Enabled first.</summary>
    public static IReadOnlyList<EngineApiMember> SettableIn(string scope) =>
        Members.Where(member => member.IsProperty && member.CanSet && member.Scope == scope)
            .OrderBy(member => member.IsEnabled ? 0 : 1).ThenBy(member => member.Name, StringComparer.Ordinal).ToList();

    /// <summary>Every area that has at least one name.</summary>
    public static IReadOnlyList<string> Areas => Members.Select(member => member.Area).Distinct(StringComparer.Ordinal).OrderBy(area => area, StringComparer.Ordinal).ToList();

    /// <summary>Every part (area and part) that has at least one property, in name order.</summary>
    public static IReadOnlyList<string> Parts => Members.Where(member => member.IsProperty && member.Part.Length > 0)
        .Select(member => member.Scope).Distinct(StringComparer.Ordinal).OrderBy(scope => scope, StringComparer.Ordinal).ToList();

    /// <summary>Whether a scope (area or part) has an Enabled property of its own.</summary>
    public static bool HasEnabled(string scope) => Members.Any(member => member.IsEnabled && member.Scope == scope);
}

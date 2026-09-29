using System.Text.RegularExpressions;
using Genesis.Application.Editors.Suite.Scripts;

namespace Genesis.Application.Editors.Suite.Terrain;

internal static class TerrainRecipeCodeAssistance
{
    private static readonly Dictionary<string, (string Signature, string Description)> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sin"] = ("sin(number radians) -> number", "Sine of an angle in radians."),
        ["cos"] = ("cos(number radians) -> number", "Cosine of an angle in radians."),
        ["abs"] = ("abs(number value) -> number", "Absolute value."),
        ["sqrt"] = ("sqrt(number value) -> number", "Square root; use a non-negative value."),
        ["min"] = ("min(number left, number right) -> number", "Smaller of two values."),
        ["max"] = ("max(number left, number right) -> number", "Larger of two values."),
        ["pow"] = ("pow(number value, number exponent) -> number", "Raise a value to an exponent."),
        ["exp"] = ("exp(number value) -> number", "Exponential function."),
        ["floor"] = ("floor(number value) -> number", "Round down to an integer."),
        ["clamp"] = ("clamp(number value, number minimum, number maximum) -> number", "Limit a value to the supplied range."),
        ["noise"] = ("noise(number x, number z) -> number", "Deterministic smooth noise from -1 to 1, using the recipe seed."),
        ["TerrainSize"] = ("TerrainSize(number width, number length)", "Set terrain dimensions in metres before sampling."),
        ["TerrainSpacing"] = ("TerrainSpacing(number metres)", "Set requested sample spacing; generation budgets may increase the effective spacing."),
    };

    public static void Attach(CodeEditor editor)
    {
        editor.SetLanguage("Terrain PGSL");
        editor.IntelligenceRequested += (_, request) =>
        {
            if (request.Kind == CodeIntelligenceRequestKind.SignatureHelp)
            {
                if (Functions.TryGetValue(request.CommandName, out var function))
                    editor.ShowSignature(function.Signature, request.ActiveParameterIndex, function.Description);
                else if(PgslCodeIntelligenceProvider.TryGetLocalSignature(editor.CodeText,request.CommandName,out string signature,out string description))
                    editor.ShowSignature(signature,request.ActiveParameterIndex,description);
                else editor.HideSignature();
                return;
            }
            IEnumerable<CodeCompletionItem> items = Functions.Select(function => new CodeCompletionItem(function.Key, function.Key, "function", function.Value.Signature));
            items = items.Concat(new[] { "x", "y", "z", "u", "v", "seed", "height", "density" }
                .Select(name => new CodeCompletionItem(name, name, "number", name is "height" or "density" ? "Terrain output value." : "Coordinate or seed supplied by the terrain generator.")));
            items = items.Concat(Regex.Matches(editor.CodeText, @"\b(?<name>[A-Za-z_]\w*)\s*=(?!=)")
                .Select(match => new CodeCompletionItem(match.Groups["name"].Value, match.Groups["name"].Value, "local value")));
            items=items.Concat(CodeContextAnalyzer.DiscoverSymbols(editor.CodeText).Select(symbol=>new CodeCompletionItem(symbol.Name,symbol.Name,symbol.Kind)));
            items=items.Concat(PgslCodeIntelligenceProvider.Keywords.Select(keyword=>new CodeCompletionItem(keyword,keyword,"keyword")));
            editor.ShowAutoComplete(items.Where(item => item.DisplayText.StartsWith(request.Prefix, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.InsertText).Take(80), request.ReplacementStart, request.ReplacementLength);
        };
        editor.ContextHelpRequested += (_, _) => editor.SetContextHint("x/y/z: metres · u/v: 0..1 · height or density: output · Ctrl+Space: terrain functions");
    }
}

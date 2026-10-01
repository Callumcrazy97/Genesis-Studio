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
        ["fbm"] = ("fbm(number x, number z, number octaves = 5) -> number", "Layered noise from -1 to 1: rolling relief with detail at every scale."),
        ["ridge"] = ("ridge(number x, number z, number octaves = 5) -> number", "Ridged noise from 0 to 1: sharp crests and valleys, for mountains."),
        ["lerp"] = ("lerp(number from, number to, number amount) -> number", "Blend between two values; amount 0 gives the first, 1 the second."),
        ["smoothstep"] = ("smoothstep(number edge0, number edge1, number value) -> number", "0 below edge0, 1 above edge1, a smooth curve between (edges may be reversed)."),
        ["length"] = ("length(number x, number z) -> number", "Distance from the centre to (x, z)."),
        ["TerrainSize"] = ("TerrainSize(number width, number length)", "Set terrain dimensions in metres before sampling."),
        ["TerrainSpacing"] = ("TerrainSpacing(number metres)", "Set requested sample spacing; the preview is coarser, the created terrain uses this spacing."),
        ["TerrainHeights"] = ("TerrainHeights(number minimum, number maximum)", "Height range the terrain stores, in metres."),
        ["Ocean"] = ("Ocean(number level = 0)", "Add a sea at this height reaching the horizon; rivers drain to it."),
        ["Erode"] = ("Erode(number strength = 0.5)", "Weather the finished land with running water: 0 none, 1 strong."),
        ["Rivers"] = ("Rivers(number count, number basinSquareKilometres = 1.5)", "Trace the largest rivers from source to sea, cut their channels and add the water."),
        ["PaintNatural"] = ("PaintNatural(number beachHeight, number snowHeight, number rockSlopeDegrees = 34)", "Paint layers 1 to 4 as grass, rock, sand and snow from height and slope."),
        ["Layer"] = ("Layer(number slot, string name, string image = \"\", number tileMetres = 6)", "Name terrain layer 1 to 4 and give it an Image that repeats every tileMetres."),
        ["Scatter"] = ("Scatter(string model, number perHectare, number minHeight, number maxHeight, number maxSlopeDegrees = 30, number layer = 0, number spacing = 3, number clumpMetres = 0, number drawDistance = 0)", "Cover suitable ground with copies of a Model: a forest, rocks. layer 1 to 4 limits it to that painted layer; drawDistance hides small things beyond that many metres."),
        ["ScatterCollision"] = ("ScatterCollision(number radius, number height = 4)", "Make the copies of the last Scatter solid: an upright box this wide (half-width, metres) and this tall, at scale 1."),
        ["Sites"] = ("Sites(number count, number minHeight, number maxHeight, number radius, number separation = 0)", "Find level, well-separated ground (near rivers when there are any) and flatten it."),
        ["SitePlace"] = ("SitePlace(string object, number count, number innerRadius = 0, number outerRadius = radius, number spacing = 8)", "Arrange Objects around each site of the last Sites command, facing the centre."),
        ["SitePaint"] = ("SitePaint(number layer)", "Paint layer 1 to 4 under each site of the last Sites command."),
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

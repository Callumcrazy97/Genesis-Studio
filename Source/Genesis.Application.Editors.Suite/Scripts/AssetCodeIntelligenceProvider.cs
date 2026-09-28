using System.Globalization;
using System.Text.RegularExpressions;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Physics;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>Assistance for the actual asset declaration formats and HLSL, rather than PGSL commands in unrelated languages.</summary>
public static class AssetCodeIntelligenceProvider
{
    private sealed record Field(string Name, string Type, string Example, string[] Choices);

    public static void AttachParticle(CodeEditor editor) => AttachDeclaration(editor, "Particle",
        ParticleCodeCodec.Serialize(new ParticleConfig()), new Dictionary<string, Type>
        {
            ["emission.shape"] = typeof(ParticleEmitShape), ["render.blend"] = typeof(ParticleBlendMode),
            ["render.alignment"] = typeof(ParticleAlignment), ["render.kind"] = typeof(ParticleRendererKind),
            ["render.space"] = typeof(ParticleSimulationSpace), ["collision.response"] = typeof(ParticleCollisionMode),
            ["bounds.mode"] = typeof(ParticleBoundsMode), ["emission.maximum"] = typeof(int), ["emission.burst"] = typeof(int),
        });

    public static void AttachPhysics(CodeEditor editor) => AttachDeclaration(editor, "Physics",
        PhysicsCodeCodec.Serialize(new PhysicsSceneConfig()), new Dictionary<string, Type>
        {
            ["body"] = typeof(PhysicsBodyKind), ["shape"] = typeof(PhysicsBodyShape),
            ["dimension"] = typeof(PhysicsDimension),
            ["spawn.shape"] = typeof(PhysicsBodyShape), ["spawn.layout"] = typeof(PhysicsSpawnLayout),
            ["spawn.count"] = typeof(int), ["solver.iterations"] = typeof(int), ["solver.substeps"] = typeof(int),
        });

    public static void AttachPathing(CodeEditor editor) => AttachDeclaration(editor, "Pathing",
        PathingCodeCodec.Encode(new PathingAsset()), new Dictionary<string, Type>
        {
            ["mode"] = typeof(PathingRouteMode), ["loop"] = typeof(PathingLoopMode), ["preview_agents"] = typeof(int),
            ["dimension"] = typeof(PathingDimension),
        });

    private static void AttachDeclaration(CodeEditor editor, string language, string example, IReadOnlyDictionary<string, Type> types)
    {
        List<Field> fields = [];
        foreach (Match match in Regex.Matches(example, @"(?m)^\s*(?<key>[\w.]+):\s*(?<value>[^\r\n]+)"))
        {
            string name = match.Groups["key"].Value;
            string value = match.Groups["value"].Value.Trim();
            Type? type = types.GetValueOrDefault(name);
            string hint = type?.IsEnum == true ? type.Name : type == typeof(int) ? "integer"
                : bool.TryParse(value, out _) ? "boolean" : value.StartsWith('"') ? "string / asset reference"
                : value.Contains(',') ? "number components" : double.TryParse(value, CultureInfo.InvariantCulture, out _) ? "number" : "value";
            fields.Add(new(name, hint, value, type?.IsEnum == true ? Enum.GetNames(type) : hint == "boolean" ? ["true", "false"] : []));
        }
        editor.SetLanguage(language);
        editor.IntelligenceRequested += (_, request) =>
        {
            if (request.Kind == CodeIntelligenceRequestKind.SignatureHelp)
            {
                editor.HideSignature();
                return;
            }
            Field? active = ActiveField(editor, fields);
            IEnumerable<CodeCompletionItem> candidates = active is not null && InValue(editor)
                ? active.Choices.Select(choice => new CodeCompletionItem(choice, choice, active.Type, active.Name + ": " + choice))
                : fields.Select(field => new CodeCompletionItem(field.Name, field.Name, field.Type,
                    field.Name + ": " + field.Example));
            if (language == "Pathing" && !InValue(editor))
                candidates = candidates.Concat(new[]
                {
                    new CodeCompletionItem("waypoint", "waypoint \"Point\" (0, 0, 0) wait 0 curve false", "route point", "Position x, y, z; wait in seconds; optional curved segment."),
                    new CodeCompletionItem("speed_key", "speed_key 0 1", "speed curve", "Time in seconds, then speed multiplier."),
                });
            editor.ShowAutoComplete(candidates.Where(item => item.DisplayText.StartsWith(request.Prefix, StringComparison.OrdinalIgnoreCase)),
                request.ReplacementStart, request.ReplacementLength);
        };
        editor.ContextHelpRequested += (_, _) =>
        {
            Field? active = ActiveField(editor, fields);
            editor.SetContextHint(active is null ? "Ctrl+Space: fields and values" : active.Name + ": " + active.Type
                + (active.Choices.Length > 0 ? " · " + string.Join(" / ", active.Choices) : " · e.g. " + active.Example));
            if (language != "Pathing") return;
            string line = CurrentLine(editor);
            int open = line.IndexOf('(');
            if (line.TrimStart().StartsWith("waypoint ", StringComparison.Ordinal) && open >= 0 && !line.Contains(')'))
                editor.ShowSignature("waypoint \"name\" (number x, number y, number z)", line[(open + 1)..].Count(c => c == ','),
                    "Follow with wait <seconds> curve <true|false>.");
        };
    }

    private static string CurrentLine(CodeEditor editor)
    {
        string source = editor.CodeText;
        int caret = Math.Clamp(editor.TextBox.SelectionStart, 0, source.Length);
        int start = caret == 0 ? 0 : source.LastIndexOf('\n', caret - 1) + 1;
        return source[start..caret];
    }

    private static bool InValue(CodeEditor editor) => CurrentLine(editor).Contains(':');

    private static Field? ActiveField(CodeEditor editor, IReadOnlyList<Field> fields)
    {
        string key = CurrentLine(editor).Split(':')[0].Trim().Trim('"');
        return fields.FirstOrDefault(field => field.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Dictionary<string, (string Signature, string Description)> HlslFunctions = new(StringComparer.Ordinal)
    {
        ["lerp"] = ("lerp(floatN x, floatN y, floatN amount) -> floatN", "Linear interpolation; amount 0 gives x and 1 gives y."),
        ["clamp"] = ("clamp(floatN value, floatN minimum, floatN maximum) -> floatN", "Clamp every component to the specified range."),
        ["saturate"] = ("saturate(floatN value) -> floatN", "Clamp every component between 0 and 1."),
        ["normalize"] = ("normalize(floatN vector) -> floatN", "Return a unit direction; avoid a zero input."),
        ["dot"] = ("dot(floatN left, floatN right) -> float", "Scalar dot product of vectors of matching dimension."),
        ["cross"] = ("cross(float3 left, float3 right) -> float3", "Cross product of two three-component vectors."),
        ["mul"] = ("mul(vectorOrMatrix left, vectorOrMatrix right)", "Matrix multiplication; the operand order determines the transform."),
        ["sin"] = ("sin(floatN radians) -> floatN", "Sine in radians, per component."),
        ["cos"] = ("cos(floatN radians) -> floatN", "Cosine in radians, per component."),
        ["pow"] = ("pow(floatN value, floatN exponent) -> floatN", "Raise each component to an exponent."),
        ["smoothstep"] = ("smoothstep(floatN minimum, floatN maximum, floatN value) -> floatN", "Smooth interpolation between the two edges."),
        ["float2"] = ("float2(float x, float y)", "Construct a two-component vector; scalar/vector constructor overloads also exist."),
        ["float3"] = ("float3(float x, float y, float z)", "Construct a three-component vector; scalar/vector constructor overloads also exist."),
        ["float4"] = ("float4(float x, float y, float z, float w)", "Construct a four-component vector; scalar/vector constructor overloads also exist."),
        ["Sample"] = ("Sample(SamplerState sampler, floatN coordinates)", "Sample a texture through its sampler; coordinate dimension follows texture dimension."),
    };

    public static void AttachHlsl(CodeEditor editor)
    {
        editor.SetLanguage("HLSL");
        editor.IntelligenceRequested += (_, request) =>
        {
            Dictionary<string, (string Signature, string Description)> functions = new(HlslFunctions, StringComparer.Ordinal);
            foreach (Match declaration in Regex.Matches(editor.CodeText,
                @"\b(?<type>(?:float|half|double|int|uint|bool)[1-4]?(?:x[1-4])?|void)\s+(?<name>[A-Za-z_]\w*)\s*\((?<parameters>[^(){}]*)\)\s*(?::[^{};]+)?\{"))
                functions[declaration.Groups["name"].Value] = (declaration.Groups["name"].Value + "(" + declaration.Groups["parameters"].Value.Trim()
                    + ") -> " + declaration.Groups["type"].Value, "Function declared in this shader.");
            if (request.Kind == CodeIntelligenceRequestKind.SignatureHelp)
            {
                string name = request.CommandName.Split('.').Last();
                if (functions.TryGetValue(name, out var signature)) editor.ShowSignature(signature.Signature, request.ActiveParameterIndex, signature.Description);
                else editor.HideSignature();
                return;
            }
            List<CodeCompletionItem> items = functions.Select(pair => new CodeCompletionItem(pair.Key, pair.Key, "function", pair.Value.Signature)).ToList();
            foreach (string type in new[] { "float", "float2", "float3", "float4", "float3x3", "float4x4", "int", "uint", "bool", "Texture2D", "SamplerState", "cbuffer", "struct", "return" })
                if (!functions.ContainsKey(type)) items.Add(new(type, type, "type / keyword"));
            foreach (Match symbol in Regex.Matches(editor.CodeText, @"\b(?<type>(?:float|half|int|uint|bool)[1-4]?(?:x[1-4])?|Texture\w*|SamplerState)\s+(?<name>[A-Za-z_]\w*)"))
                items.Add(new(symbol.Groups["name"].Value, symbol.Groups["name"].Value, symbol.Groups["type"].Value, "Declared in this shader."));
            editor.ShowAutoComplete(items.Where(item => item.DisplayText.StartsWith(request.Prefix, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.InsertText).Take(80), request.ReplacementStart, request.ReplacementLength);
        };
        editor.ContextHelpRequested += (_, _) => editor.SetContextHint("Ctrl+Space: types, symbols and functions");
    }
}

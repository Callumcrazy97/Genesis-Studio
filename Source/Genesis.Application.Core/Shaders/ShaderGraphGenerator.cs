using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Shaders;

public sealed record ShaderGraphParameterInfo(string Name, string HlslType, float[] Value);

public sealed record ShaderGraphTextureInfo(string Name, int Slot, string Binding);

/// <summary>What a graph made: its HLSL, or why it could not be made.</summary>
public sealed class ShaderGraphGenerationResult
{
    public string Source { get; internal set; } = string.Empty;
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<ShaderGraphParameterInfo> Parameters { get; } = [];
    public List<ShaderGraphTextureInfo> Textures { get; } = [];
    public bool Succeeded => Errors.Count == 0 && Source.Length > 0;
}

/// <summary>What one node's HLSL is written with: its inputs (already of the port's type) and the target's names.</summary>
public sealed class ShaderGraphEmitContext
{
    private readonly ShaderGraphGenerator.Session _session;
    private readonly string[] _inputs;
    private readonly int _order;
    private readonly ShaderGraphNodeDefinition _definition;

    internal ShaderGraphEmitContext(
        ShaderGraphGenerator.Session session,
        ShaderGraphNode node,
        ShaderGraphNodeDefinition definition,
        string[] inputs,
        ShaderGraphValueType dynamic,
        int order)
    {
        _session = session;
        Node = node;
        _definition = definition;
        _inputs = inputs;
        Dynamic = dynamic;
        _order = order;
    }

    public ShaderGraphNode Node { get; }

    /// <summary>The type the node's dynamic ports resolved to.</summary>
    public ShaderGraphValueType Dynamic { get; }

    internal ShaderGraphGenerator.TargetNames Target => _session.Target;

    public string In(int index) => _inputs[index];

    /// <summary>The local variable holding output <paramref name="output"/> (only outputs before the one being written).</summary>
    public string Var(int output) => ShaderGraphGenerator.VariableName(_order, _definition.Outputs[output].Name);

    public string Literal(float[]? values, ShaderGraphValueType type) => ShaderGraphGenerator.Literal(values, type);

    public string Helper(string name)
    {
        _session.Helpers.Add(name);
        return name;
    }

    /// <summary>A colour picked in a colour box, in the space the target's colours are in (a vertex offset takes it as it is).</summary>
    public string PickedColour(string float4Expression) => Target.Mesh && !Target.Vertex
        ? Helper("GsgColour4") + "(" + float4Expression + ")"
        : float4Expression;

    public string ObjectPosition() => Target.Vertex ? "v.Position"
        : Target.Mesh ? Helper("GsgObjectPosition") + "(i.WorldPosition)"
        : Target.ObjectPosition;

    public string MainTexture()
    {
        if (Target.Vertex) _session.TextureInVertexStage();
        return Target.MainTextureCode;
    }

    public string TextureReference(string name) => name + "|" + Target.Sampler;

    public string Sample(string textureReference, string uv)
    {
        if (Target.Vertex)
        {
            _session.TextureInVertexStage();
            return "float4(0.0, 0.0, 0.0, 0.0)";
        }
        int split = textureReference.IndexOf('|', StringComparison.Ordinal);
        string texture = split < 0 ? textureReference : textureReference[..split];
        string sampler = split < 0 ? Target.Sampler : textureReference[(split + 1)..];
        string read = $"{texture}.Sample({sampler}, {uv})";
        return Node.Mode switch
        {
            "Data" => read,
            "Normal map" => Helper("GsgUnpackNormal") + "(" + read + ")",
            _ => Target.Mesh ? Helper("GsgColour4") + "(" + read + ")" : read,
        };
    }
}

/// <summary>
/// Turns a <see cref="ShaderGraphDocument"/> into the HLSL of a Shader resource: a surface shader
/// (<c>#include "GenesisSurface.hlsl"</c>) for Mesh targets, so the engine lights it as its own
/// materials, and a <c>MainPS</c> pixel shader for Sprite and Fullscreen targets. Every node writes
/// one local variable per output, in the order the Output node needs them; nodes the Output does
/// not reach write nothing, but parameters and textures are always declared so the Inspector shows
/// them. The same graph always makes the same text.
/// </summary>
public static class ShaderGraphGenerator
{
    public const string GeneratedHeader =
        "// Made from this Shader's node graph by the Shader editor, which writes it again whenever\n"
        + "// the graph changes. Edit the graph, or detach it (Nodes > Detach graph) to edit this by hand.\n";

    /// <summary>Most numbers <c>GenesisParameters</c> (b5) holds: four float4 rows.</summary>
    public const int ParameterRows = 4;

    /// <summary>Most textures a graph may declare (the engine binds four of a draw's own).</summary>
    public const int MaxTextures = 4;

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex TypeLike = new(@"^(float|int|uint|bool|half|double|dword|min\d+\w*)([1-4](x[1-4])?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        // HLSL words that look ordinary (Documentation/PostEffects.md lists them) and C++'s.
        "line", "point", "triangle", "linear", "sample", "centroid", "precise", "shared", "in", "out", "inout",
        "vector", "matrix", "texture", "sampler", "string", "pass", "compile", "technique", "struct", "cbuffer",
        "tbuffer", "register", "static", "const", "uniform", "void", "return", "if", "else", "for", "while", "do",
        "switch", "case", "default", "break", "continue", "discard", "true", "false", "new", "this", "long",
        "char", "short", "signed", "unsigned", "class", "public", "private", "protected", "template", "typedef",
        "namespace", "operator", "sizeof", "union", "extern", "volatile", "inline", "nointerpolation", "noperspective",
        "groupshared", "snorm", "unorm", "row_major", "column_major", "packoffset", "interface", "export",
        // Names the generated code and the engine's own declarations use.
        "Time", "Frame", "Resolution", "SpriteTex", "SpriteSamp", "SceneColor", "AlbedoTex", "AlbedoSamp",
        "NormalMap", "OrmMap", "EmissionMap", "WorldMatrix", "MaterialParams", "MaterialColor", "CameraPosTime",
        "VSOut", "PreviewVSOut", "MainPS", "PS", "VS", "Surface", "IN", "i", "s",
    };

    /// <summary>Why <paramref name="name"/> cannot name a parameter, or null when it can.</summary>
    public static string? ParameterNameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A parameter needs a name.";
        if (!Identifier.IsMatch(name)) return $"'{name}' is not a name HLSL accepts: letters, digits and _, not starting with a digit.";
        if (Reserved.Contains(name) || TypeLike.IsMatch(name)
            || name.StartsWith("Gsg", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Genesis", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(name, @"^g\d+_"))
        {
            return $"'{name}' is taken by HLSL or the engine; choose another name.";
        }
        return null;
    }

    public static ShaderGraphGenerationResult Generate(ShaderGraphDocument? graph, ShaderAssetPipeline pipeline)
    {
        ShaderGraphGenerationResult result = new();
        if (graph is null)
        {
            result.Errors.Add("The shader has no node graph.");
            return result;
        }

        new Session(graph, pipeline, result).Run();
        return result;
    }

    internal static string VariableName(int order, string port) => "g" + order.ToString(CultureInfo.InvariantCulture) + "_" + port;

    public static string TypeName(ShaderGraphValueType type) => type switch
    {
        ShaderGraphValueType.Float => "float",
        ShaderGraphValueType.Float2 => "float2",
        ShaderGraphValueType.Float3 => "float3",
        ShaderGraphValueType.Float4 => "float4",
        ShaderGraphValueType.Texture => "Texture2D",
        _ => "float",
    };

    internal static string Number(float value)
    {
        if (!float.IsFinite(value)) value = 0f;
        string text = value.ToString("0.0#######", CultureInfo.InvariantCulture);
        return text == "-0.0" ? "0.0" : text;
    }

    /// <summary>A constant of <paramref name="type"/>: one value fills every component, missing ones are 0.</summary>
    public static string Literal(float[]? values, ShaderGraphValueType type)
    {
        values ??= [];
        int count = type is >= ShaderGraphValueType.Float and <= ShaderGraphValueType.Float4 ? (int)type : 1;
        string Component(int index) => Number(index < values.Length ? values[index] : values.Length == 1 ? values[0] : 0f);
        if (count == 1) return Component(0);
        return TypeName(type) + "(" + string.Join(", ", Enumerable.Range(0, count).Select(Component)) + ")";
    }

    /// <summary>An expression of type <paramref name="from"/> as <paramref name="to"/>: a number fills a vector, a vector is cut or padded (w with 1).</summary>
    public static string Convert(string expression, ShaderGraphValueType from, ShaderGraphValueType to)
    {
        if (from == to || to is ShaderGraphValueType.Texture or ShaderGraphValueType.Dynamic) return expression;
        int source = (int)from, target = (int)to;
        if (source == 1) return $"(({TypeName(to)})({expression}))";
        if (target == 1) return $"({expression}).x";
        if (target < source) return $"({expression})." + "xyzw"[..target];
        return (source, target) switch
        {
            (2, 3) => $"float3({expression}, 0.0)",
            (2, 4) => $"float4({expression}, 0.0, 1.0)",
            _ => $"float4({expression}, 1.0)",
        };
    }

    private static readonly (string Name, string Code, bool MeshOnly)[] HelperCode =
    [
        ("GsgColour4", """
            float4 GsgColour4(float4 c)
            {
                return float4(GenesisColor(c.rgb), c.a);
            }
            """, true),
        ("GsgUnpackNormal", """
            float4 GsgUnpackNormal(float4 c)
            {
                return float4(c.rgb * 2.0 - 1.0, c.a);
            }
            """, false),
        ("GsgFresnel", """
            float GsgFresnel(float3 normal, float3 view, float power)
            {
                return pow(1.0 - saturate(dot(normalize(normal), normalize(view))), max(power, 0.0001));
            }
            """, false),
        ("GsgObjectPosition", """
            // The draw's own world matrix undone (rows are the object's turned, scaled axes). Copies of a
            // mesh drawn together in one batch carry their matrices elsewhere: they give the world position.
            float3 GsgObjectPosition(float3 world)
            {
                float3x3 axes = (float3x3)WorldMatrix;
                float3 scale = float3(dot(axes[0], axes[0]), dot(axes[1], axes[1]), dot(axes[2], axes[2]));
                if (MaterialParams.w > 0.5 || min(scale.x, min(scale.y, scale.z)) < 1e-10) return world;
                return mul(axes, world - WorldMatrix[3].xyz) / scale;
            }
            """, true),
    ];

    /// <summary>The names a target's pixel shader gives its inputs.</summary>
    internal sealed class TargetNames
    {
        public required bool Mesh { get; init; }
        /// <summary>Names inside a surface shader's Vertex function, where a Vertex offset is worked out.</summary>
        public bool Vertex { get; init; }
        public required string Uv { get; init; }
        public required string WorldPosition { get; init; }
        public required string ObjectPosition { get; init; }
        public required string Normal { get; init; }
        public required string ViewDirection { get; init; }
        public required string Time { get; init; }
        public required string ScreenUv { get; init; }
        public required string VertexColourCode { get; init; }
        public required string MainTextureCode { get; init; }
        public required string MainTextureReference { get; init; }
        public required string Sampler { get; init; }
        public required int FirstTextureSlot { get; init; }

        /// <summary>
        /// Inside <c>void Vertex(GenesisVertexInput i, inout GenesisVertex v)</c>: the vertex's own UV,
        /// normal and position (object space) and colour as stored; no screen and no textures.
        /// </summary>
        public static TargetNames ForVertex() => new()
        {
            Mesh = true, Vertex = true, Uv = "v.UV", WorldPosition = "i.WorldPosition", ObjectPosition = "v.Position",
            Normal = "v.Normal", ViewDirection = "normalize(i.CameraPosition - i.WorldPosition)", Time = "i.Time",
            ScreenUv = "float2(0.0, 0.0)", VertexColourCode = "v.Color", MainTextureCode = "float4(1.0, 1.0, 1.0, 1.0)",
            MainTextureReference = "AlbedoTex|AlbedoSamp", Sampler = "GenesisLinearWrap", FirstTextureSlot = 17,
        };

        public static TargetNames For(ShaderAssetPipeline pipeline) => pipeline switch
        {
            ShaderAssetPipeline.Mesh => new TargetNames
            {
                Mesh = true, Uv = "i.UV", WorldPosition = "i.WorldPosition", ObjectPosition = "i.WorldPosition",
                Normal = "i.Normal", ViewDirection = "i.ViewDirection", Time = "i.Time", ScreenUv = "i.ScreenUV",
                VertexColourCode = "float4(GenesisColor(i.Color.rgb), i.Color.a)", MainTextureCode = "i.BaseColorTexture",
                MainTextureReference = "AlbedoTex|AlbedoSamp", Sampler = "GenesisLinearWrap", FirstTextureSlot = 17,
            },
            ShaderAssetPipeline.Fullscreen => new TargetNames
            {
                Mesh = false, Uv = "IN.UV", WorldPosition = "float3(IN.UV, 0.0)", ObjectPosition = "float3(IN.UV, 0.0)",
                Normal = "float3(0.0, 0.0, -1.0)", ViewDirection = "float3(0.0, 0.0, -1.0)", Time = "Time", ScreenUv = "IN.UV",
                VertexColourCode = "float4(1.0, 1.0, 1.0, 1.0)", MainTextureCode = "SceneColor.Sample(GsgClamp, IN.UV)",
                MainTextureReference = "SceneColor|GsgClamp", Sampler = "GsgClamp", FirstTextureSlot = 5,
            },
            _ => new TargetNames
            {
                Mesh = false, Uv = "IN.UV", WorldPosition = "float3(IN.SvPos.xy, 0.0)", ObjectPosition = "float3(IN.UV, 0.0)",
                Normal = "float3(0.0, 0.0, -1.0)", ViewDirection = "float3(0.0, 0.0, -1.0)", Time = "Time",
                ScreenUv = "(IN.SvPos.xy / max(Resolution, 1.0))",
                VertexColourCode = "IN.Color", MainTextureCode = "SpriteTex.Sample(SpriteSamp, IN.UV)",
                MainTextureReference = "SpriteTex|SpriteSamp", Sampler = "SpriteSamp", FirstTextureSlot = 17,
            },
        };
    }

    internal sealed class Session
    {
        private readonly ShaderGraphDocument _graph;
        private readonly ShaderAssetPipeline _pipeline;
        private readonly ShaderGraphGenerationResult _result;
        private readonly Dictionary<string, ShaderGraphNode> _nodes;
        private readonly Dictionary<(string Node, string Port), ShaderGraphConnection> _wires;
        private readonly Dictionary<string, Dictionary<string, (string Code, ShaderGraphValueType Type)>> _emitted = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
        private readonly StringBuilder _body = new();
        private int _order;
        private bool _vertexTextureReported;

        public Session(ShaderGraphDocument graph, ShaderAssetPipeline pipeline, ShaderGraphGenerationResult result)
        {
            _graph = graph;
            _pipeline = pipeline;
            _result = result;
            _nodes = new Dictionary<string, ShaderGraphNode>(StringComparer.Ordinal);
            _wires = new Dictionary<(string Node, string Port), ShaderGraphConnection>();
            Helpers = new SortedSet<string>(StringComparer.Ordinal);
            Target = TargetNames.For(pipeline);
        }

        /// <summary>The Vertex function's session: the same graph, wires, result and helpers, with the vertex stage's names.</summary>
        private Session(Session surface)
        {
            _graph = surface._graph;
            _pipeline = surface._pipeline;
            _result = surface._result;
            _nodes = surface._nodes;
            _wires = surface._wires;
            Helpers = surface.Helpers;
            Target = TargetNames.ForVertex();
        }

        public TargetNames Target { get; }
        public SortedSet<string> Helpers { get; }

        public void TextureInVertexStage()
        {
            if (_vertexTextureReported) return;
            _vertexTextureReported = true;
            _result.Errors.Add("The Output's Vertex offset cannot read a texture: textures are read per pixel. Work the offset out from numbers, UV, position, normal and time.");
        }

        private static string Describe(ShaderGraphNode node) =>
            (ShaderGraphCatalog.Find(node.Type)?.Title ?? node.Type) + (string.IsNullOrWhiteSpace(node.Name) ? string.Empty : " '" + node.Name + "'");

        public void Run()
        {
            foreach (ShaderGraphNode node in _graph.Nodes ?? [])
            {
                if (node is null) continue;
                if (string.IsNullOrWhiteSpace(node.Id) || !_nodes.TryAdd(node.Id, node))
                {
                    _result.Errors.Add($"Two nodes share the id '{node.Id}'.");
                    continue;
                }
                if (ShaderGraphCatalog.Find(node.Type) is null)
                    _result.Errors.Add($"Node {node.Id} is of a kind the Shader editor does not know: '{node.Type}'.");
            }

            List<ShaderGraphNode> outputs = _nodes.Values.Where(node =>
                string.Equals(node.Type, ShaderGraphCatalog.OutputType, StringComparison.OrdinalIgnoreCase)).ToList();
            if (outputs.Count != 1)
            {
                _result.Errors.Add(outputs.Count == 0
                    ? "The graph needs an Output node: it is what the shader gives the engine."
                    : "The graph has more than one Output node; keep one.");
                return;
            }

            CollectWires();
            List<ShaderGraphNode> parameters = CollectParameters();
            List<ShaderGraphNode> textures = CollectTextures();
            List<string> assignments = EmitOutput(outputs[0]);
            string? vertexFunction = EmitVertexStage(outputs[0]);
            if (_result.Errors.Count > 0) return;

            string source = Compose(parameters, textures, assignments, vertexFunction);
            try
            {
                ShaderParameterReflection.Reflect(source);
            }
            catch (InvalidDataException)
            {
                _result.Errors.Add("The graph's parameters need more than the 16 numbers GenesisParameters holds: a Colour or Vector parameter takes four, and four Numbers share four.");
                return;
            }
            _result.Source = source;
        }

        private void CollectWires()
        {
            foreach (ShaderGraphConnection wire in _graph.Connections ?? [])
            {
                if (wire is null) continue;
                if (!_nodes.TryGetValue(wire.FromNode ?? string.Empty, out ShaderGraphNode? from)
                    || !_nodes.TryGetValue(wire.ToNode ?? string.Empty, out ShaderGraphNode? to)
                    || ShaderGraphCatalog.Find(from.Type)?.FindOutput(wire.FromPort) is not { } output
                    || ShaderGraphCatalog.Find(to.Type)?.FindInput(wire.ToPort) is not { } input)
                {
                    _result.Warnings.Add($"A wire from {wire.FromNode}.{wire.FromPort} to {wire.ToNode}.{wire.ToPort} names a node or port that is not there; it is left out.");
                    continue;
                }
                bool outputTexture = output.Type == ShaderGraphValueType.Texture;
                bool inputTexture = input.Type == ShaderGraphValueType.Texture;
                if (outputTexture != inputTexture)
                {
                    _result.Errors.Add(inputTexture
                        ? $"{Describe(to)}'s {input.DisplayName} takes a texture, but {Describe(from)}'s {output.DisplayName} is a number or vector."
                        : $"{Describe(from)}'s texture can only go into a Sample texture node, not {Describe(to)}'s {input.DisplayName}.");
                    continue;
                }
                _wires[(to.Id, input.Name)] = wire;
            }
        }

        private List<ShaderGraphNode> CollectParameters()
        {
            var seen = new Dictionary<string, ShaderGraphNode>(StringComparer.OrdinalIgnoreCase);
            List<ShaderGraphNode> parameters = [];
            foreach (ShaderGraphNode node in _nodes.Values)
            {
                ShaderGraphNodeDefinition? definition = ShaderGraphCatalog.Find(node.Type);
                if (definition?.Role != ShaderGraphNodeRole.Parameter) continue;
                if (ParameterNameProblem(node.Name) is { } problem)
                {
                    _result.Errors.Add($"{definition.Title} ({node.Id}): {problem}");
                    continue;
                }
                if (!seen.TryAdd(node.Name, node))
                {
                    _result.Errors.Add($"Two parameters are named '{node.Name}'; each needs a name of its own.");
                    continue;
                }
                if (definition.Type == "TextureParameter") continue;
                parameters.Add(node);
            }

            // A float4 starts a row of its own, so the rows go first and the numbers fill what is left.
            return parameters
                .OrderBy(node => node.Type == "FloatParameter" ? 1 : 0)
                .ThenBy(node => (_graph.Nodes ?? []).IndexOf(node))
                .ToList();
        }

        private List<ShaderGraphNode> CollectTextures()
        {
            List<ShaderGraphNode> textures = _nodes.Values
                .Where(node => string.Equals(node.Type, "TextureParameter", StringComparison.OrdinalIgnoreCase)
                    && ParameterNameProblem(node.Name) is null)
                .ToList();
            if (textures.Count > MaxTextures)
                _result.Errors.Add($"The graph has {textures.Count} texture parameters; a shader may have {MaxTextures}.");
            return textures;
        }

        private List<string> EmitOutput(ShaderGraphNode output)
        {
            ShaderGraphNodeDefinition definition = ShaderGraphCatalog.Find(output.Type)!;
            _visiting.Add(output.Id);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderGraphPort port in definition.Inputs)
            {
                // The Vertex offset is worked out per vertex, in the Vertex function (EmitVertexStage).
                if (port.Name == ShaderGraphCatalog.VertexOffsetPort) continue;
                bool wired = _wires.ContainsKey((output.Id, port.Name));
                if (!wired && !output.Inputs.ContainsKey(port.Name)) continue;
                (string code, ShaderGraphValueType type) = ResolveInput(output, port, port.Type);
                values[port.Name] = Convert(code, type, port.Type);
            }
            _visiting.Remove(output.Id);

            List<string> lines = [];
            if (Target.Mesh)
            {
                if (values.TryGetValue("Colour", out string? colour)) lines.Add($"s.Albedo = {colour};");
                if (values.TryGetValue("Normal", out string? normal)) lines.Add($"s.Normal = GenesisPerturbNormal(i, s.Normal, {normal});");
                if (values.TryGetValue("Emission", out string? emission)) lines.Add($"s.Emission = {emission};");
                if (values.TryGetValue("Roughness", out string? roughness)) lines.Add($"s.Roughness = {roughness};");
                if (values.TryGetValue("Metalness", out string? metalness)) lines.Add($"s.Metalness = {metalness};");
                if (values.TryGetValue("Alpha", out string? alpha)) lines.Add($"s.Alpha = {alpha};");
                return lines;
            }

            foreach (string unused in new[] { "Normal", "Roughness", "Metalness" })
            {
                if (values.ContainsKey(unused))
                    _result.Warnings.Add($"The Output's {unused} is for Mesh targets (models and terrain); a {_pipeline} shader leaves it out.");
            }
            lines.Add($"float3 gsgColour = {(values.TryGetValue("Colour", out string? flat) ? flat : "gsgBase.rgb")};");
            lines.Add($"float3 gsgEmission = {(values.TryGetValue("Emission", out string? glow) ? glow : "float3(0.0, 0.0, 0.0)")};");
            lines.Add($"float gsgAlpha = {(values.TryGetValue("Alpha", out string? cover) ? cover : "gsgBase.a")};");
            lines.Add("return float4(gsgColour + gsgEmission, gsgAlpha);");
            return lines;
        }

        /// <summary>
        /// The Output's Vertex offset as the shader's <c>void Vertex(...)</c> (Mesh targets), worked out in a
        /// session of its own with the vertex's names; null when nothing feeds it.
        /// </summary>
        private string? EmitVertexStage(ShaderGraphNode output)
        {
            string port = ShaderGraphCatalog.VertexOffsetPort;
            if (!_wires.ContainsKey((output.Id, port)) && !output.Inputs.ContainsKey(port)) return null;
            if (!Target.Mesh)
            {
                _result.Warnings.Add($"The Output's Vertex offset is for Mesh targets (models and terrain); a {_pipeline} shader leaves it out.");
                return null;
            }
            return new Session(this).EmitVertexFunction(output);
        }

        private string EmitVertexFunction(ShaderGraphNode output)
        {
            ShaderGraphPort port = ShaderGraphCatalog.Find(output.Type)!.FindInput(ShaderGraphCatalog.VertexOffsetPort)!;
            _visiting.Add(output.Id);
            (string code, ShaderGraphValueType type) = ResolveInput(output, port, port.Type);
            string offset = Convert(code, type, port.Type);
            _visiting.Remove(output.Id);
            return "void Vertex(GenesisVertexInput i, inout GenesisVertex v)\n{\n" + _body + "    v.Position += " + offset + ";\n}\n\n";
        }

        /// <summary>The code feeding one input, and its type before conversion to the port's.</summary>
        private (string Code, ShaderGraphValueType Type) ResolveInput(ShaderGraphNode node, ShaderGraphPort port, ShaderGraphValueType resolved)
        {
            if (_wires.TryGetValue((node.Id, port.Name), out ShaderGraphConnection? wire))
            {
                Dictionary<string, (string Code, ShaderGraphValueType Type)> source = Emit(_nodes[wire.FromNode]);
                foreach ((string name, (string Code, ShaderGraphValueType Type) value) in source)
                {
                    if (string.Equals(name, wire.FromPort, StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }

            if (port.Type == ShaderGraphValueType.Texture)
                return (Target.MainTextureReference, ShaderGraphValueType.Texture);
            if (node.Inputs.TryGetValue(port.Name, out float[]? own) && own is { Length: > 0 })
                return (Literal(own, resolved), resolved);
            return port.Source switch
            {
                ShaderGraphDefaultSource.Uv => (Target.Uv, ShaderGraphValueType.Float2),
                ShaderGraphDefaultSource.Normal => (Target.Normal, ShaderGraphValueType.Float3),
                ShaderGraphDefaultSource.ViewDirection => (Target.ViewDirection, ShaderGraphValueType.Float3),
                ShaderGraphDefaultSource.MainTexture => (Target.MainTextureReference, ShaderGraphValueType.Texture),
                _ => (Literal(port.Default, resolved), resolved),
            };
        }

        /// <summary>The width of what is wired, or set, into an input whose type follows the node's.</summary>
        private ShaderGraphValueType DynamicWidth(ShaderGraphNode node, ShaderGraphPort port)
        {
            if (_wires.TryGetValue((node.Id, port.Name), out ShaderGraphConnection? wire)
                && _nodes.TryGetValue(wire.FromNode, out ShaderGraphNode? from)
                && ShaderGraphCatalog.Find(from.Type)?.FindOutput(wire.FromPort) is { } output)
            {
                if (output.Type != ShaderGraphValueType.Dynamic) return output.Type;
                Dictionary<string, (string Code, ShaderGraphValueType Type)> emitted = Emit(from);
                return emitted.TryGetValue(output.Name, out var value) ? value.Type : ShaderGraphValueType.Float;
            }

            float[] values = node.Inputs.TryGetValue(port.Name, out float[]? own) && own is { Length: > 0 } ? own : port.Default;
            return (ShaderGraphValueType)Math.Clamp(values.Length, 1, 4);
        }

        private Dictionary<string, (string Code, ShaderGraphValueType Type)> Emit(ShaderGraphNode node)
        {
            if (_emitted.TryGetValue(node.Id, out var done)) return done;
            ShaderGraphNodeDefinition? definition = ShaderGraphCatalog.Find(node.Type);
            if (definition is null || definition.Role == ShaderGraphNodeRole.Output || !_visiting.Add(node.Id))
            {
                if (definition is not null && definition.Role != ShaderGraphNodeRole.Output)
                    _result.Errors.Add($"The graph loops back on itself at {Describe(node)} ({node.Id}): a node cannot feed its own inputs.");
                else if (definition?.Role == ShaderGraphNodeRole.Output)
                    _result.Errors.Add("Nothing can be wired out of the Output node.");
                return Placeholder(definition);
            }

            ShaderGraphValueType dynamic = ShaderGraphValueType.Float;
            foreach (ShaderGraphPort port in definition.Inputs)
            {
                if (port.Type != ShaderGraphValueType.Dynamic) continue;
                ShaderGraphValueType width = DynamicWidth(node, port);
                if (width == ShaderGraphValueType.Texture)
                {
                    _result.Errors.Add($"{Describe(node)}'s {port.DisplayName} cannot take a texture; sample it first.");
                    continue;
                }
                if (width > dynamic) dynamic = width;
            }

            string[] inputs = new string[definition.Inputs.Count];
            for (int index = 0; index < inputs.Length; index++)
            {
                ShaderGraphPort port = definition.Inputs[index];
                ShaderGraphValueType wanted = port.Type == ShaderGraphValueType.Dynamic ? dynamic : port.Type;
                (string code, ShaderGraphValueType type) = ResolveInput(node, port, wanted);
                inputs[index] = Convert(code, type, wanted);
            }

            int order = _order++;
            ShaderGraphEmitContext context = new(this, node, definition, inputs, dynamic, order);
            string[] codes = definition.Emit(context);
            var outputs = new Dictionary<string, (string Code, ShaderGraphValueType Type)>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < definition.Outputs.Count; index++)
            {
                ShaderGraphPort port = definition.Outputs[index];
                ShaderGraphValueType type = port.Type == ShaderGraphValueType.Dynamic ? dynamic : port.Type;
                string code = index < codes.Length ? codes[index] : Literal([], type);
                if (type == ShaderGraphValueType.Texture)
                {
                    outputs[port.Name] = (code, type);
                    continue;
                }
                string variable = VariableName(order, port.Name);
                _body.Append("    ").Append(TypeName(type)).Append(' ').Append(variable).Append(" = ").Append(code).Append(";\n");
                outputs[port.Name] = (variable, type);
            }

            _visiting.Remove(node.Id);
            _emitted[node.Id] = outputs;
            return outputs;
        }

        private Dictionary<string, (string Code, ShaderGraphValueType Type)> Placeholder(ShaderGraphNodeDefinition? definition)
        {
            var outputs = new Dictionary<string, (string Code, ShaderGraphValueType Type)>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderGraphPort port in definition?.Outputs ?? [])
            {
                ShaderGraphValueType type = port.Type == ShaderGraphValueType.Dynamic ? ShaderGraphValueType.Float : port.Type;
                outputs[port.Name] = type == ShaderGraphValueType.Texture ? (Target.MainTextureReference, type) : (Literal([], type), type);
            }
            return outputs;
        }

        private string Compose(List<ShaderGraphNode> parameters, List<ShaderGraphNode> textures, List<string> assignments, string? vertexFunction)
        {
            StringBuilder code = new(GeneratedHeader);
            if (Target.Mesh)
            {
                code.Append("#include \"GenesisSurface.hlsl\"\n\n");
            }
            else
            {
                code.Append(_pipeline == ShaderAssetPipeline.Fullscreen
                    ? "Texture2D SceneColor : register(t0);\nSamplerState GsgClamp : register(s0);\n"
                    : "Texture2D SpriteTex : register(t0);\nSamplerState SpriteSamp : register(s0);\n");
                code.Append("cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };\n\n");
            }

            if (parameters.Count > 0)
            {
                code.Append("cbuffer GenesisParameters : register(b5)\n{\n");
                foreach (ShaderGraphNode parameter in parameters)
                {
                    bool number = parameter.Type == "FloatParameter";
                    string type = number ? "float" : "float4";
                    code.Append("    ").Append(type).Append(' ').Append(parameter.Name).Append(";\n");
                    ShaderGraphNodeDefinition definition = ShaderGraphCatalog.Find(parameter.Type)!;
                    float[] value = (parameter.Value is { Length: > 0 } ? parameter.Value : definition.DefaultValue)
                        .Concat(definition.DefaultValue.Skip(parameter.Value?.Length ?? 0))
                        .Take(number ? 1 : 4)
                        .ToArray();
                    _result.Parameters.Add(new ShaderGraphParameterInfo(parameter.Name, type, value));
                }
                code.Append("};\n");
            }

            for (int index = 0; index < textures.Count && index < MaxTextures; index++)
            {
                int slot = Target.FirstTextureSlot + index;
                code.Append("Texture2D ").Append(textures[index].Name).Append(" : register(t")
                    .Append(slot.ToString(CultureInfo.InvariantCulture)).Append(");\n");
                _result.Textures.Add(new ShaderGraphTextureInfo(textures[index].Name, slot, textures[index].Texture ?? string.Empty));
            }
            if (parameters.Count > 0 || textures.Count > 0) code.Append('\n');

            if (!Target.Mesh)
            {
                code.Append(_pipeline == ShaderAssetPipeline.Fullscreen
                    ? "struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };\n\n"
                    : "struct VSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1; };\n\n");
            }

            foreach ((string name, string helper, bool meshOnly) in HelperCode)
            {
                if (!Helpers.Contains(name) || (meshOnly && !Target.Mesh)) continue;
                code.Append(helper.Replace("\r\n", "\n", StringComparison.Ordinal)).Append("\n\n");
            }

            if (Target.Mesh)
            {
                if (vertexFunction is not null) code.Append(vertexFunction);
                code.Append("void Surface(GenesisSurfaceInput i, inout GenesisSurface s)\n{\n");
            }
            else
            {
                code.Append(_pipeline == ShaderAssetPipeline.Fullscreen
                    ? "float4 MainPS(PreviewVSOut IN) : SV_Target\n{\n    float4 gsgBase = SceneColor.Sample(GsgClamp, IN.UV);\n"
                    : "float4 MainPS(VSOut IN) : SV_Target\n{\n    float4 gsgBase = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;\n");
            }
            code.Append(_body);
            foreach (string line in assignments) code.Append("    ").Append(line).Append('\n');
            // No newline after the last brace: the code view hands back what it is given, exactly.
            code.Append('}');
            return code.ToString();
        }
    }
}

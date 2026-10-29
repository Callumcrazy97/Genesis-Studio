using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Shaders;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// Shader resources made from node graphs, as the Shader editor's Nodes view makes them: the graph
/// is kept in the resource and the code it makes is written beside it, checked by compiling it for
/// DX11 and for the SPIR-V path Vulkan uses.
/// </summary>
public static class ShaderCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new ShaderGraph(), new ShaderNodes()];

    // As the Shader editor saves: camelCase names, enums by name, indented.
    private static readonly JsonSerializerOptions EditorJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed class GraphInput
    {
        [Description("The Shader resource: its name, or its project-relative path (Assets/Rim.shader.json)."), RequiredInput]
        public string Shader { get; set; } = string.Empty;
        [Description("The node graph as the resource keeps it: \"nodes\" (id, type, x, y; a parameter's name and value; a texture parameter's texture, an Image's name; a Sample texture's mode; inputs: values of unwired inputs by port) and \"connections\" (fromNode, fromPort, toNode, toPort). shader.nodes lists the types and their ports."), RequiredInput]
        public JsonObject Graph { get; set; } = new();
        [Description("What the shader is for: Model, Terrain, Image, Particle, Fullscreen or Object. Default: as the resource has it.")]
        public string? Target { get; set; }
    }

    public sealed class ShaderGraph : AutomationCommand<GraphInput>
    {
        public override string Name => "shader.graph";
        public override string Description => "Builds a Shader resource from a node graph, as the Shader editor's Nodes view does: the graph is kept in the resource and the HLSL it makes (a surface shader for Model and Terrain, a pixel shader for Image, Particle and Fullscreen) becomes its code, checked by compiling it for DX11 and Vulkan (SPIR-V). Parameters show in the Inspector and are set by scripts by name. Returns the code. The file is backed up first.";

        protected override AutomationResult Execute(AutomationContext context, GraphInput input)
        {
            string full = ResolveShader(context, input.Shader);
            ShaderAssetDocument document = ShaderAssetDocument.ParseFlexible(File.ReadAllText(full));
            if (!string.IsNullOrWhiteSpace(input.Target))
            {
                if (!Enum.TryParse(input.Target.Trim(), ignoreCase: true, out ShaderTargetType target) || !Enum.IsDefined(target))
                    throw new AutomationException($"'{input.Target}' is not a target: {string.Join(", ", Enum.GetNames<ShaderTargetType>())}.");
                document.TargetType = target;
                document.Pipeline = target switch
                {
                    ShaderTargetType.Model or ShaderTargetType.Terrain => ShaderAssetPipeline.Mesh,
                    ShaderTargetType.Fullscreen => ShaderAssetPipeline.Fullscreen,
                    ShaderTargetType.Object => document.Pipeline,
                    _ => ShaderAssetPipeline.Sprite,
                };
            }

            ShaderGraphDocument graph;
            try
            {
                graph = JsonSerializer.Deserialize<ShaderGraphDocument>(input.Graph.ToJsonString(), EditorJson) ?? new ShaderGraphDocument();
            }
            catch (JsonException exception)
            {
                throw new AutomationException("The graph could not be read: " + exception.Message);
            }
            graph.Normalize();
            Complete(context, graph);
            document.Graph = graph;

            ShaderGraphGenerationResult result = ShaderGraphDocuments.Apply(document);
            if (!result.Succeeded)
                throw new AutomationException("The graph cannot make a shader: " + string.Join(" ", result.Errors));
            string source = document.ResolveCompiledSource();
            IReadOnlyList<string> includeRoots = ShaderCompiler.BuildDefaultIncludeSearchPaths(full, context.Root);
            try
            {
                ShaderCompiler.Compile(source, document.Entry, document.Profile, full, includeRoots);
                ShaderCompiler.CompileForBackend(source, document.Entry, GpuShaderStage.Pixel, GpuShaderBinaryFormat.SpirV, full, includeRoots);
            }
            catch (Exception exception) when (exception is not AutomationException)
            {
                throw new AutomationException("The graph's code does not compile: " + exception.Message);
            }

            ResourceBackupService.BackupBeforeOverwrite(full);
            File.WriteAllText(full, JsonSerializer.Serialize(document, EditorJson));
            JsonObject data = new()
            {
                ["path"] = context.Relative(full),
                ["target"] = document.TargetType.ToString(),
                ["parameters"] = new JsonArray(document.Parameters.Select(parameter => (JsonNode)new JsonObject
                {
                    ["name"] = parameter.Name,
                    ["type"] = parameter.Type,
                    ["value"] = new JsonArray(parameter.Value.Select(value => (JsonNode)JsonValue.Create(value)).ToArray()),
                }).ToArray()),
                ["textures"] = new JsonArray(result.Textures.Select(texture => (JsonNode)new JsonObject
                {
                    ["name"] = texture.Name,
                    ["slot"] = texture.Slot,
                    ["image"] = ShaderGraphDocuments.FindResource(document, texture.Name)?.Binding ?? string.Empty,
                }).ToArray()),
                ["warnings"] = new JsonArray(result.Warnings.Select(warning => (JsonNode?)JsonValue.Create(warning)).ToArray()),
                ["source"] = document.Source,
            };
            return AutomationResult.Success(
                $"Built {Path.GetFileName(full)} from {graph.Nodes.Count} nodes and {graph.Connections.Count} wires; its code compiles for DX11 and Vulkan.",
                data,
                context.Relative(full));
        }

        /// <summary>Gives nodes what the Shader editor would (ids, values, modes, names), checks Images and wires.</summary>
        private static void Complete(AutomationContext context, ShaderGraphDocument graph)
        {
            foreach (ShaderGraphNode node in graph.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id)) node.Id = graph.NewNodeId();
                ShaderGraphNodeDefinition definition = ShaderGraphCatalog.Find(node.Type)
                    ?? throw new AutomationException($"'{node.Type}' is not a node type; shader.nodes lists them.");
                node.Type = definition.Type;
                if (node.Value.Length == 0) node.Value = definition.DefaultValue.ToArray();
                if (definition.Modes.Count > 0)
                {
                    node.Mode = definition.Modes.FirstOrDefault(mode => string.Equals(mode, node.Mode, StringComparison.OrdinalIgnoreCase))
                        ?? (node.Mode.Length == 0 ? definition.Modes[0]
                            : throw new AutomationException($"'{node.Mode}' is not a mode of {definition.Title}: {string.Join(", ", definition.Modes)}."));
                }
                if (definition.Role == ShaderGraphNodeRole.Parameter && node.Name.Length == 0)
                    node.Name = ShaderGraphDocuments.UniqueParameterName(graph, definition.DefaultName, node);
                if (node.Texture.Length > 0)
                {
                    string image = ResourceNames.Resolve(context.Root, node.Texture, ResourceType.Image);
                    if (image.Length == 0) throw new AutomationException($"There is no Image '{node.Texture}' for the texture '{node.Name}'.");
                    node.Texture = ResourceNames.Name(context.Root, image);
                }
            }

            foreach (ShaderGraphConnection wire in graph.Connections)
            {
                if (ShaderGraphDocuments.ConnectionProblem(graph, wire.FromNode, wire.FromPort, wire.ToNode, wire.ToPort) is { } problem)
                    throw new AutomationException($"The wire from {wire.FromNode}.{wire.FromPort} to {wire.ToNode}.{wire.ToPort}: {problem}");
                wire.FromPort = ShaderGraphCatalog.Find(graph.FindNode(wire.FromNode)!.Type)!.FindOutput(wire.FromPort)!.Name;
                wire.ToPort = ShaderGraphCatalog.Find(graph.FindNode(wire.ToNode)!.Type)!.FindInput(wire.ToPort)!.Name;
            }
        }
    }

    public sealed class ShaderNodes : AutomationCommand<NoInput>
    {
        public override string Name => "shader.nodes";
        public override string Description => "The node types of shader.graph and the Shader editor's Nodes view: what each does, its inputs (with their value types and values when unwired) and outputs, its modes and starting value. Dynamic ports take the widest vector wired to the node.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, NoInput input)
        {
            static JsonArray Ports(IEnumerable<ShaderGraphPort> ports, bool inputs) => new(ports.Select(port =>
            {
                JsonObject entry = new() { ["name"] = port.Name, ["type"] = port.Type.ToString() };
                if (inputs && port.Source != ShaderGraphDefaultSource.Constant) entry["unwired"] = port.Source.ToString();
                else if (inputs && port.Default.Length > 0) entry["unwired"] = new JsonArray(port.Default.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());
                return (JsonNode)entry;
            }).ToArray());

            JsonArray nodes = new(ShaderGraphCatalog.Nodes.Select(definition =>
            {
                JsonObject entry = new()
                {
                    ["type"] = definition.Type,
                    ["title"] = definition.Title,
                    ["category"] = definition.Category,
                    ["description"] = definition.Description,
                    ["inputs"] = Ports(definition.Inputs, inputs: true),
                    ["outputs"] = Ports(definition.Outputs, inputs: false),
                };
                if (definition.Modes.Count > 0) entry["modes"] = new JsonArray(definition.Modes.Select(mode => (JsonNode?)JsonValue.Create(mode)).ToArray());
                if (definition.DefaultValue.Length > 0) entry["value"] = new JsonArray(definition.DefaultValue.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());
                return (JsonNode)entry;
            }).ToArray());
            return AutomationResult.Success($"{ShaderGraphCatalog.Nodes.Count} node types.", new JsonObject { ["nodes"] = nodes });
        }
    }

    private static string ResolveShader(AutomationContext context, string reference)
    {
        string trimmed = (reference ?? string.Empty).Trim();
        if (trimmed.EndsWith(".shader.json", StringComparison.OrdinalIgnoreCase))
        {
            string path = context.Resolve(trimmed);
            if (File.Exists(path)) return path;
        }
        string named = ResourceNames.Resolve(context.Root, trimmed, ResourceType.Shader);
        if (named.Length > 0 && File.Exists(named)) return named;
        throw new AutomationException($"There is no Shader resource '{reference}'; make one with resource.create (kind Shader).");
    }
}

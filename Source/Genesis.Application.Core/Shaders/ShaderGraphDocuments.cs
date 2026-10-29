using System.Globalization;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Shaders;

/// <summary>
/// Graph edits shared by the Shader editor's node view, automation and tests: making nodes with
/// unique names, wiring with type and loop checks, and writing a graph's HLSL into its resource.
/// </summary>
public static class ShaderGraphDocuments
{
    /// <summary>
    /// Writes the HLSL <see cref="ShaderAssetDocument.Graph"/> makes into the document's code (its
    /// selected pass). A parameter or texture new to the document starts with its node's value or
    /// Image; one the document already has keeps what the Inspector or the editor's controls gave
    /// it, and its node is brought up to date with that. On failure the code is left as it was and
    /// the result says why.
    /// </summary>
    public static ShaderGraphGenerationResult Apply(ShaderAssetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ShaderGraphGenerationResult result = ShaderGraphGenerator.Generate(document.Graph, document.Pipeline);
        if (!result.Succeeded) return result;
        HashSet<string> knownParameters = new((document.Parameters ?? []).Select(parameter => parameter.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownTextures = new((document.Resources ?? []).Select(resource => resource.Name), StringComparer.OrdinalIgnoreCase);

        document.Source = result.Source;
        document.Entry = "MainPS";
        document.VertexEntry = string.Empty;
        document.Preset = string.Empty;
        document.Passes ??= [];
        if (document.Passes.Count == 0) document.Passes.Add(new ShaderPassDefinition { Name = "Pass 0: Node graph" });
        document.ActivePassIndex = Math.Clamp(document.ActivePassIndex, 0, document.Passes.Count - 1);
        ShaderPassDefinition pass = document.Passes[document.ActivePassIndex];
        pass.Source = result.Source;
        pass.Entry = "MainPS";
        pass.VertexEntry = string.Empty;
        pass.SkinnedVertexEntry = string.Empty;

        if (!ShaderParameterReflection.TrySynchronize(document, out string? error))
        {
            result.Errors.Add(error ?? "The graph's parameters could not be read back.");
            return result;
        }
        foreach (ShaderGraphParameterInfo parameter in result.Parameters)
        {
            if (knownParameters.Contains(parameter.Name)) continue;
            ShaderParameterValue? value = FindParameter(document, parameter.Name);
            if (value is not null) value.Value = parameter.Value.Take(value.Value.Length).ToArray();
        }
        foreach (ShaderGraphTextureInfo texture in result.Textures)
        {
            if (knownTextures.Contains(texture.Name)) continue;
            ShaderResourceBinding? binding = FindResource(document, texture.Name);
            if (binding is not null) binding.Binding = texture.Binding;
        }

        foreach (ShaderGraphNode node in document.Graph!.Nodes)
        {
            if (string.Equals(node.Type, "TextureParameter", StringComparison.OrdinalIgnoreCase))
            {
                if (FindResource(document, node.Name) is { } binding) node.Texture = binding.Binding ?? string.Empty;
            }
            else if (ShaderGraphCatalog.Find(node.Type)?.Role == ShaderGraphNodeRole.Parameter
                && FindParameter(document, node.Name) is { } value)
            {
                node.Value = value.Value.ToArray();
            }
        }
        return result;
    }

    public static ShaderParameterValue? FindParameter(ShaderAssetDocument document, string name) =>
        document.Parameters?.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    public static ShaderResourceBinding? FindResource(ShaderAssetDocument document, string name) =>
        document.Resources?.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The graph a new node view starts with: the target's own picture times a Tint colour, into the
    /// Output's colour, so the shader first looks like the target with a tint.
    /// </summary>
    public static ShaderGraphDocument CreateStarter()
    {
        ShaderGraphDocument graph = new();
        ShaderGraphNode output = AddNode(graph, ShaderGraphCatalog.OutputType, 520, 40);
        ShaderGraphNode picture = AddNode(graph, "MainTexture", 40, 0);
        ShaderGraphNode tint = AddNode(graph, "ColorParameter", 40, 170);
        ShaderGraphNode multiply = AddNode(graph, "Multiply", 290, 60);
        Connect(graph, picture.Id, "RGBA", multiply.Id, "A");
        Connect(graph, tint.Id, "RGBA", multiply.Id, "B");
        Connect(graph, multiply.Id, "Out", output.Id, "Colour");
        return graph;
    }

    /// <summary>Adds a node of <paramref name="type"/> with its starting value, mode and a unique parameter name.</summary>
    public static ShaderGraphNode AddNode(ShaderGraphDocument graph, string type, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ShaderGraphNodeDefinition definition = ShaderGraphCatalog.Find(type)
            ?? throw new ArgumentException($"There is no '{type}' node.", nameof(type));
        graph.Normalize();
        ShaderGraphNode node = new()
        {
            Id = graph.NewNodeId(),
            Type = definition.Type,
            X = x,
            Y = y,
            Value = definition.DefaultValue.ToArray(),
            Mode = definition.Modes.Count > 0 ? definition.Modes[0] : string.Empty,
            Name = definition.Role == ShaderGraphNodeRole.Parameter ? UniqueParameterName(graph, definition.DefaultName) : string.Empty,
        };
        graph.Nodes.Add(node);
        return node;
    }

    /// <summary><paramref name="baseName"/>, or it with the first number that no other parameter of the graph uses.</summary>
    public static string UniqueParameterName(ShaderGraphDocument graph, string baseName, ShaderGraphNode? except = null)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Value";
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderGraphNode node in graph.Nodes ?? [])
        {
            if (!ReferenceEquals(node, except) && ShaderGraphCatalog.Find(node.Type)?.Role == ShaderGraphNodeRole.Parameter)
                taken.Add(node.Name);
        }
        if (!taken.Contains(baseName)) return baseName;
        for (int number = 2; ; number++)
        {
            string candidate = baseName + number.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Why a wire from one output to one input cannot be made, or null when it can.</summary>
    public static string? ConnectionProblem(ShaderGraphDocument graph, string fromNode, string fromPort, string toNode, string toPort)
    {
        ShaderGraphNode? from = graph.FindNode(fromNode);
        ShaderGraphNode? to = graph.FindNode(toNode);
        if (from is null || to is null) return "That node is not in the graph.";
        if (ReferenceEquals(from, to)) return "A node cannot feed itself.";
        ShaderGraphPort? output = ShaderGraphCatalog.Find(from.Type)?.FindOutput(fromPort);
        ShaderGraphPort? input = ShaderGraphCatalog.Find(to.Type)?.FindInput(toPort);
        if (output is null || input is null) return "That port is not on the node.";
        if ((output.Type == ShaderGraphValueType.Texture) != (input.Type == ShaderGraphValueType.Texture))
            return input.Type == ShaderGraphValueType.Texture
                ? "Only a texture parameter goes into a texture input."
                : "A texture goes into a Sample texture node's Texture input.";
        if (Feeds(graph, to.Id, from.Id)) return "That wire would make a loop.";
        return null;
    }

    /// <summary>Wires an output to an input, replacing what fed that input. False (and nothing changed) when <see cref="ConnectionProblem"/> objects.</summary>
    public static bool Connect(ShaderGraphDocument graph, string fromNode, string fromPort, string toNode, string toPort)
    {
        if (ConnectionProblem(graph, fromNode, fromPort, toNode, toPort) is not null) return false;
        ShaderGraphNodeDefinition target = ShaderGraphCatalog.Find(graph.FindNode(toNode)!.Type)!;
        ShaderGraphNodeDefinition source = ShaderGraphCatalog.Find(graph.FindNode(fromNode)!.Type)!;
        string inputName = target.FindInput(toPort)!.Name;
        string outputName = source.FindOutput(fromPort)!.Name;
        graph.Connections.RemoveAll(wire => wire.ToNode == toNode
            && string.Equals(wire.ToPort, inputName, StringComparison.OrdinalIgnoreCase));
        graph.Connections.Add(new ShaderGraphConnection { FromNode = fromNode, FromPort = outputName, ToNode = toNode, ToPort = inputName });
        return true;
    }

    public static bool Disconnect(ShaderGraphDocument graph, string toNode, string toPort) =>
        graph.Connections.RemoveAll(wire => wire.ToNode == toNode
            && string.Equals(wire.ToPort, toPort, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Removes nodes and every wire to or from them. The Output node stays.</summary>
    public static int RemoveNodes(ShaderGraphDocument graph, IEnumerable<string> ids)
    {
        HashSet<string> remove = new(ids, StringComparer.Ordinal);
        remove.RemoveWhere(id => string.Equals(graph.FindNode(id)?.Type, ShaderGraphCatalog.OutputType, StringComparison.OrdinalIgnoreCase));
        int removed = graph.Nodes.RemoveAll(node => remove.Contains(node.Id));
        graph.Connections.RemoveAll(wire => remove.Contains(wire.FromNode) || remove.Contains(wire.ToNode));
        return removed;
    }

    /// <summary>True when <paramref name="start"/>'s outputs reach <paramref name="target"/> through the wires.</summary>
    private static bool Feeds(ShaderGraphDocument graph, string start, string target)
    {
        Stack<string> pending = new([start]);
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (current == target) return true;
            if (!seen.Add(current)) continue;
            foreach (ShaderGraphConnection wire in graph.Connections)
                if (wire.FromNode == current) pending.Push(wire.ToNode);
        }
        return false;
    }
}

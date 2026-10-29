using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Shaders;

/// <summary>The kind of value a node port carries. Dynamic ports take the widest vector wired to the node.</summary>
public enum ShaderGraphValueType
{
    Dynamic = 0,
    Float = 1,
    Float2 = 2,
    Float3 = 3,
    Float4 = 4,
    Texture = 5,
}

/// <summary>Where an input nothing is wired to reads from when the node holds no value of its own for it.</summary>
public enum ShaderGraphDefaultSource
{
    Constant,
    Uv,
    Normal,
    ViewDirection,
    MainTexture,
}

public enum ShaderGraphNodeRole
{
    Input,
    Constant,
    Parameter,
    Texture,
    Math,
    Output,
}

public sealed record ShaderGraphPort(
    string Name,
    ShaderGraphValueType Type,
    float[] Default,
    ShaderGraphDefaultSource Source = ShaderGraphDefaultSource.Constant,
    string? Label = null)
{
    public string DisplayName => Label ?? Name;
}

/// <summary>One kind of node: its ports, and the HLSL each of its outputs is.</summary>
public sealed class ShaderGraphNodeDefinition
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required ShaderGraphNodeRole Role { get; init; }
    public IReadOnlyList<ShaderGraphPort> Inputs { get; init; } = [];
    public IReadOnlyList<ShaderGraphPort> Outputs { get; init; } = [];
    /// <summary>A parameter's or constant's starting value.</summary>
    public float[] DefaultValue { get; init; } = [];
    /// <summary>The choices <see cref="ShaderGraphNode.Mode"/> may take; the first is the default.</summary>
    public IReadOnlyList<string> Modes { get; init; } = [];
    /// <summary>A parameter's starting name; a number is added to keep names unique.</summary>
    public string DefaultName { get; init; } = string.Empty;
    internal Func<ShaderGraphEmitContext, string[]> Emit { get; init; } = _ => [];

    public ShaderGraphPort? FindInput(string name) =>
        Inputs.FirstOrDefault(port => string.Equals(port.Name, name, StringComparison.OrdinalIgnoreCase));

    public ShaderGraphPort? FindOutput(string name) =>
        Outputs.FirstOrDefault(port => string.Equals(port.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The node kinds the Shader editor's node view offers. Each writes plain HLSL for the target's
/// pipeline (a surface shader for Mesh targets, a pixel shader for Sprite and Fullscreen ones), so
/// the code a graph makes is the code the code view shows and the engine compiles.
/// </summary>
public static class ShaderGraphCatalog
{
    public const string OutputType = "Output";

    private static ShaderGraphPort In(string name, ShaderGraphValueType type, params float[] value) => new(name, type, value);
    private static ShaderGraphPort Out(string name, ShaderGraphValueType type) => new(name, type, []);

    private const ShaderGraphValueType D = ShaderGraphValueType.Dynamic;
    private const ShaderGraphValueType F1 = ShaderGraphValueType.Float;
    private const ShaderGraphValueType F2 = ShaderGraphValueType.Float2;
    private const ShaderGraphValueType F3 = ShaderGraphValueType.Float3;
    private const ShaderGraphValueType F4 = ShaderGraphValueType.Float4;
    private const ShaderGraphValueType Tex = ShaderGraphValueType.Texture;

    public static IReadOnlyList<ShaderGraphNodeDefinition> Nodes { get; } = Build();

    private static readonly Dictionary<string, ShaderGraphNodeDefinition> ByType =
        Nodes.ToDictionary(node => node.Type, StringComparer.OrdinalIgnoreCase);

    public static ShaderGraphNodeDefinition? Find(string? type) =>
        type is not null && ByType.TryGetValue(type, out ShaderGraphNodeDefinition? definition) ? definition : null;

    /// <summary>Node kinds whose title, category or description contains every word of <paramref name="search"/>.</summary>
    public static IEnumerable<ShaderGraphNodeDefinition> Search(string? search)
    {
        string[] words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Nodes.Where(node => node.Role != ShaderGraphNodeRole.Output && words.All(word =>
            node.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || node.Type.Contains(word, StringComparison.OrdinalIgnoreCase)
            || node.Category.Contains(word, StringComparison.OrdinalIgnoreCase)
            || node.Description.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<ShaderGraphNodeDefinition> Build()
    {
        List<ShaderGraphNodeDefinition> nodes = [];

        // ── Inputs ────────────────────────────────────────────────────────────────
        void Input(string type, string title, string description, ShaderGraphValueType valueType, Func<ShaderGraphEmitContext, string> expression)
        {
            nodes.Add(new ShaderGraphNodeDefinition
            {
                Type = type, Title = title, Category = "Inputs", Description = description, Role = ShaderGraphNodeRole.Input,
                Outputs = [Out("Out", valueType)],
                Emit = context => [expression(context)],
            });
        }
        Input("UV", "UV", "The mesh's or image's texture coordinates.", F2, context => context.Target.Uv);
        Input("WorldPosition", "World position", "Where the pixel is in the world (2D targets: its place on screen in pixels).", F3, context => context.Target.WorldPosition);
        Input("ObjectPosition", "Object position", "Where the pixel is on the drawn object, before it is turned, scaled and placed.", F3, context => context.ObjectPosition());
        Input("Normal", "Normal", "The mesh's own normal in world space (2D targets: towards the viewer).", F3, context => context.Target.Normal);
        Input("ViewDirection", "View direction", "Unit vector from the surface towards the camera.", F3, context => context.Target.ViewDirection);
        Input("Time", "Time", "Seconds since the game started (the Shader editor's preview clock).", F1, context => context.Target.Time);
        Input("ScreenUV", "Screen UV", "The pixel's place on screen, 0 to 1.", F2, context => context.Target.ScreenUv);
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "VertexColor", Title = "Vertex colour", Category = "Inputs", Role = ShaderGraphNodeRole.Input,
            Description = "The draw's colour: vertex colour, material colour and tint.",
            Outputs = [Out("RGBA", F4), Out("RGB", F3), Out("A", F1)],
            Emit = context => [context.Target.VertexColor(context), context.Var(0) + ".rgb", context.Var(0) + ".a"],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "MainTexture", Title = "Main texture", Category = "Inputs", Role = ShaderGraphNodeRole.Input,
            Description = "The target's own picture where the pixel is: the model's material texture, the Image, or the frame under a full-screen effect.",
            Outputs = [Out("RGBA", F4), Out("RGB", F3), Out("A", F1)],
            Emit = context => [context.Target.MainTexture(context), context.Var(0) + ".rgb", context.Var(0) + ".a"],
        });

        // ── Constants and parameters ─────────────────────────────────────────────
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "Float", Title = "Number", Category = "Constants", Role = ShaderGraphNodeRole.Constant,
            Description = "A fixed number.", DefaultValue = [1f],
            Outputs = [Out("Out", F1)],
            Emit = context => [context.Literal(context.Node.Value, F1)],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "Vector", Title = "Vector", Category = "Constants", Role = ShaderGraphNodeRole.Constant,
            Description = "A fixed vector of four numbers (or a colour).", DefaultValue = [1f, 1f, 1f, 1f],
            Outputs = [Out("Out", F4), Out("XYZ", F3), Out("XY", F2)],
            Emit = context => [context.Literal(context.Node.Value, F4), context.Var(0) + ".xyz", context.Var(0) + ".xy"],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "FloatParameter", Title = "Number parameter", Category = "Parameters", Role = ShaderGraphNodeRole.Parameter,
            Description = "A number shown in the Inspector and set by scripts (ShaderSetParameter).", DefaultValue = [1f], DefaultName = "Strength",
            Outputs = [Out("Out", F1)],
            Emit = context => [context.Node.Name],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "ColorParameter", Title = "Colour parameter", Category = "Parameters", Role = ShaderGraphNodeRole.Parameter,
            Description = "A colour shown in the Inspector and set by scripts (ShaderSetVector).", DefaultValue = [1f, 1f, 1f, 1f], DefaultName = "Tint",
            Outputs = [Out("RGBA", F4), Out("RGB", F3), Out("A", F1)],
            Emit = context => [context.PickedColour(context.Node.Name), context.Var(0) + ".rgb", context.Var(0) + ".a"],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "VectorParameter", Title = "Vector parameter", Category = "Parameters", Role = ShaderGraphNodeRole.Parameter,
            Description = "Four numbers shown in the Inspector and set by scripts (ShaderSetVector).", DefaultValue = [0f, 0f, 0f, 0f], DefaultName = "Offset",
            Outputs = [Out("Out", F4), Out("XYZ", F3), Out("XY", F2)],
            Emit = context => [context.Node.Name, context.Var(0) + ".xyz", context.Var(0) + ".xy"],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "TextureParameter", Title = "Texture parameter", Category = "Parameters", Role = ShaderGraphNodeRole.Parameter,
            Description = "An Image the shader reads, chosen in the Inspector or swapped by scripts (ShaderSetTexture).", DefaultName = "Pattern",
            Outputs = [Out("Texture", Tex)],
            Emit = context => [context.TextureReference(context.Node.Name)],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "SampleTexture", Title = "Sample texture", Category = "Textures", Role = ShaderGraphNodeRole.Texture,
            Description = "Reads a texture at a UV. Colour turns an ordinary picture into the colours the engine lights with; Normal map unpacks to -1..1.",
            Modes = ["Colour", "Data", "Normal map"],
            Inputs = [new ShaderGraphPort("Texture", Tex, [], ShaderGraphDefaultSource.MainTexture), new ShaderGraphPort("UV", F2, [0f, 0f], ShaderGraphDefaultSource.Uv)],
            Outputs = [Out("RGBA", F4), Out("RGB", F3), Out("R", F1), Out("G", F1), Out("B", F1), Out("A", F1)],
            Emit = context =>
            [
                context.Sample(context.In(0), context.In(1)),
                context.Var(0) + ".rgb", context.Var(0) + ".r", context.Var(0) + ".g", context.Var(0) + ".b", context.Var(0) + ".a",
            ],
        });

        // ── Maths ─────────────────────────────────────────────────────────────────
        void Math(string type, string title, string description, ShaderGraphPort[] inputs, ShaderGraphValueType output, Func<ShaderGraphEmitContext, string> expression)
        {
            nodes.Add(new ShaderGraphNodeDefinition
            {
                Type = type, Title = title, Category = "Maths", Description = description, Role = ShaderGraphNodeRole.Math,
                Inputs = inputs, Outputs = [Out("Out", output)],
                Emit = context => [expression(context)],
            });
        }
        void Unary(string type, string title, string description, Func<string, string> expression) =>
            Math(type, title, description, [In("In", D, 0f)], D, context => expression(context.In(0)));

        Math("Add", "Add", "A + B.", [In("A", D, 0f), In("B", D, 0f)], D, c => $"({c.In(0)} + {c.In(1)})");
        Math("Subtract", "Subtract", "A - B.", [In("A", D, 0f), In("B", D, 0f)], D, c => $"({c.In(0)} - {c.In(1)})");
        Math("Multiply", "Multiply", "A × B, component by component.", [In("A", D, 1f), In("B", D, 1f)], D, c => $"({c.In(0)} * {c.In(1)})");
        Math("Divide", "Divide", "A ÷ B.", [In("A", D, 1f), In("B", D, 1f)], D, c => $"({c.In(0)} / {c.In(1)})");
        Math("Lerp", "Lerp", "Blends from A (T = 0) to B (T = 1).", [In("A", D, 0f), In("B", D, 1f), In("T", D, 0.5f)], D, c => $"lerp({c.In(0)}, {c.In(1)}, {c.In(2)})");
        Math("Clamp", "Clamp", "Keeps In between Min and Max.", [In("In", D, 0f), In("Min", D, 0f), In("Max", D, 1f)], D, c => $"clamp({c.In(0)}, {c.In(1)}, {c.In(2)})");
        Unary("Saturate", "Saturate", "Clamps to 0..1.", x => $"saturate({x})");
        Unary("OneMinus", "One minus", "1 - In.", x => $"(1.0 - {x})");
        Math("Power", "Power", "A to the power B.", [In("A", D, 0f), In("B", D, 1f)], D, c => $"pow({c.In(0)}, {c.In(1)})");
        Unary("Sin", "Sine", "sin(In), In in radians.", x => $"sin({x})");
        Unary("Cos", "Cosine", "cos(In), In in radians.", x => $"cos({x})");
        Unary("Abs", "Absolute", "|In|.", x => $"abs({x})");
        Unary("Floor", "Floor", "The whole number at or below In.", x => $"floor({x})");
        Unary("Fract", "Fraction", "The part of In after the point (In - floor(In)).", x => $"frac({x})");
        Math("Dot", "Dot product", "A · B.", [In("A", D, 0f, 0f, 1f), In("B", D, 0f, 0f, 1f)], F1,
            c => c.Dynamic == ShaderGraphValueType.Float ? $"({c.In(0)} * {c.In(1)})" : $"dot({c.In(0)}, {c.In(1)})");
        Math("Cross", "Cross product", "A × B, a vector at right angles to both.", [In("A", F3, 1f, 0f, 0f), In("B", F3, 0f, 1f, 0f)], F3, c => $"cross({c.In(0)}, {c.In(1)})");
        Math("Normalize", "Normalize", "In made one unit long.", [In("In", D, 0f, 0f, 1f)], D,
            c => c.Dynamic == ShaderGraphValueType.Float ? $"sign({c.In(0)})" : $"normalize({c.In(0)})");
        Math("Length", "Length", "How long the vector In is.", [In("In", D, 0f, 0f, 1f)], F1,
            c => c.Dynamic == ShaderGraphValueType.Float ? $"abs({c.In(0)})" : $"length({c.In(0)})");
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "Fresnel", Title = "Fresnel", Category = "Maths", Role = ShaderGraphNodeRole.Math,
            Description = "0 where the surface faces the camera, 1 at its edges. Power 5 is physical; lower is a wider rim.",
            Inputs = [new ShaderGraphPort("Normal", F3, [0f, 0f, 1f], ShaderGraphDefaultSource.Normal),
                new ShaderGraphPort("View", F3, [0f, 0f, 1f], ShaderGraphDefaultSource.ViewDirection), In("Power", F1, 5f)],
            Outputs = [Out("Out", F1)],
            Emit = context => [context.Helper("GsgFresnel") + $"({context.In(0)}, {context.In(1)}, {context.In(2)})"],
        });
        Math("Remap", "Remap", "Maps In from the range In min..In max to Out min..Out max.",
            [In("In", D, 0f), new("InMin", D, [-1f], Label: "In min"), new("InMax", D, [1f], Label: "In max"),
                new("OutMin", D, [0f], Label: "Out min"), new("OutMax", D, [1f], Label: "Out max")], D,
            c => $"lerp({c.In(3)}, {c.In(4)}, ({c.In(0)} - {c.In(1)}) / ({c.In(2)} - {c.In(1)}))");
        Math("Step", "Step", "1 where In is at least Edge, else 0.", [In("Edge", D, 0.5f), In("In", D, 0f)], D, c => $"step({c.In(0)}, {c.In(1)})");
        Math("Smoothstep", "Smoothstep", "A smooth 0 to 1 as In goes from Edge 0 to Edge 1.",
            [new("Edge0", D, [0f], Label: "Edge 0"), new("Edge1", D, [1f], Label: "Edge 1"), In("In", D, 0.5f)], D,
            c => $"smoothstep({c.In(0)}, {c.In(1)}, {c.In(2)})");
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "Split", Title = "Split", Category = "Maths", Role = ShaderGraphNodeRole.Math,
            Description = "A vector's four components.",
            Inputs = [In("In", F4, 0f, 0f, 0f, 0f)],
            Outputs = [Out("R", F1), Out("G", F1), Out("B", F1), Out("A", F1)],
            Emit = context => [context.In(0) + ".x", context.In(0) + ".y", context.In(0) + ".z", context.In(0) + ".w"],
        });
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = "Combine", Title = "Combine", Category = "Maths", Role = ShaderGraphNodeRole.Math,
            Description = "A vector made from four numbers.",
            Inputs = [In("R", F1, 0f), In("G", F1, 0f), In("B", F1, 0f), In("A", F1, 1f)],
            Outputs = [Out("RGBA", F4), Out("RGB", F3), Out("RG", F2)],
            Emit = context => [$"float4({context.In(0)}, {context.In(1)}, {context.In(2)}, {context.In(3)})", context.Var(0) + ".rgb", context.Var(0) + ".rg"],
        });

        // ── Output ────────────────────────────────────────────────────────────────
        nodes.Add(new ShaderGraphNodeDefinition
        {
            Type = OutputType, Title = "Output", Category = "Output", Role = ShaderGraphNodeRole.Output,
            Description = "What the shader gives the engine. An input left unwired keeps the target's own value (a model's material, an Image's pixels) unless it is given a value here. Normal is tangent space, -1..1; Normal, Roughness and Metalness apply to Mesh targets only.",
            Inputs =
            [
                In("Colour", F3, 1f, 1f, 1f), In("Normal", F3, 0f, 0f, 1f), In("Emission", F3, 0f, 0f, 0f),
                In("Roughness", F1, 0.5f), In("Metalness", F1, 0f), In("Alpha", F1, 1f),
            ],
        });
        return nodes;
    }
}

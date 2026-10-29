using System.Text.RegularExpressions;
using Genesis.Rendering.Primitives;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// A surface shader's vertex stage on the Code page: the Vertex button goes to the shader's
/// <c>void Vertex(GenesisVertexInput i, inout GenesisVertex v)</c>, or adds an empty one.
/// </summary>
public sealed partial class ShaderEditorControl
{
    /// <summary>The function the Code page's Vertex button adds to a surface shader that has none.</summary>
    public const string VertexFunctionTemplate = """
        // Moves the mesh's vertices before they are drawn: object space, after skinning, before the
        // world transform. i has Time, ObjectPosition, WorldPosition, CameraPosition, Wind and
        // ObjectWind; the shader's parameters can be read here too. Documentation/MeshShaders.md.
        void Vertex(GenesisVertexInput i, inout GenesisVertex v)
        {
            // v.Position.y += sin(i.Time * 2.0 + v.Position.x * 4.0) * 0.05;
        }
        """;

    /// <summary>True when the shader is a surface shader with a Vertex function.</summary>
    public bool HasVertexStage => MeshSurfaceShaders.HasVertexStage(_code.CodeText);

    /// <summary>
    /// Adds an empty Vertex function to a surface shader written as code (one undo step). False for
    /// a shader that already has one, is not a surface shader, or is made from nodes.
    /// </summary>
    public bool AddVertexFunction()
    {
        string source = _code.CodeText;
        if (_document.Graph is not null || !MeshSurfaceShaders.IsSurfaceSource(source) || MeshSurfaceShaders.HasVertexStage(source))
            return false;
        SetSource(source.TrimEnd() + "\n\n" + VertexFunctionTemplate.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        return true;
    }

    /// <summary>The Vertex button for a surface shader; false when the shader is not one.</summary>
    private bool ShowSurfaceVertexStage()
    {
        if (!MeshSurfaceShaders.IsSurfaceSource(_code.CodeText)) return false;
        string status = "Vertex stage · Vertex(i, v) runs in the engine's vertex shaders, after skinning and before the world transform.";
        if (!HasVertexStage)
        {
            if (_document.Graph is not null)
            {
                _statusLabel.Text = "Made from nodes: the graph writes this code, so give the Output a Vertex offset in Nodes instead.";
                return true;
            }
            AddVertexFunction();
            status = "Added an empty Vertex function: it moves the mesh's vertices before they are drawn (Undo takes it away).";
        }
        Match function = Regex.Match(_code.CodeText, @"\bvoid\s+Vertex\s*\(");
        if (function.Success) _code.MoveCaret(function.Index);
        _code.Focus();
        _statusLabel.Text = status;
        return true;
    }
}

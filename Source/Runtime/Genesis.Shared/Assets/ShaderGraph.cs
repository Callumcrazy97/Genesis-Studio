using System;
using System.Collections.Generic;
using System.Linq;

namespace Genesis.Shared.Assets
{
    /// <summary>
    /// A Shader resource's node graph, kept in its <c>*.shader.json</c> as <c>graph</c>. The Shader
    /// editor writes the HLSL the graph makes into the resource's <c>source</c> at every change, so
    /// a game never reads the graph: it is the editor's, and a shader without one is code.
    /// </summary>
    public sealed class ShaderGraphDocument
    {
        public int Version { get; set; } = 1;
        public List<ShaderGraphNode> Nodes { get; set; } = new();
        public List<ShaderGraphConnection> Connections { get; set; } = new();

        public ShaderGraphNode FindNode(string id)
        {
            if (string.IsNullOrEmpty(id) || Nodes is null) return null;
            foreach (ShaderGraphNode node in Nodes)
            {
                if (node is not null && string.Equals(node.Id, id, StringComparison.Ordinal))
                    return node;
            }
            return null;
        }

        /// <summary>The connection that feeds an input port, if any (an input takes one).</summary>
        public ShaderGraphConnection FindInput(string nodeId, string port)
        {
            if (Connections is null) return null;
            foreach (ShaderGraphConnection connection in Connections)
            {
                if (connection is not null
                    && string.Equals(connection.ToNode, nodeId, StringComparison.Ordinal)
                    && string.Equals(connection.ToPort, port, StringComparison.OrdinalIgnoreCase))
                {
                    return connection;
                }
            }
            return null;
        }

        /// <summary>A node id not yet used in this graph (<c>n1</c>, <c>n2</c>...).</summary>
        public string NewNodeId()
        {
            int next = 1;
            foreach (ShaderGraphNode node in Nodes ?? new List<ShaderGraphNode>())
            {
                if (node?.Id is { Length: > 1 } id && id[0] == 'n'
                    && int.TryParse(id.AsSpan(1), out int number) && number >= next)
                {
                    next = number + 1;
                }
            }
            return "n" + next;
        }

        /// <summary>Fills missing lists after loading, so the editor and generator never meet nulls.</summary>
        public void Normalize()
        {
            Nodes ??= new List<ShaderGraphNode>();
            Connections ??= new List<ShaderGraphConnection>();
            Nodes.RemoveAll(node => node is null);
            Connections.RemoveAll(connection => connection is null);
            foreach (ShaderGraphNode node in Nodes)
            {
                node.Id ??= string.Empty;
                node.Type ??= string.Empty;
                node.Name ??= string.Empty;
                node.Mode ??= string.Empty;
                node.Texture ??= string.Empty;
                node.Value ??= Array.Empty<float>();
                // A loaded dictionary compares ports by exact case; ports are matched without it.
                var inputs = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, float[]> pair in node.Inputs ?? new Dictionary<string, float[]>())
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { Length: > 0 })
                        inputs[pair.Key] = pair.Value;
                }
                node.Inputs = inputs;
            }
        }

        public ShaderGraphDocument Clone() => new()
        {
            Version = Version,
            Nodes = (Nodes ?? new List<ShaderGraphNode>()).Select(node => node.Clone()).ToList(),
            Connections = (Connections ?? new List<ShaderGraphConnection>()).Select(connection => connection.Clone()).ToList(),
        };
    }

    /// <summary>One node: its kind (<see cref="Type"/>, a name from the editor's node catalog) and settings.</summary>
    public sealed class ShaderGraphNode
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public float X { get; set; }
        public float Y { get; set; }
        /// <summary>A parameter's name in <c>GenesisParameters</c> (or a texture's), as scripts and the Inspector name it.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>A parameter's or constant's value.</summary>
        public float[] Value { get; set; } = Array.Empty<float>();
        /// <summary>A choice the node offers (a texture sample reads Colour, Data or a Normal map).</summary>
        public string Mode { get; set; } = string.Empty;
        /// <summary>A texture parameter's Image resource.</summary>
        public string Texture { get; set; } = string.Empty;
        /// <summary>Values of inputs that nothing is connected to, by port name.</summary>
        public Dictionary<string, float[]> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public ShaderGraphNode Clone() => new()
        {
            Id = Id,
            Type = Type,
            X = X,
            Y = Y,
            Name = Name,
            Value = Value?.ToArray() ?? Array.Empty<float>(),
            Mode = Mode,
            Texture = Texture,
            Inputs = new Dictionary<string, float[]>(
                (Inputs ?? new Dictionary<string, float[]>()).ToDictionary(pair => pair.Key, pair => pair.Value?.ToArray() ?? Array.Empty<float>()),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>A wire from one node's output port to another node's input port.</summary>
    public sealed class ShaderGraphConnection
    {
        public string FromNode { get; set; } = string.Empty;
        public string FromPort { get; set; } = string.Empty;
        public string ToNode { get; set; } = string.Empty;
        public string ToPort { get; set; } = string.Empty;

        public ShaderGraphConnection Clone() => new()
        {
            FromNode = FromNode,
            FromPort = FromPort,
            ToNode = ToNode,
            ToPort = ToPort,
        };
    }
}

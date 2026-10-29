using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Shaders;
using Genesis.Application.Editors.Suite.Assets.ShaderNodes;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// The node view: a graph of nodes that writes the shader's HLSL. The graph lives in the resource
/// (<see cref="ShaderAssetDocument.Graph"/>); every edit is one undo step that writes the code
/// again, so the code view, the compile, the preview and the game all use what the graph made.
/// A shader written as code shows code until a graph is started, which replaces the code.
/// </summary>
public sealed partial class ShaderEditorControl
{
    private ShaderGraphView? _graphView;
    private Panel? _nodesEmptyState;
    private FlowLayoutPanel? _nodeProperties;
    private Panel? _nodesPreviewHost;
    private Control? _viewportHome;
    private Label? _nodesHint;
    private Label? _nodesErrors;
    private Label? _codeGraphBanner;
    private EditorCommandBar? _nodesToolbar;
    private ToolStripButton? _shaderNodesButton;
    private ToolStripButton? _nodesPlayButton;
    private bool _rebuildingNodeProperties;
    private bool _editingFromNodeProperties;
    private IReadOnlyList<string> _graphErrors = [];
    private IReadOnlyList<string> _graphWarnings = [];

    public bool HasNodeGraph => _document.Graph is not null;

    /// <summary>The graph being edited (null for a shader written as code). Change it through the editor's methods.</summary>
    public ShaderGraphDocument? NodeGraph => _document.Graph;

    public ShaderGraphView? NodeGraphView => _graphView;

    /// <summary>Why the graph cannot make a shader, when it cannot.</summary>
    public IReadOnlyList<string> NodeGraphErrors => _graphErrors;

    public IReadOnlyList<string> NodeGraphWarnings => _graphWarnings;

    /// <summary>True while the preview is shown beside the graph.</summary>
    public bool NodesPreviewShown => _nodesPreviewHost is { Visible: true } && _viewport.Parent == _nodesPreviewHost;

    private Control BuildShaderNodesDock()
    {
        _viewportHome = _viewport.Parent;
        TableLayoutPanel root = new()
        {
            BackColor = EditorChrome.Border,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Name = "ShaderNodesWorkspace",
            Padding = Padding.Empty,
            RowCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Panel canvas = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill, Margin = Padding.Empty };
        _nodesToolbar = EditorChrome.MakeToolbar();
        _nodesToolbar.Name = "ShaderNodesToolbar";
        _nodesToolbar.Items.Add(new ToolStripLabel("Nodes") { ForeColor = EditorChrome.Text });
        _nodesToolbar.Items.Add(EditorChrome.ToolButton("Add node", "Add a node (Space, a double-click or right-click on the graph)", () =>
            ShowNodeMenu(new Point(Math.Max(40, (_graphView?.Width ?? 400) / 2), Math.Max(40, (_graphView?.Height ?? 300) / 2)), null)));
        _nodesToolbar.Items.Add(EditorChrome.ToolButton("Delete", "Delete the selected nodes (Delete)", () => _graphView?.DeleteSelection()));
        _nodesToolbar.Items.Add(EditorChrome.ToolButton("Frame", "Fit the whole graph in view (F)", () => _graphView?.FrameAll()));
        _nodesToolbar.Items.Add(new ToolStripSeparator());
        _nodesToolbar.Items.Add(EditorChrome.ToolButton("Detach graph", "Keep the code the graph made and edit it by hand; the graph is removed", DetachNodeGraphFromUi));

        _graphView = new ShaderGraphView { Dock = DockStyle.Fill };
        _graphView.Edit = (label, mutation) => EditNodeGraph(label, mutation);
        _graphView.AddNodeRequested = ShowNodeMenu;
        _graphView.SelectionChanged += (_, _) => RebuildNodeProperties();
        _graphView.StatusMessage += (_, message) => _statusLabel.Text = message;
        _nodesHint = new Label
        {
            AutoEllipsis = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Bottom,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = Math.Max(26, EditorChrome.SmallFont.Height + 10),
            Padding = new Padding(10, 5, 10, 0),
            Text = "Space, double-click or right-click: add a node · drag from a port: wire · drag the background: pan · wheel: zoom · Delete: remove",
        };
        _nodesErrors = new Label
        {
            AutoEllipsis = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            ForeColor = EditorChrome.Error,
            Height = Math.Max(28, EditorChrome.BaseFont.Height + 12),
            Name = "ShaderNodesErrors",
            Padding = new Padding(10, 6, 10, 0),
            Visible = false,
        };
        _nodesEmptyState = BuildNodesEmptyState();
        canvas.Controls.Add(_graphView);
        canvas.Controls.Add(_nodesEmptyState);
        canvas.Controls.Add(_nodesErrors);
        canvas.Controls.Add(_nodesHint);
        canvas.Controls.Add(_nodesToolbar);
        _nodesEmptyState.BringToFront();
        _graphView.BringToFront();

        Panel side = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Fill, Margin = Padding.Empty };
        _nodeProperties = new FlowLayoutPanel
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Name = "ShaderNodeProperties",
            Padding = new Padding(10, 4, 10, 10),
            WrapContents = false,
        };
        _nodesPreviewHost = new Panel { BackColor = EditorChrome.Canvas, Dock = DockStyle.Bottom, Height = 280, Name = "ShaderNodesPreview" };
        EditorCommandBar previewBar = EditorChrome.MakeToolbar();
        previewBar.Items.Add(new ToolStripLabel("Preview") { ForeColor = EditorChrome.Text });
        _nodesPlayButton = EditorChrome.ToolButton("Play", "Play or pause the preview's time", () =>
        {
            TogglePlayback();
            if (_nodesPlayButton is not null) _nodesPlayButton.Checked = _playing;
        }, toggle: true);
        previewBar.Items.Add(_nodesPlayButton);
        previewBar.Items.Add(EditorChrome.ToolButton("Frame", "Fit the preview target in view", FramePreviewTarget));
        previewBar.Items.Add(EditorChrome.ToolButton("Larger", "Open the full preview", () => SelectShaderWorkspaceMode("Preview")));
        _nodesPreviewHost.Controls.Add(previewBar);
        side.Controls.Add(_nodeProperties);
        side.Controls.Add(EditorChrome.SectionLabel("NODE"));
        side.Controls.Add(_nodesPreviewHost);
        _nodeProperties.BringToFront();
        _nodeProperties.ClientSizeChanged += (_, _) =>
        {
            if (!_rebuildingNodeProperties) ResizeNodeProperties();
        };

        root.Controls.Add(canvas, 0, 0);
        root.Controls.Add(side, 1, 0);

        // The code view says where its code comes from while a graph writes it.
        _codeGraphBanner = new Label
        {
            AutoEllipsis = true,
            BackColor = EditorChrome.AccentSoft,
            Cursor = Cursors.Hand,
            Dock = DockStyle.Top,
            ForeColor = EditorChrome.Text,
            Height = Math.Max(28, EditorChrome.BaseFont.Height + 12),
            Name = "ShaderCodeFromGraph",
            Padding = new Padding(10, 6, 10, 0),
            Text = "Made from the node graph, which writes this code at every change. Click to open Nodes; Detach graph there to edit the code by hand.",
            Visible = false,
        };
        _codeGraphBanner.Click += (_, _) => SelectShaderWorkspaceMode("Nodes");
        if (_code.Parent is { } codeHost)
        {
            codeHost.Controls.Add(_codeGraphBanner);
            _codeGraphBanner.BringToFront();
            _code.BringToFront();
        }
        InspectorStateChanged += (_, _) => RefreshNodeErrors();
        return root;
    }

    private Panel BuildNodesEmptyState()
    {
        Panel panel = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill, Name = "ShaderNodesEmpty", Visible = false };
        TableLayoutPanel centre = new() { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 4, BackColor = EditorChrome.Canvas };
        centre.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        centre.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        centre.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        centre.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        Label text = new()
        {
            Anchor = AnchorStyles.None,
            AutoSize = false,
            ForeColor = EditorChrome.Text,
            Size = new Size(520, Math.Max(96, EditorChrome.BaseFont.Height * 6)),
            Text = "This shader is written as code.\n\nStart a node graph to build it from nodes instead. From then on the graph writes the shader's code, so the code it has now is replaced (Undo brings it back).",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Button start = new()
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(180, 34),
            Name = "ShaderStartNodeGraph",
            Text = "Start a node graph",
        };
        EditorChrome.StyleField(start);
        start.Click += (_, _) => StartNodeGraphFromUi();
        centre.Controls.Add(text, 0, 1);
        centre.Controls.Add(start, 0, 2);
        panel.Controls.Add(centre);
        return panel;
    }

    /// <summary>Opens the node view.</summary>
    public void ShowNodes() => SelectShaderWorkspaceMode("Nodes");

    /// <summary>
    /// Starts a node graph for this shader (the target's picture times a Tint, into the Output) and
    /// writes its code over the shader's. Asks nothing: the Shader editor's button asks first.
    /// </summary>
    public void StartNodeGraph()
    {
        if (_document.Graph is null)
        {
            RecordDocumentEdit("Start a node graph", () =>
            {
                _document.Graph = ShaderGraphDocuments.CreateStarter();
                RegenerateGraphSource(showDiagnostics: true);
                MarkDirty();
            });
        }
        // The preview's place beside the graph is shown first, then the preview moves into it.
        RefreshNodeView();
        SelectShaderWorkspaceMode("Nodes");
        _graphView?.FrameAll();
        CompileNow();
    }

    private void StartNodeGraphFromUi()
    {
        if (_document.Graph is null && !string.IsNullOrWhiteSpace(_code.CodeText)
            && MessageBox.Show(
                FindForm(),
                "Start a node graph for this shader?\n\nFrom now on the graph writes the shader's code, so the code it has now is replaced. Undo (Ctrl+Z) brings it back.",
                "Start a node graph",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        StartNodeGraph();
    }

    /// <summary>Removes the graph and keeps the code it made, to be edited by hand. Asks nothing.</summary>
    public void DetachNodeGraph()
    {
        if (_document.Graph is null) return;
        RecordDocumentEdit("Detach the node graph", () =>
        {
            _document.Graph = null;
            MarkDirty();
        });
        _graphErrors = [];
        _graphWarnings = [];
        RefreshNodeView();
        SelectShaderWorkspaceMode("Code");
    }

    private void DetachNodeGraphFromUi()
    {
        if (_document.Graph is null) return;
        if (MessageBox.Show(
                FindForm(),
                "Detach the node graph?\n\nThe code the graph made stays and can be edited by hand; the graph is removed. Undo (Ctrl+Z) brings it back.",
                "Detach the node graph",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        DetachNodeGraph();
    }

    // ── Graph edits: each is one undo step and writes the code again ──────────

    private bool EditNodeGraph(string label, Action<ShaderGraphDocument> mutation)
    {
        if (_document.Graph is null) return false;
        RecordDocumentEdit(label, () =>
        {
            mutation(_document.Graph!);
            RegenerateGraphSource(showDiagnostics: true);
            MarkDirty();
        });
        RefreshNodeView(rebuildProperties: !_editingFromNodeProperties);
        QueueLiveCompile();
        return true;
    }

    /// <summary>Adds a node of a catalog kind (<see cref="ShaderGraphCatalog"/>) at a graph point; its id, or null.</summary>
    public string? AddGraphNode(string type, float x, float y)
    {
        if (_document.Graph is null || ShaderGraphCatalog.Find(type) is not { Role: not ShaderGraphNodeRole.Output } definition) return null;
        string? id = null;
        EditNodeGraph($"Add {definition.Title} node", graph => id = ShaderGraphDocuments.AddNode(graph, definition.Type, MathF.Round(x), MathF.Round(y)).Id);
        if (id is not null) _graphView?.SelectNode(id);
        return id;
    }

    public bool ConnectGraphNodes(string fromNode, string fromPort, string toNode, string toPort)
    {
        if (_document.Graph is null) return false;
        if (ShaderGraphDocuments.ConnectionProblem(_document.Graph, fromNode, fromPort, toNode, toPort) is { } problem)
        {
            _statusLabel.Text = problem;
            return false;
        }
        return EditNodeGraph("Connect shader nodes", graph => ShaderGraphDocuments.Connect(graph, fromNode, fromPort, toNode, toPort));
    }

    public bool DisconnectGraphInput(string node, string port) =>
        _document.Graph?.FindInput(node, port) is not null
        && EditNodeGraph("Disconnect shader nodes", graph => ShaderGraphDocuments.Disconnect(graph, node, port));

    public bool RemoveGraphNodes(params string[] ids) =>
        _document.Graph is not null && ids.Any(id => _document.Graph.FindNode(id) is { Type: not ShaderGraphCatalog.OutputType })
        && EditNodeGraph(ids.Length == 1 ? "Delete shader node" : "Delete shader nodes", graph => ShaderGraphDocuments.RemoveNodes(graph, ids));

    public bool MoveGraphNode(string id, float x, float y) =>
        _document.Graph?.FindNode(id) is not null
        && EditNodeGraph("Move shader node", graph =>
        {
            ShaderGraphNode node = graph.FindNode(id)!;
            node.X = MathF.Round(x);
            node.Y = MathF.Round(y);
        });

    /// <summary>Renames a parameter node (its name in the Inspector and in scripts), keeping its value.</summary>
    public bool SetGraphNodeName(string id, string name)
    {
        ShaderGraphNode? node = _document.Graph?.FindNode(id);
        if (node is null || ShaderGraphCatalog.Find(node.Type)?.Role != ShaderGraphNodeRole.Parameter) return false;
        name = name?.Trim() ?? string.Empty;
        if (string.Equals(name, node.Name, StringComparison.Ordinal)) return true;
        string? problem = ShaderGraphGenerator.ParameterNameProblem(name);
        if (problem is null && !string.Equals(ShaderGraphDocuments.UniqueParameterName(_document.Graph!, name, node), name, StringComparison.OrdinalIgnoreCase))
            problem = $"Another parameter is already named '{name}'.";
        if (problem is not null)
        {
            _statusLabel.Text = problem;
            return false;
        }
        return EditNodeGraph($"Rename shader parameter to '{name}'", graph =>
        {
            ShaderGraphNode target = graph.FindNode(id)!;
            // The renamed parameter is new to the document: it starts from what the old one had.
            if (ShaderGraphDocuments.FindParameter(_document, target.Name) is { } value) target.Value = value.Value.ToArray();
            if (ShaderGraphDocuments.FindResource(_document, target.Name) is { } texture) target.Texture = texture.Binding ?? string.Empty;
            target.Name = name;
        });
    }

    /// <summary>Sets a parameter's or constant's value (a parameter's is also the Inspector's value).</summary>
    public bool SetGraphNodeValue(string id, params float[] value)
    {
        ShaderGraphNode? node = _document.Graph?.FindNode(id);
        ShaderGraphNodeDefinition? definition = node is null ? null : ShaderGraphCatalog.Find(node.Type);
        if (node is null || definition is null || definition.DefaultValue.Length == 0) return false;
        float[] next = FitValues(value, definition.DefaultValue.Length);
        return EditNodeGraph($"Set {definition.Title} value", graph =>
        {
            ShaderGraphNode target = graph.FindNode(id)!;
            target.Value = next;
            if (definition.Role == ShaderGraphNodeRole.Parameter && ShaderGraphDocuments.FindParameter(_document, target.Name) is { } parameter)
                parameter.Value = FitValues(next, parameter.Value.Length);
        });
    }

    /// <summary>Gives an unwired input a value of its own (null takes it away: the port's default, or for the Output the target's own).</summary>
    public bool SetGraphNodeInput(string id, string port, float[]? value)
    {
        ShaderGraphNode? node = _document.Graph?.FindNode(id);
        ShaderGraphPort? input = node is null ? null : ShaderGraphCatalog.Find(node.Type)?.FindInput(port);
        if (node is null || input is null || input.Type == ShaderGraphValueType.Texture) return false;
        int width = input.Type == ShaderGraphValueType.Dynamic ? Math.Clamp(value?.Length ?? 1, 1, 4) : (int)input.Type;
        return EditNodeGraph(value is null ? $"Clear {input.DisplayName}" : $"Set {input.DisplayName}", graph =>
        {
            ShaderGraphNode target = graph.FindNode(id)!;
            if (value is null) target.Inputs.Remove(input.Name);
            else target.Inputs[input.Name] = FitValues(value, width);
        });
    }

    public bool SetGraphNodeMode(string id, string mode)
    {
        ShaderGraphNode? node = _document.Graph?.FindNode(id);
        ShaderGraphNodeDefinition? definition = node is null ? null : ShaderGraphCatalog.Find(node.Type);
        string? chosen = definition?.Modes.FirstOrDefault(candidate => string.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase));
        if (node is null || chosen is null) return false;
        return EditNodeGraph($"Read the texture as {chosen}", graph => graph.FindNode(id)!.Mode = chosen);
    }

    /// <summary>Chooses a texture parameter's Image (an Image resource's name, or empty for none).</summary>
    public bool SetGraphNodeTexture(string id, string? image)
    {
        ShaderGraphNode? node = _document.Graph?.FindNode(id);
        if (node is null || !string.Equals(node.Type, "TextureParameter", StringComparison.OrdinalIgnoreCase)) return false;
        string next = (image ?? string.Empty).Replace('\\', '/').Trim();
        if (next.Length > 0)
        {
            string resolved = ResourceNames.Resolve(ProjectRoot, next, ResourceType.Image);
            if (resolved.Length == 0) return false;
            next = ResourceNames.Name(ProjectRoot, resolved);
            if (!_imageAssets.Contains(next, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return EditNodeGraph($"Choose the Image of '{node.Name}'", graph =>
        {
            graph.FindNode(id)!.Texture = next;
            if (ShaderGraphDocuments.FindResource(_document, node.Name) is { } binding) binding.Binding = next;
            InvalidatePreviewTarget();
        });
    }

    private static float[] FitValues(float[]? values, int count)
    {
        values ??= [];
        float[] fitted = new float[Math.Max(1, count)];
        for (int index = 0; index < fitted.Length; index++)
            fitted[index] = index < values.Length ? values[index] : values.Length == 1 ? values[0] : 0f;
        return fitted;
    }

    /// <summary>
    /// Writes what the graph makes into the document and the code view. False (with the reasons in
    /// the diagnostics when asked) when the graph cannot make a shader; the code is then unchanged.
    /// </summary>
    private bool RegenerateGraphSource(bool showDiagnostics)
    {
        if (_document.Graph is null)
        {
            _graphErrors = [];
            _graphWarnings = [];
            return true;
        }

        ShaderGraphGenerationResult result = ShaderGraphDocuments.Apply(_document);
        _graphErrors = result.Errors.ToArray();
        _graphWarnings = result.Warnings.ToArray();
        if (!result.Succeeded)
        {
            if (showDiagnostics) ShowGraphErrors();
            return false;
        }

        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        try
        {
            if (!string.Equals(_code.CodeText, _document.Source, StringComparison.Ordinal)) _code.CodeText = _document.Source;
            if (!string.Equals(_entryBox.Text, _document.Entry, StringComparison.Ordinal)) _entryBox.Text = _document.Entry;
            if (_vertexEntryBox.Text.Length > 0) _vertexEntryBox.Text = string.Empty;
        }
        finally { _updatingUi = wasUpdating; }
        return true;
    }

    private void ShowGraphErrors()
    {
        LastCompileSucceeded = false;
        _pendingPreviewApply = false;
        _output.BeginUpdate();
        _output.Items.Clear();
        foreach (string error in _graphErrors)
        {
            _output.Items.Add(new ListViewItem("✕ Node graph · " + error) { ForeColor = EditorChrome.Error, ToolTipText = error });
        }
        _output.EndUpdate();
        ResizeDiagnosticsColumn();
        _compileIndicator.Text = "● Graph error";
        _compileIndicator.ForeColor = EditorChrome.Error;
        _statusLabel.Text = _previewApplied
            ? "The node graph cannot make a shader · the preview shows the last one that worked."
            : "The node graph cannot make a shader yet · see Diagnostics.";
        SetDiagnosticsVisible(true, userChoice: false);
        RefreshNodeErrors();
    }

    private void RefreshNodeErrors()
    {
        if (_nodesErrors is null) return;
        string text = string.Empty;
        if (_document.Graph is not null)
        {
            if (_graphErrors.Count > 0) text = "✕ " + _graphErrors[0] + (_graphErrors.Count > 1 ? $"  (+{_graphErrors.Count - 1} more in Diagnostics)" : string.Empty);
            else if (!LastCompileSucceeded)
            {
                string first = _output.Items.Cast<ListViewItem>().Select(item => item.Text)
                    .FirstOrDefault(line => line.StartsWith("✕", StringComparison.Ordinal)) ?? "✕ The code the graph made does not compile.";
                text = first + "  (see Diagnostics)";
            }
        }
        _nodesErrors.Text = text;
        _nodesErrors.Visible = text.Length > 0;
    }

    // ── View state ───────────────────────────────────────────────────────────

    private void RefreshNodeView(bool rebuildProperties = true)
    {
        if (_graphView is null) return;
        bool graph = _document.Graph is not null;
        if (!ReferenceEquals(_graphView.Graph, _document.Graph)) _graphView.Graph = _document.Graph;
        _graphView.Visible = graph;
        if (_nodesEmptyState is not null) _nodesEmptyState.Visible = !graph;
        if (_nodesHint is not null) _nodesHint.Visible = graph;
        if (_nodesToolbar is not null)
            foreach (ToolStripItem item in _nodesToolbar.Items) if (item is ToolStripButton) item.Enabled = graph;
        if (_nodesPreviewHost is not null) _nodesPreviewHost.Visible = graph;
        _code.TextBox.ReadOnly = graph;
        if (_codeGraphBanner is not null) _codeGraphBanner.Visible = graph;
        if (_nodesPlayButton is not null) _nodesPlayButton.Checked = _playing;
        PlaceNodesPreview(_shaderWorkspaceMode);
        RefreshNodeErrors();
        if (rebuildProperties) RebuildNodeProperties();
        _graphView.Invalidate();
    }

    /// <summary>The preview sits beside the graph in Nodes and goes back to its own page elsewhere.</summary>
    private void PlaceNodesPreview(string mode)
    {
        if (_nodesPreviewHost is null || _viewportHome is null) return;
        Control target = mode == "Nodes" && _document.Graph is not null ? _nodesPreviewHost : _viewportHome;
        if (_viewport.Parent == target) return;
        target.Controls.Add(_viewport);
        _viewport.Dock = DockStyle.Fill;
        _viewport.BringToFront();
        _viewport.Invalidate(true);
    }

    private void ShowNodeMenu(Point client, ShaderGraphView.PortRef? wire)
    {
        if (_graphView is null || _document.Graph is null) return;
        PointF at = _graphView.ToGraph(client);
        Func<ShaderGraphNodeDefinition, bool>? accepts = null;
        if (wire is { } dragged) accepts = definition => CompatiblePort(definition, dragged) is not null;
        ShaderNodeSearchMenu menu = new(accepts);
        menu.Chosen += definition => AddNodeFromMenu(definition, at, wire);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_graphView, client);
    }

    private void AddNodeFromMenu(ShaderGraphNodeDefinition definition, PointF at, ShaderGraphView.PortRef? wire)
    {
        string? id = null;
        EditNodeGraph($"Add {definition.Title} node", graph =>
        {
            ShaderGraphNode node = ShaderGraphDocuments.AddNode(graph, definition.Type, MathF.Round(at.X), MathF.Round(at.Y));
            id = node.Id;
            if (wire is not { } dragged || CompatiblePort(definition, dragged) is not { } port) return;
            if (dragged.Output) ShaderGraphDocuments.Connect(graph, dragged.Node, dragged.Port, node.Id, port);
            else ShaderGraphDocuments.Connect(graph, node.Id, port, dragged.Node, dragged.Port);
        });
        if (id is not null) _graphView?.SelectNode(id);
    }

    /// <summary>The first port of a new node a dragged wire can go to.</summary>
    private string? CompatiblePort(ShaderGraphNodeDefinition definition, ShaderGraphView.PortRef wire)
    {
        ShaderGraphNode? node = _document.Graph?.FindNode(wire.Node);
        ShaderGraphNodeDefinition? source = node is null ? null : ShaderGraphCatalog.Find(node.Type);
        ShaderGraphPort? port = wire.Output ? source?.FindOutput(wire.Port) : source?.FindInput(wire.Port);
        if (port is null) return null;
        bool texture = port.Type == ShaderGraphValueType.Texture;
        IEnumerable<ShaderGraphPort> candidates = wire.Output ? definition.Inputs : definition.Outputs;
        return candidates.FirstOrDefault(candidate => (candidate.Type == ShaderGraphValueType.Texture) == texture)?.Name;
    }

    // ── The selected node's settings ─────────────────────────────────────────

    private int NodePropertyWidth => Math.Max(180, (_nodeProperties?.ClientSize.Width ?? 300) - (_nodeProperties?.Padding.Horizontal ?? 20) - 4);

    private void ResizeNodeProperties()
    {
        if (_nodeProperties is null) return;
        int width = NodePropertyWidth;
        foreach (Control control in _nodeProperties.Controls) control.Width = width;
    }

    private void RebuildNodeProperties()
    {
        if (_nodeProperties is null) return;
        _rebuildingNodeProperties = true;
        _nodeProperties.SuspendLayout();
        try
        {
            while (_nodeProperties.Controls.Count > 0) _nodeProperties.Controls[0].Dispose();
            ShaderGraphDocument? graph = _document.Graph;
            ShaderGraphNode? node = graph is null || _graphView?.SelectedNodeId is not { } id ? null : graph.FindNode(id);
            if (graph is null)
            {
                AddNodeNote("This shader is written as code.");
                return;
            }
            if (node is null || ShaderGraphCatalog.Find(node.Type) is not { } definition)
            {
                int count = _graphView?.SelectedNodeIds.Count ?? 0;
                AddNodeNote(count > 1
                    ? $"{count} nodes selected. Delete removes them; drag one to move them all."
                    : $"{graph.Nodes.Count} nodes, {graph.Connections.Count} wires. Select a node to see its settings.");
                foreach (string warning in _graphWarnings) AddNodeNote("Note: " + warning);
                return;
            }

            AddNodeHeading(definition.Title);
            AddNodeNote(definition.Description);
            if (definition.Role == ShaderGraphNodeRole.Parameter) AddParameterSettings(node, definition);
            else if (definition.Role == ShaderGraphNodeRole.Constant)
                AddNumbers("Value", node.Value, definition.DefaultValue.Length, values => SetGraphNodeValueFromPanel(node.Id, values));
            if (definition.Modes.Count > 0)
            {
                ComboBox mode = NodeChoice("ShaderNodeMode", definition.Modes, Math.Max(0, definition.Modes.ToList().IndexOf(node.Mode)));
                mode.SelectedIndexChanged += (_, _) =>
                {
                    if (_rebuildingNodeProperties || mode.SelectedItem is not string chosen) return;
                    _editingFromNodeProperties = true;
                    try { SetGraphNodeMode(node.Id, chosen); }
                    finally { _editingFromNodeProperties = false; }
                };
                AddNodeLabel("Read as");
                _nodeProperties.Controls.Add(mode);
            }

            List<ShaderGraphPort> unwired = definition.Inputs.Where(port =>
                port.Type != ShaderGraphValueType.Texture && graph.FindInput(node.Id, port.Name) is null).ToList();
            if (unwired.Count > 0)
            {
                AddNodeHeading(definition.Role == ShaderGraphNodeRole.Output ? "Values without a wire" : "Inputs without a wire");
                if (definition.Role == ShaderGraphNodeRole.Output)
                    AddNodeNote("Ticked: the shader uses this value. Unticked: the target keeps its own (a model's material, an Image's pixels).");
            }
            foreach (ShaderGraphPort port in unwired)
            {
                bool own = node.Inputs.TryGetValue(port.Name, out float[]? value);
                int width = port.Type == ShaderGraphValueType.Dynamic ? Math.Clamp((own ? value! : port.Default).Length, 1, 4) : (int)port.Type;
                float[] shown = own ? value! : port.Default;
                if (definition.Role == ShaderGraphNodeRole.Output)
                {
                    AddOutputValue(node, port, own, shown, width);
                    continue;
                }
                string label = port.Source != ShaderGraphDefaultSource.Constant && !own ? port.DisplayName + " (default)" : port.DisplayName;
                AddNumbers(label, shown, width, values => SetGraphNodeInputFromPanel(node.Id, port.Name, values));
            }
        }
        finally
        {
            _nodeProperties.ResumeLayout(true);
            _rebuildingNodeProperties = false;
        }
    }

    private void AddParameterSettings(ShaderGraphNode node, ShaderGraphNodeDefinition definition)
    {
        AddNodeLabel("Name (Inspector and scripts)");
        TextBox name = new() { Name = "ShaderNodeParameterName", Text = node.Name, Width = NodePropertyWidth };
        EditorChrome.StyleField(name);
        void CommitName()
        {
            if (_rebuildingNodeProperties || name.IsDisposed) return;
            string next = name.Text.Trim();
            string current = _document.Graph?.FindNode(node.Id)?.Name ?? node.Name;
            if (next == current) return;
            // The panel stays as it is (this box is the one being typed in); only the graph changes.
            _editingFromNodeProperties = true;
            try
            {
                if (!SetGraphNodeName(node.Id, next)) name.Text = current;
            }
            finally { _editingFromNodeProperties = false; }
        }
        name.Leave += (_, _) => CommitName();
        name.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            CommitName();
        };
        _nodeProperties!.Controls.Add(name);

        if (definition.Type == "TextureParameter")
        {
            AddNodeLabel("Image");
            string current = ResourceBinding(node.Name);
            if (current.Length == 0) current = node.Texture;
            List<string> choices = ["(none)", .. _imageAssetDisplayNames];
            int selected = _imageAssets.FindIndex(asset => string.Equals(asset, current, StringComparison.OrdinalIgnoreCase)) + 1;
            ComboBox image = NodeChoice("ShaderNodeTextureImage", choices, Math.Max(0, selected));
            image.SelectedIndexChanged += (_, _) =>
            {
                if (_rebuildingNodeProperties) return;
                int index = image.SelectedIndex - 1;
                _editingFromNodeProperties = true;
                try { SetGraphNodeTexture(node.Id, index >= 0 && index < _imageAssets.Count ? _imageAssets[index] : string.Empty); }
                finally { _editingFromNodeProperties = false; }
            };
            _nodeProperties.Controls.Add(image);
            return;
        }

        float[] value = ParameterValue(node.Name) is { Count: > 0 } live ? live.ToArray() : node.Value;
        if (definition.Type == "ColorParameter")
        {
            Button swatch = new()
            {
                BackColor = SwatchColour(value),
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Name = "ShaderNodeColour",
                Text = "Choose colour…",
                Width = NodePropertyWidth,
            };
            swatch.ForeColor = swatch.BackColor.GetBrightness() > 0.55f ? Color.Black : Color.White;
            swatch.Click += (_, _) =>
            {
                float[] now = ParameterValue(node.Name) is { Count: > 0 } current ? current.ToArray() : value;
                using ColorDialog dialog = new() { Color = SwatchColour(now), FullOpen = true };
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                float alpha = now.Length > 3 ? now[3] : 1f;
                SetGraphNodeValueFromPanel(node.Id, [dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, alpha]);
                // The fields below show the new colour once this click is over.
                BeginInvoke(RebuildNodeProperties);
            };
            AddNodeLabel("Colour");
            _nodeProperties.Controls.Add(swatch);
            AddNumbers("RGBA", value, 4, values => SetGraphNodeValueFromPanel(node.Id, values));
            return;
        }
        AddNumbers("Value", value, definition.DefaultValue.Length, values => SetGraphNodeValueFromPanel(node.Id, values));
    }

    private void AddOutputValue(ShaderGraphNode node, ShaderGraphPort port, bool own, float[] shown, int width)
    {
        CheckBox use = new()
        {
            AutoSize = false,
            Checked = own,
            ForeColor = EditorChrome.Text,
            Height = 26,
            Name = "ShaderNodeOutputUse_" + port.Name,
            Text = port.DisplayName,
            Width = NodePropertyWidth,
        };
        _nodeProperties!.Controls.Add(use);
        Control numbers = AddNumbers(string.Empty, shown, width, values => SetGraphNodeInputFromPanel(node.Id, port.Name, values));
        numbers.Enabled = own;
        use.CheckedChanged += (_, _) =>
        {
            if (_rebuildingNodeProperties) return;
            numbers.Enabled = use.Checked;
            float[] current = numbers.Controls.OfType<NumericUpDown>().Select(input => (float)input.Value).ToArray();
            SetGraphNodeInputFromPanel(node.Id, port.Name, use.Checked ? current : null);
        };
    }

    private void SetGraphNodeValueFromPanel(string id, float[] values)
    {
        _editingFromNodeProperties = true;
        try { SetGraphNodeValue(id, values); }
        finally { _editingFromNodeProperties = false; }
    }

    private void SetGraphNodeInputFromPanel(string id, string port, float[]? values)
    {
        _editingFromNodeProperties = true;
        try { SetGraphNodeInput(id, port, values); }
        finally { _editingFromNodeProperties = false; }
    }

    private static Color SwatchColour(float[] value) => Color.FromArgb(
        Math.Clamp((int)MathF.Round((value.Length > 0 ? value[0] : 1f) * 255f), 0, 255),
        Math.Clamp((int)MathF.Round((value.Length > 1 ? value[1] : 1f) * 255f), 0, 255),
        Math.Clamp((int)MathF.Round((value.Length > 2 ? value[2] : 1f) * 255f), 0, 255));

    private Control AddNumbers(string label, float[] values, int count, Action<float[]> commit)
    {
        count = Math.Clamp(count, 1, 4);
        bool labelled = label.Length > 0;
        TableLayoutPanel row = new()
        {
            ColumnCount = count + (labelled ? 1 : 0),
            Height = 30,
            Margin = new Padding(0, 2, 0, 2),
            Name = "ShaderNodeNumbers",
            RowCount = 1,
            Width = NodePropertyWidth,
        };
        if (labelled) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        for (int index = 0; index < count; index++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
        if (labelled)
        {
            row.Controls.Add(new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                ForeColor = EditorChrome.Muted,
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
            }, 0, 0);
        }
        float[] fitted = FitValues(values, count);
        NumericUpDown[] inputs = new NumericUpDown[count];
        for (int index = 0; index < count; index++)
        {
            NumericUpDown input = new()
            {
                DecimalPlaces = 3,
                Dock = DockStyle.Fill,
                Increment = 0.05m,
                Margin = new Padding(1, 2, 1, 2),
                Maximum = 100000m,
                Minimum = -100000m,
                Value = (decimal)Math.Clamp(float.IsFinite(fitted[index]) ? fitted[index] : 0f, -100000f, 100000f),
            };
            EditorChrome.StyleField(input);
            input.ValueChanged += (_, _) =>
            {
                if (_rebuildingNodeProperties) return;
                commit(inputs.Select(field => (float)field.Value).ToArray());
            };
            inputs[index] = input;
            row.Controls.Add(input, index + (labelled ? 1 : 0), 0);
        }
        _nodeProperties!.Controls.Add(row);
        return row;
    }

    private ComboBox NodeChoice(string name, IEnumerable<string> items, int selected)
    {
        ComboBox choice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = name, Width = NodePropertyWidth };
        EditorChrome.StyleField(choice);
        foreach (string item in items) choice.Items.Add(item);
        if (choice.Items.Count > 0) choice.SelectedIndex = Math.Clamp(selected, 0, choice.Items.Count - 1);
        return choice;
    }

    private void AddNodeHeading(string text) => _nodeProperties!.Controls.Add(new Label
    {
        AutoSize = false,
        Font = EditorChrome.HeadingFont,
        ForeColor = EditorChrome.Text,
        Height = EditorChrome.HeadingFont.Height + 10,
        Margin = new Padding(0, 8, 0, 2),
        Text = text,
        TextAlign = ContentAlignment.BottomLeft,
        UseMnemonic = false,
        Width = NodePropertyWidth,
    });

    private void AddNodeLabel(string text) => _nodeProperties!.Controls.Add(new Label
    {
        AutoSize = false,
        ForeColor = EditorChrome.Muted,
        Height = EditorChrome.BaseFont.Height + 8,
        Margin = new Padding(0, 6, 0, 0),
        Text = text,
        TextAlign = ContentAlignment.BottomLeft,
        UseMnemonic = false,
        Width = NodePropertyWidth,
    });

    private void AddNodeNote(string text)
    {
        int width = NodePropertyWidth;
        int height = TextRenderer.MeasureText(text, EditorChrome.SmallFont, new Size(width, 0), TextFormatFlags.WordBreak).Height + 6;
        _nodeProperties!.Controls.Add(new Label
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = height,
            Margin = new Padding(0, 2, 0, 4),
            Text = text,
            UseMnemonic = false,
            Width = width,
        });
    }
}

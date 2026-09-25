using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public enum ModelElementSelectionMode { Vertex, Edge, Face }

public sealed partial class ModelEditorControl
{
    private readonly HashSet<(int Mesh, int Face)> _selectedFaces = [];
    private readonly HashSet<(int Mesh, ushort A, ushort B)> _selectedEdges = [];
    private readonly Dictionary<ModelElementSelectionMode, Button> _elementButtons = [];
    private ModelElementSelectionMode _elementSelectionMode = ModelElementSelectionMode.Vertex;
    private bool _dynamicTopology;
    private float _detailSize = .12f;
    private float _topologyAmount = .18f;
    private GModelAsset? _beforeDynamicStroke;
    private TrackBar? _subdivisionSlider;
    private Label? _subdivisionValue;
    private ThemedComboBox? _shadingPicker;
    private CheckBox? _dynamicTopologyCheck;
    private TrackBar? _detailSlider;
    private Label? _detailValue;
    private CollapsibleSection? _meshSelectionSection;
    private CollapsibleSection? _boxModelingSection;
    private CollapsibleSection? _sculptDetailSection;
    private bool _syncingOrganic;
    private bool _pushPullActive;
    private Point _pushPullStart;
    private GModelAsset? _pushPullBefore;
    private Dictionary<int, int[]>? _pushPullFaces;
    private float _pushPullDistance;

    public ModelElementSelectionMode ElementSelectionMode => _elementSelectionMode;
    public int SelectedFaceCount => _selectedFaces.Count;
    public int SelectedEdgeCount => _selectedEdges.Count;
    public bool DynamicTopologyEnabled => _dynamicTopology;
    public float DynamicTopologyDetailSize => _detailSize;

    private void BuildOrganicToolbox(FlowLayoutPanel flow)
    {
        CollapsibleSection elements = _meshSelectionSection = Section(flow, "MESH SELECTION", 60);
        foreach (ModelElementSelectionMode mode in Enum.GetValues<ModelElementSelectionMode>())
        {
            ModelElementSelectionMode captured = mode;
            Button button = Tool(mode.ToString(), () => SetElementSelectionMode(captured));
            button.SetBounds(6 + (int)mode * 87, 6, 83, 44);
            elements.Content.Controls.Add(button);
            _elementButtons[mode] = button;
        }

        CollapsibleSection topology = _boxModelingSection = Section(flow, "BOX MODELING", 194);
        Label amountLabel = new() { Text = "Operation amount", Location = new Point(8, 8), Size = new Size(122, 24), ForeColor = EditorChrome.Muted };
        NumericUpDown amount = Number(.001m, 100m, (decimal)_topologyAmount, value => _topologyAmount = (float)value);
        amount.SetBounds(134, 4, 124, 27);
        topology.Content.Controls.Add(amountLabel);
        topology.Content.Controls.Add(amount);
        Add("Extrude  E", 8, 40, ExtrudeSelectedFaces);
        Add("Inset  I", 134, 40, InsetSelectedFaces);
        Add("Bevel  B", 8, 78, BevelSelectedEdges);
        Add("Loop Cut  Ctrl+R", 134, 78, LoopCutSelectedEdge);
        Label hint = new()
        {
            Text = "Face tools use selected faces. Bevel and Loop Cut use selected edges.",
            Location = new Point(8, 122), Size = new Size(250, 54), ForeColor = EditorChrome.Muted,
        };
        topology.Content.Controls.Add(hint);

        CollapsibleSection sculptDetail = _sculptDetailSection = Section(flow, "SCULPT DETAIL", 102);
        _dynamicTopologyCheck = new CheckBox { Text = "Dynamic Topology", AutoSize = true, Location = new Point(8, 7) };
        _dynamicTopologyCheck.CheckedChanged += (_, _) =>
        {
            _dynamicTopology = _dynamicTopologyCheck.Checked;
            if (_detailSlider is not null) _detailSlider.Enabled = _dynamicTopology;
            if (_detailValue is not null) _detailValue.Enabled = _dynamicTopology;
        };
        sculptDetail.Content.Controls.Add(_dynamicTopologyCheck);
        sculptDetail.Content.Controls.Add(new Label { Text = "Detail Size", Location = new Point(8, 38), Size = new Size(78, 24), ForeColor = EditorChrome.Muted });
        _detailSlider = new TrackBar { Minimum = 1, Maximum = 200, TickFrequency = 25, Value = 12, Location = new Point(82, 31), Size = new Size(132, 42), Enabled = false };
        _detailSlider.ValueChanged += (_, _) =>
        {
            _detailSize = _detailSlider.Value / 100f;
            if (_detailValue is not null) _detailValue.Text = _detailSize.ToString("0.00");
        };
        _detailValue = new Label { Text = "0.12", Location = new Point(218, 40), Size = new Size(42, 22), ForeColor = EditorChrome.Text, Enabled = false };
        sculptDetail.Content.Controls.Add(_detailSlider);
        sculptDetail.Content.Controls.Add(_detailValue);
        SetElementSelectionMode(ModelElementSelectionMode.Vertex, switchWorkspace: false);

        void Add(string text, int x, int y, Action action)
        {
            Button button = Tool(text, action);
            button.SetBounds(x, y, 120, 32);
            topology.Content.Controls.Add(button);
        }
    }

    private void BuildOrganicInspector(FlowLayoutPanel right)
    {
        CollapsibleSection mesh = Section(right, "ORGANIC SURFACE", 154);
        mesh.Content.Controls.Add(new Label { Text = "Subdivision Level", Location = new Point(8, 9), Size = new Size(118, 22), ForeColor = EditorChrome.Muted });
        _subdivisionSlider = new TrackBar { Minimum = 0, Maximum = 2, TickStyle = TickStyle.BottomRight, TickFrequency = 1, Location = new Point(118, 1), Size = new Size(104, 42) };
        _subdivisionSlider.ValueChanged += (_, _) =>
        {
            if (_syncingOrganic) return;
            SetSelectedSubdivision(_subdivisionSlider.Value);
        };
        _subdivisionValue = new Label { Text = "0", Location = new Point(228, 10), Size = new Size(28, 22), ForeColor = EditorChrome.Text };
        mesh.Content.Controls.Add(_subdivisionSlider);
        mesh.Content.Controls.Add(_subdivisionValue);
        mesh.Content.Controls.Add(new Label { Text = "Shading", Location = new Point(8, 54), Size = new Size(92, 24), ForeColor = EditorChrome.Muted });
        _shadingPicker = new ThemedComboBox { Location = new Point(102, 50), Size = new Size(156, 27) };
        _shadingPicker.Items.AddRange(["Smooth", "Flat"]);
        _shadingPicker.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingOrganic) SetSelectedShading(_shadingPicker.SelectedIndex != 1);
        };
        mesh.Content.Controls.Add(_shadingPicker);
        mesh.Content.Controls.Add(new Label
        {
            Text = "The control cage is retained at levels 1–2. Topology tools apply the visible modifier first.",
            Location = new Point(8, 88), Size = new Size(250, 52), ForeColor = EditorChrome.Muted,
        });
        RefreshOrganicInspector();
    }

    private void RefreshOrganicInspector()
    {
        if (_subdivisionSlider is null || _shadingPicker is null) return;
        GModelMesh? mesh = SelectedMesh();
        _syncingOrganic = true;
        try
        {
            bool editable = mesh is not null && !mesh.IsSkinned && (mesh.MorphTargets?.Count ?? 0) == 0;
            _subdivisionSlider.Enabled = editable;
            _shadingPicker.Enabled = editable;
            _subdivisionSlider.Value = Math.Clamp(mesh?.SubdivisionLevel ?? 0, 0, 2);
            if (_subdivisionValue is not null) _subdivisionValue.Text = _subdivisionSlider.Value.ToString();
            _shadingPicker.SelectedIndex = mesh?.SmoothShading == false ? 1 : 0;
        }
        finally { _syncingOrganic = false; }
    }

    private void SetElementSelectionMode(ModelElementSelectionMode mode, bool switchWorkspace = true)
    {
        _elementSelectionMode = mode;
        _selection.Clear();
        _selectedFaces.Clear();
        _selectedEdges.Clear();
        if (switchWorkspace) SetMode(ModelEditorMode.Mesh);
        foreach ((ModelElementSelectionMode candidate, Button button) in _elementButtons)
            button.BackColor = candidate == mode ? ImageEditorChrome.Hover : ImageEditorChrome.Raised;
        UpdateToolHeader();
        Surface.Invalidate(true);
    }

    private GModelMesh? SelectedMesh() => _groups.SelectedIndex >= 0 && _groups.SelectedIndex < Asset.Meshes.Count
        ? Asset.Meshes[_groups.SelectedIndex] : null;

    private void SetSelectedSubdivision(int level)
    {
        int meshIndex = _groups.SelectedIndex;
        if (meshIndex < 0) return;
        RunTopology("Set subdivision level", () => ModelPartBuilder.SetSubdivisionLevel(Asset.Meshes[meshIndex], level));
        if (_subdivisionValue is not null) _subdivisionValue.Text = level.ToString();
    }

    private void SetSelectedShading(bool smooth)
    {
        int meshIndex = _groups.SelectedIndex;
        if (meshIndex < 0) return;
        RunTopology(smooth ? "Smooth shading" : "Flat shading", () => ModelPartBuilder.SetSmoothShading(Asset.Meshes[meshIndex], smooth));
    }

    public void ExtrudeSelectedFaces() => ApplyFaceOperation("Extrude faces", (mesh, faces) => ModelPartBuilder.ExtrudeFaces(mesh, faces, _topologyAmount));
    public void InsetSelectedFaces() => ApplyFaceOperation("Inset faces", (mesh, faces) => ModelPartBuilder.InsetFaces(mesh, faces, Math.Clamp(_topologyAmount, .01f, .92f)));

    private void ApplyFaceOperation(string label, Func<GModelMesh, IEnumerable<int>, IReadOnlyList<int>> operation)
    {
        if (_selectedFaces.Count == 0) { Status.Text = "Select one or more faces first."; return; }
        Dictionary<int, int[]> selection = _selectedFaces.GroupBy(item => item.Mesh).ToDictionary(group => group.Key, group => group.Select(item => item.Face).ToArray());
        RunTopology(label, () =>
        {
            foreach ((int meshIndex, int[] faces) in selection)
                if ((uint)meshIndex < Asset.Meshes.Count) operation(Asset.Meshes[meshIndex], faces);
        });
        ClearElementSelection();
    }

    public void BevelSelectedEdges()
    {
        if (_selectedEdges.Count == 0) { Status.Text = "Select one or more edges first."; return; }
        Dictionary<int, (ushort A, ushort B)[]> selection = _selectedEdges.GroupBy(item => item.Mesh)
            .ToDictionary(group => group.Key, group => group.Select(item => (item.A, item.B)).ToArray());
        RunTopology("Bevel edges", () =>
        {
            foreach ((int meshIndex, (ushort A, ushort B)[] edges) in selection)
                if ((uint)meshIndex < Asset.Meshes.Count) ModelPartBuilder.BevelEdges(Asset.Meshes[meshIndex], edges, _topologyAmount);
        });
        ClearElementSelection();
    }

    public void LoopCutSelectedEdge()
    {
        if (_selectedEdges.Count == 0)
        {
            Status.Text = "Select an edge to define the loop direction.";
            return;
        }
        (int Mesh, ushort A, ushort B) selected = _selectedEdges.First();
        RunTopology("Loop cut", () => ModelPartBuilder.LoopCut(Asset.Meshes[selected.Mesh], selected.A, selected.B));
        ClearElementSelection();
    }

    private void SubdivideSelectedMesh()
    {
        GModelMesh? mesh = SelectedMesh();
        if (mesh is null) { Status.Text = "Select a mesh to subdivide."; return; }
        int next = Math.Min(2, Math.Max(1, mesh.SubdivisionLevel + 1));
        SetSelectedSubdivision(next);
    }

    private void BeginPushPull(Point point)
    {
        if (!TryPickTriangle(point, out int mesh, out int face, out _)) return;
        if (Asset.Meshes[mesh].IsSkinned || (Asset.Meshes[mesh].MorphTargets?.Count ?? 0) > 0)
        {
            Status.Text = "Push / Pull requires an unskinned mesh without morph targets.";
            return;
        }
        SelectPart(mesh);
        if (!_selectedFaces.Contains((mesh, face)))
        {
            _selectedFaces.Clear();
            _selectedEdges.Clear();
            _selection.Clear();
            _selectedFaces.Add((mesh, face));
        }
        _pushPullFaces = _selectedFaces.GroupBy(item => item.Mesh)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Face).ToArray());
        _pushPullBefore = ModelPoseWorkflow.Copy(Asset);
        _pushPullStart = point;
        _pushPullDistance = 0f;
        _pushPullActive = true;
        Surface.NavigationEnabled = false;
        Surface.Host.Capture = true;
        Status.Text = "Push / Pull · drag up to pull outward, down to push inward · Esc cancels";
    }

    private void UpdatePushPull(Point point)
    {
        if (!_pushPullActive || _pushPullBefore is null || _pushPullFaces is null) return;
        float diagonal = MathF.Max(.1f, Vector3.Distance(_pushPullBefore.Bounds.Min, _pushPullBefore.Bounds.Max));
        float distance = (_pushPullStart.Y - point.Y) * diagonal / Math.Max(120f, Surface.Host.ClientSize.Height * .45f);
        if (_snapToGrid)
        {
            float step = Math.Max(.001f, _gridSnap);
            distance = MathF.Round(distance / step) * step;
        }
        if (MathF.Abs(distance - _pushPullDistance) < .0001f) return;
        GModelAsset preview = ModelPoseWorkflow.Copy(_pushPullBefore);
        try
        {
            foreach ((int mesh, int[] faces) in _pushPullFaces)
                ModelPartBuilder.ExtrudeFaces(preview.Meshes[mesh], faces, distance);
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
            return;
        }
        Asset = preview;
        Asset.RecalculateBounds();
        _pushPullDistance = distance;
        _previewChanged = true;
        _filteredPreview = null;
        InvalidateModelGeometry();
        Surface.Invalidate(true);
        Status.Text = $"Push / Pull {distance:0.###} units · Esc cancels";
    }

    private void FinishPushPull()
    {
        if (!_pushPullActive) return;
        _pushPullActive = false;
        Surface.NavigationEnabled = true;
        Surface.Host.Capture = false;
        if (_pushPullBefore is null || MathF.Abs(_pushPullDistance) < .0001f)
        {
            if (_pushPullBefore is not null) ReplaceAsset(ModelPoseWorkflow.Copy(_pushPullBefore));
            ClearPushPullState();
            return;
        }
        GModelAsset before = ModelPoseWorkflow.Copy(_pushPullBefore);
        GModelAsset after = ModelPoseWorkflow.Copy(Asset);
        MarkDirty();
        PushEdit("Push / Pull faces", () => ReplaceAsset(ModelPoseWorkflow.Copy(after)), () => ReplaceAsset(ModelPoseWorkflow.Copy(before)));
        ClearPushPullState();
        ClearElementSelection();
        Status.Text = "Push / Pull applied.";
    }

    private bool CancelPushPull()
    {
        if (!_pushPullActive || _pushPullBefore is null) return false;
        ReplaceAsset(ModelPoseWorkflow.Copy(_pushPullBefore));
        _pushPullActive = false;
        Surface.NavigationEnabled = true;
        Surface.Host.Capture = false;
        ClearPushPullState();
        Status.Text = "Push / Pull cancelled.";
        return true;
    }

    private void ClearPushPullState()
    {
        _pushPullBefore = null;
        _pushPullFaces = null;
        _pushPullDistance = 0f;
    }

    private void RunTopology(string label, Action operation)
    {
        GModelAsset backup = ModelPoseWorkflow.Copy(Asset);
        try
        {
            ChangeAsset(label, operation);
            NotifyMeshChanged();
            RefreshOrganicInspector();
            Status.Text = label + " applied.";
        }
        catch (InvalidOperationException exception)
        {
            ReplaceAsset(backup);
            Status.Text = exception.Message;
        }
    }

    private void ClearElementSelection()
    {
        _selection.Clear();
        _selectedFaces.Clear();
        _selectedEdges.Clear();
        Surface.Invalidate(true);
    }

    private void SelectAllElements()
    {
        _selection.Clear();
        _selectedEdges.Clear();
        _selectedFaces.Clear();
        for (int mesh = 0; mesh < Asset.Meshes.Count; mesh++)
        {
            if (_hiddenGroups.Contains(mesh)) continue;
            GModelMesh data = Asset.Meshes[mesh];
            if (_elementSelectionMode == ModelElementSelectionMode.Vertex)
                for (int vertex = 0; vertex < Vertices(data).Length; vertex++) _selection.Add((mesh, vertex));
            else if (_elementSelectionMode == ModelElementSelectionMode.Face)
                for (int face = 0; face < data.Indices.Length / 3; face++) _selectedFaces.Add((mesh, face));
            else
                for (int index = 0; index + 2 < data.Indices.Length; index += 3)
                {
                    _selectedEdges.Add((mesh, Normalize(data.Indices[index], data.Indices[index + 1]).A, Normalize(data.Indices[index], data.Indices[index + 1]).B));
                    _selectedEdges.Add((mesh, Normalize(data.Indices[index + 1], data.Indices[index + 2]).A, Normalize(data.Indices[index + 1], data.Indices[index + 2]).B));
                    _selectedEdges.Add((mesh, Normalize(data.Indices[index + 2], data.Indices[index]).A, Normalize(data.Indices[index + 2], data.Indices[index]).B));
                }
        }
        Status.Text = $"Selected {SelectionCount():N0} {_elementSelectionMode.ToString().ToLowerInvariant()} element(s)";
        Surface.Invalidate(true);
    }

    private void InvertElementSelection()
    {
        HashSet<(int Mesh, int Vertex)> vertices = new(_selection);
        HashSet<(int Mesh, int Face)> faces = new(_selectedFaces);
        HashSet<(int Mesh, ushort A, ushort B)> edges = new(_selectedEdges);
        SelectAllElements();
        if (_elementSelectionMode == ModelElementSelectionMode.Vertex) _selection.ExceptWith(vertices);
        else if (_elementSelectionMode == ModelElementSelectionMode.Face) _selectedFaces.ExceptWith(faces);
        else _selectedEdges.ExceptWith(edges);
        Status.Text = $"Inverted selection · {SelectionCount():N0} element(s)";
        Surface.Invalidate(true);
    }

    private int SelectionCount() => _elementSelectionMode switch
    {
        ModelElementSelectionMode.Face => _selectedFaces.Count,
        ModelElementSelectionMode.Edge => _selectedEdges.Count,
        _ => _selection.Count,
    };

    private void DeleteSelectionOrPart()
    {
        if (_selection.Count == 0 && _selectedFaces.Count == 0 && _selectedEdges.Count == 0)
        {
            DeleteSelectedPart();
            return;
        }
        Dictionary<int, int[]> vertices = _selection.GroupBy(item => item.Mesh)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Vertex).ToArray());
        Dictionary<int, int[]> faces = _selectedFaces.GroupBy(item => item.Mesh)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Face).ToArray());
        Dictionary<int, (ushort A, ushort B)[]> edges = _selectedEdges.GroupBy(item => item.Mesh)
            .ToDictionary(group => group.Key, group => group.Select(item => (item.A, item.B)).ToArray());
        int[] affectedMeshes = vertices.Keys.Concat(faces.Keys).Concat(edges.Keys).Distinct().ToArray();
        if (affectedMeshes.Any(mesh => (uint)mesh < Asset.Meshes.Count
            && (Asset.Meshes[mesh].IsSkinned || (Asset.Meshes[mesh].MorphTargets?.Count ?? 0) > 0)))
        {
            Status.Text = "Deleting topology requires an unskinned mesh without morph targets.";
            return;
        }
        try
        {
            ChangeAsset("Delete mesh selection", () =>
            {
                foreach (int mesh in affectedMeshes)
                {
                    if ((uint)mesh >= Asset.Meshes.Count) continue;
                    ModelPartBuilder.DeleteElements(Asset.Meshes[mesh],
                        vertices.GetValueOrDefault(mesh), edges.GetValueOrDefault(mesh), faces.GetValueOrDefault(mesh));
                }
            });
            ClearElementSelection();
            Status.Text = "Deleted selected mesh elements.";
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
        }
    }

    private void ApplyRegionElementSelection(
        HashSet<(int Mesh, int Vertex)> vertexCandidates,
        IReadOnlyList<Point>? polygon,
        Rectangle bounds,
        Keys modifiers)
    {
        if (_elementSelectionMode == ModelElementSelectionMode.Vertex)
        {
            ApplySelection(vertexCandidates.Select(item => (item.Mesh, item.Vertex)).ToHashSet(), modifiers);
            return;
        }
        bool remove = (modifiers & Keys.Shift) != 0;
        bool append = (modifiers & Keys.Control) != 0;
        if (!remove && !append) { _selectedFaces.Clear(); _selectedEdges.Clear(); }
        bool brushSelection = polygon is null && bounds.IsEmpty;
        for (int mesh = 0; mesh < Asset.Meshes.Count; mesh++)
        {
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] meshVertices = Vertices(data);
            for (int face = 0; face < data.Indices.Length / 3; face++)
            {
                ushort a = data.Indices[face * 3], b = data.Indices[face * 3 + 1], c = data.Indices[face * 3 + 2];
                if (_frontFacesOnly && !IsFaceFrontFacing(mesh, face)) continue;
                if (_elementSelectionMode == ModelElementSelectionMode.Face)
                {
                    Vector3 centroid = (meshVertices[a].Position + meshVertices[b].Position + meshVertices[c].Position) / 3f;
                    if (brushSelection
                        ? !vertexCandidates.Contains((mesh, a)) && !vertexCandidates.Contains((mesh, b)) && !vertexCandidates.Contains((mesh, c))
                        : !ContainsProjected(centroid)) continue;
                    if (remove) _selectedFaces.Remove((mesh, face)); else _selectedFaces.Add((mesh, face));
                }
                else
                {
                    AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
                }
                void AddEdge(ushort from, ushort to)
                {
                    Vector3 midpoint = (meshVertices[from].Position + meshVertices[to].Position) * .5f;
                    if (brushSelection
                        ? !vertexCandidates.Contains((mesh, from)) && !vertexCandidates.Contains((mesh, to))
                        : !ContainsProjected(midpoint)) return;
                    (ushort A, ushort B) edge = Normalize(from, to);
                    if (remove) _selectedEdges.Remove((mesh, edge.A, edge.B)); else _selectedEdges.Add((mesh, edge.A, edge.B));
                }
            }
        }
        Status.Text = $"Selected {SelectionCount():N0} {_elementSelectionMode.ToString().ToLowerInvariant()} element(s)";
        Surface.Invalidate(true);

        bool ContainsProjected(Vector3 world)
        {
            Vector3 surface = Surface.WorldToSurface(world - Asset.Pivot.Position);
            if (surface.Z is < 0f or > 1f) return false;
            PointF client = Surface.SurfaceToControl(new PointF(surface.X, surface.Y));
            return polygon is { Count: >= 3 } ? Inside(client, polygon) : bounds.Contains(Point.Round(client));
        }
    }

    private bool IsVertexFrontFacing(int mesh, int vertex)
    {
        GModelMesh data = Asset.Meshes[mesh];
        for (int face = 0; face < data.Indices.Length / 3; face++)
        {
            int offset = face * 3;
            if (data.Indices[offset] != vertex && data.Indices[offset + 1] != vertex && data.Indices[offset + 2] != vertex) continue;
            if (IsFaceFrontFacing(mesh, face)) return true;
        }
        return false;
    }

    private bool IsFaceFrontFacing(int mesh, int face)
    {
        GModelMesh data = Asset.Meshes[mesh];
        MeshVertex[] vertices = Vertices(data);
        int offset = face * 3;
        if ((uint)(offset + 2) >= data.Indices.Length) return false;
        Vector3 a = vertices[data.Indices[offset]].Position;
        Vector3 b = vertices[data.Indices[offset + 1]].Position;
        Vector3 c = vertices[data.Indices[offset + 2]].Position;
        Vector3 normal = Vector3.Cross(b - a, c - a);
        Vector3 towardCamera = Surface.Camera.Eye + Asset.Pivot.Position - (a + b + c) / 3f;
        return Vector3.Dot(normal, towardCamera) >= 0f;
    }

    private void SelectMeshElementAt(Point point, Keys modifiers)
    {
        if (!TryPickTriangle(point, out int mesh, out int face, out Vector3 hit))
        {
            if ((modifiers & Keys.Control) == 0) ClearElementSelection();
            return;
        }
        SelectPart(mesh);
        bool remove = (modifiers & Keys.Shift) != 0;
        bool append = (modifiers & Keys.Control) != 0;
        if (!append && !remove) { _selectedFaces.Clear(); _selectedEdges.Clear(); }
        if (_elementSelectionMode == ModelElementSelectionMode.Face)
        {
            if (remove) _selectedFaces.Remove((mesh, face)); else _selectedFaces.Add((mesh, face));
            Status.Text = $"Selected {_selectedFaces.Count:N0} face(s)";
        }
        else if (_elementSelectionMode == ModelElementSelectionMode.Edge)
        {
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] vertices = Vertices(data);
            ushort a = data.Indices[face * 3], b = data.Indices[face * 3 + 1], c = data.Indices[face * 3 + 2];
            (ushort A, ushort B) edge = ClosestEdge(vertices, hit, a, b, c);
            var selection = (mesh, edge.A, edge.B);
            if (remove) _selectedEdges.Remove(selection); else _selectedEdges.Add(selection);
            Status.Text = $"Selected {_selectedEdges.Count:N0} edge(s)";
        }
        else
        {
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] vertices = Vertices(data);
            ushort a = data.Indices[face * 3], b = data.Indices[face * 3 + 1], c = data.Indices[face * 3 + 2];
            ushort vertex = new[] { a, b, c }.MinBy(index => Vector3.DistanceSquared(vertices[index].Position, hit));
            var selection = (mesh, (int)vertex);
            if (remove) _selection.Remove(selection); else _selection.Add(selection);
            Status.Text = $"Selected {_selection.Count:N0} vertex/vertices";
        }
        Surface.Invalidate(true);
    }

    private bool TryPickTriangle(Point point, out int meshIndex, out int faceIndex, out Vector3 hit)
    {
        (Vector3 Origin, Vector3 Direction) ray = Surface.PickRay(point);
        Vector3 origin = ray.Origin + Asset.Pivot.Position;
        float nearest = float.MaxValue;
        meshIndex = faceIndex = -1;
        hit = default;
        for (int mesh = 0; mesh < Asset.Meshes.Count; mesh++)
        {
            if (_hiddenGroups.Contains(mesh)) continue;
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] vertices = Vertices(data);
            for (int face = 0; face < data.Indices.Length / 3; face++)
            {
                if (_frontFacesOnly && !IsFaceFrontFacing(mesh, face)) continue;
                ushort ia = data.Indices[face * 3], ib = data.Indices[face * 3 + 1], ic = data.Indices[face * 3 + 2];
                if (!RayTriangle(origin, ray.Direction, vertices[ia].Position, vertices[ib].Position, vertices[ic].Position, out float distance)
                    || distance >= nearest) continue;
                nearest = distance;
                meshIndex = mesh;
                faceIndex = face;
                hit = origin + ray.Direction * distance;
            }
        }
        return meshIndex >= 0;
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;
        Vector3 edge1 = b - a, edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-7f) return false;
        float inverse = 1f / determinant;
        Vector3 t = origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u is < 0f or > 1f) return false;
        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance > 0.0001f;
    }

    private static (ushort A, ushort B) ClosestEdge(MeshVertex[] vertices, Vector3 point, ushort a, ushort b, ushort c)
    {
        (ushort A, ushort B) result = Normalize(a, b);
        float best = SegmentDistanceSquared(point, vertices[a].Position, vertices[b].Position);
        Try(b, c); Try(c, a);
        return result;
        void Try(ushort from, ushort to)
        {
            float distance = SegmentDistanceSquared(point, vertices[from].Position, vertices[to].Position);
            if (distance >= best) return;
            best = distance;
            result = Normalize(from, to);
        }
    }

    private void DrawOrganicSelectionOverlay(IRenderController renderer)
    {
        RenderColor faceColor = new(.16f, .82f, 1f, .95f);
        int shown = 0;
        foreach ((int mesh, int face) in _selectedFaces)
        {
            if (shown++ > 3000 || (uint)mesh >= Asset.Meshes.Count) break;
            GModelMesh data = Asset.Meshes[mesh];
            MeshVertex[] vertices = Vertices(data);
            if ((uint)(face * 3 + 2) >= data.Indices.Length) continue;
            ushort a = data.Indices[face * 3], b = data.Indices[face * 3 + 1], c = data.Indices[face * 3 + 2];
            DrawEdge(vertices[a].Position, vertices[b].Position, faceColor, 2f);
            DrawEdge(vertices[b].Position, vertices[c].Position, faceColor, 2f);
            DrawEdge(vertices[c].Position, vertices[a].Position, faceColor, 2f);
        }
        foreach ((int mesh, ushort a, ushort b) in _selectedEdges)
        {
            if ((uint)mesh >= Asset.Meshes.Count) continue;
            MeshVertex[] vertices = Vertices(Asset.Meshes[mesh]);
            if (a >= vertices.Length || b >= vertices.Length) continue;
            DrawEdge(vertices[a].Position, vertices[b].Position, new RenderColor(1f, .66f, .12f), 3f);
        }
        void DrawEdge(Vector3 a, Vector3 b, RenderColor color, float width)
        {
            Vector3 sa = Surface.WorldToSurface(a - Asset.Pivot.Position), sb = Surface.WorldToSurface(b - Asset.Pivot.Position);
            if (sa.Z is >= 0f and <= 1f && sb.Z is >= 0f and <= 1f) renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, color, width, -9510);
        }
    }

    private static float SegmentDistanceSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 edge = b - a;
        float t = edge.LengthSquared() < 1e-10f ? 0f : Math.Clamp(Vector3.Dot(point - a, edge) / edge.LengthSquared(), 0f, 1f);
        return Vector3.DistanceSquared(point, a + edge * t);
    }
    private static (ushort A, ushort B) Normalize(ushort a, ushort b) => a < b ? (a, b) : (b, a);
}

using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public enum ModelAuthoringTool { Brush, Line, Cube, Sphere, Cylinder, SquareFace, CircleFace, TriangleFace, Push, Pull, Smooth, Colouring, Wand, Lasso, Region, BrushSelect, Select }
public enum ModelToolPage { Create, Edit, Select, Texture, RigAnimate, Outliner }

public sealed partial class ModelEditorControl
{
    private CollapsibleSection? _outlinerSection;
    private CollapsibleSection? _primitivesSection;
    private CollapsibleSection? _legacySelectionSection;
    private CollapsibleSection? _selectionOptionsSection;
    private ModelAuthoringTool _tool = ModelAuthoringTool.Select;
    private string _drawingPlane = "XY";
    private readonly Dictionary<ModelAuthoringTool, Button> _toolButtons = [];
    private readonly Dictionary<ModelPrimitiveKind, Button> _primitiveButtons = [];
    private readonly Dictionary<ModelToolPage, ToolStripButton> _toolPageButtons = [];
    private EditorModeHost? _toolPageHost;
    private ToolStripDropDownButton? _cameraControlButton;
    private Label? _albedoValue;
    private Label? _normalValue;
    private Label? _ormValue;
    private PictureBox? _albedoPreview;
    private PictureBox? _normalPreview;
    private PictureBox? _ormPreview;
    private NumericUpDown? _roughnessValue;
    private NumericUpDown? _metallicValue;
    private NumericUpDown? _uvScaleValue;
    private NumericUpDown? _uvOffsetUValue;
    private NumericUpDown? _uvOffsetVValue;
    private CollapsibleSection? _morphSection;
    private readonly Dictionary<string, NumericUpDown> _morphInputs = new(StringComparer.OrdinalIgnoreCase);
    private CollapsibleSection? _socketSection;
    private readonly ListBox _socketList = new() { BorderStyle = BorderStyle.None, IntegralHeight = false };
    private bool _snapToGrid = true;
    private bool _snapToVertex = true;
    private bool _frontFacesOnly = true;
    private float _gridSnap = 1f;
    public ModelAuthoringTool ActiveTool => _tool;
    public string DrawingPlane { get => _drawingPlane; set => _drawingPlane = value is "XY" or "XZ" or "YZ" ? value : "XY"; }

    private void BuildToolbox()
    {
        Control[] detached = LeftPanel.Controls.Cast<Control>().ToArray();
        LeftPanel.Controls.Clear();
        Disposed += (_, _) => { foreach (Control control in detached) control.Dispose(); };
        Body.ColumnStyles[0].Width = 356;

        Panel shell = new() { Name = "ModelEditorModeShell", Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        ToolStrip rail = EditorChrome.MakeModeRail(72);
        _toolPageHost = new EditorModeHost();
        Panel context = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        Panel activeHeader = new() { Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 7, 8, 5), BackColor = EditorChrome.Raised };
        _currentTool.Dock = DockStyle.Fill;
        _currentTool.Width = 250;
        _currentTool.Height = 46;
        _currentTool.Padding = Padding.Empty;
        _currentTool.Text = "SELECT\nBox Select";
        _currentTool.ForeColor = EditorChrome.Text;
        activeHeader.Controls.Add(_currentTool);
        context.Controls.Add(_toolPageHost);
        context.Controls.Add(activeHeader);
        shell.Controls.Add(context);
        shell.Controls.Add(rail);
        LeftPanel.Controls.Add(shell);

        AddPageButton(ModelToolPage.Create, "＋\nCreate", "Create primitive and drawn geometry");
        AddPageButton(ModelToolPage.Edit, "🛠\nEdit", "Edit vertices, edges and faces");
        AddPageButton(ModelToolPage.Select, "⬚\nSelect", "Select mesh elements");
        AddPageButton(ModelToolPage.Texture, "🎨\nTexture", "Assign PBR textures and paint vertex colour");
        AddPageButton(ModelToolPage.RigAnimate, "🦴\nRig", "Rig, pose and animate the model");
        AddPageButton(ModelToolPage.Outliner, "☰\nOutliner", "Manage model mesh parts");

        FlowLayoutPanel createPage = Stack("ModelCreateTools");
        BuildCreatePage(createPage);
        _toolPageHost.AddMode(nameof(ModelToolPage.Create), createPage);

        FlowLayoutPanel editPage = Stack("ModelEditTools");
        BuildOrganicToolbox(editPage);
        BuildOrganicInspector(editPage);
        BuildEditExtras(editPage);
        _toolPageHost.AddMode(nameof(ModelToolPage.Edit), editPage);

        FlowLayoutPanel selectPage = Stack("ModelSelectionTools");
        BuildSelectionPage(selectPage);
        _toolPageHost.AddMode(nameof(ModelToolPage.Select), selectPage);

        FlowLayoutPanel texturePage = Stack("ModelTextureTools");
        BuildTexturePage(texturePage);
        _toolPageHost.AddMode(nameof(ModelToolPage.Texture), texturePage);

        FlowLayoutPanel rigPage = Stack("ModelRigAnimationTools");
        BuildRigPage(rigPage);
        _toolPageHost.AddMode(nameof(ModelToolPage.RigAnimate), rigPage);

        FlowLayoutPanel outlinerPage = Stack("ModelOutlinerTools");
        BuildOutlinerPage(outlinerPage);
        _toolPageHost.AddMode(nameof(ModelToolPage.Outliner), outlinerPage);

        ShowToolPage(ModelToolPage.Create);

        void AddPageButton(ModelToolPage page, string caption, string tooltip)
        {
            ToolStripButton button = EditorChrome.ModeButton(caption, tooltip, () => ShowToolPage(page));
            button.CheckOnClick = false;
            button.AutoSize = false;
            button.Font = EditorChrome.SmallFont;
            button.Height = 58;
            button.Width = 68;
            button.TextAlign = ContentAlignment.MiddleCenter;
            _toolPageButtons[page] = button;
            rail.Items.Add(button);
        }
    }

    private void BuildCreatePage(FlowLayoutPanel page)
    {
        _primitivesSection = Section(page, "3D PRIMITIVES", 118);
        (ModelPrimitiveKind Kind, string Name)[] primitives =
        [
            (ModelPrimitiveKind.Cube, "Cube"), (ModelPrimitiveKind.Sphere, "Sphere"),
            (ModelPrimitiveKind.Cylinder, "Cylinder"), (ModelPrimitiveKind.Capsule, "Capsule"),
            (ModelPrimitiveKind.Quad, "Plane"), (ModelPrimitiveKind.Cone, "Cone"),
        ];
        for (int index = 0; index < primitives.Length; index++)
        {
            (ModelPrimitiveKind kind, string name) = primitives[index];
            Button add = new ModelPrimitiveButton(kind)
            {
                Name = "ModelCreate" + name,
                Text = name,
                Bounds = new Rectangle(6 + index % 3 * 87, 4 + index / 3 * 54, 83, 50),
            };
            ImageEditorChrome.StyleButton(add);
            add.Click += (_, _) => ArmPrimitivePlacement(kind);
            _primitivesSection.Content.Controls.Add(add);
            _primitiveButtons[kind] = add;
        }

        AddToolGrid(page, "2D FACING SHAPES", ModelAuthoringTool.SquareFace,
            ModelAuthoringTool.CircleFace, ModelAuthoringTool.TriangleFace);
        AddToolGrid(page, "DRAW GEOMETRY", ModelAuthoringTool.Line);

        CollapsibleSection placement = Section(page, "DRAWING & SNAPPING", 156);
        CheckBox grid = Check("Snap to Grid", _snapToGrid, value => _snapToGrid = value, 8);
        CheckBox vertex = Check("Snap to Vertices", _snapToVertex, value => _snapToVertex = value, 34);
        placement.Content.Controls.Add(grid);
        placement.Content.Controls.Add(vertex);
        UiKit.ThemedComboBox plane = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        plane.Items.AddRange(["XY", "XZ", "YZ"]);
        plane.SelectedItem = DrawingPlane;
        plane.SelectedIndexChanged += (_, _) => DrawingPlane = plane.Text;
        Field(placement.Content, "Drawing Plane", plane, 64);
        NumericUpDown snap = Number(.001m, 10000, (decimal)_gridSnap, value => _gridSnap = (float)value);
        Field(placement.Content, "Grid Size", snap, 98);
        placement.Content.Controls.Add(new Label
        {
            Text = "Drag in the viewport to place facing shapes or line strips.",
            Location = new Point(8, 128), Size = new Size(250, 24), ForeColor = EditorChrome.Muted,
        });
    }

    private void BuildEditExtras(FlowLayoutPanel page)
    {
        CollapsibleSection edit = Section(page, "DIRECT EDITING", 124);
        Button pushPull = Tool("Push / Pull selected face", () => SelectTool(ModelAuthoringTool.Push));
        pushPull.SetBounds(8, 7, 250, 31);
        edit.Content.Controls.Add(pushPull);
        Button subdivide = Tool("Subdivide selected mesh", SubdivideSelectedMesh);
        subdivide.SetBounds(8, 44, 250, 31);
        edit.Content.Controls.Add(subdivide);
        Button transform = Tool("Transform selected mesh…", ShowTransformWindow);
        transform.SetBounds(8, 81, 250, 31);
        edit.Content.Controls.Add(transform);

        CollapsibleSection brushes = AddToolGrid(page, "SCULPT", ModelAuthoringTool.Brush, ModelAuthoringTool.Smooth);
        brushes.SetContentHeight(62);
    }

    private void BuildSelectionPage(FlowLayoutPanel page)
    {
        _legacySelectionSection = AddToolGrid(page, "SELECTION TOOLS", ModelAuthoringTool.Region,
            ModelAuthoringTool.Lasso, ModelAuthoringTool.Wand, ModelAuthoringTool.BrushSelect);
        _selectionOptionsSection = Section(page, "SELECTION OPTIONS", 176);
        CheckBox front = Check("Front Faces Only", _frontFacesOnly, value => _frontFacesOnly = value, 8);
        _selectionOptionsSection.Content.Controls.Add(front);
        AddAction("Select All    Ctrl+A", 38, SelectAllElements);
        AddAction("Clear    Esc", 72, ClearElementSelection);
        AddAction("Invert    Ctrl+Shift+I", 106, InvertElementSelection);
        AddAction("Delete Selection    Del", 140, DeleteSelectionOrPart);

        void AddAction(string caption, int y, Action action)
        {
            Button button = Tool(caption, action);
            button.SetBounds(8, y, 250, 29);
            _selectionOptionsSection.Content.Controls.Add(button);
        }
    }

    private void BuildTexturePage(FlowLayoutPanel page)
    {
        CollapsibleSection material = Section(page, "MATERIAL & PBR", 390);
        _material.SetBounds(8, 7, 166, 28);
        material.Content.Controls.Add(_material);
        _material.SelectedIndexChanged += (_, _) => { if (!_syncingGroups) AssignMaterial(_material.SelectedIndex); };
        Button chooseMaterial = Tool("Choose…", ChooseMaterialResource);
        chooseMaterial.SetBounds(180, 7, 78, 28);
        material.Content.Controls.Add(chooseMaterial);
        AddPbrRow(material.Content, "Albedo", 44, ref _albedoPreview, ref _albedoValue, () => ChooseMaterialImage("AlbedoTexture"));
        AddPbrRow(material.Content, "Normal", 94, ref _normalPreview, ref _normalValue, () => ChooseMaterialImage("NormalTexture"));
        AddPbrRow(material.Content, "ORM / Metallic-Roughness", 144, ref _ormPreview, ref _ormValue, () => ChooseMaterialImage("MetallicRoughnessTexture"));
        AddNumber("Roughness", 198, ref _roughnessValue, 0, 1, 1, value => SetMaterialScalar("roughness", (float)value));
        AddNumber("Metallic", 230, ref _metallicValue, 0, 1, 0, value => SetMaterialScalar("metallic", (float)value));
        AddNumber("UV Scale", 262, ref _uvScaleValue, .01m, 256, 1, value => SetMaterialScalar("uv", (float)value));
        AddNumber("UV Offset U", 294, ref _uvOffsetUValue, -1000, 1000, 0, value => SetMaterialScalar("uvOffsetU", (float)value));
        AddNumber("UV Offset V", 326, ref _uvOffsetVValue, -1000, 1000, 0, value => SetMaterialScalar("uvOffsetV", (float)value));
        Button unwrap = Tool("Quick UV Unwrap", QuickUvUnwrap);
        unwrap.SetBounds(8, 358, 122, 28);
        material.Content.Controls.Add(unwrap);
        Button baseColour = Tool("Base Colour…", ChooseMaterialBaseColour);
        baseColour.SetBounds(136, 358, 122, 28);
        material.Content.Controls.Add(baseColour);

        CollapsibleSection paint = Section(page, "VERTEX COLOUR", 100);
        _colour.SetBounds(8, 8, 250, 31);
        _colour.Text = "Brush Colour…";
        _colour.Click += (_, _) => ChooseColour();
        paint.Content.Controls.Add(_colour);
        Button brush = Tool("Paint Vertex Colour", () => SelectTool(ModelAuthoringTool.Colouring));
        brush.SetBounds(8, 47, 250, 31);
        paint.Content.Controls.Add(brush);
        Field(paint.Content, "Radius", _radiusInput = Number(.001m, 1000, (decimal)_radius, value => _radius = (float)value), 82);
        Field(paint.Content, "Strength", _strengthInput = Number(.001m, 1, (decimal)_strength, value => _strength = (float)value), 114);
        paint.SetContentHeight(150);

        void AddNumber(string caption, int y, ref NumericUpDown? field, decimal min, decimal max, decimal value, Action<decimal> changed)
        {
            material.Content.Controls.Add(new Label { Text = caption, Location = new Point(8, y + 4), Size = new Size(112, 22), ForeColor = EditorChrome.Muted });
            field = Number(min, max, value, changed);
            field.SetBounds(126, y, 132, 27);
            material.Content.Controls.Add(field);
        }
    }

    private void BuildRigPage(FlowLayoutPanel page)
    {
        CollapsibleSection rig = Section(page, "RIG & ANIMATE", 164);
        Add("Rigging & Binding…", 8, () => { SetMode(ModelEditorMode.Rig); OpenAnimation(0); });
        Add("Guided Auto-Rig Templates…", 46, OpenAutoRigWizard);
        Add("Pose Editor…", 84, () => { SetMode(ModelEditorMode.Rig); OpenAnimation(1); });
        Add("Animation Clips & Timeline…", 122, () => { SetMode(ModelEditorMode.Animate); OpenAnimation(2); });
        void Add(string caption, int y, Action action)
        {
            Button button = Tool(caption, action);
            button.SetBounds(8, y, 250, 31);
            rig.Content.Controls.Add(button);
        }
    }

    private void BuildOutlinerPage(FlowLayoutPanel page)
    {
        _outlinerSection = Section(page, "MESH PARTS", 294);
        _groups.Dock = DockStyle.None;
        _groups.SetBounds(8, 6, 250, 188);
        _outlinerSection.Content.Controls.Add(_groups);
        _groups.SelectedIndexChanged += (_, _) => SyncSelectedGroup();
        _groups.MouseDown += (_, args) =>
        {
            int index = _groups.IndexFromPoint(args.Location);
            if (index >= 0 && args.X >= _groups.ClientSize.Width - 34)
            {
                SetGroupVisible(index, _hiddenGroups.Contains(index));
                _groups.Invalidate();
            }
        };
        _groupName.SetBounds(8, 201, 166, 27);
        _outlinerSection.Content.Controls.Add(_groupName);
        Button rename = Tool("Rename", RenameGroup);
        rename.SetBounds(180, 200, 78, 29);
        _outlinerSection.Content.Controls.Add(rename);
        _visibleGroup.SetBounds(8, 235, 170, 24);
        _visibleGroup.Text = "Visible in viewport";
        _visibleGroup.CheckedChanged += (_, _) => { if (!_syncingGroups) SetGroupVisible(_groups.SelectedIndex, _visibleGroup.Checked); };
        _outlinerSection.Content.Controls.Add(_visibleGroup);
        Button remove = Tool("Delete Part", DeleteSelectedPart);
        remove.SetBounds(180, 233, 78, 29);
        _outlinerSection.Content.Controls.Add(remove);
        _outlinerSection.Content.Controls.Add(new Label
        {
            Text = "Click the eye at the right of a part to show or hide it.",
            Location = new Point(8, 266), Size = new Size(250, 22), ForeColor = EditorChrome.Muted,
        });
    }

    private CollapsibleSection AddToolGrid(FlowLayoutPanel page, string title, params ModelAuthoringTool[] tools)
    {
        CollapsibleSection section = Section(page, title, ((tools.Length + 2) / 3) * 54 + 6);
        for (int index = 0; index < tools.Length; index++)
        {
            ModelAuthoringTool tool = tools[index];
            Button button = new ModelToolButton(tool)
            {
                Name = "ModelTool" + tool,
                Text = ToolName(tool),
                Bounds = new Rectangle(6 + index % 3 * 87, 4 + index / 3 * 54, 83, 50),
            };
            ImageEditorChrome.StyleButton(button);
            button.Click += (_, _) => SelectTool(tool);
            section.Content.Controls.Add(button);
            _toolButtons[tool] = button;
        }
        return section;
    }

    private void ShowToolPage(ModelToolPage page)
    {
        CancelPrimitivePlacement();
        CancelPushPull();
        if (_toolPageHost?.ShowMode(nameof(page)) != true) return;
        foreach ((ModelToolPage candidate, ToolStripButton button) in _toolPageButtons)
            button.Checked = candidate == page;
        switch (page)
        {
            case ModelToolPage.Create:
                _tool = ModelAuthoringTool.Select;
                SetMode(ModelEditorMode.Compose);
                break;
            case ModelToolPage.Edit:
                SetMode(ModelEditorMode.Mesh);
                break;
            case ModelToolPage.Select:
                SetMode(ModelEditorMode.Mesh);
                SelectTool(ModelAuthoringTool.Region);
                break;
            case ModelToolPage.Texture:
                SetMode(ModelEditorMode.Paint);
                break;
            case ModelToolPage.RigAnimate:
                SetMode(ModelEditorMode.Rig);
                break;
            case ModelToolPage.Outliner:
                _tool = ModelAuthoringTool.Select;
                SetMode(ModelEditorMode.Compose);
                break;
        }
        UpdateToolHeader();
    }

    private static CheckBox Check(string text, bool value, Action<bool> changed, int y)
    {
        CheckBox check = new() { Text = text, Checked = value, AutoSize = true, Location = new Point(8, y) };
        check.CheckedChanged += (_, _) => changed(check.Checked);
        return check;
    }
    public void SelectTool(ModelAuthoringTool tool)
    {
        CancelAuthoringGesture(); FinishStroke(); _tool=tool;
        SetBrush(tool == ModelAuthoringTool.Push ? ModelBrushKind.Carve : tool == ModelAuthoringTool.Smooth ? ModelBrushKind.Smooth : ModelBrushKind.Draw);
        SetMode(tool == ModelAuthoringTool.Colouring ? ModelEditorMode.Paint
            : tool is ModelAuthoringTool.Brush or ModelAuthoringTool.Smooth ? ModelEditorMode.Sculpt
            : tool is ModelAuthoringTool.Push or ModelAuthoringTool.Pull or ModelAuthoringTool.Wand
                or ModelAuthoringTool.Lasso or ModelAuthoringTool.Region or ModelAuthoringTool.BrushSelect ? ModelEditorMode.Mesh
            : ModelEditorMode.Compose);
        AllowSpin(false);
        foreach (var (kind,button) in _toolButtons) button.BackColor=kind==tool?ImageEditorChrome.Hover:ImageEditorChrome.Raised;
        UpdateToolHeader();
    }

    private void UpdateToolHeader()
    {
        if (_toolPageHost is null) return;
        _currentTool.Text = _toolPageHost.ActiveMode switch
        {
            nameof(ModelToolPage.Create) when _pendingPrimitive is { } primitive => $"CREATE\nPlace {PrimitiveName(primitive)}",
            nameof(ModelToolPage.Create) when _tool is ModelAuthoringTool.Line or ModelAuthoringTool.SquareFace
                or ModelAuthoringTool.CircleFace or ModelAuthoringTool.TriangleFace => "CREATE\n" + ToolName(_tool),
            nameof(ModelToolPage.Create) => "CREATE\nChoose a primitive or drawing tool",
            nameof(ModelToolPage.Edit) => $"EDIT\nSelect {_elementSelectionMode.ToString().ToLowerInvariant()}",
            nameof(ModelToolPage.Select) => "SELECT\n" + ToolName(_tool),
            nameof(ModelToolPage.Texture) => _tool == ModelAuthoringTool.Colouring ? "TEXTURE\nVertex Paint" : "TEXTURE\nMaterial and vertex colour",
            nameof(ModelToolPage.RigAnimate) => "RIG & ANIMATE\nSkeleton, poses and clips",
            _ => "OUTLINER\nMesh part hierarchy",
        };
    }

    private static string PrimitiveName(ModelPrimitiveKind kind) => kind == ModelPrimitiveKind.Quad ? "Plane" : kind.ToString();
    private static string ToolName(ModelAuthoringTool tool) => tool switch
    {
        ModelAuthoringTool.SquareFace => "Square",
        ModelAuthoringTool.CircleFace => "Circle",
        ModelAuthoringTool.TriangleFace => "Triangle",
        ModelAuthoringTool.Colouring => "Vertex Paint",
        ModelAuthoringTool.Region => "Box Select",
        ModelAuthoringTool.Wand => "Wand",
        ModelAuthoringTool.BrushSelect => "Brush Select",
        ModelAuthoringTool.Line => "Line / Strip",
        ModelAuthoringTool.Push => "Push / Pull",
        ModelAuthoringTool.Select => "Select Mesh",
        _ => tool.ToString(),
    };
    private static FlowLayoutPanel Stack(string name) => new() { Name=name,Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(8),BackColor=ImageEditorChrome.Surface };
    private static CollapsibleSection Section(FlowLayoutPanel stack,string title,int height)
    { var section=new CollapsibleSection(title,height,270);stack.Controls.Add(section);return section; }

    private void RefreshToolboxVisibility(ModelEditorMode mode)
    {
        bool mesh = mode == ModelEditorMode.Mesh;
        bool sculpt = mode == ModelEditorMode.Sculpt;
        if (_outlinerSection is not null) _outlinerSection.Visible = true;
        if (_primitivesSection is not null) _primitivesSection.Visible = true;
        if (_legacySelectionSection is not null) _legacySelectionSection.Visible = true;
        if (_selectionOptionsSection is not null) _selectionOptionsSection.Visible = true;
        if (_meshSelectionSection is not null) _meshSelectionSection.Visible = mesh;
        if (_boxModelingSection is not null) _boxModelingSection.Visible = mesh;
        if (_sculptDetailSection is not null) _sculptDetailSection.Visible = sculpt;
    }
    private static void Field(Control parent,string title,Control input,int y)
    { parent.Controls.Add(new Label{Text=title,Location=new Point(10,y+4),Size=new Size(140,24)});input.SetBounds(154,y,102,26);parent.Controls.Add(input); }

    private void BuildAuthoringInspector()
    {
        Body.ColumnCount=3;Body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,292));
        var right=Stack("ModelEditorInspector");Body.Controls.Add(right,2,0);
        var selection=Section(right,"MODEL DETAILS",82);
        selection.Content.Controls.Add(new Label
        {
            Text="Use the mode rail for creation, topology, selection, materials, rigging, and the mesh hierarchy.",
            Location=new Point(8,8), Size=new Size(250,62), ForeColor=EditorChrome.Muted,
        });
        var onion=Section(right,"Onion Skin",60);var check=new CheckBox{Text="Enable onion skin",AutoSize=true,Location=new Point(10,8)};
        check.CheckedChanged+=(_,_)=>_onion=check.Checked;onion.Content.Controls.Add(check);
        onion.Content.Controls.Add(new Label{Text="Previous pose: blue · Next pose: red",AutoSize=true,Location=new Point(10,35),ForeColor=EditorChrome.Muted});

        _morphSection=Section(right,"MORPH TARGETS",60);
        RefreshMorphInspector();

        _socketSection=Section(right,"ATTACHMENT SOCKETS",174);
        _socketList.SetBounds(8,8,250,100);
        _socketList.DoubleClick += (_,_) => EditSocket();
        _socketSection.Content.Controls.Add(_socketList);
        var newSocket=Tool("New…",AddSocket);newSocket.SetBounds(8,116,78,30);_socketSection.Content.Controls.Add(newSocket);
        var editSocket=Tool("Edit…",EditSocket);editSocket.SetBounds(94,116,78,30);_socketSection.Content.Controls.Add(editSocket);
        var deleteSocket=Tool("Delete",DeleteSocket);deleteSocket.SetBounds(180,116,78,30);_socketSection.Content.Controls.Add(deleteSocket);
        _socketSection.Content.Controls.Add(new Label { Text="Sockets follow their node or animated bone at runtime.", Location=new Point(8,150), Size=new Size(250,22), ForeColor=EditorChrome.Muted });
        RefreshSocketInspector();

        RefreshMaterialInspector();
    }

    private void RefreshMorphInspector()
    {
        if (_morphSection is null) return;
        string[] names = Asset.Meshes
            .SelectMany(mesh => mesh.MorphTargets ?? [])
            .Select(target => target.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _syncingGroups = true;
        try
        {
            if (names.Length > 0 && names.Length == _morphInputs.Count
                && names.All(name => _morphInputs.ContainsKey(name)))
            {
                foreach (string name in names)
                {
                    float current = Asset.Meshes.SelectMany(mesh => mesh.MorphTargets ?? [])
                        .First(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
                        .DefaultWeight;
                    _morphInputs[name].Value = (decimal)Math.Clamp(current, -8f, 8f);
                }
                return;
            }

            _morphSection.Content.Controls.Clear();
            _morphInputs.Clear();
            if (names.Length == 0)
            {
                _morphSection.SetContentHeight(54);
                _morphSection.Content.AutoScroll = false;
                _morphSection.Content.Controls.Add(new Label
                {
                    Text = "This model has no imported morph targets.",
                    Location = new Point(8, 9),
                    Size = new Size(250, 36),
                    ForeColor = EditorChrome.Muted,
                });
                return;
            }

            _morphSection.Content.AutoScroll = names.Length > 7;
            _morphSection.SetContentHeight(Math.Min(260, 12 + names.Length * 34));
            for (int index = 0; index < names.Length; index++)
            {
                string name = names[index];
                float value = Asset.Meshes.SelectMany(mesh => mesh.MorphTargets ?? [])
                    .First(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
                    .DefaultWeight;
                Label label = new()
                {
                    Text = name,
                    AutoEllipsis = true,
                    Location = new Point(8, 9 + index * 34),
                    Size = new Size(140, 24),
                    ForeColor = EditorChrome.Text,
                };
                NumericUpDown input = Number(-8m, 8m, (decimal)Math.Clamp(value, -8f, 8f),
                    changed => SetMorphDefault(name, (float)changed));
                input.SetBounds(153, 5 + index * 34, 104, 27);
                _morphSection.Content.Controls.Add(label);
                _morphSection.Content.Controls.Add(input);
                _morphInputs[name] = input;
            }
        }
        finally
        {
            _syncingGroups = false;
        }
    }

    private void SetMorphDefault(string name, float value)
    {
        if (_syncingGroups) return;
        ChangeAsset("Change morph " + name, () =>
        {
            foreach (GModelMesh mesh in Asset.Meshes)
                foreach (GModelMorphTarget target in mesh.MorphTargets ?? [])
                    if (string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
                        target.DefaultWeight = value;
        });
    }

    private void RefreshSocketInspector(string? selectedName = null)
    {
        if (_socketSection is null) return;
        selectedName ??= _socketList.SelectedItem?.ToString();
        _socketList.BeginUpdate();
        try
        {
            _socketList.Items.Clear();
            foreach (GModelSocket socket in Asset.Sockets ?? []) _socketList.Items.Add(socket.Name);
            int selected = string.IsNullOrWhiteSpace(selectedName)
                ? (_socketList.Items.Count > 0 ? 0 : -1)
                : _socketList.Items.Cast<string>().ToList().FindIndex(name =>
                    string.Equals(name, selectedName, StringComparison.OrdinalIgnoreCase));
            _socketList.SelectedIndex = selected >= 0 ? selected : _socketList.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            _socketList.EndUpdate();
        }
    }

    private void AddSocket()
    {
        using ModelSocketDialog dialog = new(Asset);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        GModelSocket created = dialog.Result;
        ChangeAsset("Create attachment socket", () =>
        {
            created.Name = UniqueSocketName(created.Name, -1);
            Asset.Sockets.Add(created);
        });
        RefreshSocketInspector(created.Name);
    }

    private void EditSocket()
    {
        int index = _socketList.SelectedIndex;
        List<GModelSocket> sockets = Asset.Sockets;
        if (index < 0 || index >= sockets.Count) return;
        using ModelSocketDialog dialog = new(Asset, sockets[index]);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        GModelSocket edited = dialog.Result;
        ChangeAsset("Edit attachment socket", () =>
        {
            edited.Name = UniqueSocketName(edited.Name, index);
            Asset.Sockets[index] = edited;
        });
        RefreshSocketInspector(edited.Name);
    }

    private void DeleteSocket()
    {
        int index = _socketList.SelectedIndex;
        List<GModelSocket> sockets = Asset.Sockets;
        if (index < 0 || index >= sockets.Count) return;
        ChangeAsset("Delete attachment socket", () => Asset.Sockets.RemoveAt(index));
        RefreshSocketInspector(index < Asset.Sockets.Count ? Asset.Sockets[index].Name : null);
    }

    private string UniqueSocketName(string requested, int except)
    {
        string basis = string.IsNullOrWhiteSpace(requested) ? "Socket" : requested.Trim();
        string candidate = basis;
        int suffix = 2;
        while (Asset.Sockets.Select((socket, index) => (socket, index)).Any(pair => pair.index != except
                   && string.Equals(pair.socket.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = basis + " " + suffix++;
        return candidate;
    }

    private static void AddPbrRow(Control parent, string caption, int y, ref PictureBox? preview, ref Label? value, Action choose)
    {
        preview = new PictureBox
        {
            BackColor = EditorChrome.Canvas,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(8, y),
            Size = new Size(42, 42),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        parent.Controls.Add(preview);
        parent.Controls.Add(new Label { Text = caption, Location = new Point(58, y + 2), Size = new Size(144, 20), ForeColor = EditorChrome.Muted });
        value = new Label { Text = "(none)", AutoEllipsis = true, Location = new Point(58, y + 22), Size = new Size(144, 20), ForeColor = EditorChrome.Text };
        parent.Controls.Add(value);
        Button button = Tool("…", choose);
        button.SetBounds(208, y + 7, 50, 28);
        parent.Controls.Add(button);
    }

    private GModelMaterial? SelectedMaterial()
    {
        int mesh=_groups.SelectedIndex;if(mesh<0||mesh>=Asset.Meshes.Count)return null;
        int index=Asset.Meshes[mesh].MaterialIndex;return index>=0&&index<Asset.Materials.Count?Asset.Materials[index]:null;
    }

    private void ChooseMaterialImage(string property)
    {
        ProjectAssetEntry? picked=AssetPickerService.PickAsset(new AssetPickerRequest(ProjectRoot,ResourceKind.Image,string.Empty,"Choose model material image"),FindForm());
        if(picked is null)return;
        string relative=picked.Reference.Replace('\\','/');
        ChangeAsset("Assign "+property,()=>
        {
            GModelMaterial material=Asset.Materials[EnsureSelectedMaterial()];
            if(property=="AlbedoTexture")material.AlbedoTexture=relative;
            else if(property=="NormalTexture")material.NormalTexture=relative;
            else material.MetallicRoughnessTexture=relative;
        });
        RefreshMaterialInspector();
    }

    private void ChooseMaterialResource()
    {
        if (_groups.SelectedIndex < 0 || _groups.SelectedIndex >= Asset.Meshes.Count)
        {
            Status.Text = "Select a mesh before assigning a material.";
            return;
        }
        ProjectAssetEntry? picked = AssetPickerService.PickAsset(
            new AssetPickerRequest(ProjectRoot, ResourceKind.Image, string.Empty, "Choose model material"), FindForm());
        if (picked is null) return;
        string relative = picked.Reference.Replace('\\', '/');
        ChangeAsset("Assign material", () =>
        {
            int index = EnsureEditableMaterial(ResourceDisplayName.Format(relative));
            GModelMaterial material = Asset.Materials[index];
            material.AlbedoTexture = relative;
            Asset.Meshes[_groups.SelectedIndex].MaterialIndex = index;
            Asset.Meshes[_groups.SelectedIndex].TriangleMaterialIndices = [];
        });
        RefreshMaterialInspector();
    }

    private void SetMaterialScalar(string property,float value)
    {
        if(_syncingGroups)return;GModelMaterial? material=SelectedMaterial();if(material is null)return;
        ChangeAsset("Change material "+property,()=>
        {
            if(property=="roughness") material.RoughnessFactor=value;
            else if(property=="metallic") material.MetallicFactor=value;
            else if(property is "uvOffsetU" or "uvOffsetV")
            {
                string key = property == "uvOffsetU" ? "UvOffsetU" : "UvOffsetV";
                float previous = MetadataFloat(material,key,0,-1000,1000);
                float delta = value - previous;
                int materialIndex = Asset.Materials.IndexOf(material);
                foreach (GModelMesh mesh in Asset.Meshes.Where(mesh => mesh.MaterialIndex == materialIndex))
                {
                    MeshVertex[] vertices = Vertices(mesh);
                    for (int index = 0; index < vertices.Length; index++)
                        vertices[index].UV += property == "uvOffsetU" ? new Vector2(delta,0) : new Vector2(0,delta);
                    SetVertices(mesh,vertices);
                }
                material.Metadata[key]=value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else material.Metadata["UvScale"]=value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    private void QuickUvUnwrap()
    {
        int meshIndex = _groups.SelectedIndex;
        if (meshIndex < 0) { Status.Text = "Select a mesh before unwrapping UVs."; return; }
        RunTopology("Quick UV unwrap", () =>
        {
            GModelMesh mesh = Asset.Meshes[meshIndex];
            MeshVertex[] vertices = Vertices(mesh);
            if (vertices.Length == 0) return;
            Vector3 min = vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Min);
            Vector3 max = vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Max);
            Vector3 size = Vector3.Max(max - min, new Vector3(.0001f));
            int dropAxis = size.X <= size.Y && size.X <= size.Z ? 0 : size.Y <= size.Z ? 1 : 2;
            for (int index = 0; index < vertices.Length; index++)
            {
                Vector3 p = vertices[index].Position - min;
                vertices[index].UV = dropAxis switch
                {
                    0 => new Vector2(p.Z / size.Z, 1f - p.Y / size.Y),
                    1 => new Vector2(p.X / size.X, 1f - p.Z / size.Z),
                    _ => new Vector2(p.X / size.X, 1f - p.Y / size.Y),
                };
            }
            SetVertices(mesh, vertices);
            mesh.SourceUvProtected = false;
            mesh.UvOverride = true;
        });
    }

    private void ChooseMaterialBaseColour()
    {
        GModelMaterial? material=SelectedMaterial();if(material is null)return;
        using ColorDialog picker=new(){Color=Color.FromArgb((int)(material.BaseColor.X*255),(int)(material.BaseColor.Y*255),(int)(material.BaseColor.Z*255)),FullOpen=true};
        if(picker.ShowDialog(this)!=DialogResult.OK)return;
        ChangeAsset("Change material colour",()=>material.BaseColor=new System.Numerics.Vector4(picker.Color.R/255f,picker.Color.G/255f,picker.Color.B/255f,material.BaseColor.W));
    }

    private void RefreshMaterialInspector()
    {
        GModelMaterial? material=SelectedMaterial();
        if(material is null)
        {
            if(_albedoValue is not null)_albedoValue.Text="(none)";
            if(_normalValue is not null)_normalValue.Text="(none)";
            if(_ormValue is not null)_ormValue.Text="(none)";
            SetMaterialPreview(_albedoPreview,string.Empty);SetMaterialPreview(_normalPreview,string.Empty);SetMaterialPreview(_ormPreview,string.Empty);
            return;
        }
        _syncingGroups=true;
        try
        {
            if(_albedoValue is not null)_albedoValue.Text=ShortAsset(material.AlbedoTexture);
            if(_normalValue is not null)_normalValue.Text=ShortAsset(material.NormalTexture);
            if(_ormValue is not null)_ormValue.Text=ShortAsset(material.MetallicRoughnessTexture);
            if(_roughnessValue is not null)_roughnessValue.Value=(decimal)Math.Clamp(material.RoughnessFactor,0,1);
            if(_metallicValue is not null)_metallicValue.Value=(decimal)Math.Clamp(material.MetallicFactor,0,1);
            if(_uvScaleValue is not null)_uvScaleValue.Value=(decimal)(material.Metadata.TryGetValue("UvScale",out string? text)&&float.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float scale)?Math.Clamp(scale,.01f,256f):1f);
            if(_uvOffsetUValue is not null)_uvOffsetUValue.Value=(decimal)MetadataFloat(material,"UvOffsetU",0,-1000,1000);
            if(_uvOffsetVValue is not null)_uvOffsetVValue.Value=(decimal)MetadataFloat(material,"UvOffsetV",0,-1000,1000);
            SetMaterialPreview(_albedoPreview, material.AlbedoTexture);
            SetMaterialPreview(_normalPreview, material.NormalTexture);
            SetMaterialPreview(_ormPreview, material.MetallicRoughnessTexture);
        }
        finally{_syncingGroups=false;}
        static string ShortAsset(string value)=>string.IsNullOrWhiteSpace(value)?"(none)":Genesis.Application.Core.Resources.ResourceDisplayName.Format(value);
    }

    private void SetMaterialPreview(PictureBox? box, string reference)
    {
        if (box is null) return;
        System.Drawing.Image? previous = box.Image;
        box.Image = null;
        previous?.Dispose();
        string? path = ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, reference);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using System.Drawing.Image source = System.Drawing.Image.FromFile(path);
            box.Image = new Bitmap(source);
            box.Tag = path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            box.Tag = exception.Message;
        }
    }

    private static float MetadataFloat(GModelMaterial material, string key, float fallback, float min, float max) =>
        material.Metadata.TryGetValue(key, out string? text)
        && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private void OpenAutoRigWizard()
    {
        try
        {
            using ModelRigWizardDialog dialog=new(ResourcePath,ProjectRoot,Asset);
            if(dialog.ShowDialog(this)==DialogResult.OK)ApplyAnimationWorkspace(dialog.Result,ActiveClip);
        }
        catch(Exception exception){MessageBox.Show(this,exception.Message,"Guided Auto-Rig",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
}

internal sealed class ModelToolButton(ModelAuthoringTool tool) : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        string label=Text;using(var background=new SolidBrush(BackColor))e.Graphics.FillRectangle(background,ClientRectangle);
        using(var border=new Pen(ImageEditorChrome.Border))e.Graphics.DrawRectangle(border,0,0,Width-1,Height-1);
        var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen=new Pen(ForeColor,1.6f);float x=Width/2f;
        if(tool is ModelAuthoringTool.Region or ModelAuthoringTool.Wand or ModelAuthoringTool.Lasso)pen.DashStyle=System.Drawing.Drawing2D.DashStyle.Dash;
        if(tool is ModelAuthoringTool.Sphere or ModelAuthoringTool.CircleFace or ModelAuthoringTool.Lasso)g.DrawEllipse(pen,x-8,6,16,16);
        else if(tool is ModelAuthoringTool.SquareFace or ModelAuthoringTool.Cube or ModelAuthoringTool.Region)g.DrawRectangle(pen,x-8,6,16,16);
        else if(tool==ModelAuthoringTool.TriangleFace)g.DrawPolygon(pen,new PointF[]{new(x,5),new(x+9,23),new(x-9,23)});
        else if(tool==ModelAuthoringTool.Cylinder){g.DrawEllipse(pen,x-8,6,16,6);g.DrawLine(pen,x-8,9,x-8,22);g.DrawLine(pen,x+8,9,x+8,22);g.DrawArc(pen,x-8,18,16,6,0,180);}
        else if(tool is ModelAuthoringTool.Push or ModelAuthoringTool.Pull){int sign=tool==ModelAuthoringTool.Pull?-1:1;float y=15+sign*8;g.DrawLine(pen,x,15-sign*8,x,y);g.DrawLines(pen,new PointF[]{new(x-5,y-sign*5),new(x,y),new(x+5,y-sign*5)});}
        else {g.DrawLine(pen,x-8,23,x+8,6);g.DrawLine(pen,x-4,24,x+10,9);}
        TextRenderer.DrawText(g,label,Font,new Rectangle(1,27,Width-2,21),ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.EndEllipsis);
    }
}

internal sealed class ModelPrimitiveButton(ModelPrimitiveKind primitive) : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        using SolidBrush background = new(BackColor); e.Graphics.FillRectangle(background, ClientRectangle);
        using Pen border = new(ImageEditorChrome.Border); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using Pen pen = new(ForeColor, 1.5f); float x = Width / 2f;
        if (primitive is ModelPrimitiveKind.Sphere or ModelPrimitiveKind.Capsule)
        {
            RectangleF ellipse = primitive == ModelPrimitiveKind.Sphere ? new(x - 9, 5, 18, 18) : new(x - 7, 4, 14, 21);
            e.Graphics.DrawEllipse(pen, ellipse);
            if (primitive == ModelPrimitiveKind.Capsule) e.Graphics.DrawLine(pen, x - 7, 11, x - 7, 18);
        }
        else if (primitive is ModelPrimitiveKind.Cylinder or ModelPrimitiveKind.Cone)
        {
            e.Graphics.DrawEllipse(pen, x - 9, 5, 18, 6);
            e.Graphics.DrawLines(pen, primitive == ModelPrimitiveKind.Cone
                ? [new PointF(x - 9, 8), new PointF(x, 24), new PointF(x + 9, 8)]
                : [new PointF(x - 9, 8), new PointF(x - 9, 21), new PointF(x + 9, 21), new PointF(x + 9, 8)]);
        }
        else
        {
            e.Graphics.DrawRectangle(pen, x - 9, 6, 16, 16); e.Graphics.DrawLine(pen, x - 9, 6, x - 4, 2); e.Graphics.DrawLine(pen, x + 7, 6, x + 12, 2);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(1, 27, Width - 2, 21), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }
}
